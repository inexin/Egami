using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests;

public class RasterTests
{
    [Fact]
    public void CreateWithoutFillIsAllZero()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(5, 4);
        Assert.Equal(5, raster.Width);
        Assert.Equal(4, raster.Height);
        Assert.Equal(1, raster.FrameCount);
        Assert.Equal(Rgba8.Layout, raster.Layout);
        for (int y = 0; y < raster.Height; y++)
        {
            Assert.All(raster.Row(y).ToArray(), p => Assert.Equal(Rgba8.Transparent, p));
        }
    }

    [Fact]
    public void CreateWithFillFillsEveryPixel()
    {
        using Raster<Gray16> raster = Raster.Create(3, 3, new Gray16(500));
        for (int y = 0; y < 3; y++)
        {
            Assert.All(raster.Row(y).ToArray(), p => Assert.Equal(500, p.L));
        }
    }

    [Fact]
    public void GetAndSetPixel()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 3);
        raster.SetPixel(3, 2, Rgba8.Red);
        Assert.Equal(Rgba8.Red, raster.GetPixel(3, 2));
        Assert.Equal(Rgba8.Red, raster.Row(2)[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.GetPixel(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.GetPixel(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.SetPixel(0, 3, Rgba8.Red));
    }

    [Fact]
    public void CopyPixelsRoundTripsAsPixelsAndBytes()
    {
        Rgba8[] pixels = [.. Enumerable.Range(0, 12).Select(i => new Rgba8((byte)i, (byte)(i * 2), (byte)(i * 3), 255))];
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 3);
        raster.CopyPixelsFrom(pixels);
        Assert.Equal(pixels[5], raster.GetPixel(1, 1));

        Rgba8[] back = new Rgba8[12];
        raster.CopyPixelsTo(back);
        Assert.Equal(pixels, back);

        byte[] bytes = new byte[48];
        raster.CopyPixelsTo(bytes);
        Assert.Equal(MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(), bytes);

        using Raster<Rgba8> other = Raster.Create<Rgba8>(4, 3);
        other.CopyPixelsFrom((ReadOnlySpan<byte>)bytes);
        Assert.Equal(pixels[11], other.GetPixel(3, 2));

        Assert.Throws<ArgumentException>(() => raster.CopyPixelsTo(new Rgba8[11]));
        Assert.Throws<ArgumentException>(() => raster.CopyPixelsFrom(new Rgba8[11]));
    }

    [Fact]
    public void CopyIsDeepAndIncludesMetadata()
    {
        using Raster<Rgba8> raster = Raster.Create(2, 2, Rgba8.Blue);
        raster.Metadata.Text.Add(new TextEntry("Title", "Test"));
        raster.Metadata.IccProfile = new byte[] { 1, 2, 3 };
        raster.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(40);

        using Raster<Rgba8> copy = raster.Copy();
        raster.SetPixel(0, 0, Rgba8.Red);
        raster.Metadata.Text.Clear();

        Assert.Equal(Rgba8.Blue, copy.GetPixel(0, 0));
        Assert.Equal("Test", Assert.Single(copy.Metadata.Text).Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.Metadata.IccProfile.ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(40), copy.Frames[0].Metadata.Delay);
    }

    [Fact]
    public void AsConvertsEveryPixel()
    {
        using Raster<Rgba8> raster = Raster.Create(3, 2, Rgba8.Red);
        using Raster<Gray8> gray = raster.As<Gray8>();
        Assert.Equal(Gray8.Layout, gray.Layout);
        for (int y = 0; y < 2; y++)
        {
            Assert.All(gray.Row(y).ToArray(), p => Assert.Equal(127, p.L));
        }

        using Raster<Bgra8> bgra = ((Raster)raster).As<Bgra8>();
        Assert.Equal(new Bgra8(0, 0, 255, 255), bgra.GetPixel(2, 1));
    }

    [Fact]
    public void WrapUsesTheCallersMemory()
    {
        Gray8[] pixels = new Gray8[8];
        using (Raster<Gray8> raster = Raster.Wrap<Gray8>(pixels, 3, 2, stride: 4))
        {
            raster.SetPixel(2, 1, new Gray8(9));
            Assert.False(raster.TryGetContiguous(out _));
        }

        Assert.Equal(9, pixels[6].L);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void ForEachRowVisitsEveryRowOnce(int parallelism)
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = parallelism };
        using Raster<Gray16> raster = Raster.Create<Gray16>(600, 1000, context);
        int[] visits = new int[raster.Height];
        raster.ForEachRow((row, y) =>
        {
            Interlocked.Increment(ref visits[y]);
            row.Fill(new Gray16((ushort)y));
        });

        Assert.All(visits, v => Assert.Equal(1, v));
        Assert.Equal(999, raster.GetPixel(599, 999).L);
    }

    [Fact]
    public void DisposedRasterThrows()
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(2, 2);
        raster.Dispose();
        raster.Dispose();
        Assert.Throws<ObjectDisposedException>(() => raster.Row(0));
        Assert.Throws<ObjectDisposedException>(() => raster.GetPixel(0, 0));
        Assert.Throws<ObjectDisposedException>(() => raster.Copy());
        Assert.Throws<ObjectDisposedException>(() => raster.ForEachRow((_, _) => { }));
        Assert.Throws<ObjectDisposedException>(() => raster.TryGetContiguous(out _));
    }

    [Fact]
    public void RasterAllocatesFromTheContextMemorySource()
    {
        CountingSource source = new();
        EgamiContext context = EgamiContext.Default with { Memory = source };
        using (Raster<Rgba8> raster = Raster.Create<Rgba8>(10, 10, context))
        {
            using Raster<Rgba8> copy = raster.Copy();
            Assert.Equal(2, source.Outstanding);
        }

        Assert.Equal(0, source.Outstanding);
    }

    [Fact]
    public void ResolutionConvertsToPerInch()
    {
        Resolution perMeter = new(3779.527559, 3779.527559, ResolutionUnit.PerMeter);
        Assert.Equal(96, perMeter.ToPerInch().X, 4);
        Assert.Equal(ResolutionUnit.PerInch, perMeter.ToPerInch().Unit);
        Assert.Equal(new Resolution(1, 2, ResolutionUnit.None), new Resolution(1, 2, ResolutionUnit.None).ToPerInch());
    }

    private sealed class CountingSource : IPixelMemorySource
    {
        private int _outstanding;

        public int MaxBlockBytes => PooledPixelMemorySource.Shared.MaxBlockBytes;

        public int Outstanding => _outstanding;

        public System.Buffers.IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref _outstanding);
            return new Owner(this, PooledPixelMemorySource.Shared.Allocate(byteLength, clear));
        }

        private sealed class Owner(CountingSource source, System.Buffers.IMemoryOwner<byte> inner) : System.Buffers.IMemoryOwner<byte>
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
