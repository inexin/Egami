using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Formats.Bmp;

/// <summary>
/// Writes BMP with bottom-up rows: 24-bit with a BITMAPINFOHEADER, 32-bit BGRA with a BITMAPV4HEADER and bit-field masks, or a
/// 1, 2, 4 or 8-bit palette. An ICC profile switches to a BITMAPV5HEADER with the profile after the pixels. Only the first frame
/// is written.
/// </summary>
internal sealed class BmpWriter : IFormatWriter
{
    internal static readonly BmpWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        BmpWriterOptions bmp = options as BmpWriterOptions ?? new BmpWriterOptions { StripMetadata = options.StripMetadata };
        BmpPixelFormat layout = bmp.PixelFormat != BmpPixelFormat.Auto ? bmp.PixelFormat : !TPixel.Layout.HasAlpha || IsOpaque(raster) ? BmpPixelFormat.Bgr24 : BmpPixelFormat.Bgra32;
        int bits = layout switch
        {
            BmpPixelFormat.Indexed1 => 1,
            BmpPixelFormat.Indexed2 => 2,
            BmpPixelFormat.Indexed4 => 4,
            BmpPixelFormat.Indexed8 => 8,
            BmpPixelFormat.Bgr24 => 24,
            _ => 32,
        };

        int width = raster.Width;
        int height = raster.Height;
        long stride = (((long)width * bits) + 31) / 32 * 4;
        long imageSize = stride * height;
        ReadOnlyMemory<byte> profile = bmp.StripMetadata ? default : raster.Metadata.IccProfile;
        int headerSize = !profile.IsEmpty ? 124 : layout == BmpPixelFormat.Bgra32 ? 108 : 40;

        BuiltPalette? palette = null;
        byte[]? indices = null;
        try
        {
            if (bits <= 8)
            {
                (palette, indices) = MapToPalette(raster, bmp.Palette, 1 << bits);
            }

            int paletteBytes = (palette?.Colors.Length ?? 0) * 4;
            long offset = 14 + headerSize + paletteBytes;
            long fileSize = offset + imageSize + profile.Length;
            if (fileSize > uint.MaxValue)
            {
                throw new ArgumentException($"A {width}×{height} image at {bits} bits a pixel is too large for BMP, whose sizes are 32-bit.", nameof(raster));
            }

            using StreamBufferWriter writer = new(output);
            Span<byte> header = writer.GetSpan((int)offset);
            header[..(int)offset].Clear();
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)fileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[10..], (uint)offset);
            Span<byte> info = header.Slice(14, headerSize);
            BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)headerSize);
            BinaryPrimitives.WriteInt32LittleEndian(info[4..], width);
            BinaryPrimitives.WriteInt32LittleEndian(info[8..], height);
            BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(info[14..], (ushort)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(info[16..], layout == BmpPixelFormat.Bgra32 ? 3u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(info[20..], (uint)imageSize);
            (int xppm, int yppm) = PixelsPerMeter(raster.Metadata.Resolution);
            BinaryPrimitives.WriteInt32LittleEndian(info[24..], xppm);
            BinaryPrimitives.WriteInt32LittleEndian(info[28..], yppm);
            BinaryPrimitives.WriteUInt32LittleEndian(info[32..], (uint)(palette?.Colors.Length ?? 0));
            if (headerSize >= 108)
            {
                if (layout == BmpPixelFormat.Bgra32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(info[40..], 0x00FF_0000);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[44..], 0x0000_FF00);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[48..], 0x0000_00FF);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[52..], 0xFF00_0000);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(info[56..], profile.IsEmpty ? BmpReader.SpaceSrgb : BmpReader.SpaceEmbedded);
            }

            if (headerSize == 124)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(info[108..], 4); // LCS_GM_IMAGES, the perceptual intent
                BinaryPrimitives.WriteUInt32LittleEndian(info[112..], (uint)(headerSize + paletteBytes + imageSize));
                BinaryPrimitives.WriteUInt32LittleEndian(info[116..], (uint)profile.Length);
            }

            if (palette is not null)
            {
                Span<byte> entries = header.Slice(14 + headerSize, paletteBytes);
                for (int i = 0; i < palette.Colors.Length; i++)
                {
                    Rgba8 c = palette.Colors[i];
                    entries[4 * i] = c.B;
                    entries[(4 * i) + 1] = c.G;
                    entries[(4 * i) + 2] = c.R;
                }
            }

            writer.Advance((int)offset);
            WriteRows(writer, raster, bits, (int)stride, indices);
            if (!profile.IsEmpty)
            {
                profile.Span.CopyTo(writer.GetSpan(profile.Length));
                writer.Advance(profile.Length);
            }

            writer.Flush();
        }
        finally
        {
            palette?.Release();
            if (indices is not null)
            {
                ArrayPool<byte>.Shared.Return(indices);
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

    private static void WriteRows<TPixel>(StreamBufferWriter writer, Raster<TPixel> raster, int bits, int stride, byte[]? indices)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width;
        for (int y = raster.Height - 1; y >= 0; y--)
        {
            Span<byte> row = writer.GetSpan(stride)[..stride];
            switch (bits)
            {
                case 24:
                    PixelConvert.Convert<TPixel, Bgr8>(raster.Row(y), MemoryMarshal.Cast<byte, Bgr8>(row[..(width * 3)]));
                    row[(width * 3)..].Clear();
                    break;
                case 32:
                    PixelConvert.Convert<TPixel, Bgra8>(raster.Row(y), MemoryMarshal.Cast<byte, Bgra8>(row));
                    break;
                default:
                    row.Clear();
                    ReadOnlySpan<byte> source = indices.AsSpan(y * width, width);
                    int perByte = 8 / bits;
                    for (int x = 0; x < width; x++)
                    {
                        row[x / perByte] |= (byte)(source[x] << (8 - (bits * ((x % perByte) + 1))));
                    }

                    break;
            }

            writer.Advance(stride);
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
