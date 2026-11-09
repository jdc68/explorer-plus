using System.Runtime.InteropServices;

namespace ExplorerPlus.Interop;

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig] int DragEnter(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect);
    [PreserveSig] int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IntPtr pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect);
}

[ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTargetHelper
{
    [PreserveSig] int DragEnter(IntPtr hwndTarget, IntPtr pDataObject, ref POINT ppt, uint dwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int DragOver(ref POINT ppt, uint dwEffect);
    [PreserveSig] int Drop(IntPtr pDataObject, ref POINT ppt, uint dwEffect);
    [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool fShow);
}

public static class DropEffect
{
    public const uint None = 0;
    public const uint Copy = 1;
    public const uint Move = 2;
    public const uint Link = 4;
}

public static class OleDragDrop
{
    public static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    public static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");

    [DllImport("shell32.dll")]
    public static extern int SHDoDragDrop(IntPtr hwnd, IntPtr pdata, IntPtr pdsrc, uint dwEffect, out uint pdwEffect);
}
