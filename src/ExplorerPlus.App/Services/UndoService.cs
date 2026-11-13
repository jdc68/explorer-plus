using System.IO;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.App.Services;

/// <summary>Something done in Explorer+ that can be reverted. Reverting returns the record that redoes it.</summary>
public abstract record UndoRecord(string Label)
{
    public abstract Task<UndoRecord?> RevertAsync(IntPtr owner);

    protected static async Task<List<Pidl>> ParseAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var pidls = await StaWorkerPool.General.Run(() => list.Select(ShellNative.TryParse).ToList());
        return pidls.OfType<Pidl>().ToList();
    }
}

/// <summary>Items that now exist because of us (new, copied, restored). Undo sends them to the Recycle Bin.</summary>
public sealed record CreatedRecord(IReadOnlyList<string> Paths, string Label) : UndoRecord(Label)
{
    public override async Task<UndoRecord?> RevertAsync(IntPtr owner)
    {
        var pidls = await ParseAsync(Paths);
        if (pidls.Count == 0) return null;
        var log = new FileOpLog();
        if (await FileOperations.DeleteAsync(pidls, owner, permanent: false, confirm: false, log: log) != FileOpResult.Succeeded) return null;
        var recycled = log.TopLevel(FileOpKind.Delete).Where(e => e.RecycledItem is not null && e.Source is not null).ToList();
        return recycled.Count == 0 ? null : new RecycledRecord(recycled.Select(e => e.RecycledItem!).ToList(), recycled.Select(e => e.Source!).ToList(), Label);
    }
}

/// <summary>Items we sent to the Recycle Bin. Undo restores them to where they were.</summary>
public sealed record RecycledRecord(IReadOnlyList<Pidl> BinItems, IReadOnlyList<string> OriginalPaths, string Label) : UndoRecord(Label)
{
    public override Task<UndoRecord?> RevertAsync(IntPtr owner)
    {
        // "undelete" is the Recycle Bin's own Restore verb, so its bookkeeping stays right. Runs on the UI (STA) thread.
        using var menu = ShellContextMenu.ForItems(BinItems, owner, null);
        var ok = menu?.InvokeVerb("undelete", owner) == true;
        return Task.FromResult<UndoRecord?>(ok ? new CreatedRecord(OriginalPaths, Label) : null);
    }
}

public sealed record RenameRecord(string CurrentPath, string PreviousName) : UndoRecord("Rename")
{
    public override async Task<UndoRecord?> RevertAsync(IntPtr owner)
    {
        var pidls = await ParseAsync([CurrentPath]);
        if (pidls.Count == 0) return null;
        var log = new FileOpLog();
        if (await FileOperations.RenameAsync(pidls[0], PreviousName, owner, log) != FileOpResult.Succeeded) return null;
        var now = log.TopLevel(FileOpKind.Rename).FirstOrDefault()?.Result
                  ?? Path.Combine(Path.GetDirectoryName(CurrentPath.TrimEnd('\\')) ?? "", PreviousName);
        return new RenameRecord(now, Path.GetFileName(CurrentPath.TrimEnd('\\')));
    }
}

/// <summary>A batch rename: where each item is now and its name before. Undo renames them all back.</summary>
public sealed record BatchRenameRecord(IReadOnlyList<(string Current, string PreviousName)> Items) : UndoRecord("Rename")
{
    public override async Task<UndoRecord?> RevertAsync(IntPtr owner)
    {
        var result = await BatchRename.ApplyAsync(Items.Select(i => (i.Current, i.PreviousName)).ToList(), owner);
        foreach (var (now, oldName) in result.Renamed)
            Stars.Rename(Path.Combine(Path.GetDirectoryName(now) ?? "", oldName), now);
        return result.Renamed.Count == 0 ? null : new BatchRenameRecord(result.Renamed);
    }
}

/// <summary>Items we moved: where each is now and the folder it came from. Undo moves them back.</summary>
public sealed record MoveRecord(IReadOnlyList<(string Current, string PreviousFolder)> Items) : UndoRecord("Move")
{
    public override async Task<UndoRecord?> RevertAsync(IntPtr owner)
    {
        var back = new List<(string, string)>();
        foreach (var group in Items.GroupBy(i => i.PreviousFolder, StringComparer.OrdinalIgnoreCase))
        {
            var folder = await ParseAsync([group.Key]);
            var pidls = await ParseAsync(group.Select(g => g.Current));
            if (folder.Count == 0 || pidls.Count == 0) continue;
            var log = new FileOpLog();
            await FileOperations.MoveAsync(pidls, folder[0], owner, log);
            foreach (var e in log.TopLevel(FileOpKind.Move))
                if (e.Result is not null && e.Source is not null && Path.GetDirectoryName(e.Source.TrimEnd('\\')) is { } from)
                    back.Add((e.Result, from));
        }
        return back.Count == 0 ? null : new MoveRecord(back);
    }
}

/// <summary>Explorer+'s own undo/redo history, shared by all windows (Explorer's isn't open to other apps).</summary>
public static class UndoService
{
    private const int Limit = 50;
    private static readonly LinkedList<UndoRecord> UndoStack = new();
    private static readonly LinkedList<UndoRecord> RedoStack = new();
    private static bool _busy;

    public static event Action? Changed;

    public static string? UndoLabel => UndoStack.First?.Value.Label;
    public static string? RedoLabel => RedoStack.First?.Value.Label;

    public static void Push(UndoRecord? record)
    {
        if (record is null) return;
        UndoStack.AddFirst(record);
        while (UndoStack.Count > Limit) UndoStack.RemoveLast();
        RedoStack.Clear();
        Changed?.Invoke();
    }

    /// <summary>Records what a logged operation did, if it did anything that can be reverted.</summary>
    public static void PushFrom(FileOpLog log, FileOpKind kind, string label)
    {
        var entries = log.TopLevel(kind);
        UndoRecord? record = kind switch
        {
            FileOpKind.New or FileOpKind.Copy => entries.Select(e => e.Result).OfType<string>().ToList() is { Count: > 0 } paths ? new CreatedRecord(paths, label) : null,
            FileOpKind.Delete => entries.Where(e => e.RecycledItem is not null && e.Source is not null).ToList() is { Count: > 0 } r
                ? new RecycledRecord(r.Select(e => e.RecycledItem!).ToList(), r.Select(e => e.Source!).ToList(), label) : null,
            FileOpKind.Move => entries.Where(e => e.Result is not null && e.Source is not null)
                .Select(e => (e.Result!, Path.GetDirectoryName(e.Source!.TrimEnd('\\')) ?? "")).Where(t => t.Item2.Length > 0).ToList() is { Count: > 0 } m
                ? new MoveRecord(m) : null,
            FileOpKind.Rename => entries.FirstOrDefault(e => e.Result is not null && e.Source is not null) is { } e
                ? new RenameRecord(e.Result!, Path.GetFileName(e.Source!.TrimEnd('\\'))) : null,
            _ => null,
        };
        Push(record);
    }

    public static Task<bool> UndoAsync(IntPtr owner) => StepAsync(UndoStack, RedoStack, owner);

    public static Task<bool> RedoAsync(IntPtr owner) => StepAsync(RedoStack, UndoStack, owner);

    private static async Task<bool> StepAsync(LinkedList<UndoRecord> from, LinkedList<UndoRecord> to, IntPtr owner)
    {
        if (_busy || from.First is not { } node) return false;
        _busy = true;
        from.RemoveFirst();
        try
        {
            var inverse = await node.Value.RevertAsync(owner);
            if (inverse is not null) to.AddFirst(inverse);
            return inverse is not null;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }
}
