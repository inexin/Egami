using Egami.Pixels;
using Egami.Video;
using Egami.Video.Analysis;
using Egami.Video.Audio;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoAnalysis
{
    /// <summary>Writes a three-shot clip with a tone, the way the samples below find it on disk.</summary>
    private static void WriteClip(string path)
    {
        #region analysis-test-sources
        // Generated pictures and sounds for tests and demos: bars, a moving pattern, solid colors, gradients, tones and noise.
        TestVideoFormat format = new() { Width = 320, Height = 180, FrameRate = new Rational(25, 1) };
        MediaTime second = MediaTime.FromSeconds(1);
        using VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions
        {
            FrameRate = format.FrameRate,
            AudioCodec = Egami.Video.Audio.Codecs.FlacCodec.Instance,
            AudioSampleRate = 48000,
            AudioLayout = ChannelLayout.Stereo,
        });
        IEnumerable<YuvVideoFrame> shots = TestSources.ColorBars(format, second)
            .Concat(TestSources.Solid(format, second, new Rgba8(30, 40, 220, 255)))
            .Concat(TestSources.Pattern(format, second));
        int index = 0;
        foreach (YuvVideoFrame frame in shots)
        {
            using (frame)
            {
                writer.WriteFrame(frame.Raster, new MediaTime(index++, format.FrameRate.Reciprocal()));
            }
        }

        foreach (AudioFrame block in TestSources.Sine(null, MediaTime.FromSeconds(3), frequency: 1000, level: -18))
        {
            using (block)
            {
                writer.WriteAudio(block);
            }
        }
        #endregion
    }

    [Fact]
    public void Run()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-run
        // One pass over the file: the scene detector runs by default, and any detectors of your own run with it.
        MediaAnalysisResult result = MediaAnalysis.Run("shots.mkv");
        foreach (Scene scene in result.Scenes)
        {
            Console.WriteLine($"Scene {scene.Number}: {scene.Start.ToTimeSpan()} to {scene.End.ToTimeSpan()}, starts with a {scene.StartKind}");
        }
        #endregion

        Assert.Equal(3, result.Scenes.Count);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-scene-options
        // Content compares each frame with the one before, as ffmpeg's scene score does; Adaptive (the default) also weighs the
        // frames around it, so fast motion is not taken for a cut. Fades through black end a scene in the dark.
        SceneOptions options = new()
        {
            Method = SceneMethod.Content,
            Threshold = 0.3,
            MinSceneLength = MediaTime.FromSeconds(0.5),
            DetectFades = true,
            KeepSeries = true,
        };
        MediaAnalysisResult result = MediaAnalysis.Run("shots.mkv", new MediaAnalysisOptions { Scenes = options });
        SceneScore sharpest = result.SceneScores.MaxBy(s => s.Score);
        Console.WriteLine($"{result.Cuts.Count} cuts; the sharpest at {sharpest.Time.ToTimeSpan()}");
        #endregion

        Assert.Equal(3, result.Cuts.Count);
    }

    [Fact]
    public void Detectors()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-detectors
        // A detector takes frames in time order; this one finds the darkest frame. The scene detector can run on its own too.
        DarkestFrame darkest = new();
        SceneDetector scenes = new(new SceneOptions { Method = SceneMethod.Histogram });
        using (VideoReader reader = VideoReader.Open("shots.mkv"))
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    darkest.Add(frame);
                    if (scenes.Add(frame) is { } cut)
                    {
                        Console.WriteLine($"Cut at frame {cut.Frame}");
                    }
                }
            }

            scenes.Complete(reader.Info.Duration ?? MediaTime.Zero);
        }

        Console.WriteLine($"Darkest frame at {darkest.Time.ToTimeSpan()}, {scenes.Scenes.Count} scenes");
        #endregion

        Assert.Equal(3, scenes.Scenes.Count);
    }

    [Fact]
    public void AudioDetector()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-audio-detector
        // Audio detectors get the audio in blocks, interleaved with the video by time, in the same pass.
        Peak peak = new();
        MediaAnalysis.Run("shots.mkv", new MediaAnalysisOptions { AudioDetectors = [peak] });
        Console.WriteLine($"Peak {20 * Math.Log10(peak.Value):0.0} dBFS");
        #endregion

        Assert.Equal(-18, 20 * Math.Log10(peak.Value), 0.5);
    }

    [Fact]
    public void Frames()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-scene-frames
        // A frame from each scene, read by seeking: the first, the middle or the last.
        MediaAnalysisResult result = MediaAnalysis.Run("shots.mkv");
        using VideoReader reader = VideoReader.Open("shots.mkv");
        foreach (SceneFrame scene in SceneFrames.Read(reader, result.Scenes, ScenePosition.Middle))
        {
            using (scene.Frame)
            using (Raster<Rgba8> picture = scene.Frame.ToRaster<Rgba8>())
            {
                picture.Save($"scene-{scene.Scene.Number}.png");
            }
        }
        #endregion

        Assert.True(File.Exists("scene-3.png"));
    }

    private sealed class DarkestFrame : IVideoDetector
    {
        private double _lowest = double.MaxValue;

        public MediaTime Time { get; private set; }

        public void Add(YuvVideoFrame frame)
        {
            // The mean of the luma plane's middle row is enough for a sample.
            YuvRaster<byte> planes = (YuvRaster<byte>)frame.Raster;
            ReadOnlySpan<byte> row = planes.Y.Row(planes.Height / 2);
            double sum = 0;
            foreach (byte value in row)
            {
                sum += value;
            }

            if (sum / row.Length < _lowest)
            {
                _lowest = sum / row.Length;
                Time = frame.Timestamp;
            }
        }

        public void Complete(MediaTime endTime)
        {
        }
    }

    private sealed class Peak : IAudioDetector
    {
        public double Value { get; private set; }

        public void Add(AudioFrame frame)
        {
            for (int c = 0; c < frame.ChannelCount; c++)
            {
                foreach (float sample in frame.GetSpan<float>(c))
                {
                    Value = Math.Max(Value, Math.Abs(sample));
                }
            }
        }

        public void Complete(MediaTime endTime)
        {
        }
    }
}
