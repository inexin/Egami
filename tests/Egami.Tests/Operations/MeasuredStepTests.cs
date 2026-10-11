using Egami.Formats.Png;
using Egami.Operations;
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>The planner's split at steps whose size depends on the pixels (Phase 2.5.2, question 8).</summary>
public class MeasuredStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AChainWithAMeasuredStepEqualsItsHalvesRunByHand()
    {
        using Raster<Rgba8> raster = Spot(60, 40, new Rect(12, 9, 20, 14));
        void Before(RasterOps ops) => ops.Pad(80, 50, Rgba8.Black).Brightness(1.1f);
        void After(RasterOps ops) => ops.Resize(40, 0).Sepia();

        using Raster<Rgba8> chained = raster.Derive(ops => { Before(ops); ops.Add(new CropToBright()); After(ops); }, Ct);

        using Raster<Rgba8> first = raster.Derive(Before, Ct);
        Rect rect = CropToBright.Bounds(first.Frames[0].Plane);
        using Raster<Rgba8> second = first.Derive(ops => { ops.Crop(rect); After(ops); }, Ct);
        AssertSame(second, chained);

        // Apply gives the same pixels and size in place.
        raster.Apply(ops => { Before(ops); ops.Add(new CropToBright()); After(ops); }, Ct);
        AssertSame(second, raster);
    }

    [Fact]
    public void AnimationFramesKeepTheUnionOfTheirRectangles()
    {
        using Raster<Rgba8> animation = Spot(50, 40, new Rect(5, 5, 10, 10));
        Frame<Rgba8> second = animation.Frames.Add(animation.Frames[0]);
        second.Row(5).Clear();
        for (int y = 20; y < 30; y++)
        {
            second.Row(y).Slice(30, 12).Fill(Rgba8.White);
        }

        CropToBright step = new();
        animation.Apply(ops => ops.Add(step), Ct);
        Assert.Equal(new Size(37, 25), new Size(animation.Width, animation.Height));
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(1, step.Measured);
    }

    [Fact]
    public void TwoMeasuredStepsSplitTheChainTwice()
    {
        using Raster<Rgba8> raster = Spot(60, 40, new Rect(10, 10, 30, 20));
        using Raster<Rgba8> result = raster.Derive(ops => ops.Add(new CropToBright()).Pad(50, 40, Rgba8.Black, Anchor.TopLeft).Add(new CropToBright()).Resize(15, 10), Ct);
        Assert.Equal(new Size(15, 10), new Size(result.Width, result.Height));
        Assert.Equal(Rgba8.White, result.GetPixel(7, 5));
    }

    [Fact]
    public void DeriveLeavesTheSourceAloneEvenWhenNothingChanges()
    {
        using Raster<Rgba8> raster = Raster.Create(20, 10, Rgba8.White);
        using Raster<Rgba8> derived = raster.Derive(ops => ops.Add(new CropToBright()), Ct);
        Assert.Equal(new Size(20, 10), new Size(derived.Width, derived.Height));
        derived.Row(0).Fill(Rgba8.Red);
        Assert.Equal(Rgba8.White, raster.GetPixel(0, 0));
    }

    [Fact]
    public void TheMemoryLimitIsCheckedAgainAtTheSplit()
    {
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 200_000 } };
        using Raster<Rgba8> raster = Spot(40, 30, new Rect(5, 5, 10, 10), context);

        // The measured step's own result fits; the 400 × 400 pad after it does not, and fails before any of it runs.
        CountingOperation after = new();
        Assert.Throws<LimitExceededException>(() => raster.Derive(ops => ops.Add(new CropToBright()).Pad(400, 400).Add(after), Ct).Dispose());
        Assert.Equal(0, after.Runs);
    }

    [Fact]
    public void MeasuredStepsCannotRunInsideAScope()
    {
        using Raster<Rgba8> raster = Spot(40, 30, new Rect(5, 5, 10, 10));
        ArgumentException e = Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(new Rect(0, 0, 20, 20), s => s.Add(new CropToBright())), Ct));
        Assert.Contains("size", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscodeDecodesTheWholeImageFirst()
    {
        using Raster<Rgba8> raster = Spot(64, 48, new Rect(20, 10, 16, 12));
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);
        using MemoryStream output = new();
        Raster.Transcode(new MemoryStream(png.ToArray()), output, PngFormat.Info, ops => ops.Add(new CropToBright()).Resize(8, 6));
        using Raster<Rgba8> got = Raster.Load<Rgba8>(output.ToArray());
        using Raster<Rgba8> expected = raster.Derive(ops => ops.Add(new CropToBright()).Resize(8, 6), Ct);
        AssertSame(expected, got);
    }

    [Fact]
    public async Task TranscodeAsyncDecodesTheWholeImageFirst()
    {
        using Raster<Rgba8> raster = Spot(64, 48, new Rect(20, 10, 16, 12));
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);
        using MemoryStream output = new();
        await Raster.TranscodeAsync(new MemoryStream(png.ToArray()), output, PngFormat.Info, ops => ops.Add(new CropToBright()), cancellationToken: Ct);
        using Raster<Rgba8> got = Raster.Load<Rgba8>(output.ToArray());
        Assert.Equal(new Size(16, 12), new Size(got.Width, got.Height));
    }

    internal static Raster<Rgba8> Spot(int width, int height, Rect spot, EgamiContext? context = null)
    {
        Raster<Rgba8> raster = Raster.Create(width, height, Rgba8.Black, context);
        for (int y = spot.Y; y < spot.Bottom; y++)
        {
            raster.Row(y).Slice(spot.X, spot.Width).Fill(Rgba8.White);
        }

        return raster;
    }

    private static void AssertSame(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"row {y} differs");
        }
    }

    /// <summary>A measured step for the tests: crops every frame to the union of the frames' bright (R &gt; 128) pixels.</summary>
    private sealed class CropToBright : IMeasuredStep
    {
        public int Measured { get; private set; }

        public Size OutputSize(Size input) => throw new InvalidOperationException("A measured step is never sized up front.");

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            throw new InvalidOperationException("A measured step never runs itself.");
        }

        public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Measured++;
            Rect union = default;
            foreach (PixelPlane<TPixel> frame in frames)
            {
                Rect rect = Bounds(frame);
                union = union.IsEmpty ? rect : rect.IsEmpty ? union : Union(union, rect);
            }

            Size size = new(frames[0].Width, frames[0].Height);
            return new CropOperation(union.IsEmpty ? new Rect(0, 0, size.Width, size.Height) : union);
        }

        internal static Rect Bounds<TPixel>(PixelPlane<TPixel> plane)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < plane.Height; y++)
            {
                ReadOnlySpan<TPixel> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x].ToRgba8().R > 128)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            return right < 0 ? default : new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        private static Rect Union(Rect a, Rect b)
        {
            int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
            return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
        }
    }

    private sealed class CountingOperation : IRasterOperation
    {
        public int Runs { get; private set; }

        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Runs++;
            if (!input.IsInPlace)
            {
                for (int y = 0; y < input.Source.Height; y++)
                {
                    input.Source.Row(y).CopyTo(input.Destination.Row(y));
                }
            }
        }
    }
}
