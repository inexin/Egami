using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Editing;

namespace Egami.Video.Tests.Editing;

/// <summary>V9 stage B: speed, reverse, loop, hold, freeze, stills, colour and app clips, pan and zoom, and slideshows.</summary>
public class EditTimingTests
{
    private static readonly Rational Rate10 = new(10, 1);

    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private static List<int> Shown(VideoEdit edit) => [.. edit.RenderFrames().Select(f => EditClips.Read(f).Frame)];

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.25)]
    [InlineData(2)]
    [InlineData(10)]
    public void SpeedKeepsDurationsExact(double factor)
    {
        string a = EditClips.Make("speed", 32, 24, Rate10, 20, 40, tone: null);
        VideoEdit edit = new(new EditCanvas(32, 24, Rate10));
        edit.Track.Add(Clip.Open(a).Speed(factor));
        EditPlan plan = edit.Plan();
        Assert.Equal((long)Math.Round(20 / factor, MidpointRounding.AwayFromZero), plan.FrameCount);
        List<int> shown = Shown(edit);
        for (int n = 0; n < shown.Count; n++)
        {
            // Output frame n shows the latest source frame at or before (n + 0.5) / 10 * factor seconds, as FFmpeg's fps filter does.
            int expected = Math.Min(19, (int)Math.Floor((n + 0.5) * factor));
            Assert.Equal(expected, shown[n]);
        }
    }

    [Fact]
    public void ReverseLoopHoldAndFreeze()
    {
        string a = EditClips.Make("timing", 32, 24, Rate10, 10, 40, tone: null);
        EditCanvas canvas = new(32, 24, Rate10);

        VideoEdit reverse = new(canvas);
        reverse.Track.Add(Clip.Open(a).Reverse());
        Assert.Equal([9, 8, 7, 6, 5, 4, 3, 2, 1, 0], Shown(reverse));

        VideoEdit loop = new(canvas);
        loop.Track.Add(Clip.Open(a).Trim(S(0.2), S(0.5)).Loop(3));
        Assert.Equal([2, 3, 4, 2, 3, 4, 2, 3, 4], Shown(loop));

        VideoEdit hold = new(canvas);
        hold.Track.Add(Clip.Open(a).Hold(S(0.3)));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 9], Shown(hold));

        VideoEdit freeze = new(canvas);
        freeze.Track.Add(Clip.Open(a).Freeze(S(0.3), S(0.2)).Freeze(S(0.8), S(0.1)));
        Assert.Equal([0, 1, 2, 3, 3, 3, 4, 5, 6, 7, 8, 8, 9], Shown(freeze));

        VideoEdit both = new(canvas);
        both.Track.Add(Clip.Open(a).Reverse().Speed(2));
        Assert.Equal([9, 7, 5, 3, 1], Shown(both));
    }

    [Fact]
    public void ReverseHoldsOneKeyframeInterval()
    {
        // Motion JPEG frames are all keyframes, so a reversed clip holds one frame at a time.
        string a = EditClips.Make("reverse-long", 32, 24, Rate10, 30, 40, tone: null);
        VideoEdit edit = new(new EditCanvas(32, 24, Rate10));
        edit.Track.Add(Clip.Open(a).Reverse());
        Assert.Equal([.. Enumerable.Range(0, 30).Reverse()], Shown(edit));
    }

    [Fact]
    public void SoundFollowsSpeedReverseAndLoops()
    {
        string a = EditClips.Make("sound", 32, 24, Rate10, 10, 40, tone: 440);
        VideoEdit edit = new(new EditCanvas(32, 24, Rate10));
        edit.Track.Add(Clip.Open(a).Speed(2)).Add(Clip.Open(a).Reverse()).Add(Clip.Open(a).Loop(2).Freeze(S(0.5), S(0.5)));
        float[] sound = Render(edit, out long frames);
        Assert.Equal(5 + 10 + 25, frames);
        Assert.Equal(4 * 48000, sound.Length);

        // Tempo keeps the pitch at double speed; the frozen half second is silent.
        Assert.Equal(440, Frequency(sound.AsSpan(2400, 19200)), 440 * 0.02);
        Assert.Equal(440, Frequency(sound.AsSpan(24000 + 2400, 38400)), 440 * 0.02);
        Assert.InRange(sound.Skip(72000 + 24000 + 1200).Take(21600).Max(Math.Abs), 0, 1e-4f);
        Assert.InRange(sound.Skip(72000 + 48000 + 1200).Take(9600).Max(Math.Abs), 0.2f, 0.3f);
    }

    [Fact]
    public void StillsColoursAndAppFrames()
    {
        using Raster<Rgba8> image = Raster.Create(20, 10, new Rgba8(255, 0, 0, 255));
        VideoEdit edit = new(new EditCanvas(40, 20, Rate10));
        edit.Track.Add(Clip.Still(image, S(0.5))).Add(Clip.Color(new Rgba8(0, 0, 255, 255), S(0.3)), Transition.Fade(S(0.1))).Add(Clip.FromSource(new Counter()));
        List<Rgba8> pixels = [.. edit.RenderFrames().Select(f => f.Row(10)[20])];
        Assert.Equal(5 + 2 + 4, pixels.Count);
        Assert.Equal(new Rgba8(255, 0, 0, 255), pixels[0]);
        Assert.Equal(new Rgba8(0, 0, 255, 255), pixels[6]);
        Assert.Equal([0, 25, 50, 75], pixels[7..].Select(p => (int)p.G).ToArray());
    }

    [Fact]
    public void AnimationsPlayAtTheirDelays()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-edit-{Guid.NewGuid():N}.gif");
        try
        {
            using (Raster<Rgba8> gif = Raster.Create(16, 16, new Rgba8(255, 0, 0, 255)))
            {
                gif.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(200);
                using Raster<Rgba8> second = Raster.Create(16, 16, new Rgba8(0, 0, 255, 255));
                gif.Frames.Add(second.Frames[0]);
                gif.Frames[1].Metadata.Delay = TimeSpan.FromMilliseconds(300);
                gif.Save(path);
            }

            VideoEdit edit = new(new EditCanvas(16, 16, Rate10));
            edit.Track.Add(Clip.Animation(path).Loop(2));
            List<byte> red = [.. edit.RenderFrames().Select(f => f.Row(8)[8].R)];
            Assert.Equal(10, red.Count);
            Assert.True(red[0] > 200 && red[1] > 200 && red[2] < 50 && red[4] < 50 && red[5] > 200);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PanZoomMovesSmoothly()
    {
        // A ramp image: the column under the canvas centre moves by the same sub-pixel step every frame.
        using Raster<Rgba8> ramp = Raster.Create<Rgba8>(200, 100);
        for (int y = 0; y < 100; y++)
        {
            Span<Rgba8> row = ramp.Row(y);
            for (int x = 0; x < 200; x++)
            {
                row[x] = new Rgba8((byte)x, 0, 0, 255);
            }
        }

        VideoEdit edit = new(new EditCanvas(100, 50, Rate10));
        edit.Track.Add(Clip.Still(ramp, S(2)).PanZoom(new System.Drawing.RectangleF(0, 0, 0.5f, 0.5f), new System.Drawing.RectangleF(0.1f, 0, 0.5f, 0.5f)));
        List<double> centres = [.. edit.RenderFrames().Select(f => (double)f.Row(25)[50].R)];
        Assert.Equal(20, centres.Count);
        for (int i = 2; i < centres.Count; i++)
        {
            Assert.InRange(centres[i] - centres[i - 1], 0, 2);
        }

        Assert.Equal(50, centres[0], 1.5);
        Assert.Equal(70, centres[^1], 1.5);
    }

    [Fact]
    public void SlideshowTimingIsExact()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"egami-slides-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string[] files = [.. Enumerable.Range(0, 3).Select(i =>
            {
                string path = Path.Combine(folder, $"{i}.png");
                using Raster<Rgba8> image = Raster.Create(64, 48, new Rgba8((byte)(i * 100), 0, 0, 255));
                image.Save(path);
                return path;
            })];
            VideoEdit show = Slideshow.Create(files, new SlideshowOptions { Each = S(2), Transition = Transition.Fade(S(0.5)), PanZoom = true, Seed = 7 });
            EditPlan plan = show.Plan();
            Assert.Equal(new Rational(30, 1), plan.Canvas.FrameRate);
            Assert.Equal(3 * 60 - (2 * 15), plan.FrameCount);
            Assert.Equal(show.ToJson(), Slideshow.Create(files, new SlideshowOptions { Each = S(2), Transition = Transition.Fade(S(0.5)), PanZoom = true, Seed = 7 }).ToJson());
            Assert.Equal(3, VideoEdit.FromJson(show.ToJson()).Track.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void JsonKeepsTiming()
    {
        VideoEdit edit = new(new EditCanvas(64, 48, Rate10));
        edit.Track.Add(Clip.Open("a.mkv").Speed(1.5, keepPitch: false).Reverse().Loop(2).Hold(S(1)).Freeze(S(0.5), S(0.25)))
            .Add(Clip.Still("b.png", S(3)).PanZoom(new System.Drawing.RectangleF(0, 0, 1, 1), new System.Drawing.RectangleF(0.25f, 0.25f, 0.5f, 0.5f), Easing.EaseInOut))
            .Add(Clip.Color(Rgba8.White, S(1)))
            .Add(Clip.Animation("c.gif"));
        string json = edit.ToJson();
        Assert.Equal(json, VideoEdit.FromJson(json).ToJson());
        Assert.Throws<ArgumentOutOfRangeException>(() => Clip.Open("a.mkv").Speed(20));
    }

    private static float[] Render(VideoEdit edit, out long frames)
    {
        string output = Path.Combine(Path.GetTempPath(), $"egami-edit-{Guid.NewGuid():N}.mkv");
        try
        {
            EditPlan plan = edit.Render(output, new EditRenderOptions { Writer = new VideoWriterOptions { Codec = Egami.Video.Codecs.RawVideoCodec.Instance, AudioCodec = AudioCodecs.Pcm } },
                null, TestContext.Current.CancellationToken);
            frames = plan.FrameCount;
            using AudioReader audio = AudioReader.Open(output);
            List<AudioFrame> sound = [.. audio.ReadFrames()];
            float[] all = [.. sound.SelectMany(f => f.AsSpan<float>(0).ToArray())];
            sound.ForEach(f => f.Dispose());
            return all;
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static double Frequency(ReadOnlySpan<float> samples)
    {
        int first = -1, last = -1, crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                if (first < 0)
                {
                    first = i;
                }
                else
                {
                    crossings++;
                }

                last = i;
            }
        }

        return crossings == 0 ? 0 : crossings * 48000.0 / (last - first);
    }

    private sealed class Counter : IFrameSource
    {
        public Size Size => new(8, 8);

        public MediaTime Duration => MediaTime.FromSeconds(0.4);

        public void Render(MediaTime time, Raster<Rgba8> target)
        {
            for (int y = 0; y < target.Height; y++)
            {
                target.Row(y).Fill(new Rgba8(0, (byte)Math.Round(time.Seconds * 250), 0, 255));
            }
        }
    }
}
