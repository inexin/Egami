namespace Egami.Video.Editing;

/// <summary>
/// Global motion between two frames of a video, from their luma: corners picked by the smaller eigenvalue of the structure
/// tensor (Shi and Tomasi 1994), tracked with pyramidal Lucas-Kanade (Lucas and Kanade 1981, in Bouguet's pyramidal form),
/// checked by tracking back, and a similarity transform (shift, rotation, scale) fitted to the tracks with RANSAC (Fischler and
/// Bolles 1981) and refined by least squares on the inliers.
/// </summary>
internal sealed class MotionEstimator
{
    private const int Levels = 3;
    private const int Half = 7;
    private const int MaxCorners = 300;
    private const int Iterations = 12;

    private readonly Random _random = new(1);
    private Pyramid? _previous;

    /// <summary>Gets the width of the luma the estimator works on, after downscaling.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the height of the luma the estimator works on.</summary>
    public int Height { get; private set; }

    /// <summary>
    /// Adds the next frame's luma and returns the similarity that maps the previous frame's points onto this frame's, in the
    /// luma's own pixels, or the identity for the first frame or when too few points track.
    /// </summary>
    public Similarity Add(Gray luma)
    {
        Width = luma.Width;
        Height = luma.Height;
        Pyramid current = new(luma, Levels);
        Similarity motion = Similarity.Identity;
        if (_previous is not null)
        {
            motion = Estimate(_previous, current);
        }

        _previous = current;
        return motion;
    }

    /// <summary>Forgets the previous frame, as after a cut.</summary>
    public void Reset() => _previous = null;

    private Similarity Estimate(Pyramid from, Pyramid to)
    {
        List<(float X, float Y)> corners = Corners(from.Levels[0]);
        List<(double X, double Y, double U, double V)> pairs = [];
        foreach ((float x, float y) in corners)
        {
            if (Track(from, to, x, y, out float u, out float v) && Track(to, from, u, v, out float bx, out float by)
                && ((bx - x) * (bx - x)) + ((by - y) * (by - y)) < 0.25f)
            {
                pairs.Add((x, y, u, v));
            }
        }

        if (pairs.Count < 4)
        {
            return Similarity.Identity;
        }

        // RANSAC: two pairs fix a similarity; keep the one most pairs agree with, within a pixel.
        bool[] best = new bool[pairs.Count];
        int bestCount = 0;
        bool[] inliers = new bool[pairs.Count];
        for (int iteration = 0; iteration < 200; iteration++)
        {
            int i = _random.Next(pairs.Count), j = _random.Next(pairs.Count);
            if (i == j || !Similarity.TryFromTwo(pairs[i], pairs[j], out Similarity guess))
            {
                continue;
            }

            int count = 0;
            for (int k = 0; k < pairs.Count; k++)
            {
                (double px, double py) = guess.Apply(pairs[k].X, pairs[k].Y);
                inliers[k] = ((px - pairs[k].U) * (px - pairs[k].U)) + ((py - pairs[k].V) * (py - pairs[k].V)) < 1.0;
                count += inliers[k] ? 1 : 0;
            }

            if (count > bestCount)
            {
                bestCount = count;
                Array.Copy(inliers, best, inliers.Length);
            }
        }

        return bestCount >= 3 ? Similarity.FitLeastSquares(pairs, best) : Similarity.Identity;
    }

    /// <summary>Corners spread over the frame: the strongest corner response in each cell of a grid, above a floor.</summary>
    private static List<(float X, float Y)> Corners(Gray image)
    {
        int w = image.Width, h = image.Height;
        float[] response = new float[w * h];
        float max = 0;
        const int R = 3;
        for (int y = R + 1; y < h - R - 1; y++)
        {
            for (int x = R + 1; x < w - R - 1; x++)
            {
                double a = 0, b = 0, c = 0;
                for (int dy = -R; dy <= R; dy++)
                {
                    for (int dx = -R; dx <= R; dx++)
                    {
                        int at = ((y + dy) * w) + x + dx;
                        double gx = (image.Data[at + 1] - image.Data[at - 1]) * 0.5, gy = (image.Data[at + w] - image.Data[at - w]) * 0.5;
                        a += gx * gx;
                        b += gx * gy;
                        c += gy * gy;
                    }
                }

                float lambda = (float)(((a + c) / 2) - Math.Sqrt((((a - c) / 2) * ((a - c) / 2)) + (b * b)));
                response[(y * w) + x] = lambda;
                max = Math.Max(max, lambda);
            }
        }

        int cell = Math.Max(8, (int)Math.Sqrt((double)w * h / MaxCorners));
        int margin = Half + 2;
        List<(float X, float Y)> corners = [];
        for (int cy = margin; cy < h - margin; cy += cell)
        {
            for (int cx = margin; cx < w - margin; cx += cell)
            {
                float best = max * 0.01f;
                int bx = -1, by = -1;
                for (int y = cy; y < Math.Min(cy + cell, h - margin); y++)
                {
                    for (int x = cx; x < Math.Min(cx + cell, w - margin); x++)
                    {
                        if (response[(y * w) + x] > best)
                        {
                            best = response[(y * w) + x];
                            bx = x;
                            by = y;
                        }
                    }
                }

                if (bx >= 0)
                {
                    corners.Add((bx, by));
                }
            }
        }

        return corners;
    }

    /// <summary>Tracks a point from one pyramid to the other, coarse to fine; false when it leaves the frame or does not settle.</summary>
    private static bool Track(Pyramid from, Pyramid to, float x, float y, out float u, out float v)
    {
        float gx = 0, gy = 0;
        u = v = 0;
        int size = (2 * Half) + 1;
        Span<float> ix = stackalloc float[size * size], iy = stackalloc float[size * size], ia = stackalloc float[size * size];
        for (int level = Levels - 1; level >= 0; level--)
        {
            Gray a = from.Levels[level], b = to.Levels[level];
            float scale = 1f / (1 << level);
            float px = x * scale, py = y * scale;
            if (px < Half + 1 || py < Half + 1 || px >= a.Width - Half - 2 || py >= a.Height - Half - 2)
            {
                return false;
            }

            // The spatial gradient matrix of the window in the first image.
            double gxx = 0, gxy = 0, gyy = 0;
            int n = 0;
            for (int dy = -Half; dy <= Half; dy++)
            {
                for (int dx = -Half; dx <= Half; dx++)
                {
                    float sx = px + dx, sy = py + dy;
                    float dxv = (a.Sample(sx + 1, sy) - a.Sample(sx - 1, sy)) * 0.5f, dyv = (a.Sample(sx, sy + 1) - a.Sample(sx, sy - 1)) * 0.5f;
                    ix[n] = dxv;
                    iy[n] = dyv;
                    ia[n] = a.Sample(sx, sy);
                    gxx += dxv * dxv;
                    gxy += dxv * dyv;
                    gyy += dyv * dyv;
                    n++;
                }
            }

            double det = (gxx * gyy) - (gxy * gxy);
            if (det < 1e-6)
            {
                return false;
            }

            float vx = 0, vy = 0;
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                double bx = 0, by = 0;
                n = 0;
                for (int dy = -Half; dy <= Half; dy++)
                {
                    for (int dx = -Half; dx <= Half; dx++)
                    {
                        float diff = ia[n] - b.Sample(px + dx + gx + vx, py + dy + gy + vy);
                        bx += diff * ix[n];
                        by += diff * iy[n];
                        n++;
                    }
                }

                float ux = (float)(((gyy * bx) - (gxy * by)) / det), uy = (float)(((gxx * by) - (gxy * bx)) / det);
                vx += ux;
                vy += uy;
                if ((ux * ux) + (uy * uy) < 1e-4f)
                {
                    break;
                }
            }

            if (level > 0)
            {
                gx = 2 * (gx + vx);
                gy = 2 * (gy + vy);
            }
            else
            {
                u = x + gx + vx;
                v = y + gy + vy;
            }
        }

        return u >= 0 && v >= 0 && u < from.Levels[0].Width && v < from.Levels[0].Height;
    }

    /// <summary>An image pyramid: each level half the size of the one before, by 2x2 averages.</summary>
    private sealed class Pyramid
    {
        public Pyramid(Gray image, int levels)
        {
            Levels = new Gray[levels];
            Levels[0] = image;
            for (int i = 1; i < levels; i++)
            {
                Levels[i] = Levels[i - 1].Half();
            }
        }

        public Gray[] Levels { get; }
    }
}

/// <summary>A single-channel float image, for the motion estimate.</summary>
internal sealed class Gray(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public float[] Data { get; } = new float[width * height];

    /// <summary>Samples with bilinear interpolation, clamped at the edges.</summary>
    public float Sample(float x, float y)
    {
        x = Math.Clamp(x, 0, Width - 1.001f);
        y = Math.Clamp(y, 0, Height - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        int at = (y0 * Width) + x0;
        float top = Data[at] + ((Data[at + 1] - Data[at]) * fx);
        float bottom = Data[at + Width] + ((Data[at + Width + 1] - Data[at + Width]) * fx);
        return top + ((bottom - top) * fy);
    }

    public Gray Half()
    {
        Gray half = new(Math.Max(1, Width / 2), Math.Max(1, Height / 2));
        for (int y = 0; y < half.Height; y++)
        {
            for (int x = 0; x < half.Width; x++)
            {
                int at = (2 * y * Width) + (2 * x);
                half.Data[(y * half.Width) + x] = (Data[at] + Data[at + 1] + Data[at + Width] + Data[at + Width + 1]) * 0.25f;
            }
        }

        return half;
    }

    /// <summary>Takes a frame's luma, box-averaged down by a whole factor so the result is at most <paramref name="maxWidth"/> wide.</summary>
    public static Gray FromLuma(YuvRaster raster, int maxWidth, out int factor)
    {
        factor = Math.Max(1, (int)Math.Ceiling((double)raster.Width / maxWidth));
        int f = factor;
        Gray gray = new(raster.Width / f, raster.Height / f);
        float scale = 1f / (f * f);
        switch (raster)
        {
            case YuvRaster<byte> bytes:
                for (int y = 0; y < gray.Height; y++)
                {
                    for (int x = 0; x < gray.Width; x++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < f; dy++)
                        {
                            ReadOnlySpan<byte> row = bytes.Y.Row((y * f) + dy).Slice(x * f, f);
                            foreach (byte b in row)
                            {
                                sum += b;
                            }
                        }

                        gray.Data[(y * gray.Width) + x] = sum * scale;
                    }
                }

                break;
            case YuvRaster<ushort> words:
                float depth = 255f / ((1 << raster.BitDepth) - 1);
                for (int y = 0; y < gray.Height; y++)
                {
                    for (int x = 0; x < gray.Width; x++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < f; dy++)
                        {
                            foreach (ushort s in words.Y.Row((y * f) + dy).Slice(x * f, f))
                            {
                                sum += s;
                            }
                        }

                        gray.Data[(y * gray.Width) + x] = sum * scale * depth;
                    }
                }

                break;
        }

        return gray;
    }
}

/// <summary>A similarity transform: <c>x' = a x - b y + tx</c>, <c>y' = b x + a y + ty</c>.</summary>
internal readonly record struct Similarity(double A, double B, double Tx, double Ty)
{
    public static Similarity Identity => new(1, 0, 0, 0);

    public double Angle => Math.Atan2(B, A);

    public double Scale => Math.Sqrt((A * A) + (B * B));

    public (double X, double Y) Apply(double x, double y) => ((A * x) - (B * y) + Tx, (B * x) + (A * y) + Ty);

    /// <summary>The similarity through two point pairs.</summary>
    public static bool TryFromTwo((double X, double Y, double U, double V) p, (double X, double Y, double U, double V) q, out Similarity result)
    {
        double dx = q.X - p.X, dy = q.Y - p.Y, du = q.U - p.U, dv = q.V - p.V;
        double d = (dx * dx) + (dy * dy);
        if (d < 4)
        {
            result = Identity;
            return false;
        }

        double a = ((du * dx) + (dv * dy)) / d, b = ((dv * dx) - (du * dy)) / d;
        result = new Similarity(a, b, p.U - ((a * p.X) - (b * p.Y)), p.V - ((b * p.X) + (a * p.Y)));
        return true;
    }

    /// <summary>The least-squares similarity of the marked pairs.</summary>
    public static Similarity FitLeastSquares(List<(double X, double Y, double U, double V)> pairs, bool[] use)
    {
        // Centre both point sets; then a and b follow from the cross terms, and the shift from the centroids.
        double mx = 0, my = 0, mu = 0, mv = 0;
        int n = 0;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (use[i])
            {
                mx += pairs[i].X;
                my += pairs[i].Y;
                mu += pairs[i].U;
                mv += pairs[i].V;
                n++;
            }
        }

        mx /= n;
        my /= n;
        mu /= n;
        mv /= n;
        double sxx = 0, sab = 0, sba = 0;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (!use[i])
            {
                continue;
            }

            double x = pairs[i].X - mx, y = pairs[i].Y - my, u = pairs[i].U - mu, v = pairs[i].V - mv;
            sxx += (x * x) + (y * y);
            sab += (x * u) + (y * v);
            sba += (x * v) - (y * u);
        }

        double a = sab / sxx, b = sba / sxx;
        return new Similarity(a, b, mu - ((a * mx) - (b * my)), mv - ((b * mx) + (a * my)));
    }
}
