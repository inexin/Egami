using Egami.Colors;
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
    public void Vp9()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-encode-vp9
        // VP9 in WebM at constant quality, on the same Quality and Effort scales as VP8.
        VideoWriterOptions options = new() { Codec = VideoCodecs.Vp9, FrameRate = new Rational(30, 1), Quality = 70, Effort = 2 };
        using (VideoWriter writer = VideoWriter.Create("duck-vp9.webm", options))
        {
            for (int i = 0; i < 10; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // Expert settings: two tile columns, adaptive quantization, quantizer limits on VP9's 0 to 255 scale.
        VideoWriterOptions tuned = options with
        {
            Vp9 = new Vp9EncoderOptions { TileColumns = 2, AdaptiveQuantization = true, MinQuantizer = 20, MaxQuantizer = 200 },
        };
        #endregion

        Assert.Equal(CodecIds.Vp9, MediaFile.Probe("duck-vp9.webm").Tracks[0].CodecId);
        Assert.Equal(2, tuned.Vp9!.TileColumns);
    }

    [Fact]
    public void Ffv1()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-encode-ffv1
        // FFV1 keeps every sample: RGB frames are coded as RGB and planes keep their layout and depth. The defaults are the
        // archival profile (version 3, every frame a keyframe, slice CRCs, the range coder with a custom table).
        VideoWriterOptions options = new() { Codec = VideoCodecs.Ffv1, FrameRate = new Rational(25, 1) };
        using (VideoWriter writer = VideoWriter.Create("master.mkv", options))
        {
            for (int i = 0; i < 5; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // Expert settings: more slices for more threads, Golomb-Rice for speed, a keyframe every 10 frames for smaller files.
        VideoWriterOptions fast = options with
        {
            KeyframeInterval = 10,
            Ffv1 = new Ffv1EncoderOptions { Slices = 16, Coder = Ffv1Coder.GolombRice, ContextModel = Ffv1ContextModel.Small },
        };

        // Check the archive without decoding it.
        bool intact = Ffv1Codec.Verify("master.mkv").IsIntact;
        #endregion

        Assert.True(intact);
        Assert.Equal(16, fast.Ffv1!.Slices);
        using VideoReader reader = VideoReader.Open("master.mkv");
        using YuvVideoFrame first = reader.ReadFrame()!;
        using Raster<Rgb8> back = first.ToRaster<Rgb8>();
        using Raster<Rgb8> expected = duck.Derive(ops => ops.Crop(new Rect(0, 0, 320, 240)));
        for (int y = 0; y < back.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(back.Row(y)));
        }
    }

    [Fact]
    public void Vp9Deep()
    {
        using SampleFiles files = new();

        #region video-encode-vp9-hdr
        // 10-bit planes code profile 2 (4:2:0) or 3 (4:2:2, 4:4:4); the frames' code points and HDR metadata go into the
        // container's Colour (WebM, Matroska) or colr, mdcv and clli boxes (MP4).
        ColorCodePoints pq = new(9, 16, 9, false);
        HdrMetadata hdr = new() { MasteringMaxLuminance = 1000, MasteringMinLuminance = 0.005, MaxContentLightLevel = 1000, MaxFrameAverageLightLevel = 400 };
        VideoWriterOptions options = new() { Codec = VideoCodecs.Vp9, Quality = 80, Effort = 1 };
        using (VideoWriter writer = VideoWriter.Create("hdr.mp4", options))
        {
            for (int i = 0; i < 5; i++)
            {
                YuvRaster<ushort> planes = YuvRaster.Create(64, 48, ChromaFormat.Yuv420, 10, new YuvColor(YuvMatrix.Bt2020Ncl, YuvRange.Limited, ChromaSiting.Center));
                using YuvVideoFrame frame = new(planes, new MediaTime(i, new Rational(1, 30)), color: pq) { Hdr = hdr };
                writer.WriteFrame(frame);
            }
        }
        #endregion

        Assert.Equal(10, MediaFile.Probe("hdr.mp4").Tracks.OfType<VideoTrackInfo>().First().BitDepth);
    }

    [Fact]
    public void Av1()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-encode-av1
        // AV1 in MP4 at constant quality, on the same Quality and Effort scales as VP8 and VP9. Each group's hidden alternate
        // reference, and a hidden frame halfway to it, are shown later with show_existing_frame.
        VideoWriterOptions options = new() { Codec = VideoCodecs.Av1, FrameRate = new Rational(30, 1), Quality = 70, Effort = 1 };
        using (VideoWriter writer = VideoWriter.Create("duck-av1.mp4", options))
        {
            for (int i = 0; i < 10; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // Expert settings: no pyramid, no adaptive quantization, quantizer limits on AV1's 0 to 255 base_q_idx scale.
        VideoWriterOptions tuned = options with
        {
            Av1 = new Av1EncoderOptions { Pyramid = false, AdaptiveQuantization = false, MinQuantizer = 20, MaxQuantizer = 200 },
        };
        #endregion

        Assert.Equal(CodecIds.Av1, MediaFile.Probe("duck-av1.mp4").Tracks[0].CodecId);
        Assert.False(tuned.Av1!.Pyramid);
    }

    [Fact]
    public void Av1Hdr()
    {
        using SampleFiles files = new();

        #region video-encode-av1-hdr
        // 10-bit 4:2:0 planes code the Main profile (4:4:4 High; 12 bits or 4:2:2 Professional). The frames' code points go into
        // the sequence header and the container, their HDR metadata into metadata OBUs on every keyframe and into the container
        // (Matroska's Colour, MP4's mdcv and clli).
        ColorCodePoints pq = new(9, 16, 9, false);
        HdrMetadata hdr = new() { MasteringMaxLuminance = 1000, MasteringMinLuminance = 0.005, MaxContentLightLevel = 1000, MaxFrameAverageLightLevel = 400 };
        VideoWriterOptions options = new() { Codec = VideoCodecs.Av1, Quality = 80, Effort = 1 };
        using (VideoWriter writer = VideoWriter.Create("hdr-av1.mkv", options))
        {
            for (int i = 0; i < 5; i++)
            {
                YuvRaster<ushort> planes = YuvRaster.Create(64, 48, ChromaFormat.Yuv420, 10, new YuvColor(YuvMatrix.Bt2020Ncl, YuvRange.Limited, ChromaSiting.TopLeft));
                using YuvVideoFrame frame = new(planes, new MediaTime(i, new Rational(1, 30)), color: pq) { Hdr = hdr };
                writer.WriteFrame(frame);
            }
        }
        #endregion

        VideoTrackInfo track = MediaFile.Probe("hdr-av1.mkv").Tracks.OfType<VideoTrackInfo>().First();
        Assert.Equal(10, track.BitDepth);
        Assert.Equal(1000, track.Hdr!.MaxContentLightLevel);
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
