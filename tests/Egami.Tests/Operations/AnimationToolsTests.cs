using System.IO.Compression;
using System.Text.Json;

using Egami.Formats.Gif;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// Phase 2.5.5 stage C: MergeDuplicates, DropZeroDelay and DrawAnimation by index against ImageMagick 7.1.1's -layers RemoveDups,
/// RemoveZero and Composite (tools/make-analysis-references.py); Retime, ChangeSpeed, Reverse and DrawAnimation by time against
/// their specification on synthetic timelines; and the pipeline rules.
/// </summary>
public class AnimationToolsTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "analysis");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "analysis");
    private static readonly Lazy<JsonElement> Json = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "analysis.json"))).RootElement);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Makes an animation of flat frames: frame i is <paramref name="colors"/>[i], shown for <paramref name="delays"/>[i] ms.</summary>
    private static Raster<Rgba8> Animation(int[] delays, Rgba8[] colors, int width = 8, int height = 6, EgamiContext? context = null)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, colors[0], context);
        raster.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(delays[0]);
        for (int i = 1; i < delays.Length; i++)
        {
            using Raster<Rgba8> frame = Raster.Create<Rgba8>(width, height, colors[i]);
            raster.Frames.Add(new Frame<Rgba8>(frame.Frames[0].Plane, new FrameMetadata { Delay = TimeSpan.FromMilliseconds(delays[i]) }));
        }

        return raster;
    }

    private static Rgba8 Gray(int v) => new((byte)v, (byte)v, (byte)v, 255);

    private static int[] Delays(Raster raster) => [.. Enumerable.Range(0, raster.FrameCount).Select(i => (int)raster.FrameMetadataAt(i).Delay.TotalMilliseconds)];

    /// <summary>Returns which frame shows at millisecond <paramref name="t"/>, or -1 past the end.</summary>
    private static int At(Raster raster, double t)
    {
        double start = 0;
        for (int i = 0; i < raster.FrameCount; i++)
        {
            double end = start + raster.FrameMetadataAt(i).Delay.TotalMilliseconds;
            if (t < end)
            {
                return i;
            }

            start = end;
        }

        return -1;
    }

    private static byte[] Pixels(Raster raster)
    {
        using Raster<Rgba8> rgba = raster.As<Rgba8>();
        List<byte> all = [];
        byte[] frame = new byte[rgba.Width * rgba.Height * 4];
        for (int f = 0; f < rgba.FrameCount; f++)
        {
            using Raster<Rgba8> one = rgba.CopyFrame(f);
            one.CopyPixelsTo(frame.AsSpan());
            all.AddRange(frame);
        }

        return [.. all];
    }

    private static byte[] ReadGz(string path)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream gz = new(file, CompressionMode.Decompress);
        using MemoryStream memory = new();
        gz.CopyTo(memory);
        return memory.ToArray();
    }

    [Theory]
    [InlineData("dups.gif", "RemoveDups")]
    [InlineData("zero.gif", "RemoveZero")]
    [InlineData("allzero.gif", "RemoveZero")]
    public void FrameRemovalMatchesImageMagick(string file, string method)
    {
        using Raster gif = Raster.Load(Path.Combine(Images, file));
        using Raster result = gif.Derive(o => _ = method == "RemoveDups" ? o.MergeDuplicates() : o.DropZeroDelay(), Ct);
        int[] expected = [.. Json.Value.GetProperty("frames").GetProperty($"{file}:{method}").EnumerateArray().Select(e => e.GetInt32())];
        Assert.Equal(expected, Delays(result));
        Assert.Equal(ReadGz(Path.Combine(References, $"{file}.{method}.rgba.gz")), Pixels(result));
    }

    [Fact]
    public void DrawAnimationByIndexMatchesImageMagickComposite()
    {
        using Raster baseGif = Raster.Load(Path.Combine(Images, "base.gif"));
        using Raster over = Raster.Load(Path.Combine(Images, "over.gif"));
        using Raster result = baseGif.Derive(o => o.DrawAnimation(over, new Point(5, 3), new AnimationOverlayOptions { Timing = AnimationTiming.ByIndex }), Ct);
        int[] expected = [.. Json.Value.GetProperty("frames").GetProperty("base.gif:Composite").EnumerateArray().Select(e => e.GetInt32())];
        Assert.Equal(expected, Delays(result));
        Assert.Equal(ReadGz(Path.Combine(References, "base.gif.Composite.rgba.gz")), Pixels(result));
    }

    [Fact]
    public void MergeDuplicatesNeverChangesWhatIsOnScreen()
    {
        int[] delays = [40, 30, 0, 50, 10, 20, 70];
        Rgba8[] colors = [Gray(1), Gray(1), Gray(2), Gray(2), Gray(2), Gray(3), Gray(1)];
        using Raster<Rgba8> anim = Animation(delays, colors);
        using Raster<Rgba8> merged = anim.Derive(o => o.MergeDuplicates(), Ct);
        Assert.Equal(4, merged.FrameCount);
        for (int t = 0; t < delays.Sum(); t++)
        {
            Assert.Equal(anim.Frames[At(anim, t)].Row(0)[0], merged.Frames[At(merged, t)].Row(0)[0]);
        }
    }

    [Fact]
    public void MergeDuplicatesWithToleranceAndMetadata()
    {
        using Raster<Rgba8> anim = Animation([100, 100, 100], [Gray(100), Gray(101), Gray(140)]);
        anim.Frames[0].Metadata.Disposal = FrameDisposal.ClearToBackground;
        using Raster<Rgba8> exact = anim.Derive(o => o.MergeDuplicates(), Ct);
        Assert.Equal(3, exact.FrameCount);
        using Raster<Rgba8> loose = anim.Derive(o => o.MergeDuplicates(0.01f), Ct);
        Assert.Equal([200, 100], Delays(loose));
        Assert.Equal(FrameDisposal.ClearToBackground, loose.Frames[0].Metadata.Disposal);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(7.5)]
    [InlineData(33)]
    public void RetimeKeepsDurationAndShowsTheFrameAtEachTick(double fps)
    {
        int[] delays = [70, 30, 0, 120, 45, 15, 200, 20];
        Rgba8[] colors = [.. Enumerable.Range(0, delays.Length).Select(i => Gray(i * 20))];
        using Raster<Rgba8> anim = Animation(delays, colors);
        using Raster<Rgba8> retimed = anim.Derive(o => o.Retime(fps), Ct);
        int total = delays.Sum();
        Assert.Equal(total, Delays(retimed).Sum());
        Assert.True(retimed.FrameCount <= anim.FrameCount);
        double tick = 1000 / fps;
        for (int k = 0; k * tick < total; k++)
        {
            double t = Math.Round(k * tick, MidpointRounding.AwayFromZero);
            Assert.Equal(anim.Frames[At(anim, k * tick)].Row(0)[0], retimed.Frames[At(retimed, t)].Row(0)[0]);
        }

        // Neighbours never show the same frame twice.
        for (int i = 1; i < retimed.FrameCount; i++)
        {
            Assert.NotEqual(retimed.Frames[i - 1].Row(0)[0], retimed.Frames[i].Row(0)[0]);
        }
    }

    [Fact]
    public void RetimeAboveTheOwnRateChangesNothing()
    {
        using Raster<Rgba8> anim = Animation([100, 70, 130], [Gray(1), Gray(2), Gray(3)]);
        using Raster<Rgba8> same = anim.Derive(o => o.Retime(20), Ct);
        Assert.Equal([100, 70, 130], Delays(same));
    }

    [Fact]
    public void ChangeSpeedAndReverse()
    {
        using Raster<Rgba8> anim = Animation([100, 33, 50], [Gray(1), Gray(2), Gray(3)]);
        using Raster<Rgba8> fast = anim.Derive(o => o.ChangeSpeed(2), Ct);
        Assert.Equal([50, 17, 25], Delays(fast));
        using Raster<Rgba8> back = anim.Derive(o => o.Reverse(), Ct);
        Assert.Equal([50, 33, 100], Delays(back));
        Assert.Equal(Gray(3), back.Frames[0].Row(0)[0]);
        using Raster<Rgba8> twice = anim.Derive(o => o.Reverse().Reverse(), Ct);
        Assert.Equal(Pixels(anim), Pixels(twice));
        Assert.Equal(Delays(anim), Delays(twice));
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.Apply(o => o.ChangeSpeed(0), Ct));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 50)]
    public void DrawAnimationByTimeMatchesABruteForceTimeline(int repeat, int start)
    {
        int[] baseDelays = [100, 70, 230];
        int[] overDelays = [40, 60, 30];
        using Raster<Rgba8> background = Animation(baseDelays, [Gray(10), Gray(20), Gray(30)], 12, 10);
        using Raster<Rgba8> sticker = Animation(overDelays, [new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255)], 4, 4);
        AnimationOverlayOptions options = new() { Repeat = repeat, Start = TimeSpan.FromMilliseconds(start) };
        using Raster<Rgba8> result = background.Derive(o => o.DrawAnimation(sticker, new Point(2, 3), options), Ct);
        int total = baseDelays.Sum();
        Assert.Equal(total, Delays(result).Sum());
        int cycle = overDelays.Sum();
        for (int t = 0; t < total; t++)
        {
            Rgba8 under = background.Frames[At(background, t)].Row(0)[0];
            int local = t - start;
            bool shown = local >= 0 && (repeat == 0 || local < repeat * cycle);
            Rgba8 expected = shown ? sticker.Frames[At(sticker, local % cycle)].Row(0)[0] : under;
            Frame<Rgba8> frame = result.Frames[At(result, t)];
            Assert.Equal(under, frame.Row(0)[0]);
            Assert.Equal(expected, frame.Row(4)[3]);
        }
    }

    [Fact]
    public void StillBaseLastsOneOverlayCycleAndTakesItsLoopCount()
    {
        using Raster<Rgba8> still = Raster.Create<Rgba8>(10, 10, Gray(50));
        using Raster<Rgba8> sticker = Animation([40, 60], [new(255, 0, 0, 255), new(0, 255, 0, 255)], 2, 2);
        sticker.Metadata.LoopCount = 3;
        using Raster<Rgba8> result = still.Derive(o => o.DrawAnimation(sticker, new Point(0, 0)), Ct);
        Assert.Equal([40, 60], Delays(result));
        Assert.Equal(3, result.Metadata.LoopCount);
        using Raster<Rgba8> twice = still.Derive(o => o.DrawAnimation(sticker, new Point(0, 0), new AnimationOverlayOptions { Repeat = 2 }), Ct);
        Assert.Equal([40, 60, 40, 60], Delays(twice));
    }

    [Fact]
    public void DrawAnimationChecksTheFrameCountBeforeDrawing()
    {
        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxFrames = 5 } };
        using Raster<Rgba8> limited = Animation([100, 100, 100], [Gray(1), Gray(2), Gray(3)], context: tight);
        using Raster<Rgba8> sticker = Animation([70, 70], [Gray(200), Gray(250)], 2, 2);
        Assert.Throws<LimitExceededException>(() => limited.Apply(o => o.DrawAnimation(sticker, new Point(0, 0)), Ct));
        Assert.Equal(3, limited.FrameCount);
    }

    [Fact]
    public void FrameStepsThrowInsideWithin()
    {
        using Raster<Rgba8> anim = Animation([100, 100], [Gray(1), Gray(1)]);
        Assert.Throws<ArgumentException>(() => anim.Apply(o => o.Within(new Rect(0, 0, 2, 2), s => s.Reverse()), Ct));
    }

    [Fact]
    public void FrameStepsMixWithPerFrameSteps()
    {
        using Raster<Rgba8> anim = Animation([100, 100, 50], [Gray(10), Gray(12), Gray(200)]);
        anim.Apply(o => o.Invert().MergeDuplicates(0.02f).Resize(4, 3).Reverse(), Ct);
        Assert.Equal(2, anim.FrameCount);
        Assert.Equal(4, anim.Width);
        Assert.Equal([50, 200], Delays(anim));
        Assert.Equal(Gray(55), anim.Frames[0].Row(0)[0]);
    }

    [Fact]
    public void TranscodeEqualsFullDecode()
    {
        string path = Path.Combine(Images, "dups.gif");
        using MemoryStream streamed = new();
        using (FileStream input = File.OpenRead(path))
        {
            Raster.Transcode(input, streamed, GifFormat.Info, o => o.MergeDuplicates().Retime(5));
        }

        streamed.Position = 0;
        using Raster fromStream = Raster.Load(streamed);
        using Raster full = Raster.Load(path);
        full.Apply(o => o.MergeDuplicates().Retime(5), Ct);
        Assert.Equal(Delays(full), Delays(fromStream));
        Assert.Equal(Pixels(full), Pixels(fromStream));
    }
}
