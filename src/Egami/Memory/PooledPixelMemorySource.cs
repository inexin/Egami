using System.Buffers;

namespace Egami;

/// <summary>
/// The default <see cref="IPixelMemorySource"/>. Small blocks are arrays rented from a shared pool; larger blocks are
/// unmanaged memory aligned to 64 bytes, which stays off the large object heap and suits SIMD.
/// Planes larger than <see cref="MaxBlockBytes"/> are split into several blocks of whole rows.
/// </summary>
public sealed class PooledPixelMemorySource : IPixelMemorySource
{
    private readonly int _poolThresholdBytes = 1024 * 1024;
    private readonly int _maxBlockBytes = 64 * 1024 * 1024;

    /// <summary>Gets the shared instance with the default thresholds.</summary>
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

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLength"/> is negative.</exception>
    public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        return byteLength <= _poolThresholdBytes
            ? new PooledBlock(byteLength, clear)
            : new UnmanagedBlock(byteLength, clear);
    }
}
