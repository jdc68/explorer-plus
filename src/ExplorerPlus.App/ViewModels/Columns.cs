namespace ExplorerPlus.App.ViewModels;

/// <summary>Locations that get their own defaults (columns, grouping, actions).</summary>
public enum LocationKind { Folder, Home, ThisPC, Network, RecycleBin, Search }

public sealed record ColumnDef(string Id, string Header, double Width, bool RightAlign = false, string Category = "Common")
{
    /// <summary>Built-in columns come from the listing itself; the rest are shell properties read on demand.</summary>
    public bool IsBuiltIn => ColumnCatalog.BuiltIn.Contains(Id);
}

public static class ColumnCatalog
{
    public const string Name = "Name";
    public const string Modified = "Modified";
    public const string Type = "Type";
    public const string Size = "Size";
    public const string Created = "Created";
    public const string Folder = "System.ItemFolderPathDisplay";
    public const string Git = "ExplorerPlus.Git";
    public const string Tags = "ExplorerPlus.Tags";

    // answered by Explorer+ itself rather than by the shell
    public static bool IsCustom(string id) => id.StartsWith("ExplorerPlus.", StringComparison.Ordinal);

    /// <summary>Not a column: Home's grouping by section (Quick access, Starred, Recent files).</summary>
    public const string Section = "Section";

    public static readonly HashSet<string> BuiltIn = [Name, Modified, Type, Size, Created, Section];

    public static readonly ColumnDef[] All =
    [
        new(Name, "Name", 360),
        new(Modified, "Date modified", 160),
        new(Type, "Type", 170),
        new(Size, "Size", 110, RightAlign: true),
        new(Created, "Date created", 160),
        new("System.DateAccessed", "Date accessed", 160),
        new("System.FileAttributes", "Attributes", 90),
        new("System.FileOwner", "Owner", 170),
        new(Folder, "Location", 260),
        new(Git, "Git status", 110, Category: "Explorer+"),
        new(Tags, "Tags", 160, Category: "Explorer+"),
        new("System.Author", "Authors", 170, Category: "Documents"),
        new("System.Title", "Title", 200, Category: "Documents"),
        new("System.Keywords", "Keywords", 160, Category: "Documents"),
        new("System.Comment", "Comments", 200, Category: "Documents"),
        new("System.Rating", "Rating", 100, Category: "Documents"),
        new("System.Document.PageCount", "Pages", 70, RightAlign: true, Category: "Documents"),
        new("System.Company", "Company", 160, Category: "Documents"),
        new("System.Music.TrackNumber", "#", 50, RightAlign: true, Category: "Music"),
        new("System.Music.Artist", "Contributing artists", 180, Category: "Music"),
        new("System.Music.AlbumArtist", "Album artist", 160, Category: "Music"),
        new("System.Music.AlbumTitle", "Album", 180, Category: "Music"),
        new("System.Music.Genre", "Genre", 120, Category: "Music"),
        new("System.Media.Year", "Year", 60, RightAlign: true, Category: "Music"),
        new("System.Media.Duration", "Length", 80, RightAlign: true, Category: "Media"),
        new("System.Audio.EncodingBitrate", "Bit rate", 90, RightAlign: true, Category: "Media"),
        new("System.Video.FrameWidth", "Frame width", 90, RightAlign: true, Category: "Media"),
        new("System.Video.FrameHeight", "Frame height", 90, RightAlign: true, Category: "Media"),
        new("System.Image.Dimensions", "Dimensions", 110, Category: "Pictures"),
        new("System.Photo.DateTaken", "Date taken", 160, Category: "Pictures"),
        new("System.Photo.CameraModel", "Camera model", 150, Category: "Pictures"),
        new("System.FileVersion", "File version", 120, Category: "Programs"),
        new("System.Software.ProductName", "Product name", 160, Category: "Programs"),
        new("System.Capacity", "Total size", 110, RightAlign: true, Category: "Drives"),
        new("System.FreeSpace", "Free space", 110, RightAlign: true, Category: "Drives"),
        new("System.Recycle.DeletedFrom", "Original location", 240, Category: "Recycle Bin"),
        new("System.Recycle.DateDeleted", "Date deleted", 160, Category: "Recycle Bin"),
    ];

    private static readonly Dictionary<string, ColumnDef> ById = All.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    public static ColumnDef? Get(string id) => ById.GetValueOrDefault(id);

    public static List<string> DefaultFor(FolderContent content, LocationKind kind) => kind switch
    {
        LocationKind.ThisPC => [Name, Type, "System.Capacity", "System.FreeSpace"],
        LocationKind.Home => [Name, Modified, Folder],
        LocationKind.Search => [Name, Modified, Type, Size, Folder],
        LocationKind.RecycleBin => [Name, "System.Recycle.DeletedFrom", "System.Recycle.DateDeleted", Size, Type],
        _ => content switch
        {
            FolderContent.Music => [Name, "System.Music.TrackNumber", "System.Title", "System.Music.Artist", "System.Music.AlbumTitle", "System.Media.Duration", Size],
            FolderContent.Pictures => [Name, "System.Photo.DateTaken", "System.Image.Dimensions", Type, Size],
            FolderContent.Videos => [Name, Modified, Type, Size, "System.Media.Duration"],
            _ => [Name, Modified, Type, Size],
        },
    };
}
