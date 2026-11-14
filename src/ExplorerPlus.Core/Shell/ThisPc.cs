namespace ExplorerPlus.Core.Shell;

public static class ThisPc
{
    public const string Folders = "Folders";
    public const string Drives = "Devices and drives";
    public const string Network = "Network locations";

    /// <summary>Puts This PC's items in Explorer's groups, drives in letter order, and lists each volume once.</summary>
    public static IEnumerable<FolderEntry> Arrange(IEnumerable<FolderEntry> items) =>
        Arrange(items, root => DriveService.KindOf(root), DriveService.VolumeIdOf);

    internal static IEnumerable<FolderEntry> Arrange(IEnumerable<FolderEntry> items, Func<string, DriveKind> kindOf, Func<string, DriveKind, string?> volumeOf)
    {
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var item in items)
        {
            n++;
            if (DriveService.IsDriveRoot(item.Path))
            {
                var root = item.Path!;
                var kind = kindOf(root);
                if (volumeOf(root, kind) is { } volume && !volumes.Add(volume)) continue;
                (item.Section, item.SectionRank) = kind == DriveKind.Network ? (Network, 2) : (Drives, 1);
                item.SectionOrder = char.ToUpperInvariant(root[0]) - 'A';
            }
            else
            {
                (item.Section, item.SectionRank) = item.Path switch
                {
                    { } p when p.StartsWith(@"\\", StringComparison.Ordinal) => (Network, 2),
                    { } => (Folders, 0),
                    // phones, cameras and media servers
                    null => (Drives, 1),
                };
                item.SectionOrder = 100 + n;
            }
            yield return item;
        }
    }
}
