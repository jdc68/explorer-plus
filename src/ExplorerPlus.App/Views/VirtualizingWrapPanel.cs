using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ExplorerPlus.App.Views;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double),
        typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double),
        typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation),
        typeof(VirtualizingWrapPanel), new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Horizontal: items fill rows and the panel scrolls vertically (icon views).
    /// Vertical: items fill columns and the panel scrolls horizontally (List view).</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemCount;

    private bool Horizontal => Orientation == Orientation.Horizontal;

    /// <summary>Items per row (horizontal) or per column (vertical) at the current size.</summary>
    public int ItemsPerLine => Horizontal
        ? Math.Max(1, (int)Math.Floor(Math.Max(_viewport.Width, ItemWidth) / ItemWidth))
        : Math.Max(1, (int)Math.Floor(Math.Max(_viewport.Height, ItemHeight) / ItemHeight));

    /// <summary>Where an item sits, in content coordinates (unscrolled).</summary>
    public Rect GetItemRect(int index)
    {
        var perLine = ItemsPerLine;
        int line = index / perLine, pos = index % perLine;
        return Horizontal
            ? new Rect(pos * ItemWidth, line * ItemHeight, ItemWidth, ItemHeight)
            : new Rect(line * ItemWidth, pos * ItemHeight, ItemWidth, ItemHeight);
    }

    /// <summary>Indexes of the items intersecting a rectangle given in content coordinates (for marquee selection).</summary>
    public IEnumerable<int> IndexesIn(Rect content)
    {
        var perLine = ItemsPerLine;
        if (Horizontal)
        {
            int c0 = Math.Max(0, (int)(content.Left / ItemWidth)), c1 = Math.Min(perLine - 1, (int)(content.Right / ItemWidth));
            int r0 = Math.Max(0, (int)(content.Top / ItemHeight)), r1 = (int)(content.Bottom / ItemHeight);
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                {
                    var i = r * perLine + c;
                    if (i < _itemCount) yield return i;
                }
        }
        else
        {
            int c0 = Math.Max(0, (int)(content.Left / ItemWidth)), c1 = (int)(content.Right / ItemWidth);
            int r0 = Math.Max(0, (int)(content.Top / ItemHeight)), r1 = Math.Min(perLine - 1, (int)(content.Bottom / ItemHeight));
            for (var c = c0; c <= c1; c++)
                for (var r = r0; r <= r1; r++)
                {
                    var i = c * perLine + r;
                    if (i < _itemCount) yield return i;
                }
        }
    }

    public Point Offset => _offset;

    protected override void OnIsItemsHostChanged(bool oldIsItemsHost, bool newIsItemsHost)
    {
        base.OnIsItemsHostChanged(oldIsItemsHost, newIsItemsHost);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        _itemCount = owner?.Items.Count ?? 0;

        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? ItemWidth * 4 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? ItemHeight * 4 : availableSize.Height);
        UpdateScrollInfo(viewport);

        if (!IsItemsHost) return viewport;
        _ = InternalChildren; // connects the generator, or a panel made after its items arrived stays blank (#4)
        if (ItemContainerGenerator is null) return viewport;

        var (first, last) = RealizedRange();
        var generator = ItemContainerGenerator;
        if (_itemCount > 0 && last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (var i = first; i <= last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out var newlyRealized) is not UIElement child) break;
                    if (newlyRealized || childIndex >= InternalChildren.Count || !ReferenceEquals(InternalChildren[childIndex], child))
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
        }
        CleanUp(first, last);
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsItemsHost || ItemContainerGenerator is not { } generator) return finalSize;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var r = GetItemRect(index);
            r.Offset(-_offset.X, -_offset.Y);
            InternalChildren[i].Arrange(r);
        }
        return finalSize;
    }

    private (int First, int Last) RealizedRange()
    {
        if (_itemCount == 0) return (0, -1);
        var perLine = ItemsPerLine;
        // Realize one extra screen before and after, so scrolling and keyboard moves land on ready items.
        double start, end, lineSize;
        if (Horizontal) { start = _offset.Y - _viewport.Height; end = _offset.Y + 2 * _viewport.Height; lineSize = ItemHeight; }
        else { start = _offset.X - _viewport.Width; end = _offset.X + 2 * _viewport.Width; lineSize = ItemWidth; }
        var firstLine = Math.Max(0, (int)Math.Floor(start / lineSize));
        var lastLine = Math.Max(0, (int)Math.Ceiling(end / lineSize));
        return (Math.Min(_itemCount - 1, firstLine * perLine), Math.Min(_itemCount - 1, (lastLine + 1) * perLine - 1));
    }

    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            // Never throw away the container that has keyboard focus (or is being renamed).
            if (index >= 0 && InternalChildren[i].IsKeyboardFocusWithin) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        MakeRectVisible(GetItemRect(index));
    }

    private void MakeRectVisible(Rect r)
    {
        if (Horizontal)
        {
            if (r.Top < _offset.Y) SetVerticalOffset(r.Top);
            else if (r.Bottom > _offset.Y + _viewport.Height) SetVerticalOffset(r.Bottom - _viewport.Height);
        }
        else
        {
            if (r.Left < _offset.X) SetHorizontalOffset(r.Left);
            else if (r.Right > _offset.X + _viewport.Width) SetHorizontalOffset(r.Right - _viewport.Width);
        }
    }

    // ---------- IScrollInfo ----------

    private void UpdateScrollInfo(Size viewport)
    {
        _viewport = viewport;
        var perLine = ItemsPerLine;
        var lines = (_itemCount + perLine - 1) / perLine;
        _extent = Horizontal
            ? new Size(viewport.Width, lines * ItemHeight)
            : new Size(lines * ItemWidth, viewport.Height);
        _offset.X = Math.Max(0, Math.Min(_offset.X, _extent.Width - _viewport.Width));
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public double WheelStep => Horizontal ? Math.Max(48, ItemHeight) : ItemWidth;

    public void LineUp() => SetVerticalOffset(_offset.Y - 24);
    public void LineDown() => SetVerticalOffset(_offset.Y + 24);
    public void LineLeft() => SetHorizontalOffset(_offset.X - 24);
    public void LineRight() => SetHorizontalOffset(_offset.X + 24);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    // List view scrolls sideways with the wheel, like Explorer.
    public void MouseWheelUp() { if (Horizontal) SetVerticalOffset(_offset.Y - WheelStep); else SetHorizontalOffset(_offset.X - WheelStep); }
    public void MouseWheelDown() { if (Horizontal) SetVerticalOffset(_offset.Y + WheelStep); else SetHorizontalOffset(_offset.X + WheelStep); }
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - WheelStep);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + WheelStep);

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Width - _viewport.Width));
        if (offset == _offset.X) return;
        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset == _offset.Y) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !InternalChildren[i].IsAncestorOf(visual)) continue;
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) break;
            var r = GetItemRect(index);
            MakeRectVisible(r);
            r.Offset(-_offset.X, -_offset.Y);
            return r;
        }
        return rectangle;
    }
}
