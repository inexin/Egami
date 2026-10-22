using Egami.Pixels;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests;

/// <summary>Runs alone: it measures the whole process's managed memory, which tests running beside it would move.</summary>
[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
[Collection(nameof(MemoryTests))]
public class MemoryTests
{
    [Fact]
    public void MemoryStaysSteadyOverALongFile()
    {
        // About two minutes of video: the corpus clip's frames repeated.
        string path = AviWriterTests.TempFile("long.mkv");
        List<byte[]> packets = [.. Codecs.MotionJpegDecoderTests.Packets("avi-mjpeg-420-pcm.avi")];
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 160, Height = 120, FrameRate = new Rational(25, 1) };
        const int Frames = 3000;
        using (IMuxer muxer = MediaFile.CreateMuxer(path, [track]))
        {
            for (int i = 0; i < Frames; i++)
            {
                using EncodedPacket packet = new(1, packets[i % packets.Count], new MediaTime(i, new Rational(1, 25))) { IsKeyframe = true };
                muxer.WritePacket(packet);
            }
        }

        PooledPixelMemorySource pool = new();
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Memory = pool, MaxParallelism = 4 } };
        using VideoReader reader = VideoReader.Open(path, context: context);
        long retainedAfterWarmup = 0, managedAfterWarmup = 0, peakLive = 0;
        int n = 0;
        foreach (VideoFrame<Rgb8> frame in reader.ReadFrames<Rgb8>())
        {
            using (frame)
            {
                peakLive = Math.Max(peakLive, pool.Usage.LiveBytes);
            }

            if (++n == 500)
            {
                retainedAfterWarmup = pool.RetainedBytes;
                managedAfterWarmup = GC.GetTotalMemory(forceFullCollection: true);
            }
        }

        Assert.Equal(Frames, n);
        long frameBytes = 160 * 120 * 3;
        Assert.True(peakLive < 16 * frameBytes, $"peak live pixel memory {peakLive}");
        Assert.True(pool.RetainedBytes <= retainedAfterWarmup + (4 * frameBytes), $"pool grew from {retainedAfterWarmup} to {pool.RetainedBytes}");
        long managedGrowth = GC.GetTotalMemory(forceFullCollection: true) - managedAfterWarmup;
        Assert.True(managedGrowth < 4L << 20, $"managed memory grew by {managedGrowth} bytes");
        Assert.Equal(0, pool.Usage.LiveBytes);
    }

    [Theory]
    [InlineData("v0-keyframes.ivf", false)]
    [InlineData("alpha-odd-75x45.webm", true)]
    public void Vp8KeepsAtMostFourFrameBuffersAndStaysSteady(string clip, bool alpha)
    {
        // About 40 times the clip: each repeat starts on its keyframe, so the stream is valid all the way.
        List<EncodedPacket> packets = [];
        VideoTrackInfo track;
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.Vp8PathOf(clip)))
        {
            track = demuxer.Info.VideoTrack!;
            while (demuxer.ReadPacket() is { } packet)
            {
                packets.Add(packet);
            }
        }

        PooledPixelMemorySource pool = new();
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Memory = pool, MaxParallelism = 4 } };
        long frameBytes = 0, afterWarmup = 0, managedAfterWarmup = 0;
        int frames = 0;
        using (Egami.Video.Codecs.IVideoDecoder decoder = Egami.Video.Codecs.VideoCodecs.CreateDecoder(track, context: context))
        {
            long decoderBytes = 0;
            for (int repeat = 0; repeat < 40; repeat++)
            {
                foreach (EncodedPacket packet in packets)
                {
                    decoder.Send(packet);
                    while (decoder.TryReceive(out YuvVideoFrame? frame))
                    {
                        frameBytes = Math.Max(frameBytes, pool.Usage.LiveBytes - decoderBytes);
                        frame.Dispose();
                        frames++;
                    }

                    decoderBytes = pool.Usage.LiveBytes;
                }

                if (repeat == 5)
                {
                    afterWarmup = pool.RetainedBytes;
                    managedAfterWarmup = GC.GetTotalMemory(forceFullCollection: true);
                }
            }

            // The decoder's own buffers: a VP8 frame with a 48-pixel border (24 for chroma), four per stream and four more for alpha.
            long bordered = ((track.Width + 96L) * (track.Height + 96)) + (2L * (((track.Width + 1) / 2) + 48) * (((track.Height + 1) / 2) + 48));
            Assert.True(decoderBytes <= (alpha ? 8 : 4) * bordered * 5 / 4, $"the decoder holds {decoderBytes} bytes, {(alpha ? 8 : 4)} buffers are {(alpha ? 8 : 4) * bordered}");
        }

        foreach (EncodedPacket packet in packets)
        {
            packet.Dispose();
        }

        Assert.Equal(packets.Count * 40, frames);
        Assert.True(pool.RetainedBytes <= afterWarmup + (2 * frameBytes), $"pool grew from {afterWarmup} to {pool.RetainedBytes}");
        long managedGrowth = GC.GetTotalMemory(forceFullCollection: true) - managedAfterWarmup;
        Assert.True(managedGrowth < 2L << 20, $"managed memory grew by {managedGrowth} bytes");
        Assert.Equal(0, pool.Usage.LiveBytes);
    }
}
