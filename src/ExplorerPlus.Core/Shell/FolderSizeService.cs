using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class FolderSizeService
{
    // Sizes of folders up to this many levels below a computed folder are cached as a by-product,
    // so opening a subfolder right after its parent shows its children's sizes immediately.
    private const int CacheDepth = 2;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (long Size, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Request> _queue = new();
    private readonly ConcurrentDictionary<string, Request> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public static FolderSizeService Instance { get; } = new();

    /// <summary>Raised (on the thread that called <see cref="Invalidate"/>) with the folder whose size changed.</summary>
    public event Action<string>? Invalidated;

    private FolderSizeService()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "FolderSizes", Priority = ThreadPriority.Lowest };
        t.Start();
    }

    public bool TryGetCached(string path, out long size)
    {
        if (_cache.TryGetValue(Normalize(path), out var e) && DateTime.UtcNow - e.At < MaxAge)
        {
            size = e.Size;
            return true;
        }
        size = 0;
        return false;
    }

    /// <summary>Queues a folder for measuring. Requests are served in order; cancelled ones are skipped.</summary>
    public Task<long?> GetAsync(string path, CancellationToken ct)
    {
        path = Normalize(path);
        if (TryGetCached(path, out var cached)) return Task.FromResult<long?>(cached);
        while (true)
        {
            // A request cancelled by an earlier navigation must not answer for this one.
            if (_inFlight.TryGetValue(path, out var existing) && !existing.Token.IsCancellationRequested) return existing.Completion.Task;
            var request = new Request(path, ct);
            if (existing is null ? _inFlight.TryAdd(path, request) : _inFlight.TryUpdate(path, request, existing))
            {
                _queue.Add(request);
                return request.Completion.Task;
            }
        }
    }

    /// <summary>Something inside <paramref name="path"/> changed: forget it and every ancestor's size.</summary>
    public void Invalidate(string path, bool includeDescendants = false)
    {
        path = Normalize(path);
        if (includeDescendants)
        {
            var prefix = path + "\\";
            foreach (var key in _cache.Keys)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _cache.TryRemove(key, out _);
        }
        var removedAny = false;
        for (var p = path; p is not null; p = Parent(p))
            removedAny |= _cache.TryRemove(p, out _);
        if (removedAny) Invalidated?.Invoke(path);
    }

    private static string? Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i > 2 ? path[..i] : i == 2 && path.Length > 3 ? path[..3] : null;
    }

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    public static bool IsMeasurable(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':') return false;
        var type = Kernel32.GetDriveTypeW(path[..3]);
        return type is 2 or 3 or 6; // removable, fixed, RAM disk (never network or optical)
    }

    private void Worker()
    {
        // Background mode lowers both CPU and I/O priority, so measuring never competes with what the user does.
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
        foreach (var r in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (r.Token.IsCancellationRequested) { r.Completion.TrySetResult(null); continue; }
                if (TryGetCached(r.Path, out var cached)) { r.Completion.TrySetResult(cached); continue; }
                var size = Measure(r.Path, 0, r.Token);
                r.Completion.TrySetResult(size);
            }
            catch (OperationCanceledException) { r.Completion.TrySetResult(null); }
            catch { r.Completion.TrySetResult(null); }
            finally { _inFlight.TryRemove(KeyValuePair.Create(r.Path, r)); }
        }
    }

    private unsafe long Measure(string dir, int depth, CancellationToken ct)
    {
        if (depth > 0 && TryGetCached(dir, out var known)) return known;

        long total = 0;
        var handle = Kernel32.FindFirstFileExW(FastFolderEnumerator.ToLongPath(dir.TrimEnd('\\')) + @"\*", Kernel32.FindExInfoBasic,
            out var data, Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FIND_FIRST_EX_LARGE_FETCH);
        if (handle == Kernel32.INVALID_HANDLE_VALUE) return 0; // access denied etc.: count what we can, like Explorer
        var subdirs = new List<string>();
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var attrs = data.dwFileAttributes;
                if ((attrs & FileAttr.DIRECTORY) != 0)
                {
                    // Junctions and symlinks point elsewhere: following them double-counts or loops.
                    if ((attrs & FileAttr.REPARSE_POINT) != 0) continue;
                    var name = new string(data.cFileName); // a local's fixed buffer is already pinned
                    if (name is "." or "..") continue;
                    subdirs.Add(name);
                }
                else total += ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
            }
            while (Kernel32.FindNextFileW(handle, out data));
        }
        finally { Kernel32.FindClose(handle); }

        var prefix = dir.EndsWith('\\') ? dir : dir + "\\";
        foreach (var name in subdirs) total += Measure(prefix + name, depth + 1, ct);

        if (depth <= CacheDepth) _cache[Normalize(dir)] = (total, DateTime.UtcNow);
        return total;
    }

    private sealed class Request(string path, CancellationToken token)
    {
        public string Path { get; } = path;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<long?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
}
