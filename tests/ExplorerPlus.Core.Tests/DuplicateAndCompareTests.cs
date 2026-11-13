using ExplorerPlus.Core.Shell;
using Xunit;

namespace ExplorerPlus.Core.Tests;

public sealed class DuplicateAndCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ExplorerPlusTests", Guid.NewGuid().ToString("N"));

    public DuplicateAndCompareTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, byte[] content, DateTime? modified = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (modified is { } m) File.SetLastWriteTime(path, m);
        return path;
    }

    private static byte[] Bytes(int length, byte seed)
    {
        var b = new byte[length];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(seed + i * 7);
        return b;
    }

    [Fact]
    public void Duplicates_are_found_by_content_not_name_or_size()
    {
        var big = Bytes(200_000, 1);
        var bigTail = (byte[])big.Clone();
        bigTail[^1] ^= 0xFF; // same size and same first 64 KB, different end
        Write(@"a\photo.jpg", big);
        Write(@"b\copy of photo.jpg", big);
        Write(@"b\c\photo (2).jpg", big);
        Write(@"b\almost.jpg", bigTail);
        Write(@"small1.txt", Bytes(10, 5));
        Write(@"a\small2.txt", Bytes(10, 5));
        Write(@"other.txt", Bytes(10, 6));
        Write(@"empty1.txt", []);
        Write(@"empty2.txt", []);

        var groups = DuplicateFinder.Find(_root, default).ToList();

        Assert.Equal(2, groups.Count);
        Assert.Equal(200_000, groups[0][0].Size); // biggest first
        Assert.Equal(["copy of photo.jpg", "photo (2).jpg", "photo.jpg"], groups[0].Select(c => Path.GetFileName(c.Path)).Order().ToArray());
        Assert.Equal(["small1.txt", "small2.txt"], groups[1].Select(c => Path.GetFileName(c.Path)).Order().ToArray());
    }

    [Fact]
    public void Duplicate_entries_come_in_sections()
    {
        Write(@"x\one.bin", Bytes(5000, 9));
        Write(@"y\two.bin", Bytes(5000, 9));
        var location = new ShellLocation { Pidl = null!, DisplayName = "", ParsingName = DuplicateFinder.Url(_root), DuplicatesIn = _root };

        var entries = DuplicateFinder.Enumerate(location, default).ToList();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(entries[0].Section, e.Section));
        Assert.StartsWith("one.bin · ", entries[0].Section);
    }

    [Fact]
    public void Compare_tells_what_differs_and_which_side_is_newer()
    {
        var old = new DateTime(2024, 1, 1, 12, 0, 0);
        var later = old.AddHours(3);
        Write(@"L\same.txt", Bytes(100, 1), old);
        Write(@"R\same.txt", Bytes(100, 1), old);
        Write(@"L\edited.txt", Bytes(120, 2), later);
        Write(@"R\edited.txt", Bytes(100, 2), old);
        Write(@"L\stale.txt", Bytes(100, 3), old);
        Write(@"R\stale.txt", Bytes(100, 4), later);
        Write(@"L\sub\only-left.txt", Bytes(10, 5));
        Write(@"R\sub\keep.txt", Bytes(10, 5), old);
        Write(@"L\sub\keep.txt", Bytes(10, 5), old);
        Write(@"R\new-folder\inside.txt", Bytes(10, 6));
        Write(@"L\sneaky.txt", Bytes(100, 7), old);
        Write(@"R\sneaky.txt", Bytes(100, 8), old);

        string left = Path.Combine(_root, "L"), right = Path.Combine(_root, "R");
        var byState = FolderCompare.Compare(left, right, byContent: false).ToDictionary(i => i.Relative, i => i.State);

        Assert.Equal(CompareState.Same, byState["same.txt"]);
        Assert.Equal(CompareState.LeftNewer, byState["edited.txt"]);
        Assert.Equal(CompareState.RightNewer, byState["stale.txt"]);
        Assert.Equal(CompareState.LeftOnly, byState[@"sub\only-left.txt"]);
        Assert.Equal(CompareState.Same, byState[@"sub\keep.txt"]);
        Assert.Equal(CompareState.RightOnly, byState["new-folder"]);
        Assert.False(byState.ContainsKey(@"new-folder\inside.txt"));
        Assert.Equal(CompareState.Same, byState["sneaky.txt"]); // same size and time: only the content tells

        var deep = FolderCompare.Compare(left, right, byContent: true).ToDictionary(i => i.Relative, i => i.State);
        Assert.Equal(CompareState.Different, deep["sneaky.txt"]);
        Assert.Equal(CompareState.Same, deep["same.txt"]);
    }

    [Fact]
    public void Sync_plans_copy_the_right_way_and_recycle_what_they_replace()
    {
        string left = Path.Combine(_root, "L"), right = Path.Combine(_root, "R");
        CompareItem[] items =
        [
            new("a.txt", false, CompareState.LeftOnly),
            new("b.txt", false, CompareState.RightOnly),
            new(@"sub\c.txt", false, CompareState.LeftNewer),
            new("d.txt", false, CompareState.RightNewer),
            new("e.txt", false, CompareState.Different),
            new("f.txt", false, CompareState.Same),
        ];

        var mirror = FolderCompare.Plan(left, right, items, SyncDirection.LeftToRight, deleteExtras: true);
        Assert.Equal(
            [(Path.Combine(left, "a.txt"), right), (Path.Combine(left, @"sub\c.txt"), Path.Combine(right, "sub")),
             (Path.Combine(left, "d.txt"), right), (Path.Combine(left, "e.txt"), right)],
            mirror.Copies);
        Assert.Equal([Path.Combine(right, "b.txt"), Path.Combine(right, "d.txt"), Path.Combine(right, "e.txt"), Path.Combine(right, @"sub\c.txt")],
            mirror.Deletes.Order().ToArray());

        var both = FolderCompare.Plan(left, right, items, SyncDirection.BothWays, deleteExtras: true);
        Assert.Equal(
            [(Path.Combine(left, "a.txt"), right), (Path.Combine(right, "b.txt"), left),
             (Path.Combine(left, @"sub\c.txt"), Path.Combine(right, "sub")), (Path.Combine(right, "d.txt"), left)],
            both.Copies);
        Assert.Equal([Path.Combine(right, @"sub\c.txt"), Path.Combine(left, "d.txt")], both.Deletes);
    }
}
