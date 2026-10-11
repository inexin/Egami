using Egami.Colors;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>Processes one row of pixels.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <param name="row">The row's pixels. Valid only during the call.</param>
/// <param name="y">The row index.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="row-action"/>
/// </example>
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
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="load-edit-save"/>
/// </example>
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

    /// <summary>
    /// Gets the context the raster allocates from: its memory source, limits and <see cref="EgamiContext.MaxParallelism"/>, for
    /// code outside Egami that works on the raster's pixels, such as a canvas drawing onto it.
    /// </summary>
    public EgamiContext Context { get; }

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
    /// and the caller must keep it alive while the raster is used. An operation that changes the size gives the raster new memory
    /// of its own, so it stops showing the caller's. <see cref="SafetyLimits"/> are not applied to wrapped memory.
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

    /// <summary>
    /// Creates a raster over bytes with any row stride, such as a frame from a video decoder whose rows are padded to an alignment,
    /// without copying. When <paramref name="owner"/> is given, disposing the raster disposes it, so the memory goes back to whoever
    /// lent it; otherwise the caller keeps the memory alive while the raster is used. An operation that changes the size gives the
    /// raster new memory of its own, and the wrapped memory is released then.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="memory">The pixels, row by row.</param>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="byteStride">The distance between the starts of consecutive rows, in bytes. 0 means rows are packed.</param>
    /// <param name="owner">What to dispose when the raster lets go of the memory, or <see langword="null"/>.</param>
    /// <param name="context">The context for later allocations, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range, the stride is shorter than a row, or <paramref name="memory"/> is too short.</exception>
    public static Raster<TPixel> Wrap<TPixel>(Memory<byte> memory, int width, int height, int byteStride, IDisposable? owner = null, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return new Raster<TPixel>(PixelPlane.Wrap<TPixel>(memory, width, height, byteStride, owner), new RasterMetadata(), context ?? EgamiContext.Default);
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

    /// <summary>
    /// Returns a new raster of <typeparamref name="TTo"/> with every frame converted from the encoding this raster is in
    /// (<see cref="RasterMetadata.GetColorProfile"/>) to <paramref name="target"/>, and metadata that says so. This is how a CMYK
    /// image becomes RGB through its profile, or RGB becomes CMYK for print; <see cref="As{TTo}"/> converts numerically instead.
    /// </summary>
    /// <typeparam name="TTo">The pixel type to convert to. Its model need not be the target's: see <see cref="ColorTransform"/>.</typeparam>
    /// <param name="target">The encoding to convert to.</param>
    /// <param name="options">The intent and black point compensation, or <see langword="null"/> for the defaults.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="UnsupportedFormatException">The target is an ICC profile with no tables for converting to it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="convert-type"/>
    /// </example>
    public Raster<TTo> ConvertColor<TTo>(ColorProfile target, ColorTransformOptions? options = null)
        where TTo : unmanaged, IPixelType<TTo>
    {
        ArgumentNullException.ThrowIfNull(target);
        return ConvertColorTo<TTo>(target, options ?? ColorTransformOptions.Default);
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
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A step would make the size less than 1.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A crop, pad or fill rectangle does not fit the image when the chain runs.</exception>
    /// <exception cref="LimitExceededException">The pixels the chain needs would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public void Apply(Action<RasterOps> build, CancellationToken cancellationToken = default)
    {
        ApplyCore(build, cancellationToken);
    }

    /// <summary>Runs a chain of operations and returns the result as a new raster. See <see cref="Raster{TPixel}.Derive"/>.</summary>
    /// <param name="build">Records the chain.</param>
    /// <param name="cancellationToken">Cancels between steps and between bands of rows.</param>
    /// <returns>The new raster, in the same pixel type. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A step would make the size less than 1.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A crop, pad or fill rectangle does not fit the image when the chain runs.</exception>
    /// <exception cref="LimitExceededException">The pixels the chain needs would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Raster Derive(Action<RasterOps> build, CancellationToken cancellationToken = default)
    {
        return DeriveCore(build, cancellationToken);
    }

    /// <summary>Implements <see cref="Apply"/>.</summary>
    private protected abstract void ApplyCore(Action<RasterOps> build, CancellationToken cancellationToken);

    /// <summary>Implements <see cref="Derive"/>.</summary>
    private protected abstract Raster DeriveCore(Action<RasterOps> build, CancellationToken cancellationToken);

    /// <summary>Converts part of row <paramref name="y"/> of the first frame, from column <paramref name="x"/>, into <paramref name="destination"/>.</summary>
    internal abstract void ReadRow<TTo>(int y, int x, Span<TTo> destination)
        where TTo : unmanaged, IPixelType<TTo>;

    /// <summary>Converts part of one row of frame <paramref name="frame"/>, starting at column <paramref name="x"/>, into <paramref name="destination"/>.</summary>
    internal abstract void ReadFrameRow<TTo>(int frame, int y, int x, Span<TTo> destination)
        where TTo : unmanaged, IPixelType<TTo>;

    /// <summary>Gets the metadata of frame <paramref name="frame"/>.</summary>
    internal abstract FrameMetadata FrameMetadataAt(int frame);

    /// <summary>Calls <paramref name="visitor"/> with this raster as its own pixel type.</summary>
    internal abstract TResult Accept<TResult>(IRasterVisitor<TResult> visitor);

    /// <summary>Returns whether one of this raster's frames is <paramref name="plane"/>.</summary>
    internal abstract bool HasPlane(object plane);

    /// <summary>Implements <see cref="As{TTo}"/>.</summary>
    private protected abstract Raster<TTo> ConvertColorTo<TTo>(ColorProfile target, ColorTransformOptions options)
        where TTo : unmanaged, IPixelType<TTo>;

    private protected abstract Raster<TTo> ConvertTo<TTo>()
        where TTo : unmanaged, IPixelType<TTo>;

    /// <summary>Calls <paramref name="writer"/> with the typed raster.</summary>
    private protected abstract void WriteWith(Formats.IFormatWriter writer, Stream output, Formats.WriterOptions options);

    /// <summary>Releases the frames' memory.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected abstract void Dispose(bool disposing);
}

/// <summary>Work on a raster that needs its pixel type, reached from the untyped <see cref="Raster"/> through <see cref="Raster.Accept"/>.</summary>
/// <typeparam name="TResult">What the work returns.</typeparam>
internal interface IRasterVisitor<TResult>
{
    TResult Visit<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>;
}
