namespace ExplorerPlus.Core.Shell;

public sealed class ShellFilter
{
    public bool HideGallery { get; set; } = true;
    public bool HideLibraries { get; set; } = true;

    public static ShellFilter Default { get; set; } = new();

    private static readonly string[] OneDriveRoots = GetOneDriveRoots();

    public bool IsBlocked(string? parsingName, string? fileSystemPath)
    {
        if (parsingName is not null)
        {
            if (Contains(parsingName, ShellLocation.Known.OneDriveClsid)) return true;
            if (HideGallery && Contains(parsingName, ShellLocation.Known.GalleryClsid)) return true;
            if (HideLibraries && Contains(parsingName, ShellLocation.Known.LibrariesClsid)) return true;
        }
        if (fileSystemPath is not null)
        {
            foreach (var root in OneDriveRoots)
                if (string.Equals(fileSystemPath.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool Contains(string s, string clsid) => s.Contains(clsid, StringComparison.OrdinalIgnoreCase);

    private static string[] GetOneDriveRoots() =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
