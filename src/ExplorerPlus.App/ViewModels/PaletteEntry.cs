namespace ExplorerPlus.App.ViewModels;

public sealed class PaletteEntry(string title, Action run, string? gesture = null, string? detail = null, string? glyph = null)
{
    public string Title { get; } = title;
    public string Gesture { get; } = gesture ?? "";
    public string Detail { get; } = detail ?? "";
    public string Glyph { get; } = glyph ?? "";
    public Action Run { get; } = run;

    /// <summary>Set while filtering: places where the entry only matches on its detail line rank lower.</summary>
    public int Score { get; set; }
}
