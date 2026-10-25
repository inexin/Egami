using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>
/// Hands out a clip's frames, decoded and converted to RGBA once each. A time in the clip maps through its speed, reverse,
/// loops, freezes and hold (<see cref="ClipTiming"/>) to a source time; the frame shown is the latest source frame before it,
/// which picks the nearest frame when the canvas rate differs (dropping or repeating, like ffmpeg's <c>fps</c> filter).
/// </summary>
internal abstract class ClipFrames(ClipTiming timing) : IDisposable
{
    /// <summary>Gets the clip's timing.</summary>
    protected ClipTiming Timing { get; } = timing;

    /// <summary>Returns the frame to show at a time from the clip's start, or <see langword="null"/> when the clip has no picture. The frame stays the provider's.</summary>
    /// <param name="limit">The middle of the output frame's slot, from the clip's start.</param>
    public Raster<Rgba8>? FrameBefore(MediaTime limit) => SourceFrame(Timing.SourceAt(limit.Seconds));

    public abstract void Dispose();

    public static ClipFrames Open(Clip clip, EditPlan plan, EditCanvas canvas, MediaContext context)
    {
        SourceInfo info = plan.Sources[clip];
        ClipTiming timing = new(clip, info);
        return clip.Source switch
        {
            StackClipSource stack => new StackFrames(clip, stack, plan, canvas, timing, context),
            StillClipSource still => new StillFrames(still, timing, context),
            AnimationClipSource animation => new AnimationFrames(animation, timing, context),
            ColorClipSource color => new ColorFrames(color, canvas, timing, context),
            AppClipSource app => new AppFrames(app.Source, canvas, timing, context),
            _ => new FileFrames(clip, info, timing, context),
        };
    }

    /// <summary>
    /// Returns the decoded frame to show at a time, in the codec's own planes, for the renderer's fast path; <see langword="null"/>
    /// when the clip has no such frames (stills, stacks, reversed clips).
    /// </summary>
    public YuvVideoFrame? PlanesBefore(MediaTime limit) => SourcePlanes(Timing.SourceAt(limit.Seconds));

    /// <summary>Returns the frame for a source time from the trimmed source's start.</summary>
    protected abstract Raster<Rgba8>? SourceFrame(double source);

    /// <summary>Returns the decoded planes for a source time, or <see langword="null"/>.</summary>
    protected virtual YuvVideoFrame? SourcePlanes(double source) => null;

    /// <summary>Copies one raster's pixels into another of the same size.</summary>
    public static void Copy(Raster<Rgba8> source, Raster<Rgba8> target)
    {
        for (int y = 0; y < source.Height; y++)
        {
            source.Row(y).CopyTo(target.Row(y));
        }
    }

    /// <summary>Sets every pixel of a raster to a colour.</summary>
    public static void Fill(Raster<Rgba8> target, Rgba8 color)
    {
        for (int y = 0; y < target.Height; y++)
        {
            target.Row(y).Fill(color);
        }
    }

    /// <summary>Fits a frame into a raster of a given size, as <see cref="ClipFit"/> says, over a background.</summary>
    public static void Fit(Raster<Rgba8> source, Raster<Rgba8> target, ClipFit fit, Rgba8 background)
    {
        int w = target.Width, h = target.Height;
        if (source.Width == w && source.Height == h)
        {
            Copy(source, target);
            return;
        }

        (int sw, int sh) = fit switch
        {
            ClipFit.Stretch => (w, h),
            ClipFit.None => (source.Width, source.Height),
            _ => Scaled(source.Width, source.Height, w, h, fit == ClipFit.Cover),
        };
        Fill(target, background);
        using Raster<Rgba8> scaled = sw == source.Width && sh == source.Height ? source.Derive(_ => { }) : source.Derive(ops => ops.Resize(sw, sh));
        int x = (w - sw) / 2, y = (h - sh) / 2;
        for (int row = Math.Max(0, -y); row < sh && row + y < h; row++)
        {
            int from = Math.Max(0, -x), count = Math.Min(sw - from, w - Math.Max(0, x));
            if (count > 0)
            {
                scaled.Row(row).Slice(from, count).CopyTo(target.Row(row + y)[Math.Max(0, x)..]);
            }
        }
    }

    private static (int Width, int Height) Scaled(int width, int height, int w, int h, bool cover)
    {
        double scale = cover ? Math.Max((double)w / width, (double)h / height) : Math.Min((double)w / width, (double)h / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}

/// <summary>
/// A file or stream clip's frames. Forwards it decodes on; a jump back (a loop) seeks. Backwards it decodes one keyframe
/// interval at a time and hands its frames out from the end, so only one interval is held.
/// </summary>
internal sealed class FileFrames : ClipFrames
{
    private readonly VideoReader? _reader;
    private readonly MediaContext _context;
    private readonly MediaTime _in;
    private readonly List<YuvVideoFrame> _window = [];
    private YuvVideoFrame? _next;
    private YuvVideoFrame? _planes;
    private Raster<Rgba8>? _current;
    private MediaTime _currentTime;
    private bool _ended;

    public FileFrames(Clip clip, SourceInfo info, ClipTiming timing, MediaContext context)
        : base(timing)
    {
        _context = context;
        _in = info.In;
        if (!info.HasVideo)
        {
            _ended = true;
            return;
        }

        VideoReaderOptions options = new() { AutoOrient = true };
        _reader = clip.Source switch
        {
            StreamClipSource s => VideoReader.Open(s.Stream, options, context, leaveOpen: true),
            FileClipSource f => VideoReader.Open(f.Path, options, context),
            _ => throw new InvalidOperationException(),
        };
        if (_in > MediaTime.Zero && !timing.Reversed)
        {
            _reader.Seek(_in);
        }
    }

    public override void Dispose()
    {
        _next?.Dispose();
        _planes?.Dispose();
        _current?.Dispose();
        ClearWindow();
        _reader?.Dispose();
    }

    protected override Raster<Rgba8>? SourceFrame(double source)
    {
        if (_reader is null)
        {
            return null;
        }

        MediaTime target = Times.Add(_in, MediaTime.FromSeconds(source));
        if (Timing.Reversed)
        {
            return Backwards(target);
        }

        if (Forwards(target) is { } planes)
        {
            // Converted once a source frame, however many output frames show it.
            _current ??= planes.ToRaster<Rgba8>(_context.Egami);
        }

        return _current;
    }

    protected override YuvVideoFrame? SourcePlanes(double source) =>
        _reader is null || Timing.Reversed ? null : Forwards(Times.Add(_in, MediaTime.FromSeconds(source)));

    private YuvVideoFrame? Forwards(MediaTime target)
    {
        if (_planes is not null && target < _currentTime)
        {
            // A jump back, as a loop starts again: seek, and take the frames from there.
            _next?.Dispose();
            _next = null;
            Drop();
            _ended = false;
            _reader!.Seek(target);
        }

        while (!_ended)
        {
            _next ??= _reader!.ReadFrame();
            if (_next is null)
            {
                _ended = true;
                break;
            }

            // A frame exactly at the slot's middle counts as before it, as in FFmpeg's fps filter.
            if (_planes is not null && _next.Timestamp > target)
            {
                break;
            }

            // The next frame is due (or nothing is shown yet): it becomes the current one.
            Drop();
            _planes = _next;
            _next = null;
            _currentTime = _planes.Timestamp;
        }

        return _planes;
    }

    private void Drop()
    {
        _planes?.Dispose();
        _planes = null;
        _current?.Dispose();
        _current = null;
    }

    private Raster<Rgba8>? Backwards(MediaTime target)
    {
        if (_window.Count == 0 || target < _window[0].Timestamp || target > Times.Add(_window[^1].Timestamp, Spacing()))
        {
            Fill(target);
        }

        // The latest frame at or before the target; before the window's first, the first.
        YuvVideoFrame? pick = null;
        foreach (YuvVideoFrame frame in _window)
        {
            if (frame.Timestamp > target && pick is not null)
            {
                break;
            }

            pick = frame;
        }

        if (pick is null)
        {
            return _current;
        }

        if (_current is null || pick.Timestamp != _currentTime)
        {
            _current?.Dispose();
            _current = pick.ToRaster<Rgba8>(_context.Egami);
            _currentTime = pick.Timestamp;
        }

        return _current;
    }

    /// <summary>Decodes the keyframe interval that holds a time: from the keyframe at or before it up to it.</summary>
    private void Fill(MediaTime target)
    {
        ClearWindow();
        _next?.Dispose();
        _next = null;
        _reader!.Seek(target, SeekMode.Keyframe);
        while (_reader.ReadFrame() is { } frame)
        {
            if (frame.Timestamp > target && _window.Count > 0)
            {
                frame.Dispose();
                break;
            }

            _window.Add(frame);
        }
    }

    private MediaTime Spacing() => _window.Count > 1 ? Times.Sub(_window[^1].Timestamp, _window[^2].Timestamp) : MediaTime.Zero;

    private void ClearWindow()
    {
        foreach (YuvVideoFrame frame in _window)
        {
            frame.Dispose();
        }

        _window.Clear();
    }
}

/// <summary>A still image's frames: the image, the same at every time.</summary>
internal sealed class StillFrames : ClipFrames
{
    private readonly Raster<Rgba8> _image;
    private readonly bool _owned;

    public StillFrames(StillClipSource still, ClipTiming timing, MediaContext context)
        : base(timing)
    {
        _owned = still.Image is null;
        _image = still.Image ?? Raster.Load<Rgba8>(still.Path!, context.Egami);
    }

    public override void Dispose()
    {
        if (_owned)
        {
            _image.Dispose();
        }
    }

    protected override Raster<Rgba8> SourceFrame(double source) => _image;
}

/// <summary>An animated image's frames at their delays.</summary>
internal sealed class AnimationFrames : ClipFrames
{
    private readonly Raster<Rgba8> _animation;
    private readonly double[] _starts;
    private Raster<Rgba8>? _current;
    private int _index = -1;

    public AnimationFrames(AnimationClipSource animation, ClipTiming timing, MediaContext context)
        : base(timing)
    {
        _animation = Raster.Load<Rgba8>(animation.Path, context.Egami);
        _starts = Starts(_animation);
    }

    /// <summary>The start time of each frame, from the delays (0.1 s where a frame has none).</summary>
    public static double[] Starts(Raster<Rgba8> animation)
    {
        double[] starts = new double[animation.FrameCount + 1];
        for (int i = 0; i < animation.FrameCount; i++)
        {
            TimeSpan delay = animation.Frames[i].Metadata.Delay;
            starts[i + 1] = starts[i] + (delay > TimeSpan.Zero ? delay.TotalSeconds : 0.1);
        }

        return starts;
    }

    public override void Dispose()
    {
        _current?.Dispose();
        _animation.Dispose();
    }

    protected override Raster<Rgba8> SourceFrame(double source)
    {
        int index = Math.Clamp(Array.BinarySearch(_starts, source) is int i && i >= 0 ? i : ~i - 1, 0, _animation.FrameCount - 1);
        if (index != _index)
        {
            _current?.Dispose();
            _current = _animation.CopyFrame(index);
            _index = index;
        }

        return _current!;
    }
}

/// <summary>A colour clip's frames, the canvas size.</summary>
internal sealed class ColorFrames(ColorClipSource color, EditCanvas canvas, ClipTiming timing, MediaContext context) : ClipFrames(timing)
{
    private readonly Raster<Rgba8> _frame = Raster.Create(canvas.Width, canvas.Height, color.Color, context.Egami);

    public override void Dispose() => _frame.Dispose();

    protected override Raster<Rgba8> SourceFrame(double source) => _frame;
}

/// <summary>An app's frames.</summary>
internal sealed class AppFrames(IFrameSource source, EditCanvas canvas, ClipTiming timing, MediaContext context) : ClipFrames(timing)
{
    private readonly Raster<Rgba8> _frame = Raster.Create<Rgba8>(source.Size.Width, source.Size.Height, context.Egami);

    public override void Dispose() => _frame.Dispose();

    protected override Raster<Rgba8> SourceFrame(double time)
    {
        // The renderer asks for the middle of an output frame's slot; the app draws the frame at its start.
        double start = time - (0.5 * canvas.FrameRate.Denominator / canvas.FrameRate.Numerator);
        source.Render(MediaTime.FromSeconds(Math.Clamp(start, 0, source.Duration.Seconds)), _frame);
        return _frame;
    }
}

/// <summary>A stack's frames: its clips' frames laid out side by side, above each other or in a grid.</summary>
internal sealed class StackFrames : ClipFrames
{
    private readonly StackClipSource _stack;
    private readonly ClipFrames[] _children;
    private readonly Rect[] _cells;
    private readonly double[] _lengths;
    private readonly Raster<Rgba8> _frame;
    private readonly double _in;
    private Raster<Rgba8>[] _scratch;

    public StackFrames(Clip clip, StackClipSource stack, EditPlan plan, EditCanvas canvas, ClipTiming timing, MediaContext context)
        : base(timing)
    {
        _stack = stack;
        SourceInfo info = plan.Sources[clip];
        _in = info.In.Seconds;
        _frame = Raster.Create<Rgba8>(info.Size.Width, info.Size.Height, stack.Options.Background, context.Egami);
        _children = [.. stack.Clips.Select(c => Open(c, plan, canvas, context))];
        _lengths = [.. stack.Clips.Select(c => new ClipTiming(c, plan.Sources[c]).Length)];
        _cells = new Rect[_children.Length];
        SourceInfo first = plan.Sources[stack.Clips[0]];
        int gap = stack.Options.Gap, x = 0, y = 0;
        for (int i = 0; i < _children.Length; i++)
        {
            Size size = plan.Sources[stack.Clips[i]].Size;
            switch (stack.Layout)
            {
                case StackLayout.Horizontal:
                    int w = size.Height <= 0 ? first.Size.Width : Math.Max(1, (int)Math.Round((double)size.Width * first.Size.Height / size.Height));
                    _cells[i] = new Rect(x, 0, w, first.Size.Height);
                    x += w + gap;
                    break;
                case StackLayout.Vertical:
                    int h = size.Width <= 0 ? first.Size.Height : Math.Max(1, (int)Math.Round((double)size.Height * first.Size.Width / size.Width));
                    _cells[i] = new Rect(0, y, first.Size.Width, h);
                    y += h + gap;
                    break;
                default:
                    int column = i % stack.Columns, row = i / stack.Columns;
                    _cells[i] = new Rect(column * (first.Size.Width + gap), row * (first.Size.Height + gap), first.Size.Width, first.Size.Height);
                    break;
            }
        }

        _scratch = [.. _cells.Select(c => Raster.Create<Rgba8>(c.Width, c.Height, context.Egami))];
    }

    protected override Raster<Rgba8>? SourceFrame(double source)
    {
        double local = _in + source;
        for (int i = 0; i < _children.Length; i++)
        {
            // A clip that has ended holds its last frame.
            Raster<Rgba8>? frame = _children[i].FrameBefore(MediaTime.FromSeconds(Math.Min(local, Math.Max(0, _lengths[i] - 1e-6))));
            Rect cell = _cells[i];
            if (frame is null)
            {
                Fill(_scratch[i], _stack.Options.Background);
            }
            else
            {
                Fit(frame, _scratch[i], _stack.Layout == StackLayout.Grid ? _stack.Options.Fit : ClipFit.Stretch, _stack.Options.Background);
            }

            for (int row = 0; row < cell.Height; row++)
            {
                _scratch[i].Row(row).CopyTo(_frame.Row(cell.Y + row)[cell.X..]);
            }
        }

        return _frame;
    }

    public override void Dispose()
    {
        foreach (ClipFrames child in _children)
        {
            child.Dispose();
        }

        foreach (Raster<Rgba8> scratch in _scratch)
        {
            scratch.Dispose();
        }

        _scratch = [];
        _frame.Dispose();
    }
}
