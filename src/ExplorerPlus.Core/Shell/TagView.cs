using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public static class TagView
{
    public const string Scheme = "explorerplus-tag:";

    // the paths that carry a tag; set by the app
    public static Func<string, IReadOnlyList<string>>? Paths { get; set; }

    public static bool IsUrl(string? text) => text?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static string Url(string tag) => Scheme + Uri.EscapeDataString(tag);

    // must run on an STA thread
    public static ShellLocation? FromUrl(string url)
    {
        if (!IsUrl(url)) return null;
        string tag;
        try { tag = Uri.UnescapeDataString(url[Scheme.Length..]); }
        catch (UriFormatException) { return null; }
        if (tag.Length == 0) return null;
        var pidl = ShellNative.TryParse(ShellLocation.Known.ThisPC);
        if (pidl is null) return null;
        return new ShellLocation
        {
            Pidl = pidl,
            DisplayName = $"Tagged “{tag}”",
            ParsingName = Url(tag),
            Attributes = SFGAO.FOLDER,
            Tag = tag,
        };
    }

    internal static IEnumerable<FolderEntry> Enumerate(ShellLocation location, CancellationToken ct)
    {
        var paths = Paths?.Invoke(location.Tag!) ?? [];
        for (var i = 0; i < paths.Count; i += 256)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in HomeFeed.StatAll(paths.Skip(i).Take(256).ToList(), ct)) yield return entry;
        }
    }
}
