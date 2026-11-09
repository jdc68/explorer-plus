using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Tests;

public class ContextMenuTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-menu-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    // These calls hand the shell an array of child PIDLs; marshaled wrongly they crash the process instead of failing.
    [Fact]
    public async Task Item_menu_builds_for_a_folder_one_file_and_several_files()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var built = await StaWorkerPool.General.Run(() =>
            new[] { new[] { _root }, [sub], [a], [a, b, sub] }.Select(paths =>
            {
                using var menu = ShellContextMenu.ForItems(paths.Select(ShellNative.Parse).ToList(), IntPtr.Zero, _root);
                return menu is not null;
            }).ToList());

        Assert.All(built, Assert.True);
    }

    [Fact]
    public async Task Drag_data_object_builds_for_several_files()
    {
        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var ok = await StaWorkerPool.General.Run(() =>
        {
            var data = ShellDragDrop.CreateDataObject([ShellNative.Parse(a), ShellNative.Parse(b)], IntPtr.Zero);
            if (data == IntPtr.Zero) return false;
            System.Runtime.InteropServices.Marshal.Release(data);
            return true;
        });

        Assert.True(ok);
    }

    [Fact]
    public async Task Folder_attributes_read_for_several_children()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");

        var attrs = await StaWorkerPool.General.Run(() =>
        {
            var folder = ShellNative.BindToFolder(ShellNative.Parse(_root));
            var sub = ShellNative.Parse(Path.Combine(_root, "sub")).ToNative();
            var file = ShellNative.Parse(Path.Combine(_root, "a.txt")).ToNative();
            try
            {
                var mask = SFGAO.FILESYSTEM;
                var hr = folder.GetAttributesOf(2, [Shell32.ILFindLastID(sub.Ptr), Shell32.ILFindLastID(file.Ptr)], ref mask);
                return (hr, mask);
            }
            finally
            {
                sub.Dispose();
                file.Dispose();
                ShellNative.Release(folder);
            }
        });

        Assert.Equal(0, attrs.hr);
        Assert.True(attrs.mask.HasFlag(SFGAO.FILESYSTEM));
    }
}
