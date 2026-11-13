using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ExplorerPlus.Indexing;

namespace ExplorerPlus.IndexService;

/// <summary>
/// Serves searches on <c>\\.\pipe\ExplorerPlus.Index</c>. Any signed-in local user may connect (never over the
/// network), but each search runs impersonating the caller, so it only returns items in folders the caller may list.
/// </summary>
internal sealed class PipeServer(IndexHost host) : IDisposable
{
    private const int Listeners = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    public void Start()
    {
        // The first instance claims the name, so if another process got there first this fails instead of handing
        // it our users' searches. The other listeners start once the name is ours.
        var first = Create(PipeOptions.FirstPipeInstance);
        _loops.Add(Task.Run(() => ListenAsync(first, _cts.Token)));
        for (var i = 1; i < Listeners; i++) _loops.Add(Task.Run(() => ListenAsync(null, _cts.Token)));
    }

    private static NamedPipeServerStream Create(PipeOptions extra = 0) =>
        NamedPipeServerStreamAcl.Create(IndexProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | extra, 0, 0, Security());

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll([.. _loops], TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // ReadWrite leaves out CreateNewInstance, so no one else can open a server end of this pipe.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task ListenAsync(NamedPipeServerStream? pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pipe ??= Create();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write($"Pipe listener: {ex.Message}");
                pipe?.Dispose();
                pipe = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        pipe?.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken serviceToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        cts.CancelAfter(RequestTimeout);
        var ct = cts.Token;
        try
        {
            await using (pipe)
            {
                var request = IndexProtocol.DecodeRequest(await IndexProtocol.ReadMessageAsync(pipe, ct).ConfigureAwait(false));
                byte[] reply;
                if (request is null)
                {
                    reply = IndexProtocol.EncodeStatusResponse(host.Status());
                }
                else
                {
                    SearchResponse? response = null;
                    // Synchronous on this thread: the impersonation applies to this thread only.
                    pipe.RunAsClient(() => response = host.Search(request, ct));
                    reply = IndexProtocol.EncodeSearchResponse(response ?? new SearchResponse(SearchStatus.BadRequest, 0, [], false));
                }
                await IndexProtocol.WriteMessageAsync(pipe, reply, ct).ConfigureAwait(false);
                pipe.WaitForPipeDrain();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException) { }
        catch (Exception ex) { Log.Write($"Request failed: {ex}"); }
    }
}
