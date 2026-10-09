using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests;

public class FrameTests
{
    [Fact]
    public void AddCopiesPixelsAndMetadataFromAnotherRaster()
    {
        using Raster<Rgba8> anim = Raster.Create<Rgba8>(4, 3);
        using Raster<Rgba8> other = Raster.Create(4, 3, Rgba8.Red);
        other.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(70);

        Frame<Rgba8> added = anim.Frames.Add(other.Frames[0]);
        Assert.Equal(2, anim.FrameCount);
        Assert.Same(added, anim.Frames[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(70), added.Metadata.Delay);
        Assert.NotSame(other.Frames[0].Metadata, added.Metadata);

        // A copy: changing the source afterwards does not change the new frame.
        other.SetPixel(0, 0, Rgba8.Blue);
        Assert.Equal(Rgba8.Red, added.Row(0)[0]);
    }

    [Fact]
    public void AddFromTheSameRasterAndInsert()
    {
        using Raster<Gray8> anim = Raster.Create(2, 2, new Gray8(1));
        anim.Frames.Add(anim.Frames[0]);
        anim.Frames[1].Row(0)[0] = new Gray8(2);
        anim.Frames.Insert(1, Raster.Create(2, 2, new Gray8(3)).Frames[0]);
        Assert.Equal([1, 3, 2], anim.Frames.Select(f => (int)f.Row(0)[0].L));
    }

    [Fact]
    public void RemoveAtAndMove()
    {
        using Raster<Gray8> anim = Build(4);
        anim.Frames.Move(3, 0);
        Assert.Equal([3, 0, 1, 2], Values(anim));
        anim.Frames.Move(0, 3);
        Assert.Equal([0, 1, 2, 3], Values(anim));
        anim.Frames.RemoveAt(1);
        Assert.Equal([0, 2, 3], Values(anim));
        anim.Frames.RemoveAt(2);
        anim.Frames.RemoveAt(0);
        Assert.Equal([2], Values(anim));
        Assert.Throws<InvalidOperationException>(() => anim.Frames.RemoveAt(0));
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        using Raster<Gray8> anim = Build(2);
        using Raster<Gray8> other = Raster.Create<Gray8>(3, 2);
        Assert.Throws<ArgumentException>(() => anim.Frames.Add(other.Frames[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.Frames.Insert(3, anim.Frames[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.Frames.Insert(-1, anim.Frames[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.Frames.RemoveAt(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.Frames.Move(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.CopyFrame(2));
    }

    [Fact]
    public void AddingPastMaxTotalBytesThrows()
    {
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 3 * 100 } };
        using Raster<Gray8> anim = Raster.Create<Gray8>(10, 10, context);
        anim.Frames.Add(anim.Frames[0]);
        anim.Frames.Add(anim.Frames[0]);
        Assert.Throws<LimitExceededException>(() => anim.Frames.Add(anim.Frames[0]));
        Assert.Equal(3, anim.FrameCount);
    }

    [Fact]
    public void CopyFrameReturnsOneFrameWithRasterMetadata()
    {
        using Raster<Gray8> anim = Build(3);
        anim.Metadata.LoopCount = 4;
        anim.Frames[2].Metadata.Delay = TimeSpan.FromSeconds(1);
        using Raster<Gray8> still = anim.CopyFrame(2);
        Assert.Equal(1, still.FrameCount);
        Assert.Equal(2, still.Row(0)[0].L);
        Assert.Equal(4, still.Metadata.LoopCount);
        Assert.Equal(TimeSpan.FromSeconds(1), still.Frames[0].Metadata.Delay);
        still.Row(0)[0] = new Gray8(9);
        Assert.Equal(2, anim.Frames[2].Row(0)[0].L);
    }

    [Fact]
    public void RemovedFramesReturnTheirMemory()
    {
        CountingSource memory = new();
        EgamiContext context = EgamiContext.Default with { Memory = memory };
        using (Raster<Gray8> anim = Raster.Create<Gray8>(8, 8, context))
        {
            anim.Frames.Add(anim.Frames[0]);
            anim.Frames.Add(anim.Frames[0]);
            Assert.Equal(3, memory.Live);
            anim.Frames.RemoveAt(1);
            Assert.Equal(2, memory.Live);
        }

        Assert.Equal(0, memory.Live);
    }

    [Fact]
    public void GeometryClearsStoredLayoutButColorDoesNot()
    {
        using Raster<Rgba8> anim = Raster.Create<Rgba8>(8, 8);
        anim.Frames.Add(anim.Frames[0]);
        foreach (Frame<Rgba8> frame in anim.Frames)
        {
            frame.Metadata.Bounds = new Rect(1, 1, 2, 2);
            frame.Metadata.FormatDetails = new TestDetails();
            frame.Metadata.Delay = TimeSpan.FromMilliseconds(40);
        }

        anim.Apply(ops => ops.Brightness(1.1f), TestContext.Current.CancellationToken);
        Assert.All(anim.Frames, f => Assert.NotNull(f.Metadata.Bounds));

        using Raster<Rgba8> flipped = anim.Derive(ops => ops.Flip(FlipMode.Horizontal), TestContext.Current.CancellationToken);
        Assert.All(flipped.Frames, f => Assert.Null(f.Metadata.Bounds));
        Assert.All(anim.Frames, f => Assert.NotNull(f.Metadata.Bounds));

        anim.Apply(ops => ops.Resize(4, 4), TestContext.Current.CancellationToken);
        Assert.All(anim.Frames, f =>
        {
            Assert.Null(f.Metadata.Bounds);
            Assert.Null(f.Metadata.FormatDetails);
            Assert.Equal(TimeSpan.FromMilliseconds(40), f.Metadata.Delay);
        });
    }

    [Theory]
    [InlineData("resize")]
    [InlineData("crop")]
    [InlineData("rotate")]
    [InlineData("blur")]
    public void OperationsRunOnAHundredFramesWithinTwoPlanesPerFrame(string operation)
    {
        // Room for every frame twice: Derive keeps the source and the result; Apply needs far less.
        const int Width = 48;
        const int Height = 32;
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 2L * 100 * Width * Height * 4 } };
        using Raster<Rgba8> anim = Raster.Create<Rgba8>(Width, Height, context);
        for (int i = 1; i < 100; i++)
        {
            anim.Frames.Add(anim.Frames[0]).Row(0)[0] = new Rgba8((byte)i, 0, 0, 255);
        }

        Action<RasterOps> build = operation switch
        {
            "resize" => ops => ops.Resize(40, 30),
            "crop" => ops => ops.Crop(new Rect(4, 4, 40, 20)),
            "rotate" => ops => ops.Rotate(90),
            _ => ops => ops.Blur(2),
        };

        using (Raster<Rgba8> derived = anim.Derive(build, TestContext.Current.CancellationToken))
        {
            Assert.Equal(100, derived.FrameCount);
        }

        anim.Apply(build, TestContext.Current.CancellationToken);
        Assert.Equal(100, anim.FrameCount);
        Assert.All(anim.Frames, f => Assert.Equal(anim.Width, f.Plane.Width));
    }

    private static Raster<Gray8> Build(int count)
    {
        Raster<Gray8> anim = Raster.Create(2, 2, new Gray8(0));
        for (int i = 1; i < count; i++)
        {
            anim.Frames.Add(anim.Frames[0]).Row(0)[0] = new Gray8((byte)i);
        }

        return anim;
    }

    private static int[] Values(Raster<Gray8> anim)
    {
        return [.. anim.Frames.Select(f => (int)f.Row(0)[0].L)];
    }

    private sealed class TestDetails : FormatDetails
    {
        public override FormatDetails Copy() => new TestDetails();
    }

    private sealed class CountingSource : IPixelMemorySource
    {
        private int _live;

        public int Live => _live;

        public int MaxBlockBytes => 1 << 20;

        public System.Buffers.IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref _live);
            return new Block(this, byteLength);
        }

        private sealed class Block(CountingSource owner, int length) : System.Buffers.IMemoryOwner<byte>
        {
            public Memory<byte> Memory { get; } = new byte[length];

            public void Dispose() => Interlocked.Decrement(ref owner._live);
        }
    }
}
