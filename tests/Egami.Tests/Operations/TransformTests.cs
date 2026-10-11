using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class TransformTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(17f, ResampleFilter.Bicubic)]
    [InlineData(-33f, ResampleFilter.Lanczos3)]
    [InlineData(200f, ResampleFilter.Bilinear)]
    [InlineData(90f, ResampleFilter.Bicubic)]
    [InlineData(4.5f, ResampleFilter.Nearest)]
    public void PureRotationEqualsRotate(float degrees, ResampleFilter filter)
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(97, 61, EgamiContext.Default);
        using Raster<Rgba8> rotated = raster.Derive(ops => ops.Rotate(degrees, filter, Rgba8.Red), Ct);
        Matrix3x2 matrix = Matrix3x2.CreateRotation(degrees * MathF.PI / 180, new Vector2(10, 300));
        using Raster<Rgba8> transformed = raster.Derive(ops => ops.Transform(matrix, new TransformOptions { Filter = filter, Background = Rgba8.Red }), Ct);
        AssertSame(rotated, transformed);
    }

    [Theory]
    [InlineData(0.4f, 0.4f)]
    [InlineData(2.5f, 1.25f)]
    [InlineData(0.3f, 3f)]
    public void PureScaleEqualsResize(float sx, float sy)
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(120, 80, EgamiContext.Default);
        using Raster<Rgba8> transformed = raster.Derive(ops => ops.Transform(Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(7, -3), new TransformOptions { Filter = ResampleFilter.Mitchell }), Ct);
        using Raster<Rgba8> resized = raster.Derive(ops => ops.Resize((int)Math.Round(120 * sx), (int)Math.Round(80 * sy), ResampleFilter.Mitchell), Ct);
        AssertSame(resized, transformed);
    }

    [Fact]
    public void ScaleWithAViewportInsideTheImageEqualsResizeThenCrop()
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(60, 40, EgamiContext.Default);
        TransformOptions options = new() { Viewport = new Rect(10, 6, 60, 40) };
        using Raster<Rgba8> transformed = raster.Derive(ops => ops.Transform(Matrix3x2.CreateScale(2), options), Ct);
        using Raster<Rgba8> resized = raster.Derive(ops => ops.Resize(120, 80).Crop(10, 6, 60, 40), Ct);
        AssertSame(resized, transformed);
    }

    [Fact]
    public void CanvasIsTheRoundedBoundingBox()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(100, 50);
        Assert.Equal(new Size(111, 50), SizeAfter(raster, ops => ops.Skew(12, 0)));
        Assert.Equal(new Size(100, 71), SizeAfter(raster, ops => ops.Skew(0, -12)));
        Assert.Equal(new Size(112, 93), SizeAfter(raster, ops => ops.Transform(Matrix3x2.CreateRotation(MathF.PI / 6) * Matrix3x2.CreateTranslation(500, 500))));
        Assert.Equal(new Size(30, 20), SizeAfter(raster, ops => ops.Transform(Matrix3x2.CreateScale(2), new TransformOptions { Viewport = new Rect(-5, 5, 30, 20) })));
    }

    [Fact]
    public void NearestPointMappingFollowsTheInverseMatrix()
    {
        using Raster<Rgba8> coded = Coded(64, 48);
        Matrix3x2 matrix = Matrix3x2.CreateRotation(0.5f) * Matrix3x2.CreateScale(1.3f, 0.8f) * Matrix3x2.CreateTranslation(20, -10);
        Rect viewport = new(-10, 0, 80, 70);
        using Raster<Rgba8> result = coded.Derive(ops => ops.Transform(matrix, new TransformOptions { Filter = ResampleFilter.Nearest, Viewport = viewport, Background = new Rgba8(0, 0, 0, 0) }), Ct);
        Assert.True(Homography.FromAffine(matrix).TryInvert(out Homography inverse));
        int checkedPixels = 0;
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                (double u, double v) = inverse.Map(x + 0.5 + viewport.X, y + 0.5 + viewport.Y);
                u -= 0.5;
                v -= 0.5;

                // Skip ties at exact half pixels, where float and double round differently.
                if (Math.Abs(u - Math.Floor(u) - 0.5) < 1e-4 || Math.Abs(v - Math.Floor(v) - 0.5) < 1e-4)
                {
                    continue;
                }

                int sx = (int)Math.Floor(u + 0.5), sy = (int)Math.Floor(v + 0.5);
                Rgba8 expected = sx >= 0 && sy >= 0 && sx < 64 && sy < 48 ? coded.GetPixel(sx, sy) : default;
                Assert.Equal(expected, result.GetPixel(x, y));
                checkedPixels++;
            }
        }

        Assert.True(checkedPixels > 5000);
    }

    [Fact]
    public void QuadDistortSendsCornersToItsPoints()
    {
        using Raster<Rgba8> coded = Coded(64, 48);
        Vector2[] points = [new(10, 5), new(90, 15), new(80, 70), new(0, 60)];
        Rect viewport = new(0, 0, 100, 80);
        using Raster<Rgba8> result = coded.Derive(ops => ops.QuadDistort(points[0], points[1], points[2], points[3], new TransformOptions { Filter = ResampleFilter.Nearest, Viewport = viewport }), Ct);
        Homography h = Homography.FromQuad(new Rect(0, 0, 64, 48), points);
        Assert.True(h.TryInvert(out Homography inverse));
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                (double u, double v) = inverse.Map(x + 0.5, y + 0.5);
                u -= 0.5;
                v -= 0.5;
                if (Math.Abs(u - Math.Floor(u) - 0.5) < 1e-4 || Math.Abs(v - Math.Floor(v) - 0.5) < 1e-4)
                {
                    continue;
                }

                int sx = (int)Math.Floor(u + 0.5), sy = (int)Math.Floor(v + 0.5);
                Rgba8 expected = sx >= 0 && sy >= 0 && sx < 64 && sy < 48 ? coded.GetPixel(sx, sy) : default;
                Assert.Equal(expected, result.GetPixel(x, y));
            }
        }

        // The image's top-left pixel lands next to the first point.
        Assert.Equal(coded.GetPixel(0, 0), result.GetPixel(10, 5));
    }

    [Fact]
    public void WrapAtWholePixelsRollsTheImage()
    {
        using Raster<Rgba8> raster = Opaque(OperationCoverageTests.Noise(31, 23, EgamiContext.Default));
        TransformOptions options = new() { Viewport = new Rect(0, 0, 31, 23), Edges = EdgeMode.Wrap };
        using Raster<Rgba8> rolled = raster.Derive(ops => ops.Transform(Matrix3x2.CreateTranslation(10, -7), options), Ct);
        for (int y = 0; y < 23; y++)
        {
            for (int x = 0; x < 31; x++)
            {
                Assert.Equal(raster.GetPixel((x - 10 + 31) % 31, (y + 7) % 23), rolled.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void MirrorAndClampExtendTheImage()
    {
        using Raster<Rgba8> raster = Opaque(OperationCoverageTests.Noise(20, 10, EgamiContext.Default));
        using Raster<Rgba8> mirrored = raster.Derive(ops => ops.Transform(Matrix3x2.CreateTranslation(3, 0), new TransformOptions { Viewport = new Rect(0, 0, 20, 10), Edges = EdgeMode.Mirror }), Ct);
        using Raster<Rgba8> clamped = raster.Derive(ops => ops.Transform(Matrix3x2.CreateTranslation(3, 0), new TransformOptions { Viewport = new Rect(0, 0, 20, 10), Edges = EdgeMode.Clamp }), Ct);
        for (int y = 0; y < 10; y++)
        {
            Assert.Equal(raster.GetPixel(2, y), mirrored.GetPixel(0, y));
            Assert.Equal(raster.GetPixel(0, y), mirrored.GetPixel(2, y));
            Assert.Equal(raster.GetPixel(0, y), clamped.GetPixel(0, y));
            Assert.Equal(raster.GetPixel(0, y), clamped.GetPixel(1, y));
        }
    }

    [Fact]
    public void ShrinkingAveragesFineDetailInsteadOfAliasing()
    {
        // A one-pixel checkerboard shrunk to a quarter and turned: EWA averages each footprint to mid-gray, where a plain kernel
        // would pick stripes and moiré out of it.
        using Raster<Rgba8> checker = Raster.Create<Rgba8>(400, 300);
        for (int y = 0; y < 300; y++)
        {
            Span<Rgba8> row = checker.Row(y);
            for (int x = 0; x < 400; x++)
            {
                row[x] = ((x + y) & 1) == 0 ? Rgba8.White : Rgba8.Black;
            }
        }

        Matrix3x2 matrix = Matrix3x2.CreateRotation(0.2f) * Matrix3x2.CreateScale(0.25f);
        using Raster<Rgba8> small = checker.Derive(ops => ops.Transform(matrix, new TransformOptions { Edges = EdgeMode.Wrap }), Ct);
        (double mean, double deviation) = Gray(small, inset: 2);
        Assert.InRange(mean, 120, 135);
        Assert.True(deviation < 4, $"deviation {deviation}");
    }

    [Fact]
    public void PerspectiveTowardTheHorizonStaysSmooth()
    {
        using Raster<Rgba8> checker = Raster.Create<Rgba8>(512, 512);
        for (int y = 0; y < 512; y++)
        {
            Span<Rgba8> row = checker.Row(y);
            for (int x = 0; x < 512; x++)
            {
                row[x] = (((x / 2) + (y / 2)) & 1) == 0 ? Rgba8.White : Rgba8.Black;
            }
        }

        // The top edge shrinks to a tenth of the bottom's width: far rows average dozens of checks.
        using Raster<Rgba8> plane = checker.Derive(ops => ops.QuadDistort(new(230, 0), new(282, 0), new(512, 200), new(0, 200), new TransformOptions { Viewport = new Rect(0, 0, 512, 200) }), Ct);
        using Raster<Rgba8> top = plane.Derive(ops => ops.Crop(240, 2, 32, 20), Ct);
        (double mean, double deviation) = Gray(top, inset: 0);
        Assert.InRange(mean, 110, 145);
        Assert.True(deviation < 12, $"deviation {deviation}");
    }

    [Fact]
    public void LinearLightKeepsFlatColors()
    {
        Rgba8 color = new(200, 40, 90, 255);
        using Raster<Rgba8> raster = Raster.Create(60, 40, color);
        using Raster<Rgba8> result = raster.Derive(ops => ops.Transform(Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateScale(0.5f), new TransformOptions { ColorSpace = ColorSpace.Linear }), Ct);
        Rgba8 center = result.GetPixel(result.Width / 2, result.Height / 2);
        Assert.True(Math.Abs(center.R - color.R) <= 1 && Math.Abs(center.G - color.G) <= 1 && Math.Abs(center.B - color.B) <= 1 && center.A == 255, center.ToString());
    }

    [Fact]
    public void BadArgumentsThrowWhenRecorded()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(10, 10);
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Transform(Matrix3x2.CreateScale(0, 1)), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Transform(new Matrix3x2(float.NaN, 0, 0, 1, 0, 0)), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Transform(Matrix3x2.Identity, new TransformOptions { Viewport = new Rect(0, 0, 0, 5) }), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Skew(90, 0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Skew(float.NaN, 0), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Skew(45, 45), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.QuadDistort(new(0, 0), new(1, 1), new(2, 2), new(0, 5)), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransformOptions { Filter = (ResampleFilter)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransformOptions { Edges = (EdgeMode)99 });
    }

    [Fact]
    public void ACornerBehindTheViewerNeedsAViewport()
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(40, 40, EgamiContext.Default);
        Homography h = new(1, 0, -0.03, 0, 1, 0, 0, 0, 1);
        ArgumentException e = Assert.Throws<ArgumentException>(() => raster.Derive(ops => ops.Transform(h), Ct).Dispose());
        Assert.Contains("top-right", e.Message, StringComparison.Ordinal);

        // With a viewport it runs. Left of x = −33.3 the destination is reached only from behind the viewer, so it is background;
        // right of it the stretched image runs on to infinity.
        using Raster<Rgba8> result = raster.Derive(ops => ops.Transform(h, new TransformOptions { Viewport = new Rect(-60, 0, 160, 40), Background = Rgba8.Blue }), Ct);
        Assert.Equal(Rgba8.Blue, result.GetPixel(0, 20));
        Assert.Equal(Rgba8.Blue, result.GetPixel(25, 20));
        Assert.NotEqual(Rgba8.Blue, result.GetPixel(159, 20));
    }

    [Fact]
    public void ASameSizeTransformRunsInsideAScope()
    {
        using Raster<Rgba8> raster = Opaque(OperationCoverageTests.Noise(50, 40, EgamiContext.Default));
        using Raster<Rgba8> before = raster.Copy();
        Rect area = new(10, 10, 20, 16);
        raster.Apply(ops => ops.Within(area, s => s.Transform(Matrix3x2.CreateRotation(MathF.PI, new Vector2(10, 8)), new TransformOptions { Viewport = new Rect(0, 0, 20, 16) })), Ct);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                Rgba8 expected = area.Contains(new Point(x, y)) ? before.GetPixel(area.X + area.Right - 1 - x, area.Y + area.Bottom - 1 - y) : before.GetPixel(x, y);
                Assert.Equal(expected, raster.GetPixel(x, y));
            }
        }

        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(area, s => s.Skew(10, 0)), Ct));
    }

    [Fact]
    public void RandomMatricesComposeLikeOneTransform()
    {
        // Transform by A then B against one transform by A·B, both with the canvas kept, within the closeness gate.
        using Raster<Rgba8> photo = Smooth(96, 72);
        Random random = new(7);
        for (int i = 0; i < 12; i++)
        {
            Matrix3x2 a = Matrix3x2.CreateRotation((float)(random.NextDouble() - 0.5), new Vector2(48, 36)) * Matrix3x2.CreateScale(0.9f + (float)(random.NextDouble() * 0.3), new Vector2(48, 36));
            Matrix3x2 b = Matrix3x2.CreateSkew((float)(random.NextDouble() - 0.5) * 0.3f, 0, new Vector2(48, 36)) * Matrix3x2.CreateTranslation((float)random.NextDouble() * 4, 0);
            TransformOptions keep = new() { Viewport = new Rect(0, 0, 96, 72), Edges = EdgeMode.Clamp };
            using Raster<Rgba8> twice = photo.Derive(ops => ops.Transform(a, keep).Transform(b, keep), Ct);
            using Raster<Rgba8> once = photo.Derive(ops => ops.Transform(a * b, keep), Ct);
            double psnr = Psnr(once, twice, inset: 6);
            Assert.True(psnr >= 35, $"case {i}: PSNR {psnr:0.0} dB");
        }
    }

    [Fact]
    public void MetadataFollowsTheTransform()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(100, 50);
        raster.Metadata.Exif = new Egami.Metadata.ExifData();
        raster.Metadata.Exif.Orientation = Egami.Metadata.ImageOrientation.Rotate90;
        raster.Apply(ops => ops.Skew(10, 0), Ct);
        Assert.Equal(Egami.Metadata.ImageOrientation.Normal, raster.Metadata.Exif!.Orientation);
    }

    [Fact]
    public void EveryFrameGetsTheSameMatrix()
    {
        using Raster<Rgba8> animation = Raster.Create<Rgba8>(30, 20);
        animation.Frames.Add(animation.Frames[0]);
        animation.Apply(ops => ops.Skew(15, 0), Ct);
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(new Size(35, 20), new Size(animation.Width, animation.Height));
    }

    internal static Raster<Rgba8> Coded(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)x, (byte)y, (byte)((x * 7) ^ (y * 13)), 255);
            }
        }

        return raster;
    }

    internal static Raster<Rgba8> Smooth(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8(
                    (byte)(128 + (100 * Math.Sin(x * 0.11))),
                    (byte)(128 + (100 * Math.Cos(y * 0.09))),
                    (byte)(128 + (90 * Math.Sin((x + y) * 0.05))),
                    255);
            }
        }

        return raster;
    }

    private static Raster<Rgba8> Opaque(Raster<Rgba8> raster)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = row[x] with { A = 255 };
            }
        }

        return raster;
    }

    private static Size SizeAfter(Raster<Rgba8> raster, Action<RasterOps> chain)
    {
        using Raster<Rgba8> result = raster.Derive(chain, Ct);
        return new Size(result.Width, result.Height);
    }

    private static (double Mean, double Deviation) Gray(Raster<Rgba8> raster, int inset)
    {
        double sum = 0, squares = 0;
        int count = 0;
        for (int y = inset; y < raster.Height - inset; y++)
        {
            for (int x = inset; x < raster.Width - inset; x++)
            {
                double g = raster.GetPixel(x, y).G;
                sum += g;
                squares += g * g;
                count++;
            }
        }

        double mean = sum / count;
        return (mean, Math.Sqrt(Math.Max(0, (squares / count) - (mean * mean))));
    }

    internal static double Psnr(Raster<Rgba8> expected, Raster<Rgba8> actual, int inset)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        double squares = 0;
        long count = 0;
        for (int y = inset; y < expected.Height - inset; y++)
        {
            for (int x = inset; x < expected.Width - inset; x++)
            {
                Rgba8 e = expected.GetPixel(x, y), a = actual.GetPixel(x, y);
                squares += Sq(e.R - a.R) + Sq(e.G - a.G) + Sq(e.B - a.B) + Sq(e.A - a.A);
                count += 4;
            }
        }

        return squares == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squares / count));

        static double Sq(int v) => (double)v * v;
    }

    private static void AssertSame(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"row {y} differs");
        }
    }
}
