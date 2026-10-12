using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using Egami.Metadata;

namespace Egami.Formats.Psd;

/// <summary>
/// The flattened image's channel planes, one row of one channel at a time, in the bytes the file stores (packed bits, bytes, or
/// big-endian 16-bit integers and 32-bit floats). Raw and PackBits rows are read where they lie, found through the computed
/// offsets or the PackBits row byte counts, from memory or a seekable stream; a stream that cannot seek is read to its end
/// first. Deflate data is inflated whole, since a Deflate stream cannot be entered part way.
/// </summary>
internal sealed class PsdPlanes : IDisposable
{
    private readonly PsdFile _file;
    private readonly ArraySegment<byte> _memory;
    private readonly EgamiInput? _seekable;
    private readonly long _length;
    private readonly bool _strict;
    private readonly int[] _channelSlot;
    private readonly long[][]? _rowOffsets;
    private readonly int[][]? _rowLengths;
    private readonly byte[]? _inflated;
    private byte[] _scratch = [];
    private bool _held;

    private PsdPlanes(PsdFile file, ArraySegment<byte> memory, EgamiInput? seekable, long length, bool strict, IReadOnlyList<int> channels, WarningList warnings)
    {
        _file = file;
        _memory = memory;
        _seekable = seekable;
        _length = length;
        _strict = strict;
        Warnings = warnings;
        RowBytes = file.Depth == 1 ? (file.Width + 7) / 8 : (int)((long)file.Width * file.Depth / 8);
        _channelSlot = new int[file.Channels];
        Array.Fill(_channelSlot, -1);
        for (int i = 0; i < channels.Count; i++)
        {
            _channelSlot[channels[i]] = i;
        }

        switch (file.Compression)
        {
            case PsdCompression.PackBits:
                (_rowOffsets, _rowLengths) = ReadRowCounts(channels);
                break;
            case PsdCompression.Deflate or PsdCompression.DeflatePredicted:
                _inflated = Inflate();
                break;
        }
    }

    /// <summary>Gets the bytes of one stored row of one channel.</summary>
    public int RowBytes { get; }

    /// <summary>Gets what went wrong in lenient mode, for the raster's warnings.</summary>
    public WarningList Warnings { get; }

    /// <summary>Gets the bytes held besides the caller's: the inflated planes, a stream read whole, and the PackBits row table.</summary>
    public long WorkingBytes => (_inflated?.LongLength ?? 0) + (_held ? _memory.Count : 0) + ((_rowOffsets?.Sum(r => r.LongLength) ?? 0) * 12) + RowBytes;

    /// <summary>Returns what <see cref="Open"/> will hold for a file read from a seekable stream or memory, before opening it.</summary>
    public static long PlannedBytes(PsdFile file, int channels)
    {
        long rowBytes = file.Depth == 1 ? (file.Width + 7) / 8 : (long)file.Width * file.Depth / 8;
        return file.Compression switch
        {
            PsdCompression.PackBits => (channels * (long)file.Height * 12) + rowBytes,
            PsdCompression.Raw => rowBytes,
            _ => (rowBytes * file.Height * file.Channels) + rowBytes,
        };
    }

    /// <summary>
    /// Opens the planes of <paramref name="channels"/>, the channels the pixels need, over the input positioned at the data.
    /// </summary>
    public static PsdPlanes Open(EgamiInput input, PsdFile file, IReadOnlyList<int> channels, ReaderOptions options, WarningList warnings)
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        input.CheckMemory(PlannedBytes(file, channels.Count));
        ArraySegment<byte> whole = input.WholeBuffer;
        if (whole.Array is not null && whole.Count >= file.DataStart)
        {
            ArraySegment<byte> data = whole.Slice((int)file.DataStart);
            return new PsdPlanes(file, data, null, data.Count, strict, channels, warnings);
        }

        if (input.Length is long length && !input.Incremental)
        {
            return new PsdPlanes(file, default, input, length - file.DataStart, strict, channels, warnings);
        }

        (byte[] rest, int count, bool _) = InputBytes.ReadAll(input);
        try
        {
            PsdPlanes planes = new(file, new ArraySegment<byte>(rest, 0, count), null, count, strict, channels, warnings);
            planes._held = true;
            return planes;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rest);
            throw;
        }
    }

    /// <summary>Reads row <paramref name="y"/> of channel <paramref name="channel"/> as stored.</summary>
    public void ReadRow(int channel, int y, Span<byte> row)
    {
        row = row[..RowBytes];
        int height = _file.Height;
        if (_inflated is not null)
        {
            long at = (((long)channel * height) + y) * RowBytes;
            if (at + RowBytes <= _inflated.LongLength)
            {
                _inflated.AsSpan((int)at, RowBytes).CopyTo(row);
            }
            else
            {
                row.Clear();
            }

            return;
        }

        if (_file.Compression == PsdCompression.Raw)
        {
            long at = (((long)channel * height) + y) * RowBytes;
            Fetch(at, row);
            return;
        }

        int slot = _channelSlot[channel];
        long offset = _rowOffsets![slot][y];
        int length = _rowLengths![slot][y];
        if (_scratch.Length < length)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = ArrayPool<byte>.Shared.Rent(length);
        }

        Span<byte> packed = _scratch.AsSpan(0, length);
        Fetch(offset, packed);
        int written = PackBits.Decode(packed, row, out _);
        if (written < row.Length)
        {
            if (_strict)
            {
                throw new CorruptDataException(PsdFormat.Info.Name, _file.DataStart + offset, $"Row {y} of channel {channel} decodes to {written} bytes; it has {row.Length}.");
            }

            row[written..].Clear();
            Warnings.Add("Psd.RowShort", $"Some PackBits rows decode short; the rest of each is filled with 0.", _file.DataStart + offset);
        }
    }

    /// <summary>Copies bytes at an offset from the start of the channel data; past the end is 0, with a warning or an exception.</summary>
    private void Fetch(long offset, Span<byte> destination)
    {
        int got;
        if (_seekable is not null)
        {
            got = offset < _length ? _seekable.ReadAt(_file.DataStart + offset, destination) : 0;
        }
        else
        {
            got = (int)Math.Clamp(_memory.Count - offset, 0, destination.Length);
            if (got > 0)
            {
                _memory.AsSpan((int)offset, got).CopyTo(destination);
            }
        }

        if (got < destination.Length)
        {
            if (_strict)
            {
                throw new CorruptDataException(PsdFormat.Info.Name, _file.DataStart + offset + got, "The file ends inside the image data.") { IsTruncation = true };
            }

            destination[got..].Clear();
            Warnings.Add("Psd.DataTruncated", "The file ends inside the image data; the missing pixels are 0.", _file.DataStart + offset + got);
        }
    }

    /// <summary>Reads the PackBits row byte counts and turns them into each needed row's offset and length.</summary>
    private (long[][] Offsets, int[][] Lengths) ReadRowCounts(IReadOnlyList<int> channels)
    {
        int height = _file.Height;
        int entry = _file.Big ? 4 : 2;
        long rows = (long)_file.Channels * height;
        long tableBytes = rows * entry;
        if (tableBytes > _length)
        {
            throw new CorruptDataException(PsdFormat.Info.Name, _file.DataStart, $"The PackBits row table needs {tableBytes} bytes; the file has {_length} after the header.") { IsTruncation = true };
        }

        long[][] offsets = new long[channels.Count][];
        int[][] lengths = new int[channels.Count][];
        for (int i = 0; i < channels.Count; i++)
        {
            offsets[i] = new long[height];
            lengths[i] = new int[height];
        }

        long maxRow = (2L * RowBytes) + 64;
        long at = tableBytes;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(Math.Min(64 * 1024, (int)Math.Max(entry, tableBytes)));
        try
        {
            long read = 0;
            int filled = 0;
            int used = 0;
            for (int c = 0; c < _file.Channels; c++)
            {
                int slot = _channelSlot[c];
                for (int y = 0; y < height; y++)
                {
                    if (used + entry > filled)
                    {
                        int left = filled - used;
                        chunk.AsSpan(used, left).CopyTo(chunk);
                        int want = (int)Math.Min(chunk.Length - left, tableBytes - read);
                        Fetch(read, chunk.AsSpan(left, want));
                        read += want;
                        filled = left + want;
                        used = 0;
                    }

                    long count = entry == 2 ? BinaryPrimitives.ReadUInt16BigEndian(chunk.AsSpan(used)) : BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(used));
                    used += entry;
                    if (count > maxRow)
                    {
                        throw new CorruptDataException(PsdFormat.Info.Name, _file.DataStart + read, $"Row {y} of channel {c} has {count} PackBits bytes; a row of {RowBytes} bytes needs at most {maxRow}.");
                    }

                    if (slot >= 0)
                    {
                        offsets[slot][y] = at;
                        lengths[slot][y] = (int)count;
                    }

                    at += count;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (at > _length && _strict)
        {
            throw new CorruptDataException(PsdFormat.Info.Name, _file.DataStart + _length, "The PackBits rows run past the end of the file.") { IsTruncation = true };
        }

        return (offsets, lengths);
    }

    /// <summary>
    /// Inflates every plane and undoes the prediction. Photoshop writes one Deflate stream for all the planes; ImageMagick one a
    /// plane; both are read.
    /// </summary>
    private byte[] Inflate()
    {
        long planeBytes = (long)RowBytes * _file.Height;
        long total = planeBytes * _file.Channels;
        if (total > Array.MaxLength)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), Array.MaxLength, total);
        }

        byte[] output = new byte[total];
        byte[] compressed = Compressed();
        int got = InflateOne(new MemoryStream(compressed, writable: false), output);
        if (got == planeBytes && _file.Channels > 1)
        {
            // One stream a plane: find where each ends by feeding the inflater a byte at a time.
            got = 0;
            long at = 0;
            for (int c = 0; c < _file.Channels && at < compressed.Length; c++)
            {
                Trickle trickle = new(compressed, at);
                int n = InflateOne(trickle, output.AsSpan((int)(c * planeBytes), (int)planeBytes));
                got += n;
                at = trickle.Position;
                if (n < planeBytes)
                {
                    break;
                }
            }
        }

        if (got < total)
        {
            if (_strict)
            {
                throw new CorruptDataException(PsdFormat.Info.Name, _file.DataStart, $"The Deflate data inflates to {got} bytes; the image needs {total}.") { IsTruncation = true };
            }

            Warnings.Add("Psd.DataTruncated", "The Deflate data ends early; the missing pixels are 0.", _file.DataStart);
        }

        if (_file.Compression == PsdCompression.DeflatePredicted)
        {
            Unpredict(output);
        }

        return output;
    }

    private byte[] Compressed()
    {
        if (_seekable is null)
        {
            return _memory.AsSpan().ToArray();
        }

        if (_length > Array.MaxLength)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), Array.MaxLength, _length);
        }

        byte[] data = new byte[_length];
        Fetch(0, data);
        return data;
    }

    private static int InflateOne(Stream source, Span<byte> destination)
    {
        int total = 0;
        try
        {
            using ZLibStream zlib = new(source, CompressionMode.Decompress);
            while (total < destination.Length)
            {
                int n = zlib.Read(destination[total..]);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            // Read on to the stream's end, so its checksum is consumed and the source stands at the next stream.
            Span<byte> rest = stackalloc byte[1];
            _ = zlib.Read(rest) + 0;
        }
        catch (InvalidDataException)
        {
        }

        return total;
    }

    /// <summary>Undoes the per-row differences: of bytes, of big-endian 16-bit samples, or of a 32-bit row's byte planes.</summary>
    private void Unpredict(byte[] data)
    {
        int width = _file.Width;
        int rowBytes = RowBytes;
        byte[] temp = _file.Depth == 32 ? new byte[rowBytes] : [];
        for (long at = 0; at + rowBytes <= data.LongLength; at += rowBytes)
        {
            Span<byte> row = data.AsSpan((int)at, rowBytes);
            switch (_file.Depth)
            {
                case 8:
                    for (int x = 1; x < row.Length; x++)
                    {
                        row[x] += row[x - 1];
                    }

                    break;
                case 16:
                    ushort previous = BinaryPrimitives.ReadUInt16BigEndian(row);
                    for (int x = 2; x + 1 < row.Length; x += 2)
                    {
                        previous += BinaryPrimitives.ReadUInt16BigEndian(row[x..]);
                        BinaryPrimitives.WriteUInt16BigEndian(row[x..], previous);
                    }

                    break;
                case 32:
                    for (int x = 1; x < row.Length; x++)
                    {
                        row[x] += row[x - 1];
                    }

                    for (int x = 0; x < width; x++)
                    {
                        temp[4 * x] = row[x];
                        temp[(4 * x) + 1] = row[width + x];
                        temp[(4 * x) + 2] = row[(2 * width) + x];
                        temp[(4 * x) + 3] = row[(3 * width) + x];
                    }

                    temp.CopyTo(row);
                    break;
            }
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_scratch);
        _scratch = [];
        if (_held && _memory.Array is byte[] held)
        {
            ArrayPool<byte>.Shared.Return(held);
            _held = false;
        }
    }

    /// <summary>A stream that hands out one byte a read, so an inflater stops reading exactly where its stream ends.</summary>
    private sealed class Trickle(byte[] data, long start) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get; set; } = start;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || Position >= data.Length)
            {
                return 0;
            }

            buffer[0] = data[Position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
