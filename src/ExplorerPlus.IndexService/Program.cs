using ExplorerPlus.Indexing;

namespace ExplorerPlus.IndexService;

public static class Program
{
    /// <summary>
    /// <c>--service</c>: started by the service manager. <c>--console</c>: the same work in a normal (elevated) process,
    /// until Enter or the process is ended, for debugging. <c>--install</c> / <c>--uninstall</c>: run elevated by
    /// Explorer+; exit code 0 on success, otherwise a Win32 error code.
    /// </summary>
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            switch (mode)
            {
                case "--service":
                    return ServiceRunner.Run();
                case "--console":
                    using (var stop = new ManualResetEventSlim())
                    {
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
                        using var worker = Worker.Start();
                        stop.Wait();
                    }
                    return 0;
                case "--install":
                    return ServiceInstaller.Install();
                case "--uninstall":
                    return ServiceInstaller.Uninstall();
                default:
                    return 87; // ERROR_INVALID_PARAMETER
            }
        }
        catch (Exception ex)
        {
            Log.Write($"{mode} failed: {ex}");
            return ex.HResult & 0xFFFF;
        }
    }
}

/// <summary>The running index: the volumes and the pipe that serves them.</summary>
internal sealed class Worker : IDisposable
{
    private readonly IndexHost _host = new();
    private readonly PipeServer _pipe;

    private Worker() => _pipe = new PipeServer(_host);

    public static Worker Start()
    {
        VolumeReader.Log = Log.Write;
        IndexHost.Log = Log.Write;
        var worker = new Worker();
        worker._host.Start();
        worker._pipe.Start();
        Log.Write($"Started, version {IndexProtocol.Version}");
        return worker;
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _host.Dispose();
        Log.Write("Stopped");
    }
}

internal static class Log
{
    private static readonly Lock Gate = new();

    // Next to the service under Program Files: a folder users can't write to, so nobody can plant a link where
    // LocalSystem writes.
    public static string Folder => IndexClient.InstallFolder;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "service.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
