using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Formats.Tga;

/// <summary>
/// Writes TGA from the 2.0 specification: bottom-left origin, run-length packets that never cross a row end, and a 2.0 footer
/// with an extension area whose alpha type says straight alpha or none, holding the Author and Comment text entries. Only the
/// first frame is written.
/// </summary>
internal sealed class TgaWriter : IFormatWriter
{
    internal static readonly TgaWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        TgaWriterOptions tga = options as TgaWriterOptions ?? new TgaWriterOptions { StripMetadata = options.StripMetadata };
        int width = raster.Width;
        int height = raster.Height;
        if (width > ushort.MaxValue || height > ushort.MaxValue)
        {
            throw new ArgumentException($"TGA holds at most 65535×65535 pixels; the image is {width}×{height}.", nameof(raster));
        }

        PixelLayoutInfo layout = TPixel.Layout;
        TgaBits bits = tga.Bits;
        if (bits == TgaBits.Auto)
        {
            bool opaque = !layout.HasAlpha || IsOpaque(raster);
            bits = layout.ChannelOrder == PixelChannelOrder.Gray ? TgaBits.Gray8 : opaque ? TgaBits.Bgr24 : TgaBits.Bgra32;
        }

        BuiltPalette? palette = null;
        byte[]? indices = null;
        try
        {
            int mapEntry = 0;
            if (bits == TgaBits.Indexed8)
            {
                (palette, indices) = MapToPalette(raster, tga.Palette);
                mapEntry = palette.Colors.Any(c => c.A != 255) ? 32 : 24;
            }

            bool alpha = bits == TgaBits.Bgra32 || mapEntry == 32;
            int bpp = bits switch { TgaBits.Gray8 or TgaBits.Indexed8 => 1, TgaBits.Bgr24 => 3, _ => 4 };
            ReadOnlySpan<byte> imageId = !tga.StripMetadata && raster.Metadata.FormatDetails is TgaDetails details ? details.ImageId.Span : default;
            imageId = imageId[..Math.Min(imageId.Length, 255)];

            using StreamBufferWriter writer = new(output);
            long position = 0;
            Span<byte> header = writer.GetSpan(TgaReader.HeaderLength)[..TgaReader.HeaderLength];
            header.Clear();
            header[0] = (byte)imageId.Length;
            header[1] = (byte)(palette is null ? 0 : 1);
            header[2] = (byte)((bits switch { TgaBits.Gray8 => 3, TgaBits.Indexed8 => 1, _ => 2 }) + (tga.Rle ? 8 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(header[5..], (ushort)(palette?.Colors.Length ?? 0));
            header[7] = (byte)mapEntry;
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)height);
            header[16] = (byte)(bpp * 8);
            header[17] = (byte)(bits == TgaBits.Bgra32 ? 8 : mapEntry == 32 ? 8 : 0);
            writer.Advance(TgaReader.HeaderLength);
            position += TgaReader.HeaderLength;
            Put(writer, imageId, ref position);
            if (palette is not null)
            {
                int entryBytes = mapEntry / 8;
                Span<byte> map = writer.GetSpan(palette.Colors.Length * entryBytes);
                for (int i = 0; i < palette.Colors.Length; i++)
                {
                    Rgba8 c = palette.Colors[i];
                    map[i * entryBytes] = c.B;
                    map[(i * entryBytes) + 1] = c.G;
                    map[(i * entryBytes) + 2] = c.R;
                    if (entryBytes == 4)
                    {
                        map[(i * entryBytes) + 3] = c.A;
                    }
                }

                writer.Advance(palette.Colors.Length * entryBytes);
                position += palette.Colors.Length * entryBytes;
            }

            byte[] row = ArrayPool<byte>.Shared.Rent(width * bpp);
            uint[] keyBuffer = ArrayPool<uint>.Shared.Rent(width);
            Span<uint> keys = keyBuffer.AsSpan(0, width);
            try
            {
                for (int y = height - 1; y >= 0; y--)
                {
                    Span<byte> pixels = row.AsSpan(0, width * bpp);
                    switch (bits)
                    {
                        case TgaBits.Gray8:
                            PixelConvert.Convert<TPixel, Gray8>(raster.Row(y), MemoryMarshal.Cast<byte, Gray8>(pixels));
                            break;
                        case TgaBits.Indexed8:
                            indices.AsSpan(y * width, width).CopyTo(pixels);
                            break;
                        case TgaBits.Bgr24:
                            PixelConvert.Convert<TPixel, Bgr8>(raster.Row(y), MemoryMarshal.Cast<byte, Bgr8>(pixels));
                            break;
                        default:
                            PixelConvert.Convert<TPixel, Bgra8>(raster.Row(y), MemoryMarshal.Cast<byte, Bgra8>(pixels));
                            break;
                    }

                    if (tga.Rle)
                    {
                        position += WritePackets(writer, pixels, bpp, keys);
                    }
                    else
                    {
                        Put(writer, pixels, ref position);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(row);
                ArrayPool<uint>.Shared.Return(keyBuffer);
            }

            long extension = position;
            Put(writer, Extension(raster.Metadata, tga.StripMetadata, alpha), ref position);
            Span<byte> footer = writer.GetSpan(TgaReader.FooterLength)[..TgaReader.FooterLength];
            BinaryPrimitives.WriteUInt32LittleEndian(footer, checked((uint)extension));
            BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], 0);
            TgaReader.FooterSignature.CopyTo(footer[8..]);
            writer.Advance(TgaReader.FooterLength);
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
