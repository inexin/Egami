using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>
/// Builds what a page should load as from libtiff's raw samples, independently of the reader: samples unpacked, the photometric
/// interpretation applied, and the result written in the pixel layout Egami chose, so a load can be compared byte for byte.
/// </summary>
internal static class TiffReference
{
    /// <summary>Returns the expected pixels of <paramref name="layout"/> as bytes, or <see langword="null"/> when the raw samples cannot say.</summary>
    internal static byte[]? Expected(JsonElement page, byte[] raw, PixelLayoutInfo layout)
    {
        int width = page.GetProperty("width").GetInt32();
        int height = page.GetProperty("height").GetInt32();
        int photometric = page.GetProperty("photometric").GetInt32();
        int compression = page.GetProperty("compression").GetInt32();
        int samples = page.GetProperty("samples").GetInt32();
        int bits = page.GetProperty("bits").GetInt32();
        int format = page.GetProperty("sampleFormat").GetInt32();
        bool planar = page.GetProperty("planar").GetInt32() == 2 && samples > 1;
        int[] extra = [.. page.GetProperty("extraSamples").EnumerateArray().Select(e => e.GetInt32())];
        if (compression is 34676 or 34677)
        {
            bits = 32;
            format = 3;
            samples = photometric == 32844 ? 1 : 3;
            planar = false;
        }

        if (compression == 7 && photometric == 6)
        {
            // libtiff hands JPEG YCbCr out as RGB.
            photometric = 2;
        }

        if (photometric is 6 or 8 or 9 or 10 || (photometric == 5 && samples < 4))
        {
            return null;
        }

        int colors = photometric switch
        {
            2 => 3,
            5 => 4,
            32845 => 3,
            _ => 1,
        };
        int alpha = -1;
        bool associated = false;
        for (int i = 0; i < extra.Length; i++)
        {
            if (extra[i] is 1 or 2)
            {
                alpha = colors + i;
                associated = extra[i] == 1;
                break;
            }
        }

        double max = format == 3 ? 1 : Math.Pow(2, bits) - 1;
        int planes = planar ? samples : 1;
        int unitSamples = planar ? 1 : samples;
        long rowBytes = (((long)width * unitSamples * bits) + 7) / 8;
        long planeBytes = rowBytes * height;
        int channels = layout.BytesPerPixel / BytesPerChannel(layout.ChannelType);
        byte[] result = new byte[(long)width * height * layout.BytesPerPixel];
        double[] s = new double[samples];
        double[] o = new double[4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int k = 0; k < samples; k++)
                {
                    long bit = planar
                        ? ((k * planeBytes) + (y * rowBytes)) * 8 + ((long)x * bits)
                        : (y * rowBytes * 8) + ((((long)x * samples) + k) * bits);
                    s[k] = Read(raw, bit, bits, format);
                }

                double a = alpha >= 0 ? s[alpha] / max : 1;
                int n;
                switch (photometric)
                {
                    case 0 or 1:
                        double g = s[0] / max;
                        g = photometric == 0 ? 1 - g : g;
                        g = associated ? Divide(g, a, format) : g;
                        o[0] = g;
                        n = 1;
                        break;
                    case 3:
                        ushort[] map = Map(page, bits);
                        int entries = 1 << bits;
                        int index = (int)s[0];
                        o[0] = map[index] / 65535.0;
                        o[1] = map[entries + index] / 65535.0;
                        o[2] = map[(2 * entries) + index] / 65535.0;
                        n = 3;
                        break;
                    case 5 when layout.ChannelOrder == PixelChannelOrder.Cmyk:
                        for (int c = 0; c < 4; c++)
                        {
                            o[c] = s[c] / max;
                        }

                        n = 4;
                        break;
                    case 5:
                        double k = s[3] / max;
                        o[0] = (1 - (s[0] / max)) * (1 - k);
                        o[1] = (1 - (s[1] / max)) * (1 - k);
                        o[2] = (1 - (s[2] / max)) * (1 - k);
                        n = 3;
                        break;
                    case 32844:
                        o[0] = s[0];
                        n = 1;
                        break;
                    case 32845:
                        o[0] = (3.2404542f * (float)s[0]) - (1.5371385f * (float)s[1]) - (0.4985314f * (float)s[2]);
                        o[1] = (-0.9692660f * (float)s[0]) + (1.8760108f * (float)s[1]) + (0.0415560f * (float)s[2]);
                        o[2] = (0.0556434f * (float)s[0]) - (0.2040259f * (float)s[1]) + (1.0572252f * (float)s[2]);
                        n = 3;
                        break;
                    default:
                        for (int c = 0; c < 3; c++)
                        {
                            o[c] = associated ? Divide(s[c] / max, a, format) : s[c] / max;
                        }

                        n = 3;
                        break;
                }

                if (n == 1 && channels >= 3)
                {
                    o[1] = o[2] = o[0];
                    n = 3;
                }

                if (channels == n + 1)
                {
                    o[n] = a;
                }

                Write(result.AsSpan((int)(((long)y * width) + x) * layout.BytesPerPixel), o.AsSpan(0, channels), layout.ChannelType);
            }
        }

        return result;
    }

    private static double Divide(double c, double a, int format) => a <= 0 ? 0 : format == 3 ? c / a : Math.Min(1, c / a);

    private static ushort[] Map(JsonElement page, int bits) => page.TryGetProperty("map", out JsonElement map)
        ? [.. map.EnumerateArray().Select(v => (ushort)v.GetInt32())]
        : throw new InvalidOperationException("No ColorMap in the reference.");

    private static double Read(byte[] raw, long bit, int bits, int format)
    {
        int at = (int)(bit >> 3);
        if (format == 3)
        {
            return bits switch
            {
                16 => (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at))),
                32 => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at)),
                _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(at)),
            };
        }

        ulong value = bits switch
        {
            8 => raw[at],
            16 => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at)),
            24 => (ulong)(raw[at] | (raw[at + 1] << 8) | (raw[at + 2] << 16)),
            32 => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)),
            _ => Bits(raw, bit, bits),
        };
        if (format == 2)
        {
            value ^= 1UL << (bits - 1);
        }

        return value;
    }

    private static ulong Bits(byte[] raw, long bit, int count)
    {
        ulong value = 0;
        for (int k = 0; k < count; k++, bit++)
        {
            value = (value << 1) | (uint)((raw[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1);
        }

        return value;
    }

    private static int BytesPerChannel(PixelChannelType type) => type switch
    {
        PixelChannelType.Unorm8 => 1,
        PixelChannelType.Unorm16 or PixelChannelType.Float16 => 2,
        _ => 4,
    };

    private static void Write(Span<byte> pixel, ReadOnlySpan<double> values, PixelChannelType type)
    {
        for (int c = 0; c < values.Length; c++)
        {
            double v = values[c];
            switch (type)
            {
                case PixelChannelType.Unorm8:
                    pixel[c] = (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
                    break;
                case PixelChannelType.Unorm16:
                    BinaryPrimitives.WriteUInt16LittleEndian(pixel[(c * 2)..], (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535, MidpointRounding.AwayFromZero));
                    break;
                case PixelChannelType.Float16:
                    BinaryPrimitives.WriteHalfLittleEndian(pixel[(c * 2)..], (Half)v);
                    break;
                default:
                    BinaryPrimitives.WriteSingleLittleEndian(pixel[(c * 4)..], (float)v);
                    break;
            }
        }
    }

    /// <summary>Returns a raster's pixels as bytes, rows packed.</summary>
    internal static byte[] Bytes(Raster raster) => raster.Accept(new BytesVisitor());

    private sealed class BytesVisitor : IRasterVisitor<byte[]>
    {
        public byte[] Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            byte[] bytes = new byte[(long)raster.Width * raster.Height * raster.Layout.BytesPerPixel];
            raster.CopyPixelsTo(bytes);
            return bytes;
        }
    }

    /// <summary>Returns the largest difference between two byte arrays read as samples of <paramref name="type"/>.</summary>
    internal static double MaxDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, PixelChannelType type)
    {
        double worst = 0;
        switch (type)
        {
            case PixelChannelType.Unorm8:
                for (int i = 0; i < a.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
                }

                break;
            case PixelChannelType.Unorm16:
                ReadOnlySpan<ushort> a16 = MemoryMarshal.Cast<byte, ushort>(a);
                ReadOnlySpan<ushort> b16 = MemoryMarshal.Cast<byte, ushort>(b);
                for (int i = 0; i < a16.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs(a16[i] - b16[i]));
                }

                break;
            case PixelChannelType.Float16:
                ReadOnlySpan<Half> ah = MemoryMarshal.Cast<byte, Half>(a);
                ReadOnlySpan<Half> bh = MemoryMarshal.Cast<byte, Half>(b);
                for (int i = 0; i < ah.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs((double)ah[i] - (double)bh[i]));
                }

                break;
            default:
                ReadOnlySpan<float> af = MemoryMarshal.Cast<byte, float>(a);
                ReadOnlySpan<float> bf = MemoryMarshal.Cast<byte, float>(b);
                for (int i = 0; i < af.Length; i++)
                {
                    double scale = Math.Max(1e-6, Math.Max(Math.Abs(af[i]), Math.Abs(bf[i])));
                    worst = Math.Max(worst, Math.Abs(af[i] - bf[i]) / scale);
                }

                break;
        }

        return worst;
    }
}
