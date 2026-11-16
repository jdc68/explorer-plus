using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using ExplorerPlus.App.ViewModels;
using ExplorerPlus.App.Views;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

public static class PerfProbe
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("EXPLORERPLUS_PERF");
    private static readonly string Mode = Environment.GetEnvironmentVariable("EXPLORERPLUS_PERF_MODE") ?? "full";
    private static bool _attached;

    public static bool IsOn => Root is not null;

    public static void OnWindowLoaded(MainWindow window)
    {
        if (!IsOn || _attached) return;
        _attached = true;
        var rendered = new TaskCompletionSource<DateTime>();
        window.ContentRendered += (_, _) => rendered.TrySetResult(DateTime.Now);
        _ = RunAsync(window, rendered.Task);
    }

    private static async Task RunAsync(MainWindow window, Task<DateTime> rendered)
    {
        var result = new Dictionary<string, object?> { ["mode"] = Mode, ["machine"] = Environment.MachineName, ["cpus"] = Environment.ProcessorCount };
        try
        {
            await MeasureStartAsync(window, rendered, result);
            if (Mode == "full") await MeasureAllAsync(window, result);
            if (Mode == "filter") await MeasureFilterOnlyAsync(window, result);
            if (Mode == "menu") await MeasureMenusAsync(window, result, 15);
            if (Mode == "load") await MeasureLoadsAsync(window, result, 5);
        }
        catch (Exception ex) { result["error"] = ex.ToString(); }

        Directory.CreateDirectory(Root!);
        File.WriteAllText(Path.Combine(Root!, $"result-{Mode}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        App.ExitAll();
    }

    private static async Task MeasureStartAsync(MainWindow window, Task<DateTime> rendered, Dictionary<string, object?> result)
    {
        var started = Process.GetCurrentProcess().StartTime;
        var tab = window.PerfTab ?? await WithTimeout(FirstTab(window), 20);
        if (tab.Items.Count == 0) await WithTimeout(FirstRows(tab), 20);
        await Rendered();
        var rows = DateTime.Now;
        result["firstRowsUtc"] = rows.ToUniversalTime().ToString("O");
        result["startToFirstRowsMs"] = (rows - started).TotalMilliseconds;
        result["startToWindowMs"] = ((await WithTimeout(rendered, 20)) - started).TotalMilliseconds;
        await WithTimeout(Loaded(tab), 20);
        result["startToFullListMs"] = (DateTime.Now - started).TotalMilliseconds;
    }

    private static async Task MeasureAllAsync(MainWindow window, Dictionary<string, object?> result)
    {
        var vm = window.ViewModel;
        var tab = window.PerfTab!;
        var mid = await Resolve(Path.Combine(Root!, "10k"));

        // Memory first, before the big folders have been through the heap
        await Task.Delay(TimeSpan.FromSeconds(30));
        result["memory1Tab"] = Memory();
        await vm.OpenTabAsync(@"C:\Windows");
        await vm.OpenTabAsync(Environment.SystemDirectory);
        await Task.Delay(TimeSpan.FromSeconds(30));
        result["memory3Tabs"] = Memory();
        while (vm.Tabs.Count > 1) vm.CloseTab(vm.Tabs[^1]);
        tab = window.PerfTab!;

        await Navigate(tab, mid);
        await Task.Delay(2000);
        result["filter10k"] = await MeasureFilterAsync(window, tab);
        result["scroll10k"] = await window.MeasureScrollAsync();

        await MeasureMenusAsync(window, result, 6);

        await MeasureLoadsAsync(window, result, 3);
    }

    private static async Task MeasureLoadsAsync(MainWindow window, Dictionary<string, object?> result, int count)
    {
        var tab = window.PerfTab!;
        var big = await Resolve(Path.Combine(Root!, "100k"));
        var mid = await Resolve(Path.Combine(Root!, "10k"));
        var bigRuns = new List<object>();
        var midRuns = new List<object>();
        for (var i = 0; i < count; i++)
        {
            bigRuns.Add(await Navigate(tab, big));
            await Task.Delay(1500);
            midRuns.Add(await Navigate(tab, mid));
            await Task.Delay(1500);
        }
        result["open100k"] = bigRuns;
        result["open10k"] = midRuns;
    }

    private static async Task MeasureMenusAsync(MainWindow window, Dictionary<string, object?> result, int count)
    {
        var tab = window.PerfTab!;
        if (tab.Location?.FileSystemPath != Path.Combine(Root!, "10k")) await Navigate(tab, await Resolve(Path.Combine(Root!, "10k")));
        var item = tab.Items.FirstOrDefault(i => !i.IsFolder) ?? throw new InvalidOperationException("no file to open a menu on");
        var menus = new List<double?>();
        for (var i = 0; i < count; i++)
        {
            menus.Add(await window.MeasureItemMenuAsync(item));
            await Task.Delay(300);
        }
        result["itemMenuMs"] = menus;
    }

    // only keystrokes, for a profiler trace
    private static async Task MeasureFilterOnlyAsync(MainWindow window, Dictionary<string, object?> result)
    {
        var tab = window.PerfTab!;
        await Navigate(tab, await Resolve(Path.Combine(Root!, "10k")));
        await Task.Delay(2000);
        var samples = new List<object>();
        for (var i = 0; i < 6; i++) samples.AddRange(await MeasureFilterAsync(window, tab, pause: 100));
        result["filter10k"] = samples;
        result["scroll10k"] = await window.MeasureScrollAsync();
    }

    private static async Task<List<object>> MeasureFilterAsync(MainWindow window, TabViewModel tab, int pause = 500)
    {
        var samples = new List<object>();
        foreach (var text in new[] { "7", "q", ".", "3", "z" })
        {
            foreach (var typed in new[] { text, "" })
            {
                var changed = ItemsReset(tab);
                var watch = Stopwatch.StartNew();
                window.SetFilterBoxText(typed);
                await WithTimeout(changed, 5);
                await Rendered();
                var ms = watch.Elapsed.TotalMilliseconds;
                // cache rows and UI Automation updates come after the first frame
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                samples.Add(new { text = typed, ms, busyMs = watch.Elapsed.TotalMilliseconds, shown = tab.Items.Count });
                await Task.Delay(pause);
            }
        }
        return samples;
    }

    private static async Task<object> Navigate(TabViewModel tab, ShellLocation location)
    {
        using var lag = new UiLagMonitor(Dispatcher.CurrentDispatcher);
        var pause = GC.GetTotalPauseDuration();
        var gcs = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var first = FirstRows(tab);
        var watch = Stopwatch.StartNew();
        var load = tab.NavigateAsync(location);
        await WithTimeout(first, 30);
        await Rendered();
        var firstMs = watch.Elapsed.TotalMilliseconds;
        await WithTimeout(load, 60);
        await Rendered();
        return new
        {
            firstRowsMs = firstMs, fullListMs = watch.Elapsed.TotalMilliseconds, items = tab.Items.Count, maxUiLagMs = lag.MaxMs, uiLagsOver50ms = lag.Over50,
            gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
            gcs = $"{GC.CollectionCount(0) - gcs[0]}/{GC.CollectionCount(1) - gcs[1]}/{GC.CollectionCount(2) - gcs[2]}",
        };
    }

    private static async Task<ShellLocation> Resolve(string path) =>
        await ShellLocation.ResolveAsync(path) ?? throw new DirectoryNotFoundException(path);

    private static object Memory()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return new
        {
            workingSetMb = p.WorkingSet64 / 1048576.0,
            privateMb = p.PrivateMemorySize64 / 1048576.0,
            managedHeapMb = GC.GetTotalMemory(false) / 1048576.0,
        };
    }

    // layout runs at Render priority, so a Loaded-priority call comes after the frame is laid out
    private static Task Rendered() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task;

    private static Task<TabViewModel> FirstTab(MainWindow window)
    {
        var done = new TaskCompletionSource<TabViewModel>();
        PropertyChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (window.PerfTab is not { } tab) return;
            window.ViewModel.PropertyChanged -= handler;
            done.TrySetResult(tab);
        };
        window.ViewModel.PropertyChanged += handler;
        return done.Task;
    }

    private static Task FirstRows(TabViewModel tab)
    {
        var done = new TaskCompletionSource();
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (tab.Items.Count == 0) return;
            tab.Items.CollectionChanged -= handler;
            done.TrySetResult();
        };
        tab.Items.CollectionChanged += handler;
        return done.Task;
    }

    private static Task ItemsReset(TabViewModel tab)
    {
        var done = new TaskCompletionSource();
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            tab.Items.CollectionChanged -= handler;
            done.TrySetResult();
        };
        tab.Items.CollectionChanged += handler;
        return done.Task;
    }

    private static Task Loaded(TabViewModel tab)
    {
        if (!tab.IsLoading) return Task.CompletedTask;
        var done = new TaskCompletionSource();
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName != nameof(TabViewModel.IsLoading) || tab.IsLoading) return;
            tab.PropertyChanged -= handler;
            done.TrySetResult();
        };
        tab.PropertyChanged += handler;
        return done.Task;
    }

    private static async Task WithTimeout(Task task, int seconds)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task) throw new TimeoutException($"waited {seconds} s");
        await task;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds)
    {
        await WithTimeout((Task)task, seconds);
        return await task;
    }

    private sealed class UiLagMonitor : IDisposable
    {
        private volatile bool _stop;
        private long _waitingSince;
        private double _maxMs;

        // includes a wait still running, so a stall at the very end counts
        public double MaxMs => Math.Max(_maxMs, Interlocked.Read(ref _waitingSince) is var t and > 0 ? Stopwatch.GetElapsedTime(t).TotalMilliseconds : 0);
        public int Over50 { get; private set; }

        public UiLagMonitor(Dispatcher dispatcher)
        {
            new Thread(() =>
            {
                while (!_stop)
                {
                    var start = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _waitingSince, start);
                    dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input).Wait();
                    Interlocked.Exchange(ref _waitingSince, 0);
                    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (ms > _maxMs) _maxMs = ms;
                    if (ms > 50) Over50++;
                    Thread.Sleep(10);
                }
            }) { IsBackground = true }.Start();
        }

        // no Join: the thread may be waiting on this (UI) thread
        public void Dispose() => _stop = true;
    }
}
