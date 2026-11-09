using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jxl;

/// <summary>
/// The lossy encoder's adaptive quantization (Phase 2.9.7, effort 5 and up): libjxl's InitialQuantField and
/// AdaptiveQuantizationMap (enc_adaptive_quantization, see THIRD-PARTY-NOTICES). A masking estimate from the local Laplacian of
/// Y, eroded and downsampled to one value per block, is modulated per block by the gamma of the block's light, its high
/// frequencies and its blue content, and becomes the quantization field: more precision in flat areas, less in texture. The
/// same pass gives the masks the block-size search and the EPF heuristics use.
/// </summary>
internal sealed class JxlAdaptiveQuantization
{
    private const int TileBlocks = 8;
    private const float AcQuant = 0.765f;

    // Scaling between JPEG XL's opsin and Butteraugli's log-gamma space (kSGmul, kSGmul2, kSGRetMul, kSGVOffset).
    private const float SgMul = 226.77216153508914f;
    private const float SgMul2 = 1.0f / 73.377132366608819f;
    private const float InvLog2e = 0.6931471805599453f;
    private const float SgRetMul = SgMul2 * 18.6580932135f * InvLog2e;
    private const float SgVOffset = 7.7825991679894591f;

    private readonly float[][] _xyb;
    private readonly int _xsize;
    private readonly int _ysize;
    private readonly int _blocksX;
    private readonly int _blocksY;

    private JxlAdaptiveQuantization(float[][] xyb, int xsize, int ysize)
    {
        _xyb = xyb;
        _xsize = xsize;
        _ysize = ysize;
        _blocksX = xsize / 8;
        _blocksY = ysize / 8;
        QuantField = new float[_blocksX * _blocksY];
        Mask = new float[_blocksX * _blocksY];
        Mask1x1 = new float[xsize * ysize];
    }

    /// <summary>Gets the part of the field and masks for the blocks from (<paramref name="bx0"/>, <paramref name="by0"/>), as a chunk with a border needs them.</summary>
    internal JxlAdaptiveQuantization Crop(int bx0, int by0, int blocksX, int blocksY)
    {
        JxlAdaptiveQuantization c = new([], blocksX * 8, blocksY * 8);
        for (int y = 0; y < blocksY; y++)
        {
            Array.Copy(QuantField, ((by0 + y) * _blocksX) + bx0, c.QuantField, y * blocksX, blocksX);
            Array.Copy(Mask, ((by0 + y) * _blocksX) + bx0, c.Mask, y * blocksX, blocksX);
        }

        for (int y = 0; y < blocksY * 8; y++)
        {
            Array.Copy(Mask1x1, (((by0 * 8) + y) * _xsize) + (bx0 * 8), c.Mask1x1, y * blocksX * 8, blocksX * 8);
        }

        return c;
    }

    /// <summary>Gets the initial quantization field, one value per block.</summary>
    internal float[] QuantField { get; }

    /// <summary>Gets the per-block masking (ComputeMaskForAcStrategyUse of the eroded map) the block-size search uses.</summary>
    internal float[] Mask { get; }

    /// <summary>Gets the per-pixel masking (the blurred inverse Laplacian) the block-size search and the EPF heuristics use.</summary>
    internal float[] Mask1x1 { get; private set; }

    /// <summary>
    /// Computes libjxl's InitialQuantField for XYB planes padded to whole blocks (row stride <paramref name="xsize"/>):
    /// quantization values scaled by 0.765 over <paramref name="distance"/>.
    /// </summary>
    internal static JxlAdaptiveQuantization Compute(float[][] xyb, int xsize, int ysize, float distance, ParallelOptions parallel)
    {
        JxlAdaptiveQuantization aq = new(xyb, xsize, ysize);
        float scale = AcQuant / distance;
        int tilesX = (aq._blocksX + TileBlocks - 1) / TileBlocks;
        int tilesY = (aq._blocksY + TileBlocks - 1) / TileBlocks;
        Parallel.For(0, tilesX * tilesY, parallel, tile =>
        {
            int tx = tile % tilesX;
            int ty = tile / tilesX;
            int bx0 = tx * TileBlocks;
            int by0 = ty * TileBlocks;
            int bx1 = Math.Min(bx0 + TileBlocks, aq._blocksX);
            int by1 = Math.Min(by0 + TileBlocks, aq._blocksY);
            aq.ComputeTile(distance, scale, bx0, by0, bx1, by1);
        });
        aq.BlurMask1x1(parallel);
        return aq;
    }

    /// <summary>libjxl's RatioOfDerivativesOfCubicRootToSimpleGamma: how a step in opsin compares to one in Butteraugli's gamma.</summary>
    private static float RatioOfDerivatives(float v, bool invert)
    {
        const float Epsilon = 1e-2f;
        v = MathF.Max(v, 0);
        float numMul = SgRetMul * 3 * SgMul;
        float vOffset = (SgVOffset * InvLog2e) + Epsilon;
        float denMul = InvLog2e * SgMul;
        float v2 = v * v;
        float num = MathF.FusedMultiplyAdd(numMul, v2, Epsilon);
        float den = MathF.FusedMultiplyAdd(denMul * v, v2, vOffset);
        return invert ? num / den : den / num;
    }

    private static float MaskingSqrt(float v)
    {
        const float LogOffset = 27.505837037000106f;
        const float Mul = 211.66567973503678f;
        return 0.25f * MathF.Sqrt(MathF.FusedMultiplyAdd(v, MathF.Sqrt(Mul * 1e8f), LogOffset));
    }

    private static float ComputeMask(float outVal)
    {
        const float Base = -0.7647f;
        const float Mul4 = 9.4708735624378946f;
        const float Mul2 = 17.35036561631863f;
        const float Offset2 = 302.59587815579727f;
        const float Mul3 = 6.7943250517376494f;
        const float Offset3 = 3.7179635626140772f;
        const float Offset4 = 0.25f * Offset3;
        const float Mul0 = 0.80061762862741759f;
        float v1 = MathF.Max(outVal * Mul0, 1e-3f);
        float v2 = 1f / (v1 + Offset2);
        float v3 = 1f / MathF.FusedMultiplyAdd(v1, v1, Offset3);
        float v4 = 1f / MathF.FusedMultiplyAdd(v1, v1, Offset4);
        return Base + MathF.FusedMultiplyAdd(Mul4, v4, MathF.FusedMultiplyAdd(Mul2, v2, Mul3 * v3));
    }

    private float Y(int x, int y) => _xyb[1][(y * _xsize) + x];

    /// <summary>One 64×64 tile of AdaptiveQuantizationImpl::ComputeTile.</summary>
    private void ComputeTile(float distance, float scale, int bx0, int by0, int bx1, int by1)
    {
        int xsize = _xsize;
        int ysize = _ysize;

        // The gamma-weighted Laplacian of Y, gammac · (c − b), once per pixel for both of libjxl's passes: the 1×1 mask over
        // the tile, and the squared Laplacian averaged over 4×4, four pixels beyond the tile.
        int yStart1 = by0 * 8;
        int yEnd1 = by1 * 8;
        int xStart1 = bx0 * 8;
        int xEnd1 = bx1 * 8;
        int yStart = yStart1;
        int yEnd = yEnd1;
        int xStart = xStart1;
        int xEnd = xEnd1;
        if (xStart != 0)
        {
            xStart -= 4;
        }

        if (xEnd != xsize)
        {
            xEnd += 4;
        }

        if (yStart != 0)
        {
            yStart -= 4;
        }

        if (yEnd != ysize)
        {
            yEnd += 4;
        }

        const float Limit = 0.2f;
        int pw = (xEnd - xStart) / 4;
        int ph = (yEnd - yStart) / 4;
        TileScratch scratch = t_tile ??= new();
        Span<float> pre = scratch.Pre.AsSpan(0, pw * ph);
        Span<float> rowOut = scratch.RowOut.AsSpan(0, xEnd - xStart);
        Span<float> d = scratch.Laplacian.AsSpan(0, xEnd - xStart);
        for (int y = yStart; y < yEnd; y++)
        {
            Laplacian(y, xStart, xEnd, d);
            if (y >= yStart1 && y < yEnd1)
            {
                int row = y * xsize;
                for (int x = xStart1; x < xEnd1; x++)
                {
                    float diff = MathF.Abs(d[x - xStart]);
                    diff = (float)Math.Log(1 + (double)diff);
                    Mask1x1[row + x] = 1f / (diff + 0.01f);
                }
            }

            SquaredRow(d, rowOut, y % 4 != 0, Limit);
            if (y % 4 == 3)
            {
                int row = (y - yStart) / 4 * pw;
                for (int qx = 0; qx < pw; qx++)
                {
                    pre[row + qx] = (rowOut[qx * 4] + rowOut[(qx * 4) + 1] + rowOut[(qx * 4) + 2] + rowOut[(qx * 4) + 3]) * 0.25f;
                }
            }
        }

        int fromX0 = xStart % 8 == 0 ? 0 : 1;
        int fromY0 = yStart % 8 == 0 ? 0 : 1;
        int bw = bx1 - bx0;
        int bh = by1 - by0;
        FuzzyErosion(distance, pre, pw, ph, fromX0, fromY0, bw * 2, bh * 2, bx0, by0);
        for (int y = by0; y < by1; y++)
        {
            for (int x = bx0; x < bx1; x++)
            {
                int at = (y * _blocksX) + x;
                Mask[at] = 1.0f / (QuantField[at] + 0.001f);
            }
        }

        PerBlockModulations(distance, scale, bx0, by0, bx1, by1);
    }

    [ThreadStatic]
    private static TileScratch? t_tile;

    private static readonly float MaskingMul = MathF.Sqrt(211.66567973503678f * 1e8f);

    /// <summary>
    /// Row <paramref name="y"/> of gammac · (c − b) from <paramref name="x0"/> to <paramref name="x1"/>: c the Y sample, b the
    /// mean of its four neighbours (edges repeated), gammac <see cref="RatioOfDerivatives(float, bool)"/> of c + 0.019. Eight at a
    /// time away from the left and right edges, with the same operations (fused where they are fused) in the same order.
    /// </summary>
    private void Laplacian(int y, int x0, int x1, Span<float> d)
    {
        const float MatchGammaOffset = 0.019f;
        int xsize = _xsize;
        float[] plane = _xyb[1];
        int row = y * xsize;
        int below = (y + 1 < _ysize ? y + 1 : y) * xsize;
        int above = (y > 0 ? y - 1 : y) * xsize;
        int x = x0;
        if (Fma.IsSupported && Avx.IsSupported)
        {
            ref float p = ref MemoryMarshal.GetArrayDataReference(plane);
            ref float o = ref MemoryMarshal.GetReference(d);
            Vector256<float> quarter = Vector256.Create(0.25f);
            Vector256<float> offset = Vector256.Create(MatchGammaOffset);
            int start = Math.Max(x0, 1);
            int end = Math.Min(x1, xsize - 1) - 8;
            for (; x < start; x++)
            {
                d[x - x0] = LaplacianAt(plane, row, below, above, x);
            }

            for (; x <= end; x += 8)
            {
                Vector256<float> c = Vector256.LoadUnsafe(ref p, (nuint)(row + x));
                Vector256<float> b = quarter * (Vector256.LoadUnsafe(ref p, (nuint)(below + x)) + Vector256.LoadUnsafe(ref p, (nuint)(above + x))
                    + Vector256.LoadUnsafe(ref p, (nuint)(row + x - 1)) + Vector256.LoadUnsafe(ref p, (nuint)(row + x + 1)));
                (RatioOfDerivatives(c + offset) * (c - b)).StoreUnsafe(ref o, (nuint)(x - x0));
            }
        }

        for (; x < x1; x++)
        {
            d[x - x0] = LaplacianAt(plane, row, below, above, x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float LaplacianAt(float[] plane, int row, int below, int above, int x)
    {
        const float MatchGammaOffset = 0.019f;
        int x2 = x + 1 < _xsize ? x + 1 : x;
        int x1 = x > 0 ? x - 1 : x;
        float c = plane[row + x];
        float b = 0.25f * (plane[below + x] + plane[above + x] + plane[row + x1] + plane[row + x2]);
        return RatioOfDerivatives(c + MatchGammaOffset, false) * (c - b);
    }

    /// <summary><see cref="RatioOfDerivatives(float, bool)"/> (not inverted) of eight values, with its fused steps fused.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> RatioOfDerivatives(Vector256<float> v)
    {
        const float Epsilon = 1e-2f;
        v = Vector256.Max(v, Vector256<float>.Zero);
        float numMul = SgRetMul * 3 * SgMul;
        float vOffset = (SgVOffset * InvLog2e) + Epsilon;
        float denMul = InvLog2e * SgMul;
        Vector256<float> v2 = v * v;
        Vector256<float> num = Fma.MultiplyAdd(Vector256.Create(numMul), v2, Vector256.Create(Epsilon));
        Vector256<float> den = Fma.MultiplyAdd(Vector256.Create(denMul) * v, v2, Vector256.Create(vOffset));
        return den / num;
    }

    /// <summary><see cref="RatioOfDerivatives(float, bool)"/> inverted, of eight values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> InverseRatioOfDerivatives(Vector256<float> v)
    {
        const float Epsilon = 1e-2f;
        v = Vector256.Max(v, Vector256<float>.Zero);
        float numMul = SgRetMul * 3 * SgMul;
        float vOffset = (SgVOffset * InvLog2e) + Epsilon;
        float denMul = InvLog2e * SgMul;
        Vector256<float> v2 = v * v;
        Vector256<float> num = Fma.MultiplyAdd(Vector256.Create(numMul), v2, Vector256.Create(Epsilon));
        Vector256<float> den = Fma.MultiplyAdd(Vector256.Create(denMul) * v, v2, Vector256.Create(vOffset));
        return num / den;
    }

    /// <summary>Adds <see cref="MaskingSqrt"/> of each squared Laplacian (at most <paramref name="limit"/>) to the row sums, or starts them.</summary>
    private static void SquaredRow(ReadOnlySpan<float> d, Span<float> sums, bool add, float limit)
    {
        const float LogOffset = 27.505837037000106f;
        int x = 0;
        if (Fma.IsSupported && Avx.IsSupported)
        {
            ref float dr = ref MemoryMarshal.GetReference(d);
            ref float sr = ref MemoryMarshal.GetReference(sums);
            Vector256<float> lim = Vector256.Create(limit);
            Vector256<float> mul = Vector256.Create(MaskingMul);
            Vector256<float> logOffset = Vector256.Create(LogOffset);
            Vector256<float> quarter = Vector256.Create(0.25f);
            for (; x <= d.Length - 8; x += 8)
            {
                Vector256<float> v = Vector256.LoadUnsafe(ref dr, (nuint)x);
                v *= v;
                v = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(v, lim), lim, v);
                v = quarter * Vector256.Sqrt(Fma.MultiplyAdd(v, mul, logOffset));
                (add ? Vector256.LoadUnsafe(ref sr, (nuint)x) + v : v).StoreUnsafe(ref sr, (nuint)x);
            }
        }

        for (; x < d.Length; x++)
        {
            float v = d[x] * d[x];
            if (v >= limit)
            {
                v = limit;
            }

            v = MaskingSqrt(v);
            sums[x] = add ? sums[x] + v : v;
        }
    }

    /// <summary>A thread's buffers for <see cref="ComputeTile"/>, large enough for a 64×64 tile and its four-pixel border.</summary>
    private sealed class TileScratch
    {
        internal readonly float[] Pre = new float[18 * 18];
        internal readonly float[] RowOut = new float[72];
        internal readonly float[] Laplacian = new float[72];
    }

    /// <summary>libjxl's FuzzyErosion: a weighted sum of the four smallest of each 3×3 neighbourhood, downsampled 2×.</summary>
    private void FuzzyErosion(float distance, ReadOnlySpan<float> from, int fw, int fh, int x0, int y0, int w, int h, int bx0, int by0)
    {
        ReadOnlySpan<float> mulBase = [0.125f, 0.1f, 0.09f, 0.06f];
        ReadOnlySpan<float> mulAdd = [0.0f, -0.1f, -0.09f, -0.06f];
        float mul = distance < 2.0f ? (2.0f - distance) * (1.0f / 2.0f) : 0;
        Span<float> k = stackalloc float[4];
        float normSum = 0;
        for (int i = 0; i < 4; i++)
        {
            k[i] = mulBase[i] + (mul * mulAdd[i]);
            normSum += k[i];
        }

        const float Total = 0.29959705784054957f;
        for (int i = 0; i < 4; i++)
        {
            k[i] *= Total / normSum;
        }

        Span<float> m = stackalloc float[4];
        for (int fy = 0; fy < h; fy++)
        {
            int y = fy + y0;
            int ym1 = y >= 1 ? y - 1 : y;
            int yp1 = y + 1 < fh ? y + 1 : y;
            int outRow = (by0 + (fy / 2)) * _blocksX;
            for (int fx = 0; fx < w; fx++)
            {
                int x = fx + x0;
                int xm1 = x >= 1 ? x - 1 : x;
                int xp1 = x + 1 < fw ? x + 1 : x;
                m[0] = from[(y * fw) + x];
                m[1] = from[(y * fw) + xm1];
                m[2] = from[(y * fw) + xp1];
                m[3] = from[(ym1 * fw) + xm1];
                Sort4(m);
                StoreMin4(from[(ym1 * fw) + x], m);
                StoreMin4(from[(ym1 * fw) + xp1], m);
                StoreMin4(from[(yp1 * fw) + xm1], m);
                StoreMin4(from[(yp1 * fw) + x], m);
                StoreMin4(from[(yp1 * fw) + xp1], m);
                float v = (k[0] * m[0]) + (k[1] * m[1]) + (k[2] * m[2]) + (k[3] * m[3]);
                int at = outRow + bx0 + (fx / 2);
                if (fx % 2 == 0 && fy % 2 == 0)
                {
                    QuantField[at] = v;
                }
                else
                {
                    QuantField[at] += v;
                }
            }
        }
    }

    private static void Sort4(Span<float> m)
    {
        if (m[0] > m[1])
        {
            (m[0], m[1]) = (m[1], m[0]);
        }

        if (m[0] > m[2])
        {
            (m[0], m[2]) = (m[2], m[0]);
        }

        if (m[0] > m[3])
        {
            (m[0], m[3]) = (m[3], m[0]);
        }

        if (m[1] > m[2])
        {
            (m[1], m[2]) = (m[2], m[1]);
        }

        if (m[1] > m[3])
        {
            (m[1], m[3]) = (m[3], m[1]);
        }

        if (m[2] > m[3])
        {
            (m[2], m[3]) = (m[3], m[2]);
        }
    }

    private static void StoreMin4(float v, Span<float> m)
    {
        if (v < m[3])
        {
            if (v < m[0])
            {
                m[3] = m[2];
                m[2] = m[1];
                m[1] = m[0];
                m[0] = v;
            }
            else if (v < m[1])
            {
                m[3] = m[2];
                m[2] = m[1];
                m[1] = v;
            }
            else if (v < m[2])
            {
                m[3] = m[2];
                m[2] = v;
            }
            else
            {
                m[3] = v;
            }
        }
    }

    /// <summary>libjxl's PerBlockModulations: the eroded mask through ComputeMask, then the gamma, HF and blue modulations of the exponent.</summary>
    private void PerBlockModulations(float distance, float scale, int bx0, int by0, int bx1, int by1)
    {
        float baseLevel = 0.48f * scale;
        const float DampenRampStart = 2.0f;
        const float DampenRampEnd = 14.0f;
        float dampen = 1.0f;
        if (distance >= DampenRampStart)
        {
            dampen = MathF.Max(0, 1.0f - ((distance - DampenRampStart) / (DampenRampEnd - DampenRampStart)));
        }

        float mul = scale * dampen;
        float add = (1.0f - dampen) * baseLevel;
        for (int iy = by0; iy < by1; iy++)
        {
            for (int ix = bx0; ix < bx1; ix++)
            {
                int at = (iy * _blocksX) + ix;
                float maskVal = ComputeMask(QuantField[at]);
                maskVal = GammaModulation(ix * 8, iy * 8, maskVal);
                float outVal = HfModulation(ix * 8, iy * 8, maskVal);
                outVal = MathF.Min(outVal, BlueModulation(ix * 8, iy * 8, maskVal));
                QuantField[at] = (JxlFastMath.Pow2(outVal * 1.442695041f) * mul) + add;
            }
        }
    }

    private float GammaModulation(int x, int y, float outVal)
    {
        const float Bias = 0.16f;
        float ratio = 0;
        if (Fma.IsSupported && Avx.IsSupported)
        {
            // Each row's 16 ratios eight at a time, then summed in the order below.
            Span<float> minus = stackalloc float[8];
            Span<float> plus = stackalloc float[8];
            for (int dy = 0; dy < 8; dy++)
            {
                int row = ((y + dy) * _xsize) + x;
                Vector256<float> iny = Vector256.Create(_xyb[1], row) + Vector256.Create(Bias);
                Vector256<float> inx = Vector256.Create(_xyb[0], row);
                InverseRatioOfDerivatives(iny - inx).CopyTo(minus);
                InverseRatioOfDerivatives(iny + inx).CopyTo(plus);
                for (int dx = 0; dx < 8; dx++)
                {
                    ratio += minus[dx];
                    ratio += plus[dx];
                }
            }
        }
        else
        {
            for (int dy = 0; dy < 8; dy++)
            {
                int row = (y + dy) * _xsize;
                for (int dx = 0; dx < 8; dx++)
                {
                    float iny = _xyb[1][row + x + dx] + Bias;
                    float inx = _xyb[0][row + x + dx];
                    ratio += RatioOfDerivatives(iny - inx, true);
                    ratio += RatioOfDerivatives(iny + inx, true);
                }
            }
        }

        ratio *= 0.5f / 64;
        const float Gamma = 0.1005613337192697f;
        return MathF.FusedMultiplyAdd(Gamma, JxlFastMath.Log2(ratio), outVal);
    }

    private float HfModulation(int x, int y, float outVal)
    {
        const float ValMin = 0.0206f;
        float sum = 0;
        float[] plane = _xyb[1];
        if (Avx.IsSupported && ((y + 7) * _xsize) + x + 9 <= plane.Length)
        {
            // Each row's differences eight at a time (the eighth to the right unused), then summed in the order below.
            Span<float> across = stackalloc float[8];
            Span<float> down = stackalloc float[8];
            Vector256<float> valMin = Vector256.Create(ValMin);
            for (int dy = 0; dy < 8; dy++)
            {
                int row = ((y + dy) * _xsize) + x;
                int next = dy == 7 ? row : row + _xsize;
                Vector256<float> p = Vector256.Create(plane, row);
                Vector256.Min(valMin, Vector256.Abs(p - Vector256.Create(plane, row + 1))).CopyTo(across);
                Vector256.Min(valMin, Vector256.Abs(p - Vector256.Create(plane, next))).CopyTo(down);
                for (int dx = 0; dx < 8; dx++)
                {
                    if (dx < 7)
                    {
                        sum += across[dx];
                    }

                    sum += down[dx];
                }
            }

            return outVal + ((sum * -0.38f) + 0.42f);
        }

        for (int dy = 0; dy < 8; dy++)
        {
            int row = (y + dy) * _xsize;
            int next = dy == 7 ? row : row + _xsize;
            for (int dx = 0; dx < 8; dx++)
            {
                float p = _xyb[1][row + x + dx];
                if (dx < 7)
                {
                    sum += MathF.Min(ValMin, MathF.Abs(p - _xyb[1][row + x + dx + 1]));
                }

                sum += MathF.Min(ValMin, MathF.Abs(p - _xyb[1][next + x + dx]));
            }
        }

        return outVal + ((sum * -0.38f) + 0.42f);
    }

    private float BlueModulation(int x, int y, float outVal)
    {
        const float Limit = 0.010474084867598155f;
        const float Offset = 0.0031994768654636393f;
        float sum = 0;
        if (Avx.IsSupported)
        {
            // Each row's terms eight at a time (zero where blue is not above), then summed in order: adding zero changes nothing.
            Span<float> terms = stackalloc float[8];
            Vector256<float> offset = Vector256.Create(Offset), limit = Vector256.Create(Limit);
            for (int dy = 0; dy < 8; dy++)
            {
                int row = ((y + dy) * _xsize) + x;
                Vector256<float> px = Vector256.Create(_xyb[0], row);
                Vector256<float> pb = Vector256.Create(_xyb[2], row);
                Vector256<float> pyEffective = Vector256.Create(_xyb[1], row) + offset + Vector256.Abs(px);
                Vector256.ConditionalSelect(Vector256.GreaterThan(pb, pyEffective), Vector256.Min(pb - pyEffective, limit), Vector256<float>.Zero).CopyTo(terms);
                for (int dx = 0; dx < 8; dx++)
                {
                    sum += terms[dx];
                }
            }
        }
        else
        {
            for (int dy = 0; dy < 8; dy++)
            {
                int row = (y + dy) * _xsize;
                for (int dx = 0; dx < 8; dx++)
                {
                    float px = _xyb[0][row + x + dx];
                    float pb = _xyb[2][row + x + dx];
                    float pyEffective = _xyb[1][row + x + dx] + Offset + MathF.Abs(px);
                    if (pb > pyEffective)
                    {
                        sum += MathF.Min(pb - pyEffective, Limit);
                    }
                }
            }
        }

        if (sum >= 32 * Limit)
        {
            sum = (64 * Limit) - sum;
        }

        const float MaxLimit = 15.463398341612438f;
        if (sum >= MaxLimit * Limit)
        {
            sum = MaxLimit * Limit;
        }

        return (sum * 0.90590804735610064f) + outVal;
    }

    /// <summary>libjxl's Blur1x1Masking: a symmetric 5×5 blur of the 1×1 mask with mirrored borders.</summary>
    private void BlurMask1x1(ParallelOptions parallel)
    {
        ReadOnlySpan<float> k = [0.364911248f, 0.05f, 0.1688888021f, 0.221069183f, 0.306563504f];
        double sum = 1.0 + (4 * (k[0] + k[1] + k[2] + k[4] + (2 * k[3])));
        float normalize = (float)(1.0 / Math.Max(sum, 1e-5));
        float[] dst = GC.AllocateUninitializedArray<float>(Mask1x1.Length);
        Symmetric5(Mask1x1, dst, _xsize, _ysize, [normalize, normalize * k[0], normalize * k[2], normalize * k[1], normalize * k[4], normalize * k[3]], parallel);
        Mask1x1 = dst;
    }

    /// <summary>
    /// libjxl's Symmetric5: a 5×5 convolution symmetric in both axes with mirrored borders. <paramref name="w"/> holds the
    /// weights of the center, the 4 neighbours at distance 1, the 4 at distance 2 along the axes, the 4 diagonal neighbours, the
    /// 4 at (±2, ±2) and the 8 at (±1, ±2) and (±2, ±1).
    /// </summary>
    internal static void Symmetric5(float[] src, float[] dst, int xsize, int ysize, ReadOnlySpan<float> w, ParallelOptions parallel)
    {
        (float wc, float wr, float wR, float wd, float wD, float wL) = (w[0], w[1], w[2], w[3], w[4], w[5]);
        Parallel.For(0, ysize, parallel, y =>
        {
            int ym1 = Mirror(y - 1, ysize) * xsize, yp1 = Mirror(y + 1, ysize) * xsize, ym2 = Mirror(y - 2, ysize) * xsize, yp2 = Mirror(y + 2, ysize) * xsize;
            int y0 = y * xsize;
            int lanes = Vector256<float>.Count;
            int inner = xsize >= 4 + lanes ? 2 + ((xsize - 4) / lanes * lanes) : 2;
            if (inner > 2)
            {
                // Away from the left and right edges, several pixels at a time with the same operations in the same order.
                Vector256<float> vc = Vector256.Create(wc), vr = Vector256.Create(wr), vR = Vector256.Create(wR);
                Vector256<float> vd = Vector256.Create(wd), vD = Vector256.Create(wD), vL = Vector256.Create(wL);
                ref float s = ref src[0];
                for (int x = 2; x < inner; x += lanes)
                {
                    Vector256<float> v = vc * L(ref s, y0 + x);
                    v += vr * (L(ref s, y0 + x - 1) + L(ref s, y0 + x + 1) + L(ref s, ym1 + x) + L(ref s, yp1 + x));
                    v += vR * (L(ref s, y0 + x - 2) + L(ref s, y0 + x + 2) + L(ref s, ym2 + x) + L(ref s, yp2 + x));
                    v += vd * (L(ref s, ym1 + x - 1) + L(ref s, ym1 + x + 1) + L(ref s, yp1 + x - 1) + L(ref s, yp1 + x + 1));
                    v += vD * (L(ref s, ym2 + x - 2) + L(ref s, ym2 + x + 2) + L(ref s, yp2 + x - 2) + L(ref s, yp2 + x + 2));
                    v += vL * (L(ref s, ym1 + x - 2) + L(ref s, ym1 + x + 2) + L(ref s, yp1 + x - 2) + L(ref s, yp1 + x + 2) + L(ref s, ym2 + x - 1) + L(ref s, ym2 + x + 1) + L(ref s, yp2 + x - 1) + L(ref s, yp2 + x + 1));
                    v.CopyTo(dst, y0 + x);
                }
            }

            for (int x = 0; x < xsize; x = x == 1 && inner > 2 ? inner : x + 1)
            {
                int xm1 = Mirror(x - 1, xsize), xp1 = Mirror(x + 1, xsize), xm2 = Mirror(x - 2, xsize), xp2 = Mirror(x + 2, xsize);
                float v = wc * src[y0 + x];
                v += wr * (src[y0 + xm1] + src[y0 + xp1] + src[ym1 + x] + src[yp1 + x]);
                v += wR * (src[y0 + xm2] + src[y0 + xp2] + src[ym2 + x] + src[yp2 + x]);
                v += wd * (src[ym1 + xm1] + src[ym1 + xp1] + src[yp1 + xm1] + src[yp1 + xp1]);
                v += wD * (src[ym2 + xm2] + src[ym2 + xp2] + src[yp2 + xm2] + src[yp2 + xp2]);
                v += wL * (src[ym1 + xm2] + src[ym1 + xp2] + src[yp1 + xm2] + src[yp1 + xp2] + src[ym2 + xm1] + src[ym2 + xp1] + src[yp2 + xm1] + src[yp2 + xp1]);
                dst[y0 + x] = v;
            }
        });
    }

    /// <summary>
    /// libjxl's GaborishInverse: sharpens each plane so that the decoder's Gaborish blur gives it back, with weights tuned on
    /// Butteraugli rather than an exact inverse.
    /// </summary>
    private static Vector256<float> L(ref float origin, int at) => Vector256.LoadUnsafe(ref origin, (nuint)at);

    internal static void GaborishInverse(float[][] planes, int xsize, int ysize, ParallelOptions parallel)
    {
        ReadOnlySpan<float> k = [-0.09495815671340026f, -0.041031725066768575f, 0.013710004822696948f, 0.006510206083837737f, -0.0014789063378272242f];
        double sum = 1.0 + (4 * (k[0] + k[1] + k[2] + k[4] + (2 * k[3])));
        float normalize = (float)(1.0 / Math.Max(sum, 1e-5));
        float[] weights = [normalize, normalize * k[0], normalize * k[2], normalize * k[1], normalize * k[4], normalize * k[3]];
        for (int c = 0; c < 3; c++)
        {
            // Into a new plane, which takes the old one's place (every sample is written).
            float[] dst = GC.AllocateUninitializedArray<float>(planes[c].Length);
            if (dst.Length != xsize * ysize)
            {
                planes[c].AsSpan().CopyTo(dst);
            }

            Symmetric5(planes[c], dst, xsize, ysize, weights, parallel);
            planes[c] = dst;
        }
    }

    /// <summary>libjxl's Mirror: reflects a coordinate into 0 to size − 1, repeating the edge sample.</summary>
    private static int Mirror(int x, int size)
    {
        while (x < 0 || x >= size)
        {
            x = x < 0 ? -x - 1 : (2 * size) - 1 - x;
        }

        return x;
    }
}
