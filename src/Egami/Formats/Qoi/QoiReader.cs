using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Qoi;

/// <summary>
/// Reads QOI, written from the QOI specification 1.0. Chunks decode into packed RGBA words, straight into the frame for
/// <see cref="Rgba8"/> targets and through one pooled row otherwise.
/// </summary>
internal sealed partial class QoiReader : IFormatReader
{
    internal static readonly QoiReader Instance = new();

    internal const int HeaderLength = 14;

    /// <summary>Gets the chunk tags.</summary>
    internal const byte OpIndex = 0x00;
    internal const byte OpDiff = 0x40;
    internal const byte OpLuma = 0x80;
    internal const byte OpRun = 0xC0;
    internal const byte OpRgb = 0xFE;
    internal const byte OpRgba = 0xFF;

    /// <summary>Gets the 8-byte end marker: seven zeros and a one.</summary>
    internal static ReadOnlySpan<byte> EndMarker => [0, 0, 0, 0, 0, 0, 0, 1];

    /// <summary>Returns whether the data starts with "qoif".</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.StartsWith("qoif"u8);
    }

    /// <summary>
    /// The specification's index position, (r × 3 + g × 5 + b × 7 + a × 11) mod 64, for a pixel packed as R | G &lt;&lt; 8 |
    /// B &lt;&lt; 16 | A &lt;&lt; 24: one multiply spreads the four weighted channels into the top byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Hash(uint px)
    {
        ulong v = px;
        ulong spread = ((v & 0xFF00FF00) << 32) | (v & 0x00FF00FF);
        return (int)((spread * 0x0300_0700_0005_000BUL) >> 56) & 63;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        PixelLayoutInfo layout = header.Channels == 4 ? Rgba8.Layout : Rgb8.Layout;
        return new RasterInfo
        {
            Format = QoiFormat.Info,
            Width = (int)Math.Min(header.Width, int.MaxValue),
            Height = (int)Math.Min(header.Height, int.MaxValue),
            Layout = layout,
            FrameCount = 1,
            EstimatedBytes = (long)header.Width * header.Height * layout.BytesPerPixel,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(header),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Header header = ReadHeader(input);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatDetails = new QoiDetails { Channels = header.Channels, Linear = header.Linear };
            metadata.ColorSpace = header.Linear ? ColorSpaceHint.Linear : ColorSpaceHint.Srgb;
            Decode(input, raster, options.Recovery == RecoveryMode.Strict);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        ReadOnlySpan<byte> header = input.Peek(HeaderLength);
        return header.Length == HeaderLength && header[12] == 3 ? Read<Rgb8>(input, options) : Read<Rgba8>(input, options);
    }

    private static Header ReadHeader(EgamiInput input)
    {
        Span<byte> bytes = stackalloc byte[HeaderLength];
        input.ReadExactly(bytes);
        if (!Matches(bytes))
        {
            throw input.Corrupt(0, "The QOI signature is wrong.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
        if (width == 0 || height == 0)
        {
            throw input.Corrupt(4, $"The image size {width}×{height} is invalid.");
        }

        if (bytes[12] is not (3 or 4))
        {
            throw input.Corrupt(12, $"The channel count is {bytes[12]}; it must be 3 or 4.");
        }

        if (bytes[13] > 1)
        {
            throw input.Corrupt(13, $"The colorspace is {bytes[13]}; it must be 0 or 1.");
        }

        return new Header(width, height, bytes[12], bytes[13] == 1);
    }

    private static void Decode<TPixel>(EgamiInput input, Raster<TPixel> raster, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width;
        int height = raster.Height;
        bool direct = typeof(TPixel) == typeof(Rgba8);
        uint[]? rented = direct ? null : ArrayPool<uint>.Shared.Rent(width);
        Span<uint> index = stackalloc uint[64];
        index.Clear();
        ref uint idx = ref MemoryMarshal.GetReference(index);
        uint px = 0xFF00_0000;
        int run = 0;
        ReadOnlySpan<byte> data = input.Peek(64 * 1024);
        int pos = 0;
        try
        {
            for (int y = 0; y < height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                Span<uint> row = direct ? MemoryMarshal.Cast<TPixel, uint>(raster.Row(y)) : rented.AsSpan(0, width);
                int x = 0;
                while (x < width)
                {
                    if (run > 0)
                    {
                        int n = Math.Min(run, width - x);
                        row.Slice(x, n).Fill(px);
                        x += n;
                        run -= n;
                        continue;
                    }

                    if (data.Length - pos >= 5)
                    {
                        // Every chunk is at most 5 bytes, so up to the last 5 bytes of the window no read needs a check.
                        ref byte src = ref MemoryMarshal.GetReference(data);
                        ref uint dst = ref MemoryMarshal.GetReference(row);
                        int limit = data.Length - 5;
                        while (pos <= limit && x < width)
                        {
                            uint b = Unsafe.Add(ref src, pos++);
                            if (b < OpLuma)
                            {
                                px = b < OpDiff
                                    ? Unsafe.Add(ref idx, (nint)b)
                                    : Add(px, (int)((b >> 4) & 3) - 2, (int)((b >> 2) & 3) - 2, (int)(b & 3) - 2);
                            }
                            else if (b < OpRun)
                            {
                                int dg = (int)(b & 0x3F) - 32;
                                uint next = Unsafe.Add(ref src, pos++);
                                px = Add(px, dg - 8 + (int)(next >> 4), dg, dg - 8 + (int)(next & 0x0F));
                            }
                            else if (b < OpRgb)
                            {
                                run = (int)(b & 0x3F) + 1;
                                Unsafe.Add(ref idx, Hash(px)) = px;
                                break;
                            }
                            else if (b == OpRgb)
                            {
                                px = (px & 0xFF00_0000) | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, pos)) & 0x00FF_FFFF;
                                pos += 3;
                            }
                            else
                            {
                                px = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, pos));
                                pos += 4;
                            }

                            Unsafe.Add(ref idx, Hash(px)) = px;
                            Unsafe.Add(ref dst, x++) = px;
                        }

                        continue;
                    }

                    input.Skip(pos);
                    data = input.Peek(64 * 1024);
                    pos = 0;
                    if (data.Length >= 5)
                    {
                        continue;
                    }

                    // The last few bytes of the data: one chunk at a time, each checked for room.
                    if (pos >= data.Length || (data[pos] == OpRgb && data.Length - pos < 4) || (data[pos] == OpRgba && data.Length - pos < 5)
                        || ((data[pos] & 0xC0) == OpLuma && data.Length - pos < 2))
                    {
                        input.Skip(pos);
                        Truncated(input, raster, row, x, y, rented, strict);
                        return;
                    }

                    byte c = data[pos++];
                    if (c == OpRgb)
                    {
                        px = (px & 0xFF00_0000) | data[pos] | ((uint)data[pos + 1] << 8) | ((uint)data[pos + 2] << 16);
                        pos += 3;
                    }
                    else if (c == OpRgba)
                    {
                        px = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                        pos += 4;
                    }
                    else
                    {
                        switch (c & 0xC0)
                        {
                            case OpIndex:
                                px = index[c];
                                break;
                            case OpDiff:
                                px = Add(px, ((c >> 4) & 3) - 2, ((c >> 2) & 3) - 2, (c & 3) - 2);
                                break;
                            case OpLuma:
                                int dg = (c & 0x3F) - 32;
                                byte next = data[pos++];
                                px = Add(px, dg - 8 + (next >> 4), dg, dg - 8 + (next & 0x0F));
                                break;
                            default:
                                run = (c & 0x3F) + 1;
                                break;
                        }
                    }

                    index[Hash(px)] = px;
                    if (run == 0)
                    {
                        row[x++] = px;
                    }
                }

                if (!direct)
                {
                    PixelConvert.Convert<Rgba8, TPixel>(MemoryMarshal.Cast<uint, Rgba8>(row), raster.Row(y));
                }
            }

            input.Skip(pos);
            ReadOnlySpan<byte> end = input.Peek(EndMarker.Length);
            if (end.SequenceEqual(EndMarker))
            {
                input.Skip(EndMarker.Length);
                return;
            }

            string problem = end.Length == 0 || EndMarker.StartsWith(end)
                ? "The file ends without the QOI end marker."
                : "More data follows the last pixel where the QOI end marker should be; it was ignored.";
            if (strict)
            {
                throw input.Corrupt(input.Position, problem);
            }

            raster.Metadata.WarningList.Add(end.Length == 0 || EndMarker.StartsWith(end) ? "Qoi.EndMarkerMissing" : "Qoi.DataAfterEnd", problem, input.Position);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<uint>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Adds signed differences to the red, green and blue bytes of a packed pixel, each wrapping around.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Add(uint px, int dr, int dg, int db)
    {
        uint r = (byte)(px + (uint)dr);
        uint g = (byte)((px >> 8) + (uint)dg);
        uint b = (byte)((px >> 16) + (uint)db);
        return (px & 0xFF00_0000) | r | (g << 8) | (b << 16);
    }

    /// <summary>Keeps the rows decoded so far and clears the rest (Lenient), or throws (Strict).</summary>
    private static void Truncated<TPixel>(EgamiInput input, Raster<TPixel> raster, Span<uint> row, int x, int y, uint[]? rented, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (strict)
        {
            throw new CorruptDataException(QoiFormat.Info.Name, input.Position, "The data ends unexpectedly.") { IsTruncation = true };
        }

        row[x..].Clear();
        if (rented is not null)
        {
            PixelConvert.Convert<Rgba8, TPixel>(MemoryMarshal.Cast<uint, Rgba8>(row), raster.Row(y));
        }

        for (int rest = y + 1; rest < raster.Height; rest++)
        {
            raster.Row(rest).Clear();
        }

        raster.Metadata.WarningList.Add("Qoi.RowsMissing", $"The file ends after {y} of {raster.Height} rows; the rest is left transparent black.");
    }

    private readonly record struct Header(uint Width, uint Height, int Channels, bool Linear);
}
