using System.Buffers;

using Egami.Pixels;

namespace Egami.Tests.Memory;

public class PixelPlaneTests
{
    [Fact]
    public void SmallBlocksComeFromThePoolAndLargeOnesAreUnmanaged()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 1000 };
        using IMemoryOwner<byte> small = source.Allocate(1000, ClearMode.None);
        using IMemoryOwner<byte> large = source.Allocate(1001, ClearMode.None);
        Assert.IsType<PooledBlock>(small);
        Assert.IsType<UnmanagedBlock>(large);
        Assert.Equal(1000, small.Memory.Length);
        Assert.Equal(1001, large.Memory.Length);
    }

    [Fact]
    public unsafe void UnmanagedBlocksAre64ByteAligned()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 0 };
        for (int i = 1; i < 20; i++)
        {
            using IMemoryOwner<byte> block = source.Allocate(i * 37, ClearMode.None);
            using MemoryHandle pin = block.Memory.Pin();
            Assert.Equal(0, (nint)pin.Pointer % 64);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void ClearModeZeroClearsReusedMemory(int poolThreshold)
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = poolThreshold };
        for (int i = 0; i < 5; i++)
        {
            using (IMemoryOwner<byte> dirty = source.Allocate(4096, ClearMode.None))
            {
                dirty.Memory.Span.Fill(0xAB);
            }

            using IMemoryOwner<byte> clean = source.Allocate(4096, ClearMode.Zero);
            Assert.True(clean.Memory.Span.IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [Fact]
    public void LargePlanesAreSplitIntoBlocksOfWholeRows()
    {
        // 10 pixels × 4 bytes = 40 bytes per row; 100-byte blocks hold 2 rows.
        CountingSource source = new(maxBlockBytes: 100);
        using PixelPlane<Rgba8> plane = PixelPlane.Allocate<Rgba8>(10, 7, source);
        Assert.Equal([80, 80, 80, 40], source.Allocations);
        Assert.False(plane.TryGetContiguous(out Span<Rgba8> flat));
        Assert.True(flat.IsEmpty);

        for (int y = 0; y < plane.Height; y++)
        {
            Span<Rgba8> row = plane.Row(y);
            Assert.Equal(10, row.Length);
            row.Fill(new Rgba8((byte)y, 0, 0, 255));
        }

        for (int y = 0; y < plane.Height; y++)
        {
            Assert.All(plane.Row(y).ToArray(), p => Assert.Equal(y, p.R));
        }
    }

    [Fact]
    public void ARowLargerThanABlockStillGetsOneBlock()
    {
        CountingSource source = new(maxBlockBytes: 10);
        using PixelPlane<Rgba8> plane = PixelPlane.Allocate<Rgba8>(5, 3, source);
        Assert.Equal([20, 20, 20], source.Allocations);
    }

    [Fact]
    public void SmallPlanesAreOneContiguousBlock()
    {
        using PixelPlane<Gray16> plane = PixelPlane.Allocate<Gray16>(3, 2, PooledPixelMemorySource.Shared, ClearMode.Zero);
        Assert.True(plane.TryGetContiguous(out Span<Gray16> pixels));
        Assert.Equal(6, pixels.Length);
        pixels[4] = new Gray16(1234);
        Assert.Equal(1234, plane.Row(1)[1].L);
    }

    [Fact]
    public void DisposingReturnsEveryBlock()
    {
        CountingSource source = new(maxBlockBytes: 64);
        PixelPlane<Rgba8> plane = PixelPlane.Allocate<Rgba8>(4, 33, source);
        Assert.Equal(9, source.Outstanding);
        plane.Dispose();
        Assert.Equal(0, source.Outstanding);
        plane.Dispose();
        Assert.Equal(0, source.Outstanding);
    }

    [Fact]
    public void FailedAllocationReturnsTheBlocksAlreadyTaken()
    {
        CountingSource source = new(maxBlockBytes: 4, failAfter: 3);
        Assert.Throws<InvalidOperationException>(() => PixelPlane.Allocate<Gray8>(4, 10, source));
        Assert.Equal(0, source.Outstanding);
    }

    [Fact]
    public void UsingADisposedPlaneThrows()
    {
        PixelPlane<Rgba8> plane = PixelPlane.Allocate<Rgba8>(2, 2, PooledPixelMemorySource.Shared);
        plane.Dispose();
        Assert.Throws<ObjectDisposedException>(() => plane.Row(0));
        Assert.Throws<ObjectDisposedException>(() => plane.TryGetContiguous(out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void RowOutsideThePlaneThrows(int y)
    {
        using PixelPlane<Rgba8> plane = PixelPlane.Allocate<Rgba8>(2, 3, PooledPixelMemorySource.Shared);
        Assert.Throws<ArgumentOutOfRangeException>(() => plane.Row(y));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 5)]
    public void InvalidSizesThrow(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelPlane.Allocate<Rgba8>(width, height, PooledPixelMemorySource.Shared));
    }

    [Fact]
    public void RowLargerThanTwoGigabytesThrowsBeforeAllocating()
    {
        CountingSource source = new(maxBlockBytes: int.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelPlane.Allocate<RgbaF32>(int.MaxValue / 8, 1, source));
        Assert.Empty(source.Allocations);
    }

    [Fact]
    public void WrappedPlanesUseTheCallersMemoryAndStride()
    {
        Rgba8[] pixels = new Rgba8[11];
        using (PixelPlane<Rgba8> plane = PixelPlane.Wrap<Rgba8>(pixels, width: 3, height: 3, stride: 4))
        {
            Assert.False(plane.OwnsMemory);
            Assert.False(plane.TryGetContiguous(out _));
            plane.Row(2)[0] = Rgba8.Red;
        }

        Assert.Equal(Rgba8.Red, pixels[8]);

        using PixelPlane<Rgba8> tight = PixelPlane.Wrap<Rgba8>(pixels, width: 5, height: 2);
        Assert.True(tight.TryGetContiguous(out Span<Rgba8> flat));
        Assert.Equal(10, flat.Length);
    }

    [Fact]
    public void WrapRejectsMemoryThatIsTooShort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelPlane.Wrap<Rgba8>(new Rgba8[10], width: 3, height: 3, stride: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelPlane.Wrap<Rgba8>(new Rgba8[100], width: 3, height: 3, stride: 2));
    }

    [Fact]
    public void ForgottenUnmanagedBlocksAreFreedByTheFinalizer()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 0 };
        AllocateAndForget(source);
        Assert.True(source.Cache.LiveBytes > 0);
        for (int i = 0; i < 50 && source.Cache.LiveBytes > 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, source.Cache.LiveBytes);
        Assert.Equal(0, source.RetainedBytes);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void AllocateAndForget(PooledPixelMemorySource source)
        {
            for (int i = 0; i < 10; i++)
            {
                _ = PixelPlane.Allocate<Rgba8>(100, 100, source);
            }
        }
    }

    [Fact]
    public unsafe void DisposedUnmanagedBlocksAreReusedForSimilarSizes()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 0 };
        nint first;
        using (IMemoryOwner<byte> block = source.Allocate(3_000_000, ClearMode.None))
        {
            using MemoryHandle pin = block.Memory.Pin();
            first = (nint)pin.Pointer;
        }

        Assert.Equal(4 * 1024 * 1024, source.RetainedBytes);
        using (IMemoryOwner<byte> block = source.Allocate(4_000_000, ClearMode.Zero))
        {
            using MemoryHandle pin = block.Memory.Pin();
            Assert.Equal(first, (nint)pin.Pointer);
            Assert.Equal(4_000_000, block.Memory.Length);
            Assert.True(block.Memory.Span.IndexOfAnyExcept((byte)0) < 0);
            Assert.Equal(0, source.RetainedBytes);
        }

        source.Trim();
        Assert.Equal(0, source.RetainedBytes);
        Assert.Equal(0, source.Cache.LiveBytes);
    }

    [Fact]
    public void RetentionIsCapped()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 0, MaxRetainedBytes = 10 * 1024 * 1024 };
        List<IMemoryOwner<byte>> blocks = [.. Enumerable.Range(0, 5).Select(_ => source.Allocate(3 * 1024 * 1024, ClearMode.None))];
        blocks.ForEach(b => b.Dispose());
        Assert.Equal(8 * 1024 * 1024, source.RetainedBytes);
        Assert.Equal(8 * 1024 * 1024, source.Cache.LiveBytes);

        PooledPixelMemorySource none = new() { PoolThresholdBytes = 0, MaxRetainedBytes = 0 };
        none.Allocate(2 * 1024 * 1024, ClearMode.None).Dispose();
        Assert.Equal(0, none.RetainedBytes);
        Assert.Equal(0, none.Cache.LiveBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledPixelMemorySource { MaxRetainedBytes = -1 });
    }

    /// <summary>A memory source that records every allocation and counts blocks not yet returned.</summary>
    private sealed class CountingSource(int maxBlockBytes, int failAfter = int.MaxValue) : IPixelMemorySource
    {
        private int _outstanding;

        public int MaxBlockBytes => maxBlockBytes;

        public List<int> Allocations { get; } = [];

        public int Outstanding => _outstanding;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            if (Allocations.Count == failAfter)
            {
                throw new InvalidOperationException("Simulated allocation failure.");
            }

            Allocations.Add(byteLength);
            Interlocked.Increment(ref _outstanding);
            return new Block(this, byteLength);
        }

        private sealed class Block(CountingSource owner, int length) : IMemoryOwner<byte>
        {
            private bool _disposed;

            public Memory<byte> Memory { get; } = new byte[length];

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    Interlocked.Decrement(ref owner._outstanding);
                }
            }
        }
    }
}
