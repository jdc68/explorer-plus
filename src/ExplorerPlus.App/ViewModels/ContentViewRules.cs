using System.Text.Json.Serialization;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.ViewModels;

public enum FolderContent { General, Pictures, Videos, Music, Documents }

public sealed class ViewRule
{
    [JsonConverter(typeof(JsonStringEnumConverter<FolderContent>))]
    public FolderContent Content { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ViewMode>))]
    public ViewMode View { get; set; }

    // without the dot, lower case; empty for General
    public List<string> Extensions { get; set; } = [];
}

public static class ContentViewRules
{
    // a kind wins when it makes up this share of the files
    public const double Threshold = 0.6;

    public static readonly FolderContent[] Kinds = [FolderContent.Pictures, FolderContent.Videos, FolderContent.Music, FolderContent.Documents, FolderContent.General];

    public static string Label(FolderContent content) => content switch
    {
        FolderContent.Pictures => "Pictures",
        FolderContent.Videos => "Videos",
        FolderContent.Music => "Music",
        FolderContent.Documents => "Documents and code",
        _ => "Mixed and other folders",
    };

    public static List<ViewRule> Defaults() =>
    [
        new()
        {
            Content = FolderContent.Pictures, View = ViewMode.LargeIcons, Extensions =
            [
                "jpg", "jpeg", "jfif", "png", "gif", "bmp", "webp", "heic", "heif", "avif", "jxl", "tif", "tiff", "ico", "svg",
                "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "psd",
            ],
        },
        new()
        {
            Content = FolderContent.Videos, View = ViewMode.LargeIcons, Extensions =
            [
                "mp4", "mkv", "mov", "avi", "wmv", "webm", "m4v", "mpg", "mpeg", "3gp", "flv", "mts", "m2ts", "vob",
            ],
        },
        new()
        {
            Content = FolderContent.Music, View = ViewMode.Details, Extensions =
            [
                "mp3", "flac", "wav", "m4a", "aac", "ogg", "oga", "opus", "wma", "aiff", "aif", "ape", "alac", "mid", "midi", "amr",
            ],
        },
        new()
        {
            Content = FolderContent.Documents, View = ViewMode.Details, Extensions =
            [
                "txt", "md", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "csv", "log", "epub",
                "json", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf", "html", "htm", "css", "scss", "sql",
                "cs", "csproj", "sln", "xaml", "js", "jsx", "ts", "tsx", "mjs", "py", "java", "kt", "go", "rs", "rb", "php", "swift",
                "c", "cpp", "cc", "h", "hpp", "lua", "sh", "ps1", "psm1", "bat", "cmd",
            ],
        },
        new() { Content = FolderContent.General, View = ViewMode.Details },
    ];

    // fills in kinds missing from older settings files
    public static List<ViewRule> Complete(List<ViewRule>? rules)
    {
        var list = (rules ?? []).Where(r => Kinds.Contains(r.Content)).DistinctBy(r => r.Content).ToList();
        foreach (var d in Defaults())
            if (list.All(r => r.Content != d.Content)) list.Add(d);
        foreach (var r in list)
        {
            r.Extensions = r.Content == FolderContent.General ? [] : r.Extensions.Select(NormalizeExtension).Where(e => e.Length > 0).Distinct().ToList();
            if (!Enum.IsDefined(r.View)) r.View = ViewMode.Details;
        }
        return list.OrderBy(r => Array.IndexOf(Kinds, r.Content)).ToList();
    }

    public static string NormalizeExtension(string ext) => ext.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// Classifies a folder from the extensions already in the listing (no file is opened, unlike Explorer's
    /// folder-type discovery). A kind wins when it makes up at least 60% of the files; pictures and videos
    /// count together, so a camera folder with a few clips still opens as thumbnails.
    /// </summary>
    public static FolderContent Classify(IEnumerable<FolderEntry> entries, IReadOnlyList<ViewRule> rules)
    {
        var kindOf = new Dictionary<string, FolderContent>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
            foreach (var ext in rule.Extensions)
                kindOf.TryAdd("." + ext, rule.Content);

        int files = 0, folders = 0;
        var counts = new Dictionary<FolderContent, int>();
        foreach (var e in entries)
        {
            if (e.IsFolder) { folders++; continue; }
            files++;
            if (kindOf.TryGetValue(e.Extension, out var kind)) counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        // A couple of stray files in a folder of folders say little about it.
        if (files == 0 || files < 3 && folders > files) return FolderContent.General;
        var threshold = files * Threshold;
        int pictures = counts.GetValueOrDefault(FolderContent.Pictures), videos = counts.GetValueOrDefault(FolderContent.Videos);
        if (pictures + videos >= threshold) return pictures >= videos ? FolderContent.Pictures : FolderContent.Videos;
        if (counts.GetValueOrDefault(FolderContent.Music) >= threshold) return FolderContent.Music;
        if (counts.GetValueOrDefault(FolderContent.Documents) >= threshold) return FolderContent.Documents;
        return FolderContent.General;
    }

    public static ViewMode ModeFor(FolderContent content, IReadOnlyList<ViewRule> rules) =>
        rules.FirstOrDefault(r => r.Content == content)?.View ?? ViewMode.Details;
}
