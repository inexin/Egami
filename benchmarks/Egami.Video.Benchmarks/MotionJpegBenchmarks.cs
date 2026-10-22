using BenchmarkDotNet.Attributes;

using Egami;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>Motion JPEG decode and encode of 100 720p frames, on one thread and on all cores.</summary>
[MemoryDiagnoser]
public class MotionJpegBenchmarks
{
    private readonly List<EncodedPacket> _packets = [];
    private VideoTrackInfo _track = null!;
    private MediaContext _context = null!;
    private YuvVideoFrame[] _frames = [];

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = MediaContext.Default with
        {
            Egami = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads },
        };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Clips.MotionJpeg720p(".mkv", 1500));
        _track = demuxer.Info.VideoTrack!;
        while (_packets.Count < 100 && demuxer.ReadPacket() is { } packet)
        {
            if (packet.TrackNumber == _track.Number)
            {
                _packets.Add(packet);
            }
            else
            {
                packet.Dispose();
            }
        }

        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(_track, context: _context);
        List<YuvVideoFrame> frames = [];
        foreach (EncodedPacket packet in _packets.Take(10))
        {
            decoder.Send(packet);
            decoder.Flush();
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frames.Add(frame);
            }
        }

        _frames = [.. frames];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _packets.ForEach(p => p.Dispose());
        _packets.Clear();
        foreach (YuvVideoFrame frame in _frames)
        {
            frame.Dispose();
        }
    }

    [Benchmark(Description = "Decode 100 frames to planes")]
    public int Decode()
    {
        int count = 0;
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(_track, context: _context);
        foreach (EncodedPacket packet in _packets)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frame.Dispose();
                count++;
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            frame.Dispose();
            count++;
        }

        return count;
    }

    [Benchmark(Description = "Encode 100 frames from planes")]
    public long Encode()
    {
        long bytes = 0;
        using IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = 1280, Height = 720 }, _context);
        for (int i = 0; i < 100; i++)
        {
            encoder.Send(_frames[i % _frames.Length]);
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                bytes += packet.Data.Length;
                packet.Dispose();
            }
        }

        return bytes;
    }

    [Benchmark(Description = "Decode 100 frames to Rgb8")]
    public int DecodeRgb()
    {
        int count = 0;
        using IVideoDecoder<Rgb8> decoder = VideoCodecs.CreateDecoder<Rgb8>(_track, context: _context);
        foreach (EncodedPacket packet in _packets)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out VideoFrame<Rgb8>? frame))
            {
                frame.Dispose();
                count++;
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out VideoFrame<Rgb8>? frame))
        {
            frame.Dispose();
            count++;
        }

        return count;
    }
}
