using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.Core.Tests;

public class FolderHistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0);

    [Fact]
    public void Folders_rank_by_how_often_and_how_recently_they_were_opened()
    {
        var visits = new List<FolderVisit>();
        for (var i = 0; i < 10; i++) FolderHistory.Record(visits, @"C:\Old", Now.AddDays(-40));
        for (var i = 0; i < 3; i++) FolderHistory.Record(visits, @"C:\Work", Now.AddHours(-1));
        FolderHistory.Record(visits, @"C:\Once", Now.AddMinutes(-5));
        FolderHistory.Record(visits, @"C:\Work\", Now);

        Assert.Equal(4, visits.Single(v => v.Path == @"C:\Work").Count);
        Assert.Equal([@"C:\Work", @"C:\Once", @"C:\Old"], FolderHistory.Ranked(visits, Now));
    }

    [Fact]
    public void Recycle_bin_temp_folders_and_drive_roots_are_never_recorded()
    {
        var visits = new List<FolderVisit>();
        Assert.False(FolderHistory.Record(visits, @"C:\$Recycle.Bin\S-1-5-21", Now));
        Assert.False(FolderHistory.Record(visits, Path.Combine(Path.GetTempPath(), "build"), Now));
        Assert.False(FolderHistory.Record(visits, Path.GetTempPath(), Now));
        Assert.False(FolderHistory.Record(visits, @"D:\", Now));
        Assert.True(FolderHistory.Record(visits, @"D:\Photos", Now));
        Assert.Single(visits);
    }

    [Fact]
    public void History_keeps_its_limit_by_dropping_the_weakest_folder()
    {
        var visits = new List<FolderVisit>();
        FolderHistory.Record(visits, @"C:\Stale", Now.AddDays(-90));
        for (var i = 1; i < FolderHistory.Limit; i++)
        {
            FolderHistory.Record(visits, $@"C:\F{i}", Now.AddDays(-1));
            FolderHistory.Record(visits, $@"C:\F{i}", Now.AddDays(-1));
        }
        Assert.Equal(FolderHistory.Limit, visits.Count);

        FolderHistory.Record(visits, @"C:\New", Now);
        Assert.Equal(FolderHistory.Limit, visits.Count);
        Assert.Contains(visits, v => v.Path == @"C:\New");
        Assert.DoesNotContain(visits, v => v.Path == @"C:\Stale");
    }

    [Fact]
    public void Seeding_keeps_explorers_order_and_fades_like_any_other_visit()
    {
        var seeded = FolderHistory.Seed([@"C:\A", @"C:\B\", @"C:\a", @"D:\", @"C:\C"], Now);
        Assert.Equal([@"C:\A", @"C:\B", @"C:\C"], FolderHistory.Ranked(seeded, Now));

        for (var i = 0; i < 5; i++) FolderHistory.Record(seeded, @"C:\Mine", Now.AddMinutes(1));
        Assert.Equal(@"C:\Mine", FolderHistory.Ranked(seeded, Now.AddMinutes(1))[0]);
    }

    [Fact]
    public async Task Explorers_frequent_folders_can_be_read()
    {
        var folders = await StaWorkerPool.General.Run(FolderHistory.ReadExplorerFrequent);
        Assert.All(folders, p => Assert.True(Path.IsPathRooted(p)));
    }
}
