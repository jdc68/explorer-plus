using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExplorerPlus.App.Views;

public sealed class SmoothScroller
{
    private static readonly ConditionalWeakTable<ScrollViewer, SmoothScroller> All = new();
    private static readonly double Duration = Stopwatch.Frequency * 0.15;

    private readonly ScrollViewer _sv;
    private readonly TranslateTransform _shift = new();

    // wheel: the offset itself moves
    private bool _scrolling, _horizontal;
    private double _from, _to, _current;
    private long _start;

    // pages: the list jumps at once and its rows slide into place, so no extra layout
    private bool _gliding, _glideHorizontal;
    private double _glideFrom;
    private long _glideStart;
    private BitmapSource? _before;
    private SnapshotAdorner? _beforeAdorner;
    private bool _hooked;

    private SmoothScroller(ScrollViewer sv) => _sv = sv;

    public static SmoothScroller For(ScrollViewer sv) => All.GetValue(sv, s => new SmoothScroller(s));

    public static void Stop(ScrollViewer? sv)
    {
        if (sv is not null && All.TryGetValue(sv, out var s)) s.Stop();
    }

    /// <summary>Moves by <paramref name="delta"/> pixels; while a move is running, the delta extends its target.</summary>
    public void ScrollBy(double delta, bool horizontal, bool animate)
    {
        EndGlide();
        if (_scrolling && horizontal != _horizontal) Stop();
        var basis = _scrolling ? _to : Offset(horizontal);
        var target = Math.Clamp(basis + delta, 0, horizontal ? _sv.ScrollableWidth : _sv.ScrollableHeight);
        if (!animate)
        {
            Stop();
            Set(horizontal, target);
            return;
        }
        _horizontal = horizontal;
        _from = _current = _scrolling ? _current : Offset(horizontal);
        _to = target;
        _start = Stopwatch.GetTimestamp();
        if (Math.Abs(_to - _from) < 0.5)
        {
            Stop();
            Set(horizontal, target);
            return;
        }
        _scrolling = true;
        Hook();
    }

    /// <summary>Where the list shows its rows from, counting a slide still running.</summary>
    public (double X, double Y) Shown()
    {
        if (_scrolling) return _horizontal ? (_current, _sv.VerticalOffset) : (_sv.HorizontalOffset, _current);
        return (_sv.HorizontalOffset - _shift.X, _sv.VerticalOffset - _shift.Y);
    }

    /// <summary>Takes a picture of what the list shows, to slide out after a jump: the rows that were on screen may not be made any more.</summary>
    public void TakeSnapshot()
    {
        _before = null;
        if (_sv.Template?.FindName("PART_ScrollContentPresenter", _sv) is not ScrollContentPresenter scp || scp.ActualWidth < 1 || scp.ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(scp);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(scp.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(scp.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(scp);
        bitmap.Freeze();
        _before = bitmap;
    }

    /// <summary>The list has jumped from <paramref name="shownFrom"/> to its current offset: slide the rows over that distance.</summary>
    public void GlideFrom(double shownFrom, bool horizontal)
    {
        var before = _before;
        _before = null;
        Stop();
        // rows more than a screen away were never made, so slide over one screen at most
        var screen = horizontal ? _sv.ViewportWidth : _sv.ViewportHeight;
        var distance = Math.Clamp(Offset(horizontal) - shownFrom, -screen, screen);
        if (Math.Abs(distance) < 0.5 || _sv.Content is not UIElement content) return;
        if (!ReferenceEquals(content.RenderTransform, _shift)) content.RenderTransform = _shift;
        _glideHorizontal = horizontal;
        _glideFrom = distance;
        _glideStart = Stopwatch.GetTimestamp();
        _gliding = true;
        if (before is not null && AdornerLayer.GetAdornerLayer(content) is { } layer)
        {
            _beforeAdorner = new SnapshotAdorner(content, before);
            layer.Add(_beforeAdorner);
        }
        SetShift(distance);
        Hook();
    }

    public void Stop()
    {
        _scrolling = false;
        EndGlide();
        Unhook();
    }

    private void EndGlide()
    {
        if (!_gliding) return;
        _gliding = false;
        SetShift(0);
        RemoveSnapshot();
        if (!_scrolling) Unhook();
    }

    private void RemoveSnapshot()
    {
        if (_beforeAdorner is null) return;
        AdornerLayer.GetAdornerLayer(_beforeAdorner.AdornedElement)?.Remove(_beforeAdorner);
        _beforeAdorner = null;
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_scrolling)
        {
            var t = Math.Min(1, (now - _start) / Duration);
            _current = _from + (_to - _from) * Ease(t);
            Set(_horizontal, _current);
            if (t >= 1) _scrolling = false;
        }
        if (_gliding)
        {
            var t = Math.Min(1, (now - _glideStart) / Duration);
            SetShift(_glideFrom * (1 - Ease(t)));
            if (t >= 1)
            {
                _gliding = false;
                RemoveSnapshot();
            }
        }
        if (!_scrolling && !_gliding) Unhook();
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

    private void SetShift(double value)
    {
        _shift.X = _glideHorizontal ? value : 0;
        _shift.Y = _glideHorizontal ? 0 : value;
        _beforeAdorner?.MoveTo(_glideHorizontal ? value - _glideFrom : 0, _glideHorizontal ? 0 : value - _glideFrom);
    }

    // the old screen, drawn above the rows and moved with them
    private sealed class SnapshotAdorner : Adorner
    {
        private readonly BitmapSource _image;
        private readonly TranslateTransform _move = new();

        public SnapshotAdorner(UIElement content, BitmapSource image) : base(content)
        {
            _image = image;
            IsHitTestVisible = false;
        }

        // pinned to the list's corner, not to the sliding rows
        public override GeneralTransform GetDesiredTransform(GeneralTransform transform) => Transform.Identity;

        public void MoveTo(double x, double y)
        {
            _move.X = x;
            _move.Y = y;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            dc.PushTransform(_move);
            dc.DrawImage(_image, new Rect(0, 0, _image.PixelWidth / dpi.DpiScaleX, _image.PixelHeight / dpi.DpiScaleY));
            dc.Pop();
        }
    }

    private double Offset(bool horizontal) => horizontal ? _sv.HorizontalOffset : _sv.VerticalOffset;

    private void Set(bool horizontal, double value)
    {
        if (horizontal) _sv.ScrollToHorizontalOffset(value);
        else _sv.ScrollToVerticalOffset(value);
    }
}
