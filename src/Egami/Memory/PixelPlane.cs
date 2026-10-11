using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// A width × height grid of pixels, stored row by row. A row is always one contiguous span, but the plane as a whole
/// may be split into several blocks of whole rows. Planes belong to the raster or operation that hands them out, which
/// returns their memory; user code reads and writes them but never frees them.
/// </summary>
/// <remarks>
/// Spans returned by <see cref="Row"/> and <see cref="TryGetContiguous"/> point into the plane's memory. Do not keep them after the
/// raster is disposed, or after an operation that changes its size, which gives the frame a new plane.
/// </remarks>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-plane"/>
/// </example>
public sealed class PixelPlane<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly int _rowsPerBlock;
    private readonly int _stride;
    private readonly Memory<TPixel> _wrapped;
    private readonly Memory<byte> _wrappedBytes;
    private readonly int _byteStride;
    private readonly PixelPlane<TPixel>? _parent;
    private readonly int _x;
    private readonly int _y;
    private IDisposable? _owner;
    private readonly Memory<byte>[]? _views;
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

    /// <summary>A view of a rectangle of <paramref name="parent"/>: its rows are slices of the parent's, so nothing is copied.</summary>
    internal PixelPlane(PixelPlane<TPixel> parent, Rect area)
    {
        Width = area.Width;
        Height = area.Height;
        _rowsPerBlock = area.Height;
        _parent = parent;
        _x = area.X;
        _y = area.Y;
    }

    internal PixelPlane(Memory<byte> memory, int width, int height, int byteStride, IDisposable? owner)
    {
        Width = width;
        Height = height;
        _byteStride = byteStride;
        _rowsPerBlock = height;
        _wrappedBytes = memory;
        _owner = owner;
    }

    /// <summary>A view of blocks of whole rows that something else owns, such as a <see cref="YuvPlane{TSample}"/>.</summary>
    internal PixelPlane(int width, int height, int rowsPerBlock, Memory<byte>[] views)
    {
        Width = width;
        Height = height;
        _stride = width;
        _rowsPerBlock = rowsPerBlock;
        _views = views;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets a value indicating whether the plane owns its memory and frees it on dispose.</summary>
    internal bool OwnsMemory => _wrapped.IsEmpty && _wrappedBytes.IsEmpty && _parent is null && _views is null;

    /// <summary>Gets the plane this one is a view of, or this plane.</summary>
    internal PixelPlane<TPixel> Root => _parent?.Root ?? this;

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

        // Planes from a memory source (every decoder's and operation's) take this path: one check, no branch for the other kinds.
        IMemoryOwner<byte>[]? blocks = _blocks;
        if (blocks is not null)
        {
            if (blocks.Length == 1)
            {
                return MemoryMarshal.Cast<byte, TPixel>(blocks[0].Memory.Span).Slice(y * Width, Width);
            }

            int block = y / _rowsPerBlock;
            return MemoryMarshal.Cast<byte, TPixel>(blocks[block].Memory.Span).Slice((y - (block * _rowsPerBlock)) * Width, Width);
        }

        return OtherRow(y);
    }

    /// <summary>Gets all pixels as one span, if the plane is one block with no gap between rows.</summary>
    /// <param name="pixels">All <see cref="Width"/> × <see cref="Height"/> pixels, row by row, or an empty span.</param>
    /// <returns><see langword="true"/> if the pixels are contiguous.</returns>
    /// <exception cref="ObjectDisposedException">The plane has been disposed.</exception>
    public bool TryGetContiguous(out Span<TPixel> pixels)
    {
        if (_parent is not null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pixels = default;
            return false;
        }

        if (!OwnsMemory)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_views is not null)
            {
                pixels = _views.Length == 1 ? MemoryMarshal.Cast<byte, TPixel>(_views[0].Span)[..(Width * Height)] : default;
                return _views.Length == 1;
            }

            if (!_wrappedBytes.IsEmpty)
            {
                int rowBytes = Width * Unsafe.SizeOf<TPixel>();
                bool packed = _byteStride == rowBytes || Height == 1;
                pixels = packed ? MemoryMarshal.Cast<byte, TPixel>(_wrappedBytes.Span[..(rowBytes * Height)]) : default;
                return packed;
            }

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
    internal void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _owner, null)?.Dispose();
        IMemoryOwner<byte>[]? blocks = Interlocked.Exchange(ref _blocks, null);
        if (blocks is not null)
        {
            foreach (IMemoryOwner<byte> block in blocks)
            {
                block.Dispose();
            }
        }
    }

    /// <summary>Gets a row of a wrapped plane, a view, or a block view, or throws for a disposed plane.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<TPixel> OtherRow(int y)
    {
        ObjectDisposedException.ThrowIf(_disposed || OwnsMemory, this);
        if (_parent is not null)
        {
            return _parent.Row(_y + y).Slice(_x, Width);
        }

        if (_views is not null)
        {
            int view = y / _rowsPerBlock;
            return MemoryMarshal.Cast<byte, TPixel>(_views[view].Span).Slice((y - (view * _rowsPerBlock)) * Width, Width);
        }

        return _wrappedBytes.IsEmpty
            ? _wrapped.Span.Slice(y * _stride, Width)
            : MemoryMarshal.Cast<byte, TPixel>(_wrappedBytes.Span.Slice(y * _byteStride, Width * Unsafe.SizeOf<TPixel>()));
    }

    private void ThrowRowOutOfRange(int y)
    {
        throw new ArgumentOutOfRangeException(nameof(y), y, $"The row must be between 0 and {Height - 1}.");
    }
}

/// <summary>Creates <see cref="PixelPlane{TPixel}"/> instances.</summary>
internal static class PixelPlane
{
    /// <summary>Allocates a plane from <paramref name="source"/>.</summary>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="source">Where the memory comes from.</param>
    /// <param name="clear">Whether to zero the pixels. Skip it when every pixel will be written anyway.</param>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <returns>The new plane. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1, or a row is larger than 2 GB.</exception>
    internal static PixelPlane<TPixel> Allocate<TPixel>(int width, int height, IPixelMemorySource source, ClearMode clear = ClearMode.None)
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
    internal static PixelPlane<TPixel> Wrap<TPixel>(Memory<TPixel> memory, int width, int height, int stride = 0)
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

    /// <summary>Creates a plane over bytes with any row stride, such as a decoder's frame, optionally owning what holds them.</summary>
    internal static PixelPlane<TPixel> Wrap<TPixel>(Memory<byte> memory, int width, int height, int byteStride, IDisposable? owner)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long rowBytes = (long)width * Unsafe.SizeOf<TPixel>();
        if (byteStride == 0)
        {
            byteStride = (int)Math.Min(rowBytes, int.MaxValue);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(byteStride, rowBytes, nameof(byteStride));
        long needed = ((long)(height - 1) * byteStride) + rowBytes;
        if (memory.Length < needed)
        {
            throw new ArgumentOutOfRangeException(nameof(memory), memory.Length, $"The memory holds {memory.Length} bytes but a {width}×{height} plane with a {byteStride}-byte stride needs {needed}.");
        }

        return new PixelPlane<TPixel>(memory, width, height, byteStride, owner);
    }
}
