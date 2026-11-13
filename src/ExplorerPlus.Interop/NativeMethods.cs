using System.Runtime.InteropServices;

namespace ExplorerPlus.Interop;

public static class Shell32
{
    private const string Dll = "shell32.dll";

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport(Dll)]
    public static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport(Dll, EntryPoint = "SHCreateItemFromIDList")]
    public static extern int SHCreateItem2FromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);

    [DllImport(Dll)]
    public static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, out IntPtr ppidl);

    [DllImport(Dll)]
    public static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder ppshf);

    [DllImport(Dll)]
    public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport(Dll)]
    public static extern int SHBindToObject([MarshalAs(UnmanagedType.Interface)] IShellFolder? psf, IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, SHGFI uFlags);

    [DllImport(Dll, CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, SHGFI uFlags);

    [DllImport(Dll)]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport(Dll)]
    public static extern uint SHChangeNotifyRegister(IntPtr hwnd, int fSources, int fEvents, uint wMsg, int cEntries, ref SHChangeNotifyEntry pshcne);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport(Dll)]
    public static extern IntPtr SHChangeNotification_Lock(IntPtr hChange, uint dwProcId, out IntPtr pppidl, out int plEvent);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHChangeNotification_Unlock(IntPtr hLock);

    [DllImport(Dll)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, SIGDN sigdnName, out IntPtr ppszName);

    [DllImport(Dll)]
    public static extern int SHCreateShellItemArrayFromIDLists(uint cidl, [In] IntPtr[] rgpidl, out IntPtr ppsiItemArray);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport(Dll)]
    public static extern void ILFree(IntPtr pidl);

    [DllImport(Dll)]
    public static extern IntPtr ILClone(IntPtr pidl);

    [DllImport(Dll)]
    public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport(Dll)]
    public static extern uint ILGetSize(IntPtr pidl);

    [DllImport(Dll)]
    public static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ILRemoveLastID(IntPtr pidl);

    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ILIsEqual(IntPtr pidl1, IntPtr pidl2);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    [DllImport(Dll)]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Groups the taskbar button and jump list under one id; the installer's Start menu shortcut carries the same id.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Initializes the process's system image list; must run before icons are requested from several threads.</summary>
    [DllImport(Dll, EntryPoint = "#660")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FileIconInit([MarshalAs(UnmanagedType.Bool)] bool fRestoreCache);
}

public static class Kernel32
{
    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FIND_FIRST_EX_LARGE_FETCH = 2;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint uMode);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    public const uint SEM_FAILCRITICALERRORS = 0x1;
    public const uint SEM_NOOPENFILEERRORBOX = 0x8000;
}

public static class User32
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InsertMenuW(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    public const int ASFW_ANY = -1;
    public const int SW_RESTORE = 9;
}

public static class Comctl32
{
    [DllImport("comctl32.dll")]
    public static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    public const uint ILD_TRANSPARENT = 0x1;
}

public static class UxTheme
{
    // Undocumented but stable since 1903; used by Notepad++, Windows Terminal etc. to get dark Win32 popup menus.
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    public static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    public static extern void FlushMenuThemes();

    public const int AllowDark = 1;
}

public static class Dwmapi
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
}
