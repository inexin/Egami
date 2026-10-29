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
}
