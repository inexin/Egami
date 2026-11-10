using Egami.Colors;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoH264
{
    [Fact]
    public void Register()
    {
        using SampleFiles files = new();

        #region register
        // Opt in to H.264 once: WithH264() adds the decoder and the raw .h264 reader to a context. MediaContext.SetDefault makes it
        // the default for every call.
        MediaContext context = MediaContext.Default.WithH264();
        using VideoReader reader = VideoReader.Open("h264.mp4", context: context);
        Console.WriteLine($"{reader.Track.CodecId} {reader.Track.Width}x{reader.Track.Height}");
        #endregion

        Assert.Equal(CodecIds.Avc, reader.Track.CodecId);
        Assert.IsType<H264Codec>(context.FindCodec(reader.Track));
    }

    [Fact]
    public void Decode()
    {
        using SampleFiles files = new();
        MediaContext context = MediaContext.Default.WithH264();

        #region decode
        // Frames come out in display order, B-frames included; a broken slice is concealed from the frame before.
        using VideoReader reader = VideoReader.Open("h264.mp4", new VideoReaderOptions { Errors = DecodeErrorMode.Conceal }, context);
        int count = 0;
        foreach (VideoFrame<Rgb8> frame in reader.ReadFrames<Rgb8>())
        {
            using (frame)
            {
                if (count++ == 0)
                {
                    frame.Raster.Save("first.png");
                }
            }
        }
        #endregion

        Assert.Equal(14, count); // The MP4's edit list starts two frames in.
        Assert.True(File.Exists("first.png"));
    }

    [Fact]
    public void Fields()
    {
        using SampleFiles files = new();
        MediaContext context = MediaContext.Default.WithH264();
        List<FieldOrder> orders = [];

        #region fields
        // An interlaced frame comes out with its two fields woven; FieldOrder says which was filmed first.
        using VideoReader reader = VideoReader.Open("interlaced.h264", context: context);
        foreach (VideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Console.WriteLine($"{frame.Timestamp}: {frame.FieldOrder}, {frame.RepeatFields} repeated fields");
                orders.Add(frame.FieldOrder);
            }
        }
        #endregion

        Assert.Equal(8, orders.Count);
        Assert.All(orders, o => Assert.Equal(FieldOrder.BottomFieldFirst, o));
    }

    [Fact]
    public void Raw()
    {
        using SampleFiles files = new();

        #region raw
        // A raw .h264 stream has no timestamps: frames are timed by the stream's own frame rate, or 25 a second, unless you
        // register the format with a rate of your own.
        MediaContext context = MediaContext.Default.WithH264().WithContainer(new H264Format { FrameRate = new Rational(30, 1) });
        using VideoReader reader = VideoReader.Open("camera.h264", context: context);
        Console.WriteLine($"{reader.Track.Width}x{reader.Track.Height} at {reader.Track.FrameRate} fps");

        // Remuxing keeps the stream as it is, here into MP4.
        MediaFile.Remux("camera.h264", "camera.mp4", context: context);
        #endregion

        Assert.Equal((170, 100), (reader.Track.Width, reader.Track.Height));
        Assert.Equal(new Rational(30, 1), reader.Track.FrameRate);
        Assert.Equal(10, reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void Encode()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region encode
        // H.264 in MP4: High profile with CABAC by default, the level picked from the frame size and rate.
        VideoWriterOptions options = new() { Codec = H264Codec.Instance, FrameRate = new Rational(30, 1), Quality = 60 };
        using (VideoWriter writer = VideoWriter.Create("duck.mp4", options))
        {
            for (int i = 0; i < 10; i++)
            {
                using Raster<Rgb8> frame = duck.Derive(ops => ops.Crop(new Rect(i * 4, i * 2, 320, 240)));
                writer.WriteFrame(frame);
            }
        }

        // Expert settings: Constrained Baseline (CAVLC) at level 3.0 for the simplest players.
        VideoWriterOptions baseline = options with { H264 = new H264EncoderOptions { Profile = H264Profile.ConstrainedBaseline, Level = 30, Entropy = H264EntropyCoder.Cavlc } };
        #endregion

        using VideoReader reader = VideoReader.Open("duck.mp4", context: MediaContext.Default.WithH264());
        Assert.Equal(CodecIds.Avc, reader.Track.CodecId);
        Assert.Equal(10, reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
        Assert.Equal(H264Profile.ConstrainedBaseline, baseline.H264!.Profile);
    }
}
