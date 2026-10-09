using Egami.Formats.Gif;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Formats.Gif;

public class GifEncodeTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files() => [.. AnimCorpus.Files("gif")];

    /// <summary>Every corpus GIF (256 colors or fewer per frame) read, written and read again gives the same frames.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void ExactRoundTrips(string file)
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(file));
        foreach (GifWriterOptions options in new[] { GifWriterOptions.Default, new GifWriterOptions { OptimizeFrames = false }, new GifWriterOptions { PaletteScope = GifPaletteScope.PerFrame } })
        {
            byte[] written = Save(source, options);
            using Raster<Rgba8> again = Raster.Load<Rgba8>(written);
            AssertSameFrames(source, again);
            Assert.Empty(again.Metadata.Warnings);
            Assert.Equal(source.Frames.Select(f => f.Metadata.Delay), again.Frames.Select(f => f.Metadata.Delay));
            Assert.Equal(source.FrameCount > 1 ? source.Metadata.LoopCount : 1, again.Metadata.LoopCount);
        }
    }

    [Fact]
    public void OptimizedFramesAreSmaller()
    {
        long optimized = 0;
        long whole = 0;
        foreach (string file in AnimCorpus.Files("gif"))
        {
            using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read(file));
            if (source.FrameCount == 1)
            {
                continue;
            }

            optimized += Save(source, GifWriterOptions.Default).Length;
            whole += Save(source, new GifWriterOptions { OptimizeFrames = false }).Length;
        }

        output.WriteLine($"optimized {optimized:N0} bytes, whole frames {whole:N0} bytes");
        Assert.True(optimized < whole);
    }

    [Fact]
    public void FramesThatTurnTransparentAgainAreCleared()
    {
        // A square moves across a transparent canvas, so each frame must clear where the square was.
        using Raster<Rgba8> anim = Raster.Create<Rgba8>(20, 10);
        for (int i = 0; i < 5; i++)
        {
            Frame<Rgba8> frame = i == 0 ? anim.Frames[0] : anim.Frames.Add(anim.Frames[0]);
            for (int y = 0; y < 10; y++)
            {
                frame.Row(y).Clear();
            }

            for (int y = 2; y < 6; y++)
            {
                frame.Row(y).Slice(i * 3, 4).Fill(Rgba8.Red);
            }
        }

        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, GifWriterOptions.Default));
        AssertSameFrames(anim, again);
        Assert.Contains(again.Frames, f => f.Metadata.Disposal == FrameDisposal.ClearToBackground);
        Assert.Contains(again.Frames.Skip(1), f => f.Metadata.Bounds!.Value.Width < 20);
    }

    [Fact]
    public void IdenticalFramesStillKeepTheirDelays()
    {
        using Raster<Rgba8> anim = Raster.Create(6, 6, Rgba8.Blue);
        anim.Frames.Add(anim.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(250);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, GifWriterOptions.Default));
        Assert.Equal(2, again.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(250), again.Frames[1].Metadata.Delay);
        AssertSameFrames(anim, again);
    }

    [Theory]
    [InlineData(DitherMode.None, 34.0)]
    [InlineData(DitherMode.FloydSteinberg, 32.0)]
    [InlineData(DitherMode.Ordered, 33.0)]
    public void PhotosAreQuantizedToTheirPalette(DitherMode dither, double floor)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(AnimCorpus.Images, "ops", "duck.png"));
        byte[] written = Save(photo, new GifWriterOptions { Palette = new PaletteOptions { Dither = dither } });
        using Raster<Rgb8> again = Raster.Load<Rgb8>(written);
        double psnr = Psnr(photo, again);
        output.WriteLine($"{dither}: {written.Length:N0} bytes, {psnr:F2} dB");
        Assert.True(psnr >= floor, $"{psnr:F2} dB");
    }

    [Theory]
    [InlineData(GifPaletteScope.Auto)]
    [InlineData(GifPaletteScope.Global)]
    [InlineData(GifPaletteScope.PerFrame)]
    public void AnimatedPhotosDecodeToTheirQuantizedFrames(GifPaletteScope scope)
    {
        using Raster<Rgba8> pan = Raster.Load<Rgba8>(AnimCorpus.Read("apng/pan-rgb.png"));
        byte[] written = Save(pan, new GifWriterOptions { PaletteScope = scope });
        using Raster<Rgba8> again = Raster.Load<Rgba8>(written);
        Assert.Equal(pan.FrameCount, again.FrameCount);
        Assert.Empty(again.Metadata.Warnings);
        for (int i = 0; i < pan.FrameCount; i++)
        {
            using Raster<Rgba8> a = pan.CopyFrame(i);
            using Raster<Rgba8> b = again.CopyFrame(i);
            double psnr = Psnr(a, b);
            Assert.True(psnr > 28, $"frame {i}: {psnr:F2} dB");
        }

        output.WriteLine($"{scope}: {written.Length:N0} bytes");
    }

    [Fact]
    public void MetadataRoundTrips()
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(AnimCorpus.Read("gif/comment.gif"));
        source.Metadata.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        source.Metadata.IccProfile = new byte[600];
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(source, GifWriterOptions.Default));
        Assert.Equal(source.Metadata.Text, again.Metadata.Text);
        Assert.Equal(source.Metadata.Xmp.ToArray(), again.Metadata.Xmp.ToArray());
        Assert.Equal(600, again.Metadata.IccProfile.Length);

        using Raster<Rgba8> stripped = Raster.Load<Rgba8>(Save(source, new GifWriterOptions { StripMetadata = true }));
        Assert.Empty(stripped.Metadata.Text);
        Assert.True(stripped.Metadata.Xmp.IsEmpty);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, null)]
    [InlineData(4, 3)]
    public void LoopCountIsTotalPlays(int plays, int? repeats)
    {
        using Raster<Rgba8> anim = Raster.Create(2, 2, Rgba8.Red);
        anim.Frames.Add(anim.Frames[0]);
        anim.Metadata.LoopCount = plays;
        using Raster<Rgba8> again = Raster.Load<Rgba8>(Save(anim, GifWriterOptions.Default));
        Assert.Equal(plays, again.Metadata.LoopCount);
        Assert.Equal(repeats, ((GifDetails)again.Metadata.FormatDetails!).RepeatCount);
    }

    [Fact]
    public void StillImagesHaveNoAnimationBlocks()
    {
        using Raster<Rgb8> still = Raster.Create(3, 3, new Rgb8(1, 2, 3));
        byte[] written = Save(still, GifWriterOptions.Default);
        Assert.Equal(-1, written.AsSpan().IndexOf([(byte)0x21, (byte)0xF9]));
        Assert.Equal(-1, written.AsSpan().IndexOf("NETSCAPE"u8));
        using Raster<Rgba8> again = Raster.Load<Rgba8>(written);
        Assert.Equal(new Rgba8(1, 2, 3, 255), again.GetPixel(1, 1));
    }

    internal static byte[] Save<TPixel>(Raster<TPixel> raster, GifWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, GifFormat.Info, options);
        return stream.ToArray();
    }

    private static void AssertSameFrames(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            for (int y = 0; y < expected.Height; y++)
            {
                Span<Rgba8> a = expected.Frames[i].Row(y);
                Span<Rgba8> b = actual.Frames[i].Row(y);
                for (int x = 0; x < a.Length; x++)
                {
                    bool same = a[x].A < 128 ? b[x].A == 0 : a[x] with { A = 255 } == b[x];
                    Assert.True(same, $"frame {i} ({x}, {y}): {a[x]} became {b[x]}");
                }
            }
        }
    }

    private static double Psnr<TPixel>(Raster<TPixel> a, Raster<TPixel> b)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<Rgb8> x = a.As<Rgb8>();
        using Raster<Rgb8> y = b.As<Rgb8>();
        double sum = 0;
        for (int r = 0; r < x.Height; r++)
        {
            Span<Rgb8> p = x.Row(r);
            Span<Rgb8> q = y.Row(r);
            for (int c = 0; c < p.Length; c++)
            {
                sum += ((p[c].R - q[c].R) * (p[c].R - q[c].R)) + ((p[c].G - q[c].G) * (p[c].G - q[c].G)) + ((p[c].B - q[c].B) * (p[c].B - q[c].B));
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / (sum / (x.Width * x.Height * 3)));
    }
}
