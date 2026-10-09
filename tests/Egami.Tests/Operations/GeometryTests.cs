using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class GeometryTests
{
    public static TheoryData<string, bool> PixelTypes()
    {
        TheoryData<string, bool> data = [];
        foreach (string name in new[] { "Rgba8", "Bgra8", "Rgb8", "Bgr8", "Gray8", "Gray16", "GrayAlpha8", "Rgba16", "RgbaF32" })
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PixelTypes))]
    public void EveryOperationMatchesTheReference(string pixelType, bool chunked)
    {
        switch (pixelType)
        {
            case "Rgba8": Verify<Rgba8>(chunked); break;
            case "Bgra8": Verify<Bgra8>(chunked); break;
            case "Rgb8": Verify<Rgb8>(chunked); break;
            case "Bgr8": Verify<Bgr8>(chunked); break;
            case "Gray8": Verify<Gray8>(chunked); break;
            case "Gray16": Verify<Gray16>(chunked); break;
            case "GrayAlpha8": Verify<GrayAlpha8>(chunked); break;
            case "Rgba16": Verify<Rgba16>(chunked); break;
            case "RgbaF32": Verify<RgbaF32>(chunked); break;
            default: throw new ArgumentException(pixelType);
        }
    }

    [Fact]
    public void TurnsAndFlipsReturnTheOriginal()
    {
        using Raster<Rgba8> raster = Pattern<Rgba8>(101, 67, EgamiContext.Default);
        Grid original = Grid.Of(raster);
        CancellationToken ct = TestContext.Current.CancellationToken;

        raster.Apply(ops => ops.Rotate(90).Rotate(90).Rotate(90).Rotate(90), ct);
        Assert.Equal(original, Grid.Of(raster));
        raster.Apply(ops => ops.FlipHorizontal().FlipHorizontal().FlipVertical().FlipVertical(), ct);
        Assert.Equal(original, Grid.Of(raster));
        raster.Apply(ops => ops.Rotate(90).Rotate(-90).Rotate(180).FlipHorizontal().FlipVertical(), ct);
        Assert.Equal(original, Grid.Of(raster));
    }

    [Fact]
    public void AnglesNormalize()
    {
        using Raster<Rgba8> raster = Pattern<Rgba8>(7, 5, EgamiContext.Default);
        CancellationToken ct = TestContext.Current.CancellationToken;
        Grid original = Grid.Of(raster);
        using Raster<Rgba8> minus90 = raster.Derive(ops => ops.Rotate(-90), ct);
        using Raster<Rgba8> plus450 = raster.Derive(ops => ops.Rotate(450), ct);
        Assert.Equal(original.Rotate(clockwise: false), Grid.Of(minus90));
        Assert.Equal(original.Rotate(clockwise: true), Grid.Of(plus450));
        using Raster<Rgba8> none = raster.Derive(ops => ops.Rotate(360).Rotate(0), ct);
        Assert.Equal(original, Grid.Of(none));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RasterOpsProbe().Ops.Rotate(float.NaN));
    }

    [Fact]
    public void BadArgumentsThrowBeforeAnyPixelChanges()
    {
        using Raster<Rgba8> raster = Pattern<Rgba8>(20, 10, EgamiContext.Default);
        Grid original = Grid.Of(raster);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.FlipHorizontal().Crop(15, 0, 6, 10), ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.FlipHorizontal().Crop(-1, 0, 5, 5), ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Crop(0, 0, 0, 5), ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.FlipHorizontal().Pad(19, 30), ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Pad(0, 30), ct));

        // The rectangle is checked against the size at that step: after a 90° turn the image is 10 × 20.
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Rotate(90).FillColor(Rgba8.Red, new Rect(0, 0, 20, 10)), ct));
        Assert.Equal(original, Grid.Of(raster));

        raster.Apply(ops => ops.Rotate(90).FillColor(Rgba8.Red, new Rect(0, 0, 10, 20)), ct);
        Assert.All(raster.Row(19).ToArray(), p => Assert.Equal(Rgba8.Red, p));
    }

    [Fact]
    public void SquareQuarterTurnInPlaceUsesANewPlane()
    {
        using Raster<Rgba8> raster = Pattern<Rgba8>(40, 40, EgamiContext.Default);
        Grid original = Grid.Of(raster);
        raster.Apply(ops => ops.Rotate(90), TestContext.Current.CancellationToken);
        Assert.Equal(original.Rotate(clockwise: true), Grid.Of(raster));
    }

    [Fact]
    public void WholeImageCropAndPadKeepThePixels()
    {
        using Raster<Gray8> raster = Pattern<Gray8>(9, 4, EgamiContext.Default);
        Grid original = Grid.Of(raster);
        CancellationToken ct = TestContext.Current.CancellationToken;
        raster.Apply(ops => ops.Crop(0, 0, 9, 4).Pad(9, 4), ct);
        Assert.Equal(original, Grid.Of(raster));
        using Raster<Gray8> derived = raster.Derive(ops => ops.Crop(0, 0, 9, 4).Pad(9, 4), ct);
        Assert.Equal(original, Grid.Of(derived));
    }

    [Fact]
    public void LargeImagesRunInParallelBandsWithTheSameResult()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EgamiContext one = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> a = Pattern<Rgba8>(1000, 700, EgamiContext.Default);
        using Raster<Rgba8> b = Pattern<Rgba8>(1000, 700, one);
        static void Chain(RasterOps ops) => ops.Rotate(90).FlipHorizontal().FlipVertical().Pad(800, 1100, Rgba8.Blue, Anchor.BottomRight).Crop(13, 17, 600, 900).Rotate(270).FlipVertical();
        a.Apply(Chain, ct);
        b.Apply(Chain, ct);
        Assert.Equal(Grid.Of(b), Grid.Of(a));
    }

    [Fact]
    public void TransposeMatchesScalar()
    {
        Random random = new(7);
        uint[] source = new uint[GeometryKernels.Tile * GeometryKernels.Tile];
        foreach (ref uint v in source.AsSpan())
        {
            v = (uint)random.Next();
        }

        uint[] fast = new uint[source.Length];
        uint[] scalar = new uint[source.Length];
        GeometryKernels.Transpose<uint>(source, fast);
        GeometryKernels.TransposeScalar<uint>(source, scalar);
        Assert.Equal(scalar, fast);
        Assert.Equal(source[(3 * GeometryKernels.Tile) + 5], fast[(5 * GeometryKernels.Tile) + 3]);

        Rgb8[] small = [.. source.Select(v => new Rgb8((byte)v, (byte)(v >> 8), (byte)(v >> 16)))];
        Rgb8[] smallOut = new Rgb8[small.Length];
        GeometryKernels.Transpose<Rgb8>(small, smallOut);
        Assert.Equal(small[(3 * GeometryKernels.Tile) + 5], smallOut[(5 * GeometryKernels.Tile) + 3]);
    }

    [Fact]
    public void ReverseRowMatchesScalar()
    {
        CheckReverse<byte>();
        CheckReverse<ushort>();
        CheckReverse<Rgb8>();
        CheckReverse<uint>();
        CheckReverse<ulong>();
        CheckReverse<RgbaF32>();

        static void CheckReverse<T>()
            where T : unmanaged
        {
            Random random = new(3);
            for (int length = 0; length < 140; length++)
            {
                T[] data = new T[length];
                random.NextBytes(MemoryMarshal.AsBytes(data.AsSpan()));
                T[] scalar = [.. data];
                GeometryKernels.ReverseRow<T>(data);
                GeometryKernels.ReverseRowScalar<T>(scalar);
                Assert.True(MemoryMarshal.AsBytes(scalar.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(data.AsSpan())), $"{typeof(T).Name} × {length}");
            }
        }
    }

    private static void Verify<TPixel>(bool chunked)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        EgamiContext context = chunked ? EgamiContext.Default with { Memory = new SmallBlocks(300) } : EgamiContext.Default;
        CancellationToken ct = TestContext.Current.CancellationToken;
        Rgba8 color = new(10, 200, 30, 128);
        byte[] fill = MemoryMarshal.AsBytes([TPixel.FromRgba8(color)]).ToArray();
        foreach ((int width, int height) in new[] { (13, 9), (70, 45), (1, 1), (33, 1), (1, 33) })
        {
            using Raster<TPixel> raster = Pattern<TPixel>(width, height, context);
            Grid g = Grid.Of(raster);
            Rect crop = new(width / 3, height / 4, Math.Max(1, width / 2), Math.Max(1, height / 2));
            Rect area = new(width / 4, height / 3, Math.Max(1, width / 3), Math.Max(1, height / 3));
            (string Name, Action<RasterOps> Build, Grid Expected)[] cases =
            [
                ("crop", ops => ops.Crop(crop), g.Crop(crop)),
                ("pad", ops => ops.Pad(width + 5, height + 3, color, Anchor.TopRight), g.Pad(width + 5, height + 3, fill, new Point(5, 0))),
                ("pad center", ops => ops.Pad(width + 4, height + 7, color), g.Pad(width + 4, height + 7, fill, new Point(2, 3))),
                ("flip h", ops => ops.FlipHorizontal(), g.FlipHorizontal()),
                ("flip v", ops => ops.FlipVertical(), g.FlipVertical()),
                ("flip both", ops => ops.FlipHorizontal().FlipVertical(), g.FlipHorizontal().FlipVertical()),
                ("rotate 90", ops => ops.Rotate(90), g.Rotate(clockwise: true)),
                ("rotate 180", ops => ops.Rotate(180), g.FlipHorizontal().FlipVertical()),
                ("rotate 270", ops => ops.Rotate(270), g.Rotate(clockwise: false)),
                ("fill", ops => ops.FillColor(color), g.Fill(new Rect(0, 0, width, height), fill)),
                ("fill rect", ops => ops.FillColor(color, area), g.Fill(area, fill)),
                ("chain", ops => ops.Rotate(90).Crop(0, 0, height, Math.Max(1, width - 1)).FlipHorizontal(),
                    g.Rotate(clockwise: true).Crop(new Rect(0, 0, height, Math.Max(1, width - 1))).FlipHorizontal()),
            ];

            foreach ((string name, Action<RasterOps> build, Grid expected) in cases)
            {
                string label = $"{typeof(TPixel).Name} {width}×{height} {name}{(chunked ? " chunked" : string.Empty)}";
                using Raster<TPixel> derived = raster.Derive(build, ct);
                Assert.True(expected == Grid.Of(derived), $"Derive {label}");
                Assert.True(g == Grid.Of(raster), $"Derive changed the source: {label}");

                using Raster<TPixel> copy = raster.Copy();
                copy.Apply(build, ct);
                Assert.True(expected == Grid.Of(copy), $"Apply {label}");
            }
        }
    }

    /// <summary>A raster whose pixel bytes encode their position, so any misplaced pixel shows.</summary>
    private static Raster<TPixel> Pattern<TPixel>(int width, int height, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height, context);
        int bpp = TPixel.Layout.BytesPerPixel;
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            for (int x = 0; x < width; x++)
            {
                int n = (y * width) + x;
                for (int k = 0; k < bpp; k++)
                {
                    row[(x * bpp) + k] = (byte)((n >> (8 * (k % 3))) + (k * 61));
                }
            }
        }

        return raster;
    }

    private sealed class RasterOpsProbe
    {
        public RasterOps Ops { get; private set; } = null!;

        public RasterOpsProbe()
        {
            using Raster<Gray8> raster = Raster.Create<Gray8>(1, 1);
            raster.Apply(ops => Ops = ops, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Pixels as bytes, with reference implementations of each operation.</summary>
    private sealed record Grid(int Width, int Height, int Bpp, byte[] Bytes)
    {
        public static Grid Of<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int bpp = TPixel.Layout.BytesPerPixel;
            byte[] bytes = new byte[raster.Width * raster.Height * bpp];
            for (int y = 0; y < raster.Height; y++)
            {
                MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(bytes.AsSpan(y * raster.Width * bpp));
            }

            return new Grid(raster.Width, raster.Height, bpp, bytes);
        }

        public bool Equals(Grid? other)
        {
            return other is not null && Width == other.Width && Height == other.Height && Bytes.AsSpan().SequenceEqual(other.Bytes);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Width, Height);
        }

        public Grid Crop(Rect r)
        {
            return Map(r.Width, r.Height, (x, y) => (x + r.X, y + r.Y));
        }

        public Grid Pad(int width, int height, byte[] fill, Point at)
        {
            Grid result = Map(width, height, (x, y) => (x - at.X, y - at.Y), fill);
            return result;
        }

        public Grid FlipHorizontal()
        {
            return Map(Width, Height, (x, y) => (Width - 1 - x, y));
        }

        public Grid FlipVertical()
        {
            return Map(Width, Height, (x, y) => (x, Height - 1 - y));
        }

        public Grid Rotate(bool clockwise)
        {
            return clockwise ? Map(Height, Width, (x, y) => (y, Height - 1 - x)) : Map(Height, Width, (x, y) => (Width - 1 - y, x));
        }

        public Grid Fill(Rect r, byte[] fill)
        {
            return Map(Width, Height, (x, y) => r.Contains(new Point(x, y)) ? (-1, -1) : (x, y), fill);
        }

        /// <summary>Builds a grid whose pixel (x, y) is this grid's pixel at <paramref name="from"/>, or <paramref name="fill"/> outside it.</summary>
        private Grid Map(int width, int height, Func<int, int, (int X, int Y)> from, byte[]? fill = null)
        {
            byte[] bytes = new byte[width * height * Bpp];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (int sx, int sy) = from(x, y);
                    Span<byte> target = bytes.AsSpan(((y * width) + x) * Bpp, Bpp);
                    if ((uint)sx < (uint)Width && (uint)sy < (uint)Height)
                    {
                        Bytes.AsSpan(((sy * Width) + sx) * Bpp, Bpp).CopyTo(target);
                    }
                    else
                    {
                        fill!.CopyTo(target);
                    }
                }
            }

            return new Grid(width, height, Bpp, bytes);
        }
    }

    /// <summary>Pooled memory in small blocks, so planes span many blocks.</summary>
    private sealed class SmallBlocks(int maxBlockBytes) : IPixelMemorySource
    {
        public int MaxBlockBytes => maxBlockBytes;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            return PooledPixelMemorySource.Shared.Allocate(byteLength, clear);
        }
    }
}
