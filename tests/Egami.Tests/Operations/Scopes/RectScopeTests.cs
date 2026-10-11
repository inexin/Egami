using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Scopes;

/// <summary>Within(rect) against the whole-image result kept inside the rectangle.</summary>
public sealed class RectScopeTests : IDisposable
{
    private readonly Raster<Rgba8> _logo = OperationCoverageTests.Logo();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The same-size operations of 1.x and stage A whose whole-image result, kept in the area, is the scoped result.</summary>
    public static TheoryData<string> SameSize()
    {
        string[] moves = ["Flip-Horizontal", "Flip-Vertical", "Flip-Both"];
        TheoryData<string> data = [];
        foreach ((string name, var chain) in OperationCoverageTests.Chains)
        {
            using Raster<Rgba8> probe = Raster.Create<Rgba8>(13, 7);
            Size size = new(13, 7);
            using Raster<Rgba8> logo = OperationCoverageTests.Logo();
            RasterOps ops = Record(chain(size, logo));
            if (!moves.Contains(name) && ops.Steps.All(s => s.OutputSize(size) == size) && !name.StartsWith("Rotate", StringComparison.Ordinal))
            {
                data.Add(name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SameSize))]
    public void TheAreaEqualsTheWholeImageResult(string operation)
    {
        foreach ((int width, int height, Rect area) in new[] { (130, 70, new Rect(20, 10, 60, 40)), (61, 37, new Rect(-5, 30, 30, 20)), (40, 30, new Rect(0, 0, 40, 30)) })
        {
            using Raster<Rgba8> source = OperationCoverageTests.Noise(width, height, EgamiContext.Default);
            Action<RasterOps> chain = OperationCoverageTests.Chains[operation](new Size(width, height), _logo);
            using Raster<Rgba8> whole = source.Derive(chain, Ct);
            using Raster<Rgba8> scoped = source.Derive(ops => ops.Within(area, chain), Ct);
            Rect inside = area.Intersect(new Rect(0, 0, width, height));
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Rgba8 expected = inside.Contains(new Point(x, y)) ? whole.GetPixel(x, y) : source.GetPixel(x, y);
                    Assert.True(expected == scoped.GetPixel(x, y), $"{operation} {width}×{height} in {area}: ({x}, {y}) is {scoped.GetPixel(x, y)}, expected {expected}");
                }
            }
        }
    }

    [Fact]
    public void FlipsMirrorTheArea()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(30, 20, EgamiContext.Default);
        Rect area = new(5, 4, 10, 8);
        using Raster<Rgba8> scoped = source.Derive(ops => ops.Within(area, o => o.FlipHorizontal().Rotate(180)), Ct);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                // A horizontal flip then a half turn is a vertical flip of the area.
                Rgba8 expected = area.Contains(new Point(x, y)) ? source.GetPixel(x, area.Y + area.Bottom - 1 - y) : source.GetPixel(x, y);
                Assert.Equal(expected, scoped.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void ScopesNestAsIntersections()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(40, 30, EgamiContext.Default);
        using Raster<Rgba8> nested = source.Derive(ops => ops.Within(new Rect(0, 0, 25, 30), o => o.Within(new Rect(10, 5, 30, 10), i => i.Invert())), Ct);
        using Raster<Rgba8> single = source.Derive(ops => ops.Within(new Rect(10, 5, 15, 10), o => o.Invert()), Ct);
        Assert.Equal(Pixels(single), Pixels(nested));
    }

    /// <summary>A custom operation runs on a copy of the area and sees only it.</summary>
    [Fact]
    public void CustomOperationsSeeOnlyTheArea()
    {
        using Raster<Rgba8> source = Raster.Create(20, 10, new Rgba8(1, 2, 3, 255));
        CountingFill fill = new();
        using Raster<Rgba8> scoped = source.Derive(ops => ops.Within(new Rect(3, 2, 5, 4), o => o.Add(fill)), Ct);
        Assert.Equal((5, 4), fill.Seen);
        Assert.Equal(Rgba8.White, scoped.GetPixel(3, 2));
        Assert.Equal(Rgba8.White, scoped.GetPixel(7, 5));
        Assert.Equal(new Rgba8(1, 2, 3, 255), scoped.GetPixel(8, 5));
        Assert.Equal(new Rgba8(1, 2, 3, 255), scoped.GetPixel(2, 2));
    }

    [Fact]
    public void SizeChangingStepsThrowWhenRecorded()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(20, 10);
        Rect area = new(0, 0, 5, 5);
        Assert.Contains("Resize", Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.Resize(3, 3)), Ct)).Message, StringComparison.Ordinal);
        Assert.Contains("Crop", Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.Invert().Crop(0, 0, 2, 2)), Ct)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.Pad(30, 30)), Ct));
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.Rotate(90)), Ct));
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.Rotate(10)), Ct));
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.AutoOrient()), Ct));
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.ConvertColor(Egami.Colors.ColorProfile.DisplayP3)), Ct));
        Assert.Throws<ArgumentException>(() => source.Apply(ops => ops.Within(area, o => o.Add(new Growing())), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.Within(new Rect(0, 0, -1, 3), o => o.Invert()), Ct));
        Assert.Throws<ArgumentNullException>(() => source.Apply(ops => ops.Within(area, null!), Ct));
    }

    [Fact]
    public void AnAreaOutsideChangesNothingAndMetadataStays()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(20, 10, EgamiContext.Default);
        using Raster<Rgba8> missed = source.Derive(ops => ops.Within(new Rect(30, 30, 5, 5), o => o.Invert().Blur(2)), Ct);
        Assert.Equal(Pixels(source), Pixels(missed));
        using Raster<Rgba8> empty = source.Derive(ops => ops.Within(new Rect(5, 5, 0, 3), o => o.Invert()), Ct);
        Assert.Equal(Pixels(source), Pixels(empty));
    }

    [Fact]
    public void OneThreadGivesTheSamePixelsAsAllCores()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(300, 200, EgamiContext.Default);
        using Raster<Rgba8> one = OperationCoverageTests.Noise(300, 200, EgamiContext.Default with { MaxParallelism = 1 });
        Action<RasterOps> chain = ops => ops.Within(new Rect(50, 40, 150, 100), o => o.Blur(3).Saturation(1.4f).Within(new Rect(0, 0, 100, 100), i => i.Sharpen()));
        source.Apply(chain, Ct);
        one.Apply(chain, Ct);
        Assert.Equal(Pixels(source), Pixels(one));
    }

    [Fact]
    public void DeriveLeavesTheSourceUntouched()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(60, 40, EgamiContext.Default);
        Rgba8[] before = Pixels(source);
        using Raster<Gray8> mask = MaskScopeTests.GrayMask(30, 20);
        using Raster<Rgba8> derived = source.Derive(ops => ops
            .Within(new Rect(5, 5, 30, 20), o => o.Blur(2).Invert())
            .Within(new Mask(mask, new Point(10, 10)), o => o.FlipHorizontal().Saturation(1.3f))
            .OnChannels(ColorChannels.Alpha, o => o.BoxBlur(1)), Ct);
        Assert.Equal(before, Pixels(source));
        Assert.NotEqual(before, Pixels(derived));
    }

    [Fact]
    public void CancellationStopsInsideAScope()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(2000, 2000);
        using CancellationTokenSource cancel = new();
        CancelOnRun stop = new(cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => source.Apply(ops => ops.Within(new Rect(0, 0, 1500, 1500), o => o.Add(stop).Blur(5).Invert()), cancel.Token));
    }

    [Fact]
    public void ScratchPlanesCountAgainstTheBudget()
    {
        EgamiContext tight = EgamiContext.Default with { Limits = EgamiContext.Default.Limits with { MaxTotalBytes = 12_000_000 } };
        using Raster<Rgba8> source = Raster.Create<Rgba8>(2000, 1000, tight);
        Assert.Throws<LimitExceededException>(() => source.Apply(ops => ops.Within(new Rect(0, 0, 2000, 1000), o => o.Blur(150)), Ct));
    }

    public void Dispose()
    {
        _logo.Dispose();
    }

    private static RasterOps Record(Action<RasterOps> chain)
    {
        RasterOps ops = (RasterOps)Activator.CreateInstance(typeof(RasterOps), nonPublic: true)!;
        chain(ops);
        return ops;
    }

    private static Rgba8[] Pixels(Raster<Rgba8> raster)
    {
        Rgba8[] result = new Rgba8[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(result.AsSpan(y * raster.Width, raster.Width));
        }

        return result;
    }

    private sealed class CountingFill : IRasterOperation
    {
        public (int, int) Seen { get; private set; }

        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Seen = (input.Destination.Width, input.Destination.Height);
            for (int y = 0; y < input.Destination.Height; y++)
            {
                input.Destination.Row(y).Fill(TPixel.FromRgba8(Rgba8.White));
            }
        }
    }

    private sealed class CancelOnRun(CancellationTokenSource cancel) : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            cancel.Cancel();
        }
    }

    private sealed class Growing : IRasterOperation
    {
        public Size OutputSize(Size input) => new(input.Width + 1, input.Height);

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
        }
    }
}
