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
    internal float[] Mask1x1 { get; }

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
        const float MatchGammaOffset = 0.019f;
        int xsize = _xsize;
        int ysize = _ysize;

        // The 1×1 Laplacian of Y over the tile (libjxl widens it only for streaming tiles).
        int yStart1 = by0 * 8;
        int yEnd1 = by1 * 8;
        int xStart1 = bx0 * 8;
        int xEnd1 = bx1 * 8;
        for (int y = yStart1; y < yEnd1; y++)
        {
            int y2 = y + 1 < ysize ? y + 1 : y;
            int y1 = y > 0 ? y - 1 : y;
            for (int x = xStart1; x < xEnd1; x++)
            {
                int x2 = x + 1 < xsize ? x + 1 : x;
                int x1 = x > 0 ? x - 1 : x;
                float c = Y(x, y);
                float b = 0.25f * (Y(x, y2) + Y(x, y1) + Y(x1, y) + Y(x2, y));
                float gammac = RatioOfDerivatives(c + MatchGammaOffset, false);
                float diff = MathF.Abs(gammac * (c - b));
                diff = (float)Math.Log(1 + (double)diff);
                Mask1x1[(y * xsize) + x] = 1f / (diff + 0.01f);
            }
        }

        // Squared Laplacian (masked by the gamma ratio), averaged over 4×4, four pixels beyond the tile.
        int yStart = by0 * 8;
        int yEnd = by1 * 8;
        int xStart = bx0 * 8;
        int xEnd = bx1 * 8;
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
        float[] pre = new float[pw * ph];
        float[] rowOut = new float[xEnd - xStart];
        for (int y = yStart; y < yEnd; y++)
        {
            int y2 = y + 1 < ysize ? y + 1 : y;
            int y1 = y > 0 ? y - 1 : y;
            for (int x = xStart; x < xEnd; x++)
            {
                int x2 = x + 1 < xsize ? x + 1 : x;
                int x1 = x > 0 ? x - 1 : x;
                float c = Y(x, y);
                float b = 0.25f * (Y(x, y2) + Y(x, y1) + Y(x1, y) + Y(x2, y));
                float gammac = RatioOfDerivatives(c + MatchGammaOffset, false);
                float diff = gammac * (c - b);
                diff *= diff;
                if (diff >= Limit)
                {
                    diff = Limit;
                }

                diff = MaskingSqrt(diff);
                if ((y % 4) != 0)
                {
                    rowOut[x - xStart] += diff;
                }
                else
                {
                    rowOut[x - xStart] = diff;
                }
            }

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

    /// <summary>libjxl's FuzzyErosion: a weighted sum of the four smallest of each 3×3 neighbourhood, downsampled 2×.</summary>
    private void FuzzyErosion(float distance, float[] from, int fw, int fh, int x0, int y0, int w, int h, int bx0, int by0)
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

        ratio *= 0.5f / 64;
        const float Gamma = 0.1005613337192697f;
        return MathF.FusedMultiplyAdd(Gamma, JxlFastMath.Log2(ratio), outVal);
    }

    private float HfModulation(int x, int y, float outVal)
    {
        const float ValMin = 0.0206f;
        float sum = 0;
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
        float[] src = (float[])Mask1x1.Clone();
        Symmetric5(src, Mask1x1, _xsize, _ysize, [normalize, normalize * k[0], normalize * k[2], normalize * k[1], normalize * k[4], normalize * k[3]], parallel);
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
            for (int x = 0; x < xsize; x++)
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
    internal static void GaborishInverse(float[][] planes, int xsize, int ysize, ParallelOptions parallel)
    {
        ReadOnlySpan<float> k = [-0.09495815671340026f, -0.041031725066768575f, 0.013710004822696948f, 0.006510206083837737f, -0.0014789063378272242f];
        double sum = 1.0 + (4 * (k[0] + k[1] + k[2] + k[4] + (2 * k[3])));
        float normalize = (float)(1.0 / Math.Max(sum, 1e-5));
        float[] weights = [normalize, normalize * k[0], normalize * k[2], normalize * k[1], normalize * k[4], normalize * k[3]];
        for (int c = 0; c < 3; c++)
        {
            float[] src = (float[])planes[c].Clone();
            Symmetric5(src, planes[c], xsize, ysize, weights, parallel);
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
