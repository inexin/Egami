using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>JPEG XL extras task 5: JxlImage.WriteLayers, which ReadLayers reads back field for field.</summary>
public class JxlLayerWriteTests
{
    [Fact]
    public void ReadLayersGivesEveryFieldBack()
    {
        List<JxlLayer> layers =
        [
            new(Pattern(64, 48, 1, 255), name: "background"),
            new(Pattern(20, 16, 2, 200), -6, -4, JxlLayerBlend.Blend, name: "über"),
            new(Pattern(16, 16, 3, 255), 50, 40, JxlLayerBlend.Add),
            new(Pattern(24, 12, 4, 128), 10, 30, JxlLayerBlend.MulAdd, name: "mul add"),
            new(Pattern(30, 20, 5, 255), 20, 10, JxlLayerBlend.Multiply, name: "multiply"),
        ];
        try
        {
            using MemoryStream file = new();
            JxlImage.WriteLayers(file, 64, 48, layers);
            file.Position = 0;
            IReadOnlyList<JxlLayer> read = JxlImage.ReadLayers(file);
            try
            {
                Assert.Equal(layers.Count, read.Count);
                for (int i = 0; i < layers.Count; i++)
                {
                    Assert.Equal((layers[i].X, layers[i].Y, layers[i].Blend, layers[i].Duration, layers[i].Name), (read[i].X, read[i].Y, read[i].Blend, read[i].Duration, read[i].Name));
                    AssertSame(layers[i].Raster, read[i].Raster);
                }
            }
            finally
            {
                Dispose(read);
            }

            file.Position = 0;
            using Raster loaded = Raster.Load(file);
            Assert.Equal((64, 48, 1), (loaded.Width, loaded.Height, loaded.FrameCount));
        }
        finally
        {
            Dispose(layers);
        }
    }

    [Fact]
    public void ReplacedLayersLoadAsTheyLie()
    {
        using Raster<Rgb8> bottom = Raster.Create<Rgb8>(40, 30, new Rgb8(10, 20, 30));
        using Raster<Rgb8> top = Raster.Create<Rgb8>(10, 10, new Rgb8(200, 100, 50));
        using MemoryStream file = new();
        JxlImage.WriteLayers(file, 40, 30, [new JxlLayer(bottom.As<Rgb8>()), new JxlLayer(top.As<Rgb8>(), 35, -5)]);
        file.Position = 0;
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(file);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Rgb8 expected = x >= 35 && y < 5 ? new Rgb8(200, 100, 50) : new Rgb8(10, 20, 30);
                Assert.Equal(expected, loaded.Row(y)[x]);
            }
        }
    }

    [Fact]
    public void DurationsMakeAnAnimation()
    {
        List<JxlLayer> layers =
        [
            new(Pattern(32, 32, 1, 255), duration: TimeSpan.FromMilliseconds(100)),
            new(Pattern(8, 8, 2, 255), 4, 4),
            new(Pattern(8, 8, 3, 255), 12, 12, duration: TimeSpan.FromMilliseconds(250)),
        ];
        try
        {
            using MemoryStream file = new();
            JxlImage.WriteLayers(file, 32, 32, layers);
            file.Position = 0;
            using Raster loaded = Raster.Load(file);

            // The middle layer is not shown alone: it joins the last.
            Assert.Equal(2, loaded.FrameCount);
            Assert.Equal(TimeSpan.FromMilliseconds(100), loaded.FrameMetadataAt(0).Delay);
            Assert.Equal(TimeSpan.FromMilliseconds(250), loaded.FrameMetadataAt(1).Delay);
        }
        finally
        {
            Dispose(layers);
        }
    }

    [Fact]
    public void LayersOfOtherTypesTakeTheFirstOnes()
    {
        using Raster<Rgba16> wide = Raster.Create<Rgba16>(16, 16, new Rgba16(1000, 2000, 3000, 65535));
        using Raster<Gray8> gray = Raster.Create<Gray8>(4, 4, new Gray8(77));
        using MemoryStream file = new();
        JxlImage.WriteLayers(file, 16, 16, [new JxlLayer(wide.As<Rgba16>()), new JxlLayer(gray.As<Gray8>(), 2, 2)]);
        file.Position = 0;
        IReadOnlyList<JxlLayer> read = JxlImage.ReadLayers(file);
        try
        {
            using Raster<Rgba16> first = read[0].Raster.As<Rgba16>();
            Assert.Equal(new Rgba16(1000, 2000, 3000, 65535), first.Row(5)[5]);
            using Raster<Gray8> second = read[1].Raster.As<Gray8>();
            Assert.Equal(new Gray8(77), second.Row(1)[1]);
        }
        finally
        {
            Dispose(read);
        }
    }

    [Fact]
    public void LossyLayersStayClose()
    {
        List<JxlLayer> layers = [new(Pattern(64, 48, 1, 255)), new(Pattern(20, 16, 2, 255), 30, 20, name: "patch")];
        try
        {
            using MemoryStream file = new();
            JxlImage.WriteLayers(file, 64, 48, layers, new JxlWriterOptions { Lossless = false, Distance = 1 });
            file.Position = 0;
            IReadOnlyList<JxlLayer> read = JxlImage.ReadLayers(file);
            try
            {
                Assert.Equal(("patch", 30, 20), (read[1].Name, read[1].X, read[1].Y));
                using Raster<Rgba8> a = layers[1].Raster.As<Rgba8>();
                using Raster<Rgba8> b = read[1].Raster.As<Rgba8>();
                double error = 0;
                for (int y = 0; y < a.Height; y++)
                {
                    for (int x = 0; x < a.Width; x++)
                    {
                        error += Math.Abs(a.Row(y)[x].G - b.Row(y)[x].G);
                    }
                }

                Assert.True(error / (a.Width * a.Height) < 6, $"Mean error {error / (a.Width * a.Height):F2}.");
            }
            finally
            {
                Dispose(read);
            }
        }
        finally
        {
            Dispose(layers);
        }
    }

    [Fact]
    public void BadLayersAreRefused()
    {
        using Raster<Rgb8> r = Raster.Create<Rgb8>(4, 4);
        using MemoryStream file = new();
        Assert.Throws<ArgumentException>(() => JxlImage.WriteLayers(file, 4, 4, []));
        Assert.Throws<ArgumentNullException>(() => JxlImage.WriteLayers(file, 4, 4, [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => JxlImage.WriteLayers(file, 0, 4, [new JxlLayer(r.As<Rgb8>())]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlLayer(r, blend: (JxlLayerBlend)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlLayer(r, duration: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(() => new JxlLayer(r, name: new string('x', 1072)));
        Assert.Throws<ArgumentNullException>(() => new JxlLayer(null!));
    }

    /// <summary>Makes a layer of smooth color and a few hard edges, with alpha <paramref name="alpha"/> where it is not opaque.</summary>
    private static Raster<Rgba8> Pattern(int width, int height, int seed, byte alpha)
    {
        Raster<Rgba8> r = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte a = ((x + y) % 7 == 0) ? alpha : (byte)255;
                r.Row(y)[x] = new Rgba8((byte)((x * 9 * seed) + y), (byte)((y * 11) + (seed * 30)), (byte)(((x ^ y) * 13) + seed), a);
            }
        }

        return r;
    }

    private static void AssertSame(Raster expected, Raster actual)
    {
        using Raster<Rgba8> a = expected.As<Rgba8>();
        using Raster<Rgba8> b = actual.As<Rgba8>();
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        for (int y = 0; y < a.Height; y++)
        {
            Assert.True(a.Row(y).SequenceEqual(b.Row(y)), $"Row {y} differs.");
        }
    }

    private static void Dispose(IEnumerable<JxlLayer> layers)
    {
        foreach (JxlLayer layer in layers)
        {
            layer.Dispose();
        }
    }
}
