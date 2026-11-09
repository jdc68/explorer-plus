using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerPlus.Core.Shell;

public static class Associations
{
    private static readonly ConcurrentDictionary<string, bool> BrowsedByShell = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

    private const int ASSOCSTR_FRIENDLYAPPNAME = 4;

    /// <summary>
    /// True when the default "open" of this file type is handled by the shell itself (e.g. .zip with Windows'
    /// compressed folders). Then we browse into it instead of letting the shell open a stock Explorer window.
    /// If the user associated another app (7-Zip, WinRAR), that app is launched as usual.
    /// </summary>
    public static bool IsBrowsedByShell(string extension) =>
        BrowsedByShell.GetOrAdd(extension, static ext =>
        {
            var name = Query(ext, ASSOCSTR_FRIENDLYAPPNAME);
            return name is not null && name.Contains("Explorer", StringComparison.OrdinalIgnoreCase);
        });

    private static string? Query(string ext, int what)
    {
        uint size = 0;
        if (AssocQueryStringW(0x40 /* ASSOCF_NOTRUNCATE */, what, ext, "open", null, ref size) != 1 || size == 0) return null;
        var sb = new StringBuilder((int)size);
        return AssocQueryStringW(0x40, what, ext, "open", sb, ref size) == 0 ? sb.ToString() : null;
    }
}
