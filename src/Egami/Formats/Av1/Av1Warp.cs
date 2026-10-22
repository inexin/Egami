namespace Egami.Formats.Av1;

/// <summary>
/// Warped motion: the least squares fit of a block's local warp from its neighbours' vectors, the shear decomposition both local
/// and global warps are checked and filtered with, and the 8x8 warp filter, as libaom's warped_motion.c has them (BSD 2-clause,
/// see THIRD-PARTY-NOTICES).
/// </summary>
internal static class Av1Warp
{
    private const int PrecBits = Av1FrameHeader.WarpPrecBits;
    private const int ReduceBits = 6;
    private const int TransClamp = 1 << 23;
    private const int NonDiagClamp = 1 << 13;
    private const int LsMvMax = 256;
    private const int LsStep = 8;

    /// <summary>
    /// av1_find_projection: fits the warp of a block from <paramref name="count"/> sample pairs (block-relative, eighth samples),
    /// returning false when the fit fails or its shear is not allowed.
    /// </summary>
    internal static bool FindProjection(int count, int[] points, int[] pointsInRef, int bsize, int mvRow, int mvCol, int[] mat, int miRow, int miCol)
    {
        int a00 = 0, a01 = 0, a11 = 0, bx0 = 0, bx1 = 0, by0 = 0, by1 = 0;
        int bw = Av1Tables.BlockSizeWide[bsize], bh = Av1Tables.BlockSizeHigh[bsize];
        int rsuy = (bh / 2) - 1, rsux = (bw / 2) - 1;
        int suy = rsuy * 8, sux = rsux * 8;
        int duy = suy + mvRow, dux = sux + mvCol;
        for (int i = 0; i < count; i++)
        {
            int dx = pointsInRef[i * 2] - dux, dy = pointsInRef[(i * 2) + 1] - duy;
            int sx = points[i * 2] - sux, sy = points[(i * 2) + 1] - suy;
            if (Math.Abs(sx - dx) < LsMvMax && Math.Abs(sy - dy) < LsMvMax)
            {
                a00 += Square(sx);
                a01 += Product1(sx, sy);
                a11 += Square(sy);
                bx0 += Product2(sx, dx);
                bx1 += Product1(sy, dx);
                by0 += Product1(sx, dy);
                by1 += Product2(sy, dy);
            }
        }

        long det = ((long)a00 * a11) - ((long)a01 * a01);
        if (det == 0)
        {
            return false;
        }

        int divisor = ResolveDivisor((ulong)Math.Abs(det), out int shift) * (det < 0 ? -1 : 1);
        shift -= PrecBits;
        if (shift < 0)
        {
            divisor <<= -shift;
            shift = 0;
        }

        long px0 = ((long)a11 * bx0) - ((long)a01 * bx1);
        long px1 = (-(long)a01 * bx0) + ((long)a00 * bx1);
        long py0 = ((long)a11 * by0) - ((long)a01 * by1);
        long py1 = (-(long)a01 * by0) + ((long)a00 * by1);
        mat[2] = Diag(px0, divisor, shift);
        mat[3] = NonDiag(px1, divisor, shift);
        mat[4] = NonDiag(py0, divisor, shift);
        mat[5] = Diag(py1, divisor, shift);
        int isuy = (miRow * 4) + rsuy, isux = (miCol * 4) + rsux;
        int vx = (mvCol * (1 << (PrecBits - 3))) - ((isux * (mat[2] - (1 << PrecBits))) + (isuy * mat[3]));
        int vy = (mvRow * (1 << (PrecBits - 3))) - ((isux * mat[4]) + (isuy * (mat[5] - (1 << PrecBits))));
        mat[0] = Math.Clamp(vx, -TransClamp, TransClamp - 1);
        mat[1] = Math.Clamp(vy, -TransClamp, TransClamp - 1);
        return true;

        static int Square(int a) => ((a * a * 4) + (a * 4 * LsStep) + (LsStep * LsStep * 2)) >> 4;

        static int Product1(int a, int b) => ((a * b * 4) + ((a + b) * 2 * LsStep) + (LsStep * LsStep)) >> 4;

        static int Product2(int a, int b) => ((a * b * 4) + ((a + b) * 2 * LsStep) + (LsStep * LsStep * 2)) >> 4;

        static int NonDiag(long p, int divisor, int shift) => (int)Math.Clamp(Round2Signed(p * divisor, shift), -NonDiagClamp + 1, NonDiagClamp - 1);

        static int Diag(long p, int divisor, int shift) =>
            (int)Math.Clamp(Round2Signed(p * divisor, shift), (1 << PrecBits) - NonDiagClamp + 1, (1 << PrecBits) + NonDiagClamp - 1);
    }

    /// <summary>Gets whether a warp's shear decomposition is allowed (av1_get_shear_params).</summary>
    internal static bool ShearValid(int[] mat) => Shear(mat, out _, out _, out _, out _);

    /// <summary>av1_get_shear_params: the warp's two shears, reduced to the filter's precision; false when the warp is not allowed.</summary>
    internal static bool Shear(int[] mat, out int alpha, out int beta, out int gamma, out int delta)
    {
        alpha = beta = gamma = delta = 0;
        if (mat[2] <= 0)
        {
            return false;
        }

        alpha = Math.Clamp(mat[2] - (1 << PrecBits), short.MinValue, short.MaxValue);
        beta = Math.Clamp(mat[3], short.MinValue, short.MaxValue);
        int y = ResolveDivisor((ulong)Math.Abs(mat[2]), out int shift) * (mat[2] < 0 ? -1 : 1);
        long v = (long)mat[4] * (1 << PrecBits) * y;
        gamma = (int)Math.Clamp(Round2Signed(v, shift), short.MinValue, short.MaxValue);
        v = (long)mat[3] * mat[4] * y;
        delta = (int)Math.Clamp(mat[5] - Round2Signed(v, shift) - (1 << PrecBits), short.MinValue, short.MaxValue);
        alpha = (int)Round2Signed(alpha, ReduceBits) * (1 << ReduceBits);
        beta = (int)Round2Signed(beta, ReduceBits) * (1 << ReduceBits);
        gamma = (int)Round2Signed(gamma, ReduceBits) * (1 << ReduceBits);
        delta = (int)Round2Signed(delta, ReduceBits) * (1 << ReduceBits);
        return (4 * Math.Abs(alpha)) + (7 * Math.Abs(beta)) < 1 << PrecBits && (4 * Math.Abs(gamma)) + (4 * Math.Abs(delta)) < 1 << PrecBits;
    }

    /// <summary>
    /// Warps one 8x8 block of a plane (av1_highbd_warp_affine_c for one block): <paramref name="pred"/> gets the vertical
    /// filter's output rounded by <paramref name="round1"/> (11, or 7 for compound), at <paramref name="predOffset"/> with
    /// <paramref name="predStride"/>, at most <paramref name="w"/> by <paramref name="h"/> of it.
    /// </summary>
    internal static void Block(int[] mat, int alpha, int beta, int gamma, int delta, ushort[] reference, int stride, int width, int height,
        int x, int y, int ssx, int ssy, int round0, int round1, int[] pred, int predOffset, int predStride, int w, int h)
    {
        Span<int> tmp = stackalloc int[15 * 8];
        sbyte[] filters = Av1Tables.WarpedFilter;
        int srcX = (x + 4) << ssx, srcY = (y + 4) << ssy;
        long dstX = ((long)mat[2] * srcX) + ((long)mat[3] * srcY) + mat[0];
        long dstY = ((long)mat[4] * srcX) + ((long)mat[5] * srcY) + mat[1];
        long x4 = dstX >> ssx, y4 = dstY >> ssy;
        int ix4 = (int)(x4 >> PrecBits), iy4 = (int)(y4 >> PrecBits);
        int sx4 = (int)(x4 & ((1 << PrecBits) - 1)), sy4 = (int)(y4 & ((1 << PrecBits) - 1));
        sx4 += (alpha * -4) + (beta * -4);
        sy4 += (gamma * -4) + (delta * -4);
        sx4 &= ~((1 << ReduceBits) - 1);
        sy4 &= ~((1 << ReduceBits) - 1);
        for (int k = -7; k < 8; k++)
        {
            int row = Math.Clamp(iy4 + k, 0, height - 1) * stride;
            int sx = sx4 + (beta * (k + 4));
            for (int l = -4; l < 4; l++)
            {
                int ix = ix4 + l - 3;
                int offs = (((sx + 512) >> 10) + 64) * 8;
                int sum = 0;
                for (int m = 0; m < 8; m++)
                {
                    sum += reference[row + Math.Clamp(ix + m, 0, width - 1)] * filters[offs + m];
                }

                tmp[((k + 7) * 8) + l + 4] = (sum + (1 << (round0 - 1))) >> round0;
                sx += alpha;
            }
        }

        for (int k = -4; k < Math.Min(4, h - 4); k++)
        {
            int sy = sy4 + (delta * (k + 4));
            for (int l = -4; l < Math.Min(4, w - 4); l++)
            {
                int offs = (((sy + 512) >> 10) + 64) * 8;
                int sum = 0;
                for (int m = 0; m < 8; m++)
                {
                    sum += tmp[((k + m + 4) * 8) + l + 4] * filters[offs + m];
                }

                pred[predOffset + ((k + 4) * predStride) + l + 4] = (sum + (1 << (round1 - 1))) >> round1;
                sy += gamma;
            }
        }
    }

    private static long Round2Signed(long v, int n) => n == 0 ? v : v < 0 ? -((-v + (1L << (n - 1))) >> n) : (v + (1L << (n - 1))) >> n;

    /// <summary>resolve_divisor_64: a multiplier and shift that divide by <paramref name="d"/>.</summary>
    private static int ResolveDivisor(ulong d, out int shift)
    {
        shift = 63 - (int)ulong.LeadingZeroCount(d);
        long e = (long)(d - (1UL << shift));
        long f = shift > 8 ? (e + (1L << (shift - 9))) >> (shift - 8) : e << (8 - shift);
        shift += 14;
        return Av1Tables.DivLut[f];
    }
}
