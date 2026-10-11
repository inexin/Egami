using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;
using Egami.Streaming;

namespace Egami.Formats.Tga;

/// <summary>
/// Writes TGA from the 2.0 specification: bottom-left origin, run-length packets that never cross a row end, and a 2.0 footer
/// with an extension area whose alpha type says straight alpha or none, holding the Author and Comment text entries. Only the
/// first frame is written.
/// </summary>
internal sealed class TgaWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly TgaWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TgaWriterOptions tga = start.Options as TgaWriterOptions ?? new TgaWriterOptions { StripMetadata = start.Options.StripMetadata };
        if (start.Width > ushort.MaxValue || start.Height > ushort.MaxValue)
        {
            throw new ArgumentException($"TGA holds at most 65535×65535 pixels; the image is {start.Width}×{start.Height}.");
        }

        if (tga.PixelFormat == TgaPixelFormat.Auto && TPixel.Layout.HasAlpha && TPixel.Layout.ChannelOrder != PixelChannelOrder.Gray)
        {
            start.Refuse("TGA", "Choosing 24 or 32 bits by whether any pixel is transparent", "Set TgaWriterOptions.PixelFormat to Bgr24 or Bgra32.");
        }

        if (tga.PixelFormat == TgaPixelFormat.Indexed8)
        {
            start.Refuse("TGA", "Building a palette", "Use Gray8, Bgr24 or Bgra32.");
        }

        return new Encoder<TPixel>(output, start, tga);
    }

    /// <summary>
    /// Writes the header and color map, then rows: bottom-up in file order for Save, put in place on a seekable stream when
    /// streaming uncompressed rows, and otherwise top-down with a top-left origin; then the extension area and footer.
    /// </summary>
    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly StreamBufferWriter _writer;
        private readonly RasterMetadata _metadata;
        private readonly TgaWriterOptions _tga;
        private readonly TgaPixelFormat _bits;
        private readonly int _width;
        private readonly int _bpp;
        private readonly bool _alpha;
        private readonly BuiltPalette? _palette;
        private readonly byte[]? _indices;
        private readonly byte[] _row;
        private readonly uint[] _keys;
        private readonly BottomUpRows? _rows;
        private long _position;
        private int _y;

        internal Encoder(Stream output, EncodeStart<TPixel> start, TgaWriterOptions tga)
        {
            _tga = tga;
            _metadata = start.Metadata;
            SaveLosses.Report(start, "TGA", SaveKeeps.Alpha | SaveKeeps.Comment, ["Comment", "Author"]);
            int width = _width = start.Width;
            int height = start.Height;
            PixelLayoutInfo layout = TPixel.Layout;
            TgaPixelFormat bits = tga.PixelFormat;
            if (bits == TgaPixelFormat.Auto)
            {
                bool opaque = !layout.HasAlpha || IsOpaque(start.Whole!);
                bits = layout.ChannelOrder == PixelChannelOrder.Gray ? TgaPixelFormat.Gray8 : opaque ? TgaPixelFormat.Bgr24 : TgaPixelFormat.Bgra32;
            }

            _bits = bits;
            int mapEntry = 0;
            if (bits == TgaPixelFormat.Indexed8)
            {
                (_palette, _indices) = MapToPalette(start.Whole!, tga.Palette);
                mapEntry = _palette.Colors.Any(c => c.A != 255) ? 32 : 24;
            }

            _alpha = bits == TgaPixelFormat.Bgra32 || mapEntry == 32;
            _bpp = bits switch { TgaPixelFormat.Gray8 or TgaPixelFormat.Indexed8 => 1, TgaPixelFormat.Bgr24 => 3, _ => 4 };
            ReadOnlySpan<byte> imageId = !tga.StripMetadata && start.Metadata.FormatDetails is TgaDetails details ? details.ImageId.Span : default;
            imageId = imageId[..Math.Min(imageId.Length, 255)];

            // Streamed rows come top-down; only uncompressed rows on a seekable stream can still be stored bottom-up.
            bool topDown = start.Streaming && (tga.Rle || !start.Seekable);
            _writer = new StreamBufferWriter(output);
            Span<byte> header = _writer.GetSpan(TgaReader.HeaderLength)[..TgaReader.HeaderLength];
            header.Clear();
            header[0] = (byte)imageId.Length;
            header[1] = (byte)(_palette is null ? 0 : 1);
            header[2] = (byte)((bits switch { TgaPixelFormat.Gray8 => 3, TgaPixelFormat.Indexed8 => 1, _ => 2 }) + (tga.Rle ? 8 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(header[5..], (ushort)(_palette?.Colors.Length ?? 0));
            header[7] = (byte)mapEntry;
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)height);
            header[16] = (byte)(_bpp * 8);
            header[17] = (byte)((bits == TgaPixelFormat.Bgra32 ? 8 : mapEntry == 32 ? 8 : 0) | (topDown ? 0x20 : 0));
            _writer.Advance(TgaReader.HeaderLength);
            _position += TgaReader.HeaderLength;
            Put(_writer, imageId, ref _position);
            if (_palette is not null)
            {
                int entryBytes = mapEntry / 8;
                Span<byte> map = _writer.GetSpan(_palette.Colors.Length * entryBytes);
                for (int i = 0; i < _palette.Colors.Length; i++)
                {
                    Rgba8 c = _palette.Colors[i];
                    map[i * entryBytes] = c.B;
                    map[(i * entryBytes) + 1] = c.G;
                    map[(i * entryBytes) + 2] = c.R;
                    if (entryBytes == 4)
                    {
                        map[(i * entryBytes) + 3] = c.A;
                    }
                }

                _writer.Advance(_palette.Colors.Length * entryBytes);
                _position += _palette.Colors.Length * entryBytes;
            }

            _row = ArrayPool<byte>.Shared.Rent(width * _bpp);
            _keys = ArrayPool<uint>.Shared.Rent(width);
            if (start.Streaming && !topDown)
            {
                _rows = new BottomUpRows(output, _writer, height, (long)width * _bpp, seek: true);
            }
        }

        public override void WriteRow(ReadOnlySpan<TPixel> source)
        {
            int width = _width;
            Span<byte> pixels = _row.AsSpan(0, width * _bpp);
            switch (_bits)
            {
                case TgaPixelFormat.Gray8:
                    PixelConvert.Convert<TPixel, Gray8>(source, MemoryMarshal.Cast<byte, Gray8>(pixels));
                    break;
                case TgaPixelFormat.Indexed8:
                    // Only Save maps to a palette, so the row's place in the image is known.
                    _indices.AsSpan(_y * width, width).CopyTo(pixels);
                    break;
                case TgaPixelFormat.Bgr24:
                    PixelConvert.Convert<TPixel, Bgr8>(source, MemoryMarshal.Cast<byte, Bgr8>(pixels));
                    break;
                default:
                    PixelConvert.Convert<TPixel, Bgra8>(source, MemoryMarshal.Cast<byte, Bgra8>(pixels));
                    break;
            }

            if (_rows is not null)
            {
                _rows.Write(_writer, pixels);
                _position += pixels.Length;
            }
            else if (_tga.Rle)
            {
                _position += WritePackets(_writer, pixels, _bpp, _keys.AsSpan(0, width));
            }
            else
            {
                Put(_writer, pixels, ref _position);
            }
        }

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            for (_y = raster.Height - 1; _y >= 0; _y--)
            {
                WriteRow(raster.Row(_y));
                if ((_y & 63) == 0)
                {
                    yield return _y;
                }
            }
        }

        public override void Complete()
        {
            if (_rows is not null)
            {
                _writer.Flush();
                _rows.End();
            }

            long extension = _position;
            Put(_writer, Extension(_metadata, _tga.StripMetadata, _alpha), ref _position);
            Span<byte> footer = _writer.GetSpan(TgaReader.FooterLength)[..TgaReader.FooterLength];
            BinaryPrimitives.WriteUInt32LittleEndian(footer, checked((uint)extension));
            BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], 0);
            TgaReader.FooterSignature.CopyTo(footer[8..]);
            _writer.Advance(TgaReader.FooterLength);
            _writer.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _writer.Dispose();
            ArrayPool<byte>.Shared.Return(_row);
            ArrayPool<uint>.Shared.Return(_keys);
            _palette?.Release();
            if (_indices is not null)
            {
                ArrayPool<byte>.Shared.Return(_indices);
            }

            base.Dispose(disposing);
        }
    }

    private static void Put(StreamBufferWriter writer, ReadOnlySpan<byte> bytes, ref long position)
    {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
        position += bytes.Length;
    }

    /// <summary>Writes one row as packets: a repeat packet for 2 or more equal pixels, raw packets between them, 128 pixels at most.</summary>
    private static int WritePackets(StreamBufferWriter writer, ReadOnlySpan<byte> pixels, int bpp, Span<uint> keys)
    {
        int count = pixels.Length / bpp;
        Pack(pixels, bpp, keys[..count]);
        Span<byte> output = writer.GetSpan(count * (bpp + 1));
        int n = 0;
        int x = 0;
        while (x < count)
        {
            uint key = keys[x];
            int run = 1;
            while (x + run < count && run < 128 && keys[x + run] == key)
            {
                run++;
            }

            if (run >= 2)
            {
                output[n++] = (byte)(0x80 | (run - 1));
                pixels.Slice(x * bpp, bpp).CopyTo(output[n..]);
                n += bpp;
                x += run;
                continue;
            }

            int raw = 1;
            while (x + raw < count && raw < 128 && !(x + raw + 1 < count && keys[x + raw] == keys[x + raw + 1]))
            {
                raw++;
            }

            output[n++] = (byte)(raw - 1);
            pixels.Slice(x * bpp, raw * bpp).CopyTo(output[n..]);
            n += raw * bpp;
            x += raw;
        }

        writer.Advance(n);
        return n;
    }

    /// <summary>Packs each 1 to 4-byte pixel into a word, so runs compare in one instruction.</summary>
    private static void Pack(ReadOnlySpan<byte> pixels, int bpp, Span<uint> keys)
    {
        switch (bpp)
        {
            case 4:
                MemoryMarshal.Cast<byte, uint>(pixels).CopyTo(keys);
                break;
            case 1:
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = pixels[i];
                }

                break;
            default:
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = pixels[3 * i] | ((uint)pixels[(3 * i) + 1] << 8) | ((uint)pixels[(3 * i) + 2] << 16);
                }

                break;
        }
    }

    /// <summary>Builds the 495-byte extension area: author, comments (four lines of 80 characters) and the alpha type.</summary>
    private static byte[] Extension(RasterMetadata metadata, bool strip, bool alpha)
    {
        byte[] e = new byte[TgaReader.ExtensionLength];
        BinaryPrimitives.WriteUInt16LittleEndian(e, TgaReader.ExtensionLength);
        if (!strip)
        {
            string? author = metadata.Text.FirstOrDefault(t => t.Key == "Author")?.Value;
            if (author is not null)
            {
                Field(author, e.AsSpan(2, 40));
            }

            string? comment = metadata.Text.FirstOrDefault(t => t.Key == "Comment")?.Value;
            if (comment is not null)
            {
                string[] lines = comment.Split('\n');
                for (int i = 0; i < Math.Min(lines.Length, 4); i++)
                {
                    Field(lines[i].TrimEnd('\r'), e.AsSpan(43 + (81 * i), 80));
                }
            }
        }

        e[494] = (byte)(alpha ? TgaAlphaType.Straight : TgaAlphaType.None);
        return e;
    }

    private static void Field(string text, Span<byte> field)
    {
        string fitted = text.Length > field.Length ? text[..field.Length] : text;
        Encoding.Latin1.GetBytes(fitted, field);
    }

    private static (BuiltPalette Palette, byte[] Indices) MapToPalette<TPixel>(Raster<TPixel> raster, PaletteOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width;
        int height = raster.Height;
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        byte[] indices = ArrayPool<byte>.Shared.Rent(checked(width * height));
        BuiltPalette? palette = null;
        try
        {
            PaletteOptions limited = options with { MaxColors = Math.Min(options.MaxColors, 256) };
            palette = PaletteBuilder.Build(height, y =>
            {
                PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), row.AsSpan(0, width));
                return row.AsMemory(0, width);
            }, limited, binaryAlpha: false);
            using PaletteMapper mapper = new(palette.Colors, palette.TransparentIndex, withAlpha: true, 0);
            FrameMapper.Map(width, height, (y, target) => PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), target), palette, mapper, limited.Dither, indices.AsSpan(0, width * height));
            return (palette, indices);
        }
        catch
        {
            palette?.Release();
            ArrayPool<byte>.Shared.Return(indices);
            throw;
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }

    private static bool IsOpaque<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(raster.Width);
        try
        {
            for (int y = 0; y < raster.Height; y++)
            {
                Span<Rgba8> pixels = row.AsSpan(0, raster.Width);
                PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), pixels);
                foreach (Rgba8 p in pixels)
                {
                    if (p.A != 255)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }
}
