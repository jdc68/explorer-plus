using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class FolderSizeService
{
    // Sizes of folders up to this many levels below a computed folder are cached as a by-product,
    // so opening a subfolder right after its parent shows its children's sizes immediately.
    private const int CacheDepth = 2;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    // A walk runs this long before the next queued one gets a turn, so a huge folder can't hold up small ones
    internal TimeSpan Slice { get; set; } = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentDictionary<string, (long Size, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Request> _queue = new();
    private readonly ConcurrentDictionary<string, Request> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.OrdinalIgnoreCase);

    public static FolderSizeService Instance { get; } = new();

    /// <summary>Raised (on the thread that called <see cref="Invalidate"/>) with the folder whose size changed.</summary>
    public event Action<string>? Invalidated;

    /// <summary>A folder that ends up with no size, and why (once per folder).</summary>
    public event Action<string>? Log;

    internal FolderSizeService()
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

    /// <summary>
    /// Queues a folder for measuring; null when it can't be listed. Callers asking for the same folder share one walk,
    /// which stops only once every one of them has cancelled.
    /// </summary>
    public Task<long?> GetAsync(string path, CancellationToken ct)
    {
        path = Normalize(path);
        if (TryGetCached(path, out var cached)) return Task.FromResult<long?>(cached);
        while (true)
        {
            if (_inFlight.TryGetValue(path, out var existing))
            {
                if (existing.TryJoin(ct)) return existing.Completion.Task;
                _inFlight.TryRemove(KeyValuePair.Create(path, existing));
                continue;
            }
            var request = new Request(path, ct);
            if (_inFlight.TryAdd(path, request))
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
                if (r.TryAbandon()) Finish(r, null);
                else if (r.Stack.Count == 0 && TryGetCached(r.Path, out var cached)) Finish(r, cached);
                else if (Step(r)) Finish(r, r.Result);
                else _queue.Add(r); // back of the line
            }
            catch (Exception ex)
            {
                Report(r.Path, ex.Message);
                Finish(r, null);
            }
        }
    }

    private void Finish(Request r, long? size)
    {
        r.Completion.TrySetResult(size);
        _inFlight.TryRemove(KeyValuePair.Create(r.Path, r));
    }

    // Walks depth first until the folder is done (true) or its slice is up while others wait (false)
    private bool Step(Request r)
    {
        var clock = Stopwatch.StartNew();
        if (r.Stack.Count == 0) r.Stack.Push(new Frame(r.Path, 0));
        for (var steps = 1; ; steps++)
        {
            var f = r.Stack.Peek();
            if (f.Subdirs is null)
            {
                if (!List(f) && f.Depth == 0)
                {
                    r.Result = null;
                    return true;
                }
            }
            else if (f.Next < f.Subdirs.Count)
            {
                var child = (f.Path.EndsWith('\\') ? f.Path : f.Path + "\\") + f.Subdirs[f.Next++];
                if (TryGetCached(child, out var known)) f.Total += known;
                else r.Stack.Push(new Frame(child, f.Depth + 1));
            }
            else
            {
                r.Stack.Pop();
                if (f.Depth <= CacheDepth) _cache[Normalize(f.Path)] = (f.Total, DateTime.UtcNow);
                if (r.Stack.Count == 0)
                {
                    r.Result = f.Total;
                    return true;
                }
                r.Stack.Peek().Total += f.Total;
            }

            if (steps % 16 != 0) continue;
            if (r.TryAbandon())
            {
                r.Result = null;
                return true;
            }
            if (clock.Elapsed >= Slice && _queue.Count > 0) return false;
        }
    }

    // Adds the files' sizes and collects the subfolders; false when the folder can't be listed
    private unsafe bool List(Frame f)
    {
        f.Subdirs = [];
        var handle = Kernel32.FindFirstFileExW(FastFolderEnumerator.ToLongPath(f.Path.TrimEnd('\\')) + @"\*", Kernel32.FindExInfoBasic,
            out var data, Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FIND_FIRST_EX_LARGE_FETCH);
        if (handle == Kernel32.INVALID_HANDLE_VALUE)
        {
            // below the top, count what we can, like Explorer
            if (f.Depth == 0) Report(f.Path, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return false;
        }
        try
        {
            do
            {
                var attrs = data.dwFileAttributes;
                if ((attrs & FileAttr.DIRECTORY) != 0)
                {
                    // Junctions and symlinks point elsewhere: following them double-counts or loops.
                    if ((attrs & FileAttr.REPARSE_POINT) != 0) continue;
                    var name = new string(data.cFileName); // a local's fixed buffer is already pinned
                    if (name is "." or "..") continue;
                    f.Subdirs.Add(name);
                }
                else f.Total += ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
            }
            while (Kernel32.FindNextFileW(handle, out data));
        }
        finally { Kernel32.FindClose(handle); }
        return true;
    }

    private void Report(string path, string why)
    {
        if (_logged.TryAdd(path, 0)) Log?.Invoke($"{path}: {why}");
    }

    private sealed class Frame(string path, int depth)
    {
        public string Path { get; } = path;
        public int Depth { get; } = depth;
        public long Total;
        public List<string>? Subdirs;
        public int Next;
    }

    private sealed class Request(string path, CancellationToken token)
    {
        private readonly List<CancellationToken> _tokens = [token];
        private bool _closed;

        public string Path { get; } = path;
        public TaskCompletionSource<long?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stack<Frame> Stack { get; } = new();
        public long? Result;

        public bool TryJoin(CancellationToken ct)
        {
            lock (_tokens)
            {
                if (_closed || Completion.Task.IsCompleted) return false;
                _tokens.Add(ct);
                return true;
            }
        }

        // true once every caller has cancelled; nobody can join after that
        public bool TryAbandon()
        {
            lock (_tokens)
            {
                if (_closed) return true;
                foreach (var t in _tokens)
                    if (!t.IsCancellationRequested) return false;
                return _closed = true;
            }
        }
    }

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
}
