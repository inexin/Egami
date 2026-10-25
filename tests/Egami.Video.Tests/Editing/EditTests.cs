using Egami.Operations;
using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Editing;

namespace Egami.Video.Tests.Editing;

/// <summary>V9 stage A: the edit model, plan, JSON, the renderer, fades, transitions, layers, chroma key, stacks and effects.</summary>
public class EditTests
{
    private static readonly Rational Rate10 = new(10, 1);

    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    /// <summary>Returns a copy of rendered frame n (the last for -1).</summary>
    private static Raster<Rgba8> Nth(VideoEdit edit, int n)
    {
        Raster<Rgba8>? copy = null;
        int i = 0;
        foreach (Raster<Rgba8> frame in edit.RenderFrames())
        {
            if (i++ == n || n < 0)
            {
                copy?.Dispose();
                copy = frame.Derive(_ => { }, TestContext.Current.CancellationToken);
                if (n >= 0)
                {
                    break;
                }
            }
        }

        return copy!;
    }

    private static void Near(int expected, int actual, int tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} within {tolerance}, got {actual}");

    [Fact]
    public void PlanJoinsClipsInWholeFrames()
    {
        string a = EditClips.Make("a", 64, 48, Rate10, 20, 40);
        string b = EditClips.Make("b", 80, 45, new Rational(25, 1), 25, 80, tone: 880, sampleRate: 44100, layout: ChannelLayout.Mono);
        VideoEdit edit = new(new EditCanvas(64, 48, Rate10));
        edit.Track.Add(Clip.Open(a)).Add(Clip.Open(b).Trim(S(0.2)), Transition.Fade(S(0.3)));
        EditPlan plan = edit.Plan();
        Assert.Equal(2, plan.Clips.Count);
        Assert.Equal(20, plan.Clips[0].FrameCount);
        Assert.Equal(17, plan.Clips[1].StartFrame);
        Assert.Equal(8, plan.Clips[1].FrameCount);
        Assert.Equal(3, plan.Clips[1].TransitionFrames);
        Assert.Equal(25, plan.FrameCount);
        Assert.Equal(S(2.5), plan.Duration);
        Assert.True(plan.HasAudio);
    }

    [Fact]
    public void PlanTakesTheCanvasFromTheFirstClip()
    {
        string a = EditClips.Make("a", 64, 48, new Rational(25, 1), 10, 40);
        VideoEdit edit = new();
        edit.Track.Add(Clip.Open(a));
        EditPlan plan = edit.Plan();
        Assert.Equal(64, plan.Canvas.Width);
        Assert.Equal(new Rational(25, 1), plan.Canvas.FrameRate);
    }

    [Fact]
    public void PlanCatchesBadEdits()
    {
        string a = EditClips.Make("a", 64, 48, Rate10, 20, 40);
        Assert.Throws<InvalidOperationException>(() => new VideoEdit().Plan());

        VideoEdit late = new();
        late.Track.Add(Clip.Open(a).Trim(S(5)));
        Assert.Contains("after its source ends", Assert.Throws<InvalidOperationException>(() => late.Plan()).Message);

        VideoEdit longTransition = new();
        longTransition.Track.Add(Clip.Open(a).Trim(S(0), S(0.5))).Add(Clip.Open(a), Transition.Fade(S(1)));
        Assert.Contains("longer than a clip", Assert.Throws<InvalidOperationException>(() => longTransition.Plan()).Message);

        VideoEdit missing = new();
        missing.Track.Add(Clip.Open(Path.Combine(Path.GetTempPath(), "no-such-clip.mkv")));
        Assert.Contains("cannot be read", Assert.Throws<InvalidOperationException>(() => missing.Plan()).Message);

        VideoEdit warnings = new();
        warnings.Track.Add(Clip.Open(a).Trim(S(0), S(9)), Transition.Fade(S(0.2)));
        warnings.Layers.Add(Layer.Of(Clip.Open(a)).Between(S(5)));
        EditPlan plan = warnings.Plan();
        Assert.Equal(20, plan.FrameCount);
        Assert.Contains(plan.Warnings, w => w.Contains("first clip's transition", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("starts after the edit ends", StringComparison.Ordinal));
    }

    [Fact]
    public void JoinPicksTheNearestSourceFrame()
    {
        // 25 fps into a 10 fps canvas: output frame n shows the source frame before (n + 0.5) / 10 s, as ffmpeg's fps filter does.
        string b = EditClips.Make("b", 64, 48, new Rational(25, 1), 25, 80);
        string a = EditClips.Make("a", 64, 48, Rate10, 10, 40);
        VideoEdit edit = new(new EditCanvas(64, 48, Rate10));
        edit.Track.Add(Clip.Open(a)).Add(Clip.Open(b));
        List<(int Frame, int Green)> shown = [.. edit.RenderFrames().Select(f => EditClips.Read(f))];
        Assert.Equal(20, shown.Count);
        Assert.Equal([.. Enumerable.Range(0, 10)], shown[..10].Select(s => s.Frame).ToArray());
        Assert.All(shown[..10], s => Near(40, s.Green, 3));
        int[] expected = [.. Enumerable.Range(0, 10).Select(n => (int)Math.Ceiling(((n + 0.5) * 2.5) - 1))];
        Assert.Equal(expected, shown[10..].Select(s => s.Frame).ToArray());
        Assert.All(shown[10..], s => Near(80, s.Green, 3));
    }

    [Fact]
    public void ClipsAreFittedToTheCanvas()
    {
        string wide = EditClips.Make("wide", 80, 40, Rate10, 2, 120);
        VideoEdit edit = new(new EditCanvas(40, 40, Rate10) { Background = new Rgba8(0, 0, 255, 255) });
        edit.Track.Add(Clip.Open(wide));
        using Raster<Rgba8> frame = Nth(edit, 0);
        Assert.Equal(255, frame.Row(2)[20].B);
        Near(120, frame.Row(20)[20].G, 3);

        edit.Canvas = edit.Canvas! with { Fit = ClipFit.Cover };
        using Raster<Rgba8> cover = Nth(edit, 0);
        Near(120, cover.Row(2)[20].G, 3);
    }

    [Fact]
    public void FadesGoToTheColourAndBack()
    {
        string a = EditClips.Make("white", 32, 32, Rate10, 10, 200, tone: null);
        VideoEdit edit = new(new EditCanvas(32, 32, Rate10));
        edit.Track.Add(Clip.Open(a).FadeIn(S(0.5)).FadeOut(S(0.4)));
        List<int> green = [.. edit.RenderFrames().Select(f => (int)f.Row(16)[16].G)];
        Assert.Equal(0, green[0]);
        Assert.InRange(green[1], 30, 50);
        Assert.InRange(green[5], 196, 204);
        Assert.InRange(green[9], 40, 60);
    }

    [Theory]
    [InlineData(TransitionKind.Fade)]
    [InlineData(TransitionKind.FadeThroughColor)]
    [InlineData(TransitionKind.Dissolve)]
    [InlineData(TransitionKind.Wipe)]
    [InlineData(TransitionKind.Slide)]
    [InlineData(TransitionKind.CircleOpen)]
    [InlineData(TransitionKind.CircleClose)]
    [InlineData(TransitionKind.Clock)]
    [InlineData(TransitionKind.Zoom)]
    [InlineData(TransitionKind.Pixelize)]
    [InlineData(TransitionKind.Blur)]
    public void TransitionsGoFromOneClipToTheNext(TransitionKind kind)
    {
        string a = EditClips.Make("ta", 48, 32, Rate10, 20, 40, tone: null);
        string b = EditClips.Make("tb", 48, 32, Rate10, 20, 160, tone: null);
        VideoEdit edit = new(new EditCanvas(48, 32, Rate10));
        edit.Track.Add(Clip.Open(a)).Add(Clip.Open(b), Transition.Of(kind, S(1)));
        List<double> share = [.. edit.RenderFrames().Select(Share)];
        Assert.Equal(30, share.Count);
        Assert.Equal(0, share[9], 2);
        if (kind is not TransitionKind.FadeThroughColor and not TransitionKind.Pixelize)
        {
            Assert.InRange(share[15], 0.2, 0.8);
        }

        Assert.Equal(1, share[20], 2);

        // The part of the frame that shows the second clip: green 160 counts fully, 40 not at all.
        static double Share(Raster<Rgba8> frame)
        {
            double sum = 0;
            for (int y = 0; y < frame.Height; y++)
            {
                foreach (Rgba8 p in frame.Row(y))
                {
                    sum += Math.Clamp((p.G - 40) / 120.0, 0, 1);
                }
            }

            return sum / (frame.Width * frame.Height);
        }
    }

    [Theory]
    [InlineData(TransitionDirection.Left, 47, 0)]
    [InlineData(TransitionDirection.Right, 0, 47)]
    public void WipesUncoverFromTheRightSide(TransitionDirection direction, int first, int last)
    {
        string a = EditClips.Make("ta", 48, 32, Rate10, 20, 40, tone: null);
        string b = EditClips.Make("tb", 48, 32, Rate10, 20, 160, tone: null);
        VideoEdit edit = new(new EditCanvas(48, 32, Rate10));
        edit.Track.Add(Clip.Open(a)).Add(Clip.Open(b), Transition.Wipe(direction, S(1)));
        using Raster<Rgba8> frame = Nth(edit, 12);
        Near(160, frame.Row(10)[first].G, 4);
        Near(40, frame.Row(10)[last].G, 4);
    }

    [Fact]
    public void CustomTransitionsDrawTheirFrames()
    {
        string a = EditClips.Make("ta", 48, 32, Rate10, 20, 40, tone: null);
        string b = EditClips.Make("tb", 48, 32, Rate10, 20, 160, tone: null);
        VideoEdit edit = new(new EditCanvas(48, 32, Rate10));
        edit.Track.Add(Clip.Open(a)).Add(Clip.Open(b), Transition.Custom(new Magenta(), S(0.5)));
        using Raster<Rgba8> frame = Nth(edit, 16);
        Assert.Equal(new Rgba8(255, 0, 255, 255), frame.Row(0)[0]);
    }

    [Fact]
    public void LayersAreKeyedPlacedAndFaded()
    {
        string main = EditClips.Make("main", 64, 48, Rate10, 20, 40, tone: null);
        string green = EditClips.Make("green", 32, 24, Rate10, 20, 255, tone: null);
        VideoEdit edit = new(new EditCanvas(64, 48, Rate10));
        edit.Track.Add(Clip.Open(main));
        edit.Layers.Add(Layer.Of(Clip.Open(green)).Place(new Rect(0, 0, 32, 24)).Between(S(0.5), S(1.5)));
        edit.Layers.Add(Layer.Of(Clip.Open(green)).Place(Anchor.BottomRight, margin: 4).Key(new ChromaKey(new Rgba8(0, 255, 0, 255)) { Similarity = 0.3f }));
        List<Raster<Rgba8>> frames = [];
        foreach (Raster<Rgba8> f in edit.RenderFrames())
        {
            frames.Add(f.Derive(_ => { }, TestContext.Current.CancellationToken));
        }

        try
        {
            Near(40, frames[2].Row(5)[5].G, 3);
            Near(255, frames[7].Row(5)[5].G, 3);
            Near(40, frames[16].Row(5)[5].G, 3);

            // The keyed layer's flat green is gone, so the main clip shows through it.
            Near(40, frames[3].Row(40)[50].G, 6);
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void ChromaKeyRecoversAKnownAlpha()
    {
        // The task 8 gate on a synthetic green screen: a red disc with an anti-aliased edge and a see-through band, over
        // studio green, keyed; the alpha that comes back is within 3 % of the known one on average.
        Rgba8 screen = Rgba8.FromHex("#00b140");
        const int Size = 128;
        float[] known = new float[Size * Size];
        using Raster<Rgba8> frame = Raster.Create<Rgba8>(Size, Size);
        for (int y = 0; y < Size; y++)
        {
            Span<Rgba8> row = frame.Row(y);
            for (int x = 0; x < Size; x++)
            {
                double d = Math.Sqrt(Math.Pow(x + 0.5 - 64, 2) + Math.Pow(y + 0.5 - 64, 2));
                float a = (float)Math.Clamp(40.5 - d, 0, 1);
                if (y is >= 100 and < 110)
                {
                    a = Math.Max(a, 0.5f);
                }

                known[(y * Size) + x] = a;
                Rgba8 fg = new(200, 60, 50, 255);
                row[x] = new Rgba8((byte)Math.Round((fg.R * a) + (screen.R * (1 - a))), (byte)Math.Round((fg.G * a) + (screen.G * (1 - a))),
                    (byte)Math.Round((fg.B * a) + (screen.B * (1 - a))), 255);
            }
        }

        new ChromaKey(screen) { Similarity = 0.05f, Blend = 0.35f, Despill = 0.5f }.Apply(frame);
        double error = 0;
        for (int y = 0; y < Size; y++)
        {
            Span<Rgba8> row = frame.Row(y);
            for (int x = 0; x < Size; x++)
            {
                error += Math.Abs((row[x].A / 255.0) - known[(y * Size) + x]);
            }
        }

        double mean = error / (Size * Size);
        Assert.True(mean <= 0.03, $"mean alpha error {mean:P2}");
    }

    [Fact]
    public void LayerKeyframesMoveIt()
    {
        string main = EditClips.Make("main", 64, 48, Rate10, 20, 40, tone: null);
        string small = EditClips.Make("small", 8, 8, Rate10, 20, 200, tone: null);
        VideoEdit edit = new(new EditCanvas(64, 48, Rate10));
        edit.Track.Add(Clip.Open(main));
        edit.Layers.Add(Layer.Of(Clip.Open(small)).Place(new Rect(0, 20, 8, 8))
            .Animate(LayerProperty.X, new Keyframe(S(0), 0), new Keyframe(S(1), 40))
            .Animate(LayerProperty.Opacity, new Keyframe(S(0), 1)));
        using Raster<Rgba8> frame = Nth(edit, 5);
        Near(200, frame.Row(24)[24].G, 3);
        Near(40, frame.Row(24)[4].G, 3);
    }

    [Fact]
    public void StacksLayClipsOut()
    {
        string a = EditClips.Make("sa", 32, 24, Rate10, 10, 40, tone: null);
        string b = EditClips.Make("sb", 16, 12, Rate10, 20, 160, tone: null);
        VideoEdit side = new();
        side.Track.Add(Stack.Horizontal(Clip.Open(a), Clip.Open(b)));
        EditPlan plan = side.Plan();
        Assert.Equal(64, plan.Canvas.Width);
        Assert.Equal(24, plan.Canvas.Height);
        Assert.Equal(20, plan.FrameCount);
        using Raster<Rgba8> last = Nth(side, -1);
        Near(40, last.Row(12)[10].G, 3);
        Assert.Equal(9, EditClips.Read(last, 10, 12).Frame);
        Near(160, last.Row(12)[50].G, 3);

        VideoEdit grid = new();
        grid.Track.Add(Stack.Grid(2, 2, [Clip.Open(a), Clip.Open(b), Clip.Open(a)], new StackOptions { Gap = 2, Shortest = true }));
        plan = grid.Plan();
        Assert.Equal(66, plan.Canvas.Width);
        Assert.Equal(50, plan.Canvas.Height);
        Assert.Equal(10, plan.FrameCount);
        using Raster<Rgba8> frame = Nth(grid, 0);
        Assert.Equal(0, frame.Row(45)[50].G);
    }

    [Fact]
    public void EffectsRunInsideTheirWindows()
    {
        string a = EditClips.Make("fx", 32, 32, Rate10, 20, 100, tone: null);
        VideoEdit edit = new(new EditCanvas(32, 32, Rate10));
        edit.Track.Add(Clip.Open(a).WithEffect(EditEffect.Apply(ops => ops.Invert()).Between(S(0.5), S(1))));
        edit.Effects.Add(EditEffect.Of(new Stamp()).Between(S(1.5)));
        List<Rgba8> pixels = [.. edit.RenderFrames().Select(f => f.Row(16)[16])];
        Near(100, pixels[4].G, 3);
        Near(155, pixels[5].G, 3);
        Near(155, pixels[9].G, 3);
        Near(100, pixels[10].G, 3);
        Assert.Equal(0, pixels[14].B);
        Assert.Equal(15, pixels[15].B);
    }

    [Fact]
    public void JsonRoundTrips()
    {
        VideoEdit edit = new(new EditCanvas(1280, 720, new Rational(30000, 1001)) { Fit = ClipFit.Cover, AudioLayout = ChannelLayout.Mono });
        edit.Track.Add(Clip.Open("a.mp4").Trim(S(1), S(5)).FadeIn(S(0.5)).Volume(-3))
            .Add(Clip.Open("b.mkv").Muted(), Transition.Wipe(TransitionDirection.Up, S(1)) with { Softness = 0.1, Easing = Easing.EaseInOut })
            .Add(Stack.Grid(2, 1, [Clip.Open("c.webm"), Clip.Open("d.webm")], new StackOptions { Gap = 4, MixAudio = true }), Transition.FadeThrough(Rgba8.White, S(0.5)));
        edit.Layers.Add(Layer.Of(Clip.Open("logo.png")).Place(Anchor.TopRight, 10, 0.5).Opacity(0.8f).Between(S(2), S(4)).FadeIn(S(0.25))
            .Key(new ChromaKey(new Rgba8(0, 177, 64, 255)) { Similarity = 0.12f, Despill = 0.6f })
            .Animate(LayerProperty.Rotation, new Keyframe(S(0), 0), new Keyframe(S(2), 90, Easing.EaseOut)));
        edit.Effects.Add(EditEffect.Apply(ops => ops.Blur(8), "blur 8").Between(S(5), S(12)));
        string json = edit.ToJson();
        VideoEdit back = VideoEdit.FromJson(json, name => EditEffect.Apply(ops => ops.Invert(), name));
        Assert.Equal(json, back.ToJson());
        Assert.Equal(3, back.Track.Count);
        Assert.Equal(TransitionKind.Wipe, back.Track[1].Transition!.Kind);
        Assert.Equal(new Rational(30000, 1001), back.Canvas!.FrameRate);

        Assert.Throws<NotSupportedException>(() => SaveWithStream());
        Assert.Throws<FormatException>(() => VideoEdit.FromJson("{\"track\":[{\"clip\":{\"open\":\"a\"},\"transition\":{\"kind\":\"spin\",\"duration\":1}}]}"));
        Assert.Throws<FormatException>(() => VideoEdit.FromJson("{\"track\":[{\"clip\":{\"open\":\"a\",\"effects\":[{\"name\":\"blur 2\"}]}}]}"));

        static string SaveWithStream()
        {
            VideoEdit e = new();
            e.Track.Add(Clip.Open(new MemoryStream()));
            return e.ToJson();
        }
    }

    [Fact]
    public void RenderWritesPictureAndSoundInSync()
    {
        string a = EditClips.Make("ra", 64, 48, Rate10, 20, 40);
        string b = EditClips.Make("rb", 80, 60, new Rational(25, 1), 25, 160, tone: 880, sampleRate: 44100, layout: ChannelLayout.Mono);
        string silent = EditClips.Make("rs", 64, 48, Rate10, 10, 100, tone: null);
        VideoEdit edit = new(new EditCanvas(64, 48, Rate10));
        edit.Track.Add(Clip.Open(a)).Add(Clip.Open(b), Transition.Dissolve(S(0.5))).Add(Clip.Open(silent));
        string output = Path.Combine(Path.GetTempPath(), $"egami-edit-{Guid.NewGuid():N}.mkv");
        try
        {
            List<MediaProgress> reports = [];
            EditPlan plan = edit.Render(output, new EditRenderOptions { Writer = new VideoWriterOptions { Codec = Egami.Video.Codecs.RawVideoCodec.Instance, AudioCodec = AudioCodecs.Pcm } },
                new Collect(reports), TestContext.Current.CancellationToken);
            Assert.Equal(35, plan.FrameCount);
            Assert.True(reports[^1].IsFinal);

            using (VideoReader reader = VideoReader.Open(output))
            {
                List<YuvVideoFrame> frames = [.. reader.ReadFrames()];
                Assert.Equal(35, frames.Count);
                Assert.Equal(S(3.4), frames[^1].Timestamp);
                frames.ForEach(f => f.Dispose());
            }

            using AudioReader audio = AudioReader.Open(output);
            List<AudioFrame> sound = [.. audio.ReadFrames()];
            Assert.Equal(168000, sound.Sum(f => f.SampleCount));

            // The silent clip at the end is silent; the first second is the 440 Hz tone at its level.
            float[] all = [.. sound.SelectMany(f => f.AsSpan<float>(0).ToArray())];
            sound.ForEach(f => f.Dispose());
            Assert.InRange(all.Skip(120000 + 2400).Take(4800).Max(Math.Abs), 0, 1e-4f);
            Assert.InRange(all.Skip(4800).Take(4800).Max(), 0.24f, 0.26f);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private sealed class Collect(List<MediaProgress> list) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => list.Add(value);
    }

    private sealed class Magenta : ITransition
    {
        public void Render(Raster<Rgba8> outgoing, Raster<Rgba8> incoming, double progress, Raster<Rgba8> target)
        {
            for (int y = 0; y < target.Height; y++)
            {
                target.Row(y).Fill(new Rgba8(255, 0, 255, 255));
            }
        }
    }

    private sealed class Stamp : IFrameEffect
    {
        public void Apply(Raster<Rgba8> frame, FrameEffectContext context)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Span<Rgba8> row = frame.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x].B = (byte)(context.LocalTime.Seconds * 10);
                }
            }
        }
    }
}
