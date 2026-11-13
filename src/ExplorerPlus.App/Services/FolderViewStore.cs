using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using ExplorerPlus.App.ViewModels;

namespace ExplorerPlus.App.Services;

public sealed class FolderViewState
{
    public ViewMode Mode { get; set; } = ViewMode.Details;

    // false: Mode was only saved along with a sort or column change, so the content rules still pick the view
    public bool ModeSet { get; set; }
    public SortColumn Sort { get; set; } = SortColumn.Name;
    public string? SortProperty { get; set; }
    public bool Ascending { get; set; } = true;
    public string? GroupBy { get; set; }
    public List<string>? Columns { get; set; }

    // kept apart from Columns, so resizing a column doesn't fix which columns the folder shows
    public Dictionary<string, double>? Widths { get; set; }
    public DateTime Used { get; set; }
}

public static class FolderViewStore
{
    private const int MaxFolders = 5000;
    private static Dictionary<string, FolderViewState>? _states;
    private static DispatcherTimer? _saveTimer;

    private static string FilePath =>
        Path.Combine(AppPaths.DataFolder, "views.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    private static Dictionary<string, FolderViewState> States
    {
        get
        {
            if (_states is not null) return _states;
            try
            {
                _states = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, FolderViewState>>(File.ReadAllText(FilePath), Json)
                    : null;
            }
            catch { _states = null; }
            _states = new Dictionary<string, FolderViewState>(_states ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var state in _states.Values) ForgetOldDefaultColumns(state);
            return _states;
        }
    }

    // older versions saved the whole column list on a resize; a list equal to an old default came from that
    private static readonly string[][] OldDefaults =
    [
        ["Name", "Modified", "Type", "Size"],
        ["Name", "Modified", "Type", "Size", "System.Media.Duration"],
        ["Name", "System.Music.TrackNumber", "System.Title", "System.Music.Artist", "System.Music.AlbumTitle", "System.Media.Duration", "Size"],
        ["Name", "System.Photo.DateTaken", "System.Image.Dimensions", "Type", "Size"],
    ];

    public static void ForgetOldDefaultColumns(FolderViewState state)
    {
        if (state.Columns is not { Count: > 0 } columns) return;
        var parts = columns.Select(c => c.Split('|')).ToList();
        if (!OldDefaults.Any(d => d.SequenceEqual(parts.Select(p => p[0]), StringComparer.OrdinalIgnoreCase))) return;
        foreach (var p in parts)
            if (p.Length > 1 && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w))
                (state.Widths ??= new(StringComparer.OrdinalIgnoreCase))[p[0]] = w;
        state.Columns = null;
    }

    /// <summary>The view the user chose for this folder, or null if they never changed it.</summary>
    public static FolderViewState? Get(string key) => States.GetValueOrDefault(key);

    public static void Set(string key, FolderViewState state)
    {
        state.Used = DateTime.UtcNow;
        States[key] = state;
        if (States.Count > MaxFolders)
            foreach (var old in States.OrderBy(kv => kv.Value.Used).Take(States.Count - MaxFolders).Select(kv => kv.Key).ToList())
                States.Remove(old);
        ScheduleSave();
    }

    public static void Remove(string key)
    {
        if (States.Remove(key)) ScheduleSave();
    }

    /// <summary>Folders with a view set by hand.</summary>
    public static int Count => States.Count;

    /// <summary>Forgets every folder's view, so all folders go back to the automatic one.</summary>
    public static void Clear()
    {
        States.Clear();
        Save();
    }

    private static void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); Save(); },
            Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void Save()
    {
        if (_states is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_states, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
