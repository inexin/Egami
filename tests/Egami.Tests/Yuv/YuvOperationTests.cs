using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.4 task 10: crop on chroma boundaries and resize per plane.</summary>
public class YuvOperationTests
{
    [Theory]
    [InlineData(ChromaFormat.Yuv420, 2, 4, 9, 7)]
    [InlineData(ChromaFormat.Yuv422, 2, 3, 10, 5)]
    [InlineData(ChromaFormat.Yuv444, 1, 3, 5, 5)]
    [InlineData(ChromaFormat.Yuv400, 3, 1, 4, 4)]
    [InlineData(ChromaFormat.Yuv411, 4, 3, 9, 5)]
    [InlineData(ChromaFormat.Yuv410, 8, 4, 7, 9)]
    public void CropCopiesEveryPlaneExactly(ChromaFormat format, int x, int y, int width, int height)
    {
        using YuvRaster<ushort> yuv = YuvRaster.Create(17, 13, format, 12, YuvColor.Bt2020Limited, alpha: true);
        YuvConversionTests.Fill(yuv, new Random(1));
        using YuvRaster<ushort> crop = yuv.Crop(new Rect(x, y, width, height));
        Assert.Equal((width, height, format, 12), (crop.Width, crop.Height, crop.ChromaFormat, crop.BitDepth));
        (int sx, int sy) = YuvReference.Factors(format);
        for (int row = 0; row < height; row++)
        {
            Assert.True(crop.Y.Row(row).SequenceEqual(yuv.Y.Row(y + row).Slice(x, width)));
            Assert.True(crop.A!.Row(row).SequenceEqual(yuv.A!.Row(y + row).Slice(x, width)));
        }

        for (int row = 0; crop.U is not null && row < crop.U.Height; row++)
        {
            Assert.True(crop.U.Row(row).SequenceEqual(yuv.U!.Row((y / sy) + row).Slice(x / sx, crop.U.Width)));
            Assert.True(crop.V!.Row(row).SequenceEqual(yuv.V!.Row((y / sy) + row).Slice(x / sx, crop.V.Width)));
        }
    }

    [Fact]
    public void MisalignedOrOutsideCropsThrow()
    {
        using YuvRaster<byte> yuv = YuvRaster.Create(16, 16, ChromaFormat.Yuv420, YuvColor.Jpeg);
        Assert.Throws<ArgumentException>(() => yuv.Crop(new Rect(1, 0, 4, 4)));
        Assert.Throws<ArgumentException>(() => yuv.Crop(new Rect(0, 3, 4, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => yuv.Crop(new Rect(0, 0, 17, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => yuv.Crop(new Rect(0, 0, 0, 4)));
        using YuvRaster<byte> yuv422 = YuvRaster.Create(16, 16, ChromaFormat.Yuv422, YuvColor.Jpeg);
        using YuvRaster odd = yuv422.Crop(new Rect(2, 3, 5, 5));
        Assert.Equal(3, ((YuvRaster<byte>)odd).U!.Width);
        using YuvRaster<byte> yuv411 = YuvRaster.Create(16, 16, ChromaFormat.Yuv411, YuvColor.Jpeg);
        Assert.Throws<ArgumentException>(() => yuv411.Crop(new Rect(2, 0, 4, 4)));
        using YuvRaster quarter = yuv411.Crop(new Rect(4, 3, 5, 5));
        Assert.Equal((2, 5), (((YuvRaster<byte>)quarter).U!.Width, ((YuvRaster<byte>)quarter).U!.Height));
        using YuvRaster<byte> yuv410 = YuvRaster.Create(16, 16, ChromaFormat.Yuv410, YuvColor.Jpeg);
        Assert.Throws<ArgumentException>(() => yuv410.Crop(new Rect(4, 2, 4, 4)));
    }

    [Theory]
    [InlineData(ResampleFilter.Bicubic)]
    [InlineData(ResampleFilter.Bilinear)]
    [InlineData(ResampleFilter.Lanczos3)]
    public void ResizeMatchesTheGrayResizeOfEachPlane(ResampleFilter filter)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-444.jpg"));
        using YuvRaster<byte> yuv8 = YuvRaster.FromRaster(photo, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        using YuvRaster<byte> small8 = yuv8.Resize(123, 77, filter);
        Assert.Equal((123, 77), (small8.Width, small8.Height));
        Assert.Equal((62, 39), (small8.U!.Width, small8.U.Height));
        Same<byte, Gray8>(yuv8.Y, small8.Y, filter, 255);
        Same<byte, Gray8>(yuv8.U!, small8.U, filter, 255);

        // 10-bit samples resize as Gray16 values and are clamped to 1023 where the filter overshoots.
        using YuvRaster<ushort> yuv10 = YuvRaster.FromRaster(photo, ChromaFormat.Yuv420, 10, YuvColor.Bt2020Limited);
        using YuvRaster<ushort> large10 = yuv10.Resize(500, 401, filter);
        Same<ushort, Gray16>(yuv10.Y, large10.Y, filter, 1023);
        Same<ushort, Gray16>(yuv10.V!, large10.V!, filter, 1023);
    }

    [Fact]
    public void ResizeClampsOvershootToTheDepth()
    {
        using YuvRaster<ushort> yuv = YuvRaster.Create(8, 8, ChromaFormat.Yuv444, 10, YuvColor.Bt709Limited);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                yuv.Y.Row(y)[x] = (ushort)(x < 4 ? 0 : 1023);
            }
        }

        using YuvRaster<ushort> large = yuv.Resize(37, 8, ResampleFilter.Lanczos3);
        for (int x = 0; x < 37; x++)
        {
            Assert.InRange(large.Y.Row(3)[x], 0, 1023);
        }

        Assert.Equal(1023, large.Y.Row(3)[30]);
    }

    [Fact]
    public void ResizeToFillsAnExistingRasterAsResizeDoes()
    {
        using YuvRaster<byte> source = YuvRaster.Create(97, 61, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        YuvConversionTests.Fill(source, new Random(3));
        using YuvRaster<byte> expected = source.Resize(40, 30);
        using YuvRaster<byte> target = YuvRaster.Create(40, 30, ChromaFormat.Yuv420, YuvColor.Bt709Limited, alpha: true);
        for (int frame = 0; frame < 2; frame++)
        {
            source.ResizeTo(target);
        }

        for (int y = 0; y < 30; y++)
        {
            Assert.True(target.Y.Row(y).SequenceEqual(expected.Y.Row(y)));
            Assert.Equal(255, target.A!.Row(y)[39]);
        }

        for (int y = 0; y < 15; y++)
        {
            Assert.True(target.V!.Row(y).SequenceEqual(expected.V!.Row(y)));
        }

        using YuvRaster<byte> other = YuvRaster.Create(40, 30, ChromaFormat.Yuv422, YuvColor.Bt709Limited);
        using YuvRaster<byte> otherColor = YuvRaster.Create(40, 30, ChromaFormat.Yuv420, YuvColor.Jpeg);
        using YuvRaster<ushort> deeper = YuvRaster.Create(40, 30, ChromaFormat.Yuv420, 10, YuvColor.Bt709Limited);
        Assert.Throws<ArgumentException>(() => source.ResizeTo(other));
        Assert.Throws<ArgumentException>(() => source.ResizeTo(otherColor));
        Assert.Throws<ArgumentException>(() => source.ResizeTo(deeper));
        Assert.Throws<ArgumentException>(() => source.ResizeTo(source));
    }

    [Fact]
    public void WrappedPlanesResizeToo()
    {
        ushort[] y = new ushort[64], u = new ushort[16], v = new ushort[16];
        Array.Fill(y, (ushort)500);
        using YuvRaster<ushort> wrapped = YuvRaster.Wrap<ushort>(y, 0, u, 0, v, 0, 8, 8, ChromaFormat.Yuv420, 10, YuvColor.Bt709Limited);
        using YuvRaster resized = wrapped.Resize(4, 4);
        Assert.Equal(500, ((YuvRaster<ushort>)resized).Y.Row(2)[2]);
    }

    private static void Same<TSample, TGray>(YuvPlane<TSample> source, YuvPlane<TSample> actual, ResampleFilter filter, int max)
        where TSample : unmanaged
        where TGray : unmanaged, IPixelType<TGray>
    {
        using Raster<TGray> gray = Raster.Create<TGray>(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<TSample, TGray>(source.Row(y)).CopyTo(gray.Row(y));
        }

        using Raster resized = gray.Derive(ops => ops.Resize(actual.Width, actual.Height, filter));
        Raster<TGray> typed = (Raster<TGray>)resized;

        // The planes resize in one float channel with the same weights; the gray resize goes through 0..1 floats in four
        // channels, so a sample may round the other way.
        long same = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            ReadOnlySpan<TSample> expected = System.Runtime.InteropServices.MemoryMarshal.Cast<TGray, TSample>(typed.Row(y));
            for (int x = 0; x < actual.Width; x++)
            {
                int e = Math.Min(YuvConvert.Sample(expected, x, int.MaxValue), max), a = YuvConvert.Sample(actual.Row(y), x, int.MaxValue);
                Assert.InRange(a, e - 1, e + 1);
                same += a == e ? 1 : 0;
            }
        }

        Assert.True(same >= 0.99 * actual.Width * actual.Height, $"{same} of {actual.Width * actual.Height} identical.");
    }
}
