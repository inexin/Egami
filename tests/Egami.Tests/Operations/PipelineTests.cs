using System.Buffers;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class PipelineTests
{
    [Fact]
    public void CustomOperationRunsInPlace()
    {
        using Raster<Rgba8> raster = Gradient(40, 30);
        Span<Rgba8> before = raster.Row(5);
        Rgba8 original = before[7];

        raster.Apply(ops => ops.Add(new AddToRed(10)), TestContext.Current.CancellationToken);

        Assert.Equal((byte)(original.R + 10), raster.GetPixel(7, 5).R);
        Assert.Equal(original.G, raster.GetPixel(7, 5).G);
    }

    [Fact]
    public void DeriveLeavesTheSourceUntouched()
    {
        using Raster<Rgba8> raster = Gradient(40, 30);
        using Raster<Rgba8> copy = raster.Copy();

        using Raster<Rgba8> derived = raster.Derive(ops => ops.Add(new AddToRed(10)).Add(new AddToRed(5)), TestContext.Current.CancellationToken);

        AssertSame(copy, raster);
        Assert.Equal((byte)(copy.GetPixel(3, 4).R + 15), derived.GetPixel(3, 4).R);
    }

    [Fact]
    public void SizeChangingStepsChainAndFreeIntermediatePlanes()
    {
        CountingSource memory = new();
        EgamiContext context = EgamiContext.Default with { Memory = memory };
        using Raster<Rgba8> raster = Raster.Create(40, 30, new Rgba8(255, 0, 255, 255), context);
        int baseline = memory.Outstanding;

        raster.Apply(ops => ops.Add(new HalveWidth()).Add(new AddToRed(1)).Add(new HalveWidth()), TestContext.Current.CancellationToken);

        Assert.Equal(10, raster.Width);
        Assert.Equal(30, raster.Height);
        Assert.Equal(baseline, memory.Outstanding);
        Assert.All(raster.Row(29).ToArray(), p => Assert.Equal(new Rgba8(0, 0, 255, 255), p));

        using Raster<Rgba8> derived = raster.Derive(ops => ops.Add(new HalveWidth()), TestContext.Current.CancellationToken);
        Assert.Equal(5, derived.Width);
        Assert.Equal(10, raster.Width);
        Assert.Equal(baseline + 1, memory.Outstanding);
    }

    [Fact]
    public void EveryFrameRuns()
    {
        using Raster<Gray8> raster = Raster.Create(8, 8, new Gray8(1));
        List<int> frames = [];
        raster.Apply(ops => ops.Add(new RecordFrames(frames)), TestContext.Current.CancellationToken);
        Assert.Equal([0], frames);
    }

    [Fact]
    public void BadStepsFailBeforeAnyPixelChanges()
    {
        using Raster<Rgba8> raster = Gradient(10, 10);
        using Raster<Rgba8> before = raster.Copy();
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Add(new AddToRed(1)).Add(new Shrink(20)), TestContext.Current.CancellationToken));
        AssertSame(before, raster);
        Assert.Throws<ArgumentNullException>(() => raster.Apply(ops => ops.Add(null!), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MaxTotalBytesIsCheckedBeforeAllocating()
    {
        CountingSource memory = new();
        EgamiContext context = EgamiContext.Default with { Memory = memory, Limits = SafetyLimits.Default with { MaxTotalBytes = 799 } };
        using Raster<Rgba8> raster = Raster.Create(20, 20, Rgba8.Blue, context);
        int baseline = memory.Allocations;

        LimitExceededException e = Assert.Throws<LimitExceededException>(() => raster.Derive(ops => ops.Add(new HalveWidth()), TestContext.Current.CancellationToken));
        Assert.Equal("MaxTotalBytes", e.LimitName);
        Assert.Equal(baseline, memory.Allocations);

        // The budget counts the planes the chain allocates, not the source: 10 × 20 × 4 = 800 bytes fits.
        context = context with { Limits = SafetyLimits.Default with { MaxTotalBytes = 800 } };
        using Raster<Rgba8> small = Raster.Create(20, 20, Rgba8.Blue, context);
        using Raster<Rgba8> derived = small.Derive(ops => ops.Add(new HalveWidth()), TestContext.Current.CancellationToken);
        Assert.Equal(10, derived.Width);
    }

    [Fact]
    public void CancellationStopsBetweenBands()
    {
        using Raster<Rgba8> raster = Gradient(1000, 1000);
        using CancellationTokenSource cancel = new();
        int bands = 0;
        CancelAfterBands operation = new(cancel, () => Interlocked.Increment(ref bands));
        Assert.ThrowsAny<OperationCanceledException>(() => raster.Apply(ops => ops.Add(operation).Add(operation), cancel.Token));
        Assert.True(bands < 1000 / 16 * 2, $"{bands} bands ran after cancelling");

        using CancellationTokenSource already = new();
        already.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => raster.Derive(ops => ops.Add(new AddToRed(1)), already.Token));
    }

    [Fact]
    public void OneThreadGivesTheSamePixelsAsAllCores()
    {
        using Raster<Rgba8> all = Gradient(700, 600, EgamiContext.Default);
        using Raster<Rgba8> one = Gradient(700, 600, EgamiContext.Default with { MaxParallelism = 1 });
        HashSet<int> threads = [];
        all.Apply(ops => ops.Add(new AddToRed(3)), TestContext.Current.CancellationToken);
        one.Apply(ops => ops.Add(new AddToRed(3)).Add(new RecordThreads(threads)), TestContext.Current.CancellationToken);
        AssertSame(all, one);
        Assert.Single(threads);
    }

    [Fact]
    public void EmptyChainDeriveIsACopy()
    {
        using Raster<Rgba8> raster = Gradient(5, 5);
        raster.Apply(_ => { }, TestContext.Current.CancellationToken);
        using Raster<Rgba8> derived = raster.Derive(_ => { }, TestContext.Current.CancellationToken);
        AssertSame(raster, derived);
        derived.SetPixel(0, 0, Rgba8.Red);
        Assert.NotEqual(Rgba8.Red, raster.GetPixel(0, 0));
    }

    [Fact]
    public void NonGenericRasterDispatches()
    {
        using Raster raster = Gradient(12, 6);
        raster.Apply(ops => ops.Add(new HalveWidth()), TestContext.Current.CancellationToken);
        Assert.Equal(6, raster.Width);
        using Raster derived = raster.Derive(ops => ops.Add(new HalveWidth()), TestContext.Current.CancellationToken);
        Assert.Equal(3, derived.Width);
        Assert.IsType<Raster<Rgba8>>(derived);
    }

    [Fact]
    public void WrappedMemoryIsReplacedWhenTheSizeChanges()
    {
        Rgba8[] pixels = new Rgba8[16];
        using Raster<Rgba8> raster = Raster.Wrap<Rgba8>(pixels, 4, 4);
        raster.Apply(ops => ops.Add(new AddToRed(9)), TestContext.Current.CancellationToken);
        Assert.Equal(9, pixels[5].R);
        raster.Apply(ops => ops.Add(new HalveWidth()).Add(new AddToRed(1)), TestContext.Current.CancellationToken);
        Assert.Equal(9, pixels[5].R);
        Assert.Equal(10, raster.GetPixel(1, 1).R);
    }

    [Fact]
    public void ForEachRowVisitsEveryRowOnceThroughBands()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(300, 5000);
        int[] visits = new int[raster.Height];
        raster.ForEachRow((_, y) => Interlocked.Increment(ref visits[y]), TestContext.Current.CancellationToken);
        Assert.All(visits, v => Assert.Equal(1, v));
    }

    [Fact]
    public void RectAndAnchorGeometry()
    {
        Rect a = new(10, 20, 30, 40);
        Assert.Equal(40, a.Right);
        Assert.Equal(60, a.Bottom);
        Assert.True(a.Contains(new Point(10, 20)));
        Assert.False(a.Contains(new Point(40, 20)));
        Assert.True(a.Contains(new Rect(15, 25, 5, 5)));
        Assert.Equal(new Rect(30, 50, 10, 10), a.Intersect(new Rect(30, 50, 100, 100)));
        Assert.True(a.Intersect(new Rect(100, 100, 5, 5)).IsEmpty);

        Size outer = new(100, 50);
        Size inner = new(20, 10);
        Assert.Equal(new Point(40, 20), Anchor.Center.Offset(outer, inner));
        Assert.Equal(new Point(0, 0), Anchor.TopLeft.Offset(outer, inner));
        Assert.Equal(new Point(80, 40), Anchor.BottomRight.Offset(outer, inner));
        Assert.Equal(new Point(80, 20), Anchor.Right.Offset(outer, inner));
        Assert.Equal(new Point(-10, -5), Anchor.Center.Offset(inner, new Size(40, 20)));
    }

    [Fact]
    public void RowRunnerCoversEveryRowWithReasonableBands()
    {
        foreach ((int rows, long rowBytes, int parallelism) in new[] { (1, 4L, 8), (17, 4L, 8), (5000, 16000L, 8), (5000, 16000L, 1), (3, 10_000_000L, 4) })
        {
            int[] seen = new int[rows];
            List<int> sizes = [];
            RowRunner.Run(rows, rowBytes, parallelism, (start, end) =>
            {
                lock (sizes)
                {
                    sizes.Add(end - start);
                }

                for (int y = start; y < end; y++)
                {
                    Interlocked.Increment(ref seen[y]);
                }
            }, CancellationToken.None);
            Assert.All(seen, v => Assert.Equal(1, v));
            Assert.True(sizes.Count(s => s < 16) <= 1);
        }
    }

    private static Raster<Rgba8> Gradient(int width, int height, EgamiContext? context = null)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, context);
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)(x * 3), (byte)(y * 5), (byte)(x + y), 255);
            }
        }, TestContext.Current.CancellationToken);
        return raster;
    }

    private static void AssertSame<T>(Raster<T> expected, Raster<T> actual)
        where T : unmanaged, IPixelType<T>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.Equal(expected.Row(y).ToArray(), actual.Row(y).ToArray());
        }
    }

    /// <summary>Adds to the red channel, reading the source and writing the destination, through bands.</summary>
    private sealed class AddToRed(int amount) : IRasterOperation
    {
        public Size OutputSize(Size input)
        {
            return input;
        }

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> source = input.Source.Row(y);
                    Span<TPixel> destination = input.Destination.Row(y);
                    for (int x = 0; x < source.Length; x++)
                    {
                        Rgba8 p = source[x].ToRgba8();
                        p.R = (byte)(p.R + amount);
                        destination[x] = TPixel.FromRgba8(p);
                    }
                }
            });
        }
    }

    /// <summary>Keeps the left half of each row.</summary>
    private sealed class HalveWidth : IRasterOperation
    {
        public Size OutputSize(Size input)
        {
            return input with { Width = input.Width / 2 };
        }

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Assert.False(input.IsInPlace);
            for (int y = 0; y < input.Destination.Height; y++)
            {
                input.Source.Row(y)[..input.Destination.Width].CopyTo(input.Destination.Row(y));
            }
        }
    }

    private sealed class Shrink(int by) : IRasterOperation
    {
        public Size OutputSize(Size input)
        {
            return new Size(input.Width - by, input.Height);
        }

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            throw new InvalidOperationException("Never runs.");
        }
    }

    private sealed class RecordFrames(List<int> frames) : IRasterOperation
    {
        public Size OutputSize(Size input)
        {
            return input;
        }

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            frames.Add(input.FrameIndex);
        }
    }

    private sealed class RecordThreads(HashSet<int> threads) : IRasterOperation
    {
        public Size OutputSize(Size input)
        {
            return input;
        }

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            input.ForEachBand((_, _) =>
            {
                lock (threads)
                {
                    threads.Add(Environment.CurrentManagedThreadId);
                }
            });
        }
    }

    /// <summary>Cancels the chain after a few bands.</summary>
    private sealed class CancelAfterBands(CancellationTokenSource cancel, Func<int> countBand) : IRasterOperation
    {
        public Size OutputSize(Size input)
        {
            return input;
        }

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            input.ForEachBand((_, _) =>
            {
                if (countBand() == 3)
                {
                    cancel.Cancel();
                }
            });
        }
    }

    private sealed class CountingSource : IPixelMemorySource
    {
        private int _outstanding;
        private int _allocations;

        public int MaxBlockBytes => PooledPixelMemorySource.Shared.MaxBlockBytes;

        public int Outstanding => _outstanding;

        public int Allocations => _allocations;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref _allocations);
            Interlocked.Increment(ref _outstanding);
            return new Owner(this, PooledPixelMemorySource.Shared.Allocate(byteLength, clear));
        }

        private sealed class Owner(CountingSource source, IMemoryOwner<byte> inner) : IMemoryOwner<byte>
        {
            private int _disposed;

            public Memory<byte> Memory => inner.Memory;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    inner.Dispose();
                    Interlocked.Decrement(ref source._outstanding);
                }
            }
        }
    }
}
