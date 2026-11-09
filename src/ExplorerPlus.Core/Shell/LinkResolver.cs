using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ExplorerPlus.Core.Shell;

public static class LinkResolver
{
    /// <summary>
    /// If a .lnk points to a folder, returns that folder so we navigate in place (as Explorer does)
    /// instead of ShellExecute opening a stock Explorer window.
    /// </summary>
    public static Task<ShellLocation?> GetFolderTargetAsync(string lnkPath) =>
        StaWorkerPool.General.Run<ShellLocation?>(() =>
        {
            var type = Type.GetTypeFromCLSID(ShellLinkClsid.CLSID_ShellLink, throwOnError: true)!;
            var link = Activator.CreateInstance(type)!;
            try
            {
                ((ComTypes.IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
                if (((IShellLinkW)link).GetIDList(out var pidl) != 0 || pidl == IntPtr.Zero) return null;
                var location = ShellLocation.FromPidl(Pidl.TakeNative(pidl));
                return location.IsFolder && !location.Attributes.HasFlag(SFGAO.STREAM) ? location : null;
            }
            catch
            {
                return null;
            }
            finally { ShellNative.Release(link); }
        });
}
