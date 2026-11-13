using System.IO;
using System.Runtime.InteropServices;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public sealed partial class ShellWindowSite
{
    private const short VariantTrue = -1;
    private const int NavOpenInNewWindow = 1;

    private static int Bstr(string? value, out IntPtr bstr)
    {
        bstr = Marshal.StringToBSTR(value ?? "");
        return S_OK;
    }

    // IDispatch slots forward to the class's own
    int IWebBrowser2.GetTypeInfoCount(out uint pctinfo)
    {
        pctinfo = 0;
        return S_OK;
    }

    int IWebBrowser2.GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo)
    {
        ppTInfo = IntPtr.Zero;
        return E_NOTIMPL;
    }

    unsafe int IWebBrowser2.GetIDsOfNames(ref Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgDispId)
    {
        if (QuerySelf(ShellWindowClass.IID_IDispatch, out var d) != S_OK) return E_NOINTERFACE;
        try
        {
            var iid = riid;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, uint, uint, IntPtr, int>)(*(IntPtr**)d)[5];
            return fn(d, &iid, rgszNames, cNames, lcid, rgDispId);
        }
        finally { Marshal.Release(d); }
    }

    unsafe int IWebBrowser2.Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr)
    {
        if (QuerySelf(ShellWindowClass.IID_IDispatch, out var d) != S_OK) return E_NOINTERFACE;
        try
        {
            var iid = riid;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, IntPtr, IntPtr, IntPtr, IntPtr, int>)(*(IntPtr**)d)[6];
            return fn(d, dispIdMember, &iid, lcid, wFlags, pDispParams, pVarResult, pExcepInfo, puArgErr);
        }
        finally { Marshal.Release(d); }
    }

    // ---------- IWebBrowser ----------

    int IWebBrowser2.GoBack()
    {
        _browse(null, ShellWindowClass.SBSP_NAVIGATEBACK);
        return S_OK;
    }

    int IWebBrowser2.GoForward()
    {
        _browse(null, ShellWindowClass.SBSP_NAVIGATEFORWARD);
        return S_OK;
    }

    int IWebBrowser2.GoHome() => E_NOTIMPL;
    int IWebBrowser2.GoSearch() => E_NOTIMPL;

    int IWebBrowser2.Navigate(string url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers)
    {
        return NavigateTo(url, flags);
    }

    int IWebBrowser2.Refresh() => E_NOTIMPL;
    int IWebBrowser2.Refresh2(IntPtr level) => E_NOTIMPL;
    int IWebBrowser2.Stop() => S_OK;
    int IWebBrowser2.get_Application(out IntPtr ppDisp) => QuerySelf(ShellWindowClass.IID_IDispatch, out ppDisp);
    int IWebBrowser2.get_Parent(out IntPtr ppDisp) => QuerySelf(ShellWindowClass.IID_IDispatch, out ppDisp);
    int IWebBrowser2.get_Container(out IntPtr ppDisp)
    {
        ppDisp = IntPtr.Zero;
        return S_OK;
    }

    int IWebBrowser2.get_Document(out IntPtr ppDisp)
    {
        return QuerySelf(ShellWindowClass.IID_IDispatch, out ppDisp);
    }

    int IWebBrowser2.get_TopLevelContainer(out short pBool)
    {
        pBool = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.get_Type(out IntPtr type) => Bstr("Explorer+", out type);

    int IWebBrowser2.get_Left(out int pl) => Bound(out pl, r => r.Left);
    int IWebBrowser2.put_Left(int left) => E_NOTIMPL;
    int IWebBrowser2.get_Top(out int pl) => Bound(out pl, r => r.Top);
    int IWebBrowser2.put_Top(int top) => E_NOTIMPL;
    int IWebBrowser2.get_Width(out int pl) => Bound(out pl, r => r.Right - r.Left);
    int IWebBrowser2.put_Width(int width) => E_NOTIMPL;
    int IWebBrowser2.get_Height(out int pl) => Bound(out pl, r => r.Bottom - r.Top);
    int IWebBrowser2.put_Height(int height) => E_NOTIMPL;

    private int Bound(out int value, Func<RECT, int> pick)
    {
        value = User32.GetWindowRect(_hwnd, out var r) ? pick(r) : 0;
        return S_OK;
    }

    int IWebBrowser2.get_LocationName(out IntPtr locationName) => Bstr(LocationName, out locationName);
    int IWebBrowser2.get_LocationURL(out IntPtr locationUrl) => Bstr(LocationURL, out locationUrl);

    int IWebBrowser2.get_Busy(out short pBool)
    {
        pBool = 0;
        return S_OK;
    }

    // ---------- IWebBrowserApp ----------

    int IWebBrowser2.Quit()
    {
        _close();
        return S_OK;
    }

    int IWebBrowser2.ClientToWindow(ref int pcx, ref int pcy) => E_NOTIMPL;
    int IWebBrowser2.PutProperty(IntPtr property, Variant vtValue) => E_NOTIMPL;
    int IWebBrowser2.GetProperty(IntPtr property, IntPtr pvtValue) => E_NOTIMPL;
    int IWebBrowser2.get_Name(out IntPtr name) => Bstr(Name, out name);

    int IWebBrowser2.get_HWND(out IntPtr pHWND)
    {
        pHWND = _hwnd;
        return S_OK;
    }

    int IWebBrowser2.get_FullName(out IntPtr fullName) => Bstr(FullName, out fullName);
    int IWebBrowser2.get_Path(out IntPtr path) => Bstr(Path.GetDirectoryName(FullName), out path);

    int IWebBrowser2.get_Visible(out short pBool)
    {
        pBool = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_Visible(short value)
    {
        return S_OK;
    }

    int IWebBrowser2.get_StatusBar(out short pBool)
    {
        pBool = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_StatusBar(short value) => S_OK;

    int IWebBrowser2.get_StatusText(out IntPtr statusText) => Bstr("", out statusText);
    int IWebBrowser2.put_StatusText(IntPtr statusText) => S_OK;

    int IWebBrowser2.get_ToolBar(out int value)
    {
        value = 1;
        return S_OK;
    }

    int IWebBrowser2.put_ToolBar(int value) => S_OK;

    int IWebBrowser2.get_MenuBar(out short value)
    {
        value = 0;
        return S_OK;
    }

    int IWebBrowser2.put_MenuBar(short value) => S_OK;

    int IWebBrowser2.get_FullScreen(out short pbFullScreen)
    {
        pbFullScreen = 0;
        return S_OK;
    }

    int IWebBrowser2.put_FullScreen(short bFullScreen) => E_NOTIMPL;

    // ---------- IWebBrowser2 ----------

    int IWebBrowser2.Navigate2(IntPtr url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers)
    {
        if (url == IntPtr.Zero) return E_INVALIDARG;
        object? target;
        try { target = Marshal.GetObjectForNativeVariant(url); }
        catch { return E_INVALIDARG; }
        return target switch
        {
            string s => NavigateTo(s, flags),
            byte[] pidl => Browse(Pidl.FromArray(pidl), flags),
            _ => E_INVALIDARG,
        };
    }

    private int NavigateTo(string url, IntPtr flags)
    {
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var uri)) url = uri.LocalPath;
        if (Shell32.SHParseDisplayName(url, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return E_INVALIDARG;
        return Browse(Pidl.TakeNative(pidl), flags);
    }

    private int Browse(Pidl target, IntPtr flags)
    {
        var newTab = false;
        if (flags != IntPtr.Zero)
        {
            try { newTab = Marshal.GetObjectForNativeVariant(flags) is { } f && (Convert.ToInt32(f) & NavOpenInNewWindow) != 0; }
            catch { }
        }
        _browse(target, newTab ? ShellWindowClass.SBSP_NEWBROWSER : ShellWindowClass.SBSP_SAMEBROWSER);
        return S_OK;
    }

    int IWebBrowser2.QueryStatusWB(int cmdID, out int pcmdf)
    {
        pcmdf = 0;
        return E_NOTIMPL;
    }

    int IWebBrowser2.ExecWB(int cmdID, int cmdexecopt, IntPtr pvaIn, IntPtr pvaOut) => E_NOTIMPL;
    int IWebBrowser2.ShowBrowserBar(IntPtr pvaClsid, IntPtr pvarShow, IntPtr pvarSize) => E_NOTIMPL;

    int IWebBrowser2.get_ReadyState(out int plReadyState)
    {
        plReadyState = ReadyState;
        return S_OK;
    }

    int IWebBrowser2.get_Offline(out short pbOffline)
    {
        pbOffline = 0;
        return S_OK;
    }

    int IWebBrowser2.put_Offline(short bOffline) => S_OK;

    int IWebBrowser2.get_Silent(out short pbSilent)
    {
        pbSilent = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_Silent(short bSilent) => S_OK;

    int IWebBrowser2.get_RegisterAsBrowser(out short pbRegister)
    {
        pbRegister = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_RegisterAsBrowser(short bRegister) => S_OK;

    int IWebBrowser2.get_RegisterAsDropTarget(out short pbRegister)
    {
        pbRegister = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_RegisterAsDropTarget(short bRegister) => S_OK;

    int IWebBrowser2.get_TheaterMode(out short pbRegister)
    {
        pbRegister = 0;
        return S_OK;
    }

    int IWebBrowser2.put_TheaterMode(short bRegister) => S_OK;

    int IWebBrowser2.get_AddressBar(out short value)
    {
        value = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_AddressBar(short value) => S_OK;

    int IWebBrowser2.get_Resizable(out short value)
    {
        value = VariantTrue;
        return S_OK;
    }

    int IWebBrowser2.put_Resizable(short value) => S_OK;
}
