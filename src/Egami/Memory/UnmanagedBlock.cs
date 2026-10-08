using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami;

/// <summary>A block of unmanaged memory aligned to 64 bytes.</summary>
/// <remarks>
/// The memory is owned by an <see cref="AlignedMemoryHandle"/>. Disposing the block hands the memory back to its
/// <see cref="UnmanagedBlockCache"/> for reuse; if the block is never disposed, the handle's finalizer frees it, so a forgotten
/// plane leaks only until the next garbage collection.
/// </remarks>
internal sealed unsafe class UnmanagedBlock : MemoryManager<byte>
{
    private readonly int _length;
    private readonly AlignedMemoryHandle _handle;
    private readonly UnmanagedBlockCache _cache;
    private int _disposed;

    internal UnmanagedBlock(int length, ClearMode clear, UnmanagedBlockCache cache)
    {
        _length = length;
        _cache = cache;
        _handle = cache.Rent(length);
        if (clear == ClearMode.Zero)
        {
            NativeMemory.Clear((void*)_handle.DangerousGetHandle(), (nuint)length);
        }
    }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new Span<byte>((void*)_handle.DangerousGetHandle(), _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new MemoryHandle((byte*)_handle.DangerousGetHandle() + elementIndex, default, this);
    }

    public override void Unpin()
    {
        // Unmanaged memory never moves.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cache.Return(_handle);
        }
    }
}

/// <summary>
/// Keeps unmanaged blocks after use so later allocations of a similar size reuse them. Fresh memory from the operating system
/// is slow to touch the first time: reusing it made decoding a 1920×1080 PNG about twice as fast.
/// </summary>
/// <remarks>
/// Blocks are kept in power-of-two size classes, so a block serves any request between half its size and its size.
/// At most <c>maxRetainedBytes</c> are kept; anything beyond that is freed at once.
/// </remarks>
internal sealed class UnmanagedBlockCache(long maxRetainedBytes)
{
    /// <summary>The largest size class kept, 1 GB. Bigger blocks are always freed after use.</summary>
    private const int LargestClass = 30;

    private readonly ConcurrentBag<AlignedMemoryHandle>[] _classes = [.. Enumerable.Range(0, LargestClass + 1).Select(_ => new ConcurrentBag<AlignedMemoryHandle>())];
    private long _retainedBytes;
    private long _liveBytes;

    /// <summary>Gets the bytes kept for reuse.</summary>
    internal long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    /// <summary>Gets the bytes allocated through this cache and not yet freed, including those kept for reuse. Used by tests to find leaks.</summary>
    internal long LiveBytes => Interlocked.Read(ref _liveBytes);

    /// <summary>Returns a block of at least <paramref name="length"/> bytes, reusing a kept one when possible.</summary>
    internal AlignedMemoryHandle Rent(int length)
    {
        int sizeClass = SizeClass(length);
        if (sizeClass <= LargestClass && _classes[sizeClass].TryTake(out AlignedMemoryHandle? kept))
        {
            Interlocked.Add(ref _retainedBytes, -kept.Capacity);
            return kept;
        }

        long capacity = sizeClass <= LargestClass ? 1L << sizeClass : length;
        Interlocked.Add(ref _liveBytes, capacity);
        return AlignedMemoryHandle.Allocate((int)capacity, this);
    }

    /// <summary>Keeps a block for reuse, or frees it when the cache is full or the block is too large to keep.</summary>
    internal void Return(AlignedMemoryHandle handle)
    {
        int sizeClass = SizeClass((int)handle.Capacity);
        if (sizeClass <= LargestClass && 1L << sizeClass == handle.Capacity && Interlocked.Add(ref _retainedBytes, handle.Capacity) <= maxRetainedBytes)
        {
            _classes[sizeClass].Add(handle);
            return;
        }

        if (sizeClass <= LargestClass && 1L << sizeClass == handle.Capacity)
        {
            Interlocked.Add(ref _retainedBytes, -handle.Capacity);
        }

        handle.Dispose();
    }

    /// <summary>Frees every kept block.</summary>
    internal void Trim()
    {
        foreach (ConcurrentBag<AlignedMemoryHandle> sizeClass in _classes)
        {
            while (sizeClass.TryTake(out AlignedMemoryHandle? handle))
            {
                Interlocked.Add(ref _retainedBytes, -handle.Capacity);
                handle.Dispose();
            }
        }
    }

    internal void Released(long capacity)
    {
        Interlocked.Add(ref _liveBytes, -capacity);
    }

    /// <summary>Returns the power of two at or above <paramref name="length"/>, as an exponent.</summary>
    private static int SizeClass(int length)
    {
        return length <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)length - 1);
    }
}

/// <summary>Owns one 64-byte-aligned unmanaged allocation and frees it on dispose or, failing that, on finalization.</summary>
/// <remarks>
/// It deliberately does not call <see cref="GC.AddMemoryPressure"/>: reporting a large image's memory made the runtime run a full
/// (gen 2) collection on nearly every load. Rasters are meant to be disposed; the finalizer is only a safety net.
/// </remarks>
internal sealed unsafe class AlignedMemoryHandle : SafeHandle
{
    private const int Alignment = 64;

    private readonly UnmanagedBlockCache _owner;

    private AlignedMemoryHandle(int capacity, UnmanagedBlockCache owner)
        : base(0, ownsHandle: true)
    {
        Capacity = capacity;
        _owner = owner;
    }

    public override bool IsInvalid => handle == 0;

    /// <summary>Gets the size of the allocation in bytes.</summary>
    internal long Capacity { get; }

    internal static AlignedMemoryHandle Allocate(int capacity, UnmanagedBlockCache owner)
    {
        AlignedMemoryHandle result = new(capacity, owner);
        result.SetHandle((nint)NativeMemory.AlignedAlloc((nuint)Math.Max(capacity, 1), Alignment));
        return result;
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.AlignedFree((void*)handle);
        _owner.Released(Capacity);
        return true;
    }
}
