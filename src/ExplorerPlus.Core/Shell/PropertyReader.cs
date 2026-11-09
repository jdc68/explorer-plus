using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

/// <summary>A property as Explorer shows it, plus a value to sort by (number, date or text).</summary>
public readonly record struct PropertyValue(string Display, IComparable? Sort)
{
    public static PropertyValue Empty { get; } = new("", null);
}

public static class PropertyReader
{
    private static readonly ConcurrentDictionary<string, PROPERTYKEY?> Keys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Guid IidShellItem2 = IIDs.IShellItem2;

    /// <summary>
    /// Reads shell properties ("System.Music.Artist", "System.Image.Dimensions"…) for one item, formatted exactly
    /// as Explorer formats them. Values come from the property handlers (file metadata), so this runs on a
    /// background STA thread and only for items that are on screen.
    /// </summary>
    public static Dictionary<string, PropertyValue> Read(string? path, Pidl? pidl, IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, PropertyValue>(StringComparer.OrdinalIgnoreCase);
        IShellItem2? item = null;
        var propvar = Marshal.AllocCoTaskMem(24);
        try
        {
            var iid = IidShellItem2;
            int hr;
            if (pidl is not null)
            {
                using var native = pidl.ToNative();
                hr = Shell32.SHCreateItem2FromIDList(native.Ptr, ref iid, out item);
            }
            else if (path is not null) hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            else return result;
            if (hr != 0 || item is null) return result;

            foreach (var name in names)
            {
                if (KeyOf(name) is not { } key) { result[name] = PropertyValue.Empty; continue; }
                ZeroMemory(propvar);
                try
                {
                    if (item.GetProperty(ref key, propvar) != 0) { result[name] = PropertyValue.Empty; continue; }
                    var display = "";
                    if (PSFormatForDisplayAlloc(ref key, propvar, 0, out var text) == 0 && text != IntPtr.Zero)
                    {
                        display = Marshal.PtrToStringUni(text) ?? "";
                        Marshal.FreeCoTaskMem(text);
                    }
                    if (display.Length == 0 && (ushort)Marshal.ReadInt16(propvar) == 31) // VT_LPWSTR the formatter didn't take
                        display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(propvar + 8)) ?? "";
                    result[name] = new PropertyValue(display, SortValue(propvar, display));
                }
                finally { PropVariantClear(propvar); }
            }
        }
        catch (COMException) { }
        finally
        {
            Marshal.FreeCoTaskMem(propvar);
            ShellNative.Release(item);
        }
        return result;
    }

    private static readonly ConcurrentDictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The property's label as Explorer shows it ("Date taken", "Contributing artists"…).</summary>
    public static string LabelOf(string canonicalName) => Labels.GetOrAdd(canonicalName, n =>
    {
        if (KeyOf(n) is not { } key) return n;
        var iid = typeof(IPropertyDescription).GUID;
        try
        {
            if (PreviewNative.PSGetPropertyDescription(ref key, ref iid, out var desc) != 0 || desc is null) return n;
            try
            {
                if (desc.GetDisplayName(out var ptr) != 0 || ptr == IntPtr.Zero) return n;
                var label = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
                return string.IsNullOrEmpty(label) ? n : label;
            }
            finally { ShellNative.Release(desc); }
        }
        catch (COMException) { return n; }
    });

    /// <summary>
    /// The properties Explorer's details pane shows for this kind of item (its System.PropList.PreviewDetails list),
    /// with labels and formatted values; empty values are left out.
    /// </summary>
    public static List<(string Name, string Label, string Value)> PreviewDetails(string? path, Pidl? pidl)
    {
        var list = Read(path, pidl, ["System.PropList.PreviewDetails"]);
        var names = new List<string>();
        if (list.TryGetValue("System.PropList.PreviewDetails", out var v) && v.Display.Length > 0)
        {
            var text = v.Display.StartsWith("prop:", StringComparison.OrdinalIgnoreCase) ? v.Display[5..] : v.Display;
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part.TrimStart('*', '~', '#', '!');
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
        }
        if (names.Count == 0) names.AddRange(["System.ItemTypeText", "System.Size", "System.DateModified", "System.DateCreated"]);
        var values = Read(path, pidl, names);
        var result = new List<(string, string, string)>();
        foreach (var name in names)
            if (values.TryGetValue(name, out var value) && value.Display.Length > 0)
                result.Add((name, LabelOf(name), value.Display));
        return result;
    }

    private static PROPERTYKEY? KeyOf(string canonicalName) =>
        Keys.GetOrAdd(canonicalName, n => PSGetPropertyKeyFromName(n, out var k) == 0 ? k : null);

    // PROPVARIANT: VARTYPE at offset 0, value at offset 8.
    private static IComparable? SortValue(IntPtr pv, string display)
    {
        var vt = (ushort)Marshal.ReadInt16(pv);
        var data = pv + 8;
        return vt switch
        {
            2 => (long)Marshal.ReadInt16(data),          // VT_I2
            3 or 22 => (long)Marshal.ReadInt32(data),    // VT_I4, VT_INT
            18 => (long)(ushort)Marshal.ReadInt16(data), // VT_UI2
            19 or 23 => (long)(uint)Marshal.ReadInt32(data), // VT_UI4, VT_UINT
            20 or 21 => Marshal.ReadInt64(data),         // VT_I8, VT_UI8
            4 => (double)BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data)), // VT_R4
            5 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data)),        // VT_R8
            11 => (long)Marshal.ReadInt16(data),         // VT_BOOL
            64 => Marshal.ReadInt64(data),               // VT_FILETIME
            _ => display.Length == 0 ? null : display,
        };
    }

    private static void ZeroMemory(IntPtr p)
    {
        for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(p, i, 0);
    }

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSGetPropertyKeyFromName(string pszName, out PROPERTYKEY ppropkey);

    [DllImport("propsys.dll")]
    private static extern int PSFormatForDisplayAlloc(ref PROPERTYKEY key, IntPtr propvar, int pdff, out IntPtr ppszDisplay);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pvar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);
}
