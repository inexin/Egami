using System.Buffers;

using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// The coverage masks of a run's glyphs gathered into rows, so a line of text blends through its sink in one call a row instead of
/// one call per glyph row, with the same pixels as glyph by glyph. That holds while no two glyphs blend the same pixel:
/// <list type="bullet">
/// <item>On a premultiplied 8-bit target, coverage 0 leaves a pixel exactly as it is, so glyphs may share a row's span as long as
/// no pixel has coverage from two of them.</item>
/// <item>On a straight 8-bit target a blend can round a pixel it does not cover (alpha is premultiplied for the blend and divided
/// out after), so each row blends only the spans of the glyph rows that have coverage there, as glyph by glyph does, and glyph
/// boxes may not overlap.</item>
/// </list>
/// A glyph that breaks either rule, or falls outside the strip, first flushes what is gathered. A canvas keeps one strip, whose
/// buffer stays all zero between runs.
/// </summary>
internal sealed class GlyphStrip : IDisposable
{
    /// <summary>The most bytes a strip holds.</summary>
    private const int MaxBytes = 1 << 20;

    private readonly List<Rect> _boxes = [];
    private bool _premultiplied;
    private Rect _clip;
    private byte[]? _buffer;
    private Rect _area;
    private int _top = int.MaxValue;
    private int _bottom = int.MinValue;
    private int _left = int.MaxValue;
    private int _right = int.MinValue;

    /// <summary>
    /// Starts a run through a sink on a target of <typeparamref name="TPixel"/>; returns false where gathering would not give the
    /// same pixels: anything but a solid paint's plain sink on an 8-bit RGBA or BGRA target.
    /// </summary>
    internal bool Begin<TPixel>(ISpanSink sink, Rect clip)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (sink is not SolidSink<TPixel>)
        {
            return false;
        }

        if (typeof(TPixel) == typeof(PremulBgra8) || typeof(TPixel) == typeof(PremulRgba8))
        {
            _premultiplied = true;
        }
        else if (typeof(TPixel) == typeof(Bgra8) || typeof(TPixel) == typeof(Rgba8))
        {
            _premultiplied = false;
        }
        else
        {
            return false;
        }

        // A new run starts a new strip at its first glyph.
        _clip = clip;
        _area = default;
        return true;
    }

    /// <summary>Adds a glyph's visible coverage, flushing first when it does not fit; returns false when it is too large to gather.</summary>
    internal bool Add(GlyphMask mask, Rect placed, Rect visible, ISpanSink sink)
    {
        if (!Fits(mask, placed, visible))
        {
            Flush(sink);
            if (!Start(visible))
            {
                return false;
            }
        }

        for (int y = visible.Y; y < visible.Bottom; y++)
        {
            ReadOnlySpan<byte> from = mask.Coverage.AsSpan(((y - placed.Y) * placed.Width) + (visible.X - placed.X), visible.Width);
            Span<byte> to = Row(y).Slice(visible.X - _area.X, visible.Width);
            for (int i = 0; i < from.Length; i++)
            {
                to[i] |= from[i];
            }
        }

        _boxes.Add(visible);
        _top = Math.Min(_top, visible.Y);
        _bottom = Math.Max(_bottom, visible.Bottom);
        _left = Math.Min(_left, visible.X);
        _right = Math.Max(_right, visible.Right);
        return true;
    }

    /// <summary>Blends what is gathered and empties the strip.</summary>
    internal void Flush(ISpanSink sink)
    {
        if (_boxes.Count == 0)
        {
            return;
        }

        // Only the columns glyphs reached hold coverage; the rest of the buffer stays zero.
        for (int y = _top; y < _bottom; y++)
        {
            Span<byte> row = Row(y);
            Span<byte> used = row.Slice(_left - _area.X, _right - _left);
            if (_premultiplied)
            {
                int first = used.IndexOfAnyExcept((byte)0);
                if (first >= 0)
                {
                    int last = used.LastIndexOfAnyExcept((byte)0);
                    sink.Blend(y, _left + first, used[first..(last + 1)]);
                }
            }
            else
            {
                BlendSpans(y, row, sink);
            }

            used.Clear();
        }

        _boxes.Clear();
        _top = int.MaxValue;
        _bottom = int.MinValue;
        _left = int.MaxValue;
        _right = int.MinValue;
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    // A straight target's row: the glyph rows with coverage, as glyph by glyph blends them; touching ones go in one call.
    private void BlendSpans(int y, Span<byte> row, ISpanSink sink)
    {
        Span<(int Left, int Right)> spans = _boxes.Count <= 64 ? stackalloc (int, int)[_boxes.Count] : new (int, int)[_boxes.Count];
        int count = 0;
        foreach (Rect box in _boxes)
        {
            if (y >= box.Y && y < box.Bottom && row.Slice(box.X - _area.X, box.Width).ContainsAnyExcept((byte)0))
            {
                spans[count++] = (box.X, box.Right);
            }
        }

        spans[..count].Sort();
        for (int i = 0; i < count;)
        {
            int left = spans[i].Left, right = spans[i].Right;
            for (i++; i < count && spans[i].Left == right; i++)
            {
                right = spans[i].Right;
            }

            sink.Blend(y, left, row.Slice(left - _area.X, right - left));
        }
    }

    private bool Fits(GlyphMask mask, Rect placed, Rect visible)
    {
        if (_buffer is null || _area.IsEmpty || visible.X < _area.X || visible.Y < _area.Y || visible.Right > _area.Right || visible.Bottom > _area.Bottom)
        {
            return false;
        }

        if (!_premultiplied)
        {
            foreach (Rect box in _boxes)
            {
                if (!box.Intersect(visible).IsEmpty)
                {
                    return false;
                }
            }

            return true;
        }

        // A pixel two glyphs both cover would blend once instead of twice.
        for (int y = visible.Y; y < visible.Bottom; y++)
        {
            ReadOnlySpan<byte> from = mask.Coverage.AsSpan(((y - placed.Y) * placed.Width) + (visible.X - placed.X), visible.Width);
            ReadOnlySpan<byte> to = Row(y).Slice(visible.X - _area.X, visible.Width);
            for (int i = 0; i < from.Length; i++)
            {
                if (from[i] != 0 && to[i] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A new strip from the glyph's top left: as wide as the clip allows and a few glyph heights tall, so a line of text fits.
    private bool Start(Rect visible)
    {
        int height = Math.Max(visible.Height, 1);
        Rect area = new Rect(visible.X, visible.Y - (2 * height), Math.Min(_clip.Right - visible.X, 2048), 5 * height).Intersect(_clip);
        if (area.IsEmpty || area.Width < visible.Width || (long)area.Width * area.Height > MaxBytes)
        {
            return false;
        }

        // The buffer is all zero between flushes, whatever the area's shape, so only a new one needs clearing.
        int bytes = area.Width * area.Height;
        if (_buffer is null || _buffer.Length < bytes)
        {
            Dispose();
            _buffer = ArrayPool<byte>.Shared.Rent(bytes);
            _buffer.AsSpan().Clear();
        }

        _area = area;
        return visible.Y >= area.Y && visible.Bottom <= area.Bottom;
    }

    private Span<byte> Row(int y) => _buffer.AsSpan((y - _area.Y) * _area.Width, _area.Width);
}
