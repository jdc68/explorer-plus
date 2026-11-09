using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class CustomMenuItem
{
    public string Text { get; init; } = "";
    public int Id { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsSeparator { get; init; }
    public bool IsChecked { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<CustomMenuItem>? Children { get; init; }

    public static CustomMenuItem Separator { get; } = new() { IsSeparator = true };
}

public enum MenuResultKind { None, Custom, Verb, Invoked }

public sealed record MenuResult(MenuResultKind Kind, int CustomId = 0, string? Verb = null)
{
    public static MenuResult Nothing { get; } = new(MenuResultKind.None);
}

public sealed class ShellContextMenu : IDisposable
{
    private const uint FirstShellId = 1;
    private const uint LastShellId = 0x7EFF;
    public const int FirstCustomId = 0x7F00;

    private IContextMenu? _menu;
    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;
    private readonly string? _directory;

    /// <summary>The menu currently being tracked, for message forwarding.</summary>
    public static ShellContextMenu? Current { get; private set; }

    private ShellContextMenu(object menu, string? directory)
    {
        _menu = (IContextMenu)menu;
        _menu2 = menu as IContextMenu2;
        _menu3 = menu as IContextMenu3;
        _directory = directory;
    }

    /// <summary>Menu for one or more items that share the same parent folder.</summary>
    public static ShellContextMenu? ForItems(IReadOnlyList<Pidl> items, IntPtr hwnd, string? directory)
    {
        if (items.Count == 0) return null;
        var natives = items.Select(p => p.ToNative()).ToArray();
        IShellFolder? parent = null;
        try
        {
            // A shell menu needs one parent folder; giving the desktop full ID lists instead crashes inside the shell.
            if (!ShareParent(items)) return null;
            var iidFolder = IIDs.IShellFolder;
            if (Shell32.SHBindToParent(natives[0].Ptr, ref iidFolder, out parent, out _) != 0) return null;
            var children = natives.Select(n => Shell32.ILFindLastID(n.Ptr)).ToArray();
            var iidMenu = IIDs.IContextMenu;
            if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
                return null;
            return Wrap(ptr, directory);
        }
        finally
        {
            ShellNative.Release(parent);
            foreach (var n in natives) n.Dispose();
        }
    }

    /// <summary>True when all items sit in the same folder (a native menu can only be built for those).</summary>
    public static bool ShareParent(IReadOnlyList<Pidl> items)
    {
        if (items.Count < 2) return true;
        // Compared by name: two parses of one folder needn't give identical bytes (items carry cached metadata).
        var first = ParentName(items[0]);
        return first is not null && items.Skip(1).All(p => string.Equals(ParentName(p), first, StringComparison.OrdinalIgnoreCase));

        static string? ParentName(Pidl pidl)
        {
            if (pidl.GetParent() is not { } parent) return null;
            using var native = parent.ToNative();
            return ShellNative.GetName(native.Ptr, SIGDN.DESKTOPABSOLUTEPARSING);
        }
    }

    /// <summary>Menu for the empty area of a folder (New ▸, Paste, Properties…).</summary>
    public static ShellContextMenu? ForBackground(Pidl folder, IntPtr hwnd, string? directory)
    {
        IShellFolder? sf = null;
        try
        {
            sf = ShellNative.BindToFolder(folder);
            var iid = IIDs.IContextMenu;
            if (sf.CreateViewObject(hwnd, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            return Wrap(ptr, directory);
        }
        catch (COMException) { return null; }
        finally { ShellNative.Release(sf); }
    }

    private static ShellContextMenu Wrap(IntPtr ptr, string? directory)
    {
        try { return new ShellContextMenu(Marshal.GetObjectForIUnknown(ptr), directory); }
        finally { Marshal.Release(ptr); }
    }

    public MenuResult Show(IntPtr hwnd, int screenX, int screenY, IReadOnlyList<CustomMenuItem> customTop,
        IReadOnlyCollection<string> interceptVerbs, bool extended, Func<string?, string, bool>? hide = null)
    {
        if (_menu is null) return MenuResult.Nothing;
        var hmenu = User32.CreatePopupMenu();
        try
        {
            var flags = CMF.NORMAL | CMF.CANRENAME | (extended ? CMF.EXTENDEDVERBS : 0);
            _menu.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, flags);

            if (hide is not null) RemoveItems(hmenu, hide);

            if (customTop.Count > 0)
            {
                var pos = 0u;
                foreach (var item in customTop) Insert(hmenu, ref pos, item);
                if (User32.GetMenuItemCount(hmenu) > pos)
                    User32.InsertMenuW(hmenu, pos, MF.BYPOSITION | MF.SEPARATOR, UIntPtr.Zero, null);
            }

            Current = this;
            int cmd;
            try { cmd = User32.TrackPopupMenuEx(hmenu, TPM.RETURNCMD | TPM.RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero); }
            finally { Current = null; }

            if (cmd == 0) return MenuResult.Nothing;
            if (cmd >= FirstCustomId) return new MenuResult(MenuResultKind.Custom, cmd);

            var id = (uint)cmd - FirstShellId;
            var verb = GetVerb(id);
            if (verb is not null && interceptVerbs.Contains(verb)) return new MenuResult(MenuResultKind.Verb, Verb: verb);

            Invoke(id, null, hwnd, screenX, screenY);
            return new MenuResult(MenuResultKind.Invoked, Verb: verb);
        }
        finally
        {
            User32.DestroyMenu(hmenu);
        }
    }

    /// <summary>Invokes a canonical verb ("properties", "delete", "copy", "cut", "paste", "openas"…) without showing the menu.</summary>
    public bool InvokeVerb(string verb, IntPtr hwnd)
    {
        if (_menu is null) return false;
        var hmenu = User32.CreatePopupMenu();
        try
        {
            _menu.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, CMF.NORMAL | CMF.CANRENAME);
            return Invoke(null, verb, hwnd, 0, 0);
        }
        finally { User32.DestroyMenu(hmenu); }
    }

    private bool Invoke(uint? id, string? verb, IntPtr hwnd, int x, int y)
    {
        var ansiVerb = verb is null ? (IntPtr)id!.Value : Marshal.StringToHGlobalAnsi(verb);
        var wideVerb = verb is null ? (IntPtr)id!.Value : Marshal.StringToHGlobalUni(verb);
        var dirW = _directory is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(_directory);
        var dirA = _directory is null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(_directory);
        try
        {
            var mask = CMIC.MASK_UNICODE | (verb is null ? CMIC.MASK_PTINVOKE : 0);
            if (KeyState.Ctrl) mask |= CMIC.MASK_CONTROL_DOWN;
            if (KeyState.Shift) mask |= CMIC.MASK_SHIFT_DOWN;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = hwnd,
                lpVerb = ansiVerb,
                lpVerbW = wideVerb,
                lpDirectory = dirA,
                lpDirectoryW = dirW,
                nShow = 1,
                ptInvoke = new POINT(x, y),
            };
            return _menu!.InvokeCommand(ref info) == 0;
        }
        catch (COMException) { return false; }
        finally
        {
            if (verb is not null) { Marshal.FreeHGlobal(ansiVerb); Marshal.FreeHGlobal(wideVerb); }
            if (dirW != IntPtr.Zero) Marshal.FreeHGlobal(dirW);
            if (dirA != IntPtr.Zero) Marshal.FreeHGlobal(dirA);
        }
    }

    private string? GetVerb(uint id)
    {
        const int cch = 256;
        var buffer = Marshal.AllocCoTaskMem(cch * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (_menu!.GetCommandString((UIntPtr)id, GCS.VERBW, IntPtr.Zero, buffer, cch) != 0) return null;
            var s = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    /// <summary>Removes shell entries the user doesn't want (by canonical verb or visible text), then tidies separators.</summary>
    private void RemoveItems(IntPtr hmenu, Func<string?, string, bool> hide)
    {
        var text = new char[256];
        for (var i = User32.GetMenuItemCount(hmenu) - 1; i >= 0; i--)
        {
            var id = User32.GetMenuItemID(hmenu, i);
            var len = GetMenuStringW(hmenu, (uint)i, text, text.Length, MF.BYPOSITION);
            var label = len > 0 ? new string(text, 0, len).Replace("&", "") : "";
            string? verb = null;
            if (id != uint.MaxValue && id >= FirstShellId && id <= LastShellId) verb = GetVerb(id - FirstShellId);
            if (label.Length == 0 && verb is null) continue; // separators
            if (hide(verb, label)) RemoveMenu(hmenu, (uint)i, MF.BYPOSITION);
        }

        // No leading, trailing or doubled separators after removals.
        var previousWasSeparator = true;
        for (var i = 0; i < User32.GetMenuItemCount(hmenu); i++)
        {
            var isSeparator = (GetMenuState(hmenu, (uint)i, MF.BYPOSITION) & MF.SEPARATOR) != 0;
            if (isSeparator && previousWasSeparator) { RemoveMenu(hmenu, (uint)i, MF.BYPOSITION); i--; continue; }
            previousWasSeparator = isSeparator;
        }
        var last = User32.GetMenuItemCount(hmenu) - 1;
        if (last >= 0 && (GetMenuState(hmenu, (uint)last, MF.BYPOSITION) & MF.SEPARATOR) != 0) RemoveMenu(hmenu, (uint)last, MF.BYPOSITION);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, [Out] char[] lpString, int cchMax, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    private static void Insert(IntPtr hmenu, ref uint pos, CustomMenuItem item)
    {
        if (item.IsSeparator)
        {
            User32.InsertMenuW(hmenu, pos++, MF.BYPOSITION | MF.SEPARATOR, UIntPtr.Zero, null);
            return;
        }
        var flags = MF.BYPOSITION | MF.STRING | (item.Enabled ? 0 : MF.GRAYED) | (item.IsChecked ? 0x8u : 0);
        if (item.Children is { Count: > 0 })
        {
            var sub = User32.CreatePopupMenu();
            var subPos = 0u;
            foreach (var child in item.Children) Insert(sub, ref subPos, child);
            User32.InsertMenuW(hmenu, pos++, flags | MF.POPUP, (UIntPtr)(ulong)sub, item.Text);
        }
        else
        {
            User32.InsertMenuW(hmenu, pos++, flags, (UIntPtr)(uint)item.Id, item.Text);
            if (item.IsDefault) User32.SetMenuDefaultItem(hmenu, (uint)item.Id, 0);
        }
    }

    /// <summary>Forward WM_INITMENUPOPUP, WM_DRAWITEM, WM_MEASUREITEM and WM_MENUCHAR while the menu is open.</summary>
    public bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        try
        {
            if (_menu3 is not null)
                return _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
            if (_menu2 is not null && msg != WM.MENUCHAR)
                return _menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
        }
        catch (COMException) { }
        return false;
    }

    public void Dispose()
    {
        ShellNative.Release(_menu);
        _menu = null;
        _menu2 = null;
        _menu3 = null;
    }

    private static class KeyState
    {
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        public static bool Ctrl => GetKeyState(0x11) < 0;
        public static bool Shift => GetKeyState(0x10) < 0;
    }
}
