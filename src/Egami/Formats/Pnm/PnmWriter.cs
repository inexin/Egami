using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Pixels;

namespace Egami.Formats.Pnm;

/// <summary>
/// Writes PBM, PGM, PPM and PAM, written from the Netpbm format pages. Maxval is 255 for 8-bit pixel types and 65535 for
/// wider ones (1 for PBM). Comment text entries become # lines. Only the first frame is written.
/// </summary>
internal sealed class PnmWriter : IFormatWriter
{
    internal static readonly PnmWriter Instance = new();

    private const int PlainLineLength = 70;

    private const byte LineFeed = 0x0A;

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        PnmWriterOptions pnm = options as PnmWriterOptions ?? new PnmWriterOptions { StripMetadata = options.StripMetadata };
        PixelLayoutInfo layout = TPixel.Layout;
        bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        PnmKind kind = pnm.Kind != PnmKind.Auto ? pnm.Kind
            : layout.ChannelType == PixelChannelType.Float32 && !layout.HasAlpha ? PnmKind.FloatMap
            : layout.HasAlpha ? PnmKind.Pam : gray ? PnmKind.Graymap : PnmKind.Pixmap;
        if (kind == PnmKind.FloatMap)
        {
            Pfm.Write(raster, output);
            return;
        }

        bool wide = kind != PnmKind.Bitmap && layout.NeedsWideSamples;
        int max = kind == PnmKind.Bitmap ? 1 : wide ? 65535 : 255;
        bool plain = pnm.PlainText && kind != PnmKind.Pam;
        int depth = kind switch
        {
            PnmKind.Bitmap or PnmKind.Graymap => 1,
            PnmKind.Pixmap => 3,
            _ => (gray ? 1 : 3) + (layout.HasAlpha ? 1 : 0),
        };

        using StreamBufferWriter writer = new(output);
        WriteHeader(writer, raster, pnm, kind, plain, depth, max, gray);
        Rows<TPixel> rows = new(raster, depth, wide);
        try
        {
            for (int y = 0; y < raster.Height; y++)
            {
                if (kind == PnmKind.Bitmap)
                {
                    WriteBitmapRow(writer, rows.Gray(y), plain);
                }
                else if (wide)
                {
                    WriteSamples(writer, rows.Wide(y), plain);
                }
                else
                {
                    WriteSamples(writer, rows.Narrow(y), plain);
                }
            }
        }
        finally
        {
            rows.Dispose();
        }

        writer.Flush();
    }

    private static void WriteHeader<TPixel>(StreamBufferWriter writer, Raster<TPixel> raster, PnmWriterOptions options, PnmKind kind, bool plain, int depth, int max, bool gray)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int magic = kind switch { PnmKind.Bitmap => 1, PnmKind.Graymap => 2, PnmKind.Pixmap => 3, _ => 7 };
        if (!plain && magic != 7)
        {
            magic += 3;
        }

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"P{magic}\n");
        if (!options.StripMetadata)
        {
            foreach (Metadata.TextEntry entry in raster.Metadata.Text)
            {
                if (entry.Key == "Comment")
                {
                    foreach (string line in entry.Value.Split('\n'))
                    {
                        text.Append("# ").Append(line.TrimEnd('\r')).Append('\n');
                    }
                }
            }
        }

        if (magic == 7)
        {
            string tuple = (gray, depth) switch
            {
                (true, 1) => "GRAYSCALE",
                (true, _) => "GRAYSCALE_ALPHA",
                (false, 3) => "RGB",
                _ => "RGB_ALPHA",
            };
            text.Append(CultureInfo.InvariantCulture, $"WIDTH {raster.Width}\nHEIGHT {raster.Height}\nDEPTH {depth}\nMAXVAL {max}\nTUPLTYPE {tuple}\nENDHDR\n");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"{raster.Width} {raster.Height}\n");
            if (magic is not (1 or 4))
            {
                text.Append(CultureInfo.InvariantCulture, $"{max}\n");
            }
        }

        string header = text.ToString();
        int count = Encoding.UTF8.GetByteCount(header);
        Encoding.UTF8.GetBytes(header, writer.GetSpan(count));
        writer.Advance(count);
    }

    private static void WriteBitmapRow(StreamBufferWriter writer, ReadOnlySpan<byte> gray, bool plain)
    {
        if (plain)
        {
            PlainLine line = new(writer.GetSpan((gray.Length * 2) + 1));
            foreach (byte g in gray)
            {
                line.Add(g < 128 ? 1 : 0);
            }

            writer.Advance(line.End());
            return;
        }

        int bytes = (gray.Length + 7) / 8;
        Span<byte> span = writer.GetSpan(bytes);
        span[..bytes].Clear();
        for (int x = 0; x < gray.Length; x++)
        {
            if (gray[x] < 128)
            {
                span[x >> 3] |= (byte)(0x80 >> (x & 7));
            }
        }

        writer.Advance(bytes);
    }

    private static void WriteSamples(StreamBufferWriter writer, ReadOnlySpan<byte> samples, bool plain)
    {
        if (plain)
        {
            PlainLine line = new(writer.GetSpan((samples.Length * 4) + 1));
            foreach (byte s in samples)
            {
                line.Add(s);
            }

            writer.Advance(line.End());
            return;
        }

        samples.CopyTo(writer.GetSpan(samples.Length));
        writer.Advance(samples.Length);
    }

    private static void WriteSamples(StreamBufferWriter writer, ReadOnlySpan<ushort> samples, bool plain)
    {
        if (plain)
        {
            PlainLine line = new(writer.GetSpan((samples.Length * 6) + 1));
            foreach (ushort s in samples)
            {
                line.Add(s);
            }

            writer.Advance(line.End());
            return;
        }

        Span<ushort> span = MemoryMarshal.Cast<byte, ushort>(writer.GetSpan(samples.Length * 2)[..(samples.Length * 2)]);
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.ReverseEndianness(samples, span);
        }
        else
        {
            samples.CopyTo(span);
        }

        writer.Advance(samples.Length * 2);
    }

    /// <summary>
    /// Formats numbers separated by spaces into lines of at most 70 characters, straight into the output; each image row ends
    /// its line. The span must hold every number with a separator, plus a newline.
    /// </summary>
    private ref struct PlainLine(Span<byte> output)
    {
        private readonly Span<byte> _output = output;
        private int _count;
        private int _length;

        public void Add(int value)
        {
            int digits = value < 10 ? 1 : value < 100 ? 2 : value < 1000 ? 3 : value < 10000 ? 4 : 5;
            if (_length > 0)
            {
                bool wrap = _length + 1 + digits > PlainLineLength;
                _output[_count++] = wrap ? LineFeed : (byte)' ';
                _length = wrap ? 0 : _length + 1;
            }

            for (int i = digits - 1; i >= 0; i--)
            {
                _output[_count + i] = (byte)('0' + (value % 10));
                value /= 10;
            }

            _count += digits;
            _length += digits;
        }

        /// <summary>Ends the row's line and returns the bytes written.</summary>
        public int End()
        {
            _output[_count++] = LineFeed;
            return _count;
        }
    }

    /// <summary>Converts rows to samples in pooled buffers.</summary>
    private sealed class Rows<TPixel>(Raster<TPixel> raster, int depth, bool wide) : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly byte[] _narrow = ArrayPool<byte>.Shared.Rent(raster.Width * 4);
        private readonly ushort[] _wide = wide ? ArrayPool<ushort>.Shared.Rent(raster.Width * 4) : [];
        private readonly Rgba16[] _rgba16 = wide ? ArrayPool<Rgba16>.Shared.Rent(raster.Width) : [];

        public ReadOnlySpan<byte> Gray(int y)
        {
            Span<byte> gray = _narrow.AsSpan(0, raster.Width);
            PixelConvert.Convert<TPixel, Gray8>(raster.Row(y), MemoryMarshal.Cast<byte, Gray8>(gray));
            return gray;
        }

        public ReadOnlySpan<byte> Narrow(int y)
        {
            int width = raster.Width;
            Span<byte> samples = _narrow.AsSpan(0, width * depth);
            switch (depth)
            {
                case 1:
                    PixelConvert.Convert<TPixel, Gray8>(raster.Row(y), MemoryMarshal.Cast<byte, Gray8>(samples));
                    break;
                case 2:
                    PixelConvert.Convert<TPixel, GrayAlpha8>(raster.Row(y), MemoryMarshal.Cast<byte, GrayAlpha8>(samples));
                    break;
                case 3:
                    PixelConvert.Convert<TPixel, Rgb8>(raster.Row(y), MemoryMarshal.Cast<byte, Rgb8>(samples));
                    break;
                default:
                    PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), MemoryMarshal.Cast<byte, Rgba8>(samples));
                    break;
            }

            return samples;
        }

        public ReadOnlySpan<ushort> Wide(int y)
        {
            int width = raster.Width;
            Span<ushort> samples = _wide.AsSpan(0, width * depth);

            // 16-bit RGB and gray-alpha rows already hold the samples in file order.
            if ((depth == 3 && typeof(TPixel) == typeof(Rgb16)) || (depth == 2 && typeof(TPixel) == typeof(GrayAlpha16)))
            {
                return MemoryMarshal.Cast<TPixel, ushort>(raster.Row(y));
            }

            if (depth == 1)
            {
                PixelConvert.Convert<TPixel, Gray16>(raster.Row(y), MemoryMarshal.Cast<ushort, Gray16>(samples));
                return samples;
            }

            Span<Rgba16> pixels = _rgba16.AsSpan(0, width);
            PixelConvert.Convert<TPixel, Rgba16>(raster.Row(y), pixels);
            if (depth == 4)
            {
                MemoryMarshal.Cast<Rgba16, ushort>(pixels).CopyTo(samples);
                return samples;
            }

            if (depth == 2)
            {
                // Only gray types reach here, so the three color channels are equal.
                Span<ushort> gray = _wide.AsSpan(width * 2, width);
                PixelConvert.Convert<TPixel, Gray16>(raster.Row(y), MemoryMarshal.Cast<ushort, Gray16>(gray));
                for (int x = 0; x < width; x++)
                {
                    samples[2 * x] = gray[x];
                    samples[(2 * x) + 1] = pixels[x].A;
                }

                return samples;
            }

            for (int x = 0; x < width; x++)
            {
                samples[3 * x] = pixels[x].R;
                samples[(3 * x) + 1] = pixels[x].G;
                samples[(3 * x) + 2] = pixels[x].B;
            }

            return samples;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_narrow);
            if (wide)
            {
                ArrayPool<ushort>.Shared.Return(_wide);
                ArrayPool<Rgba16>.Shared.Return(_rgba16);
            }
        }
    }
}
