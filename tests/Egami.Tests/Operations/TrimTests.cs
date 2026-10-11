using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class TrimTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TrimFindsTheContentRectangle()
    {
        using Raster<Rgba8> raster = Bordered(50, 40, new Rect(7, 5, 30, 21), Rgba8.White);
        Assert.Equal(new Rect(7, 5, 30, 21), raster.TrimBounds());
        raster.Apply(ops => ops.Trim(), Ct);
        Assert.Equal(new Size(30, 21), new Size(raster.Width, raster.Height));
        Assert.NotEqual(Rgba8.White, raster.GetPixel(0, 0));
    }

    [Fact]
    public void ToleranceFollowsTheFuzzDistance()
    {
        using Raster<Rgba8> raster = Bordered(40, 30, new Rect(10, 10, 20, 10), new Rgba8(250, 250, 250, 255));
        raster.Row(0)[39] = new Rgba8(240, 240, 240, 255);

        // 10 levels off in every channel of an opaque pixel gives d² = 3·(10/255)², and the threshold is 3t², so the pixel counts
        // as border from t = 10/255 ≈ 0.0392 on.
        Assert.Equal(new Rect(10, 0, 30, 20), raster.TrimBounds(0.039f));
        Assert.Equal(new Rect(10, 10, 20, 10), raster.TrimBounds(0.04f));
    }

    [Fact]
    public void AGivenColorReplacesTheCornersColor()
    {
        using Raster<Rgba8> raster = Bordered(40, 30, new Rect(5, 5, 10, 10), Rgba8.Black);
        raster.Row(29)[39] = Rgba8.Red;
        Assert.Equal(new Rect(5, 5, 35, 25), raster.TrimBounds());
        Assert.Equal(new Rect(0, 0, 40, 30), raster.TrimBounds(color: Rgba8.Red));
    }

    [Fact]
    public void AllBorderIsLeftAlone()
    {
        using Raster<Rgba8> raster = Raster.Create(20, 10, Rgba8.Blue);
        Assert.True(raster.TrimBounds().IsEmpty);
        raster.Apply(ops => ops.Trim(), Ct);
        Assert.Equal(new Size(20, 10), new Size(raster.Width, raster.Height));
    }

    [Fact]
    public void TransparentBordersMatchWhateverTheirColor()
    {
        using Raster<Rgba8> raster = Bordered(30, 30, new Rect(10, 12, 5, 4), default);
        raster.Row(0)[5] = new Rgba8(255, 0, 0, 0);
        Assert.Equal(new Rect(10, 12, 5, 4), raster.TrimBounds());
    }

    [Fact]
    public void SixteenBitImagesTrimOnTheirOwnValues()
    {
        using Raster<Rgba16> raster = Raster.Create(30, 20, new Rgba16(1000, 1000, 1000, 65535));
        raster.Row(7)[9] = new Rgba16(1002, 1000, 1000, 65535);
        raster.Row(3)[2] = new Rgba16(1001, 1000, 1000, 65535);

        // ImageMagick's smallest fuzz, √½ of a 16-bit level, still counts one level off in one channel as the same color.
        Assert.Equal(new Rect(9, 7, 1, 1), raster.TrimBounds());
    }

    [Fact]
    public void FramesShareTheUnionOfTheirRectangles()
    {
        using Raster<Rgba8> animation = Bordered(40, 30, new Rect(2, 3, 5, 5), Rgba8.White);
        Frame<Rgba8> second = animation.Frames.Add(animation.Frames[0]);
        second.Row(3).Fill(Rgba8.White);
        second.Row(20)[30] = Rgba8.Black;
        Assert.Equal(new Rect(2, 3, 29, 18), animation.TrimBounds());
        animation.Apply(ops => ops.Trim().Resize(10, 0), Ct);
        Assert.Equal(new Size(10, 6), new Size(animation.Width, animation.Height));
    }

    [Fact]
    public void EntropyKeepsTheBusyTiles()
    {
        using Raster<Rgba8> raster = Raster.Create(200, 120, new Rgba8(30, 30, 30, 255));
        Random random = new(3);
        for (int y = 40; y < 90; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 50; x < 130; x++)
            {
                byte v = (byte)random.Next(256);
                row[x] = new Rgba8(v, v, v, 255);
            }
        }

        // Tiles are 16 pixels: x 50..129 touches tiles 3..8 and y 40..89 tiles 2..5, but tile 8 (128..143) holds only two noisy
        // columns, too few to reach half of the 8 bits.
        Rect bounds = raster.EntropyBounds();
        Assert.Equal(new Rect(48, 32, 80, 64), bounds);
        raster.Apply(ops => ops.EntropyCrop(), Ct);
        Assert.Equal(new Size(80, 64), new Size(raster.Width, raster.Height));
    }

    [Fact]
    public void EntropyOfFlatImagesKeepsEverything()
    {
        using Raster<Rgba8> raster = Raster.Create(50, 50, Rgba8.Green);
        Assert.True(raster.EntropyBounds().IsEmpty);
        raster.Apply(ops => ops.EntropyCrop(), Ct);
        Assert.Equal(50, raster.Width);
        int[] one = new int[256];
        one[0] = 256;
        Assert.Equal(0, Egami.Operations.Geometry.Bounds.Score(one, 256));
        int[] uniform = new int[256];
        uniform.AsSpan().Fill(1);
        Assert.Equal(1, Egami.Operations.Geometry.Bounds.Score(uniform, 256), 12);
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Trim(1.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.EntropyCrop(-0.1f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.TrimBounds(float.NaN));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(new Rect(0, 0, 2, 2), s => s.Trim()), Ct));
    }

    internal static Raster<Rgba8> Bordered(int width, int height, Rect content, Rgba8 border)
    {
        Raster<Rgba8> raster = Raster.Create(width, height, border);
        for (int y = content.Y; y < content.Bottom; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = content.X; x < content.Right; x++)
            {
                row[x] = new Rgba8((byte)(x * 5), (byte)(y * 7), 128, 255);
            }
        }

        return raster;
    }
}
