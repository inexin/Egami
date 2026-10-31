using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// JPEG XL extras task 3 and 4: animations through Save. Each frame is cut to what changed and blended or replaced, and loads
/// back with its delay and the loop count.
/// </summary>
public class JxlAnimationEncodeTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    public static TheoryData<string> Animations => new()
    {
        "apng/sprite-over-none.png",
        "apng/sprite-over-previous.png",
        "apng/sprite-source-background.png",
        "apng/pan-rgb.png",
        "gif/pan-global.gif",
        "gif/loop-3.gif",
        "gif/page-dispose-previous.gif",
    };

    [Theory]
    [MemberData(nameof(Animations))]
    public void LosslessFramesComeBackExactly(string name)
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, name));
        Assert.True(source.FrameCount > 1);
        using Raster<Rgba8> loaded = RoundTrip(source, new JxlWriterOptions { Lossless = true });

        Assert.Equal(source.FrameCount, loaded.FrameCount);
        Assert.Equal(source.Metadata.LoopCount, loaded.Metadata.LoopCount);
        for (int i = 0; i < source.FrameCount; i++)
        {
            Assert.Equal(JxlWriter.Ticks(source.Frames[i].Metadata.Delay), (uint)Math.Round(loaded.Frames[i].Metadata.Delay.TotalMilliseconds));
            for (int y = 0; y < source.Height; y++)
            {
                Assert.True(source.Frames[i].Row(y).SequenceEqual(loaded.Frames[i].Row(y)), $"Frame {i}, row {y} differs.");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void EveryEffortIsExact(int effort)
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, "apng/sprite-over-previous.png"));
        using Raster<Rgba8> loaded = RoundTrip(source, new JxlWriterOptions { Lossless = true, Effort = effort });

        AssertFramesEqual(source, loaded);
    }

    [Fact]
    public void WideAndGrayFramesAreExact()
    {
        using Raster<Rgba8> sprite = Raster.Load<Rgba8>(Path.Combine(Images, "apng/sprite-over-previous.png"));
        using Raster<Rgba16> wide = sprite.As<Rgba16>();
        using Raster<Rgba16> wideBack = RoundTrip(wide, new JxlWriterOptions { Lossless = true });
        AssertFramesEqual(wide, wideBack);

        using Raster<Gray8> gray = sprite.As<Gray8>();
        using Raster<Gray8> grayBack = RoundTrip(gray, new JxlWriterOptions { Lossless = true });
        AssertFramesEqual(gray, grayBack);
    }

    [Fact]
    public void LaterFramesAreCutToWhatChanged()
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, "apng/sprite-over-none.png"));
        using MemoryStream file = new();
        source.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = true });
        file.Position = 0;
        IReadOnlyList<JxlLayer> layers = JxlImage.ReadLayers(file);
        try
        {
            Assert.Equal(source.FrameCount, layers.Count);
            Assert.Equal((source.Width, source.Height), (layers[0].Raster.Width, layers[0].Raster.Height));
            Assert.Contains(layers.Skip(1), l => l.Raster.Width * l.Raster.Height < source.Width * source.Height);
        }
        finally
        {
            foreach (JxlLayer layer in layers)
            {
                layer.Dispose();
            }
        }
    }

    [Fact]
    public void FirstFrameOnlyWritesAStill()
    {
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, "gif/pan-global.gif"));
        using Raster<Rgba8> loaded = RoundTrip(source, new JxlWriterOptions { Lossless = true, FirstFrameOnly = true });

        Assert.Equal(1, loaded.FrameCount);
        for (int y = 0; y < source.Height; y++)
        {
            Assert.True(source.Frames[0].Row(y).SequenceEqual(loaded.Row(y)));
        }
    }

    [Theory]
    [MemberData(nameof(Animations))]
    public void LossyFramesAreAsCloseAsStills(string name)
    {
        // Each frame, cut and coded at the asked distance, is about as close as the same frame saved alone.
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, name));
        JxlWriterOptions options = new() { Lossless = false, Distance = 1 };
        using Raster<Rgba8> loaded = RoundTrip(source, options);

        Assert.Equal(source.FrameCount, loaded.FrameCount);
        for (int i = 0; i < source.FrameCount; i++)
        {
            Assert.Equal(JxlWriter.Ticks(source.Frames[i].Metadata.Delay), (uint)Math.Round(loaded.Frames[i].Metadata.Delay.TotalMilliseconds));
            using Raster<Rgba8> still = Raster.Create<Rgba8>(source.Width, source.Height);
            for (int y = 0; y < source.Height; y++)
            {
                source.Frames[i].Row(y).CopyTo(still.Row(y));
            }

            using Raster<Rgba8> stillBack = RoundTrip(still, options);
            double animated = Error(still, loaded, i), alone = Error(still, stillBack, 0);
            Assert.True(animated <= (alone * 1.1) + 0.25, $"Frame {i} differs by {animated:F2} on average; alone it differs by {alone:F2}.");
        }
    }

    /// <summary>Gets the mean absolute difference of frame <paramref name="frame"/> from the still, color weighted by its alpha.</summary>
    private static double Error(Raster<Rgba8> expected, Raster<Rgba8> actual, int frame)
    {
        double error = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<Rgba8> a = expected.Row(y), b = actual.Frames[frame].Row(y);
            for (int x = 0; x < expected.Width; x++)
            {
                double weight = a[x].A / 255.0;
                error += weight * (Math.Abs(a[x].R - b[x].R) + Math.Abs(a[x].G - b[x].G) + Math.Abs(a[x].B - b[x].B)) / 3;
                error += Math.Abs(a[x].A - b[x].A);
            }
        }

        return error / ((long)expected.Width * expected.Height);
    }

    private static Raster<TPixel> RoundTrip<TPixel>(Raster<TPixel> source, JxlWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        source.Save(file, JxlFormat.Info, options);
        file.Position = 0;
        return Raster.Load<TPixel>(file);
    }

    private static void AssertFramesEqual<TPixel>(Raster<TPixel> expected, Raster<TPixel> actual)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            for (int y = 0; y < expected.Height; y++)
            {
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.Frames[i].Row(y)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(actual.Frames[i].Row(y))), $"Frame {i}, row {y} differs.");
            }
        }
    }
}
