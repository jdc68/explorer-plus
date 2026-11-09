using System.Collections.Concurrent;

namespace ExplorerPlus.Core.Threading;

public sealed class StaWorkerPool : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly List<Thread> _threads = [];

    public StaWorkerPool(int threadCount, string name)
    {
        for (var i = 0; i < threadCount; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = $"{name}-{i}", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _threads.Add(t);
        }
    }

    /// <summary>Folder enumeration of virtual (non file-system) locations and item resolution.</summary>
    public static StaWorkerPool Enumeration { get; } = new(3, "ShellEnum");

    /// <summary>Icons and other small per-item lookups.</summary>
    public static StaWorkerPool Icons { get; } = new(2, "ShellIcons");

    /// <summary>Launching items, verbs and miscellaneous shell calls.</summary>
    public static StaWorkerPool General { get; } = new(2, "ShellGeneral");

    /// <summary>Metadata for property columns (artist, dimensions…), read only for items on screen.</summary>
    public static StaWorkerPool Properties { get; } = new(2, "ShellProps");

    /// <summary>Preview handlers: one thread, because a handler must be created, used and released on the same thread.</summary>
    public static StaWorkerPool Preview { get; } = new(1, "ShellPreview");

    public Task<T> Run<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(work()); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task Run(Action work, CancellationToken ct = default) => Run(() => { work(); return true; }, ct);

    /// <summary>Runs work on a brand-new STA thread (for long operations that show their own UI).</summary>
    public static Task<T> RunDedicated<T>(Func<T> work, string name = "ShellOp")
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            OleInitialize(IntPtr.Zero);
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = name };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    private void Worker()
    {
        // Plain P/Invoke calls (SHGetFileInfo etc.) don't trigger .NET's lazy COM init, so initialize explicitly.
        OleInitialize(IntPtr.Zero);
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try { item(); }
            catch { /* each item reports its own failures */ }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
