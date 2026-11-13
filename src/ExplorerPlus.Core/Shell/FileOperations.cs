using System.Runtime.InteropServices;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public enum FileOpResult { Succeeded, Cancelled, Failed }

public static class FileOperations
{
    private const uint BaseFlags = FOF.ALLOWUNDO | FOF.NOCONFIRMMKDIR | FOF.FOFX_ADDUNDORECORD | FOF.FOFX_SHOWELEVATIONPROMPT;

    public static Task<FileOpResult> CopyAsync(IReadOnlyList<Pidl> items, Pidl destination, IntPtr owner, bool renameOnCollision = false, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags | (renameOnCollision ? FOF.RENAMEONCOLLISION : 0), (op, keep) =>
        {
            var dest = keep.Add(ShellNative.CreateItem(destination));
            var array = keep.Add(ShellItemArray.Create(items));
            Check(op.CopyItems(array.Ptr, dest));
        });

    public static Task<FileOpResult> MoveAsync(IReadOnlyList<Pidl> items, Pidl destination, IntPtr owner, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags, (op, keep) =>
        {
            var dest = keep.Add(ShellNative.CreateItem(destination));
            var array = keep.Add(ShellItemArray.Create(items));
            Check(op.MoveItems(array.Ptr, dest));
        });

    public static Task<FileOpResult> DeleteAsync(IReadOnlyList<Pidl> items, IntPtr owner, bool permanent, bool confirm = true, FileOpLog? log = null) =>
        Run(owner, log, (permanent ? FOF.WANTNUKEWARNING | FOF.FOFX_SHOWELEVATIONPROMPT : BaseFlags | FOF.FOFX_RECYCLEONDELETE)
                   | (confirm ? 0 : FOF.NOCONFIRMATION), (op, keep) =>
        {
            var array = keep.Add(ShellItemArray.Create(items));
            Check(op.DeleteItems(array.Ptr));
        });

    public static Task<FileOpResult> RenameAsync(Pidl item, string newName, IntPtr owner, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags, (op, keep) =>
        {
            var si = keep.Add(ShellNative.CreateItem(item));
            Check(op.RenameItem(si, newName, IntPtr.Zero));
        });

    /// <summary>Renames several items in one operation (one elevation prompt, one error dialog).</summary>
    public static Task<FileOpResult> RenameManyAsync(IReadOnlyList<(Pidl Item, string NewName)> items, IntPtr owner, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags, (op, keep) =>
        {
            foreach (var (item, name) in items)
            {
                var si = keep.Add(ShellNative.CreateItem(item));
                Check(op.RenameItem(si, name, IntPtr.Zero));
            }
        });

    public static Task<FileOpResult> NewFolderAsync(Pidl parent, string name, IntPtr owner, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags, (op, keep) =>
        {
            var folder = keep.Add(ShellNative.CreateItem(parent));
            Check(op.NewItem(folder, FileAttr.DIRECTORY, name, null, IntPtr.Zero));
        });

    public static Task<FileOpResult> NewFileAsync(Pidl parent, string name, IntPtr owner, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags, (op, keep) =>
        {
            var folder = keep.Add(ShellNative.CreateItem(parent));
            Check(op.NewItem(folder, FileAttr.NORMAL, name, null, IntPtr.Zero));
        });

    /// <summary>Copies one item into a folder under a new name.</summary>
    public static Task<FileOpResult> CopyAsAsync(Pidl item, Pidl destination, string newName, IntPtr owner, FileOpLog? log = null) =>
        Run(owner, log, BaseFlags, (op, keep) =>
        {
            var source = keep.Add(ShellNative.CreateItem(item));
            var dest = keep.Add(ShellNative.CreateItem(destination));
            Check(op.CopyItem(source, dest, newName, IntPtr.Zero));
        });

    /// <summary>
    /// Creates a new item from a New ▸ template, as Explorer does: an empty file, a file with the template's bytes,
    /// a copy of the template file, or an empty file handed to the template's command (the shortcut wizard).
    /// Returns where the item was created and which kind of log entry records it.
    /// </summary>
    public static async Task<(FileOpResult Result, string? Path, FileOpKind LogKind)> CreateFromTemplateAsync(
        NewItemTemplate template, Pidl folder, string folderPath, string name, IntPtr owner, FileOpLog log)
    {
        var target = Path.Combine(folderPath, name);
        if (template.Kind == NewItemKind.FileName && template.Value is { } file && ShellNewTemplates.FindTemplateFile(file) is { } source)
        {
            var sourcePidl = await StaWorkerPool.General.Run(() => ShellNative.Parse(source));
            var copied = await CopyAsAsync(sourcePidl, folder, name, owner, log);
            if (copied == FileOpResult.Succeeded && File.Exists(target))
            {
                // A fresh document, not a copy of a years-old template: current dates, writable.
                try
                {
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    File.SetCreationTime(target, DateTime.Now);
                    File.SetLastWriteTime(target, DateTime.Now);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return (copied, target, FileOpKind.Copy);
        }

        var result = await NewFileAsync(folder, name, owner, log);
        if (result != FileOpResult.Succeeded) return (result, null, FileOpKind.New);
        try
        {
            if (template.Kind == NewItemKind.Data && template.Data is { Length: > 0 } data) await File.WriteAllBytesAsync(target, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (FileOpResult.Failed, target, FileOpKind.New); }

        if (template.Kind == NewItemKind.Command && template.Value is { } command)
        {
            var (exe, args) = SplitCommand(Environment.ExpandEnvironmentVariables(command));
            args = args?.Replace("%1", target).Replace("%2", folderPath);
            await ShellLauncher.RunAsync(exe, args, folderPath, owner);
        }
        return (result, target, FileOpKind.New);
    }

    private static (string, string?) SplitCommand(string text)
    {
        text = text.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return (text[1..end], text[(end + 1)..].Trim());
        }
        var space = text.IndexOf(' ');
        return space < 0 ? (text, null) : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>"New folder", "New folder (2)", … — the same naming as Explorer.</summary>
    public static string UniqueName(string directory, string baseName, string extension = "")
    {
        var candidate = baseName + extension;
        for (var i = 2; Exists(Path.Combine(directory, candidate)); i++)
            candidate = $"{baseName} ({i}){extension}";
        return candidate;
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static Task<FileOpResult> Run(IntPtr owner, FileOpLog? log, uint flags, Action<IFileOperation, KeepAlive> setup) =>
        StaWorkerPool.RunDedicated(() =>
        {
            var type = Type.GetTypeFromCLSID(IIDs.CLSID_FileOperation, throwOnError: true)!;
            var op = (IFileOperation)Activator.CreateInstance(type)!;
            var keep = new KeepAlive();
            uint cookie = 0;
            try
            {
                if (owner != IntPtr.Zero) op.SetOwnerWindow(owner);
                if (log is not null)
                {
                    var sink = Marshal.GetComInterfaceForObject(log, typeof(IFileOperationProgressSink));
                    try { op.Advise(sink, out cookie); }
                    finally { Marshal.Release(sink); }
                }
                Check(op.SetOperationFlags(flags));
                setup(op, keep);
                var hr = op.PerformOperations();
                op.GetAnyOperationsAborted(out var aborted);
                if (hr == HResult.COPYENGINE_E_USER_CANCELLED || hr == HResult.ERROR_CANCELLED || hr == HResult.E_ABORT || aborted)
                    return FileOpResult.Cancelled;
                return hr >= 0 ? FileOpResult.Succeeded : FileOpResult.Failed;
            }
            catch (COMException)
            {
                return FileOpResult.Failed;
            }
            finally
            {
                if (cookie != 0) op.Unadvise(cookie);
                keep.Dispose();
                ShellNative.Release(op);
            }
        }, "FileOperation");

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}

internal sealed class KeepAlive : IDisposable
{
    private readonly List<object> _items = [];

    public T Add<T>(T item) where T : notnull
    {
        _items.Add(item);
        return item;
    }

    public void Dispose()
    {
        foreach (var item in _items)
        {
            if (item is IDisposable d) d.Dispose();
            else ShellNative.Release(item);
        }
        _items.Clear();
    }
}

internal sealed class ShellItemArray : IDisposable
{
    public IntPtr Ptr { get; private set; }

    private ShellItemArray(IntPtr ptr) => Ptr = ptr;

    public static ShellItemArray Create(IReadOnlyList<Pidl> items)
    {
        var natives = items.Select(p => p.ToNative()).ToArray();
        try
        {
            var ptrs = natives.Select(n => n.Ptr).ToArray();
            Marshal.ThrowExceptionForHR(Shell32.SHCreateShellItemArrayFromIDLists((uint)ptrs.Length, ptrs, out var array));
            return new ShellItemArray(array);
        }
        finally
        {
            foreach (var n in natives) n.Dispose();
        }
    }

    public void Dispose()
    {
        if (Ptr != IntPtr.Zero) Marshal.Release(Ptr);
        Ptr = IntPtr.Zero;
    }
}
