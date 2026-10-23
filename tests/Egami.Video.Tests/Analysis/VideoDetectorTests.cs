using Egami.Pixels;
using Egami.Video.Analysis;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// The stage B video detectors (V1.4 Build Plan tasks 6 to 11) on generated frames: black, freeze, crop, interlace, duplicates and
/// the signal meter find what was drawn, at 8 and 10 bits, and allocate nothing per frame.
/// </summary>
public sealed class VideoDetectorTests
{
    private static readonly Rational Rate = new(25, 1);

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void BlackRangesLastAtLeastTheMinimum(int depth)
    {
        BlackDetector detector = new(new BlackOptions { KeepSeries = true });
        Run(detector, 175, i => i is >= 75 and < 150 ? Solid(depth, 0, 0, 0) : Solid(depth, 0.8, 0.1, 0.1));
        DetectedRange range = Assert.Single(detector.Ranges);
        Assert.Equal(3.0, range.Start.Seconds, 9);
        Assert.Equal(6.0, range.End.Seconds, 9);
        Assert.Equal(1, range.Value, 9);
        Assert.Equal(175, detector.Series.Count);
        Assert.Equal(0, detector.Series[0].Value);

        // One second of black is under blackdetect's 2 s.
        BlackDetector shorter = new();
        Run(shorter, 100, i => i is >= 25 and < 50 ? Solid(depth, 0, 0, 0) : Solid(depth, 0.8, 0.1, 0.1));
        Assert.Empty(shorter.Ranges);
    }

    [Fact]
    public void BlackCountsSamplesNearBlackLevel()
    {
        // Limited range: black is 16 and the 10 % threshold 37; dark gray at 0.08 of the range counts, 0.12 does not.
        BlackDetector detector = new(new BlackOptions { MinDuration = MediaTime.Zero });
        using (YuvVideoFrame dark = Frame(Solid(8, 0.08, 0.08, 0.08), 0))
        {
            detector.Add(dark);
            Assert.Equal(1, detector.LastRatio);
        }

        using (YuvVideoFrame gray = Frame(Solid(8, 0.12, 0.12, 0.12), 1))
        {
            detector.Add(gray);
            Assert.Equal(0, detector.LastRatio);
        }

        // Half black, half white: under the 98 % picture ratio.
        using YuvVideoFrame half = Frame(Painted(8, p => p.Fill(0, 0, 32, 36, 1, 1, 1)), 2);
        detector.Add(half);
        Assert.Equal(0.5, detector.LastRatio, 9);
        detector.Complete(MediaTime.FromSeconds(3.0 / 25));
        Assert.Single(detector.Ranges);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void FreezeRangesStartWhereThePictureStops(int depth)
    {
        using FreezeDetector detector = new();

        // A bar moves for 2 s, stands for 3 s, moves again for 1 s.
        Run(detector, 150, i => Bar(depth, i < 50 ? i : i < 125 ? 49 : i - 75));
        DetectedRange range = Assert.Single(detector.Ranges);
        Assert.Equal(49 / 25.0, range.Start.Seconds, 9);
        Assert.Equal(5.0, range.End.Seconds, 9);
        Assert.Equal(0, range.Value);

        using FreezeDetector moving = new();
        Run(moving, 100, i => Bar(depth, i));
        Assert.Empty(moving.Ranges);
    }

    [Fact]
    public void FreezeToleratesNoiseUnderTheFloor()
    {
        using FreezeDetector detector = new(new FreezeOptions { Noise = 0.01 });
        Run(detector, 75, i => Painted(8, p =>
        {
            p.Fill(0, 0, 64, 36, 0.5, 0.5, 0.5);
            p.Fill(i % 7, 3, (i % 7) + 1, 4, 0.6, 0.5, 0.5);
        }));
        DetectedRange range = Assert.Single(detector.Ranges);
        Assert.Equal(0, range.Start.Seconds);
        Assert.Equal(3.0, range.End.Seconds, 9);
        Assert.InRange(range.Value, 1e-6, 0.01);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void CropFindsLetterboxAndPillarbox(int depth)
    {
        CropDetector letterbox = new();
        Run(letterbox, 20, i => Painted(depth, p => p.Ramp(0, 6, 64, 30, 0.2, 0.9), width: 64, height: 36));
        CropReport report = letterbox.Report;
        Assert.Equal(new Rect(0, 6, 64, 24), report.Crop);
        Assert.Equal(report.Crop, report.MostCommon);
        Assert.Equal(18, report.Frames);
        Assert.True(report.HasBorders);
        CropRun run = Assert.Single(report.Runs);
        Assert.Equal(2 / 25.0, run.Start.Seconds, 9);
        Assert.Equal(20 / 25.0, run.End.Seconds, 9);

        CropDetector pillarbox = new();
        Run(pillarbox, 10, i => Painted(depth, p => p.Fill(10, 0, 54, 36, 0.6, 0.4, 0.3), width: 64, height: 36));
        Assert.Equal(new Rect(10, 0, 44, 36), pillarbox.Report.Crop);
    }

    [Fact]
    public void CropKeepsEveryFramesPicture()
    {
        // Mostly a 4:3 window in a 16:9 frame, but a few frames are wider: the crop keeps them whole, the common one does not.
        CropDetector detector = new();
        Run(detector, 30, i => Painted(8, p => p.Fill(i is >= 10 and < 14 ? 4 : 8, 0, i is >= 10 and < 14 ? 60 : 56, 36, 0.7, 0.7, 0.7), width: 64, height: 36));
        CropReport report = detector.Report;
        Assert.Equal(new Rect(4, 0, 56, 36), report.Crop);
        Assert.Equal(new Rect(8, 0, 48, 36), report.MostCommon);
        Assert.Equal(3, report.Runs.Count);

        // An all-black frame has no picture to measure.
        CropDetector black = new(new CropOptions { Skip = 0 });
        Run(black, 3, _ => Solid(8, 0, 0, 0));
        Assert.Equal(0, black.Report.Frames);
        Assert.False(black.Report.HasBorders);
    }

    [Theory]
    [InlineData(Scan.Progressive, InterlaceVerdict.Progressive)]
    [InlineData(Scan.TopFirst, InterlaceVerdict.InterlacedTopFirst)]
    [InlineData(Scan.BottomFirst, InterlaceVerdict.InterlacedBottomFirst)]
    [InlineData(Scan.Telecine, InterlaceVerdict.Telecined)]
    public void InterlaceTellsHowFieldsWereTaken(Scan scan, InterlaceVerdict verdict)
    {
        InterlaceDetector detector = new();
        Run(detector, 40, i => Fields(scan, i));
        InterlaceReport report = detector.Report;
        Assert.Equal(verdict, report.Verdict);
        if (scan == Scan.Telecine)
        {
            Assert.InRange(report.RepeatedTop + report.RepeatedBottom, 14, 16);
        }
        else
        {
            Assert.Equal(0, report.RepeatedTop + report.RepeatedBottom);
        }
    }

    [Fact]
    public void InterlaceCannotTellStillPictures()
    {
        InterlaceDetector detector = new();
        Run(detector, 10, _ => Solid(8, 0.3, 0.6, 0.2));
        Assert.Equal(InterlaceVerdict.Unknown, detector.Report.Verdict);
        Assert.Equal(9, detector.Report.Undetermined);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void DuplicatesAreCountedInRuns(int depth)
    {
        // Every picture shown twice, then one shown four times; the last kept frame is the reference.
        DuplicateDetector detector = new();
        Run(detector, 20, i => Bar(depth, i < 16 ? i / 2 * 3 : 30));
        Assert.Equal(8 + 3, detector.DuplicateCount);
        Assert.Equal(9, detector.Ranges.Count);
        Assert.All(detector.Ranges.Take(8), r => Assert.Equal(1, r.Value));
        DetectedRange last = detector.Ranges[^1];
        Assert.Equal(3, last.Value);
        Assert.Equal(17 / 25.0, last.Start.Seconds, 9);
        Assert.Equal(20 / 25.0, last.End.Seconds, 9);
    }

    [Fact]
    public void SmallChangesStillDuplicate()
    {
        // A few samples off by a little stay under mpdecimate's thresholds; a moved bar does not.
        DuplicateDetector detector = new();
        Run(detector, 3, i => Painted(8, p =>
        {
            p.Fill(0, 0, 64, 36, 0.4, 0.4, 0.4);
            p.Fill(5 + i, 5, 6 + i, 6, 0.45, 0.4, 0.4);
        }));
        Assert.Equal(2, detector.DuplicateCount);

        DuplicateDetector moved = new();
        Run(moved, 3, i => Bar(8, i * 8));
        Assert.Equal(0, moved.DuplicateCount);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void SignalLevelsAreCodeValues(int depth)
    {
        SignalMeter meter = new(new SignalOptions { KeepSeries = true });
        Run(meter, 3, _ => Solid(depth, 0.5, 0.5, 0.5));
        SignalReport report = meter.Report;
        int shift = depth - 8;
        double gray = Math.Round((16 + (219 * 0.5)) * (1 << shift), MidpointRounding.AwayFromZero);
        Assert.Equal(new PlaneLevels(gray, gray, gray, gray, gray), report.Y);
        Assert.Equal(128 << shift, report.Cb.Mean);
        Assert.Equal(0, report.Saturation.Max);
        Assert.Equal(0, report.OutOfRange);
        Assert.Equal(SignalRange.Limited, report.Range);
        Assert.Equal(AlphaUse.None, report.Alpha);
        Assert.Equal(depth, report.BitDepth);
        Assert.Equal(3, meter.Series.Count);
        Assert.Null(report.Blur);
    }

    [Fact]
    public void SignalPercentilesSaturationAndRange()
    {
        SignalMeter meter = new();
        using (YuvVideoFrame ramp = Frame(Painted(8, p => p.Ramp(0, 0, 64, 36, 0, 1)), 0))
        {
            meter.Add(ramp);
        }

        SignalReport report = meter.Report;
        Assert.Equal(16, report.Y.Min);
        Assert.Equal(235, report.Y.Max);
        Assert.InRange(report.Y.Low, 33, 41);
        Assert.InRange(report.Y.High, 210, 218);
        Assert.InRange(report.Y.Mean, 124, 127);

        // 75 % red is (Cb, Cr) = (-19, +84) from the middle, 86 away; full-range black and white are outside broadcast range.
        SignalMeter red = new();
        Run(red, 1, _ => Solid(8, 0.75, 0, 0));
        Assert.InRange(red.Report.Saturation.Mean, 84, 88);

        SignalMeter full = new();
        Run(full, 1, _ =>
        {
            YuvRaster<byte> raster = YuvRaster.Create(64, 36, ChromaFormat.Yuv420, YuvColor.Jpeg);
            new YuvPainter(raster).FillCodes(0, 0, 64, 18, 0, 128, 128);
            new YuvPainter(raster).FillCodes(0, 18, 64, 36, 255, 128, 128);
            return raster;
        });
        Assert.Equal(1, full.Report.OutOfRange);
        Assert.Equal(SignalRange.Full, full.Report.Range);
    }

    [Theory]
    [InlineData(255, 255, AlphaUse.Opaque)]
    [InlineData(235, 128, AlphaUse.Straight)]
    [InlineData(70, 128, AlphaUse.Premultiplied)]
    public void SignalTellsHowAlphaIsUsed(int luma, int alpha, AlphaUse use)
    {
        SignalMeter meter = new();
        Run(meter, 1, _ =>
        {
            YuvRaster<byte> raster = YuvRaster.Create(64, 36, ChromaFormat.Yuv420, YuvColor.Bt709Limited, alpha: true);
            new YuvPainter(raster).FillCodes(0, 0, 64, 36, luma, 128, 128);
            for (int y = 0; y < 36; y++)
            {
                raster.A!.Row(y)[..64].Fill((byte)alpha);
            }

            return raster;
        });
        Assert.Equal(use, meter.Report.Alpha);
    }

    [Fact]
    public void BlurIsWiderOnSofterEdges()
    {
        double Measure(int ramp)
        {
            SignalMeter meter = new(new SignalOptions { Blur = true });
            Run(meter, 1, _ => Painted(8, p =>
            {
                p.Fill(0, 0, 128, 72, 0.1, 0.1, 0.1);
                for (int k = 0; k < 4; k++)
                {
                    p.Ramp(10 + (k * 30), 0, 10 + (k * 30) + ramp, 72, 0.1, 0.9);
                    p.Fill(10 + (k * 30) + ramp, 0, 25 + (k * 30) + ramp, 72, 0.9, 0.9, 0.9);
                }
            }, width: 128, height: 72));
            return meter.Report.Blur!.Value;
        }

        double sharp = Measure(1), soft = Measure(4), softer = Measure(8);
        Assert.InRange(sharp, 0.9, 2);
        Assert.True(sharp < soft && soft < softer, $"{sharp} {soft} {softer}");
    }

    [Fact]
    public void BlockinessFindsTheBlockGrid()
    {
        Random random = new(5);
        SignalMeter blocky = new(new SignalOptions { Blockiness = true });
        Run(blocky, 1, _ => Painted(8, p =>
        {
            for (int by = 0; by < 72; by += 8)
            {
                for (int bx = 0; bx < 128; bx += 8)
                {
                    double v = 0.3 + (random.NextDouble() * 0.4);
                    p.Fill(bx, by, bx + 8, by + 8, v, v, v);
                }
            }
        }, width: 128, height: 72));

        SignalMeter smooth = new(new SignalOptions { Blockiness = true });
        Run(smooth, 1, _ => Painted(8, p => p.Ramp(0, 0, 128, 72, 0.2, 0.8), width: 128, height: 72));
        Assert.True(blocky.Report.Blockiness > 10, $"{blocky.Report.Blockiness}");
        Assert.InRange(smooth.Report.Blockiness!.Value, 1, 2);
    }

    [Fact]
    public void OptionsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackDetector(new BlackOptions { PictureRatio = 1.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FreezeDetector(new FreezeOptions { Noise = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropDetector(new CropOptions { Round = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InterlaceDetector(new InterlaceOptions { RepeatRatio = 0.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DuplicateDetector(new DuplicateOptions { Low = 1000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalMeter(new SignalOptions { BlockPeriodMin = 30 }));
    }

    [Fact]
    public void NoAllocationPerFrame()
    {
        List<YuvVideoFrame> frames = [.. Enumerable.Range(0, 40).Select(i => Frame(Fields(Scan.TopFirst, i), i))];
        try
        {
            using FreezeDetector freeze = new();
            IVideoDetector[] detectors = [new BlackDetector(), freeze, new CropDetector(), new InterlaceDetector(), new DuplicateDetector(), new SignalMeter()];
            for (int i = 0; i < 10; i++)
            {
                Array.ForEach(detectors, d => d.Add(frames[i]));
            }

            foreach (IVideoDetector detector in detectors)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 10; i < frames.Count; i++)
                {
                    detector.Add(frames[i]);
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(allocated < 1024, $"{detector.GetType().Name}: {allocated} bytes over 30 frames");
            }
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    public enum Scan
    {
        Progressive,
        TopFirst,
        BottomFirst,
        Telecine,
    }

    private static void Run(IVideoDetector detector, int count, Func<int, YuvRaster> make)
    {
        for (int i = 0; i < count; i++)
        {
            using YuvVideoFrame frame = Frame(make(i), i);
            detector.Add(frame);
        }

        detector.Complete(new MediaTime(count, Rate.Reciprocal()));
    }

    private static YuvVideoFrame Frame(YuvRaster raster, int index)
        => new(raster, new MediaTime(index, Rate.Reciprocal()), new MediaTime(1, Rate.Reciprocal()));

    private static YuvRaster Painted(int depth, Action<YuvPainter> paint, int width = 64, int height = 36)
    {
        YuvRaster raster = YuvPainter.Create(new TestVideoFormat { Width = width, Height = height, BitDepth = depth });
        YuvPainter painter = new(raster);
        painter.Fill(0, 0, width, height, 0, 0, 0);
        paint(painter);
        return raster;
    }

    private static YuvRaster Solid(int depth, double r, double g, double b) => Painted(depth, p => p.Fill(0, 0, p.Width, p.Height, r, g, b));

    /// <summary>A white bar on a gray ramp, its left edge at <paramref name="x"/> (wrapping).</summary>
    private static YuvRaster Bar(int depth, int x) => Painted(depth, p =>
    {
        p.Ramp(0, 0, 64, 36, 0.2, 0.5);
        int at = x % 56;
        p.Fill(at, 0, at + 8, 36, 1, 1, 1);
    });

    /// <summary>A frame of a bar moving 4 samples a field, its two fields taken the way <paramref name="scan"/> says.</summary>
    private static YuvRaster Fields(Scan scan, int frame)
    {
        // Each field shows the bar where it is at the field's moment; telecine shows film frames (one per 2.5 fields) in 3:2.
        (double top, double bottom) = scan switch
        {
            Scan.Progressive => (2 * frame, 2 * frame),
            Scan.TopFirst => (2 * frame, (2 * frame) + 1),
            Scan.BottomFirst => ((2 * frame) + 1, 2 * frame),
            _ => Telecine(frame),
        };
        YuvRaster raster = YuvPainter.Create(new TestVideoFormat { Width = 128, Height = 72 });
        YuvPainter painter = new(raster);
        painter.Ramp(0, 0, 128, 72, 0.2, 0.5);
        for (int y = 0; y < 72; y++)
        {
            int at = (int)((y % 2 == 0 ? top : bottom) * 4) % 100;
            painter.Fill(at, y, at + 20, y + 1, 1, 1, 1);
        }

        return raster;
    }

    /// <summary>The film frames behind a 3:2 frame's top and bottom fields, in field units (2.5 fields a film frame).</summary>
    private static (double Top, double Bottom) Telecine(int frame)
    {
        // Fields A A B B B C C D D D: field n shows film frame n*2/5 rounded down.
        int topField = 2 * frame, bottomField = (2 * frame) + 1;
        static double Film(int field) => (field * 2 / 5) * 2.5;
        return (Film(topField), Film(bottomField));
    }
}
