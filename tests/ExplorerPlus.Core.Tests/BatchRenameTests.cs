using System.Globalization;
using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class BatchRenameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-rename-").FullName;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public BatchRenameTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        Directory.Delete(_root, true);
    }

    private BatchRenameItem File_(string name, DateTime? modified = null) => new(Path.Combine(_root, name), false, modified);

    private static string[] New(IReadOnlyList<BatchRenamePlan> plan) => plan.Select(p => p.NewName).ToArray();

    [Fact]
    public void Pattern_numbers_items_and_keeps_extensions()
    {
        var plan = BatchRename.Plan([File_("IMG_4411.jpg"), File_("IMG_4412.JPG"), File_("notes.txt")],
            new BatchRenameOptions { Pattern = "Holiday {n:000}", Start = 1 }, _ => false);
        Assert.Equal(["Holiday 001.jpg", "Holiday 002.JPG", "Holiday 003.txt"], New(plan));
        Assert.All(plan, p => Assert.Null(p.Error));
    }

    [Fact]
    public void Find_and_replace_is_literal_unless_regex_is_on()
    {
        var items = new[] { File_("a.b (1).txt"), File_("draft-v2.txt") };
        Assert.Equal(["a-b (1).txt", "draft-v2.txt"], New(BatchRename.Plan(items, new BatchRenameOptions { Find = ".", Replace = "-" }, _ => false)));
        Assert.Equal(["a.b.txt", "draft.txt"],
            New(BatchRename.Plan(items, new BatchRenameOptions { Find = @"( \(\d+\)|-v\d+)$", Replace = "", UseRegex = true }, _ => false)));
        Assert.Equal(["$1.b (1).txt", "draft-v2.txt"], New(BatchRename.Plan(items, new BatchRenameOptions { Find = "a.", Replace = "$1." }, _ => false)));
    }

    [Fact]
    public void Case_and_extension_options()
    {
        var items = new[] { File_("My Photo.JPG"), new BatchRenameItem(Path.Combine(_root, "Some.Folder"), true) };
        Assert.Equal(["my photo.JPG", "some.folder"], New(BatchRename.Plan(items, new BatchRenameOptions { Case = NameCase.Lower }, _ => false)));
        Assert.Equal(["my photo.jpg", "some.folder"],
            New(BatchRename.Plan(items, new BatchRenameOptions { Case = NameCase.Lower, IncludeExtension = true }, _ => false)));
        Assert.Equal(["My Photo.JPG", "Some.Folder"], New(BatchRename.Plan(items, new BatchRenameOptions { Case = NameCase.Title }, _ => false)));
    }

    [Fact]
    public void Tokens_for_date_extension_and_parent()
    {
        var plan = BatchRename.Plan([File_("x.png", new DateTime(2026, 3, 9))],
            new BatchRenameOptions { Pattern = "{date:yyyyMMdd}_{parent}_{ext}" }, _ => false);
        Assert.Equal($"20260309_{Path.GetFileName(_root)}_png.png", plan[0].NewName);
    }

    [Fact]
    public void Conflicts_are_reported_per_item()
    {
        var items = new[] { File_("a.txt"), File_("b.txt"), File_("c.txt") };
        var taken = Path.Combine(_root, "taken.txt");
        var plan = BatchRename.Plan(items, new BatchRenameOptions { Pattern = "same" }, _ => false);
        Assert.All(plan, p => Assert.NotNull(p.Error));

        plan = BatchRename.Plan([items[0]], new BatchRenameOptions { Pattern = "taken" }, p => p.Equals(taken, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("An item with this name already exists.", plan[0].Error);

        // b.txt is in the batch, so a.txt may take its name while b.txt moves on.
        plan = BatchRename.Plan([items[0], items[1]], new BatchRenameOptions { Pattern = "{n}", Start = 0 }, _ => true);
        Assert.All(plan, p => Assert.Equal("An item with this name already exists.", p.Error));
        plan = BatchRename.Plan([items[0], items[1]], new BatchRenameOptions { Find = "a", Replace = "b" }, _ => true);
        Assert.Equal("Another item would get the same name.", plan[0].Error);

        Assert.NotNull(BatchRename.Plan([items[0]], new BatchRenameOptions { Pattern = "con" }, _ => false)[0].Error);
        Assert.NotNull(BatchRename.Plan([items[0]], new BatchRenameOptions { Pattern = "a?b" }, _ => false)[0].Error);
        Assert.NotNull(BatchRename.Plan([items[0]], new BatchRenameOptions { Find = "(", UseRegex = true }, _ => false)[0].Error);
    }

    [Fact]
    public async Task Apply_renames_through_the_shell_including_swaps_and_can_be_reverted()
    {
        foreach (var n in new[] { "1.txt", "2.txt", "3.txt" }) File.WriteAllText(Path.Combine(_root, n), n);
        // Shift every number up by one: 1→2, 2→3, 3→4 needs temporary names.
        var plan = BatchRename.Plan([File_("1.txt"), File_("2.txt"), File_("3.txt")], new BatchRenameOptions { Pattern = "{n}", Start = 2 });
        Assert.All(plan, p => Assert.Null(p.Error));

        var result = await BatchRename.ApplyAsync(plan.Select(p => (p.Path, p.NewName)).ToList(), IntPtr.Zero);
        Assert.Equal(FileOpResult.Succeeded, result.Result);
        Assert.Equal(3, result.Renamed.Count);
        Assert.Equal("1.txt", File.ReadAllText(Path.Combine(_root, "2.txt")));
        Assert.Equal("3.txt", File.ReadAllText(Path.Combine(_root, "4.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "1.txt")));
        Assert.Empty(Directory.GetFiles(_root, "~eprename-*"));

        // Undo: every item back to its previous name.
        var back = await BatchRename.ApplyAsync(result.Renamed.Select(r => (r.NewPath, r.OldName)).ToList(), IntPtr.Zero);
        Assert.Equal(FileOpResult.Succeeded, back.Result);
        foreach (var n in new[] { "1.txt", "2.txt", "3.txt" }) Assert.Equal(n, File.ReadAllText(Path.Combine(_root, n)));
        Assert.False(File.Exists(Path.Combine(_root, "4.txt")));
    }

    [Fact]
    public async Task Apply_handles_case_only_renames()
    {
        File.WriteAllText(Path.Combine(_root, "readme.md"), "x");
        var result = await BatchRename.ApplyAsync([(Path.Combine(_root, "readme.md"), "README.md")], IntPtr.Zero);
        Assert.Equal(FileOpResult.Succeeded, result.Result);
        Assert.Equal("README.md", Path.GetFileName(Directory.GetFiles(_root).Single()));
    }

    [Theory]
    [InlineData("set", "Settings", true)]
    [InlineData("nt", "New tab", true)]
    [InlineData("xyz", "New tab", false)]
    public void Fuzzy_match_finds_letters_in_order(string query, string text, bool matches) =>
        Assert.Equal(matches, FuzzyMatch.Score(query, text) is not null);

    [Fact]
    public void Fuzzy_match_ranks_prefixes_and_word_starts_first()
    {
        var a = FuzzyMatch.Score("tab", "Tabs: close other tabs")!.Value;
        var b = FuzzyMatch.Score("tab", "New tab")!.Value;
        var c = FuzzyMatch.Score("tab", "Toggle all bars")!.Value;
        Assert.True(a > b && b > c);
    }
}
