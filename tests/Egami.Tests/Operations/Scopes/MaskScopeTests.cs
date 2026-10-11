using Egami.Operations;
using Egami.Pixels;
using Egami.Tests.Operations.Blending;

namespace Egami.Tests.Operations.Scopes;

/// <summary>Within(mask) against the whole-image result mixed with the original by the mask.</summary>
public sealed class MaskScopeTests : IDisposable
{
    private readonly Raster<Rgba8> _logo = OperationCoverageTests.Logo();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> SameSize() => RectScopeTests.SameSize();

    [Theory]
    [MemberData(nameof(SameSize))]
    public void ResultsAreTheWholeImageMixedByTheMask(string operation)
    {
        const int Width = 61, Height = 37;
        using Raster<PremulRgba8> source = OperationCoverageTests.Noise(Width, Height, EgamiContext.Default).As<PremulRgba8>();
        using Raster<Gray8> mask = GrayMask(Width - 10, Height - 6);
        Point at = new(4, 2);
        Action<RasterOps> chain = OperationCoverageTests.Chains[operation](new Size(Width, Height), _logo);
        using Raster<PremulRgba8> whole = source.Derive(chain, Ct);
        using Raster<PremulRgba8> masked = source.Derive(ops => ops.Within(new Mask(mask, at), chain), Ct);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int m = x >= at.X && y >= at.Y && x < at.X + mask.Width && y < at.Y + mask.Height ? mask.GetPixel(x - at.X, y - at.Y).L : 0;
                PremulRgba8 o = source.GetPixel(x, y), r = whole.GetPixel(x, y);
                PremulRgba8 expected = new(Mix(o.R, r.R, m), Mix(o.G, r.G, m), Mix(o.B, r.B, m), Mix(o.A, r.A, m));
                Assert.True(expected == masked.GetPixel(x, y), $"{operation} at ({x}, {y}), coverage {m}: {masked.GetPixel(x, y)}, expected {expected}");
            }
        }
    }

    [Fact]
    public void EmptyAndFullMasksAreExactOnStraightPixels()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(50, 30, EgamiContext.Default);
        using Raster<Gray8> none = Raster.Create(50, 30, new Gray8(0));
        using Raster<Gray8> full = Raster.Create(50, 30, new Gray8(255));
        Action<RasterOps> chain = ops => ops.Blur(2).HueRotate(40);
        using Raster<Rgba8> whole = source.Derive(chain, Ct);
        using Raster<Rgba8> withNone = source.Derive(ops => ops.Within(new Mask(none), chain), Ct);
        using Raster<Rgba8> withFull = source.Derive(ops => ops.Within(new Mask(full), chain), Ct);
        Assert.Equal(Pixels(source), Pixels(withNone));
        Assert.Equal(Pixels(whole), Pixels(withFull));

        // A half mask: coverage 0 and 255 pixels are exact too, in the same rows as partial ones.
        using Raster<Gray8> half = Raster.Create<Gray8>(50, 30);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                half.Row(y)[x] = new Gray8((byte)(x % 3 == 0 ? 0 : x % 3 == 1 ? 255 : 100));
            }
        }

        using Raster<Rgba8> mixed = source.Derive(ops => ops.Within(new Mask(half), chain), Ct);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 50; x += 3)
            {
                Assert.Equal(source.GetPixel(x, y), mixed.GetPixel(x, y));
                Assert.Equal(whole.GetPixel(x + 1, y), mixed.GetPixel(x + 1, y));
            }
        }
    }

    [Fact]
    public void ChannelsAndInversionPickTheCoverage()
    {
        using Raster<Rgba8> source = Raster.Create(4, 1, new Rgba8(100, 100, 100, 255));
        using Raster<Rgba8> mask = Raster.Create<Rgba8>(4, 1);
        mask.Row(0)[0] = new Rgba8(255, 0, 0, 0);
        mask.Row(0)[1] = new Rgba8(0, 255, 0, 255);
        mask.Row(0)[2] = new Rgba8(255, 255, 255, 255);
        mask.Row(0)[3] = new Rgba8(0, 0, 255, 128);
        Action<RasterOps> fill = o => o.FillColor(Rgba8.Black);

        // Auto on a raster with alpha reads alpha: none, full, full, half.
        using Raster<Rgba8> auto = source.Derive(ops => ops.Within(new Mask(mask), fill), Ct);
        Assert.Equal([new Rgba8(100, 100, 100, 255), Rgba8.Black, Rgba8.Black, new Rgba8(50, 50, 50, 255)], auto.Row(0).ToArray());
        using Raster<Rgba8> red = source.Derive(ops => ops.Within(new Mask(mask, Channel: MaskChannel.Red), fill), Ct);
        Assert.Equal([Rgba8.Black, new Rgba8(100, 100, 100, 255), Rgba8.Black, new Rgba8(100, 100, 100, 255)], red.Row(0).ToArray());
        using Raster<Rgba8> inverted = source.Derive(ops => ops.Within(new Mask(mask, Channel: MaskChannel.Red, Inverted: true), fill), Ct);
        Assert.Equal([new Rgba8(100, 100, 100, 255), Rgba8.Black, new Rgba8(100, 100, 100, 255), Rgba8.Black], inverted.Row(0).ToArray());
        using Raster<Rgba8> luminance = source.Derive(ops => ops.Within(new Mask(mask, Channel: MaskChannel.Luminance), fill), Ct);
        Assert.InRange(luminance.GetPixel(1, 0).R, 27, 29);

        // Outside the raster, coverage is 0, or full when inverted.
        using Raster<Rgba8> placed = source.Derive(ops => ops.Within(new Mask(mask, new Point(3, 0), MaskChannel.Red, Inverted: true), fill), Ct);
        Assert.Equal(Rgba8.Black, placed.GetPixel(0, 0));
        Assert.Equal(new Rgba8(100, 100, 100, 255), placed.GetPixel(3, 0));
    }

    [Fact]
    public void MasksNestByMultiplying()
    {
        using Raster<RgbaF32> source = Raster.Create(1, 1, new RgbaF32(1, 1, 1, 1));
        using Raster<GrayF32> half = Raster.Create(1, 1, new GrayF32(0.5f));
        using Raster<RgbaF32> nested = source.Derive(ops => ops.Within(new Mask(half), o => o.Within(new Mask(half), i => i.FillColor(Rgba8.Black))), Ct);
        Assert.Equal(0.75f, nested.GetPixel(0, 0).R, 1e-6f);
    }

    [Fact]
    public void SixteenBitMasksKeepTheirGradient()
    {
        using Raster<Rgba16> source = Raster.Create(1000, 1, new Rgba16(65535, 65535, 65535, 65535));
        using Raster<Gray16> ramp = Raster.Create<Gray16>(1000, 1);
        for (int x = 0; x < 1000; x++)
        {
            ramp.Row(0)[x] = new Gray16((ushort)(x * 65));
        }

        using Raster<Rgba16> faded = source.Derive(ops => ops.Within(new Mask(ramp), o => o.FillColor(Rgba8.Black)), Ct);
        HashSet<ushort> levels = [.. faded.Row(0).ToArray().Select(p => p.R)];
        Assert.True(levels.Count > 900, $"{levels.Count} distinct levels");
    }

    [Fact]
    public void MaskingARasterWithItselfReadsACopy()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(10, 1);
        for (int x = 0; x < 10; x++)
        {
            raster.Row(0)[x] = new Gray8((byte)(x * 25));
        }

        raster.Apply(ops => ops.Within(new Mask(raster), o => o.FillColor(Rgba8.White)), Ct);
        for (int x = 0; x < 10; x++)
        {
            int v = x * 25;
            Assert.InRange(raster.GetPixel(x, 0).L, Math.Round(v + (v * (255 - v) / 255.0)) - 1, Math.Round(v + (v * (255 - v) / 255.0)) + 1);
        }
    }

    /// <summary>The fused path holds no plane beyond the image and the mask; other chains hold one plane the size of the mask's box.</summary>
    [Fact]
    public void ScratchMemoryIsTheMasksBoxAtMost()
    {
        const long Image = 1000 * 800 * 4, MaskBytes = 500 * 400, Box = 500 * 400 * 4;
        Egami.Tests.Memory.MemoryPeak fused = Egami.Tests.Memory.PeakMemory.Measure(context =>
        {
            using Raster<Rgba8> raster = Raster.Create(1000, 800, new Rgba8(10, 20, 30, 255), context);
            using Raster<Gray8> mask = Raster.Create(500, 400, new Gray8(128), context);
            raster.Apply(ops => ops.Within(new Mask(mask, new Point(100, 100)), o => o.Saturation(1.2f).Contrast(1.1f)), Ct);
        });
        Assert.True(fused.PeakPixelBytes <= Image + MaskBytes, fused.ToString());

        Egami.Tests.Memory.MemoryPeak scratch = Egami.Tests.Memory.PeakMemory.Measure(context =>
        {
            using Raster<Rgba8> raster = Raster.Create(1000, 800, new Rgba8(10, 20, 30, 255), context);
            using Raster<Gray8> mask = Raster.Create(500, 400, new Gray8(128), context);
            raster.Apply(ops => ops.Within(new Mask(mask, new Point(100, 100)), o => o.Invert().FlipHorizontal()), Ct);
        });
        Assert.True(scratch.PeakPixelBytes <= Image + MaskBytes + Box, scratch.ToString());
        Assert.Equal(0, scratch.LeftBytes);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(default(Mask), o => o.Invert()), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.Within(new Mask(source, Channel: (MaskChannel)9), o => o.Invert()), Ct));
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(new Mask(source), o => o.Resize(2, 2)), Ct));
    }

    public void Dispose()
    {
        _logo.Dispose();
    }

    internal static Raster<Gray8> GrayMask(int width, int height)
    {
        Raster<Gray8> mask = Raster.Create<Gray8>(width, height);
        byte[] values = BlendCorpus.Mask(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                mask.Row(y)[x] = new Gray8(values[(y * width) + x]);
            }
        }

        return mask;
    }

    private static byte Mix(byte original, byte result, int m)
    {
        return m == 0 ? original : m == 255 ? result : (byte)(((result * m) + (original * (255 - m)) + 127) / 255);
    }

    private static Rgba8[] Pixels(Raster<Rgba8> raster)
    {
        Rgba8[] result = new Rgba8[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(result.AsSpan(y * raster.Width, raster.Width));
        }

        return result;
    }
}
