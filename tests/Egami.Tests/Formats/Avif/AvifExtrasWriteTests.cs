using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// AVIF extras writing (tasks 5 to 8): layered (progressive) images that load and preview layer by layer, 16-bit images through a
/// sample transform that load back exactly when lossless, the target size search, and the gain map's own quality.
/// </summary>
public class AvifExtrasWriteTests
{
    private static readonly string PhotoPath = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-q100.jpg");

    [Fact]
    public void AProgressiveImageHasTwoLayersAndItsBestIsTheImage()
    {
        using Raster<Rgb8> photo = Photo(160, 120);
        byte[] layered = AvifWriterTests.Save(photo, new AvifWriterOptions { Quality = 60, Progressive = true });
        byte[] plain = AvifWriterTests.Save(photo, new AvifWriterOptions { Quality = 60 });
        using Raster loaded = Raster.Load(layered);
        AvifDetails details = (AvifDetails)loaded.Metadata.FormatDetails!;
        Assert.True(details.IsProgressive);
        Assert.Equal([new Size(160, 120), new Size(160, 120)], details.Layers);

        // The first layer, at quality 2, is small; the best is about the single-layer image.
        HeifFile heif = HeifFile.Parse(layered, "AVIF");
        List<(int Offset, int Length)> ranges = AvifCodec.LayerRanges(heif, heif.Property(heif.PrimaryItem!, AvifProperties.A1lx)!, heif.ItemData(heif.PrimaryItem!).Length);
        Assert.Equal(2, ranges.Count);
        Assert.True(ranges[0].Length < ranges[1].Length / 2, $"layers of {ranges[0].Length} and {ranges[1].Length} bytes");
        using Raster<Rgb8> best = loaded.As<Rgb8>();
        using Raster<Rgb8> single = Raster.Load<Rgb8>(plain);
        Assert.True(Psnr(photo, best) > Psnr(photo, single) - 1.5, $"layered {Psnr(photo, best):F2} dB, single {Psnr(photo, single):F2} dB");
        using Raster<Rgb8> first = Raster.Load<Rgb8>(layered, options: new AvifReaderOptions { Layer = 0 });
        Assert.True(Psnr(photo, first) < Psnr(photo, best));

        // Each layer previews as it arrives.
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        using Raster<Rgb8> preview = Raster.Create<Rgb8>(160, 120);
        decoder.Append(layered.AsSpan(0, layered.Length - ranges[1].Length));
        Assert.True(decoder.RenderPreview(preview));
        Assert.True(AvifProgressiveTests.Same(first, preview));
    }

    [Fact]
    public void LayersHaveTheirOwnQualityAndScale()
    {
        using Raster<Rgb8> photo = Photo(160, 120);
        byte[] file = AvifWriterTests.Save(photo, new AvifWriterOptions
        {
            Layers = [new AvifLayer { Quality = 20, Scale = 0.5 }, new AvifLayer { Quality = 50 }, new AvifLayer { Quality = 80 }],
        });
        using Raster loaded = Raster.Load(file);
        Assert.Equal([new Size(80, 60), new Size(160, 120), new Size(160, 120)], ((AvifDetails)loaded.Metadata.FormatDetails!).Layers);
        double previous = 0;
        for (int layer = 0; layer < 3; layer++)
        {
            using Raster<Rgb8> image = Raster.Load<Rgb8>(file, options: new AvifReaderOptions { Layer = layer });
            Assert.Equal((160, 120), (image.Width, image.Height));
            double psnr = Psnr(photo, image);
            Assert.True(psnr > previous, $"layer {layer}: {psnr:F2} dB");
            previous = psnr;
        }
    }

    [Fact]
    public void ALosslessProgressiveImageLoadsBackExactly()
    {
        using Raster<Rgb8> rgb = Photo(96, 64);
        using Raster<Rgba8> photo = rgb.As<Rgba8>();
        byte[] file = AvifWriterTests.Save(photo, new AvifWriterOptions { Lossless = true, Progressive = true });
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(file);
        Assert.True(AvifProgressiveTests.Same(photo, loaded));
        Assert.Equal(2, ((AvifDetails)Raster.Load(file).Metadata.FormatDetails!).Layers.Count);
    }

    [Fact]
    public void LayersNeedTheLastAtFullSize()
    {
        Assert.Throws<ArgumentException>(() => new AvifWriterOptions { Layers = [new AvifLayer { Scale = 0.5 }] });
        Assert.Throws<ArgumentException>(() => new AvifWriterOptions { Layers = [] });
        Assert.Throws<ArgumentException>(() => new AvifWriterOptions { Layers = [new(), new(), new(), new(), new()] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifLayer { Scale = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifReaderOptions { Layer = -1 });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SixteenBitsLosslessLoadBackExactly(bool alpha, bool gray)
    {
        using Raster<Rgba16> source = AvifWriterTests.Pattern<Rgba16>(70, 45, (x, y) =>
        {
            ushort v = (ushort)((x * 911) + (y * 1777) ^ (x * y * 37));
            return gray ? new Rgba16(v, v, v, alpha ? (ushort)(x * 900) : ushort.MaxValue)
                : new Rgba16(v, (ushort)(v * 3), (ushort)(65535 - v), alpha ? (ushort)((x * 900) + y) : ushort.MaxValue);
        });
        using Raster input = gray ? (alpha ? source.As<GrayAlpha16>() : source.As<Gray16>()) : alpha ? source.Copy() : source.As<Rgb16>();
        byte[] file = AvifWriterTests.Save(input, new AvifWriterOptions { Lossless = true, BitDepth = 16 });
        using Raster loaded = Raster.Load(file);
        Assert.Equal(input.Layout, loaded.Layout);
        AvifDetails details = (AvifDetails)loaded.Metadata.FormatDetails!;
        Assert.True(details.HasSampleTransform);
        Assert.Equal(16, details.BitDepth);
        using Raster<Rgba16> expected = input.As<Rgba16>();
        using Raster<Rgba16> actual = loaded.As<Rgba16>();
        Assert.True(AvifProgressiveTests.Same(expected, actual));

        // Readers without sample transforms get the 8-bit high bytes.
        using Raster fallback = Raster.Load(file, options: new AvifReaderOptions { SampleTransform = false });
        Assert.Equal(8, ((AvifDetails)fallback.Metadata.FormatDetails!).BitDepth);
    }

    [Fact]
    public void SixteenBitsLossyBeatTwelveBits()
    {
        // Photo detail in the high bits and fine noise in the low ones, which 12 bits cannot hold.
        using Raster<Rgb8> photo = Photo(128, 96);
        using Raster<Rgb16> source = AvifWriterTests.Pattern<Rgb16>(128, 96, (x, y) =>
        {
            Rgb8 p = photo.Row(y)[x];
            int noise = ((x * 7919) + (y * 104729)) & 15;
            return new Rgb16((ushort)((p.R * 257) ^ noise), (ushort)((p.G * 257) ^ noise), (ushort)((p.B * 257) ^ noise));
        });
        byte[] wide = AvifWriterTests.Save(source, new AvifWriterOptions { Quality = 90, BitDepth = 16 });
        byte[] twelve = AvifWriterTests.Save(source, new AvifWriterOptions { Quality = 90, BitDepth = 12 });
        using Raster<Rgb16> fromWide = Raster.Load<Rgb16>(wide);
        using Raster<Rgb16> fromTwelve = Raster.Load<Rgb16>(twelve);
        Assert.True(((AvifDetails)Raster.Load(wide).Metadata.FormatDetails!).HasSampleTransform);
        Assert.True(Error(source, fromWide) < Error(source, fromTwelve), $"16-bit {Error(source, fromWide):F1}, 12-bit {Error(source, fromTwelve):F1}");
        Assert.Equal(12, ((AvifDetails)Raster.Load(wide, options: new AvifReaderOptions { SampleTransform = false }).Metadata.FormatDetails!).BitDepth);
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(4000)]
    [InlineData(9000)]
    public void TargetSizeWritesTheLargestFileNotAboveIt(int target)
    {
        using Raster<Rgb8> photo = Photo(160, 120);
        byte[] file = AvifWriterTests.Save(photo, new AvifWriterOptions { TargetSize = target });
        Assert.True(file.Length <= target, $"{file.Length} bytes for a target of {target}");

        // Within 5 % below the target (the gate), or one quality step up is above it: sizes do not always grow with quality, so a
        // binary search can stop short.
        int quality = Enumerable.Range(0, 101).First(q => AvifWriterTests.Save(photo, new AvifWriterOptions { Quality = q }).Length == file.Length);
        int next = quality == 100 ? int.MaxValue : AvifWriterTests.Save(photo, new AvifWriterOptions { Quality = quality + 1 }).Length;
        Assert.True(file.Length >= target * 0.95 || next > target, $"quality {quality}: {file.Length} bytes, quality {quality + 1}: {next} bytes, target {target}");
    }

    [Fact]
    public void ATargetTooSmallWritesQualityZeroWithAWarning()
    {
        using Raster<Rgb8> source = Photo(160, 120);
        List<EgamiDiagnostic> warnings = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = warnings.Add };
        using Raster<Rgb8> photo = Raster.Create<Rgb8>(160, 120, context);
        for (int y = 0; y < 120; y++)
        {
            source.Row(y).CopyTo(photo.Row(y));
        }

        using MemoryStream stream = new();
        photo.Save(stream, AvifFormat.Info, new AvifWriterOptions { TargetSize = 50 });
        Assert.Equal(AvifWriterTests.Save(source, new AvifWriterOptions { Quality = 0 }), stream.ToArray());
        Assert.Contains(warnings, w => w.Id == "Avif.TargetSizeExceeded");
    }

    private static Raster<Rgb8> Photo(int width, int height)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(PhotoPath);
        return photo.Derive(r => r.Crop(new Rect((photo.Width - (2 * width)) / 2, (photo.Height - (2 * height)) / 2, 2 * width, 2 * height)).Resize(width, height));
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgb8> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                sum += Sq(ra[x].R - rb[x].R) + Sq(ra[x].G - rb[x].G) + Sq(ra[x].B - rb[x].B);
            }
        }

        double mse = sum / (3.0 * a.Width * a.Height);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / mse);

        static double Sq(int d) => d * d;
    }

    private static double Error(Raster<Rgb16> a, Raster<Rgb16> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgb16> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                sum += Math.Abs(ra[x].R - rb[x].R) + Math.Abs(ra[x].G - rb[x].G) + Math.Abs(ra[x].B - rb[x].B);
            }
        }

        return sum / (3.0 * a.Width * a.Height);
    }
}
