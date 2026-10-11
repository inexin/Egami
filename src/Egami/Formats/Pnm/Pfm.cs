using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Pnm;

/// <summary>
/// The Portable Float Map, from the Netpbm PFM page: "PF" (RGB) or "Pf" (gray), the width and height, and a scale whose sign
/// gives the byte order (negative for little-endian), each followed by whitespace; then rows of 32-bit floats, bottom row
/// first. It loads into <see cref="RgbF32"/> or <see cref="GrayF32"/>, its exact layouts, marked as linear light.
/// </summary>
internal static class Pfm
{
    private const int MaxToken = 64;

    /// <summary>Returns whether the input starts with a PFM signature.</summary>
    internal static bool Is(EgamiInput input)
    {
        ReadOnlySpan<byte> start = input.Peek(3);
        return start.Length == 3 && start[0] == 'P' && start[1] is (byte)'F' or (byte)'f' && PnmReader.IsSpace(start[2]);
    }

    internal static RasterInfo ReadInfo(EgamiInput input)
    {
        Header header = ReadHeader(input);
        PixelLayoutInfo layout = header.Color ? RgbF32.Layout : GrayF32.Layout;
        return new RasterInfo
        {
            Format = PnmFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = layout,
            FrameCount = 1,
            EstimatedBytes = (long)header.Width * header.Height * layout.BytesPerPixel,
        };
    }

    internal static Raster Read(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        return header.Color ? Read<RgbF32>(input, options, header) : Read<GrayF32>(input, options, header);
    }

    internal static Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Read<TPixel>(input, options, ReadHeader(input));
    }

    /// <summary>
    /// Starts writing RGB types as PF and gray types as Pf, little-endian with scale −1, as Netpbm and ImageMagick do. Rows are
    /// stored bottom row first, so streaming needs a seekable output.
    /// </summary>
    internal static RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (start.Streaming && !start.Seekable)
        {
            throw new NotSupportedException("PFM stores rows bottom-up, so streaming it needs a seekable stream.");
        }

        return new Encoder<TPixel>(output, start);
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly StreamBufferWriter _writer;
        private readonly bool _gray;
        private readonly int _width;
        private readonly float[] _row;
        private readonly BottomUpRows _rows;

        internal Encoder(Stream output, EncodeStart<TPixel> start)
        {
            _gray = TPixel.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha or PixelChannelOrder.Alpha;
            _width = start.Width;
            int channels = _gray ? 1 : 3;
            _writer = new StreamBufferWriter(output);
            string header = string.Create(CultureInfo.InvariantCulture, $"{(_gray ? "Pf" : "PF")}\n{start.Width} {start.Height}\n-1.0\n");
            Encoding.ASCII.GetBytes(header, _writer.GetSpan(header.Length));
            _writer.Advance(header.Length);
            _row = ArrayPool<float>.Shared.Rent(start.Width * channels);
            _rows = new BottomUpRows(output, _writer, start.Height, (long)start.Width * channels * 4, start.Streaming);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            Span<float> samples = _row.AsSpan(0, _width * (_gray ? 1 : 3));
            if (_gray)
            {
                PixelConvert.Convert(row, MemoryMarshal.Cast<float, GrayF32>(samples));
            }
            else
            {
                PixelConvert.Convert(row, MemoryMarshal.Cast<float, RgbF32>(samples));
            }

            if (!BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<float, uint>(samples), MemoryMarshal.Cast<float, uint>(samples));
            }

            _rows.Write(_writer, MemoryMarshal.AsBytes(samples));
        }

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            for (int y = raster.Height - 1; y >= 0; y--)
            {
                WriteRow(raster.Row(y));
                if ((y & 63) == 0)
                {
                    yield return y;
                }
            }
        }

        public override void Complete()
        {
            _writer.Flush();
            _rows.End();
        }

        protected override void Dispose(bool disposing)
        {
            _writer.Dispose();
            ArrayPool<float>.Shared.Return(_row);
            base.Dispose(disposing);
        }
    }

    private static Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options, Header header)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            raster.Metadata.FormatDetails = new PnmDetails
            {
                Kind = PnmKind.FloatMap,
                Scale = Math.Abs(header.Scale),
                LittleEndian = header.Scale < 0,
                TupleType = header.Color ? "RGB" : "GRAYSCALE",
            };
            raster.Metadata.ColorSpace = ColorSpaceHint.Linear;
            Decode(input, header, raster, options.Recovery == RecoveryMode.Strict);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static void Decode<TPixel>(EgamiInput input, Header header, Raster<TPixel> raster, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int channels = header.Color ? 3 : 1;
        int count = header.Width * channels;
        bool swap = (header.Scale < 0) != BitConverter.IsLittleEndian;
        bool direct = typeof(TPixel) == (header.Color ? typeof(RgbF32) : typeof(GrayF32));
        float[] buffer = direct ? [] : ArrayPool<float>.Shared.Rent(count);
        int read = 0;
        try
        {
            for (; read < header.Height; read++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                Span<TPixel> target = raster.Row(header.Height - 1 - read);
                Span<float> samples = direct ? MemoryMarshal.Cast<TPixel, float>(target) : buffer.AsSpan(0, count);
                input.ReadExactly(MemoryMarshal.AsBytes(samples));
                if (swap)
                {
                    BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<float, uint>(samples), MemoryMarshal.Cast<float, uint>(samples));
                }

                if (!direct)
                {
                    if (header.Color)
                    {
                        PixelConvert.Convert(MemoryMarshal.Cast<float, RgbF32>(samples), target);
                    }
                    else
                    {
                        PixelConvert.Convert(MemoryMarshal.Cast<float, GrayF32>(samples), target);
                    }
                }
            }
        }
        catch (CorruptDataException e) when (!strict)
        {
            // Rows are stored bottom first, so the rows never read are at the top.
            for (int y = 0; y < header.Height - read; y++)
            {
                raster.Row(y).Clear();
            }

            raster.Metadata.WarningList.Add("Pnm.RowsMissing", $"{e.Message} The top {header.Height - read} rows are left black.", e);
        }
        finally
        {
            if (buffer.Length > 0)
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
    }

    private static Header ReadHeader(EgamiInput input)
    {
        input.ReadByte();
        bool color = input.ReadByte() == 'F';
        int width = ParseInt(input, Token(input), "width");
        int height = ParseInt(input, Token(input), "height");
        string scaleText = Token(input);
        if (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || scale == 0 || !double.IsFinite(scale))
        {
            throw input.Corrupt(input.Position, $"The scale '{scaleText}' is not a nonzero number.");
        }

        long at = input.Position;
        if (!PnmReader.IsSpace(input.ReadByte()))
        {
            throw input.Corrupt(at, "The header does not end with a whitespace character.");
        }

        return new Header(color, width, height, scale);
    }

    /// <summary>Skips whitespace, then reads characters up to the next whitespace.</summary>
    private static string Token(EgamiInput input)
    {
        Span<char> text = stackalloc char[MaxToken];
        int length = 0;
        while (true)
        {
            long at = input.Position;
            ReadOnlySpan<byte> next = input.Peek(1);
            if (next.IsEmpty)
            {
                throw input.Corrupt(at, "The file ends inside the header.");
            }

            if (PnmReader.IsSpace(next[0]))
            {
                if (length > 0)
                {
                    return new string(text[..length]);
                }

                input.ReadByte();
                continue;
            }

            if (length == MaxToken || next[0] < 0x21 || next[0] > 0x7E)
            {
                throw input.Corrupt(at, "The header holds something that is not a number.");
            }

            text[length++] = (char)input.ReadByte();
        }
    }

    private static int ParseInt(EgamiInput input, string token, string name)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1)
        {
            throw input.Corrupt(input.Position, $"The {name} '{token}' is not a positive whole number.");
        }

        return value;
    }

    private sealed record Header(bool Color, int Width, int Height, double Scale);
}
