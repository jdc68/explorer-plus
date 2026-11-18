using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.ViewModels;

public sealed record InUseReport(IReadOnlyList<string> Paths, IReadOnlyList<FileHolder> Holders, FileOpKind Kind, Func<Task>? Retry)
{
    public bool IsDelete => Kind == FileOpKind.Delete;

    public string Subject => Paths.Count == 1 ? $"\u201c{System.IO.Path.GetFileName(Paths[0].TrimEnd('\\'))}\u201d" : $"{Paths.Count} items";

    public string Message
    {
        get
        {
            var verb = Kind switch
            {
                FileOpKind.Delete => "deleted",
                FileOpKind.Move => "moved",
                FileOpKind.Copy => "copied",
                _ => "renamed",
            };
            return $"{Subject} couldn't be {verb}: {(Paths.Count == 1 ? "it's" : "they're")} open in {Names(Holders)}.";
        }
    }

    public static string Names(IReadOnlyList<FileHolder> holders)
    {
        var names = holders.Select(h => h.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return names.Count switch
        {
            0 => "another app",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{names[0]} and {names.Count - 1} other apps",
        };
    }
}
