using System.Runtime.InteropServices;
using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ExplorerPlus.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler
{
    [PreserveSig] int SetWindow(IntPtr hwnd, ref RECT prc);
    [PreserveSig] int SetRect(ref RECT prc);
    [PreserveSig] int DoPreview();
    [PreserveSig] int Unload();
    [PreserveSig] int SetFocus();
    [PreserveSig] int QueryFocus(out IntPtr phwnd);
    [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
}

[ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerVisuals
{
    [PreserveSig] int SetBackgroundColor(uint color);
    [PreserveSig] int SetFont(IntPtr plf);
    [PreserveSig] int SetTextColor(uint color);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithItem
{
    [PreserveSig] int Initialize(IShellItem psi, uint grfMode);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig] int Initialize(ComTypes.IStream pstream, uint grfMode);
}

[ComImport, Guid("6f79d558-3e96-4549-a1d1-7d75d2288814"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyDescription
{
    // Only the first methods are used; the rest of the vtable follows them and is never called.
    [PreserveSig] int GetPropertyKey(out PROPERTYKEY pkey);
    [PreserveSig] int GetCanonicalName(out IntPtr ppszName);
    [PreserveSig] int GetPropertyType(out ushort pvartype);
    [PreserveSig] int GetDisplayName(out IntPtr ppszName);
}

public static class PreviewNative
{
    public const string IID_IPreviewHandler = "8895b1c6-b41f-4c1c-a562-0d564250836f";
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint STGM_READ = 0;
    public const uint STGM_SHARE_DENY_NONE = 0x40;
    public const int ASSOCSTR_SHELLEXTENSION = 16;
    public const int ASSOCF_NOTRUNCATE = 0x20;
    public const int ASSOCF_INIT_DEFAULTTOSTAR = 0x4;

    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPCHILDREN = 0x02000000;

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryStringW(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IntPtr pstmTemplate,
        out ComTypes.IStream ppstm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("propsys.dll")]
    public static extern int PSGetPropertyDescription(ref PROPERTYKEY propkey, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyDescription ppv);
}
