using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

/// <summary>
/// <see cref="InterpolationSpace.GdiPlusGamma"/> against GDI+'s gamma-corrected ramps as measured in egami-compare (2.7.6 task
/// 13): a 256-pixel gradient gives pixel i the value GDI+ draws at index i, within one level.
/// </summary>
public class GdiPlusGradientTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(32, 100)]
    [InlineData(64, 136)]
    [InlineData(96, 163)]
    [InlineData(128, 186)]
    [InlineData(160, 206)]
    [InlineData(192, 224)]
    [InlineData(224, 240)]
    [InlineData(255, 255)]
    public void BlackToWhiteFollowsGdiPlus(int i, int gdiPlus)
    {
        Rgba8[] row = Row(new Rgba8(0, 0, 0, 255), new Rgba8(255, 255, 255, 255));
        Assert.InRange(row[i].G, gdiPlus - 1, gdiPlus + 1);
    }

    [Theory]
    [InlineData(4, 36, 48, 198)]
    [InlineData(16, 74, 68, 194)]
    [InlineData(64, 134, 111, 176)]
    [InlineData(128, 183, 148, 147)]
    [InlineData(200, 224, 179, 102)]
    [InlineData(252, 248, 199, 37)]
    public void ColorsFollowGdiPlus(int i, int r, int g, int b)
    {
        Rgba8 p = Row(new Rgba8(20, 40, 200, 255), new Rgba8(250, 200, 30, 255))[i];
        Assert.InRange(p.R, r - 1, r + 1);
        Assert.InRange(p.G, g - 1, g + 1);
        Assert.InRange(p.B, b - 1, b + 1);
    }

    /// <summary>The first sixteenth runs straight to the first knot, as GDI+'s does: no gamma curve within a part.</summary>
    [Fact]
    public void PartsAreStraightBetweenKnots()
    {
        Rgba8[] row = Row(new Rgba8(0, 0, 0, 255), new Rgba8(255, 255, 255, 255));
        int knot = (int)MathF.Round(MathF.Pow(1 / 16f, 1 / 2.2f) * 255);
        for (int i = 1; i < 15; i++)
        {
            Assert.InRange(row[i].G, (knot * (i + 0.5f) / 16) - 1.5f, (knot * (i + 0.5f) / 16) + 1.5f);
        }
    }

    /// <summary>The mixing survives a recording's round trip through .egdr.</summary>
    [Fact]
    public void RoundTripsThroughEgdr()
    {
        LinearGradient gradient = new(Vector2.Zero, new Vector2(256, 0), new GradientStop(0, Rgba8.Black), new GradientStop(1, Rgba8.White)) { Interpolation = GradientInterpolation.GdiPlusGamma };
        PictureRecorder recorder = new(new RectF(0, 0, 256, 1));
        recorder.DrawPaint(gradient);
        using MemoryStream stream = new();
        recorder.Finish().Save(stream);
        Picture loaded = Picture.Load(new MemoryStream(stream.ToArray()));
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(256, 1);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            loaded.Playback(canvas);
        }

        Assert.Equal(Row(Rgba8.Black, Rgba8.White)[100], raster.GetPixel(100, 0));
    }

    private static Rgba8[] Row(Rgba8 start, Rgba8 end)
    {
        LinearGradient gradient = new(Vector2.Zero, new Vector2(256, 0), new GradientStop(0, start), new GradientStop(1, end)) { Interpolation = GradientInterpolation.GdiPlusGamma };
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(256, 1);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.DrawPaint(gradient);
        }

        return [.. Enumerable.Range(0, 256).Select(x => raster.GetPixel(x, 0))];
    }
}
