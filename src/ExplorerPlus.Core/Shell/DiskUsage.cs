using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class DiskNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public DiskNode? Parent { get; init; }

    /// <summary>Bytes, -1 while a folder is still being measured.</summary>
    public long Size { get; internal set; }

    /// <summary>Null until the folder was listed; biggest first.</summary>
    public IReadOnlyList<DiskNode>? Children { get; internal set; }

    // a stand-in for the many small items a chart doesn't draw one by one
    public int OtherCount { get; init; }
    public bool IsOther => OtherCount > 0;

    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();

    public bool IsAncestorOf(DiskNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n == this) return true;
        return false;
    }
}

/// <summary>
/// The disk usage tree below a folder. A folder's children are listed when asked for, with folder totals from the
/// index service where it has them and from <see cref="FolderSizeService"/>'s walk elsewhere.
/// </summary>
public sealed class DiskUsage(string root, bool useIndex = true)
{
    private readonly SemaphoreSlim _gate = new(4);

    public DiskNode Root { get; } = new()
    {
        Name = System.IO.Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } name ? name : root,
        Path = root,
        IsFolder = true,
        Size = -1,
    };

    /// <summary>A node's size or children changed. Raised on worker threads.</summary>
    public event Action<DiskNode>? Changed;

    /// <summary>Folders whose total is still being walked.</summary>
    public int Measuring => Volatile.Read(ref _measuring);
    private int _measuring;

    public bool FromIndex { get; private set; }

    /// <summary>Lists a folder (once) with its subfolders' totals from the index; those it lacks are walked afterwards (<see cref="Changed"/>).</summary>
    public async Task LoadAsync(DiskNode folder, CancellationToken ct)
    {
        if (!folder.IsFolder || folder.Children is not null) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        List<FolderEntry> listed;
        try
        {
            if (folder.Children is not null) return;
            listed = await Task.Run(() =>
            {
                try { return FastFolderEnumerator.Enumerate(folder.Path, ct).ToList(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        // links and junctions point at space counted elsewhere
        var children = listed.Where(e => !(e.IsFolder && (e.FileAttributes & FileAttr.REPARSE_POINT) != 0))
            .Select(e => new DiskNode
            {
                Name = e.Name,
                Path = e.Path!,
                IsFolder = e.IsFolder,
                Parent = folder,
                Size = e.IsFolder ? -1 : Math.Max(0, e.Size),
            }).ToList();
        lock (folder)
        {
            if (folder.Children is not null) return;
            folder.Children = Sorted(children);
        }
        Recount(folder);
        Changed?.Invoke(folder);

        var folders = children.Where(c => c.IsFolder).ToList();
        if (folders.Count == 0) return;
        var indexed = useIndex ? await IndexSearch.GetFolderSizesAsync(folders.Select(f => f.Path).ToList(), ct).ConfigureAwait(false) : null;
        if (indexed is not null) FromIndex = true;
        for (var i = 0; i < folders.Count; i++)
        {
            if (indexed is not null && indexed[i] >= 0) SetSize(folders[i], indexed[i]);
            else _ = WalkAsync(folders[i], ct);
        }
        if (indexed is not null) Changed?.Invoke(folder);
    }

    private async Task WalkAsync(DiskNode folder, CancellationToken ct)
    {
        Interlocked.Increment(ref _measuring);
        try
        {
            var size = await FolderSizeService.Instance.GetAsync(folder.Path, ct).ConfigureAwait(false);
            SetSize(folder, size ?? 0);
        }
        catch (OperationCanceledException) { return; }
        catch { SetSize(folder, 0); }
        finally { Interlocked.Decrement(ref _measuring); }
        Changed?.Invoke(folder);
    }

    private void SetSize(DiskNode folder, long size)
    {
        folder.Size = size;
        if (folder.Parent is { } parent)
        {
            lock (parent) parent.Children = Sorted(parent.Children!);
            Recount(parent);
        }
    }

    /// <summary>Takes a deleted item out of the tree; its folders get smaller.</summary>
    public void Remove(DiskNode node)
    {
        if (node.Parent is not { } parent) return;
        lock (parent) parent.Children = parent.Children?.Where(c => c != node).ToList();
        Recount(parent);
        Changed?.Invoke(parent);
    }

    // a listed folder's size is its children's, once all of them are known; the change goes up to the root
    private void Recount(DiskNode folder)
    {
        for (var f = folder; f is not null; f = f.Parent)
        {
            var children = f.Children;
            if (children is null || children.Any(c => c.Size < 0)) return;
            var total = children.Sum(c => c.Size);
            if (total == f.Size) return;
            f.Size = total;
            if (f.Parent is { } parent)
                lock (parent) parent.Children = Sorted(parent.Children!);
        }
    }

    private static List<DiskNode> Sorted(IEnumerable<DiskNode> nodes) =>
        [.. nodes.OrderByDescending(n => n.Size).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The children a chart draws: the biggest ones, and the rest (past <paramref name="max"/>, or each under
    /// <paramref name="minShare"/> of the total) as one "other" node.
    /// </summary>
    public static IReadOnlyList<DiskNode> Visible(DiskNode folder, int max, double minShare)
    {
        var children = folder.Children ?? [];
        var total = Math.Max(1, children.Sum(c => Math.Max(0, c.Size)));
        var shown = new List<DiskNode>();
        long rest = 0;
        var restCount = 0;
        foreach (var c in children)
        {
            if (c.Size <= 0) continue;
            if (shown.Count < max && c.Size >= total * minShare) shown.Add(c);
            else
            {
                rest += c.Size;
                restCount++;
            }
        }
        if (restCount == 1 && shown.Count < max + 1)
            shown.Add(children.First(c => c.Size > 0 && !shown.Contains(c)));
        else if (restCount > 0)
            shown.Add(new DiskNode
            {
                Name = $"{restCount:N0} smaller items",
                Path = folder.Path,
                Parent = folder,
                Size = rest,
                OtherCount = restCount,
            });
        return shown;
    }
}
