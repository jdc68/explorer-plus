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
            var image = Fetch(factory, size, flags);
            // Windows' thumbnail cache sometimes holds broken folder thumbnails: fully opaque on a black square, or
            // the folder drawn a few pixels wide in the middle of the canvas. A folder thumbnail always has a
            // transparent outline, so anything else falls back to the folder's icon, which is what Explorer shows.
            if (!iconOnly && image is not null && IsPlainFolder(factory) && !IsSaneFolderImage(image))
                image = Fetch(factory, size, SIIGBF.RESIZETOFIT | SIIGBF.ICONONLY) ?? image;
            return image;
        }
        catch (COMException) { return null; }
        finally { ShellNative.Release(factory); }
    }

    private static RawImage? Fetch(IShellItemImageFactory factory, int size, SIIGBF flags)
    {
        var hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE(size, size), flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return ToRaw(hbm);
        }
        finally
        {
            if (hbm != IntPtr.Zero) Gdi32.DeleteObject(hbm);
        }
    }

    private static bool IsPlainFolder(IShellItemImageFactory factory)
    {
        // A zip file is a folder and a stream; its thumbnail is a file's.
        if (factory is not IShellItem item) return false;
        return item.GetAttributes(SFGAO.FOLDER | SFGAO.STREAM, out var attrs) >= 0 // S_FALSE when only some bits are set
            && (attrs & SFGAO.FOLDER) != 0 && (attrs & SFGAO.STREAM) == 0;
    }

    internal static bool IsSaneFolderImage(RawImage image)
    {
        if (!image.HasAlpha) return false;
        int minX = int.MaxValue, maxX = -1, transparent = 0;
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            if (p[i + 3] == 0) { transparent++; continue; }
            var x = i / 4 % image.Width;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
        }
        return transparent > 0 && maxX >= 0 && maxX - minX + 1 >= image.Width / 2;
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
        if (hasAlpha) Premultiply(pixels);
        return new RawImage(w, h, pixels, hasAlpha);
    }

    /// <summary>
    /// Callers draw the pixels as premultiplied BGRA, but the shell hands back some images (several folder thumbnails
    /// among them) with straight alpha and white in the transparent pixels, which then draw as a white square.
    /// Clears what is fully transparent and premultiplies when any colour is brighter than its alpha.
    /// </summary>
    internal static void Premultiply(byte[] p)
    {
        var straight = false;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a == 0) { p[i] = p[i + 1] = p[i + 2] = 0; continue; }
            if (p[i] > a || p[i + 1] > a || p[i + 2] > a) straight = true;
        }
        if (!straight) return;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a is 0 or 255) continue;
            p[i] = (byte)((p[i] * a + 127) / 255);
            p[i + 1] = (byte)((p[i + 1] * a + 127) / 255);
            p[i + 2] = (byte)((p[i + 2] * a + 127) / 255);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
}
