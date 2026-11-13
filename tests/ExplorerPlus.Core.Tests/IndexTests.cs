using ExplorerPlus.Indexing;

namespace ExplorerPlus.Core.Tests;

public class IndexTests
{
    private const ulong RootRef = 0x0005_0000_0000_0005;
    private const uint Dir = 0x10, Hidden = 0x2, System = 0x4, Normal = 0x80;

    private static ulong Ref(int record, ushort sequence = 1) => ((ulong)sequence << 48) | (uint)record;

    /// <summary>
    /// C:\Users\denis\Documents\report.pdf, C:\Users\denis\Documents\Report draft.docx, C:\Users\denis\AppData (hidden)\cache.pdf,
    /// C:\Windows (system) \notepad.exe, C:\$Recycle.Bin\$R1.pdf, C:\Projects\src\report.cs
    /// </summary>
    private static VolumeIndex Sample()
    {
        var index = new VolumeIndex(@"C:\", RootRef, 1234);
        index.EnterWrite();
        index.Upsert(Ref(20), RootRef, "Users", Dir);
        index.Upsert(Ref(21), Ref(20), "denis", Dir);
        index.Upsert(Ref(22), Ref(21), "Documents", Dir);
        index.Upsert(Ref(23), Ref(22), "report.pdf", Normal);
        index.Upsert(Ref(24), Ref(22), "Report draft.docx", Normal);
        index.Upsert(Ref(25), Ref(21), "AppData", Dir | Hidden);
        index.Upsert(Ref(26), Ref(25), "cache.pdf", Normal);
        index.Upsert(Ref(27), RootRef, "Windows", Dir);
        index.Upsert(Ref(28), Ref(27), "notepad.exe", Normal);
        index.Upsert(Ref(29), RootRef, "$Recycle.Bin", Dir | Hidden | System);
        index.Upsert(Ref(30), Ref(29), "$R1.pdf", Normal);
        index.Upsert(Ref(31), RootRef, "Projects", Dir);
        index.Upsert(Ref(32), Ref(31), "src", Dir);
        index.Upsert(Ref(33), Ref(32), "report.cs", Normal);
        // An orphan (its parent is NTFS metadata) never shows.
        index.Upsert(Ref(34), Ref(11), "$UsnJrnl", Normal);
        index.ExitWrite();
        return index;
    }

    private static List<string> Find(VolumeIndex index, string query, IndexSearchOptions? options = null, int? scope = null, int limit = 100) =>
        index.Search(IndexQuery.Parse(query), options ?? new IndexSearchOptions(), scope, limit).Hits.Select(h => h.Path).ToList();

    [Theory]
    [InlineData("*.pdf", "report.PDF", true)]
    [InlineData("*.pdf", "report.pdf.txt", false)]
    [InlineData("rep?rt*", "Report draft.docx", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "", true)]
    public void Glob_matches_whole_name(string pattern, string text, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, text));

    [Fact]
    public void Words_must_all_match_anywhere_in_the_name()
    {
        var index = Sample();
        Assert.Equal([@"C:\Users\denis\Documents\Report draft.docx"], Find(index, "draft rep"));
        Assert.Empty(Find(index, "report zzz"));
    }

    [Fact]
    public void Quotes_keep_spaces_and_bang_excludes()
    {
        var index = Sample();
        Assert.Equal([@"C:\Users\denis\Documents\Report draft.docx"], Find(index, "\"report draft\""));
        Assert.DoesNotContain(@"C:\Users\denis\Documents\report.pdf", Find(index, "report !pdf"));
        Assert.True(IndexQuery.Parse("!pdf").IsEmpty);
    }

    [Fact]
    public void Best_matches_come_first()
    {
        var hits = Find(Sample(), "report");
        // Whole name (without extension) first, shorter names first, then names that start with the word.
        Assert.Equal(@"C:\Projects\src\report.cs", hits[0]);
        Assert.Equal(@"C:\Users\denis\Documents\report.pdf", hits[1]);
        Assert.Equal(@"C:\Users\denis\Documents\Report draft.docx", hits[2]);
    }

    [Fact]
    public void Wildcards_and_path_words()
    {
        var index = Sample();
        Assert.Equal([@"C:\Users\denis\AppData\cache.pdf", @"C:\Users\denis\Documents\report.pdf"], Find(index, "*.pdf").Order().ToList());
        Assert.Equal([@"C:\Projects\src\report.cs"], Find(index, @"report projects\src"));
        Assert.Equal([@"C:\Projects\src\report.cs"], Find(index, "report projects/src"));
    }

    [Fact]
    public void Hidden_and_protected_items_follow_the_options()
    {
        var index = Sample();
        Assert.Contains(@"C:\Users\denis\AppData\cache.pdf", Find(index, "cache"));
        // With hidden items off, nothing inside a hidden folder shows either.
        Assert.Empty(Find(index, "cache", new IndexSearchOptions(IncludeHidden: false)));
        // The Recycle Bin's contents never show, even with protected files on.
        Assert.Empty(Find(index, "$R1", new IndexSearchOptions(true, true)));
        Assert.Empty(Find(index, "UsnJrnl", new IndexSearchOptions(true, true)));
    }

    [Fact]
    public void Scope_limits_to_a_folder_even_inside_a_hidden_one()
    {
        var index = Sample();
        var docs = index.Find(Ref(22))!.Value;
        Assert.Equal([@"C:\Users\denis\Documents\report.pdf"], Find(index, "*.pdf", scope: docs));
        var appData = index.Find(Ref(25))!.Value;
        Assert.Equal([@"C:\Users\denis\AppData\cache.pdf"], Find(index, "pdf", new IndexSearchOptions(IncludeHidden: false), appData));
        Assert.Null(index.Find(Ref(22, sequence: 9)));
    }

    [Fact]
    public void Renames_moves_and_deletes_update_the_index()
    {
        var index = Sample();
        index.EnterWrite();
        index.Upsert(Ref(23), Ref(31), "final.pdf", Normal); // renamed and moved to Projects
        index.Remove(Ref(24, sequence: 2)); // a stale delete for a reused record is ignored
        index.Remove(Ref(28));
        index.ExitWrite();
        Assert.Equal([@"C:\Projects\final.pdf"], Find(index, "final"));
        Assert.Empty(Find(index, "notepad"));
        Assert.Single(Find(index, "draft"));
        Assert.Equal(14, index.Count);
    }

    [Fact]
    public void Limit_and_access_filter()
    {
        var index = Sample();
        var (hits, total) = index.Search(IndexQuery.Parse("report"), new IndexSearchOptions(), null, 1);
        Assert.Equal(3, total);
        Assert.Single(hits);
        var asked = new List<string>();
        (hits, _) = index.Search(IndexQuery.Parse("report"), new IndexSearchOptions(), null, 10,
            (_, folder) => { asked.Add(folder); return !folder.StartsWith(@"C:\Users", StringComparison.Ordinal); });
        Assert.Equal([@"C:\Projects\src\report.cs"], hits.Select(h => h.Path));
        Assert.Equal(2, asked.Count); // once per parent folder
    }

    [Fact]
    public void Index_grows_and_compacts_names()
    {
        var index = new VolumeIndex(@"D:\", RootRef, 1, capacity: 64);
        index.EnterWrite();
        for (var i = 0; i < 5000; i++) index.Upsert(Ref(100 + i), RootRef, $"file{i}.txt", Normal);
        index.ExitWrite();
        for (var round = 0; round < 40; round++)
        {
            index.EnterWrite();
            for (var i = 0; i < 5000; i++) index.Upsert(Ref(100 + i), RootRef, $"renamed{round}_{i}_with_a_longer_name.txt", Normal);
            index.ExitWrite();
        }
        Assert.Equal(5000, index.Count);
        Assert.Equal([@"D:\renamed39_4999_with_a_longer_name.txt"], Find(index, "renamed39_4999_"));
        Assert.True(index.MemoryBytes < 20_000_000, $"{index.MemoryBytes} bytes");
    }

    [Fact]
    public void File_information_matches_the_native_layout() =>
        Assert.Equal(52, global::System.Runtime.InteropServices.Marshal.SizeOf<ExplorerPlus.Interop.BY_HANDLE_FILE_INFORMATION>());

    [Fact]
    public void File_ids_are_read_correctly()
    {
        // A file's id from GetFileInformationByHandle must be what FileSystemInfo reports through the same call.
        var path = Path.Combine(Path.GetTempPath(), $"ep-index-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        try
        {
            using var handle = File.OpenHandle(path);
            Assert.True(ExplorerPlus.Interop.VolumeNative.GetFileInformationByHandle(handle, out var info));
            Assert.Equal(1u, info.NumberOfLinks);
            Assert.Equal(1u, info.FileSizeLow);
            Assert.Equal((uint)FileAttributes.Archive, info.FileAttributes & (uint)FileAttributes.Archive);
            Assert.Equal(new FileInfo(path).LastWriteTimeUtc, DateTime.FromFileTimeUtc(info.LastWriteTime));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Protocol_round_trips()
    {
        var request = new SearchRequest("report *.pdf", @"C:\Users", 500, true, false);
        Assert.Equal(request, IndexProtocol.DecodeRequest(IndexProtocol.EncodeSearch(request)));
        Assert.Null(IndexProtocol.DecodeRequest(IndexProtocol.EncodeStatusRequest()));

        var response = new SearchResponse(SearchStatus.Ok, 7, [new(@"C:\a.txt", Normal), new(@"C:\b", Dir)], true);
        var back = IndexProtocol.DecodeSearchResponse(IndexProtocol.EncodeSearchResponse(response));
        Assert.Equal(response.Hits, back.Hits);
        Assert.Equal((7, true), (back.Total, back.Scanning));
        Assert.True(back.Hits[1].IsFolder);

        var status = new StatusResponse("1.2.3", [new(@"C:\", VolumeState.Ready, 42, null), new(@"D:\", VolumeState.Failed, 0, "denied")]);
        var statusBack = IndexProtocol.DecodeStatusResponse(IndexProtocol.EncodeStatusResponse(status));
        Assert.Equal(status.Volumes, statusBack.Volumes);
    }
}
