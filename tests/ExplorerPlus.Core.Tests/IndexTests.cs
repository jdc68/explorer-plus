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
        var folders = Assert.IsType<FolderSizesRequest>(IndexProtocol.DecodeRequest(IndexProtocol.EncodeFolderSizes([@"C:\Users", @"D:\"])));
        Assert.Equal([@"C:\Users", @"D:\"], folders.Paths);
        Assert.Equal([5L, -1L], IndexProtocol.DecodeFolderSizesResponse(IndexProtocol.EncodeFolderSizesResponse([5, -1])));

        var response = new SearchResponse(SearchStatus.Ok, 7, [new(@"C:\a.txt", Normal, 12, 34), new(@"C:\b", Dir)], true);
        var back = IndexProtocol.DecodeSearchResponse(IndexProtocol.EncodeSearchResponse(response));
        Assert.Equal(response.Hits, back.Hits);
        Assert.Equal((7, true), (back.Total, back.Scanning));
        Assert.True(back.Hits[1].IsFolder);

        var status = new StatusResponse("1.2.3", [new(@"C:\", VolumeState.Ready, 42, null, true), new(@"D:\", VolumeState.Failed, 0, "denied")]);
        var statusBack = IndexProtocol.DecodeStatusResponse(IndexProtocol.EncodeStatusResponse(status));
        Assert.Equal(status.Volumes, statusBack.Volumes);
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
        index.ApplySizes(sizes, modified);
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
        index.SetFileInfo(Ref(24), 80, 1);
        index.Remove(Ref(33));
        index.Upsert(Ref(26, 2), Ref(20), "new.txt", Normal); // record reused, its old file's delete missed
        index.ExitWrite();
        Assert.Equal(80, index.FolderSize(Ref(22)));
        Assert.Equal(180, index.FolderSize(Ref(31)));
        Assert.Equal(0, index.FolderSize(Ref(21)));
        Assert.Equal(1183, index.FolderSize(RootRef));
    }

    [Fact]
    public void Mft_records_give_sizes_times_and_data_runs()
    {
        var record = FileRecord(baseRef: 0, StandardInformation(0x1234567), NonResidentData(5_000_000, [0x21, 0x10, 0x00, 0x01, 0x21, 0x08, 0x80, 0xFF]));
        var sizes = new long[50];
        Array.Fill(sizes, -1L);
        var modified = new long[50];
        MftReader.ParseRecord(record.ToArray(), 40, sizes, modified);
        Assert.Equal((5_000_000L, 0x1234567L), (sizes[40], modified[40]));

        // the second run's offset is relative and negative
        Assert.Equal([(0L, 0x100L, 0x10L), (0x10L, 0x80L, 0x8L)], MftReader.DataRuns(record.ToArray(), 1024));

        // an extension record counts toward its base record
        MftReader.ParseRecord(FileRecord(baseRef: Ref(7), ResidentData(42)), 41, sizes, modified);
        Assert.Equal((42L, -1L), (sizes[7], sizes[41]));

        // a torn write is skipped
        var torn = FileRecord(baseRef: 0, ResidentData(9));
        torn[1022] ^= 0xFF;
        MftReader.ParseRecord(torn, 42, sizes, modified);
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

    private static byte[] StandardInformation(long modified)
    {
        var a = Attribute(0x10, 0x60, false);
        BitConverter.TryWriteBytes(a.AsSpan(0x10), 0x48);
        BitConverter.TryWriteBytes(a.AsSpan(0x14), (ushort)0x18);
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
