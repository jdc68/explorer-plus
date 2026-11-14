using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class ThisPcTests
{
    private static FolderEntry Entry(string name, string? path) =>
        new() { Name = name, DisplayName = name, Path = path, TypeName = "", IsFolder = true };

    [Fact]
    public void Items_go_into_explorers_groups_with_drives_in_letter_order_and_each_volume_once()
    {
        var kinds = new Dictionary<string, DriveKind>
        {
            [@"E:\"] = DriveKind.Fixed, [@"C:\"] = DriveKind.Fixed, [@"F:\"] = DriveKind.Fixed,
            [@"Z:\"] = DriveKind.Network, [@"G:\"] = DriveKind.Removable,
        };
        // F: is a second letter for E:'s volume
        var volumes = new Dictionary<string, string?> { [@"E:\"] = "vol-e", [@"C:\"] = "vol-c", [@"F:\"] = "vol-e", [@"G:\"] = null };

        var items = new[]
        {
            Entry("Data (E:)", @"E:\"), Entry("Local Disk (C:)", @"C:\"), Entry("Backup (F:)", @"F:\"),
            Entry("share (Z:)", @"Z:\"), Entry("USB (G:)", @"G:\"), Entry("Phone", null),
            Entry("Documents", @"C:\Users\me\Documents"), Entry("NAS", @"\\nas\media"),
        };
        var arranged = ThisPc.Arrange(items, root => kinds[root], (root, _) => volumes.GetValueOrDefault(root)).ToList();

        Assert.DoesNotContain(arranged, e => e.Path == @"F:\");
        Assert.Equal(7, arranged.Count);
        var drives = arranged.Where(e => e.Section == ThisPc.Drives).OrderBy(e => e.SectionOrder).Select(e => e.Name);
        Assert.Equal(["Local Disk (C:)", "Data (E:)", "USB (G:)", "Phone"], drives);
        Assert.Equal([ "NAS", "share (Z:)" ], arranged.Where(e => e.Section == ThisPc.Network).Select(e => e.Name).Order());
        Assert.Equal(ThisPc.Folders, arranged.Single(e => e.Name == "Documents").Section);
        Assert.True(arranged.Single(e => e.Name == "Documents").SectionRank < arranged[0].SectionRank);
    }

    [Fact]
    public async Task The_system_drive_reports_its_space_file_system_and_disk()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var reading = await DriveService.ReadAsync(root, withFacts: true, timeoutMs: 10_000);

        Assert.NotNull(reading);
        Assert.True(reading.Space is { Total: > 0 } s && s.Free <= s.Total);
        Assert.Equal("NTFS", reading.Facts!.FileSystem);
        Assert.False(string.IsNullOrEmpty(reading.Facts.Media));
        Assert.True(reading.Facts.RecycleBinBytes >= 0);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"c:\", true)]
    [InlineData(@"C:", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(null, false)]
    public void Drive_roots_are_recognised(string? path, bool expected) => Assert.Equal(expected, DriveService.IsDriveRoot(path));
}
