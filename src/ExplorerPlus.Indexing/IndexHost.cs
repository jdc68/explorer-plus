using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using ExplorerPlus.Interop;
using Microsoft.Win32.SafeHandles;

namespace ExplorerPlus.Indexing;

/// <summary>
/// The index service's state: one <see cref="VolumeReader"/> per fixed NTFS volume (one per volume, however many
/// drive letters it has), rechecked every minute so new and removed drives are picked up.
/// </summary>
public sealed class IndexHost : IDisposable
{
    private static readonly TimeSpan RescanVolumes = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether a user may list a folder, remembered for a few minutes: checking means opening the folder, which
    /// takes milliseconds on a spinning disk, and a broad search checks thousands of folders.
    /// </summary>
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(5);
    private const int AccessCacheLimit = 500_000;
    private readonly ConcurrentDictionary<(string User, uint Volume, int Folder), (bool Allowed, long Expires)> _access = new();

    /// <summary>Where slow searches are reported (the service's log).</summary>
    public static Action<string>? Log { get; set; }
    private readonly Dictionary<string, VolumeReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private Timer? _timer;

    public void Start() => _timer = new Timer(_ => RefreshVolumes(), null, TimeSpan.Zero, RescanVolumes);

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            foreach (var reader in _readers.Values) reader.Dispose();
            _readers.Clear();
        }
    }

    public IReadOnlyList<VolumeReader> Readers
    {
        get { lock (_gate) return [.. _readers.Values.OrderBy(r => r.Root, StringComparer.OrdinalIgnoreCase)]; }
    }

    private void RefreshVolumes()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // volume name -> root
        var drives = VolumeNative.GetLogicalDrives();
        for (var i = 0; i < 26; i++)
        {
            if ((drives & (1u << i)) == 0) continue;
            var root = $"{(char)('A' + i)}:\\";
            if (VolumeNative.GetDriveTypeW(root) != VolumeNative.DRIVE_FIXED || !IsNtfs(root)) continue;
            var name = new char[64];
            if (!Kernel32.GetVolumeNameForVolumeMountPointW(root, name, (uint)name.Length)) continue;
            found.TryAdd(new string(name).TrimEnd('\0'), root);
        }
        lock (_gate)
        {
            foreach (var gone in _readers.Keys.Where(k => !found.ContainsKey(k)).ToList())
            {
                _readers[gone].Dispose();
                _readers.Remove(gone);
            }
            foreach (var (volume, root) in found)
            {
                if (_readers.ContainsKey(volume)) continue;
                var reader = new VolumeReader(root, volume);
                _readers[volume] = reader;
                reader.Start();
            }
        }
    }

    private static bool IsNtfs(string root)
    {
        var fs = new char[32];
        return VolumeNative.GetVolumeInformationW(root, null, 0, out _, out _, out _, fs, fs.Length) &&
               new string(fs).TrimEnd('\0').Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs a search over every indexed volume, or inside <paramref name="scope"/>. Call it on a thread that
    /// impersonates the client: the scope is opened and folders are checked with the client's own rights.
    /// </summary>
    public SearchResponse Search(SearchRequest request, CancellationToken ct = default)
    {
        var query = IndexQuery.Parse(request.Query);
        if (query.IsEmpty) return new SearchResponse(SearchStatus.Ok, 0, [], false);
        var readers = Readers;
        var scanning = readers.Any(r => r.Index is null && r.State == VolumeState.Scanning);
        var options = new IndexSearchOptions(request.IncludeHidden, request.IncludeProtected);
        var limit = Math.Clamp(request.Limit, 1, 100_000);

        List<(VolumeIndex Index, int? Scope)> targets = [];
        if (string.IsNullOrEmpty(request.Scope))
        {
            targets.AddRange(readers.Where(r => r.Index is not null).Select(r => (r.Index!, (int?)null)));
        }
        else
        {
            if (!TryOpenScope(request.Scope, out var serial, out var reference)) return new SearchResponse(SearchStatus.NotIndexed, 0, [], scanning);
            var index = readers.Select(r => r.Index).FirstOrDefault(i => i is not null && i.SerialNumber == serial);
            if (index is null) return new SearchResponse(scanning ? SearchStatus.Ok : SearchStatus.NotIndexed, 0, [], scanning);
            var record = index.Find(reference);
            if (record is null) return new SearchResponse(SearchStatus.NotIndexed, 0, [], scanning);
            targets.Add((index, record));
        }

        var user = WindowsIdentity.GetCurrent(ifImpersonating: true)?.User?.Value ?? "";
        if (_access.Count > AccessCacheLimit) _access.Clear();
        var now = Environment.TickCount64;
        var sw = Stopwatch.StartNew();
        var opened = 0;
        var hits = new List<IndexHit>();
        var total = 0;
        foreach (var (index, scope) in targets)
        {
            var serial = index.SerialNumber;
            var (found, count) = index.Search(query, options, scope, limit, (record, folder) =>
            {
                var key = (user, serial, record);
                if (_access.TryGetValue(key, out var known) && known.Expires > now) return known.Allowed;
                opened++;
                var allowed = CanList(folder);
                _access[key] = (allowed, now + (long)AccessLifetime.TotalMilliseconds);
                return allowed;
            }, ct);
            hits.AddRange(found);
            total += count;
        }
        if (sw.ElapsedMilliseconds > 1000)
            Log?.Invoke($"Slow search ({sw.ElapsedMilliseconds:N0} ms, {total:N0} matches, {opened:N0} folders checked): {request.Query}");
        hits.Sort((a, b) => a.Key.CompareTo(b.Key));
        if (hits.Count > limit) hits.RemoveRange(limit, hits.Count - limit);
        return new SearchResponse(SearchStatus.Ok, total, hits.Select(h => new SearchHit(h.Path, h.Attributes)).ToList(), scanning);
    }

    public StatusResponse Status() =>
        new(IndexProtocol.Version, Readers.Select(r => new VolumeStatus(r.Root, r.State, r.Index?.Count ?? 0, r.Error)).ToList());

    private static bool TryOpenScope(string path, out uint serial, out ulong reference)
    {
        serial = 0;
        reference = 0;
        using var handle = Open(path, VolumeNative.FILE_READ_ATTRIBUTES);
        if (handle.IsInvalid || !VolumeNative.GetFileInformationByHandle(handle, out var info)) return false;
        serial = info.VolumeSerialNumber;
        reference = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return true;
    }

    /// <summary>Whether the (impersonated) caller may list <paramref name="folder"/>.</summary>
    private static bool CanList(string folder)
    {
        using var handle = Open(folder, VolumeNative.FILE_LIST_DIRECTORY);
        return !handle.IsInvalid;
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        if (path.Length >= 248 && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = @"\\?\" + path;
        return VolumeNative.CreateFileW(path, access,
            VolumeNative.FILE_SHARE_READ | VolumeNative.FILE_SHARE_WRITE | VolumeNative.FILE_SHARE_DELETE, IntPtr.Zero,
            VolumeNative.OPEN_EXISTING, VolumeNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
    }
}
