using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>Argb8, Abgr8 and Alpha8 (Phase 2.1.1 task 4).</summary>
public class AlphaFirstTypeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AlphaFirstTypesStoreAlphaInTheFirstByte()
    {
        Rgba8 color = new(10, 20, 30, 40);
        Assert.Equal(new byte[] { 40, 10, 20, 30 }, Bytes(Argb8.FromRgba8(color)));
        Assert.Equal(new byte[] { 40, 30, 20, 10 }, Bytes(Abgr8.FromRgba8(color)));
        Assert.Equal(color, new Argb8(40, 10, 20, 30).ToRgba8());
        Assert.Equal(color, new Abgr8(40, 30, 20, 10).ToRgba8());
        Assert.Equal("Argb8(40, 10, 20, 30)", Argb8.FromRgba8(color).ToString());
    }

    [Fact]
    public void EveryByteValueSurvivesTheShuffles()
    {
        Rgba8[] all = new Rgba8[256 * 4];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = new Rgba8((byte)i, (byte)(i * 7), (byte)(255 - i), (byte)(i >> 2));
        }

        Argb8[] argb = new Argb8[all.Length];
        Abgr8[] abgr = new Abgr8[all.Length];
        Bgra8[] bgra = new Bgra8[all.Length];
        Rgba8[] back = new Rgba8[all.Length];
        PixelConvert.Convert<Rgba8, Argb8>(all, argb);
        PixelConvert.Convert<Argb8, Abgr8>(argb, abgr);
        PixelConvert.Convert<Abgr8, Bgra8>(abgr, bgra);
        PixelConvert.Convert<Bgra8, Rgba8>(bgra, back);
        Assert.Equal(all, back);
    }

    [Fact]
    public void Alpha8KeepsOnlyAlphaAndReadsAsBlack()
    {
        Assert.Equal(new Alpha8(77), Alpha8.FromRgba8(new Rgba8(1, 2, 3, 77)));
        Assert.Equal(new Rgba8(0, 0, 0, 77), new Alpha8(77).ToRgba8());

        Alpha8[] masks = new Alpha8[2];
        PixelConvert.Convert<Rgb8, Alpha8>([new Rgb8(9, 9, 9), new Rgb8(0, 0, 0)], masks);
        Assert.Equal([new Alpha8(255), new Alpha8(255)], masks);

        Gray8[] gray = new Gray8[1];
        PixelConvert.Convert<Alpha8, Gray8>([new Alpha8(200)], gray);
        Assert.Equal(new Gray8(0), gray[0]);
    }

    [Fact]
    public void GeometryOnAlphaFirstTypesIsExact()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(9, 5);
        source.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)(x * 20), (byte)(y * 40), (byte)(x + y), (byte)(100 + x));
            }
        }, Ct);

        using Raster<Rgba8> expected = source.Derive(ops => ops.Crop(1, 1, 7, 4).FlipHorizontal().Rotate(90), Ct);
        using Raster<Abgr8> abgr = source.As<Abgr8>();
        abgr.Apply(ops => ops.Crop(1, 1, 7, 4).FlipHorizontal().Rotate(90), Ct);
        using Raster<Rgba8> back = abgr.As<Rgba8>();
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(back.Row(y)), $"row {y}");
        }
    }

    [Fact]
    public void BlurringAMaskBlursItsAlpha()
    {
        using Raster<Alpha8> mask = Raster.Create<Alpha8>(21, 21);
        mask.Row(10)[10] = new Alpha8(255);
        mask.Apply(ops => ops.Blur(2), Ct);
        Assert.InRange(mask.Row(10)[10].A, 1, 254);
        Assert.True(mask.Row(10)[11].A > 0);
        Assert.Equal(0, mask.Row(0)[0].A);
    }

    private static byte[] Bytes<T>(T pixel)
        where T : unmanaged
    {
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in pixel)).ToArray();
    }
}
