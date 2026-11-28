using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Editing;
using Egami.Video.Subtitles;
using Egami.Video.Subtitles.Rendering;

namespace Egami.DocSamples;

[Collection("Samples")]
public class SubtitleBurnInSamples
{
    private const string Srt = "1\r\n00:00:00,000 --> 00:00:02,000\r\n<i>Where were you?</i>\r\n\r\n2\r\n00:00:02,000 --> 00:00:04,000\r\nOut.\r\n";

    private static string Fonts => Path.Combine(AppContext.BaseDirectory, "fonts");

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        File.WriteAllText("movie.srt", Srt);
        string fonts = Fonts;
        var warnings = new List<MediaWarning>();

        #region burnin-options
        // Fonts come from the folders first, then the system's. A font a style names that none has is replaced by the default one,
        // with a warning; ForceStyle changes every style, as FFmpeg's force_style does.
        SubtitleRenderOptions options = new()
        {
            FontFolders = [fonts],
            UseSystemFonts = false,
            ForceStyle = style => style with { FontName = "Noto Sans", FontSize = 20 },
        };
        MediaContext context = MediaContext.Default with { Warnings = warnings.Add };
        using SubtitleRenderer renderer = new(SubtitleDocument.Load("movie.srt"), options, context);
        #endregion

        using Raster<Rgba8> frame = Raster.Create(320, 180, new Rgba8(0, 0, 0, 255));
        renderer.Draw(frame, MediaTime.FromSeconds(1));
        Assert.Empty(warnings);
        Assert.Contains(Enumerable.Range(0, frame.Height), y => frame.Row(y).ToArray().Any(p => p.R > 200));
    }

    [Fact]
    public void Frames()
    {
        using SampleFiles files = new();
        File.WriteAllText("movie.srt", Srt);
        string fonts = Fonts;
        int written = 0;

        #region burnin-frames
        // Burn subtitles into a file's frames: each frame is drawn into in its own matrix, range and bit depth.
        using VideoReader reader = VideoReader.Open("movie.webm");
        using SubtitleRenderer renderer = new(SubtitleDocument.Load("movie.srt"), new SubtitleRenderOptions { FontFolders = [fonts] });
        VideoWriterOptions options = new() { Codec = VideoCodecs.Ffv1, FrameRate = reader.Track.FrameRate ?? new Rational(25, 1) };
        using (VideoWriter writer = VideoWriter.Create("subtitled.mkv", options))
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    renderer.Draw(frame);
                    writer.WriteFrame(frame);
                    written++;
                }
            }
        }
        #endregion

        Assert.True(written > 0);
    }

    [Fact]
    public void RgbaRaster()
    {
        using SampleFiles files = new();
        string fonts = Fonts;

        #region burnin-raster
        // Draw onto an RGBA raster at a time on the subtitles' clock: a preview, a thumbnail, a frame an app made itself.
        SubtitleDocument subtitles = SubtitleDocument.Parse("""
            [Script Info]
            PlayResX: 640
            PlayResY: 360

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Sign,Noto Sans,28,&H0000FFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,8,20,20,20,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:05.00,Sign,,0,0,0,,THE {\i1}NORTH{\i0} GATE\NOpen at dawn
            """);
        using SubtitleRenderer renderer = new(subtitles, new SubtitleRenderOptions { FontFolders = [fonts] });
        using Raster<Rgba8> still = Raster.Load<Rgba8>("duck.png");
        renderer.Draw(still, MediaTime.FromSeconds(1));
        still.Save("sign.png");
        #endregion

        Assert.True(File.Exists("sign.png"));
    }

    [Fact]
    public void Edit()
    {
        using SampleFiles files = new();
        File.WriteAllText("movie.srt", Srt);
        string fonts = Fonts;

        #region burnin-edit
        // Subtitles over a whole edit: the effect draws each frame at its time on the edit's clock.
        using SubtitleRenderer renderer = new(SubtitleDocument.Load("movie.srt"), new SubtitleRenderOptions { FontFolders = [fonts] });
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("movie.webm"));
        edit.Effects.Add(EditEffect.Of(renderer.AsEffect()));
        edit.Render("subtitled.mkv");
        #endregion

        Assert.True(File.Exists("subtitled.mkv"));
    }

    [Fact]
    public void Filter()
    {
        using SampleFiles files = new();
        File.WriteAllText("movie.srt", Srt);
        string fonts = Fonts;
        int written = 0;

        #region burnin-filter
        // As a video filter, the burn-in leaves the frames sent to it alone and hands out subtitled copies.
        using VideoReader reader = VideoReader.Open("movie.webm");
        using SubtitleBurnIn burnIn = new(SubtitleDocument.Load("movie.srt"), new SubtitleRenderOptions { FontFolders = [fonts] });
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                burnIn.Send(frame);
            }

            while (burnIn.TryReceive(out YuvVideoFrame? subtitled))
            {
                using (subtitled)
                {
                    written++; // encode, show or analyse the subtitled frame
                }
            }
        }
        #endregion

        Assert.True(written > 0);
    }
}
