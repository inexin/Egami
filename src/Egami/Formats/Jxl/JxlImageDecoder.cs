using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Decodes a JPEG XL image into a raster: the whole file read into memory, its frames decoded, the samples converted from
/// the declared bit depth into the natural pixel type (design question 5), then into the type asked for.
/// </summary>
internal static class JxlImageDecoder
{
    /// <summary>Decodes into the natural pixel type.</summary>
    internal static Raster Decode(EgamiInput input, ReaderOptions options)
    {
        (JxlFile file, JxlFrameChannels frame) = DecodeFrame(input);
        PixelLayoutInfo layout = JxlReader.NaturalLayout(file.Header);
        return layout switch
        {
            _ when layout == Gray8.Layout => Fill<Gray8>(input, file, frame, 1, false),
            _ when layout == GrayAlpha8.Layout => Fill<GrayAlpha8>(input, file, frame, 1, true),
            _ when layout == Rgb8.Layout => Fill<Rgb8>(input, file, frame, 3, false),
            _ when layout == Rgba8.Layout => Fill<Rgba8>(input, file, frame, 3, true),
            _ when layout == Gray16.Layout => Fill<Gray16>(input, file, frame, 1, false),
            _ when layout == GrayAlpha16.Layout => Fill<GrayAlpha16>(input, file, frame, 1, true),
            _ when layout == Rgb16.Layout => Fill<Rgb16>(input, file, frame, 3, false),
            _ when layout == Rgba16.Layout => Fill<Rgba16>(input, file, frame, 3, true),
            _ when layout == RgbaF16.Layout => Fill<RgbaF16>(input, file, frame, 3, true),
            _ when layout == GrayF32.Layout => Fill<GrayF32>(input, file, frame, 1, false),
            _ when layout == RgbF32.Layout => Fill<RgbF32>(input, file, frame, 3, false),
            _ when layout == RgbaF32.Layout => Fill<RgbaF32>(input, file, frame, 3, true),
            _ => throw new UnsupportedFormatException("Egami does not decode JPEG XL CMYK images yet."),
        };
    }

    /// <summary>Decodes into <typeparamref name="TPixel"/>, through the natural type.</summary>
    internal static Raster<TPixel> Decode<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster natural = Decode(input, options);
        if (natural is Raster<TPixel> same)
        {
            return same;
        }

        using (natural)
        {
            Raster<TPixel> raster = input.CreateRaster<TPixel>(natural.Width, natural.Height);
            try
            {
                raster.Metadata.ReplaceWith(natural.Metadata.Copy());
                natural.Accept(new Converter<TPixel>(raster));
                return raster;
            }
            catch
            {
                raster.Dispose();
                throw;
            }
        }
    }

    /// <summary>Decodes to RgbaF32 or GrayF32 without clamping, as the conformance references are; Load goes through the natural type.</summary>
    internal static Raster DecodeFloat(EgamiInput input)
    {
        (JxlFile file, JxlFrameChannels frame) = DecodeFrame(input);
        bool gray = file.Header.ColorChannels == 1;
        bool alpha = file.Header.AlphaIndex >= 0;
        return gray && !alpha ? Fill<GrayF32>(input, file, frame, 1, false) : Fill<RgbaF32>(input, file, frame, 3, true);
    }

    private static (JxlFile File, JxlFrameChannels Frame) DecodeFrame(EgamiInput input)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            using MemoryStream stream = new(data, 0, length, writable: false);
            EgamiInput copy = new(stream, input.Context, input.CancellationToken);
            try
            {
                JxlFile file = JxlFile.Read(copy, allBoxes: true);
                byte[] codestream = file.Container.Codestream(data.AsSpan(0, length));
                if (file.Frames.Count != 1 || file.Frames[0] is not { Type: JxlFrameType.Regular, HaveCrop: false, Upsampling: 1, Blending.Mode: JxlBlendMode.Replace })
                {
                    throw new UnsupportedFormatException("Egami does not decode JPEG XL animations, layers, crops or upsampled frames yet.");
                }

                if (file.Header.XybEncoded || file.Frames[0].YCbCr || file.Frames[0].ExtraChannelUpsampling.Any(u => u != 1)
                    || file.Frames[0].Gaborish || file.Frames[0].EpfIterations > 0)
                {
                    throw new UnsupportedFormatException("Egami decodes lossless JPEG XL only so far.");
                }

                JxlFrameDecoder decoder = new(codestream, file.Header, file.Frames[0], file.Tocs[0], file.SectionOffsets[0], input.Limits);
                return (file, decoder.Decode());
            }
            finally
            {
                copy.Dispose();
            }
        }
        finally
        {
            if (pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    private static Raster<TPixel> Fill<TPixel>(EgamiInput input, JxlFile file, JxlFrameChannels frame, int color, bool alpha)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JxlImageHeader h = file.Header;
        Raster<TPixel> raster = input.CreateRaster<TPixel>(frame.Width, frame.Height);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatName = JxlFormat.Info.Name;
            metadata.FormatDetails = JxlDetails.From(file);
            if (file.Icc is byte[] icc)
            {
                metadata.IccProfile = icc;
            }

            List<int[]> planes = [];
            List<JxlBitDepth> depths = [];
            for (int c = 0; c < color; c++)
            {
                planes.Add(frame.Channels[Math.Min(c, frame.ColorChannels - 1)].Data);
                depths.Add(h.BitDepth);
            }

            int alphaIndex = h.AlphaIndex;
            if (alpha)
            {
                planes.Add(alphaIndex >= 0 ? frame.Channels[frame.ColorChannels + alphaIndex].Data : []);
                depths.Add(alphaIndex >= 0 ? h.ExtraChannels[alphaIndex].BitDepth : h.BitDepth);
            }

            PixelLayoutInfo layout = TPixel.Layout;
            int samples = planes.Count;
            for (int y = 0; y < frame.Height; y++)
            {
                Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
                int at = y * frame.Width;
                for (int x = 0; x < frame.Width; x++)
                {
                    for (int s = 0; s < samples; s++)
                    {
                        int[] plane = planes[s];
                        int v = plane.Length > 0 ? plane[at + x] : -1;
                        int i = (x * samples) + s;
                        switch (layout.ChannelType)
                        {
                            case PixelChannelType.Unorm8:
                                row[i] = v < 0 && plane.Length == 0 ? (byte)255 : (byte)Scale(v, depths[s], 255);
                                break;
                            case PixelChannelType.Unorm16:
                                MemoryMarshal.Cast<byte, ushort>(row)[i] = v < 0 && plane.Length == 0 ? (ushort)65535 : (ushort)Scale(v, depths[s], 65535);
                                break;
                            case PixelChannelType.Float16:
                                MemoryMarshal.Cast<byte, Half>(row)[i] = (Half)(plane.Length == 0 ? 1f : Float(v, depths[s]));
                                break;
                            default:
                                MemoryMarshal.Cast<byte, float>(row)[i] = plane.Length == 0 ? 1f : Float(v, depths[s]);
                                break;
                        }
                    }
                }
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Scales an integer sample of <paramref name="depth"/> bits to 0 to <paramref name="max"/>, rounding.</summary>
    private static int Scale(int v, JxlBitDepth depth, int max)
    {
        if (depth.IsFloat)
        {
            return (int)Math.Round(Math.Clamp(Float(v, depth), 0f, 1f) * max);
        }

        long top = (1L << depth.BitsPerSample) - 1;
        long clamped = Math.Clamp(v, 0, top);
        return top == max ? (int)clamped : (int)(((clamped * max * 2) + top) / (2 * top));
    }

    /// <summary>Gets a sample as a float: integers divided by their maximum, float bit patterns as they are (H.7).</summary>
    internal static float Float(int v, JxlBitDepth depth)
    {
        if (!depth.IsFloat)
        {
            return v / (float)((1L << depth.BitsPerSample) - 1);
        }

        int bits = depth.BitsPerSample;
        int exponentBits = depth.ExponentBits;
        int mantissaBits = bits - exponentBits - 1;
        uint sample = (uint)v;
        bool negative = (sample & (1u << (bits - 1))) != 0;
        uint mantissa = sample & ((1u << mantissaBits) - 1);
        int exponent = (int)((sample >> mantissaBits) & ((1u << exponentBits) - 1));
        if (bits == 32 && exponentBits == 8)
        {
            return BitConverter.UInt32BitsToSingle(sample);
        }

        int bias = (1 << (exponentBits - 1)) - 1;
        if (exponent == 0)
        {
            // Subnormal in the source format.
            float sub = mantissa * MathF.Pow(2, 1 - bias - mantissaBits);
            return negative ? -sub : sub;
        }

        if (exponent == (1 << exponentBits) - 1)
        {
            return mantissa == 0 ? (negative ? float.NegativeInfinity : float.PositiveInfinity) : float.NaN;
        }

        int e = exponent - bias + 127;
        uint m = mantissaBits <= 23 ? mantissa << (23 - mantissaBits) : mantissa >> (mantissaBits - 23);
        if (e <= 0)
        {
            float value = (1 + (mantissa / (float)(1u << mantissaBits))) * MathF.Pow(2, exponent - bias);
            return negative ? -value : value;
        }

        return BitConverter.UInt32BitsToSingle((negative ? 0x80000000u : 0) | ((uint)e << 23) | m);
    }

    private sealed class Converter<TPixel>(Raster<TPixel> target) : IRasterVisitor<Raster>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        public Raster Visit<TFrom>(Raster<TFrom> source)
            where TFrom : unmanaged, IPixelType<TFrom>
        {
            for (int y = 0; y < source.Height; y++)
            {
                PixelConvert.Convert<TFrom, TPixel>(source.Row(y), target.Row(y));
            }

            return target;
        }
    }
}
