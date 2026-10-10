using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;
using Egami.Streaming;

namespace Egami.Formats.Bmp;

/// <summary>
/// Writes BMP with bottom-up rows: 24-bit with a BITMAPINFOHEADER, 32-bit BGRA with a BITMAPV4HEADER and bit-field masks, or a
/// 1, 2, 4 or 8-bit palette. An ICC profile switches to a BITMAPV5HEADER with the profile after the pixels. Only the first frame
/// is written.
/// </summary>
internal sealed class BmpWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly BmpWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        BmpWriterOptions bmp = start.Options as BmpWriterOptions ?? new BmpWriterOptions { StripMetadata = start.Options.StripMetadata };
        if (bmp.PixelFormat == BmpPixelFormat.Auto && TPixel.Layout.HasAlpha)
        {
            start.Refuse("BMP", "Choosing 24 or 32 bits by whether any pixel is transparent", "Set BmpWriterOptions.PixelFormat to Bgr24 or Bgra32.");
        }

        if (bmp.PixelFormat is BmpPixelFormat.Indexed1 or BmpPixelFormat.Indexed2 or BmpPixelFormat.Indexed4 or BmpPixelFormat.Indexed8)
        {
            start.Refuse("BMP", "Building a palette", "Use Bgr24 or Bgra32.");
        }

        return new Encoder<TPixel>(output, start, bmp);
    }

    /// <summary>
    /// Writes the headers, then rows: bottom-up in file order for Save, put in place on a seekable stream when streaming, and
    /// top-down (a negative height) on a stream that cannot seek.
    /// </summary>
    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly StreamBufferWriter _writer;
        private readonly int _width;
        private readonly int _bits;
        private readonly int _stride;
        private readonly bool _topDown;
        private readonly ReadOnlyMemory<byte> _profile;
        private readonly BottomUpRows? _rows;
        private readonly BuiltPalette? _palette;
        private readonly byte[]? _indices;
        private int _y;

        internal Encoder(Stream output, EncodeStart<TPixel> start, BmpWriterOptions bmp)
        {
            _output = output;
            Raster<TPixel>? raster = start.Whole;
            BmpPixelFormat layout = bmp.PixelFormat != BmpPixelFormat.Auto ? bmp.PixelFormat : !TPixel.Layout.HasAlpha || IsOpaque(raster!) ? BmpPixelFormat.Bgr24 : BmpPixelFormat.Bgra32;
            _bits = layout switch
            {
                BmpPixelFormat.Indexed1 => 1,
                BmpPixelFormat.Indexed2 => 2,
                BmpPixelFormat.Indexed4 => 4,
                BmpPixelFormat.Indexed8 => 8,
                BmpPixelFormat.Bgr24 => 24,
                _ => 32,
            };

            int width = _width = start.Width;
            int height = start.Height;
            long stride = (((long)width * _bits) + 31) / 32 * 4;
            long imageSize = stride * height;
            _profile = bmp.StripMetadata ? default : start.Options.ProfileToWrite(start.Metadata);
            int headerSize = !_profile.IsEmpty ? 124 : layout == BmpPixelFormat.Bgra32 ? 108 : 40;
            _topDown = start.Streaming && !start.Seekable;

            if (_bits <= 8)
            {
                (_palette, _indices) = MapToPalette(raster!, bmp.Palette, 1 << _bits);
            }

            int paletteBytes = (_palette?.Colors.Length ?? 0) * 4;
            long offset = 14 + headerSize + paletteBytes;
            long fileSize = offset + imageSize + _profile.Length;
            if (fileSize > uint.MaxValue)
            {
                throw new ArgumentException($"A {width}×{height} image at {_bits} bits a pixel is too large for BMP, whose sizes are 32-bit.");
            }

            _stride = (int)stride;
            _writer = new StreamBufferWriter(output);
            Span<byte> header = _writer.GetSpan((int)offset);
            header[..(int)offset].Clear();
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)fileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[10..], (uint)offset);
            Span<byte> info = header.Slice(14, headerSize);
            BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)headerSize);
            BinaryPrimitives.WriteInt32LittleEndian(info[4..], width);
            BinaryPrimitives.WriteInt32LittleEndian(info[8..], _topDown ? -height : height);
            BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(info[14..], (ushort)_bits);
            BinaryPrimitives.WriteUInt32LittleEndian(info[16..], layout == BmpPixelFormat.Bgra32 ? 3u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(info[20..], (uint)imageSize);
            (int xppm, int yppm) = PixelsPerMeter(start.Metadata.Resolution);
            BinaryPrimitives.WriteInt32LittleEndian(info[24..], xppm);
            BinaryPrimitives.WriteInt32LittleEndian(info[28..], yppm);
            BinaryPrimitives.WriteUInt32LittleEndian(info[32..], (uint)(_palette?.Colors.Length ?? 0));
            if (headerSize >= 108)
            {
                if (layout == BmpPixelFormat.Bgra32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(info[40..], 0x00FF_0000);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[44..], 0x0000_FF00);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[48..], 0x0000_00FF);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[52..], 0xFF00_0000);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(info[56..], _profile.IsEmpty ? BmpReader.SpaceSrgb : BmpReader.SpaceEmbedded);
            }

            if (headerSize == 124)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(info[108..], 4); // LCS_GM_IMAGES, the perceptual intent
                BinaryPrimitives.WriteUInt32LittleEndian(info[112..], (uint)(headerSize + paletteBytes + imageSize));
                BinaryPrimitives.WriteUInt32LittleEndian(info[116..], (uint)_profile.Length);
            }

            if (_palette is not null)
            {
                Span<byte> entries = header.Slice(14 + headerSize, paletteBytes);
                for (int i = 0; i < _palette.Colors.Length; i++)
                {
                    Rgba8 c = _palette.Colors[i];
                    entries[4 * i] = c.B;
                    entries[(4 * i) + 1] = c.G;
                    entries[(4 * i) + 2] = c.R;
                }
            }

            _writer.Advance((int)offset);
            if (start.Streaming && !_topDown)
            {
                _rows = new BottomUpRows(output, _writer, height, stride, seek: true);
            }
        }

        public override void WriteRow(ReadOnlySpan<TPixel> source)
        {
            Span<byte> row = _writer.GetSpan(_stride)[.._stride];
            Pack(source, row);
            if (_rows is null)
            {
                _writer.Advance(_stride);
            }
            else
            {
                // Put in place: the packed row goes straight to its offset, not through the buffer.
                _rows.Write(_writer, row);
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
            _writer.Flush();
            _rows?.End();
            if (!_profile.IsEmpty)
            {
                _output.Write(_profile.Span);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _writer.Dispose();
            _palette?.Release();
            if (_indices is not null)
            {
                ArrayPool<byte>.Shared.Return(_indices);
            }

            base.Dispose(disposing);
        }

        private void Pack(ReadOnlySpan<TPixel> source, Span<byte> row)
        {
            int width = _width;
            switch (_bits)
            {
                case 24:
                    PixelConvert.Convert<TPixel, Bgr8>(source, MemoryMarshal.Cast<byte, Bgr8>(row[..(width * 3)]));
                    row[(width * 3)..].Clear();
                    break;
                case 32:
                    PixelConvert.Convert<TPixel, Bgra8>(source, MemoryMarshal.Cast<byte, Bgra8>(row));
                    break;
                default:
                    // Only Save maps to a palette, so the row's place in the image is known.
                    row.Clear();
                    ReadOnlySpan<byte> indices = _indices.AsSpan(_y * width, width);
                    int perByte = 8 / _bits;
                    for (int x = 0; x < width; x++)
                    {
                        row[x / perByte] |= (byte)(indices[x] << (8 - (_bits * ((x % perByte) + 1))));
                    }

                    break;
            }
        }
    }

    private static (int X, int Y) PixelsPerMeter(Resolution? resolution)
    {
        if (resolution is not Resolution r || r.Unit == ResolutionUnit.None)
        {
            return (0, 0);
        }

        double factor = r.Unit switch { ResolutionUnit.PerInch => 1 / 0.0254, ResolutionUnit.PerCentimeter => 100, _ => 1 };
        return ((int)Math.Clamp(Math.Round(r.X * factor), 0, int.MaxValue), (int)Math.Clamp(Math.Round(r.Y * factor), 0, int.MaxValue));
    }

    /// <summary>Builds an opaque palette (exact when the colors fit) and maps the first frame to it.</summary>
    private static (BuiltPalette Palette, byte[] Indices) MapToPalette<TPixel>(Raster<TPixel> raster, PaletteOptions options, int maxColors)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width;
        int height = raster.Height;
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        byte[] indices = ArrayPool<byte>.Shared.Rent(checked(width * height));
        BuiltPalette? palette = null;
        try
        {
            void Opaque(int y, Span<Rgba8> target)
            {
                PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), target);
                foreach (ref Rgba8 p in target)
                {
                    p.A = 255;
                }
            }

            PaletteOptions limited = options with { MaxColors = Math.Min(options.MaxColors, maxColors) };
            palette = PaletteBuilder.Build(height, y =>
            {
                Opaque(y, row.AsSpan(0, width));
                return row.AsMemory(0, width);
            }, limited, binaryAlpha: false);
            using PaletteMapper mapper = new(palette.Colors, -1, withAlpha: false, 0);
            FrameMapper.Map(width, height, Opaque, palette, mapper, limited.Dither, indices.AsSpan(0, width * height));
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
