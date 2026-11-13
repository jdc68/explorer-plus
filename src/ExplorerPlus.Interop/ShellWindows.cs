using System.Runtime.InteropServices;

namespace ExplorerPlus.Interop;

public static class ShellWindowClass
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid SID_SShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid SID_SFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellView = new("000214E3-0000-0000-C000-000000000046");
    public static readonly Guid IID_IFolderView = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    public static readonly Guid IID_IOleWindow = new("00000114-0000-0000-C000-000000000046");
    public static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    public static readonly Guid IID_IWebBrowser = new("EAB22AC1-30C1-11CF-A7EB-0000C05BAE0B");
    public static readonly Guid IID_IWebBrowserApp = new("0002DF05-0000-0000-C000-000000000046");
    public static readonly Guid IID_IWebBrowser2 = new("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E");

    public const int SWC_BROWSER = 1; // File Explorer's windows
    public const int SWFO_NEEDDISPATCH = 1;

    public const uint SVSI_SELECT = 0x1;
    public const uint SVSI_EDIT = 0x3;
    public const uint SVSI_DESELECTOTHERS = 0x4;

    public const uint SBSP_SAMEBROWSER = 0x1;
    public const uint SBSP_NEWBROWSER = 0x2;
    public const uint SBSP_RELATIVE = 0x1000;
    public const uint SBSP_PARENT = 0x2000;
    public const uint SBSP_NAVIGATEBACK = 0x4000;
    public const uint SBSP_NAVIGATEFORWARD = 0x8000;
}

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IShellWindows
{
    int Count { get; }
    [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
    [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
    int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
    int RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass);
    void Revoke(int cookie);
    void OnNavigate(int cookie, ref object loc);
    void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
    [return: MarshalAs(UnmanagedType.IDispatch)] object? FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int options);
    void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
    void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IComServiceProvider
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
    [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
    [PreserveSig] int EnableModelessSB(int fEnable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
    [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
    [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
    [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
    [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
    [PreserveSig] int OnViewWindowActive(IntPtr pshv);
    [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellView
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
    [PreserveSig] int EnableModeless(int fEnable);
    [PreserveSig] int UIActivate(uint uState);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(IntPtr pfs);
    [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
    [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
}

[ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    [PreserveSig] int GetSpacing(IntPtr ppt);
    [PreserveSig] int GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
}

// IDispatch slots spelled out; IWebBrowser and IWebBrowserApp are prefixes of this vtable
[ComImport, Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWebBrowser2
{
    [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
    [PreserveSig] int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
    [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgDispId);
    [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

    // IWebBrowser
    [PreserveSig] int GoBack();
    [PreserveSig] int GoForward();
    [PreserveSig] int GoHome();
    [PreserveSig] int GoSearch();
    [PreserveSig] int Navigate([MarshalAs(UnmanagedType.BStr)] string url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers);
    [PreserveSig] int Refresh();
    [PreserveSig] int Refresh2(IntPtr level);
    [PreserveSig] int Stop();
    [PreserveSig] int get_Application(out IntPtr ppDisp);
    [PreserveSig] int get_Parent(out IntPtr ppDisp);
    [PreserveSig] int get_Container(out IntPtr ppDisp);
    [PreserveSig] int get_Document(out IntPtr ppDisp);
    [PreserveSig] int get_TopLevelContainer(out short pBool);
    [PreserveSig] int get_Type(out IntPtr type);
    [PreserveSig] int get_Left(out int pl);
    [PreserveSig] int put_Left(int left);
    [PreserveSig] int get_Top(out int pl);
    [PreserveSig] int put_Top(int top);
    [PreserveSig] int get_Width(out int pl);
    [PreserveSig] int put_Width(int width);
    [PreserveSig] int get_Height(out int pl);
    [PreserveSig] int put_Height(int height);
    [PreserveSig] int get_LocationName(out IntPtr locationName);
    [PreserveSig] int get_LocationURL(out IntPtr locationUrl);
    [PreserveSig] int get_Busy(out short pBool);

    // IWebBrowserApp
    [PreserveSig] int Quit();
    [PreserveSig] int ClientToWindow(ref int pcx, ref int pcy);
    [PreserveSig] int PutProperty(IntPtr property, Variant vtValue);
    [PreserveSig] int GetProperty(IntPtr property, IntPtr pvtValue);
    [PreserveSig] int get_Name(out IntPtr name);
    [PreserveSig] int get_HWND(out IntPtr pHWND);
    [PreserveSig] int get_FullName(out IntPtr fullName);
    [PreserveSig] int get_Path(out IntPtr path);
    [PreserveSig] int get_Visible(out short pBool);
    [PreserveSig] int put_Visible(short value);
    [PreserveSig] int get_StatusBar(out short pBool);
    [PreserveSig] int put_StatusBar(short value);
    [PreserveSig] int get_StatusText(out IntPtr statusText);
    [PreserveSig] int put_StatusText(IntPtr statusText);
    [PreserveSig] int get_ToolBar(out int value);
    [PreserveSig] int put_ToolBar(int value);
    [PreserveSig] int get_MenuBar(out short value);
    [PreserveSig] int put_MenuBar(short value);
    [PreserveSig] int get_FullScreen(out short pbFullScreen);
    [PreserveSig] int put_FullScreen(short bFullScreen);

    // IWebBrowser2
    [PreserveSig] int Navigate2(IntPtr url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers);
    [PreserveSig] int QueryStatusWB(int cmdID, out int pcmdf);
    [PreserveSig] int ExecWB(int cmdID, int cmdexecopt, IntPtr pvaIn, IntPtr pvaOut);
    [PreserveSig] int ShowBrowserBar(IntPtr pvaClsid, IntPtr pvarShow, IntPtr pvarSize);
    [PreserveSig] int get_ReadyState(out int plReadyState);
    [PreserveSig] int get_Offline(out short pbOffline);
    [PreserveSig] int put_Offline(short bOffline);
    [PreserveSig] int get_Silent(out short pbSilent);
    [PreserveSig] int put_Silent(short bSilent);
    [PreserveSig] int get_RegisterAsBrowser(out short pbRegister);
    [PreserveSig] int put_RegisterAsBrowser(short bRegister);
    [PreserveSig] int get_RegisterAsDropTarget(out short pbRegister);
    [PreserveSig] int put_RegisterAsDropTarget(short bRegister);
    [PreserveSig] int get_TheaterMode(out short pbRegister);
    [PreserveSig] int put_TheaterMode(short bRegister);
    [PreserveSig] int get_AddressBar(out short value);
    [PreserveSig] int put_AddressBar(short value);
    [PreserveSig] int get_Resizable(out short value);
    [PreserveSig] int put_Resizable(short value);
}

// VARIANT by value
[StructLayout(LayoutKind.Sequential)]
public struct Variant
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr p1;
    public IntPtr p2;
}
