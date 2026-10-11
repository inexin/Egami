using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// One plane of a <see cref="YuvRaster{TSample}"/>: a width × height grid of raw samples, stored row by row. A row is always one
/// contiguous span. Samples are stored low-bit aligned: a 10-bit sample is 0 to 1023 in a <see cref="ushort"/>.
/// </summary>
/// <remarks>
/// Spans returned by <see cref="Row"/> and <see cref="TryGetContiguous"/> point into the raster's memory. Do not keep them after the
/// raster is disposed.
/// </remarks>
/// <typeparam name="TSample">The sample type: <see cref="byte"/> for 8 bits, <see cref="ushort"/> for 10 and 12 bits.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="planes"/>
/// </example>
public sealed class YuvPlane<TSample>
    where TSample : unmanaged
{
    private readonly Memory<byte>[]? _blocks;
    private readonly Memory<TSample> _wrapped;
    private readonly int _rowsPerBlock;
    private readonly int _stride;
    private readonly YuvRaster _owner;

    internal YuvPlane(YuvRaster owner, int width, int height, Memory<byte>[] blocks, int rowsPerBlock)
    {
        _owner = owner;
        Width = width;
        Height = height;
        _blocks = blocks;
        _rowsPerBlock = rowsPerBlock;
        _stride = width;
    }

    internal YuvPlane(YuvRaster owner, int width, int height, Memory<TSample> memory, int stride)
    {
        _owner = owner;
        Width = width;
        Height = height;
        _wrapped = memory;
        _rowsPerBlock = height;
        _stride = stride;
    }

    /// <summary>Gets the width in samples.</summary>
    public int Width { get; }

    /// <summary>Gets the height in samples.</summary>
    public int Height { get; }

    /// <summary>Gets the distance between the starts of consecutive rows, in samples.</summary>
    internal int Stride => _stride;

    /// <summary>Returns one row of samples.</summary>
    /// <param name="y">The row index, 0 to <see cref="Height"/> - 1.</param>
    /// <returns>The row, exactly <see cref="Width"/> samples long.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the plane.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Span<TSample> Row(int y)
    {
        if ((uint)y >= (uint)Height)
        {
            ThrowRowOutOfRange(y);
        }

        _owner.ThrowIfDisposed();
        if (_blocks is null)
        {
            return _wrapped.Span.Slice(y * _stride, Width);
        }

        int block = y / _rowsPerBlock;
        return MemoryMarshal.Cast<byte, TSample>(_blocks[block].Span).Slice((y - (block * _rowsPerBlock)) * _stride, Width);
    }

    /// <summary>Gets all samples as one span, if the plane is one block with no gap between rows.</summary>
    /// <param name="samples">All <see cref="Width"/> × <see cref="Height"/> samples, row by row, or an empty span.</param>
    /// <returns><see langword="true"/> if the samples are contiguous.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public bool TryGetContiguous(out Span<TSample> samples)
    {
        _owner.ThrowIfDisposed();
        bool contiguous = (_blocks is null || _blocks.Length == 1) && (_stride == Width || Height == 1);
        samples = !contiguous ? default
            : _blocks is null ? _wrapped.Span[..(Width * Height)]
            : MemoryMarshal.Cast<byte, TSample>(_blocks[0].Span)[..(Width * Height)];
        return contiguous;
    }

    /// <summary>
    /// Returns a view of an 8-bit plane as <see cref="Gray8"/> pixels, without copying, so gray operations can read and write it.
    /// The view shares this plane's memory and is valid only as long as the raster.
    /// </summary>
    /// <returns>The view.</returns>
    /// <exception cref="NotSupportedException">The samples are not 8-bit.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public PixelPlane<Gray8> AsGray8()
    {
        if (typeof(TSample) != typeof(byte))
        {
            throw new NotSupportedException("Only an 8-bit plane can be viewed as Gray8.");
        }

        _owner.ThrowIfDisposed();
        if (_blocks is null)
        {
            Memory<byte> bytes = Unsafe.As<Memory<TSample>, Memory<byte>>(ref Unsafe.AsRef(in _wrapped));
            return new PixelPlane<Gray8>(bytes, Width, Height, _stride, owner: null);
        }

        return new PixelPlane<Gray8>(Width, Height, _rowsPerBlock, _blocks);
    }

    private void ThrowRowOutOfRange(int y)
    {
        throw new ArgumentOutOfRangeException(nameof(y), y, $"The row must be between 0 and {Height - 1}.");
    }
}
