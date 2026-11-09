using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Navigation;

public enum AddressKind { Invalid, Location, Url, Command }

public sealed record AddressParseResult(AddressKind Kind, string Text, string? Arguments = null);

public static class PathParser
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["this pc"] = ShellLocation.Known.ThisPC,
        ["computer"] = ShellLocation.Known.ThisPC,
        ["my computer"] = ShellLocation.Known.ThisPC,
        ["recycle bin"] = ShellLocation.Known.RecycleBin,
        ["control panel"] = ShellLocation.Known.ControlPanel,
        ["network"] = ShellLocation.Known.Network,
        ["home"] = ShellLocation.Known.Home,
        ["quick access"] = ShellLocation.Known.Home,
        ["desktop"] = "shell:Desktop",
        ["downloads"] = "shell:Downloads",
        ["documents"] = "shell:Personal",
        ["pictures"] = "shell:My Pictures",
        ["music"] = "shell:My Music",
        ["videos"] = "shell:My Video",
        ["linux"] = ShellLocation.Known.Linux,
    };

    private static readonly string[] UrlSchemes = ["http://", "https://", "mailto:", "ms-settings:"];

    public static AddressParseResult Parse(string? input, string? currentDirectory)
    {
        var text = (input ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        if (text.Length == 0) return new(AddressKind.Invalid, "");

        foreach (var scheme in UrlSchemes)
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return new(AddressKind.Url, text);

        // Before expanding variables: a search URL is full of %XX escapes.
        if (SearchLocation.IsSearchUrl(text)) return new(AddressKind.Location, text);

        text = Environment.ExpandEnvironmentVariables(text);

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("::", StringComparison.Ordinal) ||
            text.StartsWith(@"\\", StringComparison.Ordinal))
            return new(AddressKind.Location, text);

        // "c:" or "c" + ":" → drive root
        if (text.Length == 2 && char.IsLetter(text[0]) && text[1] == ':') return new(AddressKind.Location, text + "\\");

        if (Path.IsPathFullyQualified(text)) return new(AddressKind.Location, Normalize(text));

        // Relative to the current folder ("..", "src\app", "Downloads" if such a subfolder exists).
        if (currentDirectory is not null && text.IndexOfAny(Path.GetInvalidPathChars()) < 0)
        {
            try
            {
                var combined = Path.GetFullPath(Path.Combine(currentDirectory, text));
                if (Directory.Exists(combined) || File.Exists(combined)) return new(AddressKind.Location, Normalize(combined));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        if (Aliases.TryGetValue(text, out var alias)) return new(AddressKind.Location, alias);

        // Anything else runs like the Run dialog, with the current folder as working directory.
        var (exe, args) = SplitCommand(text);
        return new(AddressKind.Command, exe, args);
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }
        catch { return path; }
    }

    private static (string, string?) SplitCommand(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return (text[1..end], NullIfEmpty(text[(end + 1)..].Trim()));
        }
        var space = text.IndexOf(' ');
        return space < 0 ? (text, null) : (text[..space], NullIfEmpty(text[(space + 1)..].Trim()));
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
