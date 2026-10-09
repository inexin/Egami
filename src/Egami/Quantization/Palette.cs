using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    private readonly byte[] _cache;
    private readonly ulong[] _filled;
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
        // An index per cell (palettes have at most 256 entries), and a bit per cell saying whether it is known yet: half the
        // memory of 16-bit entries with a "not yet" value, so more of the table stays in cache while dithering jumps around it.
        _cache = ArrayPool<byte>.Shared.Rent(_cacheLength);
        _filled = ArrayPool<ulong>.Shared.Rent(_cacheLength / 64);
        _filled.AsSpan(0, _cacheLength / 64).Clear();

        // The palette by channel, padded to whole vectors with entries too far away to win; the transparent entry is one of those
        // too, since it is never the nearest color.
        int padded = (palette.Length + 7) & ~7;
        _channels = new int[4 * Math.Max(padded, 8)];
        _channels.AsSpan().Fill(FarAway);
        _padded = Math.Max(padded, 8);
        for (int i = 0; i < palette.Length; i++)
        {
            if (i == transparentIndex)
            {
                continue;
            }

            _channels[i] = palette[i].R;
            _channels[_padded + i] = palette[i].G;
            _channels[(2 * _padded) + i] = palette[i].B;
            _channels[(3 * _padded) + i] = withAlpha ? palette[i].A : 0;
        }

        // The same as 16-bit pairs, red with green and blue with alpha, for multiply-add of adjacent pairs.
        _pairs = new short[4 * _padded];
        for (int i = 0; i < _padded; i++)
        {
            _pairs[2 * i] = (short)_channels[i];
            _pairs[(2 * i) + 1] = (short)_channels[_padded + i];
            _pairs[(2 * _padded) + (2 * i)] = (short)_channels[(2 * _padded) + i];
            _pairs[(2 * _padded) + (2 * i) + 1] = (short)_channels[(3 * _padded) + i];
        }
    }

    private readonly short[] _pairs;


    /// <summary>A channel value for padding entries: farther from any color than any real entry can be.</summary>
    private const int FarAway = 10_000;

    private readonly int[] _channels;
    private readonly int _padded;

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

        return (Volatile.Read(ref _filled[key >> 6]) & (1UL << key)) != 0 ? _cache[key] : Fill(key);
    }

    /// <summary>Returns the index nearest to <paramref name="pixel"/> itself, without the lookup table (for dithering's exact colors).</summary>
    internal int Nearest(Rgba8 pixel)
    {
        return Vector256.IsHardwareAccelerated ? NearestVector(pixel) : NearestScalar(pixel);
    }

    /// <summary>
    /// <see cref="NearestScalar"/> eight entries at a time: each lane keeps its smallest distance and the first index reaching
    /// it, and the lanes are then reduced to the smallest distance with the lowest index, the same answer the scalar loop gives.
    /// </summary>
    internal int NearestVector(Rgba8 pixel)
    {
        if (Avx2.IsSupported)
        {
            return NearestAvx2(pixel);
        }

        ref int r = ref MemoryMarshal.GetArrayDataReference(_channels);
        Vector256<int> pr = Vector256.Create((int)pixel.R);
        Vector256<int> pg = Vector256.Create((int)pixel.G);
        Vector256<int> pb = Vector256.Create((int)pixel.B);
        Vector256<int> pa = Vector256.Create(_withAlpha ? pixel.A : 0);
        Vector256<int> index = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<int> step = Vector256.Create(8);
        Vector256<int> best = Vector256.Create(int.MaxValue);
        Vector256<int> bestIndex = Vector256<int>.Zero;
        for (int i = 0; i < _padded; i += 8)
        {
            Vector256<int> dr = Vector256.LoadUnsafe(ref r, (nuint)i) - pr;
            Vector256<int> dg = Vector256.LoadUnsafe(ref r, (nuint)(_padded + i)) - pg;
            Vector256<int> db = Vector256.LoadUnsafe(ref r, (nuint)((2 * _padded) + i)) - pb;
            Vector256<int> da = Vector256.LoadUnsafe(ref r, (nuint)((3 * _padded) + i)) - pa;
            Vector256<int> distance = (dr * dr) + (dg * dg) + (db * db) + (da * da);
            Vector256<int> closer = Vector256.LessThan(distance, best);
            best = Vector256.ConditionalSelect(closer, distance, best);
            bestIndex = Vector256.ConditionalSelect(closer, index, bestIndex);
            index += step;
        }

        int bestDistance = int.MaxValue;
        int result = 0;
        for (int lane = 0; lane < 8; lane++)
        {
            int d = best.GetElement(lane);
            int k = bestIndex.GetElement(lane);
            if (d < bestDistance || (d == bestDistance && k < result))
            {
                bestDistance = d;
                result = k;
            }
        }

        return result;
    }

    /// <summary>
    /// <see cref="NearestVector"/> with 16-bit differences: red and green (and blue and alpha) of eight entries sit in adjacent
    /// pairs, so one multiply-add of the differences with themselves gives dr² + dg² per entry, exact in 32 bits.
    /// </summary>
    private int NearestAvx2(Rgba8 pixel)
    {
        ref short pairs = ref MemoryMarshal.GetArrayDataReference(_pairs);
        Vector256<short> rg = Vector256.Create(pixel.R | (pixel.G << 16)).AsInt16();
        Vector256<short> ba = Vector256.Create(pixel.B | ((_withAlpha ? pixel.A : 0) << 16)).AsInt16();
        Vector256<int> index = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<int> step = Vector256.Create(8);
        Vector256<int> best = Vector256.Create(int.MaxValue);
        Vector256<int> bestIndex = Vector256<int>.Zero;
        nuint second = (nuint)(2 * _padded);
        for (nuint i = 0; i < second; i += 16)
        {
            Vector256<short> d0 = Vector256.LoadUnsafe(ref pairs, i) - rg;
            Vector256<short> d1 = Vector256.LoadUnsafe(ref pairs, second + i) - ba;
            Vector256<int> distance = Avx2.MultiplyAddAdjacent(d0, d0) + Avx2.MultiplyAddAdjacent(d1, d1);
            Vector256<int> closer = Vector256.LessThan(distance, best);
            best = Vector256.Min(distance, best);
            bestIndex = Vector256.ConditionalSelect(closer, index, bestIndex);
            index += step;
        }

        // The smallest distance, then the lowest index holding it.
        Vector256<int> smallest = Vector256.Min(best, Avx2.Shuffle(best, 0b10_11_00_01));
        smallest = Vector256.Min(smallest, Avx2.Shuffle(smallest, 0b01_00_11_10));
        smallest = Vector256.Min(smallest, Avx2.Permute2x128(smallest, smallest, 0x01));
        Vector256<int> candidates = Vector256.ConditionalSelect(Vector256.Equals(best, smallest), bestIndex, Vector256.Create(int.MaxValue));
        Vector256<int> lowest = Vector256.Min(candidates, Avx2.Shuffle(candidates, 0b10_11_00_01));
        lowest = Vector256.Min(lowest, Avx2.Shuffle(lowest, 0b01_00_11_10));
        lowest = Vector256.Min(lowest, Avx2.Permute2x128(lowest, lowest, 0x01));
        return lowest.ToScalar();
    }

    /// <summary>Returns the first palette index at the smallest squared distance (the transparent entry excluded).</summary>
    internal int NearestScalar(Rgba8 pixel)
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
        ArrayPool<byte>.Shared.Return(_cache);
        ArrayPool<ulong>.Shared.Return(_filled);
    }

    private int Fill(int key)
    {
        Rgba8 center = _withAlpha
            ? new Rgba8((byte)(((key >> 15) << 3) | 4), (byte)((((key >> 10) & 31) << 3) | 4), (byte)((((key >> 5) & 31) << 3) | 4), (byte)(((key & 31) << 3) | 4))
            : new Rgba8((byte)(((key >> 12) << 2) | 2), (byte)((((key >> 6) & 63) << 2) | 2), (byte)(((key & 63) << 2) | 2), 255);
        int index = Nearest(center);
        _cache[key] = (byte)index;
        Interlocked.Or(ref _filled[key >> 6], 1UL << key);
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
    /// <param name="rowCount">How many rows there are.</param>
    /// <param name="rows">Returns row y.</param>
    /// <param name="options">The color limit and transparency threshold.</param>
    /// <param name="binaryAlpha">Whether transparency is on or off (GIF) rather than kept in the colors.</param>
    /// <param name="reserve">Entries a quantized palette leaves free (an exact one uses every entry it needs).</param>
    internal static BuiltPalette Build(int rowCount, Func<int, ReadOnlyMemory<Rgba8>> rows, PaletteOptions options, bool binaryAlpha, int reserve = 0)
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

        Rgba8[] built = wu.Build(Math.Max(2, options.MaxColors - reserve - (anyTransparent ? 1 : 0)));
        if (anyTransparent)
        {
            built = [.. built, default];
        }

        return new BuiltPalette(built, anyTransparent ? built.Length - 1 : -1, false, null);
    }
}
