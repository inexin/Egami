using BenchmarkDotNet.Attributes;

using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V1.3 progress gate: remuxing without a handler costs what it did before progress existed, and a handler with a
/// cancellable token adds at most 1%. The baseline is the loop Remux ran before, in the same process. Two clips: 720p Motion JPEG with PCM audio, 500 frames, about 80 MB; and 40,000
/// packets of 1 KB, where the per-packet cost of progress and cancellation shows most.
/// </summary>
[MemoryDiagnoser]
public class RemuxBenchmarks
{
    private string _input = "";
    private string _output = "";

    [Params("720p", "small packets")]
    public string Clip { get; set; } = "720p";

    [GlobalSetup]
    public void Setup()
    {
        _input = Clip == "720p" ? Clips.MotionJpeg720p(".mkv", 500) : SmallPackets();
        _output = Path.Combine(Clips.Directory, "remux-out.mkv");
    }

    [Benchmark(Baseline = true, Description = "Loop as before progress")]
    public long Loop()
    {
        // MediaFile.Remux as it was before V1.3, run in the same process so machine load cancels out.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(_input);
        TrackInfo[] kept = [.. demuxer.Tracks];
        HashSet<int> numbers = [.. kept.Select(t => t.Number)];
        using IMuxer muxer = MediaFile.CreateMuxer(_output, kept, metadata: demuxer.Info.Metadata);
        long count = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (numbers.Contains(packet.TrackNumber))
                {
                    muxer.WritePacket(packet);
                    count++;
                }
            }
        }

        muxer.Finish();
        return count;
    }

    [Benchmark(Description = "Remux, no handler")]
    public long Remux() => MediaFile.Remux(_input, _output);

    [Benchmark(Description = "Remux, handler and token")]
    public long RemuxWithProgress()
    {
        using CancellationTokenSource cts = new();
        return MediaFile.Remux(_input, _output, progress: Ignore.Instance, cancellationToken: cts.Token);
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

    private sealed class Ignore : IProgress<MediaProgress>
    {
        public static readonly Ignore Instance = new();

        public void Report(MediaProgress value)
        {
        }
    }
}
