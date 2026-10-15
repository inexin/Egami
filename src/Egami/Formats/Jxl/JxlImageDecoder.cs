using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Decodes a JPEG XL image into a raster: the whole file read into memory, its frames decoded (LF frames kept for the frames
/// that use them), the restoration filters and upsampling run, XYB converted to the file's color space, then the samples put
/// into the natural pixel type (design question 5) and from there the type asked for.
/// </summary>
internal static class JxlImageDecoder
{
    /// <summary>Decodes into the natural pixel type.</summary>
    internal static Raster Decode(EgamiInput input, ReaderOptions options)
    {
        JxlDecoded decoded = DecodeImage(input);
        PixelLayoutInfo layout = JxlReader.NaturalLayout(decoded.File.Header);
        return layout switch
        {
            _ when layout == Gray8.Layout => Fill<Gray8>(input, decoded),
            _ when layout == GrayAlpha8.Layout => Fill<GrayAlpha8>(input, decoded),
            _ when layout == Rgb8.Layout => Fill<Rgb8>(input, decoded),
            _ when layout == Rgba8.Layout => Fill<Rgba8>(input, decoded),
            _ when layout == Gray16.Layout => Fill<Gray16>(input, decoded),
            _ when layout == GrayAlpha16.Layout => Fill<GrayAlpha16>(input, decoded),
            _ when layout == Rgb16.Layout => Fill<Rgb16>(input, decoded),
            _ when layout == Rgba16.Layout => Fill<Rgba16>(input, decoded),
            _ when layout == RgbaF16.Layout => Fill<RgbaF16>(input, decoded),
            _ when layout == GrayF32.Layout => Fill<GrayF32>(input, decoded),
            _ when layout == RgbF32.Layout => Fill<RgbF32>(input, decoded),
            _ when layout == RgbaF32.Layout => Fill<RgbaF32>(input, decoded),
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
        JxlDecoded decoded = DecodeImage(input);
        bool gray = decoded.File.Header.ColorChannels == 1;
        bool alpha = decoded.File.Header.AlphaIndex >= 0;
        decoded.ForceFloat();
        return gray && !alpha ? Fill<GrayF32>(input, decoded) : Fill<RgbaF32>(input, decoded);
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
        if (bits == 32 && exponentBits == 8)
        {
            return BitConverter.UInt32BitsToSingle(sample);
        }

        bool negative = (sample & (1u << (bits - 1))) != 0;
        uint mantissa = sample & ((1u << mantissaBits) - 1);
        int exponent = (int)((sample >> mantissaBits) & ((1u << exponentBits) - 1));
        int bias = (1 << (exponentBits - 1)) - 1;
        if (exponent == 0)
        {
            float sub = mantissa * MathF.Pow(2, 1 - bias - mantissaBits);
            return negative ? -sub : sub;
        }

        if (exponent == (1 << exponentBits) - 1)
        {
            return mantissa == 0 ? (negative ? float.NegativeInfinity : float.PositiveInfinity) : float.NaN;
        }

        float value = (1 + (mantissa / (float)(1u << mantissaBits))) * MathF.Pow(2, exponent - bias);
        return negative ? -value : value;
    }

    private static JxlDecoded DecodeImage(EgamiInput input)
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
                return DecodeFrames(file, codestream, input);
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

    private static JxlDecoded DecodeFrames(JxlFile file, byte[] codestream, EgamiInput input)
    {
        JxlImageHeader h = file.Header;
        float[][]?[] lfFrames = new float[4][][];
        int displayed = file.Frames.Count(f => f.IsNormal);
        for (int i = 0; i < file.Frames.Count; i++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            JxlFrameHeader f = file.Frames[i];
            if (f.Type == JxlFrameType.Lf)
            {
                JxlFrameChannels lf = new JxlFrameDecoder(codestream, h, f, file.Tocs[i], file.SectionOffsets[i], input.Limits).Decode(lfFrames[f.LfLevel]);
                lfFrames[f.LfLevel - 1] = XybPlanes(lf, h);
                continue;
            }

            if (f.Type == JxlFrameType.ReferenceOnly || displayed != 1 || f.HaveCrop || f.Blending.Mode != JxlBlendMode.Replace)
            {
                throw new UnsupportedFormatException("Egami does not decode JPEG XL animations, layers or crops yet.");
            }

            if (f.YCbCr)
            {
                throw new UnsupportedFormatException("Egami does not decode recompressed JPEG frames yet.");
            }

            JxlFrameChannels frame = new JxlFrameDecoder(codestream, h, f, file.Tocs[i], file.SectionOffsets[i], input.Limits).Decode(lfFrames[f.LfLevel]);
            return Finish(file, f, frame, h);
        }

        throw new CorruptDataException(JxlFormat.Info.Name, -1, "The codestream has no frame to show.");
    }

    /// <summary>Gets an LF frame's X, Y and B at its coded size.</summary>
    private static float[][] XybPlanes(JxlFrameChannels lf, JxlImageHeader h)
    {
        int count = lf.Width * lf.Height;
        if (lf.ColorPlanes is float[][] planes)
        {
            return [.. planes.Select((p, c) => Crop(p, lf.ColorStrides[c], lf.Width, lf.Height))];
        }

        return ModularXyb(lf, count);
    }

    /// <summary>Converts a Modular XYB frame's integer Y, X and B into X, Y and B floats (B coded as B - Y).</summary>
    private static float[][] ModularXyb(JxlFrameChannels frame, int count)
    {
        int[] y = frame.Channels[0].Data;
        int[] x = frame.Channels[1].Data;
        int[] b = frame.Channels[2].Data;
        float mx = frame.LfDequant[0] / 128;
        float my = frame.LfDequant[1] / 128;
        float mb = frame.LfDequant[2] / 128;
        float[][] planes = [new float[count], new float[count], new float[count]];
        for (int i = 0; i < count; i++)
        {
            planes[0][i] = x[i] * mx;
            planes[1][i] = y[i] * my;
            planes[2][i] = (int)Math.Clamp((long)b[i] + y[i], int.MinValue, int.MaxValue) * mb;
        }

        return planes;
    }

    private static float[] Crop(float[] plane, int stride, int width, int height)
    {
        if (stride == width && plane.Length == width * height)
        {
            return plane;
        }

        float[] output = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            plane.AsSpan(y * stride, width).CopyTo(output.AsSpan(y * width));
        }

        return output;
    }

    /// <summary>Runs the filters and upsampling, and converts color, on a decoded frame.</summary>
    private static JxlDecoded Finish(JxlFile file, JxlFrameHeader f, JxlFrameChannels frame, JxlImageHeader h)
    {
        int width = frame.Width;
        int height = frame.Height;
        bool filters = f.Gaborish || f.EpfIterations > 0;
        bool xyb = h.XybEncoded;
        if (frame.ColorPlanes is null && !xyb && !filters && f.Upsampling == 1 && f.ExtraChannelUpsampling.All(u => u == 1))
        {
            // Lossless Modular: keep the integers, so 8 and 16-bit output is exact.
            return new JxlDecoded(file, f.Width, f.Height, frame);
        }

        float[][] color;
        int count = width * height;
        if (frame.ColorPlanes is float[][] planes)
        {
            color = [.. planes.Select((p, c) => Crop(p, frame.ColorStrides[c], width, height))];
        }
        else if (xyb)
        {
            color = ModularXyb(frame, count);
        }
        else
        {
            color = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                int[] data = frame.Channels[Math.Min(c, frame.ColorChannels - 1)].Data;
                float[] plane = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plane[i] = Float(data[i], h.BitDepth);
                }

                color[c] = plane;
            }
        }

        if (f.Gaborish)
        {
            JxlFilters.Gaborish(color, width, height, width, f.LoopFilter.GaborishWeights);
        }

        if (f.EpfIterations > 0)
        {
            float[]? sigma = frame.VarDct?.EpfSigma;
            int blocksX = frame.VarDct?.BlocksX ?? 0;
            float modularSigma = f.LoopFilter.SigmaForModular;
            JxlFilters.Epf(color, width, height, width, f.LoopFilter, sigma is null ? (_, _) => modularSigma : (bx, by) => sigma[(by * blocksX) + bx]);
        }

        int outWidth = f.Width;
        int outHeight = f.Height;
        if (f.Upsampling > 1)
        {
            for (int c = 0; c < 3; c++)
            {
                color[c] = Crop(JxlFilters.Upsample(color[c], width, height, width, f.Upsampling, h), width * f.Upsampling, outWidth, outHeight);
            }
        }

        int outCount = outWidth * outHeight;
        if (xyb)
        {
            JxlColor.XybToLinear(color, outCount, h);
            if (!JxlColor.TransferOnly(h.Color))
            {
                throw new UnsupportedFormatException("Egami does not convert JPEG XL XYB to this color space yet.");
            }

            if (h.ColorChannels == 1)
            {
                // Gray: the luminance of linear sRGB.
                for (int i = 0; i < outCount; i++)
                {
                    color[1][i] = (0.2126f * color[0][i]) + (0.7152f * color[1][i]) + (0.0722f * color[2][i]);
                }

                color = [color[1], color[1], color[1]];
                JxlColor.Encode(color[0], outCount, h.Color);
            }
            else
            {
                for (int c = 0; c < 3; c++)
                {
                    JxlColor.Encode(color[c], outCount, h.Color);
                }
            }
        }

        // Extra channels: as floats, upsampled to the frame's full size where they were coded smaller.
        List<float[]> extras = [];
        for (int e = 0; e < h.ExtraChannels.Count; e++)
        {
            JxlChannel c = frame.Channels[frame.ExtraStart + e];
            float[] plane = new float[c.Width * c.Height];
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = Float(c.Data[i], h.ExtraChannels[e].BitDepth);
            }

            int factor = f.ExtraChannelUpsampling[e];
            if (factor > 1)
            {
                plane = Crop(JxlFilters.Upsample(plane, c.Width, c.Height, c.Width, factor, h), c.Width * factor, outWidth, outHeight);
            }

            extras.Add(plane);
        }

        return new JxlDecoded(file, outWidth, outHeight, color, extras);
    }

    private static Raster<TPixel> Fill<TPixel>(EgamiInput input, JxlDecoded d)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JxlImageHeader h = d.File.Header;
        Raster<TPixel> raster = input.CreateRaster<TPixel>(d.Width, d.Height);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatName = JxlFormat.Info.Name;
            metadata.FormatDetails = JxlDetails.From(d.File);
            if (d.File.Icc is byte[] icc)
            {
                metadata.IccProfile = icc;
            }

            if (d.File.Container.Exif is byte[] exif)
            {
                metadata.Exif = Jpeg.JpegSegmentReader.ParseExif(exif, input.Limits.MaxMetadataBytes, metadata.WarningList);
            }

            if (d.File.Container.Xmp is byte[] xmp)
            {
                metadata.Xmp = xmp;
            }

            // The codestream's orientation is the one that counts (design question 11); AutoOrient reads it from EXIF.
            if (h.Orientation != 1 || metadata.Exif is not null)
            {
                metadata.Exif ??= new ExifData();
                metadata.Exif.Orientation = (ImageOrientation)h.Orientation;
            }

            foreach (JxlProblem problem in d.File.Container.Problems)
            {
                metadata.WarningList.Add(problem.Id, problem.Message, problem.Offset);
            }

            PixelLayoutInfo layout = TPixel.Layout;
            int samples = layout.ChannelCount;
            bool alpha = layout.HasAlpha;
            int colorSamples = alpha ? samples - 1 : samples;
            int alphaIndex = h.AlphaIndex;
            for (int y = 0; y < d.Height; y++)
            {
                Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
                int at = y * d.Width;
                for (int x = 0; x < d.Width; x++)
                {
                    for (int s = 0; s < samples; s++)
                    {
                        bool isAlpha = alpha && s == samples - 1;
                        int i = (x * samples) + s;
                        if (d.Ints is JxlFrameChannels ints)
                        {
                            int[]? plane = isAlpha ? (alphaIndex >= 0 ? ints.Channels[ints.ExtraStart + alphaIndex].Data : null) : ints.Channels[Math.Min(s, ints.ColorChannels - 1)].Data;
                            JxlBitDepth depth = isAlpha && alphaIndex >= 0 ? h.ExtraChannels[alphaIndex].BitDepth : h.BitDepth;
                            int v = plane?[at + x] ?? 0;
                            switch (layout.ChannelType)
                            {
                                case PixelChannelType.Unorm8:
                                    row[i] = plane is null ? (byte)255 : (byte)Scale(v, depth, 255);
                                    break;
                                case PixelChannelType.Unorm16:
                                    MemoryMarshal.Cast<byte, ushort>(row)[i] = plane is null ? (ushort)65535 : (ushort)Scale(v, depth, 65535);
                                    break;
                                case PixelChannelType.Float16:
                                    MemoryMarshal.Cast<byte, Half>(row)[i] = (Half)(plane is null ? 1f : Float(v, depth));
                                    break;
                                default:
                                    MemoryMarshal.Cast<byte, float>(row)[i] = plane is null ? 1f : Float(v, depth);
                                    break;
                            }

                            continue;
                        }

                        float f = isAlpha ? (alphaIndex >= 0 ? d.Extras![alphaIndex][at + x] : 1f) : d.Color![Math.Min(s, colorSamples - 1) % 3][at + x];
                        switch (layout.ChannelType)
                        {
                            case PixelChannelType.Unorm8:
                                row[i] = (byte)MathF.Round(Math.Clamp(f, 0, 1) * 255);
                                break;
                            case PixelChannelType.Unorm16:
                                MemoryMarshal.Cast<byte, ushort>(row)[i] = (ushort)MathF.Round(Math.Clamp(f, 0, 1) * 65535);
                                break;
                            case PixelChannelType.Float16:
                                MemoryMarshal.Cast<byte, Half>(row)[i] = (Half)f;
                                break;
                            default:
                                MemoryMarshal.Cast<byte, float>(row)[i] = f;
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

    /// <summary>A decoded image: either the Modular integers as coded (lossless), or float planes after filters and color.</summary>
    private sealed class JxlDecoded
    {
        internal JxlDecoded(JxlFile file, int width, int height, JxlFrameChannels ints)
        {
            File = file;
            Width = width;
            Height = height;
            Ints = ints;
        }

        internal JxlDecoded(JxlFile file, int width, int height, float[][] color, List<float[]> extras)
        {
            File = file;
            Width = width;
            Height = height;
            Color = color;
            Extras = extras;
        }

        internal JxlFile File { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal JxlFrameChannels? Ints { get; private set; }

        internal float[][]? Color { get; private set; }

        internal List<float[]>? Extras { get; private set; }

        /// <summary>Turns the integer form into floats, unclamped.</summary>
        internal void ForceFloat()
        {
            if (Ints is not JxlFrameChannels ints)
            {
                return;
            }

            JxlImageHeader h = File.Header;
            int count = Width * Height;
            Color = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                int[] data = ints.Channels[Math.Min(c, ints.ColorChannels - 1)].Data;
                float[] plane = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plane[i] = Float(data[i], h.BitDepth);
                }

                Color[c] = plane;
            }

            Extras = [];
            for (int e = 0; e < h.ExtraChannels.Count; e++)
            {
                int[] data = ints.Channels[ints.ExtraStart + e].Data;
                float[] plane = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plane[i] = Float(data[i], h.ExtraChannels[e].BitDepth);
                }

                Extras.Add(plane);
            }

            Ints = null;
        }
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
