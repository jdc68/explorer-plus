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

/// <summary>What changed since <paramref name="Since"/> (a FILETIME), newest first; answered with a <see cref="SearchResponse"/>.</summary>
public sealed record RecentRequest(string? Scope, long Since, int Limit, bool IncludeHidden, bool IncludeProtected, bool IncludeAppData) : IndexRequest;

/// <summary>Every file below <paramref name="Scope"/> in path order, optionally only some extensions (no dot); answered with a <see cref="SearchResponse"/>.</summary>
public sealed record FilesRequest(string Scope, List<string>? Extensions, int Limit, bool IncludeHidden, bool IncludeProtected) : IndexRequest;

/// <summary>
/// The largest files, files not touched since <paramref name="Before"/> (a FILETIME) of at least
/// <paramref name="MinSize"/> bytes, or empty folders; answered with a <see cref="SearchResponse"/>.
/// </summary>
public sealed record CleanupRequest(string? Scope, CleanupKind Kind, long Before, long MinSize, int Limit, bool IncludeSystem) : IndexRequest;

// Size -1 and Modified 0 mean unknown (sizes still being read)
public sealed record SearchHit(string Path, uint Attributes, long Size = -1, long Modified = 0, long Created = 0)
{
    public bool IsFolder => (Attributes & VolumeIndex.DirectoryAttribute) != 0;
}

/// <param name="Total">Matches before the access check and the limit.</param>
/// <param name="Scanning">Some volume is still being read, so results may be missing.</param>
public sealed record SearchResponse(SearchStatus Status, int Total, List<SearchHit> Hits, bool Scanning);

public sealed record VolumeStatus(string Root, VolumeState State, int Count, string? Error, bool HasSizes = false);

/// <param name="Protocol">The requests the service understands; 2 for a service from before this field.</param>
public sealed record StatusResponse(string Version, List<VolumeStatus> Volumes, int Protocol = IndexProtocol.ProtocolVersion)
{
    /// <summary>Understands search, recent changes and folder sizes as this app sends them.</summary>
    public bool Speaks => Protocol >= IndexProtocol.RequestVersion;

    public bool HasFiles => Protocol >= IndexProtocol.FilesVersion;

    public bool HasCleanup => Protocol >= IndexProtocol.CleanupVersion;
}

/// <summary>
/// Messages between Explorer+ and the index service on <see cref="PipeName"/>: each is an Int32 length followed by
/// that many bytes. A request starts with the protocol version and a kind.
/// </summary>
public static class IndexProtocol
{
    public const string PipeName = "ExplorerPlus.Index";
    public const int ProtocolVersion = 5;

    // requests a version-3 service also takes are still sent as 3, so it keeps serving them until it is updated
    public const int RequestVersion = 3;
    public const int FilesVersion = 4;
    public const int CleanupVersion = 5;
    public const int MaxExtensions = 1000;

    // status requests keep the first version's number, so an older service still says which version it is
    private const int StatusVersion = 2;
    public const int MaxFolderSizes = 10_000;
    public const int MaxMessage = 64 << 20;
    public const int MaxQueryLength = 2048;

    public static string Version => typeof(IndexProtocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const byte KindSearch = 1, KindStatus = 2, KindFolderSizes = 3, KindRecent = 4, KindFiles = 5, KindCleanup = 6;

    public static byte[] EncodeCleanup(CleanupRequest request) => Encode(w =>
    {
        w.Write(CleanupVersion);
        w.Write(KindCleanup);
        w.Write(request.Scope ?? "");
        w.Write((byte)request.Kind);
        w.Write(request.Before);
        w.Write(request.MinSize);
        w.Write(request.Limit);
        w.Write(request.IncludeSystem);
    });

    public static byte[] EncodeSearch(SearchRequest request) => Encode(w =>
    {
        w.Write(RequestVersion);
        w.Write(KindSearch);
        w.Write(request.Query);
        w.Write(request.Scope ?? "");
        w.Write(request.Limit);
        w.Write(request.IncludeHidden);
        w.Write(request.IncludeProtected);
    });

    public static byte[] EncodeRecent(RecentRequest request) => Encode(w =>
    {
        w.Write(RequestVersion);
        w.Write(KindRecent);
        w.Write(request.Scope ?? "");
        w.Write(request.Since);
        w.Write(request.Limit);
        w.Write(request.IncludeHidden);
        w.Write(request.IncludeProtected);
        w.Write(request.IncludeAppData);
    });

    public static byte[] EncodeFiles(FilesRequest request) => Encode(w =>
    {
        w.Write(FilesVersion);
        w.Write(KindFiles);
        w.Write(request.Scope);
        w.Write(request.Extensions?.Count ?? -1);
        foreach (var ext in request.Extensions ?? []) w.Write(ext);
        w.Write(request.Limit);
        w.Write(request.IncludeHidden);
        w.Write(request.IncludeProtected);
    });

    public static byte[] EncodeStatusRequest() => Encode(w =>
    {
        w.Write(StatusVersion);
        w.Write(KindStatus);
    });

    public static byte[] EncodeFolderSizes(IReadOnlyList<string> paths) => Encode(w =>
    {
        w.Write(RequestVersion);
        w.Write(KindFolderSizes);
        w.Write(paths.Count);
        foreach (var path in paths) w.Write(path);
    });

    // throws for anything malformed
    public static IndexRequest DecodeRequest(byte[] message)
    {
        using var r = Reader(message);
        var version = r.ReadInt32();
        var kind = r.ReadByte();
        if (kind == KindStatus && version >= StatusVersion) return new StatusRequest();
        if (version < RequestVersion || version > ProtocolVersion || kind == KindFiles && version < FilesVersion ||
            kind == KindCleanup && version < CleanupVersion)
            throw new InvalidDataException("Unsupported protocol version.");
        if (kind == KindCleanup)
        {
            var scopePath = r.ReadString();
            if (scopePath.Length > 32767) throw new InvalidDataException("Request too long.");
            return new CleanupRequest(scopePath.Length == 0 ? null : scopePath, (CleanupKind)r.ReadByte(), r.ReadInt64(), r.ReadInt64(),
                r.ReadInt32(), r.ReadBoolean());
        }
        if (kind == KindFiles)
        {
            var scopePath = r.ReadString();
            if (scopePath.Length is 0 or > 32767) throw new InvalidDataException("Bad scope.");
            var count = r.ReadInt32();
            if (count < -1 || count > MaxExtensions) throw new InvalidDataException("Too many extensions.");
            List<string>? extensions = null;
            if (count >= 0)
            {
                extensions = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    var ext = r.ReadString();
                    if (ext.Length > 64) throw new InvalidDataException("Extension too long.");
                    extensions.Add(ext);
                }
            }
            return new FilesRequest(scopePath, extensions, r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean());
        }
        if (kind == KindRecent)
        {
            var scopePath = r.ReadString();
            if (scopePath.Length > 32767) throw new InvalidDataException("Request too long.");
            return new RecentRequest(scopePath.Length == 0 ? null : scopePath, r.ReadInt64(), r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean());
        }
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
            w.Write(hit.Created);
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
        for (var i = 0; i < count; i++) hits.Add(new SearchHit(r.ReadString(), r.ReadUInt32(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64()));

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
        w.Write(response.Protocol);
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
        var protocol = r.BaseStream.Position < r.BaseStream.Length ? r.ReadInt32() : StatusVersion;
        return new StatusResponse(version, volumes, protocol);

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
