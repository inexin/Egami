using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The lossless JPEG writer's sample-row entry point, which TIFF and DNG tiles use: it codes exactly what the pixel path codes
/// for the same samples, every precision round trips, and a CFA mosaic coded as two components half as wide comes back as it
/// went in.
/// </summary>
public class JpegLosslessSampleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int?, bool, int> Settings()
    {
        TheoryData<int?, bool, int> data = [];
        foreach (int? predictor in (int?[])[null, 1, 2, 3, 4, 5, 6, 7])
        {
            data.Add(predictor, false, 0);
            data.Add(predictor, true, 0);
        }

        data.Add(4, false, 5);
        data.Add(1, true, 40);
        return data;
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void SamplesCodeAsThePixelPathDoes(int? predictor, bool arithmetic, int restart)
    {
        using Raster<Rgb8> rgb = JpegLosslessWriteTests.Noisy(37, 23, 21);
        AssertSamePath(rgb, 3, predictor, arithmetic, restart, invert: false);
        using Raster<Rgb16> rgb16 = rgb.As<Rgb16>();
        AssertSamePath(rgb16, 3, predictor, arithmetic, restart, invert: false);
        using Raster<Gray8> gray = rgb.As<Gray8>();
        AssertSamePath(gray, 1, predictor, arithmetic, restart, invert: false);
        using Raster<Gray16> gray16 = rgb.As<Gray16>();
        AssertSamePath(gray16, 1, predictor, arithmetic, restart, invert: false);
        using Raster<Cmyk8> cmyk = rgb.As<Cmyk8>();
        AssertSamePath(cmyk, 4, predictor, arithmetic, restart, invert: true);
        using Raster<Cmyk16> cmyk16 = rgb.As<Cmyk16>();
        AssertSamePath(cmyk16, 4, predictor, arithmetic, restart, invert: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryPrecisionRoundTrips(int components)
    {
        Random random = new(components);
        for (int precision = 2; precision <= 16; precision++)
        {
            int width = 19, height = 11;
            ushort[] samples = new ushort[width * height * components];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (ushort)random.Next(1 << precision);
            }

            foreach (int? predictor in (int?[])[null, 1, 6])
            {
                byte[] file = Encode(samples, components, width, precision, predictor);
                ushort[] back = JpegEmbedded.DecodeLosslessSamples(default, file, EgamiContext.Default, out JpegDetails details, Ct);
                Assert.Equal(samples, back);
                Assert.Equal(precision, details.Precision);
            }
        }
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(256, 256)]
    [InlineData(64, 1)]
    public void ACfaMosaicCodesAsTwoComponentsHalfAsWide(int width, int height)
    {
        ushort[] mosaic = Mosaic(width, height, 14);
        using MemoryStream output = new();
        JpegLosslessWriter.WriteCfa(output, mosaic, width, 14);
        byte[] file = output.ToArray();
        ushort[] back = JpegEmbedded.DecodeLosslessSamples(default, file, EgamiContext.Default, out _, Ct);
        Assert.Equal(mosaic, back);

        // The frame is half as wide, with two components, coded with predictor 1 as Adobe's DNG files are.
        (int frameWidth, int components, int predictor) = Frame(file);
        Assert.Equal((width / 2, 2, 1), (frameWidth, components, predictor));
    }

    [Fact]
    public void BadArgumentsAreRefused()
    {
        ushort[] samples = new ushort[12];
        using MemoryStream output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLosslessWriter.Write(output, samples, 0, 4, 8, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLosslessWriter.Write(output, samples, 5, 4, 8, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLosslessWriter.Write(output, samples, 1, 4, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLosslessWriter.Write(output, samples, 1, 4, 17, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLosslessWriter.Write(output, samples, 1, 4, 8, 8));
        Assert.Throws<ArgumentException>(() => JpegLosslessWriter.Write(output, samples, 1, 5, 8, 1));
        Assert.Throws<ArgumentException>(() => JpegLosslessWriter.Write(output, [], 1, 4, 8, 1));
        Assert.Throws<ArgumentException>(() => JpegLosslessWriter.WriteCfa(output, samples, 3, 8));
    }

    /// <summary>A Bayer-like mosaic: smooth per colour, the four sites of each 2 × 2 cell at different levels, with sensor noise.</summary>
    internal static ushort[] Mosaic(int width, int height, int bits)
    {
        Random random = new(width + height);
        int max = (1 << bits) - 1;
        ushort[] mosaic = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double level = ((x + y) * 7.0 / (width + height)) + ((x & 1) * 0.5) + ((y & 1) * 0.25);
                mosaic[(y * width) + x] = (ushort)Math.Clamp((int)(level * max / 8) + random.Next(-20, 21), 0, max);
            }
        }

        return mosaic;
    }

    private static byte[] Encode(ushort[] samples, int components, int width, int precision, int? predictor, int restart = 0, bool arithmetic = false)
    {
        using MemoryStream output = new();
        JpegLosslessWriter.Write(output, samples, components, width, precision, predictor, restart, arithmetic);
        return output.ToArray();
    }

    /// <summary>Saves the raster through the pixel path and its samples through the sample path, and compares the streams past the APP segments.</summary>
    private static void AssertSamePath<TPixel>(Raster<TPixel> raster, int components, int? predictor, bool arithmetic, int restart, bool invert)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream pixels = new();
        raster.Save(pixels, JpegFormat.Info, new JpegWriterOptions
        {
            Lossless = new JpegLosslessOptions { Predictor = predictor },
            Arithmetic = arithmetic,
            RestartInterval = restart,
            StripMetadata = true,
        });

        bool wide = TPixel.Layout.ChannelType != PixelChannelType.Unorm8;
        int max = wide ? 65535 : 255;
        ushort[] samples = new ushort[raster.Width * raster.Height * components];
        for (int y = 0; y < raster.Height; y++)
        {
            ReadOnlySpan<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y));
            for (int i = 0; i < raster.Width * components; i++)
            {
                int value = wide ? row[2 * i] | (row[(2 * i) + 1] << 8) : row[i];
                samples[(y * raster.Width * components) + i] = (ushort)(invert ? max - value : value);
            }
        }

        byte[] coded = Encode(samples, components, raster.Width, wide ? 16 : 8, predictor, restart, arithmetic);
        Assert.Equal(WithoutApp(pixels.ToArray()), coded);
    }

    /// <summary>Returns the stream without its APP segments.</summary>
    private static byte[] WithoutApp(byte[] jpeg)
    {
        List<byte> rest = [0xFF, 0xD8];
        int at = 2;
        while (jpeg[at + 1] is >= 0xE0 and <= 0xEF)
        {
            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        }

        rest.AddRange(jpeg.AsSpan(at).ToArray());
        return [.. rest];
    }

    /// <summary>Returns the SOF3 frame's width and component count and the scan's predictor.</summary>
    private static (int Width, int Components, int Predictor) Frame(byte[] jpeg)
    {
        int width = 0, components = 0, at = 2;
        while (true)
        {
            byte marker = jpeg[at + 1];
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (marker == 0xC3)
            {
                width = (jpeg[at + 7] << 8) | jpeg[at + 8];
                components = jpeg[at + 9];
            }
            else if (marker == 0xDA)
            {
                return (width, components, jpeg[at + 5 + (2 * jpeg[at + 4])]);
            }

            at += 2 + length;
        }
    }
}
