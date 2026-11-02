using System.Buffers.Binary;

using Egami.Pixels;
using Egami.Video.Analysis;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// Video 1.2.0 Build Plan task 4: the Stream overloads of AudioPictures.Waveform, AudioPictures.Spectrogram and
/// MediaAnalysis.Run give what the path versions give, from a seekable stream at any position and from one that cannot seek,
/// and leave the stream to the caller.
/// </summary>
public sealed class StreamOverloadTests
{
    public static TheoryData<string> AudioFiles => ["mkv-mjpeg-pcm.mkv", "webm-vp8-opus.webm", "audio/opus/ogg-vorbis.ogg", "audio/opus/opus-51.opus", "audio/flac-l5-stereo.flac", "audio/wav-s16-stereo.wav"];

    [Theory]
    [MemberData(nameof(AudioFiles))]
    public void PicturesFromStreamsMatchTheFile(string name)
    {
        string path = Path.Combine(Corpus.Directory, name);
        WaveformOptions waveform = new() { Width = 200, Height = 60 };
        SpectrogramOptions spectrogram = new() { Width = 120, Height = 80 };
        using Raster<Rgba8> waveFromFile = AudioPictures.Waveform(path, waveform, cancellationToken: TestContext.Current.CancellationToken);
        using Raster<Rgba8> spectrumFromFile = AudioPictures.Spectrogram(path, spectrogram, cancellationToken: TestContext.Current.CancellationToken);
        foreach (bool seekable in new[] { true, false })
        {
            using (Stream stream = Open(path, seekable, out Tracked tracked))
            {
                using Raster<Rgba8> wave = AudioPictures.Waveform(stream, waveform, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(Same(waveFromFile, wave), $"{name} waveform, seekable {seekable}");
                Assert.False(tracked.Disposed);
            }

            using (Stream stream = Open(path, seekable, out _))
            {
                using Raster<Rgba8> spectrum = AudioPictures.Spectrogram(stream, spectrogram, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(Same(spectrumFromFile, spectrum), $"{name} spectrogram, seekable {seekable}");
            }
        }
    }

    [Theory]
    [InlineData("mkv-mjpeg-pcm.mkv")]
    [InlineData("webm-vp8-opus.webm")]
    [InlineData("avi-mjpeg-420-pcm.avi")]
    public void AnalysisOfStreamsMatchesTheFile(string name)
    {
        string path = Path.Combine(Corpus.Directory, name);
        foreach (MediaTime? start in new MediaTime?[] { null, MediaTime.FromSeconds(0.4) })
        {
            MediaAnalysisResult file = Analyze(o => MediaAnalysis.Run(path, o, cancellationToken: TestContext.Current.CancellationToken), start, out (long, double) fileLevels);
            foreach (bool seekable in new[] { true, false })
            {
                using Stream stream = Open(path, seekable, out Tracked tracked);
                MediaAnalysisResult result = Analyze(o => MediaAnalysis.Run(stream, o, cancellationToken: TestContext.Current.CancellationToken), start, out (long, double) levels);
                string what = $"{name} from {start}, seekable {seekable}";
                Assert.True(file.VideoFrames == result.VideoFrames || (!seekable && start is not null), what);
                Assert.Equal(file.Cuts.Count, result.Cuts.Count);
                if (seekable || start is null)
                {
                    Assert.Equal(file.End, result.End);
                    Assert.Equal(fileLevels, levels);
                }

                Assert.False(tracked.Disposed);
            }
        }
    }

    [Fact]
    public void AnUnseekableOggHasNoDurationSoThePicturesTakeTheCopy()
    {
        // The set above covers the temporary copy only if some unseekable stream says no duration; Ogg needs its last page.
        using Tracked stream = Open(Path.Combine(Corpus.Directory, "audio", "opus", "ogg-vorbis.ogg"), seekable: false, out _);
        using AudioReader reader = AudioReader.Open(stream, leaveOpen: true);
        Assert.Null(reader.Track.Duration ?? reader.Info.Duration);
    }

    [Fact]
    public void SeekableStreamsAreReadFromTheirPosition()
    {
        string path = Path.Combine(Corpus.Directory, "audio", "wav-s16-stereo.wav");
        byte[] file = File.ReadAllBytes(path);
        using MemoryStream padded = new([.. new byte[1000], .. file]);
        padded.Position = 1000;
        WaveformOptions options = new() { Width = 100, Height = 40 };
        using Raster<Rgba8> expected = AudioPictures.Waveform(path, options, cancellationToken: TestContext.Current.CancellationToken);
        using Raster<Rgba8> actual = AudioPictures.Waveform(padded, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Same(expected, actual));
        padded.Position = 1000;
        MediaAnalysisResult result = MediaAnalysis.Run(padded, new MediaAnalysisOptions { Scenes = null, AudioDetectors = [new AudioLevelMeter()] }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.End > MediaTime.Zero);
    }

    private static MediaAnalysisResult Analyze(Func<MediaAnalysisOptions, MediaAnalysisResult> run, MediaTime? start, out (long Count, double Level) levels)
    {
        AudioLevelMeter meter = new();
        MediaAnalysisResult result = run(new MediaAnalysisOptions { AudioDetectors = [meter], Start = start });
        AudioLevelReport report = meter.Report();
        levels = (report.Samples, Math.Round(report.Overall.Rms, 6));
        return result;
    }

    internal static Stream Unseekable(byte[] data) => new Tracked(data, seekable: false);

    private static Tracked Open(string path, bool seekable, out Tracked tracked)
    {
        tracked = new Tracked(File.ReadAllBytes(path), seekable);
        return tracked;
    }

    private static bool Same(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A memory stream that can refuse to seek and tells whether it was disposed.</summary>
    private sealed class Tracked(byte[] data, bool seekable) : MemoryStream(data, writable: false)
    {
        public bool Disposed { get; private set; }

        public override bool CanSeek => seekable;

        public override long Length => seekable ? base.Length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? base.Position : throw new NotSupportedException();
            set
            {
                if (!seekable)
                {
                    throw new NotSupportedException();
                }

                base.Position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin loc) => seekable ? base.Seek(offset, loc) : throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The review fix on #238: an unseekable stream whose container gives the duration draws in one pass without keeping a copy of
/// what it read. Runs alone, in <see cref="MemoryTests"/>' collection, since it measures allocations and the memory tests
/// measure the process.
/// </summary>
[Collection(nameof(MemoryTests))]
public sealed class StreamOverloadMemoryTests
{
    [Fact]
    public void AnUnseekableStreamWithADurationIsNotHeldInMemory()
    {
        // 30 s of 48 kHz stereo WAV, 5.8 MB: the header gives the duration, so the picture draws in one pass and must not keep
        // the bytes it has read.
        const int Rate = 48000, Seconds = 30;
        byte[] wav = new byte[44 + (Rate * Seconds * 4)];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), Rate * Seconds * 4);
        for (int i = 0; i < Rate * Seconds; i++)
        {
            short value = (short)(8000 * Math.Sin(i * 0.05));
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * 4)), value);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(46 + (i * 4)), value);
        }

        WaveformOptions options = new() { Width = 100, Height = 40 };
        using (Stream warm = StreamOverloadTests.Unseekable(wav))
        using (Raster<Rgba8> first = AudioPictures.Waveform(warm, options, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        using Stream stream = StreamOverloadTests.Unseekable(wav);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using Raster<Rgba8> picture = AudioPictures.Waveform(stream, options, cancellationToken: TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < wav.Length / 4, $"{allocated:N0} bytes allocated for a {wav.Length:N0}-byte stream");
    }
}
