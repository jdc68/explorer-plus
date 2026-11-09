namespace ExplorerPlus.App.ViewModels;

public enum FolderContent { General, Pictures, Videos, Music, Documents }

public static class ContentViewRules
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".avif", ".jxl", ".tif", ".tiff", ".ico",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".psd",
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".flv", ".mts", ".m2ts",
    };

    private static readonly HashSet<string> Music = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".ape", ".alac",
    };

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".rtf", ".csv", ".log",
        ".json", ".xml", ".yaml", ".yml", ".ini", ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".html", ".css", ".sql",
    };

    /// <summary>
    /// Classifies a folder from the extensions already in the listing (no file is opened, unlike Explorer's
    /// folder-type discovery). A type wins when it makes up at least 60% of the files.
    /// </summary>
    public static FolderContent Classify(IReadOnlyCollection<ItemViewModel> items)
    {
        int files = 0, pictures = 0, videos = 0, music = 0, documents = 0, folders = 0;
        foreach (var i in items)
        {
            if (i.IsFolder) { folders++; continue; }
            files++;
            var ext = i.Entry.Extension;
            if (Pictures.Contains(ext)) pictures++;
            else if (Videos.Contains(ext)) videos++;
            else if (Music.Contains(ext)) music++;
            else if (Documents.Contains(ext)) documents++;
        }
        // A couple of stray files in a folder of folders say little about it.
        if (files == 0 || files < 3 && folders > files) return FolderContent.General;
        var threshold = files * 0.6;
        if (pictures + videos >= threshold) return pictures >= videos ? FolderContent.Pictures : FolderContent.Videos;
        if (music >= threshold) return FolderContent.Music;
        if (documents >= threshold) return FolderContent.Documents;
        return FolderContent.General;
    }

    public static ViewMode ModeFor(FolderContent content) => content switch
    {
        FolderContent.Pictures or FolderContent.Videos => ViewMode.LargeIcons,
        _ => ViewMode.Details,
    };
}
