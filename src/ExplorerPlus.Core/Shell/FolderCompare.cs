using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum CompareState { Same, LeftOnly, RightOnly, LeftNewer, RightNewer, Different }

public enum SyncDirection { LeftToRight, RightToLeft, BothWays }

public sealed record CompareItem(string Relative, bool IsFolder, CompareState State,
    long LeftSize = -1, DateTime? LeftModified = null, long RightSize = -1, DateTime? RightModified = null);

public sealed record SyncPlan(IReadOnlyList<(string Source, string DestinationFolder)> Copies, IReadOnlyList<string> Deletes)
{
    public bool IsEmpty => Copies.Count == 0 && Deletes.Count == 0;
}

public static class FolderCompare
{
    // FAT drives and some shares keep times to 2 seconds
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    public static List<CompareItem> Compare(string left, string right, bool byContent, CancellationToken ct = default)
    {
        var result = new List<CompareItem>();
        Walk(left.TrimEnd('\\'), right.TrimEnd('\\'), "", byContent, result, ct);
        return result;
    }

    private static void Walk(string left, string right, string relative, bool byContent, List<CompareItem> result, CancellationToken ct)
    {
        var l = List(Join(left, relative), ct);
        var r = List(Join(right, relative), ct);
        var names = l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => !(l.GetValueOrDefault(n) ?? r[n]).IsFolder).ThenBy(n => n, NaturalComparer.Instance);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var rel = relative.Length == 0 ? name : relative + "\\" + name;
            l.TryGetValue(name, out var a);
            r.TryGetValue(name, out var b);
            if (b is null) result.Add(new CompareItem(rel, a!.IsFolder, CompareState.LeftOnly, a.Size, a.Modified));
            else if (a is null) result.Add(new CompareItem(rel, b.IsFolder, CompareState.RightOnly, RightSize: b.Size, RightModified: b.Modified));
            else if (a.IsFolder != b.IsFolder)
                result.Add(new CompareItem(rel, a.IsFolder, CompareState.Different, a.Size, a.Modified, b.Size, b.Modified));
            else if (a.IsFolder)
            {
                // a junction on either side could lead in circles
                if (((a.FileAttributes | b.FileAttributes) & FileAttr.REPARSE_POINT) == 0) Walk(left, right, rel, byContent, result, ct);
            }
            else
            {
                Func<bool>? content = byContent ? () => SameContent(Join(left, rel), Join(right, rel), ct) : null;
                result.Add(new CompareItem(rel, false, StateOf(a.Size, a.Modified, b.Size, b.Modified, content), a.Size, a.Modified, b.Size, b.Modified));
            }
        }
    }

    internal static CompareState StateOf(long leftSize, DateTime? leftTime, long rightSize, DateTime? rightTime, Func<bool>? sameContent)
    {
        var diff = (leftTime ?? default) - (rightTime ?? default);
        var sameTime = diff.Duration() <= Slack;
        if (leftSize == rightSize && (sameContent?.Invoke() ?? sameTime)) return CompareState.Same;
        if (sameTime) return CompareState.Different;
        return diff > TimeSpan.Zero ? CompareState.LeftNewer : CompareState.RightNewer;
    }

    // one way makes the target a copy of the source; both ways copies whatever is newer and leaves clashes alone
    public static SyncPlan Plan(string left, string right, IEnumerable<CompareItem> items, SyncDirection direction, bool deleteExtras)
    {
        var copies = new List<(string, string)>();
        var deletes = new List<string>();
        foreach (var item in items)
        {
            var toRight = direction switch
            {
                SyncDirection.LeftToRight => item.State is CompareState.LeftOnly or CompareState.LeftNewer or CompareState.RightNewer or CompareState.Different,
                SyncDirection.BothWays => item.State is CompareState.LeftOnly or CompareState.LeftNewer,
                _ => false,
            };
            var toLeft = direction switch
            {
                SyncDirection.RightToLeft => item.State is CompareState.RightOnly or CompareState.RightNewer or CompareState.LeftNewer or CompareState.Different,
                SyncDirection.BothWays => item.State is CompareState.RightOnly or CompareState.RightNewer,
                _ => false,
            };
            if (toRight) Add(left, right, item, copies, deletes);
            else if (toLeft) Add(right, left, item, copies, deletes);
            else if (deleteExtras && direction == SyncDirection.LeftToRight && item.State == CompareState.RightOnly) deletes.Add(Join(right, item.Relative));
            else if (deleteExtras && direction == SyncDirection.RightToLeft && item.State == CompareState.LeftOnly) deletes.Add(Join(left, item.Relative));
        }
        return new SyncPlan(copies, deletes);
    }

    private static void Add(string from, string to, CompareItem item, List<(string, string)> copies, List<string> deletes)
    {
        var target = Join(to, item.Relative);
        // what it replaces goes to the Recycle Bin first, so undo can bring it back
        if (item.State is not (CompareState.LeftOnly or CompareState.RightOnly)) deletes.Add(target);
        copies.Add((Join(from, item.Relative), Path.GetDirectoryName(target) ?? to));
    }

    private static Dictionary<string, FolderEntry> List(string folder, CancellationToken ct)
    {
        var map = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in FastFolderEnumerator.Enumerate(folder, ct)) map[e.Name] = e;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return map;
    }

    private static bool SameContent(string a, string b, CancellationToken ct)
    {
        var x = DuplicateFinder.Hash(a, long.MaxValue, null, ct);
        return x is not null && x == DuplicateFinder.Hash(b, long.MaxValue, null, ct);
    }

    private static string Join(string root, string relative) =>
        relative.Length == 0 ? root : root.EndsWith('\\') ? root + relative : root + "\\" + relative;
}
