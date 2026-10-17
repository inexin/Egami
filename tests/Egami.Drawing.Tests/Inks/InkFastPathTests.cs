using System.Numerics;

using Egami.Colors;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 15: the fast paths for solid fills and images give exactly the values of the general compositor. Overprint
/// On with a CMYK colour takes the general path; over opaque paper with empty spot plates it must print the same bytes.
/// </summary>
public class InkFastPathTests
{
    private static readonly InkSet Set = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", null);

    [Theory]
    [InlineData(1f)]
    [InlineData(0.37f)]
    public void SolidFillsMatchTheGeneralPath(float opacity)
    {
        Random random = new(16);
        List<(Path Path, InkColor Ink, float Alpha)> shapes = [];
        for (int i = 0; i < 60; i++)
        {
            Vector2 c = new((float)(random.NextDouble() * 60), (float)(random.NextDouble() * 40));
            float r = 2 + (float)(random.NextDouble() * 12);
            InkColor ink = InkColor.Cmyk((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble() * 0.5f);
            shapes.Add((i % 2 == 0 ? Path.Circle(c, r) : Path.Star(c, r, r / 2.3f, 5), ink, (float)random.NextDouble()));
        }

        byte[][] fast = Draw(canvas =>
        {
            foreach ((Path path, InkColor ink, float alpha) in shapes)
            {
                canvas.FillPath(path, new SolidPaint(ink) { Opacity = opacity * alpha });
            }
        });
        byte[][] general = Draw(canvas =>
        {
            foreach ((Path path, InkColor ink, float alpha) in shapes)
            {
                canvas.FillPath(path, new SolidPaint(ink) { Opacity = opacity * alpha, Overprint = Overprint.On });
            }
        });

        Assert.Equal(general, fast);
    }

    [Theory]
    [InlineData(ResampleFilter.Bilinear)]
    [InlineData(ResampleFilter.Nearest)]
    public void TurnedImagesMatchTheGeneralPath(ResampleFilter filter)
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(23, 17);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                image.Row(y)[x] = new Rgba8((byte)(x * 11), (byte)(y * 15), (byte)((x ^ y) * 9), (byte)(120 + (x * 5)));
            }
        }

        Matrix3x2 turn = Matrix3x2.CreateRotation(0.4f) * Matrix3x2.CreateScale(1.7f) * Matrix3x2.CreateTranslation(18, 2);
        SamplingOptions sampling = new() { Filter = filter };
        byte[][] fast = Draw(canvas =>
        {
            canvas.SetMatrix(turn);
            canvas.DrawImage(image, Vector2.Zero, sampling, new SolidPaint(Rgba8.Black) { Opacity = 0.8f });
        });
        byte[][] general = Draw(canvas =>
        {
            canvas.SetMatrix(turn);
            canvas.DrawImage(image, Vector2.Zero, sampling, new SolidPaint(Rgba8.Black) { Opacity = 0.8f, Overprint = Overprint.On });
        });

        Assert.Equal(general, fast);
    }

    [Fact]
    public void AClippedClearSetsPaperInsideAndLeavesTheRest()
    {
        // Clear is Source: inside the clip and outside it take the fast path, the edge's partial coverage the general one.
        byte[][] cleared = Draw(canvas =>
        {
            canvas.ClipPath(Path.Circle(new Vector2(32, 24), 15));
            canvas.Clear(InkColor.None);
        });
        byte[][] backdrop = Draw(_ => { });
        Assert.Equal(0, cleared[0][(24 * 64) + 32]);              // inside: paper
        Assert.Equal(backdrop[0][2], cleared[0][2]);              // outside: untouched
        Assert.Equal(255, cleared[^1][(24 * 64) + 32]);           // paper is opaque
    }

    private static byte[][] Draw(Action<InkCanvas> draw)
    {
        using InkRaster plates = new(Set, 64, 48);
        using (InkCanvas canvas = new(plates))
        {
            canvas.Clear(InkColor.None);
            canvas.FillRect(new RectF(0, 0, 64, 48), new SolidPaint(InkColor.Cmyk(0.1f, 0.3f, 0.05f, 0)));
            draw(canvas);
        }

        return [.. plates.Plates.Append(plates.Alpha).Select(p => Enumerable.Range(0, 48).SelectMany(y => p.Row8(y).ToArray()).ToArray())];
    }
}
