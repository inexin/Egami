namespace Egami.Formats.Av1;

/// <summary>
/// Global motion estimation for each reference of an inter frame, after the ideas of libaom's global_motion.c and ransac.c (BSD
/// 2-clause, see THIRD-PARTY-NOTICES): correspondences from a whole-sample block match of the frame's textured 16x16 blocks against
/// the reference, a translation, rotation-zoom and affine model fitted to them by RANSAC and refined by least squares on the
/// inliers, rounded to the precision the frame header codes, and kept when it explains the blocks clearly better than no motion.
/// </summary>
internal static class Av1GlobalMotion
{
    private const int BlockSize = 16;
    private const int Range = 32;
    private const int Iterations = 128;

    /// <summary>Estimates each searched reference's global motion into the frame's parameters; references it does not help stay identity.</summary>
    internal static void Estimate(Av1EncoderInput input, Av1FrameParameters parameters, Av1ReferencePlane?[] planes, int[] references, int bitDepth)
    {
        int cols = input.Width / BlockSize, rows = input.Height / BlockSize;
        if (cols * rows < 12)
        {
            return;
        }

        ushort[] source = input.Planes[0];
        foreach (int rf in references)
        {
            Av1ReferencePlane plane = planes[rf]!;
            List<(double X, double Y, double Tx, double Ty)> matches = [];
            long zeroTotal = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * BlockSize, y0 = r * BlockSize;
                    zeroTotal += Sad(source, input.Width, x0, y0, plane, 0, 0);
                    if (Flat(source, input.Width, x0, y0, bitDepth))
                    {
                        continue;
                    }

                    (int dx, int dy) = Match(source, input.Width, x0, y0, plane);
                    double cx = x0 + (BlockSize / 2.0) - 0.5, cy = y0 + (BlockSize / 2.0) - 0.5;
                    matches.Add((cx, cy, cx + dx, cy + dy));
                }
            }

            if (matches.Count < 8)
            {
                continue;
            }

            (int Type, int[] Params) best = (GlobalMotion.Identity, Av1FrameHeader.DefaultWarpParams());
            long bestError = zeroTotal;
            foreach (int type in (ReadOnlySpan<int>)[GlobalMotion.Translation, GlobalMotion.RotZoom, GlobalMotion.Affine])
            {
                double[]? model = Ransac(matches, type);
                if (model is null)
                {
                    continue;
                }

                int[] p = Quantize(model, type, parameters.AllowHighPrecisionMv);
                if (type != GlobalMotion.Translation && !Av1Warp.ShearValid(p))
                {
                    continue;
                }

                // Each model must clearly beat the simpler ones: its parameters and its warp cost something.
                long error = ModelError(source, input.Width, cols, rows, plane, type == GlobalMotion.Translation ? [p[1], p[0], p[2], p[3], p[4], p[5]] : p);
                if (error < bestError * (type == GlobalMotion.Translation ? 0.9 : 0.95))
                {
                    (best, bestError) = ((type, p), error);
                }
            }

            parameters.GmType[rf] = best.Type;
            parameters.GmParams[rf] = best.Params;
        }
    }

    /// <summary>Gets whether a block is too flat for its match to mean anything.</summary>
    private static bool Flat(ushort[] source, int stride, int x0, int y0, int bitDepth)
    {
        long sum = 0, squares = 0;
        for (int y = 0; y < BlockSize; y++)
        {
            for (int x = 0; x < BlockSize; x++)
            {
                int v = source[((y0 + y) * stride) + x0 + x] >> (bitDepth - 8);
                sum += v;
                squares += v * v;
            }
        }

        const long n = BlockSize * BlockSize;
        return (squares * n) - (sum * sum) < 64 * n * n;
    }

    private static int Sad(ushort[] source, int stride, int x0, int y0, Av1ReferencePlane plane, int dx, int dy) =>
        Av1MotionSearch.Sad(source, (y0 * stride) + x0, stride, plane.Samples, plane.At(x0 + dx, y0 + dy), plane.Stride, BlockSize, BlockSize);

    /// <summary>Finds a block's whole-sample offset in the reference: a coarse grid, then a descent by halving steps.</summary>
    private static (int Dx, int Dy) Match(ushort[] source, int stride, int x0, int y0, Av1ReferencePlane plane)
    {
        (int Dx, int Dy, int Sad) best = (0, 0, Sad(source, stride, x0, y0, plane, 0, 0));
        for (int dy = -Range; dy <= Range; dy += 8)
        {
            for (int dx = -Range; dx <= Range; dx += 8)
            {
                Try(dx, dy);
            }
        }

        for (int step = 4; step >= 1; step >>= 1)
        {
            (int cx, int cy, _) = best;
            for (int dy = -step; dy <= step; dy += step)
            {
                for (int dx = -step; dx <= step; dx += step)
                {
                    Try(cx + dx, cy + dy);
                }
            }
        }

        return (best.Dx, best.Dy);

        void Try(int dx, int dy)
        {
            if (Math.Abs(dx) > Range + 8 || Math.Abs(dy) > Range + 8 || !plane.Holds(x0 + dx, y0 + dy, BlockSize, BlockSize))
            {
                return;
            }

            int sad = Sad(source, stride, x0, y0, plane, dx, dy);
            if (sad < best.Sad)
            {
                best = (dx, dy, sad);
            }
        }
    }

    /// <summary>
    /// Fits a model to the matches by RANSAC: minimal sets drawn from a fixed sequence, the model with the most inliers (within two
    /// samples) kept and refit to them by least squares. The model maps x, y to m[2]·x + m[3]·y + m[0], m[4]·x + m[5]·y + m[1].
    /// </summary>
    private static double[]? Ransac(List<(double X, double Y, double Tx, double Ty)> matches, int type)
    {
        int minimal = type == GlobalMotion.Translation ? 1 : type == GlobalMotion.RotZoom ? 2 : 3;
        uint state = 0x9E3779B9;
        double[]? bestModel = null;
        int bestInliers = 0;
        int[] set = new int[minimal];
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            for (int i = 0; i < minimal; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                set[i] = (int)(state % (uint)matches.Count);
            }

            double[]? model = Fit(matches, set, type);
            if (model is null)
            {
                continue;
            }

            int count = Inliers(matches, model).Count;
            if (count > bestInliers)
            {
                (bestInliers, bestModel) = (count, model);
            }
        }

        if (bestModel is null || bestInliers < Math.Max(minimal + 4, matches.Count / 4))
        {
            return null;
        }

        return Fit(matches, [.. Inliers(matches, bestModel)], type) ?? bestModel;
    }

    private static List<int> Inliers(List<(double X, double Y, double Tx, double Ty)> matches, double[] m)
    {
        List<int> inliers = [];
        for (int i = 0; i < matches.Count; i++)
        {
            (double x, double y, double tx, double ty) = matches[i];
            double ex = (m[2] * x) + (m[3] * y) + m[0] - tx, ey = (m[4] * x) + (m[5] * y) + m[1] - ty;
            if ((ex * ex) + (ey * ey) <= 4)
            {
                inliers.Add(i);
            }
        }

        return inliers;
    }

    /// <summary>Fits a model to some matches by least squares; null when they do not fix it.</summary>
    private static double[]? Fit(List<(double X, double Y, double Tx, double Ty)> matches, int[] indices, int type)
    {
        if (type == GlobalMotion.Translation)
        {
            double sx = 0, sy = 0;
            foreach (int i in indices)
            {
                sx += matches[i].Tx - matches[i].X;
                sy += matches[i].Ty - matches[i].Y;
            }

            return [sx / indices.Length, sy / indices.Length, 1, 0, 0, 1];
        }

        if (type == GlobalMotion.RotZoom)
        {
            // x' = a x + b y + tx, y' = -b x + a y + ty: four unknowns, two equations a match.
            double[,] ata = new double[4, 4];
            double[] atb = new double[4];
            foreach (int i in indices)
            {
                (double x, double y, double tx, double ty) = matches[i];
                Accumulate(ata, atb, [x, y, 1, 0], tx);
                Accumulate(ata, atb, [y, -x, 0, 1], ty);
            }

            double[]? s = Solve(ata, atb);
            return s is null ? null : [s[2], s[3], s[0], s[1], -s[1], s[0]];
        }

        // Affine: each axis on its own, three unknowns.
        double[,] ax = new double[3, 3], ay = new double[3, 3];
        double[] bx = new double[3], by = new double[3];
        foreach (int i in indices)
        {
            (double x, double y, double tx, double ty) = matches[i];
            Accumulate(ax, bx, [x, y, 1], tx);
            Accumulate(ay, by, [x, y, 1], ty);
        }

        double[]? mx = Solve(ax, bx), my = Solve(ay, by);
        return mx is null || my is null ? null : [mx[2], my[2], mx[0], mx[1], my[0], my[1]];

        static void Accumulate(double[,] ata, double[] atb, double[] row, double value)
        {
            for (int r = 0; r < row.Length; r++)
            {
                for (int c = 0; c < row.Length; c++)
                {
                    ata[r, c] += row[r] * row[c];
                }

                atb[r] += row[r] * value;
            }
        }
    }

    /// <summary>Solves a small linear system by Gaussian elimination with partial pivoting; null when it is singular.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        double[] x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-9)
            {
                return null;
            }

            for (int c = 0; c < n; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }

            (x[col], x[pivot]) = (x[pivot], x[col]);
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int c = col; c < n; c++)
                {
                    a[r, c] -= f * a[col, c];
                }

                x[r] -= f * x[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            for (int c = r + 1; c < n; c++)
            {
                x[r] -= a[r, c] * x[c];
            }

            x[r] /= a[r, r];
        }

        return x;
    }

    /// <summary>
    /// Rounds a model to the frame header's global motion parameters: the matrix in 1/32768, translations in 1/64 sample, or for a
    /// translation alone at the vector precision.
    /// </summary>
    internal static int[] Quantize(double[] m, int type, bool allowHp)
    {
        const int prec = Av1FrameHeader.WarpPrecBits;
        const int alphaPrecDiff = prec - 15, alphaMax = 1 << 12;
        int[] p = Av1FrameHeader.DefaultWarpParams();
        if (type >= GlobalMotion.RotZoom)
        {
            p[2] = (Math.Clamp((int)Math.Round((m[2] - 1) * (1 << 15)), -alphaMax, alphaMax) << alphaPrecDiff) + (1 << prec);
            p[3] = Math.Clamp((int)Math.Round(m[3] * (1 << 15)), -alphaMax, alphaMax) << alphaPrecDiff;
        }

        if (type == GlobalMotion.Affine)
        {
            p[4] = Math.Clamp((int)Math.Round(m[4] * (1 << 15)), -alphaMax, alphaMax) << alphaPrecDiff;
            p[5] = (Math.Clamp((int)Math.Round((m[5] - 1) * (1 << 15)), -alphaMax, alphaMax) << alphaPrecDiff) + (1 << prec);
        }
        else
        {
            p[4] = -p[3];
            p[5] = p[2];
        }

        int hp = allowHp ? 0 : 1;
        int transBits = type == GlobalMotion.Translation ? 9 - hp : 12;
        int precDiff = type == GlobalMotion.Translation ? prec - 3 + hp : prec - 6;
        int limit = 1 << transBits;
        // A translation's first parameter is its vertical one, as the specification's setup_global_mv reads it (as libaom's
        // gm_get_motion_vector does); the warp of the other models reads the first as horizontal.
        (double first, double second) = type == GlobalMotion.Translation ? (m[1], m[0]) : (m[0], m[1]);
        p[0] = Math.Clamp((int)Math.Round(first * (1 << prec) / (1 << precDiff)), -limit, limit) << precDiff;
        p[1] = Math.Clamp((int)Math.Round(second * (1 << prec) / (1 << precDiff)), -limit, limit) << precDiff;
        return p;
    }

    /// <summary>Gets the blocks' summed error when each is predicted at the model's whole-sample displacement at its centre (the matrix's first parameter horizontal).</summary>
    private static long ModelError(ushort[] source, int stride, int cols, int rows, Av1ReferencePlane plane, int[] p)
    {
        const int prec = Av1FrameHeader.WarpPrecBits;
        long total = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int x0 = c * BlockSize, y0 = r * BlockSize;
                double cx = x0 + (BlockSize / 2.0) - 0.5, cy = y0 + (BlockSize / 2.0) - 0.5;
                double tx = ((p[2] * cx) + (p[3] * cy) + p[0]) / (1 << prec), ty = ((p[4] * cx) + (p[5] * cy) + p[1]) / (1 << prec);
                int dx = (int)Math.Round(tx - cx), dy = (int)Math.Round(ty - cy);
                total += Sad(source, stride, x0, y0, plane, plane.Holds(x0 + dx, y0 + dy, BlockSize, BlockSize) ? dx : 0,
                    plane.Holds(x0 + dx, y0 + dy, BlockSize, BlockSize) ? dy : 0);
            }
        }

        return total;
    }
}
