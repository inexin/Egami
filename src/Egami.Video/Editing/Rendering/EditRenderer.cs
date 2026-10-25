using System.Diagnostics;
using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>
/// The pull renderer: walks the output one frame at a time, asks the main track which clip (or two clips, inside a
/// transition) shows, draws the layers over it, runs the effects and hands the frame on. Only the frames in use are held.
/// </summary>
internal sealed class EditRenderer : IDisposable
{
    private readonly EditPlan _plan;
    private readonly MediaContext _context;
    private readonly EditCanvas _canvas;
    private readonly Dictionary<int, ClipFrames> _clips = [];
    private readonly Dictionary<int, ClipFrames> _layers = [];
    private readonly AudioTimeline? _audio;
    private readonly Raster<Rgba8> _frame;
    private readonly Raster<Rgba8> _from;
    private readonly Raster<Rgba8> _to;

    public EditRenderer(EditPlan plan, bool audio, MediaContext context)
    {
        _plan = plan;
        _context = context;
        _canvas = plan.Canvas;
        _frame = Raster.Create<Rgba8>(_canvas.Width, _canvas.Height, context.Egami);
        _from = Raster.Create<Rgba8>(_canvas.Width, _canvas.Height, context.Egami);
        _to = Raster.Create<Rgba8>(_canvas.Width, _canvas.Height, context.Egami);
        _audio = audio ? new AudioTimeline(plan, context) : null;
    }

    /// <summary>Renders every frame and the sound into a writer.</summary>
    public void Run(VideoWriter writer, IProgress<MediaProgress>? progress, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        long last = -1;
        for (long n = 0; n < _plan.FrameCount; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Raster<Rgba8> frame = Render(n);
            writer.WriteFrame(frame, _plan.TimeOf(n));
            _audio?.WriteUntil(_plan.TimeOf(n + 1), writer, final: false);
            if (progress is not null && (clock.ElapsedMilliseconds - last >= 250 || n == 0))
            {
                last = clock.ElapsedMilliseconds;
                Report(progress, n + 1, clock.Elapsed, final: false);
            }
        }

        _audio?.WriteUntil(_plan.Duration, writer, final: true);
        if (progress is not null)
        {
            Report(progress, _plan.FrameCount, clock.Elapsed, final: true);
        }
    }

    /// <summary>Renders the frames one by one; each one is valid until the next is asked for.</summary>
    public IEnumerable<Raster<Rgba8>> Frames()
    {
        for (long n = 0; n < _plan.FrameCount; n++)
        {
            yield return Render(n);
        }
    }

    public void Dispose()
    {
        foreach (ClipFrames frames in _clips.Values.Concat(_layers.Values))
        {
            frames.Dispose();
        }

        _clips.Clear();
        _layers.Clear();
        _audio?.Dispose();
        _frame.Dispose();
        _from.Dispose();
        _to.Dispose();
    }

    /// <summary>Composes output frame <paramref name="n"/> into the renderer's frame.</summary>
    public Raster<Rgba8> Render(long n)
    {
        int current = ClipAt(n);
        PlannedClip clip = _plan.Clips[current];
        long local = n - clip.StartFrame;
        if (local < clip.TransitionFrames && current > 0)
        {
            PlannedClip previous = _plan.Clips[current - 1];
            ClipFrame(previous, n - previous.StartFrame, _from);
            ClipFrame(clip, local, _to);
            Transition transition = _plan.Edit.Track[current].Transition!;
            TransitionPainter.Paint(transition, _from, _to, (double)local / clip.TransitionFrames, _frame);
        }
        else
        {
            ClipFrame(clip, local, _frame);
        }

        // Clips that are done hold nothing.
        foreach (int index in _clips.Keys.Where(i => i < current - 1 || (i == current - 1 && local >= clip.TransitionFrames)).ToList())
        {
            _clips[index].Dispose();
            _clips.Remove(index);
        }

        foreach (PlannedLayer layer in _plan.Layers)
        {
            DrawLayer(layer, n);
        }

        MediaTime time = _plan.TimeOf(n);
        foreach (EditEffect effect in _plan.Edit.Effects)
        {
            effect.Run(_frame, time, time, _plan.Duration);
        }

        return _frame;
    }

    private void Report(IProgress<MediaProgress> progress, long frames, TimeSpan elapsed, bool final)
    {
        MediaTime position = _plan.TimeOf(frames);
        progress.Report(new MediaProgress
        {
            Position = position,
            Duration = _plan.Duration,
            Fraction = _plan.FrameCount == 0 ? 1 : (double)frames / _plan.FrameCount,
            Frames = frames,
            Elapsed = elapsed,
            Speed = elapsed.TotalSeconds > 0 ? position.Seconds / elapsed.TotalSeconds : null,
            IsFinal = final,
        });
    }

    private int ClipAt(long n)
    {
        for (int i = _plan.Clips.Count - 1; i >= 0; i--)
        {
            if (n >= _plan.Clips[i].StartFrame)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Draws a main-track clip's frame for a frame index from its start: fitted, faded and with its effects.</summary>
    private void ClipFrame(PlannedClip clip, long local, Raster<Rgba8> target)
    {
        if (!_clips.TryGetValue(clip.Index, out ClipFrames? frames))
        {
            frames = ClipFrames.Open(clip.Clip, _plan, _context);
            _clips[clip.Index] = frames;
        }

        Raster<Rgba8>? source = frames.FrameBefore(Limit(local));
        if (source is null)
        {
            ClipFrames.Fill(target, _canvas.Background);
        }
        else
        {
            ClipFrames.Fit(source, target, _canvas.Fit, _canvas.Background);
        }

        Clip c = clip.Clip;
        long fadeIn = EditPlanner.Frames(c.FadeInDuration, _canvas), fadeOut = EditPlanner.Frames(c.FadeOutDuration, _canvas);
        if (local < fadeIn)
        {
            TransitionPainter.MixColor(target, c.FadeColor, 1 - ((double)local / fadeIn), target);
        }

        if (fadeOut > 0 && local >= clip.FrameCount - fadeOut)
        {
            TransitionPainter.MixColor(target, c.FadeColor, 1 - ((double)(clip.FrameCount - local) / fadeOut), target);
        }

        MediaTime time = _plan.TimeOf(clip.StartFrame + local);
        foreach (EditEffect effect in c.Effects)
        {
            effect.Run(target, time, _plan.TimeOf(local), _plan.TimeOf(clip.FrameCount));
        }
    }

    /// <summary>The source time limit for a clip's frame: the latest source frame before the middle of the output frame's slot.</summary>
    private MediaTime Limit(long local) => new((2 * local * _canvas.FrameRate.Denominator) + _canvas.FrameRate.Denominator, new Rational(1, 2 * _canvas.FrameRate.Numerator));

    private void DrawLayer(PlannedLayer planned, long n)
    {
        long local = n - planned.StartFrame;
        Layer layer = planned.Layer;
        if (local < 0 || local >= planned.FrameCount)
        {
            if (local >= planned.FrameCount && _layers.Remove(planned.Index, out ClipFrames? done))
            {
                done.Dispose();
            }

            return;
        }

        if (!_layers.TryGetValue(planned.Index, out ClipFrames? frames))
        {
            frames = ClipFrames.Open(layer.Clip, _plan, _context);
            _layers[planned.Index] = frames;
        }

        // After its clip ends a holding layer keeps asking for the last frame's time.
        Raster<Rgba8>? source = frames.FrameBefore(Limit(Math.Min(local, planned.ClipFrames - 1)));
        if (source is null)
        {
            return;
        }

        MediaTime localTime = _plan.TimeOf(local);
        double opacity = layer.OpacityAmount * Math.Clamp(layer.ValueAt(LayerProperty.Opacity, localTime, 1), 0, 1);
        long fadeIn = EditPlanner.Frames(layer.FadeInDuration, _canvas), fadeOut = EditPlanner.Frames(layer.FadeOutDuration, _canvas);
        if (local < fadeIn)
        {
            opacity *= (double)local / fadeIn;
        }

        if (fadeOut > 0 && local >= planned.FrameCount - fadeOut)
        {
            opacity *= (double)(planned.FrameCount - local) / fadeOut;
        }

        if (opacity <= 0)
        {
            return;
        }

        using Raster<Rgba8> image = source.Derive(_ => { });
        layer.ChromaKey?.Apply(image);
        foreach (EditEffect effect in layer.Effects)
        {
            effect.Run(image, _plan.TimeOf(n), localTime, _plan.TimeOf(planned.FrameCount));
        }

        Rect area = Area(layer, image.Width, image.Height);
        double scale = layer.ValueAt(LayerProperty.Scale, localTime, 1);
        double rotation = layer.ValueAt(LayerProperty.Rotation, localTime, 0);
        int width = Math.Max(1, (int)Math.Round(area.Width * scale)), height = Math.Max(1, (int)Math.Round(area.Height * scale));
        double cx = area.X + (area.Width / 2.0) + layer.ValueAt(LayerProperty.X, localTime, 0);
        double cy = area.Y + (area.Height / 2.0) + layer.ValueAt(LayerProperty.Y, localTime, 0);
        Raster<Rgba8> placed = width == image.Width && height == image.Height ? image : image.Derive(ops => ops.Resize(width, height));
        try
        {
            if (Math.Abs(rotation) > 1e-6)
            {
                Raster<Rgba8> rotated = Rotate(placed, rotation);
                if (!ReferenceEquals(placed, image))
                {
                    placed.Dispose();
                }

                placed = rotated;
            }

            Point at = new((int)Math.Round(cx - (placed.Width / 2.0)), (int)Math.Round(cy - (placed.Height / 2.0)));
            Raster<Rgba8> draw = placed;
            _frame.Apply(ops => ops.DrawImage(draw, at, layer.BlendMode, (float)Math.Clamp(opacity, 0, 1)));
        }
        finally
        {
            if (!ReferenceEquals(placed, image))
            {
                placed.Dispose();
            }
        }
    }

    private static Raster<Rgba8> Rotate(Raster<Rgba8> image, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        int w = (int)Math.Ceiling((image.Width * cos) + (image.Height * sin)), h = (int)Math.Ceiling((image.Width * sin) + (image.Height * cos));
        Matrix3x2 matrix = Matrix3x2.CreateTranslation(-image.Width / 2f, -image.Height / 2f)
            * Matrix3x2.CreateRotation((float)radians)
            * Matrix3x2.CreateTranslation(w / 2f, h / 2f);
        return image.Derive(ops => ops.Transform(matrix, new TransformOptions { Viewport = new Rect(0, 0, w, h) }));
    }

    private Rect Area(Layer layer, int width, int height)
    {
        if (layer.Area is Rect area)
        {
            return area;
        }

        int w = Math.Max(1, (int)Math.Round(width * layer.Scale)), h = Math.Max(1, (int)Math.Round(height * layer.Scale));
        int m = layer.Margin, cw = _canvas.Width, ch = _canvas.Height;
        int x = layer.Anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => m,
            Anchor.TopRight or Anchor.Right or Anchor.BottomRight => cw - w - m,
            _ => (cw - w) / 2,
        };
        int y = layer.Anchor switch
        {
            Anchor.TopLeft or Anchor.Top or Anchor.TopRight => m,
            Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => ch - h - m,
            _ => (ch - h) / 2,
        };
        return new Rect(x, y, w, h);
    }
}
