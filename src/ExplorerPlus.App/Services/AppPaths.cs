using System.IO;

namespace ExplorerPlus.App.Services;

public static class AppPaths
{
    /// <summary>
    /// EXPLORERPLUS_PROFILE=name runs a separate instance with its own settings, e.g. a dev build next to the
    /// everyday one: it neither forwards to the running instance nor touches its settings.
    /// </summary>
    public static string? Profile { get; } = Sanitize(Environment.GetEnvironmentVariable("EXPLORERPLUS_PROFILE"));

    public static string DataFolder { get; } = Profile is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerPlus")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplorerPlus", "profiles", Profile);

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? null : clean;
    }
}
