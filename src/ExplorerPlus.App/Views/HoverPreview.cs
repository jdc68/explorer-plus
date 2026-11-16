using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Views;

public sealed class HoverPreview
{
    // HoverPreview, the standalone tool, leaves windows with this property to us
    public const string WindowMark = "ExplorerPlus.HoverPreview";

    private const int TextBytes = 64 * 1024, TextChars = 8000, TextLines = 200;
    private const int GlideMs = 1500, GlideDelayMs = 100;

    private readonly Window _owner;
    private readonly DispatcherTimer _rest = new(DispatcherPriority.Input);
    private readonly DispatcherTimer _recheck = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _idle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private HoverPopup? _popup;
    private ListView? _list;
    private Point _last;
    private ItemViewModel? _pending;
    private ItemViewModel? _shown;
    private CancellationTokenSource? _load;
    private long _hiddenAt = long.MinValue / 2;
    private IntPtr _hwnd;
    private bool _marked;

    public HoverPreview(Window owner)
    {
        _owner = owner;
        _rest.Tick += (_, _) =>
        {
            _rest.Stop();
            _ = BeginAsync();
        };
        _recheck.Tick += (_, _) => Recheck();
        _idle.Tick += (_, _) =>
        {
            // a closed popup holds no memory between bursts of hovering
            _idle.Stop();
            _popup?.Close();
        };
        owner.PreviewMouseDown += (_, _) => Cancel();
        owner.PreviewMouseWheel += (_, _) => Cancel();
        owner.PreviewKeyDown += (_, _) => Cancel();
        owner.Deactivated += (_, _) => Cancel();
        owner.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(owner).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            UpdateMark();
        };
        AppSettings.Current.Changed += OnSettingsChanged;
        owner.Closed += (_, _) =>
        {
            AppSettings.Current.Changed -= OnSettingsChanged;
            Cancel();
            _idle.Stop();
        };
    }

    public void Attach(ListView list)
    {
        list.MouseMove += OnMouseMove;
        list.MouseLeave += (_, _) => Cancel();
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) Cancel();
        }));
    }

    private void OnSettingsChanged()
    {
        UpdateMark();
        if (!AppSettings.Current.HoverPreview) Cancel();
    }

    private void UpdateMark()
    {
        var on = AppSettings.Current.HoverPreview;
        if (_hwnd == IntPtr.Zero || on == _marked) return;
        if (on) User32.SetPropW(_hwnd, WindowMark, 1);
        else User32.RemovePropW(_hwnd, WindowMark);
        _marked = on;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_DESTROY && _marked)
        {
            User32.RemovePropW(hwnd, WindowMark);
            _marked = false;
        }
        return IntPtr.Zero;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!AppSettings.Current.HoverPreview) return;
        var list = (ListView)sender;
        var pos = e.GetPosition(list);
        // WPF repeats the last move after every layout; only real movement counts
        if (ReferenceEquals(list, _list) && Math.Abs(pos.X - _last.X) <= 2 && Math.Abs(pos.Y - _last.Y) <= 2) return;
        _list = list;
        _last = pos;

        if (_shown is not null)
        {
            if (ItemAt(list, out _) == _shown && _popup?.ContainsCursor() != true) return;
            Hide();
        }
        if (_pending is not null && ItemAt(list, out _) != _pending) CancelLoad();

        _rest.Stop();
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed) return;
        var delay = Math.Clamp(AppSettings.Current.HoverDelayMs, 50, 3000);
        // right after a preview closed the next one opens quicker, so you can glide over a folder
        if (Environment.TickCount64 - _hiddenAt < GlideMs) delay = Math.Min(delay, GlideDelayMs);
        _rest.Interval = TimeSpan.FromMilliseconds(delay);
        _rest.Start();
    }

    private async Task BeginAsync()
    {
        var settings = AppSettings.Current;
        if (_list is not { IsMouseOver: true } list || _shown is not null || !settings.HoverPreview) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed) return;
        var item = ItemAt(list, out var hit);
        if (item is null || item == _pending || item.IsFolder || item.Entry.Path is not { } path || HasOwnUi(hit)) return;
        if (PreviewLoader.KindOf(path) == MediaKind.None) return;

        CancelLoad();
        var cts = new CancellationTokenSource();
        _load = cts;
        _pending = item;
        var place = HoverPlace.At(settings.HoverSize);
        HoverContent? content;
        try
        {
            content = await HoverContent.LoadAsync(path, place, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (_load == cts)
            {
                _load = null;
                _pending = null;
            }
        }
        if (cts.IsCancellationRequested || content is null || _shown is not null) return;
        // the load took a moment: the mouse may have left the item
        if (!list.IsMouseOver || ItemAt(list, out _) != item) return;

        _idle.Stop();
        if (_popup is null)
        {
            var popup = new HoverPopup(_owner);
            popup.Closed += (_, _) => { if (_popup == popup) _popup = null; };
            _popup = popup;
        }
        _shown = item;
        _recheck.Start();
        content.ShowIn(_popup, place, Path.GetFileName(path), settings.HoverSound);
    }

    // the list may have scrolled or reloaded under a resting mouse
    private void Recheck()
    {
        if (_shown is null) { _recheck.Stop(); return; }
        if (_list is not { IsMouseOver: true } list || ItemAt(list, out _) != _shown) Hide();
    }

    private void Hide()
    {
        if (_shown is null) return;
        _shown = null;
        _recheck.Stop();
        _popup?.Clear();
        _hiddenAt = Environment.TickCount64;
        _idle.Start();
    }

    private void Cancel()
    {
        _rest.Stop();
        CancelLoad();
        Hide();
    }

    private void CancelLoad()
    {
        _load?.Cancel();
        _load = null;
        _pending = null;
    }

    private static ItemViewModel? ItemAt(ListView list, out DependencyObject? hit)
    {
        hit = list.InputHitTest(Mouse.GetPosition(list)) as DependencyObject;
        // text runs aren't visuals: start from the element that holds them
        while (hit is not null and not Visual) hit = LogicalTreeHelper.GetParent(hit);
        if (hit is null) return null;
        return ItemsControl.ContainerFromElement(list, hit) is ListViewItem { DataContext: ItemViewModel item } ? item : null;
    }

    // the star, check box and rename box have their own tooltip or job: hovering them isn't hovering the file
    private static bool HasOwnUi(DependencyObject? hit)
    {
        for (var d = hit; d is not null and not ListViewItem; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TextBoxBase or ButtonBase) return true;
            if (d is FrameworkElement { ToolTip: not null }) return true;
        }
        return false;
    }

    private sealed class HoverContent
    {
        private MediaKind _kind;
        private long _size;
        private DecodedImage? _image;
        private AnimatedImage? _animation;
        private BitmapSource? _thumbnail;
        private TextContent? _text;
        private string _path = "";

        public static async Task<HoverContent?> LoadAsync(string path, HoverPlace place, CancellationToken ct)
        {
            var content = await Task.Run(() => Read(path, place, ct), ct);
            if (content is null) return null;
            if (content._kind == MediaKind.Image && content._image is null && content._animation is null)
            {
                // a format WPF has no codec for (RAW, HEIC without the extension): the shell may still have a thumbnail
                content._thumbnail = await StaWorkerPool.Icons.Run(() => PreviewLoader.ShellThumbnail(path, Math.Max(place.BoxWidth, place.BoxHeight)), ct);
                if (content._thumbnail is null) return null;
            }
            else if (content._kind == MediaKind.Audio)
                content._thumbnail = await StaWorkerPool.Icons.Run(() => PreviewLoader.ShellThumbnail(path, (int)(128 * place.Scale)), ct);
            return content;
        }

        private static HoverContent? Read(string path, HoverPlace place, CancellationToken ct)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var content = new HoverContent { _kind = PreviewLoader.ResolveKind(path), _size = info.Length, _path = path };
            switch (content._kind)
            {
                case MediaKind.Text:
                    content._text = PreviewLoader.ReadText(path, TextBytes, TextChars, TextLines);
                    return content._text is null ? null : content;
                case MediaKind.Image:
                    if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                        content._animation = PreviewLoader.DecodeGif(path, place.BoxWidth, place.BoxHeight, ct);
                    if (content._animation is null) content._image = PreviewLoader.DecodeImage(path, place.BoxWidth, place.BoxHeight);
                    return content;
                case MediaKind.Video or MediaKind.Audio:
                    return content;
                default:
                    return null;
            }
        }

        public void ShowIn(HoverPopup popup, HoverPlace place, string name, bool sound)
        {
            switch (_kind)
            {
                case MediaKind.Text:
                    popup.ShowText(place, name, _size, _text!);
                    break;
                case MediaKind.Image when _animation is { } a:
                    popup.ShowImage(place, name, _size, a.Frames[0], a, (a.SourceWidth, a.SourceHeight), alpha: true);
                    break;
                case MediaKind.Image when _image is { } i:
                    popup.ShowImage(place, name, _size, i.Image, null, (i.SourceWidth, i.SourceHeight), i.HasAlpha);
                    break;
                case MediaKind.Image:
                    popup.ShowImage(place, name, _size, _thumbnail!, null, null, alpha: false);
                    break;
                default:
                    popup.ShowMedia(place, name, _size, _path, _kind, sound, _thumbnail);
                    break;
            }
        }
    }
}
