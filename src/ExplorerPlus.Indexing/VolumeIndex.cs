using System.Buffers;

namespace ExplorerPlus.Indexing;

public readonly record struct IndexHit(string Path, uint Attributes, long Key)
{
    public bool IsFolder => (Attributes & VolumeIndex.DirectoryAttribute) != 0;
}

public sealed record IndexSearchOptions(bool IncludeHidden = true, bool IncludeProtected = false);

/// <summary>
/// Every file and folder name on one NTFS volume, kept in flat arrays indexed by MFT record number (the low 48 bits
/// of a file reference number): parent record, sequence number, attributes, and the name as a slice of one big
/// character pool. About 20 bytes plus the name per item, and no object per item. Searches take a read lock and
/// run in parallel; journal updates take the write lock.
/// </summary>
public sealed class VolumeIndex
{
    public const uint DirectoryAttribute = 0x10;
    private const uint Hidden = 0x2, System = 0x4;
    private const int Absent = -2, Top = -1;
    private const int MaxDepth = 512;
    private const int ChunkSize = 1 << 15;

    /// <summary>Records below this are NTFS metadata ($MFT, $LogFile, $Extend, ...), never listed.</summary>
    private const int FirstUserRecord = 16;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private int[] _parent;
    private ushort[] _sequence;
    private uint[] _attributes;
    private int[] _nameStart;
    private byte[] _nameLength;
    private char[] _names;
    private int _namesUsed;
    private int _namesWasted;
    private int _count;

    /// <param name="root">The drive's root path, such as <c>C:\</c>.</param>
    /// <param name="rootReference">File reference number of the root folder.</param>
    /// <param name="capacity">Expected number of MFT records (grows as needed).</param>
    public VolumeIndex(string root, ulong rootReference, uint serialNumber, int capacity = 1 << 16)
    {
        Root = root.EndsWith('\\') ? root : root + "\\";
        SerialNumber = serialNumber;
        RootRecord = Record(rootReference);
        if (RootRecord >= 1 << 30) throw new ArgumentOutOfRangeException(nameof(rootReference), $"Implausible root reference {rootReference:X}.");
        capacity = Math.Max(capacity, RootRecord + 1);
        _parent = new int[capacity];
        Array.Fill(_parent, Absent);
        _sequence = new ushort[capacity];
        _attributes = new uint[capacity];
        _nameStart = new int[capacity];
        _nameLength = new byte[capacity];
        _names = new char[Math.Max(1024, capacity * 12)];
        _parent[RootRecord] = Top;
        _sequence[RootRecord] = Sequence(rootReference);
        _attributes[RootRecord] = DirectoryAttribute;
    }

    public string Root { get; }
    public uint SerialNumber { get; }
    public int RootRecord { get; }

    /// <summary>Files and folders in the index (the root not counted).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Approximate memory the index holds, in bytes.</summary>
    public long MemoryBytes => (long)_parent.Length * 15 + (long)_names.Length * 2;

    public static int Record(ulong reference) => (int)Math.Min(reference & 0xFFFF_FFFF_FFFF, int.MaxValue);
    private static ushort Sequence(ulong reference) => (ushort)(reference >> 48);

    public void EnterWrite() => _lock.EnterWriteLock();

    public void ExitWrite()
    {
        if (_namesWasted > 1 << 20 && _namesWasted > _namesUsed / 3) CompactNames();
        _lock.ExitWriteLock();
    }

    /// <summary>Adds or updates an item. Call between <see cref="EnterWrite"/> and <see cref="ExitWrite"/>.</summary>
    public void Upsert(ulong reference, ulong parentReference, ReadOnlySpan<char> name, uint attributes)
    {
        var r = Record(reference);
        if (r < FirstUserRecord || r == RootRecord || r == int.MaxValue || name.Length == 0 || name.Length > 255) return;
        var parent = Record(parentReference);
        if (r >= _parent.Length) Grow(r + 1);
        var present = _parent[r] != Absent;
        if (present && name.SequenceEqual(NameOf(r)))
        {
            _parent[r] = parent;
            _sequence[r] = Sequence(reference);
            _attributes[r] = attributes;
            return;
        }
        if (present) _namesWasted += _nameLength[r];
        else _count++;
        _parent[r] = parent;
        _sequence[r] = Sequence(reference);
        _attributes[r] = attributes;
        _nameStart[r] = AppendName(name);
        _nameLength[r] = (byte)name.Length;
    }

    /// <summary>Removes an item, unless its record already holds a newer file. Call inside a write.</summary>
    public void Remove(ulong reference)
    {
        var r = Record(reference);
        if (r >= _parent.Length || r == RootRecord || _parent[r] == Absent || _sequence[r] != Sequence(reference)) return;
        _parent[r] = Absent;
        _namesWasted += _nameLength[r];
        _nameLength[r] = 0;
        _count--;
    }

    private ReadOnlySpan<char> NameOf(int r) => new(_names, _nameStart[r], _nameLength[r]);

    private int AppendName(ReadOnlySpan<char> name)
    {
        if (_namesUsed + name.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 3 / 2, _namesUsed + name.Length + 1024));
        var start = _namesUsed;
        name.CopyTo(_names.AsSpan(start));
        _namesUsed += name.Length;
        return start;
    }

    private void Grow(int needed)
    {
        var size = Math.Max(needed, (int)Math.Min(int.MaxValue, _parent.Length * 3L / 2));
        var old = _parent.Length;
        Array.Resize(ref _parent, size);
        _parent.AsSpan(old).Fill(Absent);
        Array.Resize(ref _sequence, size);
        Array.Resize(ref _attributes, size);
        Array.Resize(ref _nameStart, size);
        Array.Resize(ref _nameLength, size);
    }

    private void CompactNames()
    {
        var pool = new char[Math.Max(1024, (_namesUsed - _namesWasted) * 5 / 4)];
        var used = 0;
        for (var r = 0; r < _parent.Length; r++)
        {
            if (_parent[r] == Absent || _nameLength[r] == 0) continue;
            NameOf(r).CopyTo(pool.AsSpan(used));
            _nameStart[r] = used;
            used += _nameLength[r];
        }
        _names = pool;
        _namesUsed = used;
        _namesWasted = 0;
    }

    /// <summary>The record of a folder or file in this index, if it holds that exact file reference.</summary>
    public int? Find(ulong reference)
    {
        _lock.EnterReadLock();
        try
        {
            var r = Record(reference);
            return r < _parent.Length && _parent[r] != Absent && _sequence[r] == Sequence(reference) ? r : null;
        }
        finally { _lock.ExitReadLock(); }
    }

    public string? GetPath(int record)
    {
        _lock.EnterReadLock();
        try
        {
            var buffer = new char[MaxDepth * 64];
            var length = BuildPath(record, buffer);
            return length < 0 ? null : new string(buffer, 0, length);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Writes the full path of <paramref name="record"/> into <paramref name="buffer"/>; -1 when it can't be reached from the root.</summary>
    private int BuildPath(int record, char[] buffer)
    {
        if (record == RootRecord)
        {
            Root.CopyTo(buffer);
            return Root.Length;
        }
        Span<int> chain = stackalloc int[MaxDepth];
        var depth = 0;
        var length = Root.Length - 1; // "C:" without the trailing backslash; each part adds "\name"
        for (var r = record; r != RootRecord; r = _parent[r])
        {
            if (r < 0 || r >= _parent.Length || _parent[r] < 0 || depth == MaxDepth) return -1;
            chain[depth++] = r;
            length += 1 + _nameLength[r];
        }
        if (length > buffer.Length) return -1;
        Root.AsSpan(0, Root.Length - 1).CopyTo(buffer);
        var pos = Root.Length - 1;
        for (var i = depth - 1; i >= 0; i--)
        {
            buffer[pos++] = '\\';
            NameOf(chain[i]).CopyTo(buffer.AsSpan(pos));
            pos += _nameLength[chain[i]];
        }
        return pos;
    }

    /// <summary>
    /// Finds the items matching <paramref name="query"/> under <paramref name="scopeRecord"/> (or the whole volume),
    /// best first, at most <paramref name="limit"/> of them. <paramref name="canList"/> is asked once per parent
    /// folder (its record and path), so the caller can drop what the user isn't allowed to see. Returns the hits and the number of matches
    /// before that filter and the limit.
    /// </summary>
    public (List<IndexHit> Hits, int Total) Search(IndexQuery query, IndexSearchOptions options, int? scopeRecord, int limit,
        Func<int, string, bool>? canList = null, CancellationToken ct = default)
    {
        if (query.IsEmpty) return ([], 0);
        _lock.EnterReadLock();
        var state = ArrayPool<byte>.Shared.Rent(_parent.Length);
        try
        {
            var scope = scopeRecord ?? RootRecord;
            Array.Clear(state, 0, _parent.Length);
            state[scope] = InScope;
            var parts = new List<List<long>>();
            var chunks = (_parent.Length + ChunkSize - 1) / ChunkSize;
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct },
                () => new Worker(),
                (chunk, _, worker) =>
                {
                    var end = Math.Min(_parent.Length, (chunk + 1) * ChunkSize);
                    for (var r = chunk * ChunkSize; r < end; r++) Test(r, scope, query, options, state, worker);
                    return worker;
                },
                worker => { lock (parts) parts.Add(worker.Keys); });

            var total = parts.Sum(p => p.Count);
            var keys = new long[total];
            var at = 0;
            foreach (var part in parts)
            {
                part.CopyTo(keys, at);
                at += part.Count;
            }
            Array.Sort(keys);

            var hits = new List<IndexHit>(Math.Min(limit, total));
            var allowed = new Dictionary<int, bool>();
            var buffer = new char[MaxDepth * 64];
            foreach (var key in keys)
            {
                if (hits.Count >= limit) break;
                ct.ThrowIfCancellationRequested();
                var r = (int)(key & 0x7FFF_FFFF);
                var length = BuildPath(r, buffer);
                if (length < 0) continue;
                var parent = _parent[r];
                if (canList is not null)
                {
                    if (!allowed.TryGetValue(parent, out var ok))
                    {
                        var cut = Math.Max(Root.Length, Array.LastIndexOf(buffer, '\\', length - 1));
                        allowed[parent] = ok = canList(parent, new string(buffer, 0, cut));
                    }
                    if (!ok) continue;
                }
                hits.Add(new IndexHit(new string(buffer, 0, length), _attributes[r], key));
            }
            return (hits, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(state);
            _lock.ExitReadLock();
        }
    }

    private const byte InScope = 1, OutOfScope = 2;

    private sealed class Worker
    {
        public readonly List<long> Keys = [];
        public readonly char[] Path = new char[MaxDepth * 64];
        public readonly int[] Stack = new int[MaxDepth];
    }

    private void Test(int r, int scope, IndexQuery query, IndexSearchOptions options, byte[] state, Worker worker)
    {
        var parent = _parent[r];
        if (parent < 0 || r == scope) return;
        var attributes = _attributes[r];
        if (!Visible(attributes, options)) return;
        if (!query.MatchesName(NameOf(r), out var rank)) return;
        if (Place(parent, state, options, worker.Stack) != InScope) return;
        if (query.HasPathTerms)
        {
            var length = BuildPath(r, worker.Path);
            if (length < 0 || !query.MatchesPath(worker.Path.AsSpan(0, length))) return;
        }
        // Best first: rank, then folders before files, then shorter names.
        var isFile = (attributes & DirectoryAttribute) == 0 ? 1L : 0L;
        worker.Keys.Add(((long)rank << 56) | (isFile << 48) | ((long)_nameLength[r] << 32) | (uint)r);
    }

    private static bool Visible(uint attributes, IndexSearchOptions options)
    {
        if (!options.IncludeHidden && (attributes & Hidden) != 0) return false;
        if (!options.IncludeProtected && (attributes & (Hidden | System)) == (Hidden | System)) return false;
        return true;
    }

    /// <summary>
    /// Whether a folder lies inside the scope and below no hidden or excluded folder, remembered per folder for the
    /// rest of the search (threads may race on a slot, but they always write the same answer).
    /// </summary>
    private byte Place(int folder, byte[] state, IndexSearchOptions options, int[] stack)
    {
        var depth = 0;
        var d = folder;
        byte result;
        while (true)
        {
            if (d < 0 || d >= _parent.Length) { result = OutOfScope; break; }
            var known = Volatile.Read(ref state[d]);
            if (known != 0) { result = known; break; }
            if (d == RootRecord || _parent[d] < 0 || depth == stack.Length || !Visible(_attributes[d], options) || IsExcludedFolder(d))
            {
                result = OutOfScope;
                stack[depth < stack.Length ? depth++ : depth - 1] = d;
                break;
            }
            stack[depth++] = d;
            d = _parent[d];
        }
        for (var i = 0; i < depth; i++) Volatile.Write(ref state[stack[i]], result);
        return result;
    }

    /// <summary>The Recycle Bin and System Volume Information, whose contents are never useful search results.</summary>
    private bool IsExcludedFolder(int d) =>
        _parent[d] == RootRecord &&
        (NameOf(d).Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
         NameOf(d).Equals("System Volume Information", StringComparison.OrdinalIgnoreCase));
}
