namespace ExplorerPlus.App.ViewModels;

// Same order as Explorer's Ctrl+Shift+1…8 shortcuts.
public enum ViewMode { ExtraLargeIcons, LargeIcons, MediumIcons, SmallIcons, List, Details, Tiles, Content }

public static class ViewModes
{
    public static readonly ViewMode[] All = Enum.GetValues<ViewMode>();

    public static string Label(ViewMode mode) => mode switch
    {
        ViewMode.ExtraLargeIcons => "Extra large icons",
        ViewMode.LargeIcons => "Large icons",
        ViewMode.MediumIcons => "Medium icons",
        ViewMode.SmallIcons => "Small icons",
        ViewMode.List => "List",
        ViewMode.Details => "Details",
        ViewMode.Tiles => "Tiles",
        _ => "Content",
    };

    public static string Glyph(ViewMode mode) => mode switch
    {
        ViewMode.ExtraLargeIcons or ViewMode.LargeIcons or ViewMode.MediumIcons => "",
        ViewMode.SmallIcons => "",
        ViewMode.List => "",
        ViewMode.Details => "",
        ViewMode.Tiles => "",
        _ => "",
    };

    /// <summary>Image size the items show in this mode; 0 means the 16 px system icon.</summary>
    public static int ImageSize(ViewMode mode) => mode switch
    {
        ViewMode.ExtraLargeIcons => 256,
        ViewMode.LargeIcons => 96,
        ViewMode.MediumIcons or ViewMode.Tiles or ViewMode.Content => 48,
        _ => 0,
    };

    public static bool UsesThumbnails(ViewMode mode) => ImageSize(mode) > 0;
}
