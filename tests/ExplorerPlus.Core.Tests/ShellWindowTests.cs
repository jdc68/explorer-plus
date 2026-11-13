using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Tests;

public class ShellWindowTests
{
    [Fact]
    public void Show_in_folder_finds_the_window_and_selects_through_it()
    {
        var dir = Directory.CreateTempSubdirectory("ep-shellwindow-").FullName;
        var file = Path.Combine(dir, "pick me.txt");
        File.WriteAllText(file, "x");
        try
        {
            RunSta(() =>
            {
                using var window = new HwndSource(new HwndSourceParameters("ep shell window test") { WindowStyle = 0 });
                var location = ShellLocation.FromParsingNameAsync(dir).Result!;
                string? selected = null;
                uint flags = 0;
                var registration = new ShellWindowRegistration(window.Handle, (pidl, f) =>
                {
                    using var native = pidl.ToNative();
                    selected = ShellNative.GetName(native.Ptr, SIGDN.FILESYSPATH);
                    flags = f;
                }, (_, _) => { }, () => { });
                try
                {
                    registration.Navigated(location);
                    ShellWindowRegistration.Flush(TimeSpan.FromSeconds(10));

                    // Callers hand over either kind of PIDL for the same folder
                    var byPath = Shell32.ILCreateFromPathW(dir);
                    Assert.Equal(0, Shell32.SHParseDisplayName(dir, IntPtr.Zero, out var parsed, 0, out _));
                    foreach (var pidl in new[] { byPath, parsed })
                    {
                        var found = Find(Pidl.TakeNative(pidl), out var hwnd);
                        Assert.Equal(window.Handle, hwnd);
                        SelectLikeTheShell(found!, file);
                        Assert.Equal(file, selected, ignoreCase: true);
                        Assert.Equal(0x1Du, flags);
                        selected = null;
                    }

                    registration.Dispose();
                    ShellWindowRegistration.Flush(TimeSpan.FromSeconds(10));
                    Assert.Null(Find(location.Pidl, out _));
                }
                finally { registration.Dispose(); }
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    private static object? Find(Pidl folder, out IntPtr hwnd)
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowClass.CLSID_ShellWindows, true)!)!;
        object loc = folder.ToArray();
        object root = null!;
        var found = windows.FindWindowSW(ref loc, ref root, ShellWindowClass.SWC_BROWSER, out var h, ShellWindowClass.SWFO_NEEDDISPATCH);
        hwnd = h;
        return found;
    }

    // What SHOpenFolderAndSelectItems does with the window it found
    private static void SelectLikeTheShell(object window, string file)
    {
        var browser = (IWebBrowser2)window;
        Assert.Equal(0, browser.get_Document(out var document));
        try
        {
            var provider = (IComServiceProvider)Marshal.GetObjectForIUnknown(document);
            var service = ShellWindowClass.SID_SFolderView;
            var iid = ShellWindowClass.IID_IShellView;
            Assert.Equal(0, provider.QueryService(ref service, ref iid, out var viewPtr));
            var view = (IShellView)Marshal.GetObjectForIUnknown(viewPtr);
            Marshal.Release(viewPtr);
            var item = Pidl.TakeNative(Shell32.ILCreateFromPathW(file));
            using var native = item.ToNative();
            Assert.Equal(0, view.SelectItem(Shell32.ILFindLastID(native.Ptr), 0x1D));
        }
        finally { Marshal.Release(document); }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
