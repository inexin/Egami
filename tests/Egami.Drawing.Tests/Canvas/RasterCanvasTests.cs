using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

public class RasterCanvasTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AFilledRectangleBlendsItsCoverage()
    {
        using Raster<Rgba8> raster = Raster.Create(10, 10, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(2, 2, 4.5f, 3), Rgba8.Black);
        }

        Assert.Equal(Rgba8.Black, raster.GetPixel(3, 3));
        Assert.Equal(Rgba8.White, raster.GetPixel(1, 3));
        Rgba8 half = raster.GetPixel(6, 3);
        Assert.InRange(half.R, 126, 129);
        Assert.Equal(255, half.A);
    }

    [Fact]
    public void BlendModesOpacityAndLinearLightReachTheCompositor()
    {
        using Raster<Rgba8> raster = Raster.Create(4, 1, new Rgba8(200, 100, 50, 255));
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 4, 1), new SolidPaint(new Rgba8(100, 100, 100, 255)) { BlendMode = BlendMode.Multiply, Opacity = 0.5f });
        }

        Rgba8[] expectedRow = [new Rgba8(200, 100, 50, 255)];
        Compositor.Blend<Rgba8>(BlendMode.Multiply, [new Rgba8(100, 100, 100, 255)], expectedRow, 0.5f);
        Rgba8 expected = expectedRow[0], actual = raster.GetPixel(1, 0);
        Assert.InRange(actual.R, expected.R - 1, expected.R + 1);
        Assert.InRange(actual.G, expected.G - 1, expected.G + 1);

        // Half coverage in linear light gives a lighter edge than in encoded values.
        using Raster<Rgba8> encoded = Raster.Create(4, 1, Rgba8.White), linear = Raster.Create(4, 1, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = encoded.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 0.5f, 1), Rgba8.Black);
        }

        using (RasterCanvas<Rgba8> canvas = linear.CreateCanvas(new CanvasOptions { ColorSpace = ColorSpace.Linear }))
        {
            canvas.FillRect(new RectF(0, 0, 0.5f, 1), Rgba8.Black);
        }

        Assert.InRange(encoded.GetPixel(0, 0).R, 126, 129);
        Assert.InRange(linear.GetPixel(0, 0).R, 180, 194);
    }

    [Fact]
    public void EveryPixelTypeDraws()
    {
        Check<Gray8>();
        Check<Gray16>();
        Check<RgbaF32>();
        Check<PremulBgra8>();
        Check<PremulRgbaF32>();
        Check<Bgr565>();
        Check<Cmyk8>();

        static void Check<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            using Raster<TPixel> raster = Raster.Create<TPixel>(12, 12);
            using (RasterCanvas<TPixel> canvas = raster.CreateCanvas())
            {
                canvas.Clear(Rgba8.White);
                canvas.FillCircle(new Vector2(6, 6), 4, Rgba8.Black);
            }

            Rgba8 center = raster.GetPixel(6, 6).ToRgba8(), corner = raster.GetPixel(0, 0).ToRgba8();
            Assert.True(center.R < 10, $"{typeof(TPixel).Name} center {center}");
            Assert.True(corner.R > 245, $"{typeof(TPixel).Name} corner {corner}");
        }
    }

    [Fact]
    public void FloatTargetsGetFullPrecisionColors()
    {
        using Raster<RgbaF32> raster = Raster.Create<RgbaF32>(4, 4);
        using (RasterCanvas<RgbaF32> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 4, 4), new RgbaF32(0.123456f, 0.5f, 0.75f, 1));
        }

        Assert.Equal(0.123456f, raster.GetPixel(1, 1).R, 5);
    }

    [Fact]
    public void ResultsAreIdenticalAtAnyBandCount()
    {
        Path path = new PathBuilder().AddCircle(new Vector2(150, 150), 120).AddPath(Path.Star(new Vector2(150, 150), 140, 40, 11)).ToPath(FillRule.EvenOdd);
        byte[] one = Draw(1), many = Draw(9), auto = Draw(0);
        Assert.Equal(one, many);
        Assert.Equal(one, auto);

        byte[] Draw(int bands)
        {
            using Raster<Rgba8> raster = Raster.Create(300, 300, Rgba8.White);
            using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
            {
                canvas.ForcedBands = bands;
                canvas.FillPath(path, new SolidPaint(new Rgba8(20, 80, 200, 180)));
                canvas.StrokePath(path, new Stroke(7) { Join = LineJoin.Round }, new Rgba8(200, 20, 20, 255));
            }

            byte[] bytes = new byte[300 * 300 * 4];
            raster.CopyPixelsTo(bytes);
            return bytes;
        }
    }

    [Fact]
    public void AntialiasOffDrawsOnlyWholePixels()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(20, 20);
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(10, 10), 6.3f, new SolidPaint(Rgba8.White) { Antialias = false });
        }

        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                byte v = raster.GetPixel(x, y).L;
                Assert.True(v is 0 or 255);
                bool inside = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(10, 10)) < 6.25f;
                if (inside)
                {
                    Assert.Equal(255, v);
                }
            }
        }
    }

    [Fact]
    public void RectClipsMatchACropExactly()
    {
        Path star = Path.Star(new Vector2(20, 20), 18, 7, 7, 3);
        using Raster<Rgba8> whole = Raster.Create(40, 40, Rgba8.White), clipped = Raster.Create(40, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = whole.CreateCanvas())
        {
            canvas.FillPath(star, new Rgba8(0, 0, 0, 200));
        }

        using (RasterCanvas<Rgba8> canvas = clipped.CreateCanvas())
        {
            canvas.Translate(2, 0);
            canvas.ClipRect(new RectF(5, 7, 20, 15));
            canvas.Translate(-2, 0);
            canvas.FillPath(star, new Rgba8(0, 0, 0, 200));
        }

        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                bool inside = x >= 7 && x < 27 && y >= 7 && y < 22;
                Assert.Equal(inside ? whole.GetPixel(x, y) : Rgba8.White, clipped.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void IntersectAndDifferenceOfOnePathMakeTheWhole()
    {
        Path clip = Path.Circle(new Vector2(16.3f, 15.8f), 11.2f);
        using Raster<RgbaF32> inside = Raster.Create<RgbaF32>(32, 32), outside = Raster.Create<RgbaF32>(32, 32);
        using (RasterCanvas<RgbaF32> canvas = inside.CreateCanvas())
        {
            canvas.ClipPath(clip);
            canvas.DrawPaint(Rgba8.White);
        }

        using (RasterCanvas<RgbaF32> canvas = outside.CreateCanvas())
        {
            canvas.ClipPath(clip, ClipOp.Difference);
            canvas.DrawPaint(Rgba8.White);
        }

        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                Assert.InRange(inside.GetPixel(x, y).A + outside.GetPixel(x, y).A, 1 - (1.5f / 255), 1 + (1.5f / 255));
            }
        }
    }

    [Fact]
    public void ClipsStackAndRestore()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(30, 30);
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            canvas.Save();
            canvas.ClipPath(Path.Circle(new Vector2(15, 15), 10));
            canvas.Save();
            canvas.ClipRect(new RectF(0, 0, 15, 30));
            canvas.ClipRoundRect(new RoundRect(new RectF(0, 10, 30, 10), 3), ClipOp.Difference);
            canvas.DrawPaint(Rgba8.White);
            canvas.Restore();
            canvas.FillRect(new RectF(15, 0, 15, 30), new Rgba8(128, 128, 128, 255));
            canvas.Restore();
            canvas.FillRect(new RectF(0, 0, 2, 2), Rgba8.White);
        }

        Assert.Equal(255, raster.GetPixel(10, 7).L);
        Assert.Equal(0, raster.GetPixel(10, 15).L);
        Assert.Equal(0, raster.GetPixel(20, 2).L);
        Assert.Equal(128, raster.GetPixel(20, 15).L);
        Assert.Equal(255, raster.GetPixel(1, 1).L);
    }

    [Fact]
    public void IdentityImagesDrawExactlyAsDrawImage()
    {
        using Raster<Rgba8> image = Pattern(13, 9);
        foreach (BlendMode mode in new[] { BlendMode.SourceOver, BlendMode.Multiply, BlendMode.Xor })
        {
            using Raster<Rgba8> expected = Pattern(30, 20, 77), actual = Pattern(30, 20, 77);
            expected.Apply(ops => ops.DrawImage(image, new Point(5, 4), mode, 0.75f), Ct);
            using (RasterCanvas<Rgba8> canvas = actual.CreateCanvas())
            {
                canvas.Translate(2, 1);
                canvas.DrawImage(image, new Vector2(3, 3), paint: new SolidPaint(Rgba8.Black) { BlendMode = mode, Opacity = 0.75f });
            }

            Assert.Equal(Bytes(expected), Bytes(actual));
        }
    }

    [Fact]
    public void TransformedImagesMatchTheTransformStep()
    {
        using Raster<Rgba8> image = Pattern(16, 12);
        Matrix3x2 m = Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateScale(1.7f, 1.3f) * Matrix3x2.CreateTranslation(14, 3);
        using Raster<Rgba8> drawn = Raster.Create<Rgba8>(48, 40);
        using (RasterCanvas<Rgba8> canvas = drawn.CreateCanvas())
        {
            canvas.SetMatrix(m);
            canvas.DrawImage(image, Vector2.Zero, new SamplingOptions { Filter = ResampleFilter.Bilinear });
        }

        using Raster<Rgba8> transformed = image.Derive(ops => ops.Transform(m, new TransformOptions { Filter = ResampleFilter.Bilinear, Edges = EdgeMode.Clamp, Viewport = new Rect(0, 0, 48, 40) }), Ct);

        // Inside the image's outline (coverage 255), the canvas shows the transform's pixels, to a level: the transform runs over
        // only the part of the canvas the image reaches, whose offset changes the sampler's rounding.
        Path outline = Path.Rect(new RectF(0, 0, 16, 12)).Transform(m);
        int compared = 0;
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                if (outline.Contains(new Vector2(x, y)) && outline.Contains(new Vector2(x + 1, y)) && outline.Contains(new Vector2(x, y + 1)) && outline.Contains(new Vector2(x + 1, y + 1)))
                {
                    Rgba8 t = transformed.GetPixel(x, y), d = drawn.GetPixel(x, y);
                    Assert.True(Math.Abs(t.R - d.R) <= 1 && Math.Abs(t.G - d.G) <= 1 && Math.Abs(t.B - d.B) <= 1 && Math.Abs(t.A - d.A) <= 1, $"({x}, {y}): {t} {d}");
                    compared++;
                }
            }
        }

        Assert.True(compared > 150);
    }

    [Fact]
    public void ImagesHonorSourceRectanglesAndClips()
    {
        using Raster<Rgba8> image = Pattern(20, 20);
        using Raster<Rgba8> raster = Raster.Create(20, 20, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.ClipPath(Path.Circle(new Vector2(10, 10), 6));
            canvas.DrawImage(image, new RectF(5, 5, 10, 10), new RectF(0, 0, 20, 20), new SamplingOptions { Filter = ResampleFilter.Nearest }, new SolidPaint(Rgba8.Black) { BlendMode = BlendMode.Source });
        }

        Assert.Equal(Rgba8.White, raster.GetPixel(1, 1));
        // Source replaces the pixel; the premultiplied blend rounds a translucent color by up to a level.
        Rgba8 e = image.GetPixel(10, 10), a = raster.GetPixel(10, 10);
        Assert.True(Math.Abs(e.R - a.R) <= 1 && Math.Abs(e.G - a.G) <= 1 && Math.Abs(e.B - a.B) <= 1 && e.A == a.A, $"{e} {a}");

        // A different pixel type is converted.
        using Raster<Gray8> gray = Raster.Create<Gray8>(4, 4, new Gray8(100));
        using Raster<Rgba8> target = Raster.Create<Rgba8>(4, 4);
        using (RasterCanvas<Rgba8> canvas = target.CreateCanvas())
        {
            canvas.DrawImage(gray, Vector2.Zero);
        }

        Assert.Equal(new Rgba8(100, 100, 100, 255), target.GetPixel(2, 2));
    }

    [Fact]
    public void TheDrawStepWorksInsideWithinAndPerFrame()
    {
        using Raster<Rgba8> raster = Raster.Create(20, 20, Rgba8.White);
        raster.Apply(ops => ops.Within(new Rect(10, 10, 10, 10), inner => inner.Draw(c => c.FillRect(new RectF(0, 0, 5, 5), Rgba8.Black))), Ct);
        Assert.Equal(Rgba8.Black, raster.GetPixel(12, 12));
        Assert.Equal(Rgba8.White, raster.GetPixel(2, 2));
        Assert.Equal(20, raster.Width);

        using Raster<Rgba8> blurred = Raster.Create(20, 20, Rgba8.White);
        blurred.Apply(ops => ops.Draw(c => c.FillRect(new RectF(0, 0, 10, 20), Rgba8.Black)).Draw(c => c.FillRect(new RectF(0, 0, 5, 5), Rgba8.Red)), Ct);
        Assert.Equal(Rgba8.Red, blurred.GetPixel(1, 1));
        Assert.Equal(Rgba8.Black, blurred.GetPixel(7, 7));

        using Raster<Rgba8> animation = Raster.Create(10, 10, Rgba8.White);
        animation.Frames.Add(animation.Frames[0]);
        animation.Apply(ops => ops.Draw((c, frame) => c.FillRect(new RectF(frame * 5, 0, 5, 5), Rgba8.Black)), Ct);
        Assert.Equal(Rgba8.Black, animation.Frames[0].Row(1)[1]);
        Assert.Equal(Rgba8.White, animation.Frames[0].Row(1)[6]);
        Assert.Equal(Rgba8.Black, animation.Frames[1].Row(1)[6]);
    }

    internal static Raster<Rgba8> Pattern(int width, int height, int seed = 3)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new(seed);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(128 + random.Next(128)));
            }
        }

        return raster;
    }

    internal static byte[] Bytes(Raster<Rgba8> raster)
    {
        byte[] bytes = new byte[raster.Width * raster.Height * 4];
        raster.CopyPixelsTo(bytes);
        return bytes;
    }
}
