using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// A width × height grid of pixels, stored row by row. A row is always one contiguous span, but the plane as a whole
/// may be split into several blocks of whole rows. A plane that allocated its memory must be disposed.
/// </summary>
/// <remarks>
/// Spans returned by <see cref="Row"/> and <see cref="TryGetContiguous"/> point into the plane's memory.
/// Do not use them after the plane is disposed.
/// </remarks>
/// <typeparam name="TPixel">The pixel type.</typeparam>
public sealed class PixelPlane<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly int _rowsPerBlock;
    private readonly int _stride;
    private readonly Memory<TPixel> _wrapped;
    private IMemoryOwner<byte>[]? _blocks;
    private bool _disposed;

    internal PixelPlane(int width, int height, int rowsPerBlock, IMemoryOwner<byte>[] blocks)
    {
        Width = width;
        Height = height;
        _stride = width;
        _rowsPerBlock = rowsPerBlock;
        _blocks = blocks;
    }

    internal PixelPlane(Memory<TPixel> memory, int width, int height, int stride)
    {
        Width = width;
        Height = height;
        _stride = stride;
        _rowsPerBlock = height;
        _wrapped = memory;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets a value indicating whether the plane owns its memory and frees it on dispose.</summary>
    public bool OwnsMemory => _wrapped.IsEmpty;

    /// <summary>Returns one row of pixels.</summary>
    /// <param name="y">The row index, 0 to <see cref="Height"/> - 1.</param>
    /// <returns>The row, exactly <see cref="Width"/> pixels long.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the plane.</exception>
    /// <exception cref="ObjectDisposedException">The plane has been disposed.</exception>
    public Span<TPixel> Row(int y)
    {
        if ((uint)y >= (uint)Height)
        {
            ThrowRowOutOfRange(y);
        }

        if (!OwnsMemory)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _wrapped.Span.Slice(y * _stride, Width);
        }

        IMemoryOwner<byte>[] blocks = _blocks ?? throw new ObjectDisposedException(GetType().FullName);
        int block = y / _rowsPerBlock;
        int row = y - (block * _rowsPerBlock);
        return MemoryMarshal.Cast<byte, TPixel>(blocks[block].Memory.Span).Slice(row * Width, Width);
    }

    /// <summary>Gets all pixels as one span, if the plane is one block with no gap between rows.</summary>
    /// <param name="pixels">All <see cref="Width"/> × <see cref="Height"/> pixels, row by row, or an empty span.</param>
    /// <returns><see langword="true"/> if the pixels are contiguous.</returns>
    /// <exception cref="ObjectDisposedException">The plane has been disposed.</exception>
    public bool TryGetContiguous(out Span<TPixel> pixels)
    {
        if (!OwnsMemory)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool contiguous = _stride == Width || Height == 1;
            pixels = contiguous ? _wrapped.Span[..(Width * Height)] : default;
            return contiguous;
        }

        IMemoryOwner<byte>[] blocks = _blocks ?? throw new ObjectDisposedException(GetType().FullName);
        if (blocks.Length == 1)
        {
            pixels = MemoryMarshal.Cast<byte, TPixel>(blocks[0].Memory.Span);
            return true;
        }

        pixels = default;
        return false;
    }

    /// <summary>Returns the plane's memory to its source. Wrapped memory is left alone. Calling it again does nothing.</summary>
    public void Dispose()
    {
        _disposed = true;
        IMemoryOwner<byte>[]? blocks = Interlocked.Exchange(ref _blocks, null);
        if (blocks is not null)
        {
            foreach (IMemoryOwner<byte> block in blocks)
            {
                block.Dispose();
            }
        }
    }

    private void ThrowRowOutOfRange(int y)
    {
        throw new ArgumentOutOfRangeException(nameof(y), y, $"The row must be between 0 and {Height - 1}.");
    }
}

/// <summary>Creates <see cref="PixelPlane{TPixel}"/> instances.</summary>
public static class PixelPlane
{
    /// <summary>Allocates a plane from <paramref name="source"/>.</summary>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="source">Where the memory comes from.</param>
    /// <param name="clear">Whether to zero the pixels. Skip it when every pixel will be written anyway.</param>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <returns>The new plane. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1, or a row is larger than 2 GB.</exception>
    public static PixelPlane<TPixel> Allocate<TPixel>(int width, int height, IPixelMemorySource source, ClearMode clear = ClearMode.None)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(source);

        long rowBytes = (long)width * Unsafe.SizeOf<TPixel>();
        if (rowBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A row of this many pixels is larger than 2 GB.");
        }

        int rowsPerBlock = (int)Math.Clamp(source.MaxBlockBytes / rowBytes, 1, height);
        int blockCount = (int)(((long)height + rowsPerBlock - 1) / rowsPerBlock);
        IMemoryOwner<byte>[] blocks = new IMemoryOwner<byte>[blockCount];
        try
        {
            for (int i = 0; i < blockCount; i++)
            {
                int rows = Math.Min(rowsPerBlock, height - (i * rowsPerBlock));
                blocks[i] = source.Allocate((int)(rows * rowBytes), clear);
                if (blocks[i].Memory.Length != rows * rowBytes)
                {
                    throw new InvalidOperationException("The memory source returned a block of the wrong length.");
                }
            }
        }
        catch
        {
            foreach (IMemoryOwner<byte>? block in blocks)
            {
                block?.Dispose();
            }

            throw;
        }

        return new PixelPlane<TPixel>(width, height, rowsPerBlock, blocks);
    }

    /// <summary>
    /// Creates a plane over memory the caller owns. The plane never frees it, and the caller must keep it alive and unchanged in size
    /// while the plane is used.
    /// </summary>
    /// <param name="memory">The pixels, row by row.</param>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="stride">The distance between the starts of consecutive rows, in pixels. Defaults to <paramref name="width"/>.</param>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <returns>The plane.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range, or <paramref name="memory"/> is too short.</exception>
    public static PixelPlane<TPixel> Wrap<TPixel>(Memory<TPixel> memory, int width, int height, int stride = 0)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (stride == 0)
        {
            stride = width;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        long needed = ((long)(height - 1) * stride) + width;
        if (memory.Length < needed)
        {
            throw new ArgumentOutOfRangeException(nameof(memory), memory.Length, $"The memory holds {memory.Length} pixels but a {width}×{height} plane with stride {stride} needs {needed}.");
        }

        return new PixelPlane<TPixel>(memory, width, height, stride);
    }
}
