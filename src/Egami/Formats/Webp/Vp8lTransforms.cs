using System.Buffers;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// The forward VP8L transforms (RFC 9649 section 4) and the choices behind them: subtract green, the predictor with each tile's
/// mode chosen by the bits its residuals would cost, the color transform with each tile's multipliers found the same way, and
/// color indexing with a palette and pixel bundling.
/// </summary>
internal static class Vp8lTransforms
{
    /// <summary>Subtracts green from red and blue (section 4.2).</summary>
    internal static void SubtractGreen(Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            uint g = (p >> 8) & 0xFF;
            // Green and alpha set to all ones, so a borrow out of blue cannot reach red.
            uint redBlue = (p | 0xFF00FF00) - ((g << 16) | g);
            pixels[i] = (p & 0xFF00FF00) | (redBlue & 0x00FF00FF);
        }
    }

    /// <summary>The spatial transform combinations the estimate chooses among.</summary>
    internal enum SpatialMode
    {
        Direct,
        Spatial,
        SubtractGreen,
        SpatialSubtractGreen,
    }

    /// <summary>
    /// Ranks the four spatial combinations by the estimated bits of their four channels: the pixels as they are, with green
    /// subtracted, and each again as differences from the left pixel (standing in for the predictor), counting only pixels that
    /// differ from both their left and upper neighbours. Returns the best two.
    /// </summary>
    internal static (SpatialMode First, SpatialMode Second) RankSpatialModes(ReadOnlySpan<uint> pixels, int width)
    {
        uint[] histograms = new uint[4 * 4 * 256];
        uint left = 0xFF000000;
        uint leftGreen = 0xFF000000;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            uint green = SubtractGreenOf(p);
            if (i % width == 0)
            {
                (left, leftGreen) = i >= width ? (pixels[i - width], SubtractGreenOf(pixels[i - width])) : (0xFF000000, 0xFF000000);
            }

            // A pixel equal to its left or upper neighbour is left to the backward references, which code it almost for free.
            if (p != left && (i < width || p != pixels[i - width]))
            {
                Count(histograms, 0, p);
                Count(histograms, 1, Vp8lEncoder.Subtract(p, left));
                Count(histograms, 2, green);
                Count(histograms, 3, Vp8lEncoder.Subtract(green, leftGreen));
            }

            (left, leftGreen) = (p, green);
        }

        Span<double> bits = stackalloc double[4];
        for (int mode = 0; mode < 4; mode++)
        {
            for (int c = 0; c < 4; c++)
            {
                bits[mode] += Vp8lCost.Bits(histograms.AsSpan(((mode * 4) + c) * 256, 256));
            }
        }

        // The transforms' own data costs a little: on a near tie, fewer transforms win.
        bits[1] *= 1.01;
        bits[2] *= 1.002;
        bits[3] *= 1.012;
        int first = 0;
        for (int mode = 1; mode < 4; mode++)
        {
            first = bits[mode] < bits[first] ? mode : first;
        }

        int second = first == 0 ? 1 : 0;
        for (int mode = 0; mode < 4; mode++)
        {
            if (mode != first && bits[mode] < bits[second])
            {
                second = mode;
            }
        }

        return ((SpatialMode)first, (SpatialMode)second);
    }

    private static uint SubtractGreenOf(uint p)
    {
        uint g = (p >> 8) & 0xFF;
        return (p & 0xFF00FF00) | (((p | 0xFF00FF00) - ((g << 16) | g)) & 0x00FF00FF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Count(uint[] histograms, int mode, uint p)
    {
        int at = mode * 1024;
        histograms[at + (int)(p >> 24)]++;
        histograms[at + 256 + (int)((p >> 16) & 0xFF)]++;
        histograms[at + 512 + (int)((p >> 8) & 0xFF)]++;
        histograms[at + 768 + (int)(p & 0xFF)]++;
    }

    // --- Predictor ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Chooses each tile's predictor mode (2^<paramref name="bits"/> pixels square) by the bits its residuals would cost under a
    /// model built from the residuals of the tiles chosen before, and writes the mode image (mode in green) to
    /// <paramref name="modes"/>. <paramref name="candidates"/> lists the modes to try; a single mode is used everywhere.
    /// </summary>
    internal static void ChooseModes(ReadOnlySpan<uint> pixels, int width, int height, int bits, ReadOnlySpan<int> candidates, Span<uint> modes)
    {
        int tilesX = Vp8lDecoder.SubSampleSize(width, bits);
        int tilesY = Vp8lDecoder.SubSampleSize(height, bits);
        if (candidates.Length == 1)
        {
            modes[..(tilesX * tilesY)].Fill(0xFF000000u | ((uint)candidates[0] << 8));
            return;
        }

        uint[] accumulated = ArrayPool<uint>.Shared.Rent(4 * 256);
        float[] costs = ArrayPool<float>.Shared.Rent(4 * 256);
        try
        {
            Span<uint> accum = accumulated.AsSpan(0, 4 * 256);
            Prior(accum);
            Span<float> cost = costs.AsSpan(0, 4 * 256);
            int size = 1 << bits;
            for (int ty = 0; ty < tilesY; ty++)
            {
                int y0 = ty * size;
                int y1 = Math.Min(height, y0 + size);
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = tx * size;
                    int x1 = Math.Min(width, x0 + size);
                    CostTable(accum, cost);
                    int left = tx > 0 ? (int)((modes[(ty * tilesX) + tx - 1] >> 8) & 0xF) : -1;
                    int above = ty > 0 ? (int)((modes[((ty - 1) * tilesX) + tx] >> 8) & 0xF) : -1;
                    int best = candidates[0];
                    float bestCost = float.MaxValue;
                    foreach (int mode in candidates)
                    {
                        // A mode its neighbours already use costs almost nothing in the mode image.
                        float bias = mode == left || mode == above ? -SpatialBias : 0;
                        float c = TileCost(pixels, width, x0, x1, y0, y1, mode, cost, bestCost - bias) + bias;
                        if (c < bestCost)
                        {
                            (bestCost, best) = (c, mode);
                        }
                    }

                    modes[(ty * tilesX) + tx] = 0xFF000000u | ((uint)best << 8);
                    Accumulate(pixels, width, x0, x1, y0, y1, best, accum);
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(accumulated);
            ArrayPool<float>.Shared.Return(costs);
        }
    }

    /// <summary>The bits a tile's mode or multipliers save in the transform image by matching a neighbour's.</summary>
    private const float SpatialBias = 15;

    /// <summary>Writes the residual of every pixel against its prediction (section 4.1), the modes read from <paramref name="modes"/>.</summary>
    internal static void ApplyPredictor(ReadOnlySpan<uint> pixels, int width, int height, int bits, ReadOnlySpan<uint> modes, Span<uint> residuals)
    {
        int tilesX = Vp8lDecoder.SubSampleSize(width, bits);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice(y * width, width);
            Span<uint> output = residuals.Slice(y * width, width);
            if (y == 0)
            {
                output[0] = Vp8lEncoder.Subtract(row[0], 0xFF000000);
                for (int x = 1; x < width; x++)
                {
                    output[x] = Vp8lEncoder.Subtract(row[x], row[x - 1]);
                }

                continue;
            }

            ReadOnlySpan<uint> above = pixels.Slice((y - 1) * width, width);
            output[0] = Vp8lEncoder.Subtract(row[0], above[0]);
            ReadOnlySpan<uint> tileModes = modes.Slice((y >> bits) * tilesX, tilesX);
            int size = 1 << bits;
            for (int x = 1; x < width;)
            {
                int end = Math.Min(width, ((x >> bits) + 1) * size);
                int mode = (int)((tileModes[x >> bits] >> 8) & 0xF);
                ResidualRun(mode, row, above, x, end, output);
                x = end;
            }
        }
    }

    /// <summary>Writes the residuals of row pixels x to end against mode <paramref name="mode"/>'s prediction from the original pixels.</summary>
    private static void ResidualRun(int mode, ReadOnlySpan<uint> row, ReadOnlySpan<uint> above, int x, int end, Span<uint> output)
    {
        switch (mode)
        {
            case 0:
                for (; x < end; x++)
                {
                    output[x] = Vp8lEncoder.Subtract(row[x], 0xFF000000);
                }

                break;
            case 1:
                for (; x < end; x++)
                {
                    output[x] = Vp8lEncoder.Subtract(row[x], row[x - 1]);
                }

                break;
            case 2:
                for (; x < end; x++)
                {
                    output[x] = Vp8lEncoder.Subtract(row[x], above[x]);
                }

                break;
            default:
                for (; x < end; x++)
                {
                    uint tr = x + 1 < row.Length ? above[x + 1] : row[0];
                    output[x] = Vp8lEncoder.Subtract(row[x], Vp8lPredictors.Predict(mode, row[x - 1], above[x], above[x - 1], tr));
                }

                break;
        }
    }

    /// <summary>Returns the modeled bits of a tile's residuals under one mode, stopping early once over <paramref name="limit"/>.</summary>
    private static float TileCost(ReadOnlySpan<uint> pixels, int width, int x0, int x1, int y0, int y1, int mode, ReadOnlySpan<float> cost, float limit)
    {
        float total = 0;
        int start = Math.Max(1, x0);
        for (int y = Math.Max(1, y0); y < y1; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice(y * width, width);
            ReadOnlySpan<uint> above = pixels.Slice((y - 1) * width, width);
            for (int x = start; x < x1; x++)
            {
                uint tr = x + 1 < width ? above[x + 1] : row[0];
                uint residual = Vp8lEncoder.Subtract(row[x], Vp8lPredictors.Predict(mode, row[x - 1], above[x], above[x - 1], tr));
                total += cost[(int)(residual >> 24)] + cost[256 + (int)((residual >> 16) & 0xFF)] + cost[512 + (int)((residual >> 8) & 0xFF)]
                    + cost[768 + (int)(residual & 0xFF)];
            }

            if (total >= limit)
            {
                return total;
            }
        }

        return total;
    }

    private static void Accumulate(ReadOnlySpan<uint> pixels, int width, int x0, int x1, int y0, int y1, int mode, Span<uint> accum)
    {
        int start = Math.Max(1, x0);
        for (int y = Math.Max(1, y0); y < y1; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice(y * width, width);
            ReadOnlySpan<uint> above = pixels.Slice((y - 1) * width, width);
            for (int x = start; x < x1; x++)
            {
                uint tr = x + 1 < width ? above[x + 1] : row[0];
                uint residual = Vp8lEncoder.Subtract(row[x], Vp8lPredictors.Predict(mode, row[x - 1], above[x], above[x - 1], tr));
                accum[(int)(residual >> 24)]++;
                accum[256 + (int)((residual >> 16) & 0xFF)]++;
                accum[512 + (int)((residual >> 8) & 0xFF)]++;
                accum[768 + (int)(residual & 0xFF)]++;
            }
        }
    }

    /// <summary>Starts a model of four channels with residuals near zero more likely than far ones.</summary>
    private static void Prior(Span<uint> accum)
    {
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < 256; i++)
            {
                int distance = Math.Min(i, 256 - i);
                accum[(c * 256) + i] = (uint)Math.Max(1, 64 >> Math.Min(distance, 6));
            }
        }
    }

    /// <summary>Turns counts into bits per symbol: log2(total) − log2(count + 1), channel by channel.</summary>
    private static void CostTable(ReadOnlySpan<uint> accum, Span<float> cost)
    {
        for (int c = 0; c < 4; c++)
        {
            ReadOnlySpan<uint> counts = accum.Slice(c * 256, 256);
            uint total = 0;
            foreach (uint n in counts)
            {
                total += n + 1;
            }

            float logTotal = Vp8lCost.Log2(total);
            Span<float> target = cost.Slice(c * 256, 256);
            for (int i = 0; i < 256; i++)
            {
                target[i] = logTotal - Vp8lCost.Log2(counts[i] + 1);
            }
        }
    }

    // --- Color transform ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Chooses each tile's color transform multipliers (green to red, green to blue, red to blue) by the bits the transformed red
    /// and blue would cost, searching around the left tile's values in halving steps, and writes the transform image.
    /// </summary>
    internal static void ChooseCrossColor(ReadOnlySpan<uint> pixels, int width, int height, int bits, int effort, Span<uint> elements)
    {
        int tilesX = Vp8lDecoder.SubSampleSize(width, bits);
        int tilesY = Vp8lDecoder.SubSampleSize(height, bits);
        int size = 1 << bits;
        uint[] counts = ArrayPool<uint>.Shared.Rent(512);
        float[] costs = ArrayPool<float>.Shared.Rent(512);
        try
        {
            Span<uint> redAccum = counts.AsSpan(0, 256);
            Span<uint> blueAccum = counts.AsSpan(256, 256);
            Span<float> redCost = costs.AsSpan(0, 256);
            Span<float> blueCost = costs.AsSpan(256, 256);
            for (int i = 0; i < 256; i++)
            {
                int distance = Math.Min(i, 256 - i);
                redAccum[i] = blueAccum[i] = (uint)Math.Max(1, 64 >> Math.Min(distance, 6));
            }

            int maxStep = effort >= 5 ? 64 : effort >= 3 ? 32 : 16;
            sbyte leftG2R = 0, leftG2B = 0, leftR2B = 0;
            for (int ty = 0; ty < tilesY; ty++)
            {
                int y0 = ty * size;
                int y1 = Math.Min(height, y0 + size);
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = tx * size;
                    int x1 = Math.Min(width, x0 + size);
                    Costs(redAccum, redCost);
                    Costs(blueAccum, blueCost);
                    sbyte aboveG2R = 0, aboveG2B = 0, aboveR2B = 0;
                    if (ty > 0)
                    {
                        uint e = elements[((ty - 1) * tilesX) + tx];
                        (aboveG2R, aboveG2B, aboveR2B) = ((sbyte)e, (sbyte)(e >> 8), (sbyte)(e >> 16));
                    }

                    Tile tile = new(x0, x1, y0, y1);
                    sbyte g2r = Search(pixels, width, tile, Multiplier.GreenToRed, 0, redCost, leftG2R, aboveG2R, maxStep);
                    sbyte g2b = Search(pixels, width, tile, Multiplier.GreenToBlue, leftR2B, blueCost, leftG2B, aboveG2B, maxStep);
                    sbyte r2b = Search(pixels, width, tile, Multiplier.RedToBlue, g2b, blueCost, leftR2B, aboveR2B, maxStep);
                    if (effort >= 5)
                    {
                        g2b = Search(pixels, width, tile, Multiplier.GreenToBlue, r2b, blueCost, g2b, g2b, 4);
                    }

                    elements[(ty * tilesX) + tx] = 0xFF000000u | ((uint)(byte)r2b << 16) | ((uint)(byte)g2b << 8) | (byte)g2r;
                    (leftG2R, leftG2B, leftR2B) = (g2r, g2b, r2b);
                    AccumulateColor(pixels, width, x0, x1, y0, y1, g2r, g2b, r2b, redAccum, blueAccum);
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(counts);
            ArrayPool<float>.Shared.Return(costs);
        }
    }

    /// <summary>Applies the color transform in place: red less green × g2r, blue less green × g2b and red × r2b (section 4.3).</summary>
    internal static void ApplyCrossColor(Span<uint> pixels, int width, int height, int bits, ReadOnlySpan<uint> elements)
    {
        int tilesX = Vp8lDecoder.SubSampleSize(width, bits);
        for (int y = 0; y < height; y++)
        {
            Span<uint> row = pixels.Slice(y * width, width);
            ReadOnlySpan<uint> tiles = elements.Slice((y >> bits) * tilesX, tilesX);
            for (int x = 0; x < width; x++)
            {
                uint e = tiles[x >> bits];
                row[x] = Transform(row[x], (sbyte)e, (sbyte)(e >> 8), (sbyte)(e >> 16));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Transform(uint p, sbyte g2r, sbyte g2b, sbyte r2b)
    {
        sbyte green = (sbyte)(p >> 8);
        sbyte red = (sbyte)(p >> 16);
        int newRed = (int)(p >> 16) - ((g2r * green) >> 5);
        int newBlue = (int)p - ((g2b * green) >> 5) - ((r2b * red) >> 5);
        return (p & 0xFF00FF00) | (((uint)newRed & 0xFF) << 16) | ((uint)newBlue & 0xFF);
    }

    private enum Multiplier
    {
        GreenToRed,
        GreenToBlue,
        RedToBlue,
    }

    private readonly record struct Tile(int X0, int X1, int Y0, int Y1);

    /// <summary>
    /// Finds the multiplier with the least cost: the left and upper tiles' values and zero first, then steps that halve around the
    /// best. <paramref name="other"/> is the blue multiplier held fixed while the other one is searched.
    /// </summary>
    private static sbyte Search(ReadOnlySpan<uint> pixels, int width, Tile tile, Multiplier which, sbyte other, ReadOnlySpan<float> cost, sbyte left, sbyte above, int maxStep)
    {
        // A value neither neighbour uses costs about a third of the spatial bias in the transform image.
        float Penalty(sbyte value) => value == left || value == above ? 0 : SpatialBias / 3;

        sbyte best = left;
        float bestCost = Evaluate(pixels, width, tile, which, left, other, cost);
        foreach (sbyte start in (ReadOnlySpan<sbyte>)[above, 0])
        {
            if (start != best)
            {
                float c = Evaluate(pixels, width, tile, which, start, other, cost) + Penalty(start);
                if (c < bestCost)
                {
                    (bestCost, best) = (c, start);
                }
            }
        }

        for (int step = maxStep; step >= 1; step >>= 1)
        {
            sbyte center = best;
            foreach (int delta in (ReadOnlySpan<int>)[-step, step])
            {
                int candidate = center + delta;
                if (candidate is < -128 or > 127)
                {
                    continue;
                }

                float c = Evaluate(pixels, width, tile, which, (sbyte)candidate, other, cost) + Penalty((sbyte)candidate);
                if (c < bestCost)
                {
                    (bestCost, best) = (c, (sbyte)candidate);
                }
            }
        }

        return best;
    }

    private static float Evaluate(ReadOnlySpan<uint> pixels, int width, Tile tile, Multiplier which, sbyte value, sbyte other, ReadOnlySpan<float> cost)
    {
        return which switch
        {
            Multiplier.GreenToRed => RedCost(pixels, width, tile.X0, tile.X1, tile.Y0, tile.Y1, value, cost),
            Multiplier.GreenToBlue => BlueCost(pixels, width, tile.X0, tile.X1, tile.Y0, tile.Y1, value, other, cost),
            _ => BlueCost(pixels, width, tile.X0, tile.X1, tile.Y0, tile.Y1, other, value, cost),
        };
    }

    private static float RedCost(ReadOnlySpan<uint> pixels, int width, int x0, int x1, int y0, int y1, sbyte g2r, ReadOnlySpan<float> cost)
    {
        float total = 0;
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice((y * width) + x0, x1 - x0);
            foreach (uint p in row)
            {
                int newRed = (int)(p >> 16) - ((g2r * (sbyte)(p >> 8)) >> 5);
                total += cost[newRed & 0xFF];
            }
        }

        return total;
    }

    private static float BlueCost(ReadOnlySpan<uint> pixels, int width, int x0, int x1, int y0, int y1, sbyte g2b, sbyte r2b, ReadOnlySpan<float> cost)
    {
        float total = 0;
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice((y * width) + x0, x1 - x0);
            foreach (uint p in row)
            {
                int newBlue = (int)p - ((g2b * (sbyte)(p >> 8)) >> 5) - ((r2b * (sbyte)(p >> 16)) >> 5);
                total += cost[newBlue & 0xFF];
            }
        }

        return total;
    }

    private static void AccumulateColor(ReadOnlySpan<uint> pixels, int width, int x0, int x1, int y0, int y1, sbyte g2r, sbyte g2b, sbyte r2b, Span<uint> red, Span<uint> blue)
    {
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice((y * width) + x0, x1 - x0);
            foreach (uint p in row)
            {
                uint t = Transform(p, g2r, g2b, r2b);
                red[(int)((t >> 16) & 0xFF)]++;
                blue[(int)(t & 0xFF)]++;
            }
        }
    }

    private static void Costs(ReadOnlySpan<uint> counts, Span<float> cost)
    {
        uint total = 0;
        foreach (uint n in counts)
        {
            total += n;
        }

        float logTotal = Vp8lCost.Log2(total);
        for (int i = 0; i < 256; i++)
        {
            cost[i] = logTotal - Vp8lCost.Log2(counts[i]);
        }
    }

    // --- Color indexing -----------------------------------------------------------------------------------------------------

    /// <summary>Collects up to 256 distinct colors into <paramref name="palette"/>, sorted; returns how many, or -1 for more.</summary>
    internal static int FindPalette(ReadOnlySpan<uint> pixels, Span<uint> palette)
    {
        const int TableBits = 11;
        Span<uint> keys = stackalloc uint[1 << TableBits];
        Span<bool> used = stackalloc bool[1 << TableBits];
        used.Clear();
        int count = 0;
        uint last = ~pixels[0];
        foreach (uint p in pixels)
        {
            if (p == last)
            {
                continue;
            }

            last = p;
            int slot = (int)((p * 0x1E35A7BDu) >> (32 - TableBits));
            while (used[slot] && keys[slot] != p)
            {
                slot = (slot + 1) & ((1 << TableBits) - 1);
            }

            if (!used[slot])
            {
                if (count == 256)
                {
                    return -1;
                }

                used[slot] = true;
                keys[slot] = p;
                palette[count++] = p;
            }
        }

        palette[..count].Sort();
        return count;
    }

    /// <summary>Reorders a sorted palette so each color is followed by its nearest remaining one, which keeps the coded deltas small.</summary>
    internal static void OrderByNearest(Span<uint> palette)
    {
        for (int i = 1; i < palette.Length; i++)
        {
            uint previous = palette[i - 1];
            int best = i;
            int bestDistance = int.MaxValue;
            for (int j = i; j < palette.Length; j++)
            {
                int d = Distance(previous, palette[j]);
                if (d < bestDistance)
                {
                    (bestDistance, best) = (d, j);
                }
            }

            (palette[i], palette[best]) = (palette[best], palette[i]);
        }
    }

    private static int Distance(uint a, uint b)
    {
        int total = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int d = (int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF);
            total += Math.Abs((int)(sbyte)d);
        }

        return total;
    }

    /// <summary>Returns the bundling bits for a palette size: 0 above 16 colors, 1 above 4, 2 above 2, else 3.</summary>
    internal static int BundleBits(int colors) => colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;

    /// <summary>
    /// Writes the index image: each pixel's palette index in green, bundled 2^<paramref name="bundleBits"/> to a pixel (the first
    /// in the lowest bits), and returns its width.
    /// </summary>
    internal static int ApplyPalette(ReadOnlySpan<uint> pixels, int width, int height, ReadOnlySpan<uint> palette, int bundleBits, Span<uint> output)
    {
        const int TableBits = 11;
        Span<uint> keys = stackalloc uint[1 << TableBits];
        Span<short> values = stackalloc short[1 << TableBits];
        values.Fill(-1);
        for (int i = 0; i < palette.Length; i++)
        {
            int slot = (int)((palette[i] * 0x1E35A7BDu) >> (32 - TableBits));
            while (values[slot] >= 0)
            {
                slot = (slot + 1) & ((1 << TableBits) - 1);
            }

            keys[slot] = palette[i];
            values[slot] = (short)i;
        }

        int packedWidth = Vp8lDecoder.SubSampleSize(width, bundleBits);
        int bitsPerPixel = 8 >> bundleBits;
        int perPixel = 1 << bundleBits;
        uint lastColor = ~pixels[0];
        int lastIndex = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> row = pixels.Slice(y * width, width);
            Span<uint> target = output.Slice(y * packedWidth, packedWidth);
            for (int px = 0; px < packedWidth; px++)
            {
                uint bundle = 0;
                for (int j = 0; j < perPixel; j++)
                {
                    int x = (px << bundleBits) + j;
                    if (x >= width)
                    {
                        break;
                    }

                    uint color = row[x];
                    if (color != lastColor)
                    {
                        int slot = (int)((color * 0x1E35A7BDu) >> (32 - TableBits));
                        while (keys[slot] != color || values[slot] < 0)
                        {
                            slot = (slot + 1) & ((1 << TableBits) - 1);
                        }

                        (lastColor, lastIndex) = (color, values[slot]);
                    }

                    bundle |= (uint)lastIndex << (j * bitsPerPixel);
                }

                target[px] = 0xFF000000u | (bundle << 8);
            }
        }

        return packedWidth;
    }

    // --- Near-lossless ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Near-lossless preprocessing at <paramref name="level"/> (0 to 99): each channel of a pixel whose neighbours differ from it by
    /// more than the step is rounded to a multiple of the step (2^(5 − level / 20), at most 32), so flat areas stay exact. Edges of
    /// the image are left alone.
    /// </summary>
    internal static void NearLossless(Span<uint> pixels, int width, int height, int level)
    {
        int bits = 5 - (level / 20);
        if (bits <= 0 || width < 3 || height < 3)
        {
            return;
        }

        uint[] copy = ArrayPool<uint>.Shared.Rent(width * 3);
        try
        {
            Span<uint> rows = copy.AsSpan(0, width * 3);
            pixels[..(2 * width)].CopyTo(rows);
            int limit = 1 << bits;
            for (int y = 1; y < height - 1; y++)
            {
                // Rows y − 1 and y as they were, and y + 1 still untouched in place.
                Span<uint> above = rows.Slice(((y - 1) % 3) * width, width);
                Span<uint> current = rows.Slice((y % 3) * width, width);
                Span<uint> nextCopy = rows.Slice(((y + 1) % 3) * width, width);
                pixels.Slice((y + 1) * width, width).CopyTo(nextCopy);
                Span<uint> row = pixels.Slice(y * width, width);
                for (int x = 1; x < width - 1; x++)
                {
                    uint p = current[x];
                    if (IsSmooth(p, current[x - 1], limit) && IsSmooth(p, current[x + 1], limit) && IsSmooth(p, above[x], limit) && IsSmooth(p, nextCopy[x], limit))
                    {
                        continue;
                    }

                    row[x] = Quantize(p, bits);
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(copy);
        }
    }

    private static bool IsSmooth(uint a, uint b, int limit)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) >= limit)
            {
                return false;
            }
        }

        return true;
    }

    private static uint Quantize(uint p, int bits)
    {
        uint result = 0;
        int half = 1 << (bits - 1);
        for (int shift = 0; shift < 32; shift += 8)
        {
            int v = (int)((p >> shift) & 0xFF);
            int q = Math.Min(255, ((v + half) >> bits) << bits);
            result |= (uint)q << shift;
        }

        return result;
    }
}
