using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.Services;

public static class ThumbnailService
{
    private const int MaxQueued = 500;
    private const long CacheBudgetBytes = 192L * 1024 * 1024;

    private sealed record Job(string Key, string? Path, Pidl? Pidl, int Size, Action<ImageSource?> Done, Action Dropped);

    // Last in, first out: what the user scrolled to most recently is what they're looking at.
    private static readonly LinkedList<Job> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly Dictionary<(string, int), LinkedListNode<(string Key, int Size, ImageSource Image, long Bytes)>> Cache = [];
    private static readonly LinkedList<(string Key, int Size, ImageSource Image, long Bytes)> Lru = new();
    private static long _cacheBytes;
    private static Dispatcher? _dispatcher;

    public static void Start(int threads = 3)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        for (var i = 0; i < threads; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = $"Thumbnails-{i}", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public static string KeyOf(string? path, Pidl? pidl) => path ?? pidl?.ToBase64() ?? "";

    public static ImageSource? TryGet(string key, int size)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((key, size), out var node)) return null;
            Lru.Remove(node);
            Lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>Queues a thumbnail; <paramref name="done"/> runs on the UI thread. <paramref name="dropped"/> runs if the
    /// request was pushed out of the queue by newer ones (the item is probably no longer visible).</summary>
    public static void Request(string? path, Pidl? pidl, int size, Action<ImageSource?> done, Action dropped)
    {
        var key = KeyOf(path, pidl);
        if (TryGet(key, size) is { } cached) { done(cached); return; }
        Job? evicted = null;
        lock (Queue)
        {
            Queue.AddLast(new Job(key, path, pidl, size, done, dropped));
            if (Queue.Count > MaxQueued)
            {
                evicted = Queue.First!.Value;
                Queue.RemoveFirst();
            }
        }
        if (evicted is null) Signal.Release();
        else evicted.Dropped();
    }

    /// <summary>The file changed: drop its cached thumbnails.</summary>
    public static void Invalidate(string key)
    {
        lock (Cache)
        {
            foreach (var k in Cache.Keys.Where(k => k.Item1 == key).ToList())
            {
                var node = Cache[k];
                _cacheBytes -= node.Value.Bytes;
                Lru.Remove(node);
                Cache.Remove(k);
            }
        }
    }

    private static void Worker()
    {
        OleInitialize(IntPtr.Zero);
        while (true)
        {
            Signal.Wait();
            Job? job;
            lock (Queue)
            {
                if (Queue.Count == 0) continue;
                job = Queue.Last!.Value;
                Queue.RemoveLast();
            }

            ImageSource? image = TryGet(job.Key, job.Size);
            if (image is null)
            {
                try
                {
                    var raw = ThumbnailExtractor.Get(job.Path, job.Pidl, job.Size);
                    if (raw is not null)
                    {
                        var format = raw.HasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
                        var bmp = BitmapSource.Create(raw.Width, raw.Height, 96, 96, format, null, raw.Pixels, raw.Width * 4);
                        bmp.Freeze();
                        image = bmp;
                        Add(job.Key, job.Size, bmp, raw.Pixels.Length);
                    }
                }
                catch { /* no thumbnail: the item keeps its icon */ }
            }
            var result = image;
            _dispatcher?.BeginInvoke(DispatcherPriority.Background, () => job.Done(result));
        }
    }

    private static void Add(string key, int size, ImageSource image, long bytes)
    {
        lock (Cache)
        {
            if (Cache.ContainsKey((key, size))) return;
            var node = Lru.AddFirst((key, size, image, bytes));
            Cache[(key, size)] = node;
            _cacheBytes += bytes;
            while (_cacheBytes > CacheBudgetBytes && Lru.Last is { } last)
            {
                _cacheBytes -= last.Value.Bytes;
                Cache.Remove((last.Value.Key, last.Value.Size));
                Lru.RemoveLast();
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);
}
