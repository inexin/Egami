using Egami.Colors;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoHevc
{
    [Fact]
    public void Register()
    {
        using SampleFiles files = new();

        #region register
        // Opt in to HEVC once: WithHevc() adds the decoder and the raw .h265 reader to a context. MediaContext.SetDefault makes it
        // the default for every call; WithH264() and WithHevc() go together.
        MediaContext context = MediaContext.Default.WithH264().WithHevc();
        using VideoReader reader = VideoReader.Open("hevc.mp4", context: context);
        Console.WriteLine($"{reader.Track.CodecId} {reader.Track.Width}x{reader.Track.Height}");
        #endregion

        Assert.Equal(CodecIds.Hevc, reader.Track.CodecId);
        Assert.IsType<HevcCodec>(context.FindCodec(reader.Track));
    }

    [Fact]
    public void Decode()
    {
        using SampleFiles files = new();
        MediaContext context = MediaContext.Default.WithHevc();

        #region decode
        // Frames come out in display order, B-frames included; an HDR10 stream gives its mastering display and light levels on
        // every frame. Here 10-bit frames are converted to 16-bit RGB.
        using VideoReader reader = VideoReader.Open("hdr10.h265", context: context);
        int count = 0;
        foreach (VideoFrame<Rgb16> frame in reader.ReadFrames<Rgb16>())
        {
            using (frame)
            {
                if (count++ == 0)
                {
                    Console.WriteLine($"{frame.Color}, up to {frame.Hdr?.MaxContentLightLevel} nits");
                    frame.Raster.Save("first.png");
                }
            }
        }
        #endregion

        Assert.Equal(6, count);
        Assert.True(File.Exists("first.png"));
    }

    [Fact]
    public void Raw()
    {
        using SampleFiles files = new();

        #region raw
        // A raw .h265 stream has no timestamps: frames are timed by the stream's own frame rate, or 25 a second, unless you
        // register the format with a rate of your own.
        MediaContext context = MediaContext.Default.WithHevc().WithContainer(new HevcFormat { FrameRate = new Rational(30, 1) });
        using VideoReader reader = VideoReader.Open("camera.h265", context: context);
        Console.WriteLine($"{reader.Track.Width}x{reader.Track.Height} at {reader.Track.FrameRate} fps");

        // Remuxing keeps the stream as it is, here into MP4.
        MediaFile.Remux("camera.h265", "camera.mp4", context: context);
        #endregion

        Assert.Equal((170, 100), (reader.Track.Width, reader.Track.Height));
        Assert.Equal(new Rational(30, 1), reader.Track.FrameRate);
        Assert.Equal(8, reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void Encode()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region encode
        // HEVC in MP4 (an hvc1 track): Main for 8-bit frames, Main 10 for 16-bit planes, the level picked from the frame size and rate.
        VideoWriterOptions options = new() { Codec = HevcCodec.Instance, FrameRate = new Rational(30, 1), Quality = 60 };
        using (VideoWriter writer = VideoWriter.Create("duck.mp4", options))
        {
            for (int i = 0; i < 10; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // Expert settings: Main 10 at level 4.1, without adaptive quantization (to measure PSNR).
        VideoWriterOptions main10 = options with { Hevc = new HevcEncoderOptions { Profile = HevcProfile.Main10, Level = 41, AdaptiveQuantization = false } };
        #endregion

        using VideoReader reader = VideoReader.Open("duck.mp4", context: MediaContext.Default.WithHevc());
        Assert.Equal(CodecIds.Hevc, reader.Track.CodecId);
        Assert.Equal(10, reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
        Assert.Equal(HevcProfile.Main10, main10.Hevc!.Profile);
    }

    [Fact]
    public void FromH264()
    {
        using SampleFiles files = new();

        #region cookbook-hevc-from-h264
        // Re-encode an H.264 video as HEVC at the same frame rate: the decoder's own planes go straight to the encoder.
        MediaContext context = MediaContext.Default.WithH264().WithHevc();
        using (VideoReader reader = VideoReader.Open("h264.mp4", context: context))
        {
            VideoWriterOptions options = new() { Codec = HevcCodec.Instance, FrameRate = reader.Track.FrameRate ?? new Rational(30, 1), Quality = 55 };
            using VideoWriter writer = VideoWriter.Create("hevc.mp4", options, context);
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    writer.WriteFrame(frame);
                }
            }
        }
        #endregion

        Assert.Equal(CodecIds.Hevc, MediaFile.Probe("hevc.mp4").Tracks.OfType<VideoTrackInfo>().First().CodecId);
    }

    [Fact]
    public void Hdr10()
    {
        using SampleFiles files = new();

        #region cookbook-hevc-hdr10
        // HDR10: 10-bit BT.2020 planes with the PQ transfer code Main 10, and the frame's HDR metadata goes into the stream as
        // mastering display and content light level SEI (and into MP4's mdcv and clli boxes).
        ColorCodePoints pq = new(9, 16, 9, false);
        HdrMetadata hdr = new() { MasteringMaxLuminance = 1000, MasteringMinLuminance = 0.005, MaxContentLightLevel = 1000, MaxFrameAverageLightLevel = 400 };
        VideoWriterOptions options = new() { Codec = HevcCodec.Instance, Quality = 70 };
        using (VideoWriter writer = VideoWriter.Create("hdr10.mp4", options))
        {
            for (int i = 0; i < 5; i++)
            {
                YuvRaster<ushort> planes = YuvRaster.Create(64, 48, ChromaFormat.Yuv420, 10, new YuvColor(YuvMatrix.Bt2020Ncl, YuvRange.Limited, ChromaSiting.Left));
                using YuvVideoFrame frame = new(planes, new MediaTime(i, new Rational(1, 30)), color: pq) { Hdr = hdr };
                writer.WriteFrame(frame);
            }
        }
        #endregion

        using VideoReader reader = VideoReader.Open("hdr10.mp4", context: MediaContext.Default.WithHevc());
        using YuvVideoFrame first = reader.ReadFrame()!;
        Assert.Equal(10, first.Planes.BitDepth);
        Assert.Equal(1000, first.Hdr!.MaxContentLightLevel);
        Assert.Equal(1000, MediaFile.Probe("hdr10.mp4").Tracks.OfType<VideoTrackInfo>().First().Hdr?.MaxContentLightLevel);
    }

    [Fact]
    public void Live()
    {
        using SampleFiles files = new();

        #region cookbook-hevc-live
        // A raw .h265 stream for a live sender: Annex B packets (start codes, the parameter sets with every keyframe) at a
        // constant 500 kbit/s, no B-frames and no lookahead, so each packet leaves as soon as its frame comes in.
        VideoEncoderOptions options = new()
        {
            Width = 320,
            Height = 240,
            FrameRate = new Rational(30, 1),
            LatencyMode = LatencyMode.Realtime,
            RateControl = RateControlMode.ConstantBitrate,
            Bitrate = 500_000,
            Effort = 2,
            Hevc = new HevcEncoderOptions { AnnexB = true },
        };
        using (FileStream file = File.Create("live.h265"))
        using (IVideoEncoder encoder = HevcCodec.Instance.CreateEncoder(options, MediaContext.Default))
        {
            for (int i = 0; i < 30; i++)
            {
                using VideoFrame<Rgb8> frame = new(Raster.Create(320, 240, new Rgb8((byte)(i * 8), 90, 160)), new MediaTime(i, new Rational(1, 30)));
                encoder.Send(frame);
                while (encoder.TryReceive(out EncodedPacket? packet))
                {
                    using (packet)
                    {
                        file.Write(packet.Data.Span);
                    }
                }
            }

            encoder.Flush();
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                using (packet)
                {
                    file.Write(packet.Data.Span);
                }
            }
        }
        #endregion

        using VideoReader reader = VideoReader.Open("live.h265", context: MediaContext.Default.WithHevc());
        Assert.Equal(30, reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }
}
