using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public static class IconCache
{
    private static readonly Dictionary<(int Index, int List), ImageSource?> Cache = [];
    private static readonly Dictionary<int, IntPtr> Lists = [];

    /// <summary>Small (16 px) icon from the system image list.</summary>
    public static ImageSource? Get(int index) => Get(index, 16);

    /// <summary>Icon at the nearest system image list size: 16, 32, 48 or 256 px.</summary>
    public static ImageSource? Get(int index, int size)
    {
        if (index < 0) return null;
        var list = size switch { <= 16 => SHIL.SMALL, <= 32 => SHIL.LARGE, <= 48 => SHIL.EXTRALARGE, _ => SHIL.JUMBO };
        if (Cache.TryGetValue((index, list), out var cached)) return cached;

        if (!Lists.TryGetValue(list, out var himl))
        {
            var iid = IIDs.IImageList;
            Shell32.SHGetImageList(list, ref iid, out himl);
            Lists[list] = himl;
        }

        ImageSource? image = null;
        var hicon = himl == IntPtr.Zero ? IntPtr.Zero : Comctl32.ImageList_GetIcon(himl, index, Comctl32.ILD_TRANSPARENT);
        if (hicon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                image = bmp;
            }
            finally { User32.DestroyIcon(hicon); }
        }
        Cache[(index, list)] = image;
        return image;
    }
}
