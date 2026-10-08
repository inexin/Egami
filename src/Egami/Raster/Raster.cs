using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>Processes one row of pixels.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <param name="row">The row's pixels. Valid only during the call.</param>
/// <param name="y">The row index.</param>
public delegate void RowAction<TPixel>(Span<TPixel> row, int y)
    where TPixel : unmanaged, IPixelType<TPixel>;

/// <summary>
/// An image: one or more frames of the same size and pixel type, plus metadata.
/// This base class is used when the pixel type is not known at compile time; <see cref="Raster{TPixel}"/> gives pixel access.
/// </summary>
/// <remarks>
/// A raster is not thread-safe. Several threads may read it at once, but nothing may read while another thread writes.
/// Methods that return a new raster say so in their name (<c>Copy</c>, <c>As</c>); everything else works in place.
/// </remarks>
public abstract partial class Raster : IDisposable
{
    private protected Raster(int width, int height, RasterMetadata metadata, EgamiContext context)
    {
        Width = width;
        Height = height;
        Metadata = metadata;
        Context = context;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; private protected set; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; private protected set; }

    /// <summary>Gets the memory layout of the pixel type.</summary>
    public abstract PixelLayoutInfo Layout { get; }

    /// <summary>Gets the number of frames. Always at least 1.</summary>
    public abstract int FrameCount { get; }

    /// <summary>Gets the raster's metadata.</summary>
    public RasterMetadata Metadata { get; }

    /// <summary>Gets the context the raster allocates from.</summary>
    internal EgamiContext Context { get; }

    /// <summary>Creates a raster with one frame, with every byte zero (transparent black for types with alpha).</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    public static Raster<TPixel> Create<TPixel>(int width, int height, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        context ??= EgamiContext.Default;
        return new Raster<TPixel>(PixelPlane.Allocate<TPixel>(width, height, context.Memory, ClearMode.Zero), new RasterMetadata(), context);
    }

    /// <summary>Creates a raster with one frame, filled with one color.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="fill">The color of every pixel.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    public static Raster<TPixel> Create<TPixel>(int width, int height, TPixel fill, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        context ??= EgamiContext.Default;
        PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(width, height, context.Memory);
        for (int y = 0; y < height; y++)
        {
            plane.Row(y).Fill(fill);
        }

        return new Raster<TPixel>(plane, new RasterMetadata(), context);
    }

    /// <summary>
    /// Creates a raster over memory the caller owns, without copying. The raster never frees the memory,
    /// and the caller must keep it alive while the raster is used.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="memory">The pixels, row by row.</param>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="stride">The distance between the starts of consecutive rows, in pixels. Defaults to <paramref name="width"/>.</param>
    /// <param name="context">The context for later allocations, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range, or <paramref name="memory"/> is too short.</exception>
    public static Raster<TPixel> Wrap<TPixel>(Memory<TPixel> memory, int width, int height, int stride = 0, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return new Raster<TPixel>(PixelPlane.Wrap(memory, width, height, stride), new RasterMetadata(), context ?? EgamiContext.Default);
    }

    /// <summary>Returns a new raster with every frame converted to <typeparamref name="TTo"/>. See <see cref="PixelConvert.Convert"/>.</summary>
    /// <typeparam name="TTo">The pixel type to convert to.</typeparam>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Raster<TTo> As<TTo>()
        where TTo : unmanaged, IPixelType<TTo>
    {
        return ConvertTo<TTo>();
    }

    /// <summary>Returns the raster's memory to its source. Calling it again does nothing.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs a chain of operations on every frame, changing this raster in place. See <see cref="Raster{TPixel}.Apply"/>.
    /// </summary>
    /// <param name="build">Records the chain.</param>
    /// <param name="cancellationToken">Cancels between steps and between bands of rows.</param>
    public void Apply(Action<RasterOps> build, CancellationToken cancellationToken = default)
    {
        ApplyCore(build, cancellationToken);
    }

    /// <summary>Runs a chain of operations and returns the result as a new raster. See <see cref="Raster{TPixel}.Derive"/>.</summary>
    /// <param name="build">Records the chain.</param>
    /// <param name="cancellationToken">Cancels between steps and between bands of rows.</param>
    /// <returns>The new raster, in the same pixel type. The caller must dispose it.</returns>
    public Raster Derive(Action<RasterOps> build, CancellationToken cancellationToken = default)
    {
        return DeriveCore(build, cancellationToken);
    }

    /// <summary>Implements <see cref="Apply"/>.</summary>
    private protected abstract void ApplyCore(Action<RasterOps> build, CancellationToken cancellationToken);

    /// <summary>Implements <see cref="Derive"/>.</summary>
    private protected abstract Raster DeriveCore(Action<RasterOps> build, CancellationToken cancellationToken);

    /// <summary>Implements <see cref="As{TTo}"/>.</summary>
    private protected abstract Raster<TTo> ConvertTo<TTo>()
        where TTo : unmanaged, IPixelType<TTo>;

    /// <summary>Calls <paramref name="writer"/> with the typed raster.</summary>
    private protected abstract void WriteWith(Formats.IFormatWriter writer, Stream output, Formats.WriterOptions options);

    /// <summary>Releases the frames' memory.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected abstract void Dispose(bool disposing);
}
