namespace Egami.Formats.Av1;

/// <summary>
/// The palette search of <see cref="Av1TileEncoder"/>, after libaom's palette.c (BSD 2-clause, see THIRD-PARTY-NOTICES): a block
/// of 2 to 64 colors gets candidate palettes of up to 8 colors, each found by k-means from the block's most frequent colors,
/// then weighed by rate-distortion with the other modes.
/// </summary>
internal sealed partial class Av1TileEncoder
{
    private readonly ushort[][] _lumaPalettes = new ushort[7][];
    private readonly int[] _lumaPaletteSizes = new int[7];
    private int _lumaPaletteCount;
    private bool _mapInSearch;
    private readonly ushort[][] _chromaPalettes = new ushort[7][];
    private readonly int[] _chromaPaletteSizes = new int[7];
    private int _chromaPaletteCount;
    private readonly int[] _paletteSamples = new int[64 * 64];
    private readonly int[] _values = new int[64 * 64];
    private readonly int[] _counts = new int[64 * 64];
    private readonly long[] _keys = new long[64 * 64];

    /// <summary>Gets whether a block may use a palette: screen content tools on, and 8x8 to 64x64.</summary>
    private bool PaletteAllowed(int bsize) => _header.AllowScreenContentTools && bsize >= Bs.B8x8
        && Av1Tables.BlockSizeWide[bsize] <= 64 && Av1Tables.BlockSizeHigh[bsize] <= 64;

    /// <summary>Gets the step between the palette sizes tried, from the largest down: all of them, every other one, or the largest alone.</summary>
    private int PaletteStep => _profile.PaletteSizes switch { 0 => 8, 1 => 2, _ => 1 };

    /// <summary>Finds the current block's candidate luma palettes, from the source.</summary>
    private void FindLumaPalettes(int bsize)
    {
        _lumaPaletteCount = 0;
        if (!PaletteAllowed(bsize))
        {
            return;
        }

        int count = BlockSamples(0, uv: false);
        int distinct = Histogram(count);
        if (distinct < 2 || distinct > 64)
        {
            return;
        }

        Span<double> centroids = stackalloc double[8];
        Span<ushort> colors = stackalloc ushort[8];
        int maxValue = (1 << _sequence.BitDepth) - 1;
        for (int n = Math.Min(distinct, 8); n >= 2; n -= PaletteStep)
        {
            // The n most frequent values start the clusters (the histogram is sorted by count).
            for (int i = 0; i < n; i++)
            {
                centroids[i] = _values[i];
            }

            KMeans1(distinct, centroids[..n]);
            for (int i = 0; i < n; i++)
            {
                colors[i] = (ushort)Math.Clamp((int)Math.Round(centroids[i]), 0, maxValue);
            }

            colors[..n].Sort();
            int size = 0;
            for (int i = 0; i < n; i++)
            {
                if (size == 0 || colors[i] != colors[size - 1])
                {
                    colors[size++] = colors[i];
                }
            }

            if (size >= 2 && !HasPalette(_lumaPalettes, _lumaPaletteSizes, _lumaPaletteCount, colors[..size]))
            {
                ushort[] palette = _lumaPalettes[_lumaPaletteCount] ??= new ushort[8];
                colors[..size].CopyTo(palette);
                _lumaPaletteSizes[_lumaPaletteCount++] = size;
            }
        }
    }

    /// <summary>Finds the current block's candidate chroma palettes, U and V together, from the source.</summary>
    private void FindChromaPalettes(int bsize)
    {
        _chromaPaletteCount = 0;
        if (!PaletteAllowed(bsize))
        {
            return;
        }

        int count = BlockSamples(1, uv: true);
        int distinct = Histogram(count);
        if (distinct < 2 || distinct > 64)
        {
            return;
        }

        Span<double> centroids = stackalloc double[16];
        Span<int> pairs = stackalloc int[8];
        int maxValue = (1 << _sequence.BitDepth) - 1;
        for (int n = Math.Min(distinct, 8); n >= 2; n -= PaletteStep)
        {
            for (int i = 0; i < n; i++)
            {
                (centroids[2 * i], centroids[(2 * i) + 1]) = (_values[i] >> 16, _values[i] & 0xffff);
            }

            KMeans2(distinct, centroids[..(2 * n)]);
            for (int i = 0; i < n; i++)
            {
                int u = Math.Clamp((int)Math.Round(centroids[2 * i]), 0, maxValue);
                int v = Math.Clamp((int)Math.Round(centroids[(2 * i) + 1]), 0, maxValue);
                pairs[i] = (u << 16) | v;
            }

            // Sorted by U, as the decoder sorts the U colors it reads; V follows its U.
            pairs[..n].Sort();
            int size = 0;
            for (int i = 0; i < n; i++)
            {
                if (size == 0 || pairs[i] != pairs[size - 1])
                {
                    pairs[size++] = pairs[i];
                }
            }

            if (size < 2)
            {
                continue;
            }

            ushort[] palette = _chromaPalettes[_chromaPaletteCount] ??= new ushort[16];
            for (int i = 0; i < size; i++)
            {
                (palette[i], palette[8 + i]) = ((ushort)(pairs[i] >> 16), (ushort)(pairs[i] & 0xffff));
            }

            if (!HasPalette(_chromaPalettes, _chromaPaletteSizes, _chromaPaletteCount, palette.AsSpan(0, size), palette.AsSpan(8, size)))
            {
                _chromaPaletteSizes[_chromaPaletteCount++] = size;
            }
        }
    }

    private static bool HasPalette(ushort[][] palettes, int[] sizes, int count, ReadOnlySpan<ushort> first, ReadOnlySpan<ushort> second = default)
    {
        for (int i = 0; i < count; i++)
        {
            if (sizes[i] == first.Length && palettes[i].AsSpan(0, first.Length).SequenceEqual(first)
                && (second.IsEmpty || palettes[i].AsSpan(8, second.Length).SequenceEqual(second)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gathers the current block's source samples inside the frame into <see cref="_paletteSamples"/>: luma values, or U and V packed
    /// as <c>u &lt;&lt; 16 | v</c>; returns their count.
    /// </summary>
    private int BlockSamples(int plane, bool uv)
    {
        (_, _, int rows, int cols) = BlockDimensions(plane);
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int x0 = (_miCol * 4) >> ssx, y0 = (_miRow * 4) >> ssy;
        int stride = _frame.Strides[plane];
        ushort[] first = _source.Planes[plane];
        ushort[]? second = uv ? _source.Planes[2] : null;
        int count = 0;
        for (int r = 0; r < rows; r++)
        {
            int at = ((y0 + r) * stride) + x0;
            for (int c = 0; c < cols; c++)
            {
                _paletteSamples[count++] = second is null ? first[at + c] : (first[at + c] << 16) | second[at + c];
            }
        }

        return count;
    }

    /// <summary>Makes <see cref="_values"/> and <see cref="_counts"/> the distinct samples and their counts, most frequent first; returns how many.</summary>
    private int Histogram(int count)
    {
        Span<int> samples = _paletteSamples.AsSpan(0, count);
        samples.CopyTo(_values);
        Span<int> values = _values.AsSpan(0, count);
        values.Sort();
        int distinct = 0;
        for (int i = 0; i < count; i++)
        {
            if (distinct > 0 && values[i] == values[distinct - 1])
            {
                _counts[distinct - 1]++;
            }
            else
            {
                values[distinct] = values[i];
                _counts[distinct++] = 1;
            }
        }

        // Most frequent first; equal counts keep the lower value first.
        Span<long> keys = _keys.AsSpan(0, distinct);
        for (int i = 0; i < distinct; i++)
        {
            keys[i] = ((long)(int.MaxValue - _counts[i]) << 32) | (uint)values[i];
        }

        keys.Sort();
        for (int i = 0; i < distinct; i++)
        {
            (values[i], _counts[i]) = ((int)(uint)keys[i], int.MaxValue - (int)(keys[i] >> 32));
        }

        return distinct;
    }

    /// <summary>Runs k-means on the distinct values, each weighted by its count, from the centroids given.</summary>
    private void KMeans1(int distinct, Span<double> centroids)
    {
        int k = centroids.Length;
        Span<double> sums = stackalloc double[8];
        Span<long> weights = stackalloc long[8];
        for (int iteration = 0; iteration < 50; iteration++)
        {
            sums.Clear();
            weights.Clear();
            for (int i = 0; i < distinct; i++)
            {
                int best = Nearest1(centroids, _values[i]);
                sums[best] += (double)_values[i] * _counts[i];
                weights[best] += _counts[i];
            }

            bool moved = false;
            for (int c = 0; c < k; c++)
            {
                double next = weights[c] > 0 ? sums[c] / weights[c] : centroids[c];
                moved |= Math.Abs(next - centroids[c]) > 0.01;
                centroids[c] = next;
            }

            if (!moved)
            {
                break;
            }
        }
    }

    /// <summary>Runs k-means on the distinct U, V pairs, each weighted by its count, from the centroids given (U, V interleaved).</summary>
    private void KMeans2(int distinct, Span<double> centroids)
    {
        int k = centroids.Length / 2;
        Span<double> sums = stackalloc double[16];
        Span<long> weights = stackalloc long[8];
        for (int iteration = 0; iteration < 50; iteration++)
        {
            sums.Clear();
            weights.Clear();
            for (int i = 0; i < distinct; i++)
            {
                int u = _values[i] >> 16, v = _values[i] & 0xffff;
                int best = Nearest2(centroids, u, v);
                sums[2 * best] += (double)u * _counts[i];
                sums[(2 * best) + 1] += (double)v * _counts[i];
                weights[best] += _counts[i];
            }

            bool moved = false;
            for (int c = 0; c < k; c++)
            {
                for (int d = 0; d < 2; d++)
                {
                    double next = weights[c] > 0 ? sums[(2 * c) + d] / weights[c] : centroids[(2 * c) + d];
                    moved |= Math.Abs(next - centroids[(2 * c) + d]) > 0.01;
                    centroids[(2 * c) + d] = next;
                }
            }

            if (!moved)
            {
                break;
            }
        }
    }

    private static int Nearest1(ReadOnlySpan<double> centroids, double value)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int c = 0; c < centroids.Length; c++)
        {
            double d = Math.Abs(value - centroids[c]);
            if (d < bestDistance)
            {
                (best, bestDistance) = (c, d);
            }
        }

        return best;
    }

    private static int Nearest2(ReadOnlySpan<double> centroids, double u, double v)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int c = 0; c < centroids.Length / 2; c++)
        {
            double du = u - centroids[2 * c], dv = v - centroids[(2 * c) + 1];
            double d = (du * du) + (dv * dv);
            if (d < bestDistance)
            {
                (best, bestDistance) = (c, d);
            }
        }

        return best;
    }

    /// <summary>
    /// Gives the current block a luma palette (index into the candidates, or -1 for none) and maps each source sample to its
    /// nearest color, extending the map past the frame's edge as the decoder does.
    /// </summary>
    private void SetLumaPalette(int index)
    {
        Av1BlockInfo b = _b;
        b.PaletteSize[0] = 0;
        if (index < 0)
        {
            return;
        }

        int n = _lumaPaletteSizes[index];
        _lumaPalettes[index].AsSpan(0, n).CopyTo(b.PaletteColors);
        b.PaletteSize[0] = n;
        MapColors(0);
    }

    /// <summary>Gives the current block a chroma palette, as <see cref="SetLumaPalette"/> does for luma.</summary>
    private void SetChromaPalette(int index)
    {
        Av1BlockInfo b = _b;
        b.PaletteSize[1] = 0;
        if (index < 0)
        {
            return;
        }

        int n = _chromaPaletteSizes[index];
        _chromaPalettes[index].AsSpan(0, n).CopyTo(b.PaletteColors.AsSpan(8));
        _chromaPalettes[index].AsSpan(8, n).CopyTo(b.PaletteColors.AsSpan(16));
        b.PaletteSize[1] = n;
        MapColors(1);
    }

    private void MapColors(int plane)
    {
        Av1BlockInfo b = _b;
        int n = b.PaletteSize[plane];
        (int width, int height, int rows, int cols) = BlockDimensions(plane);
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int x0 = (_miCol * 4) >> ssx, y0 = (_miRow * 4) >> ssy;
        int stride = _frame.Strides[plane];
        byte[] map = ColorMaps[plane];
        ushort[] first = _source.Planes[plane];
        ushort[]? second = plane > 0 ? _source.Planes[2] : null;
        ReadOnlySpan<ushort> colors = b.PaletteColors;
        int offset = plane * 8;
        for (int r = 0; r < rows; r++)
        {
            int at = ((y0 + r) * stride) + x0;
            for (int c = 0; c < cols; c++)
            {
                int best = 0;
                long bestDistance = long.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    long du = first[at + c] - colors[offset + i];
                    long d = du * du;
                    if (second is not null)
                    {
                        long dv = second[at + c] - colors[16 + i];
                        d += dv * dv;
                    }

                    if (d < bestDistance)
                    {
                        (best, bestDistance) = (i, d);
                    }
                }

                map[(r * width) + c] = (byte)best;
            }

            map.AsSpan((r * width) + cols, width - cols).Fill(map[(r * width) + cols - 1]);
        }

        for (int r = rows; r < height; r++)
        {
            map.AsSpan((rows - 1) * width, width).CopyTo(map.AsSpan(r * width));
        }
    }

    /// <summary>Gets the rate of the current block's color index map in a plane.</summary>
    private long ColorMapRate(int plane)
    {
        Av1CostSink rate = new(false);
        _mapInSearch = true;
        CodeColorMap(ref rate, plane);
        _mapInSearch = false;
        return rate.Cost;
    }

    /// <summary>Keeps the chosen block's color index maps for the writer.</summary>
    private void KeepColorMaps()
    {
        Av1BlockInfo b = _b;
        if (b.PaletteSize[0] == 0 && b.PaletteSize[1] == 0)
        {
            return;
        }

        byte[][] maps = new byte[2][];
        for (int plane = 0; plane < 2; plane++)
        {
            if (b.PaletteSize[plane] > 0)
            {
                (int width, int height, _, _) = BlockDimensions(plane);
                maps[plane] = ColorMaps[plane].AsSpan(0, width * height).ToArray();
            }
        }

        lock (_decisions.ColorMaps)
        {
            _decisions.ColorMaps[b] = maps;
        }
    }

    /// <summary>Gets the map being tried while the search weighs a palette, else the chosen block's map.</summary>
    private protected override byte[] ColorMap(int plane) => _mapInSearch ? ColorMaps[plane] : base.ColorMap(plane);
}
