using System.Buffers;
using System.Security.Cryptography;
using ExplorerPlus.Interop;
using Microsoft.Win32.SafeHandles;

namespace ExplorerPlus.Core.Shell;

public static class DuplicateFinder
{
    public const string Scheme = "explorerplus-dupes:";
    internal const int HeadBytes = 64 * 1024;

    // cloud placeholders would be downloaded just to hash them
    private const uint Skipped = FileAttr.OFFLINE | FileAttr.RECALL_ON_OPEN | 0x400000;

    internal sealed record Candidate(string Path, long Size, long Modified, uint Attributes);

    public static bool IsUrl(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static string Url(string folder) => Scheme + Uri.EscapeDataString(folder);

    // must run on an STA thread
    public static ShellLocation? FromUrl(string url)
    {
        if (!IsUrl(url)) return null;
        string folder;
        try { folder = Uri.UnescapeDataString(url[Scheme.Length..]); }
        catch (UriFormatException) { return null; }
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return null;
        var pidl = ShellNative.TryParse(ShellLocation.Known.ThisPC);
        if (pidl is null) return null;
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        return new ShellLocation
        {
            Pidl = pidl,
            DisplayName = $"Duplicates in {(name.Length > 0 ? name : folder)}",
            ParsingName = Url(folder),
            Attributes = SFGAO.FOLDER,
            DuplicatesIn = folder,
        };
    }

    // one section per set of identical files, biggest files first
    internal static IEnumerable<FolderEntry> Enumerate(ShellLocation location, CancellationToken ct)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rank = 0;
        foreach (var group in Find(location.DuplicatesIn!, ct))
        {
            // the plainest name, so "report.docx" rather than "report - Copy (2).docx"
            var name = group.Select(c => Path.GetFileName(c.Path)).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).First();
            var section = $"{name} · {SizeFormat.Format(group[0].Size)} each";
            for (var n = 2; !used.Add(section); n++) section = $"{name} · {SizeFormat.Format(group[0].Size)} each ({n})";
            rank++;
            for (var i = 0; i < group.Count; i++)
            {
                var c = group[i];
                if (FastFolderEnumerator.FromIndex(c.Path, c.Attributes, c.Size, c.Modified) is not { } entry) continue;
                entry.Section = section;
                entry.SectionRank = rank;
                entry.SectionOrder = i;
                yield return entry;
            }
        }
    }

    internal static IEnumerable<List<Candidate>> Find(string root, CancellationToken ct)
    {
        var bySize = new Dictionary<long, List<Candidate>>();
        foreach (var c in Walk(root, ct))
        {
            if (!bySize.TryGetValue(c.Size, out var list)) bySize[c.Size] = list = [];
            list.Add(c);
        }
        foreach (var (_, sameSize) in bySize.Where(p => p.Value.Count > 1).OrderByDescending(p => p.Key))
            foreach (var group in Split(sameSize, ct))
                yield return group.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<Candidate> Walk(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<FolderEntry> entries;
            try { entries = FastFolderEnumerator.Enumerate(dir, ct).ToList(); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            foreach (var e in entries)
            {
                if (e.Path is null || (e.FileAttributes & FileAttr.REPARSE_POINT) != 0) continue; // junctions would count things twice
                if (e.IsFolder) pending.Push(e.Path);
                else if (e.Size > 0 && (e.FileAttributes & Skipped) == 0)
                    yield return new Candidate(e.Path, e.Size, e.Modified?.ToFileTime() ?? 0, e.FileAttributes);
            }
        }
    }

    private static IEnumerable<List<Candidate>> Split(List<Candidate> sameSize, CancellationToken ct)
    {
        // the start of each file first, so most look-alikes never get read in full
        var files = new HashSet<(uint, ulong)>();
        var byHead = new Dictionary<string, List<Candidate>>();
        foreach (var c in sameSize)
        {
            if (Hash(c.Path, HeadBytes, files, ct) is not { } head) continue;
            if (!byHead.TryGetValue(head, out var list)) byHead[head] = list = [];
            list.Add(c);
        }
        foreach (var list in byHead.Values.Where(l => l.Count > 1))
        {
            if (list[0].Size <= HeadBytes)
            {
                yield return list;
                continue;
            }
            var byContent = new Dictionary<string, List<Candidate>>();
            foreach (var c in list)
            {
                if (Hash(c.Path, long.MaxValue, null, ct) is not { } full) continue;
                if (!byContent.TryGetValue(full, out var same)) byContent[full] = same = [];
                same.Add(c);
            }
            foreach (var same in byContent.Values.Where(l => l.Count > 1)) yield return same;
        }
    }

    // null when the file can't be read, or is a hard link to one already seen
    internal static string? Hash(string path, long limit, HashSet<(uint, ulong)>? files, CancellationToken ct)
    {
        SafeFileHandle handle;
        try { handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        using (handle)
        {
            if (files is not null && VolumeNative.GetFileInformationByHandle(handle, out var info)
                && !files.Add((info.VolumeSerialNumber, (ulong)info.FileIndexHigh << 32 | info.FileIndexLow))) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                long offset = 0;
                while (offset < limit)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, limit - offset)), offset);
                    if (n == 0) break;
                    hash.AppendData(buffer, 0, n);
                    offset += n;
                }
            }
            catch (IOException) { return null; }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
