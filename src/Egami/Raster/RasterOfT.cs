using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>An image whose pixels are of type <typeparamref name="TPixel"/>. Pixel access is by row span.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="pixel-types"/>
/// </example>
public sealed class Raster<TPixel> : Raster
    where TPixel : unmanaged, IPixelType<TPixel>
{
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
        Frames = new FrameList<TPixel>(frames, this);
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
    /// Calls <paramref name="action"/> once for every row of the first frame. Large rasters are processed in bands of rows on several
    /// threads, up to the context's <see cref="EgamiContext.MaxParallelism"/>, so <paramref name="action"/> may run concurrently for
    /// different rows.
    /// </summary>
    /// <param name="action">The work to do on each row.</param>
    /// <param name="cancellationToken">Cancels between bands of rows.</param>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public void ForEachRow(RowAction<TPixel> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();
        PixelPlane<TPixel> plane = _frames[0].Plane;
        RowRunner.Run(Height, (long)Width * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>(), Context.MaxParallelism, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                action(plane.Row(y), y);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Runs a chain of operations on every frame, changing this raster in place. Steps that keep the size work on the existing
    /// pixels; steps that change it get new pixel memory, and the old memory is returned at once.
    /// </summary>
    /// <param name="build">Records the chain, for example <c>ops =&gt; ops.Add(operation)</c>.</param>
    /// <param name="cancellationToken">Cancels between steps and between bands of rows.</param>
    /// <remarks>
    /// Arguments are checked while the chain is recorded, before any pixel changes. If a step fails or the chain is cancelled
    /// part way, the raster may be left partly changed. Spans from <see cref="Row"/> taken before a size change become invalid.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="LimitExceededException">A new plane would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public new void Apply(Action<RasterOps> build, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using OperationPipeline.Plan plan = OperationPipeline.Build(build, new Size(Width, Height), Metadata);
        if (plan.Steps.Count == 0)
        {
            return;
        }

        BudgetedMemorySource memory = new(Context.Memory, Context.Limits.MaxTotalBytes);
        for (int i = 0; i < _frames.Count; i++)
        {
            Frame<TPixel> frame = _frames[i];
            PixelPlane<TPixel> result = OperationPipeline.RunFrame(plan, frame.Plane, inPlace: true, Context, memory, i, cancellationToken);
            if (!ReferenceEquals(result, frame.Plane))
            {
                PixelPlane<TPixel> old = frame.Plane;
                frame.Plane = result;
                old.Dispose();
            }
        }

        plan.UpdateMetadata(Metadata, new Size(Width, Height));
        if (plan.ChangesGeometry(new Size(Width, Height)))
        {
            ClearStoredLayout(_frames);
        }

        Size size = plan.FinalSize(new Size(Width, Height));
        Width = size.Width;
        Height = size.Height;
    }

    /// <summary>
    /// Runs a chain of operations and returns the result as a new raster, leaving this one untouched. The first step reads this
    /// raster directly, so no full-size copy is made first.
    /// </summary>
    /// <param name="build">Records the chain, for example <c>ops =&gt; ops.Add(operation)</c>.</param>
    /// <param name="cancellationToken">Cancels between steps and between bands of rows.</param>
    /// <returns>The new raster, with a copy of this raster's metadata. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="LimitExceededException">A new plane would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public new Raster<TPixel> Derive(Action<RasterOps> build, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using OperationPipeline.Plan plan = OperationPipeline.Build(build, new Size(Width, Height), Metadata);
        if (plan.Steps.Count == 0)
        {
            return Copy();
        }

        BudgetedMemorySource memory = new(Context.Memory, Context.Limits.MaxTotalBytes);
        List<Frame<TPixel>> frames = new(_frames.Count);
        try
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                PixelPlane<TPixel> result = OperationPipeline.RunFrame(plan, _frames[i].Plane, inPlace: false, Context, memory, i, cancellationToken);
                frames.Add(new Frame<TPixel>(result, _frames[i].Metadata.Copy()));
            }
        }
        catch
        {
            foreach (Frame<TPixel> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }

        RasterMetadata metadata = Metadata.Copy();
        plan.UpdateMetadata(metadata, new Size(Width, Height));
        if (plan.ChangesGeometry(new Size(Width, Height)))
        {
            ClearStoredLayout(frames);
        }

        return new Raster<TPixel>(frames, metadata, Context);
    }

    private protected override void ApplyCore(Action<RasterOps> build, CancellationToken cancellationToken)
    {
        Apply(build, cancellationToken);
    }

    private protected override Raster DeriveCore(Action<RasterOps> build, CancellationToken cancellationToken)
    {
        return Derive(build, cancellationToken);
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

    /// <summary>Returns a new one-frame raster with a copy of frame <paramref name="index"/> and of the raster's metadata.</summary>
    /// <param name="index">The frame to copy.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a frame.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Raster<TPixel> CopyFrame(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)_frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The raster has {_frames.Count} frames.");
        }

        Frame<TPixel> source = _frames[index];
        PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(Width, Height, Context.Memory);
        for (int y = 0; y < Height; y++)
        {
            source.Plane.Row(y).CopyTo(plane.Row(y));
        }

        return new Raster<TPixel>([new Frame<TPixel>(plane, source.Metadata.Copy())], Metadata.Copy(), Context);
    }

    /// <summary>Returns a deep copy of the raster: every frame and all metadata.</summary>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Raster<TPixel> Copy()
    {
        return As<TPixel>();
    }

    internal override void ReadRow<TTo>(int y, int x, Span<TTo> destination)
    {
        ThrowIfDisposed();
        PixelConvert.Convert<TPixel, TTo>(_frames[0].Plane.Row(y).Slice(x, destination.Length), destination);
    }

    internal override bool HasPlane(object plane)
    {
        foreach (Frame<TPixel> frame in _frames)
        {
            if (ReferenceEquals(frame.Plane, plane))
            {
                return true;
            }
        }

        return false;
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

    private protected override void WriteWith(Formats.IFormatWriter writer, Stream output, Formats.WriterOptions options)
    {
        ThrowIfDisposed();
        writer.Write(this, output, options);
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

    internal void CheckNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>Forgets where each frame was stored in its file and its format details, which a size or layout change makes wrong.</summary>
    private static void ClearStoredLayout(List<Frame<TPixel>> frames)
    {
        foreach (Frame<TPixel> frame in frames)
        {
            frame.Metadata.Bounds = null;
            frame.Metadata.FormatDetails = null;
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
