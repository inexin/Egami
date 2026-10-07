using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami;

/// <summary>An image whose pixels are of type <typeparamref name="TPixel"/>. Pixel access is by row span.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
public sealed class Raster<TPixel> : Raster
    where TPixel : unmanaged, IPixelType<TPixel>
{
    // Below this many pixels, ForEachRow runs on the calling thread.
    private const int ParallelThreshold = 256 * 1024;

    private readonly List<Frame<TPixel>> _frames;
    private bool _disposed;

    internal Raster(PixelPlane<TPixel> plane, RasterMetadata metadata, EgamiContext context)
        : this([new Frame<TPixel>(plane, new FrameMetadata())], metadata, context)
    {
    }

    internal Raster(List<Frame<TPixel>> frames, RasterMetadata metadata, EgamiContext context)
        : base(frames[0].Plane.Width, frames[0].Plane.Height, metadata, context)
    {
        _frames = frames;
        Frames = new FrameList<TPixel>(frames);
    }

    /// <inheritdoc/>
    public override PixelLayoutInfo Layout => TPixel.Layout;

    /// <inheritdoc/>
    public override int FrameCount => _frames.Count;

    /// <summary>Gets the frames. <c>Frames[0]</c> always exists.</summary>
    public FrameList<TPixel> Frames { get; }

    /// <summary>Returns one row of the first frame. Do not use the span after the raster is disposed or resized.</summary>
    /// <param name="y">The row index, 0 to <see cref="Raster.Height"/> - 1.</param>
    /// <returns>The row, exactly <see cref="Raster.Width"/> pixels long.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the raster.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Span<TPixel> Row(int y)
    {
        ThrowIfDisposed();
        return _frames[0].Plane.Row(y);
    }

    /// <summary>Gets all pixels of the first frame as one span, if they are contiguous in memory.</summary>
    /// <param name="pixels">All pixels, row by row, or an empty span.</param>
    /// <returns><see langword="true"/> if the pixels are contiguous.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public bool TryGetContiguous(out Span<TPixel> pixels)
    {
        ThrowIfDisposed();
        return _frames[0].Plane.TryGetContiguous(out pixels);
    }

    /// <summary>
    /// Calls <paramref name="action"/> once for every row of the first frame. Large rasters are processed on several threads,
    /// up to the context's <see cref="EgamiContext.MaxParallelism"/>, so <paramref name="action"/> may run concurrently for different rows.
    /// </summary>
    /// <param name="action">The work to do on each row.</param>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void ForEachRow(RowAction<TPixel> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();
        PixelPlane<TPixel> plane = _frames[0].Plane;
        if (Context.MaxParallelism == 1 || (long)Width * Height < ParallelThreshold)
        {
            for (int y = 0; y < Height; y++)
            {
                action(plane.Row(y), y);
            }

            return;
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = Context.MaxParallelism };
        Parallel.For(0, Height, options, y => action(plane.Row(y), y));
    }

    /// <summary>Reads one pixel of the first frame. This is slow; use <see cref="Row"/> for bulk access.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the raster.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public TPixel GetPixel(int x, int y)
    {
        CheckColumn(x);
        return Row(y)[x];
    }

    /// <summary>Writes one pixel of the first frame. This is slow; use <see cref="Row"/> for bulk access.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <param name="value">The pixel.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the raster.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void SetPixel(int x, int y, TPixel value)
    {
        CheckColumn(x);
        Row(y)[x] = value;
    }

    /// <summary>Copies the first frame's pixels, row by row with no gaps, to <paramref name="destination"/>.</summary>
    /// <param name="destination">Where to copy. It must hold at least <see cref="Raster.Width"/> × <see cref="Raster.Height"/> pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void CopyPixelsTo(Span<TPixel> destination)
    {
        ThrowIfDisposed();
        CheckLength(destination.Length, nameof(destination));
        for (int y = 0; y < Height; y++)
        {
            Row(y).CopyTo(destination.Slice(y * Width, Width));
        }
    }

    /// <summary>Copies the first frame's pixels as raw bytes, row by row with no gaps, to <paramref name="destination"/>.</summary>
    /// <param name="destination">Where to copy. It must hold at least <see cref="Raster.Width"/> × <see cref="Raster.Height"/> × the pixel size bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void CopyPixelsTo(Span<byte> destination)
    {
        CopyPixelsTo(MemoryMarshal.Cast<byte, TPixel>(destination));
    }

    /// <summary>Overwrites the first frame's pixels from <paramref name="source"/>, which holds them row by row with no gaps.</summary>
    /// <param name="source">The pixels. It must hold at least <see cref="Raster.Width"/> × <see cref="Raster.Height"/> pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is too short.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void CopyPixelsFrom(ReadOnlySpan<TPixel> source)
    {
        ThrowIfDisposed();
        CheckLength(source.Length, nameof(source));
        for (int y = 0; y < Height; y++)
        {
            source.Slice(y * Width, Width).CopyTo(Row(y));
        }
    }

    /// <summary>Overwrites the first frame's pixels from raw bytes laid out row by row with no gaps.</summary>
    /// <param name="source">The bytes. It must hold at least <see cref="Raster.Width"/> × <see cref="Raster.Height"/> × the pixel size bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is too short.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void CopyPixelsFrom(ReadOnlySpan<byte> source)
    {
        CopyPixelsFrom(MemoryMarshal.Cast<byte, TPixel>(source));
    }

    /// <summary>Returns a deep copy of the raster: every frame and all metadata.</summary>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Raster<TPixel> Copy()
    {
        return As<TPixel>();
    }

    private protected override Raster<TTo> ConvertTo<TTo>()
    {
        ThrowIfDisposed();
        List<Frame<TTo>> frames = new(_frames.Count);
        try
        {
            foreach (Frame<TPixel> frame in _frames)
            {
                PixelPlane<TTo> plane = PixelPlane.Allocate<TTo>(Width, Height, Context.Memory);
                frames.Add(new Frame<TTo>(plane, frame.Metadata.Copy()));
                for (int y = 0; y < Height; y++)
                {
                    PixelConvert.Convert<TPixel, TTo>(frame.Plane.Row(y), plane.Row(y));
                }
            }
        }
        catch
        {
            foreach (Frame<TTo> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }

        return new Raster<TTo>(frames, Metadata.Copy(), Context);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Frame<TPixel> frame in _frames)
        {
            frame.Plane.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void CheckColumn(int x)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"The column must be between 0 and {Width - 1}.");
        }
    }

    private void CheckLength(int length, string paramName)
    {
        long needed = (long)Width * Height;
        if (length < needed)
        {
            throw new ArgumentException($"Holds {length} pixels but the raster has {needed}.", paramName);
        }
    }
}
