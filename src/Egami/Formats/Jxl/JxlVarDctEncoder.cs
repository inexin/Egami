namespace Egami.Formats.Jxl;

/// <summary>What the lossy encoder is asked for: the distance, the effort and the threads it may use.</summary>
internal sealed record JxlLossySettings
{
    /// <summary>Gets the Butteraugli-style distance, 1.0 visually lossless.</summary>
    internal float Distance { get; init; } = 1;

    /// <summary>Gets the effort, 1 to 7, on libjxl's scale (its speed tiers Lightning to Squirrel).</summary>
    internal int Effort { get; init; } = 5;

    internal int Parallelism { get; init; } = Environment.ProcessorCount;
}

/// <summary>
/// Writes a lossy JPEG XL codestream (Phase 2.9.7): one VarDCT frame in XYB, as libjxl 0.12.0's encoder makes it at the same
/// distance and effort (enc_frame, enc_heuristics, enc_group, enc_cache, enc_modular's VarDCT streams, enc_coeff_order and
/// enc_entropy_coder; see THIRD-PARTY-NOTICES). The raster's linear sRGB goes to XYB (<see cref="JxlXyb"/>), each varblock through
/// its forward transform (<see cref="JxlForwardDct"/>), its coefficients quantized by the quantization field and the default
/// weights, the LF image and the HF metadata through Modular streams, and the HF coefficients as tokens.
/// </summary>
internal sealed class JxlVarDctEncoder
{
    private const int GroupDim = 256;
    private const int GroupBlocks = GroupDim / 8;
    private const int LfGroupBlocks = GroupDim;
    private const int TileBlocks = 8;
    private const int NumOrders = 13;
    private const int GlobalScaleDenom = 1 << 16;
    private const int GlobalScaleNumerator = 4096;
    private const int QuantMax = 256;
    private const int NonZeroBuckets = 37;
    private const int ZeroDensityContexts = 458;
    private const int ColorFactor = 84;

    // libjxl's InitialQuantDC constants (enc_adaptive_quantization).
    private const float DcQuantPow = 0.83f;
    private const float DcQuant = 1.095924047623553f;

    private static readonly float[] InvDcQuant = [4096f, 512f, 256f];

    // Dequantization biases for the round trip of Y (kDefaultQuantBias): X, Y and B for ±1, and the numerator above.
    private static readonly float[] QuantBias = [1 - 0.05465007330715401f, 1 - 0.07005449891748593f, 1 - 0.049935103337343655f, 0.145f];

    // The default block context map (HfBlockContext with all_default), per channel (Y, X, B) and order.
    private static readonly byte[] DefaultBlockContexts = [0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14];
    private const int DefaultBlockClusters = 15;

    private static readonly int[] CoeffFreqContext =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 23, 23, 23, 23,
        24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    private static readonly int[] CoeffNumNonzeroContext =
    [
        0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180, 180, 180, 180,
        180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
    ];

    private readonly JxlLossySettings _settings;
    private readonly ParallelOptions _parallel;
    private readonly int _width;
    private readonly int _height;
    private readonly int _blocksX;
    private readonly int _blocksY;
    private readonly int _groupsX;
    private readonly int _groups;
    private readonly int _lfGroupsX;
    private readonly int _lfGroups;
    private readonly int _tilesX;
    private readonly int _tilesY;

    // The XYB planes, padded to whole blocks by repeating the last column and row; row stride _blocksX * 8.
    private readonly float[][] _xyb;

    // Per block: the varblock transform (for the first block of a varblock; -1 for the others), the raw quantization field and
    // the EPF sharpness. Per 64×64 tile: the chroma-from-luma factors.
    private readonly sbyte[] _strategy;
    private readonly int[] _quantField;
    private readonly byte[] _epfSharpness;
    private readonly sbyte[] _yToX;
    private readonly sbyte[] _yToB;

    // The LF image (one value per block, per channel, X, Y and B) and its quantized values.
    private readonly float[][] _lf = new float[3][];
    private readonly int[][] _lfQuant = new int[3][];

    // Each pass group's quantized coefficients, per channel, varblock after varblock in the coefficient layout.
    private readonly int[][][] _coeffs;
    private readonly int[][] _orders = new int[NumOrders * 3][];
    private int _usedOrders;

    // The block context map (HfBlockContext): the default one, or from effort 4 one clustered by the quantization field.
    private byte[] _blockContexts = DefaultBlockContexts;
    private int _blockClusters = DefaultBlockClusters;
    private int[] _qfThresholds = [];

    // The LF image's extra precision bits (1 from effort 4, where LF values are quantized against the WP predictor).
    private int _lfPrecision;

    // From effort 5: the adaptive quantization's masks, and whether Gaborish is on.
    private JxlAdaptiveQuantization? _aq;
    private bool _gaborish;

    private int _globalScale;
    private int _quantDc;
    private int _xQmScale;
    private int _bQmScale;
    private int _epfIterations;

    private JxlVarDctEncoder(JxlLinearImage image, JxlLossySettings settings, float intensityTarget)
    {
        _settings = settings;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.Parallelism) };
        _width = image.Width;
        _height = image.Height;
        _blocksX = Ceil(_width, 8);
        _blocksY = Ceil(_height, 8);
        _groupsX = Ceil(_width, GroupDim);
        _groups = _groupsX * Ceil(_height, GroupDim);
        _lfGroupsX = Ceil(_blocksX, LfGroupBlocks);
        _lfGroups = _lfGroupsX * Ceil(_blocksY, LfGroupBlocks);
        _tilesX = Ceil(_blocksX, TileBlocks);
        _tilesY = Ceil(_blocksY, TileBlocks);
        int blocks = _blocksX * _blocksY;
        _strategy = new sbyte[blocks];
        _quantField = new int[blocks];
        _epfSharpness = new byte[blocks];
        _yToX = new sbyte[_tilesX * _tilesY];
        _yToB = new sbyte[_tilesX * _tilesY];
        _coeffs = new int[_groups][][];
        for (int c = 0; c < 3; c++)
        {
            _lf[c] = new float[blocks];
            _lfQuant[c] = new int[blocks];
        }

        _xyb = Pad(image, intensityTarget, _blocksX * 8, _blocksY * 8, _parallel);
    }

    private int Speed => Math.Clamp(_settings.Effort, 1, 7);

    /// <summary>Encodes the image as a bare codestream: the image header from <paramref name="header"/>, then one VarDCT frame.</summary>
    internal static byte[] Encode(JxlLinearImage image, JxlEncodeInput header, JxlLossySettings settings, float intensityTarget = 255)
    {
        JxlVarDctEncoder e = new(image, settings, intensityTarget);
        e.ChooseParameters();
        e.ComputeChromaFromLuma();
        e.ComputeCoefficients();
        e.QuantizeLf();
        e.ComputeOrders();
        e.FindBlockEntropyModel();
        List<byte[]> sections = e.Sections();

        JxlBitWriter w = new();
        JxlLosslessEncoder.WriteImageHeader(w, header with { Xyb = true });
        if (header.Icc is not null)
        {
            JxlIccEncoder.Write(w, header.Icc);
        }

        w.ZeroPadToByte();
        e.WriteFrameHeader(w);
        w.WriteBool(false); // no TOC permutation
        w.ZeroPadToByte();
        JxlU32 size = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        foreach (byte[] s in sections)
        {
            w.WriteU32(size, (uint)s.Length);
        }

        w.ZeroPadToByte();
        foreach (byte[] s in sections)
        {
            w.Append(s);
        }

        return w.ToArray();
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    private static int CeilLog2(long x) => x <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(x - 1));

    /// <summary>Converts the linear planes to XYB and pads them to whole blocks, repeating the last column and row.</summary>
    private static float[][] Pad(JxlLinearImage image, float intensityTarget, int width, int height, ParallelOptions parallel)
    {
        int w = image.Width;
        int h = image.Height;
        float[][] xyb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            xyb[c] = new float[(long)width * height];
        }

        Parallel.For(0, h, parallel, y =>
        {
            float[][] row = [new float[w], new float[w], new float[w]];
            for (int c = 0; c < 3; c++)
            {
                image.Color[c].AsSpan(y * w, w).CopyTo(row[c]);
            }

            JxlXyb.LinearToXyb(row, 0, w, intensityTarget);
            for (int c = 0; c < 3; c++)
            {
                Span<float> dst = xyb[c].AsSpan(y * width, width);
                row[c].CopyTo(dst);
                dst[w..].Fill(row[c][w - 1]);
            }
        });

        for (int c = 0; c < 3; c++)
        {
            for (int y = h; y < height; y++)
            {
                xyb[c].AsSpan((h - 1) * width, width).CopyTo(xyb[c].AsSpan(y * width, width));
            }
        }

        return xyb;
    }

    /// <summary>
    /// Chooses the frame's settings as libjxl's LossyFrameHeuristics does from effort 1 to 4: DCT8 everywhere, one quantization
    /// value for every block from the distance, the DC quantizer from InitialQuantDC, the X and B quantization matrix scales and
    /// the edge-preserving filter's iterations from the distance.
    /// </summary>
    private void ChooseParameters()
    {
        float d = _settings.Distance;
        float quantDc = InitialQuantDc(d);
        // Gaborish from effort 5 above distance 0.5, as libjxl's LoopFilterFromParams.
        _gaborish = Speed >= 5 && d > 0.5f;
        if (Speed >= 5)
        {
            // InitialQuantField on the image before Gaborish, at a lower distance when Gaborish is off.
            float iqfDistance = _gaborish ? d : d * 0.62f;
            _aq = JxlAdaptiveQuantization.Compute(_xyb, _blocksX * 8, _blocksY * 8, iqfDistance, _parallel);
            ComputeGlobalScaleAndQuant(quantDc, 0.39f / d, 0);
            if (_gaborish)
            {
                JxlAdaptiveQuantization.GaborishInverse(_xyb, _blocksX * 8, _blocksY * 8, _parallel);
            }

            float inv = (float)(1.0 * GlobalScaleDenom / _globalScale);
            float[] field = _aq.QuantField;
            AdjustQuantField(field, d);
            for (int i = 0; i < field.Length; i++)
            {
                _quantField[i] = ClampQuant((field[i] * inv) + 0.5f);
            }
        }
        else
        {
            float q = 0.79f / d;
            ComputeGlobalScaleAndQuant(quantDc, q, 0);
            float invGlobalScale = (float)(1.0 * GlobalScaleDenom / _globalScale);
            int raw = ClampQuant((q * invGlobalScale) + 0.5f);
            Array.Fill(_quantField, raw);
        }

        Array.Fill(_epfSharpness, (byte)4);

        _xQmScale = 3;
        foreach (float step in (ReadOnlySpan<float>)[2.5f, 5.5f, 9.5f])
        {
            if (d > step)
            {
                _xQmScale++;
            }
        }

        _bQmScale = 2;
        _epfIterations = 0;
        foreach (float threshold in (ReadOnlySpan<float>)[0.7f, 1.5f, 4.0f])
        {
            if (d >= threshold)
            {
                _epfIterations++;
            }
        }
    }

    /// <summary>
    /// libjxl's AdjustQuantField: each varblock larger than 8×8 takes the largest quantization value of its blocks, moved towards
    /// their mean above distance 1.54 for varblocks of four blocks or more.
    /// </summary>
    private void AdjustQuantField(float[] field, float distance)
    {
        float mixer = 1.0f;
        const float Limit = 1.54138f;
        const float Mul = 0.56391f;
        if (distance > Limit)
        {
            mixer = MathF.Max(0, mixer - ((distance - Limit) * Mul));
        }

        for (int y = 0; y < _blocksY; y++)
        {
            for (int x = 0; x < _blocksX; x++)
            {
                int at = (y * _blocksX) + x;
                if (_strategy[at] < 0)
                {
                    continue;
                }

                (int w8, int h8) = JxlTransforms8.Size((JxlTransformType)_strategy[at]);
                if (w8 * h8 == 1)
                {
                    continue;
                }

                float max = field[at];
                float mean = 0;
                for (int iy = 0; iy < h8; iy++)
                {
                    for (int ix = 0; ix < w8; ix++)
                    {
                        float v = field[at + ix + (iy * _blocksX)];
                        mean += v;
                        max = MathF.Max(v, max);
                    }
                }

                mean /= w8 * h8;
                if (w8 * h8 >= 4)
                {
                    max *= mixer;
                    max += (1.0f - mixer) * mean;
                }

                for (int iy = 0; iy < h8; iy++)
                {
                    for (int ix = 0; ix < w8; ix++)
                    {
                        field[at + ix + (iy * _blocksX)] = max;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Chooses each 64×64 tile's chroma-from-luma factors from effort 5 (CfLHeuristics::ComputeTile after the block sizes and
    /// quantization field are known): fast least squares at efforts 5 and 6, Newton steps at 7.
    /// </summary>
    private void ComputeChromaFromLuma()
    {
        if (Speed < 5)
        {
            return;
        }

        JxlDequantMatrices matrices = JxlDequantMatrices.Defaults;
        bool fast = Speed <= 6;
        int stride = _blocksX * 8;
        Parallel.For(0, _tilesX * _tilesY, _parallel, tile =>
        {
            int tx = tile % _tilesX;
            int ty = tile / _tilesX;
            int x0 = tx * TileBlocks, y0 = ty * TileBlocks;
            int x1 = Math.Min(x0 + TileBlocks, _blocksX), y1 = Math.Min(y0 + TileBlocks, _blocksY);
            List<float> yx = [], xs = [], yb = [], bs = [];
            float[] scratch = new float[64 * 64];
            float[][] block = [new float[64 * 64], new float[64 * 64], new float[64 * 64]];
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int at = (y * _blocksX) + x;
                    if (_strategy[at] < 0)
                    {
                        continue;
                    }

                    JxlTransformType t = (JxlTransformType)_strategy[at];
                    (int w8, int h8) = JxlTransforms8.Size(t);
                    if (w8 + x0 > x1 || h8 + y0 > y1)
                    {
                        continue;
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        Transform(t, _xyb[c], (y * 8 * stride) + (x * 8), stride, scratch, block[c]);
                    }

                    int cw = Math.Max(w8, h8);
                    int ch = Math.Min(w8, h8);
                    for (int iy = 0; iy < ch; iy++)
                    {
                        for (int ix = 0; ix < cw; ix++)
                        {
                            int k = (iy * cw * 8) + ix;
                            block[0][k] = block[1][k] = block[2][k] = 0;
                        }
                    }

                    float[] qmX = matrices.GetWide(0, t);
                    float[] qmB = matrices.GetWide(2, t);
                    float q = Scale * 128 * _quantField[at];
                    int size = w8 * h8 * 64;
                    for (int i = 0; i < size; i++)
                    {
                        float qx = q / qmX[i];
                        float qb = q / qmB[i];
                        yx.Add(block[1][i] * qx);
                        xs.Add(block[0][i] * qx);
                        yb.Add(block[1][i] * qb);
                        bs.Add(block[2][i] * qb);
                    }
                }
            }

            int t2 = (ty * _tilesX) + tx;
            _yToX[t2] = (sbyte)JxlChromaFromLuma.FindBestMultiplier(yx.ToArray(), xs.ToArray(), 0f, fast);
            _yToB[t2] = (sbyte)JxlChromaFromLuma.FindBestMultiplier(yb.ToArray(), bs.ToArray(), 1f, fast);
        });
    }

    /// <summary>libjxl's InitialQuantDC: the DC quantizer for a distance, with a gentler slope at high distances.</summary>
    internal static float InitialQuantDc(float distance)
    {
        const float DcMul = 0.3f;
        float dcDistance = MathF.Max(0.5f * distance, MathF.Min(distance, DcMul * MathF.Pow((1.0f / DcMul) * distance, DcQuantPow)));
        return MathF.Min(DcQuant / dcDistance, 50f);
    }

    /// <summary>libjxl's Quantizer::ComputeGlobalScaleAndQuant: the global scale puts the median quantization value near 5.</summary>
    private void ComputeGlobalScaleAndQuant(float quantDc, float quantMedian, float quantMedianAbsd)
    {
        const float QuantFieldTarget = 5;
        float scale = GlobalScaleDenom * (quantMedian - quantMedianAbsd) / QuantFieldTarget;
        scale = Math.Clamp(scale, 1, 1 << 15);
        int globalScale = (int)scale;
        int scaledQuantDc = (int)(quantDc * GlobalScaleNumerator * 1.6);
        if (globalScale > scaledQuantDc)
        {
            globalScale = Math.Max(scaledQuantDc, 1);
        }

        _globalScale = globalScale;
        float invGlobalScale = (float)(1.0 * GlobalScaleDenom / _globalScale);
        float fval = MathF.Min(1 << 16, (quantDc * invGlobalScale) + 0.5f);
        _quantDc = (int)fval;
    }

    private static int ClampQuant(float value) => (int)MathF.Max(1.0f, MathF.Min(value, QuantMax));

    /// <summary>Gets the quantization step multiplier libjxl's Quantizer::Scale gives: the global scale over 2^16.</summary>
    private float Scale => (float)(_globalScale * (1.0 / GlobalScaleDenom));

    /// <summary>Gets the LF step of a channel (GetDcStep): what one quantized LF unit is worth.</summary>
    private float DcStep(int c) => (float)(1.0 * GlobalScaleDenom / _globalScale) / _quantDc * (1f / InvDcQuant[c]);

    /// <summary>Gets the reciprocal LF step of a channel (GetInvDcStep).</summary>
    private float InvDcStep(int c) => InvDcQuant[c] * (Scale * _quantDc);

    /// <summary>Gets a pass group's place and size in blocks.</summary>
    private (int X0, int Y0, int W, int H) GroupRect(int g)
    {
        int x0 = g % _groupsX * GroupBlocks;
        int y0 = g / _groupsX * GroupBlocks;
        return (x0, y0, Math.Min(GroupBlocks, _blocksX - x0), Math.Min(GroupBlocks, _blocksY - y0));
    }

    /// <summary>Gets an LF group's place and size in blocks.</summary>
    private (int X0, int Y0, int W, int H) LfGroupRect(int g)
    {
        int x0 = g % _lfGroupsX * LfGroupBlocks;
        int y0 = g / _lfGroupsX * LfGroupBlocks;
        return (x0, y0, Math.Min(LfGroupBlocks, _blocksX - x0), Math.Min(LfGroupBlocks, _blocksY - y0));
    }

    /// <summary>
    /// Computes every pass group's coefficients (libjxl's ComputeCoefficients): each varblock's forward transform in X, Y and B,
    /// Y quantized and dequantized again so that chroma from luma takes out what the decoder will add back, then X and B
    /// quantized; the LF samples of each channel come from the lowest frequencies.
    /// </summary>
    private void ComputeCoefficients()
    {
        JxlDequantMatrices matrices = JxlDequantMatrices.Defaults;
        float xQmMul = MathF.Pow(1.25f, _xQmScale - 2.0f);
        float bQmMul = MathF.Pow(1.25f, _bQmScale - 2.0f);
        float[] qmMul = [xQmMul, 1f, bQmMul];
        float[] baseCorrelation = [0f, 0f, 1f];
        int stride = _blocksX * 8;
        Parallel.For(0, _groups, _parallel, g =>
        {
            (int gx0, int gy0, int gw, int gh) = GroupRect(g);
            List<int>[] lists = [new(gw * gh * 64), new(gw * gh * 64), new(gw * gh * 64)];
            float[] pixels = new float[64 * 64];
            float[][] coeffs = [new float[64 * 64], new float[64 * 64], new float[64 * 64]];
            int[][] quantized = [new int[64 * 64], new int[64 * 64], new int[64 * 64]];
            for (int by = gy0; by < gy0 + gh; by++)
            {
                for (int bx = gx0; bx < gx0 + gw; bx++)
                {
                    int at = (by * _blocksX) + bx;
                    JxlTransformType t = (JxlTransformType)_strategy[at];
                    (int w8, int h8) = JxlTransforms8.Size(t);
                    int size = w8 * h8 * 64;
                    int tile = ((by / TileBlocks) * _tilesX) + (bx / TileBlocks);
                    float xFactor = baseCorrelation[0] + (_yToX[tile] * (1f / ColorFactor));
                    float bFactor = baseCorrelation[2] + (_yToB[tile] * (1f / ColorFactor));
                    for (int c = 0; c < 3; c++)
                    {
                        Transform(t, _xyb[c], ((by * 8) * stride) + (bx * 8), stride, pixels, coeffs[c]);
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        LowFrequencies(t, coeffs[c], c, bx, by);
                    }

                    int quant = _quantField[at];
                    QuantizeRoundtripY(matrices, t, w8, h8, ref quant, coeffs, qmMul, quantized[1]);
                    for (int k = 0; k < size; k++)
                    {
                        coeffs[0][k] -= xFactor * coeffs[1][k];
                        coeffs[2][k] -= bFactor * coeffs[1][k];
                    }

                    foreach (int c in (ReadOnlySpan<int>)[0, 2])
                    {
                        Span<float> thresholds = [0.58f, 0.62f, 0.62f, 0.62f];
                        QuantizeBlock(matrices, c, qmMul[c], t, w8, h8, thresholds, coeffs[c], quant, quantized[c]);
                    }

                    _quantField[at] = quant;
                    for (int c = 0; c < 3; c++)
                    {
                        lists[c].AddRange(quantized[c].AsSpan(0, size));
                    }
                }
            }

            _coeffs[g] = [.. lists.Select(l => l.ToArray())];
        });
    }

    /// <summary>Runs a varblock's forward transform on its pixels and puts the coefficients in the coefficient layout (transposed for tall and square DCTs).</summary>
    private static void Transform(JxlTransformType t, float[] plane, int offset, int stride, float[] scratch, float[] output)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        int width = w8 * 8;
        int height = h8 * 8;
        for (int y = 0; y < height; y++)
        {
            plane.AsSpan(offset + (y * stride), width).CopyTo(scratch.AsSpan(y * width, width));
        }

        JxlForwardDct.Forward(t, scratch, 0, width);
        if (JxlTransforms8.Transposed(t))
        {
            // The coefficient layout is height wide: natural (x, y) is stored at (y, x).
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    output[(x * height) + y] = scratch[(y * width) + x];
                }
            }
        }
        else
        {
            scratch.AsSpan(0, width * height).CopyTo(output);
        }
    }

    /// <summary>Stores the varblock's LF samples (DCFromLowestFrequencies) from coefficients in the coefficient layout.</summary>
    private void LowFrequencies(JxlTransformType t, float[] coeffs, int c, int bx, int by)
    {
        (int w8, int h8) = JxlTransforms8.Size(t);
        if (JxlTransforms8.IsSmall(t))
        {
            _lf[c][(by * _blocksX) + bx] = coeffs[0];
            return;
        }

        // Back to the natural layout's lowest frequencies, then the inverse of the decoder's LLF step.
        bool transposed = JxlTransforms8.Transposed(t);
        int cw = transposed ? h8 * 8 : w8 * 8;
        Span<float> llf = stackalloc float[w8 * h8];
        for (int y = 0; y < h8; y++)
        {
            for (int x = 0; x < w8; x++)
            {
                llf[(y * w8) + x] = transposed ? coeffs[(x * cw) + y] : coeffs[(y * cw) + x];
            }
        }

        Span<float> natural = stackalloc float[w8 * h8 * 64];
        natural.Clear();
        for (int y = 0; y < h8; y++)
        {
            llf.Slice(y * w8, w8).CopyTo(natural.Slice(y * w8 * 8, w8));
        }

        Span<float> lf = stackalloc float[w8 * h8];
        JxlForwardDct.LfOf(t, natural, 0, w8 * 8, lf, 0, w8);
        for (int y = 0; y < h8; y++)
        {
            lf.Slice(y * w8, w8).CopyTo(_lf[c].AsSpan(((by + y) * _blocksX) + bx, w8));
        }
    }

    /// <summary>
    /// Quantizes a channel's block (libjxl's QuantizeBlockAC): each coefficient divided by its weight and the step, rounded, and
    /// set to zero below the threshold of its quadrant. X and B thresholds go down for larger blocks.
    /// </summary>
    private void QuantizeBlock(JxlDequantMatrices matrices, int c, float qmMultiplier, JxlTransformType t, int w8, int h8, Span<float> thresholds, float[] block, int quant, int[] output)
    {
        float[] qm = matrices.GetWide(c, t);
        float qac = Scale * quant * qmMultiplier;
        if (c != 1 && w8 * h8 >= 4)
        {
            for (int i = 0; i < 4; i++)
            {
                thresholds[i] = MathF.Max(0.5f, thresholds[i] - (0.00744f * w8 * h8));
            }
        }

        // The coefficient layout is the wider side across.
        int cw = Math.Max(w8, h8) * 8;
        int ch = Math.Min(w8, h8) * 8;
        for (int y = 0; y < ch; y++)
        {
            int yfix = (y >= ch / 2 ? 1 : 0) * 2;
            int row = y * cw;
            for (int x = 0; x < cw; x++)
            {
                float threshold = thresholds[yfix + (x >= cw / 2 ? 1 : 0)];
                float val = block[row + x] * (qac / qm[row + x]);
                output[row + x] = MathF.Abs(val) >= threshold ? (int)MathF.Round(val, MidpointRounding.ToEven) : 0;
            }
        }
    }

    /// <summary>
    /// Quantizes Y (QuantizeRoundtripYBlockAC at effort 1 to 4: fixed thresholds) and replaces its coefficients with their
    /// dequantized values, biases included, as the decoder will see them.
    /// </summary>
    private void QuantizeRoundtripY(JxlDequantMatrices matrices, JxlTransformType t, int w8, int h8, ref int quant, float[][] coeffs, float[] qmMul, int[] output)
    {
        float[] block = coeffs[1];
        Span<float> thresholds = [0.56f, 0.62f, 0.62f, 0.62f];
        if (Speed >= 5)
        {
            // The quantization value goes up where blocks would turn too flat or lose isolated high frequencies (effort 5 and up).
            int original = quant;
            int maxQuant = 0;
            Span<float> thresY = stackalloc float[4];
            foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
            {
                Span<float> thres = [0.58f, 0.64f, 0.64f, 0.64f];
                int q = original;
                AdjustQuantBlock(matrices, c, qmMul[c], t, w8, h8, thres, coeffs[c], ref q);
                if (c == 1)
                {
                    thres.CopyTo(thresY);
                }

                maxQuant = Math.Max(q, maxQuant);
            }

            quant = maxQuant;
            thresY.CopyTo(thresholds);
        }

        QuantizeBlock(matrices, 1, 1f, t, w8, h8, thresholds, block, quant, output);
        float[] dm = matrices.GetWide(1, t);
        float invQac = (float)(1.0 * GlobalScaleDenom / _globalScale) / quant;
        int size = w8 * h8 * 64;
        for (int k = 0; k < size; k++)
        {
            block[k] = AdjustQuantBias(1, output[k]) * dm[k] * invQac;
        }
    }

    /// <summary>
    /// libjxl's AdjustQuantBlockAC (effort 5 and up): raises a block's quantization value when its quantized HF would vanish
    /// from a quadrant with a noticeable error, when its highest frequencies stand alone, when a DCT8 block is nearly flat and
    /// when a large varblock's error is high against its values; lowers it again in busy blocks.
    /// </summary>
    private void AdjustQuantBlock(JxlDequantMatrices matrices, int c, float qmMultiplier, JxlTransformType t, int w8, int h8, Span<float> thresholds, float[] block, ref int quant)
    {
        if (JxlTransforms8.IsSmall(t) && t != JxlTransformType.Dct8)
        {
            return;
        }

        float[] qm = matrices.GetWide(c, t);
        float qac = Scale * quant;
        int xsize = Math.Max(w8, h8);
        int ysize = Math.Min(w8, h8);
        if (xsize > 1 || ysize > 1)
        {
            for (int i = 0; i < 4; i++)
            {
                thresholds[i] = MathF.Max(0.54f, thresholds[i] - Math.Clamp(0.003f * xsize * ysize, 0f, 0.08f));
            }
        }

        float sumHighest = 0;
        float sumError = 0;
        float sumVals = 0;
        Span<float> hfNonZeros = stackalloc float[4];
        Span<float> hfMaxError = stackalloc float[4];
        hfNonZeros.Clear();
        hfMaxError.Clear();
        int cw = xsize * 8;
        for (int y = 0; y < ysize * 8; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int pos = (y * cw) + x;
                if (x < xsize && y < ysize)
                {
                    continue;
                }

                int hfix = ((y >= ysize * 8 / 2) ? 2 : 0) + ((x >= xsize * 8 / 2) ? 1 : 0);
                float val = block[pos] * (qac * qmMultiplier / qm[pos]);
                float v = MathF.Abs(val) < thresholds[hfix] ? 0 : MathF.Round(val, MidpointRounding.ToEven);
                float error = MathF.Abs(val - v);
                sumError += error;
                sumVals += MathF.Abs(v);
                if (c == 1 && v == 0 && hfMaxError[hfix] < error)
                {
                    hfMaxError[hfix] = error;
                }

                if (v != 0.0f)
                {
                    hfNonZeros[hfix] += MathF.Abs(v);
                    bool inCorner = y >= 7 * ysize && x >= 7 * xsize;
                    bool onBorder = y == (ysize * 8) - 1 || x == (xsize * 8) - 1;
                    bool inLargerCorner = x >= 4 * xsize && y >= 4 * ysize;
                    if (inCorner || (onBorder && inLargerCorner))
                    {
                        sumHighest += MathF.Abs(val);
                    }
                }
            }
        }

        if (c == 1 && sumVals * 8 < xsize * ysize)
        {
            const float Limit = 0.46f;
            const float KMul = 0.9999f;
            int orig = quant;
            int newQuant = quant;
            for (int i = 1; i < 4; i++)
            {
                if (hfNonZeros[i] == 0 && hfMaxError[i] > Limit)
                {
                    newQuant = orig + 1;
                    break;
                }
            }

            quant = newQuant;
            if (hfNonZeros[3] == 0 && hfMaxError[3] > Limit)
            {
                thresholds[3] = KMul * hfMaxError[3] * newQuant / orig;
            }
            else if ((hfNonZeros[1] == 0 && hfMaxError[1] > Limit) || (hfNonZeros[2] == 0 && hfMaxError[2] > Limit))
            {
                thresholds[1] = KMul * MathF.Max(hfMaxError[1], hfMaxError[2]) * newQuant / orig;
                thresholds[2] = thresholds[1];
            }
            else if (hfNonZeros[0] == 0 && hfMaxError[0] > Limit)
            {
                thresholds[0] = KMul * hfMaxError[0] * newQuant / orig;
            }
        }

        // High-frequency patterns with no medium frequencies around them.
        {
            float all = hfNonZeros[0] + hfNonZeros[1] + hfNonZeros[2] + hfNonZeros[3] + 1;
            ReadOnlySpan<float> mul = [70, 30, 60];
            if (mul[c] * sumHighest >= all)
            {
                quant += (int)(mul[c] * sumHighest / all);
                quant = Math.Min(quant, QuantMax - 1);
            }
        }

        if (t == JxlTransformType.Dct8 && hfNonZeros[0] + hfNonZeros[1] + hfNonZeros[2] + hfNonZeros[3] < 11)
        {
            quant = Math.Min(quant + 1, QuantMax - 1);
        }

        {
            const double QuantNormalizer = 2.2942708343284721;
            sumError = (float)(sumError * QuantNormalizer);
            sumVals = (float)(sumVals * QuantNormalizer);
            if ((int)t >= (int)JxlTransformType.Dct16)
            {
                int ix = t switch
                {
                    JxlTransformType.Dct32x16 or JxlTransformType.Dct16x32 => 1,
                    JxlTransformType.Dct16 => 0,
                    JxlTransformType.Dct32 => 2,
                    _ => 3,
                };
                double[][] mul1 =
                [
                    [0.22080615753848404, 0.45797479824262011, 0.29859235095977965],
                    [0.70109486510286834, 0.16185281305512639, 0.14387691730035473],
                    [0.114985964456218638, 0.44656840441027695, 0.10587658215149048],
                    [0.46849665264409396, 0.41239077937781954, 0.088667407767185444],
                ];
                double[][] mul2 =
                [
                    [0.27450281941822197, 1.1255766549984996, 0.98950459134128388],
                    [0.4652168675598285, 0.40945807983455818, 0.36581899811751367],
                    [0.28034972424715715, 0.9182653201929738, 1.5581531543057416],
                    [0.26873118114033728, 0.68863712390392484, 1.2082185408666786],
                ];
                double limit = (mul1[ix][c] * xsize * ysize * 64) + (mul2[ix][c] * sumVals);
                int step = (int)(sumError / limit);
                step = Math.Clamp(step, 0, 2);
                if (sumError > limit)
                {
                    quant = Math.Min(quant + step, QuantMax - 1);
                }
            }
        }

        {
            // Lower the value in highly active areas.
            int div = xsize * ysize;
            float minNonZeros = MathF.Min(MathF.Min(hfNonZeros[0], hfNonZeros[1]), MathF.Min(hfNonZeros[2], hfNonZeros[3]));
            int activity = 15;
            if (minNonZeros < 15.0f * div)
            {
                activity = ((int)minNonZeros + (div / 2)) / div;
            }

            int qp = quant - activity;
            if (c == 1)
            {
                for (int i = 1; i < 4; i++)
                {
                    thresholds[i] += 0.01f * activity;
                }
            }

            quant = Math.Max(qp, Math.Max(4, quant / 2));
        }
    }

    /// <summary>libjxl's AdjustQuantBias: ±1 pulled towards zero by the channel's bias, larger values by the numerator over the value.</summary>
    private static float AdjustQuantBias(int c, int q)
    {
        if (q == 0)
        {
            return 0;
        }

        if (q is 1 or -1)
        {
            return q * QuantBias[c];
        }

        return q - (QuantBias[3] / q);
    }

    /// <summary>
    /// Quantizes the LF image (AddVarDCTDC): each channel at its step, X and B after taking out the LF chroma from luma of the
    /// quantized Y. Up to effort 3 values are rounded; from effort 4 they get an extra bit of precision and are quantized
    /// against the weighted predictor (QuantizeWP): a residual inside the dead zone becomes zero and one beyond ±2 even.
    /// </summary>
    private void QuantizeLf()
    {
        bool nonlinear = Speed >= 4;
        _lfPrecision = nonlinear ? 1 : 0;
        float mul = 1 << _lfPrecision;
        float yStep = DcStep(1) / mul;
        float[] dcFactors = [0f, 0f, 1f];
        if (!nonlinear)
        {
            Parallel.For(0, _blocksY, _parallel, y =>
            {
                int row = y * _blocksX;
                float invY = InvDcStep(1);
                for (int x = 0; x < _blocksX; x++)
                {
                    _lfQuant[1][row + x] = (int)MathF.Round(_lf[1][row + x] * invY, MidpointRounding.AwayFromZero);
                }

                foreach (int c in (ReadOnlySpan<int>)[0, 2])
                {
                    float inv = InvDcStep(c);
                    for (int x = 0; x < _blocksX; x++)
                    {
                        float v = _lf[c][row + x] - (_lfQuant[1][row + x] * (yStep * dcFactors[c]));
                        _lfQuant[c][row + x] = (int)MathF.Round(v * inv, MidpointRounding.AwayFromZero);
                    }
                }
            });
            return;
        }

        Parallel.For(0, _lfGroups, _parallel, g =>
        {
            (int x0, int y0, int w, int h) = LfGroupRect(g);
            int[] yq = new int[w * h];
            foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
            {
                float inv = InvDcStep(c) * mul;
                int[] q = new int[w * h];
                JxlWeightedPredictor wp = new(w, new JxlWpParams());
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int at = (y * w) + x;
                        float value = _lf[c][((y0 + y) * _blocksX) + x0 + x];
                        if (c != 1)
                        {
                            value -= yq[at] * (yStep * dcFactors[c]);
                        }

                        int west = x > 0 ? q[at - 1] : y > 0 ? q[at - w] : 0;
                        int n = y > 0 ? q[at - w] : west;
                        int nw = x > 0 && y > 0 ? q[at - w - 1] : west;
                        int ne = x + 1 < w && y > 0 ? q[at - w + 1] : n;
                        int nn = y > 1 ? q[at - (2 * w)] : n;
                        int guess = (int)((wp.Predict(n, nw, ne, west, nn) + 3) >> 3);
                        float svalue = (value * inv) - guess;
                        if (svalue > -0.62f && svalue < 0.62f)
                        {
                            svalue = 0;
                        }

                        int residual = (int)MathF.Round(svalue, MidpointRounding.AwayFromZero);
                        if (residual is > 2 or < -2)
                        {
                            residual = (int)MathF.Round(svalue * 0.5f, MidpointRounding.AwayFromZero) * 2;
                        }

                        q[at] = residual + guess;
                        wp.Record(q[at]);
                    }
                }

                if (c == 1)
                {
                    q.CopyTo(yq, 0);
                }

                for (int y = 0; y < h; y++)
                {
                    q.AsSpan(y * w, w).CopyTo(_lfQuant[c].AsSpan(((y0 + y) * _blocksX) + x0, w));
                }
            }
        });
    }

    /// <summary>
    /// Clusters the block contexts by the quantization field and transform from effort 4 (FindBestBlockEntropyModel): on large
    /// images the field is cut into segments of equal counts, and the least used (segment, order) pairs are merged until a few
    /// clusters remain, chroma sharing at most a handful.
    /// </summary>
    private void FindBlockEntropyModel()
    {
        if (Speed < 4)
        {
            return;
        }

        long tot = (long)_blocksX * _blocksY;
        long sizeForCtxModel = (long)((1 << 10) * _settings.Distance);
        if (tot < sizeForCtxModel)
        {
            return;
        }

        long[] qfCounts = new long[256];
        long[][] qfOrdCounts = new long[NumOrders][];
        for (int i = 0; i < NumOrders; i++)
        {
            qfOrdCounts[i] = new long[256];
        }

        for (int y = 0; y < _blocksY; y++)
        {
            for (int x = 0; x < _blocksX; x++)
            {
                int at = (y * _blocksX) + x;
                int t = _strategy[at] >= 0 ? _strategy[at] : FirstStrategy(x, y);
                int ord = JxlTransforms8.OrderId((JxlTransformType)t);
                int qf = _quantField[at] - 1;
                qfCounts[qf]++;
                qfOrdCounts[ord][qf]++;
            }
        }

        long sizeForQfSplit = (long)((1 << 13) * _settings.Distance);
        int segments = tot < sizeForQfSplit ? 1 : 2;
        List<int> qft = [];
        long cumsum = 0;
        int next = 1;
        int lastCut = 256;
        long cut = tot * next / segments;
        for (int j = 0; j < 256; j++)
        {
            cumsum += qfCounts[j];
            if (cumsum > cut)
            {
                if (j != 0)
                {
                    qft.Add(j);
                }

                lastCut = j;
                while (cumsum > cut)
                {
                    next++;
                    cut = tot * next / segments;
                }
            }
            else if (next > qft.Count + 1)
            {
                if (j - 1 == lastCut && j != 0)
                {
                    qft.Add(j);
                }
            }
        }

        int segs = qft.Count + 1;
        long[] counts = new long[NumOrders * segs];
        int pos = 0;
        for (int j = 0; j < 256; j++)
        {
            if (pos < qft.Count && j == qft[pos])
            {
                pos++;
            }

            for (int i = 0; i < NumOrders; i++)
            {
                counts[pos + (i * segs)] += qfOrdCounts[i][j];
            }
        }

        // Repeatedly merge the least used pair (libjxl sorts with std::sort, which leaves ties of unused pairs in no set order;
        // a stable sort is used here).
        int n = segs * NumOrders;
        int[] remap = [.. Enumerable.Range(0, n)];
        List<int> clusters = [.. remap];
        int nbClusters = (int)Math.Clamp(tot / sizeForCtxModel / 2, 2, 9);
        int nbChroma = (int)Math.Clamp(tot / sizeForCtxModel / 3, 1, 5);
        while (clusters.Count > nbClusters)
        {
            clusters = [.. clusters.OrderByDescending(a => counts[a])];
            counts[clusters[^2]] += counts[clusters[^1]];
            counts[clusters[^1]] = 0;
            remap[clusters[^1]] = clusters[^2];
            clusters.RemoveAt(clusters.Count - 1);
        }

        for (int i = 0; i < n; i++)
        {
            while (remap[remap[i]] != remap[i])
            {
                remap[i] = remap[remap[i]];
            }
        }

        int[] relabel = new int[n];
        Array.Fill(relabel, n);
        int num = 0;
        for (int i = 0; i < n; i++)
        {
            if (relabel[remap[i]] == n)
            {
                relabel[remap[i]] = num++;
            }

            remap[i] = relabel[remap[i]];
        }

        byte[] map = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            map[i] = (byte)remap[i];
        }

        for (int i = n; i < n * 3; i++)
        {
            map[i] = (byte)(num + Math.Clamp(remap[i % n], 0, nbChroma - 1));
        }

        _blockContexts = map;
        _blockClusters = map.Max() + 1;
        _qfThresholds = [.. qft];
    }

    /// <summary>Gets the transform of the varblock a block belongs to when it is not the varblock's first block.</summary>
    private int FirstStrategy(int x, int y)
    {
        for (int yy = y; yy >= 0; yy--)
        {
            for (int xx = x; xx >= 0; xx--)
            {
                int t = _strategy[(yy * _blocksX) + xx];
                if (t >= 0)
                {
                    (int w8, int h8) = JxlTransforms8.Size((JxlTransformType)t);
                    if (xx + w8 > x && yy + h8 > y)
                    {
                        return t;
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Chooses the coefficient orders (ComputeCoeffOrder): positions sorted by how often they are zero, counted in a sample of
    /// the varblocks, ties kept in the natural order; an order equal to the natural one is not sent.
    /// </summary>
    private void ComputeOrders()
    {
        // Only DCT8 is used from effort 1 to 3, which libjxl samples at half the blocks; it reads the sampled blocks' coefficients
        // as if the skipped ones were not there.
        long[][] numZeros = [new long[64], new long[64], new long[64]];
        ulong s0 = 0x94D049BB133111EB, s1 = 0xBF58476D1CE4E5B9;
        const ulong Threshold = 2147483647;
        for (int g = 0; g < _groups; g++)
        {
            (_, _, int gw, int gh) = GroupRect(g);
            int offset = 0;
            for (int i = 0; i < gw * gh; i++)
            {
                ulong a = s0;
                ulong b = s1;
                ulong bits = a + b;
                s0 = b;
                a ^= a << 23;
                a ^= b ^ (a >> 18) ^ (b >> 5);
                s1 = a;
                if ((bits >> 32) > Threshold)
                {
                    continue;
                }

                for (int c = 0; c < 3; c++)
                {
                    int[] p = _coeffs[g][c];
                    for (int k = 0; k < 64; k++)
                    {
                        if (p[offset + k] == 0)
                        {
                            numZeros[c][k]++;
                        }
                    }

                    numZeros[c][0] = -1;
                }

                offset += 64;
            }
        }

        int[] natural = JxlHfPass.Natural(0);
        bool nondefault = false;
        for (int c = 0; c < 3; c++)
        {
            (ulong Key, int Pos)[] items = new (ulong, int)[64];
            for (int i = 0; i < 64; i++)
            {
                int pos = natural[i];
                float count = (numZeros[c][pos] * 0.125f) + 0.1f;
                ulong q = count <= 0 ? 0 : (ulong)count;
                items[i] = ((q << 16) | (uint)i, pos);
            }

            Array.Sort(items, (x, y) => x.Key.CompareTo(y.Key));
            int[] order = new int[64];
            for (int i = 0; i < 64; i++)
            {
                order[i] = items[i].Pos;
                nondefault |= order[i] != natural[i];
            }

            _orders[c] = order;
        }

        _usedOrders = nondefault ? 1 : 0;
        if (!nondefault)
        {
            _orders[0] = _orders[1] = _orders[2] = natural;
        }
    }

    /// <summary>Gets the order of a channel for an order id: the chosen one for DCT8 when sent, else the natural order.</summary>
    private int[] Order(int orderId, int c) => orderId == 0 && _usedOrders != 0 ? _orders[c] : JxlHfPass.Natural(orderId);

    /// <summary>Tokenizes a pass group's coefficients (TokenizeCoefficients), Y, X then B for each varblock.</summary>
    private List<JxlToken> TokenizeGroup(int g)
    {
        (int x0, int y0, int w, int h) = GroupRect(g);
        List<JxlToken> tokens = new(w * h * 3 * 16);
        int[][] rows = [new int[w], new int[w], new int[w]];
        int[] offsets = new int[3];
        ReadOnlySpan<int> channels = [1, 0, 2];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int at = ((y0 + y) * _blocksX) + x0 + x;
                if (_strategy[at] < 0)
                {
                    continue;
                }

                JxlTransformType t = (JxlTransformType)_strategy[at];
                (int w8, int h8) = JxlTransforms8.Size(t);
                int covered = w8 * h8;
                int logCovered = System.Numerics.BitOperations.Log2((uint)covered);
                int size = covered * 64;
                int orderId = JxlTransforms8.OrderId(t);
                int qfIndex = 0;
                foreach (int threshold in _qfThresholds)
                {
                    if (_quantField[at] > threshold)
                    {
                        qfIndex++;
                    }
                }

                for (int k = 0; k < 3; k++)
                {
                    int c = channels[k];
                    ReadOnlySpan<int> block = _coeffs[g][c].AsSpan(offsets[c], size);
                    offsets[c] += size;
                    int[] order = Order(orderId, c);
                    int nz = 0;
                    for (int i = covered; i < size; i++)
                    {
                        nz += block[order[i]] != 0 ? 1 : 0;
                    }

                    int[] row = rows[c];
                    int predicted = y == 0 ? (x == 0 ? 32 : row[x - 1]) : x == 0 ? row[x] : (row[x] + row[x - 1] + 1) >> 1;
                    int shifted = (nz + covered - 1) >> logCovered;
                    for (int dx = 0; dx < w8 && x + dx < w; dx++)
                    {
                        row[x + dx] = shifted;
                    }

                    int blockCtx = _blockContexts[(((k * NumOrders) + orderId) * (_qfThresholds.Length + 1)) + qfIndex];
                    int nzIndex = predicted >= 8 ? 4 + (Math.Min(predicted, 64) / 2) : predicted;
                    tokens.Add(new JxlToken(blockCtx + (nzIndex * _blockClusters), (uint)nz));
                    int baseCtx = (blockCtx * ZeroDensityContexts) + (NonZeroBuckets * _blockClusters);
                    int prev = nz > size / 16 ? 0 : 1;
                    for (int i = covered; i < size && nz != 0; i++)
                    {
                        int coeff = block[order[i]];
                        int ctx = baseCtx + ((CoeffNumNonzeroContext[(nz - 1) >> logCovered] + CoeffFreqContext[(i - covered) >> logCovered]) * 2) + prev;
                        tokens.Add(new JxlToken(ctx, JxlTreeLearner.PackSigned(coeff)));
                        prev = coeff != 0 ? 1 : 0;
                        nz -= prev;
                    }
                }
            }
        }

        return tokens;
    }

    /// <summary>Writes the frame header (C.2): a last regular VarDCT frame in XYB with its quantization matrix scales and restoration filters.</summary>
    private void WriteFrameHeader(JxlBitWriter w)
    {
        w.WriteBool(false);
        w.Write(0, 2); // regular frame
        w.Write(0, 1); // VarDCT
        w.WriteU64(0); // no flags
        w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 1); // upsampling
        w.Write((ulong)_xQmScale, 3);
        w.Write((ulong)_bQmScale, 3);
        w.WriteU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 3), 1); // one pass
        w.WriteBool(false); // no crop
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 2), 0); // replace
        w.WriteBool(true); // last frame
        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0); // no name
        // The restoration filters: all default is Gaborish with its default weights and one EPF iteration.
        bool defaults = _gaborish && _epfIterations == 1;
        w.WriteBool(defaults);
        if (!defaults)
        {
            w.WriteBool(_gaborish);
            if (_gaborish)
            {
                w.WriteBool(false); // default Gaborish weights
            }

            w.Write((ulong)_epfIterations, 2);
            if (_epfIterations > 0)
            {
                w.WriteBool(false); // default sharpness LUT
                w.WriteBool(false); // default channel scales
                w.WriteBool(false); // default sigma
            }

            w.WriteNoExtensions();
        }

        w.WriteNoExtensions();
    }

    /// <summary>Encodes the sections in TOC order: LF global, the LF groups, HF global and the pass groups (all in one for a single group).</summary>
    private List<byte[]> Sections()
    {
        int effort = Speed;

        // The Modular streams: each LF group's LF image and HF metadata, under one tree joined on the stream id.
        int dcStream0 = 1;
        int acmStream0 = 1 + (2 * _lfGroups);
        List<(int Id, List<JxlChannel> Channels)> streams = [];
        long dcPixels = 0;
        long acmPixels = 0;
        int[] metaCounts = new int[_lfGroups];
        for (int g = 0; g < _lfGroups; g++)
        {
            List<JxlChannel> dc = LfChannels(g);
            dcPixels += dc.Sum(ch => (long)ch.Width * ch.Height);
            streams.Add((dcStream0 + g, dc));
        }

        for (int g = 0; g < _lfGroups; g++)
        {
            List<JxlChannel> meta = MetaChannels(g);
            metaCounts[g] = meta[2].Width;
            acmPixels += meta.Sum(ch => (long)ch.Width * ch.Height);
            streams.Add((acmStream0 + g, meta));
        }

        // libjxl's trees: the fixed WP tree for the LF image (kWPFixedDC), and for the HF metadata the left predictor up to
        // effort 3 (kFalconACMeta) and its fixed tree from effort 4 (kACMeta); merged on the stream id (MergeTrees).
        List<JxlTreeNode> dcTree = JxlModularEncoder.FixedTree(15, 6, Math.Max(dcPixels, 1), 8);
        List<JxlTreeNode> acmTree = effort >= 4 && acmPixels >= 1024 ? AcMetaTree() : [JxlTreeNode.Leaf(1)];
        List<JxlTreeNode> tree = [new JxlTreeNode { Property = 1, SplitValue = acmStream0 - 1, Left = 1, Right = 1 + acmTree.Count }];
        foreach (List<JxlTreeNode> sub in (List<JxlTreeNode>[])[acmTree, dcTree])
        {
            int root = tree.Count;
            foreach (JxlTreeNode n in sub)
            {
                tree.Add(n.Property < 0 ? n : n with { Left = n.Left + root, Right = n.Right + root });
            }
        }

        List<List<JxlToken>> treeTokens = [[]];
        (int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] context) = JxlModularEncoder.TokenizeTree(tree, treeTokens[0]);
        bool usesWp = property.Any(p => p == 15) || property.Select((p, i) => p < 0 && predictor[i] == 6).Any(b => b);
        List<JxlToken>[] streamTokens = new List<JxlToken>[streams.Count];
        Parallel.For(0, streams.Count, _parallel, s =>
        {
            List<JxlToken> t = [];
            (int id, List<JxlChannel> chs) = streams[s];
            for (int c = 0; c < chs.Count; c++)
            {
                if (chs[c].Width > 0 && chs[c].Height > 0)
                {
                    t.AddRange(JxlModularEncoder.Tokenize(chs[c], c, id, property, value, left, right, predictor, context, usesWp));
                }
            }

            streamTokens[s] = t;
        });

        List<List<JxlToken>> modularTokens = [.. streamTokens];
        int[] widths = [.. streams.Select(s => s.Channels.Max(ch => ch.Width))];

        // The HF tokens of every pass group.
        List<JxlToken>[] acTokens = new List<JxlToken>[_groups];
        Parallel.For(0, _groups, _parallel, g => acTokens[g] = TokenizeGroup(g));

        // LF global.
        JxlBitWriter global = new();
        global.WriteBool(true); // default LF dequantization
        global.WriteU32(new JxlU32(1, 11, 2049, 11, 4097, 12, 8193, 16), (uint)_globalScale);
        global.WriteU32(new JxlU32(16, 0, 1, 5, 1, 8, 1, 16), (uint)_quantDc);
        WriteBlockContexts(global);
        global.WriteBool(true); // default chroma from luma
        global.WriteBool(true); // global tree
        // HistogramParams::ForModular for VarDCT data: fast clustering, approximate ANS histograms, and a hybrid integer search
        // when the LF image has extra precision.
        JxlHistogramParams mp = new()
        {
            Clustering = JxlHistogramParams.ClusteringKind.Fast,
            AnsStrategy = JxlHistogramParams.AnsKind.Approximate,
            UintMethod = _lfPrecision != 0 ? JxlHistogramParams.UintKind.Fast : JxlHistogramParams.UintKind.None,
        };
        JxlEntropyCode treeCode = JxlEntropyCode.Write(global, mp, 6, treeTokens);
        treeCode.WriteTokens(global, treeTokens[0]);
        JxlEntropyCode code = JxlEntropyCode.Write(global, mp with { ImageWidths = widths }, context.Max() + 1, modularTokens);

        // LF groups.
        JxlBitWriter[] lfGroups = new JxlBitWriter[_lfGroups];
        Parallel.For(0, _lfGroups, _parallel, g =>
        {
            JxlBitWriter w = new();
            w.Write((ulong)_lfPrecision, 2);
            WriteStream(w, code, modularTokens[g]);
            (_, _, int bw, int bh) = LfGroupRect(g);
            w.Write((ulong)(metaCounts[g] - 1), CeilLog2((long)bw * bh));
            WriteStream(w, code, modularTokens[_lfGroups + g]);
            lfGroups[g] = w;
        });

        // HF global: the default matrices, one HF preset, the orders and the HF distributions.
        JxlBitWriter hf = new();
        hf.WriteBool(true);
        hf.Write(0, CeilLog2(_groups));
        hf.WriteU32(new JxlU32(0x5F, 0, 0x13, 0, 0, 0, 0, 13), (uint)_usedOrders);
        if (_usedOrders != 0)
        {
            WriteOrders(hf);
        }

        // HistogramParams for the HF tokens: at most four clusters above effort 3 (libjxl's kFastest), then fast clustering.
        JxlHistogramParams acp = new()
        {
            Clustering = JxlHistogramParams.ClusteringKind.Fast,
            UintMethod = JxlHistogramParams.UintKind.None,
            AnsStrategy = JxlHistogramParams.AnsKind.Approximate,
            MaxHistograms = effort <= 2 ? 4 : int.MaxValue,
        };
        List<List<JxlToken>> ac = [.. acTokens];
        JxlEntropyCode acCode = JxlEntropyCode.Write(hf, acp, (NonZeroBuckets + ZeroDensityContexts) * _blockClusters, ac);
        JxlBitWriter[] passGroups = new JxlBitWriter[_groups];
        Parallel.For(0, _groups, _parallel, g =>
        {
            JxlBitWriter w = new();
            acCode.WriteTokens(w, ac[g]);
            passGroups[g] = w;
        });

        if (_groups == 1)
        {
            JxlBitWriter all = global;
            all.AppendBits(lfGroups[0]);
            all.AppendBits(hf);
            all.AppendBits(passGroups[0]);
            all.ZeroPadToByte();
            return [all.ToArray()];
        }

        List<byte[]> sections = [Padded(global)];
        sections.AddRange(lfGroups.Select(Padded));
        sections.Add(Padded(hf));
        sections.AddRange(passGroups.Select(Padded));
        return sections;
    }

    private static byte[] Padded(JxlBitWriter w)
    {
        w.ZeroPadToByte();
        return w.ToArray();
    }

    /// <summary>Writes a Modular stream that uses the global tree: its header (default WP, no transforms) and tokens.</summary>
    private static void WriteStream(JxlBitWriter w, JxlEntropyCode code, List<JxlToken> tokens)
    {
        w.WriteBool(true);
        w.WriteBool(true);
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 8), 0);
        code.WriteTokens(w, tokens);
    }

    /// <summary>Gets an LF group's quantized LF channels: Y, X and B.</summary>
    private List<JxlChannel> LfChannels(int g)
    {
        (int x0, int y0, int w, int h) = LfGroupRect(g);
        List<JxlChannel> channels = [];
        foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
        {
            JxlChannel ch = new(w, h, 0, 0);
            for (int y = 0; y < h; y++)
            {
                _lfQuant[c].AsSpan(((y0 + y) * _blocksX) + x0, w).CopyTo(ch.Data.AsSpan(y * w, w));
            }

            channels.Add(ch);
        }

        return channels;
    }

    /// <summary>Gets an LF group's HF metadata (AddACMetadata): the chroma-from-luma factors, each varblock's transform and quantization value, and the EPF sharpness.</summary>
    private List<JxlChannel> MetaChannels(int g)
    {
        (int x0, int y0, int w, int h) = LfGroupRect(g);
        int tx0 = x0 / TileBlocks, ty0 = y0 / TileBlocks;
        int tw = Ceil(w, TileBlocks), th = Ceil(h, TileBlocks);
        JxlChannel xfy = new(tw, th, 0, 0);
        JxlChannel bfy = new(tw, th, 0, 0);
        for (int y = 0; y < th; y++)
        {
            for (int x = 0; x < tw; x++)
            {
                xfy.Data[(y * tw) + x] = _yToX[((ty0 + y) * _tilesX) + tx0 + x];
                bfy.Data[(y * tw) + x] = _yToB[((ty0 + y) * _tilesX) + tx0 + x];
            }
        }

        List<int> acs = [];
        List<int> qf = [];
        JxlChannel epf = new(w, h, 0, 0);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int at = ((y0 + y) * _blocksX) + x0 + x;
                epf.Data[(y * w) + x] = _epfSharpness[at];
                if (_strategy[at] < 0)
                {
                    continue;
                }

                acs.Add(_strategy[at]);
                qf.Add(_quantField[at] - 1);
            }
        }

        JxlChannel blocks = new(acs.Count, 2, 0, 0);
        acs.CopyTo(blocks.Data, 0);
        qf.CopyTo(blocks.Data, acs.Count);
        return [xfy, bfy, blocks, epf];
    }

    /// <summary>Writes the block context map (EncodeBlockCtxMap): the default bit, or the thresholds and the clustered map.</summary>
    private void WriteBlockContexts(JxlBitWriter w)
    {
        if (ReferenceEquals(_blockContexts, DefaultBlockContexts))
        {
            w.WriteBool(true);
            return;
        }

        w.WriteBool(false);
        for (int c = 0; c < 3; c++)
        {
            w.Write(0, 4); // no LF thresholds
        }

        w.Write((ulong)_qfThresholds.Length, 4);
        foreach (int t in _qfThresholds)
        {
            w.WriteU32(new JxlU32(0, 2, 4, 3, 12, 5, 44, 8), (uint)(t - 1));
        }

        JxlEntropyCode.WriteContextMap(w, _blockContexts, _blockClusters);
    }

    /// <summary>libjxl's kACMeta tree for the HF metadata: gradient for the chroma-from-luma factors, splits on the left value for the rest.</summary>
    private static List<JxlTreeNode> AcMetaTree()
    {
        return
        [
            JxlTreeNode.Split(0, 1, 1),
            JxlTreeNode.Split(0, 2, 3),
            JxlTreeNode.Split(0, 0, 5),
            JxlTreeNode.Split(6, 3, 21),
            JxlTreeNode.Split(2, 0, 7),
            JxlTreeNode.Leaf(5),
            JxlTreeNode.Leaf(5),
            JxlTreeNode.Split(7, 5, 9),
            JxlTreeNode.Split(7, 5, 15),
            JxlTreeNode.Split(7, 11, 11),
            JxlTreeNode.Split(7, 3, 13),
            JxlTreeNode.Leaf(1),
            JxlTreeNode.Leaf(1),
            JxlTreeNode.Leaf(1),
            JxlTreeNode.Leaf(1),
            JxlTreeNode.Split(7, 11, 17),
            JxlTreeNode.Split(7, 3, 19),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Split(7, 3, 23),
            JxlTreeNode.Split(7, 3, 25),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Leaf(0),
            JxlTreeNode.Leaf(0),
        ];
    }

    /// <summary>Writes the coefficient orders as Lehmer codes against the natural order (EncodeCoeffOrders).</summary>
    private void WriteOrders(JxlBitWriter w)
    {
        int[] natural = JxlHfPass.Natural(0);
        int[] lut = new int[64];
        for (int i = 0; i < 64; i++)
        {
            lut[natural[i]] = i;
        }

        List<JxlToken> tokens = [];
        for (int c = 0; c < 3; c++)
        {
            int[] perm = new int[64];
            for (int i = 0; i < 64; i++)
            {
                perm[i] = lut[_orders[c][i]];
            }

            int[] lehmer = new int[64];
            bool[] taken = new bool[64];
            for (int i = 0; i < 64; i++)
            {
                int smaller = 0;
                for (int v = 0; v < perm[i]; v++)
                {
                    smaller += taken[v] ? 0 : 1;
                }

                lehmer[i] = smaller;
                taken[perm[i]] = true;
            }

            int end = 64;
            while (end > 1 && lehmer[end - 1] == 0)
            {
                end--;
            }

            tokens.Add(new JxlToken(OrderContext(64), (uint)(end - 1)));
            uint last = 0;
            for (int i = 1; i < end; i++)
            {
                tokens.Add(new JxlToken(OrderContext(last), (uint)lehmer[i]));
                last = (uint)lehmer[i];
            }
        }

        List<List<JxlToken>> streams = [tokens];
        JxlHistogramParams p = new() { Rle = true };
        JxlEntropyCode code = JxlEntropyCode.Write(w, p, 8, streams);
        code.WriteTokens(w, streams[0]);
    }

    private static int OrderContext(uint x) => Math.Min(JxlEntropyDecoder.CeilLog2Plus1(x), 7);
}
