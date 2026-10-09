using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Quantization;

/// <summary>
/// The distinct colors of an image while there are few enough for a palette: an open-addressing hash of 32-bit colors that gives
/// up once it would need more than its limit. Also maps a color to its index once the palette is final.
/// </summary>
internal sealed class ExactColors
{
    private const int Size = 1024;

    private readonly uint[] _keys = new uint[Size];
    private readonly byte[] _indices = new byte[Size];
    private readonly bool[] _used = new bool[Size];
    private readonly List<Rgba8> _colors = [];
    private readonly int _limit;
    private readonly bool _binaryAlpha;
    private readonly byte _threshold;

    /// <param name="limit">The most colors to keep, at most 256.</param>
    /// <param name="binaryAlpha">Whether every pixel below <paramref name="threshold"/> alpha is one transparent color and the rest opaque.</param>
    /// <param name="threshold">The alpha below which a pixel is transparent, when <paramref name="binaryAlpha"/> is set.</param>
    internal ExactColors(int limit, bool binaryAlpha, byte threshold)
    {
        _limit = limit;
        _binaryAlpha = binaryAlpha;
        _threshold = threshold;
    }

    /// <summary>Gets whether more distinct colors than the limit were seen; the counter then stops counting.</summary>
    internal bool Overflowed { get; private set; }

    /// <summary>Gets whether a transparent pixel was seen (binary alpha only).</summary>
    internal bool HasTransparent { get; private set; }

    /// <summary>Gets the colors seen, in first-seen order; transparent pixels (binary alpha) are not among them.</summary>
    internal IReadOnlyList<Rgba8> Colors => _colors;

    /// <summary>Adds pixels; returns false once the colors no longer fit.</summary>
    internal bool Add(ReadOnlySpan<Rgba8> pixels)
    {
        if (Overflowed)
        {
            return false;
        }

        uint last = 0;
        bool haveLast = false;
        foreach (Rgba8 pixel in pixels)
        {
            if (_binaryAlpha && pixel.A < _threshold)
            {
                if (!HasTransparent)
                {
                    HasTransparent = true;
                    if (_colors.Count + 1 > _limit)
                    {
                        Overflowed = true;
                        return false;
                    }
                }

                continue;
            }

            uint key = Key(pixel);
            if (haveLast && key == last)
            {
                continue;
            }

            last = key;
            haveLast = true;
            int slot = Slot(key, out bool found);
            if (found)
            {
                continue;
            }

            if (_colors.Count + (HasTransparent ? 1 : 0) + 1 > _limit)
            {
                Overflowed = true;
                return false;
            }

            _used[slot] = true;
            _keys[slot] = key;
            _indices[slot] = (byte)_colors.Count;
            _colors.Add(_binaryAlpha ? new Rgba8(pixel.R, pixel.G, pixel.B, 255) : pixel);
        }

        return true;
    }

    /// <summary>Returns the palette index of a color that was added (see <see cref="Colors"/>), or -1 for a transparent pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int IndexOf(Rgba8 pixel)
    {
        if (_binaryAlpha && pixel.A < _threshold)
        {
            return -1;
        }

        int slot = Slot(Key(pixel), out _);
        return _indices[slot];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Key(Rgba8 pixel)
    {
        uint key = Unsafe.As<Rgba8, uint>(ref pixel);
        return _binaryAlpha ? key | 0xFF000000u : key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(uint key, out bool found)
    {
        int slot = (int)((key * 2654435761u) >> 22);
        while (_used[slot])
        {
            if (_keys[slot] == key)
            {
                found = true;
                return slot;
            }

            slot = (slot + 1) & (Size - 1);
        }

        found = false;
        return slot;
    }
}

/// <summary>
/// Maps colors to the nearest entry of a palette by squared distance in encoded RGB (plus alpha when the palette keeps it). A
/// lookup table on the top bits of each channel answers most pixels in one load; a miss searches the palette for the cell's
/// center and fills the slot, so the answer does not depend on which pixel came first.
/// </summary>
internal sealed class PaletteMapper : IDisposable
{
    private readonly Rgba8[] _palette;
    private readonly int _transparent;
    private readonly bool _withAlpha;
    private readonly byte _threshold;
    private readonly short[] _cache;
    private readonly int _cacheLength;

    /// <param name="palette">The palette; a transparent entry, if any, is at <paramref name="transparentIndex"/>.</param>
    /// <param name="transparentIndex">The index pixels below <paramref name="threshold"/> alpha take, or -1 (binary alpha only).</param>
    /// <param name="withAlpha">Whether alpha takes part in the distance (palette PNG); otherwise alpha is on or off.</param>
    /// <param name="threshold">The alpha below which a pixel is transparent, without alpha in the distance.</param>
    internal PaletteMapper(Rgba8[] palette, int transparentIndex, bool withAlpha, byte threshold)
    {
        _palette = palette;
        _transparent = transparentIndex;
        _withAlpha = withAlpha;
        _threshold = threshold;
        _cacheLength = withAlpha ? 1 << 20 : 1 << 18;
        _cache = ArrayPool<short>.Shared.Rent(_cacheLength);
        _cache.AsSpan(0, _cacheLength).Fill(-1);
    }

    internal Rgba8[] Palette => _palette;

    internal int TransparentIndex => _transparent;

    /// <summary>Gets whether alpha takes part in the distance.</summary>
    internal bool WithAlpha => _withAlpha;

    /// <summary>Returns whether a pixel counts as transparent: fully so when alpha is kept, below the threshold otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsTransparent(Rgba8 pixel) => _withAlpha ? pixel.A == 0 : pixel.A < _threshold;

    /// <summary>Returns the index for <paramref name="pixel"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Map(Rgba8 pixel)
    {
        int key;
        if (_withAlpha)
        {
            key = ((pixel.R >> 3) << 15) | ((pixel.G >> 3) << 10) | ((pixel.B >> 3) << 5) | (pixel.A >> 3);
        }
        else
        {
            if (pixel.A < _threshold && _transparent >= 0)
            {
                return _transparent;
            }

            key = ((pixel.R >> 2) << 12) | ((pixel.G >> 2) << 6) | (pixel.B >> 2);
        }

        int index = _cache[key];
        return index >= 0 ? index : Fill(key);
    }

    /// <summary>Returns the index nearest to <paramref name="pixel"/> itself, without the lookup table (for dithering's exact colors).</summary>
    internal int Nearest(Rgba8 pixel)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < _palette.Length; i++)
        {
            if (i == _transparent)
            {
                continue;
            }

            Rgba8 c = _palette[i];
            int dr = c.R - pixel.R;
            int dg = c.G - pixel.G;
            int db = c.B - pixel.B;
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (_withAlpha)
            {
                int da = c.A - pixel.A;
                distance += da * da;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Maps a row of pixels to indices.</summary>
    internal void MapRow(ReadOnlySpan<Rgba8> pixels, Span<byte> indices)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            indices[i] = (byte)Map(pixels[i]);
        }
    }

    public void Dispose()
    {
        ArrayPool<short>.Shared.Return(_cache);
    }

    private int Fill(int key)
    {
        Rgba8 center = _withAlpha
            ? new Rgba8((byte)(((key >> 15) << 3) | 4), (byte)((((key >> 10) & 31) << 3) | 4), (byte)((((key >> 5) & 31) << 3) | 4), (byte)(((key & 31) << 3) | 4))
            : new Rgba8((byte)(((key >> 12) << 2) | 2), (byte)((((key >> 6) & 63) << 2) | 2), (byte)(((key & 63) << 2) | 2), 255);
        int index = Nearest(center);
        _cache[key] = (short)index;
        return index;
    }
}

/// <summary>A palette ready for mapping: its colors, which index (if any) is transparent, and whether it holds every color exactly.</summary>
internal sealed record BuiltPalette(Rgba8[] Colors, int TransparentIndex, bool Exact, ExactColors? ExactTable)
{
    /// <summary>Gets the bits needed to index the palette, 1 to 8.</summary>
    internal int Bits => Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)Math.Max(1, Colors.Length - 1)));
}

/// <summary>Builds palettes: exactly when the pixels have few enough colors, with Wu's quantizer otherwise.</summary>
internal static class PaletteBuilder
{
    /// <summary>
    /// Builds a palette for the pixels <paramref name="rows"/> gives (each call adds one row, index 0 to <paramref name="rowCount"/> − 1).
    /// With binary alpha (GIF), transparent pixels get one entry at the end; otherwise alpha is kept in the colors (palette PNG).
    /// </summary>
    internal static BuiltPalette Build(int rowCount, Func<int, ReadOnlyMemory<Rgba8>> rows, PaletteOptions options, bool binaryAlpha)
    {
        ExactColors exact = new(options.MaxColors, binaryAlpha, options.AlphaThreshold);
        for (int y = 0; y < rowCount && !exact.Overflowed; y++)
        {
            exact.Add(rows(y).Span);
        }

        if (!exact.Overflowed)
        {
            List<Rgba8> colors = [.. exact.Colors];
            int transparent = -1;
            if (exact.HasTransparent)
            {
                transparent = colors.Count;
                colors.Add(default);
            }

            if (colors.Count == 0)
            {
                colors.Add(default);
            }

            return new BuiltPalette([.. colors], transparent, true, exact);
        }

        using WuQuantizer wu = new(!binaryAlpha);
        bool anyTransparent = false;
        for (int y = 0; y < rowCount; y++)
        {
            ReadOnlySpan<Rgba8> row = rows(y).Span;
            wu.Add(row, binaryAlpha ? options.AlphaThreshold : (byte)0);
            if (binaryAlpha && !anyTransparent)
            {
                foreach (Rgba8 p in row)
                {
                    if (p.A < options.AlphaThreshold)
                    {
                        anyTransparent = true;
                        break;
                    }
                }
            }
        }

        Rgba8[] built = wu.Build(options.MaxColors - (anyTransparent ? 1 : 0));
        if (anyTransparent)
        {
            built = [.. built, default];
        }

        return new BuiltPalette(built, anyTransparent ? built.Length - 1 : -1, false, null);
    }
}
