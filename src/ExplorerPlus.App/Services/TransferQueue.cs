using System.Collections.ObjectModel;
using ExplorerPlus.App.Infrastructure;

namespace ExplorerPlus.App.Services;

public enum TransferState { Queued, Running }

public sealed class TransferJob : ObservableObject
{
    private TransferState _state;

    internal TransferJob(string title, string detail, Func<Task> work)
    {
        Title = title;
        Detail = detail;
        Work = work;
    }

    public string Title { get; }

    /// <summary>Where the items go, for the transfers list.</summary>
    public string Detail { get; }

    internal Func<Task> Work { get; }

    internal TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TransferState State
    {
        get => _state;
        internal set
        {
            if (Set(ref _state, value)) OnPropertyChanged(nameof(IsQueued));
        }
    }

    public bool IsQueued => _state == TransferState.Queued;
}

/// <summary>
/// Runs copies and moves (paste, drops between Explorer+ panes and windows, copy or move to the other pane) one after
/// another instead of all at once, so they don't fight over the same disks. Each job still shows Windows' own progress
/// and conflict dialogs while it runs. Queued jobs can be cancelled or started right away. App-wide and UI-thread only.
/// </summary>
public static class TransferQueue
{
    public static ObservableCollection<TransferJob> Jobs { get; } = [];

    public static event Action? Changed;

    public static int RunningCount => Jobs.Count(j => j.State == TransferState.Running);

    public static string Summary
    {
        get
        {
            var running = RunningCount;
            var queued = Jobs.Count - running;
            if (Jobs.Count == 0) return "";
            var text = running == 1 ? "1 transfer running" : $"{running} transfers running";
            return queued == 0 ? text : $"{text}, {queued} queued";
        }
    }

    /// <summary>
    /// Adds a job and returns once it has run (true) or was cancelled before it started (false). With queueing turned
    /// off in Settings, or when nothing else runs, the job starts at once.
    /// </summary>
    public static Task<bool> Enqueue(string title, string detail, Func<Task> work)
    {
        var job = new TransferJob(title, detail, work);
        Jobs.Add(job);
        Changed?.Invoke();
        if (!AppSettings.Current.QueueTransfers || RunningCount == 0) Start(job);
        return job.Done.Task;
    }

    /// <summary>Starts a queued job now, next to whatever already runs.</summary>
    public static void RunNow(TransferJob job)
    {
        if (job.IsQueued && Jobs.Contains(job)) Start(job);
    }

    public static void Cancel(TransferJob job)
    {
        if (!job.IsQueued || !Jobs.Remove(job)) return;
        job.Done.TrySetResult(false);
        Changed?.Invoke();
    }

    private static async void Start(TransferJob job)
    {
        job.State = TransferState.Running;
        Changed?.Invoke();
        try { await job.Work(); }
        catch { /* the work reports its own errors */ }
        Jobs.Remove(job);
        job.Done.TrySetResult(true);
        Changed?.Invoke();
        if (RunningCount == 0 && Jobs.FirstOrDefault(j => j.IsQueued) is { } next) Start(next);
    }
}
