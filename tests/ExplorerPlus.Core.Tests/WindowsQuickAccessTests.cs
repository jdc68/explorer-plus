using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.Core.Tests;

public class WindowsQuickAccessTests
{
    [Fact]
    public void Plan_pins_what_is_wanted_and_unpins_the_rest_ignoring_case_and_trailing_slashes()
    {
        var wanted = new[] { @"C:\Docs", @"D:\Missing", @"C:\Pics" };
        var pinned = new[] { @"c:\pics\", @"C:\Old" };

        var (pin, unpin) = WindowsQuickAccess.Plan(wanted, pinned, path => path != @"D:\Missing");

        Assert.Equal([@"C:\Docs"], pin);
        Assert.Equal([@"C:\Old"], unpin);
    }

    [Fact]
    public async Task Pinning_and_unpinning_a_folder_through_the_shell_shows_in_quick_access()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExplorerPlusQuickAccess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await StaWorkerPool.General.Run(() => WindowsQuickAccess.Apply([folder], []));
            Assert.Contains(await StaWorkerPool.General.Run(WindowsQuickAccess.ReadPinned), p => FolderHistory.Same(p, folder));

            await StaWorkerPool.General.Run(() => WindowsQuickAccess.Apply([], [folder]));
            Assert.DoesNotContain(await StaWorkerPool.General.Run(WindowsQuickAccess.ReadPinned), p => FolderHistory.Same(p, folder));
        }
        finally
        {
            await StaWorkerPool.General.Run(() => WindowsQuickAccess.Apply([], [folder]));
            Directory.Delete(folder);
        }
    }
}
