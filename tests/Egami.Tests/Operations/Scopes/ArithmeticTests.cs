using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Scopes;

public sealed class ArithmeticTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(PixelMath.Add, 0.1f, 100, 126)]
    [InlineData(PixelMath.Subtract, 0.5f, 100, 0)]
    [InlineData(PixelMath.Multiply, 2f, 100, 200)]
    [InlineData(PixelMath.Divide, 4f, 100, 25)]
    [InlineData(PixelMath.Pow, 2f, 128, 64)]
    [InlineData(PixelMath.Min, 0.2f, 100, 51)]
    [InlineData(PixelMath.Max, 0.2f, 10, 51)]
    [InlineData(PixelMath.Set, 1f, 10, 255)]
    public void OperatorsWorkOnValuesScaledToOne(PixelMath op, float value, byte input, byte expected)
    {
        using Raster<Rgba8> raster = Raster.Create(1, 1, new Rgba8(input, input, input, 77));
        raster.Apply(ops => ops.Arithmetic(op, value), Ct);
        Assert.Equal(new Rgba8(expected, expected, expected, 77), raster.GetPixel(0, 0));
    }

    [Fact]
    public void ChannelsChooseWhatChanges()
    {
        using Raster<Rgba8> raster = Raster.Create(1, 1, new Rgba8(100, 100, 100, 100));
        raster.Apply(ops => ops.Arithmetic(PixelMath.Set, 0, ColorChannels.Green | ColorChannels.Alpha), Ct);
        Assert.Equal(new Rgba8(100, 0, 100, 0), raster.GetPixel(0, 0));
        using Raster<Rgba16> wide = Raster.Create(1, 1, new Rgba16(1000, 2000, 3000, 4000));
        wide.Apply(ops => ops.Arithmetic(PixelMath.Multiply, 2, ColorChannels.Red), Ct);
        Assert.Equal(new Rgba16(2000, 2000, 3000, 4000), wide.GetPixel(0, 0));
        using Raster<RgbaF32> floats = Raster.Create(1, 1, new RgbaF32(0.5f, 0.5f, 0.5f, 1));
        floats.Apply(ops => ops.Arithmetic(PixelMath.Log, 9), Ct);
        Assert.Equal(Math.Log(5.5) / Math.Log(10), floats.GetPixel(0, 0).R, 1e-6);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Arithmetic((PixelMath)20, 1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Arithmetic(PixelMath.Add, float.NaN), Ct));
    }

    [Theory]
    [InlineData(StackMode.Mean, 20)]
    [InlineData(StackMode.Median, 20)]
    [InlineData(StackMode.Min, 10)]
    [InlineData(StackMode.Max, 40)]
    public void StackCombinesEachChannel(StackMode mode, byte expected)
    {
        using Raster<Rgba8> a = Raster.Create(2, 1, new Rgba8(10, 10, 10, 255));
        using Raster<Rgba8> b = Raster.Create(2, 1, new Rgba8(20, 20, 20, 255));
        using Raster<Gray8> c = Raster.Create(2, 1, new Gray8(30));
        using Raster<Rgba16> d = Raster.Create(2, 1, new Rgba16(40 * 257, 40 * 257, 40 * 257, 65535));
        Raster[] images = mode == StackMode.Max ? [a, b, c, d] : [a, b, c];
        using Raster stacked = Raster.Stack(images, mode);
        Rgba8 p = Assert.IsType<Raster<Rgba8>>(stacked).GetPixel(1, 0);
        Assert.Equal(new Rgba8(expected, expected, expected, 255), p);
    }

    [Fact]
    public void StackChecksItsInputs()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(2, 2);
        using Raster<Rgba8> b = Raster.Create<Rgba8>(3, 2);
        Assert.Throws<ArgumentException>(() => Raster.Stack([a], StackMode.Mean));
        Assert.Throws<ArgumentException>(() => Raster.Stack([a, b], StackMode.Mean));
        Assert.Throws<ArgumentNullException>(() => Raster.Stack([a, null!], StackMode.Mean));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Stack([a, a], (StackMode)9));
    }

    /// <summary>Stacking 50 inputs holds one row of each per worker, not 50 images.</summary>
    [Fact]
    public void FiftyInputsStayWithinTheirRows()
    {
        const int Width = 1000, Height = 600, Count = 50;
        Egami.Tests.Memory.MemoryPeak peak = Egami.Tests.Memory.PeakMemory.Measure(context =>
        {
            EgamiContext one = context with { MaxParallelism = 1 };
            List<Raster> inputs = [.. Enumerable.Range(0, Count).Select(i => (Raster)Raster.Create(Width, Height, new Rgba8((byte)i, 0, 0, 255), one))];
            try
            {
                using Raster stacked = Raster.Stack(inputs, StackMode.Median);
                Assert.Equal((byte)25, ((Raster<Rgba8>)stacked).GetPixel(5, 5).R);
            }
            finally
            {
                inputs.ForEach(r => r.Dispose());
            }
        });

        long images = (long)Count * Width * Height * 4, output = (long)Width * Height * 4, rows = (long)Count * Width * 8;
        Assert.True(peak.PeakPixelBytes <= images + output + rows + 65536, peak.ToString());
    }
}
