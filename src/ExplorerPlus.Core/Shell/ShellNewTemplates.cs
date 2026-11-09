using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ExplorerPlus.Core.Shell;

public enum NewItemKind { NullFile, Data, FileName, Command }

/// <summary>One entry of Explorer's "New ▸" menu, read from a file type's ShellNew registry key.</summary>
/// <param name="BaseName">Name of the new item without extension ("New Text Document").</param>
/// <param name="Value">Template file (FileName) or command line (Command).</param>
public sealed record NewItemTemplate(string Extension, string TypeName, string BaseName, NewItemKind Kind, string? Value, byte[]? Data, int IconIndex);

/// <summary>Explorer's New ▸ templates (HKCR\.ext\ShellNew and HKCR\.ext\ProgID\ShellNew).</summary>
public static class ShellNewTemplates
{
    // Libraries are cut by default (bloat review); briefcases and contacts are long dead.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".library-ms", ".bfc", ".contact" };

    private static Task<IReadOnlyList<NewItemTemplate>>? _cached;

    /// <summary>The templates, read once in the background (a few thousand registry keys) and cached.</summary>
    public static Task<IReadOnlyList<NewItemTemplate>> GetAsync() => _cached ??= Task.Run(Load);

    public static void Invalidate() => _cached = null;

    internal static IReadOnlyList<NewItemTemplate> Load()
    {
        var result = new List<NewItemTemplate>();
        var root = Registry.ClassesRoot;
        foreach (var ext in root.GetSubKeyNames())
        {
            if (ext.Length < 2 || ext[0] != '.' || Skipped.Contains(ext)) continue;
            try
            {
                using var extKey = root.OpenSubKey(ext);
                if (extKey is null) continue;
                var progId = extKey.GetValue("") as string;
                using var shellNew = extKey.OpenSubKey("ShellNew")
                                     ?? (string.IsNullOrEmpty(progId) ? null : extKey.OpenSubKey(progId + @"\ShellNew"));
                if (shellNew is null) continue;
                if (Read(ext, progId, shellNew) is { } template) result.Add(template);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return result
            .GroupBy(t => t.TypeName, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.TypeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static NewItemTemplate? Read(string ext, string? progId, RegistryKey shellNew)
    {
        var names = shellNew.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        NewItemKind kind;
        string? value = null;
        byte[]? data = null;
        // Same precedence as Explorer: Command, Data, FileName, NullFile.
        if (names.Contains("Command"))
        {
            kind = NewItemKind.Command;
            value = shellNew.GetValue("Command") as string;
        }
        else if (names.Contains("Data"))
        {
            kind = NewItemKind.Data;
            data = shellNew.GetValue("Data") switch
            {
                byte[] b => b,
                string s => Encoding.Default.GetBytes(s),
                _ => [],
            };
        }
        else if (names.Contains("FileName"))
        {
            kind = NewItemKind.FileName;
            value = shellNew.GetValue("FileName") as string;
        }
        else if (names.Contains("NullFile")) kind = NewItemKind.NullFile;
        else return null;
        if (kind is NewItemKind.Command or NewItemKind.FileName && string.IsNullOrWhiteSpace(value)) return null;

        var (shellType, icon) = ShellInfoCache.Get(ext, false);
        var typeName = FriendlyTypeName(progId) ?? shellType;
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        var baseName = Indirect(shellNew.GetValue("ItemName") as string);
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            baseName = baseName.Trim();
            if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^ext.Length];
        }
        else baseName = "New " + typeName;

        return new NewItemTemplate(ext, typeName, baseName, kind, value, data, icon);
    }

    private static string? FriendlyTypeName(string? progId)
    {
        if (string.IsNullOrEmpty(progId)) return null;
        using var key = Registry.ClassesRoot.OpenSubKey(progId);
        if (key is null) return null;
        return Indirect(key.GetValue("FriendlyTypeName") as string) is { Length: > 0 } friendly
            ? friendly
            : key.GetValue("") as string is { Length: > 0 } plain ? plain : null;
    }

    /// <summary>Resolves "@%SystemRoot%\system32\notepad.exe,-470" style resource strings.</summary>
    private static string? Indirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@') return value;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(value, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    /// <summary>Where a FileName template lives: as given if rooted, else the user's, everyone's or Windows' ShellNew folder.</summary>
    public static string? FindTemplateFile(string fileName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(fileName);
        if (Path.IsPathRooted(expanded)) return File.Exists(expanded) ? expanded : null;
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
        ];
        return folders.Where(f => f.Length > 0).Select(f => Path.Combine(f, expanded)).FirstOrDefault(File.Exists);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);
}
