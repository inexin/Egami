using System.Runtime.Intrinsics;

namespace Egami.Formats.Jxl;

/// <summary>
/// The lossy encoder's block-size search (Phase 2.9.7, effort 5 and up): libjxl's AcStrategyHeuristics and ProcessRectACS
/// (enc_ac_strategy, see THIRD-PARTY-NOTICES). Each 64×64 tile first gets the best 8×8 transform of every block, scored by an
/// entropy estimate plus the masked information loss; the scores are then merged into 16×8, 8×16, 16×16 and up to 64×64 where
/// a larger DCT costs less. Effort 5 tries DCT8, DCT4x4, DCT2x2 and IDENTITY and aligned merges; effort 6 and up also DCT4x8 and
/// AFV and the merges between aligned positions.
/// </summary>
internal sealed class JxlAcStrategySearch
{
    // (type, encoding speed tier limit, entropy multiplier) of the 8×8 candidates; libjxl's speed tiers are 10 − effort.
    private static readonly (JxlTransformType Type, int SpeedLimit, double EntropyMul)[] Transforms8x8 =
    [
        (JxlTransformType.Dct8, 9, 0.8),
        (JxlTransformType.Dct4, 5, 1.08),
        (JxlTransformType.Dct2, 5, 0.95),
        (JxlTransformType.Dct4x8, 4, 0.85931637428340035),
        (JxlTransformType.Dct8x4, 4, 0.85931637428340035),
        (JxlTransformType.Hornuss, 5, 1.0427542510634957),
        (JxlTransformType.Afv0, 4, 0.81779489591359944),
        (JxlTransformType.Afv1, 4, 0.81779489591359944),
        (JxlTransformType.Afv2, 4, 0.81779489591359944),
        (JxlTransformType.Afv3, 4, 0.81779489591359944),
    ];

    private static readonly float[] ChannelMul = [(float)Math.Pow(8.2, 8.0), (float)Math.Pow(1.0, 8.0), (float)Math.Pow(1.03, 8.0)];

    // Per channel and order of transform: the inverse weights, with zeros where the LF image carries the coefficients.
    private static readonly float[][][] Inverse = BuildInverse();
    private static readonly float[] MaskOffset = [12.0f, 0.0f, 4.0f];

    // (type, priority, entropy multiplier) of the merges into larger transforms, in libjxl's order.
    private static readonly (JxlTransformType Type, byte Priority, float EntropyMul)[] Merges =
    [
        (JxlTransformType.Dct16x8, 2, 1.21f),
        (JxlTransformType.Dct8x16, 2, 1.21f),
        (JxlTransformType.Dct16x32, 4, 1.49f),
        (JxlTransformType.Dct32x16, 4, 1.49f),
        (JxlTransformType.Dct64x32, 6, 2.25f),
        (JxlTransformType.Dct32x64, 6, 2.25f),
    ];

    [ThreadStatic]
    private static Scratch? t_scratch;

    private readonly float[][] _xyb;
    private readonly int _stride;
    private readonly int _blocksX;
    private readonly int _blocksY;
    private readonly float[] _quant;
    private readonly float[] _mask1x1;
    private readonly float _distance;
    private readonly int _speedTier;
    private readonly float _infoLossMultiplier;
    private readonly float _zerosMul;
    private readonly float _costDelta;
    private readonly sbyte[] _strategy;
    private readonly bool[] _first;
    private readonly JxlDequantMatrices _matrices = JxlDequantMatrices.Defaults;

    /// <summary>Prepares the search over XYB planes (after Gaborish), the initial quantization field and the 1×1 mask.</summary>
    internal JxlAcStrategySearch(float[][] xyb, int blocksX, int blocksY, float[] quant, float[] mask1x1, float distance, int effort, sbyte[] strategy, bool[] first)
    {
        _xyb = xyb;
        _stride = blocksX * 8;
        _blocksX = blocksX;
        _blocksY = blocksY;
        _quant = quant;
        _mask1x1 = mask1x1;
        _distance = distance;
        _speedTier = 10 - effort;
        _strategy = strategy;
        _first = first;
        const float Bias = 0.13731742964354549f;
        float ratio = (distance + Bias) / (1.0f + Bias);
        _infoLossMultiplier = 1.2f * MathF.Pow(ratio, 0.33677806662454718f);
        _zerosMul = 9.3089059022677905f * MathF.Pow(ratio, 0.50990926717963703f);
        _costDelta = 10.833273317067883f * MathF.Pow(ratio, 0.36702940662370243f);
    }

    /// <summary>Chooses the transforms of the 64×64 tile at block (<paramref name="bx"/>, <paramref name="by"/>), with the tile's chroma-from-luma factors.</summary>
    internal void ProcessTile(int bx, int by, int w, int h, float xFactor, float bFactor)
    {
        // One scratch per thread: every buffer is written before it is read, so the tiles share it.
        Scratch s = t_scratch ??= new();
        float[] cmap = s.Cmap;
        cmap[0] = xFactor;
        cmap[2] = bFactor;
        float[] entropyEstimate = s.EntropyEstimate;
        Array.Clear(entropyEstimate);
        Array.Clear(s.Kind);
        (s.TileX, s.TileY) = (bx, by);
        const float K8x8Mul1 = -0.4f;
        const float K8x8Mul2 = 1.0f;
        const float K8x8Base = 1.4f;
        float mul8x8 = K8x8Mul2 + (K8x8Mul1 / (_distance + K8x8Base));
        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                (JxlTransformType best, float entropy) = FindBest8x8(8 * (bx + ix), 8 * (by + iy), cmap, s);
                Set(bx + ix, by + iy, best);
                entropyEstimate[(iy * 8) + ix] = entropy * mul8x8;
            }
        }

        const float Mul16X8 = 1.21f;
        const float Mul16X16 = 1.34f;
        const float Mul16X32 = 1.49f;
        const float Mul32X32 = 1.48f;
        const float Mul64X64 = 2.25f;
        byte[] priority = s.Priority;
        Array.Clear(priority);
        foreach ((JxlTransformType type, byte prio, float entropyMul) in Merges)
        {
            (int cw, int ch) = JxlTransforms8.Size(type);
            for (int cy = 0; cy + ch - 1 < h; cy += ch)
            {
                for (int cx = 0; cx + cw - 1 < w; cx += cw)
                {
                    if (cy + 7 < h && cx + 7 < w)
                    {
                        if (type == JxlTransformType.Dct32x64)
                        {
                            if ((cy | cx) % 8 == 0)
                            {
                                FirstLevelDivision(8, true, bx, by, cx, cy, cmap, entropyMul, Mul64X64, entropyEstimate, s);
                            }

                            continue;
                        }
                        else if (type == JxlTransformType.Dct32x16)
                        {
                            continue;
                        }
                    }

                    if ((type == JxlTransformType.Dct16x32 && cy % 4 != 0) || (type == JxlTransformType.Dct32x16 && cx % 4 != 0))
                    {
                        continue;
                    }

                    if (cy + 3 < h && cx + 3 < w)
                    {
                        if (type == JxlTransformType.Dct16x32)
                        {
                            if ((cy | cx) % 4 == 0)
                            {
                                FirstLevelDivision(4, true, bx, by, cx, cy, cmap, entropyMul, Mul32X32, entropyEstimate, s);
                            }

                            continue;
                        }
                        else if (type == JxlTransformType.Dct32x16)
                        {
                            continue;
                        }
                    }

                    if ((type == JxlTransformType.Dct16x32 && cy % 4 != 0) || (type == JxlTransformType.Dct32x16 && cx % 4 != 0))
                    {
                        continue;
                    }

                    if (cy + 1 < h && cx + 1 < w)
                    {
                        if (type == JxlTransformType.Dct8x16)
                        {
                            if ((cy | cx) % 2 == 0)
                            {
                                FirstLevelDivision(2, true, bx, by, cx, cy, cmap, entropyMul, Mul16X16, entropyEstimate, s);
                            }

                            continue;
                        }
                        else if (type == JxlTransformType.Dct16x8)
                        {
                            continue;
                        }
                    }

                    if ((type == JxlTransformType.Dct8x16 && cy % 2 == 1) || (type == JxlTransformType.Dct16x8 && cx % 2 == 1))
                    {
                        continue;
                    }

                    TryMerge(type, bx, by, cx, cy, cmap, entropyMul, prio, priority, entropyEstimate, s);
                }
            }
        }

        if (_speedTier >= 5)
        {
            return;
        }

        // Unaligned 16×8, 8×16 and 16×16, then 32×32, 16×32 and 32×16 every other block.
        for (int cy = 0; cy + 1 < h; cy++)
        {
            for (int cx = 0; cx + 1 < w; cx++)
            {
                if ((cy | cx) % 2 != 0)
                {
                    FirstLevelDivision(2, true, bx, by, cx, cy, cmap, Mul16X8, Mul16X16, entropyEstimate, s);
                }
            }
        }

        int step = _speedTier >= 1 ? 2 : 1;
        for (int cy = 0; cy + 3 < h; cy += step)
        {
            for (int cx = 0; cx + 3 < w; cx += step)
            {
                if ((cy | cx) % 4 == 0)
                {
                    continue;
                }

                FirstLevelDivision(4, true, bx, by, cx, cy, cmap, Mul16X32, Mul32X32, entropyEstimate, s);
            }
        }
    }

    /// <summary>Sets a varblock: its first block and every block it covers.</summary>
    private void Set(int x, int y, JxlTransformType t)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        for (int iy = 0; iy < h8; iy++)
        {
            for (int ix = 0; ix < w8; ix++)
            {
                int at = ((y + iy) * _blocksX) + x + ix;
                _strategy[at] = (sbyte)t;
                _first[at] = ix == 0 && iy == 0;
            }
        }
    }

    private (JxlTransformType, float) FindBest8x8(int x, int y, float[] cmap, Scratch s)
    {
        double best = 1e30;
        JxlTransformType bestTx = Transforms8x8[0].Type;
        foreach ((JxlTransformType type, int limit, double mul) in Transforms8x8)
        {
            if (limit < _speedTier)
            {
                continue;
            }

            float entropyMul = (float)(mul / Transforms8x8[0].EntropyMul);
            if (type is JxlTransformType.Dct2 or JxlTransformType.Hornuss && _distance < 5.0)
            {
                const float Favor2X2AtHighQuality = 0.4f;
                float weight = MathF.Pow((5.0f - _distance) / 5.0f, 2.0f);
                entropyMul -= Favor2X2AtHighQuality * weight;
            }

            if (type is not (JxlTransformType.Dct8 or JxlTransformType.Dct2 or JxlTransformType.Hornuss) && _distance > 4.0)
            {
                const float AvoidEntropyOfTransforms = 0.5f;
                float m = 1.0f;
                if (_distance < 12.0)
                {
                    m *= (12.0f - 4.0f) / (_distance - 4.0f);
                }

                entropyMul += AvoidEntropyOfTransforms * m;
            }

            float entropy = EstimateEntropy(type, entropyMul, x, y, cmap, s, (float)best);

            if (entropy < best)
            {
                bestTx = type;
                best = entropy;
            }
        }

        return (bestTx, (float)best);
    }

    /// <summary>Scratch buffers for one tile's estimates.</summary>
    private sealed class Scratch
    {
        internal readonly float[][] Block = [new float[64 * 64], new float[64 * 64], new float[64 * 64]];
        internal readonly float[] Pixels = new float[64 * 64];
        internal readonly float[] Error = new float[64 * 64];
        internal readonly float[] Cmap = new float[3];
        internal readonly float[] EntropyEstimate = new float[64];
        internal readonly byte[] Priority = new byte[64];

        // The tile's estimates of the merged transforms, by transform and first block: the unaligned passes ask for many twice.
        internal readonly float[] Value = new float[Cached * 64];
        internal readonly float[] Mul = new float[Cached * 64];
        internal readonly byte[] Kind = new byte[Cached * 64];  // 0 none, 1 the estimate, 2 a lower bound of it
        internal int TileX;
        internal int TileY;
        internal float Bound;
    }

    private const int Cached = 9;

    // The slot of a merged transform in the tile's estimates.
    private static int Slot(JxlTransformType t) => t switch
    {
        JxlTransformType.Dct16x8 => 0,
        JxlTransformType.Dct8x16 => 1,
        JxlTransformType.Dct16 => 2,
        JxlTransformType.Dct32x16 => 3,
        JxlTransformType.Dct16x32 => 4,
        JxlTransformType.Dct32 => 5,
        JxlTransformType.Dct64x32 => 6,
        JxlTransformType.Dct32x64 => 7,
        _ => 8,
    };

    /// <summary>
    /// <see cref="EstimateEntropy"/> of a merged transform at block (<paramref name="x"/>, <paramref name="y"/>), from the
    /// tile's estimates when it was made before with the same multiplier (it depends on nothing that changes within a tile).
    /// </summary>
    private float Estimate(JxlTransformType t, float entropyMul, int x, int y, float[] cmap, Scratch s, float threshold)
    {
        int at = (Slot(t) * 64) + ((y - s.TileY) * 8) + x - s.TileX;
        if (s.Kind[at] != 0 && s.Mul[at] == entropyMul)
        {
            if (s.Kind[at] == 1)
            {
                return s.Value[at];
            }

            if (s.Value[at] >= threshold)
            {
                return float.MaxValue;
            }
        }

        float entropy = EstimateEntropy(t, entropyMul, x * 8, y * 8, cmap, s, threshold);
        s.Mul[at] = entropyMul;
        (s.Kind[at], s.Value[at]) = entropy == float.MaxValue ? ((byte)2, s.Bound) : ((byte)1, entropy);
        return entropy;
    }

    /// <summary>
    /// libjxl's EstimateEntropy: the bits a varblock would take (√|q| per coefficient plus the cost of its nonzero count) times
    /// the transform's multiplier, plus the information loss: the quantization error, back in pixels, weighted by the 1×1 mask
    /// and taken to the 8th norm. Every caller only asks whether the estimate is below <paramref name="threshold"/> unless it
    /// is: once the channels so far show it cannot be, the rest is skipped and <see cref="float.MaxValue"/> returned (with the
    /// lower bound in <see cref="Scratch.Bound"/>), which decides the same.
    /// </summary>
    private float EstimateEntropy(JxlTransformType t, float entropyMul, int x, int y, float[] cmap, Scratch s, float threshold = float.MaxValue)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        int numBlocks = w8 * h8;
        int size = numBlocks * 64;
        int offset = (y * _stride) + x;

        // Y first: X and B are taken less their part predicted from Y, and each waits until it is needed.
        Transform(t, _xyb[1], offset, _stride, s.Pixels, s.Block[1]);

        int qx = x / 8, qy = y / 8;
        float quantNorm16;
        if (numBlocks == 1)
        {
            quantNorm16 = Quant(qx, qy);
        }
        else if (numBlocks == 2)
        {
            quantNorm16 = h8 == 2 ? MathF.Max(Quant(qx, qy), Quant(qx, qy + 1)) : MathF.Max(Quant(qx, qy), Quant(qx + 1, qy));
        }
        else
        {
            quantNorm16 = 0;
            for (int iy = 0; iy < h8; iy++)
            {
                for (int ix = 0; ix < w8; ix++)
                {
                    float q = Quant(qx + ix, qy + iy);
                    q *= q;
                    q *= q;
                    q *= q;
                    quantNorm16 += q * q;
                }
            }

            quantNorm16 /= numBlocks;
            quantNorm16 = JxlFastMath.Pow(quantNorm16, 1.0f / 16.0f);
        }

        bool transposed = JxlTransforms8.Transposed(t);
        int cw = Math.Max(w8, h8) * 8;
        int ch = Math.Min(w8, h8) * 8;
        Vector256<float> quant = Vector256.Create(quantNorm16);
        bool exits = threshold != float.MaxValue && entropyMul > 0 && quantNorm16 > 0;

        // Each channel's bits (the root sum, then the nonzero count) and loss, summed below in libjxl's order. Y goes first: it
        // decides most estimates that lose, so X and B are not transformed until it has been seen.
        Span<float> bits = stackalloc float[6];
        Span<Vector256<float>> losses = stackalloc Vector256<float>[3];
        Span<bool> done = stackalloc bool[3];
        float w = numBlocks >= 2 ? 1.0f + MathF.Min(3.0f, numBlocks / 8.0f) : 1;
        for (int step = 0; step < 3; step++)
        {
            int c = step == 0 ? 1 : step == 1 ? 0 : 2;
            if (c != 1)
            {
                Transform(t, _xyb[c], offset, _stride, s.Pixels, s.Block[c]);
            }

            float[] matrix = _matrices.GetWide(c, t);

            // libjxl's inverse weights, which are zero at the lowest frequencies the LF image carries.
            float[] inv = InverseWeights(c, t);
            float[] block = s.Block[c];
            float[] blockY = s.Block[1];
            Vector256<float> factor = Vector256.Create(cmap[c]);
            Vector256<float> entropyV = Vector256<float>.Zero;
            Vector256<float> zerosV = Vector256<float>.Zero;
            Vector256<float> one = Vector256.Create(1.0f);
            for (int i = 0; i < size; i += 8)
            {
                Vector256<float> val = (Vector256.LoadUnsafe(ref block[i]) - (Vector256.LoadUnsafe(ref blockY[i]) * factor)) * (Vector256.LoadUnsafe(ref inv[i]) * quant);
                Vector256<float> rval = Vector256.Round(val);
                (Vector256.LoadUnsafe(ref matrix[i]) * (val - rval)).StoreUnsafe(ref s.Error[i]);
                Vector256<float> q = Vector256.Abs(rval);
                entropyV = Vector256.Sqrt(q) + entropyV;
                zerosV += Vector256.ConditionalSelect(Vector256.Equals(q, Vector256<float>.Zero), Vector256<float>.Zero, one);
            }

            bits[2 * c] = _costDelta * SumOfLanes(entropyV);
            int nonZeros = (int)SumOfLanes(zerosV);
            int nbits = CeilLog2Nonzero((uint)nonZeros + 1) + 1;
            bits[(2 * c) + 1] = _zerosMul * (CeilLog2Nonzero((uint)nbits + 17) + nbits);
            losses[c] = Vector256<float>.Zero;
            done[c] = true;
            if (exits && Exits(bits, losses, done, w, entropyMul, size, quantNorm16, threshold, s))
            {
                return float.MaxValue;
            }

            // The error back in pixels, in the natural layout (which untransposed coefficients already have).
            int width = w8 * 8;
            float[] pixels = s.Error;
            if (transposed && JxlDct.InverseTransposed(s.Error, s.Pixels, width, width, h8 * 8))
            {
                // The coefficient layout is the transpose the DCT works on: no turning there and back.
                pixels = s.Pixels;
            }
            else
            {
                if (transposed)
                {
                    // Coefficient layout (cx, cy) is natural (cy, cx).
                    pixels = s.Pixels;
                    if (System.Runtime.Intrinsics.X86.Avx.IsSupported)
                    {
                        JxlDct.TransposeBlock(s.Error, cw, pixels, width, ch, cw);
                    }
                    else
                    {
                        for (int cy = 0; cy < ch; cy++)
                        {
                            for (int cx = 0; cx < cw; cx++)
                            {
                                pixels[(cx * width) + cy] = s.Error[(cy * cw) + cx];
                            }
                        }
                    }
                }

                JxlDct.Inverse(t, pixels, 0, width);
            }

            Vector256<float> maskOffset = Vector256.Create(MaskOffset[c]);
            Vector256<float> lossc = Vector256<float>.Zero;
            for (int py = 0; py < h8 * 8; py++)
            {
                int maskRow = ((y + py) * _stride) + x;
                int row = py * width;
                for (int px = 0; px < width; px += 8)
                {
                    Vector256<float> v = (Vector256.LoadUnsafe(ref _mask1x1[maskRow + px]) + maskOffset) * Vector256.LoadUnsafe(ref pixels[row + px]);
                    v *= v;
                    v *= v;
                    v *= v;
                    lossc += v;
                }
            }

            losses[c] = lossc;
            if (exits && step < 2 && Exits(bits, losses, done, w, entropyMul, size, quantNorm16, threshold, s))
            {
                return float.MaxValue;
            }
        }

        (float entropy, Vector256<float> loss) = Sum(bits, losses, done, w);
        return Total(entropy, loss, entropyMul, size, quantNorm16);
    }

    /// <summary>
    /// The bits and loss of the channels done so far, summed as libjxl sums all three (X, Y, B, with X's ringing weight
    /// <paramref name="w"/> on what X adds): the others count as zero, and every term only adds, so it bounds the whole from below.
    /// </summary>
    private static (float Entropy, Vector256<float> Loss) Sum(ReadOnlySpan<float> bits, ReadOnlySpan<Vector256<float>> losses, ReadOnlySpan<bool> done, float w)
    {
        float entropy = 0;
        Vector256<float> loss = Vector256<float>.Zero;
        for (int c = 0; c < 3; c++)
        {
            if (done[c])
            {
                loss += Vector256.Create(ChannelMul[c]) * losses[c];
                entropy += bits[2 * c];
                entropy += bits[(2 * c) + 1];
            }

            if (c == 0 && w != 1)
            {
                // X ringing in large blocks costs more.
                entropy *= w;
                loss *= w;
            }
        }

        return (entropy, loss);
    }

    // Whether the channels so far already put the estimate at or above the threshold, less a margin for rounding and
    // Math.Pow; the bound is kept for Estimate.
    private bool Exits(ReadOnlySpan<float> bits, ReadOnlySpan<Vector256<float>> losses, ReadOnlySpan<bool> done, float w, float entropyMul, int size, float quantNorm16, float threshold, Scratch s)
    {
        (float entropy, Vector256<float> loss) = Sum(bits, losses, done, w);
        float bound = Total(entropy, loss, entropyMul, size, quantNorm16) * 0.999f;
        if (float.IsFinite(bound) && bound >= threshold)
        {
            s.Bound = bound;
            return true;
        }

        return false;
    }

    // The estimate from the bits and the loss summed so far. Every channel only adds to both, so the estimate of the channels so
    // far, less a margin for rounding and Math.Pow, is a lower bound of the whole one.
    private float Total(float entropy, Vector256<float> loss, float entropyMul, int size, float quantNorm16)
    {
        float lossScalar = (float)(Math.Pow(SumOfLanes(loss) / size, 1.0f / 8.0f) * size / quantNorm16);
        entropy *= entropyMul;
        entropy += _infoLossMultiplier * lossScalar;
        return entropy;
    }

    /// <summary>Highway's SumOfLanes on AVX2: the two halves added, then pairs within, then the last two.</summary>
    private static float SumOfLanes(Vector256<float> v)
    {
        Vector128<float> h = v.GetLower() + v.GetUpper();
        Vector128<float> p = h + Vector128.Shuffle(h, Vector128.Create(2, 3, 0, 1));
        return p.GetElement(0) + p.GetElement(1);
    }

    private static float[] InverseWeights(int c, JxlTransformType t) => Inverse[(int)t][c];

    private static float[][][] BuildInverse()
    {
        JxlDequantMatrices m = JxlDequantMatrices.Defaults;
        float[][][] all = new float[(int)JxlTransformType.Dct128x256 + 1][][];
        for (int ti = 0; ti < all.Length; ti++)
        {
            JxlTransformType t = (JxlTransformType)ti;
            (int w8, int h8) = JxlTransforms8.Size(t);
            if (w8 > 8 || h8 > 8)
            {
                continue;
            }

            int cw = Math.Max(w8, h8) * 8;
            int ch = Math.Min(w8, h8) * 8;
            all[ti] = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                float[] matrix = m.GetWide(c, t);
                float[] inv = new float[matrix.Length];
                for (int i = 0; i < inv.Length; i++)
                {
                    bool llf = i % cw < cw / 8 && i / cw < ch / 8;
                    inv[i] = llf ? 0 : 1.0f / matrix[i];
                }

                all[ti][c] = inv;
            }
        }

        return all;
    }

    private static int CeilLog2Nonzero(uint x) => x <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(x - 1);

    private float Quant(int bx, int by) => _quant[(by * _blocksX) + bx];

    /// <summary>A varblock's forward transform into the coefficient layout (as JxlVarDctEncoder.Transform).</summary>
    private static void Transform(JxlTransformType t, float[] plane, int offset, int stride, float[] scratch, float[] output)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        int width = w8 * 8;
        int height = h8 * 8;
        bool transposed = JxlTransforms8.Transposed(t);

        // Untransposed coefficients keep the natural layout: the transform runs in place in the output.
        float[] work = transposed ? scratch : output;
        JxlForwardDct.Forward(t, plane.AsSpan(offset), stride, work);
        if (transposed)
        {
            if (System.Runtime.Intrinsics.X86.Avx.IsSupported)
            {
                JxlDct.TransposeBlock(scratch, width, output, height, height, width);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        output[(x * height) + y] = scratch[(y * width) + x];
                    }
                }
            }
        }
    }

    /// <summary>libjxl's TryMergeAcs: a larger transform replaces the blocks it covers when its estimate is lower than theirs.</summary>
    private void TryMerge(JxlTransformType t, int bx, int by, int cx, int cy, float[] cmap, float entropyMul, byte candidatePriority, byte[] priority, float[] entropyEstimate, Scratch s)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        float current = 0;
        for (int iy = 0; iy < h8; iy++)
        {
            for (int ix = 0; ix < w8; ix++)
            {
                if (priority[((cy + iy) * 8) + cx + ix] >= candidatePriority)
                {
                    return;
                }

                current += entropyEstimate[((cy + iy) * 8) + cx + ix];
            }
        }

        float candidate = Estimate(t, entropyMul, bx + cx, by + cy, cmap, s, current);
        if (candidate >= current)
        {
            return;
        }

        for (int iy = 0; iy < h8; iy++)
        {
            for (int ix = 0; ix < w8; ix++)
            {
                entropyEstimate[((cy + iy) * 8) + cx + ix] = 0;
                priority[((cy + iy) * 8) + cx + ix] = candidatePriority;
            }
        }

        Set(bx + cx, by + cy, t);
        entropyEstimate[(cy * 8) + cx] = candidate;
    }

    private static void SetEntropy(int cx, int cy, JxlTransformType t, float entropy, float[] entropyEstimate)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        for (int dy = 0; dy < h8; dy++)
        {
            for (int dx = 0; dx < w8; dx++)
            {
                entropyEstimate[((cy + dy) * 8) + cx + dx] = 0;
            }
        }

        entropyEstimate[(cy * 8) + cx] = entropy;
    }

    private static JxlTransformType Square(int blocks) => blocks == 2 ? JxlTransformType.Dct16 : blocks == 4 ? JxlTransformType.Dct32 : JxlTransformType.Dct64;

    private static JxlTransformType VerticalSplit(int blocks) => blocks == 2 ? JxlTransformType.Dct16x8 : blocks == 4 ? JxlTransformType.Dct32x16 : JxlTransformType.Dct64x32;

    private static JxlTransformType HorizontalSplit(int blocks) => blocks == 2 ? JxlTransformType.Dct8x16 : blocks == 4 ? JxlTransformType.Dct16x32 : JxlTransformType.Dct32x64;

    /// <summary>
    /// libjxl's FindBestFirstLevelDivisionForSquare: a square of <paramref name="blocks"/> blocks becomes one square DCT, two
    /// halves split one way or the other, or stays as it is, whichever costs least.
    /// </summary>
    private void FirstLevelDivision(int blocks, bool allowSquare, int bx, int by, int cx, int cy, float[] cmap, float mulJxK, float mulJxJ, float[] entropyEstimate, Scratch s)
    {
        int half = blocks / 2;
        JxlTransformType jxk = VerticalSplit(blocks);
        JxlTransformType kxj = HorizontalSplit(blocks);
        JxlTransformType jxj = Square(blocks);
        int x = bx + cx, y = by + cy;
        if (CrossesHorizontal(x, y, x + blocks) || CrossesHorizontal(x, y + blocks, x + blocks) || CrossesVertical(x, y, y + blocks) || CrossesVertical(x + blocks, y, y + blocks))
        {
            return;
        }

        bool allowJxK = !CrossesVertical(x + half, y, y + blocks);
        bool allowKxJ = !CrossesHorizontal(x, y + half, x + blocks);
        Span<float> entropy = stackalloc float[4];
        entropy.Clear();
        for (int dy = 0; dy < blocks; dy++)
        {
            for (int dx = 0; dx < blocks; dx++)
            {
                entropy[((dy / half) * 2) + (dx / half)] += entropyEstimate[((cy + dy) * 8) + cx + dx];
            }
        }

        float jxkLeft = float.MaxValue, jxkRight = float.MaxValue, kxjTop = float.MaxValue, kxjBottom = float.MaxValue, square = float.MaxValue;
        // Each half only counts when below the blocks it covers, and the square when below both splits: estimates above their
        // threshold decide nothing, so their rest is skipped.
        if (allowJxK)
        {
            if (_strategy[(y * _blocksX) + x] != (sbyte)jxk)
            {
                jxkLeft = Estimate(jxk, mulJxK, x, y, cmap, s, entropy[0] + entropy[2]);
            }

            if (_strategy[(y * _blocksX) + x + half] != (sbyte)jxk)
            {
                jxkRight = Estimate(jxk, mulJxK, x + half, y, cmap, s, entropy[1] + entropy[3]);
            }
        }

        if (allowKxJ)
        {
            if (_strategy[(y * _blocksX) + x] != (sbyte)kxj)
            {
                kxjTop = Estimate(kxj, mulJxK, x, y, cmap, s, entropy[0] + entropy[1]);
            }

            if (_strategy[((y + half) * _blocksX) + x] != (sbyte)kxj)
            {
                kxjBottom = Estimate(kxj, mulJxK, x, y + half, cmap, s, entropy[2] + entropy[3]);
            }
        }

        float costJxN = MathF.Min(jxkLeft, entropy[0] + entropy[2]) + MathF.Min(jxkRight, entropy[1] + entropy[3]);
        float costNxJ = MathF.Min(kxjTop, entropy[0] + entropy[1]) + MathF.Min(kxjBottom, entropy[2] + entropy[3]);
        if (allowSquare)
        {
            square = Estimate(jxj, mulJxJ, x, y, cmap, s, MathF.Min(costJxN, costNxJ));
        }

        if (square < costJxN && square < costNxJ)
        {
            Set(x, y, jxj);
            SetEntropy(cx, cy, jxj, square, entropyEstimate);
        }
        else if (costJxN < costNxJ)
        {
            if (jxkLeft < entropy[0] + entropy[2])
            {
                Set(x, y, jxk);
                SetEntropy(cx, cy, jxk, jxkLeft, entropyEstimate);
            }

            if (jxkRight < entropy[1] + entropy[3])
            {
                Set(x + half, y, jxk);
                SetEntropy(cx + half, cy, jxk, jxkRight, entropyEstimate);
            }
        }
        else
        {
            if (kxjTop < entropy[0] + entropy[1])
            {
                Set(x, y, kxj);
                SetEntropy(cx, cy, kxj, kxjTop, entropyEstimate);
            }

            if (kxjBottom < entropy[2] + entropy[3])
            {
                Set(x, y + half, kxj);
                SetEntropy(cx, cy + half, kxj, kxjBottom, entropyEstimate);
            }
        }
    }

    /// <summary>libjxl's MultiBlockTransformCrossesHorizontalBoundary: whether a varblock crosses the row boundary at <paramref name="y"/> between the two x.</summary>
    private bool CrossesHorizontal(int startX, int y, int endX)
    {
        if (startX >= _blocksX || y >= _blocksY || y % 8 == 0)
        {
            return false;
        }

        endX = Math.Min(endX, _blocksX);
        int limit = startX & ~7;
        int row = y * _blocksX;
        while (startX != limit && !_first[row + startX])
        {
            startX--;
        }

        for (int x = startX; x < endX;)
        {
            if (!_first[row + x])
            {
                return true;
            }

            x += JxlTransforms8.Size((JxlTransformType)_strategy[row + x]).W;
        }

        return false;
    }

    /// <summary>libjxl's MultiBlockTransformCrossesVerticalBoundary: whether a varblock crosses the column boundary at <paramref name="x"/> between the two y.</summary>
    private bool CrossesVertical(int x, int startY, int endY)
    {
        if (x >= _blocksX || startY >= _blocksY || x % 8 == 0)
        {
            return false;
        }

        endY = Math.Min(endY, _blocksY);
        int limit = startY & ~7;
        while (startY != limit && !_first[(startY * _blocksX) + x])
        {
            startY--;
        }

        for (int y = startY; y < endY;)
        {
            if (!_first[(y * _blocksX) + x])
            {
                return true;
            }

            y += JxlTransforms8.Size((JxlTransformType)_strategy[(y * _blocksX) + x]).H;
        }

        return false;
    }
}
