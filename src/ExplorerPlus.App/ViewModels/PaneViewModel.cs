using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

public enum PreviewKind { Message, Image, Text, Handler }

public sealed record PreviewTarget(Guid Handler, string Path);

public sealed class PaneViewModel : ObservableObject
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".jxr", ".wdp", ".avif",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".json", ".jsonc", ".xml", ".yml", ".yaml", ".ini", ".cfg", ".conf", ".config", ".csv", ".tsv",
        ".cs", ".xaml", ".csproj", ".vbproj", ".fsproj", ".sln", ".props", ".targets", ".resx", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".css", ".scss", ".less", ".vue", ".svelte", ".py", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt", ".kts", ".go", ".rs", ".rb", ".php", ".sql", ".toml", ".gitignore", ".gitattributes",
        ".editorconfig", ".env", ".lua", ".swift", ".vb", ".fs", ".r", ".dart", ".gradle", ".properties", ".reg", ".nfo", ".srt", ".vtt", ".diff",
        ".patch", ".tex", ".gql", ".graphql", ".proto", ".cmake", ".mk", ".dockerfile",
    };

    private const int TextLimit = 256 * 1024;

    private readonly DispatcherTimer _timer;
    private TabViewModel? _tab;
    private int _version;

    private PaneMode _mode;
    private string _title = "";
    private string _subtitle = "";
    private ImageSource? _image;
    private PreviewKind _kind = PreviewKind.Message;
    private string _message = "";
    private ImageSource? _previewImage;
    private string _previewText = "";
    private PreviewTarget? _handlerTarget;

    public PaneViewModel()
    {
        _mode = AppSettings.Current.Pane;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => { _timer.Stop(); _ = RefreshAsync(); };
    }

    public PaneMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            AppSettings.Current.Pane = value;
            AppSettings.Current.Save();
            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(IsPreview));
            OnPropertyChanged(nameof(IsDetails));
            if (value != PaneMode.Preview) HandlerTarget = null;
            Schedule(immediate: true);
        }
    }

    public bool IsOpen => _mode != PaneMode.None;
    public bool IsPreview => _mode == PaneMode.Preview;
    public bool IsDetails => _mode == PaneMode.Details;

    /// <summary>Alt+P / Alt+Shift+P: the same key opens and closes its pane, and switches from the other one.</summary>
    public void Toggle(PaneMode mode) => Mode = Mode == mode ? PaneMode.None : mode;

    // ---------- Details ----------

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public ImageSource? Image { get => _image; private set => Set(ref _image, value); }
    public ObservableCollection<DetailRow> Rows { get; } = [];

    // ---------- Preview ----------

    public PreviewKind Kind
    {
        get => _kind;
        private set
        {
            if (!Set(ref _kind, value)) return;
            OnPropertyChanged(nameof(ShowMessage));
            OnPropertyChanged(nameof(ShowImage));
            OnPropertyChanged(nameof(ShowText));
            OnPropertyChanged(nameof(ShowHandler));
        }
    }

    public bool ShowMessage => _kind == PreviewKind.Message;
    public bool ShowImage => _kind == PreviewKind.Image;
    public bool ShowText => _kind == PreviewKind.Text;
    public bool ShowHandler => _kind == PreviewKind.Handler;

    public string Message { get => _message; private set => Set(ref _message, value); }
    public ImageSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }

    /// <summary>A native preview handler should draw this file; the window hosts it.</summary>
    public PreviewTarget? HandlerTarget { get => _handlerTarget; private set => Set(ref _handlerTarget, value); }

    /// <summary>The handler couldn't show the file (not installed properly, failed to load…).</summary>
    public void HandlerFailed(PreviewTarget target)
    {
        if (target != _handlerTarget) return;
        HandlerTarget = null;
        ShowMessageText("No preview available.");
    }

    // ---------- Input ----------

    public void Attach(TabViewModel? tab)
    {
        if (_tab == tab) return;
        if (_tab is not null) _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = tab;
        if (_tab is not null) _tab.PropertyChanged += OnTabPropertyChanged;
        Schedule(immediate: true);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.SelectedItems) or nameof(TabViewModel.Location)) Schedule(immediate: false);
    }

    /// <summary>Selection changes are coalesced so arrowing through a folder doesn't start a preview per item.</summary>
    private void Schedule(bool immediate)
    {
        _timer.Stop();
        if (!IsOpen) { ++_version; return; }
        if (immediate) _ = RefreshAsync();
        else _timer.Start();
    }

    private async Task RefreshAsync()
    {
        var version = ++_version;
        var tab = _tab;
        var selection = tab?.SelectedItems ?? [];
        if (_mode == PaneMode.Details) await LoadDetailsAsync(version, tab, selection);
        else if (_mode == PaneMode.Preview) await LoadPreviewAsync(version, selection);
    }

    // ---------- Details pane ----------

    private async Task LoadDetailsAsync(int version, TabViewModel? tab, IReadOnlyList<ItemViewModel> selection)
    {
        Rows.Clear();
        if (selection.Count == 1)
        {
            var item = selection[0];
            Title = item.DisplayName;
            Subtitle = item.TypeName;
            Image = IconCache.Get(item.IconIndex, 256);
            var path = item.Entry.Path;
            var pidl = item.Entry.Pidl ?? (path is null ? await item.Entry.GetPidlAsync() : null);
            ThumbnailService.Request(path, pidl, 256, img => { if (version == _version && img is not null) Image = img; }, () => { });

            var details = await StaWorkerPool.Properties.Run(() => PropertyReader.PreviewDetails(path, pidl));
            if (version != _version) return;
            foreach (var (name, label, value) in details)
            {
                if (name is "System.ItemTypeText" or "System.ItemNameDisplay") continue;
                Rows.Add(new DetailRow(label, value));
            }
            if (item.IsFolder && item.FolderSize is { } folderSize && !details.Any(d => d.Name == "System.Size"))
                Rows.Insert(0, new DetailRow("Size", ItemViewModel.FormatSizeShort(folderSize)));
            return;
        }

        if (selection.Count > 1)
        {
            Title = $"{selection.Count} items selected";
            Subtitle = "";
            Image = IconCache.Get(selection[0].IconIndex, 256);
            var sizes = selection.Select(i => i.SortSize).Where(s => s >= 0).ToList();
            if (sizes.Count > 0) Rows.Add(new DetailRow("Total size", ItemViewModel.FormatSizeShort(sizes.Sum())));
            var dates = selection.Select(i => i.Entry.Modified).OfType<DateTime>().ToList();
            if (dates.Count > 0)
            {
                var (min, max) = (dates.Min(), dates.Max());
                Rows.Add(new DetailRow("Date modified", min.Date == max.Date ? max.ToString("g") : $"{min:d} – {max:d}"));
            }
            var folders = selection.Count(i => i.IsFolder);
            if (folders > 0 && folders < selection.Count) Rows.Add(new DetailRow("Contains", $"{selection.Count - folders} files, {folders} folders"));
            return;
        }

        // Nothing selected: describe the folder itself.
        var location = tab?.Location;
        Title = location?.DisplayName ?? "";
        Subtitle = tab is null ? "" : $"{tab.Items.Count} item{(tab.Items.Count == 1 ? "" : "s")}";
        Image = null;
        if (location is null) return;
        var folderDetails = await StaWorkerPool.Properties.Run(() => PropertyReader.PreviewDetails(location.FileSystemPath, location.Pidl));
        if (version != _version) return;
        foreach (var (name, label, value) in folderDetails)
            if (name is not ("System.ItemTypeText" or "System.ItemNameDisplay")) Rows.Add(new DetailRow(label, value));
    }

    // ---------- Preview pane ----------

    private void ShowMessageText(string text)
    {
        Message = text;
        PreviewImage = null;
        PreviewText = "";
        Kind = PreviewKind.Message;
    }

    private async Task LoadPreviewAsync(int version, IReadOnlyList<ItemViewModel> selection)
    {
        if (selection.Count != 1)
        {
            HandlerTarget = null;
            ShowMessageText(selection.Count == 0 ? "Select a file to preview." : "Select a single file to preview.");
            return;
        }
        var item = selection[0];
        var path = item.Entry.Path;
        if (item.IsFolder || path is null)
        {
            HandlerTarget = null;
            ShowMessageText(item.IsFolder ? "Select a file to preview." : "No preview available.");
            return;
        }

        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext) && await Task.Run(() => DecodeImage(path, 1600)) is { } picture)
        {
            if (version != _version) return;
            HandlerTarget = null;
            PreviewImage = picture;
            Kind = PreviewKind.Image;
            return;
        }
        if (version != _version) return;

        var handler = PreviewHandlers.Find(ext);
        if (TextExtensions.Contains(ext) || (handler is null && item.Entry.Size is >= 0 and < 4 * 1024 * 1024))
        {
            var text = await Task.Run(() => ReadText(path, requireText: !TextExtensions.Contains(ext)));
            if (version != _version) return;
            if (text is not null)
            {
                HandlerTarget = null;
                PreviewText = text;
                Kind = PreviewKind.Text;
                return;
            }
        }

        if (handler is { } clsid)
        {
            HandlerTarget = new PreviewTarget(clsid, path);
            Kind = PreviewKind.Handler;
            return;
        }

        // No handler: a big thumbnail is still better than nothing (videos, fonts, 3D models…).
        HandlerTarget = null;
        ShowMessageText("No preview available.");
        ThumbnailService.Request(path, item.Entry.Pidl, 512, img =>
        {
            if (version != _version || img is null) return;
            PreviewImage = img;
            Kind = PreviewKind.Image;
        }, () => { });
    }

    private static BitmapSource? DecodeImage(string path, int maxSize)
    {
        try
        {
            int width, height;
            ushort orientation = 1;
            using (var probe = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
                try
                {
                    if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
                }
                catch (Exception) { }
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (width > maxSize || height > maxSize)
            {
                if (width >= height) image.DecodePixelWidth = maxSize;
                else image.DecodePixelHeight = maxSize;
            }
            image.EndInit();

            // Phones store photos sideways plus an EXIF orientation; Explorer shows them upright.
            BitmapSource result = image;
            var angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            if (angle != 0) result = new TransformedBitmap(image, new RotateTransform(angle));
            result.Freeze();
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadText(string path, bool requireText)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(TextLimit, Math.Max(0, stream.Length))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var bytes = buffer.AsSpan(0, read);

            var hasBom = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) || bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF });
            // Binary files have NUL bytes early on (UTF-16 text has them too, but then it has a BOM).
            if (requireText && !hasBom && bytes[..Math.Min(bytes.Length, 8192)].IndexOf((byte)0) >= 0) return null;

            using var reader = new StreamReader(new MemoryStream(buffer, 0, read), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (stream.Length > TextLimit) text += "\n\n…";
            return text;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
