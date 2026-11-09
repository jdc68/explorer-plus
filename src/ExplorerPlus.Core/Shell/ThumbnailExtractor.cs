using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed record RawImage(int Width, int Height, byte[] Pixels, bool HasAlpha);

public static class ThumbnailExtractor
{
    private static readonly Guid IidImageFactory = typeof(IShellItemImageFactory).GUID;

    /// <summary>
    /// The shell's own thumbnail (or, for files without one, the icon) at the requested size, as 32-bit
    /// top-down BGRA pixels. Uses Windows' thumbnail cache, so it's the same image Explorer shows.
    /// Must run on an STA thread.
    /// </summary>
    public static RawImage? Get(string? path, Pidl? pidl, int size, bool iconOnly = false)
    {
        IShellItemImageFactory? factory = null;
        var hbm = IntPtr.Zero;
        try
        {
            var iid = IidImageFactory;
            int hr;
            if (pidl is not null)
            {
                using var native = pidl.ToNative();
                hr = SHCreateItemFromIDList(native.Ptr, ref iid, out factory);
            }
            else if (path is not null) hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            else return null;
            if (hr != 0 || factory is null) return null;

            var flags = SIIGBF.RESIZETOFIT | (iconOnly ? SIIGBF.ICONONLY : 0);
            if (factory.GetImage(new SIZE(size, size), flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return ToRaw(hbm);
        }
        catch (COMException) { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) Gdi32.DeleteObject(hbm);
            ShellNative.Release(factory);
        }
    }

    private static RawImage? ToRaw(IntPtr hbm)
    {
        if (Gdi32.GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight == 0) return null;
        var w = bmp.bmWidth;
        var h = Math.Abs(bmp.bmHeight);
        var pixels = new byte[w * h * 4];
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative: ask for top-down rows whatever the source orientation
            biPlanes = 1,
            biBitCount = 32,
        };
        var hdc = Gdi32.GetDC(IntPtr.Zero);
        try
        {
            if (Gdi32.GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { Gdi32.ReleaseDC(IntPtr.Zero, hdc); }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        return new RawImage(w, h, pixels, hasAlpha);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
}
