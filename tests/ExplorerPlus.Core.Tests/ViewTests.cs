using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ExplorerPlus.App.Infrastructure;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.App.Views;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class ViewTests
{
    private static FolderEntry AFile(string name) => new() { Name = name, DisplayName = name, TypeName = "File" };
    private static FolderEntry Folder(string name) => new() { Name = name, DisplayName = name, TypeName = "File folder", IsFolder = true };

    private static FolderContent Classify(params FolderEntry[] entries) => ContentViewRules.Classify(entries, ContentViewRules.Defaults());

    [Fact]
    public void Videos_with_some_folders_open_as_videos()
    {
        var entries = Enumerable.Range(0, 11).Select(i => Folder($"f{i}"))
            .Concat(Enumerable.Range(0, 600).Select(i => AFile($"clip {i}.mp4")))
            .Concat([AFile("notes.txt"), AFile("cover.jpg")]).ToArray();
        Assert.Equal(FolderContent.Videos, Classify(entries));
    }

    [Fact]
    public void Pictures_and_a_few_clips_count_together()
    {
        var entries = Enumerable.Range(0, 5).Select(i => AFile($"p{i}.jpg"))
            .Concat(Enumerable.Range(0, 2).Select(i => AFile($"v{i}.mov")))
            .Concat([AFile("a.txt"), AFile("b.txt")]).ToArray();
        Assert.Equal(FolderContent.Pictures, Classify(entries));
    }

    [Fact]
    public void Mixed_and_folder_only_folders_get_the_general_view()
    {
        Assert.Equal(FolderContent.General, Classify(AFile("a.mp3"), AFile("b.pdf"), AFile("c.jpg"), AFile("d.exe"), AFile("e.zip")));
        Assert.Equal(FolderContent.General, Classify(Folder("a"), Folder("b"), Folder("c"), AFile("x.jpg")));
        Assert.Equal(FolderContent.General, Classify());
    }

    [Fact]
    public void Code_folders_are_documents_and_ts_is_not_video()
    {
        Assert.Equal(FolderContent.Documents, Classify(AFile("a.ts"), AFile("b.tsx"), AFile("c.json"), AFile("d.md")));
        Assert.Equal(FolderContent.Music, Classify(AFile("1.flac"), AFile("2.flac"), AFile("3.flac"), AFile("cover.jpg")));
    }

    [Fact]
    public void Rules_from_settings_decide_extensions_and_view()
    {
        var rules = ContentViewRules.Complete(
        [
            new ViewRule { Content = FolderContent.Music, View = ViewMode.List, Extensions = [".XYZ", "*.abc", "abc"] },
        ]);
        Assert.Equal(ContentViewRules.Kinds.Length, rules.Count);
        var music = rules.Single(r => r.Content == FolderContent.Music);
        Assert.Equal(["xyz", "abc"], music.Extensions);
        Assert.Equal(FolderContent.Music, ContentViewRules.Classify([AFile("a.xyz"), AFile("b.abc"), AFile("c.abc")], rules));
        Assert.Equal(ViewMode.List, ContentViewRules.ModeFor(FolderContent.Music, rules));
        Assert.Equal(ViewMode.LargeIcons, ContentViewRules.ModeFor(FolderContent.Videos, rules));
    }

    [Fact]
    public void Music_and_picture_folders_get_their_columns()
    {
        var music = ColumnCatalog.DefaultFor(FolderContent.Music, LocationKind.Folder);
        Assert.Equal([ColumnCatalog.Name, "System.Media.Duration", ColumnCatalog.Modified, ColumnCatalog.Type, ColumnCatalog.Size], music);
        foreach (var dropped in new[] { "System.Music.TrackNumber", "System.Title", "System.Music.Artist", "System.Music.AlbumTitle" })
            Assert.NotNull(ColumnCatalog.Get(dropped)); // still in the column picker

        var pictures = ColumnCatalog.DefaultFor(FolderContent.Pictures, LocationKind.Folder);
        Assert.Contains(ColumnCatalog.PictureDate, pictures);
        Assert.DoesNotContain(ColumnCatalog.DateTaken, pictures);
        Assert.Equal("Date", ColumnCatalog.Get(ColumnCatalog.PictureDate)!.Header);
    }

    [Fact]
    public void Columns_saved_by_a_resize_follow_the_new_defaults()
    {
        var resized = new FolderViewState
        {
            Columns = ["Name", "System.Music.TrackNumber", "System.Title|73", "System.Music.Artist|167", "System.Music.AlbumTitle|84", "System.Media.Duration", "Size"],
        };
        FolderViewStore.ForgetOldDefaultColumns(resized);
        Assert.Null(resized.Columns);
        Assert.Equal(73, resized.Widths!["System.Title"]);

        var chosen = new FolderViewState { Columns = ["Name", "System.Music.Artist", "Size|90"] };
        FolderViewStore.ForgetOldDefaultColumns(chosen);
        Assert.Equal(3, chosen.Columns!.Count);
        Assert.Null(chosen.Widths);
    }

    // #4: the icon view stayed empty until the window was resized
    public enum Order { ItemsBeforeLayout, ItemsAfterLayout, FromScrolledDetails, OpensInIcons }

    [Theory]
    [InlineData(Order.OpensInIcons)]
    [InlineData(Order.ItemsBeforeLayout)]
    [InlineData(Order.ItemsAfterLayout)]
    [InlineData(Order.FromScrolledDetails)]
    public void Icon_view_draws_items_after_a_view_switch(Order order) => RunSta(() =>
    {
        var items = new RangeObservableCollection<string>();
        var list = new ListBox { ItemsSource = items };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        var wrap = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
        wrap.SetValue(VirtualizingWrapPanel.ItemWidthProperty, 124.0);
        wrap.SetValue(VirtualizingWrapPanel.ItemHeightProperty, 156.0);
        var icons = new ItemsPanelTemplate(wrap);
        var newItems = Enumerable.Range(0, 639).Select(i => $"new {i}").ToList();
        var opensInIcons = order == Order.OpensInIcons;
        list.ItemsPanel = opensInIcons ? icons : new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        if (opensInIcons) items.ReplaceAll(newItems);
        var window = new Window
        {
            Content = list, Width = 900, Height = 600, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            if (!opensInIcons)
            {
                items.ReplaceAll(Enumerable.Range(0, 300).Select(i => $"old {i}"));
                Pump();
                if (order == Order.FromScrolledDetails)
                {
                    FindChild<ScrollViewer>(list)!.ScrollToVerticalOffset(250);
                    Pump();
                }

                // a new folder: the list is cleared, the content rules pick icons, then the items arrive
                items.ReplaceAll([]);
                list.ItemsPanel = icons;
                if (order != Order.ItemsBeforeLayout) Pump();
                items.ReplaceAll(newItems);
            }
            Pump();

            var panel = FindChild<VirtualizingWrapPanel>(list);
            Assert.NotNull(panel);
            Assert.True(panel.Children.Count > 0, "no items realized");
            var first = (FrameworkElement)panel.Children[0];
            Assert.Equal("new 0", first.DataContext);
            Assert.True(first.IsVisible && first.ActualHeight > 0, "first item not laid out");
            var origin = first.TranslatePoint(new Point(0, 0), panel);
            Assert.Equal(0, origin.Y);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Smooth_scrolling_extends_moves_and_slides_back_to_rest() => RunSta(() =>
    {
        var list = new ListBox { ItemsSource = Enumerable.Range(0, 2000).Select(i => $"item {i}").ToList() };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        var window = new Window
        {
            Content = list, Width = 600, Height = 400, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        try
        {
            Pump();
            var sv = FindChild<ScrollViewer>(list)!;
            var scroller = SmoothScroller.For(sv);

            scroller.ScrollBy(-500, false, animate: false);
            Pump();
            Assert.Equal(0, sv.VerticalOffset);

            // a second tick while the first one still moves goes on from where the first was going
            scroller.ScrollBy(120, false, animate: true);
            scroller.ScrollBy(120, false, animate: true);
            PumpFor(TimeSpan.FromMilliseconds(500));
            Assert.Equal(240, sv.VerticalOffset, 3);

            // a page jump: the rows start a screen back and slide into place
            var content = (UIElement)sv.Content;
            var shownFrom = sv.VerticalOffset;
            sv.ScrollToVerticalOffset(shownFrom + sv.ViewportHeight);
            list.UpdateLayout();
            scroller.TakeSnapshot();
            scroller.GlideFrom(shownFrom, false);
            Assert.Equal(sv.ViewportHeight, ((TranslateTransform)content.RenderTransform).Y, 3);
            Assert.Equal(shownFrom, scroller.Shown().Y, 3);
            PumpFor(TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, ((TranslateTransform)content.RenderTransform).Y);
            Assert.Null(System.Windows.Documents.AdornerLayer.GetAdornerLayer(content)?.GetAdorners(content));
        }
        finally { window.Close(); }
    });

    private static void PumpFor(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        Pump();
    }

    private static void Pump()
    {
        for (var n = 0; n < 5; n++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
