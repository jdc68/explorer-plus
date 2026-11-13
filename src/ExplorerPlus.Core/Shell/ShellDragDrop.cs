using System.Runtime.InteropServices;
using ExplorerPlus.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ExplorerPlus.Core.Shell;

public static class ShellDragDrop
{
    /// <summary>
    /// Drags items (all from one folder) with the shell's own data object and drop source, so any app that
    /// accepts files from Explorer accepts them from us, with the same drag image and Ctrl/Shift/Alt behaviour.
    /// Blocks in a modal loop until the drop; returns the effect the target performed. UI thread only.
    /// </summary>
    public static uint DoDrag(IReadOnlyList<Pidl> items, IntPtr hwnd)
    {
        var data = CreateDataObject(items, hwnd);
        if (data == IntPtr.Zero) return DropEffect.None;
        try
        {
            OleDragDrop.SHDoDragDrop(hwnd, data, IntPtr.Zero, DropEffect.Copy | DropEffect.Move | DropEffect.Link, out var effect);
            return effect;
        }
        finally { Marshal.Release(data); }
    }

    // so our "Move to …" label doesn't follow the pointer into an app that sets none
    public static void ClearDropDescription(IntPtr dataObject)
    {
        if (dataObject == IntPtr.Zero) return;
        object? obj = null;
        var hglobal = IntPtr.Zero;
        try
        {
            obj = Marshal.GetObjectForIUnknown(dataObject);
            if (obj is not ComTypes.IDataObject data) return;
            var format = new ComTypes.FORMATETC
            {
                cfFormat = (short)ShellClipboard.RegisterClipboardFormatW("DropDescription"),
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
            };
            // DROPDESCRIPTION: an image type (DROPIMAGE_INVALID) and two MAX_PATH strings
            const int size = 4 + 2 * 260 * 2;
            hglobal = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i += 4) Marshal.WriteInt32(hglobal, i, 0);
            Marshal.WriteInt32(hglobal, -1);
            var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = hglobal };
            data.SetData(ref format, ref medium, true);
            hglobal = IntPtr.Zero;
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException) { }
        finally
        {
            if (hglobal != IntPtr.Zero) Marshal.FreeHGlobal(hglobal);
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, [In] IntPtr[] apidl, IntPtr pdtInner, ref Guid riid, out IntPtr ppv);

    /// <summary>The shell's data object for the items (what Explorer puts on the clipboard or drags). Caller releases it.</summary>
    public static IntPtr CreateDataObject(IReadOnlyList<Pidl> items, IntPtr hwnd)
    {
        if (items.Count == 0) return IntPtr.Zero;
        var natives = items.Select(p => p.ToNative()).ToArray();
        IShellFolder? parent = null;
        try
        {
            if (!ShellContextMenu.ShareParent(items))
            {
                // Items from several folders (Home): a data object rooted at the desktop holds full ID lists.
                using var desktop = Pidl.Desktop.ToNative();
                var iidObject = IIDs.IDataObject;
                return SHCreateDataObject(desktop.Ptr, (uint)natives.Length, natives.Select(n => n.Ptr).ToArray(), IntPtr.Zero, ref iidObject, out var multi) == 0
                    ? multi : IntPtr.Zero;
            }
            var iid = IIDs.IShellFolder;
            if (Shell32.SHBindToParent(natives[0].Ptr, ref iid, out parent, out _) != 0) return IntPtr.Zero;
            var children = natives.Select(n => Shell32.ILFindLastID(n.Ptr)).ToArray();
            var iidData = IIDs.IDataObject;
            return parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidData, IntPtr.Zero, out var ptr) == 0 ? ptr : IntPtr.Zero;
        }
        finally
        {
            ShellNative.Release(parent);
            foreach (var n in natives) n.Dispose();
        }
    }

    /// <summary>
    /// The shell's drop handler for a folder's empty area (<paramref name="background"/>) or for an item
    /// (subfolder, zip, program, shortcut). Dropping through it gives Explorer's exact behaviour: copy/move/link
    /// rules, the right-drag menu, conflict dialogs, and drops into zips, phones and the Recycle Bin.
    /// </summary>
    public static IDropTarget? GetDropTarget(Pidl target, bool background, IntPtr hwnd)
    {
        var ptr = IntPtr.Zero;
        var iid = OleDragDrop.IID_IDropTarget;
        try
        {
            if (background)
            {
                var folder = ShellNative.BindToFolder(target);
                try { if (folder.CreateViewObject(hwnd, ref iid, out ptr) != 0) return null; }
                finally { ShellNative.Release(folder); }
            }
            else
            {
                using var native = target.ToNative();
                var iidFolder = IIDs.IShellFolder;
                if (Shell32.SHBindToParent(native.Ptr, ref iidFolder, out var parent, out var child) != 0) return null;
                try { if (parent.GetUIObjectOf(hwnd, 1, [child], ref iid, IntPtr.Zero, out ptr) != 0) return null; }
                finally { ShellNative.Release(parent); }
            }
            return ptr == IntPtr.Zero ? null : (IDropTarget)Marshal.GetObjectForIUnknown(ptr);
        }
        catch (COMException) { return null; }
        finally { if (ptr != IntPtr.Zero) Marshal.Release(ptr); }
    }
}
