using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class KnockoutAndMaskTests
{
    private static readonly RgbaF32 HalfRed = new(1, 0, 0, 0.5f);
    private static readonly RgbaF32 HalfBlue = new(0, 0, 1, 0.5f);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InAKnockoutGroupTheLaterShapeReplacesTheEarlier(bool isolated)
    {
        using Raster<Rgba8> drawn = Pixels.Draw(40, 20, c =>
        {
            c.SaveLayer(new LayerOptions { Knockout = true, Isolated = isolated });
            c.FillRect(new RectF(0, 0, 25, 20), HalfRed);
            c.FillRect(new RectF(15, 0, 25, 20), HalfBlue);
            c.Restore();
        });

        // Where they overlap only the blue shows, over the white the group started from; elsewhere each shape alone.
        Assert.Equal(new Rgba8(127, 127, 255, 255), Near(drawn.GetPixel(20, 10)));
        Assert.Equal(new Rgba8(255, 127, 127, 255), Near(drawn.GetPixel(5, 10)));
        Assert.Equal(new Rgba8(127, 127, 255, 255), Near(drawn.GetPixel(35, 10)));

        // An ordinary group darkens the overlap.
        using Raster<Rgba8> plain = Pixels.Draw(40, 20, c =>
        {
            c.SaveLayer(new LayerOptions { Isolated = isolated });
            c.FillRect(new RectF(0, 0, 25, 20), HalfRed);
            c.FillRect(new RectF(15, 0, 25, 20), HalfBlue);
            c.Restore();
        });
        Assert.True(plain.GetPixel(20, 10).G < 100);
    }

    [Fact]
    public void ShapesThatDoNotOverlapDrawAsInAnOrdinaryGroup()
    {
        static void Scene(Egami.Drawing.Canvas c, bool knockout)
        {
            c.SaveLayer(new LayerOptions { Knockout = knockout, Opacity = 0.8f });
            c.FillCircle(new Vector2(12.3f, 10.6f), 8.2f, HalfRed);
            c.FillPath(Path.Star(new Vector2(40, 12), 9, 4, 5), new LinearGradient(new Vector2(30, 0), new Vector2(50, 20), HalfBlue, new RgbaF32(0, 1, 0, 1)));
            c.Restore();
        }

        using Raster<Rgba8> knockout = Pixels.Draw(56, 24, c => Scene(c, true)), plain = Pixels.Draw(56, 24, c => Scene(c, false));
        Assert.True(Pixels.MaxDifference(knockout, plain) <= 1);
    }

    [Fact]
    public void ImagesKnockOutToo()
    {
        using Raster<Rgba8> image = Raster.Create(20, 20, new Rgba8(0, 0, 255, 128));
        using Raster<Rgba8> drawn = Pixels.Draw(40, 20, c =>
        {
            c.SaveLayer(new LayerOptions { Knockout = true });
            c.FillRect(new RectF(0, 0, 25, 20), HalfRed);
            c.DrawImage(image, new Vector2(15, 0));
            c.Restore();
        });
        Assert.Equal(new Rgba8(127, 127, 255, 255), Near(drawn.GetPixel(20, 10)));
    }

    [Fact]
    public void AGrayMaskPaintsWhereItIsWhite()
    {
        using Raster<Gray8> mask = Raster.Create<Gray8>(8, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                mask.SetPixel(x, y, new Gray8(255));
            }
        }

        SamplingOptions nearest = new() { Filter = ResampleFilter.Nearest };
        using Raster<Rgba8> drawn = Pixels.Draw(32, 16, c => c.DrawImageMask(mask, new RectF(0, 0, 32, 16), Rgba8.Red, nearest));
        Assert.Equal(Rgba8.Red, drawn.GetPixel(5, 5));
        Assert.Equal(Rgba8.White, drawn.GetPixel(25, 5));
    }

    [Fact]
    public void AnAlphaMaskCoversByItsAlpha()
    {
        using Raster<Rgba8> mask = Raster.Create(4, 1, new Rgba8(0, 0, 0, 64));
        SamplingOptions nearest = new() { Filter = ResampleFilter.Nearest };
        using Raster<Rgba8> drawn = Pixels.Draw(8, 2, c => c.DrawImageMask(mask, new RectF(0, 0, 8, 2), Rgba8.Black, nearest));
        Assert.InRange(drawn.GetPixel(3, 1).R, 190, 192);

        // The default hook, for other backends, cuts the paint out with the mask in a layer.
        using Canvas.TraceCanvas trace = new(16, 16);
        trace.DrawImageMask(mask, new RectF(0, 0, 8, 2), Rgba8.Black);
        Assert.Contains(trace.Calls, c => c.StartsWith("SaveLayer", StringComparison.Ordinal));
        Assert.Contains(trace.Calls, c => c.StartsWith("DrawImage 4x1", StringComparison.Ordinal));
    }

    [Fact]
    public void PicturesSaveKnockoutGroupsAndMasks()
    {
        using Raster<Gray8> mask = Raster.Create(6, 6, new Gray8(255));
        PictureRecorder recorder = new(new RectF(0, 0, 40, 20));
        recorder.SaveLayer(new LayerOptions { Knockout = true, Isolated = false });
        recorder.FillRect(new RectF(0, 0, 25, 20), HalfRed);
        recorder.DrawImageMask(mask, new RectF(15, 0, 20, 20), HalfBlue);
        recorder.Restore();
        Picture picture = recorder.Finish();
        using MemoryStream first = new();
        picture.Save(first);
        Picture loaded = Picture.Load(new MemoryStream(first.ToArray()));
        using MemoryStream second = new();
        loaded.Save(second);
        Assert.Equal(first.ToArray(), second.ToArray());
        using Raster<Rgba8> a = Pixels.Draw(40, 20, picture.Playback), b = Pixels.Draw(40, 20, loaded.Playback);
        Pixels.Equal(a, b);
        Assert.Equal(new Rgba8(127, 127, 255, 255), Near(a.GetPixel(20, 10)));
    }

    /// <summary>Rounds each channel to the nearest of 127 and 128 apart, so 8-bit rounding in either direction compares equal.</summary>
    private static Rgba8 Near(Rgba8 p)
    {
        static byte N(byte v) => v is 128 ? (byte)127 : v;
        return new Rgba8(N(p.R), N(p.G), N(p.B), p.A);
    }
}
