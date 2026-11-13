using System.Text;

namespace ExplorerPlus.Indexing;

public enum SearchStatus : byte
{
    Ok = 0,

    /// <summary>The scope isn't on an indexed volume (network share, FAT drive): use another search.</summary>
    NotIndexed = 1,

    BadRequest = 2,
}

public abstract record IndexRequest;

public sealed record StatusRequest : IndexRequest;

public sealed record SearchRequest(string Query, string? Scope, int Limit, bool IncludeHidden, bool IncludeProtected) : IndexRequest;

public sealed record FolderSizesRequest(List<string> Paths) : IndexRequest;

// Size -1 and Modified 0 mean unknown (sizes still being read)
public sealed record SearchHit(string Path, uint Attributes, long Size = -1, long Modified = 0)
{
    public bool IsFolder => (Attributes & VolumeIndex.DirectoryAttribute) != 0;
}

/// <param name="Total">Matches before the access check and the limit.</param>
/// <param name="Scanning">Some volume is still being read, so results may be missing.</param>
public sealed record SearchResponse(SearchStatus Status, int Total, List<SearchHit> Hits, bool Scanning);

public sealed record VolumeStatus(string Root, VolumeState State, int Count, string? Error, bool HasSizes = false);

public sealed record StatusResponse(string Version, List<VolumeStatus> Volumes);

/// <summary>
/// Messages between Explorer+ and the index service on <see cref="PipeName"/>: each is an Int32 length followed by
/// that many bytes. A request starts with the protocol version and a kind.
/// </summary>
public static class IndexProtocol
{
    public const string PipeName = "ExplorerPlus.Index";
    public const int ProtocolVersion = 2;
    public const int MaxFolderSizes = 10_000;
    public const int MaxMessage = 64 << 20;
    public const int MaxQueryLength = 2048;

    public static string Version => typeof(IndexProtocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const byte KindSearch = 1, KindStatus = 2, KindFolderSizes = 3;

    public static byte[] EncodeSearch(SearchRequest request) => Encode(w =>
    {
        w.Write(ProtocolVersion);
        w.Write(KindSearch);
        w.Write(request.Query);
        w.Write(request.Scope ?? "");
        w.Write(request.Limit);
        w.Write(request.IncludeHidden);
        w.Write(request.IncludeProtected);
    });

    public static byte[] EncodeStatusRequest() => Encode(w =>
    {
        w.Write(ProtocolVersion);
        w.Write(KindStatus);
    });

    public static byte[] EncodeFolderSizes(IReadOnlyList<string> paths) => Encode(w =>
    {
        w.Write(ProtocolVersion);
        w.Write(KindFolderSizes);
        w.Write(paths.Count);
        foreach (var path in paths) w.Write(path);
    });

    // throws for anything malformed
    public static IndexRequest DecodeRequest(byte[] message)
    {
        using var r = Reader(message);
        if (r.ReadInt32() != ProtocolVersion) throw new InvalidDataException("Unsupported protocol version.");
        var kind = r.ReadByte();
        if (kind == KindStatus) return new StatusRequest();
        if (kind == KindFolderSizes)
        {
            var count = r.ReadInt32();
            if (count < 0 || count > MaxFolderSizes) throw new InvalidDataException("Too many folders.");
            var paths = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var path = r.ReadString();
                if (path.Length > 32767) throw new InvalidDataException("Path too long.");
                paths.Add(path);
            }
            return new FolderSizesRequest(paths);
        }
        if (kind != KindSearch) throw new InvalidDataException("Unknown request.");
        var query = r.ReadString();
        var scope = r.ReadString();
        if (query.Length > MaxQueryLength || scope.Length > 32767) throw new InvalidDataException("Request too long.");
        return new SearchRequest(query, scope.Length == 0 ? null : scope, r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean());
    }

    public static byte[] EncodeSearchResponse(SearchResponse response) => Encode(w =>
    {
        w.Write((byte)response.Status);
        w.Write(response.Total);
        w.Write(response.Scanning);
        w.Write(response.Hits.Count);
        foreach (var hit in response.Hits)
        {
            w.Write(hit.Path);
            w.Write(hit.Attributes);
            w.Write(hit.Size);
            w.Write(hit.Modified);
        }
    });

    public static SearchResponse DecodeSearchResponse(byte[] message)
    {
        using var r = Reader(message);
        var status = (SearchStatus)r.ReadByte();
        var total = r.ReadInt32();
        var scanning = r.ReadBoolean();
        var count = r.ReadInt32();
        var hits = new List<SearchHit>(Math.Min(count, 100_000));
        for (var i = 0; i < count; i++) hits.Add(new SearchHit(r.ReadString(), r.ReadUInt32(), r.ReadInt64(), r.ReadInt64()));
        return new SearchResponse(status, total, hits, scanning);
    }

    public static byte[] EncodeStatusResponse(StatusResponse response) => Encode(w =>
    {
        w.Write(response.Version);
        w.Write(response.Volumes.Count);
        foreach (var v in response.Volumes)
        {
            w.Write(v.Root);
            w.Write((byte)v.State);
            w.Write(v.Count);
            w.Write(v.Error ?? "");
            w.Write(v.HasSizes);
        }
    });

    public static byte[] EncodeFolderSizesResponse(long[] sizes) => Encode(w =>
    {
        w.Write(sizes.Length);
        foreach (var size in sizes) w.Write(size);
    });

    public static long[] DecodeFolderSizesResponse(byte[] message)
    {
        using var r = Reader(message);
        var count = r.ReadInt32();
        if (count < 0 || count > MaxFolderSizes) throw new InvalidDataException("Bad folder sizes.");
        var sizes = new long[count];
        for (var i = 0; i < count; i++) sizes[i] = r.ReadInt64();
        return sizes;
    }

    public static StatusResponse DecodeStatusResponse(byte[] message)
    {
        using var r = Reader(message);
        var version = r.ReadString();
        var count = r.ReadInt32();
        var volumes = new List<VolumeStatus>();
        for (var i = 0; i < count; i++)
        {
            var root = r.ReadString();
            var state = (VolumeState)r.ReadByte();
            var items = r.ReadInt32();
            var error = r.ReadString();
            volumes.Add(new VolumeStatus(root, state, items, error.Length == 0 ? null : error, r.ReadBoolean()));
        }
        return new StatusResponse(version, volumes);
    }

    public static async Task WriteMessageAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var frame = new byte[4 + message.Length];
        BitConverter.TryWriteBytes(frame, message.Length);
        message.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxMessage) throw new InvalidDataException("Bad message length.");
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, ct).ConfigureAwait(false);
        return message;
    }

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static BinaryReader Reader(byte[] message) => new(new MemoryStream(message, writable: false), Encoding.UTF8);
}
