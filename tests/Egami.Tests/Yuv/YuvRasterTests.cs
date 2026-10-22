using System.Buffers;

using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.4 task 2: creating, wrapping and disposing YUV rasters.</summary>
public class YuvRasterTests
{
    [Theory]
    [InlineData(ChromaFormat.Yuv420, 17, 9, 9, 5)]
    [InlineData(ChromaFormat.Yuv420, 1, 1, 1, 1)]
    [InlineData(ChromaFormat.Yuv422, 17, 9, 9, 9)]
    [InlineData(ChromaFormat.Yuv444, 17, 9, 17, 9)]
    [InlineData(ChromaFormat.Yuv400, 17, 9, 0, 0)]
    [InlineData(ChromaFormat.Yuv411, 17, 9, 5, 9)]
    [InlineData(ChromaFormat.Yuv410, 17, 9, 5, 3)]
    [InlineData(ChromaFormat.Yuv410, 1, 1, 1, 1)]
    public void PlaneSizesRoundUp(ChromaFormat format, int width, int height, int chromaWidth, int chromaHeight)
    {
        using YuvRaster<byte> yuv = YuvRaster.Create(width, height, format, YuvColor.Jpeg);
        Assert.Equal((width, height), (yuv.Y.Width, yuv.Y.Height));
        Assert.Equal(8, yuv.BitDepth);
        Assert.False(yuv.HasAlpha);
        if (chromaWidth == 0)
        {
            Assert.Null(yuv.U);
            Assert.Null(yuv.V);
        }
        else
        {
            Assert.Equal((chromaWidth, chromaHeight), (yuv.U!.Width, yuv.U.Height));
            Assert.Equal((chromaWidth, chromaHeight), (yuv.V!.Width, yuv.V.Height));
        }

        for (int y = 0; y < height; y++)
        {
            Assert.All(yuv.Y.Row(y).ToArray(), s => Assert.Equal(0, s));
        }
    }

    [Fact]
    public void PlanesComeFromOneAllocationCountedByTheMemorySource()
    {
        PooledPixelMemorySource source = new();
        EgamiContext context = EgamiContext.Default with { Memory = source };
        YuvRaster<ushort> yuv = YuvRaster.Create(1920, 1080, ChromaFormat.Yuv420, 10, YuvColor.Bt2020Limited, alpha: true, context);
        Assert.Equal(1, source.Usage.LiveBlocks);
        Assert.True(source.Usage.LiveBytes >= 2 * ((1920 * 1080 * 2) + (960 * 540 * 2)));
        Assert.True(yuv.Y.TryGetContiguous(out Span<ushort> luma));
        Assert.Equal(1920 * 1080, luma.Length);
        Assert.NotNull(yuv.A);
        yuv.Dispose();
        yuv.Dispose();
        Assert.Equal(0, source.Usage.LiveBlocks);
        Assert.Throws<ObjectDisposedException>(() => yuv.Y.Row(0));
    }

    [Fact]
    public void LargePlanesAreSplitIntoBlocksOfRows()
    {
        PooledPixelMemorySource source = new() { MaxBlockBytes = 1000 };
        EgamiContext context = EgamiContext.Default with { Memory = source };
        using YuvRaster<byte> yuv = YuvRaster.Create(100, 25, ChromaFormat.Yuv420, YuvColor.Jpeg, context: context);
        Assert.True(source.Usage.LiveBlocks > 3);
        Assert.False(yuv.Y.TryGetContiguous(out _));
        for (int y = 0; y < 25; y++)
        {
            yuv.Y.Row(y).Fill((byte)y);
        }

        for (int y = 0; y < 25; y++)
        {
            Assert.Equal((byte)y, yuv.Y.Row(y)[99]);
        }

        PixelPlane<Gray8> gray = yuv.Y.AsGray8();
        Assert.Equal(24, gray.Row(24)[0].L);
    }

    [Fact]
    public void LimitsApplyToTheLumaSize()
    {
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 100 } };
        Assert.Throws<LimitExceededException>(() => YuvRaster.Create(11, 10, ChromaFormat.Yuv420, YuvColor.Jpeg, context: context));
        using YuvRaster<byte> ok = YuvRaster.Create(10, 10, ChromaFormat.Yuv444, YuvColor.Jpeg, alpha: true, context: context);
        EgamiContext bytes = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 1000 } };
        Assert.Throws<LimitExceededException>(() => YuvRaster.Create(20, 20, ChromaFormat.Yuv444, YuvColor.Jpeg, context: bytes));
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Create(0, 1, ChromaFormat.Yuv420, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Create(1, 1, (ChromaFormat)7, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Create(1, 1, ChromaFormat.Yuv420, 9, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Create(1, 1, ChromaFormat.Yuv420, 8, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Create(1, 1, ChromaFormat.Yuv420, new YuvColor((YuvMatrix)9, YuvRange.Full, ChromaSiting.Center)));
        Assert.Throws<NotSupportedException>(() => YuvRaster.Wrap<int>(new int[1], 0, new int[1], 0, new int[1], 0, 1, 1, ChromaFormat.Yuv444, 8, YuvColor.Jpeg));
        Assert.Throws<NotSupportedException>(() => YuvRaster.Create(2, 2, ChromaFormat.Yuv444, 10, YuvColor.Jpeg).Y.AsGray8());
    }

    [Fact]
    public void WrapUsesTheCallersMemoryWithPaddedStrides()
    {
        // 17 × 9 at 4:2:0 with rows padded to 32 luma and 16 chroma samples, as a decoder hands them out.
        ushort[] y = new ushort[(8 * 32) + 17], u = new ushort[(4 * 16) + 9], v = new ushort[(4 * 16) + 9];
        for (int i = 0; i < y.Length; i++)
        {
            y[i] = (ushort)i;
        }

        Owner owner = new();
        YuvRaster<ushort> yuv = YuvRaster.Wrap<ushort>(y, 32, u, 16, v, 16, 17, 9, ChromaFormat.Yuv420, 10, YuvColor.Bt709Limited, owner);
        Assert.Equal((ushort)(32 * 3), yuv.Y.Row(3)[0]);
        Assert.Equal(17, yuv.Y.Row(8).Length);
        Assert.False(yuv.Y.TryGetContiguous(out _));
        yuv.U!.Row(4)[8] = 77;
        Assert.Equal(77, u[(4 * 16) + 8]);
        yuv.Dispose();
        Assert.Equal(1, owner.Disposed);
        yuv.Dispose();
        Assert.Equal(1, owner.Disposed);
    }

    [Fact]
    public void WrapChecksSizesAndStridesUpFront()
    {
        byte[] plane = new byte[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<byte>(plane, 9, plane, 0, plane, 0, 10, 10, ChromaFormat.Yuv444, 8, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<byte>(plane, 0, plane.AsMemory(0, 99), 0, plane, 0, 10, 10, ChromaFormat.Yuv444, 8, YuvColor.Jpeg));
        Assert.Throws<ArgumentException>(() => YuvRaster.Wrap<byte>(plane, 0, plane, 0, plane, 0, 10, 10, ChromaFormat.Yuv400, 8, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<byte>(plane, 0, default, 0, default, 0, 10, 10, ChromaFormat.Yuv420, 8, YuvColor.Jpeg));
        Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<byte>(plane, 0, plane, 0, plane, 0, plane.AsMemory(1), 0, 10, 10, ChromaFormat.Yuv444, 8, YuvColor.Jpeg));
        using YuvRaster<byte> gray = YuvRaster.Wrap<byte>(plane, 0, default, 0, default, 0, 10, 10, ChromaFormat.Yuv400, 8, YuvColor.Jpeg);
        Assert.True(gray.Y.TryGetContiguous(out Span<byte> all));
        Assert.Equal(100, all.Length);
        using YuvRaster<byte> alpha = YuvRaster.Wrap<byte>(plane, 0, plane, 0, plane, 0, plane, 0, 10, 10, ChromaFormat.Yuv444, 8, YuvColor.Jpeg);
        Assert.True(alpha.HasAlpha);
    }

    [Fact]
    public void AsGray8SharesTheSamples()
    {
        using YuvRaster<byte> yuv = YuvRaster.Create(5, 3, ChromaFormat.Yuv420, YuvColor.Jpeg);
        PixelPlane<Gray8> gray = yuv.Y.AsGray8();
        gray.Row(2)[4] = new Gray8(200);
        Assert.Equal(200, yuv.Y.Row(2)[4]);
        byte[] wrapped = new byte[30];
        using YuvRaster<byte> view = YuvRaster.Wrap<byte>(wrapped, 6, default, 0, default, 0, 5, 3, ChromaFormat.Yuv400, 8, YuvColor.Jpeg);
        view.Y.AsGray8().Row(1)[2] = new Gray8(9);
        Assert.Equal(9, wrapped[8]);
    }

    [Fact]
    public void H273CodesMapBothWays()
    {
        Assert.True(YuvColor.TryFromH273(1, false, ChromaSiting.Left, out YuvColor bt709));
        Assert.Equal(YuvColor.Bt709Limited, bt709);
        Assert.True(YuvColor.TryFromH273(5, true, ChromaSiting.Center, out YuvColor jpeg));
        Assert.Equal(YuvColor.Jpeg, jpeg);
        Assert.True(YuvColor.TryFromH273(9, false, ChromaSiting.TopLeft, out YuvColor bt2020));
        Assert.Equal(YuvColor.Bt2020Limited, bt2020);
        Assert.True(YuvColor.TryFromH273(0, true, ChromaSiting.Center, out YuvColor gbr));
        Assert.Equal(YuvMatrix.Identity, gbr.Matrix);
        Assert.False(YuvColor.TryFromH273(2, true, ChromaSiting.Center, out _));
        Assert.False(YuvColor.TryFromH273(10, true, ChromaSiting.Center, out _));
        Assert.True(new Egami.Colors.ColorCodePoints(1, 1, 1, false).TryGetYuvColor(ChromaSiting.Left, out YuvColor tagged));
        Assert.Equal(YuvColor.Bt709Limited, tagged);
        Egami.Colors.ColorCodePoints unknown = Egami.Colors.ColorCodePoints.Srgb with { MatrixCoefficients = 2 };
        Assert.False(unknown.TryGetYuvColor(ChromaSiting.Center, out _));
        Assert.Equal([0, 1, 6, 9], Enum.GetValues<YuvMatrix>().Select(m => new YuvColor(m, YuvRange.Full, ChromaSiting.Center).ToH273()).Order());
    }

    [Fact]
    public void MetadataTravelsBothWays()
    {
        using Raster<Rgb8> rgb = Raster.Create<Rgb8>(4, 4);
        rgb.Metadata.Xmp = new byte[] { 1, 2, 3 };
        rgb.Metadata.CodePoints = new Egami.Colors.ColorCodePoints(1, 1, 1, false);
        using YuvRaster<byte> yuv = YuvRaster.FromRaster(rgb, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        Assert.Equal(3, yuv.Metadata.Xmp.Length);
        Assert.False(yuv.HasAlpha);
        using Raster back = yuv.ToRaster();
        Assert.Equal(3, back.Metadata.Xmp.Length);
        Assert.Equal(new Egami.Colors.ColorCodePoints(1, 1, 0, true), back.Metadata.CodePoints);
        Assert.IsType<Raster<Rgba8>>(back);
    }

    [Fact]
    public void AlphaPlanesComeOnlyWhenAsked()
    {
        using Raster<Rgba8> rgba = Raster.Create(4, 4, new Rgba8(10, 20, 30, 40));
        using YuvRaster<byte> dropped = YuvRaster.FromRaster(rgba, ChromaFormat.Yuv420, YuvColor.Jpeg);
        Assert.False(dropped.HasAlpha);
        using YuvRaster<byte> kept = YuvRaster.FromRaster(rgba, ChromaFormat.Yuv420, YuvColor.Jpeg, alpha: true);
        Assert.Equal(40, kept.A!.Row(3)[3]);
        using Raster<Rgb8> rgb = Raster.Create<Rgb8>(4, 4);
        using YuvRaster<ushort> opaque = YuvRaster.FromRaster(rgb, ChromaFormat.Yuv444, 10, YuvColor.Jpeg, alpha: true);
        Assert.Equal(1023, opaque.A!.Row(0)[0]);
    }

    [Fact]
    public void CopyToChecksSizes()
    {
        using YuvRaster<byte> yuv = YuvRaster.Create(4, 4, ChromaFormat.Yuv420, YuvColor.Jpeg);
        using Raster<Rgba8> wrong = Raster.Create<Rgba8>(4, 5);
        Assert.Throws<ArgumentException>(() => yuv.CopyTo(wrong, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => yuv.CopyFrom(wrong, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => yuv.CopyTo<Rgba8>(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class Owner : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }
}
