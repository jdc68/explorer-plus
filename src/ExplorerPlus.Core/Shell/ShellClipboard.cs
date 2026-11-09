using System.Runtime.InteropServices;
using ExplorerPlus.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ExplorerPlus.Core.Shell;

public static class ShellClipboard
{
    private const int DROPEFFECT_COPY = 1;
    private const int DROPEFFECT_MOVE = 2;

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(IntPtr pDataObj);

    [DllImport("ole32.dll")]
    public static extern int OleFlushClipboard();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClipboardFormatW(string lpszFormat);

    public static bool SetItems(IReadOnlyList<Pidl> items, bool cut, IntPtr hwnd)
    {
        if (items.Count == 0) return false;
        var ptr = ShellDragDrop.CreateDataObject(items, hwnd);
        if (ptr == IntPtr.Zero) return false;
        try
        {
            SetDropEffect(ptr, cut ? DROPEFFECT_MOVE : DROPEFFECT_COPY);
            return OleSetClipboard(ptr) == 0;
        }
        finally { Marshal.Release(ptr); }
    }

    private static void SetDropEffect(IntPtr dataObjectPtr, int effect)
    {
        var obj = (ComTypes.IDataObject)Marshal.GetObjectForIUnknown(dataObjectPtr);
        try
        {
            var format = new ComTypes.FORMATETC
            {
                cfFormat = (short)RegisterClipboardFormatW("Preferred DropEffect"),
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
            };
            var hglobal = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(hglobal, effect);
            var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = hglobal };
            obj.SetData(ref format, ref medium, true);
        }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(obj); }
    }
}
