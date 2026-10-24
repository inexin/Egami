using Egami.Pixels;
using Egami.Video.Analysis;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// The public scene detector (V1.4 Build Plan tasks 3 and 4): cuts land on the right frame for every method, motion, flashes and
/// fades are not taken for cuts, fades through black start a scene in the dark run, and memory stays flat.
/// </summary>
public sealed class SceneDetectorTests
{
    private static readonly TestVideoFormat Small = new() { Width = 96, Height = 54, FrameRate = new Rational(25, 1) };

    [Theory]
    [InlineData(SceneMethod.Adaptive)]
    [InlineData(SceneMethod.Content)]
    [InlineData(SceneMethod.Histogram)]
    public void CutsLandOnTheirFrame(SceneMethod method)
    {
        // Shots of 30 frames: solid red, a gradient, solid blue, then the moving test pattern.
        List<Shot> shots =
        [
            Shot.Solid(new Rgba8(220, 30, 30, 255), 30),
            Shot.Gradient(new Rgba8(0, 0, 0, 255), new Rgba8(255, 255, 255, 255), 30),
            Shot.Solid(new Rgba8(30, 40, 220, 255), 30),
            Shot.Pattern(30),
        ];
        SceneDetector detector = new(new SceneOptions { Method = method });
        Feed(detector, shots);
        Assert.Equal([0L, 30, 60, 90], detector.Cuts.Select(c => c.Frame));
        Assert.Equal(SceneStart.First, detector.Cuts[0].Kind);
        Assert.All(detector.Cuts.Skip(1), c => Assert.Equal(SceneStart.Cut, c.Kind));
        Assert.All(detector.Cuts.Skip(1), c => Assert.InRange(c.Score, 0.15, 1));

        IReadOnlyList<Scene> scenes = detector.Scenes;
        Assert.Equal(4, scenes.Count);
        Assert.Equal((30L, 59L), (scenes[1].StartFrame, scenes[1].EndFrame));
        Assert.Equal(1.2, scenes[1].Start.Seconds, 9);
        Assert.Equal(2.4, scenes[1].End.Seconds, 9);
        Assert.Equal(119L, scenes[3].EndFrame);
        Assert.Equal(4.8, scenes[3].End.Seconds, 9);
        Assert.Equal(1.8, scenes[1].Middle.Seconds, 9);
    }

    [Fact]
    public void MotionIsNotACut()
    {
        // The pattern's marker crosses the picture every second: every frame changes, but steadily.
        SceneDetector detector = new();
        Feed(detector, [Shot.Pattern(100)]);
        Assert.Single(detector.Cuts);
    }

    [Fact]
    public void AFlashIsMergedAway()
    {
        List<Shot> shots = [Shot.Solid(new Rgba8(40, 120, 40, 255), 25), Shot.Solid(new Rgba8(255, 255, 255, 255), 2), Shot.Solid(new Rgba8(40, 120, 40, 255), 25)];
        SceneDetector merge = new();
        Feed(merge, shots);
        Assert.Single(merge.Cuts);

        // Without the flash test only the minimum length applies: the flash's first cut stays, the cut back is too close.
        SceneDetector suppress = new(new SceneOptions { Flash = FlashMode.Suppress });
        Feed(suppress, shots);
        Assert.Equal([0L, 25], suppress.Cuts.Select(c => c.Frame));
    }

    [Fact]
    public void ACutTooSoonAfterTheLastIsNotTaken()
    {
        List<Shot> shots = [Shot.Solid(new Rgba8(200, 30, 30, 255), 20), Shot.Solid(new Rgba8(30, 30, 200, 255), 5), Shot.Solid(new Rgba8(30, 200, 30, 255), 20)];
        SceneDetector detector = new(new SceneOptions { MinSceneLength = MediaTime.FromSeconds(0.5) });
        Feed(detector, shots);
        Assert.Equal([0L, 20], detector.Cuts.Select(c => c.Frame));

        SceneDetector shorter = new(new SceneOptions { MinSceneLength = MediaTime.FromSeconds(0.1) });
        Feed(shorter, shots);
        Assert.Equal([0L, 20, 25], shorter.Cuts.Select(c => c.Frame));
    }

    [Fact]
    public void AFadeThroughBlackStartsTheSceneInTheDarkRun()
    {
        // Red for 20 frames, down to black over 10, black for 6, up to blue over 10, blue for 20.
        List<Rgba8> colors = [];
        void Add(double r, double g, double b) => colors.Add(new Rgba8((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255), 255));
        for (int i = 0; i < 20; i++)
        {
            Add(0.8, 0.1, 0.1);
        }

        for (int i = 1; i <= 10; i++)
        {
            double k = 1 - (i / 10.0);
            Add(0.8 * k, 0.1 * k, 0.1 * k);
        }

        for (int i = 0; i < 6; i++)
        {
            Add(0, 0, 0);
        }

        for (int i = 1; i <= 10; i++)
        {
            double k = i / 10.0;
            Add(0.1 * k, 0.1 * k, 0.8 * k);
        }

        for (int i = 0; i < 20; i++)
        {
            Add(0.1, 0.1, 0.8);
        }

        SceneDetector detector = new();
        int index = 0;
        foreach (Rgba8 color in colors)
        {
            using YuvVideoFrame frame = SolidFrame(color, index++);
            detector.Add(frame);
        }

        detector.Complete(new MediaTime(index, new Rational(1, 25)));

        // The dark run is from the frame where the mean luma drops under the fade level to the first frame above it.
        SceneCut fade = Assert.Single(detector.Cuts.Skip(1));
        Assert.Equal(SceneStart.Fade, fade.Kind);
        Assert.InRange(fade.Frame, 30, 36);
        Fade fadeOut = Assert.Single(detector.Fades, f => f.Kind == FadeKind.Out);
        Assert.InRange(fadeOut.StartFrame, 19, 22);
        Assert.InRange(fadeOut.EndFrame, 27, 31);
        Fade fadeIn = Assert.Single(detector.Fades, f => f.Kind == FadeKind.In);
        Assert.InRange(fadeIn.StartFrame, 34, 40);
        Assert.InRange(fadeIn.EndFrame, 44, 47);

        // Without fade detection a fade is gradual and makes no cut at all.
        SceneDetector plain = new(new SceneOptions { DetectFades = false });
        int n = 0;
        foreach (Rgba8 color in colors)
        {
            using YuvVideoFrame frame = SolidFrame(color, n++);
            plain.Add(frame);
        }

        plain.Complete(new MediaTime(n, new Rational(1, 25)));
        Assert.Single(plain.Cuts);
        Assert.Empty(plain.Fades);
    }

    [Fact]
    public void ACutBetweenDarkShotsIsACut()
    {
        // Dark red to dark blue, both with a mean luma of about 4 % of the range: a cut, not a fade (a scene in a dark cave keeps
        // its cuts). A fade level of 0.06, the old default, took both shots for black and found nothing.
        List<Shot> shots = [Shot.Solid(new Rgba8(50, 0, 0, 255), 20), Shot.Solid(new Rgba8(0, 12, 50, 255), 20)];
        SceneDetector detector = new();
        Feed(detector, shots);
        SceneCut cut = Assert.Single(detector.Cuts.Skip(1));
        Assert.Equal((20L, SceneStart.Cut), (cut.Frame, cut.Kind));
        Assert.Empty(detector.Fades);

        SceneDetector old = new(new SceneOptions { FadeLevel = 0.06 });
        Feed(old, shots);
        Assert.Single(old.Cuts);
    }

    [Fact]
    public void ACutThroughBlackIsOneSceneChange()
    {
        List<Shot> shots = [Shot.Solid(new Rgba8(200, 200, 30, 255), 20), Shot.Solid(new Rgba8(0, 0, 0, 255), 8), Shot.Solid(new Rgba8(30, 200, 200, 255), 20)];
        SceneDetector detector = new();
        Feed(detector, shots);
        SceneCut cut = Assert.Single(detector.Cuts.Skip(1));
        Assert.Equal((24L, SceneStart.Fade), (cut.Frame, cut.Kind));

        // FadeBias moves it to the run's ends.
        SceneDetector early = new(new SceneOptions { FadeBias = -1 });
        Feed(early, shots);
        Assert.Equal(20, early.Cuts[1].Frame);
        SceneDetector late = new(new SceneOptions { FadeBias = 1 });
        Feed(late, shots);
        Assert.Equal(28, late.Cuts[1].Frame);

        // Without fade detection the two cuts are each taken, when the black is longer than the minimum scene length.
        SceneDetector cuts = new(new SceneOptions { DetectFades = false, MinSceneLength = MediaTime.FromSeconds(0.2) });
        Feed(cuts, shots);
        Assert.Equal([0L, 20, 28], cuts.Cuts.Select(c => c.Frame));
    }

    [Fact]
    public void AVideoThatOpensInBlackHasNoExtraScene()
    {
        SceneDetector detector = new();
        Feed(detector, [Shot.Solid(new Rgba8(0, 0, 0, 255), 15), Shot.Solid(new Rgba8(120, 120, 200, 255), 30)]);
        Assert.Single(detector.Cuts);
    }

    [Fact]
    public void SettledFramesTrailTheNewestFrame()
    {
        SceneDetector detector = new();
        int index = 0;
        foreach (Shot shot in (Shot[])[Shot.Solid(new Rgba8(200, 30, 30, 255), 20), Shot.Solid(new Rgba8(30, 30, 200, 255), 30)])
        {
            foreach (YuvVideoFrame frame in shot.Frames(index))
            {
                using (frame)
                {
                    detector.Add(frame);
                }

                index++;
                Assert.True(detector.SettledFrames <= detector.FrameCount);
                Assert.All(detector.Cuts, c => Assert.True(c.Frame < detector.SettledFrames || c.Frame == 0));
            }
        }

        // The cut at 20 is held for the flash test until 0.6 s (15 frames) have passed.
        Assert.Equal(2, detector.Cuts.Count);
        detector.Complete(new MediaTime(index, new Rational(1, 25)));
        Assert.Equal(detector.FrameCount, detector.SettledFrames);
    }

    [Fact]
    public void SeriesAndHigherBitDepths()
    {
        TestVideoFormat deep = Small with { BitDepth = 10 };
        SceneDetector detector = new(new SceneOptions { KeepSeries = true });
        int n = 0;
        foreach (YuvVideoFrame frame in TestSources.Solid(deep, MediaTime.FromSeconds(1), new Rgba8(255, 255, 255, 255))
            .Concat(TestSources.Solid(deep, MediaTime.FromSeconds(1), new Rgba8(0, 0, 255, 255))))
        {
            using (frame)
            {
                detector.Add(new YuvVideoFrame(Clone(frame.Raster), new MediaTime(n++, new Rational(1, 25))));
            }
        }

        detector.Complete(new MediaTime(n, new Rational(1, 25)));
        Assert.Equal(50, detector.Series.Count);
        Assert.Equal(1, detector.Series[0].Luma, 2);
        Assert.Equal([0L, 25], detector.Cuts.Select(c => c.Frame));
    }

    [Fact]
    public void OptionsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneDetector(new SceneOptions { Threshold = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneDetector(new SceneOptions { Window = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneDetector(new SceneOptions { AdaptiveRatio = 0.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneDetector(new SceneOptions { FadeBias = 2 }));
        SceneDetector done = new();
        done.Complete(MediaTime.Zero);
        using YuvVideoFrame frame = SolidFrame(new Rgba8(1, 2, 3, 255), 0);
        Assert.Throws<InvalidOperationException>(() => done.Add(frame));
    }

    [Fact]
    public void NoAllocationPerFrame()
    {
        List<YuvVideoFrame> frames = [.. Shot.Pattern(60).Frames(0)];
        try
        {
            SceneDetector detector = new();
            for (int i = 0; i < 20; i++)
            {
                detector.Add(frames[i]);
            }

            FrameAllocations.AssertNone(nameof(SceneDetector), frames.Count - 20, i => detector.Add(frames[20 + i]));
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    internal static YuvVideoFrame SolidFrame(Rgba8 color, int index)
    {
        YuvRaster raster = YuvPainter.Create(Small);
        new YuvPainter(raster).Fill(0, 0, Small.Width, Small.Height, color.R / 255.0, color.G / 255.0, color.B / 255.0);
        return new YuvVideoFrame(raster, new MediaTime(index, new Rational(1, 25)), new MediaTime(1, new Rational(1, 25)));
    }

    private static YuvRaster Clone(YuvRaster source)
    {
        YuvRaster copy = source.BitDepth == 8
            ? YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, source.Color)
            : YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, source.BitDepth, source.Color);
        YuvPainter.Copy(source, copy);
        return copy;
    }

    private static void Feed(SceneDetector detector, IEnumerable<Shot> shots)
    {
        int index = 0;
        foreach (Shot shot in shots)
        {
            foreach (YuvVideoFrame frame in shot.Frames(index))
            {
                using (frame)
                {
                    detector.Add(frame);
                }

                index++;
            }
        }

        detector.Complete(new MediaTime(index, new Rational(1, 25)));
    }

    /// <summary>A run of frames for a test clip, numbered from where it starts in the clip.</summary>
    internal sealed record Shot(Func<MediaTime, IEnumerable<YuvVideoFrame>> Make, int Count)
    {
        public static Shot Solid(Rgba8 color, int count) => new(d => TestSources.Solid(Small, d, color), count);

        public static Shot Gradient(Rgba8 from, Rgba8 to, int count) => new(d => TestSources.Gradient(Small, d, from, to), count);

        public static Shot Pattern(int count) => new(d => TestSources.Pattern(Small, d), count);

        public IEnumerable<YuvVideoFrame> Frames(int first)
        {
            Rational timeBase = new(1, 25);
            int i = 0;
            foreach (YuvVideoFrame frame in Make(new MediaTime(Count, timeBase)))
            {
                // The source numbers from 0; the clip numbers on from the shots before.
                yield return new YuvVideoFrame(TakeRaster(frame), new MediaTime(first + i, timeBase), new MediaTime(1, timeBase));
                i++;
            }
        }

        private static YuvRaster TakeRaster(YuvVideoFrame frame)
        {
            YuvRaster copy = Clone(frame.Raster);
            frame.Dispose();
            return copy;
        }
    }
}
