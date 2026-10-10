using System.Numerics;

using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>The image files every streaming test runs over, and helpers to compare what they decode to.</summary>
internal static class StreamingCorpus
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "images");

    private static readonly Lazy<string[]> AllFiles = new(() =>
    [
        .. Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("fuzz/", StringComparison.Ordinal) && !f.StartsWith("LICENSES", StringComparison.Ordinal) && Loads(f))
            .Order(StringComparer.Ordinal),
    ]);

    /// <summary>Gets every corpus file Egami loads, as "png/name.png".</summary>
    public static IReadOnlyList<string> Files => AllFiles.Value;

    public static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Root, file));

    public static void Equal<TPixel>(Raster<TPixel> expected, int frame, Raster<TPixel> actual, string because)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Assert.True((expected.Width, expected.Height) == (actual.Width, actual.Height), $"{because}: {actual.Width}×{actual.Height}, expected {expected.Width}×{expected.Height}.");
        for (int y = 0; y < expected.Height; y++)
        {
            if (!expected.Frames[frame].Row(y).SequenceEqual(actual.Row(y)))
            {
                Assert.Fail($"{because}: frame {frame}, row {y} differs.");
            }
        }
    }

    public static Rect Bounds(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    private static bool Loads(string file)
    {
        try
        {
            using Raster raster = Raster.Load(Path.Combine(Root, file));
            return true;
        }
        catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException or LimitExceededException)
        {
            return false;
        }
    }
}

/// <summary>
/// The fallback path serves rows converted from the reader's natural pixel type; this holds that the conversion gives exactly
/// what reading straight into the target type gives, for every corpus file.
/// </summary>
public class NaturalConversionTests
{
    public static TheoryData<string> Files() => [.. StreamingCorpus.Files];

    [Theory]
    [MemberData(nameof(Files))]
    public void NaturalThenConvertEqualsDirect(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster natural = Raster.Load(data);
        Check<Rgba8>(data, natural);
        Check<Rgb8>(data, natural);
        Check<Gray8>(data, natural);
        Check<Rgba16>(data, natural);
        Check<RgbaF32>(data, natural);
        Check<Bgra8>(data, natural);
    }

    private static void Check<TPixel>(byte[] data, Raster natural)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> direct = Raster.Load<TPixel>(data);
        using Raster<TPixel> converted = natural.As<TPixel>();
        Assert.Equal(direct.FrameCount, converted.FrameCount);
        for (int f = 0; f < direct.FrameCount; f++)
        {
            for (int y = 0; y < direct.Height; y++)
            {
                Assert.True(direct.Frames[f].Row(y).SequenceEqual(converted.Frames[f].Row(y)), $"{typeof(TPixel).Name}: frame {f}, row {y}");
            }
        }
    }
}

/// <summary>Phase 2.2 task 2: every file decodes through the session exactly as Load decodes it.</summary>
public class RasterDecoderTests
{
    public static TheoryData<string> Files() => [.. StreamingCorpus.Files];

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadRowsEqualsLoadAtEveryBandHeight(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        foreach (int bandHeight in (int[])[1, 7, 256, expected.Height])
        {
            using RasterDecoder decoder = RasterDecoder.Open(data);
            Assert.Equal((expected.Width, expected.Height), (decoder.Info.Width, decoder.Info.Height));
            using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, bandHeight);
            for (int frame = 0; frame < expected.FrameCount; frame++)
            {
                int y = 0;
                int rows;
                while ((rows = decoder.ReadRows(band)) > 0)
                {
                    for (int i = 0; i < rows; i++)
                    {
                        Assert.True(expected.Frames[frame].Row(y + i).SequenceEqual(band.Row(i)), $"band {bandHeight}, frame {frame}, row {y + i}");
                    }

                    y += rows;
                }

                Assert.Equal(expected.Height, y);
                Assert.Equal(frame < expected.FrameCount - 1, decoder.NextFrame());
            }

            Assert.Null(decoder.CurrentFrame);
            Assert.Equal(0, decoder.ReadRows(band));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadFrameEqualsLoadInEveryPixelType(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        CheckFrames<Rgb8>(data);
        CheckFrames<Gray8>(data);
        CheckFrames<Rgba16>(data);
        CheckFrames<RgbaF32>(data);
        CheckFrames<Bgra8>(data);
        CheckFrames<Gray16>(data);
        CheckFrames<Cmyk8>(data);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MetadataAndInfoMatchLoad(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster expected = Raster.Load(data);
        RasterInfo probe = Raster.Probe(data);
        using RasterDecoder decoder = RasterDecoder.Open(data);
        Assert.Equal(expected.Layout, decoder.Info.Layout);
        Assert.True(decoder.Info.FrameCount is null || decoder.Info.FrameCount == expected.FrameCount);
        Assert.Equal(probe.Format, decoder.Info.Format);
        Assert.Equal(probe.Orientation, decoder.Info.Orientation);
        Assert.Equal(probe.Access, decoder.Info.Access);
        Assert.Equal(probe.WorkingBytes, decoder.Info.WorkingBytes);
        if (decoder.Info.Access == RasterAccess.Buffered)
        {
            Assert.Equal(BytesOf(expected), decoder.Info.WorkingBytes);
        }

        // Metadata after the image data arrives once the last frame is read.
        while (decoder.ReadFrame<Rgba8>() is Raster<Rgba8> frame)
        {
            frame.Dispose();
        }

        Assert.True(decoder.IsMetadataComplete);
        RasterMetadata a = expected.Metadata;
        RasterMetadata b = decoder.Metadata;
        Assert.Equal(a.FormatName, b.FormatName);
        Assert.Equal(a.Exif is null, b.Exif is null);
        Assert.Equal(a.IccProfile.ToArray(), b.IccProfile.ToArray());
        Assert.Equal(a.Xmp.ToArray(), b.Xmp.ToArray());
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Warnings, b.Warnings);
        Assert.Equal(a.LoopCount, b.LoopCount);
        Assert.Equal(a.Resolution, b.Resolution);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void LoadIntoEqualsTheFirstFrameOfLoad(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using Raster<Rgba8> target = Raster.Create(expected.Width, expected.Height, Rgba8.Red);
        target.Metadata.Text.Add(new TextEntry("Stale", "yes"));
        Raster.LoadInto(data, target);
        StreamingCorpus.Equal(expected, 0, target, file);
        Assert.Equal(expected.Metadata.Text, target.Metadata.Text);
        Assert.Equal(expected.Metadata.FormatName, target.Metadata.FormatName);
        Assert.Equal(expected.Frames[0].Metadata.Delay, target.Frames[0].Metadata.Delay);
    }

    [Fact]
    public void LoadIntoAWrongSizeNamesBothSizes()
    {
        byte[] data = StreamingCorpus.Read("ops/shapes.png");
        using Raster<Rgba8> target = Raster.Create<Rgba8>(10, 10);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Raster.LoadInto(data, target));
        Assert.Contains("10×10", e.Message, StringComparison.Ordinal);
        Assert.Contains("200×150", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipRowsMovesPastRows()
    {
        byte[] data = StreamingCorpus.Read("ops/shapes.png");
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = RasterDecoder.Open(data);
        Assert.Equal(100, decoder.SkipRows(100));
        using Raster<Rgba8> band = Raster.Create<Rgba8>(200, 10);
        Assert.Equal(10, decoder.ReadRows(band));
        Assert.True(expected.Row(105).SequenceEqual(band.Row(5)));
        Assert.Equal(40, decoder.SkipRows(1000));
        Assert.Equal(0, decoder.ReadRows(band));
    }

    [Fact]
    public void ABandOfTheWrongWidthThrows()
    {
        using RasterDecoder decoder = RasterDecoder.Open(StreamingCorpus.Read("ops/shapes.png"));
        using Raster<Rgba8> band = Raster.Create<Rgba8>(199, 4);
        Assert.Throws<ArgumentException>(() => decoder.ReadRows(band));
    }

    [Fact]
    public void ADisposedDecoderThrows()
    {
        RasterDecoder decoder = RasterDecoder.Open(StreamingCorpus.Read("ops/shapes.png"));
        decoder.Dispose();
        decoder.Dispose();
        using Raster<Rgba8> band = Raster.Create<Rgba8>(200, 4);
        Assert.Throws<ObjectDisposedException>(() => decoder.ReadRows(band));
    }

    [Fact]
    public void DisposingReturnsEveryBuffer()
    {
        byte[] data = StreamingCorpus.Read("ops/egret.png");
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, context);
            using Raster<Rgb8>? frame = decoder.ReadFrame<Rgb8>();
        });
        Assert.Equal(0, peak.LeftBytes);
        Assert.True(peak.PeakPixelBytes >= 480 * 640 * 3);
    }

    [Fact]
    public void TheFramesOptionSelectsFramesAsInLoad()
    {
        string file = StreamingCorpus.Files.First(f => f.StartsWith("gif/", StringComparison.Ordinal) && Raster.Probe(StreamingCorpus.Read(f)).FrameCount > 2);
        byte[] data = StreamingCorpus.Read(file);
        ReaderOptions options = new() { Frames = 1..2 };
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data, options: options);
        using RasterDecoder decoder = RasterDecoder.Open(data, options: options);
        using Raster<Rgba8>? frame = decoder.ReadFrame<Rgba8>();
        Assert.NotNull(frame);
        StreamingCorpus.Equal(expected, 0, frame, file);
        Assert.Null(decoder.ReadFrame<Rgba8>());
    }

    private static long BytesOf(Raster raster) => (long)raster.Width * raster.Height * raster.Layout.BytesPerPixel * raster.FrameCount;

    private static void CheckFrames<TPixel>(byte[] data)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> expected = Raster.Load<TPixel>(data);
        using RasterDecoder decoder = RasterDecoder.Open(data);
        using Raster<TPixel> target = Raster.Create<TPixel>(decoder.Info.Width, decoder.Info.Height);
        for (int frame = 0; frame < expected.FrameCount; frame++)
        {
            FrameMetadata? metadata;
            if (frame % 2 == 0)
            {
                metadata = decoder.ReadFrame(target);
                StreamingCorpus.Equal(expected, frame, target, typeof(TPixel).Name);
            }
            else
            {
                using Raster<TPixel>? own = decoder.ReadFrame<TPixel>();
                Assert.NotNull(own);
                StreamingCorpus.Equal(expected, frame, own, typeof(TPixel).Name);
                metadata = own.Frames[0].Metadata;
            }

            FrameMetadata e = expected.Frames[frame].Metadata;
            Assert.NotNull(metadata);
            Assert.Equal((e.Delay, e.Disposal, e.Blend, e.Bounds), (metadata.Delay, metadata.Disposal, metadata.Blend, metadata.Bounds));
        }

        Assert.Null(decoder.ReadFrame(target));
    }
}

/// <summary>Region and reduced decoding, through Load and the session, against Load then Crop and a scalar box-average reference.</summary>
public class RegionAndReductionTests
{
    public static TheoryData<string> Files() => [.. StreamingCorpus.Files];

    public static TheoryData<string> NotJpeg() => [.. StreamingCorpus.Files.Where(f => !IsJpeg(f))];

    public static TheoryData<string> Jpeg() => [.. StreamingCorpus.Files.Where(IsJpeg)];

    [Theory]
    [MemberData(nameof(Files))]
    public void RegionEqualsLoadThenCrop(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> full = Raster.Load<Rgba8>(data);
        foreach (Rect region in Regions(full.Width, full.Height, file))
        {
            Rect clipped = Clip(region, full.Width, full.Height);
            using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, options: new ReaderOptions { Region = region });
            Assert.Equal(full.FrameCount, loaded.FrameCount);
            for (int f = 0; f < full.FrameCount; f++)
            {
                using Raster<Rgba8> expected = full.CopyFrame(f);
                expected.Apply(ops => ops.Crop(clipped), TestContext.Current.CancellationToken);
                using Raster<Rgba8> actual = loaded.CopyFrame(f);
                StreamingCorpus.Equal(expected, 0, actual, $"{file} {region}");
            }

            // The session gives the same.
            using RasterDecoder decoder = RasterDecoder.Open(data, options: new ReaderOptions { Region = region });
            Assert.Equal((clipped.Width, clipped.Height), (decoder.Info.Width, decoder.Info.Height));
            using Raster<Rgba8>? first = decoder.ReadFrame<Rgba8>();
            StreamingCorpus.Equal(loaded, 0, first!, $"{file} {region} session");
        }
    }

    [Theory]
    [MemberData(nameof(NotJpeg))]
    public void ReductionEqualsTheScalarBoxAverage(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster natural = Raster.Load(data);
        using Raster<RgbaF32> floats = natural.As<RgbaF32>();
        Random random = new(file.Length);
        foreach (Size minimum in (Size[])[new(floats.Width / 2, floats.Height / 2), new(floats.Width / 3, 1), new(1, 1), new(5, 7), new(floats.Width + 5, 1)])
        {
            foreach (Rect? region in (Rect?[])[null, RandomRect(random, floats.Width, floats.Height)])
            {
                ReaderOptions options = new() { MinimumSize = minimum, Region = region };
                Rect r = region is Rect given ? Clip(given, floats.Width, floats.Height) : new Rect(0, 0, floats.Width, floats.Height);
                int n = ExpectedFactor(r.Size, minimum);
                CheckReduced<Rgba8>(data, options, floats, r, n, file);
                CheckReduced<Rgba16>(data, options, floats, r, n, file);
                CheckReduced<Gray8>(data, options, floats, r, n, file);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Jpeg))]
    public void JpegRegionAndReductionEqualAScaledLoadCropped(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        RasterInfo info = Raster.Probe(data);
        Random random = new(file.Length);
        foreach (Size minimum in (Size[])[new(info.Width / 3, info.Height / 3), new(info.Width / 8, info.Height / 8), new(2, 2)])
        {
            foreach (Rect? region in (Rect?[])[null, RandomRect(random, info.Width, info.Height), new Rect(info.Width - 3, info.Height - 5, 10, 10)])
            {
                Rect r = region is Rect given ? Clip(given, info.Width, info.Height) : new Rect(0, 0, info.Width, info.Height);
                int n = ExpectedFactor(r.Size, minimum);
                int scale = n >= 8 ? 8 : n >= 4 ? 4 : n >= 2 ? 2 : 1;
                using Raster<Rgb8> scaled = Raster.Load<Rgb8>(data, options: new ReaderOptions
                {
                    MinimumSize = new Size((info.Width + scale - 1) / scale, (info.Height + scale - 1) / scale),
                });
                Rect cropped = StreamingCorpus.Bounds(r.X / scale, r.Y / scale, Math.Min(scaled.Width, (r.Right + scale - 1) / scale), Math.Min(scaled.Height, (r.Bottom + scale - 1) / scale));
                using Raster<RgbaF32> floats = scaled.As<RgbaF32>();
                int k = scale == 8 ? ExpectedFactor(cropped.Size, minimum) : 1;
                using Raster<Rgb8> expected = Reference<Rgb8>(floats, cropped, k);
                using Raster<Rgb8> actual = Raster.Load<Rgb8>(data, options: new ReaderOptions { MinimumSize = minimum, Region = region });
                StreamingCorpus.Equal(expected, 0, actual, $"{file} {minimum} {region} scale {scale} box {k}");
            }
        }
    }

    [Fact]
    public void ARegionThatMissesTheImageThrows()
    {
        byte[] data = StreamingCorpus.Read("ops/shapes.png");
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(data, options: new ReaderOptions { Region = new Rect(200, 0, 5, 5) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Probe(data, options: new ReaderOptions { Region = new Rect(-10, -10, 10, 10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RasterDecoder.Open(data, options: new ReaderOptions { Region = new Rect(5, 5, 0, 3) }));
    }

    [Fact]
    public void ARegionDropsTheStoredLayoutFromFrameMetadata()
    {
        string file = StreamingCorpus.Files.First(f => f.StartsWith("apng/", StringComparison.Ordinal) && Raster.Probe(StreamingCorpus.Read(f)).FrameCount > 1);
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(StreamingCorpus.Read(file), options: new ReaderOptions { Region = new Rect(1, 1, 4, 4) });
        Assert.All(loaded.Frames, f => Assert.Null(f.Metadata.Bounds));
    }

    [Fact]
    public void ProbeReportsTheWorkingBytesOfTheBuffer()
    {
        byte[] data = StreamingCorpus.Read("bmp/rgb24.bmp");
        RasterInfo full = Raster.Probe(data);
        Assert.Equal(RasterAccess.Buffered, full.Access);
        Assert.Equal(64L * 48 * full.Layout.BytesPerPixel, full.WorkingBytes);
        RasterInfo reduced = Raster.Probe(data, options: new ReaderOptions { MinimumSize = new Size(16, 12) });
        Assert.True(reduced.WorkingBytes > full.WorkingBytes);
        byte[] jpeg = StreamingCorpus.Read("jpeg/egret-420.jpg");
        RasterInfo jpegFull = Raster.Probe(jpeg);
        Assert.Equal(RasterAccess.Streamed, jpegFull.Access);
        Assert.True(Raster.Probe(jpeg, options: new ReaderOptions { MinimumSize = new Size(30, 40) }).WorkingBytes < jpegFull.WorkingBytes);
    }

    private static void CheckReduced<TPixel>(byte[] data, ReaderOptions options, Raster<RgbaF32> natural, Rect region, int factor, string file)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> actual = Raster.Load<TPixel>(data, options: options);
        using Raster<TPixel> direct = Raster.Load<TPixel>(data);
        Assert.Equal(natural.FrameCount, actual.FrameCount);
        for (int f = 0; f < natural.FrameCount; f++)
        {
            // Without reduction, rows convert straight from the file's samples, as Load does, so the reference is a crop.
            using Raster<RgbaF32> frame = natural.CopyFrame(f);
            using Raster<TPixel> plain = direct.CopyFrame(f);
            using Raster<TPixel> expected = factor == 1 ? plain.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken) : Reference<TPixel>(frame, region, factor);
            using Raster<TPixel> got = actual.CopyFrame(f);
            StreamingCorpus.Equal(expected, 0, got, $"{file} {typeof(TPixel).Name} {options.MinimumSize} {options.Region} factor {factor}");
        }

        // Rows from the session match, in bands of 3.
        using RasterDecoder decoder = RasterDecoder.Open(data, options: options);
        using Raster<TPixel> band = Raster.Create<TPixel>(decoder.Info.Width, 3);
        int y = 0;
        int rows;
        while ((rows = decoder.ReadRows(band)) > 0)
        {
            for (int i = 0; i < rows; i++)
            {
                Assert.True(actual.Row(y + i).SequenceEqual(band.Row(i)));
            }

            y += rows;
        }

        Assert.Equal(actual.Height, y);
    }

    /// <summary>The plain box average: each output pixel straight from its block, alpha-weighted color and mean alpha.</summary>
    private static Raster<TPixel> Reference<TPixel>(Raster<RgbaF32> source, Rect region, int n)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = (region.Width + n - 1) / n;
        int height = (region.Height + n - 1) / n;
        Raster<TPixel> result = Raster.Create<TPixel>(width, height);
        for (int oy = 0; oy < height; oy++)
        {
            for (int ox = 0; ox < width; ox++)
            {
                if (n == 1)
                {
                    result.Row(oy)[ox] = TPixel.FromScaled(source.Row(region.Y + oy)[region.X + ox].ToScaled());
                    continue;
                }

                double r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int y = region.Y + (oy * n); y < Math.Min(region.Bottom, region.Y + ((oy + 1) * n)); y++)
                {
                    for (int x = region.X + (ox * n); x < Math.Min(region.Right, region.X + ((ox + 1) * n)); x++)
                    {
                        RgbaF32 p = source.Row(y)[x];
                        r += p.R * (double)p.A;
                        g += p.G * (double)p.A;
                        b += p.B * (double)p.A;
                        a += p.A;
                        count++;
                    }
                }

                Vector4 average = a <= 0 ? Vector4.Zero : new Vector4((float)(r / a), (float)(g / a), (float)(b / a), (float)(a / count));
                result.Row(oy)[ox] = TPixel.FromScaled(average);
            }
        }

        return result;
    }

    /// <summary>The largest N with ceil(size / N) at least the minimum, found by trying every N.</summary>
    private static int ExpectedFactor(Size size, Size minimum)
    {
        if (minimum.Width < 1 || minimum.Height < 1)
        {
            return 1;
        }

        int best = 1;
        for (int n = 1; n <= Math.Max(size.Width, size.Height); n++)
        {
            if ((size.Width + n - 1) / n >= minimum.Width && (size.Height + n - 1) / n >= minimum.Height)
            {
                best = n;
            }
        }

        return best;
    }

    private static IEnumerable<Rect> Regions(int width, int height, string file)
    {
        Random random = new(file.Length * 31);
        yield return new Rect(0, 0, width, height);
        yield return new Rect(-5, -5, 7, 9);
        yield return new Rect(width - 1, height - 1, 50, 50);
        yield return new Rect(0, height / 2, width, 1);
        for (int i = 0; i < 3; i++)
        {
            yield return RandomRect(random, width, height);
        }
    }

    private static Rect RandomRect(Random random, int width, int height)
    {
        int x = random.Next(width);
        int y = random.Next(height);
        return new Rect(x, y, random.Next(1, width - x + 1), random.Next(1, height - y + 1));
    }

    private static Rect Clip(Rect r, int width, int height)
    {
        return StreamingCorpus.Bounds(Math.Max(0, r.X), Math.Max(0, r.Y), Math.Min(width, r.Right), Math.Min(height, r.Bottom));
    }

    private static bool IsJpeg(string file) => file.StartsWith("jpeg/", StringComparison.Ordinal);
}
