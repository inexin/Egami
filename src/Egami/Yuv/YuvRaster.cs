using System.Buffers;
using System.Runtime.CompilerServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami;

/// <summary>
/// A picture held as separate luma (Y) and chroma (U, V) planes, plus an optional alpha plane, the way video codecs, AVIF, lossy
/// WebP and JPEG store it. This base class is used when the sample type is not known at compile time;
/// <see cref="YuvRaster{TSample}"/> gives the planes. Convert to and from a <see cref="Raster"/> with <see cref="ToRaster(YuvConvertOptions?)"/>,
/// <see cref="CopyTo{TPixel}"/> and <see cref="FromRaster(Raster, ChromaFormat, YuvColor, bool, YuvConvertOptions?)"/>.
/// </summary>
/// <remarks>
/// A YUV raster is not a <see cref="Raster"/>: one pixel is spread over planes of different sizes. Operations other than
/// <c>Crop</c> and <c>Resize</c> run on a <see cref="Raster"/> after conversion. It is not thread-safe for writing, like a raster.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="create"/>
/// </example>
public abstract partial class YuvRaster : IDisposable
{
    private int _disposed;
    private IDisposable? _state;

    private protected YuvRaster(int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, RasterMetadata metadata, EgamiContext context)
    {
        Width = width;
        Height = height;
        ChromaFormat = chromaFormat;
        BitDepth = bitDepth;
        Color = color;
        Metadata = metadata;
        Context = context;
    }

    /// <summary>Gets the width of the luma plane in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height of the luma plane in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the chroma resolution against luma.</summary>
    public ChromaFormat ChromaFormat { get; }

    /// <summary>Gets the bits per sample: 8 for <see cref="byte"/> samples; 10, 12 or 16 for <see cref="ushort"/> samples.</summary>
    public int BitDepth { get; }

    /// <summary>Gets how the samples turn into RGB: matrix, range and chroma siting.</summary>
    public YuvColor Color { get; }

    /// <summary>Gets a value indicating whether the raster has an alpha plane.</summary>
    public abstract bool HasAlpha { get; }

    /// <summary>Gets the metadata (EXIF, ICC, XMP and the rest), carried to and from a <see cref="Raster"/> on conversion.</summary>
    public RasterMetadata Metadata { get; }

    /// <summary>Gets the context the raster allocates from.</summary>
    internal EgamiContext Context { get; }

    /// <summary>Gets the largest sample value at <see cref="BitDepth"/>.</summary>
    internal int MaxSample => (1 << BitDepth) - 1;

    /// <summary>Creates an 8-bit raster with every sample zero.</summary>
    /// <param name="width">The luma width in pixels, at least 1.</param>
    /// <param name="height">The luma height in pixels, at least 1.</param>
    /// <param name="chromaFormat">The chroma resolution.</param>
    /// <param name="color">How the samples turn into RGB.</param>
    /// <param name="alpha">Whether to add a full-size alpha plane.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1, or an enum value is undefined.</exception>
    /// <exception cref="LimitExceededException">The size or memory breaks the context's <see cref="SafetyLimits"/>.</exception>
    public static YuvRaster<byte> Create(int width, int height, ChromaFormat chromaFormat, YuvColor color, bool alpha = false, EgamiContext? context = null)
    {
        return YuvRaster<byte>.Allocate(width, height, chromaFormat, 8, color, alpha, new RasterMetadata(), context ?? EgamiContext.Default, ClearMode.Zero);
    }

    /// <summary>Creates a 10-, 12- or 16-bit raster with every sample zero.</summary>
    /// <param name="width">The luma width in pixels, at least 1.</param>
    /// <param name="height">The luma height in pixels, at least 1.</param>
    /// <param name="chromaFormat">The chroma resolution.</param>
    /// <param name="bitDepth">The bits per sample: 10, 12 or 16.</param>
    /// <param name="color">How the samples turn into RGB.</param>
    /// <param name="alpha">Whether to add a full-size alpha plane.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1, the bit depth is not 10, 12 or 16, or an enum value is undefined.</exception>
    /// <exception cref="LimitExceededException">The size or memory breaks the context's <see cref="SafetyLimits"/>.</exception>
    public static YuvRaster<ushort> Create(int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, bool alpha = false, EgamiContext? context = null)
    {
        return YuvRaster<ushort>.Allocate(width, height, chromaFormat, bitDepth, color, alpha, new RasterMetadata(), context ?? EgamiContext.Default, ClearMode.Zero);
    }

    /// <summary>
    /// Creates a raster over planes the caller owns, such as a decoder's or the operating system's frame buffers, without copying.
    /// When <paramref name="owner"/> is given, disposing the raster disposes it; otherwise the caller keeps the memory alive while
    /// the raster is used. Sizes and strides are checked up front; <see cref="SafetyLimits"/> do not apply to wrapped memory.
    /// </summary>
    /// <typeparam name="TSample"><see cref="byte"/> for 8 bits, <see cref="ushort"/> for 10, 12 and 16 bits.</typeparam>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">The distance between luma rows, in samples. 0 means rows are packed.</param>
    /// <param name="u">The U (Cb) plane, or empty for 4:0:0.</param>
    /// <param name="uStride">The distance between U rows, in samples. 0 means rows are packed.</param>
    /// <param name="v">The V (Cr) plane, or empty for 4:0:0.</param>
    /// <param name="vStride">The distance between V rows, in samples. 0 means rows are packed.</param>
    /// <param name="width">The luma width in pixels, at least 1.</param>
    /// <param name="height">The luma height in pixels, at least 1.</param>
    /// <param name="chromaFormat">The chroma resolution.</param>
    /// <param name="bitDepth">The bits per sample: 8 for bytes; 10, 12 or 16 for <see cref="ushort"/>.</param>
    /// <param name="color">How the samples turn into RGB.</param>
    /// <param name="owner">What to dispose with the raster, or <see langword="null"/>.</param>
    /// <param name="context">The context for later allocations, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size, stride or bit depth is out of range, or a plane is too short.</exception>
    /// <exception cref="ArgumentException">Chroma planes are given for 4:0:0, or missing for another format.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="TSample"/> is not <see cref="byte"/> or <see cref="ushort"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="wrap"/>
    /// </example>
    public static YuvRaster<TSample> Wrap<TSample>(
        Memory<TSample> y, int yStride, Memory<TSample> u, int uStride, Memory<TSample> v, int vStride,
        int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, IDisposable? owner = null, EgamiContext? context = null)
        where TSample : unmanaged
    {
        return YuvRaster<TSample>.WrapPlanes(y, yStride, u, uStride, v, vStride, default, 0, hasAlpha: false, width, height, chromaFormat, bitDepth, color, owner, context);
    }

    /// <summary>
    /// Creates a raster over planes the caller owns, with an alpha plane, without copying. See
    /// <see cref="Wrap{TSample}(Memory{TSample}, int, Memory{TSample}, int, Memory{TSample}, int, int, int, ChromaFormat, int, YuvColor, IDisposable?, EgamiContext?)"/>.
    /// </summary>
    /// <typeparam name="TSample"><see cref="byte"/> for 8 bits, <see cref="ushort"/> for 10, 12 and 16 bits.</typeparam>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">The distance between luma rows, in samples. 0 means rows are packed.</param>
    /// <param name="u">The U (Cb) plane, or empty for 4:0:0.</param>
    /// <param name="uStride">The distance between U rows, in samples. 0 means rows are packed.</param>
    /// <param name="v">The V (Cr) plane, or empty for 4:0:0.</param>
    /// <param name="vStride">The distance between V rows, in samples. 0 means rows are packed.</param>
    /// <param name="alpha">The full-size alpha plane, straight (not premultiplied), at the same bit depth.</param>
    /// <param name="alphaStride">The distance between alpha rows, in samples. 0 means rows are packed.</param>
    /// <param name="width">The luma width in pixels, at least 1.</param>
    /// <param name="height">The luma height in pixels, at least 1.</param>
    /// <param name="chromaFormat">The chroma resolution.</param>
    /// <param name="bitDepth">The bits per sample: 8 for bytes; 10, 12 or 16 for <see cref="ushort"/>.</param>
    /// <param name="color">How the samples turn into RGB.</param>
    /// <param name="owner">What to dispose with the raster, or <see langword="null"/>.</param>
    /// <param name="context">The context for later allocations, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size, stride or bit depth is out of range, or a plane is too short.</exception>
    /// <exception cref="ArgumentException">Chroma planes are given for 4:0:0, or missing for another format.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="TSample"/> is not <see cref="byte"/> or <see cref="ushort"/>.</exception>
    public static YuvRaster<TSample> Wrap<TSample>(
        Memory<TSample> y, int yStride, Memory<TSample> u, int uStride, Memory<TSample> v, int vStride, Memory<TSample> alpha, int alphaStride,
        int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, IDisposable? owner = null, EgamiContext? context = null)
        where TSample : unmanaged
    {
        return YuvRaster<TSample>.WrapPlanes(y, yStride, u, uStride, v, vStride, alpha, alphaStride, hasAlpha: true, width, height, chromaFormat, bitDepth, color, owner, context);
    }

    /// <summary>
    /// Converts the first frame of a raster to a new 8-bit YUV raster. Metadata is copied.
    /// </summary>
    /// <param name="source">The raster to convert, of any pixel type.</param>
    /// <param name="chromaFormat">The chroma resolution.</param>
    /// <param name="color">The matrix, range and siting to encode with.</param>
    /// <param name="alpha">
    /// Whether to add an alpha plane, holding the source's alpha (opaque for sources without). Defaults to
    /// <see langword="false"/>: alpha is dropped.
    /// </param>
    /// <param name="options">The chroma filters, or <see langword="null"/> for <see cref="YuvConvertOptions.Default"/>.</param>
    /// <returns>The new raster, allocated from the source's context. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="source"/> has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="convert"/>
    /// </example>
    public static YuvRaster<byte> FromRaster(Raster source, ChromaFormat chromaFormat, YuvColor color, bool alpha = false, YuvConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Accept(new FromRasterVisitor<byte>(chromaFormat, 8, color, alpha, options ?? YuvConvertOptions.Default));
    }

    /// <summary>
    /// Converts the first frame of a raster to a new 10-, 12- or 16-bit YUV raster. Metadata is copied.
    /// </summary>
    /// <param name="source">The raster to convert, of any pixel type.</param>
    /// <param name="chromaFormat">The chroma resolution.</param>
    /// <param name="bitDepth">The bits per sample: 10, 12 or 16.</param>
    /// <param name="color">The matrix, range and siting to encode with.</param>
    /// <param name="alpha">
    /// Whether to add an alpha plane, holding the source's alpha (opaque for sources without). Defaults to
    /// <see langword="false"/>: alpha is dropped.
    /// </param>
    /// <param name="options">The chroma filters, or <see langword="null"/> for <see cref="YuvConvertOptions.Default"/>.</param>
    /// <returns>The new raster, allocated from the source's context. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The bit depth is not 10, 12 or 16.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="source"/> has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="convert"/>
    /// </example>
    public static YuvRaster<ushort> FromRaster(Raster source, ChromaFormat chromaFormat, int bitDepth, YuvColor color, bool alpha = false, YuvConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Accept(new FromRasterVisitor<ushort>(chromaFormat, bitDepth, color, alpha, options ?? YuvConvertOptions.Default));
    }

    /// <summary>
    /// Converts to a new raster in the natural pixel type: <see cref="Rgba8"/> for 8-bit samples, <see cref="Rgba16"/> for more.
    /// RGB comes out in the picture's own transfer function; nothing is linearized or tone mapped.
    /// </summary>
    /// <param name="options">The chroma filters, or <see langword="null"/> for <see cref="YuvConvertOptions.Default"/>.</param>
    /// <returns>The new raster, with a copy of the metadata. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="convert"/>
    /// </example>
    public Raster ToRaster(YuvConvertOptions? options = null)
    {
        return BitDepth == 8 ? ToRaster<Rgba8>(options) : ToRaster<Rgba16>(options);
    }

    /// <summary>Converts to a new raster of <typeparamref name="TPixel"/>. See <see cref="ToRaster(YuvConvertOptions?)"/>.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="options">The chroma filters, or <see langword="null"/> for <see cref="YuvConvertOptions.Default"/>.</param>
    /// <returns>The new raster, with a copy of the metadata. The caller must dispose it.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <exception cref="LimitExceededException">The new raster breaks the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="convert"/>
    /// </example>
    public Raster<TPixel> ToRaster<TPixel>(YuvConvertOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ThrowIfDisposed();
        long bytes = (long)Width * Height * Unsafe.SizeOf<TPixel>();
        if (bytes > Context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), Context.Limits.MaxTotalBytes, bytes);
        }

        Raster<TPixel> target = new(PixelPlane.Allocate<TPixel>(Width, Height, Context.Memory), Metadata.Copy(), Context);
        try
        {
            ConvertTo(target, options ?? YuvConvertOptions.Default, CancellationToken.None);
            YuvConvert.DescribeRgb(target.Metadata, this);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Converts into the first frame of an existing raster of the same size, with no allocation after the first call, so a video
    /// can reuse one raster for every frame. The target's metadata is left alone.
    /// </summary>
    /// <typeparam name="TPixel">The target's pixel type.</typeparam>
    /// <param name="target">The raster to write.</param>
    /// <param name="options">The chroma filters, or <see langword="null"/> for <see cref="YuvConvertOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels between bands of rows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The target is not the same size.</exception>
    /// <exception cref="ObjectDisposedException">Either raster has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="reuse"/>
    /// </example>
    public void CopyTo<TPixel>(Raster<TPixel> target, YuvConvertOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        if (target.Width != Width || target.Height != Height)
        {
            throw new ArgumentException($"The target is {target.Width}×{target.Height} but this raster is {Width}×{Height}.", nameof(target));
        }

        ConvertTo(target, options ?? YuvConvertOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Converts the first frame of a raster of the same size into this one, with no allocation after the first call. The alpha
    /// plane, if any, takes the source's alpha (opaque for sources without). This raster's metadata is left alone.
    /// </summary>
    /// <typeparam name="TPixel">The source's pixel type.</typeparam>
    /// <param name="source">The raster to read.</param>
    /// <param name="options">The chroma filters, or <see langword="null"/> for <see cref="YuvConvertOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels between bands of rows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source is not the same size.</exception>
    /// <exception cref="ObjectDisposedException">Either raster has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="reuse"/>
    /// </example>
    public void CopyFrom<TPixel>(Raster<TPixel> source, YuvConvertOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Width != Width || source.Height != Height)
        {
            throw new ArgumentException($"The source is {source.Width}×{source.Height} but this raster is {Width}×{Height}.", nameof(source));
        }

        ConvertFrom(source, options ?? YuvConvertOptions.Default, cancellationToken);
    }

    /// <summary>Returns the planes' memory to its source, or disposes the owner of wrapped planes. Calling it again does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _state, null)?.Dispose();
            Release();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Takes the conversion state (row buffers) kept from the last single-threaded conversion, or <see langword="null"/>. A
    /// conversion running at the same time on another thread then makes its own.
    /// </summary>
    internal IDisposable? TakeState() => Interlocked.Exchange(ref _state, null);

    /// <summary>Keeps conversion state for the next conversion, freeing what was kept before; after disposal, frees it at once.</summary>
    internal void KeepState(IDisposable state)
    {
        IDisposable? previous = Interlocked.Exchange(ref _state, state);
        if (previous is not null && !ReferenceEquals(previous, state))
        {
            previous.Dispose();
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _state, null)?.Dispose();
        }
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>Converts into an existing raster of the same size.</summary>
    private protected abstract void ConvertTo<TPixel>(Raster<TPixel> target, YuvConvertOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Converts from an existing raster of the same size.</summary>
    private protected abstract void ConvertFrom<TPixel>(Raster<TPixel> source, YuvConvertOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Releases the planes' memory.</summary>
    private protected abstract void Release();

    private sealed class FromRasterVisitor<TSample>(ChromaFormat format, int bitDepth, YuvColor color, bool alpha, YuvConvertOptions options) : IRasterVisitor<YuvRaster<TSample>>
        where TSample : unmanaged
    {
        public YuvRaster<TSample> Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            YuvRaster<TSample> result = YuvRaster<TSample>.Allocate(raster.Width, raster.Height, format, bitDepth, color, alpha, raster.Metadata.Copy(), raster.Context, ClearMode.None);
            try
            {
                result.CopyFrom(raster, options);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }
}

/// <summary>
/// A picture held as separate Y, U, V and optional alpha planes of <typeparamref name="TSample"/> samples. See
/// <see cref="YuvRaster"/>.
/// </summary>
/// <typeparam name="TSample"><see cref="byte"/> for 8 bits, <see cref="ushort"/> for 10, 12 and 16 bits.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="planes"/>
/// </example>
public sealed partial class YuvRaster<TSample> : YuvRaster
    where TSample : unmanaged
{
    /// <summary>Planes start on 64-byte boundaries within one allocation, so vector loads of a row start aligned.</summary>
    private const int PlaneAlignment = 64;

    private IMemoryOwner<byte>[]? _owned;
    private IDisposable? _owner;

    private YuvRaster(int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, RasterMetadata metadata, EgamiContext context)
        : base(width, height, chromaFormat, bitDepth, color, metadata, context)
    {
        Y = null!;
    }

    /// <summary>Gets the luma plane, <see cref="YuvRaster.Width"/> × <see cref="YuvRaster.Height"/>.</summary>
    public YuvPlane<TSample> Y { get; private set; }

    /// <summary>
    /// Gets the U (Cb, blue difference) plane, or <see langword="null"/> for 4:0:0. It is half the luma width, rounded up, for
    /// 4:2:0 and 4:2:2, and half the luma height, rounded up, for 4:2:0; a quarter of the luma width, rounded up, for 4:1:1 and
    /// 4:1:0, and a quarter of the luma height, rounded up, for 4:1:0.
    /// </summary>
    public YuvPlane<TSample>? U { get; private set; }

    /// <summary>Gets the V (Cr, red difference) plane, the same size as <see cref="U"/>, or <see langword="null"/> for 4:0:0.</summary>
    public YuvPlane<TSample>? V { get; private set; }

    /// <summary>Gets the alpha plane, full size, straight and always full range, or <see langword="null"/> without alpha.</summary>
    public YuvPlane<TSample>? A { get; private set; }

    /// <inheritdoc/>
    public override bool HasAlpha => A is not null;

    /// <summary>Allocates a raster from the context's memory source: one allocation for all planes when they fit in one block.</summary>
    internal static YuvRaster<TSample> Allocate(int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, bool alpha, RasterMetadata metadata, EgamiContext context, ClearMode clear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Validate(chromaFormat, bitDepth, color);
        context.Limits.CheckFrameSize(width, height);
        (int cw, int ch) = YuvPlaneSize.ChromaSize(chromaFormat, width, height);
        int sampleBytes = Unsafe.SizeOf<TSample>();
        long lumaBytes = (long)width * height * sampleBytes;
        long chromaBytes = (long)cw * ch * sampleBytes;
        long total = lumaBytes + (2 * chromaBytes) + (alpha ? lumaBytes : 0);
        if (total > context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, total);
        }

        YuvRaster<TSample> raster = new(width, height, chromaFormat, bitDepth, color, metadata, context);
        IPixelMemorySource source = context.Memory;
        long[] sizes = [lumaBytes, chromaBytes, chromaBytes, alpha ? lumaBytes : 0];
        long packed = 0;
        foreach (long size in sizes)
        {
            packed += Align(size);
        }

        List<IMemoryOwner<byte>> owned = [];
        try
        {
            if (packed <= source.MaxBlockBytes)
            {
                IMemoryOwner<byte> block = source.Allocate((int)packed, clear);
                owned.Add(block);
                Memory<byte> memory = block.Memory;
                if (memory.Length != packed)
                {
                    throw new InvalidOperationException("The memory source returned a block of the wrong length.");
                }

                int offset = 0;
                raster.Y = Slice(raster, width, height, memory, ref offset);
                if (cw > 0)
                {
                    raster.U = Slice(raster, cw, ch, memory, ref offset);
                    raster.V = Slice(raster, cw, ch, memory, ref offset);
                }

                raster.A = alpha ? Slice(raster, width, height, memory, ref offset) : null;
            }
            else
            {
                raster.Y = Blocks(raster, width, height, source, clear, owned);
                if (cw > 0)
                {
                    raster.U = Blocks(raster, cw, ch, source, clear, owned);
                    raster.V = Blocks(raster, cw, ch, source, clear, owned);
                }

                raster.A = alpha ? Blocks(raster, width, height, source, clear, owned) : null;
            }
        }
        catch
        {
            foreach (IMemoryOwner<byte> block in owned)
            {
                block.Dispose();
            }

            throw;
        }

        raster._owned = [.. owned];
        return raster;

        static long Align(long bytes) => (bytes + PlaneAlignment - 1) / PlaneAlignment * PlaneAlignment;

        static YuvPlane<TSample> Slice(YuvRaster<TSample> owner, int w, int h, Memory<byte> memory, ref int offset)
        {
            int bytes = w * h * Unsafe.SizeOf<TSample>();
            YuvPlane<TSample> plane = new(owner, w, h, [memory.Slice(offset, bytes)], h);
            offset += (int)Align(bytes);
            return plane;
        }

        static YuvPlane<TSample> Blocks(YuvRaster<TSample> owner, int w, int h, IPixelMemorySource source, ClearMode clear, List<IMemoryOwner<byte>> owned)
        {
            long rowBytes = (long)w * Unsafe.SizeOf<TSample>();
            if (rowBytes > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException(nameof(width), w, "A row of this many samples is larger than 2 GB.");
            }

            int rowsPerBlock = (int)Math.Clamp(source.MaxBlockBytes / rowBytes, 1, h);
            int count = (h + rowsPerBlock - 1) / rowsPerBlock;
            Memory<byte>[] blocks = new Memory<byte>[count];
            for (int i = 0; i < count; i++)
            {
                int rows = Math.Min(rowsPerBlock, h - (i * rowsPerBlock));
                IMemoryOwner<byte> block = source.Allocate((int)(rows * rowBytes), clear);
                owned.Add(block);
                blocks[i] = block.Memory;
                if (blocks[i].Length != rows * rowBytes)
                {
                    throw new InvalidOperationException("The memory source returned a block of the wrong length.");
                }
            }

            return new YuvPlane<TSample>(owner, w, h, blocks, rowsPerBlock);
        }
    }

    internal static YuvRaster<TSample> WrapPlanes(
        Memory<TSample> y, int yStride, Memory<TSample> u, int uStride, Memory<TSample> v, int vStride, Memory<TSample> a, int aStride, bool hasAlpha,
        int width, int height, ChromaFormat chromaFormat, int bitDepth, YuvColor color, IDisposable? owner, EgamiContext? context)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Validate(chromaFormat, bitDepth, color);
        (int cw, int ch) = YuvPlaneSize.ChromaSize(chromaFormat, width, height);
        if (cw == 0 && (!u.IsEmpty || !v.IsEmpty))
        {
            throw new ArgumentException("A 4:0:0 raster has no chroma planes; pass empty U and V.", nameof(u));
        }

        YuvRaster<TSample> raster = new(width, height, chromaFormat, bitDepth, color, new RasterMetadata(), context ?? EgamiContext.Default);
        raster.Y = WrapPlane(raster, y, yStride, width, height, nameof(y), nameof(yStride));
        if (cw > 0)
        {
            raster.U = WrapPlane(raster, u, uStride, cw, ch, nameof(u), nameof(uStride));
            raster.V = WrapPlane(raster, v, vStride, cw, ch, nameof(v), nameof(vStride));
        }

        raster.A = hasAlpha ? WrapPlane(raster, a, aStride, width, height, nameof(a), nameof(aStride)) : null;
        raster._owner = owner;
        return raster;
    }

    /// <inheritdoc/>
    private protected override void ConvertTo<TPixel>(Raster<TPixel> target, YuvConvertOptions options, CancellationToken cancellationToken)
    {
        YuvConvert.ToRgb(this, target, options, cancellationToken);
    }

    /// <inheritdoc/>
    private protected override void ConvertFrom<TPixel>(Raster<TPixel> source, YuvConvertOptions options, CancellationToken cancellationToken)
    {
        YuvConvert.FromRgb(source, this, options, cancellationToken);
    }

    /// <inheritdoc/>
    private protected override void Release()
    {
        IMemoryOwner<byte>[]? owned = Interlocked.Exchange(ref _owned, null);
        if (owned is not null)
        {
            foreach (IMemoryOwner<byte> block in owned)
            {
                block.Dispose();
            }
        }

        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }

    private static void Validate(ChromaFormat chromaFormat, int bitDepth, YuvColor color)
    {
        if (typeof(TSample) == typeof(byte))
        {
            if (bitDepth != 8)
            {
                throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "Byte samples are 8 bits.");
            }
        }
        else if (typeof(TSample) == typeof(ushort))
        {
            if (bitDepth is not (10 or 12 or 16))
            {
                throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "UInt16 samples are 10, 12 or 16 bits.");
            }
        }
        else
        {
            throw new NotSupportedException($"YUV samples are byte or ushort, not {typeof(TSample).Name}.");
        }

        if (!Enum.IsDefined(chromaFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(chromaFormat), chromaFormat, "Not a ChromaFormat.");
        }

        if (!Enum.IsDefined(color.Matrix) || !Enum.IsDefined(color.Range) || !Enum.IsDefined(color.Siting))
        {
            throw new ArgumentOutOfRangeException(nameof(color), color, "Not a valid YuvColor.");
        }
    }

    private static YuvPlane<TSample> WrapPlane(YuvRaster<TSample> owner, Memory<TSample> memory, int stride, int width, int height, string name, string strideName)
    {
        if (stride == 0)
        {
            stride = width;
        }

        if (stride < width)
        {
            throw new ArgumentOutOfRangeException(strideName, stride, $"The stride must be at least the plane width, {width}.");
        }

        long needed = ((long)(height - 1) * stride) + width;
        if (memory.Length < needed)
        {
            throw new ArgumentOutOfRangeException(name, memory.Length, $"The plane holds {memory.Length} samples but {width}×{height} with stride {stride} needs {needed}.");
        }

        return new YuvPlane<TSample>(owner, width, height, memory, stride);
    }
}

/// <summary>Plane sizes for each <see cref="ChromaFormat"/>.</summary>
internal static class YuvPlaneSize
{
    /// <summary>Returns the chroma plane size, or (0, 0) for 4:0:0.</summary>
    internal static (int Width, int Height) ChromaSize(ChromaFormat format, int width, int height) => format switch
    {
        ChromaFormat.Yuv420 => ((width + 1) >> 1, (height + 1) >> 1),
        ChromaFormat.Yuv422 => ((width + 1) >> 1, height),
        ChromaFormat.Yuv444 => (width, height),
        ChromaFormat.Yuv411 => ((width + 3) >> 2, height),
        ChromaFormat.Yuv410 => ((width + 3) >> 2, (height + 3) >> 2),
        _ => (0, 0),
    };
}
