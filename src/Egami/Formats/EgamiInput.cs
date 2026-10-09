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
public sealed class EgamiInput : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly BudgetedMemorySource _memory;
    private readonly long _origin;
    private byte[]? _buffer;
    private int _start;
    private int _end;
    private long _position;
    private string _formatName = "image";

    internal EgamiInput(Stream stream, EgamiContext context, CancellationToken cancellationToken)
    {
        _stream = stream;
        Context = context;
        CancellationToken = cancellationToken;
        _memory = new BudgetedMemorySource(context.Memory, context.Limits.MaxTotalBytes);
        _origin = stream.CanSeek ? stream.Position : -1;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>Gets the context of the load.</summary>
    public EgamiContext Context { get; }

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
        _memory.Check(width * height * TPixel.Layout.BytesPerPixel);
        PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>((int)width, (int)height, _memory, clear);
        return new Raster<TPixel>(plane, new RasterMetadata { FormatName = FormatName }, Context);
    }

    /// <summary>Gets the length of the data from the start of the image, or <see langword="null"/> if the stream cannot seek.</summary>
    internal long? Length => _origin >= 0 ? _stream.Length - _origin : null;

    /// <summary>
    /// Reads bytes at <paramref name="offset"/> from the start of the image without moving the read position, for formats that
    /// keep a footer at the end. Returns <see langword="false"/> if the stream cannot seek or the bytes are not all there.
    /// </summary>
    internal bool TryReadAt(long offset, Span<byte> destination)
    {
        if (_origin < 0 || offset < 0)
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

    private CorruptDataException Truncated(long offset)
    {
        return new CorruptDataException(FormatName, offset, "The data ends unexpectedly.") { IsTruncation = true };
    }
}
