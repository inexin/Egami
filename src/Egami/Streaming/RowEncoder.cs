using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>Writes a whole raster through a format's row encoder, as Save does.</summary>
internal static class RowEncoding
{
    public static void Save<TPixel>(IRowFormatWriter writer, Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        using RowEncoder<TPixel> encoder = writer.Start(output, Whole(raster, options));
        foreach (int _ in encoder.WriteWhole(raster))
        {
        }

        encoder.Complete();
    }

    public static EncodeStart<TPixel> Whole<TPixel>(Raster<TPixel> raster, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return new EncodeStart<TPixel>(raster.Width, raster.Height, raster.Metadata, options, raster.Context, raster, seekable: false);
    }
}

/// <summary>
/// The stream a <see cref="RasterEncoder{TPixel}"/> writes through: bytes collect in a pooled buffer and go to the target when it
/// passes 64 KB, or when an async call writes them out. Seeking writes out what is held first.
/// </summary>
internal sealed class EncodeOutput(Stream target) : Stream
{
    private const int FlushBytes = 64 * 1024;

    private byte[] _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(FlushBytes * 2);
    private int _count;

    /// <summary>Gets or sets whether bytes are held until <see cref="FlushAsync(CancellationToken)"/>, while an async call encodes.</summary>
    public bool Hold { get; set; }

    /// <summary>Gets the bytes held.</summary>
    public int Held => _count;

    public override bool CanRead => false;

    public override bool CanSeek => target.CanSeek;

    public override bool CanWrite => true;

    public override long Length => target.Length + _count;

    public override long Position
    {
        get => target.Position + _count;
        set
        {
            WriteOut();
            target.Position = value;
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_count + buffer.Length > _buffer.Length)
        {
            if (!Hold)
            {
                WriteOut();
            }

            if (buffer.Length > _buffer.Length - _count)
            {
                if (!Hold && buffer.Length >= FlushBytes)
                {
                    target.Write(buffer);
                    return;
                }

                byte[] larger = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _count + buffer.Length));
                _buffer.AsSpan(0, _count).CopyTo(larger);
                System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = larger;
            }
        }

        buffer.CopyTo(_buffer.AsSpan(_count));
        _count += buffer.Length;
        if (!Hold && _count >= FlushBytes)
        {
            WriteOut();
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush()
    {
        WriteOut();
        target.Flush();
    }

    /// <summary>Writes out what is held asynchronously, without flushing the target.</summary>
    public async ValueTask WriteOutAsync(CancellationToken cancellationToken)
    {
        if (_count > 0)
        {
            await target.WriteAsync(_buffer.AsMemory(0, _count), cancellationToken).ConfigureAwait(false);
            _count = 0;
        }
    }

    /// <summary>Writes out what is held, then flushes the target, asynchronously.</summary>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await WriteOutAsync(cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        WriteOut();
        return target.Seek(offset, origin);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null!);
        if (buffer is not null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        base.Dispose(disposing);
    }

    private void WriteOut()
    {
        if (_count > 0)
        {
            target.Write(_buffer, 0, _count);
            _count = 0;
        }
    }
}

/// <summary>
/// Puts the equal-sized rows of a bottom-up file in place. Save hands them over bottom row first, which is file order; a streamed
/// encode hands them over top-down, and each is written at its offset in a seekable output.
/// </summary>
internal sealed class BottomUpRows
{
    private readonly Stream _output;
    private readonly bool _seek;
    private readonly long _start;
    private readonly long _stride;
    private readonly int _height;
    private int _y;

    /// <summary>Starts the rows where the output is now, after flushing <paramref name="writer"/> when seeking.</summary>
    public BottomUpRows(Stream output, Formats.StreamBufferWriter writer, int height, long stride, bool seek)
    {
        _output = output;
        _seek = seek;
        _height = height;
        _stride = stride;
        if (seek)
        {
            writer.Flush();
            _start = output.Position;
        }
    }

    /// <summary>Writes the next row handed over: in file order through <paramref name="writer"/>, or seeking to its place.</summary>
    public void Write(Formats.StreamBufferWriter writer, ReadOnlySpan<byte> row)
    {
        if (!_seek)
        {
            row.CopyTo(writer.GetSpan(row.Length));
            writer.Advance(row.Length);
        }
        else
        {
            _output.Position = _start + ((_height - 1 - _y) * _stride);
            _output.Write(row);
        }

        _y++;
    }

    /// <summary>Moves a seeking output past the last row, where what follows the rows goes.</summary>
    public void End()
    {
        if (_seek)
        {
            _output.Position = _start + (_height * _stride);
        }
    }
}
