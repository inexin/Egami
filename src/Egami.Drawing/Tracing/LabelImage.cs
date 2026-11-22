using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// The label image grayscale and color tracing work on: each pixel's palette entry, one byte per pixel, and the pixel's own
/// color over white, three bytes per pixel, which places edges between pixels. Regions of one label are 4-connected.
/// </summary>
internal sealed class LabelImage
{
    internal LabelImage(int width, int height, Rgba8[] palette, bool colors = true)
    {
        Width = width;
        Height = height;
        Palette = palette;
        Labels = new byte[checked(width * height)];
        Rgb = colors ? new byte[checked(width * height * 3)] : [];
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Gets the palette entry of each pixel, row by row.</summary>
    internal byte[] Labels { get; }

    /// <summary>
    /// Gets each pixel's color over white (red, green, blue), row by row, after any smoothing; empty for a label image sampled
    /// from coverage fields, whose edges are placed by the fields.
    /// </summary>
    internal byte[] Rgb { get; }

    /// <summary>Gets the palette.</summary>
    internal Rgba8[] Palette { get; private set; }

    /// <summary>The label of a pixel, or -1 outside the image.</summary>
    internal int this[int x, int y] => (uint)x < (uint)Width && (uint)y < (uint)Height ? Labels[(y * Width) + x] : -1;

    /// <summary>
    /// Quantizes a raster's first frame: to <see cref="TraceOptions.Palette"/> when it is set, else to a palette of the picture's
    /// own flat colors (<see cref="TraceOptions.Colors"/> of them at most; gray levels in grayscale mode).
    /// </summary>
    internal static LabelImage FromRaster<TPixel>(Raster<TPixel> raster, TraceOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width, height = raster.Height;
        bool gray = options.Mode == TraceMode.Grayscale;
        byte[] rgb = new byte[checked(width * height * 3)];
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            Span<TPixel> row = raster.Row(y);
            Span<byte> output = rgb.AsSpan(y * width * 3, width * 3);
            for (int x = 0; x < row.Length; x++)
            {
                Vector4 c = row[x].ToScaled();
                float alpha = Math.Clamp(c.W, 0, 1);
                float r = (alpha * Math.Clamp(c.X, 0, 1)) + (1 - alpha);
                float g = (alpha * Math.Clamp(c.Y, 0, 1)) + (1 - alpha);
                float b = (alpha * Math.Clamp(c.Z, 0, 1)) + (1 - alpha);
                if (gray)
                {
                    r = g = b = (0.212656f * r) + (0.715158f * g) + (0.072186f * b);
                }

                output[(3 * x) + 0] = (byte)((r * 255) + 0.5f);
                output[(3 * x) + 1] = (byte)((g * 255) + 0.5f);
                output[(3 * x) + 2] = (byte)((b * 255) + 0.5f);
            }
        });

        if (options.Smoothing > 0)
        {
            // One to three passes as the amount grows.
            for (int pass = 0; pass < 1 + (int)MathF.Round(options.Smoothing * 2); pass++)
            {
                rgb = Smooth(rgb, width, height, options.Smoothing, cancellationToken);
            }
        }

        // The labels are not set yet: the palette search marks the pixels it counts in them.
        LabelImage image = new(width, height, []);
        image.Palette = options.Palette is { } fixedColors ? [.. fixedColors] : BuildPalette(rgb, width, height, options.Colors, gray, cancellationToken, scratch: image.Labels);
        rgb.CopyTo(image.Rgb, 0);
        image.Assign(gray, cancellationToken);
        if (options.Palette is null)
        {
            image.AddSoftBands(options.Colors, cancellationToken);
        }

        return image;
    }

    /// <summary>
    /// Gives the middle of each soft edge a color of its own: where two palette colors meet through a blend three or more pixels
    /// wide (a blurred or JPEG-compressed edge, unlike an antialiased one, which is one pixel wide), the pixels halfway along the
    /// blend take a new palette entry, the mean of their colors, one per pair of colors, so the traced edge fades in a step as
    /// the picture does. The bands with the most pixels come first, while the palette has fewer than <paramref name="colors"/>.
    /// </summary>
    /// <param name="colors">The most colors the palette may reach.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    internal void AddSoftBands(int colors, CancellationToken cancellationToken)
    {
        int w = Width, h = Height;
        // The band pixels, each as its pair of colors (high half) and its index (low half): few, so they are listed rather than
        // marked in a map the size of the picture.
        List<long> found = [];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(2, Math.Max(2, h - 2), parallel, () => new List<long>(), (y, _, list) =>
        {
            for (int x = 2; x < w - 2; x++)
            {
                int i = (y * w) + x;
                int key = 0;
                bool sharp = false;
                for (int axis = 0; axis < 2 && !sharp; axis++)
                {
                    int step = axis == 0 ? 1 : w;
                    int a = Labels[i - (2 * step)], b = Labels[i + (2 * step)];
                    if (a == b)
                    {
                        continue;
                    }

                    if (Along(i - step, a, b) is double before && Along(i, a, b) is double middle && Along(i + step, a, b) is double after
                        && middle is >= 0.3 and <= 0.7 && before >= 0.08 && after <= 0.92 && before <= middle && middle <= after)
                    {
                        key = (Math.Min(a, b) << 8) | Math.Max(a, b);
                    }
                    else
                    {
                        sharp = true;
                    }
                }

                if (!sharp && key > 0)
                {
                    list.Add(((long)key << 32) | (uint)i);
                }
            }

            return list;
        }, list =>
        {
            lock (found)
            {
                found.AddRange(list);
            }
        });

        Dictionary<int, (long Count, long R, long G, long B)> bands = [];
        foreach (long entry in found)
        {
            int key = (int)(entry >> 32), i = (int)entry;
            bands.TryGetValue(key, out var band);
            bands[key] = (band.Count + 1, band.R + Rgb[3 * i], band.G + Rgb[(3 * i) + 1], band.B + Rgb[(3 * i) + 2]);
        }

        Dictionary<int, byte> label = [];
        foreach ((int key, var band) in bands.OrderByDescending(b => b.Value.Count).ThenBy(b => b.Key))
        {
            if (Palette.Length >= colors || band.Count < 8)
            {
                break;
            }

            label[key] = (byte)Palette.Length;
            AddColor(new Rgba8((byte)(band.R / band.Count), (byte)(band.G / band.Count), (byte)(band.B / band.Count), 255));
        }

        foreach (long entry in found)
        {
            if (label.TryGetValue((int)(entry >> 32), out byte l))
            {
                Labels[(int)entry] = l;
            }
        }

        // How far pixel i's color lies from palette color a toward b, when it lies near the line between them; otherwise null.
        double? Along(int i, int a, int b)
        {
            Rgba8 ca = Palette[a], cb = Palette[b];
            double dr = cb.R - ca.R, dg = cb.G - ca.G, db = cb.B - ca.B;
            double length = (dr * dr) + (dg * dg) + (db * db);
            if (length < 32 * 32)
            {
                return null;
            }

            double pr = Rgb[3 * i] - ca.R, pg = Rgb[(3 * i) + 1] - ca.G, pb = Rgb[(3 * i) + 2] - ca.B;
            double t = ((pr * dr) + (pg * dg) + (pb * db)) / length;
            double er = pr - (t * dr), eg = pg - (t * dg), eb = pb - (t * db);
            return (er * er) + (eg * eg) + (eb * eb) <= (0.05 * length) + 100 ? t : null;
        }
    }

    /// <summary>Sets each pixel's label to its nearest palette entry, by intensity in grayscale mode and by color otherwise.</summary>
    internal void Assign(bool gray, CancellationToken cancellationToken)
    {
        int count = Palette.Length;
        int[] pr = new int[count], pg = new int[count], pb = new int[count];
        for (int i = 0; i < count; i++)
        {
            Rgba8 c = Palette[i];
            if (gray)
            {
                pr[i] = pg[i] = pb[i] = (int)((0.212656f * c.R) + (0.715158f * c.G) + (0.072186f * c.B) + 0.5f);
            }
            else
            {
                (pr[i], pg[i], pb[i]) = (c.R, c.G, c.B);
            }
        }

        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, Height, parallel, y =>
        {
            int last = -1, lastLabel = 0;
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width) + x;
                int r = Rgb[3 * i], g = Rgb[(3 * i) + 1], b = Rgb[(3 * i) + 2];
                int key = (r << 16) | (g << 8) | b;
                if (key != last)
                {
                    int best = 0, bestDistance = int.MaxValue;
                    for (int k = 0; k < count; k++)
                    {
                        int dr = r - pr[k], dg = g - pg[k], db = b - pb[k];
                        int distance = (dr * dr) + (dg * dg) + (db * db);
                        if (distance < bestDistance)
                        {
                            (best, bestDistance) = (k, distance);
                        }
                    }

                    (last, lastLabel) = (key, best);
                }

                Labels[i] = (byte)lastLabel;
            }
        });

        // A blend along an edge takes the label of the side it is nearer, not a palette color that happens to lie between the
        // sides: an antialiased black-on-white edge stays black or white, never a thin ring of gray.
        byte[] first = [.. Labels];
        Parallel.For(0, Height, parallel, y =>
        {
            for (int x = 0; x < Width; x++)
            {
                int source = BlendSource(Rgb, Width, Height, x, y);
                if (source >= 0)
                {
                    Labels[(y * Width) + x] = first[source];
                }
            }
        });

        // Along a shallow edge the pixels on either side are blends too, so the test above misses some; a pixel that lies
        // nearer the line between two colors around it than its own color is a blend of those two and takes the nearer.
        byte[] second = [.. Labels];
        Parallel.For(0, Height, parallel, y =>
        {
            Span<int> around = stackalloc int[9];
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width) + x;
                int own = second[i], count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx < (uint)Width && (uint)ny < (uint)Height)
                        {
                            int label = second[(ny * Width) + nx];
                            if (label != own && !around[..count].Contains(label))
                            {
                                around[count++] = label;
                            }
                        }
                    }
                }

                if (count >= 2)
                {
                    Labels[i] = (byte)Unblend(Rgb[3 * i], Rgb[(3 * i) + 1], Rgb[(3 * i) + 2], own, around[..count], pr, pg, pb);
                }
            }
        });
    }

    /// <summary>
    /// The label a pixel of color (r, g, b) labelled <paramref name="own"/> takes: when it lies within a quarter of its distance
    /// to its own color from the line between two of the <paramref name="around"/> colors, well inside it, the nearer of the two;
    /// otherwise its own.
    /// </summary>
    private static int Unblend(int r, int g, int b, int own, ReadOnlySpan<int> around, int[] pr, int[] pg, int[] pb)
    {
        double or = r - pr[own], og = g - pg[own], ob = b - pb[own];
        double ownDistance = (or * or) + (og * og) + (ob * ob);
        if (ownDistance < 24 * 24)
        {
            return own;
        }

        int best = own;
        double bestResidual = ownDistance / 16;
        for (int m = 0; m < around.Length; m++)
        {
            for (int n = m + 1; n < around.Length; n++)
            {
                int a = around[m], c = around[n];
                double dr = pr[c] - pr[a], dg = pg[c] - pg[a], db = pb[c] - pb[a];
                double length = (dr * dr) + (dg * dg) + (db * db);
                if (length < 40 * 40)
                {
                    continue;
                }

                double qr = r - pr[a], qg = g - pg[a], qb = b - pb[a];
                double t = ((qr * dr) + (qg * dg) + (qb * db)) / length;
                if (t is < 0.05 or > 0.95)
                {
                    continue;
                }

                double er = qr - (t * dr), eg = qg - (t * dg), eb = qb - (t * db);
                double residual = (er * er) + (eg * eg) + (eb * eb);
                if (residual < bestResidual)
                {
                    (best, bestResidual) = (t < 0.5 ? a : c, residual);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Builds a palette of the picture's own flat colors: a weighted 15-bit histogram that leaves out the blends along edges,
    /// split by median cut (Heckbert 1982) into at most <paramref name="colors"/> boxes, refined by weighted k-means (Lloyd), with
    /// colors closer than a few levels merged.
    /// </summary>
    /// <param name="rgb">The pixels' colors, three bytes each.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="colors">The most colors.</param>
    /// <param name="gray">Whether the palette is gray levels.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <param name="alpha">Each pixel's alpha, when pixels under half opaque are to be left out; otherwise <see langword="null"/>.</param>
    /// <param name="scratch">A buffer of one byte per pixel the search may use, or <see langword="null"/> to allocate one.</param>
    internal static Rgba8[] BuildPalette(byte[] rgb, int width, int height, int colors, bool gray, CancellationToken cancellationToken, byte[]? alpha = null, byte[]? scratch = null)
    {
        const int Bins = 1 << 15;
        double[] weight = new double[Bins], sumR = new double[Bins], sumG = new double[Bins], sumB = new double[Bins];
        double total = 0;

        // Which pixels count, row by row in parallel; the sums are of whole numbers, so they come out the same in any order. A
        // pixel that is no blend counts 1, or 2 when it is also close in color to all four neighbours: the inside of a flat area.
        // Compression noise rings around edges in colors of its own that the blend test misses; inside flat areas it only
        // wobbles about the true color, so when enough of the picture is flat only those pixels count.
        byte[] counted = scratch ?? new byte[width * height];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        int flatCount = 0;
        Parallel.For(0, height, parallel, () => 0, (y, _, count) =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                byte kind = 0;
                if (alpha is null || alpha[i] >= 128)
                {
                    int flat = Flatness(rgb, width, height, x, y);

                    // A pixel the same as its four neighbours lies on no line between two of them that differ: no blend.
                    if (flat == 0 || !IsBlend(rgb, width, height, x, y))
                    {
                        kind = (byte)(flat <= FlatLimit ? 2 : 1);
                    }
                }

                counted[i] = kind;
                count += kind >> 1;
            }

            return count;
        }, count => Interlocked.Add(ref flatCount, count));

        byte least = (byte)(flatCount >= width * height / 8 ? 2 : 1);
        for (int i = 0; i < width * height; i++)
        {
            if (counted[i] < least)
            {
                continue;
            }

            int r = rgb[3 * i], g = rgb[(3 * i) + 1], b = rgb[(3 * i) + 2];
            int bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            weight[bin]++;
            sumR[bin] += r;
            sumG[bin] += g;
            sumB[bin] += b;
            total++;
        }

        if (total == 0)
        {
            // Every pixel is a blend (a picture smaller than its edges): count them all.
            for (int i = 0; i < width * height; i++)
            {
                if (alpha is not null && alpha[i] == 0)
                {
                    continue;
                }

                int r = rgb[3 * i], g = rgb[(3 * i) + 1], b = rgb[(3 * i) + 2];
                int bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                weight[bin]++;
                sumR[bin] += r;
                sumG[bin] += g;
                sumB[bin] += b;
            }
        }

        // The occupied bins, each at its mean color.
        List<int> used = [];
        for (int bin = 0; bin < Bins; bin++)
        {
            if (weight[bin] > 0)
            {
                used.Add(bin);
            }
        }

        int n = used.Count;
        double[] w = new double[n];
        double[][] c = [new double[n], new double[n], new double[n]];
        for (int k = 0; k < n; k++)
        {
            int bin = used[k];
            w[k] = weight[bin];
            c[0][k] = sumR[bin] / weight[bin];
            c[1][k] = sumG[bin] / weight[bin];
            c[2][k] = sumB[bin] / weight[bin];
        }

        // Cut into more boxes than colors wanted, so a large color spread by noise cannot take the boxes a small distinct color
        // needs; the extra colors are merged away below.
        List<double[]> centers = MedianCut(w, c, Math.Min(256, colors * 4));
        KMeans(w, c, centers, 10, cancellationToken);

        // Colors a few levels apart are one color with noise or a faint blend; colors covering almost nothing are leftovers of
        // blends the edge test missed. Their pixels go to the nearest color kept.
        DropRare(w, c, centers, 0.001);
        MergeClose(centers, gray ? 12 : 20);
        Reduce(w, c, centers, colors);
        for (int round = 0; round < 3; round++)
        {
            // The refined colors can draw back together: merge them again until none are close.
            KMeans(w, c, centers, 3, cancellationToken);
            int before = centers.Count;
            MergeClose(centers, gray ? 12 : 20);
            if (centers.Count == before)
            {
                break;
            }
        }

        Rgba8[] palette = new Rgba8[centers.Count];
        for (int k = 0; k < palette.Length; k++)
        {
            byte r = ToByte(centers[k][0]), g = ToByte(centers[k][1]), b = ToByte(centers[k][2]);
            palette[k] = gray ? new Rgba8(r, r, r, 255) : new Rgba8(r, g, b, 255);
        }

        return palette;
    }

    /// <summary>
    /// Whether a pixel is a blend between two of its opposite neighbours that differ clearly: the pixel lies on the color line
    /// between them, well inside it. Such pixels are an edge's antialiasing or blur, not a color of their own.
    /// </summary>
    internal static bool IsBlend(byte[] rgb, int width, int height, int x, int y) => BlendSource(rgb, width, height, x, y) >= 0;

    /// <summary>The largest squared color distance from a pixel to its four neighbours inside the picture at which it is flat.</summary>
    private const int FlatLimit = 24 * 24;

    /// <summary>The largest squared color distance from a pixel to one of its four neighbours inside the picture.</summary>
    private static int Flatness(byte[] rgb, int width, int height, int x, int y)
    {
        int p = 3 * ((y * width) + x);
        int most = 0;
        if (x > 0)
        {
            most = Math.Max(most, Distance(rgb, p, p - 3));
        }

        if (x < width - 1)
        {
            most = Math.Max(most, Distance(rgb, p, p + 3));
        }

        if (y > 0)
        {
            most = Math.Max(most, Distance(rgb, p, p - (3 * width)));
        }

        if (y < height - 1)
        {
            most = Math.Max(most, Distance(rgb, p, p + (3 * width)));
        }

        return most;

        static int Distance(byte[] rgb, int a, int b)
        {
            int dr = rgb[a] - rgb[b], dg = rgb[a + 1] - rgb[b + 1], db = rgb[a + 2] - rgb[b + 2];
            return (dr * dr) + (dg * dg) + (db * db);
        }
    }

    /// <summary>
    /// For a blend pixel (see <see cref="IsBlend"/>), the index of the neighbour whose color it is nearer on the line between its
    /// two sides; otherwise -1.
    /// </summary>
    internal static int BlendSource(byte[] rgb, int width, int height, int x, int y)
    {
        int i = (y * width) + x;
        int p = 3 * i;
        ReadOnlySpan<int> dxs = [-1, 0, -1, 1], dys = [0, -1, -1, -1];
        for (int k = 0; k < 4; k++)
        {
            int ax = x + dxs[k], ay = y + dys[k], bx = x - dxs[k], by = y - dys[k];
            if ((uint)ax >= (uint)width || (uint)ay >= (uint)height || (uint)bx >= (uint)width || (uint)by >= (uint)height)
            {
                continue;
            }

            int step = (dys[k] * width) + dxs[k];
            int a = p + (3 * step), b = p - (3 * step);
            double dr = rgb[b] - rgb[a], dg = rgb[b + 1] - rgb[a + 1], db = rgb[b + 2] - rgb[a + 2];
            double length = (dr * dr) + (dg * dg) + (db * db);
            if (length < 40 * 40)
            {
                continue;
            }

            double pr = rgb[p] - rgb[a], pg = rgb[p + 1] - rgb[a + 1], pb = rgb[p + 2] - rgb[a + 2];
            double t = ((pr * dr) + (pg * dg) + (pb * db)) / length;
            if (t is < 0.12 or > 0.88)
            {
                continue;
            }

            double er = pr - (t * dr), eg = pg - (t * dg), eb = pb - (t * db);
            if ((er * er) + (eg * eg) + (eb * eb) <= (0.04 * length) + 64)
            {
                return t < 0.5 ? i + step : i - step;
            }
        }

        return -1;
    }

    /// <summary>Median cut over weighted colors: splits the box of most weighted spread at the weighted median of its widest channel.</summary>
    private static List<double[]> MedianCut(double[] w, double[][] c, int colors)
    {
        // The boxes are ranges of one index array, sorted in place, so splitting allocates nothing.
        int[] order = new int[w.Length];
        for (int k = 0; k < order.Length; k++)
        {
            order[k] = k;
        }

        double[] keys = new double[w.Length];
        List<(int Start, int Length)> boxes = [(0, w.Length)];
        while (boxes.Count < colors)
        {
            int best = -1, bestChannel = 0;
            double bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Length < 2)
                {
                    continue;
                }

                for (int ch = 0; ch < 3; ch++)
                {
                    double score = Spread(order.AsSpan(boxes[i].Start, boxes[i].Length), w, c[ch]);
                    if (score > bestScore)
                    {
                        (best, bestChannel, bestScore) = (i, ch, score);
                    }
                }
            }

            if (best < 0)
            {
                break;
            }

            (int start, int length) = boxes[best];
            double[] channel = c[bestChannel];
            for (int k = start; k < start + length; k++)
            {
                keys[k] = channel[order[k]];
            }

            Array.Sort(keys, order, start, length);
            double half = 0, sum = 0;
            for (int k = start; k < start + length; k++)
            {
                half += w[order[k]];
            }

            half /= 2;
            int cut = 1;
            for (int k = 0; k < length - 1; k++)
            {
                sum += w[order[start + k]];
                cut = k + 1;
                if (sum >= half)
                {
                    break;
                }
            }

            boxes[best] = (start, cut);
            boxes.Add((start + cut, length - cut));
        }

        List<double[]> centers = [];
        foreach ((int start, int length) in boxes)
        {
            double sw = 0, r = 0, g = 0, b = 0;
            foreach (int k in order.AsSpan(start, length))
            {
                sw += w[k];
                r += w[k] * c[0][k];
                g += w[k] * c[1][k];
                b += w[k] * c[2][k];
            }

            centers.Add([r / sw, g / sw, b / sw]);
        }

        return centers;
    }

    /// <summary>The weighted sum of squared deviations of one channel in a box.</summary>
    private static double Spread(ReadOnlySpan<int> box, double[] w, double[] channel)
    {
        double sw = 0, sum = 0, squares = 0;
        foreach (int k in box)
        {
            sw += w[k];
            sum += w[k] * channel[k];
            squares += w[k] * channel[k] * channel[k];
        }

        return squares - (sum * sum / sw);
    }

    /// <summary>Weighted k-means (Lloyd's iterations) of the colors around the centers; empty clusters are dropped.</summary>
    private static void KMeans(double[] w, double[][] c, List<double[]> centers, int iterations, CancellationToken cancellationToken)
    {
        int n = w.Length;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int k = centers.Count;
            double[] sw = new double[k], sr = new double[k], sg = new double[k], sb = new double[k];
            for (int i = 0; i < n; i++)
            {
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int j = 0; j < k; j++)
                {
                    double dr = c[0][i] - centers[j][0], dg = c[1][i] - centers[j][1], db = c[2][i] - centers[j][2];
                    double distance = (dr * dr) + (dg * dg) + (db * db);
                    if (distance < bestDistance)
                    {
                        (best, bestDistance) = (j, distance);
                    }
                }

                sw[best] += w[i];
                sr[best] += w[i] * c[0][i];
                sg[best] += w[i] * c[1][i];
                sb[best] += w[i] * c[2][i];
            }

            bool moved = false;
            for (int j = k - 1; j >= 0; j--)
            {
                if (sw[j] == 0)
                {
                    centers.RemoveAt(j);
                    moved = true;
                    continue;
                }

                double[] next = [sr[j] / sw[j], sg[j] / sw[j], sb[j] / sw[j]];
                moved |= Math.Abs(next[0] - centers[j][0]) + Math.Abs(next[1] - centers[j][1]) + Math.Abs(next[2] - centers[j][2]) > 0.01;
                centers[j] = next;
            }

            if (!moved)
            {
                break;
            }
        }
    }

    /// <summary>Drops the centers whose nearest colors weigh less than <paramref name="share"/> of the total, while any are kept.</summary>
    private static void DropRare(double[] w, double[][] c, List<double[]> centers, double share)
    {
        double[] weight = new double[centers.Count];
        double total = 0;
        for (int i = 0; i < w.Length; i++)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int j = 0; j < centers.Count; j++)
            {
                double dr = c[0][i] - centers[j][0], dg = c[1][i] - centers[j][1], db = c[2][i] - centers[j][2];
                double distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    (best, bestDistance) = (j, distance);
                }
            }

            weight[best] += w[i];
            total += w[i];
        }

        for (int j = centers.Count - 1; j >= 0 && centers.Count > 1; j--)
        {
            if (weight[j] < share * total)
            {
                centers.RemoveAt(j);
            }
        }
    }

    /// <summary>
    /// Merges pairs of centers until at most <paramref name="colors"/> are left, each time the pair whose merge adds the least
    /// squared error (Ward 1963), into their weighted mean.
    /// </summary>
    private static void Reduce(double[] w, double[][] c, List<double[]> centers, int colors)
    {
        if (centers.Count <= colors)
        {
            return;
        }

        List<double> weight = [.. new double[centers.Count]];
        for (int i = 0; i < w.Length; i++)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int j = 0; j < centers.Count; j++)
            {
                double dr = c[0][i] - centers[j][0], dg = c[1][i] - centers[j][1], db = c[2][i] - centers[j][2];
                double distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    (best, bestDistance) = (j, distance);
                }
            }

            weight[best] += w[i];
        }

        while (centers.Count > colors)
        {
            int a = 0, b = 1;
            double least = double.MaxValue;
            for (int i = 0; i < centers.Count; i++)
            {
                for (int j = i + 1; j < centers.Count; j++)
                {
                    double dr = centers[i][0] - centers[j][0], dg = centers[i][1] - centers[j][1], db = centers[i][2] - centers[j][2];
                    double sum = weight[i] + weight[j];
                    double cost = sum > 0 ? weight[i] * weight[j] / sum * ((dr * dr) + (dg * dg) + (db * db)) : 0;
                    if (cost < least)
                    {
                        (a, b, least) = (i, j, cost);
                    }
                }
            }

            double wa = weight[a], wb = weight[b], total = wa + wb;
            double fa = total > 0 ? wa / total : 0.5, fb = 1 - fa;
            centers[a] = [(fa * centers[a][0]) + (fb * centers[b][0]), (fa * centers[a][1]) + (fb * centers[b][1]), (fa * centers[a][2]) + (fb * centers[b][2])];
            weight[a] = total;
            centers.RemoveAt(b);
            weight.RemoveAt(b);
        }
    }

    /// <summary>Merges the closest pair of centers while they are nearer than <paramref name="levels"/> levels.</summary>
    private static void MergeClose(List<double[]> centers, double levels)
    {
        while (centers.Count > 1)
        {
            int a = -1, b = -1;
            double best = levels * levels;
            for (int i = 0; i < centers.Count; i++)
            {
                for (int j = i + 1; j < centers.Count; j++)
                {
                    double dr = centers[i][0] - centers[j][0], dg = centers[i][1] - centers[j][1], db = centers[i][2] - centers[j][2];
                    double distance = (dr * dr) + (dg * dg) + (db * db);
                    if (distance < best)
                    {
                        (a, b, best) = (i, j, distance);
                    }
                }
            }

            if (a < 0)
            {
                return;
            }

            centers[a] = [(centers[a][0] + centers[b][0]) / 2, (centers[a][1] + centers[b][1]) / 2, (centers[a][2] + centers[b][2]) / 2];
            centers.RemoveAt(b);
        }
    }

    /// <summary>
    /// One pass of edge-keeping smoothing for photos: a bilateral filter (Tomasi and Manduchi 1998) whose radius and color range
    /// grow with <paramref name="amount"/>, so texture and noise flatten while edges between clearly different colors stay put.
    /// </summary>
    internal static byte[] Smooth(byte[] rgb, int width, int height, float amount, CancellationToken cancellationToken)
    {
        int radius = 1 + (int)MathF.Round(amount * 3);
        double spatial = 2.0 * (radius / 2.0) * (radius / 2.0) + 1e-9;
        double range = 20 + (60 * amount);
        double range2 = 2 * range * range;
        double[] spatialWeight = new double[((2 * radius) + 1) * ((2 * radius) + 1)];
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                spatialWeight[((dy + radius) * ((2 * radius) + 1)) + dx + radius] = Math.Exp(-((dx * dx) + (dy * dy)) / spatial);
            }
        }

        double[] rangeWeight = new double[(3 * 255 * 255) + 1];
        for (int d = 0; d < rangeWeight.Length; d++)
        {
            rangeWeight[d] = Math.Exp(-d / range2);
        }

        byte[] output = new byte[rgb.Length];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, height, parallel, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int p = 3 * ((y * width) + x);
                int r0 = rgb[p], g0 = rgb[p + 1], b0 = rgb[p + 2];
                double sw = 0, sr = 0, sg = 0, sb = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, height - 1);
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, width - 1);
                        int q = 3 * ((yy * width) + xx);
                        int dr = rgb[q] - r0, dg = rgb[q + 1] - g0, db = rgb[q + 2] - b0;
                        double weight = spatialWeight[((dy + radius) * ((2 * radius) + 1)) + dx + radius] * rangeWeight[(dr * dr) + (dg * dg) + (db * db)];
                        sw += weight;
                        sr += weight * rgb[q];
                        sg += weight * rgb[q + 1];
                        sb += weight * rgb[q + 2];
                    }
                }

                output[p] = ToByte(sr / sw);
                output[p + 1] = ToByte(sg / sw);
                output[p + 2] = ToByte(sb / sw);
            }
        });

        return output;
    }

    /// <summary>
    /// Labels the 4-connected regions of equal label, numbered in the order a row-by-row scan meets their first pixel. Returns
    /// the number of regions.
    /// </summary>
    internal int LabelRegions(int[] regions, CancellationToken cancellationToken)
    {
        // Each row's runs of one label are joined to the runs of the same label they touch in the row above. A region's number
        // depends only on where the scan first meets it, so any order of joining gives the same numbers.
        int width = Width, height = Height;
        byte[] labels = Labels;
        using RunTable runs = new();
        int previousFirst = 0, previousEnd = 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * width;
            int first = runs.Count;
            int above = previousFirst;
            for (int x = 0; x < width;)
            {
                byte c = labels[row + x];
                int end = x + 1;
                while (end < width && labels[row + end] == c)
                {
                    end++;
                }

                int run = runs.Add(row + x);
                int[] runStart = runs.Start;

                // The runs above that overlap columns x to end − 1.
                while (above < previousEnd && RunEnd(runStart, above, previousEnd, width) <= x)
                {
                    above++;
                }

                for (int a = above; a < previousEnd && runStart[a] - (row - width) < end; a++)
                {
                    if (labels[runStart[a]] == c)
                    {
                        runs.Join(a, run);
                    }
                }

                for (int i = row + x; i < row + end; i++)
                {
                    regions[i] = run;
                }

                x = end;
            }

            (previousFirst, previousEnd) = (first, runs.Count);
        }

        // Number the regions in the order their first runs come.
        int[] number = runs.Number(out int count);

        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, height, parallel, y =>
        {
            for (int i = y * width; i < (y + 1) * width; i++)
            {
                regions[i] = number[regions[i]];
            }
        });

        System.Buffers.ArrayPool<int>.Shared.Return(number);
        return count;
    }

    /// <summary>The index one past the last pixel's column of run <paramref name="run"/>, whose row's runs end at <paramref name="last"/>.</summary>
    private static int RunEnd(int[] runStart, int run, int last, int width)
    {
        int start = runStart[run];
        int end = run + 1 < last ? runStart[run + 1] : ((start / width) + 1) * width;
        return end - ((start / width) * width);
    }

    /// <summary>
    /// Merges every region of fewer than <paramref name="minArea"/> pixels into the neighbour it shares the most pixel edges with,
    /// smallest first, until each region left is at least that large or has no neighbour.
    /// </summary>
    internal void RemoveNoise(int minArea, CancellationToken cancellationToken)
    {
        if (minArea <= 1)
        {
            return;
        }

        int[] regions = new int[Labels.Length];
        int count = LabelRegions(regions, cancellationToken);
        long[] size = new long[count];
        int[] first = new int[count];
        int[] nextPixel = new int[Labels.Length];
        int[] last = new int[count];
        Array.Fill(first, -1);
        for (int i = 0; i < regions.Length; i++)
        {
            int r = regions[i];
            size[r]++;
            nextPixel[i] = -1;
            if (first[r] < 0)
            {
                first[r] = i;
            }
            else
            {
                nextPixel[last[r]] = i;
            }

            last[r] = i;
        }

        List<int> small = [];
        for (int r = 0; r < count; r++)
        {
            if (size[r] < minArea)
            {
                small.Add(r);
            }
        }

        if (small.Count == 0)
        {
            return;
        }

        byte[] label = new byte[count];
        int[] parent = new int[count];
        for (int r = 0; r < count; r++)
        {
            parent[r] = r;
            label[r] = Labels[first[r]];
        }

        small.Sort((a, b) => size[a] != size[b] ? size[a].CompareTo(size[b]) : a.CompareTo(b));
        Dictionary<int, int> shared = [];
        foreach (int r in small)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int root = Find(parent, r);
            if (size[root] >= minArea)
            {
                continue;
            }

            // Count the pixel edges the region shares with each neighbouring region.
            shared.Clear();
            for (int i = first[root]; i >= 0; i = nextPixel[i])
            {
                int x = i % Width, y = i / Width;
                if (x > 0)
                {
                    Count(shared, parent, root, regions[i - 1]);
                }

                if (x + 1 < Width)
                {
                    Count(shared, parent, root, regions[i + 1]);
                }

                if (y > 0)
                {
                    Count(shared, parent, root, regions[i - Width]);
                }

                if (y + 1 < Height)
                {
                    Count(shared, parent, root, regions[i + Width]);
                }
            }

            int into = -1, most = 0;
            foreach ((int neighbour, int edges) in shared)
            {
                if (edges > most || (edges == most && (size[neighbour] > size[into] || (size[neighbour] == size[into] && neighbour < into))))
                {
                    (into, most) = (neighbour, edges);
                }
            }

            if (into < 0)
            {
                continue;
            }

            parent[root] = into;
            size[into] += size[root];
            nextPixel[last[into]] = first[root];
            last[into] = last[root];
        }

        for (int i = 0; i < Labels.Length; i++)
        {
            Labels[i] = label[Find(parent, regions[i])];
        }
    }

    /// <summary>Adds a color to the end of the palette.</summary>
    internal void AddColor(Rgba8 color) => Palette = [.. Palette, color];

    /// <summary>Drops palette entries no pixel uses and renumbers the labels.</summary>
    /// <returns>Each old entry's new number, or -1 for an entry dropped.</returns>
    internal int[] Compact()
    {
        int[] used = new int[Palette.Length];
        foreach (byte label in Labels)
        {
            used[label]++;
        }

        int[] map = new int[Palette.Length];
        List<Rgba8> palette = [];
        for (int k = 0; k < Palette.Length; k++)
        {
            map[k] = used[k] > 0 ? palette.Count : -1;
            if (used[k] > 0)
            {
                palette.Add(Palette[k]);
            }
        }

        if (palette.Count == Palette.Length)
        {
            return map;
        }

        for (int i = 0; i < Labels.Length; i++)
        {
            Labels[i] = (byte)map[Labels[i]];
        }

        Palette = [.. palette];
        return map;
    }

    private static void Count(Dictionary<int, int> shared, int[] parent, int root, int region)
    {
        int other = Find(parent, region);
        if (other != root)
        {
            shared[other] = shared.GetValueOrDefault(other) + 1;
        }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static int Find(int[] parent, int i)
    {
        int root = i;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[i] != root)
        {
            int next = parent[i];
            parent[i] = root;
            i = next;
        }

        return root;
    }
}
