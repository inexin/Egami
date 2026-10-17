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
        e.ComputeCoefficients();
        e.QuantizeLf();
        e.ComputeOrders();
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
        float q = 0.79f / d;
        ComputeGlobalScaleAndQuant(quantDc, q, 0);
        float invGlobalScale = (float)(1.0 * GlobalScaleDenom / _globalScale);
        int raw = ClampQuant((q * invGlobalScale) + 0.5f);
        Array.Fill(_quantField, raw);
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
                    QuantizeRoundtripY(matrices, t, w8, h8, quant, coeffs[1], quantized[1]);
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
    private void QuantizeRoundtripY(JxlDequantMatrices matrices, JxlTransformType t, int w8, int h8, int quant, float[] block, int[] output)
    {
        Span<float> thresholds = [0.56f, 0.62f, 0.62f, 0.62f];
        QuantizeBlock(matrices, 1, 1f, t, w8, h8, thresholds, block, quant, output);
        float[] dm = matrices.GetWide(1, t);
        float invQac = (float)(1.0 * GlobalScaleDenom / _globalScale) / quant;
        int size = w8 * h8 * 64;
        for (int k = 0; k < size; k++)
        {
            block[k] = AdjustQuantBias(1, output[k]) * dm[k] * invQac;
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
    /// Quantizes the LF image (AddVarDCTDC at effort 1 to 3): each channel rounded at its step, X and B after taking out the
    /// LF chroma from luma of the quantized Y.
    /// </summary>
    private void QuantizeLf()
    {
        float yStep = DcStep(1);
        float[] dcFactors = [0f, 0f, 1f];
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

                    int blockCtx = DefaultBlockContexts[(k * NumOrders) + orderId];
                    int nzIndex = predicted >= 8 ? 4 + (Math.Min(predicted, 64) / 2) : predicted;
                    tokens.Add(new JxlToken(blockCtx + (nzIndex * DefaultBlockClusters), (uint)nz));
                    int baseCtx = (blockCtx * ZeroDensityContexts) + (NonZeroBuckets * DefaultBlockClusters);
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
        w.WriteBool(false); // restoration filters given (the default has Gaborish)
        w.WriteBool(false); // no Gaborish
        w.Write((ulong)_epfIterations, 2);
        if (_epfIterations > 0)
        {
            w.WriteBool(false); // default sharpness LUT
            w.WriteBool(false); // default channel scales
            w.WriteBool(false); // default sigma
        }

        w.WriteNoExtensions();
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

        // libjxl's trees: the fixed WP tree for the LF image (kWPFixedDC) and the left predictor for the HF metadata
        // (kFalconACMeta), merged on the stream id.
        List<JxlTreeNode> dcTree = JxlModularEncoder.FixedTree(15, 6, Math.Max(dcPixels, 1), 8);
        List<JxlTreeNode> tree = [new JxlTreeNode { Property = 1, SplitValue = acmStream0 - 1, Left = 1, Right = 2 }, JxlTreeNode.Leaf(1)];
        int dcRoot = tree.Count;
        foreach (JxlTreeNode n in dcTree)
        {
            tree.Add(n.Property < 0 ? n : n with { Left = n.Left + dcRoot, Right = n.Right + dcRoot });
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
        global.WriteBool(true); // default block context map
        global.WriteBool(true); // default chroma from luma
        global.WriteBool(true); // global tree
        // HistogramParams::ForModular for VarDCT data: fast clustering, the fastest ANS strategy above effort 2.
        JxlHistogramParams mp = new()
        {
            Clustering = JxlHistogramParams.ClusteringKind.Fast,
            AnsStrategy = effort <= 1 ? JxlHistogramParams.AnsKind.Fast : JxlHistogramParams.AnsKind.Approximate,
            UintMethod = JxlHistogramParams.UintKind.None,
        };
        JxlEntropyCode treeCode = JxlEntropyCode.Write(global, mp, 6, treeTokens);
        treeCode.WriteTokens(global, treeTokens[0]);
        JxlEntropyCode code = JxlEntropyCode.Write(global, mp with { ImageWidths = widths }, context.Max() + 1, modularTokens);

        // LF groups.
        JxlBitWriter[] lfGroups = new JxlBitWriter[_lfGroups];
        Parallel.For(0, _lfGroups, _parallel, g =>
        {
            JxlBitWriter w = new();
            w.Write(0, 2); // no extra LF precision
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
        JxlEntropyCode acCode = JxlEntropyCode.Write(hf, acp, (NonZeroBuckets + ZeroDensityContexts) * DefaultBlockClusters, ac);
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
