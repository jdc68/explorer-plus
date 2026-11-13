using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.Core.Tests;

public class FileOperationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-ops-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private static Task<Pidl> ParseAsync(string path) => StaWorkerPool.General.Run(() => ShellNative.Parse(path));

    [Fact]
    public async Task Copy_log_reports_only_the_top_level_item_with_its_final_path()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src", "Photos")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(source, "inner"));
        File.WriteAllText(Path.Combine(source, "inner", "b.txt"), "b");
        var dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

        var log = new FileOpLog();
        var result = await FileOperations.CopyAsync([await ParseAsync(source)], await ParseAsync(dest), IntPtr.Zero, log: log);

        Assert.Equal(FileOpResult.Succeeded, result);
        Assert.True(File.Exists(Path.Combine(dest, "Photos", "inner", "b.txt")));
        var top = Assert.Single(log.TopLevel(FileOpKind.Copy));
        Assert.Equal(source, top.Source, ignoreCase: true);
        Assert.Equal(Path.Combine(dest, "Photos"), top.Result, ignoreCase: true);
    }

    [Fact]
    public async Task Sync_copies_each_item_into_its_own_folder_in_one_operation()
    {
        var left = Directory.CreateDirectory(Path.Combine(_root, "L", "sub")).Parent!.FullName;
        var right = Directory.CreateDirectory(Path.Combine(_root, "R", "sub")).Parent!.FullName;
        File.WriteAllText(Path.Combine(left, "top.txt"), "new top");
        File.WriteAllText(Path.Combine(left, "sub", "deep.txt"), "deep");
        File.WriteAllText(Path.Combine(right, "top.txt"), "old top");

        var log = new FileOpLog();
        var result = await FileOperations.SyncAsync([],
            [(await ParseAsync(Path.Combine(left, "top.txt")), await ParseAsync(right)),
             (await ParseAsync(Path.Combine(left, "sub", "deep.txt")), await ParseAsync(Path.Combine(right, "sub")))],
            IntPtr.Zero, log);

        Assert.Equal(FileOpResult.Succeeded, result);
        Assert.Equal("new top", File.ReadAllText(Path.Combine(right, "top.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(right, "sub", "deep.txt")));
        Assert.Equal(2, log.TopLevel(FileOpKind.Copy).Count(e => e.Result is not null));
    }

    [Fact]
    public async Task Copy_into_same_folder_logs_the_renamed_copy()
    {
        var file = Path.Combine(_root, "note.txt");
        File.WriteAllText(file, "x");

        var log = new FileOpLog();
        await FileOperations.CopyAsync([await ParseAsync(file)], await ParseAsync(_root), IntPtr.Zero, renameOnCollision: true, log: log);

        var entry = Assert.Single(log.TopLevel(FileOpKind.Copy));
        Assert.NotNull(entry.Result);
        Assert.NotEqual(file, entry.Result, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(entry.Result));
    }

    [Fact]
    public async Task Rename_and_move_logs_give_paths_to_revert_with()
    {
        var file = Path.Combine(_root, "old.txt");
        File.WriteAllText(file, "x");
        var log = new FileOpLog();
        Assert.Equal(FileOpResult.Succeeded, await FileOperations.RenameAsync(await ParseAsync(file), "new.txt", IntPtr.Zero, log));
        var rename = Assert.Single(log.TopLevel(FileOpKind.Rename));
        Assert.Equal(file, rename.Source, ignoreCase: true);
        Assert.Equal(Path.Combine(_root, "new.txt"), rename.Result, ignoreCase: true);

        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var moveLog = new FileOpLog();
        await FileOperations.MoveAsync([await ParseAsync(rename.Result!)], await ParseAsync(target), IntPtr.Zero, moveLog);
        var move = Assert.Single(moveLog.TopLevel(FileOpKind.Move));
        Assert.Equal(Path.Combine(target, "new.txt"), move.Result, ignoreCase: true);

        // Undo of a move: move it back to the folder it came from.
        var back = new FileOpLog();
        await FileOperations.MoveAsync([await ParseAsync(move.Result!)], await ParseAsync(Path.GetDirectoryName(move.Source!)!), IntPtr.Zero, back);
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public async Task New_folder_log_reports_the_created_path()
    {
        var log = new FileOpLog();
        await FileOperations.NewFolderAsync(await ParseAsync(_root), "Fresh", IntPtr.Zero, log);
        var entry = Assert.Single(log.TopLevel(FileOpKind.New));
        Assert.Equal(Path.Combine(_root, "Fresh"), entry.Result, ignoreCase: true);
    }

    [Fact]
    public async Task Preview_details_lists_formatted_properties()
    {
        var file = Path.Combine(_root, "doc.txt");
        File.WriteAllText(file, new string('x', 2048));
        var details = await StaWorkerPool.Properties.Run(() => PropertyReader.PreviewDetails(file, null));
        Assert.NotEmpty(details);
        Assert.All(details, d => Assert.False(string.IsNullOrEmpty(d.Label)));
        Assert.Contains(details, d => d.Name.Equals("System.Size", StringComparison.OrdinalIgnoreCase));
    }
}
