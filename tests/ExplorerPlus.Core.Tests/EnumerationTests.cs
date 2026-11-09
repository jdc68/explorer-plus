using System.Diagnostics;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using Xunit.Abstractions;

namespace ExplorerPlus.Core.Tests;

public class EnumerationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-enum-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Fast_enumerator_matches_dotnet_enumeration()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(_root, "b.bin"), new byte[5000]);
        var hidden = Path.Combine(_root, ".hidden");
        File.WriteAllText(hidden, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var expected = Directory.EnumerateFileSystemEntries(_root).Select(p => Path.GetFileName(p)!).Order().ToList();
        var entries = FastFolderEnumerator.Enumerate(_root).ToList();
        Assert.Equal(expected, entries.Select(e => e.Name).Order().ToList());

        var a = entries.Single(e => e.Name == "a.txt");
        Assert.Equal(5, a.Size);
        Assert.False(a.IsFolder);
        Assert.Equal(Path.Combine(_root, "a.txt"), a.Path);
        Assert.False(string.IsNullOrEmpty(a.TypeName));
        Assert.True(entries.Single(e => e.Name == "sub").IsFolder);
        Assert.True(entries.Single(e => e.Name == ".hidden").IsHidden);
        Assert.Equal(5000, entries.Single(e => e.Name == "b.bin").Size);
    }

    [Fact]
    public void Missing_folder_throws_directory_not_found()
    {
        Assert.Throws<DirectoryNotFoundException>(() => FastFolderEnumerator.Enumerate(Path.Combine(_root, "nope", "deeper")).ToList());
    }

    [Fact]
    public void Empty_folder_yields_nothing() => Assert.Empty(FastFolderEnumerator.Enumerate(_root));

    [Fact]
    public void Stat_reads_single_item()
    {
        var f = Path.Combine(_root, "x.log");
        File.WriteAllText(f, "123");
        var e = FastFolderEnumerator.Stat(f);
        Assert.NotNull(e);
        Assert.Equal("x.log", e!.Name);
        Assert.Equal(3, e.Size);
        Assert.Null(FastFolderEnumerator.Stat(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public void Ten_thousand_files_list_quickly()
    {
        for (var i = 0; i < 10_000; i++) File.WriteAllBytes(Path.Combine(_root, $"file{i}.dat"), []);
        // Warm up type cache, then measure.
        _ = FastFolderEnumerator.Enumerate(_root).Take(10).ToList();
        var sw = Stopwatch.StartNew();
        var count = FastFolderEnumerator.Enumerate(_root).Count();
        sw.Stop();
        output.WriteLine($"10,000 files listed in {sw.ElapsedMilliseconds} ms");
        Assert.Equal(10_000, count);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Folder_loader_streams_first_batch_early()
    {
        for (var i = 0; i < 3000; i++) File.WriteAllBytes(Path.Combine(_root, $"f{i}.dat"), []);
        var location = await ShellLocation.ResolveAsync(_root);
        Assert.NotNull(location);
        Assert.True(location!.IsFileSystemFolder);

        var batches = new List<int>();
        await foreach (var b in FolderLoader.LoadAsync(location)) batches.Add(b.Count);
        Assert.Equal(3000, batches.Sum());
        Assert.True(batches.Count > 1, "expected a small first batch followed by the rest");
        Assert.True(batches[0] <= 300);
    }

    [Fact]
    public async Task This_PC_lists_each_drive_once_and_no_OneDrive()
    {
        var thisPc = await ShellLocation.ResolveAsync(ShellLocation.Known.ThisPC);
        Assert.NotNull(thisPc);
        Assert.False(thisPc!.IsFileSystemFolder);
        var items = await StaWorkerPool.Enumeration.Run(() => ShellFolderEnumerator.Enumerate(thisPc.Pidl).ToList());
        foreach (var i in items) output.WriteLine($"{i.DisplayName} | {i.Path} | {i.TypeName}");

        var driveRoots = items.Where(i => i.Path is { Length: 3 }).Select(i => i.Path!.ToUpperInvariant()).ToList();
        Assert.NotEmpty(driveRoots);
        Assert.Equal(driveRoots.Count, driveRoots.Distinct().Count());
        Assert.DoesNotContain(items, i => i.DisplayName.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Drive_service_deduplicates_volumes()
    {
        var drives = await DriveService.GetDrivesAsync();
        foreach (var d in drives) output.WriteLine($"{d.Root} {d.DisplayName} {d.Kind} {d.FreeBytes}/{d.TotalBytes} {d.VolumeId}");
        Assert.Contains(drives, d => d.Root.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(drives.Count, drives.Select(d => d.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var ids = drives.Where(d => d.VolumeId is not null).Select(d => d.VolumeId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Known_folder_resolves_to_file_system_path()
    {
        var loc = await ShellLocation.ResolveAsync("shell:Downloads");
        Assert.NotNull(loc);
        output.WriteLine($"{loc!.DisplayName} | {loc.ParsingName} | {loc.FileSystemPath} | {loc.Attributes}");
        Assert.True(loc.IsFileSystemFolder);
        Assert.Equal(loc.FileSystemPath, loc.ParsingName, ignoreCase: true);

        var chain = await loc.GetAncestryAsync();
        output.WriteLine(string.Join(" > ", chain.Select(c => c.DisplayName)));
        Assert.True(chain.Count >= 3);
        Assert.Equal(loc.DisplayName, chain[^1].DisplayName);
        Assert.Contains(chain, c => c.FileSystemPath is { Length: 3 }); // the drive is part of the path

        var parent = await loc.GetParentAsync();
        Assert.Equal(Path.GetDirectoryName(loc.FileSystemPath), parent!.FileSystemPath, ignoreCase: true);

        var drive = await ShellLocation.ResolveAsync(@"C:\");
        var up = await drive!.GetParentAsync();
        Assert.Equal(ShellLocation.Known.ThisPC, up!.ParsingName, ignoreCase: true);
    }

    [Fact]
    public void Natural_sort_matches_explorer()
    {
        var names = new[] { "file10.txt", "file2.txt", "File1.txt" };
        Assert.Equal(["File1.txt", "file2.txt", "file10.txt"], names.Order(NaturalComparer.Instance).ToArray());
    }

    [Fact]
    public void Unique_name_follows_explorer_numbering()
    {
        Assert.Equal("New folder", FileOperations.UniqueName(_root, "New folder"));
        Directory.CreateDirectory(Path.Combine(_root, "New folder"));
        Assert.Equal("New folder (2)", FileOperations.UniqueName(_root, "New folder"));
        Directory.CreateDirectory(Path.Combine(_root, "New folder (2)"));
        Assert.Equal("New folder (3)", FileOperations.UniqueName(_root, "New folder"));
    }

    [Fact]
    public async Task File_operations_create_rename_and_delete()
    {
        var folder = await ShellLocation.ResolveAsync(_root);
        Assert.Equal(FileOpResult.Succeeded, await FileOperations.NewFolderAsync(folder!.Pidl, "made", IntPtr.Zero));
        Assert.True(Directory.Exists(Path.Combine(_root, "made")));

        var made = await StaWorkerPool.General.Run(() => ShellNative.Parse(Path.Combine(_root, "made")));
        Assert.Equal(FileOpResult.Succeeded, await FileOperations.RenameAsync(made, "renamed", IntPtr.Zero));
        Assert.True(Directory.Exists(Path.Combine(_root, "renamed")));

        var file = Path.Combine(_root, "copy-me.txt");
        File.WriteAllText(file, "x");
        var src = await StaWorkerPool.General.Run(() => ShellNative.Parse(file));
        Assert.Equal(FileOpResult.Succeeded, await FileOperations.CopyAsync([src], folder.Pidl, IntPtr.Zero, renameOnCollision: true));
        Assert.Equal(2, Directory.GetFiles(_root, "copy-me*.txt").Length);

        var renamed = await StaWorkerPool.General.Run(() => ShellNative.Parse(Path.Combine(_root, "renamed")));
        Assert.Equal(FileOpResult.Succeeded, await FileOperations.DeleteAsync([renamed], IntPtr.Zero, permanent: true, confirm: false));
        Assert.False(Directory.Exists(Path.Combine(_root, "renamed")));
    }
}
