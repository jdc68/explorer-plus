using System.IO;
using System.Runtime.InteropServices;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

[ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed partial class ShellWindowSite : IComServiceProvider, IShellBrowser, IShellView, IFolderView, IWebBrowser2, ICustomQueryInterface
{
    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);

    private readonly IntPtr _hwnd;
    private readonly Action<Pidl, uint> _select;
    private readonly Action<Pidl?, uint> _browse;
    private readonly Action _close;
    private volatile ShellLocation? _location;

    public ShellWindowSite(IntPtr hwnd, Action<Pidl, uint> select, Action<Pidl?, uint> browse, Action close)
    {
        _hwnd = hwnd;
        _select = select;
        _browse = browse;
        _close = close;
    }

    internal ShellLocation? Location
    {
        get => _location;
        set => _location = value;
    }

    // For scripts going through Shell.Application
    public long HWND => _hwnd.ToInt64();
    public string Name => "Explorer+";
    public string FullName => Environment.ProcessPath ?? "";
    public string LocationName => _location?.DisplayName ?? "";
    public string LocationURL => _location?.FileSystemPath is { } p && Path.IsPathRooted(p) ? new Uri(p).AbsoluteUri : "";
    public bool Visible => true;
    public bool Busy => false;
    public int ReadyState => 4;
    public void Quit() => ((IWebBrowser2)this).Quit();
    public void Navigate(string url) => ((IWebBrowser2)this).Navigate(url, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        // the shell asks for these, and IWebBrowser2 starts with both
        if (iid != ShellWindowClass.IID_IWebBrowserApp && iid != ShellWindowClass.IID_IWebBrowser) return CustomQueryInterfaceResult.NotHandled;
        ppv = Marshal.GetComInterfaceForObject(this, typeof(IWebBrowser2), CustomQueryInterfaceMode.Ignore);
        return CustomQueryInterfaceResult.Handled;
    }

    private int QuerySelf(Guid iid, out IntPtr ppv)
    {
        var unk = Marshal.GetIUnknownForObject(this);
        try { return Marshal.QueryInterface(unk, iid, out ppv); }
        finally { Marshal.Release(unk); }
    }

    int IComServiceProvider.QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (guidService != ShellWindowClass.SID_STopLevelBrowser && guidService != ShellWindowClass.SID_SShellBrowser &&
            guidService != ShellWindowClass.SID_SFolderView)
            return E_NOINTERFACE;
        return QuerySelf(riid, out ppvObject);
    }

    // ---------- IShellBrowser ----------

    int IShellBrowser.GetWindow(out IntPtr phwnd)
    {
        phwnd = _hwnd;
        return S_OK;
    }

    int IShellBrowser.ContextSensitiveHelp(int fEnterMode) => E_NOTIMPL;
    int IShellBrowser.InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths) => E_NOTIMPL;
    int IShellBrowser.SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject) => E_NOTIMPL;
    int IShellBrowser.RemoveMenusSB(IntPtr hmenuShared) => E_NOTIMPL;
    int IShellBrowser.SetStatusTextSB(IntPtr pszStatusText) => E_NOTIMPL;
    int IShellBrowser.EnableModelessSB(int fEnable) => E_NOTIMPL;
    int IShellBrowser.TranslateAcceleratorSB(IntPtr pmsg, ushort wID) => E_NOTIMPL;

    int IShellBrowser.BrowseObject(IntPtr pidl, uint wFlags)
    {
        if ((wFlags & (ShellWindowClass.SBSP_PARENT | ShellWindowClass.SBSP_NAVIGATEBACK | ShellWindowClass.SBSP_NAVIGATEFORWARD)) != 0)
        {
            _browse(null, wFlags);
            return S_OK;
        }
        if (pidl == IntPtr.Zero) return E_INVALIDARG;
        if ((wFlags & ShellWindowClass.SBSP_RELATIVE) == 0)
        {
            _browse(Pidl.FromNative(pidl), wFlags);
            return S_OK;
        }
        if (_location is not { } current) return E_FAIL;
        _browse(current.Pidl.Combine(pidl), wFlags);
        return S_OK;
    }

    int IShellBrowser.GetViewStateStream(uint grfMode, out IntPtr ppStrm)
    {
        ppStrm = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int IShellBrowser.GetControlWindow(uint id, out IntPtr phwnd)
    {
        phwnd = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int IShellBrowser.SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret)
    {
        pret = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int IShellBrowser.QueryActiveShellView(out IntPtr ppshv)
    {
        return QuerySelf(ShellWindowClass.IID_IShellView, out ppshv);
    }
    int IShellBrowser.OnViewWindowActive(IntPtr pshv) => E_NOTIMPL;
    int IShellBrowser.SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags) => E_NOTIMPL;

    // ---------- IShellView ----------

    int IShellView.GetWindow(out IntPtr phwnd)
    {
        phwnd = _hwnd;
        return S_OK;
    }

    int IShellView.ContextSensitiveHelp(int fEnterMode) => E_NOTIMPL;
    int IShellView.TranslateAccelerator(IntPtr pmsg) => E_NOTIMPL;
    int IShellView.EnableModeless(int fEnable) => E_NOTIMPL;
    int IShellView.UIActivate(uint uState) => S_OK;
    int IShellView.Refresh() => E_NOTIMPL;

    int IShellView.CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd)
    {
        phWnd = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int IShellView.DestroyViewWindow() => E_NOTIMPL;
    int IShellView.GetCurrentInfo(IntPtr pfs) => E_NOTIMPL;
    int IShellView.AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam) => E_NOTIMPL;
    int IShellView.SaveViewState() => E_NOTIMPL;
    int IShellView.SelectItem(IntPtr pidlItem, uint uFlags) => Select(pidlItem, uFlags);

    int IShellView.GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return E_NOTIMPL;
    }

    private int Select(IntPtr child, uint flags)
    {
        if (child == IntPtr.Zero) return E_INVALIDARG;
        if (_location is not { } current) return E_FAIL;
        _select(current.Pidl.Combine(child), flags);
        return S_OK;
    }

    // ---------- IFolderView ----------

    int IFolderView.GetCurrentViewMode(out uint pViewMode)
    {
        pViewMode = 0;
        return E_NOTIMPL;
    }

    int IFolderView.SetCurrentViewMode(uint viewMode) => E_NOTIMPL;

    int IFolderView.GetFolder(ref Guid riid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (_location is not { } current) return E_FAIL;
        using var native = current.Pidl.ToNative();
        return Shell32.SHBindToObjectPtr(IntPtr.Zero, native.Ptr, IntPtr.Zero, ref riid, out ppv);
    }

    int IFolderView.Item(int iItemIndex, out IntPtr ppidl)
    {
        ppidl = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int IFolderView.ItemCount(uint uFlags, out int pcItems)
    {
        pcItems = 0;
        return E_NOTIMPL;
    }

    int IFolderView.Items(uint uFlags, ref Guid riid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        return E_NOTIMPL;
    }

    int IFolderView.GetSelectionMarkedItem(out int piItem)
    {
        piItem = -1;
        return E_NOTIMPL;
    }

    int IFolderView.GetFocusedItem(out int piItem)
    {
        piItem = -1;
        return E_NOTIMPL;
    }

    int IFolderView.GetItemPosition(IntPtr pidl, out POINT ppt)
    {
        ppt = default;
        return E_NOTIMPL;
    }

    int IFolderView.GetSpacing(IntPtr ppt) => E_NOTIMPL;

    int IFolderView.GetDefaultSpacing(out POINT ppt)
    {
        ppt = default;
        return E_NOTIMPL;
    }

    int IFolderView.GetAutoArrange() => E_NOTIMPL;
    int IFolderView.SelectItem(int iItem, uint dwFlags) => E_NOTIMPL;

    int IFolderView.SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags)
    {
        if (apidl == IntPtr.Zero) return E_INVALIDARG;
        for (var i = 0; i < cidl; i++)
        {
            var hr = Select(Marshal.ReadIntPtr(apidl, i * IntPtr.Size), i == 0 ? dwFlags : dwFlags & ~ShellWindowClass.SVSI_DESELECTOTHERS);
            if (hr != S_OK) return hr;
        }
        return S_OK;
    }
}
