using Egami.Operations;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class DrawImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DivisionBy255RoundsToNearest()
    {
        for (int x = 0; x <= 65025 + 127; x++)
        {
            Assert.Equal((int)Math.Round(x / 255.0), ComposeKernels.Div255(x));
        }
    }

    [Theory]
    [InlineData(255, 0, 0, 128, 100, 150, 200, 255, 1f, 178, 75, 100, 255)]
    [InlineData(10, 20, 30, 100, 0, 0, 0, 0, 1f, 10, 20, 30, 100)]
    [InlineData(0, 0, 255, 128, 200, 100, 0, 128, 1f, 66, 33, 170, 192)]
    [InlineData(9, 9, 9, 255, 1, 2, 3, 4, 1f, 9, 9, 9, 255)]
    [InlineData(9, 9, 9, 0, 1, 2, 3, 4, 1f, 1, 2, 3, 4)]
    [InlineData(255, 255, 255, 255, 0, 0, 0, 255, 0.5f, 128, 128, 128, 255)]
    public void HandComputedBlends(int sr, int sg, int sb, int sa, int dr, int dg, int db, int da, float opacity, int r, int g, int b, int a)
    {
        using Raster<Rgba8> under = Raster.Create(1, 1, new Rgba8((byte)dr, (byte)dg, (byte)db, (byte)da));
        using Raster<Rgba8> top = Raster.Create(1, 1, new Rgba8((byte)sr, (byte)sg, (byte)sb, (byte)sa));
        under.Apply(ops => ops.DrawImage(top, new Point(0, 0), opacity), Ct);
        Assert.Equal(new Rgba8((byte)r, (byte)g, (byte)b, (byte)a), under.GetPixel(0, 0));
    }

    [Fact]
    public void EveryBlendIsTheTrueValueRounded()
    {
        Random random = new(8);
        for (int i = 0; i < 200_000; i++)
        {
            Rgba8 s = Random(random);
            Rgba8 d = Random(random);
            int opacity = random.Next(4) == 0 ? random.Next(256) : 255;
            uint[] row = [Pack(d)];
            ComposeKernels.SourceOver8([Pack(s)], row, opacity);
            Assert.Equal(Expected(s, d, opacity), Unpack(row[0]));
        }
    }

    [Fact]
    public void OnlyTheOverlapChanges()
    {
        using Raster<Rgba8> top = Raster.Create(10, 6, Rgba8.Red);
        foreach ((int x, int y) in new[] { (-4, -2), (15, 7), (-10, 0), (0, -6), (20, 0), (-3, 9), (5, 5) })
        {
            using Raster<Rgba8> under = Raster.Create(20, 12, Rgba8.Blue);
            under.Apply(ops => ops.DrawImage(top, new Point(x, y)), Ct);
            for (int py = 0; py < 12; py++)
            {
                for (int px = 0; px < 20; px++)
                {
                    bool inside = px >= x && px < x + 10 && py >= y && py < y + 6;
                    Assert.Equal(inside ? Rgba8.Red : Rgba8.Blue, under.GetPixel(px, py));
                }
            }
        }
    }

    [Fact]
    public void AnySourceTypeOntoAnyDestination()
    {
        using Raster<Gray8> grayLogo = Raster.Create(3, 3, new Gray8(200));
        using Raster<Rgba8> photo = Raster.Create(5, 5, new Rgba8(10, 20, 30, 255));
        photo.Apply(ops => ops.DrawImage(grayLogo, new Point(1, 1)), Ct);
        Assert.Equal(new Rgba8(200, 200, 200, 255), photo.GetPixel(2, 2));
        Assert.Equal(new Rgba8(10, 20, 30, 255), photo.GetPixel(0, 0));

        // The float path (Rgb8, RgbaF32 destinations) agrees with the integer path within rounding.
        using Raster<Rgba8> logo = Noise(17, 11);
        using Raster<Rgba8> opaque = Noise(30, 20, opaque: true);
        using Raster<Rgba8> expected = opaque.Derive(ops => ops.DrawImage(logo, new Point(3, 4), 0.7f), Ct);
        using Raster<Rgb8> rgb = opaque.As<Rgb8>();
        rgb.Apply(ops => ops.DrawImage(logo, new Point(3, 4), 0.7f), Ct);
        using Raster<RgbaF32> floats = opaque.As<RgbaF32>();
        floats.Apply(ops => ops.DrawImage(logo, new Point(3, 4), 0.7f), Ct);
        using Raster<Bgra8> bgra = opaque.As<Bgra8>();
        using Raster<Bgra8> bgraLogo = logo.As<Bgra8>();
        bgra.Apply(ops => ops.DrawImage(bgraLogo, new Point(3, 4), 0.7f), Ct);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                Rgba8 e = expected.GetPixel(x, y);
                Rgb8 c = rgb.GetPixel(x, y);
                Rgba8 f = floats.GetPixel(x, y).ToRgba8();
                Assert.True(Math.Abs(e.R - c.R) <= 1 && Math.Abs(e.G - c.G) <= 1 && Math.Abs(e.B - c.B) <= 1, $"rgb ({x}, {y})");
                Assert.True(Math.Abs(e.R - f.R) <= 1 && Math.Abs(e.G - f.G) <= 1 && Math.Abs(e.B - f.B) <= 1 && e.A == f.A, $"float ({x}, {y})");
                Assert.Equal(e, bgra.GetPixel(x, y).ToRgba8());
            }
        }
    }

    [Fact]
    public void DeriveSelfDrawAndArguments()
    {
        using Raster<Rgba8> raster = Noise(12, 8);
        using Raster<Rgba8> before = raster.Copy();
        using Raster<Rgba8> top = Raster.Create(4, 4, Rgba8.Lime);
        using Raster<Rgba8> derived = raster.Derive(ops => ops.DrawImage(top, new Point(2, 2)), Ct);
        Assert.Equal(before.GetPixel(3, 3), raster.GetPixel(3, 3));
        Assert.Equal(Rgba8.Lime, derived.GetPixel(3, 3));

        // Drawing a raster onto itself reads the image as it was before the step.
        using Raster<Rgba8> copy = raster.Copy();
        using Raster<Rgba8> expected = raster.Derive(ops => ops.DrawImage(copy, new Point(3, 1)), Ct);
        raster.Apply(ops => ops.DrawImage(raster, new Point(3, 1)), Ct);
        for (int y = 0; y < 8; y++)
        {
            Assert.Equal(expected.Row(y).ToArray(), raster.Row(y).ToArray());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.DrawImage(top, default, 1.5f), Ct));
        Assert.Throws<ArgumentNullException>(() => raster.Apply(ops => ops.DrawImage(null!, default), Ct));
        Raster<Rgba8> gone = Raster.Create<Rgba8>(2, 2);
        gone.Dispose();
        Assert.Throws<ObjectDisposedException>(() => raster.Apply(ops => ops.DrawImage(gone, default), Ct));
    }

    private static Rgba8 Expected(Rgba8 s, Rgba8 d, int opacity)
    {
        // The definition in exact rationals (units of 1/255²), rounded half up: sa is the source alpha scaled by opacity, itself
        // rounded to 8 bits.
        long sa = ((2L * s.A * opacity) + 255) / 510;
        if (sa == 0)
        {
            return d;
        }

        if (sa == 255)
        {
            return s with { A = 255 };
        }

        long ws = sa * 255, wd = d.A * (255 - sa), total = ws + wd;
        byte C(byte sc, byte dc) => (byte)(((2 * ((sc * ws) + (dc * wd))) + total) / (2 * total));
        return new Rgba8(C(s.R, d.R), C(s.G, d.G), C(s.B, d.B), (byte)(((2 * total) + 255) / 510));
    }

    private static Rgba8 Random(Random random)
    {
        int kind = random.Next(4);
        byte a = kind switch { 0 => 0, 1 => 255, _ => (byte)random.Next(256) };
        return new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), a);
    }

    private static uint Pack(Rgba8 p) => p.R | ((uint)p.G << 8) | ((uint)p.B << 16) | ((uint)p.A << 24);

    private static Rgba8 Unpack(uint v) => new((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));

    private static Raster<Rgba8> Noise(int width, int height, bool opaque = false)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new((width * 5) + height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), opaque ? (byte)255 : (byte)random.Next(256));
            }
        }

        return raster;
    }

    /// <summary>Drawing an area gives the same pixels as cropping it out first and drawing the crop.</summary>
    [Theory]
    [InlineData(0, 0, 10, 6, 3, 2)]
    [InlineData(2, 1, 5, 3, 0, 0)]
    [InlineData(4, 2, 6, 4, -3, -1)]
    [InlineData(7, 3, 10, 10, 15, 9)]
    [InlineData(-2, -3, 6, 6, 4, 4)]
    public void SourceAreaMatchesDrawingACrop(int ax, int ay, int aw, int ah, int px, int py)
    {
        DrawAreaLikeCrop<Rgba8>(ax, ay, aw, ah, px, py);
        DrawAreaLikeCrop<Rgba16>(ax, ay, aw, ah, px, py);
    }

    [Fact]
    public void SourceAreaOutsideTheImageThrows()
    {
        using Raster<Rgba8> top = Raster.Create(10, 6, Rgba8.Red);
        using Raster<Rgba8> under = Raster.Create(4, 4, Rgba8.Blue);
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.DrawImage(top, new Rect(10, 0, 3, 3), new Point(0, 0)), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.DrawImage(top, new Rect(0, 0, 0, 3), new Point(0, 0)), Ct));
    }

    private static void DrawAreaLikeCrop<TPixel>(int ax, int ay, int aw, int ah, int px, int py)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Random random = new(ax + (ay * 7) + (px * 13));
        using Raster<TPixel> top = Raster.Create<TPixel>(10, 6);
        using Raster<TPixel> under = Raster.Create<TPixel>(16, 12);
        foreach (Raster<TPixel> raster in new[] { top, under })
        {
            for (int y = 0; y < raster.Height; y++)
            {
                Span<TPixel> row = raster.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = TPixel.FromRgba8(Random(random));
                }
            }
        }

        Rect area = new(ax, ay, aw, ah);
        Rect clipped = area.Intersect(new Rect(0, 0, top.Width, top.Height));
        using Raster crop = top.Derive(ops => ops.Crop(clipped), Ct);
        using Raster expected = under.Derive(ops => ops.DrawImage(crop, new Point(px + clipped.X - ax, py + clipped.Y - ay)), Ct);
        using Raster actual = under.Derive(ops => ops.DrawImage(top, area, new Point(px, py)), Ct);
        Assert.Equal(Bytes((Raster<TPixel>)expected), Bytes((Raster<TPixel>)actual));
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<byte> bytes = [];
        for (int y = 0; y < raster.Height; y++)
        {
            bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y)).ToArray());
        }

        return [.. bytes];
    }
}
