using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Views;

// where the popup goes and how big its content may get, in pixels of the monitor under the cursor
public sealed record HoverPlace(int CursorX, int CursorY, RECT Work, double Scale, int BoxWidth, int BoxHeight)
{
    private const int Offset = 20, Margin = 8, Pad = 6, Footer = 30;

    public double BoxWidthDip => BoxWidth / Scale;
    public double BoxHeightDip => BoxHeight / Scale;

    public static HoverPlace At(int maxSize)
    {
        User32.GetCursorPos(out var pt);
        var monitor = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        var scale = (User32.GetDpiForMonitor(monitor, User32.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi : 96) / 96.0;
        int S(int v) => (int)Math.Round(v * scale);

        var wa = info.rcWork;
        int max = S(Math.Clamp(maxSize, 200, 2000)), off = S(Offset), m = S(Margin), pad = S(Pad), footer = S(Footer);
        var side = Math.Max(wa.Right - (pt.X + off) - m, pt.X - off - wa.Left - m);
        int width, height;
        if (side >= S(260))
        {
            width = Math.Min(max, side - 2 * pad);
            height = Math.Min(max, wa.Bottom - wa.Top - 2 * m - pad - footer);
        }
        else
        {
            var below = wa.Bottom - (pt.Y + off) - m;
            var above = pt.Y - off - wa.Top - m;
            width = Math.Min(max, wa.Right - wa.Left - 2 * m - 2 * pad);
            height = Math.Min(max, Math.Max(below, above) - pad - footer);
        }
        return new HoverPlace(pt.X, pt.Y, wa, scale, Math.Max(width, S(120)), Math.Max(height, S(90)));
    }

    // beside the cursor if it fits on either side, otherwise below or above it
    public (int X, int Y) Position(int width, int height)
    {
        int off = (int)Math.Round(Offset * Scale), m = (int)Math.Round(Margin * Scale);
        var wa = Work;
        int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(v, hi));
        if (CursorX + off + width <= wa.Right - m)
            return (CursorX + off, Clamp(CursorY - height / 2, wa.Top + m, wa.Bottom - m - height));
        if (CursorX - off - width >= wa.Left + m)
            return (CursorX - off - width, Clamp(CursorY - height / 2, wa.Top + m, wa.Bottom - m - height));
        var y = CursorY + off + height <= wa.Bottom - m ? CursorY + off : CursorY - off - height;
        return (Clamp(CursorX - width / 2, wa.Left + m, wa.Right - m - width), Clamp(y, wa.Top + m, wa.Bottom - m - height));
    }
}

public partial class HoverPopup : Window
{
    private const double MinMediaWidth = 268;

    private readonly DispatcherTimer _frames = new(DispatcherPriority.Render);
    private readonly DispatcherTimer _progress = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private IntPtr _hwnd;
    private HoverPlace? _place;
    private MediaKind _kind;
    private string _name = "";
    private string _sizeText = "";
    private string _videoSize = "";
    private bool _muted;
    private AnimatedImage? _animation;
    private int _frame;
    private MediaPlayer? _player;
    private TimeSpan _duration;

    public HoverPopup(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        _frames.Tick += OnFrame;
        _progress.Tick += (_, _) => UpdateProgress();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)User32.GetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, (IntPtr)(ex | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE));
        // WPF gives every window its own class, so this shadow is ours alone
        var cls = (long)User32.GetClassLongPtr(_hwnd, User32.GCL_STYLE);
        User32.SetClassLongPtr(_hwnd, User32.GCL_STYLE, (IntPtr)(cls | User32.CS_DROPSHADOW));
        var corner = Dwmapi.DWMWCP_ROUND;
        Dwmapi.DwmSetWindowAttribute(_hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    // the mouse goes through to the list underneath, and clicks never activate the popup
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_NCHITTEST) { handled = true; return User32.HTTRANSPARENT; }
        if (msg == User32.WM_MOUSEACTIVATE) { handled = true; return User32.MA_NOACTIVATE; }
        return IntPtr.Zero;
    }

    public bool ContainsCursor()
    {
        if (!IsVisible || _hwnd == IntPtr.Zero || !User32.GetWindowRect(_hwnd, out var r) || !User32.GetCursorPos(out var pt)) return false;
        return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
    }

    public void ShowImage(HoverPlace place, string name, long size, BitmapSource image, AnimatedImage? animation, (int Width, int Height)? source, bool alpha)
    {
        Reset(MediaKind.Image, place, name, size);
        var (pw, ph) = source ?? (image.PixelWidth, image.PixelHeight);
        var (w, h) = PreviewLoader.Fit(pw, ph, place.BoxWidthDip, place.BoxHeightDip, 1);
        Picture.Source = image;
        Picture.Width = w;
        Picture.Height = h;
        PictureBack.Background = alpha ? (Brush)FindResource("Checker") : null;
        PictureBack.Visibility = Visibility.Visible;
        _animation = animation;
        if (animation is not null)
        {
            _frame = 0;
            _frames.Interval = TimeSpan.FromMilliseconds(Math.Max(20, animation.Delays[0]));
            _frames.Start();
        }
        InfoText.Text = Join(source is { } s ? $"{s.Width} × {s.Height}" : "", _sizeText);
        Present();
    }

    public void ShowText(HoverPlace place, string name, long size, TextContent text)
    {
        Reset(MediaKind.Text, place, name, size);
        var empty = text.Text.Length == 0;
        TextView.Text = empty ? "(empty file)" : text.Text.Replace("\t", "    ");
        TextView.SetResourceReference(ForegroundProperty, empty ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        TextArea.MinWidth = MinMediaWidth;
        TextArea.MaxWidth = Math.Max(place.BoxWidthDip, MinMediaWidth);
        TextArea.MaxHeight = Math.Max(place.BoxHeightDip, 40);
        TextArea.Visibility = Visibility.Visible;
        InfoText.Text = Join(text.Encoding, text.Lines > 0 ? $"{text.Lines:N0} line{(text.Lines == 1 ? "" : "s")}" : "", _sizeText);
        Present();
    }

    // videos show once Windows has opened them; songs show their card at once
    public void ShowMedia(HoverPlace place, string name, long size, string path, MediaKind kind, bool sound, ImageSource? cover)
    {
        Reset(kind, place, name, size);
        _muted = kind == MediaKind.Video && !sound;
        Cover.Source = cover;
        var player = new MediaPlayer { IsMuted = _muted };
        _player = player;
        player.MediaOpened += (_, _) => { if (player == _player) OnMediaOpened(player); };
        player.MediaFailed += (_, _) => { if (player == _player) ShowCard("Windows can't play this file"); };
        player.MediaEnded += (_, _) =>
        {
            if (player != _player) return;
            player.Position = TimeSpan.Zero;
            player.Play();
        };
        try
        {
            player.Open(new Uri(path));
            player.Play();
        }
        catch (Exception)
        {
            ShowCard("Windows can't play this file");
            return;
        }
        if (kind == MediaKind.Audio) ShowCard(null);
    }

    private void OnMediaOpened(MediaPlayer player)
    {
        _duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        if (_kind == MediaKind.Video && player.HasVideo && player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
        {
            var (w, h) = PreviewLoader.Fit(player.NaturalVideoWidth, player.NaturalVideoHeight, _place!.BoxWidthDip, _place.BoxHeightDip, 1);
            Video.Source = new DrawingImage(new VideoDrawing { Player = player, Rect = new Rect(0, 0, w, h) });
            Video.Width = w;
            Video.Height = h;
            Video.Visibility = Visibility.Visible;
            VideoProgress.Visibility = Visibility.Visible;
            _videoSize = $"{player.NaturalVideoWidth} × {player.NaturalVideoHeight}";
            UpdateProgress();
            Present();
        }
        else if (Card.Visibility != Visibility.Visible) ShowCard(null);
        UpdateProgress();
        _progress.Start();
    }

    private void ShowCard(string? error)
    {
        if (error is not null) StopPlayer();
        Media.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        VideoProgress.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        CardName.Text = _name;
        CardInfo.Text = error ?? Join(Path.GetExtension(_name).TrimStart('.').ToUpperInvariant(), _sizeText);
        CardGlyph.Text = error is null ? "" : "";
        CardGlyph.SetResourceReference(ForegroundProperty, error is null ? "AccentFillColorDefaultBrush" : "SystemFillColorCautionBrush");
        Cover.Visibility = error is null ? Visibility.Visible : Visibility.Collapsed;
        CardProgress.Visibility = error is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress();
        Present();
    }

    private void UpdateProgress()
    {
        if (_player is not { } player) return;
        var position = player.Position;
        var fraction = _duration > TimeSpan.Zero ? Math.Clamp(position / _duration, 0, 1) : 0;
        var time = $"{Time(position)} / {Time(_duration)}";
        if (Card.Visibility == Visibility.Visible)
        {
            CardTime.Text = time;
            CardFill.Width = CardTrack.ActualWidth * fraction;
        }
        else if (VideoProgress.Visibility == Visibility.Visible)
        {
            VideoFill.Width = Video.Width * fraction;
            InfoText.Text = Join(_videoSize, time, _muted ? "muted" : "", _sizeText);
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_animation is not { } a) { _frames.Stop(); return; }
        _frame = (_frame + 1) % a.Frames.Count;
        Picture.Source = a.Frames[_frame];
        _frames.Interval = TimeSpan.FromMilliseconds(Math.Max(20, a.Delays[_frame]));
    }

    private void Reset(MediaKind kind, HoverPlace place, string name, long size)
    {
        StopContent();
        _kind = kind;
        _place = place;
        _name = name;
        _sizeText = size >= 0 ? SizeFormat.Format(size) : "";
        _videoSize = "";
        _duration = TimeSpan.Zero;
        Media.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        Card.Visibility = Visibility.Collapsed;
        VideoProgress.Visibility = Visibility.Collapsed;
        PictureBack.Visibility = Visibility.Collapsed;
        Video.Visibility = Visibility.Collapsed;
        TextArea.Visibility = Visibility.Collapsed;
        NameText.Text = name;
        InfoText.Text = _sizeText;
        VideoFill.Width = 0;
        CardFill.Width = 0;
    }

    // measured before it shows, so it opens at its final size and place
    private void Present()
    {
        var place = _place!;
        if (Footer.Visibility == Visibility.Visible)
        {
            Footer.Width = double.NaN;
            Media.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Footer.Width = Math.Max(Media.DesiredSize.Width, MinMediaWidth) - Footer.Margin.Left - Footer.Margin.Right;
        }
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = (int)Math.Ceiling(Root.DesiredSize.Width * place.Scale);
        var h = (int)Math.Ceiling(Root.DesiredSize.Height * place.Scale);
        var (x, y) = place.Position(w, h);
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        const uint flags = User32.SWP_NOACTIVATE;
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, w, h, flags);
        if (!IsVisible) Show();
        // moving to a monitor with another DPI makes WPF resize the window: put it back
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, w, h, flags);
    }

    public void Clear()
    {
        StopContent();
        Hide();
    }

    private void StopContent()
    {
        _frames.Stop();
        _progress.Stop();
        _animation = null;
        Picture.Source = null;
        Cover.Source = null;
        TextView.Text = "";
        StopPlayer();
    }

    private void StopPlayer()
    {
        _progress.Stop();
        Video.Source = null;
        if (_player is not { } player) return;
        _player = null;
        player.Stop();
        player.Close();
    }

    private static string Join(params string[] parts) => string.Join("  ·  ", parts.Where(p => p.Length > 0));

    private static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
