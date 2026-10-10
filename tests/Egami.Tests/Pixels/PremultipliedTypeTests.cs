using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>PremulRgba8, PremulBgra8 and PremulRgbaF32 (Phase 2.1.1 task 8).</summary>
public class PremultipliedTypeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryStoredPixelRoundTripsThroughStraight()
    {
        // All 65,536 (color, alpha) pairs; a color above its alpha cannot come from a straight color and converts as the alpha.
        PremulRgba8[] stored = new PremulRgba8[65536];
        for (int a = 0; a < 256; a++)
        {
            for (int c = 0; c < 256; c++)
            {
                stored[(a * 256) + c] = new PremulRgba8((byte)c, (byte)(c / 2), (byte)Math.Min(c, a), (byte)a);
            }
        }

        Rgba8[] straight = new Rgba8[stored.Length];
        PremulRgba8[] back = new PremulRgba8[stored.Length];
        PixelConvert.Convert<PremulRgba8, Rgba8>(stored, straight);
        PixelConvert.Convert<Rgba8, PremulRgba8>(straight, back);
        for (int i = 0; i < stored.Length; i++)
        {
            PremulRgba8 p = stored[i];
            PremulRgba8 expected = p.A == 0 ? default : new PremulRgba8(Math.Min(p.R, p.A), Math.Min(p.G, p.A), p.B, p.A);
            Assert.True(expected == back[i], $"{p} came back as {back[i]}");
        }
    }

    [Fact]
    public void KernelsMatchTheScalarPathForEveryPair()
    {
        if (PixelKernels.HardwareBits < 128)
        {
            return;
        }

        Rgba8[] straight = new Rgba8[65536];
        PremulRgba8[] stored = new PremulRgba8[65536];
        for (int i = 0; i < straight.Length; i++)
        {
            straight[i] = new Rgba8((byte)i, (byte)(255 - i), (byte)(i * 3), (byte)(i >> 8));
            stored[i] = new PremulRgba8((byte)i, (byte)(255 - i), (byte)(i * 3), (byte)(i >> 8));
        }

        PremulRgba8[] kernel = new PremulRgba8[straight.Length], scalar = new PremulRgba8[straight.Length];
        PixelConvert.Convert<Rgba8, PremulRgba8>(straight, kernel);
        PixelConvert.ConvertScalar<Rgba8, PremulRgba8>(straight, scalar);
        Assert.Equal(scalar, kernel);

        Rgba8[] kernelBack = new Rgba8[stored.Length], scalarBack = new Rgba8[stored.Length];
        PixelConvert.Convert<PremulRgba8, Rgba8>(stored, kernelBack);
        PixelConvert.ConvertScalar<PremulRgba8, Rgba8>(stored, scalarBack);
        Assert.Equal(scalarBack, kernelBack);
    }

    [Fact]
    public void ConversionsTakeAndReturnStraightColors()
    {
        Rgba8 color = new(200, 100, 50, 128);
        PremulRgba8 p = PremulRgba8.FromRgba8(color);
        Assert.Equal(new PremulRgba8(100, 50, 25, 128), p);
        Assert.Equal(new PremulBgra8(25, 50, 100, 128), PremulBgra8.FromRgba8(color));
        Assert.Equal(new Rgba8(199, 100, 50, 128), p.ToRgba8());   // 200 × 128 / 255 = 100.4 is stored as 100
        Assert.Equal(default, PremulRgba8.FromRgba8(new Rgba8(9, 9, 9, 0)));
        Assert.Equal(new Rgba8(255, 255, 255, 10), new PremulRgba8(200, 200, 200, 10).ToRgba8());

        PremulRgbaF32 f = PremulRgbaF32.FromRgba8(color);
        Assert.Equal(color.ToScaled().X * color.ToScaled().W, f.R, 6);
        Assert.Equal(color, f.ToRgba8());
        Assert.Equal(default, new PremulRgbaF32(0.2f, 0f, 0f, 0f).ToRgba8());
    }

    public static TheoryData<string> FastPathChains() => ["Resize", "Blur", "Sharpen", "Convolve", "Rotate", "DrawImage"];

    [Theory]
    [MemberData(nameof(FastPathChains))]
    public void FastPathsMatchTheStraightPath(string chain)
    {
        using Raster<Rgba8> logo = Raster.Create<Rgba8>(20, 20, new Rgba8(255, 0, 0, 160));
        Action<RasterOps> build = chain switch
        {
            "Resize" => ops => ops.Resize(37, 23, ResampleFilter.Lanczos3),
            "Blur" => ops => ops.Blur(2.5f),
            "Sharpen" => ops => ops.Sharpen(1, 1.5f),
            "Convolve" => ops => ops.Convolve(new ConvolutionKernel(3, 3, [1, 2, 1, 2, 4, 2, 1, 2, 1], divisor: 16), EdgeMode.Mirror),
            "Rotate" => ops => ops.Rotate(23, ResampleFilter.Bicubic, new Rgba8(0, 0, 255, 100)),
            _ => ops => ops.DrawImage(logo, new Point(10, 5), 0.8f),
        };

        using Raster<Rgba8> straight = Noise(64, 48);
        using Raster<PremulRgba8> premultiplied = straight.As<PremulRgba8>();
        using Raster<PremulRgbaF32> floats = straight.As<PremulRgbaF32>();
        straight.Apply(build, Ct);
        premultiplied.Apply(build, Ct);
        floats.Apply(build, Ct);

        // Compared as premultiplied bytes, where both paths' rounding is at most a level or two.
        using Raster<PremulRgba8> expected = straight.As<PremulRgba8>();
        using Raster<PremulRgba8> fromFloats = floats.As<PremulRgba8>();
        int worst = 0, worstFloat = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<byte> e = MemoryMarshal.AsBytes(expected.Row(y));
            ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(premultiplied.Row(y));
            ReadOnlySpan<byte> f = MemoryMarshal.AsBytes(fromFloats.Row(y));
            for (int i = 0; i < e.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(e[i] - a[i]));
                worstFloat = Math.Max(worstFloat, Math.Abs(e[i] - f[i]));
            }
        }

        Assert.True(worst <= 2, $"{chain}: PremulRgba8 differs by {worst} levels");
        Assert.True(worstFloat <= 2, $"{chain}: PremulRgbaF32 differs by {worstFloat} levels");
    }

    [Fact]
    public void ColorAdjustmentsUnpremultiplyFirst()
    {
        using Raster<Rgba8> straight = Noise(32, 8);
        using Raster<PremulRgba8> premultiplied = straight.As<PremulRgba8>();
        straight.Apply(ops => ops.Invert().Contrast(1.3f), Ct);
        premultiplied.Apply(ops => ops.Invert().Contrast(1.3f), Ct);
        using Raster<PremulRgba8> expected = straight.As<PremulRgba8>();
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<byte> e = MemoryMarshal.AsBytes(expected.Row(y));
            ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(premultiplied.Row(y));
            for (int i = 0; i < e.Length; i++)
            {
                Assert.InRange(Math.Abs(e[i] - a[i]), 0, 3);
            }
        }
    }

    [Fact]
    public void RawLoadAndStoreMatchTheirScalarReferences()
    {
        Random random = new(4);
        byte[] bytes = new byte[4 * 1001];
        random.NextBytes(bytes);
        float[] loaded = new float[bytes.Length], reference = new float[bytes.Length];
        Egami.Operations.Resampling.ResizeKernels.LoadRaw8(bytes, loaded);
        Egami.Operations.Resampling.ResizeKernels.Load8Scalar(bytes, reference, opaque: true);
        Assert.Equal(reference, loaded);

        // Working values with a filter's overshoot on both sides, alpha included.
        float[] working = new float[4 * 1001];
        for (int i = 0; i < working.Length; i++)
        {
            working[i] = (float)((random.NextDouble() * 1.6) - 0.3);
        }

        byte[] stored = new byte[working.Length], storedReference = new byte[working.Length];
        Egami.Operations.Resampling.ResizeKernels.StoreRaw8((float[])working.Clone(), stored);
        Egami.Operations.Resampling.ResizeKernels.StoreRaw8Scalar(working, storedReference);
        Assert.Equal(storedReference, stored);
    }

    private static Raster<Rgba8> Noise(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new(11);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                // Smooth color with varied alpha, including fully transparent and fully opaque runs.
                byte a = (byte)(x < 6 ? 0 : x > width - 6 ? 255 : random.Next(256));
                row[x] = new Rgba8((byte)(x * 4), (byte)(y * 5), (byte)((x + y) * 2), a);
            }
        }

        return raster;
    }
}
