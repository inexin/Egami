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
    private readonly EgamiContext _context;
    private readonly ReaderOptions _options;
    private readonly IDisposable? _owned;
    private readonly AppendBuffer? _append;
    private readonly CancellationToken _cancellationToken;
    private EgamiInput? _input;
    private EgamiInput? _incremental;
    private Stream? _feed;
    private byte[]? _feedBuffer;
    private RowSource? _source;
    private ReadPlan? _plan;
    private RowStage? _stage;
    private RasterInfo? _info;
    private FrameMetadata? _frame;
    private RasterMetadata? _upkept;
    private RasterMetadata? _warnings;
    private DiagnosticReporter? _diagnostics;
    private bool _upkeptComplete;
    private bool _started;
    private bool _advancing;
    private bool _waiting;
    private long _appendedSinceWait;
    private long _neededSinceWait;
    private bool _disposed;

    private RasterDecoder(RowSource source, FormatInfo format, EgamiContext context, ReaderOptions options, IDisposable? owned)
    {
        _context = context;
        _options = options;
        _owned = owned;
        Initialize(source, format);
    }

    private RasterDecoder(EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        _context = context;
        _options = options;
        _cancellationToken = cancellationToken;
        _append = new AppendBuffer();
    }

    /// <summary>Sets the decoder up over an opened source: the plan, the row stage and what <see cref="Info"/> says.</summary>
    private void Initialize(RowSource source, FormatInfo format)
    {
        // The stored size is checked in every mode, before any row: a small file can declare billions of pixels, and streaming
        // them would cost the time a full decode's allocation check saves.
        _context.Limits.CheckFrameSize(source.StoredWidth, source.StoredHeight);
        _source = source;
        _warnings = source.Metadata;
        ReaderOptions options = _options;
        _plan = ReadPlan.Create(source.StoredWidth, source.StoredHeight, options.Region, options.MinimumSize, source.MaxNativeScale);
        if (_plan.NativeScale != source.NativeScale)
        {
            throw new InvalidOperationException($"The reader decoded at 1/{source.NativeScale} where the plan asks for 1/{_plan.NativeScale}.");
        }

        _stage = new RowStage(source, _plan);
        PixelLayoutInfo layout = source.Layout;
        if (options.ConvertColorTo is Colors.ColorProfile target)
        {
            Colors.ColorProfile stored = source.Metadata.GetColorProfile();
            _stage.Color = source.WithNaturalType(new ColorRows.Factory(stored, source.Metadata.Hdr, target, _context));
            layout = WithNaturalType(new LayoutVisitor());
            _context.Emit(ref _diagnostics, DiagnosticLevel.Info, "Color.ConvertedOnLoad", $"The pixels are converted from {stored.Name} to {target.Name} as they are read.");
        }

        if (_plan.NativeScale > 1 || _plan.Factor > 1)
        {
            _context.Emit(ref _diagnostics, DiagnosticLevel.Info, "Decode.ScaleChosen", $"For ReaderOptions.MinimumSize the {format.Name} reader decodes at 1/{_plan.NativeScale} ({_plan.ScaledSize.Width}Ã—{_plan.ScaledSize.Height}), then averages {_plan.Factor}Ã—{_plan.Factor} blocks to {_plan.Output.Width}Ã—{_plan.Output.Height}.");
        }

        RasterMetadata metadata = Metadata;
        Size output = _plan.Output;
        _info = new RasterInfo
        {
            Format = format,
            Width = output.Width,
            Height = output.Height,
            Layout = layout,
            FrameCount = source.FrameCount,
            HasMetadata = metadata.Exif is not null || metadata.HasIptc || !metadata.IccProfile.IsEmpty || !metadata.Xmp.IsEmpty || metadata.Text.Count > 0,
            EstimatedBytes = (long)output.Width * output.Height * layout.BytesPerPixel * (source.FrameCount ?? 1),
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
    /// <exception cref="InvalidOperationException">An incremental decode has not received the header yet; see <see cref="HeaderRead"/>.</exception>
    public RasterInfo Info => _info ?? throw NoHeader();

    /// <summary>Gets whether the header has been read, so <see cref="Info"/> and <see cref="Metadata"/> can be used. Always true unless the decode is incremental.</summary>
    public bool HeaderRead => _source is not null;

    /// <summary>
    /// Gets where the decode stands: rows can be read, it needs more bytes (incremental decodes only), the current frame has been
    /// read, or every frame has.
    /// </summary>
    public DecodeStatus Status
    {
        get
        {
            if (_source is null || _waiting)
            {
                return DecodeStatus.NeedsInput;
            }

            if (_started && _frame is null)
            {
                return DecodeStatus.Completed;
            }

            return _started && (_advancing || _stage!.Remaining == 0) ? DecodeStatus.FrameEnded : DecodeStatus.RowsAvailable;
        }
    }

    /// <summary>
    /// Gets the metadata read so far; see <see cref="IsMetadataComplete"/>. When the region or reduction changes the size, the
    /// EXIF dimensions follow it and the EXIF thumbnail is dropped, as a crop or resize would.
    /// </summary>
    /// <exception cref="InvalidOperationException">An incremental decode has not received the header yet; see <see cref="HeaderRead"/>.</exception>
    public RasterMetadata Metadata
    {
        get
        {
            RowSource source = _source ?? throw NoHeader();
            Colors.ColorProfile? target = _options.ConvertColorTo;
            if (!_plan!.ChangesGeometry && target is null)
            {
                return source.Metadata;
            }

            if (_upkept is null || _upkeptComplete != source.MetadataComplete)
            {
                _upkeptComplete = source.MetadataComplete;
                _upkept = source.Metadata.Copy();
                if (_plan.ChangesGeometry)
                {
                    ExifUpkeep.Dimensions(_upkept, _plan.Output);
                    ExifUpkeep.DropThumbnail(_upkept);
                }

                if (target is not null)
                {
                    // The rows are converted, so the metadata says what they now mean, as ConvertColor records it.
                    _upkept.SetColorProfile(target);
                }
            }

            return _upkept;
        }
    }

    /// <summary>
    /// Gets whether <see cref="Metadata"/> holds everything the file has. PNG and GIF may put text, EXIF and comments after the
    /// image data, which arrive once the last frame has been read.
    /// </summary>
    public bool IsMetadataComplete => _source?.MetadataComplete ?? false;

    /// <summary>Gets the metadata of the frame being read, or <see langword="null"/> after the last frame (or while an incremental decode waits for it).</summary>
    public FrameMetadata? CurrentFrame
    {
        get
        {
            return Run(() => Start(), finishMove: false) && _frame is not null && !_advancing ? FrameOut(_frame) : null;
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
    /// Opens an image of a known format from a stream without detecting it, for data that has no signature of its own. See
    /// <see cref="Open(Stream, EgamiContext?, ReaderOptions?)"/>.
    /// </summary>
    /// <param name="stream">The encoded image, read from its current position and left open.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ReaderOptions.Region"/> has no area or misses the image.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterDecoder Open(Stream stream, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        return Open(stream, context, Raster.WithFormat(format, options), owned: null);
    }

    /// <summary>Opens an image of a known format from bytes in memory. See <see cref="Open(Stream, FormatInfo, EgamiContext?, ReaderOptions?)"/>.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="format">The format to read with. It need not be registered in the context.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException"><paramref name="format"/> has no reader.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format's rules, including data of another format.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ReaderOptions.Region"/> has no area or misses the image.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="named-format"/>
    /// </example>
    public static RasterDecoder Open(ReadOnlyMemory<byte> data, FormatInfo format, EgamiContext? context = null, ReaderOptions? options = null)
    {
        ReaderOptions named = Raster.WithFormat(format, options);
        MemoryStream stream = Raster.AsStream(data);
        return Open(stream, context, named, owned: stream);
    }

    /// <summary>
    /// Starts an incremental decode: bytes are given with <see cref="Append"/> as they arrive, and rows, frames and previews come
    /// out as far as the bytes reach. <see cref="Status"/> says when more bytes are needed and <see cref="HeaderRead"/> when
    /// <see cref="Info"/> is known; <see cref="EndOfInput"/> says the last byte has come.
    /// </summary>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <remarks>
    /// Every 1.x format decodes as its bytes arrive, keeping only the bytes since the step it is in (an MCU row, a PNG chunk or
    /// scanline, a GIF block, a row). Files that are decoded into a buffer first (PFM, plain-text PNM, bottom-up BMP and TGA, a
    /// 32-bit BMP whose alpha may not count, TGA alpha set by a footer) and formats from plugins wait for
    /// <see cref="EndOfInput"/> and are then read whole.
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="incremental"/>
    /// </example>
    public static RasterDecoder OpenIncremental(EgamiContext? context = null, ReaderOptions? options = null)
    {
        return OpenIncrementalCore(context ?? EgamiContext.Default, options ?? ReaderOptions.Default, CancellationToken.None);
    }

    /// <summary>
    /// Opens an image from a stream read asynchronously: the header is read before this returns, and
    /// <see cref="ReadRowsAsync"/>, <see cref="ReadFrameAsync"/> and <see cref="NextFrameAsync"/> read on as they need. Only the
    /// bytes since the decode's last step are held, not the whole file; formats from plugins, and files decoded into a buffer
    /// first, gather the file and are read at the end. The stream is left open; the decoder does not dispose it.
    /// </summary>
    /// <param name="stream">The encoded image, read from its current position.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <param name="options">How to read, or <see langword="null"/> for <see cref="ReaderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading and, between rows, decoding.</param>
    /// <returns>The decoder, its header read. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="async"/>
    /// </example>
    public static async Task<RasterDecoder> OpenAsync(Stream stream, EgamiContext? context = null, ReaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        RasterDecoder decoder = OpenIncrementalCore(context ?? EgamiContext.Default, options ?? ReaderOptions.Default, cancellationToken);
        try
        {
            decoder.Attach(stream);
            while (!decoder.HeaderRead && await decoder.MoreAsync(cancellationToken).ConfigureAwait(false))
            {
            }

            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fills <paramref name="band"/> with the next rows of the current frame, reading the stream asynchronously as far as they
    /// need. See <see cref="ReadRows"/>.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="band">Where to write: <see cref="Info"/>'s width, any height.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The rows written, at least one unless the frame has ended; 0 at the end of the frame.</returns>
    /// <exception cref="InvalidOperationException">The decoder was not opened with <see cref="OpenAsync"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="async"/>
    /// </example>
    public async ValueTask<int> ReadRowsAsync<TPixel>(Raster<TPixel> band, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        CheckFeed();
        while (true)
        {
            int rows = ReadRows(band);
            if (rows > 0 || Status != DecodeStatus.NeedsInput || _feed is null)
            {
                return rows;
            }

            await MoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes the rest of the current frame into <paramref name="target"/>, reading the stream asynchronously as far as it needs,
    /// and moves to the next frame. See <see cref="ReadFrame{TPixel}(Raster{TPixel})"/>.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to produce.</typeparam>
    /// <param name="target">Where to write: <see cref="Info"/>'s size.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The frame's metadata, or <see langword="null"/> after the last frame.</returns>
    /// <exception cref="InvalidOperationException">The decoder was not opened with <see cref="OpenAsync"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="async"/>
    /// </example>
    public async ValueTask<FrameMetadata?> ReadFrameAsync<TPixel>(Raster<TPixel> target, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        CheckFeed();
        while (true)
        {
            FrameMetadata? frame = ReadFrame(target);
            if (frame is not null || Status != DecodeStatus.NeedsInput || _feed is null)
            {
                return frame;
            }

            await MoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Moves to the next frame, reading the stream asynchronously as far as it needs. See <see cref="NextFrame"/>.</summary>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns><see langword="true"/> if there is another frame; <see langword="false"/> after the last.</returns>
    /// <exception cref="InvalidOperationException">The decoder was not opened with <see cref="OpenAsync"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="async"/>
    /// </example>
    public async ValueTask<bool> NextFrameAsync(CancellationToken cancellationToken = default)
    {
        CheckFeed();
        while (true)
        {
            if (NextFrame())
            {
                return true;
            }

            if (Status != DecodeStatus.NeedsInput || _feed is null)
            {
                return false;
            }

            await MoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts an incremental decode whose steps check <paramref name="cancellationToken"/>.</summary>
    internal static RasterDecoder OpenIncrementalCore(EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        return new RasterDecoder(context, options, cancellationToken);
    }

    /// <summary>The bytes read from an attached stream at a time: large enough that a step (an MCU row) rarely spans two reads.</summary>
    private const int FeedBytes = 256 * 1024;

    /// <summary>Feeds this incremental decoder from <paramref name="stream"/>, read asynchronously by <see cref="MoreAsync"/>.</summary>
    internal void Attach(Stream stream)
    {
        _feed = stream;
        _feedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(FeedBytes);
    }

    /// <summary>
    /// Gets whether the format is known and will wait for the last byte before decoding: a format without incremental decoding,
    /// or a file it decodes into a buffer first.
    /// </summary>
    internal bool WaitsForEnd { get; private set; }

    /// <summary>Gets or sets whether a file that yields no row before its last byte waits for the end too, for a caller with no use for previews.</summary>
    internal bool WholeIfRowsAtEnd { get; set; }

    /// <summary>Gets or sets whether the decoder keeps its warnings from the diagnostics handler, for a decode that only plans another.</summary>
    internal bool Quiet { get; set; }

    /// <summary>Gets the context the decode runs in.</summary>
    internal EgamiContext Context => _context;

    /// <summary>Gets the options the decode reads with.</summary>
    internal ReaderOptions Options => _options;

    /// <summary>
    /// Opens the bytes received and not yet read, in place, which for a decode that waits for the end is the whole file so far.
    /// </summary>
    internal Stream OpenReceived() => new JoinedStream(_input?.BufferedUnread ?? default, _append!.Unread);

    /// <summary>Reads the next chunk of the attached stream into the decoder; at its end, says so and returns false.</summary>
    internal async ValueTask<bool> MoreAsync(CancellationToken cancellationToken)
    {
        if (_feed is not Stream stream || _feedBuffer is not byte[] buffer)
        {
            return false;
        }

        int read = await stream.ReadAsync(buffer.AsMemory(0, FeedBytes), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            _feed = null;
            EndOfInput();
            return false;
        }

        Append(buffer.AsSpan(0, read));
        return true;
    }

    /// <summary>Gets the most received bytes an incremental decode has held at once, before reading them.</summary>
    internal long MostBytesHeld { get; private set; }

    /// <summary>Gets the bytes of the image the decode has consumed, which is where a seekable stream should be left.</summary>
    internal long ConsumedBytes => _incremental?.ImageEnd ?? _incremental?.Position ?? 0;

    /// <summary>The frames an asynchronous load fills as the bytes arrive.</summary>
    internal sealed class LoadFrames<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        public List<Frame<TPixel>> Frames { get; } = [];

        public Frame<TPixel>? Current { get; set; }

        public void Dispose()
        {
            foreach (Frame<TPixel> frame in Frames)
            {
                frame.Plane.Dispose();
            }

            Frames.Clear();
        }
    }

    /// <summary>
    /// Decodes as much as the bytes so far allow into <paramref name="frames"/>, starting a new frame raster as each frame starts.
    /// Returns whether every frame is done.
    /// </summary>
    internal bool Pump<TPixel>(LoadFrames<TPixel> frames)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        while (true)
        {
            if (!Run(() => Start()))
            {
                return false;
            }

            if (_frame is null)
            {
                return true;
            }

            if (frames.Current is null)
            {
                frames.Current = new Frame<TPixel>(Allocate<TPixel>(), FrameOut(_frame));
                frames.Frames.Add(frames.Current);
            }

            Frame<TPixel> current = frames.Current;
            if (!Run(() =>
            {
                while (_stage!.Remaining > 0)
                {
                    _stage.Read(current.Plane.Row(_stage.Row));
                }
            }))
            {
                return false;
            }

            frames.Current = null;
            _advancing = true;
            if (!Run(() => { }))
            {
                return false;
            }
        }
    }

    /// <summary>Returns the frames an asynchronous load filled as one raster, as Load returns it.</summary>
    internal Raster<TPixel> Finish<TPixel>(LoadFrames<TPixel> frames)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (frames.Frames.Count == 0)
        {
            throw new InvalidOperationException("The options select no frame.");
        }

        Raster<TPixel> raster = new([.. frames.Frames], Metadata.Copy(), _context);
        frames.Frames.Clear();
        return raster;
    }

    /// <summary>Calls <paramref name="visitor"/> with the natural pixel type, once the header has been read.</summary>
    internal TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
    {
        RowSource source = _source ?? throw NoHeader();
        return _options.ConvertColorTo is Colors.ColorProfile target
            ? source.WithNaturalType(new ConvertedTypeVisitor<TResult>(target, visitor))
            : source.WithNaturalType(visitor);
    }

    /// <summary>Passes on the type a color-converting load gives for the source's natural type.</summary>
    private sealed class ConvertedTypeVisitor<TResult>(Colors.ColorProfile target, INaturalTypeVisitor<TResult> visitor) : INaturalTypeVisitor<TResult>
    {
        public TResult Visit<TNatural>()
            where TNatural : unmanaged, IPixelType<TNatural>
        {
            return ColorLoad.WithConvertedType<TNatural, TResult>(target, visitor);
        }
    }

    private sealed class LayoutVisitor : INaturalTypeVisitor<PixelLayoutInfo>
    {
        public PixelLayoutInfo Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel> => TPixel.Layout;
    }

    private void CheckFeed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_feedBuffer is null)
        {
            throw new InvalidOperationException("Only a decoder from OpenAsync reads its stream asynchronously.");
        }
    }

    /// <summary>Gives an incremental decode the next bytes of the file, and reads the header once it has all arrived.</summary>
    /// <param name="data">The bytes, in order after those appended before. They are copied.</param>
    /// <exception cref="InvalidOperationException">The decoder is not incremental, or <see cref="EndOfInput"/> was called.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules.</exception>
    /// <exception cref="LimitExceededException">The header breaks a safety limit.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public void Append(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppendBuffer append = _append ?? throw new InvalidOperationException("Only a decoder from OpenIncremental takes appended bytes.");
        int capacity = append.Capacity;
        append.Append(data);
        if (append.Capacity > capacity)
        {
            _context.Emit(ref _diagnostics, DiagnosticLevel.Debug, "Streaming.BufferGrown", $"The incremental buffer grew to {append.Capacity:N0} bytes to hold the {append.Available:N0} bytes not yet decoded.");
        }
        _appendedSinceWait += data.Length;
        MostBytesHeld = Math.Max(MostBytesHeld, append.Available);
        if (append.Available > _context.Limits.MaxTotalBytes)
        {
            // Bytes held while waiting (for a header, or for the end of a file decoded at once) count like pixels.
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), _context.Limits.MaxTotalBytes, append.Available);
        }

        _waiting = _waiting && _appendedSinceWait < _neededSinceWait;
        TryOpen();
    }

    /// <summary>Says the last byte has been appended: from now on, missing bytes are the end of the file, as in a normal read.</summary>
    /// <exception cref="InvalidOperationException">The decoder is not incremental.</exception>
    /// <exception cref="UnsupportedFormatException">No registered format matches the data.</exception>
    /// <exception cref="CorruptDataException">The header breaks the format's rules or is cut off.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public void EndOfInput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppendBuffer append = _append ?? throw new InvalidOperationException("Only a decoder from OpenIncremental takes EndOfInput.");
        append.End();
        _waiting = false;
        _appendedSinceWait = 0;
        _neededSinceWait = 0;
        TryOpen();
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
        if (_source is null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return 0;
        }

        if (band.Width != Info.Width)
        {
            throw new ArgumentException($"The band is {band.Width} pixels wide but the image is {Info.Width}.", nameof(band));
        }

        int rows = 0;
        Run(() =>
        {
            Start();
            if (_frame is null || _advancing)
            {
                return;
            }

            int count = Math.Min(band.Height, _stage!.Remaining);
            for (; rows < count; rows++)
            {
                _stage.Read(band.Row(rows));
            }

            EndFrameIfDone();
        }, finishMove: false);
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
        int skipped = 0;
        Run(() =>
        {
            Start();
            if (_frame is null || _advancing)
            {
                return;
            }

            skipped = Math.Min(count, _stage!.Remaining);
            _stage.Skip(skipped);
            EndFrameIfDone();
        }, finishMove: false);
        return skipped;
    }

    /// <summary>Moves to the next frame, leaving any rows of the current one unread.</summary>
    /// <returns><see langword="true"/> if there is another frame; <see langword="false"/> after the last.</returns>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    public bool NextFrame()
    {
        // A move that ReadFrame started and had to wait on is finished first, and is the move asked for.
        bool pending = _advancing;
        bool done = Run(() =>
        {
            Start();
            if (!pending && _frame is not null)
            {
                Advance();
            }
        });
        return done && _frame is not null;
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
        if (_source is null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return null;
        }

        CheckSize(target, nameof(target));
        FrameMetadata? frame = null;
        Run(() =>
        {
            Start();
            if (_frame is null || _advancing)
            {
                return;
            }

            while (_stage!.Remaining > 0)
            {
                _stage.Read(target.Row(_stage.Row));
            }

            // The frame is complete: it is handed out even if moving on to the next one has to wait for more bytes.
            frame = FrameOut(_frame);
            _advancing = true;
            Advance();
        });
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
        if (_source is null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return null;
        }

        if (!Run(() => Start()) || _frame is null || _advancing)
        {
            return null;
        }

        PixelPlane<TPixel> plane = Allocate<TPixel>();
        Raster<TPixel> raster = new([new Frame<TPixel>(plane, FrameOut(_frame))], Metadata.Copy(), _context);
        try
        {
            if (!Run(() =>
            {
                while (_stage!.Remaining > 0)
                {
                    _stage.Read(raster.Row(_stage.Row));
                }
            }))
            {
                // An incremental decode keeps its place; ReadFrame(target) gives the rows already read.
                raster.Dispose();
                return null;
            }
        }
        catch
        {
            raster.Dispose();
            throw;
        }

        // Moving to the next frame is the pending move that Run finishes first; an incremental decode may leave it for later.
        _advancing = true;
        Run(() => { });
        return raster;
    }

    /// <summary>
    /// Draws what is known of the frame in progress: an interlaced PNG from the passes received so far, a progressive JPEG from its
    /// scans so far, a GIF frame from the rows received so far (interlaced ones repeated over the rows they stand for), each later
    /// pass sharpening it. Region and
    /// reduction apply as to rows. The decode does not move on.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type to draw in.</typeparam>
    /// <param name="target">Where to draw: <see cref="Info"/>'s size. Only its first frame is written.</param>
    /// <returns>Whether a preview was drawn; <see langword="false"/> before the header or for files that hand out finished rows instead.</returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not <see cref="Info"/>'s size.</exception>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="incremental"/>
    /// </example>
    public bool RenderPreview<TPixel>(Raster<TPixel> target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not { CanPreview: true } source)
        {
            return false;
        }

        CheckSize(target, nameof(target));
        RowStage stage = new(new PreviewSource(source), _plan!);
        for (int y = 0; y < target.Height; y++)
        {
            stage.Read(target.Row(y));
        }

        return true;
    }

    /// <summary>Returns the source's buffers and closes the file when the decoder opened it.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            RasterMetadata? warnings = Quiet ? null : _warnings;
            _source?.Dispose();
            _input?.Dispose();
            _append?.Dispose();
            _owned?.Dispose();
            byte[]? buffer = Interlocked.Exchange(ref _feedBuffer, null);
            if (buffer is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            _diagnostics?.Complete();
            if (warnings is not null && _context.Diagnostics is Action<EgamiDiagnostic> handler)
            {
                // Warnings are sent once the session ends, from the metadata as it ends up: a reader may still take one back or
                // move them to new metadata while it waits for more input. Last, so a handler that throws leaks nothing.
                warnings.BindWarnings(handler);
                warnings.WarningList.Flush();
            }
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
        if (!_started && !_plan!.ChangesGeometry && _source is BufferedRowSource { Raster: Raster<TPixel> })
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
                while (_stage!.Remaining > 0)
                {
                    _stage.Read(frame.Plane.Row(_stage.Row));
                }

                Advance();
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
        return WithNaturalType(new ReadAllVisitor(this));
    }

    /// <summary>Throws unless <paramref name="target"/> is <see cref="Info"/>'s size.</summary>
    internal void CheckSize(Raster target, string name)
    {
        if (target.Width != Info.Width || target.Height != Info.Height)
        {
            throw new ArgumentException($"The target is {target.Width}×{target.Height} but the image decodes to {Info.Width}×{Info.Height}.", name);
        }
    }

    private static InvalidOperationException NoHeader()
    {
        return new InvalidOperationException("The header has not arrived yet; append more bytes until HeaderRead is true.");
    }

    /// <summary>
    /// Runs a step of the decode. In an incremental decode the step can run out of bytes: it then leaves the decode where it was,
    /// <see cref="Status"/> says <see cref="DecodeStatus.NeedsInput"/>, and the result is <see langword="false"/>.
    /// </summary>
    private bool Run(Action step, bool finishMove = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is null)
        {
            return false;
        }

        if (_waiting)
        {
            // Still too few new bytes since the last step ran out to make trying again worth it.
            return false;
        }

        long before = _incremental?.Position ?? 0;
        try
        {
            // A move to the next frame that had to wait is finished by the frame-level calls; row calls leave it to NextFrame.
            if (_advancing && finishMove)
            {
                Advance();
            }

            step();
            return true;
        }
        catch (NeedsInputException)
        {
            _waiting = true;
            _appendedSinceWait = 0;
            _neededSinceWait = Math.Max(1, ((_incremental?.Position ?? 0) - before) / 2);
            return false;
        }
    }

    /// <summary>Finishes the current frame and moves to the next; an incremental decode may resume it after more bytes.</summary>
    private void Advance()
    {
        _advancing = true;
        _source!.EndFrame();
        _frame = _source.NextFrame();
        _stage!.Reset();
        _advancing = false;
    }

    /// <summary>Opens the source of an incremental decode once the format can be told and its header has arrived.</summary>
    private void TryOpen()
    {
        if (_source is not null)
        {
            return;
        }

        AppendBuffer append = _append!;
        if (WaitsForEnd && !append.Ended)
        {
            // Known to wait for the last byte: nothing to look at until then.
            return;
        }

        EgamiInput input = _input ??= new EgamiInput(append, _context, _cancellationToken);
        _incremental = input;
        int signature = 0;
        foreach (FormatInfo known in _context.Formats)
        {
            signature = Math.Max(signature, known.SignatureLength);
        }

        if (!append.Ended && input.Available < signature)
        {
            return;
        }

        FormatInfo format = FormatPipeline.Detect(input, _context, _options);
        input.SetFormat(format);
        if (!append.Ended)
        {
            // Formats that cannot decode while bytes arrive, and files they decode into a buffer first, wait for the end.
            HeaderState state = format.Reader is IIncrementalFormatReader incremental ? incremental.HeaderStatus(input, _options) : HeaderState.WaitsForEnd;
            WaitsForEnd = state == HeaderState.WaitsForEnd || (state == HeaderState.ArrivedRowsAtEnd && WholeIfRowsAtEnd);
            if (WaitsForEnd || state == HeaderState.NotYet)
            {
                return;
            }
        }

        _input = null;
        RowSource source = FormatPipeline.OpenSource(input, format, _options);
        try
        {
            Initialize(source, format);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void EndFrameIfDone()
    {
        if (_stage!.Remaining == 0)
        {
            _source!.EndFrame();
        }
    }

    private void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            _frame = _source!.NextFrame();
            _started = true;
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
        if (_plan!.ChangesGeometry)
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

/// <summary>Where a <see cref="RasterDecoder"/> stands, as <see cref="RasterDecoder.Status"/> reports it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Streaming.cs" region="incremental"/>
/// </example>
public enum DecodeStatus
{
    /// <summary>Rows of the current frame can be read.</summary>
    RowsAvailable,

    /// <summary>An incremental decode needs more bytes to go on: the header, or the next rows or frame. Append them.</summary>
    NeedsInput,

    /// <summary>Every row of the current frame has been read; <see cref="RasterDecoder.NextFrame"/> moves to the next.</summary>
    FrameEnded,

    /// <summary>Every frame has been read.</summary>
    Completed,
}
