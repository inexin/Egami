using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Blending;

public class CompositorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<BlendMode> Modes()
    {
        return [.. Enum.GetValues<BlendMode>()];
    }

    /// <summary>A row blended by the Compositor equals the same row drawn by DrawImage, for every mode and several pixel types.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void RowsEqualDrawImage(BlendMode mode)
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        Same<Rgba8>(mode, s, d);
        Same<PremulBgra8>(mode, s, d);
        Same<RgbaF32>(mode, s, d);
        Same<Gray16>(mode, s, d);
        Same<Cmyk8>(mode, s, d);
    }

    [Fact]
    public void LinearRowsEqualDrawImageInLinearLight()
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        using Raster<Rgba8> top = BlendOperationTests.From<Rgba8>(s);
        using Raster<Rgba8> drawn = BlendOperationTests.From<Rgba8>(d);
        drawn.Apply(ops => ops.DrawImage(top, new Point(0, 0), BlendMode.Screen, 0.7f, ColorSpace.Linear), Ct);
        Rgba8[] rows = (Rgba8[])d.Clone();
        Compositor.Blend<Rgba8>(BlendMode.Screen, s, rows, 0.7f, ColorSpace.Linear);
        Assert.Equal(Pixels(drawn), rows);
    }

    [Theory]
    [InlineData(BlendMode.SourceOver)]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Clear)]
    [InlineData(BlendMode.Hue)]
    public void CoverageMixesFromNothingToTheFullBlend(BlendMode mode)
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        uint[] ps = BlendKernelTests.Premultiplied(s), pd = BlendKernelTests.Premultiplied(d);
        PremulRgba8[] source = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, PremulRgba8>(ps).ToArray();
        PremulRgba8[] original = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, PremulRgba8>(pd).ToArray();
        PremulRgba8[] full = (PremulRgba8[])original.Clone();
        Compositor.Blend<PremulRgba8>(mode, source, full);
        PremulRgba8[] none = (PremulRgba8[])original.Clone();
        Compositor.Blend<PremulRgba8>(mode, source, none, new byte[original.Length]);
        Assert.Equal(original, none);
        PremulRgba8[] all = (PremulRgba8[])original.Clone();
        byte[] opaque = new byte[original.Length];
        opaque.AsSpan().Fill(255);
        Compositor.Blend<PremulRgba8>(mode, source, all, opaque);
        Assert.Equal(full, all);
    }

    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Color)]
    [InlineData(BlendMode.SourceIn)]
    public void AColorThroughFullCoverageEqualsBlendColor(BlendMode mode)
    {
        (_, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        Rgba8 color = new(30, 140, 220, 200);
        using Raster<Bgra8> raster = BlendOperationTests.From<Bgra8>(d);
        raster.Apply(ops => ops.BlendColor(color, mode, 0.9f), Ct);
        using Raster<Bgra8> rows = BlendOperationTests.From<Bgra8>(d);
        byte[] coverage = new byte[64];
        coverage.AsSpan().Fill(255);
        for (int y = 0; y < 48; y++)
        {
            Compositor.Blend(mode, color, rows.Row(y), coverage, 0.9f);
        }

        Assert.Equal(BlendOperationTests.Bytes(raster), BlendOperationTests.Bytes(rows));
    }

    /// <summary>A one-pixel straight 8-bit row is premultiplied like a longer one, so it blends as premultiplied and float rows do.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.Screen)]
    [InlineData(BlendMode.Darken)]
    [InlineData(BlendMode.Hue)]
    public void OnePixelStraightRowsArePremultiplied(BlendMode mode)
    {
        Rgba8 s = new(100, 180, 30, 128), d = new(200, 100, 50, 255);
        Rgba8[] one = [d];
        Compositor.Blend<Rgba8>(mode, [s], one);
        Rgba8[] two = [d, d];
        Compositor.Blend<Rgba8>(mode, [s, s], two);
        Bgra8[] bgra = [Bgra8.FromRgba8(d)];
        Compositor.Blend<Bgra8>(mode, [Bgra8.FromRgba8(s)], bgra);
        Assert.Equal(two[0], one[0]);
        Assert.Equal(two[0], bgra[0].ToRgba8());
    }

    [Fact]
    public void OnePixelOverloadsMatchTheRows()
    {
        Rgba8 s = new(200, 40, 90, 150), d = new(20, 160, 240, 220);
        Rgba8[] row = [d];
        Compositor.Blend<Rgba8>(BlendMode.Overlay, [s], row);
        Assert.Equal(row[0], Compositor.Blend(BlendMode.Overlay, s, d));
        RgbaF32 fs = RgbaF32.FromRgba8(s), fd = RgbaF32.FromRgba8(d);
        RgbaF32[] frow = [fd];
        Compositor.Blend<RgbaF32>(BlendMode.SoftLight, [fs], frow);
        Assert.Equal(frow[0], Compositor.Blend(BlendMode.SoftLight, fs, fd));
        Assert.Equal(d, Compositor.Blend(BlendMode.Destination, s, d));
    }

    [Fact]
    public void BlendingAllocatesNothingOnceWarm()
    {
        Rgba8[] s = new Rgba8[1920], d = new Rgba8[1920];
        PremulRgba8[] ps = new PremulRgba8[1920], pd = new PremulRgba8[1920];
        RgbaF32[] fs = new RgbaF32[1920], fd = new RgbaF32[1920];
        byte[] coverage = new byte[1920];
        Action[] calls =
        [
            () => Compositor.Blend<Rgba8>(BlendMode.Multiply, s, d),
            () => Compositor.Blend<Rgba8>(BlendMode.SourceOver, s, d, coverage),
            () => Compositor.Blend<PremulRgba8>(BlendMode.Hue, ps, pd, coverage, 0.5f),
            () => Compositor.Blend<RgbaF32>(BlendMode.Screen, fs, fd, 1, ColorSpace.Linear),
            () => Compositor.Blend(BlendMode.Color, Rgba8.Red, pd.AsSpan(), coverage),
            () => Compositor.Blend(BlendMode.Color, Rgba8.Red, fd.AsSpan(), coverage),
        ];

        // Warm enough that the JIT has finished tiering, which allocates on the calling thread. Other tests run in parallel and the
        // runtime trims the shared array pool under memory pressure, so each call's fewest bytes over many tries is what it allocates.
        foreach (Action call in calls)
        {
            for (int i = 0; i < 300; i++)
            {
                call();
            }

            long fewest = long.MaxValue;
            for (int i = 0; i < 30 && fewest > 0; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                call();
                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            Assert.Equal(0, fewest);
        }
    }

    [Fact]
    public void ManyThreadsGetTheSameResults()
    {
        (Rgba8[] s, Rgba8[] d) = BlendCorpus.Layers(64, 48);
        Rgba8[] expected = (Rgba8[])d.Clone();
        Compositor.Blend<Rgba8>(BlendMode.ColorBurn, s, expected, 0.8f);
        Parallel.For(0, 64, _ =>
        {
            Rgba8[] mine = (Rgba8[])d.Clone();
            Compositor.Blend<Rgba8>(BlendMode.ColorBurn, s, mine, 0.8f);
            Assert.Equal(expected, mine);
        });
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Rgba8[] two = new Rgba8[2], three = new Rgba8[3];
        Assert.Throws<ArgumentException>(() => Compositor.Blend<Rgba8>(BlendMode.Screen, two, three));
        Assert.Throws<ArgumentException>(() => Compositor.Blend<Rgba8>(BlendMode.Screen, two, two, new byte[3]));
        Assert.Throws<ArgumentException>(() => Compositor.Blend(BlendMode.Screen, Rgba8.Red, two.AsSpan(), new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Compositor.Blend<Rgba8>((BlendMode)99, two, two));
        Assert.Throws<ArgumentOutOfRangeException>(() => Compositor.Blend<Rgba8>(BlendMode.Screen, two, two, -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Compositor.Blend<Rgba8>(BlendMode.Screen, two, two, 1, (ColorSpace)5));
    }

    private static void Same<TPixel>(BlendMode mode, Rgba8[] s, Rgba8[] d)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> top = BlendOperationTests.From<TPixel>(s);
        using Raster<TPixel> drawn = BlendOperationTests.From<TPixel>(d);
        using Raster<TPixel> rows = BlendOperationTests.From<TPixel>(d);
        drawn.Apply(ops => ops.DrawImage(top, new Point(0, 0), mode, 0.8f), Ct);
        for (int y = 0; y < rows.Height; y++)
        {
            Compositor.Blend<TPixel>(mode, top.Row(y), rows.Row(y), 0.8f);
        }

        Assert.True(BlendOperationTests.Bytes(drawn).AsSpan().SequenceEqual(BlendOperationTests.Bytes(rows)), $"{mode} on {typeof(TPixel).Name}");
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
