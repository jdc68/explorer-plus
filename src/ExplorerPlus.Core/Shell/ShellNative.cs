using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public static class ShellNative
{
    public static string? GetName(IShellItem item, SIGDN sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? GetName(IShellItem2 item, SIGDN sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? GetName(IntPtr pidl, SIGDN sigdn)
    {
        if (Shell32.SHGetNameFromIDList(pidl, sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? GetString(IShellItem2 item, PROPERTYKEY key)
    {
        if (item.GetString(ref key, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static IShellItem CreateItem(Pidl pidl)
    {
        using var native = pidl.ToNative();
        var iid = IIDs.IShellItem;
        Marshal.ThrowExceptionForHR(Shell32.SHCreateItemFromIDList(native.Ptr, ref iid, out var item));
        return item;
    }

    public static IShellItem2? TryCreateItem2(IntPtr pidl)
    {
        var iid = IIDs.IShellItem2;
        return Shell32.SHCreateItem2FromIDList(pidl, ref iid, out var item) == 0 ? item : null;
    }

    public static Pidl? TryParse(string parsingName)
    {
        var hr = Shell32.SHParseDisplayName(parsingName, IntPtr.Zero, out var p, 0, out _);
        return hr == 0 && p != IntPtr.Zero ? Pidl.TakeNative(p) : null;
    }

    public static Pidl Parse(string parsingName)
    {
        var hr = Shell32.SHParseDisplayName(parsingName, IntPtr.Zero, out var p, 0, out _);
        Marshal.ThrowExceptionForHR(hr);
        return Pidl.TakeNative(p);
    }

    public static IShellFolder BindToFolder(Pidl pidl)
    {
        if (pidl.IsDesktop)
        {
            Marshal.ThrowExceptionForHR(Shell32.SHGetDesktopFolder(out var desktop));
            return desktop;
        }
        using var native = pidl.ToNative();
        var iid = IIDs.IShellFolder;
        Marshal.ThrowExceptionForHR(Shell32.SHBindToObject(null, native.Ptr, IntPtr.Zero, ref iid, out var folder));
        return folder;
    }

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
