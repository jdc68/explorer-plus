using System.IO;
using System.IO.Pipes;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = $"ExplorerPlus-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}{(AppPaths.Profile is { } p ? "-" + p : "")}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsPrimary { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Id, out var created);
        IsPrimary = created;
    }

    public bool TrySendToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            User32.AllowSetForegroundWindow(User32.ASFW_ANY);
            // NUL-separated: no argument can contain one, and no serializer has to warm up in this short-lived process.
            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\0', args));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>After asking the running instance to quit: waits until it has exited and released the lock.</summary>
    public bool WaitForPrimaryExit(TimeSpan timeout)
    {
        try
        {
            if (!_mutex.WaitOne(timeout)) return false;
        }
        catch (AbandonedMutexException) { /* it exited without releasing: we own the mutex now */ }
        _mutex.ReleaseMutex();
        return true;
    }

    public void Listen(Action<string[]> onArgs)
    {
        var token = _cts.Token;
        new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server);
                    var text = reader.ReadToEnd();
                    var args = text.Length == 0 ? [] : text.Split('\0');
                    onArgs(args);
                }
                catch (OperationCanceledException) { return; }
                catch { Thread.Sleep(100); }
            }
        }) { IsBackground = true, Name = "SingleInstancePipe" }.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
