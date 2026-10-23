using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Audio.Codecs.G711;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.9 stage A: AIFF and AIFF-C read and write, and G.711 in AIFF-C, WAV and MOV, against FFmpeg's decode of the clips in
/// tests/video/corpus/audio/aiff (see tools/video/make-audio-corpus.py --aiff).
/// </summary>
public class AiffTests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "aiff");

    private static readonly Dictionary<string, AudioCorpus.Reference> Manifest = AudioCorpus.Load(Path.Combine(Folder, "manifest.json"));

    public static IEnumerable<object[]> Clips => Manifest.Where(c => c.Value.Md5.Length > 0).Select(c => c.Key).Order(StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Clips))]
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

    [Fact]
    public void PassesOtherCompressionsThrough()
    {
        string path = Path.Combine(Folder, "aifc-ima4-stereo.aifc");
        MediaInfo info = MediaFile.Probe(path);
        AudioTrackInfo track = (AudioTrackInfo)Assert.Single(info.Tracks);
        Assert.Equal("X_AIFC/ima4", track.CodecId);
        Assert.Equal(22050, track.SampleRate);
        Assert.Equal(0.3, info.Duration!.Value.Seconds, 1);

        using IDemuxer demuxer = AiffFormat.Instance.OpenDemuxer(File.OpenRead(path), MediaContext.Default, leaveOpen: false);
        long bytes = 0;
        MediaTime last = MediaTime.Zero;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                Assert.True(packet.Timestamp >= last);
                last = packet.Timestamp;
                bytes += packet.Data.Length;
            }
        }

        Assert.Equal(new FileInfo(path).Length - 12 - (8 + 4) - (8 + 24) - (8 + 8), bytes);
    }

    [Fact]
    public void ReadsTagsAndLayout()
    {
        MediaInfo info = MediaFile.Probe(Path.Combine(Folder, "aiff-tags-id3.aiff"));
        Assert.Equal("AIFF", info.FormatName);
        Assert.Equal("Corpus tone", info.Tags["TITLE"]);
        Assert.Equal("Egami", info.Tags["ARTIST"]);
        Assert.Equal("Egami corpus", info.Tags["ALBUM"]);
        Assert.Equal("Test clip", info.Tags["COMMENT"]);
        AudioTrackInfo surround = (AudioTrackInfo)MediaFile.Probe(Path.Combine(Folder, "aiff-s24-51.aiff")).Tracks[0];
        Assert.Equal(ChannelLayout.Surround51, surround.Layout);
        Assert.Equal(24, surround.BitsPerSample);
        Assert.Equal(CodecIds.G711MuLaw, MediaFile.Probe(Path.Combine(Folder, "wav-ulaw-mono.wav")).Tracks[0].CodecId);
        Assert.Equal(CodecIds.G711ALaw, MediaFile.Probe(Path.Combine(Folder, "aifc-alaw-mono.aifc")).Tracks[0].CodecId);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(16, true)]
    [InlineData(24, true)]
    public void IntegersRoundTrip(int bits, bool littleEndianInput)
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, bits);
        string path = AudioRoundTripTests.OutputPath($"pcm-{bits}-{(littleEndianInput ? "le" : "be")}.aiff");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = littleEndianInput ? PcmCodec.Instance : null }))
        {
            writer.Write(input);
        }

        Assert.Equal("AIFF"u8.ToArray(), File.ReadAllBytes(path)[8..12]);
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(path).Tracks[0];
        Assert.Equal(CodecIds.PcmBigEndian, track.CodecId);
        Assert.Equal(bits, track.BitsPerSample);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void FloatRoundTripsAsAifc(int bits)
    {
        using AudioFrame ints = AudioRoundTripTests.Signal(2, 24);
        using AudioFrame input = ints.Convert(bits == 64 ? SampleFormat.F64Planar : SampleFormat.F32Planar);
        string path = AudioRoundTripTests.OutputPath($"float-{bits}.aifc");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = PcmCodec.FloatingPoint, BitsPerSample = bits }))
        {
            writer.Write(input);
        }

        byte[] file = File.ReadAllBytes(path);
        Assert.Equal("AIFC"u8.ToArray(), file[8..12]);
        Assert.Contains(bits == 64 ? "fl64" : "fl32", System.Text.Encoding.ASCII.GetString(file, 0, 80), StringComparison.Ordinal);
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = null });
        int offset = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            using (AudioFrame planar = frame.Convert(SampleFormat.F64Planar))
            using (AudioFrame expected = input.Convert(SampleFormat.F64Planar))
            {
                Assert.True(planar.AsSpan<double>(1).SequenceEqual(expected.AsSpan<double>(1).Slice(offset, frame.SampleCount)));
                offset += frame.SampleCount;
            }
        }

        Assert.Equal(input.SampleCount, offset);
    }

    [Fact]
    public void KeepsTagsChaptersCoverAndLayout()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(6, 16, rate: 8000, samples: 16000);
        byte[] png = [0x89, .. "PNG"u8, 13, 10, 26, 10, 1, 2, 3];
        MediaMetadata metadata = new()
        {
            Tags = new Dictionary<string, string> { ["TITLE"] = "Round trip", ["ARTIST"] = "Ægami", ["ALBUM"] = "Corpus", ["COMMENT"] = "Plain" },
            Chapters = [new ChapterInfo(MediaTime.Zero, null, "One"), new ChapterInfo(MediaTime.FromSeconds(1), null, "Two")],
            Attachments = [new AttachmentInfo("cover.png", "image/png", png)],
        };
        string path = AudioRoundTripTests.OutputPath("tags.aiff");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Metadata = metadata, Layout = ChannelLayout.Surround51 }))
        {
            writer.Write(input);
        }

        string chunks = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)[..200]);
        Assert.Contains("NAME", chunks, StringComparison.Ordinal);
        Assert.Contains("ANNO", chunks, StringComparison.Ordinal);
        Assert.Contains("ID3 ", chunks, StringComparison.Ordinal);
        MediaInfo info = MediaFile.Probe(path);
        Assert.Equal("Round trip", info.Tags["TITLE"]);
        Assert.Equal("Ægami", info.Tags["ARTIST"]);
        Assert.Equal("Corpus", info.Tags["ALBUM"]);
        Assert.Equal("Plain", info.Tags["COMMENT"]);
        Assert.Equal(["One", "Two"], info.Chapters.Select(c => c.Title));
        Assert.Equal(MediaTime.FromSeconds(1), info.Chapters[1].Start);
        Assert.True(Assert.Single(info.Attachments).Data.Span.SequenceEqual(png));
        Assert.Equal(ChannelLayout.Surround51, ((AudioTrackInfo)info.Tracks[0]).Layout);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Fact]
    public void NonSeekableOutputGoesThroughATempFile()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, 16);
        using MemoryStream seekable = new();
        using (AudioWriter writer = AudioWriter.Create(seekable, "aiff", leaveOpen: true))
        {
            writer.Write(input);
        }

        using MemoryStream piped = new();
        using (AudioWriter writer = AudioWriter.Create(new WriteOnlyStream(piped), "aiff"))
        {
            writer.Write(input);
        }

        Assert.Equal(seekable.ToArray(), piped.ToArray());
    }

    [Fact]
    public void SeeksToTheSample()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, 16, samples: 20000);
        string path = AudioRoundTripTests.OutputPath("seek.aiff");
        using (AudioWriter writer = AudioWriter.Create(path))
        {
            writer.Write(input);
        }

        using IDemuxer demuxer = AiffFormat.Instance.OpenDemuxer(File.OpenRead(path), MediaContext.Default, leaveOpen: false);
        Rational timeBase = demuxer.Tracks[0].TimeBase;
        demuxer.Seek(new MediaTime(12345, timeBase));
        using EncodedPacket packet = demuxer.ReadPacket()!;
        Assert.Equal(12345, packet.Timestamp.Rescale(timeBase).Ticks);
        short first = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(packet.Data.Span);
        Assert.Equal(input.AsSpan<short>(0)[12345], first);
    }

    [Fact]
    public void ReadsCutOffAndBrokenFiles()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, "aiff-s16-stereo.aiff"));
        using (AudioReader reader = AudioReader.Open(new MemoryStream(file[..^1001]), new AudioReaderOptions { SampleFormat = null }))
        {
            long samples = reader.ReadFrames().Sum(f =>
            {
                using (f)
                {
                    return (long)f.SampleCount;
                }
            });
            Assert.Equal(Manifest["aiff-s16-stereo.aiff"].Samples - 251, samples);
        }

        byte[] noCommon = [.. file];
        "XXXX"u8.CopyTo(noCommon.AsSpan(12));
        Assert.Throws<CorruptDataException>(() => MediaFile.Probe(new MemoryStream(noCommon)));
    }

    [Fact]
    public void RefusesWhatAiffCannotHold()
    {
        AudioTrackInfo flac = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.Flac, TimeBase = new Rational(1, 44100), SampleRate = 44100, Channels = 2 };
        Assert.Throws<NotSupportedException>(() => AiffFormat.Instance.CreateMuxer(new MemoryStream(), [flac], MediaContext.Default, leaveOpen: false));
        AudioTrackInfo pcm12 = flac with { CodecId = CodecIds.PcmBigEndian, BitsPerSample = 12 };
        Assert.Throws<NotSupportedException>(() => AiffFormat.Instance.CreateMuxer(new MemoryStream(), [pcm12], MediaContext.Default, leaveOpen: false));
    }

    [Fact]
    public void ExpandsG711ByTheStandardTables()
    {
        Assert.Equal(0, G711Tables.MuLaw[0xFF]);
        Assert.Equal(-32124, G711Tables.MuLaw[0x00]);
        Assert.Equal(32124, G711Tables.MuLaw[0x80]);
        Assert.Equal(8, G711Tables.ALaw[0xD5]);
        Assert.Equal(-8, G711Tables.ALaw[0x55]);
        Assert.Equal(32256, G711Tables.ALaw[0xAA]);
        Assert.Equal(-32256, G711Tables.ALaw[0x2A]);

        // Both tables are odd-symmetric and rise with the code within each sign.
        for (int i = 0; i < 128; i++)
        {
            Assert.Equal(G711Tables.MuLaw[i], -G711Tables.MuLaw[i | 0x80]);
            Assert.Equal(G711Tables.ALaw[i], -G711Tables.ALaw[i | 0x80]);
        }
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
}
