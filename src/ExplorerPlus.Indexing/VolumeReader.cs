using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExplorerPlus.Interop;
using Microsoft.Win32.SafeHandles;

namespace ExplorerPlus.Indexing;

public enum VolumeState : byte { Scanning, Ready, Failed }

/// <summary>
/// Keeps one NTFS volume's <see cref="VolumeIndex"/>: reads every name from the MFT (FSCTL_ENUM_USN_DATA, a few
/// seconds for millions of files), then follows the USN change journal to stay current. Needs administrator rights
/// (the index service runs as LocalSystem). When the journal is reset or wraps past what was read, it scans again,
/// keeping the old index searchable meanwhile.
/// </summary>
public sealed class VolumeReader : IDisposable
{
    private const int BufferSize = 1 << 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Where failures are reported in full (the service's log).</summary>
    public static Action<string>? Log { get; set; }

    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public VolumeReader(string root, string volumeName)
    {
        Root = root;
        VolumeName = volumeName;
    }

    /// <summary>Drive root, such as <c>C:\</c>.</summary>
    public string Root { get; }

    /// <summary>The volume's GUID path (<c>\\?\Volume{...}\</c>), which identifies it across drive letters.</summary>
    public string VolumeName { get; }

    public VolumeIndex? Index { get; private set; }
    public VolumeState State { get; private set; } = VolumeState.Scanning;
    public string? Error { get; private set; }
    public TimeSpan LastScanTime { get; private set; }

    public void Start() =>
        _task = Task.Factory.StartNew(() => Run(_cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose()
    {
        _cts.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }

    private void Run(CancellationToken ct)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var volume = VolumeNative.CreateFileW(@"\\.\" + Root.TrimEnd('\\'), VolumeNative.GENERIC_READ,
                    VolumeNative.FILE_SHARE_READ | VolumeNative.FILE_SHARE_WRITE, IntPtr.Zero, VolumeNative.OPEN_EXISTING, 0, IntPtr.Zero);
                if (volume.IsInvalid) throw new Win32Exception();
                var journal = QueryJournal(volume);
                State = Index is null ? VolumeState.Scanning : State;
                var sw = Stopwatch.StartNew();
                var index = Scan(volume, buffer, ct);
                LastScanTime = sw.Elapsed;
                Log?.Invoke($"{Root}: read {index.Count:N0} items in {sw.ElapsedMilliseconds:N0} ms, about {index.MemoryBytes >> 20} MB");
                Index = index;
                State = VolumeState.Ready;
                Error = null;
                Follow(volume, index, journal, buffer, ct); // returns when the journal needs a fresh scan
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                State = Index is null ? VolumeState.Failed : State;
                Error = ex.Message;
                Log?.Invoke($"{Root}: {ex}");
                ct.WaitHandle.WaitOne(RetryInterval);
            }
        }
    }

    private static unsafe USN_JOURNAL_DATA_V0 QueryJournal(SafeFileHandle volume)
    {
        USN_JOURNAL_DATA_V0 data;
        if (VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_QUERY_USN_JOURNAL, null, 0, &data, sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
            return data;
        var error = Marshal.GetLastWin32Error();
        if (error != VolumeNative.ERROR_JOURNAL_NOT_ACTIVE) throw new Win32Exception(error);

        // No journal on this volume yet: create one the size Windows uses by default for C:.
        var create = new CREATE_USN_JOURNAL_DATA { MaximumSize = 32UL << 20, AllocationDelta = 8UL << 20 };
        if (!VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_CREATE_USN_JOURNAL, &create, sizeof(CREATE_USN_JOURNAL_DATA), null, 0, out _, IntPtr.Zero))
            throw new Win32Exception();
        if (!VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_QUERY_USN_JOURNAL, null, 0, &data, sizeof(USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
            throw new Win32Exception();
        return data;
    }

    private unsafe VolumeIndex Scan(SafeFileHandle volume, byte[] buffer, CancellationToken ct)
    {
        using var rootHandle = VolumeNative.CreateFileW(Root, VolumeNative.FILE_READ_ATTRIBUTES,
            VolumeNative.FILE_SHARE_READ | VolumeNative.FILE_SHARE_WRITE | VolumeNative.FILE_SHARE_DELETE, IntPtr.Zero,
            VolumeNative.OPEN_EXISTING, VolumeNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (rootHandle.IsInvalid || !VolumeNative.GetFileInformationByHandle(rootHandle, out var info)) throw new Win32Exception();
        var rootReference = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;

        var index = new VolumeIndex(Root, rootReference, info.VolumeSerialNumber, RecordCountHint(volume));
        var request = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
        index.EnterWrite();
        try
        {
            fixed (byte* buf = buffer)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_ENUM_USN_DATA, &request, sizeof(MFT_ENUM_DATA_V0), buf, buffer.Length, out var read, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == VolumeNative.ERROR_HANDLE_EOF) break;
                        throw new Win32Exception(error);
                    }
                    if (read <= 8) break;
                    request.StartFileReferenceNumber = *(ulong*)buf;
                    for (var offset = 8; offset < read;)
                    {
                        var record = buf + offset;
                        var length = *(uint*)record;
                        if (length == 0) break;
                        if (*(ushort*)(record + UsnRecord.MajorVersion) == 2) Apply(index, record, scanning: true);
                        offset += (int)length;
                    }
                }
            }
        }
        finally { index.ExitWrite(); }
        return index;
    }

    private static unsafe int RecordCountHint(SafeFileHandle volume)
    {
        var data = stackalloc byte[VolumeNative.NtfsVolumeDataSize];
        if (!VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, VolumeNative.NtfsVolumeDataSize, out _, IntPtr.Zero))
            return 1 << 16;
        var mftBytes = *(long*)(data + VolumeNative.NtfsMftValidDataLength);
        var recordSize = *(uint*)(data + VolumeNative.NtfsBytesPerFileRecordSegment);
        return recordSize == 0 ? 1 << 16 : (int)Math.Clamp(mftBytes / recordSize + 1024, 1 << 16, int.MaxValue - 1);
    }

    private unsafe void Follow(SafeFileHandle volume, VolumeIndex index, USN_JOURNAL_DATA_V0 journal, byte[] buffer, CancellationToken ct)
    {
        var request = new READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = journal.NextUsn,
            ReasonMask = 0xFFFFFFFF,
            UsnJournalID = journal.UsnJournalID,
        };
        fixed (byte* buf = buffer)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_READ_USN_JOURNAL, &request, sizeof(READ_USN_JOURNAL_DATA_V0), buf, buffer.Length, out var read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is VolumeNative.ERROR_JOURNAL_ENTRY_DELETED or VolumeNative.ERROR_JOURNAL_NOT_ACTIVE or VolumeNative.ERROR_JOURNAL_DELETE_IN_PROGRESS)
                        return; // changes were lost: scan again
                    throw new Win32Exception(error);
                }
                if (read < 8) return;
                var next = *(long*)buf;
                if (read == 8 || next == request.StartUsn)
                {
                    ct.WaitHandle.WaitOne(PollInterval);
                    continue;
                }
                request.StartUsn = next;
                index.EnterWrite();
                try
                {
                    for (var offset = 8; offset < read;)
                    {
                        var record = buf + offset;
                        var length = *(uint*)record;
                        if (length == 0) break;
                        if (*(ushort*)(record + UsnRecord.MajorVersion) == 2) Apply(index, record, scanning: false);
                        offset += (int)length;
                    }
                }
                finally { index.ExitWrite(); }
            }
        }
    }

    private static unsafe void Apply(VolumeIndex index, byte* record, bool scanning)
    {
        var reference = *(ulong*)(record + UsnRecord.FileReferenceNumber);
        if (!scanning)
        {
            var reason = *(uint*)(record + UsnRecord.Reason);
            if ((reason & UsnRecord.REASON_FILE_DELETE) != 0)
            {
                index.Remove(reference);
                return;
            }
            // The old name is followed by a record with the new one.
            if ((reason & UsnRecord.REASON_RENAME_OLD_NAME) != 0) return;
        }
        var parent = *(ulong*)(record + UsnRecord.ParentFileReferenceNumber);
        var attributes = *(uint*)(record + UsnRecord.FileAttributes);
        var nameLength = *(ushort*)(record + UsnRecord.FileNameLength) / 2;
        var nameOffset = *(ushort*)(record + UsnRecord.FileNameOffset);
        index.Upsert(reference, parent, new ReadOnlySpan<char>(record + nameOffset, nameLength), attributes);
    }
}
