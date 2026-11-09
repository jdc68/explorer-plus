using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerPlus.Interop;
using Microsoft.Win32;

namespace ExplorerPlus.Core.Shell;

public static class PreviewHandlers
{
    private const string HandlerKey = "{" + PreviewNative.IID_IPreviewHandler + "}";
    private static readonly ConcurrentDictionary<string, Guid?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The preview handler registered for a file extension (".pdf", ".docx"…), as Explorer's preview pane finds it.</summary>
    public static Guid? Find(string extension) =>
        string.IsNullOrEmpty(extension) ? null : Cache.GetOrAdd(extension, Lookup);

    private static Guid? Lookup(string ext)
    {
        uint size = 64;
        var sb = new StringBuilder((int)size);
        if (PreviewNative.AssocQueryStringW(PreviewNative.ASSOCF_NOTRUNCATE, PreviewNative.ASSOCSTR_SHELLEXTENSION, ext, HandlerKey, sb, ref size) == 0
            && Guid.TryParse(sb.ToString(), out var found))
            return found;

        // Handlers registered for a kind of file rather than an extension.
        try
        {
            if (Read($@"SystemFileAssociations\{ext}\ShellEx\{HandlerKey}") is { } byExt) return byExt;
            using var key = Registry.ClassesRoot.OpenSubKey(ext);
            if (key?.GetValue("PerceivedType") is string perceived && Read($@"SystemFileAssociations\{perceived}\ShellEx\{HandlerKey}") is { } byKind)
                return byKind;
        }
        catch (Exception) { }
        return null;
    }

    private static Guid? Read(string subKey)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(subKey);
        return key?.GetValue(null) is string s && Guid.TryParse(s, out var g) ? g : null;
    }
}

/// <summary>
/// A running preview handler drawing into a window of ours. Handlers run out of process (prevhost.exe), as in
/// Explorer, so a crashing handler can't take the app down. Every call must come from <see cref="Threading.StaWorkerPool.Preview"/>.
/// </summary>
public sealed class PreviewSession : IDisposable
{
    private IPreviewHandler? _handler;

    private PreviewSession(IPreviewHandler handler) => _handler = handler;

    public static PreviewSession? Open(Guid clsid, string path, IntPtr hwnd, RECT rect, uint? background, uint? foreground)
    {
        var iid = new Guid(PreviewNative.IID_IPreviewHandler);
        if (PreviewNative.CoCreateInstance(ref clsid, IntPtr.Zero, PreviewNative.CLSCTX_LOCAL_SERVER, ref iid, out var obj) != 0 || obj is not IPreviewHandler handler)
            return null;
        try
        {
            if (!Initialize(obj, path)) { ShellNative.Release(obj); return null; }
            if (obj is IPreviewHandlerVisuals visuals)
            {
                if (background is { } bg) visuals.SetBackgroundColor(bg);
                if (foreground is { } fg) visuals.SetTextColor(fg);
            }
            if (handler.SetWindow(hwnd, ref rect) != 0 || handler.DoPreview() != 0)
            {
                handler.Unload();
                ShellNative.Release(obj);
                return null;
            }
            return new PreviewSession(handler);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            ShellNative.Release(obj);
            return null;
        }
    }

    private static bool Initialize(object obj, string path)
    {
        const uint mode = PreviewNative.STGM_READ;
        if (obj is IInitializeWithFile file) return file.Initialize(path, mode) == 0;
        if (obj is IInitializeWithItem withItem)
        {
            if (ShellNative.TryParse(path) is not { } pidl) return false;
            var item = ShellNative.CreateItem(pidl);
            try { return withItem.Initialize(item, mode) == 0; }
            finally { ShellNative.Release(item); }
        }
        if (obj is IInitializeWithStream withStream)
        {
            if (PreviewNative.SHCreateStreamOnFileEx(path, PreviewNative.STGM_READ | PreviewNative.STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out var stream) != 0)
                return false;
            // The handler keeps the stream until Unload; it owns the reference from here on.
            return withStream.Initialize(stream, mode) == 0;
        }
        return false;
    }

    public void Resize(RECT rect)
    {
        try { _handler?.SetRect(ref rect); }
        catch (COMException) { }
    }

    public void Dispose()
    {
        if (_handler is null) return;
        try { _handler.Unload(); }
        catch (COMException) { }
        ShellNative.Release(_handler);
        _handler = null;
    }
}
