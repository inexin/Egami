using System.Buffers;

namespace Egami;

/// <summary>
/// The default <see cref="IPixelMemorySource"/>. Small blocks are arrays rented from a shared pool; larger blocks are
/// unmanaged memory aligned to 64 bytes, which stays off the large object heap and suits SIMD.
/// Planes larger than <see cref="MaxBlockBytes"/> are split into several blocks of whole rows.
/// </summary>
/// <remarks>
/// Unmanaged blocks are kept after use, up to <see cref="MaxRetainedBytes"/>, and reused by later allocations of a similar size,
/// because memory fresh from the operating system is slow the first time it is written. <see cref="Trim"/> frees what is kept.
/// </remarks>
public sealed class PooledPixelMemorySource : IPixelMemorySource
{
    private readonly int _poolThresholdBytes = 1024 * 1024;
    private readonly int _maxBlockBytes = 64 * 1024 * 1024;
    private readonly long _maxRetainedBytes = 128L * 1024 * 1024;
    private UnmanagedBlockCache? _cache;

    /// <summary>Gets the shared instance with the default settings.</summary>
    public static PooledPixelMemorySource Shared { get; } = new();

    /// <summary>Gets the largest block, in bytes, served from the array pool. Larger blocks use unmanaged memory. Defaults to 1 MB.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int PoolThresholdBytes
    {
        get => _poolThresholdBytes;
        init => _poolThresholdBytes = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold must not be negative.");
    }

    /// <inheritdoc/>
    /// <remarks>Defaults to 64 MB.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxBlockBytes
    {
        get => _maxBlockBytes;
        init => _maxBlockBytes = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The block size must be at least 1 byte.");
    }

    /// <summary>
    /// Gets the most unmanaged memory, in bytes, kept after use for reuse. 0 frees every block as soon as it is disposed.
    /// Defaults to 128 MB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long MaxRetainedBytes
    {
        get => _maxRetainedBytes;
        init => _maxRetainedBytes = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must not be negative.");
    }

    /// <summary>Gets the bytes currently kept for reuse.</summary>
    public long RetainedBytes => _cache?.RetainedBytes ?? 0;

    /// <summary>Gets the cache, created on first use so that init-only settings are in place.</summary>
    internal UnmanagedBlockCache Cache => _cache ?? Interlocked.CompareExchange(ref _cache, new UnmanagedBlockCache(_maxRetainedBytes), null) ?? _cache!;

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLength"/> is negative.</exception>
    public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        return byteLength <= _poolThresholdBytes
            ? new PooledBlock(byteLength, clear)
            : new UnmanagedBlock(byteLength, clear, Cache);
    }

    /// <summary>Frees all unmanaged memory kept for reuse. Memory in use is not affected.</summary>
    public void Trim()
    {
        _cache?.Trim();
    }
}
