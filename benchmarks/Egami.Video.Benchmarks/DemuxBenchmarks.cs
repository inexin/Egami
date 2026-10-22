using BenchmarkDotNet.Attributes;

using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V1 design's demux gate: reading every packet of a file must be faster than reading the file's bytes twice. The clip is
/// 720p Motion JPEG with PCM audio, about 250 MB.
/// </summary>
[MemoryDiagnoser]
public class DemuxBenchmarks
{
    private readonly byte[] _buffer = new byte[1 << 16];
    private string _path = "";

    [Params(".avi", ".mkv")]
    public string Container { get; set; } = ".avi";

    [GlobalSetup]
    public void Setup() => _path = Clips.MotionJpeg720p(Container, 1500);

    [Benchmark(Baseline = true, Description = "Read the file twice")]
    public long ReadTwice() => ReadOnce() + ReadOnce();

    [Benchmark(Description = "Demux every packet")]
    public long Demux()
    {
        long bytes = 0;
        using IDemuxer demuxer = MediaFile.OpenDemuxer(_path);
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                bytes += packet.Data.Length;
            }
        }

        return bytes;
    }

    private long ReadOnce()
    {
        long total = 0;
        using FileStream file = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        int n;
        while ((n = file.Read(_buffer)) > 0)
        {
            total += n;
        }

        return total;
    }
}
