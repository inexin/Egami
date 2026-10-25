using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>
/// Hands out a clip's frames in time order, decoded and converted to RGBA once each: the latest source frame before a time,
/// which picks the nearest frame when the canvas rate differs (dropping or repeating, like ffmpeg's <c>fps</c> filter).
/// </summary>
internal abstract class ClipFrames : IDisposable
{
    /// <summary>Returns the frame to show at a time from the clip's start, or <see langword="null"/> when the clip has no picture. The frame stays the provider's.</summary>
    /// <param name="limit">The frame shown is the latest whose time from the clip's start is before this.</param>
    public abstract Raster<Rgba8>? FrameBefore(MediaTime limit);

    public abstract void Dispose();

    public static ClipFrames Open(Clip clip, EditPlan plan, MediaContext context) => clip.Source switch
    {
        StackClipSource stack => new StackFrames(clip, stack, plan, context),
        _ => new FileFrames(clip, plan.Sources[clip], context),
    };

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

/// <summary>A file or stream clip's frames.</summary>
internal sealed class FileFrames : ClipFrames
{
    private readonly VideoReader? _reader;
    private readonly MediaContext _context;
    private readonly MediaTime _in;
    private YuvVideoFrame? _next;
    private Raster<Rgba8>? _current;
    private bool _ended;

    public FileFrames(Clip clip, SourceInfo info, MediaContext context)
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
        if (_in > MediaTime.Zero)
        {
            _reader.Seek(_in);
        }
    }

    public override Raster<Rgba8>? FrameBefore(MediaTime limit)
    {
        MediaTime target = Times.Add(_in, limit);
        while (!_ended)
        {
            _next ??= _reader!.ReadFrame();
            if (_next is null)
            {
                _ended = true;
                break;
            }

            if (_current is not null && _next.Timestamp >= target)
            {
                break;
            }

            // The next frame is due (or nothing is shown yet): it becomes the current one.
            _current?.Dispose();
            using (YuvVideoFrame frame = _next)
            {
                _next = null;
                _current = frame.ToRaster<Rgba8>(_context.Egami);
            }
        }

        return _current;
    }

    public override void Dispose()
    {
        _next?.Dispose();
        _current?.Dispose();
        _reader?.Dispose();
    }
}

/// <summary>A stack's frames: its clips' frames laid out side by side, above each other or in a grid.</summary>
internal sealed class StackFrames : ClipFrames
{
    private readonly StackClipSource _stack;
    private readonly ClipFrames[] _children;
    private readonly Rect[] _cells;
    private readonly MediaTime[] _lengths;
    private readonly Raster<Rgba8> _frame;
    private readonly MediaTime _in;
    private Raster<Rgba8>[] _scratch;

    public StackFrames(Clip clip, StackClipSource stack, EditPlan plan, MediaContext context)
    {
        _stack = stack;
        SourceInfo info = plan.Sources[clip];
        _in = info.In;
        _frame = Raster.Create<Rgba8>(info.Size.Width, info.Size.Height, stack.Options.Background, context.Egami);
        _children = [.. stack.Clips.Select(c => Open(c, plan, context))];
        _lengths = [.. stack.Clips.Select(c => Times.Sub(plan.Sources[c].Out, plan.Sources[c].In))];
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

    public override Raster<Rgba8>? FrameBefore(MediaTime limit)
    {
        MediaTime local = Times.Add(_in, limit);
        for (int i = 0; i < _children.Length; i++)
        {
            // A clip that has ended holds its last frame.
            MediaTime child = local < _lengths[i] ? local : _lengths[i];
            Raster<Rgba8>? frame = _children[i].FrameBefore(child);
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
