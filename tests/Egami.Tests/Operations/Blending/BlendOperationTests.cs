using Egami.Operations;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Tests.Operations.Blending;

public class BlendOperationTests
{
    private static readonly string[] Keys = ["norm", "mul ", "scrn", "over", "dark", "lite", "div ", "idiv", "hLit", "sLit", "diff", "smud", "lddg", "fsub", "hue ", "sat ", "colr", "lum "];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>SourceOver on stored values runs the 1.x kernels, so every pixel type gives exactly the 1.x result.</summary>
    [Fact]
    public void SourceOverEqualsTheOneXDrawImage()
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        using Raster<Rgba8> top = From<Rgba8>(s);
        foreach (Func<Raster> make in new Func<Raster>[]
        {
            () => From<Rgba8>(d), () => From<Bgra8>(d), () => From<PremulRgba8>(d), () => From<RgbaF32>(d), () => From<Rgb16>(d), () => From<Cmyk8>(d),
        })
        {
            using Raster a = make(), b = make();
            a.Apply(ops => ops.DrawImage(top, new Point(5, -3), 0.8f), Ct);
            b.Apply(ops => ops.DrawImage(top, new Point(5, -3), BlendMode.SourceOver, 0.8f), Ct);
            Assert.Equal(Bytes(a), Bytes(b));
        }
    }

    /// <summary>On premultiplied rasters a mode is the kernel itself; on straight ones it is premultiply, blend, divide back.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Overlay)]
    [InlineData(BlendMode.Xor)]
    [InlineData(BlendMode.Luminosity)]
    public void ModesMatchTheReferenceOnEightBitRasters(BlendMode mode)
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        using Raster<Rgba8> top = From<Rgba8>(s);
        using Raster<PremulRgba8> premul = From<PremulRgba8>(d);
        using Raster<Rgba8> straight = From<Rgba8>(d);
        premul.Apply(ops => ops.DrawImage(top, new Point(0, 0), mode), Ct);
        straight.Apply(ops => ops.DrawImage(top, new Point(0, 0), mode), Ct);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                PremulRgba8 ps = PremulRgba8.FromRgba8(s[(y * 64) + x]), pd = PremulRgba8.FromRgba8(d[(y * 64) + x]);
                var expected = BlendReference.Blend8(mode, (ps.R, ps.G, ps.B, ps.A), (pd.R, pd.G, pd.B, pd.A));
                PremulRgba8 actual = premul.GetPixel(x, y);
                Assert.True(Near(expected, (actual.R, actual.G, actual.B, actual.A), BlendKernels.IsInteger(mode) ? 0 : 1), $"{mode} at {x},{y}");
                if (BlendKernels.IsInteger(mode))
                {
                    Assert.Equal(new PremulRgba8(expected.R, expected.G, expected.B, expected.A).ToRgba8(), straight.GetPixel(x, y));
                }
            }
        }
    }

    /// <summary>Only the overlap changes: DestinationIn cuts the image's shape out of the overlap and leaves the rest.</summary>
    [Fact]
    public void OnlyTheOverlapChanges()
    {
        using Raster<Rgba8> under = Raster.Create(10, 10, new Rgba8(10, 20, 30, 255));
        using Raster<Rgba8> top = Raster.Create(4, 4, new Rgba8(0, 0, 0, 0));
        under.Apply(ops => ops.DrawImage(top, new Point(8, 8), BlendMode.DestinationIn), Ct);
        Assert.Equal(new Rgba8(10, 20, 30, 255), under.GetPixel(7, 7));
        Assert.Equal(new Rgba8(0, 0, 0, 0), under.GetPixel(8, 8));
        Assert.Equal(new Rgba8(0, 0, 0, 0), under.GetPixel(9, 9));

        // Opacity 0 still applies SourceIn's rule inside the overlap: transparent source there, transparent result.
        under.Apply(ops => ops.DrawImage(top, new Point(0, 0), BlendMode.SourceIn, 0), Ct);
        Assert.Equal(new Rgba8(0, 0, 0, 0), under.GetPixel(3, 3));
        Assert.Equal(new Rgba8(10, 20, 30, 255), under.GetPixel(4, 4));
    }

    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Screen)]
    [InlineData(BlendMode.Color)]
    [InlineData(BlendMode.SourceAtop)]
    public void BlendColorEqualsDrawingASolidImage(BlendMode mode)
    {
        (_, Rgba8[] d) = BlendCorpus.Layers(40, 30);
        Rgba8 color = new(200, 90, 40, 170);
        using Raster<Rgba8> solid = Raster.Create(40, 30, color);
        foreach (Func<Raster> make in new Func<Raster>[] { () => From<Rgba8>(d), () => From<PremulBgra8>(d), () => From<RgbaF32>(d), () => From<Gray16>(d) })
        {
            using Raster a = make(), b = make();
            a.Apply(ops => ops.BlendColor(color, mode, 0.6f), Ct);
            b.Apply(ops => ops.DrawImage(solid, new Point(0, 0), mode, 0.6f), Ct);
            Assert.Equal(Bytes(b), Bytes(a));
        }
    }

    /// <summary>Linear light on float pixels: the blend of the decoded values, encoded again, within 1e-6 of the double reference.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.SoftLight)]
    [InlineData(BlendMode.Hue)]
    [InlineData(BlendMode.SourceOver)]
    public void LinearLightMatchesTheFloatReference(BlendMode mode)
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(40, 30);
        using Raster<RgbaF32> top = From<RgbaF32>(s);
        using Raster<RgbaF32> under = From<RgbaF32>(d);
        using Raster<Rgba8> under8 = From<Rgba8>(d);
        under.Apply(ops => ops.DrawImage(top, new Point(0, 0), mode, 1, ColorSpace.Linear), Ct);
        under8.Apply(ops => ops.DrawImage(top, new Point(0, 0), mode, 1, ColorSpace.Linear), Ct);
        for (int i = 0; i < s.Length; i++)
        {
            (double R, double G, double B, double A) expected = LinearBlend(mode, s[i], d[i]);
            RgbaF32 actual = under.GetPixel(i % 40, i / 40);
            if (expected.A > 1e-6)
            {
                Assert.Equal(expected.R, actual.R, 2e-6);
                Assert.Equal(expected.G, actual.G, 2e-6);
                Assert.Equal(expected.B, actual.B, 2e-6);
            }

            Assert.Equal(expected.A, actual.A, 1e-6);
            Rgba8 actual8 = under8.GetPixel(i % 40, i / 40);
            if (expected.A * 255 >= 8)
            {
                Assert.True(Math.Abs(actual8.R - (expected.R * 255)) <= 1.5 && Math.Abs(actual8.G - (expected.G * 255)) <= 1.5
                    && Math.Abs(actual8.B - (expected.B * 255)) <= 1.5, $"{mode} {i}: {actual8} vs {expected}");
            }
        }
    }

    [Fact]
    public void ArithmeticWeighsBothImages()
    {
        using Raster<PremulRgbaF32> under = Raster.Create(2, 1, new PremulRgbaF32(0.2f, 0.4f, 0.1f, 0.5f));
        using Raster<PremulRgbaF32> top = Raster.Create(1, 1, new PremulRgbaF32(0.3f, 0.3f, 0.6f, 0.6f));
        under.Apply(ops => ops.DrawArithmetic(top, new Point(1, 0), 1, 0.5f, 0.25f, 0.05f), Ct);
        Assert.Equal(new PremulRgbaF32(0.2f, 0.4f, 0.1f, 0.5f), under.GetPixel(0, 0));
        PremulRgbaF32 p = under.GetPixel(1, 0);

        // Alpha: 0.6·0.5 + 0.3 + 0.125 + 0.05 = 0.775; red: 0.06 + 0.15 + 0.05 + 0.05 = 0.31; blue 0.06 + 0.3 + 0.025 + 0.05 = 0.435.
        Assert.Equal(0.775f, p.A, 1e-6f);
        Assert.Equal(0.31f, p.R, 1e-6f);
        Assert.Equal(0.12f + 0.15f + 0.1f + 0.05f, p.G, 1e-6f);
        Assert.Equal(0.435f, p.B, 1e-6f);

        // Clamped: color never exceeds alpha.
        under.Apply(ops => ops.DrawArithmetic(top, new Point(0, 0), 0, 2, 0, 0.5f), Ct);
        PremulRgbaF32 clamped = under.GetPixel(0, 0);
        Assert.Equal(1f, clamped.A);
        Assert.True(clamped.R <= clamped.A && clamped.B <= clamped.A);
    }

    [Fact]
    public void DestinationIsDroppedAndArgumentsAreChecked()
    {
        using Raster<Rgba8> top = Raster.Create(2, 2, Rgba8.Red);
        using Raster<Rgba8> under = Raster.Create(2, 2, Rgba8.Blue);
        under.Apply(ops => ops.DrawImage(top, new Point(0, 0), BlendMode.Destination).BlendColor(Rgba8.Lime, BlendMode.Destination), Ct);
        Assert.Equal(Rgba8.Blue, under.GetPixel(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.DrawImage(top, new Point(0, 0), (BlendMode)40), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.DrawImage(top, new Point(0, 0), BlendMode.Screen, 1.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.BlendColor(Rgba8.Red, BlendMode.Screen, 1, (ColorSpace)7), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.DrawArithmetic(top, new Point(0, 0), float.NaN, 0, 0, 0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => under.Apply(ops => ops.DrawImage(top, new Rect(5, 5, 2, 2), new Point(0, 0), BlendMode.Screen), Ct));
    }

    [Fact]
    public void PhotoshopKeysMapToModes()
    {
        Assert.Equal(BlendMode.SourceOver, PsdBlendKeys.ToBlendMode("norm"));
        Assert.Equal(BlendMode.ColorDodge, PsdBlendKeys.ToBlendMode("div "));
        Assert.Equal(BlendMode.Add, PsdBlendKeys.ToBlendMode("lddg"));
        Assert.Equal(BlendMode.Luminosity, PsdBlendKeys.ToBlendMode("lum "));
        Assert.Null(PsdBlendKeys.ToBlendMode("diss"));
        Assert.Null(PsdBlendKeys.ToBlendMode("pass"));
        Assert.Equal(18, Keys.Select(PsdBlendKeys.ToBlendMode).Distinct().Count());
    }

    internal static Raster<TPixel> From<TPixel>(Rgba8[] pixels)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = pixels.Length switch { 64 * 48 => 64, 40 * 30 => 40, _ => throw new ArgumentException("Unknown size.") };
        Raster<TPixel> raster = Raster.Create<TPixel>(width, pixels.Length / width);
        for (int y = 0; y < raster.Height; y++)
        {
            PixelConvert.Convert<Rgba8, TPixel>(pixels.AsSpan(y * width, width), raster.Row(y));
        }

        return raster;
    }

    internal static byte[] Bytes(Raster raster)
    {
        using Raster<Rgba16> wide = raster.As<Rgba16>();
        List<byte> bytes = [];
        for (int y = 0; y < wide.Height; y++)
        {
            bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(wide.Row(y)).ToArray());
        }

        return [.. bytes];
    }

    private static (double R, double G, double B, double A) LinearBlend(BlendMode mode, Rgba8 s, Rgba8 d)
    {
        static (double, double, double, double) Linear(Rgba8 p)
        {
            double a = p.A / 255.0;
            return (Srgb.ToLinear(p.R / 255.0) * a, Srgb.ToLinear(p.G / 255.0) * a, Srgb.ToLinear(p.B / 255.0) * a, a);
        }

        (double r, double g, double b, double a) = BlendReference.Blend(mode, Linear(s), Linear(d));
        if (a <= 0)
        {
            return (0, 0, 0, 0);
        }

        return (Srgb.FromLinear(Math.Clamp(r / a, 0, 1)), Srgb.FromLinear(Math.Clamp(g / a, 0, 1)), Srgb.FromLinear(Math.Clamp(b / a, 0, 1)), a);
    }

    private static bool Near((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b, int levels)
    {
        return a.A == b.A && Math.Abs(a.R - b.R) <= levels && Math.Abs(a.G - b.G) <= levels && Math.Abs(a.B - b.B) <= levels;
    }
}
