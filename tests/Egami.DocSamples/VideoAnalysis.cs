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

    [Fact]
    public void VideoDetectors()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-video-detectors
        // ffmpeg's detection filters as detectors, with their documented defaults: black and frozen stretches, black borders,
        // field order and repeated frames, all from one decode.
        BlackDetector black = new(new BlackOptions { MinDuration = MediaTime.FromSeconds(0.5) });
        using FreezeDetector freeze = new(new FreezeOptions { MinDuration = MediaTime.FromSeconds(0.5) });
        CropDetector crop = new();
        InterlaceDetector fields = new();
        DuplicateDetector duplicates = new();
        MediaAnalysis.Run("shots.mkv", new MediaAnalysisOptions { VideoDetectors = [black, freeze, crop, fields, duplicates] });

        foreach (DetectedRange range in freeze.Ranges)
        {
            Console.WriteLine($"Frozen from {range.Start.ToTimeSpan()} to {range.End.ToTimeSpan()}");
        }

        Console.WriteLine($"{black.Ranges.Count} black stretches, crop to {crop.Report().Crop}, {fields.Report().Verdict}");
        Console.WriteLine($"{duplicates.DuplicateCount} repeated frames");
        #endregion

        Assert.Empty(black.Ranges);
        Assert.Equal(2, freeze.Ranges.Count);
        Assert.False(crop.Report().HasBorders);
    }

    [Fact]
    public void Signal()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-signal
        // Levels, saturation and broadcast range as signalstats gives them, the range really used, and optional blur and
        // blockiness scores.
        SignalMeter meter = new(new SignalOptions { Blur = true, Blockiness = true });
        MediaAnalysis.Run("shots.mkv", new MediaAnalysisOptions { Scenes = null, VideoDetectors = [meter] });
        SignalReport report = meter.Report();
        Console.WriteLine($"Y {report.Y.Min}..{report.Y.Max} (mean {report.Y.Mean:0.0}), {report.OutOfRange:P1} out of range, {report.Range} range");
        Console.WriteLine($"Edges {report.Blur:0.00} px wide, blockiness {report.Blockiness:0.00}");
        #endregion

        // Motion JPEG decodes to full-range planes, and the bars' PLUGE reaches below video black.
        Assert.Equal(SignalRange.Full, report.Range);
        Assert.Equal(75, report.Frames);
    }

    [Fact]
    public void AudioDetectors()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-audio-detectors
        // Silent stretches as silencedetect finds them (under -60 dBFS for 2 s by default), and the sample levels volumedetect
        // and astats report, per channel and over all channels.
        SilenceDetector silence = new(new SilenceOptions { Noise = -50, MinDuration = MediaTime.FromSeconds(0.5) });
        AudioLevelMeter levels = new();
        MediaAnalysis.Run("shots.mkv", new MediaAnalysisOptions { Scenes = null, AudioDetectors = [silence, levels] });

        Console.WriteLine($"{silence.Ranges.Count} silent stretches");
        AudioLevelReport report = levels.Report();
        Console.WriteLine($"Peak {report.Overall.Peak:0.0} dBFS, RMS {report.Overall.Rms:0.0} dBFS, {report.Overall.Clipped} clipped");
        #endregion

        Assert.Empty(silence.Ranges);
        Assert.Equal(-18, report.Overall.Peak, 0.1);
        Assert.Equal(-21, report.Overall.Rms, 0.1);
    }

    [Fact]
    public void Loudness()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-loudness
        // EBU R 128 loudness: integrated, range and true peak at the end, momentary and short-term while the audio plays.
        LoudnessMeter meter = new(new LoudnessOptions { KeepSeries = true });
        MediaAnalysis.Run("shots.mkv", new MediaAnalysisOptions { Scenes = null, AudioDetectors = [meter] });
        LoudnessReport loudness = meter.Report();
        Console.WriteLine($"I {loudness.Integrated:0.0} LUFS, LRA {loudness.Range:0.0} LU, true peak {loudness.TruePeak:0.0} dBTP");
        foreach (LoudnessValue value in meter.ShortTermSeries)
        {
            Console.WriteLine($"{value.Time.ToTimeSpan()}: {value.Loudness:0.0} LUFS");
        }
        #endregion

        Assert.Equal(-18, loudness.Integrated, 0.1);
        Assert.Equal(-18, loudness.TruePeak, 0.2);
        Assert.NotEmpty(meter.ShortTermSeries);
    }

    [Fact]
    public void Pictures()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");

        #region analysis-pictures
        // A waveform with each channel in its own strip, and a spectrogram on a logarithmic frequency axis in magma colors.
        using Raster<Rgba8> waveform = AudioPictures.Waveform("shots.mkv", new WaveformOptions
        {
            Width = 1200,
            Height = 240,
            Channels = AudioPictureChannels.Stacked,
        });
        waveform.Save("waveform.png");

        using Raster<Rgba8> spectrogram = AudioPictures.Spectrogram("shots.mkv", new SpectrogramOptions
        {
            Width = 1200,
            Height = 400,
            Scale = FrequencyScale.Logarithmic,
            Floor = -100,
        });
        spectrogram.Save("spectrogram.png");
        #endregion

        Assert.Equal(1200, waveform.Width);
        Assert.Equal(400, spectrogram.Height);

        // The -18 dBFS tone reaches an eighth of each strip's half height from its middle.
        int top = Enumerable.Range(0, 120).First(y => waveform.Row(y)[600].A != 0);
        Assert.InRange(top, 51, 53);
    }

    [Fact]
    public void FromStreams()
    {
        using SampleFiles files = new();
        WriteClip("shots.mkv");
        using Stream download = new NoSeek(File.OpenRead("shots.mkv"));

        #region analysis-stream
        // From a stream, such as an upload or a download, with the same options as from a file. A stream that cannot seek
        // is read once; the caller keeps the stream and disposes it.
        using Raster<Rgba8> picture = AudioPictures.Waveform(download, new WaveformOptions { Width = 800, Height = 120 });
        picture.Save("upload-waveform.png");

        using FileStream file = File.OpenRead("shots.mkv");
        MediaAnalysisResult result = MediaAnalysis.Run(file, new MediaAnalysisOptions { AudioDetectors = [new SilenceDetector()] });
        Console.WriteLine($"{result.Scenes.Count} scenes in {result.VideoFrames} frames");
        #endregion

        Assert.Equal(800, picture.Width);
        Assert.True(result.VideoFrames > 0);
    }

    /// <summary>A file read as a pipe or a download is: forwards only.</summary>
    private sealed class NoSeek(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            base.Dispose(disposing);
        }
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
