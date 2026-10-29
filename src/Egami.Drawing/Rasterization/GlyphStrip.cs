using System.Buffers;

using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// The coverage masks of a run's glyphs gathered into rows, so a line of text blends through its sink in one call a row instead of
/// one call per glyph row, with the same pixels as glyph by glyph. On a premultiplied 8-bit target coverage 0 leaves a pixel
/// exactly as it is, so glyphs share a row's span as long as no pixel has coverage from two of them. (A straight target is left
/// out: there a blend rounds pixels it does not cover, so only glyph boxes that do not overlap could share a call, too few to pay.)
/// A glyph that would cover a pixel already gathered, or falls outside the strip, first flushes what is gathered. A canvas keeps
/// one strip, whose buffer stays all zero between runs.
/// </summary>
internal sealed class GlyphStrip : IDisposable
{
    /// <summary>The most bytes a strip holds.</summary>
    private const int MaxBytes = 1 << 20;

    private Rect _clip;
    private bool _gathered;
    private byte[]? _buffer;
    private Rect _area;
    private int _top = int.MaxValue;
    private int _bottom = int.MinValue;
    private int _left = int.MaxValue;
    private int _right = int.MinValue;

    /// <summary>
    /// Starts a run through a sink on a target of <typeparamref name="TPixel"/>; returns false where gathering would not give the
    /// same pixels: anything but a solid paint's plain sink on a premultiplied 8-bit RGBA or BGRA target.
    /// </summary>
    internal bool Begin<TPixel>(ISpanSink sink, Rect clip)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (sink is not SolidSink<TPixel> || (typeof(TPixel) != typeof(PremulBgra8) && typeof(TPixel) != typeof(PremulRgba8)))
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

        _gathered = true;
        _top = Math.Min(_top, visible.Y);
        _bottom = Math.Max(_bottom, visible.Bottom);
        _left = Math.Min(_left, visible.X);
        _right = Math.Max(_right, visible.Right);
        return true;
    }

    /// <summary>Blends what is gathered and empties the strip.</summary>
    internal void Flush(ISpanSink sink)
    {
        if (!_gathered)
        {
            return;
        }

        // Only the columns glyphs reached hold coverage; the rest of the buffer stays zero.
        for (int y = _top; y < _bottom; y++)
        {
            Span<byte> row = Row(y);
            Span<byte> used = row.Slice(_left - _area.X, _right - _left);
            int first = used.IndexOfAnyExcept((byte)0);
            if (first >= 0)
            {
                int last = used.LastIndexOfAnyExcept((byte)0);
                sink.Blend(y, _left + first, used[first..(last + 1)]);
            }

            used.Clear();
        }

        _gathered = false;
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

    private bool Fits(GlyphMask mask, Rect placed, Rect visible)
    {
        if (_buffer is null || _area.IsEmpty || visible.X < _area.X || visible.Y < _area.Y || visible.Right > _area.Right || visible.Bottom > _area.Bottom)
        {
            return false;
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
