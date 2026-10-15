using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>Phase 2.9.3 task 10: animations through Load, frame ranges, layers, the preview and the LF-only decode.</summary>
public class JxlFrameTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jxl");

    [Theory]
    [InlineData("pan-anim-lossless.jxl", "pan-anim-lossless.png")]
    [InlineData("sprite-anim-lossless.jxl", "sprite-anim-lossless.png")]
    public void AnimationDelaysAndLoopsEqualDjxl(string file, string reference)
    {
        using Raster egami = Raster.Load(Path.Combine(Corpus, file));
        using Raster djxl = Raster.Load(Path.Combine(References, reference));
        Assert.Equal(djxl.FrameCount, egami.FrameCount);
        Assert.Equal(djxl.Metadata.LoopCount, egami.Metadata.LoopCount);
        using Raster<Rgba8> a = egami.As<Rgba8>();
        using Raster<Rgba8> b = djxl.As<Rgba8>();
        for (int i = 0; i < a.FrameCount; i++)
        {
            Assert.Equal(b.Frames[i].Metadata.Delay.TotalMilliseconds, a.Frames[i].Metadata.Delay.TotalMilliseconds, 0.5);
        }

        Assert.Equal(egami.FrameCount, Raster.Probe(Path.Combine(Corpus, file)).FrameCount);
    }

    [Fact]
    public void FrameRangeGivesThoseFrames()
    {
        string path = Path.Combine(Corpus, "pan-anim-lossless.jxl");
        using Raster<Rgb8> all = Raster.Load<Rgb8>(path);
        using Raster<Rgb8> some = Raster.Load<Rgb8>(path, options: new ReaderOptions { Frames = 2..4 });
        using Raster<Rgb8> last = Raster.Load<Rgb8>(path, options: new ReaderOptions { Frames = ^1.. });
        Assert.Equal(2, some.FrameCount);
        Assert.Equal(1, last.FrameCount);
        for (int y = 0; y < all.Height; y++)
        {
            Assert.True(all.Frames[2].Row(y).SequenceEqual(some.Frames[0].Row(y)));
            Assert.True(all.Frames[3].Row(y).SequenceEqual(some.Frames[1].Row(y)));
            Assert.True(all.Frames[all.FrameCount - 1].Row(y).SequenceEqual(last.Frames[0].Row(y)));
        }
    }

    [Fact]
    public void LayersAreTheFramesBeforeBlending()
    {
        string path = Path.Combine(Corpus, "libjxl", "cropped_traffic_light.jxl");
        JxlFile file = JxlInfoText.Read(File.ReadAllBytes(path));
        IReadOnlyList<JxlLayer> layers = JxlImage.ReadLayers(path);
        try
        {
            JxlFrameHeader[] regular = [.. file.Frames.Where(f => f.IsNormal)];
            Assert.Equal(regular.Length, layers.Count);
            for (int i = 0; i < layers.Count; i++)
            {
                Assert.Equal((regular[i].X0, regular[i].Y0), (layers[i].X, layers[i].Y));
                Assert.Equal((regular[i].Width, regular[i].Height), (layers[i].Raster.Width, layers[i].Raster.Height));
                Assert.Equal((int)regular[i].Blending.Mode, (int)layers[i].Blend);
            }

            // Replacing onto the canvas: a layer that covers it all is the frame Load shows.
            using Raster<Rgba8> shown = Raster.Load<Rgba8>(path);
            using Raster<Rgba8> first = layers[0].Raster.As<Rgba8>();
            if (layers[0] is { X: 0, Y: 0, Blend: JxlLayerBlend.Replace } && first.Width == shown.Width && first.Height == shown.Height)
            {
                for (int y = 0; y < shown.Height; y++)
                {
                    Assert.True(first.Row(y).SequenceEqual(shown.Frames[0].Row(y)));
                }
            }
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
    public void PreviewIsNullWithoutOne()
    {
        Assert.Null(JxlImage.ReadPreview(Path.Combine(Corpus, "duck-d1.jxl")));
    }

    [Theory]
    [InlineData("duck-d1.jxl")]
    [InlineData("duck-d1-progressive.jxl")]
    public void MaxDownscaleEightDecodesTheLfImage(string file)
    {
        string path = Path.Combine(Corpus, file);
        using Raster<RgbF32> full = Raster.Load<RgbF32>(path);
        using Raster<RgbF32> lf = Raster.Load<RgbF32>(path, options: new JxlReaderOptions { MaxDownscale = 8 });
        Assert.Equal(((full.Width + 7) / 8, (full.Height + 7) / 8), (lf.Width, lf.Height));

        // Each LF sample is close to the average of its 8x8 block.
        double total = 0;
        for (int by = 0; by < full.Height / 8; by++)
        {
            for (int bx = 0; bx < full.Width / 8; bx++)
            {
                float sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        sum += full.Row((by * 8) + y)[(bx * 8) + x].G;
                    }
                }

                total += Math.Abs((sum / 64) - lf.Row(by)[bx].G);
            }
        }

        Assert.InRange(total / ((full.Width / 8) * (full.Height / 8)), 0, 0.02);
    }

    [Fact]
    public void MaxDownscaleDecodesLosslessInFull()
    {
        string path = Path.Combine(Corpus, "duck-lossless-e1.jxl");
        using Raster lf = Raster.Load(path, options: new JxlReaderOptions { MaxDownscale = 8 });
        Assert.Equal(Raster.Probe(path).Width, lf.Width);
    }
}
