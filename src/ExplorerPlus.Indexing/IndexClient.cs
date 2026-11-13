using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Indexing;

public enum IndexServiceState { NotInstalled, Stopped, Starting, Running, Unknown }

/// <summary>Explorer+'s side of the index service: whether it is installed and running, and queries over its pipe.</summary>
public static class IndexClient
{
    public const string ServiceName = "ExplorerPlusIndex";
    public const string ServiceDisplayName = "Explorer+ Index";
    public const string ServiceExe = "ExplorerPlus.IndexService.exe";

    /// <summary>Where the service runs from: under Program Files, which only administrators can change.</summary>
    public static string InstallFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Explorer+ Index");

    /// <summary>The service's state, from the service manager (needs no rights beyond a normal user's).</summary>
    public static IndexServiceState QueryService()
    {
        var manager = ServiceNative.OpenSCManagerW(null, null, ServiceNative.SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) return IndexServiceState.Unknown;
        try
        {
            var service = ServiceNative.OpenServiceW(manager, ServiceName, ServiceNative.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ServiceNative.ERROR_SERVICE_DOES_NOT_EXIST ? IndexServiceState.NotInstalled : IndexServiceState.Unknown;
            try
            {
                if (!ServiceNative.QueryServiceStatus(service, out var status)) return IndexServiceState.Unknown;
                return status.CurrentState switch
                {
                    ServiceNative.SERVICE_RUNNING => IndexServiceState.Running,
                    ServiceNative.SERVICE_START_PENDING => IndexServiceState.Starting,
                    _ => IndexServiceState.Stopped,
                };
            }
            finally { ServiceNative.CloseServiceHandle(service); }
        }
        finally { ServiceNative.CloseServiceHandle(manager); }
    }

    /// <summary>Searches through the service; null when it can't be reached.</summary>
    public static async Task<SearchResponse?> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var reply = await ExchangeAsync(IndexProtocol.EncodeSearch(request), ct).ConfigureAwait(false);
        return reply is null ? null : IndexProtocol.DecodeSearchResponse(reply);
    }

    public static async Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        var reply = await ExchangeAsync(IndexProtocol.EncodeStatusRequest(), ct).ConfigureAwait(false);
        return reply is null ? null : IndexProtocol.DecodeStatusResponse(reply);
    }

    public static async Task<long[]?> GetFolderSizesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var reply = await ExchangeAsync(IndexProtocol.EncodeFolderSizes(paths), ct).ConfigureAwait(false);
        if (reply is null) return null;
        var sizes = IndexProtocol.DecodeFolderSizesResponse(reply);
        return sizes.Length == paths.Count ? sizes : null;
    }

    private static async Task<byte[]?> ExchangeAsync(byte[] request, CancellationToken ct)
    {
        // Impersonation lets the service check folders with this user's rights, so results only show what we may see.
        await using var pipe = new NamedPipeClientStream(".", IndexProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync(1000, ct).ConfigureAwait(false);
            await IndexProtocol.WriteMessageAsync(pipe, request, ct).ConfigureAwait(false);
            return await IndexProtocol.ReadMessageAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
