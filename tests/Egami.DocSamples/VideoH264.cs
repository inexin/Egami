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
}
