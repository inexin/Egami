using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Tests.Codecs;

namespace Egami.Video.Tests;

/// <summary>
/// <see cref="MediaFile.Trim(string, string, MediaTime, MediaTime?, TrimMode, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>:
/// frame-exact smart cuts across codecs and containers (the cut's frames against the source's frames in range, copied packets
/// byte for byte), keyframe and re-encode modes, the fallback warning, audio cut at the sample, progress and cancellation.
/// </summary>
public class TrimTests
{
    private static readonly Rational Rate = new(30, 1);

    /// <summary>The source clips: 40 frames at 30 fps, a keyframe every 10 frames (0, 10, 20, 30).</summary>
    private const int FrameCount = 40;

    public static TheoryData<string, string> SmartCases => new()
    {
        { "vp8", "webm" }, { "vp8", "mkv" }, { "vp8", "ivf" }, { "vp8", "mp4" },
        { "vp9", "webm" }, { "vp9", "mp4" }, { "vp9", "ivf" },
        { "av1", "mkv" }, { "av1", "mp4" }, { "av1", "ivf" }, { "av1", "webm" },
        { "mjpeg", "avi" }, { "mjpeg", "mkv" },
    };

    [Theory]
    [MemberData(nameof(SmartCases))]
    public void SmartCutIsFrameExactAndCopiesFromTheNextKeyframe(string codec, string container)
    {
        byte[] source = MakeClip(Codec(codec), container);
        TrimResult result = Trim(source, container, Frame(13), Frame(27), TrimMode.Smart, out byte[] cut);

        // The times asked for are exact; Matroska keeps milliseconds, and the frames within half of one are at them.
        List<DecodedFrame> all = Decode(source);
        Assert.Equal(FrameCount, all.Count);
        Assert.Equal(TrimMode.Smart, result.Mode);
        Assert.Equal(all[13].Time, result.Start);
        Assert.Equal(14, result.Frames);
        bool allKeys = codec == "mjpeg";
        Assert.Equal(allKeys ? 0 : 7, result.ReencodedFrames);
        Assert.Empty(result.Warnings);

        List<DecodedFrame> want = all[13..27];
        List<DecodedFrame> got = Decode(cut);
        Assert.Equal(want.Count, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            Assert.Equal(want[i].Time - all[13].Time, got[i].Time);
            if (allKeys || i >= 7)
            {
                Assert.True(want[i].Luma.AsSpan().SequenceEqual(got[i].Luma), $"frame {13 + i} differs from the source");
            }
            else
            {
                double psnr = Psnr(want[i].Luma, got[i].Luma);
                Assert.True(psnr > 30, $"frame {13 + i}: {psnr:F1} dB");
            }
        }

        // Every packet from the keyframe at 20 on is the source's, byte for byte.
        Dictionary<MediaTime, byte[]> sourcePackets = VideoPackets(source).ToDictionary(p => p.Time, p => p.Data);
        List<(MediaTime Time, byte[] Data, bool Key)> cutPackets = VideoPackets(cut);
        Assert.True(cutPackets[0].Key);
        int copied = 0;
        foreach ((MediaTime time, byte[] data, _) in cutPackets.Where(p => p.Time + all[13].Time >= all[allKeys ? 13 : 20].Time))
        {
            Assert.Equal(sourcePackets[time + all[13].Time], data);
            copied++;
        }

        Assert.Equal(allKeys ? 14 : 7, copied);
    }

    [Theory]
    [InlineData("vp9", "webm")]
    [InlineData("av1", "mkv")]
    public void KeyframeModeStartsAtTheKeyframeBeforeAndCopiesEveryPacket(string codec, string container)
    {
        byte[] source = MakeClip(Codec(codec), container);
        TrimResult result = Trim(source, container, Frame(13), Frame(27), TrimMode.Keyframe, out byte[] cut);
        List<DecodedFrame> all = Decode(source);
        Assert.Equal(TrimMode.Keyframe, result.Mode);
        Assert.Equal(all[10].Time, result.Start);
        Assert.Equal(17, result.Frames);
        Assert.Equal(0, result.ReencodedFrames);
        Assert.Contains(result.Warnings, w => w.Code == "trim-keyframe");

        Dictionary<MediaTime, byte[]> sourcePackets = VideoPackets(source).ToDictionary(p => p.Time, p => p.Data);
        List<(MediaTime Time, byte[] Data, bool Key)> cutPackets = VideoPackets(cut);
        Assert.Equal(17, cutPackets.Count);
        foreach ((MediaTime time, byte[] data, _) in cutPackets)
        {
            Assert.Equal(sourcePackets[time + all[10].Time], data);
        }
    }

    [Theory]
    [InlineData("vp8", "webm")]
    [InlineData("vp9", "ivf")]
    [InlineData("av1", "mp4")]
    public void ReencodeModeEncodesEveryFrameOfTheCut(string codec, string container)
    {
        byte[] source = MakeClip(Codec(codec), container);
        TrimResult result = Trim(source, container, Frame(13), Frame(27), TrimMode.Reencode, out byte[] cut);
        List<DecodedFrame> all = Decode(source);
        Assert.Equal(TrimMode.Reencode, result.Mode);
        Assert.Equal(all[13].Time, result.Start);
        Assert.Equal(14, result.ReencodedFrames);
        List<DecodedFrame> want = all[13..27];
        List<DecodedFrame> got = Decode(cut);
        Assert.Equal(want.Count, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            Assert.Equal(want[i].Time - all[13].Time, got[i].Time);
            Assert.True(Psnr(want[i].Luma, got[i].Luma) > 30, $"frame {13 + i}");
        }
    }

    [Theory]
    [InlineData("vp8")]
    [InlineData("vp9")]
    [InlineData("av1")]
    public void StartOnAKeyframeIsAPlainCopy(string codec)
    {
        byte[] source = MakeClip(Codec(codec), "mkv");
        TrimResult result = Trim(source, "mkv", Frame(10), null, TrimMode.Smart, out byte[] cut);
        Assert.Equal(0, result.ReencodedFrames);
        Assert.Equal(30, result.Frames);
        Dictionary<MediaTime, byte[]> sourcePackets = VideoPackets(source).ToDictionary(p => p.Time, p => p.Data);
        MediaTime start = Decode(source)[10].Time;
        Assert.All(VideoPackets(cut), p => Assert.Equal(sourcePackets[p.Time + start], p.Data));
    }

    [Fact]
    public void CutInsideOneGroupIsEncodedWhole()
    {
        byte[] source = MakeClip(Codec("vp9"), "webm");
        TrimResult result = Trim(source, "webm", Frame(12), Frame(17), TrimMode.Smart, out byte[] cut);
        Assert.Equal(5, result.Frames);
        Assert.Equal(5, result.ReencodedFrames);
        Assert.Equal(5, Decode(cut).Count);
    }

    [Theory]
    [InlineData("vp8")]
    [InlineData("vp9")]
    [InlineData("av1")]
    public void CutOutDropsHiddenFramesThatNothingShows(string codec)
    {
        // Quality latency codes hidden alternate references: VP8 as packets of their own, VP9 in superframes, AV1 shown later.
        byte[] source = MakeClip(Codec(codec), "webm", hidden: true);
        List<DecodedFrame> all = Decode(source);
        Assert.Equal(FrameCount, all.Count);
        TrimResult result = Trim(source, "webm", MediaTime.Zero, Frame(25), TrimMode.Smart, out byte[] cut);
        Assert.Equal(25, result.Frames);
        List<DecodedFrame> got = Decode(cut);
        Assert.Equal(25, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            Assert.True(all[i].Luma.AsSpan().SequenceEqual(got[i].Luma), $"frame {i}");
        }

        // The last packet shows a frame: a hidden frame after it would serve only frames that were cut.
        (_, byte[] last, _) = VideoPackets(cut)[^1];
        if (codec == "vp8")
        {
            Assert.Equal(1, (last[0] >> 4) & 1);
        }
    }

    [Fact]
    public void AStreamTheEncoderCannotMatchIsEncodedAgainWholeWithAWarning()
    {
        // libvpx VP8 version 1 (bilinear filters, simple loop filter): Egami's encoder writes version 0 only.
        string path = Path.Combine(Corpus.Directory, "vp8", "v1-pan.webm");
        byte[] source = File.ReadAllBytes(path);
        List<DecodedFrame> all = Decode(source);
        List<MediaTime> keys = [.. VideoPackets(source).Where(p => p.Key).Select(p => p.Time)];
        MediaTime start = all.First(f => f.Time > keys[0]).Time;
        MediaTime end = all[Math.Min(all.Count - 1, all.FindIndex(f => f.Time == start) + 6)].Time;
        List<MediaWarning> reported = [];
        TrimResult result = Trim(source, "webm", start, end, TrimMode.Smart, out byte[] cut, new MediaContext { Warnings = reported.Add });
        Assert.Equal(TrimMode.Reencode, result.Mode);
        MediaWarning warning = Assert.Single(result.Warnings);
        Assert.Equal("trim-reencode", warning.Code);
        Assert.Contains("version", warning.Message);
        Assert.Equal(result.Warnings, reported);
        List<DecodedFrame> want = [.. all.Where(f => f.Time >= start && f.Time < end)];
        List<DecodedFrame> got = Decode(cut);
        Assert.Equal(want.Count, got.Count);
        Assert.Equal(want.Count, result.ReencodedFrames);
    }

    [Fact]
    public void ACodecSmartCutCannotEncodeStartsAtTheKeyframeWithAWarning()
    {
        // A context without VP8, as for H.264 and HEVC, which Egami.Video decodes but cannot encode.
        byte[] source = MakeClip(Codec("vp8"), "mkv");
        MediaContext context = new() { Codecs = [.. MediaContext.Default.Codecs.Where(c => c.CodecId != CodecIds.Vp8)] };
        List<DecodedFrame> all = Decode(source);
        TrimResult result = Trim(source, "mkv", Frame(13), null, TrimMode.Smart, out byte[] cut, context);
        Assert.Equal(TrimMode.Keyframe, result.Mode);
        Assert.Equal(all[10].Time, result.Start);
        Assert.Equal(0, result.ReencodedFrames);
        Assert.Contains(result.Warnings, w => w.Code == "trim-keyframe" && w.Message.Contains("VP8", StringComparison.Ordinal));
        Assert.Equal(30, VideoPackets(cut).Count);
        Assert.Throws<NotSupportedException>(() => Trim(source, "mkv", Frame(13), null, TrimMode.Reencode, out _, context));
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("mp4")]
    [InlineData("avi")]
    public void PcmIsCutAtTheSample(string container)
    {
        byte[] source = MakeClip(VideoCodecs.MotionJpeg, container, audio: PcmCodec.Instance);
        short[] all = Pcm(source);
        TrimResult result = Trim(source, container, Frame(13), Frame(27), TrimMode.Smart, out byte[] cut);
        short[] got = Pcm(cut);

        // The cut starts at the first frame's time as the container keeps it (Matroska's 433 ms), and ends at the end asked for.
        int from = (int)result.Start.Rescale(new Rational(1, 48000)).Ticks, to = (int)Frame(27).Rescale(new Rational(1, 48000)).Ticks;
        Assert.Equal(all[from..to], got);
    }

    [Theory]
    [InlineData("webm")]
    [InlineData("mp4")]
    public void OpusIsCutAtTheSampleByTheContainer(string container)
    {
        byte[] source = MakeClip(Codec("vp9"), container, audio: OpusCodec.Instance);
        TrimResult result = Trim(source, container, Frame(13), Frame(27), TrimMode.Smart, out byte[] cut);
        Assert.DoesNotContain(result.Warnings, w => w.Code == "trim-audio-packet");
        using AudioReader reader = AudioReader.Open(new MemoryStream(cut));
        long samples = 0;
        MediaTime? first = null;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                first ??= frame.Timestamp;
                samples += frame.SampleCount;
            }
        }

        Assert.Equal(MediaTime.Zero, first);
        Assert.Equal(Frame(27).Rescale(new Rational(1, 48000)).Ticks - result.Start.Rescale(new Rational(1, 48000)).Ticks, samples);
    }

    [Fact]
    public void Ac3InMatroskaIsCutAtTheNearestPacketWithAWarning()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Corpus.Directory, "mkv-ac3.mkv"));
        List<DecodedFrame> all = Decode(source);
        int audio = MediaFile.Probe(new MemoryStream(source)).Tracks.First(t => t.Kind == TrackKind.Audio).Number;
        TrimResult result = Trim(source, "mkv", all[3].Time, all[^3].Time, TrimMode.Smart, out byte[] cut);
        Assert.Equal(all.Count - 6, result.Frames);
        Assert.Contains(result.Warnings, w => w.Code == "trim-audio-packet" && w.TrackNumber == audio);
        using AudioReader reader = AudioReader.Open(new MemoryStream(cut));
        long samples = reader.ReadFrames().Sum(f =>
        {
            using (f)
            {
                return (long)f.SampleCount;
            }
        });
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(new MemoryStream(cut)).Tracks.First(t => t.Kind == TrackKind.Audio);
        double seconds = (all[^3].Time - all[3].Time).Seconds;

        // Within one AC-3 frame (1536 samples) of the video's length.
        Assert.InRange(samples, (seconds * track.SampleRate) - 1536, (seconds * track.SampleRate) + 1536);
    }

    [Fact]
    public void LacedVorbisInMatroskaKeepsItsSamples()
    {
        // mkvmerge laces Vorbis packets under one block time: each gets its own time, so none of the cut is lost.
        byte[] source = File.ReadAllBytes(Path.Combine(Corpus.Directory, "mkv-vorbis-xiph-laced.mkv"));
        List<DecodedFrame> all = Decode(source);
        TrimResult result = Trim(source, "mkv", all[5].Time, all[^5].Time, TrimMode.Smart, out byte[] cut);
        using AudioReader reader = AudioReader.Open(new MemoryStream(cut));
        long samples = reader.ReadFrames().Sum(f =>
        {
            using (f)
            {
                return (long)f.SampleCount;
            }
        });
        double seconds = (all[^5].Time - all[5].Time).Seconds;

        // Within two Vorbis long blocks (2 x 256 samples at 8 kHz) of the video's length.
        Assert.InRange(samples, (seconds * 8000) - 512, (seconds * 8000) + 512);
    }

    [Fact]
    public void ProgressEndsWithAFinalReportAndTheStreamOverloadReadsAPipe()
    {
        byte[] source = MakeClip(Codec("vp8"), "webm");
        List<MediaProgress> reports = [];
        using MemoryStream output = new();
        TrimResult result = MediaFile.Trim(new PipeStream(source), output, "webm", Frame(13), Frame(27), progress: new Sync<MediaProgress>(reports.Add), leaveOpen: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(14, result.Frames);
        Assert.True(reports[^1].IsFinal);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.Equal(result.Packets, reports[^1].Packets);
        Assert.Equal(14, Decode(output.ToArray()).Count);
    }

    [Fact]
    public void CancellationStopsTheJobAndDeletesTheFile()
    {
        byte[] source = MakeClip(Codec("vp9"), "webm");
        string folder = Directory.CreateTempSubdirectory("egami-trim-").FullName;
        try
        {
            string input = Path.Combine(folder, "in.webm"), output = Path.Combine(folder, "out.webm");
            File.WriteAllBytes(input, source);
            using CancellationTokenSource cancel = new();
            Assert.ThrowsAny<OperationCanceledException>(() => MediaFile.Trim(input, output, Frame(13), null, progress: new Sync<MediaProgress>(p =>
            {
                // The first report comes once the output is open; the job stops at its next packet.
                cancel.Cancel();
            }), cancellationToken: cancel.Token));
            Assert.False(File.Exists(output));

            using CancellationTokenSource before = new();
            before.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => MediaFile.Trim(input, output, Frame(13), null, cancellationToken: before.Token));
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BadRangesThrow()
    {
        byte[] source = MakeClip(Codec("vp8"), "ivf");
        Assert.Throws<ArgumentOutOfRangeException>(() => Trim(source, "ivf", Frame(-1), null, TrimMode.Smart, out _));
        Assert.Throws<ArgumentException>(() => Trim(source, "ivf", Frame(5), Frame(5), TrimMode.Smart, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Trim(source, "ivf", Frame(FrameCount + 5), null, TrimMode.Smart, out _));
    }

    [Fact]
    public void AV1SequenceHeaderFromLibaomIsReproduced()
    {
        // A libaom stream: the re-encoded group must carry its sequence header byte for byte, as av1C holds it.
        string path = Path.Combine(Corpus.Directory, "av1", "tools-8bit.mkv");
        byte[] source = File.ReadAllBytes(path);
        List<(MediaTime Time, byte[] Data, bool Key)> packets = VideoPackets(source);
        List<DecodedFrame> all = Decode(source);
        MediaTime start = all[Math.Min(3, all.Count - 1)].Time;
        Assert.SkipWhen(packets.First(p => p.Time >= start).Key, "The frame is a keyframe.");
        TrimResult result = Trim(source, "mkv", start, null, TrimMode.Smart, out byte[] cut);
        Assert.Equal(TrimMode.Smart, result.Mode);
        Assert.True(result.ReencodedFrames > 0);
        Assert.Equal(MediaFile.Probe(new MemoryStream(source)).Tracks[0].CodecPrivate.ToArray(), MediaFile.Probe(new MemoryStream(cut)).Tracks[0].CodecPrivate.ToArray());
        List<DecodedFrame> got = Decode(cut);
        Assert.Equal(all.Count(f => f.Time >= start), got.Count);
    }

    /// <summary>
    /// The committed libvpx and libaom clips: a few of each codec in a normal run (a stream the encoder matches, one with hidden
    /// frames, one it cannot match, one that changes size), every one when EGAMI_CORPUS names "trim" or says "all".
    /// </summary>
    public static TheoryData<string> CorpusClips()
    {
        string asked = Environment.GetEnvironmentVariable("EGAMI_CORPUS") ?? string.Empty;
        bool every = asked.Split(',', StringSplitOptions.TrimEntries).Any(a => a is "trim" or "all");
        HashSet<string> few =
        [
            "vp8/v0-altref.webm", "vp8/v0-keyframes.ivf", "vp8/v1-pan.webm", "vp8/alpha-ramp.webm",
            "vp9/p0-altref.webm", "vp9/p2-10bit.mkv", "vp9/p1-444.ivf",
            "av1/tools-8bit.mkv", "av1/forward-keyframes.webm", "av1/monochrome.ivf", "av1/resize.mkv",
        ];
        TheoryData<string> clips = [];
        foreach (string codec in (string[])["vp8", "vp9", "av1"])
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Corpus.Directory, codec)).Where(f => f.EndsWith(".webm", StringComparison.Ordinal) || f.EndsWith(".mkv", StringComparison.Ordinal) || f.EndsWith(".ivf", StringComparison.Ordinal)).Order())
            {
                if (every || few.Contains($"{codec}/{Path.GetFileName(file)}"))
                {
                    clips.Add(Path.Combine(codec, Path.GetFileName(file)));
                }
            }
        }

        return clips;
    }

    /// <summary>
    /// libvpx and libaom clips cut a few frames after their first keyframe: the cut has the source's frames from there, the ones
    /// after the next keyframe exactly; a stream the encoder cannot match is encoded again whole, with a warning.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusClips))]
    public void CorpusClipsAreCutFrameExact(string name)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Corpus.Directory, name));
        List<DecodedFrame> all = Decode(source);
        Assert.SkipWhen(all.Count < 6, "The clip is too short.");
        string container = Path.GetExtension(name)[1..];
        int first = Math.Min(3, all.Count - 3);
        TrimResult result = Trim(source, container, all[first].Time, null, TrimMode.Smart, out byte[] cut);
        List<DecodedFrame> got = Decode(cut);
        Assert.Equal(result.Frames, got.Count);
        if (result.Mode != TrimMode.Keyframe)
        {
            Assert.Equal(all.Count - first, got.Count);
            Assert.Equal(all[first].Time, result.Start);
        }
        if (result.Mode == TrimMode.Reencode)
        {
            Assert.Equal("trim-reencode", Assert.Single(result.Warnings).Code);
            return;
        }

        if (result.Mode == TrimMode.Keyframe)
        {
            // Frames that change size: the cut copies from the keyframe before.
            Assert.Equal("trim-keyframe", Assert.Single(result.Warnings).Code);
            Assert.Equal(0, result.ReencodedFrames);
            return;
        }

        Assert.Equal(TrimMode.Smart, result.Mode);
        for (int i = (int)result.ReencodedFrames; i < got.Count; i++)
        {
            Assert.True(all[first + i].Luma.AsSpan().SequenceEqual(got[i].Luma), $"copied frame {first + i} differs");
        }

        for (int i = 0; i < result.ReencodedFrames; i++)
        {
            Assert.Equal(all[first + i].Time - all[first].Time, got[i].Time);
            Assert.True(Psnr(all[first + i].Luma, got[i].Luma) > 25, $"frame {first + i}: {Psnr(all[first + i].Luma, got[i].Luma):F1} dB");
        }
    }

    internal static TrimResult Trim(byte[] source, string container, MediaTime start, MediaTime? end, TrimMode mode, out byte[] cut, MediaContext? context = null)
    {
        using MemoryStream output = new();
        TrimResult result = MediaFile.Trim(new MemoryStream(source), output, container, start, end, mode, context: context, leaveOpen: true);
        cut = output.ToArray();
        return result;
    }

    internal static MediaTime Frame(int index) => new(index, Rate.Reciprocal());

    internal static VideoCodec Codec(string name) => name switch
    {
        "vp8" => VideoCodecs.Vp8,
        "vp9" => VideoCodecs.Vp9,
        "av1" => VideoCodecs.Av1,
        _ => VideoCodecs.MotionJpeg,
    };

    /// <summary>A clip with a keyframe every 10 frames; <paramref name="hidden"/> lets the encoder code hidden frames, and audio adds a 48 kHz mono track of a rising tone.</summary>
    internal static byte[] MakeClip(VideoCodec codec, string container, bool hidden = false, AudioCodec? audio = null)
    {
        using Vp8EncoderTests.Clip clip = Vp8EncoderTests.Clip.Moving(64, 48, FrameCount);
        using MemoryStream file = new();
        VideoWriterOptions options = new()
        {
            Codec = codec,
            FrameRate = Rate,
            Quality = 70,
            KeyframeInterval = 10,
            SceneCutKeyframes = false,
            Vp8 = hidden ? null : new Vp8EncoderOptions { AlternateReference = false },
            Vp9 = hidden ? null : new Vp9EncoderOptions { AlternateReference = false },
            Av1 = hidden ? null : new Av1EncoderOptions { AlternateReference = false, Pyramid = false },
            AudioCodec = audio,
            AudioSampleRate = audio is null ? 0 : 48000,
            AudioLayout = audio is null ? null : ChannelLayout.Mono,
            AudioBitsPerSample = audio is null ? 0 : 16,
        };
        using (VideoWriter writer = VideoWriter.Create(file, container, options, leaveOpen: true))
        {
            for (int i = 0; i < clip.Frames.Count; i++)
            {
                writer.WriteFrame(clip.Frames[i], Frame(i));
                if (audio is not null)
                {
                    using AudioFrame tone = new(SampleFormat.S16, ChannelLayout.Mono, 48000, 1600, Frame(i));
                    Span<short> samples = tone.GetSpan<short>();
                    for (int s = 0; s < samples.Length; s++)
                    {
                        int n = (i * 1600) + s;
                        samples[s] = (short)(8000 * Math.Sin(n * (0.01 + (n * 1e-7))));
                    }

                    writer.WriteAudio(tone);
                }
            }
        }

        return file.ToArray();
    }

    internal sealed record DecodedFrame(MediaTime Time, byte[] Luma);

    internal static List<DecodedFrame> Decode(byte[] file)
    {
        using VideoReader reader = VideoReader.Open(new MemoryStream(file));
        List<DecodedFrame> frames = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                frames.Add(new DecodedFrame(frame.Timestamp, Luma(frame.Raster)));
            }
        }

        return frames;
    }

    internal static byte[] Luma(YuvRaster raster)
    {
        using MemoryStream bytes = new();
        if (raster is YuvRaster<byte> b)
        {
            for (int y = 0; y < b.Height; y++)
            {
                bytes.Write(b.Y.Row(y));
            }
        }
        else if (raster is YuvRaster<ushort> w)
        {
            for (int y = 0; y < w.Height; y++)
            {
                foreach (ushort v in w.Y.Row(y))
                {
                    bytes.WriteByte((byte)(v >> (w.BitDepth - 8)));
                }
            }
        }

        return bytes.ToArray();
    }

    internal static List<(MediaTime Time, byte[] Data, bool Key)> VideoPackets(byte[] file)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        int track = demuxer.Tracks.First(t => t.Kind == TrackKind.Video).Number;
        List<(MediaTime, byte[], bool)> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track)
                {
                    packets.Add((packet.Timestamp, packet.Data.ToArray(), packet.IsKeyframe));
                }
            }
        }

        return packets;
    }

    private static short[] Pcm(byte[] file)
    {
        using AudioReader reader = AudioReader.Open(new MemoryStream(file), new AudioReaderOptions { SampleFormat = SampleFormat.S16 });
        List<short> samples = [];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                samples.AddRange(frame.AsSpan<short>().ToArray());
            }
        }

        return [.. samples];
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * a.Length / sum);
    }

    /// <summary>Calls back on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class Sync<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    /// <summary>A stream that reads like a pipe: no seeking, no length.</summary>
    private sealed class PipeStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
