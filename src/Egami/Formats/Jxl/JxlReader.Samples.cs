using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <content>Decoding to stored samples, for <see cref="SampleImage.Load(Stream, EgamiContext?, ReaderOptions?)"/>.</content>
internal sealed partial class JxlReader : ISampleFormatReader
{
    /// <summary>
    /// Decodes an integer image (up to 16 bits a sample, gray or RGB, with or without alpha) in its natural pixel type and takes
    /// each sample back to the file's bits per sample: 8-bit samples pass through, and a 16-bit pixel holds a P-bit sample scaled
    /// by (2^16 − 1) / (2^P − 1), which the reverse rounding undoes. A lossless image gives the coded values exactly; a lossy one
    /// the decoded values rounded to the file's depth.
    /// </summary>
    public SampleImage ReadSamples(EgamiInput input, ReaderOptions options)
    {
        Raster raster = JxlImageDecoder.Decode(input, options with { Hdr = null });
        using (raster)
        {
            JxlDetails details = raster.Metadata.FormatDetails as JxlDetails ?? throw new InvalidOperationException("The JPEG XL decoder gave no details.");
            int precision = details.BitsPerSample;
            if (details.ExponentBits > 0 || precision > 16)
            {
                throw new NotSupportedException($"This JPEG XL image has {(details.ExponentBits > 0 ? "floating-point" : $"{precision}-bit")} samples; stored samples are given for integer samples up to 16 bits.");
            }

            PixelLayoutInfo layout = raster.Layout;
            SampleColor color = layout.ChannelOrder switch
            {
                PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => SampleColor.Gray,
                PixelChannelOrder.Rgb or PixelChannelOrder.Rgba => SampleColor.Rgb,
                _ => throw new NotSupportedException($"This JPEG XL image loads as {layout.ChannelOrder}; stored samples are given for gray and RGB, with or without alpha."),
            };
            int count = layout.ChannelCount;
            int[] samples = SampleImage.Allocate(input, raster.Width, raster.Height, count);
            switch (raster)
            {
                case Raster<Gray8> r: Narrow(r, precision, samples); break;
                case Raster<GrayAlpha8> r: Narrow(r, precision, samples); break;
                case Raster<Rgb8> r: Narrow(r, precision, samples); break;
                case Raster<Rgba8> r: Narrow(r, precision, samples); break;
                case Raster<Gray16> r: Narrow(r, precision, samples); break;
                case Raster<GrayAlpha16> r: Narrow(r, precision, samples); break;
                case Raster<Rgb16> r: Narrow(r, precision, samples); break;
                case Raster<Rgba16> r: Narrow(r, precision, samples); break;
                default: throw new NotSupportedException($"This JPEG XL image loads as {layout.ChannelOrder} at {layout.BitsPerChannel} bits; stored samples are given for integer gray and RGB.");
            }

            SampleComponent[] components = [.. Enumerable.Repeat(new SampleComponent(precision, false), count)];
            return new SampleImage(raster.Width, raster.Height, components, color, samples, raster.Metadata);
        }
    }

    /// <summary>Takes the samples of 8 or 16-bit pixels back to <paramref name="precision"/> bits.</summary>
    private static void Narrow<TPixel>(Raster<TPixel> raster, int precision, int[] samples)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int stride = raster.Width * TPixel.Layout.ChannelCount;
        long max = (1L << precision) - 1;
        for (int y = 0; y < raster.Height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            Span<int> target = samples.AsSpan(y * stride, stride);
            if (TPixel.Layout.BitsPerChannel == 8)
            {
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = max == 255 ? row[i] : (int)(((row[i] * max) + 127) / 255);
                }
            }
            else
            {
                ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(row);
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = max == 65535 ? words[i] : (int)(((words[i] * max) + 32767) / 65535);
                }
            }
        }
    }
}
