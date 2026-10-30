using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using Egami.Metadata;

namespace Egami.Formats.Psd;

/// <summary>
/// Decodes the layers' channel data, which follows the layer records in the file: per layer, per channel, a compression code
/// and the channel's rows, raw, PackBits (with a row byte count table, 2 bytes a row in PSD, 4 in PSB), Deflate, or Deflate of
/// each row's differences. Read in file order straight from the input, so it works on streams that cannot seek.
/// </summary>
internal static class PsdChannels
{
    /// <summary>
    /// Reads every layer's channels from <paramref name="input"/>, positioned at the first, and hands each layer's decoded planes
    /// to <paramref name="each"/> in file order. Damage is a warning outside <see cref="RecoveryMode.Strict"/>: the damaged
    /// channel's missing rows are 0.
    /// </summary>
    /// <param name="input">The input, at the start of the channel data.</param>
    /// <param name="file">The file, its records read.</param>
    /// <param name="end">Where the layer info ends.</param>
    /// <param name="options">The caller's options.</param>
    /// <param name="each">Called with each record's index and its planes; the planes are returned to the pool afterwards.</param>
    public static void Read(EgamiInput input, PsdFile file, long end, ReaderOptions options, Action<int, PsdPlaneSet> each)
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        for (int i = 0; i < file.Records.Count; i++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            PsdLayerRecord record = file.Records[i];
            using PsdPlaneSet planes = new(file, record);
            foreach (PsdChannel channel in record.Channels)
            {
                long available = Math.Max(0, end - input.Position);
                long length = Math.Min(channel.Length, available);
                (int width, int height) = planes.SizeOf(channel.Id);
                bool wanted = planes.Wants(channel.Id) && width > 0 && height > 0;
                if (!wanted || length < 2)
                {
                    input.Skip(length);
                    if (wanted && channel.Length < 2 && channel.Length != 0)
                    {
                        Damaged(file, strict, input.Position, $"Channel {channel.Id} of layer {i} is {channel.Length} bytes; it needs at least 2.");
                    }

                    continue;
                }

                if (length > Array.MaxLength)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), Array.MaxLength, length);
                }

                long at = input.Position;
                int compression = input.ReadUInt16BigEndian();
                byte[] payload = ArrayPool<byte>.Shared.Rent((int)(length - 2));
                try
                {
                    input.ReadExactly(payload.AsSpan(0, (int)(length - 2)));
                    byte[] plane = planes.Allocate(channel.Id, input);
                    if (length < channel.Length)
                    {
                        Damaged(file, strict, end, $"The file ends inside channel {channel.Id} of layer {i}; the missing pixels are 0.", truncation: true);
                    }

                    if (compression > 3)
                    {
                        Damaged(file, strict, at, $"Channel {channel.Id} of layer {i} has the unknown compression code {compression}; it was left 0.");
                        continue;
                    }

                    string? problem = Decode(compression, payload.AsSpan(0, (int)(length - 2)), plane, width, height, file.Depth, file.Big);
                    if (problem is not null)
                    {
                        Damaged(file, strict, at, $"Channel {channel.Id} of layer {i}: {problem}");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }

            each(i, planes);
        }
    }

    private static void Damaged(PsdFile file, bool strict, long offset, string message, bool truncation = false)
    {
        if (strict)
        {
            throw new CorruptDataException(PsdFormat.Info.Name, offset, message) { IsTruncation = truncation };
        }

        file.Warnings.Add(truncation ? "Psd.LayerDataTruncated" : "Psd.LayerDataDamaged", message, offset);
    }

    /// <summary>Returns the bytes of one stored row: packed bits, bytes, or 2 or 4 bytes a sample.</summary>
    public static int RowBytes(int width, int depth) => depth == 1 ? (width + 7) / 8 : (int)((long)width * depth / 8);

    /// <summary>
    /// Decodes one channel's data after its compression code into <paramref name="plane"/>, its rows one after another as
    /// stored. Returns what was wrong, or <see langword="null"/>; rows it could not fill are left as they were (0).
    /// </summary>
    public static string? Decode(int compression, ReadOnlySpan<byte> data, Span<byte> plane, int width, int height, int depth, bool big)
    {
        int rowBytes = RowBytes(width, depth);
        long size = (long)rowBytes * height;
        plane = plane[..(int)size];
        switch (compression)
        {
            case 0:
                {
                    int n = (int)Math.Min(data.Length, size);
                    data[..n].CopyTo(plane);
                    return n < size ? $"its raw data is {data.Length} bytes; it needs {size}." : null;
                }

            case 1:
                {
                    int entry = big ? 4 : 2;
                    long table = (long)entry * height;
                    if (table > data.Length)
                    {
                        return $"its PackBits row table needs {table} bytes; it has {data.Length}.";
                    }

                    long at = table;
                    string? problem = null;
                    for (int y = 0; y < height; y++)
                    {
                        long count = entry == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data[(y * 2)..]) : BinaryPrimitives.ReadUInt32BigEndian(data[(y * 4)..]);
                        if (at + count > data.Length)
                        {
                            return problem ?? "its PackBits rows run past its end.";
                        }

                        int written = PackBits.Decode(data.Slice((int)at, (int)count), plane.Slice(y * rowBytes, rowBytes), out _);
                        if (written < rowBytes)
                        {
                            problem ??= $"row {y} decodes to {written} bytes; it has {rowBytes}.";
                        }

                        at += count;
                    }

                    return problem;
                }

            default:
                {
                    int got = Inflate(data, plane);
                    if (compression == 3)
                    {
                        Unpredict(plane, depth, width, rowBytes);
                    }

                    return got < size ? $"its Deflate data inflates to {got} bytes; it needs {size}." : null;
                }
        }
    }

    private static int Inflate(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int total = 0;
        byte[] copy = data.ToArray();
        try
        {
            using ZLibStream zlib = new(new MemoryStream(copy, writable: false), CompressionMode.Decompress);
            while (total < destination.Length)
            {
                int n = zlib.Read(destination[total..]);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // Damaged data ends the stream; what was inflated before it stands.
        }

        return total;
    }

    /// <summary>Undoes the per-row differences: of bytes, of big-endian 16-bit samples, or of a 32-bit row's byte planes.</summary>
    public static void Unpredict(Span<byte> data, int depth, int width, int rowBytes)
    {
        byte[]? temp = depth == 32 ? ArrayPool<byte>.Shared.Rent(rowBytes) : null;
        try
        {
            for (int at = 0; at + rowBytes <= data.Length; at += rowBytes)
            {
                Span<byte> row = data.Slice(at, rowBytes);
                switch (depth)
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
                            temp![4 * x] = row[x];
                            temp[(4 * x) + 1] = row[width + x];
                            temp[(4 * x) + 2] = row[(2 * width) + x];
                            temp[(4 * x) + 3] = row[(3 * width) + x];
                        }

                        temp.AsSpan(0, rowBytes).CopyTo(row);
                        break;
                }
            }
        }
        finally
        {
            if (temp is not null)
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }
    }
}

/// <summary>
/// One layer's decoded channel planes as stored (big-endian samples): the color channels, the transparency and the user mask,
/// each rented from the pool and zeroed, or <see langword="null"/> when the layer has no such channel.
/// </summary>
internal sealed class PsdPlaneSet : IDisposable
{
    private readonly PsdFile _file;

    public PsdPlaneSet(PsdFile file, PsdLayerRecord record)
    {
        _file = file;
        Record = record;
        (Width, Height) = record.Size;
        Color = new byte[]?[file.ColorChannels];
    }

    public PsdLayerRecord Record { get; }

    public int Width { get; }

    public int Height { get; }

    public int Depth => _file.Depth;

    /// <summary>Gets the color planes, by channel number.</summary>
    public byte[]?[] Color { get; }

    /// <summary>Gets the transparency plane (channel −1).</summary>
    public byte[]? Alpha { get; private set; }

    /// <summary>Gets the user mask plane (channel −2), the mask record's size.</summary>
    public byte[]? Mask { get; private set; }

    /// <summary>Gets the bytes of one row of a layer plane.</summary>
    public int RowBytes => PsdChannels.RowBytes(Width, Depth);

    /// <summary>Returns whether the channel is one Egami keeps: color, transparency or the user mask.</summary>
    public bool Wants(short id) => id is -1 or -2 || (id >= 0 && id < Color.Length);

    /// <summary>Returns the size of a channel's plane: the user mask's own for −2, the real mask's for −3, else the layer's.</summary>
    public (int Width, int Height) SizeOf(short id) => id switch
    {
        -2 => Record.Mask?.Size ?? (0, 0),
        -3 => Record.RealMask?.Size ?? (0, 0),
        _ => (Width, Height),
    };

    /// <summary>Rents and zeroes the plane of a channel the layer keeps, checking the size against the limits.</summary>
    public byte[] Allocate(short id, EgamiInput input)
    {
        (int width, int height) = SizeOf(id);
        input.Limits.CheckFrameSize(width, height);
        long size = (long)PsdChannels.RowBytes(width, Depth) * height;
        if (size > Array.MaxLength)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), Array.MaxLength, size);
        }

        input.CheckMemory(size);
        byte[] plane = ArrayPool<byte>.Shared.Rent((int)size);
        Array.Clear(plane, 0, (int)size);
        switch (id)
        {
            case -1:
                Return(Alpha);
                Alpha = plane;
                break;
            case -2:
                Return(Mask);
                Mask = plane;
                break;
            default:
                Return(Color[id]);
                Color[id] = plane;
                break;
        }

        return plane;
    }

    public void Dispose()
    {
        for (int c = 0; c < Color.Length; c++)
        {
            Return(Color[c]);
            Color[c] = null;
        }

        Return(Alpha);
        Return(Mask);
        Alpha = Mask = null;
    }

    private static void Return(byte[]? plane)
    {
        if (plane is not null)
        {
            ArrayPool<byte>.Shared.Return(plane);
        }
    }
}
