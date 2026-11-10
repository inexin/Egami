using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Egami.Video.Audio;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.10 task 2: raw AAC in ADTS frames, read and written, against ffprobe's packet counts and the raw data blocks FFmpeg copies
/// out of the clips in tests/video/corpus/audio/aac (see tools/video/make-audio-corpus.py --aac), which FFmpeg's aac encoder
/// and the FDK AAC library's aac-enc made.
/// </summary>
public class AdtsTests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "aac");

    private static readonly Dictionary<string, Clip> Manifest = LoadManifest();

    public static IEnumerable<object[]> Clips => Manifest.Keys.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    public static IEnumerable<object[]> RawClips => Manifest.Keys.Where(n => n.EndsWith(".aac", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Clips))]
    public void ReadsBlocksLikeFfmpeg(string name)
    {
        Clip expected = Manifest[name];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(Folder, name));
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        Assert.Equal(CodecIds.Aac, track.CodecId);
        Assert.Equal(expected.CoreRate, track.SampleRate);
        Assert.Equal(expected.Channels, track.Channels);
        Assert.Equal(expected.Channels, track.EffectiveLayout.ChannelCount);
        (int packets, string md5, long samples) = ReadAll(demuxer);
        Assert.Equal(expected.Blocks, packets);
        Assert.Equal(expected.Md5, md5);
        if (name.EndsWith(".aac", StringComparison.Ordinal))
        {
            Assert.Equal(expected.Blocks * 1024L, samples);
        }
    }

    [Theory]
    [MemberData(nameof(RawClips))]
    public void ProbesRawStreams(string name)
    {
        Clip expected = Manifest[name];
        using FileStream file = File.OpenRead(Path.Combine(Folder, name));
        MediaInfo info = MediaFile.Probe(file);
        Assert.Equal("ADTS", info.FormatName);

        // The clips fit in the first window, so the estimate from their frame sizes is exact.
        Assert.Equal(expected.Blocks * 1024L, info.Duration!.Value.Rescale(new Rational(1, expected.CoreRate)).Ticks);
    }

    [Fact]
    public void BuildsTheAudioSpecificConfigLikeMp4()
    {
        using IDemuxer adts = MediaFile.OpenDemuxer(Path.Combine(Folder, "ff-lc-stereo-44k.aac"));
        using IDemuxer mp4 = MediaFile.OpenDemuxer(Path.Combine(Folder, "ff-lc-stereo.m4a"));
        // FFmpeg's MP4 config adds a sync extension saying there is no SBR after the two bytes ADTS has fields for.
        Assert.Equal(mp4.Tracks[0].CodecPrivate[..2].ToArray(), adts.Tracks[0].CodecPrivate.ToArray());
        Assert.Equal(ChannelLayout.Stereo, ((AudioTrackInfo)adts.Tracks[0]).Layout);
    }

    [Fact]
    public void ReadsId3TagsAndMpeg2Headers()
    {
        MediaInfo tagged = MediaFile.Probe(Path.Combine(Folder, "ff-lc-id3.aac"));
        Assert.Equal("ADTS", tagged.FormatName);
        Assert.Equal("Corpus tone", tagged.Tags["TITLE"]);
        Assert.Equal("Egami", tagged.Tags["ARTIST"]);

        // FFmpeg's MPEG-2 ADTS carries the same blocks as its MPEG-4 ADTS.
        Assert.Equal(Manifest["ff-lc-stereo-32k.aac"].Md5, Manifest["ff-lc-mpeg2.aac"].Md5);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(Folder, "ff-lc-mpeg2.aac"));
        Assert.Equal(Manifest["ff-lc-mpeg2.aac"].Md5, ReadAll(demuxer).Md5);
    }

    [Theory]
    [InlineData("ff-lc-stereo-44k.aac")]
    [InlineData("fdk-lc-51-48k.aac")]
    public void ReadsFramesWithCrcs(string name)
    {
        // Each header is rewritten with protection_absent clear and a check word after it; the reader strips both.
        byte[] source = File.ReadAllBytes(Path.Combine(Folder, name));
        List<byte> file = [];
        for (int at = 0; at < source.Length;)
        {
            int length = ((source[at + 3] & 3) << 11) | (source[at + 4] << 3) | (source[at + 5] >> 5);
            byte[] header = source[at..(at + 7)];
            int crcLength = length + 2;
            header[1] &= 0xFE;
            header[3] = (byte)((header[3] & 0xFC) | (crcLength >> 11));
            header[4] = (byte)(crcLength >> 3);
            header[5] = (byte)((header[5] & 0x1F) | ((crcLength & 7) << 5));
            file.AddRange(header);
            file.AddRange([0xA5, 0x5A]);
            file.AddRange(source.AsSpan((at + 7)..(at + length)));
            at += length;
        }

        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream([.. file]));
        (int packets, string md5, _) = ReadAll(demuxer);
        Assert.Equal(Manifest[name].Blocks, packets);
        Assert.Equal(Manifest[name].Md5, md5);
    }

    [Theory]
    [MemberData(nameof(RawClips))]
    public void ReadsForwardOnlyAndAfterJunk(string name)
    {
        Clip expected = Manifest[name];
        byte[] junk = [0xFF, 0xF1, 0x50, 0x80, 0x01, 0xFF, 0xFC, 0xFF, 0xFF, .. Encoding.ASCII.GetBytes("not a frame")];
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, name));
        byte[] file = name == "ff-lc-id3.aac" ? data : [.. junk, .. data];
        using IDemuxer demuxer = AdtsFormat.Instance.OpenDemuxer(new ForwardOnlyStream(new MemoryStream(file)), MediaContext.Default, leaveOpen: false);
        Assert.False(demuxer.CanSeek);
        (int packets, string md5, _) = ReadAll(demuxer);
        Assert.Equal(expected.Blocks, packets);
        Assert.Equal(expected.Md5, md5);
    }

    [Fact]
    public void ResyncsAfterDamage()
    {
        // Ten bytes of a middle frame's header are overwritten: that frame is lost and the rest are read.
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, "ff-lc-stereo-48k.aac"));
        int at = 0;
        for (int i = 0; i < 5; i++)
        {
            at += ((file[at + 3] & 3) << 11) | (file[at + 4] << 3) | (file[at + 5] >> 5);
        }

        file.AsSpan(at, 10).Fill(0x11);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal(Manifest["ff-lc-stereo-48k.aac"].Blocks - 1, ReadAll(demuxer).Packets);
    }

    [Theory]
    [MemberData(nameof(RawClips))]
    public void SeeksToFrames(string name)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(Folder, name));
        List<(long Time, byte[] Data)> all = Packets(demuxer);
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        foreach (int i in new[] { all.Count / 2, 0, all.Count - 1 })
        {
            demuxer.Seek(new MediaTime(all[i].Time + 100, track.TimeBase));
            using EncodedPacket packet = demuxer.ReadPacket()!;
            Assert.Equal(all[i].Time, packet.Timestamp.Rescale(track.TimeBase).Ticks);
            Assert.True(packet.Data.Span.SequenceEqual(all[i].Data));
        }
    }

    [Theory]
    [InlineData("ff-lc-stereo.m4a", "ff-lc-stereo-44k.aac")]
    [InlineData("ff-lc-51.mp4", "ff-lc-51-48k.aac")]
    public void WritesRawStreamsLikeFfmpeg(string source, string raw)
    {
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, source)), output, "aac", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Folder, raw)), output.ToArray());
    }

    [Theory]
    [InlineData("ff-lc-stereo-44k.aac", "mkv")]
    [InlineData("ff-lc-51-48k.aac", "mp4")]
    [InlineData("fdk-lc-stereo-48k-vbr.aac", "caf")]
    [InlineData("ff-lc-mono-8k.aac", "avi")]
    [InlineData("ff-lc-stereo.mka", "aac")]
    public void RemuxesPacketsUnchanged(string name, string format)
    {
        Clip expected = Manifest[name];
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, name)), output, format, leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(output.ToArray()));
        Assert.Equal(CodecIds.Aac, demuxer.Tracks[0].CodecId);
        Assert.Equal(expected.Channels, ((AudioTrackInfo)demuxer.Tracks[0]).Channels);
        (int packets, string md5, _) = ReadAll(demuxer);
        Assert.Equal(expected.Blocks, packets);
        Assert.Equal(expected.Md5, md5);
    }

    [Fact]
    public void WritesExplicitSbrAsItsCore()
    {
        // Object type 5 (SBR) at 44.1 kHz over an AAC LC core at 22.05 kHz, stereo.
        AudioTrackInfo track = Track([0x2B, 0x92, 0x08, 0x00], 44100, 2);
        using MemoryStream output = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(output, "aac", [track], leaveOpen: true))
        {
            using EncodedPacket packet = EncodedPacket.Rent(1, 3, MediaTime.Zero);
            muxer.WritePacket(packet);
        }

        byte[] frame = output.ToArray();
        Assert.Equal(10, frame.Length);
        Assert.Equal(new byte[] { 0xFF, 0xF1, 0x5C, 0x80, 0x01, 0x5F, 0xFC }, frame[..7]);
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x00 })]
    [InlineData(new byte[] { 0x17, 0x80, 0x30, 0xD4, 0x10 })]
    [InlineData(new byte[] { 0xF8, 0x00, 0x00 })]
    [InlineData(new byte[0])]
    public void RefusesTracksAdtsCannotHold(byte[] config)
    {
        // A program config element (channel configuration 0), a rate with no index (25 kHz written out), object type 31, none.
        AudioTrackInfo track = Track(config, 48000, 2);
        Assert.Throws<NotSupportedException>(() => MediaFile.CreateMuxer(new MemoryStream(), "aac", [track]));
        Assert.Throws<NotSupportedException>(() => MediaFile.CreateMuxer(new MemoryStream(), "aac", [track with { CodecId = CodecIds.Mp3 }]));
    }

    [Fact]
    public void TellsAdtsFromOtherStreams()
    {
        Assert.False(AdtsFormat.Instance.IsMatch(File.ReadAllBytes(Path.Combine(AudioCorpus.Directory, "mp3", "mp3-id3v1-noxing.mp3"))));
        Assert.False(AdtsFormat.Instance.IsMatch(File.ReadAllBytes(Path.Combine(AudioCorpus.Directory, "ac3", "ac3-stereo-48k.ac3"))));
        Assert.Equal("MP3", MediaFile.Probe(Path.Combine(AudioCorpus.Directory, "mp3", "mp3-cbr-stereo.mp3")).FormatName);
        Assert.True(AdtsFormat.Instance.IsMatch(File.ReadAllBytes(Path.Combine(Folder, "ff-lc-id3.aac"))));
        Assert.Throws<CorruptDataException>(() => AdtsFormat.Instance.OpenDemuxer(new MemoryStream(new byte[70000]), MediaContext.Default, leaveOpen: false));
    }

    private static AudioTrackInfo Track(byte[] config, int rate, int channels) => new()
    {
        Number = 1,
        Kind = TrackKind.Audio,
        CodecId = CodecIds.Aac,
        CodecPrivate = config,
        TimeBase = new Rational(1, rate),
        SampleRate = rate,
        Channels = channels,
    };

    private static (int Packets, string Md5, long Samples) ReadAll(IDemuxer demuxer)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        int packets = 0;
        long samples = 0;
        MediaTime? last = null;
        TrackInfo track = demuxer.Tracks[0];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                Assert.True(last is null || packet.Timestamp > last);
                last = packet.Timestamp;
                md5.AppendData(packet.Data.Span);
                samples += packet.Duration?.Rescale(track.TimeBase).Ticks ?? 0;
                packets++;
            }
        }

        return (packets, Convert.ToHexStringLower(md5.GetHashAndReset()), samples);
    }

    private static List<(long Time, byte[] Data)> Packets(IDemuxer demuxer)
    {
        List<(long, byte[])> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add((packet.Timestamp.Rescale(demuxer.Tracks[0].TimeBase).Ticks, packet.Data.ToArray()));
            }
        }

        return packets;
    }

    private static Dictionary<string, Clip> LoadManifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json")));
        Dictionary<string, Clip> result = [];
        foreach (JsonProperty clip in json.RootElement.EnumerateObject())
        {
            JsonElement e = clip.Value;

            // aac-enc's HE-AAC signals SBR and PS implicitly, so ADTS gives the core: half the rate it decodes to, and for
            // HE-AAC v2 one channel.
            int rate = e.GetProperty("rate").GetInt32();
            int coreRate = clip.Name.StartsWith("fdk-he", StringComparison.Ordinal) ? rate / 2 : rate;
            int coreChannels = clip.Name.StartsWith("fdk-hev2", StringComparison.Ordinal) ? 1 : e.GetProperty("channels").GetInt32();
            result[clip.Name] = new Clip(e.GetProperty("blocks").GetInt32(), coreRate, coreChannels, e.GetProperty("blocksMd5").GetString()!);
        }

        return result;
    }

    private sealed record Clip(int Blocks, int CoreRate, int Channels, string Md5);

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
