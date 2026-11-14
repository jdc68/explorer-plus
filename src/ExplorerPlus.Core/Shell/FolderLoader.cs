using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ExplorerPlus.Core.Threading;

namespace ExplorerPlus.Core.Shell;

public static class FolderLoader
{
    private const int FirstBatchSize = 300;
    private static readonly TimeSpan FirstBatchTime = TimeSpan.FromMilliseconds(30);
    private const int BatchSize = 5000;
    private static readonly TimeSpan BatchTime = TimeSpan.FromMilliseconds(150);

    /// <summary>What Home lists; set by the app, read when a Home tab loads.</summary>
    public static Func<HomeFeedOptions>? HomeOptions { get; set; }

    public static async IAsyncEnumerable<IReadOnlyList<FolderEntry>> LoadAsync(
        ShellLocation location, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<List<FolderEntry>>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });

        var home = location.IsHome ? HomeOptions?.Invoke() ?? new HomeFeedOptions([], [], true) : null;
        Task producer = home is not null
            ? StaWorkerPool.Enumeration.Run(() => Produce(HomeFeed.Enumerate(home, ct), channel.Writer, ct), ct)
            : location.IsIndexSearch
            ? Task.Factory.StartNew(() => Produce(IndexSearch.Enumerate(location, ct), channel.Writer, ct),
                ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : location.IsDuplicates
            ? Task.Factory.StartNew(() => Produce(DuplicateFinder.Enumerate(location, ct), channel.Writer, ct),
                ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : location.IsTagView
            ? Task.Factory.StartNew(() => Produce(TagView.Enumerate(location, ct), channel.Writer, ct),
                ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : location.IsFileSystemFolder
            ? Task.Factory.StartNew(() => Produce(FastFolderEnumerator.Enumerate(location.FileSystemPath!, ct), channel.Writer, ct),
                ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : location.IsThisPC
            ? StaWorkerPool.Enumeration.Run(() => Produce(ThisPc.Arrange(ShellFolderEnumerator.Enumerate(location.Pidl, ct)), channel.Writer, ct), ct)
            : StaWorkerPool.Enumeration.Run(() => Produce(ShellFolderEnumerator.Enumerate(location.Pidl, ct), channel.Writer, ct), ct);

        _ = producer.ContinueWith(t => channel.Writer.TryComplete(t.Exception?.GetBaseException()), TaskScheduler.Default);

        await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return batch;

        await producer.ConfigureAwait(false);
    }

    private static void Produce(IEnumerable<FolderEntry> source, ChannelWriter<List<FolderEntry>> writer, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var first = true;
        var batch = new List<FolderEntry>(FirstBatchSize);
        foreach (var entry in source)
        {
            batch.Add(entry);
            var limit = first ? FirstBatchSize : BatchSize;
            var time = first ? FirstBatchTime : BatchTime;
            if (batch.Count >= limit || sw.Elapsed >= time)
            {
                Write(writer, batch, ct);
                batch = new List<FolderEntry>(BatchSize);
                first = false;
                sw.Restart();
            }
        }
        if (batch.Count > 0) Write(writer, batch, ct);
    }

    private static void Write(ChannelWriter<List<FolderEntry>> writer, List<FolderEntry> batch, CancellationToken ct)
    {
        while (!writer.TryWrite(batch))
        {
            if (!writer.WaitToWriteAsync(ct).AsTask().GetAwaiter().GetResult()) return;
        }
    }
}
