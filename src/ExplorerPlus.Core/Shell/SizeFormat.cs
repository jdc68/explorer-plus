namespace ExplorerPlus.Core.Shell;

public static class SizeFormat
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    /// <summary>Size column text: whole KB below 1,000 KB ("0 KB", "740 KB"), then two decimals in the largest unit
    /// that stays under 1,000 ("12.45 MB", "4.65 GB"). Units are 1024-based, as everywhere in Windows.</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 KB";
        var kb = Math.Max(1, (bytes + 1023) / 1024);
        if (kb < 1000) return kb.ToString("N0") + " KB";
        double v = bytes / 1024.0;
        var u = 0;
        // Switch on the rounded value so 999.996 MB reads "0.98 GB", not "1,000.00 MB".
        while (Math.Round(v, 2) >= 1000 && u < Units.Length - 1) { v /= 1024; u++; }
        return v.ToString("N2") + " " + Units[u];
    }

    /// <summary>Like <see cref="Format"/>, but sizes under 1 KB are given in bytes (status bar, details pane).</summary>
    public static string FormatWithBytes(long bytes) =>
        bytes is >= 0 and < 1024 ? $"{bytes:N0} bytes" : Format(bytes);
}
