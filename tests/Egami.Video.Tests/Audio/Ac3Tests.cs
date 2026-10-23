using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.9 stage D, tasks 13 and 16: raw AC-3 and E-AC-3, and both in MP4 (dac3, dec3), Matroska and CAF, against ffprobe's packet
/// counts and the frames FFmpeg copies out of the clips in tests/video/corpus/audio/ac3 (see tools/video/make-audio-corpus.py --ac3).
/// </summary>
public class Ac3Tests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "ac3");

    private static readonly Dictionary<string, Clip> Manifest = LoadManifest();

    public static IEnumerable<object[]> Clips => Manifest.Keys.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    public static IEnumerable<object[]> RawClips => Manifest.Keys.Where(n => n.EndsWith("ac3", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Clips))]
    public void ReadsFramesLikeFfmpeg(string name)
    {
        Clip expected = Manifest[name];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(Folder, name));
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        Assert.Equal(expected.Eac3 ? CodecIds.EAc3 : CodecIds.Ac3, track.CodecId);
        Assert.Equal(expected.Rate, track.SampleRate);
        Assert.Equal(expected.Channels, track.Channels);
        Assert.Equal(expected.Channels, track.EffectiveLayout.ChannelCount);
        (int packets, string md5, _) = ReadAll(demuxer);
        Assert.Equal(expected.Packets, packets);
        Assert.Equal(expected.Md5, md5);
    }

    [Theory]
    [MemberData(nameof(RawClips))]
    public void ProbesRawStreams(string name)
    {
        Clip expected = Manifest[name];
        using FileStream file = File.OpenRead(Path.Combine(Folder, name));
        MediaInfo info = MediaFile.Probe(file);
        Assert.Equal("AC-3", info.FormatName);

        // Every frame of these clips holds 1536 samples, so the duration is exact.
        Assert.Equal(expected.Packets * 1536L, info.Duration!.Value.Rescale(new Rational(1, expected.Rate)).Ticks);
    }

    [Theory]
    [MemberData(nameof(RawClips))]
    public void ReadsForwardOnlyAndAfterJunk(string name)
    {
        Clip expected = Manifest[name];
        byte[] junk = [0x0B, 0x77, 0x00, 0x00, 0x13, 0x0B, 0x0B, 0x77, 0xFF, .. Encoding.ASCII.GetBytes("not a frame")];
        byte[] file = [.. junk, .. File.ReadAllBytes(Path.Combine(Folder, name))];
        using IDemuxer demuxer = Ac3Format.Instance.OpenDemuxer(new ForwardOnlyStream(new MemoryStream(file)), MediaContext.Default, leaveOpen: false);
        Assert.False(demuxer.CanSeek);
        (int packets, string md5, _) = ReadAll(demuxer);
        Assert.Equal(expected.Packets, packets);
        Assert.Equal(expected.Md5, md5);
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
    [InlineData("mka-ac3-stereo.mka", "ac3-stereo-48k.ac3")]
    [InlineData("mka-eac3-51.mka", "eac3-51-48k.eac3")]
    public void WritesRawStreams(string source, string raw)
    {
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, source)), output, "ac3", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Folder, raw)), output.ToArray());
    }

    [Theory]
    [InlineData("ac3-51-48k.ac3", "mp4-ac3-51.mp4", "dac3")]
    [InlineData("eac3-stereo-48k.eac3", "mp4-eac3-stereo.mp4", "dec3")]
    public void WritesMp4ConfigurationLikeFfmpeg(string raw, string ffmpeg, string box)
    {
        Clip expected = Manifest[raw];
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, raw)), output, "mp4", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        byte[] ours = output.ToArray();
        Assert.Equal(Box(File.ReadAllBytes(Path.Combine(Folder, ffmpeg)), box), Box(ours, box));

        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(ours));
        Assert.Equal(expected.Eac3 ? CodecIds.EAc3 : CodecIds.Ac3, demuxer.Tracks[0].CodecId);
        Assert.Equal(expected.Channels, ((AudioTrackInfo)demuxer.Tracks[0]).Channels);
        (int packets, string md5, _) = ReadAll(demuxer);
        Assert.Equal(expected.Packets, packets);
        Assert.Equal(expected.Md5, md5);
    }

    [Theory]
    [InlineData("ac3-stereo-48k.ac3", "mkv")]
    [InlineData("eac3-51-48k.eac3", "mkv")]
    [InlineData("ac3-mono-44k.ac3", "caf")]
    [InlineData("eac3-stereo-48k.eac3", "caf")]
    [InlineData("mp4-eac3-stereo.mp4", "mp4")]
    public void RemuxesPacketsUnchanged(string name, string format)
    {
        Clip expected = Manifest[name];
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, name)), output, format, leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(output.ToArray()));
        Assert.Equal(expected.Eac3 ? CodecIds.EAc3 : CodecIds.Ac3, demuxer.Tracks[0].CodecId);
        (int packets, string md5, long samples) = ReadAll(demuxer);
        Assert.Equal(expected.Packets, packets);
        Assert.Equal(expected.Md5, md5);
        if (format == "caf")
        {
            // CAF's packet table gives every packet its duration (Matroska and FFmpeg's MP4 edit list differ).
            Assert.Equal(expected.Packets * 1536L, samples);
        }
    }

    [Fact]
    public void BuildsConfigurationFromTheTrackAlone()
    {
        AudioTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Audio,
            CodecId = CodecIds.Ac3,
            TimeBase = new Rational(1, 48000),
            SampleRate = 48000,
            Channels = 6,
        };
        using MemoryStream output = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(output, "mp4", [track], leaveOpen: true))
        {
        }

        // fscod 0, bsid 8, bsmod 0, acmod 7 (3/2), LFE on, bit rate code 0.
        Assert.Equal(new byte[] { 0x10, 0x3C, 0x00 }, Box(output.ToArray(), "dac3"));
    }

    [Fact]
    public void RefusesStreamsThatAreNotAc3()
    {
        byte[] mp3 = File.ReadAllBytes(Path.Combine(AudioCorpus.Directory, "wav-s16-stereo.wav"));
        Assert.False(Ac3Format.Instance.IsMatch(mp3));
        Assert.Throws<CorruptDataException>(() => Ac3Format.Instance.OpenDemuxer(new MemoryStream(new byte[70000]), MediaContext.Default, leaveOpen: false));
    }

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

        samples = samples * ((AudioTrackInfo)track).SampleRate / track.TimeBase.Denominator * track.TimeBase.Numerator;
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

    /// <summary>The body of the first box of this type anywhere in an MP4 file.</summary>
    private static byte[] Box(byte[] file, string type)
    {
        int at = file.AsSpan().IndexOf(Encoding.ASCII.GetBytes(type));
        Assert.True(at >= 4, $"No {type} box.");
        int size = (file[at - 4] << 24) | (file[at - 3] << 16) | (file[at - 2] << 8) | file[at - 1];
        return file[(at + 4)..(at - 4 + size)];
    }

    private static Dictionary<string, Clip> LoadManifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json")));
        Dictionary<string, Clip> result = [];
        foreach (JsonProperty clip in json.RootElement.EnumerateObject())
        {
            JsonElement e = clip.Value;
            result[clip.Name] = new Clip(e.GetProperty("packets").GetInt32(), e.GetProperty("rate").GetInt32(), e.GetProperty("channels").GetInt32(),
                e.GetProperty("eac3").GetBoolean(), e.GetProperty("md5").GetString()!);
        }

        return result;
    }

    private sealed record Clip(int Packets, int Rate, int Channels, bool Eac3, string Md5);

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
