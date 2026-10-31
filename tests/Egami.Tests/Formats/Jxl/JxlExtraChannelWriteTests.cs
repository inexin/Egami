using Egami.Colors;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>JPEG XL extras task 6: JxlWriterOptions.ExtraChannels, and the black channel of lossless CMYK.</summary>
public class JxlExtraChannelWriteTests
{
    private const int Width = 70;
    private const int Height = 45;

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    [InlineData(true, 7)]
    [InlineData(false, 3)]
    [InlineData(false, 7)]
    public void ExtraChannelsComeBackExactly(bool lossless, int effort)
    {
        using Raster<Rgb8> image = Photo();
        List<(JxlExtraChannel Channel, Raster<GrayF32> Samples)> extras = Extras();
        try
        {
            byte[] file = Save(image, new JxlWriterOptions { Lossless = lossless, Effort = effort, ExtraChannels = [.. extras.Select(e => new JxlExtraChannelImage(e.Channel, e.Samples))] });
            using Raster loaded = Raster.Load(new MemoryStream(file));
            JxlDetails details = (JxlDetails)loaded.Metadata.FormatDetails!;
            Assert.Equal(extras.Count, details.ExtraChannels.Count);
            for (int i = 0; i < extras.Count; i++)
            {
                JxlExtraChannel expected = extras[i].Channel, actual = details.ExtraChannels[i];
                Assert.Equal((expected.Kind, expected.Name, expected.BitsPerSample, expected.ExponentBits, expected.Subsampling), (actual.Kind, actual.Name, actual.BitsPerSample, actual.ExponentBits, actual.Subsampling));
                using Raster<GrayF32> back = JxlImage.ReadExtraChannel(new MemoryStream(file), i, JxlReaderOptions.Default with { RenderSpotColors = false });
                AssertSamples(extras[i].Samples, back, expected.Subsampling);
            }

            // The spot color is stored as half floats.
            Assert.Equal([(float)(Half)0.9f, (float)(Half)0.1f, (float)(Half)0.2f, 1f], details.ExtraChannels[3].SpotColor);
        }
        finally
        {
            foreach ((_, Raster<GrayF32> samples) in extras)
            {
                samples.Dispose();
            }
        }
    }

    [Fact]
    public void ExtraChannelsFollowAlpha()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                image.Row(y)[x] = new Rgba8((byte)(x * 3), (byte)(y * 5), 90, (byte)(x * 255 / Width));
            }
        }

        using Raster<GrayF32> depth = Ramp(Width, Height, 0);
        JxlExtraChannel channel = new() { Kind = JxlExtraChannelKind.Depth, BitsPerSample = 12, Name = "depth" };
        byte[] file = Save(image, new JxlWriterOptions { Lossless = true, ExtraChannels = [new JxlExtraChannelImage(channel, depth)] });
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(new MemoryStream(file));
        Assert.Equal(image.Row(20)[33], loaded.Row(20)[33]);
        using Raster<GrayF32> back = JxlImage.ReadExtraChannel(new MemoryStream(file), 1);
        AssertSamples(Quantized(depth, 12), back, 0);
    }

    [Fact]
    public void LowBitrateLossyKeepsSubsampledChannelsExact()
    {
        // From distance 10 the frame is coded at half size: a channel subsampled once keeps its samples.
        using Raster<Rgb8> image = Photo();
        using Raster<GrayF32> mask = Ramp((Width + 1) / 2, (Height + 1) / 2, 3);
        JxlExtraChannel channel = new() { Kind = JxlExtraChannelKind.SelectionMask, BitsPerSample = 8, Subsampling = 1 };
        byte[] file = Save(image, new JxlWriterOptions { Lossless = false, Distance = 12, ExtraChannels = [new JxlExtraChannelImage(channel, mask)] });
        using Raster<GrayF32> back = JxlImage.ReadExtraChannel(new MemoryStream(file), 0);
        AssertSamples(Quantized(mask, 8), back, 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChannelsDeeperThanTheColorAreExact(bool lossless)
    {
        // Found by the jxl-frames fuzz target: a 10-bit channel beside 8-bit color, whose samples a palette codes. The palette's
        // implicit colors depend on the bit depth, which a decoder takes from the image header, not from the channel.
        byte[] bytes =
        [
            0x64, 0x64, 0x29, 0x0B, 0xEF, 0x10, 0xFF, 0x8B, 0xF8, 0x00, 0x67, 0x3B, 0x45, 0x3F, 0xD4, 0x06, 0xFF, 0xEF, 0x10, 0xFF, 0xD8,
            0xFF, 0xF9, 0x05, 0xFF, 0xFF, 0x05, 0x00, 0x00, 0x00, 0x82, 0x8B, 0xF8, 0x00, 0x67, 0xC4, 0x45, 0x37, 0xD4, 0x06, 0xFF, 0x61,
        ];
        const int Side = 21;
        using Raster<Rgba8> image = Raster.Create<Rgba8>(Side, Side);
        using Raster<GrayF32> depth = Raster.Create<GrayF32>(Side, Side);
        for (int y = 0; y < Side; y++)
        {
            Span<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Row(y));
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = bytes[((y * row.Length) + i) % bytes.Length];
            }

            for (int x = 0; x < Side; x++)
            {
                int v = bytes[((y * Side) + x) % bytes.Length] | (bytes[((y * Side) + x + 1) % bytes.Length] << 8);
                depth.Row(y)[x] = new GrayF32((v % 1024) / 1023f);
            }
        }

        JxlExtraChannel channel = new() { Kind = JxlExtraChannelKind.Depth, BitsPerSample = 10 };
        byte[] file = Save(image, new JxlWriterOptions { Lossless = lossless, Distance = lossless ? null : 2.5f, ExtraChannels = [new JxlExtraChannelImage(channel, depth)] });
        using Raster<GrayF32> back = JxlImage.ReadExtraChannel(new MemoryStream(file), 1);
        AssertSamples(depth, back, 0);
    }

    [Theory]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 5, false)]
    [InlineData(false, 5, false)]
    [InlineData(false, 7, false)]
    [InlineData(false, 5, true)]
    public void ChannelsSubsampledThreeTimesGoInTheLfGroups(bool lossless, int effort, bool progressive)
    {
        // A channel subsampled 3 times both ways is coded in the LF groups' streams (H.4), here on a frame of 18 groups, which
        // lossless effort 2 codes group by group; text-like edges give lossy effort 7 patches, and progressive an LF frame.
        const int Width = 1300, Height = 700;
        using Raster<Rgba8> image = Raster.Create<Rgba8>(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte ink = (x / 6 % 5 == 0 && y / 9 % 3 == 0) ? (byte)0 : (byte)230;
                image.Row(y)[x] = new Rgba8(ink, (byte)(ink ^ (x >> 2)), (byte)(y >> 2), (byte)(255 - (x % 5)));
            }
        }

        using Raster<GrayF32> depth = Quantized(Ramp((Width + 7) / 8, (Height + 7) / 8, 5), 8);
        JxlExtraChannel channel = new() { Kind = JxlExtraChannelKind.Depth, Subsampling = 3 };
        JxlWriterOptions options = new() { Lossless = lossless, Effort = effort, Progressive = progressive, ExtraChannels = [new JxlExtraChannelImage(channel, depth)] };
        byte[] file = Save(image, options);
        using Raster<GrayF32> back = JxlImage.ReadExtraChannel(new MemoryStream(file), 1);
        AssertSamples(depth, back, 3);
        if (lossless)
        {
            using Raster<Rgba8> loaded = Raster.Load<Rgba8>(new MemoryStream(file));
            Assert.Equal(image.Row(650)[1250], loaded.Row(650)[1250]);
        }
    }

    [Fact]
    public void LosslessCmykKeepsItsInks()
    {
        using Raster<Cmyk8> source = Raster.Create<Cmyk8>(64, 48);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.Row(y)[x] = new Cmyk8((byte)(x * 3), (byte)(y * 4), 40, (byte)(x < 32 ? 0 : 200));
            }
        }

        source.Metadata.IccProfile = ColorProfile.Fogra39.ToIcc();
        using Raster loaded = Raster.Load(new MemoryStream(Save(source, new JxlWriterOptions { Lossless = true })));
        Assert.Equal(Cmyk8.Layout, loaded.Layout);
        Raster<Cmyk8> back = (Raster<Cmyk8>)loaded;
        for (int y = 0; y < source.Height; y++)
        {
            Assert.True(source.Row(y).SequenceEqual(back.Row(y)), $"Row {y} differs.");
        }
    }

    [Fact]
    public void WrongSizesAndAnimationsAreRefused()
    {
        using Raster<Rgb8> image = Photo();
        using Raster<GrayF32> small = Raster.Create<GrayF32>(10, 10);
        JxlExtraChannel depth = new() { Kind = JxlExtraChannelKind.Depth };
        Assert.Throws<ArgumentException>(() => Save(image, new JxlWriterOptions { Lossless = true, ExtraChannels = [new JxlExtraChannelImage(depth, small)] }));
        Assert.Throws<ArgumentException>(() => new JxlExtraChannelImage(new JxlExtraChannel { Kind = JxlExtraChannelKind.SpotColor }, small));
        Assert.Throws<ArgumentException>(() => new JxlExtraChannelImage(new JxlExtraChannel { BitsPerSample = 32 }, small));
        Assert.Throws<ArgumentException>(() => new JxlExtraChannelImage(new JxlExtraChannel { Subsampling = 4 }, small));

        using Raster<Rgba8> animation = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "gif", "pan-global.gif"));
        using Raster<GrayF32> full = Raster.Create<GrayF32>(animation.Width, animation.Height);
        Assert.Throws<NotSupportedException>(() => Save(animation, new JxlWriterOptions { Lossless = true, ExtraChannels = [new JxlExtraChannelImage(depth, full)] }));
    }

    /// <summary>Depth (16 bits), a selection mask (1 bit, subsampled once), thermal data (half float, subsampled twice) and a spot color.</summary>
    private static List<(JxlExtraChannel, Raster<GrayF32>)> Extras()
    {
        int w2 = (Width + 1) / 2, h2 = (Height + 1) / 2, w4 = (Width + 3) / 4, h4 = (Height + 3) / 4;
        return
        [
            (new JxlExtraChannel { Kind = JxlExtraChannelKind.Depth, BitsPerSample = 16, Name = "depth" }, Quantized(Ramp(Width, Height, 0), 16)),
            (new JxlExtraChannel { Kind = JxlExtraChannelKind.SelectionMask, BitsPerSample = 1, Subsampling = 1, Name = "selection" }, Quantized(Ramp(w2, h2, 1), 1)),
            (new JxlExtraChannel { Kind = JxlExtraChannelKind.Thermal, BitsPerSample = 16, ExponentBits = 5, Subsampling = 2 }, Halves(Ramp(w4, h4, 2))),
            (new JxlExtraChannel { Kind = JxlExtraChannelKind.SpotColor, BitsPerSample = 8, Name = "varnish", SpotColor = [0.9f, 0.1f, 0.2f, 1f] }, Quantized(Ramp(Width, Height, 3), 8)),
        ];
    }

    private static Raster<Rgb8> Photo()
    {
        Raster<Rgb8> r = Raster.Create<Rgb8>(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                r.Row(y)[x] = new Rgb8((byte)((x * 7) + y), (byte)(y * 5), (byte)((x ^ y) * 9));
            }
        }

        return r;
    }

    /// <summary>Makes a plane of values in 0 to 1 with some structure.</summary>
    private static Raster<GrayF32> Ramp(int width, int height, int seed)
    {
        Raster<GrayF32> r = Raster.Create<GrayF32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                r.Row(y)[x] = new GrayF32((((x * (seed + 3)) + (y * 7) + (seed * 11)) % 97) / 96f);
            }
        }

        return r;
    }

    /// <summary>Rounds each sample to what <paramref name="bits"/> bits hold.</summary>
    private static Raster<GrayF32> Quantized(Raster<GrayF32> r, int bits)
    {
        float max = (1 << bits) - 1;
        for (int y = 0; y < r.Height; y++)
        {
            Span<GrayF32> row = r.Row(y);
            for (int x = 0; x < r.Width; x++)
            {
                row[x] = new GrayF32(MathF.Round(row[x].L * max) / max);
            }
        }

        return r;
    }

    private static Raster<GrayF32> Halves(Raster<GrayF32> r)
    {
        for (int y = 0; y < r.Height; y++)
        {
            Span<GrayF32> row = r.Row(y);
            for (int x = 0; x < r.Width; x++)
            {
                row[x] = new GrayF32((float)(Half)row[x].L);
            }
        }

        return r;
    }

    /// <summary>
    /// Checks the image-size channel read back against the samples; a subsampled channel through the decoder's upsampling
    /// (the image header's default 5×5 kernels), so equal values mean the stored samples are the ones given.
    /// </summary>
    private static void AssertSamples(Raster<GrayF32> expected, Raster<GrayF32> actual, int shift)
    {
        int w = expected.Width, h = expected.Height;
        float[] plane = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                plane[(y * w) + x] = expected.Row(y)[x].L;
            }
        }

        int stride = w;
        if (shift > 0)
        {
            plane = JxlFilters.Upsample(plane, w, h, w, 1 << shift, new JxlImageHeader(), new JxlParallel(1, CancellationToken.None));
            stride = w << shift;
        }

        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                float e = plane[(y * stride) + x], a = actual.Row(y)[x].L;
                Assert.True(Math.Abs(e - a) < 1e-5, $"({x}, {y}): {a}, expected {e}.");
            }
        }
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, JxlWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, options);
        return file.ToArray();
    }
}
