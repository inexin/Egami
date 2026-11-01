using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Editing;
using Egami.Video.Filters;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoFilters
{
    [Fact]
    public void Deinterlace()
    {
        using SampleFiles files = new();
        int written = 0;

        #region deinterlace
        // Deinterlace a file: interlaced frames come out progressive, one per frame in, and progressive frames pass unchanged.
        // The track gives the field order for frames that do not carry their own.
        using VideoReader reader = VideoReader.Open("movie.webm");
        using Deinterlacer deinterlacer = new(reader.Track, new DeinterlacerOptions
        {
            Mode = DeinterlaceMode.Adaptive,      // or Bob: from each field alone
            Rate = DeinterlaceRate.Frame,         // or Field: one frame per field, 50i becomes 50p
            Frames = DeinterlaceFrames.All,       // also frames the decoder does not flag as interlaced
        });
        VideoWriterOptions options = new() { Codec = VideoCodecs.Ffv1, FrameRate = reader.Track.FrameRate ?? new Rational(25, 1) };
        using (VideoWriter writer = VideoWriter.Create("progressive.mkv", options))
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    deinterlacer.Send(frame);
                }

                while (deinterlacer.TryReceive(out YuvVideoFrame? output))
                {
                    using (output)
                    {
                        writer.WriteFrame(output);
                        written++;
                    }
                }
            }

            // The deinterlacer holds one frame back to look at the next; Flush hands it out.
            deinterlacer.Flush();
            while (deinterlacer.TryReceive(out YuvVideoFrame? output))
            {
                using (output)
                {
                    writer.WriteFrame(output);
                    written++;
                }
            }
        }
        #endregion

        using VideoReader check = VideoReader.Open("movie.webm");
        Assert.Equal(check.ReadFrames().Count(f => { f.Dispose(); return true; }), written);
    }

    [Fact]
    public void EditDeinterlace()
    {
        using SampleFiles files = new();

        #region edit-deinterlace
        // Deinterlace a broadcast clip as the edit decodes it; one frame per field fills a 50 fps canvas with every field.
        VideoEdit edit = new(new EditCanvas(320, 240, new Rational(50, 1)));
        edit.Track.Add(Clip.Open("movie.webm").Deinterlace(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }));
        edit.Render("smooth.mkv");
        #endregion

        Assert.True(File.Exists("smooth.mkv"));
    }
}
