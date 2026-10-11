using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// The robustness properties that stand in for a fuzz run in 2.6.1 (it adds no parser): random paths, transforms and coordinates,
/// checked for properties that must hold whatever the input.
/// </summary>
public class PropertyTests
{
    private const int Cases = 150;

    /// <summary>Moving a path by whole pixels moves its pixels exactly.</summary>
    [Fact]
    public void WholePixelTranslationMovesPixelsExactly()
    {
        Random random = new(1);
        for (int i = 0; i < Cases; i++)
        {
            Path path = RandomPath(random, 40);
            int dx = random.Next(-10, 11), dy = random.Next(-10, 11);
            Stroke? stroke = random.Next(3) == 0 ? new Stroke(1 + (float)(random.NextDouble() * 5)) { Join = (LineJoin)random.Next(3), Cap = (LineCap)random.Next(3) } : null;
            byte[,] a = Draw(path, stroke, 64, Matrix3x2.CreateTranslation(12, 12));
            byte[,] b = Draw(path, stroke, 64, Matrix3x2.CreateTranslation(12 + dx, 12 + dy));
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int sx = x - dx, sy = y - dy;
                    if (sx is >= 0 and < 64 && sy is >= 0 and < 64)
                    {
                        Assert.True(a[sy, sx] == b[y, x], $"case {i}: ({x}, {y}) {b[y, x]} against ({sx}, {sy}) {a[sy, sx]}");
                    }
                }
            }
        }
    }

    /// <summary>On a simple polygon (one contour that never crosses itself) the fill rules agree.</summary>
    [Fact]
    public void FillRulesAgreeOnSimplePaths()
    {
        Random random = new(2);
        for (int i = 0; i < Cases; i++)
        {
            // A star-shaped polygon: points sorted by angle around a center never cross.
            int n = 3 + random.Next(20);
            Vector2[] points = new Vector2[n];
            double[] angles = [.. Enumerable.Range(0, n).Select(_ => random.NextDouble() * 2 * Math.PI).Order()];
            for (int k = 0; k < n; k++)
            {
                double r = 5 + (random.NextDouble() * 25);
                points[k] = new Vector2(32 + (float)(r * Math.Cos(angles[k])), 32 + (float)(r * Math.Sin(angles[k])));
            }

            Path polygon = Path.Polygon(points);
            Assert.Equal(Draw(polygon, null, 64, Matrix3x2.Identity), Draw(polygon.WithFillRule(FillRule.EvenOdd), null, 64, Matrix3x2.Identity));
        }
    }

    /// <summary>Huge, tiny, degenerate and far-away coordinates draw without throwing, and the drawing stays inside the raster.</summary>
    [Fact]
    public void ExtremeCoordinatesNeverThrow()
    {
        Random random = new(3);
        float[] magnitudes = [0, 1e-30f, 1e-6f, 0.5f, 3e4f, 9e6f, 1e12f, 3e38f];
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(48, 48);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        for (int i = 0; i < Cases * 4; i++)
        {
            float Next() => magnitudes[random.Next(magnitudes.Length)] * (random.Next(2) == 0 ? -1 : 1) + (float)(random.NextDouble() * 48);
            PathBuilder builder = new PathBuilder().MoveTo(Next(), Next());
            for (int k = 0; k < 1 + random.Next(5); k++)
            {
                switch (random.Next(3))
                {
                    case 0:
                        builder.LineTo(Next(), Next());
                        break;
                    case 1:
                        builder.QuadTo(Next(), Next(), Next(), Next());
                        break;
                    default:
                        builder.CubicTo(Next(), Next(), Next(), Next(), Next(), Next());
                        break;
                }
            }

            Path path = builder.Close().ToPath(random.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd);
            canvas.Save();
            if (random.Next(3) == 0)
            {
                canvas.Scale(magnitudes[random.Next(magnitudes.Length)] + 0.001f);
            }

            canvas.FillPath(path, new Rgba8(10, 20, 30, 200));
            canvas.StrokePath(path, new Stroke((float)(random.NextDouble() * 4)) { Join = (LineJoin)random.Next(3), Cap = (LineCap)random.Next(3) }, Rgba8.Red);
            canvas.ClipPath(path, (ClipOp)random.Next(2));
            canvas.DrawPaint(new Rgba8(0, 0, 0, 10));
            canvas.Restore();
        }
    }

    /// <summary>Random scenes give the same pixels on one band as on many.</summary>
    [Fact]
    public void BandsNeverChangeTheResult()
    {
        Random random = new(4);
        for (int i = 0; i < 30; i++)
        {
            Path path = RandomPath(random, 200);
            Stroke stroke = new(1 + (float)(random.NextDouble() * 9)) { Join = (LineJoin)random.Next(3) };
            Matrix3x2 m = Matrix3x2.CreateRotation((float)random.NextDouble()) * Matrix3x2.CreateTranslation(100, 100);
            Assert.Equal(Draw(path, stroke, 300, m, bands: 1), Draw(path, stroke, 300, m, bands: 2 + random.Next(30)));
        }
    }

    /// <summary>Contains agrees with an aliased fill: a pixel center is covered exactly when the path contains it.</summary>
    [Fact]
    public void ContainsAgreesWithAliasedFills()
    {
        Random random = new(5);
        for (int i = 0; i < 60; i++)
        {
            Path path = RandomPath(random, 30).Transform(Matrix3x2.CreateTranslation(32, 32));
            using Raster<Gray8> raster = Raster.Create<Gray8>(64, 64);
            using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
            {
                canvas.FillPath(path, new SolidPaint(Rgba8.White) { Antialias = false });
            }

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    Vector2 center = new(x + 0.5f, y + 0.5f);
                    // Curves are flattened to 0.1 pixel, so centers that close to the outline may land either side.
                    bool near = path.Contours.Any(contour =>
                    {
                        Geometry.PathTests.PolygonContains(Geometry.PathTests.Flatten(contour, 400), center, FillRule.NonZero, out double distance);
                        return distance < 0.15;
                    });
                    if (!near)
                    {
                        Assert.True(path.Contains(center) == (raster.GetPixel(x, y).L == 255), $"case {i} ({x}, {y})");
                    }
                }
            }
        }
    }

    internal static Path RandomPath(Random random, float size)
    {
        PathBuilder builder = new();
        int contours = 1 + random.Next(3);
        for (int c = 0; c < contours; c++)
        {
            Vector2 Next() => new((float)((random.NextDouble() - 0.5) * size), (float)((random.NextDouble() - 0.5) * size));
            builder.MoveTo(Next());
            for (int k = 0; k < 2 + random.Next(6); k++)
            {
                switch (random.Next(4))
                {
                    case 0:
                        builder.QuadTo(Next(), Next());
                        break;
                    case 1:
                        builder.CubicTo(Next(), Next(), Next());
                        break;
                    default:
                        builder.LineTo(Next());
                        break;
                }
            }

            if (random.Next(4) != 0)
            {
                builder.Close();
            }
        }

        return builder.ToPath(random.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd);
    }

    private static byte[,] Draw(Path path, Stroke? stroke, int size, Matrix3x2 matrix, int bands = 1)
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(size, size);
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.ForcedBands = bands;
            canvas.SetMatrix(matrix);
            if (stroke is null)
            {
                canvas.FillPath(path, Rgba8.White);
            }
            else
            {
                canvas.StrokePath(path, stroke, Rgba8.White);
            }
        }

        byte[,] result = new byte[size, size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                result[y, x] = raster.GetPixel(x, y).L;
            }
        }

        return result;
    }
}
