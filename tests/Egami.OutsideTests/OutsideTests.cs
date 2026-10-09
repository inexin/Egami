using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.OutsideTests;

/// <summary>What an outside package can do with only Egami's public API (Phase 1.5 API review, decided 2026-10-09).</summary>
public class OutsideTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AnOutsidePixelTypeConvertsThroughItsOwnMethods()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(9, 4, new Rgba8(255, 0, 255, 255));
        using Raster<Rgb565> packed = source.As<Rgb565>();
        Assert.Equal(0xF81F, packed.Row(0)[0].Value);
        using Raster<Rgba8> back = packed.As<Rgba8>();
        Assert.Equal(new Rgba8(255, 0, 255, 255), back.Row(3)[8]);
        packed.Apply(ops => ops.Resize(4, 2).FlipHorizontal(), Ct);
        Assert.Equal((4, 2), (packed.Width, packed.Height));
        Assert.Equal(0xF81F, packed.Row(1)[3].Value);
    }

    [Fact]
    public void AnOutsideMemorySourceServesEveryAllocation()
    {
        CountingMemorySource memory = new();
        EgamiContext context = new() { Memory = memory };
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(16, 16, context);
        raster.Apply(ops => ops.Resize(32, 32), Ct);
        Assert.True(memory.Allocations >= 2);
    }

    [Fact]
    public void AnOutsideOperationRunsInTheChain()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(40, 30, new Rgba8(10, 20, 30, 128));
        raster.Apply(ops => ops.Add(new InvertOperation()), Ct);
        Assert.Equal(new Rgba8(245, 235, 225, 128), raster.Row(29)[39]);
    }

    [Fact]
    public void AnOutsideOperationThatResizesUpdatesExifAndFrames()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(40, 30);
        raster.Metadata.Exif = ExifData.Parse(ReadOnlySpan<byte>.Empty);
        raster.Metadata.Exif.Set(ExifKey.PixelXDimension, 40u);
        raster.Metadata.Exif.Set(ExifKey.PixelYDimension, 30u);
        raster.Apply(ops => ops.Add(new HalveOperation()), Ct);
        Assert.Equal((20, 15), (raster.Width, raster.Height));
        Assert.Equal(20, raster.Metadata.Exif!.TryGet(ExifKey.PixelXDimension, out ExifEntry? x) ? x.AsInteger() : -1);
    }

    [Fact]
    public void StridedBytesAreWrappedAndTheirOwnerIsReleased()
    {
        // Rgb8 rows of 5 pixels (15 bytes) padded to 16 bytes, as decoders align them.
        byte[] bytes = new byte[16 * 3];
        bytes[16 + 3] = 200;
        DisposalCounter owner = new();
        Raster<Rgb8> raster = Raster.Wrap<Rgb8>(bytes, 5, 3, byteStride: 16, owner);
        Assert.Equal(200, raster.Row(1)[1].R);
        raster.Row(2)[4] = new Rgb8(1, 2, 3);
        Assert.Equal(3, bytes[(2 * 16) + 14]);
        raster.Dispose();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void AnOutsideSingleFrameFormatLoadsProbesAndSaves()
    {
        EgamiContext context = EgamiContext.Default.WithFormat(TestFormat.Info);
        using Raster<Rgba8> source = Raster.Create<Rgba8>(7, 5, new Rgba8(1, 2, 3, 4));
        using MemoryStream stream = new();
        source.Save(stream, TestFormat.Info);
        byte[] data = stream.ToArray();
        RasterInfo info = Raster.Probe(data, context);
        Assert.Equal((TestFormat.Info, 7, 5), (info.Format, info.Width, info.Height));
        using Raster<Bgra8> loaded = Raster.Load<Bgra8>(data, context);
        Assert.Equal(new Bgra8(3, 2, 1, 4), loaded.Row(4)[6]);
        Assert.Equal("EGT1", loaded.Metadata.FormatName);

        using Raster<Rgba8> cut = Raster.Load<Rgba8>(data.AsMemory(0, data.Length - 10), context);
        Assert.Single(cut.Metadata.Warnings);
    }

    [Fact]
    public void AnOutsideAnimatedFormatBuildsFramesWithinTheLimits()
    {
        EgamiContext context = EgamiContext.Default.WithFormat(AnimatedTestFormat.Info);
        using Raster<Rgba8> source = Raster.Create<Rgba8>(6, 4, new Rgba8(9, 9, 9, 255));
        source.Frames.Add(source.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(80);
        source.Frames[1].Row(3)[5] = new Rgba8(1, 2, 3, 255);
        using MemoryStream stream = new();
        source.Save(stream, AnimatedTestFormat.Info);
        byte[] data = stream.ToArray();

        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, context);
        Assert.Equal(2, loaded.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(80), loaded.Frames[1].Metadata.Delay);
        Assert.Equal(new Rgba8(1, 2, 3, 255), loaded.Frames[1].Row(3)[5]);

        EgamiContext strict = context with { Limits = SafetyLimits.Default with { MaxFrames = 1 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(data, strict));
        EgamiContext tight = context with { Limits = SafetyLimits.Default with { MaxTotalBytes = 6 * 4 * 4 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(data, tight));
    }

    [Fact]
    public void TheDefaultContextIsFixedOnceRead()
    {
        _ = EgamiContext.Default;
        Assert.Throws<InvalidOperationException>(() => EgamiContext.SetDefault(new EgamiContext()));
    }
}
