using System.Buffers;

namespace Egami;

/// <summary>Says whether new pixel memory is cleared.</summary>
public enum ClearMode
{
    /// <summary>Leave whatever the memory held. Use this when every pixel will be written anyway.</summary>
    None,

    /// <summary>Set every byte to zero.</summary>
    Zero,
}

/// <summary>
/// Supplies the memory behind every <see cref="PixelPlane{TPixel}"/>. Implement it to control where pixel memory comes from.
/// Implementations must be thread-safe.
/// </summary>
public interface IPixelMemorySource
{
    /// <summary>
    /// Gets the largest block, in bytes, the source hands out in one piece. Planes bigger than this are split into
    /// several blocks, each holding whole rows. A single row larger than this still gets one block of its own.
    /// </summary>
    int MaxBlockBytes { get; }

    /// <summary>Allocates one contiguous block of memory.</summary>
    /// <param name="byteLength">The size of the block in bytes. The returned memory has exactly this length.</param>
    /// <param name="clear">Whether to zero the block.</param>
    /// <returns>The block. Disposing it returns the memory to the source.</returns>
    IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear);
}
