using System.Windows;
using System.Windows.Controls;

namespace ExplorerPlus.App.Views;

public sealed class BreadcrumbPanel : Panel
{
    public bool HasHiddenItems { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var firstVisible = children.Count;
        double used = 0;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var w = children[i].DesiredSize.Width;
            if (used + w > finalSize.Width && i < children.Count - 1) break;
            used += w;
            firstVisible = i;
        }
        HasHiddenItems = firstVisible > 0;

        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (i < firstVisible)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }
}
