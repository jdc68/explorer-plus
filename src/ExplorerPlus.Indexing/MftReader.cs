using System.ComponentModel;
using ExplorerPlus.Interop;
using Microsoft.Win32.SafeHandles;

namespace ExplorerPlus.Indexing;

public static class MftReader
{
    private const uint FileSignature = 0x454C4946; // "FILE"
    private const uint AttrStandardInformation = 0x10, AttrData = 0x80, AttrEnd = 0xFFFFFFFF;
    private const int ChunkSize = 4 << 20;

    // indexed by record number; size -1 and time 0 mean unknown
    public sealed record Result(long[] Sizes, long[] Modified);

    public static unsafe Result Read(SafeFileHandle volume, CancellationToken ct)
    {
        var data = stackalloc byte[VolumeNative.NtfsVolumeDataSize];
        if (!VolumeNative.DeviceIoControl(volume, VolumeNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, VolumeNative.NtfsVolumeDataSize, out _, IntPtr.Zero))
            throw new Win32Exception();
        var bytesPerSector = *(uint*)(data + 40);
        var bytesPerCluster = *(uint*)(data + 44);
        var recordSize = (int)*(uint*)(data + VolumeNative.NtfsBytesPerFileRecordSegment);
        var validLength = *(long*)(data + VolumeNative.NtfsMftValidDataLength);
        var mftStart = *(long*)(data + 64);
        if (recordSize < 512 || bytesPerCluster == 0) throw new InvalidDataException("Unexpected NTFS geometry.");
        var count = (int)Math.Min(validLength / recordSize, int.MaxValue - 1);

        var sizes = new long[count];
        Array.Fill(sizes, -1L);
        var modified = new long[count];

        // record 0 is the $MFT itself, its data runs map the rest
        var first = new byte[Align(recordSize, (int)Math.Max(bytesPerSector, 512))];
        RandomAccess.Read(volume, first, mftStart * bytesPerCluster);
        var runs = DataRuns(first, recordSize);
        if (runs.Count == 0) throw new InvalidDataException("The $MFT record has no data runs.");

        var buffer = GC.AllocateUninitializedArray<byte>(ChunkSize);
        foreach (var (vcn, lcn, clusters) in runs)
        {
            var offset = lcn * bytesPerCluster;
            var remaining = clusters * bytesPerCluster;
            var record = vcn * bytesPerCluster / recordSize;
            while (remaining > 0 && record < count)
            {
                ct.ThrowIfCancellationRequested();
                var length = (int)Math.Min(remaining, ChunkSize);
                var read = RandomAccess.Read(volume, buffer.AsSpan(0, length), offset);
                if (read <= 0) break;
                for (var at = 0; at + recordSize <= read && record < count; at += recordSize, record++)
                    ParseRecord(buffer.AsSpan(at, recordSize), (int)record, sizes, modified);
                offset += read;
                remaining -= read;
            }
        }
        return new Result(sizes, modified);
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    // extension records count toward their base record
    internal static void ParseRecord(Span<byte> record, int number, long[] sizes, long[] modified)
    {
        if (record.Length < 64 || Read32(record, 0) != FileSignature) return;
        var flags = Read16(record, 0x16);
        if ((flags & 1) == 0 || !Fixup(record)) return; // not in use, or torn
        var baseRef = Read64(record, 0x20);
        var target = baseRef == 0 ? number : VolumeIndex.Record(baseRef);
        if ((uint)target >= (uint)sizes.Length) return;

        for (var a = (int)Read16(record, 0x14); a + 16 <= record.Length;)
        {
            var type = Read32(record, a);
            if (type == AttrEnd) break;
            var length = (int)Read32(record, a + 4);
            if (length < 16 || a + length > record.Length) break;
            var nonResident = record[a + 8] != 0;
            var nameLength = record[a + 9];
            if (type == AttrStandardInformation && !nonResident)
            {
                var value = a + Read16(record, a + 0x14);
                if (value + 16 <= record.Length) modified[target] = (long)Read64(record, value + 8);
            }
            else if (type == AttrData && nameLength == 0)
            {
                if (!nonResident) sizes[target] = Read32(record, a + 0x10);
                else if (length >= 0x38 && Read64(record, a + 0x10) == 0) sizes[target] = (long)Read64(record, a + 0x30); // first extent holds the sizes
            }
            a += length;
        }
    }

    // puts back the last two bytes of each sector, swapped out to detect torn writes
    internal static bool Fixup(Span<byte> record)
    {
        int usaOffset = Read16(record, 4), usaCount = Read16(record, 6);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length) return false;
        var stride = record.Length / (usaCount - 1);
        if (stride < 512) return false;
        var check = Read16(record, usaOffset);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * stride - 2;
            if (Read16(record, end) != check) return false;
            record[end] = record[usaOffset + 2 * i];
            record[end + 1] = record[usaOffset + 2 * i + 1];
        }
        return true;
    }

    internal static List<(long Vcn, long Lcn, long Clusters)> DataRuns(Span<byte> buffer, int recordSize)
    {
        var record = buffer[..recordSize];
        var runs = new List<(long, long, long)>();
        if (Read32(record, 0) != FileSignature || !Fixup(record)) return runs;
        for (var a = (int)Read16(record, 0x14); a + 16 <= record.Length;)
        {
            var type = Read32(record, a);
            if (type == AttrEnd) break;
            var length = (int)Read32(record, a + 4);
            if (length < 16 || a + length > record.Length) break;
            if (type == AttrData && record[a + 9] == 0 && record[a + 8] != 0)
            {
                var vcn = (long)Read64(record, a + 0x10);
                long lcn = 0;
                var p = a + Read16(record, a + 0x20);
                while (p < a + length && record[p] != 0)
                {
                    int lengthBytes = record[p] & 0xF, offsetBytes = record[p] >> 4;
                    p++;
                    if (p + lengthBytes + offsetBytes > a + length) break;
                    var clusters = ReadVarUnsigned(record.Slice(p, lengthBytes));
                    p += lengthBytes;
                    if (offsetBytes == 0) { vcn += clusters; continue; } // sparse
                    lcn += ReadVarSigned(record.Slice(p, offsetBytes));
                    p += offsetBytes;
                    runs.Add((vcn, lcn, clusters));
                    vcn += clusters;
                }
                break;
            }
            a += length;
        }
        return runs;
    }

    private static long ReadVarUnsigned(ReadOnlySpan<byte> bytes)
    {
        long v = 0;
        for (var i = bytes.Length - 1; i >= 0; i--) v = (v << 8) | bytes[i];
        return v;
    }

    private static long ReadVarSigned(ReadOnlySpan<byte> bytes)
    {
        var v = ReadVarUnsigned(bytes);
        var bits = bytes.Length * 8;
        return bits < 64 && (bytes[^1] & 0x80) != 0 ? v - (1L << bits) : v;
    }

    private static ushort Read16(ReadOnlySpan<byte> s, int at) => BitConverter.ToUInt16(s[at..]);
    private static uint Read32(ReadOnlySpan<byte> s, int at) => BitConverter.ToUInt32(s[at..]);
    private static ulong Read64(ReadOnlySpan<byte> s, int at) => BitConverter.ToUInt64(s[at..]);
}
