using Egami.Pixels;
using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// One pass over a file with every detector (V1.4 Build Plan tasks 1 and 4): video and audio frames reach their detectors in time
/// order, a window limits the pass, progress and cancellation work, and the first, middle or last frame of each scene reads back.
/// </summary>
public sealed class MediaAnalysisTests
{
    private static readonly TestVideoFormat Small = new() { Width = 96, Height = 54, FrameRate = new Rational(25, 1) };

    private static readonly Lazy<string> Clip = new(WriteClip);

    [Fact]
    public void FindsTheScenesOfAFile()
    {
        MediaAnalysisResult result = MediaAnalysis.Run(Clip.Value, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(75, result.VideoFrames);
        Assert.Equal([0L, 25, 50], result.Cuts.Select(c => c.Frame));
        Assert.Equal(3, result.Scenes.Count);
        Assert.Equal(1.0, result.Scenes[1].Start.Seconds, 3);
        Assert.Equal(3.0, result.Scenes[2].End.Seconds, 2);
        Assert.Equal(3.0, result.End.Seconds, 2);
        Assert.Empty(result.SceneScores);
    }

    [Fact]
    public void FeedsEveryDetectorInTimeOrder()
    {
        Recorder recorder = new();
        MediaAnalysisOptions options = new() { Scenes = null, VideoDetectors = [recorder.Video], AudioDetectors = [recorder.Audio] };
        MediaAnalysisResult result = MediaAnalysis.Run(Clip.Value, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Cuts);
        Assert.Equal(75, recorder.VideoFrames);
        Assert.Equal(3.0, recorder.AudioSeconds, 2);
        Assert.Equal(2, recorder.Completed);
        Assert.True(recorder.InOrder, "frames out of time order");
    }

    [Fact]
    public void AFreezeDetectorKeepsTheFrameItselfWithTheSameResult()
    {
        // The first freeze detector is handed the frames it keeps; the second copies them, as one fed outside the pass does.
        FreezeOptions anyLength = new() { MinDuration = MediaTime.Zero };
        using FreezeDetector handed = new(anyLength), copied = new(anyLength), alone = new(anyLength);
        MediaAnalysisOptions options = new() { Scenes = null, VideoDetectors = [handed, copied] };
        MediaAnalysis.Run(Clip.Value, options, cancellationToken: TestContext.Current.CancellationToken);
        using (VideoReader reader = VideoReader.Open(Clip.Value))
        {
            MediaTime end = MediaTime.Zero;
            while (reader.ReadFrame() is { } frame)
            {
                using (frame)
                {
                    alone.Add(frame);
                    end = frame.Timestamp + frame.Duration!.Value;
                }
            }

            alone.Complete(end);
        }

        Assert.Equal(2, handed.Ranges.Count);
        Assert.Equal(alone.Ranges, handed.Ranges);
        Assert.Equal(alone.Ranges, copied.Ranges);
    }

    [Fact]
    public void AWindowLimitsThePass()
    {
        MediaAnalysisOptions options = new() { Start = MediaTime.FromSeconds(0.2), Duration = MediaTime.FromSeconds(1.0) };
        MediaAnalysisResult result = MediaAnalysis.Run(Clip.Value, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.InRange(result.VideoFrames, 24, 26);
        Assert.Equal(2, result.Cuts.Count);
        Assert.Equal(1.0, result.Cuts[1].Time.Seconds, 3);
        Assert.Equal(0.2, result.Start.Seconds, 2);
        Assert.Equal(1.2, result.End.Seconds, 2);
    }

    [Fact]
    public void ReportsProgressAndStops()
    {
        List<MediaProgress> reports = [];
        MediaAnalysis.Run(Clip.Value, progress: new Inline(reports.Add), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1].Fraction ?? 0, 2);

        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MediaAnalysis.Run(Clip.Value, cancellationToken: cancel.Token));
        Assert.Throws<ArgumentException>(() => MediaAnalysis.Run(Clip.Value, new MediaAnalysisOptions { Scenes = null }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ScenePosition.First, new[] { 0.0, 1.0, 2.0 })]
    [InlineData(ScenePosition.Middle, new[] { 0.48, 1.48, 2.48 })]
    [InlineData(ScenePosition.Last, new[] { 0.96, 1.96, 2.96 })]
    public void ReadsAFramePerScene(ScenePosition position, double[] times)
    {
        MediaAnalysisResult result = MediaAnalysis.Run(Clip.Value, cancellationToken: TestContext.Current.CancellationToken);
        using VideoReader reader = VideoReader.Open(Clip.Value);
        int i = 0;
        foreach (SceneFrame scene in SceneFrames.Read(reader, result.Scenes, position))
        {
            using (scene.Frame)
            {
                Assert.Equal(result.Scenes[i], scene.Scene);
                Assert.Equal(times[i], scene.Frame.Timestamp.Seconds, 2);
                i++;
            }
        }

        Assert.Equal(3, i);
    }

    private static string WriteClip()
    {
        string path = AviWriterTests.TempFile("analysis-clip.mkv");
        VideoWriterOptions options = new()
        {
            FrameRate = Small.FrameRate,
            Quality = 90,
            AudioCodec = FlacCodec.Instance,
            AudioSampleRate = 48000,
            AudioLayout = ChannelLayout.Stereo,
        };
        using VideoWriter writer = VideoWriter.Create(path, options);
        MediaTime second = MediaTime.FromSeconds(1);
        IEnumerable<YuvVideoFrame> video = TestSources.Solid(Small, second, new Rgba8(220, 30, 30, 255))
            .Concat(TestSources.Solid(Small, second, new Rgba8(30, 40, 220, 255)))
            .Concat(TestSources.Pattern(Small, second));
        int n = 0;
        foreach (YuvVideoFrame frame in video)
        {
            using (frame)
            {
                writer.WriteFrame(frame.Raster, new MediaTime(n++, new Rational(1, 25)));
            }
        }

        foreach (AudioFrame frame in TestSources.Sine(null, MediaTime.FromSeconds(3)))
        {
            using (frame)
            {
                writer.WriteAudio(frame);
            }
        }

        return path;
    }

    private sealed class Inline(Action<MediaProgress> report) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => report(value);
    }

    private sealed class Recorder
    {
        private double _last = double.NegativeInfinity;

        public Recorder()
        {
            Video = new VideoCounter(this);
            Audio = new AudioCounter(this);
        }

        public IVideoDetector Video { get; }

        public IAudioDetector Audio { get; }

        public int VideoFrames { get; private set; }

        public double AudioSeconds { get; private set; }

        public int Completed { get; private set; }

        public bool InOrder { get; private set; } = true;

        private void Saw(MediaTime time)
        {
            // Audio blocks start up to one block before the next video frame; allow that much.
            InOrder &= time.Seconds >= _last - 0.05;
            _last = Math.Max(_last, time.Seconds);
        }

        private sealed class VideoCounter(Recorder owner) : IVideoDetector
        {
            public void Add(YuvVideoFrame frame)
            {
                owner.Saw(frame.Timestamp);
                owner.VideoFrames++;
            }

            public void Complete(MediaTime endTime) => owner.Completed++;
        }

        private sealed class AudioCounter(Recorder owner) : IAudioDetector
        {
            public void Add(AudioFrame frame)
            {
                owner.Saw(frame.Timestamp);
                owner.AudioSeconds += frame.Duration.Seconds;
            }

            public void Complete(MediaTime endTime) => owner.Completed++;
        }
    }
}
