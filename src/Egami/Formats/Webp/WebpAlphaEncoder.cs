namespace Egami.Formats.Webp;

/// <summary>
/// Writes the ALPH chunk of a lossy file (RFC 9649 section 2.7.1.2): the alpha plane, its levels reduced first when the alpha
/// quality is below 100, filtered (none, horizontal, vertical or gradient) and compressed as a headerless VP8L image whose green
/// channel carries the alpha, or stored raw when that is smaller. Low efforts try the filter an entropy estimate favours (and no
/// filter where that is likely to win, as libwebp does); efforts 5 and 6 try all four and keep the smallest.
/// </summary>
internal static class WebpAlphaEncoder
{
    /// <summary>Returns the ALPH payload for <paramref name="alpha"/>, one byte per pixel, rows top-down.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> alpha, int width, int height, int quality, int effort)
    {
        byte[] plane = alpha.ToArray();
        bool reduce = quality < 100;
        if (reduce)
        {
            // libwebp's mapping: quality 0 to 70 gives 2 to 16 levels, 70 to 100 gives 16 to 256.
            int levels = quality <= 70 ? 2 + (quality / 5) : 16 + ((quality - 70) * 8);
            ReduceLevels(plane, levels);
        }

        byte[]? best = null;
        byte[] filtered = new byte[plane.Length];
        foreach (int filter in Filters(plane, width, height, effort))
        {
            Filter(plane, width, height, filter, filtered);
            byte[] payload = Compress(filtered, width, height, filter, reduce, effort);
            if (best is null || payload.Length < best.Length)
            {
                best = payload;
            }
        }

        return best!;
    }

    private static byte[] Compress(byte[] data, int width, int height, int filter, bool reduce, int effort)
    {
        byte header = (byte)((filter << 2) | (reduce ? 1 << 4 : 0));
        uint[] argb = new uint[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            argb[i] = (uint)data[i] << 8;
        }

        using Vp8lBitWriter bits = new(data.Length);
        Vp8lEncoder.Encode(argb, width, height, new Vp8lEncoderOptions(effort) { Headerless = true, NoColorCache = true }, bits);
        ReadOnlySpan<byte> lossless = bits.Finish();
        if (lossless.Length < data.Length)
        {
            byte[] payload = new byte[1 + lossless.Length];
            payload[0] = (byte)(header | 1);
            lossless.CopyTo(payload.AsSpan(1));
            return payload;
        }

        byte[] raw = new byte[1 + data.Length];
        raw[0] = header;
        data.CopyTo(raw, 1);
        return raw;
    }

    /// <summary>The filters to try at this effort.</summary>
    private static IEnumerable<int> Filters(byte[] plane, int width, int height, int effort)
    {
        if (effort >= 5)
        {
            return [0, 1, 2, 3];
        }

        Span<bool> seen = stackalloc bool[256];
        int colors = 0;
        foreach (byte a in plane)
        {
            if (!seen[a])
            {
                seen[a] = true;
                colors++;
            }
        }

        if (colors <= 16)
        {
            return [0];
        }

        int estimate = EstimateBestFilter(plane, width, height);
        return estimate != 0 && (effort > 3 || colors > 192) ? [estimate, 0] : [estimate];
    }

    /// <summary>The filter whose residuals have the lowest entropy over every other row and column.</summary>
    private static int EstimateBestFilter(byte[] plane, int width, int height)
    {
        Span<int> histogram = stackalloc int[4 * 256];
        histogram.Clear();
        int samples = 0;
        for (int y = 2; y < height; y += 2)
        {
            int row = y * width;
            for (int x = 2; x < width; x += 2)
            {
                int a = plane[row + x], left = plane[row + x - 1], top = plane[row - width + x], topLeft = plane[row - width + x - 1];
                histogram[a]++;
                histogram[256 + (byte)(a - left)]++;
                histogram[512 + (byte)(a - top)]++;
                histogram[768 + (byte)(a - Math.Clamp(left + top - topLeft, 0, 255))]++;
                samples++;
            }
        }

        if (samples == 0)
        {
            return 0;
        }

        int best = 0;
        double bestBits = double.MaxValue;
        for (int filter = 0; filter < 4; filter++)
        {
            double bits = 0;
            foreach (int count in histogram.Slice(filter * 256, 256))
            {
                if (count > 0)
                {
                    bits -= count * Math.Log2((double)count / samples);
                }
            }

            if (bits < bestBits)
            {
                bestBits = bits;
                best = filter;
            }
        }

        return best;
    }

    /// <summary>Applies an alpha filter: each value minus its prediction from the original plane, the inverse of <see cref="WebpAlpha.Unfilter"/>.</summary>
    internal static void Filter(byte[] plane, int width, int height, int filter, byte[] output)
    {
        if (filter == 0)
        {
            plane.CopyTo(output, 0);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int pred;
                if (y == 0)
                {
                    pred = x == 0 ? 0 : plane[row + x - 1];
                }
                else if (x == 0)
                {
                    pred = plane[row - width];
                }
                else
                {
                    int left = plane[row + x - 1], top = plane[row - width + x];
                    pred = filter switch
                    {
                        1 => left,
                        2 => top,
                        _ => Math.Clamp(left + top - plane[row - width + x - 1], 0, 255),
                    };
                }

                output[row + x] = (byte)(plane[row + x] - pred);
            }
        }
    }

    /// <summary>
    /// Reduces the plane to at most <paramref name="levels"/> values by k-means over its histogram. The lowest and highest values
    /// present stay as they are, so fully transparent and fully opaque pixels remain exact.
    /// </summary>
    internal static void ReduceLevels(byte[] plane, int levels)
    {
        Span<long> histogram = stackalloc long[256];
        histogram.Clear();
        foreach (byte a in plane)
        {
            histogram[a]++;
        }

        int min = 0, max = 255, distinct = 0;
        while (histogram[min] == 0)
        {
            min++;
        }

        while (histogram[max] == 0)
        {
            max--;
        }

        foreach (long count in histogram)
        {
            distinct += count > 0 ? 1 : 0;
        }

        if (distinct <= levels)
        {
            return;
        }

        Span<double> centers = stackalloc double[levels];
        for (int i = 0; i < levels; i++)
        {
            centers[i] = min + ((double)(max - min) * i / (levels - 1));
        }

        Span<int> assign = stackalloc int[256];
        Span<double> sums = stackalloc double[levels];
        Span<long> counts = stackalloc long[levels];
        for (int iteration = 0; iteration < 8; iteration++)
        {
            sums.Clear();
            counts.Clear();
            int c = 0;
            for (int v = min; v <= max; v++)
            {
                while (c + 1 < levels && Math.Abs(v - centers[c + 1]) <= Math.Abs(v - centers[c]))
                {
                    c++;
                }

                assign[v] = c;
                sums[c] += v * (double)histogram[v];
                counts[c] += histogram[v];
            }

            double moved = 0;
            for (int i = 1; i < levels - 1; i++)
            {
                if (counts[i] > 0)
                {
                    double center = sums[i] / counts[i];
                    moved += Math.Abs(center - centers[i]);
                    centers[i] = center;
                }
            }

            if (moved < 0.5)
            {
                break;
            }
        }

        Span<byte> map = stackalloc byte[256];
        for (int v = min; v <= max; v++)
        {
            map[v] = (byte)Math.Round(centers[assign[v]]);
        }

        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = map[plane[i]];
        }
    }
}
