using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class WarpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FromFunctionEqualsTheSameMappingAsAnIWarp()
    {
        // Random smooth warps, written once as a delegate and once as a row warp, give identical pixels.
        using Raster<Rgba8> photo = TransformTests.Smooth(80, 60);
        Random random = new(11);
        for (int i = 0; i < 10; i++)
        {
            float ax = (float)(random.NextDouble() * 6), ay = (float)(random.NextDouble() * 6);
            float fx = (float)(0.02 + (random.NextDouble() * 0.2)), fy = (float)(0.02 + (random.NextDouble() * 0.2));
            float scale = (float)(0.5 + random.NextDouble());
            Vector2 Map(Vector2 p) => new((p.X * scale) + (ax * MathF.Sin(p.Y * fy)), (p.Y * scale) + (ay * MathF.Cos(p.X * fx)));
            SamplingOptions options = new() { Edges = (EdgeMode)(i % 4), Filter = (ResampleFilter)(i % 6) };
            using Raster<Rgba8> byFunction = photo.Derive(ops => ops.Warp(Warp.FromFunction(Map), options), Ct);
            using Raster<Rgba8> byRows = photo.Derive(ops => ops.Warp(new RowWarp(Map), options), Ct);
            for (int y = 0; y < byRows.Height; y++)
            {
                Assert.True(byRows.Row(y).SequenceEqual(byFunction.Row(y)), $"case {i}, row {y}");
            }
        }
    }

    [Fact]
    public void TheIdentityWarpKeepsEveryPixel()
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(37, 23, EgamiContext.Default);
        using Raster<Rgba8> warped = raster.Derive(ops => ops.Warp(Warp.FromFunction(p => p)), Ct);
        using Raster<PremulRgba8> expected = raster.As<PremulRgba8>();
        using Raster<PremulRgba8> actual = warped.As<PremulRgba8>();
        for (int y = 0; y < 23; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"row {y}");
        }
    }

    [Fact]
    public void SizeChangingWarpsThrowInsideAScope()
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(60, 40, EgamiContext.Default);
        Rect area = new(5, 5, 30, 20);
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(area, s => s.Warp(Warps.Wave(4, 20))), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(area, s => s.Warp(Warps.Polar())), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(area, s => s.Warp(Warps.Arc(90))), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(area, s => s.Tile(40, 40)), Ct));

        // A size-keeping warp swirls the area only.
        using Raster<Rgba8> before = raster.Copy();
        raster.Apply(ops => ops.Within(area, s => s.Warp(Warps.Swirl(90))), Ct);
        Assert.Equal(before.GetPixel(0, 0), raster.GetPixel(0, 0));
        Assert.Equal(before.GetPixel(59, 39), raster.GetPixel(59, 39));
    }

    [Fact]
    public void UnmappedPixelsShowTheBackground()
    {
        using Raster<Rgba8> raster = Raster.Create(20, 10, Rgba8.Red);
        IWarp half = Warp.FromFunction(p => p.X < 10 ? p : new Vector2(float.NaN, 0));
        using Raster<Rgba8> warped = raster.Derive(ops => ops.Warp(half, new SamplingOptions { Background = Rgba8.Blue, Edges = EdgeMode.Clamp }), Ct);
        Assert.Equal(Rgba8.Red, warped.GetPixel(5, 5));
        Assert.Equal(Rgba8.Blue, warped.GetPixel(15, 5));
    }

    [Fact]
    public void OffsetMovesWholePixels()
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(20, 12, EgamiContext.Default);
        using Raster<Rgba8> rolled = raster.Derive(ops => ops.Offset(-3, 5), Ct);
        using Raster<Rgba8> shifted = raster.Derive(ops => ops.Offset(4, 0, EdgeMode.Transparent, Rgba8.Lime), Ct);
        using Raster<Rgba8> clamped = raster.Derive(ops => ops.Offset(0, 2, EdgeMode.Clamp), Ct);
        using Raster<Rgba8> mirrored = raster.Derive(ops => ops.Offset(2, 0, EdgeMode.Mirror), Ct);
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                Assert.Equal(raster.GetPixel((x + 3) % 20, (y + 7) % 12), rolled.GetPixel(x, y));
                Assert.Equal(x < 4 ? Rgba8.Lime : raster.GetPixel(x - 4, y), shifted.GetPixel(x, y));
                Assert.Equal(raster.GetPixel(x, Math.Max(0, y - 2)), clamped.GetPixel(x, y));
            }

            Assert.Equal(raster.GetPixel(1, y), mirrored.GetPixel(0, y));
            Assert.Equal(raster.GetPixel(0, y), mirrored.GetPixel(1, y));
        }
    }

    [Fact]
    public void TileRepeatsTheImageFromTheOrigin()
    {
        using Raster<Rgba8> raster = OperationCoverageTests.Noise(7, 5, EgamiContext.Default);
        using Raster<Rgba8> tiled = raster.Derive(ops => ops.Tile(30, 17, new Point(3, -2)), Ct);
        using Raster<Rgba8> part = raster.Derive(ops => ops.Tile(9, 9, source: new Rect(2, 1, 3, 2)), Ct);
        Assert.Equal(new Size(30, 17), new Size(tiled.Width, tiled.Height));
        for (int y = 0; y < 17; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                Assert.Equal(raster.GetPixel((((x - 3) % 7) + 7) % 7, (((y + 2) % 5) + 5) % 5), tiled.GetPixel(x, y));
            }
        }

        Assert.Equal(raster.GetPixel(2, 1), part.GetPixel(3, 2));
        Assert.Equal(raster.GetPixel(4, 2), part.GetPixel(8, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Tile(9, 9, source: new Rect(5, 1, 3, 2)), Ct).Dispose());
    }

    [Fact]
    public void DisplaceMovesByTheMapAndChecksItsSize()
    {
        using Raster<Rgba8> raster = TransformTests.Coded(30, 20);

        // Red 191 and green 64 everywhere move every pixel by about (+5, −2.5) at scales 20 and 10.
        using Raster<Rgba8> map = Raster.Create(30, 20, new Rgba8(191, 64, 0, 255));
        using Raster<Rgba8> moved = raster.Derive(ops => ops.Displace(map, 20, 10, options: new SamplingOptions { Filter = ResampleFilter.Nearest }), Ct);
        Assert.Equal(raster.GetPixel(15, 8), moved.GetPixel(10, 10));

        using Raster<Rgba8> small = Raster.Create<Rgba8>(10, 10);
        ArgumentException e = Assert.Throws<ArgumentException>(() => raster.Derive(ops => ops.Displace(small, 4, 4), Ct).Dispose());
        Assert.Contains("10×10", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MagnifierZoomsTheLensOnly()
    {
        using Raster<Rgba8> coded = TransformTests.Coded(100, 80);
        Rect lens = new(20, 20, 40, 30);
        using Raster<Rgba8> zoomed = coded.Derive(ops => ops.Warp(Warps.Magnifier(lens, 2, 5), new SamplingOptions { Filter = ResampleFilter.Nearest }), Ct);
        Assert.Equal(coded.GetPixel(5, 5), zoomed.GetPixel(5, 5));
        Assert.Equal(coded.GetPixel(70, 60), zoomed.GetPixel(70, 60));

        // The lens's center stays put and a point 10 pixels right of it shows the point 5 pixels right.
        Assert.Equal(coded.GetPixel(40, 35), zoomed.GetPixel(40, 35));
        Assert.Equal(coded.GetPixel(45, 35), zoomed.GetPixel(50, 35));

        using Raster<Rgba8> none = coded.Derive(ops => ops.Warp(Warps.Magnifier(lens, 1, 5), new SamplingOptions { Filter = ResampleFilter.Nearest }), Ct);
        Assert.Equal(coded.GetPixel(50, 35), none.GetPixel(50, 35));
    }

    [Fact]
    public void BuiltInWarpsKeepOrChangeTheSizeAsDocumented()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(200, 150);
        Assert.Equal(new Size(200, 150), SizeAfter(raster, Warps.Barrel(0.1, 0, 0)));
        Assert.Equal(new Size(200, 150), SizeAfter(raster, Warps.Swirl(45)));
        Assert.Equal(new Size(200, 150), SizeAfter(raster, Warps.Implode(0.3f)));
        Assert.Equal(new Size(152, 152), SizeAfter(raster, Warps.Polar()));
        Assert.Equal(new Size(236, 75), SizeAfter(raster, Warps.DePolar()));
        Assert.Equal(new Size(200, 166), SizeAfter(raster, Warps.Wave(-8, 40)));
        Assert.Equal(new Size(268, 166), SizeAfter(raster, Warps.Arc(60)));
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Arc(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Arc(90, 0, -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Wave(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Barrel(double.NaN, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Polar(5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Polar(-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Swirl(90, radius: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Magnifier(new Rect(0, 0, 0, 5), 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warps.Magnifier(new Rect(0, 0, 5, 5), 2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Warp.FromFunction(p => p, new Size(0, 5)));
        Assert.Throws<ArgumentNullException>(() => Warp.FromFunction(null!));
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentNullException>(() => raster.Apply(ops => ops.Warp(null!), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Offset(1, 1, (EdgeMode)9), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Tile(0, 4), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Displace(raster, float.NaN, 1), Ct));
    }

    private static Size SizeAfter(Raster<Rgba8> raster, IWarp warp)
    {
        using Raster<Rgba8> result = raster.Derive(ops => ops.Warp(warp), Ct);
        return new Size(result.Width, result.Height);
    }

    private sealed class RowWarp(Func<Vector2, Vector2> map) : IWarp
    {
        public Size OutputSize(Size input) => input;

        public void Map(int y, Size input, Span<Vector2> source)
        {
            for (int x = 0; x < source.Length; x++)
            {
                source[x] = map(new Vector2(x + 0.5f, y + 0.5f));
            }
        }
    }
}
