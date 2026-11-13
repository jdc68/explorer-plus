using System.Runtime.InteropServices;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class Pidl : IEquatable<Pidl>
{
    private readonly byte[] _bytes;

    private Pidl(byte[] bytes) => _bytes = bytes;

    public static Pidl Desktop { get; } = new([0, 0]);

    public bool IsDesktop => _bytes.Length <= 2;

    public int Length => _bytes.Length;

    public static Pidl FromNative(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) throw new ArgumentNullException(nameof(pidl));
        var size = (int)Shell32.ILGetSize(pidl);
        var bytes = new byte[size];
        Marshal.Copy(pidl, bytes, 0, size);
        return new Pidl(bytes);
    }

    /// <summary>Copies the native PIDL and frees it.</summary>
    public static Pidl TakeNative(IntPtr pidl)
    {
        try { return FromNative(pidl); }
        finally { Shell32.ILFree(pidl); }
    }

    public NativePidl ToNative()
    {
        var ptr = Marshal.AllocCoTaskMem(_bytes.Length);
        Marshal.Copy(_bytes, 0, ptr, _bytes.Length);
        return new NativePidl(ptr);
    }

    public Pidl? GetParent()
    {
        if (IsDesktop) return null;
        using var native = ToNative();
        Shell32.ILRemoveLastID(native.Ptr);
        return FromNative(native.Ptr);
    }

    /// <summary>Combines this folder PIDL with a child PIDL relative to it.</summary>
    public Pidl Combine(IntPtr relativeChild)
    {
        using var native = ToNative();
        return TakeNative(Shell32.ILCombine(native.Ptr, relativeChild));
    }

    public bool Equals(Pidl? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is Pidl p && Equals(p);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public static Pidl FromArray(byte[] bytes) => new((byte[])bytes.Clone());

    public string ToBase64() => Convert.ToBase64String(_bytes);

    public static Pidl FromBase64(string s) => new(Convert.FromBase64String(s));
}

public readonly struct NativePidl(IntPtr ptr) : IDisposable
{
    public IntPtr Ptr { get; } = ptr;

    public void Dispose()
    {
        if (Ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(Ptr);
    }
}
