using ExplorerPlus.Core.Shell;
using Xunit;

namespace ExplorerPlus.Core.Tests;

public sealed class FolderSizeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ExplorerPlusTests", Guid.NewGuid().ToString("N"));

    public FolderSizeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, int length)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    // a folder with many subfolders takes thousands of steps to walk
    private string Big(string name, int folders = 1500)
    {
        for (var i = 0; i < folders; i++) Write($@"{name}\{i % 30}\{i}\f.bin", 10);
        return Path.Combine(_root, name);
    }

    [Fact]
    public async Task Sizes_count_files_in_every_subfolder()
    {
        Write(@"a\one.bin", 100);
        Write(@"a\b\two.bin", 20);
        Write(@"a\b\c\d\three.bin", 3);
        Directory.CreateDirectory(Path.Combine(_root, @"a\empty"));
        var service = new FolderSizeService();

        Assert.Equal(123, await service.GetAsync(Path.Combine(_root, "a"), default).WaitAsync(Timeout));
        Assert.True(service.TryGetCached(Path.Combine(_root, @"a\b"), out var b));
        Assert.Equal(23, b);
    }

    [Fact]
    public async Task A_small_folder_is_not_held_up_by_a_big_one()
    {
        var big = Big("big");
        Write(@"small\x.bin", 7);
        var service = new FolderSizeService { Slice = TimeSpan.Zero };

        var bigSize = service.GetAsync(big, default);
        var smallSize = service.GetAsync(Path.Combine(_root, "small"), default);

        Assert.Equal(7, await smallSize.WaitAsync(Timeout));
        Assert.False(bigSize.IsCompleted);
        Assert.Equal(15_000, await bigSize.WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_shared_walk_keeps_going_when_one_caller_cancels()
    {
        var big = Big("big");
        Write(@"shared\x.bin", 5);
        var service = new FolderSizeService { Slice = TimeSpan.FromSeconds(30) };
        var shared = Path.Combine(_root, "shared");

        var busy = service.GetAsync(big, default); // keeps the worker away until both callers asked
        using var first = new CancellationTokenSource();
        var firstSize = service.GetAsync(shared, first.Token);
        var secondSize = service.GetAsync(shared, default);
        first.Cancel();

        Assert.Equal(5, await secondSize.WaitAsync(Timeout));
        Assert.False(busy.IsFaulted);
    }

    [Fact]
    public async Task A_walk_stops_once_every_caller_cancelled()
    {
        var big = Big("big");
        var service = new FolderSizeService { Slice = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource();

        var size = service.GetAsync(big, cts.Token);
        cts.Cancel();

        Assert.Null(await size.WaitAsync(Timeout));
        Assert.False(service.TryGetCached(big, out _));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_has_no_size_and_is_logged()
    {
        var service = new FolderSizeService();
        var logged = new List<string>();
        service.Log += logged.Add;
        var missing = Path.Combine(_root, "missing");

        Assert.Null(await service.GetAsync(missing, default).WaitAsync(Timeout));
        Assert.Single(logged);
        Assert.StartsWith(missing, logged[0]);
    }
}
