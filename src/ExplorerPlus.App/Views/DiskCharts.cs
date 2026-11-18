using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Views;

public enum FileKind { Pictures, Videos, Music, Documents, Code, Archives, Other }

public static class FileKinds
{
    public static readonly FileKind[] All = Enum.GetValues<FileKind>();

    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string list)
        {
            foreach (var ext in list.Split(' ')) map[ext] = kind;
        }
        Add(FileKind.Pictures, "jpg jpeg jfif png gif bmp webp heic heif avif jxl tif tiff ico svg dng cr2 cr3 nef arw orf rw2 raf psd");
        Add(FileKind.Videos, "mp4 mkv mov avi wmv webm m4v mpg mpeg 3gp flv mts m2ts vob");
        Add(FileKind.Music, "mp3 flac wav m4a aac ogg oga opus wma aiff aif ape alac mid midi amr");
        Add(FileKind.Documents, "txt md pdf doc docx xls xlsx ppt pptx odt ods odp rtf csv log epub");
        Add(FileKind.Code, "json xml yaml yml toml ini cfg conf html htm css scss sql cs csproj sln xaml js jsx ts tsx mjs py java kt go rs rb php swift c cpp cc h hpp lua sh ps1 psm1 bat cmd dll pdb exe");
        Add(FileKind.Archives, "zip 7z rar tar gz tgz bz2 xz zst cab iso img vhd vhdx msi wim");
        return map;
    }

    public static FileKind Of(DiskNode node) => ByExtension.GetValueOrDefault(node.Extension, FileKind.Other);

    public static string Label(FileKind kind) => kind switch
    {
        FileKind.Code => "Code and programs",
        FileKind.Archives => "Archives and disk images",
        _ => kind.ToString(),
    };

    public static readonly Brush FolderBrush = Frozen(0x5B, 0x6B, 0x7F);
    public static readonly Brush OtherItemsBrush = Frozen(0x80, 0x80, 0x80);

    private static readonly Dictionary<FileKind, Brush> KindBrushes = new()
    {
        [FileKind.Pictures] = Frozen(0x2B, 0xA5, 0x9B),
        [FileKind.Videos] = Frozen(0x8A, 0x6B, 0xD6),
        [FileKind.Music] = Frozen(0xD0, 0x5C, 0x96),
        [FileKind.Documents] = Frozen(0x3D, 0x86, 0xD9),
        [FileKind.Code] = Frozen(0xC9, 0x8E, 0x26),
        [FileKind.Archives] = Frozen(0xC2, 0x62, 0x3A),
        [FileKind.Other] = Frozen(0x9A, 0x9A, 0x9A),
    };

    public static Brush BrushOf(FileKind kind) => KindBrushes[kind];

    public static Brush BrushOf(DiskNode node) => node.IsOther ? OtherItemsBrush : node.IsFolder ? FolderBrush : KindBrushes[Of(node)];

    // folders get lighter the deeper they are drawn, so nested ones stand apart
    private static readonly Brush[] FolderShades = [FolderBrush, Frozen(0x6C, 0x7D, 0x93), Frozen(0x7F, 0x90, 0xA6), Frozen(0x93, 0xA3, 0xB8)];

    public static Brush BrushOf(DiskNode node, int depth) =>
        node.IsFolder && !node.IsOther ? FolderShades[Math.Clamp(depth, 0, FolderShades.Length - 1)] : BrushOf(node);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public static class TreemapLayout
{
    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk): one rectangle per value, largest first, filling <paramref name="bounds"/>.</summary>
    public static Rect[] Squarify(IReadOnlyList<double> values, Rect bounds)
    {
        var result = new Rect[values.Count];
        var total = values.Sum();
        if (values.Count == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
        var scale = bounds.Width * bounds.Height / total;
        var areas = values.Select(v => v * scale).ToArray();
        var r = bounds;
        var start = 0;
        while (start < areas.Length)
        {
            var side = Math.Min(r.Width, r.Height);
            var end = start + 1;
            var sum = areas[start];
            var worst = Worst(areas[start], areas[start], sum, side);
            while (end < areas.Length)
            {
                var next = Worst(areas[start], areas[end], sum + areas[end], side);
                if (next > worst) break;
                worst = next;
                sum += areas[end++];
            }
            var thickness = side > 0 ? sum / side : 0;
            var at = 0.0;
            for (var i = start; i < end; i++)
            {
                var length = thickness > 0 ? areas[i] / thickness : 0;
                result[i] = r.Width >= r.Height
                    ? new Rect(r.X, r.Y + at, thickness, length)
                    : new Rect(r.X + at, r.Y, length, thickness);
                at += length;
            }
            r = r.Width >= r.Height
                ? new Rect(r.X + thickness, r.Y, Math.Max(0, r.Width - thickness), r.Height)
                : new Rect(r.X, r.Y + thickness, r.Width, Math.Max(0, r.Height - thickness));
            start = end;
        }
        return result;
    }

    // the worst aspect ratio in a row whose biggest and smallest areas are given
    private static double Worst(double biggest, double smallest, double sum, double side)
    {
        if (sum <= 0 || side <= 0 || smallest <= 0) return double.MaxValue;
        var s2 = sum * sum;
        var w2 = side * side;
        return Math.Max(w2 * biggest / s2, s2 / (w2 * smallest));
    }
}

/// <summary>A chart of one folder's disk usage, drawn by hand so thousands of cells stay cheap; hit-tests its own shapes.</summary>
public abstract class DiskChart : FrameworkElement
{
    private DiskNode? _root;
    private DiskNode? _hovered;
    private DiskNode? _selected;
    private IReadOnlySet<FileKind> _kinds = new HashSet<FileKind>();
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    protected DiskChart()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0);
        Focusable = false;
    }

    public DiskNode? Root
    {
        get => _root;
        set { _root = value; _hovered = null; InvalidateVisual(); }
    }

    public DiskNode? Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; InvalidateVisual(); } }
    }

    /// <summary>Kinds picked in the legend; the other files are dimmed. Empty shows everything.</summary>
    public IReadOnlySet<FileKind> Kinds
    {
        get => _kinds;
        set { _kinds = value; InvalidateVisual(); }
    }

    public Brush Background { get; set; } = Brushes.Transparent;

    public event Action<DiskNode>? NodeClicked;
    public event Action<DiskNode>? NodeDoubleClicked;
    public event Action<DiskNode, Point>? NodeRightClicked;

    protected DiskNode? Hovered => _hovered;

    protected abstract DiskNode? HitTest(Point p);

    protected bool IsDimmed(DiskNode node) =>
        _kinds.Count > 0 && (node.IsFolder || node.IsOther || !_kinds.Contains(FileKinds.Of(node)));

    protected bool IsMarked(DiskNode node) => node == _hovered || node == _selected;

    protected FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    public static string Describe(DiskNode node) =>
        node.Size < 0 ? $"{node.Name}\nMeasuring…" : $"{node.Name}\n{SizeFormat.Format(node.Size)}" + (node.IsOther || node.IsFolder ? "" : $"  ·  {FileKinds.Label(FileKinds.Of(node))}");

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == _hovered) return;
        _hovered = hit;
        _tip.Content = hit is null ? null : Describe(hit);
        _tip.IsOpen = hit is not null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.IsOpen = false;
        if (_hovered is null) return;
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } node) NodeClicked?.Invoke(node);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && HitTest(e.GetPosition(this)) is { } node)
        {
            e.Handled = true;
            NodeDoubleClicked?.Invoke(node);
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } node)
        {
            e.Handled = true;
            _tip.IsOpen = false;
            NodeRightClicked?.Invoke(node, e.GetPosition(this));
        }
    }
}

public sealed class TreemapView : DiskChart
{
    private const double Header = 18;
    private readonly List<(Rect Rect, DiskNode Node)> _cells = [];
    private static readonly Pen Mark = Frozen(new Pen(Brushes.White, 2));

    protected override void OnRender(DrawingContext dc)
    {
        _cells.Clear();
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (Root is not { } root) return;
        Draw(dc, root, new Rect(RenderSize), 0);
    }

    private void Draw(DrawingContext dc, DiskNode folder, Rect area, int depth)
    {
        var items = DiskUsage.Visible(folder, depth == 0 ? 150 : 40, depth == 0 ? 0.0004 : 0.004);
        var rects = TreemapLayout.Squarify(items.Select(i => (double)i.Size).ToList(), area);
        for (var i = 0; i < items.Count; i++)
        {
            var node = items[i];
            var rect = rects[i];
            if (rect.Width < 2 || rect.Height < 2) continue;
            rect.Inflate(-1, -1);
            _cells.Add((rect, node));
            var dim = IsDimmed(node);
            var nested = node.IsFolder && node.Children is not null && depth < 2 && rect.Width > 60 && rect.Height > 50;
            if (dim) dc.PushOpacity(0.25);
            dc.DrawRectangle(FileKinds.BrushOf(node, depth), null, rect);
            if (dim) dc.Pop();
            if (nested)
            {
                Label(dc, node, new Rect(rect.X, rect.Y, rect.Width, Header), header: true);
                Draw(dc, node, new Rect(rect.X + 2, rect.Y + Header, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - Header - 2)), depth + 1);
            }
            else Label(dc, node, rect, header: false);
            if (IsMarked(node)) dc.DrawRectangle(null, Mark, rect);
        }
    }

    private void Label(DrawingContext dc, DiskNode node, Rect rect, bool header)
    {
        if (rect.Width < 48 || rect.Height < 16) return;
        var name = Text(node.Name, 12, Brushes.White, bold: header || node.IsFolder);
        name.MaxTextWidth = Math.Max(1, rect.Width - 8);
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(name, new Point(rect.X + 4, rect.Y + 2));
        if (!header && rect.Height >= 34 && node.Size >= 0)
        {
            var size = Text(SizeFormat.Format(node.Size), 11, Brushes.White);
            size.MaxTextWidth = Math.Max(1, rect.Width - 8);
            size.MaxLineCount = 1;
            dc.DrawText(size, new Point(rect.X + 4, rect.Y + 17));
        }
        dc.Pop();
    }

    protected override DiskNode? HitTest(Point p)
    {
        for (var i = _cells.Count - 1; i >= 0; i--)
            if (_cells[i].Rect.Contains(p)) return _cells[i].Node;
        return null;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}

public sealed class SunburstView : DiskChart
{
    private const int Rings = 3;
    private readonly List<(int Ring, double Start, double Sweep, DiskNode Node)> _arcs = [];
    private Point _center;
    private double _inner, _ring;

    /// <summary>The middle was clicked: go up a level.</summary>
    public event Action? CenterClicked;

    protected override void OnRender(DrawingContext dc)
    {
        _arcs.Clear();
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (Root is not { } root) return;
        var radius = Math.Max(0, Math.Min(RenderSize.Width, RenderSize.Height) / 2 - 8);
        _center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        _inner = radius * 0.28;
        _ring = (radius - _inner) / Rings;
        if (_ring < 4) return;
        var gap = new Pen(Background, 1);
        gap.Freeze();
        DrawRing(dc, root, 0, 360, 0, gap);

        dc.DrawEllipse(FileKinds.FolderBrush, null, _center, _inner - 2, _inner - 2);
        var shown = Hovered ?? root;
        var name = Text(shown.Name, 13, Brushes.White, bold: true);
        name.MaxTextWidth = Math.Max(1, _inner * 1.6);
        name.MaxLineCount = 2;
        name.Trimming = TextTrimming.CharacterEllipsis;
        name.TextAlignment = TextAlignment.Center;
        var size = Text(shown.Size < 0 ? "Measuring…" : SizeFormat.Format(shown.Size), 12, Brushes.White);
        size.TextAlignment = TextAlignment.Center;
        dc.DrawText(name, new Point(_center.X - name.MaxTextWidth / 2, _center.Y - name.Height));
        dc.DrawText(size, new Point(_center.X, _center.Y + 2));
    }

    private void DrawRing(DrawingContext dc, DiskNode folder, double start, double sweep, int ring, Pen gap)
    {
        if (ring >= Rings || folder.Children is null || sweep < 1) return;
        var items = DiskUsage.Visible(folder, 200, 0.6 / sweep);
        var total = (double)items.Sum(i => i.Size);
        if (total <= 0) return;
        var at = start;
        foreach (var node in items)
        {
            var part = sweep * node.Size / total;
            if (part >= 0.3)
            {
                _arcs.Add((ring, at, part, node));
                var brush = IsMarked(node) ? Brushes.White : FileKinds.BrushOf(node, ring);
                var dim = IsDimmed(node);
                if (dim) dc.PushOpacity(0.25);
                dc.DrawGeometry(brush, gap, Arc(_inner + ring * _ring, _inner + (ring + 1) * _ring, at, part));
                if (dim) dc.Pop();
                if (node.IsFolder) DrawRing(dc, node, at, part, ring + 1, gap);
            }
            at += part;
        }
    }

    private Geometry Arc(double r1, double r2, double start, double sweep)
    {
        sweep = Math.Min(sweep, 359.99);
        var large = sweep > 180;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(At(r2, start), true, true);
            c.ArcTo(At(r2, start + sweep), new Size(r2, r2), 0, large, SweepDirection.Clockwise, true, false);
            c.LineTo(At(r1, start + sweep), true, false);
            c.ArcTo(At(r1, start), new Size(r1, r1), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // degrees clockwise from twelve o'clock
    private Point At(double r, double degrees)
    {
        var a = (degrees - 90) * Math.PI / 180;
        return new Point(_center.X + r * Math.Cos(a), _center.Y + r * Math.Sin(a));
    }

    protected override DiskNode? HitTest(Point p)
    {
        var dx = p.X - _center.X;
        var dy = p.Y - _center.Y;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < _inner || _ring <= 0) return null;
        var ring = (int)((r - _inner) / _ring);
        var angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        foreach (var arc in _arcs)
            if (arc.Ring == ring && angle >= arc.Start && angle < arc.Start + arc.Sweep) return arc.Node;
        return null;
    }

    public bool IsCenter(Point p) => (p - _center).Length < _inner;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsCenter(e.GetPosition(this))) CenterClicked?.Invoke();
        else base.OnMouseLeftButtonUp(e);
    }
}
