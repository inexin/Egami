using Egami.Operations;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Codecs;
using Egami.Video.Editing;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Editing
{
    /// <summary>Writes small test clips with sound: intro.avi (160x90 at 25 fps), talk.avi (320x240 at 30 fps) and green.avi.</summary>
    private static void MakeClips()
    {
        Make("intro.avi", new TestVideoFormat { Width = 160, Height = 90, FrameRate = new Rational(25, 1) }, 440);
        Make("talk.avi", new TestVideoFormat { Width = 320, Height = 240, FrameRate = new Rational(30, 1) }, 220);
        Make("outro.avi", new TestVideoFormat { Width = 160, Height = 90, FrameRate = new Rational(25, 1) }, 330);

        static void Make(string name, TestVideoFormat format, double tone)
        {
            using VideoWriter writer = VideoWriter.Create(name, new VideoWriterOptions
            {
                Codec = MotionJpegCodec.Instance,
                FrameRate = format.FrameRate,
                AudioCodec = AudioCodecs.Pcm,
                AudioSampleRate = 48000,
                AudioLayout = ChannelLayout.Stereo,
            });
            foreach (YuvVideoFrame frame in TestSources.Pattern(format, MediaTime.FromSeconds(2)))
            {
                using (frame)
                {
                    writer.WriteFrame(frame);
                }
            }

            foreach (AudioFrame audio in TestSources.Sine(TestAudioFormat.Default, MediaTime.FromSeconds(2), tone))
            {
                using (audio)
                {
                    writer.WriteAudio(audio);
                }
            }

            writer.Finish();
        }
    }

    [Fact]
    public void Join()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-join
        // Three clips of different sizes and rates, fitted to one 320x180 canvas at 25 fps, with fades at both ends.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)) { Fit = ClipFit.Contain });
        edit.Track
            .Add(Clip.Open("intro.avi").FadeIn(MediaTime.FromSeconds(0.5)))
            .Add(Clip.Open("talk.avi").Trim(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(1.5)))
            .Add(Clip.Open("outro.avi").FadeOut(MediaTime.FromSeconds(0.5)));
        EditPlan plan = edit.Render("joined.mkv", new EditRenderOptions { Writer = new VideoWriterOptions { Codec = MotionJpegCodec.Instance, Quality = 80 } });
        Console.WriteLine($"{plan.FrameCount} frames, {plan.Duration.Seconds} s");
        #endregion

        Assert.Equal(125, plan.FrameCount);
        Assert.Equal(2, MediaFile.Probe("joined.mkv").Tracks.Count);
    }

    [Fact]
    public void Transitions()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-transitions
        // A dissolve, then a soft wipe with eased progress: each overlaps the two clips by its length, sound crossfading too.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi"))
            .Add(Clip.Open("talk.avi"), Transition.Dissolve(MediaTime.FromSeconds(0.5)))
            .Add(Clip.Open("outro.avi"), Transition.Wipe(TransitionDirection.Left, MediaTime.FromSeconds(0.5)) with { Softness = 0.1, Easing = Easing.EaseInOut });
        EditPlan plan = edit.Render("transitions.mkv");
        #endregion

        Assert.Equal(124, plan.FrameCount);
    }

    [Fact]
    public void CustomTransition()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-custom-transition
        // An app's own transition: the incoming clip appears from the top down.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi")).Add(Clip.Open("outro.avi"), Transition.Custom(new Blinds(), MediaTime.FromSeconds(1)));
        edit.Render("blinds.mkv");
        #endregion

        Assert.True(File.Exists("blinds.mkv"));
    }

    #region edit-blinds
    private sealed class Blinds : ITransition
    {
        public void Render(Raster<Rgba8> outgoing, Raster<Rgba8> incoming, double progress, Raster<Rgba8> target)
        {
            int rows = (int)Math.Round(progress * target.Height);
            for (int y = 0; y < target.Height; y++)
            {
                (y < rows ? incoming : outgoing).Row(y).CopyTo(target.Row(y));
            }
        }
    }
    #endregion

    [Fact]
    public void Layers()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-layers
        // Picture-in-picture: talk.avi in the bottom-right corner from 0.5 s to 1.5 s, sliding in from the right as it fades in.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi"));
        edit.Layers.Add(Layer.Of(Clip.Open("talk.avi").Muted())
            .Place(Anchor.BottomRight, margin: 8, scale: 0.3)
            .Between(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(1.5))
            .FadeIn(MediaTime.FromSeconds(0.25))
            .Animate(LayerProperty.X, new Keyframe(MediaTime.Zero, 60, Easing.EaseOut), new Keyframe(MediaTime.FromSeconds(0.25), 0)));
        edit.Render("pip.mkv");
        #endregion

        Assert.True(File.Exists("pip.mkv"));
    }

    [Fact]
    public void ChromaKeys()
    {
        using SampleFiles files = new();
        MakeClips();
        using (VideoWriter writer = VideoWriter.Create("presenter-green.avi", new VideoWriterOptions { Codec = MotionJpegCodec.Instance, FrameRate = new Rational(25, 1) }))
        {
            foreach (YuvVideoFrame frame in TestSources.Solid(new TestVideoFormat { Width = 160, Height = 90, FrameRate = new Rational(25, 1) }, MediaTime.FromSeconds(1), Rgba8.FromHex("#00b140")))
            {
                using (frame)
                {
                    writer.WriteFrame(frame);
                }
            }

            writer.Finish();
        }

        #region edit-chroma-key
        // A green-screen clip over another: the key takes the studio green out and despill cleans its edges.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi"));
        edit.Layers.Add(Layer.Of(Clip.Open("presenter-green.avi"))
            .Key(new ChromaKey(Rgba8.FromHex("#00b140")) { Similarity = 0.12f, Blend = 0.06f, Despill = 0.6f })
            .Place(new Rect(160, 90, 160, 90)));
        edit.Render("keyed.mkv");
        #endregion

        Assert.True(File.Exists("keyed.mkv"));
    }

    [Fact]
    public void Stacks()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-stack
        // Two clips side by side with a 4-pixel gap, each scaled to the first one's height; a stack is a clip like any other.
        VideoEdit edit = new();
        edit.Track.Add(Stack.Horizontal([Clip.Open("intro.avi"), Clip.Open("talk.avi")], new StackOptions { Gap = 4, MixAudio = true }));
        EditPlan plan = edit.Render("side-by-side.mkv");
        Console.WriteLine($"{plan.Canvas.Width}x{plan.Canvas.Height}");
        #endregion

        Assert.Equal(160 + 4 + 120, plan.Canvas.Width);
    }

    [Fact]
    public void Effects()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-effects
        // Blur the whole edit from 0.5 s to 1 s, and darken one clip with an app's own effect that knows the time.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi").WithEffect(EditEffect.Of(new Darken())));
        edit.Effects.Add(EditEffect.Apply(ops => ops.Blur(4), "blur 4").Between(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(1)));
        edit.Render("effects.mkv");
        #endregion

        Assert.True(File.Exists("effects.mkv"));
    }

    #region edit-darken
    private sealed class Darken : IFrameEffect
    {
        public void Apply(Raster<Rgba8> frame, FrameEffectContext context)
        {
            float keep = 1 - (0.5f * (float)context.Progress);
            for (int y = 0; y < frame.Height; y++)
            {
                foreach (ref Rgba8 p in frame.Row(y))
                {
                    p = new Rgba8((byte)(p.R * keep), (byte)(p.G * keep), (byte)(p.B * keep), p.A);
                }
            }
        }
    }
    #endregion

    [Fact]
    public void Timing()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-timing
        // Twice as fast with the pitch kept, then backwards, then twice in a row with a frame frozen for half a second.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track
            .Add(Clip.Open("talk.avi").Speed(2))
            .Add(Clip.Open("intro.avi").Reverse())
            .Add(Clip.Open("outro.avi").Trim(MediaTime.Zero, MediaTime.FromSeconds(1)).Loop(2).Freeze(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(0.5)).Hold(MediaTime.FromSeconds(1)));
        EditPlan plan = edit.Render("timing.mkv");
        #endregion

        Assert.Equal(25 + 50 + 50 + 13 + 25, plan.FrameCount);
    }

    [Fact]
    public void Stills()
    {
        using SampleFiles files = new();

        #region edit-stills
        // Photos as a slideshow with fades and a slow pan and zoom, then a title an app draws, on black.
        VideoEdit show = Slideshow.Create(["duck.png", "egret.png"], new SlideshowOptions
        {
            Each = MediaTime.FromSeconds(3),
            PanZoom = true,
            Canvas = new EditCanvas(320, 180, new Rational(25, 1)) { Fit = ClipFit.Cover },
        });
        show.Track.Add(Clip.Color(Rgba8.Black, MediaTime.FromSeconds(0.5)), Transition.Fade(MediaTime.FromSeconds(0.5)));
        show.Track.Add(Clip.FromSource(new Title()));
        show.Track.Add(Clip.Still("duck.png", MediaTime.FromSeconds(2))
            .PanZoom(new System.Drawing.RectangleF(0, 0, 1, 1), new System.Drawing.RectangleF(0.3f, 0.3f, 0.4f, 0.4f), Easing.EaseInOut));
        show.Render("slideshow.mkv");
        #endregion

        Assert.True(File.Exists("slideshow.mkv"));
    }

    #region edit-title
    private sealed class Title : IFrameSource
    {
        public Size Size => new(320, 180);

        public MediaTime Duration => MediaTime.FromSeconds(1);

        public void Render(MediaTime time, Raster<Rgba8> target)
        {
            // A bar that grows across the frame: draw anything here, such as text with Egami.Drawing.
            int width = (int)(target.Width * time.Seconds / Duration.Seconds);
            for (int y = 0; y < target.Height; y++)
            {
                Span<Rgba8> row = target.Row(y);
                row.Fill(Rgba8.Black);
                if (y is > 80 and < 100)
                {
                    row[..width].Fill(Rgba8.White);
                }
            }
        }
    }
    #endregion

    [Fact]
    public void ReadingFrames()
    {
        using SampleFiles files = new();
        MakeClips();
        int count = 0;

        #region edit-read-frames
        // The rendered pictures, for a preview: each is a new frame with its time, to dispose.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi")).Add(Clip.Open("outro.avi"), Transition.Dissolve(MediaTime.FromSeconds(0.5)));
        foreach (VideoFrame<Rgba8> frame in edit.ReadFrames())
        {
            using (frame)
            {
                count++; // show frame.Raster, or encode it
            }
        }
        #endregion

        Assert.Equal(87, count);
    }

    [Fact]
    public void Planning()
    {
        using SampleFiles files = new();
        MakeClips();

        #region edit-plan
        // Lay out the timeline before rendering, and save the edit as the JSON file 'egami-video edit' runs.
        VideoEdit edit = new(new EditCanvas(320, 180, new Rational(25, 1)));
        edit.Track.Add(Clip.Open("intro.avi")).Add(Clip.Open("talk.avi"), Transition.Fade(MediaTime.FromSeconds(1)));
        EditPlan plan = edit.Plan();
        foreach (PlannedClip clip in plan.Clips)
        {
            Console.WriteLine($"clip {clip.Index}: frames {clip.StartFrame} to {clip.StartFrame + clip.FrameCount - 1}, {clip.SourceSize.Width}x{clip.SourceSize.Height}");
        }

        foreach (string warning in plan.Warnings)
        {
            Console.WriteLine(warning);
        }

        File.WriteAllText("edit.json", edit.ToJson());
        VideoEdit again = VideoEdit.FromJson(File.ReadAllText("edit.json"));
        #endregion

        Assert.Equal(75, plan.FrameCount);
        Assert.Equal(2, again.Track.Count);
    }
}
