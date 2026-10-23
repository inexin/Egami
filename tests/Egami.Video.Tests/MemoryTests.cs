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

    [Fact]
    public void Mp4SampleTablesOfATenHourFileStaySmall()
    {
        // Ten hours of 30 fps video and 48 kHz audio in 1024-sample packets: 2.8 million samples of one or two bytes each.
        string path = AviWriterTests.TempFile("ten-hours.mp4");
        VideoTrackInfo video = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 30), Width = 16, Height = 16 };
        AudioTrackInfo audio = new() { Number = 2, Kind = TrackKind.Audio, CodecId = CodecIds.PcmLittleEndian, TimeBase = new Rational(1, 48000), SampleRate = 48000, Channels = 1, BitsPerSample = 16 };
        const int Seconds = 10 * 3600;
        byte[][] data = [[1], [1, 2]];
        using (IMuxer muxer = new Mp4Format { MoovAtEnd = true }.CreateMuxer(File.Create(path), [video, audio], MediaContext.Default, leaveOpen: false))
        {
            long audioTicks = 0;
            for (int frame = 0; frame < Seconds * 30; frame++)
            {
                using EncodedPacket v = new(1, data[frame & 1], new MediaTime(frame, video.TimeBase)) { IsKeyframe = true, Duration = new MediaTime(1, video.TimeBase) };
                muxer.WritePacket(v);
                while (audioTicks * 30 < (frame + 1) * 48000L)
                {
                    using EncodedPacket a = new(2, data[(int)(audioTicks / 1024) & 1], new MediaTime(audioTicks, audio.TimeBase)) { IsKeyframe = true, Duration = new MediaTime(1024, audio.TimeBase) };
                    muxer.WritePacket(a);
                    audioTicks += 1024;
                }
            }
        }

        try
        {
            long before = GC.GetTotalMemory(forceFullCollection: true);
            using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
            long retained = GC.GetTotalMemory(forceFullCollection: true) - before;

            // Four bytes a sample for the sizes, runs for the rest: about 11 MB, well inside the default limits.
            Assert.True(retained < 24L << 20, $"the open file holds {retained} bytes");
            Assert.Equal(MediaTime.FromSeconds(Seconds).ToTimeSpan(), demuxer.Info.Duration!.Value.ToTimeSpan());
            demuxer.Seek(MediaTime.FromSeconds(Seconds - 1));
            using EncodedPacket last = demuxer.ReadPacket()!;
            Assert.Equal(TimeSpan.FromSeconds(Seconds - 1), last.Timestamp.ToTimeSpan());
        }
        finally
        {
            File.Delete(path);
        }
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

    [Fact]
    public void AWriteOnlyStreamGetsTheFileWithMemoryUnderOneFragmentPlusAMegabyte()
    {
        // 30 frames a second of 64 KB each with a keyframe a second: two-second fragments of about 3.9 MB.
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 30), Width = 16, Height = 16 };
        const int Frame = 64 << 10, Frames = 900;
        long fragment = 60L * Frame;
        CountingStream output = new();
        byte[] data = new byte[Frame];
        EncodedPacket[] packets = [.. Enumerable.Range(0, 30).Select(i => new EncodedPacket(1, data, default) { IsKeyframe = i == 0 })];
        long baseline = GC.GetTotalMemory(forceFullCollection: true), peak = 0, allocated = 0;
        using (IMuxer muxer = Mp4Format.Instance.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
        {
            for (int i = 0; i < Frames; i++)
            {
                EncodedPacket packet = packets[i % 30];
                packet.Timestamp = new MediaTime(i, track.TimeBase);
                long start = GC.GetAllocatedBytesForCurrentThread();
                muxer.WritePacket(packet);
                if (i >= 300)
                {
                    allocated += GC.GetAllocatedBytesForCurrentThread() - start;
                }

                if (i % 60 == 59)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                }
            }
        }

        Assert.True(peak < fragment + (1 << 20), $"held {peak} bytes; a fragment is {fragment}");
        Assert.True(allocated < 20 * 1024, $"{allocated} bytes allocated over 600 packets and 10 fragments in steady state");
        Assert.True(output.Written > Frames * (long)Frame);
        foreach (EncodedPacket packet in packets)
        {
            packet.Dispose();
        }
    }

    /// <summary>A write-only stream that keeps nothing, counting the bytes.</summary>
    private sealed class CountingStream : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written += count;

        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    }
}
