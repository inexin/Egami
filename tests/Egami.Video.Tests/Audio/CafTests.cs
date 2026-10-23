using System.Buffers.Binary;
using System.Text.Json;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.9 stage B: CAF read and write, against FFmpeg's decode and ffprobe's packet counts of the clips in
/// tests/video/corpus/audio/caf (see tools/video/make-audio-corpus.py --caf).
/// </summary>
public class CafTests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "caf");

    private static readonly Dictionary<string, AudioCorpus.Reference> Manifest = AudioCorpus.Load(Path.Combine(Folder, "manifest.json"));

    public static IEnumerable<object[]> Lossless => Manifest
        .Where(c => c.Value.Md5.Length > 0 && Decodable(c.Key))
        .Select(c => c.Key).Order(StringComparer.Ordinal).Select(n => new object[] { n });

    private static readonly string[] CompressedClips = ["caf-ac3-stereo.caf", "caf-alac-24-51.caf", "caf-alac-stereo.caf", "caf-mp3-stereo.caf", "caf-opus-stereo.caf"];

    public static IEnumerable<object[]> Compressed => CompressedClips.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Lossless))]
    public void DecodesLikeFfmpeg(string name)
    {
        AudioCorpus.Reference expected = Manifest[name];
        using AudioReader reader = AudioReader.Open(Path.Combine(Folder, name), new AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Throw });
        Assert.Equal(expected.Rate, reader.Track.SampleRate);
        Assert.Equal(expected.Channels, reader.Track.Channels);
        (string md5, long samples) = AudioCorpus.Hash(reader.ReadFrames(), expected.Bits, expected.IsFloat);
        Assert.Equal(expected.Samples, samples);
        Assert.Equal(expected.Md5, md5);
        Assert.Equal(expected.Samples, MediaFile.Probe(Path.Combine(Folder, name)).Tracks[0].Duration!.Value.Ticks);
    }

    [Theory]
    [MemberData(nameof(Compressed))]
    public void CutsPacketsLikeFfprobe(string name)
    {
        string path = Path.Combine(Folder, name);
        using IDemuxer demuxer = CafFormat.Instance.OpenDemuxer(File.OpenRead(path), MediaContext.Default, leaveOpen: false);
        int packets = 0;
        long bytes = 0;
        MediaTime? last = null;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                Assert.True(last is null || packet.Timestamp > last);
                last = packet.Timestamp;
                packets++;
                bytes += packet.Data.Length;
            }
        }

        Assert.Equal(PacketCount(name), packets);
        Assert.True(bytes > 0);
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        Assert.Equal(name switch
        {
            _ when name.Contains("opus", StringComparison.Ordinal) => CodecIds.Opus,
            _ when name.Contains("mp3", StringComparison.Ordinal) => CodecIds.Mp3,
            _ when name.Contains("ac3", StringComparison.Ordinal) => CodecIds.Ac3,
            _ when name.Contains("alac", StringComparison.Ordinal) => CodecIds.Alac,
            _ => CodecIds.Flac,
        }, track.CodecId);
    }

    [Fact]
    public void ReadsOpusAndMp3WithTheirPadding()
    {
        AudioTrackInfo opus = (AudioTrackInfo)MediaFile.Probe(Path.Combine(Folder, "caf-opus-stereo.caf")).Tracks[0];
        Assert.Equal(48000, opus.SampleRate);
        Assert.Equal(2, opus.Channels);
        Assert.True(opus.CodecPrivate.Span.StartsWith("OpusHead"u8));
        Assert.Equal(16 * 960, opus.Duration!.Value.Ticks);
        using (AudioReader reader = AudioReader.Open(Path.Combine(Folder, "caf-opus-stereo.caf")))
        {
            Assert.Equal(16 * 960, reader.ReadFrames().Sum(f => Count(f)));
        }

        using (AudioReader reader = AudioReader.Open(Path.Combine(Folder, "caf-mp3-stereo.caf")))
        {
            long samples = reader.ReadFrames().Sum(f => Count(f));
            Assert.InRange(samples, 13230, 13 * 1152);
        }
    }

    [Fact]
    public void ReadsTagsAndLayout()
    {
        MediaInfo info = MediaFile.Probe(Path.Combine(Folder, "caf-tags.caf"));
        Assert.Equal("CAF", info.FormatName);
        Assert.Equal("Corpus tone", info.Tags["TITLE"]);
        Assert.Equal("Egami", info.Tags["ARTIST"]);
        Assert.Equal("Egami corpus", info.Tags["ALBUM"]);
        Assert.Equal("Test clip", info.Tags["COMMENT"]);
        AudioTrackInfo surround = (AudioTrackInfo)MediaFile.Probe(Path.Combine(Folder, "caf-s24be-51.caf")).Tracks[0];
        Assert.Equal(ChannelLayout.Surround51, surround.EffectiveLayout);
        Assert.Equal(24, surround.BitsPerSample);
        Assert.Equal(CodecIds.PcmBigEndian, surround.CodecId);
        Assert.Equal(CodecIds.G711MuLaw, MediaFile.Probe(Path.Combine(Folder, "caf-ulaw-mono.caf")).Tracks[0].CodecId);
        AudioTrackInfo alac = (AudioTrackInfo)MediaFile.Probe(Path.Combine(Folder, "caf-alac-24-51.caf")).Tracks[0];
        Assert.Equal(24, alac.CodecPrivate.Length);
        Assert.Equal(6, alac.CodecPrivate.Span[9]);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(16, true)]
    [InlineData(24, true)]
    public void IntegersRoundTrip(int bits, bool bigEndianInput)
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, bits);
        string path = AudioRoundTripTests.OutputPath($"pcm-{bits}-{(bigEndianInput ? "be" : "le")}.caf");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = bigEndianInput ? PcmCodec.BigEndian : null }))
        {
            writer.Write(input);
        }

        Assert.Equal("caff"u8.ToArray(), File.ReadAllBytes(path)[..4]);
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(path).Tracks[0];
        Assert.Equal(bits, track.BitsPerSample);
        Assert.Equal(input.SampleCount, track.Duration!.Value.Ticks);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void FloatRoundTrips(int bits)
    {
        using AudioFrame ints = AudioRoundTripTests.Signal(2, 24);
        using AudioFrame input = ints.Convert(bits == 64 ? SampleFormat.F64Planar : SampleFormat.F32Planar);
        string path = AudioRoundTripTests.OutputPath($"float-{bits}.caf");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = PcmCodec.FloatingPoint, BitsPerSample = bits }))
        {
            writer.Write(input);
        }

        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = null });
        using AudioFrame expected = input.Convert(SampleFormat.F64Planar);
        int offset = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            using (AudioFrame planar = frame.Convert(SampleFormat.F64Planar))
            {
                Assert.True(planar.AsSpan<double>(1).SequenceEqual(expected.AsSpan<double>(1).Slice(offset, frame.SampleCount)));
                offset += frame.SampleCount;
            }
        }

        Assert.Equal(input.SampleCount, offset);
    }

    [Fact]
    public void StreamsPcmToANonSeekableOutput()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, 16);
        using MemoryStream piped = new();
        using (AudioWriter writer = AudioWriter.Create(new WriteOnlyStream(piped), "caf"))
        {
            writer.Write(input);
        }

        // The data chunk is the last one and runs to the end of the file.
        byte[] file = piped.ToArray();
        int data = file.AsSpan().IndexOf("data"u8);
        Assert.Equal(-1, BinaryPrimitives.ReadInt64BigEndian(file.AsSpan(data + 4)));
        Assert.Equal(file.Length, data + 16 + (input.SampleCount * 4));

        using AudioReader reader = AudioReader.Open(new ForwardOnlyStream(new MemoryStream(file)), new AudioReaderOptions { SampleFormat = null });
        Assert.Null(reader.Track.Duration);
        Assert.Equal(input.SampleCount, reader.ReadFrames().Sum(f => Count(f)));
    }

    [Fact]
    public void OpusRoundTripsWithItsPreSkip()
    {
        using AudioFrame ints = AudioRoundTripTests.Signal(2, 16, rate: 48000, samples: 30000);
        using AudioFrame input = ints.Convert(SampleFormat.F32Planar);
        foreach (bool seekable in new[] { true, false })
        {
            using MemoryStream output = new();
            using (AudioWriter writer = seekable
                ? AudioWriter.Create(output, "caf", new AudioWriterOptions { Codec = OpusCodec.Instance }, leaveOpen: true)
                : AudioWriter.Create(new WriteOnlyStream(output), "caf", new AudioWriterOptions { Codec = OpusCodec.Instance }))
            {
                writer.Write(input);
            }

            byte[] file = output.ToArray();
            bool tableFirst = file.AsSpan().IndexOf("pakt"u8) < file.AsSpan().IndexOf("data"u8);
            Assert.Equal(!seekable, tableFirst);
            Stream read = seekable ? new MemoryStream(file) : new ForwardOnlyStream(new MemoryStream(file));
            using AudioReader reader = AudioReader.Open(read, new AudioReaderOptions { SampleFormat = null });
            Assert.Equal(CodecIds.Opus, reader.Track.CodecId);
            Assert.Equal(312, reader.Track.CodecDelay!.Value.Rescale(new Rational(1, 48000)).Ticks);
            Assert.Equal(input.SampleCount, reader.Track.Duration!.Value.Ticks);
            Assert.Equal(input.SampleCount, reader.ReadFrames().Sum(f => Count(f)));
        }
    }

    [Fact]
    public void FlacRoundTripsLosslessly()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, 16, samples: 20001);
        string path = AudioRoundTripTests.OutputPath("flac.caf");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = FlacCodec.Instance }))
        {
            writer.Write(input);
        }

        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(path).Tracks[0];
        Assert.Equal(CodecIds.Flac, track.CodecId);
        Assert.True(track.CodecPrivate.Span.StartsWith("fLaC"u8));
        Assert.Equal(input.SampleCount, track.Duration!.Value.Ticks);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Theory]
    [InlineData("caf-opus-stereo.caf")]
    [InlineData("caf-mp3-stereo.caf")]
    [InlineData("caf-ac3-stereo.caf")]
    [InlineData("caf-alac-24-51.caf")]
    public void RemuxesPacketsUnchanged(string name)
    {
        List<(long Time, long? Duration, byte[] Data)> original = Packets(File.OpenRead(Path.Combine(Folder, name)), out TrackInfo track);
        foreach (bool seekable in new[] { true, false })
        {
            using MemoryStream output = new();
            using (IMuxer muxer = CafFormat.Instance.CreateMuxer(seekable ? output : new WriteOnlyStream(output), [track], MediaContext.Default, leaveOpen: true))
            {
                using IDemuxer demuxer = CafFormat.Instance.OpenDemuxer(File.OpenRead(Path.Combine(Folder, name)), MediaContext.Default, leaveOpen: false);
                while (demuxer.ReadPacket() is { } packet)
                {
                    using (packet)
                    {
                        muxer.WritePacket(packet);
                    }
                }
            }

            List<(long Time, long? Duration, byte[] Data)> copy = Packets(new MemoryStream(output.ToArray()), out TrackInfo copied);
            Assert.Equal(track.CodecId, copied.CodecId);
            Assert.True(track.CodecPrivate.Span.SequenceEqual(copied.CodecPrivate.Span));
            Assert.Equal(track.Duration, copied.Duration);
            Assert.Equal(original.Count, copy.Count);
            for (int i = 0; i < original.Count; i++)
            {
                Assert.Equal(original[i].Time, copy[i].Time);
                Assert.Equal(original[i].Duration, copy[i].Duration);
                Assert.Equal(original[i].Data, copy[i].Data);
            }
        }
    }

    [Fact]
    public void KeepsTagsChaptersAndLayout()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(6, 16, rate: 8000, samples: 16000);
        MediaMetadata metadata = new()
        {
            Tags = new Dictionary<string, string> { ["TITLE"] = "Round trip", ["ARTIST"] = "Ægami", ["ALBUM"] = "Corpus", ["COMMENT"] = "Plain", ["MOOD"] = "Calm" },
            Chapters = [new ChapterInfo(MediaTime.Zero, null, "One"), new ChapterInfo(MediaTime.FromSeconds(1), null, "Two")],
            Attachments = [new AttachmentInfo("cover.png", "image/png", new byte[] { 1, 2, 3 })],
        };
        List<MediaWarning> warnings = [];
        string path = AudioRoundTripTests.OutputPath("tags.caf");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Metadata = metadata, Layout = ChannelLayout.Surround51 }, MediaContext.Default with { Warnings = warnings.Add }))
        {
            writer.Write(input);
        }

        Assert.Contains(warnings, w => w.Code == "metadata-dropped");
        MediaInfo info = MediaFile.Probe(path);
        Assert.Equal("Round trip", info.Tags["TITLE"]);
        Assert.Equal("Ægami", info.Tags["ARTIST"]);
        Assert.Equal("Corpus", info.Tags["ALBUM"]);
        Assert.Equal("Plain", info.Tags["COMMENT"]);
        Assert.Equal("Calm", info.Tags["MOOD"]);
        Assert.Equal(["One", "Two"], info.Chapters.Select(c => c.Title));
        Assert.Equal(MediaTime.FromSeconds(1), info.Chapters[1].Start);
        Assert.Equal(MediaTime.FromSeconds(2), info.Chapters[1].End);
        Assert.Equal(ChannelLayout.Surround51, ((AudioTrackInfo)info.Tracks[0]).Layout);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Fact]
    public void SeeksToTheSampleAndToThePacket()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, 16, samples: 20000);
        string path = AudioRoundTripTests.OutputPath("seek.caf");
        using (AudioWriter writer = AudioWriter.Create(path))
        {
            writer.Write(input);
        }

        using (IDemuxer demuxer = CafFormat.Instance.OpenDemuxer(File.OpenRead(path), MediaContext.Default, leaveOpen: false))
        {
            Rational timeBase = demuxer.Tracks[0].TimeBase;
            demuxer.Seek(new MediaTime(12345, timeBase));
            using EncodedPacket packet = demuxer.ReadPacket()!;
            Assert.Equal(12345, packet.Timestamp.Rescale(timeBase).Ticks);
            Assert.Equal(input.AsSpan<short>(0)[12345], BinaryPrimitives.ReadInt16LittleEndian(packet.Data.Span));
        }

        using (IDemuxer demuxer = CafFormat.Instance.OpenDemuxer(File.OpenRead(Path.Combine(Folder, "caf-opus-stereo.caf")), MediaContext.Default, leaveOpen: false))
        {
            Rational timeBase = demuxer.Tracks[0].TimeBase;
            demuxer.Seek(new MediaTime(5000, timeBase));
            using EncodedPacket packet = demuxer.ReadPacket()!;
            Assert.Equal(4800, packet.Timestamp.Rescale(timeBase).Ticks);
            demuxer.Seek(MediaTime.Zero);
            using EncodedPacket first = demuxer.ReadPacket()!;
            Assert.Equal(0, first.Timestamp.Ticks);
        }
    }

    [Fact]
    public void ReadsCutOffAndBrokenFiles()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, "caf-s16le-stereo.caf"));
        using (AudioReader reader = AudioReader.Open(new MemoryStream(file[..^1001]), new AudioReaderOptions { SampleFormat = null }))
        {
            Assert.Equal(Manifest["caf-s16le-stereo.caf"].Samples - 251, reader.ReadFrames().Sum(f => Count(f)));
        }

        byte[] noDesc = [.. file];
        "XXXX"u8.CopyTo(noDesc.AsSpan(8));
        Assert.Throws<CorruptDataException>(() => MediaFile.Probe(new MemoryStream(noDesc)));

        // FFmpeg puts the packet table after the samples, which a forward-only reader cannot use.
        byte[] opus = File.ReadAllBytes(Path.Combine(Folder, "caf-opus-stereo.caf"));
        Assert.Throws<UnsupportedFormatException>(() => CafFormat.Instance.OpenDemuxer(new ForwardOnlyStream(new MemoryStream(opus)), MediaContext.Default, leaveOpen: false));
        Assert.Throws<CorruptDataException>(() => CafFormat.Instance.OpenDemuxer(new MemoryStream(opus[..^200]), MediaContext.Default, leaveOpen: false));

        // With the table first, a file cut in the samples keeps the packets that are whole.
        List<(long Time, long? Duration, byte[] Data)> all = Packets(new MemoryStream(opus), out TrackInfo track);
        using MemoryStream streamed = new();
        using (IMuxer muxer = CafFormat.Instance.CreateMuxer(new WriteOnlyStream(streamed), [track], MediaContext.Default, leaveOpen: true))
        {
            using IDemuxer demuxer = CafFormat.Instance.OpenDemuxer(new MemoryStream(opus), MediaContext.Default, leaveOpen: false);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        byte[] cut = streamed.ToArray()[..^(all[^1].Data.Length + 1)];
        Assert.Equal(all.Count - 2, Packets(new ForwardOnlyStream(new MemoryStream(cut)), out _).Count);
    }

    [Fact]
    public void RefusesWhatCafCannotHold()
    {
        AudioTrackInfo vorbis = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.Vorbis, TimeBase = new Rational(1, 44100), SampleRate = 44100, Channels = 2 };
        Assert.Throws<NotSupportedException>(() => CafFormat.Instance.CreateMuxer(new MemoryStream(), [vorbis], MediaContext.Default, leaveOpen: false));
        AudioTrackInfo pcm12 = vorbis with { CodecId = CodecIds.PcmBigEndian, BitsPerSample = 12 };
        Assert.Throws<NotSupportedException>(() => CafFormat.Instance.CreateMuxer(new MemoryStream(), [pcm12], MediaContext.Default, leaveOpen: false));
        Assert.Throws<NotSupportedException>(() => CafFormat.Instance.CreateMuxer(new MemoryStream(), [vorbis, vorbis with { Number = 2 }], MediaContext.Default, leaveOpen: false));
    }

    private static bool Decodable(string name) => !name.Contains("alac", StringComparison.Ordinal);

    private static long Count(AudioFrame frame)
    {
        using (frame)
        {
            return frame.SampleCount;
        }
    }

    private static int PacketCount(string name)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json")));
        return json.RootElement.GetProperty(name).GetProperty("packets").GetInt32();
    }

    private static List<(long Time, long? Duration, byte[] Data)> Packets(Stream stream, out TrackInfo track)
    {
        using IDemuxer demuxer = CafFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: false);
        track = demuxer.Tracks[0];
        List<(long, long?, byte[])> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add((packet.Timestamp.Rescale(track.TimeBase).Ticks, packet.Duration?.Rescale(track.TimeBase).Ticks, packet.Data.ToArray()));
            }
        }

        return packets;
    }

    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
