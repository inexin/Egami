using Egami.Formats.Extended.Raw;
using Egami.Formats.Jpeg;
using Egami.Formats.Tiff;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>The RAW reader on DNG files built in code: detection, decoding paths, the develop maths and the outputs.</summary>
public class RawReaderTests
{
    private static readonly double[] Curve = [0, 0, 0.1525, 0.3, 1, 1];

    /// <summary>XYZ (D65) to linear Display P3.</summary>
    private static readonly double[] P3Camera = [2.4934969, -0.9313836, -0.4027108, -0.8294890, 1.7626641, 0.0236247, 0.0358458, -0.0761724, 0.9568845];

    private static EgamiContext Context => TestImages.Context;

    /// <summary>The 16-bit sRGB value of a linear one, as the develop encodes it.</summary>
    private static int Srgb(double linear)
    {
        double v = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
        return (int)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    }

    private static DngFile Flat(int value, int size = 16) => new DngFile { Width = size, Height = size }.Fill((_, _, _) => value);

    private static void Near(int expected, int actual, int tolerance = 40)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} ± {tolerance}, got {actual}.");
    }

    [Fact]
    public void DetectsDngAheadOfTiff()
    {
        using Raster<Rgb8> thumbnail = Raster.Create<Rgb8>(4, 4);
        using MemoryStream jpeg = new();
        thumbnail.Save(jpeg, JpegFormat.Info);
        DngFile dng = Flat(1000);
        dng.Preview = jpeg.ToArray();
        dng.PreviewSize = (4, 4);
        byte[] file = dng.Build();
        RasterInfo info = Raster.Probe(file, Context);
        Assert.Equal("RAW", info.Format.Name);
        Assert.Equal(Rgb16.Layout, info.Layout);
        Assert.Equal((16, 16), (info.Width, info.Height));

        // Without RAW, the file is a TIFF; with only RAW's own matcher, a plain TIFF is not RAW.
        Assert.Equal(TiffFormat.Info.Name, Raster.Probe(file, new EgamiContext()).Format.Name);
        using Raster<Rgba8> plain = Raster.Create<Rgba8>(4, 4);
        using MemoryStream tiff = new();
        plain.Save(tiff, TiffFormat.Info);
        Assert.False(RawFormat.Info.Matches(tiff.ToArray()));
        Assert.Equal(TiffFormat.Info.Name, Raster.Probe(tiff.ToArray(), Context).Format.Name);
    }

    public static TheoryData<int, int, int, bool, int> Layouts => new()
    {
        { 16, 1, 0, false, 1 },
        { 16, 1, 0, true, 1 },
        { 12, 1, 0, false, 1 },
        { 14, 1, 0, true, 1 },
        { 10, 1, 8, false, 1 },
        { 8, 1, 0, false, 1 },
        { 16, 7, 0, false, 1 },
        { 12, 7, 8, false, 1 },
        { 14, 7, 0, true, 1 },
        { 16, 8, 0, false, 1 },
        { 16, 8, 8, true, 2 },
        { 12, 8, 0, false, 1 },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void MosaicIsTheStoredValues(int bits, int compression, int tile, bool bigEndian, int predictor)
    {
        int max = (1 << bits) - 1;
        DngFile dng = new DngFile { Width = 20, Height = 12, Bits = bits, Compression = compression, Tile = tile, BigEndian = bigEndian, Predictor = predictor, White = max, RowsPerStrip = tile == 0 ? 5 : 0 }
            .Fill((x, y, _) => ((x * 7919) + (y * 104729) + (x * y * 31)) % (max + 1));
        byte[] file = dng.Build();
        RawReaderOptions options = new() { Output = RawOutput.Mosaic };
        RasterInfo info = Raster.Probe(file, Context, options);
        Assert.Equal(Gray16.Layout, info.Layout);
        using Raster<Gray16> mosaic = Assert.IsType<Raster<Gray16>>(Raster.Load(file, Context, options));
        for (int y = 0; y < dng.Height; y++)
        {
            for (int x = 0; x < dng.Width; x++)
            {
                Assert.Equal(dng.Samples[(y * dng.Width) + x], mosaic.Row(y)[x].L);
            }
        }

        RawDetails details = Assert.IsType<RawDetails>(mosaic.Metadata.FormatDetails);
        Assert.Equal(RawOutput.Mosaic, details.Output);
        Assert.Equal("RGGB", details.CfaPattern);
        Assert.Equal(bits, details.BitsPerSample);
        Assert.Equal(compression, details.Compression);
        Assert.Equal(new Version(1, 4, 0, 0), details.DngVersion);
        Assert.Empty(mosaic.Metadata.Warnings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void LosslessJpegPredictorsAndRestarts(int predictor)
    {
        ushort[] samples = new ushort[24 * 6];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)((i * 2654435761u) >> 20);
        }

        byte[] jpeg = LosslessJpegEncoder.Encode(samples, 24, 6, 1, 12, predictor, restart: 12);
        Formats.Extended.Raw.LosslessJpeg decoder = Formats.Extended.Raw.LosslessJpeg.Open(jpeg, 0, jpeg.Length);
        ushort[] output = new ushort[decoder.SampleCount];
        Assert.True(decoder.Decode(output));
        Assert.Equal(samples, output);
    }

    [Theory]
    [InlineData(RawDemosaicMethod.Bilinear)]
    [InlineData(RawDemosaicMethod.Malvar)]
    [InlineData(RawDemosaicMethod.Ahd)]
    public void FlatGrayDevelopsToItsValue(RawDemosaicMethod method)
    {
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(Flat(20000).Build(), Context, new RawReaderOptions { Demosaic = method });
        int expected = Srgb(20000 / 65535.0);
        foreach (Rgb16 p in new[] { raster.Row(0)[0], raster.Row(8)[8], raster.Row(15)[15] })
        {
            Near(expected, p.R);
            Near(expected, p.G);
            Near(expected, p.B);
        }

        Assert.Equal(ColorSpaceHint.Srgb, raster.Metadata.ColorSpace);
    }

    [Theory]
    [InlineData(RawDemosaicMethod.Bilinear)]
    [InlineData(RawDemosaicMethod.Malvar)]
    [InlineData(RawDemosaicMethod.Ahd)]
    public void FlatColourDevelopsToItsValueInside(RawDemosaicMethod method)
    {
        // Camera RGB is linear sRGB, so a patch of (0.5, 0.25, 0.1) comes out as those values, sRGB-encoded.
        double[] colour = [0.5, 0.25, 0.1];
        DngFile dng = new DngFile { Width = 24, Height = 24 }.Fill((x, y, _) => (int)(colour[((y & 1) << 1 | (x & 1)) switch { 0 => 0, 3 => 2, _ => 1 }] * 65535));
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { Demosaic = method, ColorNoiseReduction = 0 });
        Rgb16 p = raster.Row(12)[12];
        Near(Srgb(0.5), p.R);
        Near(Srgb(0.25), p.G);
        Near(Srgb(0.1), p.B);
    }

    [Fact]
    public void HalfSizeAveragesBlocks()
    {
        byte[] file = Flat(30000).Build();
        RawReaderOptions options = new() { Output = RawOutput.HalfSize };
        RasterInfo info = Raster.Probe(file, Context, options);
        Assert.Equal((8, 8), (info.Width, info.Height));
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(file, Context, options);
        Assert.Equal((8, 8), (raster.Width, raster.Height));
        Near(Srgb(30000 / 65535.0), raster.Row(3)[3].G);
        Assert.Equal(RawOutput.HalfSize, Assert.IsType<RawDetails>(raster.Metadata.FormatDetails).Output);
    }

    [Fact]
    public void BlackWhiteAndLinearization()
    {
        // Stored 1000 is black, 5000 white; a table doubles stored values first.
        DngFile dng = Flat(2000);
        dng.Black = [1000];
        dng.White = 5000;
        dng.Linearization = [.. Enumerable.Range(0, 4096).Select(i => (ushort)Math.Min(65535, i * 2))];
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context);
        Near(Srgb((4000 - 1000) / 4000.0), raster.Row(5)[5].G);

        // Black per CFA position: a 2 × 2 repeat with a higher black on blue.
        DngFile repeat = Flat(3000);
        repeat.BlackRepeat = [2, 2];
        repeat.Black = [1000, 1000, 1000, 2000];
        using Raster<Rgb16> blue = Raster.Load<Rgb16>(repeat.Build(), Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        Rgb16 p = blue.Row(6)[6];
        Near(Srgb(2000 / 64535.0), p.R);
        Near(Srgb(1000 / 63535.0), p.B);
    }

    [Fact]
    public void ActiveAreaAndCrop()
    {
        DngFile dng = Flat(10000, 20);
        dng.ActiveArea = [2, 2, 18, 20];
        dng.Crop = [2, 2, 12, 10];
        byte[] file = dng.Build();
        RasterInfo info = Raster.Probe(file, Context);
        Assert.Equal((12, 10), (info.Width, info.Height));
        using Raster raster = Raster.Load(file, Context);
        RawDetails details = Assert.IsType<RawDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(new Rect(2, 2, 18, 16), details.ActiveArea);
        Assert.Equal(new Rect(4, 4, 12, 10), details.CropArea);
        Assert.Equal(20, details.SensorWidth);
        Assert.Equal((20, 20), (Raster.Probe(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic }).Width, 20));
    }

    [Fact]
    public void WhiteBalanceFromNeutral()
    {
        // The camera saw gray as (0.5, 1, 0.8): those multipliers make a patch with those values gray.
        DngFile dng = new DngFile { Neutral = [0.5, 1, 0.8] }.Fill((x, y, _) => ((y & 1) << 1 | (x & 1)) switch { 0 => 10000, 3 => 16000, _ => 20000 });
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context);
        Rgb16 p = raster.Row(8)[8];
        Near(p.G, p.R, 60);
        Near(p.G, p.B, 60);
        RawDetails details = Assert.IsType<RawDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(2, details.WhiteBalance[0], 3);
        Assert.Equal(1, details.WhiteBalance[1], 3);
        Assert.Equal(1.25, details.WhiteBalance[2], 3);
        Assert.Equal([0.5, 1, 0.8], details.AsShotNeutral.Select(v => Math.Round(v, 3)));

        // Auto finds the same gray; Custom at the camera's own white (D65) gives gray for a neutral camera.
        using Raster<Rgb16> auto = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { WhiteBalance = RawWhiteBalance.Auto });
        Near(auto.Row(8)[8].G, auto.Row(8)[8].R, 60);
        Near(auto.Row(8)[8].G, auto.Row(8)[8].B, 60);
        (double t, double tint) = RawColor.Temperature((0.31271, 0.32902));
        using Raster<Rgb16> custom = Raster.Load<Rgb16>(Flat(20000).Build(), Context, new RawReaderOptions { WhiteBalance = RawWhiteBalance.Custom, Temperature = t, Tint = tint });
        Near(custom.Row(8)[8].G, custom.Row(8)[8].R, 100);
        Near(custom.Row(8)[8].G, custom.Row(8)[8].B, 100);
        using Raster<Rgb16> warm = Raster.Load<Rgb16>(Flat(20000).Build(), Context, new RawReaderOptions { WhiteBalance = RawWhiteBalance.Custom, Temperature = 3000 });
        Assert.True(warm.Row(8)[8].B > warm.Row(8)[8].R, "Balancing for warm light makes a neutral camera's gray blue.");
    }

    [Theory]
    [InlineData(2850, 0)]
    [InlineData(5000, 10)]
    [InlineData(6504, -20)]
    [InlineData(10000, 5)]
    public void TemperatureAndTintRoundTrip(double temperature, double tint)
    {
        (double x, double y) = RawColor.WhiteOf(temperature, tint);
        (double t, double n) = RawColor.Temperature((x, y));
        Assert.Equal(temperature, t, temperature * 0.002);
        Assert.Equal(tint, n, 0.5);
    }

    [Fact]
    public void D65IsNearItsTemperature()
    {
        (double t, double tint) = RawColor.Temperature((0.31271, 0.32902));
        Assert.InRange(t, 6450, 6550);
        Assert.InRange(tint, -10, 10);
    }

    [Fact]
    public void ExposureAndBaselineExposure()
    {
        DngFile dng = Flat(10000);
        dng.BaselineExposure = 1;
        using Raster<Rgb16> baseline = Raster.Load<Rgb16>(dng.Build(), Context);
        Near(Srgb(20000 / 65535.0), baseline.Row(8)[8].G);
        using Raster<Rgb16> off = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { ApplyBaselineExposure = false, Exposure = -1 });
        Near(Srgb(5000 / 65535.0), off.Row(8)[8].G);
    }

    [Fact]
    public void OpcodeGainMapAndPolynomial()
    {
        DngFile dng = Flat(10000);
        dng.Opcodes2 = new OpcodeList()
            .Add(9, p => p.Area(0, 0, 16, 16).U32(2, 2).F64(1, 1, 0, 0).U32(1).F32(2, 2, 2, 2))
            .Build();
        using Raster<Rgb16> gain = Raster.Load<Rgb16>(dng.Build(), Context);
        Near(Srgb(20000 / 65535.0), gain.Row(8)[8].G);
        RawDetails details = Assert.IsType<RawDetails>(gain.Metadata.FormatDetails);
        Assert.Equal([(2, 9)], details.Opcodes);

        DngFile poly = Flat(10000);
        poly.Opcodes1 = new OpcodeList().Add(8, p => p.Area(0, 0, 16, 16).U32(1).F64(0, 3)).Build();
        using Raster<Rgb16> tripled = Raster.Load<Rgb16>(poly.Build(), Context);
        Near(Srgb(30000 / 65535.0), tripled.Row(8)[8].G);

        // An opcode Egami does not know is skipped with a warning.
        DngFile unknown = Flat(10000);
        unknown.Opcodes3 = new OpcodeList().Add(99, p => p.U32(0), flags: 1).Build();
        using Raster<Rgb16> skipped = Raster.Load<Rgb16>(unknown.Build(), Context);
        Assert.Contains(skipped.Metadata.Warnings, w => w.Id == "Raw.OpcodeSkipped");
    }

    [Fact]
    public void OpcodeVignetteAndWarp()
    {
        DngFile dng = Flat(10000);
        dng.Opcodes3 = new OpcodeList()
            .Add(3, p => p.F64(1, 0, 0, 0, 0, 0.5, 0.5))
            .Add(1, p => p.U32(1).F64(1, 0, 0, 0, 0, 0, 0.5, 0.5))
            .Build();
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context);

        // Gain 1 + r²: the corner (r = 1) doubles, the centre stays; the identity warp changes nothing.
        Near(Srgb(10000 / 65535.0), raster.Row(8)[8].G, 300);
        Near(Srgb(20000 / 65535.0 * 0.95), raster.Row(0)[0].G, 1500);
        using Raster<Rgb16> off = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { LensCorrections = false });
        Near(Srgb(10000 / 65535.0), off.Row(0)[0].G);
    }

    [Fact]
    public void OpcodeFixesBadPixels()
    {
        DngFile dng = Flat(10000);
        dng.Samples[(6 * 16) + 6] = 65535;
        dng.Opcodes1 = new OpcodeList().Add(5, p => p.U32(0, 1, 0).U32(6, 6)).Build();
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { Output = RawOutput.Developed });
        Near(Srgb(10000 / 65535.0), raster.Row(6)[6].R);
    }

    [Fact]
    public void PreviewAndMinimumSize()
    {
        using Raster<Rgb8> picture = Raster.Create<Rgb8>(8, 6);
        for (int y = 0; y < 6; y++)
        {
            picture.Row(y).Fill(new Rgb8(200, 100, 50));
        }
        using MemoryStream jpeg = new();
        picture.Save(jpeg, JpegFormat.Info);
        DngFile dng = Flat(10000);
        dng.Preview = jpeg.ToArray();
        dng.PreviewSize = (8, 6);
        byte[] file = dng.Build();

        RawReaderOptions preview = new() { Output = RawOutput.Preview };
        RasterInfo info = Raster.Probe(file, Context, preview);
        Assert.Equal((8, 6), (info.Width, info.Height));
        Assert.Equal(2, info.ImageCount);
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(file, Context, preview);
        Assert.InRange(loaded.Row(3)[4].R, 190, 210);
        RawDetails details = Assert.IsType<RawDetails>(loaded.Metadata.FormatDetails);
        Assert.Equal(RawOutput.Preview, details.Output);
        Assert.Equal([new RawPreview(8, 6, RawPreviewKind.Jpeg)], details.Previews);

        // MinimumSize takes the preview when it covers the size, else a half-size develop, else the full develop.
        Assert.Equal(8, Raster.Probe(file, Context, new ReaderOptions { MinimumSize = new Size(8, 6) }).Width);
        Assert.Equal(8, Raster.Probe(file, Context, new ReaderOptions { MinimumSize = new Size(8, 8) }).Width);
        Assert.Equal(16, Raster.Probe(file, Context, new ReaderOptions { MinimumSize = new Size(9, 9) }).Width);

        using RasterList all = Raster.LoadAll(file, Context);
        Assert.Equal(2, all.Count);
        Assert.Equal((16, 16), (all[0].Width, all[0].Height));
        Assert.Equal((8, 6), (all[1].Width, all[1].Height));

        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(file, Context, new RawReaderOptions { Output = RawOutput.Preview, PreviewIndex = 3 }));
    }

    [Fact]
    public void MetadataAndOrientation()
    {
        DngFile dng = Flat(10000);
        dng.Orientation = 6;
        byte[] file = dng.Build();
        Assert.Equal(ImageOrientation.Rotate90, Raster.Probe(file, Context).Orientation);
        using Raster raster = Raster.Load(file, Context);
        Assert.NotNull(raster.Metadata.Exif);
        Assert.Equal(ImageOrientation.Rotate90, raster.Metadata.Exif!.Orientation);
        RawDetails details = Assert.IsType<RawDetails>(raster.Metadata.FormatDetails);
        Assert.Equal("Egami", details.Make);
        Assert.Equal("Test Camera", details.Model);
        Assert.Equal("Egami Test Camera", details.UniqueCameraModel);
        Assert.Equal(9, details.CameraToXyz.Count);
        Assert.InRange(details.Temperature, 6400, 6600);
    }

    [Fact]
    public void LenientAndStrictOnTruncation()
    {
        byte[] whole = Flat(10000).Build();
        byte[] cut = whole[..(whole.Length - 100)];
        using Raster raster = Raster.Load(cut, Context);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Raw.DataDamaged");
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
        Assert.Throws<CorruptDataException>(() => Raster.Load(whole.AsMemory(0, 60), RawFormat.Info, Context));
    }

    [Fact]
    public void StreamingMatchesLoad()
    {
        DngFile dng = new DngFile { Width = 24, Height = 20 }.Fill((x, y, _) => (x * 1500) + (y * 900));
        byte[] file = dng.Build();
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(file, Context);
        using RasterDecoder decoder = RasterDecoder.Open(new MemoryStream(file), Context);
        using Raster<Rgba8> band = Raster.Create<Rgba8>(24, 7);
        int y = 0;
        while (decoder.ReadRows(band) is int rows and > 0)
        {
            for (int i = 0; i < rows; i++)
            {
                Assert.True(band.Row(i).SequenceEqual(loaded.Row(y + i)), $"Row {y + i} differs.");
            }

            y += rows;
        }

        Assert.Equal(20, y);
    }

    [Fact]
    public void ThreadCountDoesNotChangeTheResult()
    {
        DngFile dng = new DngFile { Width = 64, Height = 70, Tile = 16, Compression = 7 }.Fill((x, y, _) => ((x * 977) + (y * 1931) + (x * y * 13)) % 60000);
        byte[] file = dng.Build();
        RawReaderOptions options = new() { LuminanceNoiseReduction = 30, Highlights = RawHighlightMode.Rebuild };
        using Raster<Rgb16> many = Raster.Load<Rgb16>(file, Context, options);
        using Raster<Rgb16> one = Raster.Load<Rgb16>(file, Context with { MaxParallelism = 1 }, options);
        for (int y = 0; y < many.Height; y++)
        {
            Assert.True(many.Row(y).SequenceEqual(one.Row(y)), $"Row {y} differs.");
        }
    }

    [Fact]
    public void LinearRaw()
    {
        DngFile dng = new DngFile { LinearRaw = true }.Fill((_, _, s) => s switch { 0 => 30000, 1 => 15000, _ => 5000 });
        byte[] file = dng.Build();
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        Rgb16 p = raster.Row(4)[4];
        Near(Srgb(30000 / 65535.0), p.R);
        Near(Srgb(15000 / 65535.0), p.G);
        Near(Srgb(5000 / 65535.0), p.B);
        using Raster mosaic = Raster.Load(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic });
        Assert.Equal(Rgb16.Layout, mosaic.Layout);
        Assert.Null(Assert.IsType<RawDetails>(mosaic.Metadata.FormatDetails).CfaPattern);
    }

    [Theory]
    [InlineData(16, 1, 1)]
    [InlineData(32, 1, 1)]
    [InlineData(16, 8, 3)]
    [InlineData(32, 8, 3)]
    public void FloatingPointData(int bits, int compression, int predictor)
    {
        DngFile dng = new() { Bits = bits, Compression = compression, Predictor = predictor, White = 1 };
        dng.FloatSamples = [.. Enumerable.Range(0, 256).Select(i => (float)((i % 16) / 32.0))];
        byte[] file = dng.Build();
        using Raster<GrayF32> mosaic = Raster.Load<GrayF32>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic });
        Assert.Equal(GrayF32.Layout, Raster.Probe(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic }).Layout);
        for (int x = 0; x < 16; x++)
        {
            Assert.Equal(x / 32.0, mosaic.Row(3)[x].L, 3);
        }

        using Raster<Rgb16> developed = Raster.Load<Rgb16>(file, Context);
        Assert.Equal((16, 16), (developed.Width, developed.Height));
    }

    [Fact]
    public void LuminanceNoiseReductionLowersNoise()
    {
        Random random = new(7);
        DngFile dng = new DngFile { Width = 64, Height = 64 }.Fill((_, _, _) => Math.Clamp(20000 + (int)(random.NextDouble() * 6000) - 3000, 0, 65535));
        dng.NoiseProfile = [0, 3e-4];
        byte[] file = dng.Build();
        double Spread(Raster<Rgb16> r)
        {
            List<double> values = [];
            for (int y = 8; y < 56; y++)
            {
                for (int x = 8; x < 56; x++)
                {
                    values.Add(r.Row(y)[x].G);
                }
            }

            double mean = values.Average();
            return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        }

        using Raster<Rgb16> noisy = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        using Raster<Rgb16> clean = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0, LuminanceNoiseReduction = 100 });
        Assert.True(Spread(clean) < Spread(noisy) * 0.6, $"{Spread(clean)} vs {Spread(noisy)}");
    }

    [Fact]
    public void HighlightModes()
    {
        // Red clipped at the white level, green and blue not: a warm highlight under a neutral (1, 0.5, 1) balance.
        DngFile dng = new DngFile { Neutral = [1, 0.5, 1] }.Fill((x, y, _) => ((y & 1) << 1 | (x & 1)) switch { 0 => 65535, 3 => 30000, _ => 40000 });
        byte[] file = dng.Build();
        using Raster<Rgb16> clip = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { Highlights = RawHighlightMode.Clip, ColorNoiseReduction = 0 });
        using Raster<Rgb16> blend = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { Highlights = RawHighlightMode.Blend, ColorNoiseReduction = 0 });
        using Raster<Rgb16> rebuild = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { Highlights = RawHighlightMode.Rebuild, Exposure = -2, ColorNoiseReduction = 0 });
        Assert.True(blend.Row(8)[8].B >= clip.Row(8)[8].B);
        Assert.True(rebuild.Row(8)[8].R > 0);
    }

    [Fact]
    public void ProfileHueSatMapShiftsHue()
    {
        // A 2 × 2 × 1 map that turns every hue by 120 degrees: red becomes green.
        double[] map = [.. Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? 120.0 : 1.0)];
        DngFile dng = new DngFile { Width = 24, Height = 24 }.Fill((x, y, _) => ((y & 1) << 1 | (x & 1)) == 0 ? 30000 : 3000);
        dng.ExtraIfd0.Add(TiffTag.Long(50937, 2, 2, 1));
        dng.ExtraIfd0.Add(TiffTag.Float(50938, map));
        byte[] file = dng.Build();
        using Raster<Rgb16> shifted = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        using Raster<Rgb16> plain = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0, ApplyProfile = false });
        Assert.True(plain.Row(12)[12].R > plain.Row(12)[12].G);
        Assert.True(shifted.Row(12)[12].G > shifted.Row(12)[12].R);
    }

    [Fact]
    public void ProfileToneCurve()
    {
        DngFile dng = Flat(10000);
        dng.ExtraIfd0.Add(TiffTag.Float(50940, Curve));
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context);
        Assert.True(raster.Row(8)[8].G > Srgb(10000 / 65535.0) + 2000);
    }

    [Fact]
    public void DefaultToneCurveIsOptInAndYieldsToTheProfiles()
    {
        // 18 % gray: scene-linear by default; the default curve lifts it (to about 38 % linear); a profile's own curve wins.
        DngFile gray = Flat(11796);
        byte[] file = gray.Build();
        using Raster<Rgb16> linear = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        using Raster<Rgb16> curved = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0, DefaultToneCurve = true });
        Near(Srgb(0.18), linear.Row(8)[8].G, 300);
        Near(Srgb(0.38), curved.Row(8)[8].G, 1500);

        gray.ExtraIfd0.Add(TiffTag.Float(50940, Curve));
        byte[] withProfile = gray.Build();
        using Raster<Rgb16> profile = Raster.Load<Rgb16>(withProfile, Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        using Raster<Rgb16> both = Raster.Load<Rgb16>(withProfile, Context, new RawReaderOptions { ColorNoiseReduction = 0, DefaultToneCurve = true });
        Assert.Equal(profile.Row(8)[8], both.Row(8)[8]);
    }

    [Fact]
    public void RendersIntoConvertColorTo()
    {
        // A camera whose RGB is linear Display P3 sees P3's green, beyond sRGB: rendered into P3 it keeps its colour, through
        // sRGB it is clipped and comes back paler.
        DngFile dng = new DngFile { Width = 24, Height = 24, ColorMatrix = P3Camera }.Fill((x, y, _) => ((y & 1) << 1 | (x & 1)) is 1 or 2 ? 40000 : 200);
        byte[] file = dng.Build();
        ReaderOptions p3 = new RawReaderOptions { ConvertColorTo = Colors.ColorProfile.DisplayP3, ColorNoiseReduction = 0 };
        using Raster<Rgb16> direct = Raster.Load<Rgb16>(file, Context, p3);
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, direct.Metadata.ColorSpace);
        Assert.True(direct.Metadata.GetColorProfile().IsEquivalentTo(Colors.ColorProfile.DisplayP3));
        using Raster<Rgb16> viaSrgb = Raster.Load<Rgb16>(file, Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        using Raster<Rgb16> converted = viaSrgb.ConvertColor<Rgb16>(Colors.ColorProfile.DisplayP3);
        Assert.True(direct.Row(12)[12].R < converted.Row(12)[12].R, "Rendered straight into P3, the green is not clipped to sRGB first.");

        // Natural loads, streams and other models too.
        using Raster natural = Raster.Load(file, Context, p3);
        Assert.Equal(Rgb16.Layout, natural.Layout);
        using Raster cmyk = Raster.Load(file, Context, new ReaderOptions { ConvertColorTo = Colors.ColorProfile.Fogra39 });
        Assert.Equal(Cmyk16.Layout, cmyk.Layout);
        using RasterDecoder decoder = RasterDecoder.Open(new MemoryStream(file), Context, p3);
        using Raster<Rgb16> band = Raster.Create<Rgb16>(24, 24);
        decoder.ReadRows(band);
        Assert.True(band.Row(12).SequenceEqual(direct.Row(12)));
    }

    [Fact]
    public void FindsAPreviewBeyondTheFirstRead()
    {
        // The preview's bytes come after 128 KB of sensor data, past what Probe reads of the file.
        using Raster<Rgb8> picture = Raster.Create<Rgb8>(64, 48);
        using MemoryStream jpeg = new();
        picture.Save(jpeg, JpegFormat.Info);
        DngFile dng = Flat(10000, 256);
        dng.Preview = jpeg.ToArray();
        dng.PreviewSize = (64, 48);
        byte[] file = dng.Build();
        Assert.True(file.AsSpan().IndexOf(dng.Preview) > 128 * 1024);
        RasterInfo info = Raster.Probe(new MemoryStream(file), Context, new RawReaderOptions { Output = RawOutput.Preview });
        Assert.Equal((64, 48), (info.Width, info.Height));
        Assert.Equal(2, info.ImageCount);
        using Raster preview = Raster.Load(file, Context, new ReaderOptions { MinimumSize = new Size(64, 48) });
        Assert.Equal(RawOutput.Preview, Assert.IsType<RawDetails>(preview.Metadata.FormatDetails).Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlackDeltasOverTheActiveAreaOrTheImage(bool overActive)
    {
        // Columns of the active area get 0, 100, 200, … added to the black level; a sample of 4000 + that delta is 4000 above black.
        DngFile dng = new DngFile { Width = 20, Height = 16, ActiveArea = [0, 4, 16, 20] }.Fill((x, _, _) => 4000 + (Math.Max(0, x - 4) * 100));
        int columns = overActive ? 16 : 20;
        dng.ExtraRaw.Add(TiffTag.SRational(50715, [.. Enumerable.Range(0, columns).Select(i => (double)(overActive ? i * 100 : Math.Max(0, i - 4) * 100))]));
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { ColorNoiseReduction = 0 });
        for (int x = 2; x < 14; x++)
        {
            Near(Srgb(4000 / (65535.0 - (x * 100))), raster.Row(8)[x].G, 120);
        }

        Assert.Equal(16, Assert.IsType<RawDetails>(raster.Metadata.FormatDetails).ActiveArea.Width);
    }

    [Fact]
    public void HalfSizeOfAnXTransPattern()
    {
        // X-Trans's 6 × 6 pattern has 2 × 2 blocks without red or blue; each colour still comes from its nearest samples.
        byte[] xtrans = [1, 1, 0, 1, 1, 2, 1, 1, 2, 1, 1, 0, 2, 0, 1, 0, 2, 1, 1, 1, 2, 1, 1, 0, 1, 1, 0, 1, 1, 2, 0, 2, 1, 2, 0, 1];
        double[] colour = [0.5, 0.25, 0.1];
        DngFile dng = new DngFile { Width = 24, Height = 24, Cfa = xtrans }.Fill((x, y, _) => (int)(colour[xtrans[((y % 6) * 6) + (x % 6)]] * 65535));
        using Raster<Rgb16> half = Raster.Load<Rgb16>(dng.Build(), Context, new RawReaderOptions { Output = RawOutput.HalfSize, ColorNoiseReduction = 0 });
        Assert.Equal((12, 12), (half.Width, half.Height));
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                Rgb16 p = half.Row(y)[x];
                Near(Srgb(0.5), p.R);
                Near(Srgb(0.25), p.G);
                Near(Srgb(0.1), p.B);
            }
        }
    }

    [Fact]
    public void StreamingConvertsColourOnce()
    {
        using Raster<Rgb8> picture = Raster.Create<Rgb8>(8, 6);
        for (int y = 0; y < 6; y++)
        {
            picture.Row(y).Fill(new Rgb8(40, 180, 90));
        }

        using MemoryStream jpeg = new();
        picture.Save(jpeg, JpegFormat.Info);
        DngFile dng = new DngFile { Width = 16, Height = 16 }.Fill((x, y, _) => ((y & 1) << 1 | (x & 1)) == 0 ? 30000 : 9000);
        dng.Preview = jpeg.ToArray();
        dng.PreviewSize = (8, 6);
        byte[] file = dng.Build();
        foreach (ReaderOptions options in new ReaderOptions[]
        {
            new RawReaderOptions { Output = RawOutput.Preview, ConvertColorTo = Colors.ColorProfile.DisplayP3 },
            new RawReaderOptions { ConvertColorTo = Colors.ColorProfile.Fogra39, ColorNoiseReduction = 0 },
            new RawReaderOptions { ConvertColorTo = Colors.ColorProfile.GraySrgb, ColorNoiseReduction = 0 },
        })
        {
            using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file, Context, options);
            using RasterDecoder decoder = RasterDecoder.Open(new MemoryStream(file), Context, options);
            using Raster<Rgba16> band = Raster.Create<Rgba16>(loaded.Width, loaded.Height);
            Assert.Equal(loaded.Height, decoder.ReadRows(band));
            for (int y = 0; y < loaded.Height; y++)
            {
                for (int x = 0; x < loaded.Width; x++)
                {
                    Rgba16 a = loaded.Row(y)[x], b = band.Row(y)[x];
                    Assert.True(Math.Abs(a.R - b.R) <= 300 && Math.Abs(a.G - b.G) <= 300 && Math.Abs(a.B - b.B) <= 300, $"{options.ConvertColorTo!.Name} pixel {x},{y}: Load {a}, streamed {b}.");
                }
            }
        }
    }

    [Fact]
    public void OptionsValidate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawReaderOptions { Temperature = 1000 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawReaderOptions { Tint = 200 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawReaderOptions { Exposure = 11 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawReaderOptions { LuminanceNoiseReduction = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawReaderOptions { ColorNoiseReduction = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawReaderOptions { PreviewIndex = -1 });
        Assert.Equal(25, new RawReaderOptions().ColorNoiseReduction);
        Assert.Equal(RawDemosaicMethod.Ahd, new RawReaderOptions().Demosaic);
    }
}

/// <summary>
/// RAW Build Plan task 4: a develop's managed allocations must not grow with the image. The file's bytes, the mosaic, the
/// RGB plane and the band buffers come from pools; only fixed-size objects reach the GC (EGAMI_ALLOCATION_TESTS=1).
/// </summary>
public class RawAllocationTests
{
    [Trait("Category", "Allocation")]
    [Theory]
    [InlineData(RawOutput.Developed, RawDemosaicMethod.Ahd)]
    [InlineData(RawOutput.Developed, RawDemosaicMethod.Malvar)]
    [InlineData(RawOutput.HalfSize, RawDemosaicMethod.Ahd)]
    [InlineData(RawOutput.Mosaic, RawDemosaicMethod.Ahd)]
    public void DevelopAllocatesTheSameForFewRowsAndMany(RawOutput output, RawDemosaicMethod method)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        EgamiContext context = TestImages.Context with { MaxParallelism = 1 };
        byte[] small = new DngFile { Width = 256, Height = 32, Compression = 7, Tile = 64 }.Fill((x, y, _) => (x * 97) + (y * 31)).Build();
        byte[] large = new DngFile { Width = 256, Height = 1024, Compression = 7, Tile = 64 }.Fill((x, y, _) => (x * 97) + (y * 31)).Build();
        RawReaderOptions options = new() { Output = output, Demosaic = method };
        long few = Fewest(() => Raster.Load(small, context, options).Dispose());
        long many = Fewest(() => Raster.Load(large, context, options).Dispose());
        Assert.True(many - few < 16 * 1024, $"1,024 rows allocated {many:N0} bytes against {few:N0} for 32.");
    }

    private static long Fewest(Action action)
    {
        for (int i = 0; i < 3; i++)
        {
            action();
        }

        long fewest = long.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }
}

/// <summary>RAW Build Plan task 6: the camera colour table, generated from Adobe DNG Converter output.</summary>
public class RawCameraTableTests
{
    [Fact]
    public void ParsesLinesAndFindsByMakeAndModel()
    {
        string text = "# comment\nCanon\tCanon EOS Test\tCanon EOS Test\t17\t1 0 0 0 1 0 0 0 1\t21\t2 0 0 0 2 0 0 0 2\t-\t-\t0.25"
            + "\t5280 3528\t52 72 3528 5280\t12 12 5184 3456\t2 2\t2047 2047 2048 2047\t12277\nbroken line\n";
        Dictionary<string, RawCameraEntry> cameras = RawCameraTable.Parse(text);
        RawCameraEntry camera = Assert.Single(cameras).Value;
        RawProfile profile = camera.Profile;
        Assert.Equal((5280, 3528), (camera.Width, camera.Height));
        Assert.Equal(new Rect(72, 52, 5208, 3476), camera.ActiveArea);
        Assert.Equal(new Rect(12, 12, 5184, 3456), camera.DefaultCrop);
        Assert.Equal((2, 2), camera.BlackRepeat);
        Assert.Equal(2048, camera.Black[2]);
        Assert.Equal(12277, camera.White);
        Assert.Equal(17, profile.Illuminant1);
        Assert.Equal(21, profile.Illuminant2);
        Assert.Equal(2, profile.ColorMatrix2![0]);
        Assert.Null(profile.ForwardMatrix1);
        Assert.Equal(0.25, profile.BaselineExposure);
        Assert.Equal("Canon EOS Test", profile.Name);
    }

    [Fact]
    public void TheEmbeddedTableHasCameras()
    {
        Assert.True(RawCameraTable.Count > 0);
        RawCameraEntry? camera = RawCameraTable.Find(" canon ", "CANON EOS 1000D");
        Assert.NotNull(camera);
        Assert.Equal(9, camera.Profile.ColorMatrix1!.Length);
        Assert.True(camera.Width > 0);
        Assert.Null(RawCameraTable.Find("Egami", "No Such Camera"));
    }

    [Fact]
    public void ADngWithoutColourDataUsesTheTable()
    {
        // The Canon 1000D's matrix gives a different colour transform from the sRGB fallback a camera without data gets.
        byte[] known = new DngFile { ColorMatrix = null, Make = "Canon", Model = "Canon EOS 1000D" }.Fill((_, _, _) => 20000).Build();
        byte[] unknown = new DngFile { ColorMatrix = null, Make = "Egami", Model = "No Such Camera" }.Fill((_, _, _) => 20000).Build();
        using Raster a = Raster.Load(known, TestImages.Context);
        using Raster b = Raster.Load(unknown, TestImages.Context);
        Assert.NotEqual(((RawDetails)a.Metadata.FormatDetails!).CameraToXyz, ((RawDetails)b.Metadata.FormatDetails!).CameraToXyz);
    }
}
