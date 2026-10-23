using BenchmarkDotNet.Attributes;

using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V4.3 speed gate: writing fragmented MP4 takes at most 1.05 times as long as writing flat MP4 (moov first, the media moved
/// forward at the end), and allocates no more per packet. The packets are read into memory first, so only the writer is timed.
/// Two inputs: 720p Motion JPEG with PCM audio, 500 frames, about 80 MB; and 40,000 packets of 1 KB of PCM, where per-packet
/// costs show most. Both write to a file.
/// </summary>
[MemoryDiagnoser]
public class Mp4WriteBenchmarks
{
    private static readonly Mp4Format Fragmented = new() { Fragments = new Mp4FragmentOptions() };

    private TrackInfo[] _tracks = [];
    private EncodedPacket[] _packets = [];
    private string _output = "";

    [Params("720p", "small packets")]
    public string Clip { get; set; } = "720p";

    [GlobalSetup]
    public void Setup()
    {
        string input = Clip == "720p" ? Clips.MotionJpeg720p(".mkv", 500) : SmallPackets();
        using IDemuxer demuxer = MediaFile.OpenDemuxer(input);
        _tracks = [.. demuxer.Tracks];
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add(new EncodedPacket(packet.TrackNumber, packet.Data.ToArray(), packet.Timestamp) { DecodeTimestamp = packet.DecodeTimestamp, Duration = packet.Duration, IsKeyframe = packet.IsKeyframe });
            }
        }

        _packets = [.. packets];
        _output = Path.Combine(Clips.Directory, "mp4-write-out.mp4");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (EncodedPacket packet in _packets)
        {
            packet.Dispose();
        }
    }

    [Benchmark(Baseline = true, Description = "Flat, moov first")]
    public long Flat() => Write(Mp4Format.Instance);

    [Benchmark(Description = "Fragmented")]
    public long Fragments() => Write(Fragmented);

    private long Write(Mp4Format format)
    {
        using FileStream file = new(_output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using (IMuxer muxer = format.CreateMuxer(file, _tracks, MediaContext.Default, leaveOpen: true))
        {
            foreach (EncodedPacket packet in _packets)
            {
                muxer.WritePacket(packet);
            }
        }

        return file.Length;
    }

    private static string SmallPackets()
    {
        string path = Path.Combine(Clips.Directory, "small-packets.mkv");
        if (!File.Exists(path))
        {
            AudioTrackInfo audio = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.PcmLittleEndian, TimeBase = new Rational(1, 48000), SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
            byte[] data = new byte[1024];
            using IMuxer muxer = MediaFile.CreateMuxer(path, [audio]);
            for (int i = 0; i < 40_000; i++)
            {
                using EncodedPacket packet = new(1, data, new MediaTime(i * 256L, audio.TimeBase)) { IsKeyframe = true };
                muxer.WritePacket(packet);
            }
        }

        return path;
    }
}
