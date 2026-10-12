using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Colors;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Exr;

/// <summary>
/// Writes single-part scanline OpenEXR files (HDR Design question 6): R, G, B and A (Y and A for gray), half samples for half
/// and 8-bit pixel types and float otherwise, compressed with NONE, RLE, ZIPS, ZIP or PIZ, blocks in parallel. Pixels are
/// written as linear light: a raster whose profile has another transfer is converted first, keeping its primaries, which
/// the chromaticities attribute names when they are not Rec. 709's.
/// </summary>
internal sealed class ExrWriter : IFormatWriter
{
    internal static readonly ExrWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ExrWriterOptions exr = options as ExrWriterOptions ?? new ExrWriterOptions { StripMetadata = options.StripMetadata };
        if (exr.Compression is not (ExrCompression.None or ExrCompression.Rle or ExrCompression.Zips or ExrCompression.Zip or ExrCompression.Piz))
        {
            throw new NotSupportedException($"Egami writes OpenEXR with None, Rle, Zips, Zip or Piz compression, not {exr.Compression}.");
        }

        SaveLosses.Report(RowEncoding.Whole(raster, options), "OpenEXR", SaveKeeps.Alpha | SaveKeeps.Wide | SaveKeeps.Float);
        PixelLayoutInfo layout = TPixel.Layout;
        ExrPixelType type = exr.PixelType ?? (layout.ChannelType is PixelChannelType.Float16 or PixelChannelType.Unorm8 ? ExrPixelType.Half : ExrPixelType.Float);
        if (type == ExrPixelType.UInt)
        {
            throw new NotSupportedException("Egami writes OpenEXR color as half or float samples.");
        }

        bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        bool alpha = layout.HasAlpha;
        ColorProfile profile = raster.Metadata.GetColorProfile();
        ColorPrimaries primaries = profile.Primaries ?? ColorPrimaries.Rec709;
        Raster<RgbaF32>? linear = null;
        try
        {
            if (profile.Transfer is TransferFunction transfer && !transfer.IsLinear)
            {
                linear = raster.ConvertColor<RgbaF32>(ColorProfile.Create(primaries, TransferFunction.Linear, "Linear"), new ColorTransformOptions { ToneMapping = ToneMapping.Clip });
            }

            Write(raster, linear, output, exr, type, gray, alpha, primaries);
        }
        finally
        {
            linear?.Dispose();
        }
    }

    private static void Write<TPixel>(Raster<TPixel> raster, Raster<RgbaF32>? linear, Stream output, ExrWriterOptions options, ExrPixelType type, bool gray, bool alpha, ColorPrimaries primaries)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width, height = raster.Height;

        // Channels in the file's (alphabetical) order, each with the RGBA component it takes.
        List<(string Name, int Component)> channels = [];
        if (alpha)
        {
            channels.Add(("A", 3));
        }

        if (gray)
        {
            channels.Add(("Y", 1));
        }
        else
        {
            channels.AddRange([("B", 2), ("G", 1), ("R", 0)]);
        }

        ExrPartHeader part = new()
        {
            Info = new ExrPart { Compression = options.Compression },
            Channels = [.. channels.Select(c => new ExrChannelInfo(c.Name, type, 1, 1, false))],
            MinX = 0,
            MinY = 0,
            MaxX = width - 1,
            MaxY = height - 1,
        };
        int lines = part.LinesPerBlock;
        int blocks = (height + lines - 1) / lines;
        byte[][] chunks = new byte[blocks][];
        int[] sizes = new int[blocks];
        int sampleBytes = type == ExrPixelType.Half ? 2 : 4;
        int lineBytes = width * channels.Count * sampleBytes;
        Parallel.For(
            0,
            blocks,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, raster.Context.MaxParallelism) },
            () => (ArrayPool<RgbaF32>.Shared.Rent(width), ArrayPool<byte>.Shared.Rent((lineBytes * lines) + 64)),
            (b, _, buffers) =>
            {
                int y0 = b * lines, y1 = Math.Min(height - 1, y0 + lines - 1);
                Span<byte> raw = buffers.Item2.AsSpan(0, lineBytes * (y1 - y0 + 1));
                Span<RgbaF32> row = buffers.Item1.AsSpan(0, width);
                for (int y = y0; y <= y1; y++)
                {
                    if (linear is not null)
                    {
                        linear.Row(y).CopyTo(row);
                    }
                    else
                    {
                        PixelConvert.Convert<TPixel, RgbaF32>(raster.Row(y), row);
                    }

                    Span<byte> line = raw.Slice((y - y0) * lineBytes, lineBytes);
                    for (int c = 0; c < channels.Count; c++)
                    {
                        int component = channels[c].Component;
                        Span<byte> samples = line.Slice(c * width * sampleBytes, width * sampleBytes);
                        ReadOnlySpan<float> values = MemoryMarshal.Cast<RgbaF32, float>(row);
                        for (int x = 0; x < width; x++)
                        {
                            float v = values[(x * 4) + component];
                            if (type == ExrPixelType.Half)
                            {
                                BinaryPrimitives.WriteUInt16LittleEndian(samples[(x * 2)..], BitConverter.HalfToUInt16Bits((Half)v));
                            }
                            else
                            {
                                BinaryPrimitives.WriteSingleLittleEndian(samples[(x * 4)..], v);
                            }
                        }
                    }
                }

                ExrBlock block = new(part, 0, y0, width - 1, y1);
                byte[] packed = Compress(block, raw, options.Compression);
                chunks[b] = packed;
                sizes[b] = packed.Length;
                return buffers;
            },
            buffers =>
            {
                ArrayPool<RgbaF32>.Shared.Return(buffers.Item1);
                ArrayPool<byte>.Shared.Return(buffers.Item2);
            });

        // Header, chunk table, chunks.
        using MemoryStream header = new();
        BinaryWriter w = new(header, Encoding.ASCII, leaveOpen: true);
        w.Write(ExrHeaderReader.Magic);
        w.Write(2);
        void Attribute(string name, string attrType, ReadOnlySpan<byte> value)
        {
            w.Write(Encoding.ASCII.GetBytes(name));
            w.Write((byte)0);
            w.Write(Encoding.ASCII.GetBytes(attrType));
            w.Write((byte)0);
            w.Write(value.Length);
            w.Write(value);
        }

        using (MemoryStream list = new())
        {
            Span<byte> info = stackalloc byte[16];
            foreach ((string name, int _) in channels)
            {
                list.Write(Encoding.ASCII.GetBytes(name));
                list.WriteByte(0);
                info.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(info, (int)type);
                BinaryPrimitives.WriteInt32LittleEndian(info[8..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(info[12..], 1);
                list.Write(info);
            }

            list.WriteByte(0);
            Attribute("channels", "chlist", list.ToArray());
        }

        Attribute("compression", "compression", [(byte)options.Compression]);
        Span<byte> box = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(box[8..], width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(box[12..], height - 1);
        Attribute("dataWindow", "box2i", box);
        Attribute("displayWindow", "box2i", box);
        Attribute("lineOrder", "lineOrder", [0]);
        Attribute("pixelAspectRatio", "float", BitConverter.GetBytes(1.0f));
        Attribute("screenWindowCenter", "v2f", new byte[8]);
        Attribute("screenWindowWidth", "float", BitConverter.GetBytes(1.0f));
        if (!primaries.IsCloseTo(ColorPrimaries.Rec709))
        {
            Span<byte> chroma = stackalloc byte[32];
            Chromaticity[] points = [primaries.Red, primaries.Green, primaries.Blue, primaries.White];
            for (int i = 0; i < 4; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(chroma[(i * 8)..], (float)points[i].X);
                BinaryPrimitives.WriteSingleLittleEndian(chroma[((i * 8) + 4)..], (float)points[i].Y);
            }

            Attribute("chromaticities", "chromaticities", chroma);
        }

        w.Write((byte)0);
        w.Flush();
        long offset = header.Length + (8L * blocks);
        for (int b = 0; b < blocks; b++)
        {
            w.Write(offset);
            offset += 8 + sizes[b];
        }

        w.Flush();
        header.WriteTo(output);
        Span<byte> chunkHead = stackalloc byte[8];
        for (int b = 0; b < blocks; b++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(chunkHead, b * lines);
            BinaryPrimitives.WriteInt32LittleEndian(chunkHead[4..], sizes[b]);
            output.Write(chunkHead);
            output.Write(chunks[b]);
        }
    }

    /// <summary>Compresses one block; returns it raw when compression does not make it smaller.</summary>
    private static byte[] Compress(in ExrBlock block, ReadOnlySpan<byte> raw, ExrCompression compression)
    {
        byte[]? packed = compression switch
        {
            ExrCompression.Rle => Rle(raw),
            ExrCompression.Zips or ExrCompression.Zip => Zip(raw),
            ExrCompression.Piz => Piz(block, raw),
            _ => null,
        };
        return packed is not null && packed.Length < raw.Length ? packed : raw.ToArray();
    }

    private static byte[]? Piz(in ExrBlock block, ReadOnlySpan<byte> raw)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(raw.Length + 65536);
        try
        {
            int n = ExrPiz.Compress(block, raw, buffer);
            return n < 0 ? null : buffer.AsSpan(0, n).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] Zip(ReadOnlySpan<byte> raw)
    {
        byte[] t = ArrayPool<byte>.Shared.Rent(raw.Length);
        try
        {
            ExrCodec.Predict(raw, t.AsSpan(0, raw.Length));
            using MemoryStream packed = new();
            using (ZLibStream zlib = new(packed, new ZLibCompressionOptions { CompressionLevel = 4 }, leaveOpen: true))
            {
                zlib.Write(t, 0, raw.Length);
            }

            return packed.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(t);
        }
    }

    /// <summary>OpenEXR's RLE after the byte predictor: runs of 3 to 128 bytes as a count and the byte, the rest as literals.</summary>
    private static byte[] Rle(ReadOnlySpan<byte> raw)
    {
        byte[] t = ArrayPool<byte>.Shared.Rent(raw.Length);
        try
        {
            Span<byte> src = t.AsSpan(0, raw.Length);
            ExrCodec.Predict(raw, src);
            List<byte> output = new(raw.Length / 2);
            int runs = 0;
            int end = src.Length;
            int rune = 1;
            while (runs < end)
            {
                int count = 0;
                while (rune < end && src[runs] == src[rune] && count < 127)
                {
                    rune++;
                    count++;
                }

                if (count >= 2)
                {
                    output.Add((byte)count);
                    output.Add(src[runs]);
                    runs = rune;
                }
                else
                {
                    count++;
                    while (rune < end && ((rune + 1 >= end || src[rune] != src[rune + 1]) || (rune + 2 >= end || src[rune + 1] != src[rune + 2])) && count < 127)
                    {
                        count++;
                        rune++;
                    }

                    output.Add((byte)(sbyte)-count);
                    while (runs < rune)
                    {
                        output.Add(src[runs++]);
                    }
                }

                rune++;
            }

            return [.. output];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(t);
        }
    }
}
