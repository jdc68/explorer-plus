using System.Runtime.InteropServices;

namespace ExplorerPlus.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct SIZE(int cx, int cy)
{
    public int cx = cx;
    public int cy = cy;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public int biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[Flags]
public enum SIIGBF
{
    RESIZETOFIT = 0x0,
    BIGGERSIZEOK = 0x1,
    MEMORYONLY = 0x2,
    ICONONLY = 0x4,
    THUMBNAILONLY = 0x8,
    INCACHEONLY = 0x10,
    CROPTOSQUARE = 0x20,
    WIDETHUMBNAILS = 0x40,
    ICONBACKGROUND = 0x80,
    SCALEUP = 0x100,
}

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

public static class Gdi32
{
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
