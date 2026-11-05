using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <content>Decoding to stored samples, for <see cref="SampleImage.Load(Stream, EgamiContext?, ReaderOptions?)"/>.</content>
internal sealed partial class JpegReader : ISampleFormatReader
{
    /// <summary>
    /// Decodes gray or three-component files with the color conversion turned off, through the usual 8 or 16-bit pixels, then
    /// takes each sample back to the file's precision: 8-bit samples pass through, and a 16-bit pixel holds a P-bit sample
    /// scaled by (2^16 − 1) / (2^P − 1), which the reverse rounding undoes exactly.
    /// </summary>
    public SampleImage ReadSamples(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        int count = header.Components.Length;
        if (count is not (1 or 3))
        {
            throw new NotSupportedException($"This JPEG has {count} components; stored samples are given for gray and three-component files.");
        }

        JpegColorSpace space = header.Details.ColorSpace;
        if (count == 3)
        {
            // Read the three components as they are stored: as an RGB file, which has no conversion.
            header.AdobeTransform = 0;
            header.Details.ColorSpace = JpegColorSpace.Rgb;
        }

        int precision = header.Precision;
        int[] samples = SampleImage.Allocate(input, header.Width, header.Height, count);
        Metadata.RasterMetadata metadata = precision <= 8
            ? (count == 1 ? Fill<Gray8>(input, header, options, precision, samples) : Fill<Rgb8>(input, header, options, precision, samples))
            : (count == 1 ? Fill<Gray16>(input, header, options, precision, samples) : Fill<Rgb16>(input, header, options, precision, samples));
        header.Details.ColorSpace = space;
        SampleComponent[] components = [.. Enumerable.Repeat(new SampleComponent(precision, false), count)];
        SampleColor color = space switch
        {
            JpegColorSpace.Gray => SampleColor.Gray,
            JpegColorSpace.Rgb => SampleColor.Rgb,
            _ => SampleColor.YCbCr,
        };
        return new SampleImage(header.Width, header.Height, components, color, samples, metadata);
    }

    /// <summary>Decodes into <typeparamref name="TPixel"/> and narrows its samples into <paramref name="samples"/>; returns the metadata.</summary>
    private static Metadata.RasterMetadata Fill<TPixel>(EgamiInput input, JpegHeader header, ReaderOptions options, int precision, int[] samples)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> raster = Decode<TPixel>(input, header, options);
        int stride = raster.Width * TPixel.Layout.ChannelCount;
        for (int y = 0; y < raster.Height; y++)
        {
            ReadOnlySpan<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y));
            Span<int> target = samples.AsSpan(y * stride, stride);
            if (precision <= 8)
            {
                Narrow(row, 255, precision, target);
            }
            else
            {
                Narrow(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row), precision, target);
            }
        }

        return raster.Metadata;
    }

    /// <summary>Takes 8-bit pixel samples back to <paramref name="precision"/> bits.</summary>
    private static void Narrow(ReadOnlySpan<byte> source, int full, int precision, Span<int> target)
    {
        int max = (1 << precision) - 1;
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = max == full ? source[i] : ((source[i] * max) + (full >> 1)) / full;
        }
    }

    /// <summary>Takes 16-bit pixel samples back to <paramref name="precision"/> bits.</summary>
    private static void Narrow(ReadOnlySpan<ushort> source, int precision, Span<int> target)
    {
        long max = (1L << precision) - 1;
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = max == 65535 ? source[i] : (int)(((source[i] * max) + 32767) / 65535);
        }
    }
}
