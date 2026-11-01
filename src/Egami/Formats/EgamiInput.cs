using System.Buffers;
using System.Buffers.Binary;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats;

/// <summary>
/// The data a reader decodes: the caller's stream behind a pooled 64 KB buffer, with exact-read helpers.
/// It also carries the context, limits and cancellation token for the load, and is the only way a reader allocates pixel memory.
/// </summary>
/// <remarks>Readers never call the stream directly. Egami never disposes the caller's stream.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-format"/>
/// </example>
public sealed class EgamiInput : IDisposable
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Reads at least this large skip the buffer when it is empty.</summary>
    private const int DirectReadBytes = 4 * 1024;

    private readonly Stream _stream;

    private readonly BudgetedMemorySource _memory;
    private readonly long _origin;
    private byte[]? _buffer;
    private int _start;
    private int _end;
    private long _position;
    private string _formatName = "image";
    private DiagnosticReporter? _diagnostics;

    // The bytes of an incremental decode, which are the stream itself.
    private Streaming.AppendBuffer? Append => _stream as Streaming.AppendBuffer;

    /// <summary>
    /// Creates the input of a load a plug-in runs itself, outside <see cref="Raster.Load(Stream, EgamiContext?, ReaderOptions?)"/>:
    /// a format package that reads a file to convert it, say. Dispose it when the load ends.
    /// </summary>
    /// <param name="stream">The data, from its current position. It is left open.</param>
    /// <param name="context">The context of the load: its limits, memory and diagnostics.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public EgamiInput(Stream stream, EgamiContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        _stream = stream;
        Context = context;
        CancellationToken = cancellationToken;
        _memory = new BudgetedMemorySource(context.Memory, context.Limits.MaxTotalBytes);
        _origin = stream.CanSeek ? stream.Position : -1;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>Creates the input of an incremental decode, which reads the bytes appended so far.</summary>
    internal EgamiInput(Streaming.AppendBuffer append, EgamiContext context, CancellationToken cancellationToken)
        : this((Stream)append, context, cancellationToken)
    {
    }

    /// <summary>Gets the context of the load.</summary>
    public EgamiContext Context { get; }

    /// <summary>
    /// Gets the load's link to <see cref="EgamiContext.Diagnostics"/>, for a reader's Info and Debug events. Warnings go through
    /// <see cref="Warn(Raster, string, string, long?)"/>, so they are also listed in <see cref="RasterMetadata.Warnings"/>.
    /// </summary>
    public DiagnosticReporter Diagnostics =>
        _diagnostics ?? Interlocked.CompareExchange(ref _diagnostics, DiagnosticReporter.Start(Context), null) ?? _diagnostics;

    /// <summary>Gets whether events at <paramref name="level"/> reach a handler, so a site builds them only then.</summary>
    internal bool Reports(DiagnosticLevel level) => Context.Diagnostics is not null && level >= Context.DiagnosticLevel;

    /// <summary>Gets the safety limits of the load.</summary>
    public SafetyLimits Limits => Context.Limits;

    /// <summary>Gets the token that cancels the load. Readers check it between rows.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the number of bytes consumed since the start of the image.</summary>
    public long Position => _position;

    /// <summary>Gets the name of the format being read, used in <see cref="CorruptDataException"/> messages.</summary>
    public string FormatName => _formatName;

    /// <summary>
    /// Returns up to <paramref name="count"/> upcoming bytes without consuming them. The span is shorter only at the end of the data.
    /// It is valid until the next read.
    /// </summary>
    /// <param name="count">How many bytes to look at, at most 64 KB.</param>
    /// <returns>The upcoming bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or above 64 KB.</exception>
    public ReadOnlySpan<byte> Peek(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, BufferSize);
        Fill(count);
        return Buffer.AsSpan(_start, Math.Min(count, _end - _start));
    }

    /// <summary>Reads as many bytes as are available, up to the length of <paramref name="destination"/>.</summary>
    /// <param name="destination">Where to put the bytes.</param>
    /// <returns>The number of bytes read. Less than requested only at the end of the data.</returns>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_start == _end && destination.Length - total >= DirectReadBytes)
            {
                // A large read with nothing buffered goes straight from the stream into the destination: one copy, not two.
                CancellationToken.ThrowIfCancellationRequested();
                int read = _stream.Read(destination[total..]);
                if (read == 0)
                {
                    break;
                }

                _position += read;
                total += read;
                continue;
            }

            if (_start == _end && !Fill(1))
            {
                break;
            }

            int n = Math.Min(destination.Length - total, _end - _start);
            Buffer.AsSpan(_start, n).CopyTo(destination[total..]);
            _start += n;
            _position += n;
            total += n;
        }

        return total;
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes.</summary>
    /// <param name="destination">Where to put the bytes.</param>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public void ReadExactly(Span<byte> destination)
    {
        long start = _position;
        if (Read(destination) < destination.Length)
        {
            throw Truncated(start);
        }
    }

    /// <summary>Skips <paramref name="count"/> bytes.</summary>
    /// <param name="count">How many bytes to skip.</param>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public void Skip(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long start = _position;
        while (count > 0)
        {
            if (_start == _end && !Fill(1))
            {
                throw Truncated(start);
            }

            int n = (int)Math.Min(count, _end - _start);
            _start += n;
            _position += n;
            count -= n;
        }
    }

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    /// <exception cref="CorruptDataException">The data has ended.</exception>
    public byte ReadByte()
    {
        if (_start == _end && !Fill(1))
        {
            throw Truncated(_position);
        }

        _position++;
        return Buffer[_start++];
    }

    /// <summary>Reads a big-endian 16-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public ushort ReadUInt16BigEndian()
    {
        return BinaryPrimitives.ReadUInt16BigEndian(ReadSmall(2));
    }

    /// <summary>Reads a little-endian 16-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public ushort ReadUInt16LittleEndian()
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(ReadSmall(2));
    }

    /// <summary>Reads a big-endian 32-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public uint ReadUInt32BigEndian()
    {
        return BinaryPrimitives.ReadUInt32BigEndian(ReadSmall(4));
    }

    /// <summary>Reads a little-endian 32-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public uint ReadUInt32LittleEndian()
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(ReadSmall(4));
    }

    /// <summary>
    /// Creates the raster a reader decodes into, after checking the size against <see cref="SafetyLimits"/> and the memory budget.
    /// Unless <paramref name="clear"/> asks for it, pixels are not cleared and the reader must write every one.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="width">The width from the header.</param>
    /// <param name="height">The height from the header.</param>
    /// <param name="clear">Whether to zero the pixels first. Readers that write every pixel skip it.</param>
    /// <returns>A one-frame raster. The reader owns it until it returns it, and must dispose it if decoding fails.</returns>
    /// <exception cref="LimitExceededException">The size breaks a safety limit.</exception>
    /// <exception cref="CorruptDataException">The width or height is zero or negative.</exception>
    public Raster<TPixel> CreateRaster<TPixel>(long width, long height, ClearMode clear = ClearMode.None)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (width < 1 || height < 1)
        {
            throw new CorruptDataException(FormatName, -1, $"The image size {width}×{height} is invalid.");
        }

        Limits.CheckFrameSize(width, height);
        _memory.Check(width * height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
        PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>((int)width, (int)height, _memory, clear);
        return new Raster<TPixel>(plane, new RasterMetadata { FormatName = FormatName }, Context);
    }

    /// <summary>
    /// Appends a new frame the raster's size to a raster this load created with <see cref="CreateRaster"/>, for readers of animated
    /// formats. The frame counts against <see cref="SafetyLimits.MaxFrames"/> and the load's memory budget. Unless
    /// <paramref name="clear"/> asks for it, its pixels are not cleared.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="raster">The raster being read.</param>
    /// <param name="clear">Whether to zero the pixels first.</param>
    /// <returns>The new frame, last in <see cref="Raster{TPixel}.Frames"/>.</returns>
    /// <exception cref="LimitExceededException">The frame count or the memory budget would be exceeded.</exception>
    public Frame<TPixel> AddFrame<TPixel>(Raster<TPixel> raster, ClearMode clear = ClearMode.None)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        Limits.CheckFrameCount(raster.FrameCount + 1L);
        _memory.Check((long)raster.Width * raster.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
        PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(raster.Width, raster.Height, _memory, clear);
        Frame<TPixel> frame = new(plane, new FrameMetadata());
        raster.Frames.AddOwned(frame);
        return frame;
    }

    /// <summary>
    /// Rents scratch memory (a canvas row, a previous frame, a decompression buffer) counted against the load's memory budget
    /// with the raster. Dispose it when done.
    /// </summary>
    /// <param name="byteLength">The size in bytes.</param>
    /// <param name="clear">Whether to zero it.</param>
    /// <returns>The memory.</returns>
    /// <exception cref="LimitExceededException">The memory budget would be exceeded.</exception>
    public IMemoryOwner<byte> RentScratch(int byteLength, ClearMode clear = ClearMode.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        return _memory.Allocate(byteLength, clear);
    }

    /// <summary>Checks that <paramref name="bytes"/> more would fit the load's memory budget, without allocating.</summary>
    /// <param name="bytes">The bytes about to be needed.</param>
    /// <exception cref="LimitExceededException">They would not fit.</exception>
    public void CheckMemory(long bytes)
    {
        _memory.Check(bytes);
    }

    /// <summary>
    /// Checks a frame size against <see cref="SafetyLimits.MaxWidth"/>, <see cref="SafetyLimits.MaxHeight"/> and
    /// <see cref="SafetyLimits.MaxPixels"/>, as <see cref="CreateRaster"/> does, for a reader that allocates its own row buffers
    /// before it creates a raster, such as an <see cref="IRowFormatReader"/>.
    /// </summary>
    /// <param name="width">The width the file says the image has.</param>
    /// <param name="height">The height the file says the image has.</param>
    /// <exception cref="LimitExceededException">The size breaks a limit.</exception>
    public void CheckFrameSize(long width, long height)
    {
        Limits.CheckFrameSize(width, height);
    }

    /// <summary>Checks a frame count against <see cref="SafetyLimits.MaxFrames"/>.</summary>
    /// <param name="frames">The frames the file says it has, or will have.</param>
    /// <exception cref="LimitExceededException">There are too many.</exception>
    public void CheckFrameCount(long frames)
    {
        Limits.CheckFrameCount(frames);
    }

    /// <summary>Checks a metadata size against <see cref="SafetyLimits.MaxMetadataBytes"/>.</summary>
    /// <param name="bytes">The metadata bytes kept so far, or about to be read.</param>
    /// <exception cref="LimitExceededException">They are too many.</exception>
    public void CheckMetadataBytes(long bytes)
    {
        Limits.CheckMetadataBytes(bytes);
    }

    /// <summary>
    /// Notes a problem a lenient read recovered from in the raster's <see cref="RasterMetadata.Warnings"/>, which also sends it to
    /// <see cref="EgamiContext.Diagnostics"/> when the load ends.
    /// </summary>
    /// <param name="raster">The raster being read.</param>
    /// <param name="id">
    /// A stable id of the form <c>Source.Name</c>, such as <c>"Pcx.PaletteMissing"</c>; <see cref="EgamiDiagnostic.Source"/> is the
    /// part before the dot. Keep ids the same from version to version so apps can filter on them.
    /// </param>
    /// <param name="warning">What was wrong and what the reader did, as a sentence.</param>
    /// <param name="offset">The byte offset in the input the problem is at, if known.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> has no dot, or <paramref name="warning"/> is empty.</exception>
    public void Warn(Raster raster, string id, string warning, long? offset = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(warning);
        if (id.IndexOf('.', StringComparison.Ordinal) < 1)
        {
            throw new ArgumentException($"The id '{id}' is not of the form Source.Name.", nameof(id));
        }

        ObjectDisposedException.ThrowIf(_buffer is null, this);
        raster.Metadata.WarningList.Add(id, warning, offset);
    }

    /// <summary>
    /// Notes a problem a lenient read recovered from in <paramref name="metadata"/>'s <see cref="RasterMetadata.Warnings"/>, for an
    /// <see cref="IRowFormatReader"/>'s open image, which has its metadata but no raster. The decoder sends the warnings to
    /// <see cref="EgamiContext.Diagnostics"/> as it does for <see cref="Warn(Raster, string, string, long?)"/>.
    /// </summary>
    /// <param name="metadata">The open image's <see cref="RowReader.Metadata"/>.</param>
    /// <param name="id">A stable id of the form <c>Source.Name</c>, such as <c>"Pcx.PaletteMissing"</c>.</param>
    /// <param name="warning">What was wrong and what the reader did, as a sentence.</param>
    /// <param name="offset">The byte offset in the input the problem is at, if known.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> has no dot, or <paramref name="warning"/> is empty.</exception>
    public void Warn(RasterMetadata metadata, string id, string warning, long? offset = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(warning);
        if (id.IndexOf('.', StringComparison.Ordinal) < 1)
        {
            throw new ArgumentException($"The id '{id}' is not of the form Source.Name.", nameof(id));
        }

        ObjectDisposedException.ThrowIf(_buffer is null, this);
        metadata.WarningList.Add(id, warning, offset);
    }

    /// <summary>Has the Warnings of the raster a load returns sent to the handler, if any, as the load ends.</summary>
    internal TRaster Watch<TRaster>(TRaster raster)
        where TRaster : Raster
    {
        if (Context.Diagnostics is Action<EgamiDiagnostic> handler && raster.Metadata.BindWarnings(handler))
        {
            Diagnostics.Track(raster.Metadata);
        }

        return raster;
    }

    /// <summary>
    /// Ends the load's reporting once the input is disposed: sends the Warnings of the raster <see cref="Watch"/> was given and the
    /// summaries of capped ids. A handler that throws fails the load, and <paramref name="result"/> is disposed first.
    /// </summary>
    internal T Deliver<T>(T result)
    {
        if (_diagnostics is DiagnosticReporter diagnostics)
        {
            try
            {
                diagnostics.Complete();
            }
            catch
            {
                (result as IDisposable)?.Dispose();
                throw;
            }
        }

        return result;
    }

    /// <summary>Gets the length of the data from the start of the image, or <see langword="null"/> if the stream cannot seek.</summary>
    internal long? Length => _origin >= 0 ? _stream.Length - _origin : null;

    /// <summary>
    /// Gets the bytes of a memory stream from the start of the image to its end without copying, for formats that read at
    /// offsets (TIFF), or an empty segment for any other stream.
    /// </summary>
    internal ArraySegment<byte> WholeBuffer =>
        Append is null && _stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> whole) && _origin >= 0
            ? whole.Slice((int)_origin, (int)(memory.Length - _origin))
            : default;

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes at <paramref name="offset"/> from the start of the image without
    /// moving the read position, for formats that read at offsets; returns how many there were. The stream must be seekable.
    /// </summary>
    internal int ReadAt(long offset, Span<byte> destination)
    {
        if (_origin < 0 || offset < 0 || offset >= Length)
        {
            return 0;
        }

        CancellationToken.ThrowIfCancellationRequested();
        long saved = _stream.Position;
        try
        {
            _stream.Position = _origin + offset;
            return _stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        }
        finally
        {
            _stream.Position = saved;
        }
    }

    /// <summary>
    /// Gets or sets where the image ends, from its start, when a reader read past it: TGA looks for its footer at the end of a
    /// stream that cannot seek. <see langword="null"/> when the bytes consumed are the image.
    /// </summary>
    internal long? ImageEnd { get; set; }

    /// <summary>Gets whether this is an incremental decode, whose bytes are appended as they arrive.</summary>
    internal bool Incremental => Append is not null;

    /// <summary>Gets whether running out of bytes is the end of the data: always, unless an incremental decode is still receiving.</summary>
    internal bool AtEndOfInput => Append is null || Append.Ended;

    /// <summary>Gets whether an incremental decode has read every byte received so far while more may come.</summary>
    internal bool Dry => Append is { Ended: false } && _start == _end && Append.Available == 0;

    /// <summary>Gets the bytes an incremental decode can read now, without waiting for more.</summary>
    internal long Available => (_end - _start) + (Append?.Available ?? 0);

    /// <summary>Gets the bytes read ahead into this input's buffer and not consumed, valid until the next read.</summary>
    internal ReadOnlyMemory<byte> BufferedUnread => Buffer.AsMemory(_start, _end - _start);

    /// <summary>
    /// Copies upcoming bytes of an incremental decode, from <paramref name="offset"/> bytes after the read position, without
    /// consuming them. Returns <see langword="false"/> if they have not all arrived.
    /// </summary>
    internal bool TryPeekAt(long offset, Span<byte> destination)
    {
        int buffered = _end - _start;
        if (offset < 0 || offset > Available - destination.Length)
        {
            return false;
        }

        int fromBuffer = (int)Math.Clamp(buffered - offset, 0, destination.Length);
        if (fromBuffer > 0)
        {
            Buffer.AsSpan(_start + (int)offset, fromBuffer).CopyTo(destination);
        }

        return fromBuffer == destination.Length || Append!.TryPeek(Math.Max(0, offset - buffered), destination[fromBuffer..]);
    }

    /// <summary>
    /// Reads bytes at <paramref name="offset"/> from the start of the image without moving the read position, for formats that
    /// keep a footer at the end. Returns <see langword="false"/> if the stream cannot seek or the bytes are not all there.
    /// </summary>
    internal bool TryReadAt(long offset, Span<byte> destination)
    {
        if (_origin < 0 || offset < 0 || offset > Length - destination.Length)
        {
            return false;
        }

        long saved = _stream.Position;
        try
        {
            _stream.Position = _origin + offset;
            return _stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false) == destination.Length;
        }
        finally
        {
            _stream.Position = saved;
        }
    }

    /// <summary>The memory source of this load, which counts against <see cref="SafetyLimits.MaxTotalBytes"/> with the raster.</summary>
    internal BudgetedMemorySource Memory => _memory;

    /// <summary>Creates a <see cref="CorruptDataException"/> for this format.</summary>
    /// <param name="offset">The byte offset of the problem, or -1 if unknown.</param>
    /// <param name="problem">What is wrong, as a sentence.</param>
    /// <returns>The exception, for the reader to throw.</returns>
    public CorruptDataException Corrupt(long offset, string problem)
    {
        return new CorruptDataException(FormatName, offset, problem);
    }

    /// <summary>Returns the buffer to the pool. Leaves the stream open.</summary>
    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal void SetFormat(FormatInfo format)
    {
        _formatName = format.Name;
    }

    /// <summary>Moves a seekable stream back to just after the bytes the reader consumed.</summary>
    internal void ReturnUnreadBytes()
    {
        int unread = _end - _start;
        if (unread > 0 && _stream.CanSeek)
        {
            _stream.Seek(-unread, SeekOrigin.Current);
            _start = _end;
        }
    }

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(EgamiInput));

    private ReadOnlySpan<byte> ReadSmall(int count)
    {
        long start = _position;
        if (!Fill(count) || _end - _start < count)
        {
            throw Truncated(start);
        }

        ReadOnlySpan<byte> bytes = Buffer.AsSpan(_start, count);
        _start += count;
        _position += count;
        return bytes;
    }

    /// <summary>Makes at least <paramref name="count"/> bytes available in the buffer if the stream has them.</summary>
    /// <returns><see langword="false"/> if no bytes at all are available.</returns>
    private bool Fill(int count)
    {
        byte[] buffer = Buffer;
        if (_end - _start >= count)
        {
            return true;
        }

        if (_start > 0)
        {
            buffer.AsSpan(_start, _end - _start).CopyTo(buffer);
            _end -= _start;
            _start = 0;
        }

        while (_end < count)
        {
            CancellationToken.ThrowIfCancellationRequested();
            int n = _stream.Read(buffer, _end, buffer.Length - _end);
            if (n == 0)
            {
                break;
            }

            _end += n;
        }

        return _end > _start;
    }

    private Exception Truncated(long offset)
    {
        // An incremental decode still receiving waits for more bytes instead.
        return Append is { Ended: false }
            ? new Streaming.NeedsInputException()
            : new CorruptDataException(FormatName, offset, "The data ends unexpectedly.") { IsTruncation = true };
    }
}
