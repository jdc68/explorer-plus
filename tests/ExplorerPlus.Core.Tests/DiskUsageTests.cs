using System.Windows;
using ExplorerPlus.App.Views;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class DiskUsageTests
{
    [Fact]
    public void Treemap_fills_the_area_in_proportion_without_overlaps()
    {
        double[] values = [500, 250, 120, 60, 40, 20, 6, 3, 1];
        var bounds = new Rect(10, 20, 400, 300);
        var rects = TreemapLayout.Squarify(values, bounds);
        var scale = bounds.Width * bounds.Height / values.Sum();
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i] * scale, rects[i].Width * rects[i].Height, 3);
            Assert.True(bounds.Contains(new Rect(rects[i].X + 0.001, rects[i].Y + 0.001, Math.Max(0, rects[i].Width - 0.002), Math.Max(0, rects[i].Height - 0.002))));
            for (var j = i + 1; j < values.Length; j++)
            {
                var overlap = Rect.Intersect(rects[i], rects[j]);
                Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 1e-6);
            }
        }
        // squarified: the big cells stay close to square
        Assert.True(Math.Max(rects[0].Width / rects[0].Height, rects[0].Height / rects[0].Width) < 3);
        Assert.Empty(TreemapLayout.Squarify([], bounds));
    }

    [Fact]
    public async Task Folders_get_their_totals_and_shrink_after_a_delete()
    {
        var root = Path.Combine(Path.GetTempPath(), "ep-usage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Write(@"big\a.bin", 6000);
            Write(@"big\inner\b.bin", 3000);
            Write(@"small\c.bin", 500);
            Write("top.bin", 1000);
            var usage = new DiskUsage(root, useIndex: false);
            await usage.LoadAsync(usage.Root, CancellationToken.None);
            for (var i = 0; i < 100 && usage.Root.Size != 10_500; i++) await Task.Delay(50);

            Assert.Equal(10_500, usage.Root.Size);
            Assert.Equal(["big", "top.bin", "small"], usage.Root.Children!.Select(c => c.Name));
            var big = usage.Root.Children![0];
            Assert.Equal(9000, big.Size);
            Assert.Null(big.Children);

            await usage.LoadAsync(big, CancellationToken.None);
            for (var i = 0; i < 100 && big.Children!.Any(c => c.Size < 0); i++) await Task.Delay(50);
            Assert.Equal(["a.bin", "inner"], big.Children!.Select(c => c.Name));
            Assert.True(big.IsAncestorOf(big.Children![1]));

            usage.Remove(big.Children![0]);
            Assert.Equal(3000, big.Size);
            Assert.Equal(4500, usage.Root.Size);
            Assert.Equal(["big", "top.bin", "small"], usage.Root.Children!.Select(c => c.Name));

            // past the limit, and anything too small to see, is drawn as one "other" cell
            var shown = DiskUsage.Visible(usage.Root, 1, 0);
            Assert.Equal(2, shown.Count);
            Assert.Equal((2, 1500L), (shown[1].OtherCount, shown[1].Size));
        }
        finally { Directory.Delete(root, recursive: true); }

        void Write(string name, int bytes)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
    }
}
