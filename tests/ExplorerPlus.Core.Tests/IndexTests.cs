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
        Assert.IsType<StatusRequest>(IndexProtocol.DecodeRequest(IndexProtocol.EncodeStatusRequest()));
        var recent = new RecentRequest(@"C:\Users", 133_000_000_000_000_000, 10_000, true, false, true);
        Assert.Equal(recent, IndexProtocol.DecodeRequest(IndexProtocol.EncodeRecent(recent)));
        Assert.Equal(recent with { Scope = null }, IndexProtocol.DecodeRequest(IndexProtocol.EncodeRecent(recent with { Scope = null })));
        var folders = Assert.IsType<FolderSizesRequest>(IndexProtocol.DecodeRequest(IndexProtocol.EncodeFolderSizes([@"C:\Users", @"D:\"])));
        Assert.Equal([@"C:\Users", @"D:\"], folders.Paths);
        Assert.Equal([5L, -1L], IndexProtocol.DecodeFolderSizesResponse(IndexProtocol.EncodeFolderSizesResponse([5, -1])));

        var response = new SearchResponse(SearchStatus.Ok, 7, [new(@"C:\a.txt", Normal, 12, 34, 56), new(@"C:\b", Dir)], true);
        var back = IndexProtocol.DecodeSearchResponse(IndexProtocol.EncodeSearchResponse(response));
        Assert.Equal(response.Hits, back.Hits);
        Assert.Equal((7, true), (back.Total, back.Scanning));
        Assert.True(back.Hits[1].IsFolder);

        var status = new StatusResponse("1.2.3", [new(@"C:\", VolumeState.Ready, 42, null, true), new(@"D:\", VolumeState.Failed, 0, "denied")]);
        var statusBack = IndexProtocol.DecodeStatusResponse(IndexProtocol.EncodeStatusResponse(status));
        Assert.Equal(status.Volumes, statusBack.Volumes);
        Assert.True(statusBack.Speaks);

        // a service from before protocol 3 sends no protocol number, and the app leaves it alone until it is updated
        var old = IndexProtocol.EncodeStatusResponse(status)[..^4];
        Assert.Equal((2, false), (IndexProtocol.DecodeStatusResponse(old).Protocol, IndexProtocol.DecodeStatusResponse(old).Speaks));
    }

    [Fact]
    public void Files_request_round_trips_and_older_requests_stay_at_version_3()
    {
        var files = Assert.IsType<FilesRequest>(IndexProtocol.DecodeRequest(IndexProtocol.EncodeFiles(new FilesRequest(@"C:\Projects", ["jpg", "png"], 10_000, false, true))));
        Assert.Equal((@"C:\Projects", 10_000, false, true), (files.Scope, files.Limit, files.IncludeHidden, files.IncludeProtected));
        Assert.Equal(["jpg", "png"], files.Extensions);
        Assert.Null(Assert.IsType<FilesRequest>(IndexProtocol.DecodeRequest(IndexProtocol.EncodeFiles(new FilesRequest(@"C:\", null, 5, true, false)))).Extensions);

        // a version-3 service keeps answering these, and turns down only the files request
        Assert.Equal(3, BitConverter.ToInt32(IndexProtocol.EncodeSearch(new SearchRequest("a", null, 1, true, false))));
        Assert.Equal(3, BitConverter.ToInt32(IndexProtocol.EncodeRecent(new RecentRequest(null, 1, 1, true, false, true))));
        Assert.Equal(3, BitConverter.ToInt32(IndexProtocol.EncodeFolderSizes([@"C:\"])));
        var asVersion3 = IndexProtocol.EncodeFiles(new FilesRequest(@"C:\", null, 5, true, false));
        BitConverter.GetBytes(3).CopyTo(asVersion3, 0);
        Assert.Throws<InvalidDataException>(() => IndexProtocol.DecodeRequest(asVersion3));

        var v3 = new StatusResponse("0.1.35", [], Protocol: 3);
        Assert.Equal((true, false), (v3.Speaks, v3.HasFiles));
        Assert.True(new StatusResponse("0.1.38", []).HasFiles);
    }

    [Fact]
    public void Flat_view_lists_files_in_path_order()
    {
        var index = Sample();
        index.Upsert(Ref(35), Ref(31), "readme.md", Normal);
        index.Upsert(Ref(36), Ref(32), "App.cs", Normal);
        index.Upsert(Ref(37), Ref(31), "zeta", Dir);
        index.Upsert(Ref(38), Ref(37), "a.txt", Normal);
        index.Upsert(Ref(39), Ref(31), "obj", Dir | Hidden);
        index.Upsert(Ref(40), Ref(39), "b.cs", Normal);
        List<string> Files(IReadOnlySet<string>? extensions = null, IndexSearchOptions? options = null, Func<int, string, bool>? canList = null) =>
            index.Files(options ?? new IndexSearchOptions(IncludeHidden: false), 31, extensions, 100, canList).Hits.Select(h => h.Path).ToList();

        Assert.Equal([@"C:\Projects\readme.md", @"C:\Projects\src\App.cs", @"C:\Projects\src\report.cs", @"C:\Projects\zeta\a.txt"], Files());
        Assert.Equal([@"C:\Projects\obj\b.cs", @"C:\Projects\src\App.cs", @"C:\Projects\src\report.cs"],
            Files(new HashSet<string> { "CS" }, new IndexSearchOptions(IncludeHidden: true)));
        Assert.Equal([@"C:\Projects\readme.md", @"C:\Projects\zeta\a.txt"], Files(canList: (_, path) => !path.EndsWith(@"\src")));

        var cut = index.Files(new IndexSearchOptions(IncludeHidden: false), 31, null, 2);
        Assert.Equal((2, 4), (cut.Hits.Count, cut.Total));
        Assert.Equal([0L, 1L], cut.Hits.Select(h => h.Key));

        // the whole drive: the Recycle Bin never, hidden folders by the options
        Assert.Equal([@"C:\Users\denis\AppData\cache.pdf", @"C:\Users\denis\Documents\report.pdf"],
            index.Files(new IndexSearchOptions(IncludeHidden: true), index.RootRecord, new HashSet<string> { "pdf" }, 100).Hits.Select(h => h.Path));
    }

    [Fact]
    public void Folder_sizes_add_up_and_follow_changes()
    {
        var index = Sample();
        var sizes = new long[40];
        var modified = new long[40];
        (sizes[23], sizes[24], sizes[26], sizes[28], sizes[30], sizes[33]) = (100, 50, 7, 1000, 3, 20);
        modified[28] = 777;
        index.EnterWrite();
        index.ApplySizes(sizes, modified, new long[40]);
        index.ExitWrite();
        Assert.True(index.HasSizes);
        Assert.Equal(150, index.FolderSize(Ref(22)));
        Assert.Equal(157, index.FolderSize(Ref(20)));
        Assert.Equal(1180, index.FolderSize(RootRef));
        Assert.Null(index.FolderSize(Ref(23)));
        var notepad = index.Search(IndexQuery.Parse("notepad"), new IndexSearchOptions(), null, 10).Hits.Single();
        Assert.Equal((1000L, 777L), (notepad.Size, notepad.Modified));

        index.EnterWrite();
        index.Upsert(Ref(23), Ref(32), "report.pdf", Normal); // file moved
        index.Upsert(Ref(22), Ref(31), "Documents", Dir); // folder moved with what's in it
        index.SetFileInfo(Ref(24), 80, 1, 1);
        index.Remove(Ref(33));
        index.Upsert(Ref(26, 2), Ref(20), "new.txt", Normal); // record reused, its old file's delete missed
        index.ExitWrite();
        Assert.Equal(80, index.FolderSize(Ref(22)));
        Assert.Equal(180, index.FolderSize(Ref(31)));
        Assert.Equal(0, index.FolderSize(Ref(21)));
        Assert.Equal(1183, index.FolderSize(RootRef));
    }

    [Fact]
    public void Recent_changes_come_newest_first()
    {
        var index = Sample();
        var modified = new long[40];
        var created = new long[40];
        (modified[23], created[23]) = (500, 100);
        (modified[24], created[24]) = (50, 600); // copied in: an old modified time, a new created time
        modified[26] = 700; // in AppData
        modified[28] = 5000; // a date in the future
        modified[30] = 800; // in the Recycle Bin
        modified[33] = 400;
        (modified[22], created[22]) = (900, 5); // a folder whose content changed
        created[31] = 450; // a new folder
        index.EnterWrite();
        index.ApplySizes(new long[40], modified, created);
        index.ExitWrite();

        List<string> Recent(bool appData, int? scope = null) =>
            index.Recent(300, 1000, new IndexSearchOptions(IncludeAppData: appData), scope, 100).Hits.Select(h => h.Path).ToList();

        Assert.Equal([@"C:\Users\denis\Documents\Report draft.docx", @"C:\Users\denis\Documents\report.pdf", @"C:\Projects", @"C:\Projects\src\report.cs"],
            Recent(appData: false));
        Assert.Equal(@"C:\Users\denis\AppData\cache.pdf", Recent(appData: true)[0]);
        Assert.Equal([@"C:\Projects\src\report.cs"], Recent(appData: false, scope: 31));
        Assert.Equal(-700, index.Recent(300, 1000, new IndexSearchOptions(), null, 1).Hits.Single().Key);
    }

    [Fact]
    public void Cleanup_request_round_trips_and_needs_version_5()
    {
        var sent = new CleanupRequest(@"D:\Photos", CleanupKind.Old, 123, 10 << 20, 10_000, true);
        Assert.Equal(sent, IndexProtocol.DecodeRequest(IndexProtocol.EncodeCleanup(sent)));
        Assert.Null(Assert.IsType<CleanupRequest>(IndexProtocol.DecodeRequest(IndexProtocol.EncodeCleanup(sent with { Scope = null }))).Scope);

        var asVersion4 = IndexProtocol.EncodeCleanup(sent);
        BitConverter.GetBytes(4).CopyTo(asVersion4, 0);
        Assert.Throws<InvalidDataException>(() => IndexProtocol.DecodeRequest(asVersion4));
        Assert.False(new StatusResponse("0.1.41", [], Protocol: 4).HasCleanup);
        Assert.True(new StatusResponse("0.1.44", []).HasCleanup);
    }

    // sizes: report.pdf 100, Report draft.docx 50, cache.pdf (AppData) 7, notepad.exe (Windows) 1000, $R1.pdf 3, report.cs 20
    private static VolumeIndex WithSizes(Action<long[], long[]>? times = null)
    {
        var index = Sample();
        var sizes = new long[40];
        var modified = new long[40];
        var created = new long[40];
        (sizes[23], sizes[24], sizes[26], sizes[28], sizes[30], sizes[33]) = (100, 50, 7, 1000, 3, 20);
        times?.Invoke(modified, created);
        index.EnterWrite();
        index.ApplySizes(sizes, modified, created);
        index.ExitWrite();
        return index;
    }

    private static readonly IndexSearchOptions NoSystem = new(IncludeHidden: false, IncludeProtected: false, IncludeAppData: false, IncludeSystemFolders: false);

    [Fact]
    public void Largest_and_old_files_come_biggest_first_without_system_folders()
    {
        var index = WithSizes((modified, created) =>
        {
            (modified[23], modified[24], modified[33]) = (100, 900, 200);
            created[33] = 950; // copied in lately: not old
        });
        List<string> Largest(IndexSearchOptions options, int? scope = null, int limit = 100) =>
            index.Cleanup(CleanupKind.Largest, 0, 0, options, scope, limit).Hits.Select(h => h.Path).ToList();

        Assert.Equal([@"C:\Users\denis\Documents\report.pdf", @"C:\Users\denis\Documents\Report draft.docx", @"C:\Projects\src\report.cs"], Largest(NoSystem));
        Assert.Equal([@"C:\Windows\notepad.exe", @"C:\Users\denis\Documents\report.pdf"], Largest(new IndexSearchOptions(true, true), limit: 2));
        Assert.Equal([@"C:\Projects\src\report.cs"], Largest(NoSystem, scope: 31));
        var cut = index.Cleanup(CleanupKind.Largest, 0, 0, NoSystem, null, 1);
        Assert.Equal((1, 3, -100L), (cut.Hits.Count, cut.Total, cut.Hits[0].Key));

        // untouched before 500, at least 30 bytes: report.pdf only (report.cs is new, the draft was changed at 900)
        Assert.Equal([@"C:\Users\denis\Documents\report.pdf"], index.Cleanup(CleanupKind.Old, 500, 30, NoSystem, null, 100).Hits.Select(h => h.Path));
        // a file with no known time is never old
        Assert.DoesNotContain(@"C:\Windows\notepad.exe", index.Cleanup(CleanupKind.Old, 500, 0, new IndexSearchOptions(true, true), null, 100).Hits.Select(h => h.Path));
    }

    [Fact]
    public void Empty_folders_list_only_the_outermost()
    {
        var index = WithSizes();
        index.EnterWrite();
        index.Upsert(Ref(40), Ref(31), "empty", Dir);
        index.Upsert(Ref(41), Ref(31), "tree", Dir);
        index.Upsert(Ref(42), Ref(41), "a", Dir);
        index.Upsert(Ref(43), Ref(42), "b", Dir);
        index.Upsert(Ref(44), Ref(41), "c", Dir);
        index.Upsert(Ref(45), Ref(31), "custom", Dir);
        index.Upsert(Ref(46), Ref(45), "desktop.ini", Normal | Hidden | System); // a hidden file still counts
        index.Upsert(Ref(47), Ref(31), "links", Dir);
        index.Upsert(Ref(48), Ref(47), "junction", Dir | 0x400); // a junction isn't empty, nor what holds it
        index.Upsert(Ref(49), Ref(27), "Temp", Dir); // in Windows
        index.Upsert(Ref(50), Ref(31), ".cache", Dir | Hidden);
        index.ExitWrite();
        List<string> Empty(IndexSearchOptions options, int? scope = null, Func<int, string, bool>? canList = null) =>
            index.Cleanup(CleanupKind.Empty, 0, 0, options, scope, 100, canList).Hits.Select(h => h.Path).ToList();

        Assert.Equal([@"C:\Projects\empty", @"C:\Projects\tree"], Empty(NoSystem));
        Assert.Equal([@"C:\Projects\.cache", @"C:\Projects\empty", @"C:\Projects\tree", @"C:\Windows\Temp"], Empty(new IndexSearchOptions(true, true)));
        // inside an empty tree, its folders are listed one level down
        Assert.Equal([@"C:\Projects\tree\a", @"C:\Projects\tree\c"], Empty(NoSystem, scope: 41));
        Assert.Empty(Empty(NoSystem, canList: (_, path) => path != @"C:\Projects"));
        Assert.Equal((2, 0L), (index.Cleanup(CleanupKind.Empty, 0, 0, NoSystem, null, 1).Total, index.Cleanup(CleanupKind.Empty, 0, 0, NoSystem, null, 1).Hits[0].Key));
    }

    [Fact]
    public void Empty_folders_are_checked_on_disk()
    {
        var root = Directory.CreateTempSubdirectory("ExplorerPlusEmpty").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tree", "a", "b"));
            Directory.CreateDirectory(Path.Combine(root, "full", "a"));
            File.WriteAllText(Path.Combine(root, "full", "a", "x.txt"), "x");
            Assert.True(Shell.CleanupList.IsEmptyOnDisk(Path.Combine(root, "tree"), 0, default));
            Assert.False(Shell.CleanupList.IsEmptyOnDisk(Path.Combine(root, "full"), 0, default));
            Assert.False(Shell.CleanupList.IsEmptyOnDisk(Path.Combine(root, "gone"), 0, default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Recent_changes_wait_for_the_times()
    {
        var index = Sample();
        Assert.Empty(index.Recent(0, long.MaxValue, new IndexSearchOptions(), null, 100).Hits);
    }

    [Fact]
    public void Mft_records_give_sizes_times_and_data_runs()

    {
        var record = FileRecord(baseRef: 0, StandardInformation(0x1234567, 0x7654), NonResidentData(5_000_000, [0x21, 0x10, 0x00, 0x01, 0x21, 0x08, 0x80, 0xFF]));
        var sizes = new long[50];
        Array.Fill(sizes, -1L);
        var modified = new long[50];
        var created = new long[50];
        MftReader.ParseRecord(record.ToArray(), 40, sizes, modified, created);
        Assert.Equal((5_000_000L, 0x1234567L, 0x7654L), (sizes[40], modified[40], created[40]));

        // the second run's offset is relative and negative
        Assert.Equal([(0L, 0x100L, 0x10L), (0x10L, 0x80L, 0x8L)], MftReader.DataRuns(record.ToArray(), 1024));

        // an extension record counts toward its base record
        MftReader.ParseRecord(FileRecord(baseRef: Ref(7), ResidentData(42)), 41, sizes, modified, created);
        Assert.Equal((42L, -1L), (sizes[7], sizes[41]));

        // a torn write is skipped
        var torn = FileRecord(baseRef: 0, ResidentData(9));
        torn[1022] ^= 0xFF;
        MftReader.ParseRecord(torn, 42, sizes, modified, created);
        Assert.Equal(-1, sizes[42]);
    }

    private static byte[] FileRecord(ulong baseRef, params byte[][] attributes)
    {
        var r = new byte[1024];
        "FILE"u8.CopyTo(r);
        BitConverter.TryWriteBytes(r.AsSpan(4), (ushort)0x30); // update sequence: check value, then one saved pair per sector
        BitConverter.TryWriteBytes(r.AsSpan(6), (ushort)3);
        BitConverter.TryWriteBytes(r.AsSpan(0x14), (ushort)0x38);
        BitConverter.TryWriteBytes(r.AsSpan(0x16), (ushort)1);
        BitConverter.TryWriteBytes(r.AsSpan(0x20), baseRef);
        var at = 0x38;
        foreach (var a in attributes)
        {
            a.CopyTo(r, at);
            at += a.Length;
        }
        BitConverter.TryWriteBytes(r.AsSpan(at), 0xFFFFFFFFu);
        BitConverter.TryWriteBytes(r.AsSpan(0x30), (ushort)0xABCD);
        foreach (var (end, saved) in new[] { (510, 0x32), (1022, 0x34) })
        {
            r[saved] = r[end];
            r[saved + 1] = r[end + 1];
            BitConverter.TryWriteBytes(r.AsSpan(end), (ushort)0xABCD);
        }
        return r;
    }

    private static byte[] Attribute(uint type, int length, bool nonResident)
    {
        var a = new byte[length];
        BitConverter.TryWriteBytes(a, type);
        BitConverter.TryWriteBytes(a.AsSpan(4), length);
        a[8] = (byte)(nonResident ? 1 : 0);
        return a;
    }

    private static byte[] StandardInformation(long modified, long created = 0)
    {
        var a = Attribute(0x10, 0x60, false);
        BitConverter.TryWriteBytes(a.AsSpan(0x10), 0x48);
        BitConverter.TryWriteBytes(a.AsSpan(0x14), (ushort)0x18);
        BitConverter.TryWriteBytes(a.AsSpan(0x18), created);
        BitConverter.TryWriteBytes(a.AsSpan(0x18 + 8), modified);
        return a;
    }

    private static byte[] ResidentData(int size)
    {
        var a = Attribute(0x80, 0x18 + ((size + 7) & ~7), false);
        BitConverter.TryWriteBytes(a.AsSpan(0x10), size);
        BitConverter.TryWriteBytes(a.AsSpan(0x14), (ushort)0x18);
        return a;
    }

    private static byte[] NonResidentData(long size, byte[] runs)
    {
        var a = Attribute(0x80, 0x50, true);
        BitConverter.TryWriteBytes(a.AsSpan(0x20), (ushort)0x40);
        BitConverter.TryWriteBytes(a.AsSpan(0x28), size); // allocated
        BitConverter.TryWriteBytes(a.AsSpan(0x30), size);
        runs.CopyTo(a, 0x40);
        return a;
    }
}
