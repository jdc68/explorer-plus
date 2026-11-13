using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.Core.Tests;

public class RecycleBinTests
{
    [Fact]
    public async Task A_recycled_file_is_restored_through_its_Recycle_Bin_item()
    {
        var root = Directory.CreateTempSubdirectory("epp-bin-").FullName;
        try
        {
            var file = Path.Combine(root, "restore me.txt");
            File.WriteAllText(file, "x");
            var log = new FileOpLog();
            var pidl = await StaWorkerPool.General.Run(() => ShellNative.Parse(file));
            Assert.Equal(FileOpResult.Succeeded, await FileOperations.DeleteAsync([pidl], IntPtr.Zero, permanent: false, confirm: false, log: log));
            var recycled = Assert.Single(log.TopLevel(FileOpKind.Delete)).RecycledItem;
            Assert.NotNull(recycled);
            Assert.False(File.Exists(file));

            var restored = await StaWorkerPool.RunDedicated(() =>
            {
                using var menu = ShellContextMenu.ForItems(RecycleBin.BinItemsFor([recycled]), IntPtr.Zero, null);
                return menu?.InvokeVerb("undelete", IntPtr.Zero) == true;
            }, "test");

            Assert.True(restored);
            Assert.True(File.Exists(file));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
