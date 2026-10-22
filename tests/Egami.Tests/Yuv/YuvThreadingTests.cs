using System.Runtime.InteropServices;

using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.4 task 6: row bands across threads give the same samples, and repeated conversions allocate nothing.</summary>
public class YuvThreadingTests
{
    [Theory]
    [InlineData(ChromaFormat.Yuv420, ChromaSiting.TopLeft, 8)]
    [InlineData(ChromaFormat.Yuv420, ChromaSiting.Center, 10)]
    [InlineData(ChromaFormat.Yuv422, ChromaSiting.Left, 8)]
    [InlineData(ChromaFormat.Yuv444, ChromaSiting.Center, 12)]
    [InlineData(ChromaFormat.Yuv411, ChromaSiting.Left, 8)]
    [InlineData(ChromaFormat.Yuv410, ChromaSiting.TopLeft, 10)]
    public void OneThreadAndAllCoresGiveTheSameSamples(ChromaFormat format, ChromaSiting siting, int depth)
    {
        EgamiContext one = EgamiContext.Default with { MaxParallelism = 1 };
        EgamiContext all = EgamiContext.Default with { MaxParallelism = Math.Max(Environment.ProcessorCount, 4) };
        YuvColor color = new(YuvMatrix.Bt709, YuvRange.Limited, siting);
        using Raster<Rgba8> source = Raster.Create<Rgba8>(613, 401, all);
        Random random = new(7);
        for (int y = 0; y < source.Height; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(source.Row(y)));
        }

        using Raster<Rgba8> sourceOne = Raster.Create<Rgba8>(613, 401, one);
        for (int y = 0; y < source.Height; y++)
        {
            source.Row(y).CopyTo(sourceOne.Row(y));
        }

        using YuvRaster yuvAll = depth == 8 ? YuvRaster.FromRaster(source, format, color) : YuvRaster.FromRaster(source, format, depth, color);
        using YuvRaster yuvOne = depth == 8 ? YuvRaster.FromRaster(sourceOne, format, color) : YuvRaster.FromRaster(sourceOne, format, depth, color);
        Assert.Equal(Planes(yuvOne), Planes(yuvAll));
        using Raster<Rgba16> backAll = yuvAll.ToRaster<Rgba16>();
        using Raster<Rgba16> backOne = yuvOne.ToRaster<Rgba16>();
        for (int y = 0; y < source.Height; y++)
        {
            Assert.True(backAll.Row(y).SequenceEqual(backOne.Row(y)));
        }
    }

    [Trait("Category", AllocationMeter.Category)]
    [Fact]
    public void RepeatedCopiesAllocateNothingOnOneThread()
    {
        AllocationMeter.SkipUnlessOnDemand();
        EgamiContext one = EgamiContext.Default with { MaxParallelism = 1 };
        using YuvRaster<byte> yuv8 = YuvRaster.Create(640, 480, ChromaFormat.Yuv420, YuvColor.Bt709Limited, context: one);
        using YuvRaster<ushort> yuv10 = YuvRaster.Create(640, 480, ChromaFormat.Yuv420, 10, YuvColor.Bt2020Limited, alpha: true, context: one);
        using Raster<Rgba8> rgba8 = Raster.Create<Rgba8>(640, 480, one);
        using Raster<Rgba16> rgba16 = Raster.Create<Rgba16>(640, 480, one);
        using Raster<RgbaF32> floats = Raster.Create<RgbaF32>(640, 480, one);
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(0, Allocated(() => yuv8.CopyTo(rgba8, cancellationToken: token)));
        Assert.Equal(0, Allocated(() => yuv8.CopyFrom(rgba8, cancellationToken: token)));
        Assert.Equal(0, Allocated(() => yuv10.CopyTo(rgba16, cancellationToken: token)));
        Assert.Equal(0, Allocated(() => yuv10.CopyFrom(rgba16, cancellationToken: token)));
        Assert.Equal(0, Allocated(() => yuv10.CopyTo(floats, cancellationToken: token)));
    }

    [Trait("Category", AllocationMeter.Category)]
    [Fact]
    public void ParallelCopiesAllocateAFixedHandful()
    {
        AllocationMeter.SkipUnlessOnDemand();
        EgamiContext all = EgamiContext.Default with { MaxParallelism = Math.Max(Environment.ProcessorCount, 4) };
        using YuvRaster<byte> small = YuvRaster.Create(640, 480, ChromaFormat.Yuv420, YuvColor.Bt709Limited, context: all);
        using YuvRaster<byte> large = YuvRaster.Create(1920, 1440, ChromaFormat.Yuv420, YuvColor.Bt709Limited, context: all);
        using Raster<Rgba8> smallRgb = Raster.Create<Rgba8>(640, 480, all);
        using Raster<Rgba8> largeRgb = Raster.Create<Rgba8>(1920, 1440, all);
        CancellationToken token = TestContext.Current.CancellationToken;
        long a = Allocated(() => small.CopyTo(smallRgb, cancellationToken: token));
        long b = Allocated(() => large.CopyTo(largeRgb, cancellationToken: token));
        Assert.True(a < 64 * 1024 && b < 64 * 1024, $"{a:N0} and {b:N0} bytes.");
    }

    [Fact]
    public void CancellationStopsBetweenBands()
    {
        using YuvRaster<byte> yuv = YuvRaster.Create(64, 64, ChromaFormat.Yuv420, YuvColor.Jpeg);
        using Raster<Rgba8> rgb = Raster.Create<Rgba8>(64, 64);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => yuv.CopyTo(rgb, cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => yuv.CopyFrom(rgb, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void ConcurrentConversionsOfOneRasterAgree()
    {
        EgamiContext one = EgamiContext.Default with { MaxParallelism = 1 };
        using YuvRaster<byte> yuv = YuvRaster.Create(200, 100, ChromaFormat.Yuv420, YuvColor.Bt601Limited, context: one);
        YuvConversionTests.Fill(yuv, new Random(2));
        using Raster<Rgba8> expected = yuv.ToRaster<Rgba8>();
        Parallel.For(0, 16, _ =>
        {
            using Raster<Rgba8> actual = yuv.ToRaster<Rgba8>();
            for (int y = 0; y < 100; y++)
            {
                Assert.True(actual.Row(y).SequenceEqual(expected.Row(y)));
            }
        });
    }

    private static byte[] Planes(YuvRaster raster)
    {
        List<byte> bytes = [];
        if (raster is YuvRaster<byte> b)
        {
            foreach (YuvPlane<byte>? plane in new[] { b.Y, b.U, b.V, b.A })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    bytes.AddRange(plane.Row(y).ToArray());
                }
            }
        }
        else if (raster is YuvRaster<ushort> w)
        {
            foreach (YuvPlane<ushort>? plane in new[] { w.Y, w.U, w.V, w.A })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    bytes.AddRange(MemoryMarshal.AsBytes(plane.Row(y)).ToArray());
                }
            }
        }

        return [.. bytes];
    }

    /// <summary>The fewest bytes one call allocated on this thread over several tries (other tests can empty the shared pools).</summary>
    private static long Allocated(Action action) => AllocationMeter.Fewest(action, warmUps: 3, tries: 10);
}
