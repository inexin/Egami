using Egami.Formats;
using Egami.Metadata;
using Egami.Operations.Geometry;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami;

/// <summary>
/// One open image, decoded a band of rows or a frame at a time. Opening reads the header and the metadata that comes before the
/// image data; <see cref="ReadRows"/> and <see cref="ReadFrame{TPixel}(Raster{TPixel})"/> then hand out the image in any pixel
/// type, with <see cref="ReaderOptions.Region"/> and <see cref="ReaderOptions.MinimumSize"/> applied as in
/// <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/>.
/// </summary>
/// <remarks>
/// A decoder is one caller's object and is not thread-safe. <see cref="RasterInfo.Access"/> says whether the format streams its
/// rows or is decoded into a buffer first, and <see cref="RasterInfo.WorkingBytes"/> how much that holds.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="decode-rows"/>
/// </example>
public sealed class RasterDecoder : IDisposable
{
    private readonly RowSource _source;
    private readonly ReadPlan _plan;
    private readonly RowStage _stage;
    private readonly EgamiContext _context;
    private readonly IDisposable? _owned;
    private FrameMetadata? _frame;
    private RasterMetadata? _upkept;
    private bool _upkeptComplete;
    private bool _started;
    private bool _disposed;

    private RasterDecoder(RowSource source, FormatInfo format, EgamiContext context, ReaderOptions options, IDisposable? owned)
    {
        _source = source;
        _context = context;
        _owned = owned;
        _plan = ReadPlan.Create(source.StoredWidth, source.StoredHeight, options.Region, options.MinimumSize, source.MaxNativeScale);
        if (_plan.NativeScale != source.NativeScale)
        {
            throw new InvalidOperationException($"The reader decoded at 1/{source.NativeScale} where the plan asks for 1/{_plan.NativeScale}.");
        }

        _stage = new RowStage(source, _plan);
        RasterMetadata metadata = Metadata;
        Size output = _plan.Output;
        Info = new RasterInfo
        {
            Format = format,
            Width = output.Width,
            Height = output.Height,
            Layout = source.Layout,
            FrameCount = source.FrameCount,
            HasMetadata = metadata.Exif is not null || !metadata.IccProfile.IsEmpty || !metadata.Xmp.IsEmpty || metadata.Text.Count > 0,
            EstimatedBytes = (long)output.Width * output.Height * source.Layout.BytesPerPixel * (source.FrameCount ?? 1),
            Orientation = metadata.Exif?.Orientation ?? ImageOrientation.Normal,
            LoopCount = source.FrameCount > 1 ? metadata.LoopCount : null,
            Access = source.Access,
            WorkingBytes = source.WorkingBytes + _stage.WorkingBytes,
        };
    }

    /// <summary>
    /// Gets what the decoder produces: the size after <see cref="ReaderOptions.Region"/> and <see cref="ReaderOptions.MinimumSize"/>,
    /// the natural pixel layout, the frame count when known, and how rows are produced.
    /// </summary>
    public RasterInfo Info { get; }

    /// <summary>
    /// Gets the metadata read so far; see <see cref="IsMetadataComplete"/>. When the region or reduction changes the size, the
    /// EXIF dimensions follow it and the EXIF thumbnail is dropped, as a crop or resize would.
    /// </summary>
    public RasterMetadata Metadata
    {
        get
        {
            if (!_plan.ChangesGeometry)
            {
                return _source.Metadata;
            }

            if (_upkept is null || _upkeptComplete != _source.MetadataComplete)
            {
                _upkeptComplete = _source.MetadataComplete;
                _upkept = _source.Metadata.Copy();
                ExifUpkeep.Dimensions(_upkept, _plan.Output);
                ExifUpkeep.DropThumbnail(_upkept);
            }

            return _upkept;
        }
    }

    /// <summary>
    /// Gets whether <see cref="Metadata"/> holds everything the file has. PNG and GIF may put text, EXIF and comments after the
    /// image data, which arrive once the last frame has been read.
    /// </summary>
    public bool IsMetadataComplete => _source.MetadataComplete;

    /// <summary>Gets the metadata of the frame being read, or <see langword="null"/> after the last frame.</summary>
    public FrameMetadata? CurrentFrame
    {
        get
        {
            Start();
            return _frame is null ? null : FrameOut(_frame);
        }
    }

    /// <summary>
    /// Opens an image from a stream: detects the format and reads the header. The stream is read from its current position and
    /// left open; the decoder does not dispose it.
    /// </summary>
    /// <param name="stream">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ReaderOptions.Region"/> has no area or misses the image.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="decode-rows"/>
    /// </example>
    public static RasterDecoder Open(Stream stream, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Open(stream, context, options, owned: null);
    }

    /// <summary>Opens an image file. See <see cref="Open(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it, which closes the file.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ReaderOptions.Region"/> has no area or misses the image.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="decode-rows"/>
    /// </example>
    public static RasterDecoder Open(string path, EgamiContext? context = null, ReaderOptions? options = null)
    {
        FileStream stream = Raster.OpenRead(path);
        return Open(stream, context, options, owned: stream);
    }

    /// <summary>Opens an image from bytes in memory. See <see cref="Open(Stream, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ReaderOptions.Region"/> has no area or misses the image.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="decode-rows"/>
    /// </example>
    public static RasterDecoder Open(ReadOnlyMemory<byte> data, EgamiContext? context = null, ReaderOptions? options = null)
    {
        MemoryStream stream = Raster.AsStream(data);
        return Open(stream, context, options, owned: stream);
    }

    /// <summary>
    /// Fills <paramref name="band"/> from its top row with the next rows of the current frame, converting while decoding.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to produce; it may differ from call to call.</typeparam>
    /// <param name="band">Where to write: <see cref="Info"/>'s width, any height. Only its first frame is written.</param>
    /// <returns>The rows written; 0 at the end of the frame. Call <see cref="NextFrame"/> to go on to the next frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="band"/> is not <see cref="Info"/>'s width.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public int ReadRows<TPixel>(Raster<TPixel> band)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(band);
        if (band.Width != Info.Width)
        {
            throw new ArgumentException($"The band is {band.Width} pixels wide but the image is {Info.Width}.", nameof(band));
        }

        Start();
        if (_frame is null)
        {
            return 0;
        }

        int rows = Math.Min(band.Height, _stage.Remaining);
        for (int y = 0; y < rows; y++)
        {
            _stage.Read(band.Row(y));
        }

        EndFrameIfDone();
        return rows;
    }

    /// <summary>Moves past rows of the current frame, decoding them only where the format requires it.</summary>
    /// <param name="count">The rows to skip.</param>
    /// <returns>The rows skipped, fewer than <paramref name="count"/> at the end of the frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public int SkipRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Start();
        if (_frame is null)
        {
            return 0;
        }

        int skipped = Math.Min(count, _stage.Remaining);
        _stage.Skip(skipped);
        EndFrameIfDone();
        return skipped;
    }

    /// <summary>Moves to the next frame, leaving any rows of the current one unread.</summary>
    /// <returns><see langword="true"/> if there is another frame; <see langword="false"/> after the last.</returns>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public bool NextFrame()
    {
        Start();
        if (_frame is not null)
        {
            _source.EndFrame();
            _frame = _source.NextFrame();
            _stage.Reset();
        }

        return _frame is not null;
    }

    /// <summary>
    /// Decodes the rest of the current frame into <paramref name="target"/>, each row at its own place, and moves to the next frame.
    /// Animations come fully composed, the same pixels <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/> gives.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="target">Where to write: <see cref="Info"/>'s size. Only its first frame is written.</param>
    /// <returns>The frame's metadata, or <see langword="null"/> after the last frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not <see cref="Info"/>'s size.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="frames"/>
    /// </example>
    public FrameMetadata? ReadFrame<TPixel>(Raster<TPixel> target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(target);
        CheckSize(target, nameof(target));
        Start();
        if (_frame is null)
        {
            return null;
        }

        FrameMetadata frame = FrameOut(_frame);
        while (_stage.Remaining > 0)
        {
            _stage.Read(target.Row(_stage.Row));
        }

        NextFrame();
        return frame;
    }

    /// <summary>Decodes the rest of the current frame into a new raster and moves to the next frame. See <see cref="ReadFrame{TPixel}(Raster{TPixel})"/>.</summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <returns>The frame, with a copy of <see cref="Metadata"/>, or <see langword="null"/> after the last frame. The caller must dispose it.</returns>
    /// <exception cref="LimitExceededException">The raster would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public Raster<TPixel>? ReadFrame<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Start();
        if (_frame is null)
        {
            return null;
        }

        PixelPlane<TPixel> plane = Allocate<TPixel>();
        Raster<TPixel> raster = new([new Frame<TPixel>(plane, FrameOut(_frame))], Metadata.Copy(), _context);
        try
        {
            while (_stage.Remaining > 0)
            {
                _stage.Read(raster.Row(_stage.Row));
            }
        }
        catch
        {
            raster.Dispose();
            throw;
        }

        NextFrame();
        return raster;
    }

    /// <summary>Returns the source's buffers and closes the file when the decoder opened it.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _source.Dispose();
            _owned?.Dispose();
        }
    }

    // Opens a decoder over the stream, disposing `owned` with it.
    internal static RasterDecoder Open(Stream stream, EgamiContext? context, ReaderOptions? options, IDisposable? owned)
    {
        try
        {
            context ??= EgamiContext.Default;
            options ??= ReaderOptions.Default;
            (RowSource source, FormatInfo format) = FormatPipeline.OpenSource(stream, context, options, CancellationToken.None);
            try
            {
                return new RasterDecoder(source, format, context, options, owned);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
        catch
        {
            owned?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes every remaining frame into one raster, as Load returns it. A buffered image that needs no change is handed over
    /// without a copy.
    /// </summary>
    internal Raster<TPixel> ReadAll<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (!_started && !_plan.ChangesGeometry && _source is BufferedRowSource { Raster: Raster<TPixel> })
        {
            return (Raster<TPixel>)((BufferedRowSource)_source).Take();
        }

        List<Frame<TPixel>> frames = [];
        try
        {
            Start();
            while (_frame is not null)
            {
                Frame<TPixel> frame = new(Allocate<TPixel>(), FrameOut(_frame));
                frames.Add(frame);
                while (_stage.Remaining > 0)
                {
                    _stage.Read(frame.Plane.Row(_stage.Row));
                }

                NextFrame();
            }

            if (frames.Count == 0)
            {
                throw new InvalidOperationException("The options select no frame.");
            }

            return new Raster<TPixel>(frames, Metadata.Copy(), _context);
        }
        catch
        {
            foreach (Frame<TPixel> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }
    }

    /// <summary>Decodes every remaining frame into one raster in the natural pixel type. See <see cref="ReadAll{TPixel}"/>.</summary>
    internal Raster ReadAll()
    {
        return _source.WithNaturalType(new ReadAllVisitor(this));
    }

    /// <summary>Throws unless <paramref name="target"/> is <see cref="Info"/>'s size.</summary>
    internal void CheckSize(Raster target, string name)
    {
        if (target.Width != Info.Width || target.Height != Info.Height)
        {
            throw new ArgumentException($"The target is {target.Width}×{target.Height} but the image decodes to {Info.Width}×{Info.Height}.", name);
        }
    }

    private void EndFrameIfDone()
    {
        if (_stage.Remaining == 0)
        {
            _source.EndFrame();
        }
    }

    private void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            _started = true;
            _frame = _source.NextFrame();
        }
    }

    private PixelPlane<TPixel> Allocate<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        BudgetedMemorySource memory = new(_context.Memory, _context.Limits.MaxTotalBytes);
        memory.Check((long)Info.Width * Info.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
        return PixelPlane.Allocate<TPixel>(Info.Width, Info.Height, memory);
    }

    /// <summary>Returns a copy of a frame's metadata, without the stored layout when the region or reduction moved its pixels.</summary>
    private FrameMetadata FrameOut(FrameMetadata frame)
    {
        FrameMetadata copy = frame.Copy();
        if (_plan.ChangesGeometry)
        {
            copy.Bounds = null;
            copy.FormatDetails = null;
        }

        return copy;
    }

    private sealed class ReadAllVisitor(RasterDecoder decoder) : INaturalTypeVisitor<Raster>
    {
        public Raster Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return decoder.ReadAll<TPixel>();
        }
    }
}
