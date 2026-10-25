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
    private readonly Dictionary<int, YuvRaster<byte>> _scaled = [];
    private readonly Dictionary<int, StabilizationPath> _paths = [];
    private YuvRaster<byte>? _mixed;

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
            if (Planes(n) is { } planes)
            {
                writer.WriteFrame(planes, _plan.TimeOf(n));
            }
            else
            {
                writer.WriteFrame(Render(n), _plan.TimeOf(n));
            }

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
        _mixed?.Dispose();
        foreach (YuvRaster<byte> scaled in _scaled.Values)
        {
            scaled.Dispose();
        }

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

    /// <summary>
    /// The fast path: when output frame <paramref name="n"/> is a source frame of the canvas size, or a fade or hard wipe
    /// between two, with no fit, fade to a colour, effect, motion or layer, it is made from the decoded planes, as FFmpeg's
    /// filters work, without going through RGBA. Returns <see langword="null"/> when the frame needs the full renderer.
    /// </summary>
    private YuvRaster<byte>? Planes(long n)
    {
        if (_plan.Edit.Effects.Count > 0)
        {
            return null;
        }

        List<(PlannedLayer Layer, YuvRaster<byte> Planes, Rect Area)> layers = [];
        foreach (PlannedLayer layer in _plan.Layers)
        {
            if (n < layer.StartFrame || n >= layer.StartFrame + layer.FrameCount)
            {
                continue;
            }

            if (SimpleLayer(layer, n - layer.StartFrame) is not { } placed)
            {
                return null;
            }

            layers.Add((layer, placed.Planes, placed.Area));
        }

        YuvRaster<byte>? frame = TrackPlanes(n);
        if (frame is null || layers.Count == 0)
        {
            return frame;
        }

        if (layers.Any(l => l.Planes.ChromaFormat != frame.ChromaFormat))
        {
            return null;
        }

        // Layers draw into a copy of the track's frame, each scaled into its area, plane by plane.
        if (!ReferenceEquals(frame, _mixed))
        {
            YuvRaster<byte> copy = Mixed(frame);
            for (int plane = 0; plane < 3; plane++)
            {
                YuvPlane<byte> source = Plane(frame, plane), target = Plane(copy, plane);
                for (int y = 0; y < source.Height; y++)
                {
                    source.Row(y).CopyTo(target.Row(y));
                }
            }
        }

        foreach ((PlannedLayer layer, YuvRaster<byte> planes, Rect area) in layers)
        {
            if (!_scaled.TryGetValue(layer.Index, out YuvRaster<byte>? scaled) || scaled.Width != area.Width || scaled.Height != area.Height)
            {
                scaled?.Dispose();
                scaled = YuvRaster.Create(area.Width, area.Height, planes.ChromaFormat, planes.Color, context: _context.Egami);
                _scaled[layer.Index] = scaled;
            }

            planes.ResizeTo(scaled);
            for (int plane = 0; plane < 3; plane++)
            {
                YuvPlane<byte> source = Plane(scaled, plane), target = Plane(_mixed!, plane);
                int sx = plane == 0 ? 1 : _mixed!.Width / target.Width, sy = plane == 0 ? 1 : _mixed!.Height / target.Height;
                for (int y = 0; y < source.Height; y++)
                {
                    source.Row(y).CopyTo(target.Row((area.Y / sy) + y)[(area.X / sx)..]);
                }
            }
        }

        return _mixed;
    }

    /// <summary>A layer's planes and area when it can be drawn as planes: opaque, unkeyed, still, aligned to the chroma grid, inside the canvas.</summary>
    private (YuvRaster<byte> Planes, Rect Area)? SimpleLayer(PlannedLayer planned, long local)
    {
        Layer layer = planned.Layer;
        Clip c = layer.Clip;
        if (layer.ChromaKey is not null || layer.Effects.Count > 0 || layer.Animations.Count > 0 || layer.OpacityAmount < 1 || layer.BlendMode != BlendMode.SourceOver
            || local < EditPlanner.Frames(layer.FadeInDuration, _canvas) || (layer.FadeOutDuration > MediaTime.Zero && local >= planned.FrameCount - EditPlanner.Frames(layer.FadeOutDuration, _canvas))
            || c.Effects.Count > 0 || c.Motion is not null || c.Stabilization is not null || c.IsReversed || c.Source is not (FileClipSource or StreamClipSource))
        {
            return null;
        }

        if (!_layers.TryGetValue(planned.Index, out ClipFrames? frames))
        {
            frames = ClipFrames.Open(c, _plan, _canvas, _context);
            _layers[planned.Index] = frames;
        }

        if (frames.PlanesBefore(Limit(Math.Min(local, planned.ClipFrames - 1)))?.Raster is not YuvRaster<byte> { HasAlpha: false } planes || planes.ChromaFormat == ChromaFormat.Yuv400)
        {
            return null;
        }

        Rect area = Area(layer, planes.Width, planes.Height);
        int align = planes.ChromaFormat == ChromaFormat.Yuv444 ? 1 : 2;
        bool aligned = area.X % align == 0 && area.Width % align == 0 && (planes.ChromaFormat != ChromaFormat.Yuv420 || (area.Y % 2 == 0 && area.Height % 2 == 0));
        bool inside = area.X >= 0 && area.Y >= 0 && area.X + area.Width <= _canvas.Width && area.Y + area.Height <= _canvas.Height;
        return aligned && inside ? (planes, area) : null;
    }

    private YuvRaster<byte> Mixed(YuvRaster<byte> like)
    {
        if (_mixed is null || _mixed.ChromaFormat != like.ChromaFormat)
        {
            _mixed?.Dispose();
            _mixed = YuvRaster.Create(_canvas.Width, _canvas.Height, like.ChromaFormat, like.Color, context: _context.Egami);
        }

        return _mixed;
    }

    /// <summary>The main track's frame as planes: a clip's own, or a fade or hard wipe between two.</summary>
    private YuvRaster<byte>? TrackPlanes(long n)
    {
        int current = ClipAt(n);
        PlannedClip clip = _plan.Clips[current];
        long local = n - clip.StartFrame;
        YuvRaster<byte>? to = SimplePlanes(clip, local);
        if (to is null)
        {
            return null;
        }

        if (local >= clip.TransitionFrames || current == 0)
        {
            Release(current);
            return to;
        }

        Transition transition = _plan.Edit.Track[current].Transition!;
        if (transition.Kind is not (TransitionKind.Fade or TransitionKind.Wipe) || (transition.Kind == TransitionKind.Wipe && transition.Softness > 0))
        {
            return null;
        }

        PlannedClip previous = _plan.Clips[current - 1];
        YuvRaster<byte>? from = SimplePlanes(previous, n - previous.StartFrame);
        if (from is null || from.ChromaFormat != to.ChromaFormat)
        {
            return null;
        }

        YuvRaster<byte> mixed = Mixed(to);
        double p = Math.Clamp(Keyframe.Ease(transition.Easing, (double)local / clip.TransitionFrames), 0, 1);
        for (int plane = 0; plane < 3; plane++)
        {
            YuvPlane<byte> a = Plane(from, plane), b = Plane(to, plane), target = Plane(mixed, plane);
            if (transition.Kind == TransitionKind.Fade)
            {
                MixPlane(a, b, p, target);
            }
            else
            {
                WipePlane(a, b, p, transition.Direction, target);
            }
        }

        return mixed;
    }

    /// <summary>A clip's planes at a frame when they can go straight out: 8-bit, the canvas size, no fit, fade, effect or motion.</summary>
    private YuvRaster<byte>? SimplePlanes(PlannedClip clip, long local)
    {
        Clip c = clip.Clip;
        if (c.Effects.Count > 0 || c.Motion is not null || c.Stabilization is not null || c.Source is not (FileClipSource or StreamClipSource) || c.IsReversed
            || local < EditPlanner.Frames(c.FadeInDuration, _canvas) || (c.FadeOutDuration > MediaTime.Zero && local >= clip.FrameCount - EditPlanner.Frames(c.FadeOutDuration, _canvas)))
        {
            return null;
        }

        if (!_clips.TryGetValue(clip.Index, out ClipFrames? frames))
        {
            frames = ClipFrames.Open(clip.Clip, _plan, _canvas, _context);
            _clips[clip.Index] = frames;
        }

        return frames.PlanesBefore(Limit(local))?.Raster is YuvRaster<byte> { HasAlpha: false } planes && planes.Width == _canvas.Width && planes.Height == _canvas.Height
            && planes.ChromaFormat != ChromaFormat.Yuv400 ? planes : null;
    }

    /// <summary>Lets go of the clips before the one showing, as <see cref="Render"/> does.</summary>
    private void Release(int current)
    {
        foreach (int index in _clips.Keys.Where(i => i < current).ToList())
        {
            _clips[index].Dispose();
            _clips.Remove(index);
        }
    }

    private static YuvPlane<byte> Plane(YuvRaster<byte> raster, int plane) => plane switch
    {
        0 => raster.Y,
        1 => raster.U!,
        _ => raster.V!,
    };

    private static void MixPlane(YuvPlane<byte> a, YuvPlane<byte> b, double p, YuvPlane<byte> target)
    {
        int w = (int)Math.Round(p * 65536), keep = 65536 - w;
        for (int y = 0; y < target.Height; y++)
        {
            Span<byte> s = a.Row(y), t = b.Row(y), d = target.Row(y);
            for (int x = 0; x < d.Length; x++)
            {
                d[x] = (byte)(((s[x] * keep) + (t[x] * w) + 32768) >> 16);
            }
        }
    }

    /// <summary>A hard wipe on one plane; the edge falls where the RGBA wipe's does, in the plane's own resolution.</summary>
    private static void WipePlane(YuvPlane<byte> a, YuvPlane<byte> b, double p, TransitionDirection direction, YuvPlane<byte> target)
    {
        bool horizontal = direction is TransitionDirection.Left or TransitionDirection.Right;
        int extent = horizontal ? target.Width : target.Height;
        double edge = p * extent;
        for (int y = 0; y < target.Height; y++)
        {
            Span<byte> s = a.Row(y), t = b.Row(y), d = target.Row(y);
            if (!horizontal)
            {
                double dy = direction == TransitionDirection.Up ? extent - y - 0.5 : y + 0.5;
                (dy < edge ? t : s).CopyTo(d);
                continue;
            }

            // The uncovered part is a run from one side: copy the two runs.
            int uncovered = Math.Clamp((int)Math.Ceiling(edge - 0.5), 0, d.Length);
            if (direction == TransitionDirection.Left)
            {
                s[..(d.Length - uncovered)].CopyTo(d);
                t[(d.Length - uncovered)..].CopyTo(d[(d.Length - uncovered)..]);
            }
            else
            {
                t[..uncovered].CopyTo(d);
                s[uncovered..].CopyTo(d[uncovered..]);
            }
        }
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
            frames = ClipFrames.Open(clip.Clip, _plan, _canvas, _context);
            _clips[clip.Index] = frames;
        }

        Raster<Rgba8>? source = frames.FrameBefore(Limit(local));
        using Raster<Rgba8>? steady = source is not null && clip.Clip.Stabilization is { } stabilization && frames.FrameTime is MediaTime shown
            ? Stabilize(clip.Index, stabilization, source, shown) : null;
        source = steady ?? source;
        if (source is null)
        {
            ClipFrames.Fill(target, _canvas.Background);
        }
        else if (clip.Clip.Motion is PanZoomPath motion)
        {
            PanZoom(source, target, motion.At(clip.FrameCount <= 1 ? 0 : (double)local / (clip.FrameCount - 1)));
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
            frames = ClipFrames.Open(layer.Clip, _plan, _canvas, _context);
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

    /// <summary>
    /// Draws a part of a frame (fractions of it) fitted to the canvas, as the canvas fit says, with a sub-pixel bicubic
    /// transform from the whole frame, so a slow pan or zoom moves smoothly.
    /// </summary>
    private void PanZoom(Raster<Rgba8> source, Raster<Rgba8> target, System.Drawing.RectangleF part)
    {
        double x = part.X * source.Width, y = part.Y * source.Height;
        double w = Math.Max(1e-3, part.Width * source.Width), h = Math.Max(1e-3, part.Height * source.Height);
        double sx = target.Width / w, sy = target.Height / h;
        if (_canvas.Fit is ClipFit.Contain or ClipFit.Cover)
        {
            sx = sy = _canvas.Fit == ClipFit.Contain ? Math.Min(sx, sy) : Math.Max(sx, sy);
        }

        double ox = (target.Width - (w * sx)) / 2, oy = (target.Height - (h * sy)) / 2;
        Matrix3x2 matrix = Matrix3x2.CreateTranslation((float)-x, (float)-y) * Matrix3x2.CreateScale((float)sx, (float)sy) * Matrix3x2.CreateTranslation((float)ox, (float)oy);
        using Raster<Rgba8> moved = source.Derive(ops => ops.Transform(matrix, new TransformOptions { Viewport = new Rect(0, 0, target.Width, target.Height) }));
        ClipFrames.Fill(target, _canvas.Background);
        target.Apply(ops => ops.DrawImage(moved, new Point(0, 0)));
    }

    /// <summary>Warps a source frame onto the clip's smooth camera path; the uncovered edges zoomed away, black or blurred.</summary>
    private Raster<Rgba8> Stabilize(int clip, ClipStabilization stabilization, Raster<Rgba8> source, MediaTime time)
    {
        if (!_paths.TryGetValue(clip, out StabilizationPath? path))
        {
            path = new StabilizationPath(stabilization.Motion, stabilization.Options);
            _paths[clip] = path;
        }

        // The motion was measured at its own size; the frame may be another (a smaller proxy measured, say).
        Matrix3x2 correction = path.CorrectionAt(time);
        float kx = (float)source.Width / stabilization.Motion.Width, ky = (float)source.Height / stabilization.Motion.Height;
        if (kx != 1 || ky != 1)
        {
            correction = Matrix3x2.CreateScale(1 / kx, 1 / ky) * correction * Matrix3x2.CreateScale(kx, ky);
        }

        Raster<Rgba8> warped = source.Derive(ops => ops.Transform(correction, new TransformOptions { Viewport = new Rect(0, 0, source.Width, source.Height) }));
        if (stabilization.Options.Edges != StabilizeEdges.Blur)
        {
            if (stabilization.Options.Edges == StabilizeEdges.Black)
            {
                Raster<Rgba8> black = Raster.Create(source.Width, source.Height, Rgba8.Black, _context.Egami);
                black.Apply(ops => ops.DrawImage(warped, new Point(0, 0)));
                warped.Dispose();
                return black;
            }

            return warped;
        }

        Raster<Rgba8> blurred = source.Derive(ops => ops.Blur(Math.Max(source.Width, source.Height) / 40f));
        blurred.Apply(ops => ops.DrawImage(warped, new Point(0, 0)));
        warped.Dispose();
        return blurred;
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
