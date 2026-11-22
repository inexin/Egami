using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// Logo mode: each pixel is split into the shares of the one or two palette colors it blends (and of transparency), each color's
/// shares become a smooth field (<see cref="CoverageField"/>), and the fields are sampled finer than the pixels. Edges lie where
/// the two largest fields cross, found between samples as marching squares does, then go through the same fit and layering as
/// color tracing.
/// </summary>
internal static class LogoTracer
{
    /// <summary>The most samples the fine grid has before fewer samples per pixel are taken.</summary>
    internal const int SampleBudget = 4_000_000;

    /// <summary>The most samples per pixel along each axis.</summary>
    internal const int MaxScale = 4;

    /// <summary>The share of opaque pixels that may be neither a palette color nor a blend of two before an image is refused.</summary>
    internal const double FlatShare = 0.1;

    /// <summary>The share of opaque pixels under which a color between two others counts as an edge fringe.</summary>
    private const double RareShare = 0.02;

    /// <summary>The share of a color's pixels inside it (with four neighbours of the color) under which it only lines edges.</summary>
    private const double EdgeOnly = 0.05;

    /// <summary>How close, in color levels, a rare color may lie to a more common one before it counts as a tint of it.</summary>
    private const double Close = 40;

    /// <summary>How far, in color levels, a pixel may lie from the color or blend that explains it.</summary>
    private const double Explained = 32;

    internal static TraceResult Trace<TPixel>(Raster<TPixel> raster, TraceOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = raster.Width, height = raster.Height;
        int n = checked(width * height);
        byte[] rgb = new byte[checked(n * 3)], alpha = new byte[n];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, height, parallel, y =>
        {
            Span<TPixel> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Vector4 c = row[x].ToScaled();
                int i = (y * width) + x;
                alpha[i] = ToByte(c.W);
                rgb[3 * i] = ToByte(c.X);
                rgb[(3 * i) + 1] = ToByte(c.Y);
                rgb[(3 * i) + 2] = ToByte(c.Z);
            }
        });

        bool transparent = alpha.AsSpan().ContainsAnyExcept((byte)255), visible = alpha.AsSpan().ContainsAnyExcept((byte)0);

        if (!visible)
        {
            return new TraceResult([], width, height);
        }

        // Room for transparency and, for enclosed transparency, white.
        int most = transparent ? 254 : 256;
        if (options.Palette is { Count: > 254 } && transparent)
        {
            throw new ArgumentException($"Logo mode takes at most {most} palette colors for an image with transparency.", nameof(options));
        }

        // The image's own flat colors split the pixels into colors and blends, even with a fixed palette: a blend is between the
        // colors actually drawn.
        Rgba8[] palette = LabelImage.BuildPalette(rgb, width, height, Math.Min(options.Colors, most), false, cancellationToken, transparent ? alpha : null);
        byte[] nearest = Nearest(rgb, palette, cancellationToken);
        palette = Prune(palette, ref nearest, rgb, alpha, width, cancellationToken);
        int clear = options.Palette?.Count ?? palette.Length;
        (byte[] labels, float[] weights, long unexplained, long opaque) = Split(rgb, alpha, width, height, palette, nearest, clear, cancellationToken);
        if (unexplained > 16 && unexplained > FlatShare * opaque)
        {
            throw new ArgumentException(
                $"Logo mode traces flat-color art, and {100.0 * unexplained / opaque:0}% of this image's pixels are neither one of its "
                + $"{palette.Length} colors nor a blend of two: it looks like a photo or a gradient (trace it with TraceMode.Color), "
                + "or it has more flat colors than TraceOptions.Colors allows.",
                nameof(raster));
        }

        if (options.Palette is { } fixedColors)
        {
            // Each of the image's colors takes the nearest fixed color, and the shapes take the fixed colors exactly.
            Rgba8[] own = palette;
            palette = [.. fixedColors.Select(c => new Rgba8(c.R, c.G, c.B, 255))];
            byte[] map = new byte[own.Length];
            for (int k = 0; k < own.Length; k++)
            {
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int f = 0; f < palette.Length; f++)
                {
                    double d = Distance(palette[f], own[k].R, own[k].G, own[k].B);
                    if (d < bestDistance)
                    {
                        (best, bestDistance) = (f, d);
                    }
                }

                map[k] = (byte)best;
            }

            // The third slot of each pixel holds transparency alone.
            for (int i = 0; i < labels.Length; i++)
            {
                if (i % 3 != 2 && weights[i] != 0)
                {
                    labels[i] = map[labels[i]];
                }
            }
        }

        int colors = palette.Length + (transparent ? 1 : 0);
        int scale = Scale(width, height);
        (byte[] fine, float[] margins) = CoverageField.Sample(width, height, colors, labels, weights, scale, cancellationToken);
        Rgba8[] finePalette = transparent ? [.. palette, new Rgba8(255, 255, 255, 0)] : palette;
        LabelImage image = new(width * scale, height * scale, finePalette, colors: false);
        fine.CopyTo(image.Labels, 0);
        if (transparent)
        {
            Enclose(image, clear, cancellationToken);
        }

        image.RemoveNoise(checked(options.Noise * scale * scale), cancellationToken);
        int[] renumbered = image.Compact();
        int background = transparent ? renumbered[clear] : -1;
        return ColorTracer.Trace(image, options, background, margins, scale, cancellationToken);
    }

    /// <summary>The samples per pixel along each axis for an image: the most that keep the fine grid within the budget.</summary>
    internal static int Scale(int width, int height)
    {
        double pixels = (double)width * height;
        return Math.Clamp((int)Math.Sqrt(SampleBudget / pixels), 1, MaxScale);
    }

    /// <summary>
    /// Splits each pixel into up to three colors and their shares: the palette color it is, or the two palette colors (its own
    /// and a neighbour's) it blends, scaled by its alpha, and transparency for the rest. Counts the opaque pixels that no color
    /// or blend explains.
    /// </summary>
    internal static (byte[] Labels, float[] Weights, long Unexplained, long Opaque) Split(
        byte[] rgb, byte[] alpha, int width, int height, Rgba8[] palette, byte[] nearest, int clear, CancellationToken cancellationToken)
    {
        int n = width * height;
        byte[] labels = new byte[3 * n];
        float[] weights = new float[3 * n];
        long[] unexplained = new long[height], opaque = new long[height];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, height, parallel, y =>
        {
            Span<int> candidates = stackalloc int[9];
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                float a = alpha[i] / 255f;
                if (alpha[i] < 255)
                {
                    labels[(3 * i) + 2] = (byte)clear;
                    weights[(3 * i) + 2] = 1 - a;
                }

                if (alpha[i] == 0)
                {
                    continue;
                }

                // The pixel's nearest color and its opaque neighbours' nearest colors.
                int count = 0;
                candidates[count++] = nearest[i];
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((dx | dy) == 0 || (uint)xx >= (uint)width || (uint)yy >= (uint)height || alpha[(yy * width) + xx] < 128)
                        {
                            continue;
                        }

                        int label = nearest[(yy * width) + xx];
                        if (!candidates[..count].Contains(label))
                        {
                            candidates[count++] = label;
                        }
                    }
                }

                int r = rgb[3 * i], g = rgb[(3 * i) + 1], b = rgb[(3 * i) + 2];
                int pure = candidates[0];
                double pureDistance = Distance(palette[pure], r, g, b);
                for (int k = 1; k < count; k++)
                {
                    double d = Distance(palette[candidates[k]], r, g, b);
                    if (d < pureDistance)
                    {
                        (pure, pureDistance) = (candidates[k], d);
                    }
                }

                int first = -1, second = -1;
                double t = 0, pairDistance = double.MaxValue;
                for (int p = 0; p < count; p++)
                {
                    for (int q = p + 1; q < count; q++)
                    {
                        Rgba8 ca = palette[candidates[p]], cb = palette[candidates[q]];
                        double dr = cb.R - ca.R, dg = cb.G - ca.G, db = cb.B - ca.B;
                        double length = (dr * dr) + (dg * dg) + (db * db);
                        if (length < 32 * 32)
                        {
                            continue;
                        }

                        double pr = r - ca.R, pg = g - ca.G, pb = b - ca.B;
                        double s = Math.Clamp(((pr * dr) + (pg * dg) + (pb * db)) / length, 0, 1);
                        double er = pr - (s * dr), eg = pg - (s * dg), eb = pb - (s * db);
                        double d = Math.Sqrt((er * er) + (eg * eg) + (eb * eb));
                        if (d < pairDistance)
                        {
                            (first, second, t, pairDistance) = (candidates[p], candidates[q], s, d);
                        }
                    }
                }

                // A blend only when it explains the pixel clearly better than one color does.
                bool blend = first >= 0 && pureDistance > 6 && pairDistance + 2 < pureDistance;
                if (blend)
                {
                    labels[3 * i] = (byte)first;
                    weights[3 * i] = (float)(a * (1 - t));
                    labels[(3 * i) + 1] = (byte)second;
                    weights[(3 * i) + 1] = (float)(a * t);
                }
                else
                {
                    labels[3 * i] = (byte)pure;
                    weights[3 * i] = a;
                }

                if (alpha[i] >= 128)
                {
                    opaque[y]++;
                    if ((blend ? pairDistance : pureDistance) > Explained)
                    {
                        unexplained[y]++;
                    }
                }
            }
        });

        return (labels, weights, unexplained.Sum(), opaque.Sum());
    }

    /// <summary>
    /// Transparency the shapes enclose is painted white: each transparent region that does not reach the image's edge takes the
    /// palette's white (added when there is none), which <see cref="TraceOptions.IgnoreWhite"/> then leaves out like any white.
    /// </summary>
    private static void Enclose(LabelImage image, int clear, CancellationToken cancellationToken)
    {
        int w = image.Width, h = image.Height;
        int[] regions = new int[image.Labels.Length];
        int count = image.LabelRegions(regions, cancellationToken);
        bool[] outside = new bool[count], isClear = new bool[count];
        for (int i = 0; i < regions.Length; i++)
        {
            if (image.Labels[i] == clear)
            {
                isClear[regions[i]] = true;
                int x = i % w, y = i / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                {
                    outside[regions[i]] = true;
                }
            }
        }

        int white = -1;
        for (int i = 0; i < regions.Length; i++)
        {
            int r = regions[i];
            if (isClear[r] && !outside[r])
            {
                if (white < 0)
                {
                    white = Array.IndexOf(image.Palette, Rgba8.White);
                    if (white < 0)
                    {
                        image.AddColor(Rgba8.White);
                        white = image.Palette.Length - 1;
                    }
                }

                image.Labels[i] = (byte)white;
            }
        }
    }

    /// <summary>
    /// Drops the palette colors that are rare or only line edges and that lie between two more common ones or close to one: the
    /// fringes and tints that antialiasing, blurring, chroma subsampling and compression leave, which a flat-color image has no
    /// use for. Their pixels then read as blends of the two, or as the one.
    /// </summary>
    internal static Rgba8[] Prune(Rgba8[] palette, ref byte[] nearest, byte[] rgb, byte[] alpha, int width, CancellationToken cancellationToken)
    {
        int height = nearest.Length / width;
        while (palette.Length > 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Each color's pixels and interior pixels, counted row by row in parallel (whole numbers, so in any order).
            long[] count = new long[palette.Length], interior = new long[palette.Length];
            byte[] labels = nearest;
            int colors = palette.Length;
            Parallel.For(
                0,
                height,
                new ParallelOptions { CancellationToken = cancellationToken },
                () => new long[2 * colors],
                (y, _, local) =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width) + x;
                        if (alpha[i] >= 128)
                        {
                            int k = labels[i];
                            local[k]++;
                            if (x > 0 && x + 1 < width && y > 0 && y + 1 < height && labels[i - 1] == k && labels[i + 1] == k && labels[i - width] == k && labels[i + width] == k)
                            {
                                local[colors + k]++;
                            }
                        }
                    }

                    return local;
                },
                local =>
                {
                    lock (count)
                    {
                        for (int k = 0; k < colors; k++)
                        {
                            count[k] += local[k];
                            interior[k] += local[colors + k];
                        }
                    }
                });
            long total = count.Sum();

            int drop = -1;
            for (int k = 0; k < palette.Length && drop < 0; k++)
            {
                // A fringe is rare, or lines edges without filling anything: almost none of its pixels lie inside it.
                if (count[k] >= RareShare * total && interior[k] >= EdgeOnly * count[k])
                {
                    continue;
                }

                // A rare color that only lines edges may stray further from the color it tints, as ringing does.
                double close = count[k] < RareShare * total && interior[k] < EdgeOnly * count[k] ? 1.6 * Close : Close;
                for (int a = 0; a < palette.Length && drop < 0; a++)
                {
                    if (a != k && count[a] > count[k] && Distance(palette[a], palette[k].R, palette[k].G, palette[k].B) <= close)
                    {
                        drop = k;
                    }

                    for (int b = a + 1; b < palette.Length && drop < 0; b++)
                    {
                        if (a != k && b != k && count[a] > count[k] && count[b] > count[k] && Between(palette[k], palette[a], palette[b]))
                        {
                            drop = k;
                        }
                    }
                }
            }

            if (drop < 0)
            {
                break;
            }

            palette = [.. palette[..drop], .. palette[(drop + 1)..]];
            nearest = Nearest(rgb, palette, cancellationToken);
        }

        return palette;
    }

    /// <summary>Whether a color lies on the line between two others, at least 8 levels from either.</summary>
    private static bool Between(Rgba8 c, Rgba8 a, Rgba8 b)
    {
        double dr = b.R - a.R, dg = b.G - a.G, db = b.B - a.B;
        double length = (dr * dr) + (dg * dg) + (db * db);
        if (length < 48 * 48)
        {
            return false;
        }

        double pr = c.R - a.R, pg = c.G - a.G, pb = c.B - a.B;
        double t = ((pr * dr) + (pg * dg) + (pb * db)) / length;
        double er = pr - (t * dr), eg = pg - (t * dg), eb = pb - (t * db);
        double reach = 8 / Math.Sqrt(length);
        return t > reach && t < 1 - reach && Math.Sqrt((er * er) + (eg * eg) + (eb * eb)) <= 16;
    }

    /// <summary>Each pixel's nearest palette color by its own (unblended) color.</summary>
    private static byte[] Nearest(byte[] rgb, Rgba8[] palette, CancellationToken cancellationToken)
    {
        int n = rgb.Length / 3;
        byte[] nearest = new byte[n];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        Parallel.For(0, (n + 4095) / 4096, parallel, block =>
        {
            int last = -1, lastLabel = 0;
            for (int i = block * 4096; i < Math.Min(n, (block + 1) * 4096); i++)
            {
                int r = rgb[3 * i], g = rgb[(3 * i) + 1], b = rgb[(3 * i) + 2];
                int key = (r << 16) | (g << 8) | b;
                if (key != last)
                {
                    int best = 0;
                    double bestDistance = double.MaxValue;
                    for (int k = 0; k < palette.Length; k++)
                    {
                        double d = Distance(palette[k], r, g, b);
                        if (d < bestDistance)
                        {
                            (best, bestDistance) = (k, d);
                        }
                    }

                    (last, lastLabel) = (key, best);
                }

                nearest[i] = (byte)lastLabel;
            }
        });

        return nearest;
    }

    private static double Distance(Rgba8 c, int r, int g, int b)
    {
        int dr = r - c.R, dg = g - c.G, db = b - c.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static byte ToByte(float value) => (byte)((Math.Clamp(value, 0, 1) * 255) + 0.5f);
}
