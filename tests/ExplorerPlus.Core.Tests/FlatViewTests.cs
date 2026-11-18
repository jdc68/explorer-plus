using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Tests;

public class FlatViewTests
{
    [Fact]
    public void Walk_lists_files_in_path_order_with_a_section_per_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ep-flat-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var file in new[] { "b.txt", "a.jpg", @"sub\c.txt", @"sub\deep\d.jpg", @"zz\f.txt", @"Sub2\e.png" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
                File.WriteAllText(Path.Combine(root, file), "x");
            }
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            var folder = new ShellLocation
            {
                Pidl = Pidl.Desktop, DisplayName = "Project", ParsingName = root, FileSystemPath = root,
                Attributes = SFGAO.FOLDER | SFGAO.FILESYSTEM,
            };

            var all = FlatView.Create(folder);
            var listed = FlatView.Enumerate(all, CancellationToken.None, useIndex: false).ToList();
            Assert.Equal(["a.jpg", "b.txt", "c.txt", "d.jpg", "e.png", "f.txt"], listed.Select(e => e.Name));
            Assert.Equal(["Project", "Project", "sub", @"sub\deep", "Sub2", "zz"], listed.Select(e => e.Section));
            Assert.Equal([0, 0, 1, 2, 3, 4], listed.Select(e => e.SectionRank));
            Assert.Equal((6, 6), (all.FlatResults!.Total, all.FlatResults.Count));

            FlatView.KindExtensions = kind => kind == "Pictures" ? ["jpg", "png"] : null;
            var pictures = FlatView.Enumerate(FlatView.Create(folder, "Pictures"), CancellationToken.None, useIndex: false).ToList();
            Assert.Equal(["a.jpg", "d.jpg", "e.png"], pictures.Select(e => e.Name));
            Assert.Equal([0, 1, 2], pictures.Select(e => e.SectionRank));
        }
        finally
        {
            FlatView.KindExtensions = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Url_keeps_the_folder_and_kind()
    {
        Assert.Equal(@"explorerplus-flat:in=C%3A%5CUsers%5Cme&kind=Pictures", FlatView.Url(@"C:\Users\me", "Pictures"));
        Assert.Equal(@"explorerplus-flat:in=C%3A%5C", FlatView.Url(@"C:\"));
    }
}
