using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoEncode
{
    [Fact]
    public void Vp8()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-encode-vp8
        // VP8 in WebM at constant quality: Quality 0 to 100 (default 60) and Effort 0 to 10 (default 4).
        VideoWriterOptions options = new() { Codec = VideoCodecs.Vp8, FrameRate = new Rational(30, 1), Quality = 70, Effort = 4 };
        using (VideoWriter writer = VideoWriter.Create("duck.webm", options))
        {
            for (int i = 0; i < 30; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // Expert settings: no hidden alternate reference frames, and quantizer limits.
        VideoWriterOptions tuned = options with { Vp8 = new Vp8EncoderOptions { AlternateReference = false, MinQuantizer = 10, MaxQuantizer = 90 } };
        #endregion

        Assert.Equal(CodecIds.Vp8, MediaFile.Probe("duck.webm").Tracks[0].CodecId);
        Assert.False(tuned.Vp8!.AlternateReference);
    }

    [Fact]
    public void Bitrate()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-encode-bitrate
        // An average of 1 Mbit/s, the bits spread by how hard each frame is; ConstantBitrate keeps a decoder buffer from running dry.
        VideoWriterOptions options = new()
        {
            Codec = VideoCodecs.Vp8,
            RateControl = RateControlMode.Bitrate,
            Bitrate = 1_000_000,
        };
        using (VideoWriter writer = VideoWriter.Create("duck.mkv", options))
        {
            for (int i = 0; i < 30; i++)
            {
                writer.WriteFrame(duck);
            }
        }
        #endregion

        Assert.True(new FileInfo("duck.mkv").Length > 0);
    }

    [Fact]
    public void TwoPass()
    {
        using SampleFiles files = new();
        using (VideoWriter source = VideoWriter.Create("clip.ivf", new VideoWriterOptions { Codec = VideoCodecs.Vp8, Effort = 0 }))
        {
            using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");
            for (int i = 0; i < 10; i++)
            {
                source.WriteFrame(duck);
            }
        }

        #region video-encode-two-pass
        // Pass one reads the clip and learns how hard each frame is; save it to run the passes apart.
        FirstPassStats stats = VideoWriter.AnalyzeFirstPass("clip.ivf");
        stats.Save("clip.stats");

        // Pass two spreads 500 kbit/s over the whole clip by what pass one learned.
        VideoWriterOptions options = new()
        {
            Codec = VideoCodecs.Vp8,
            RateControl = RateControlMode.Bitrate,
            Bitrate = 500_000,
            FirstPassStats = FirstPassStats.Load("clip.stats"),
        };
        using (VideoReader reader = VideoReader.Open("clip.ivf"))
        using (VideoWriter writer = VideoWriter.Create("clip.webm", options))
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    writer.WriteFrame(frame);
                }
            }
        }
        #endregion

        Assert.Equal(10, stats.FrameCount);
    }

    [Fact]
    public void Realtime()
    {
        using MemoryStream stream = new();

        #region video-encode-realtime
        // Live: no lookahead, so each packet comes out as soon as its frame goes in, at a constant bitrate.
        VideoEncoderOptions options = new()
        {
            Width = 320,
            Height = 240,
            LatencyMode = LatencyMode.Realtime,
            RateControl = RateControlMode.ConstantBitrate,
            Bitrate = 300_000,
        };
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(options, MediaContext.Default);
        using IMuxer muxer = MediaFile.CreateMuxer(stream, "webm", [encoder.Track], leaveOpen: true);
        for (int i = 0; i < 60; i++)
        {
            using VideoFrame<Rgb8> frame = new(Raster.Create(320, 240, new Rgb8((byte)i, 90, 160)), new MediaTime(i, new Rational(1, 30)));

            // A viewer joined: start a keyframe so they can decode from here.
            encoder.Send(frame, new EncodeFrameOptions { ForceKeyframe = i == 30 });
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        encoder.Flush();
        muxer.Finish();
        #endregion

        Assert.True(stream.Length > 0);
    }
}
