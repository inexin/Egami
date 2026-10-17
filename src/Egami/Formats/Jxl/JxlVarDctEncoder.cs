namespace Egami.Formats.Jxl;

/// <summary>What the lossy encoder is asked for: the distance, the effort and the threads it may use.</summary>
internal sealed record JxlLossySettings
{
    /// <summary>Gets the Butteraugli-style distance, 1.0 visually lossless.</summary>
    internal float Distance { get; init; } = 1;

    /// <summary>Gets the effort, 1 to 7, on libjxl's scale (its speed tiers Lightning to Squirrel).</summary>
    internal int Effort { get; init; } = 5;

    internal int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Gets the ISO of the photon noise the decoder adds, or 0 for none.</summary>
    internal float PhotonNoiseIso { get; init; }

    /// <summary>Gets whether the frame is progressive as cjxl -p makes it: an LF frame and the HF in three passes.</summary>
    internal bool Progressive { get; init; }

    /// <summary>Gets the distance alpha is coded at: 0 lossless, more for lossy Modular.</summary>
    internal float AlphaDistance { get; init; }

    /// <summary>Gets whether the color of fully transparent pixels may be replaced by that of their neighbours (libjxl's SimplifyInvisible).</summary>
    internal bool SimplifyInvisible { get; init; }
}

/// <summary>
/// Writes a lossy JPEG XL codestream (Phase 2.9.7): one VarDCT frame in XYB, as libjxl 0.12.0's encoder makes it at the same
/// distance and effort (enc_frame, enc_heuristics, enc_group, enc_cache, enc_modular's VarDCT streams, enc_coeff_order and
/// enc_entropy_coder; see THIRD-PARTY-NOTICES). The raster's linear sRGB goes to XYB (<see cref="JxlXyb"/>), each varblock through
/// its forward transform (<see cref="JxlForwardDct"/>), its coefficients quantized by the quantization field and the default
/// weights, the LF image and the HF metadata through Modular streams, and the HF coefficients as tokens.
/// </summary>
internal sealed partial class JxlVarDctEncoder
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

    // Per block: the transform of the varblock it belongs to and whether it is that varblock's first (top left) block, the raw
    // quantization field and the EPF sharpness. Per 64×64 tile: the chroma-from-luma factors.
    private readonly sbyte[] _strategy;
    private readonly bool[] _isFirst;
    private readonly int[] _quantField;
    private readonly byte[] _epfSharpness;
    private readonly sbyte[] _yToX;
    private readonly sbyte[] _yToB;

    // The LF image (one value per block, per channel, X, Y and B) and its quantized values.
    private readonly float[][] _lf = new float[3][];
    private readonly int[][] _lfQuant = new int[3][];

    // Each pass group's quantized coefficients, per channel, varblock after varblock in the coefficient layout.
    private readonly int[][][] _coeffs;
    // Per pass: the coefficients (the whole set when there is one pass), the coefficient orders and which are sent.
    private int[][][][] _passCoeffs = [];
    private int[][][] _passOrders = [new int[NumOrders * 3][]];
    private int[] _passUsed = [0];
    private int _passes = 1;

    // With Progressive: the LF frame (frame header, TOC and sections) that carries the LF image.
    private byte[]? _lfFrame;

    // The block context map (HfBlockContext): the default one, or from effort 4 one clustered by the quantization field.
    private byte[] _blockContexts = DefaultBlockContexts;
    private int _blockClusters = DefaultBlockClusters;
    private int[] _qfThresholds = [];

    // The LF image's extra precision bits (1 from effort 4, where LF values are quantized against the WP predictor).
    private int _lfPrecision;

    // From effort 5: the adaptive quantization's masks, and whether Gaborish is on.
    private JxlAdaptiveQuantization? _aq;
    private bool _gaborish;

    // The XYB planes before patches and Gaborish, cropped to the image, kept for the EPF search at efforts 6 and 7.
    private float[][]? _original;

    // At effort 7, the patch dictionary: the patches, the reference-only Modular frame that holds them (its frame header, TOC
    // and sections) and that frame as a decoder reconstructs it, row stride _patchWidth.
    private JxlPatch[]? _patches;
    private byte[]? _patchFrame;
    private float[][]? _patchReference;
    private int _patchWidth;

    // The frame's upsampling (2 at libjxl's low-bitrate point) and the distance asked for, which sets the X matrix scale.
    private readonly int _upsampling;
    private readonly float _originalDistance;
    private readonly (float Dx, float Db, float ExposedBlue) _pixelStats;
    private readonly float[]? _noise;

    // The extra channels (alpha, then black), coded losslessly beside the VarDCT data: every stream of the frame by id, the
    // tree for them and their tokens per stream; or none.
    private JxlEncodeImage[]? _extraStreams;
    private List<JxlTreeNode>? _extraTree;
    private int _extraCount;

    private int _globalScale;
    private int _quantDc;
    private int _xQmScale;
    private int _bQmScale;
    private int _epfIterations;

    private JxlVarDctEncoder(JxlLinearImage image, JxlLossySettings settings, float intensityTarget, int upsampling, float originalDistance, int[]? alpha, JxlChunkedFrame? frame = null, int chunk = 0, (int L, int T, int R, int B) border = default)
    {
        _settings = settings;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.Parallelism) };
        _upsampling = upsampling;
        _originalDistance = originalDistance;
        _frame = frame;
        _chunk = chunk;
        int fullWidth = Ceil(image.Width, 8) * 8, fullHeight = Ceil(image.Height, 8) * 8;
        float[][] xyb = Pad(image, intensityTarget, fullWidth, fullHeight, _parallel);
        if (alpha is not null && settings.SimplifyInvisible)
        {
            SimplifyInvisible(xyb, alpha, image.Width, image.Height, fullWidth, fullHeight);
        }

        // A chunk keeps its border (up to a block each way) for the inverse Gaborish and adaptive quantization, and codes
        // only what is inside it.
        int innerWidth = image.Width - border.L - border.R, innerHeight = image.Height - border.T - border.B;
        if (frame is not null)
        {
            _bordered = xyb;
            _borderStride = fullWidth;
            _borderRows = fullHeight;
            (_borderLeft, _borderTop) = (border.L, border.T);
            int iw = Ceil(innerWidth, 8) * 8, ih = Ceil(innerHeight, 8) * 8;
            float[][] inner = [new float[iw * ih], new float[iw * ih], new float[iw * ih]];
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < ih; y++)
                {
                    xyb[c].AsSpan(((y + border.T) * fullWidth) + border.L, iw).CopyTo(inner[c].AsSpan(y * iw, iw));
                }
            }

            xyb = inner;
            (fullWidth, fullHeight) = (iw, ih);
        }

        if (Speed >= 7)
        {
            _pixelStats = PixelStats(xyb, fullWidth, fullHeight);
        }

        _width = Ceil(innerWidth, upsampling);
        _height = Ceil(innerHeight, upsampling);
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
        _isFirst = new bool[blocks];
        Array.Fill(_isFirst, true);
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

        if (upsampling == 2)
        {
            // DownsampleImage2_Sharper of the padded planes, padded again to whole blocks.
            int hw = fullWidth / 2, hh = fullHeight / 2;
            int pw = _blocksX * 8, ph = _blocksY * 8;
            for (int c = 0; c < 3; c++)
            {
                float[] small = DownsampleSharper(xyb[c], fullWidth, fullHeight, _parallel);
                float[] padded = new float[pw * ph];
                for (int y = 0; y < ph; y++)
                {
                    Span<float> row = padded.AsSpan(y * pw, pw);
                    small.AsSpan(Math.Min(y, hh - 1) * hw, Math.Min(hw, pw)).CopyTo(row);
                    row[Math.Min(hw, pw)..].Fill(row[Math.Min(hw, pw) - 1]);
                }

                xyb[c] = padded;
            }
        }

        _xyb = xyb;
        if (frame is not null)
        {
            _noise = frame.Noise;
        }
        else if (settings.PhotonNoiseIso > 0)
        {
            _noise = SimulatePhotonNoise(_width, _height, settings.PhotonNoiseIso);
        }
    }

    /// <summary>
    /// libjxl's SimplifyInvisible for lossy color, on the XYB planes before padding: each fully transparent pixel takes a weighted
    /// mean of its already simplified left neighbour, the one above right, and its visible neighbours (those at twice the
    /// weight); the padding is then made again from the new edge.
    /// </summary>
    private static void SimplifyInvisible(float[][] xyb, int[] alpha, int w, int h, int stride, int paddedHeight)
    {
        for (int c = 0; c < 3; c++)
        {
            float[] p = xyb[c];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride, arow = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[arow + x] != 0)
                    {
                        continue;
                    }

                    float d = 0;
                    float v = 0;
                    if (x > 0)
                    {
                        v += p[row + x - 1];
                        d++;
                        if (alpha[arow + x - 1] > 0)
                        {
                            v += p[row + x - 1];
                            d++;
                        }
                    }

                    if (x + 1 < w)
                    {
                        if (y > 0)
                        {
                            v += p[row - stride + x + 1];
                            d++;
                        }

                        if (alpha[arow + x + 1] > 0)
                        {
                            v += 2.0f * p[row + x + 1];
                            d += 2.0f;
                        }

                        if (y > 0 && alpha[arow - w + x + 1] > 0)
                        {
                            v += 2.0f * p[row - stride + x + 1];
                            d += 2.0f;
                        }

                        if (y + 1 < h && alpha[arow + w + x + 1] > 0)
                        {
                            v += 2.0f * p[row + stride + x + 1];
                            d += 2.0f;
                        }
                    }

                    if (y > 0 && alpha[arow - w + x] > 0)
                    {
                        v += 2.0f * p[row - stride + x];
                        d += 2.0f;
                    }

                    if (y + 1 < h && alpha[arow + w + x] > 0)
                    {
                        v += 2.0f * p[row + stride + x];
                        d += 2.0f;
                    }

                    p[row + x] = d > 1.0f ? v / d : v;
                }

                p.AsSpan(row + w, stride - w).Fill(p[row + w - 1]);
            }

            for (int y = h; y < paddedHeight; y++)
            {
                p.AsSpan((h - 1) * stride, stride).CopyTo(p.AsSpan(y * stride, stride));
            }
        }
    }

    /// <summary>
    /// libjxl's SimulatePhotonNoise: the noise of a 35 mm sensor at <paramref name="iso"/> spread over the frame's pixels (read
    /// noise, shot noise and response non-uniformity), as the noise LUT over intensity.
    /// </summary>
    internal static float[] SimulatePhotonNoise(int xsize, int ysize, float iso)
    {
        const float PhotonsPerLxSPerUm2 = 11260;
        const float QuantumEfficiency = 0.20f;
        const float NonUniformity = 0.005f;
        const float ReadNoise = 3;
        const float SensorAreaUm2 = 36000.0f * 24000;
        const float Bias = 0.0037930732552754493f;
        float biasCbrt = MathF.Cbrt(Bias);
        float h18 = 10 / iso;
        float pixelArea = SensorAreaUm2 / ((float)xsize * ysize);
        float electrons18 = QuantumEfficiency * PhotonsPerLxSPerUm2 * h18 * pixelArea;
        float[] lut = new float[8];
        for (int i = 0; i < 8; i++)
        {
            float scaled = i / (8 - 2.0f);
            float y = 2 * scaled;
            float t = y - biasCbrt;
            float linear = MathF.Max(0.0f, (t * t * t) + Bias);
            float electrons = electrons18 * (linear / 0.18f);
            float noise = MathF.Sqrt((ReadNoise * ReadNoise) + electrons + (NonUniformity * electrons * NonUniformity * electrons));
            float linearNoise = noise * (0.18f / electrons18);
            float cb = MathF.Cbrt(linear - Bias);
            float derivative = (1.0f / 3) / (cb * cb);
            lut[i] = Math.Clamp(linearNoise * derivative / (0.22f * MathF.Sqrt(2.0f) * 1.13f), 0.0f, 1023.4999f / 1024.0f);
        }

        return lut;
    }

    /// <summary>
    /// libjxl's PixelStatsForChromacityAdjustment at effort 7, on the full padded XYB: the largest step of X between neighbours,
    /// and of B − Y, and the largest exposed blue (B above 1.2 Y) weighted by its steps.
    /// </summary>
    private static (float Dx, float Db, float ExposedBlue) PixelStats(float[][] xyb, int width, int height)
    {
        float xmax = 0, ymax = 0;
        float eb = 0, bx = 0, by = 0;
        for (int ty = 1; ty < height; ty++)
        {
            int row = ty * width, prow = (ty - 1) * width;
            for (int tx = 1; tx < width; tx++)
            {
                float cur = xyb[0][row + tx];
                xmax = MathF.Max(xmax, MathF.Abs(cur - xyb[0][row + tx - 1]));
                ymax = MathF.Max(ymax, MathF.Abs(cur - xyb[0][prow + tx]));
                float cy = xyb[1][row + tx], cb = xyb[2][row + tx];
                float exposed = (float)(cb - (cy * 1.2));
                float diff = cb - cy;
                float prevRow = xyb[2][prow + tx], prev = xyb[2][row + tx - 1];
                float diffPrevRow = prevRow - xyb[1][prow + tx];
                float diffPrev = prev - xyb[1][row + tx - 1];
                bx = MathF.Max(bx, MathF.Abs(diff - diffPrev));
                by = MathF.Max(by, MathF.Abs(diff - diffPrevRow));
                if (exposed >= 0)
                {
                    exposed *= MathF.Abs(cb - prev) + MathF.Abs(cb - prevRow);
                    eb = MathF.Max(eb, exposed);
                }
            }
        }

        return (MathF.Max(xmax, ymax), MathF.Max(bx, by), eb);
    }

    /// <summary>
    /// libjxl's DownsampleImage2_Sharper: a 12×12 kernel fitted to the decoder's 2× upsampling, each result clamped to the range
    /// of the 2×2 source pixels it covers widened by the second smallest step around it in the box-downsampled image.
    /// </summary>
    private static float[] DownsampleSharper(float[] input, int xsize, int ysize, ParallelOptions parallel)
    {
        int ow = (xsize + 1) / 2, oh = (ysize + 1) / 2;
        float[] box = new float[ow * oh];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int cnt = 0;
                float sum = 0;
                for (int iy = 0; iy < 2 && iy + (2 * y) < ysize; iy++)
                {
                    for (int ix = 0; ix < 2 && ix + (2 * x) < xsize; ix++)
                    {
                        sum += input[(((2 * y) + iy) * xsize) + (2 * x) + ix];
                        cnt++;
                    }
                }

                box[(y * ow) + x] = sum / cnt;
            }
        }

        float[] mask = new float[ow * oh];
        for (int y = 0; y < oh; y++)
        {
            int n = (y > 0 ? y - 1 : y) * ow, row = y * ow, s = (y + 1 < oh ? y + 1 : y) * ow;
            for (int x = 0; x < ow; x++)
            {
                float c = box[row + x];
                float w = x > 0 ? box[row + x - 1] : c;
                float e = x + 1 < ow ? box[row + x + 1] : c;
                float min = float.MaxValue, min2 = float.MaxValue;
                foreach (float v in (ReadOnlySpan<float>)[MathF.Abs(c - w), MathF.Abs(c - e), MathF.Abs(c - box[n + x]), MathF.Abs(c - box[s + x])])
                {
                    if (v < min2)
                    {
                        if (v < min)
                        {
                            min2 = min;
                            min = v;
                        }
                        else
                        {
                            min2 = v;
                        }
                    }
                }

                mask[row + x] = min2;
            }
        }

        float[] output = new float[ow * oh];
        Parallel.For(0, oh, parallel, y =>
        {
            Span<int> rows = stackalloc int[12];
            for (int ky = 0; ky < 12; ky++)
            {
                rows[ky] = Math.Clamp((y * 2) + ky - 5, 0, ysize - 1) * xsize;
            }

            for (int x = 0; x < ow; x++)
            {
                // libjxl starts the maximum at the smallest positive float.
                float min = float.MaxValue, max = 1.17549435E-38f;
                for (int ky = 5; ky + 5 < 12; ky++)
                {
                    for (int kx = 5; kx + 5 < 12; kx++)
                    {
                        float v = input[rows[ky] + Math.Clamp((x * 2) + kx - 5, 0, xsize - 1)];
                        min = MathF.Min(min, v);
                        max = MathF.Max(max, v);
                    }
                }

                float sum = 0;
                for (int ky = 0; ky < 12; ky++)
                {
                    for (int kx = 0; kx < 12; kx++)
                    {
                        sum += input[rows[ky] + Math.Clamp((x * 2) + kx - 5, 0, xsize - 1)] * SharperKernel[(ky * 12) + kx];
                    }
                }

                float a = mask[(y * ow) + x];
                output[(y * ow) + x] = sum < min - a ? min - a : sum > max + a ? max + a : sum;
            }
        });

        return output;
    }

    private int Speed => Math.Clamp(_settings.Effort, 1, 7);

    /// <summary>Encodes the image as a bare codestream: the image header from <paramref name="header"/>, then one VarDCT frame.</summary>
    internal static byte[] Encode(JxlLinearImage image, JxlEncodeInput header, JxlLossySettings settings, float intensityTarget = 255)
    {
        // libjxl's low-bitrate point: from distance 10 the frame is coded at half size, at a quarter of the distance plus 0.25.
        float original = settings.Distance;
        int upsampling = 1;
        if (original >= 10)
        {
            upsampling = 2;
            settings = settings with { Distance = (original * 0.25f) + 0.25f };
        }

        JxlVarDctEncoder e = new(image, settings, intensityTarget, upsampling, original, header.Alpha);
        e.PrepareExtraChannels(image, header);
        e.ChooseParameters();
        e.ComputeCoefficients();
        e.QuantizeLf();
        if (settings.Progressive)
        {
            e.MakeProgressive();
        }
        else
        {
            e._passCoeffs = [e._coeffs];
        }

        e.ComputeOrders();
        e.FindBlockEntropyModel();
        e.ChooseEpfSharpness(header);

        return e.Codestream(header);
    }

    /// <summary>Writes the codestream with the frame as it stands: image header, frame header, TOC and sections.</summary>
    private byte[] Codestream(JxlEncodeInput header)
    {
        List<byte[]> sections = Sections();
        JxlBitWriter w = new();
        JxlLosslessEncoder.WriteImageHeader(w, header with { Xyb = true });
        if (header.Icc is not null)
        {
            JxlIccEncoder.Write(w, header.Icc);
        }

        w.ZeroPadToByte();
        if (_patchFrame is not null)
        {
            w.Append(_patchFrame);
        }

        if (_lfFrame is not null)
        {
            w.Append(_lfFrame);
        }

        WriteFrameHeader(w);
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

    private static readonly float[] SharperKernel =
    [
        -0.000314256996835f, -0.000314256996835f, -0.000897597057705f, -0.000562751488849f, -0.000176807273646f, 0.001864627368902f,
        0.001864627368902f, -0.000176807273646f, -0.000562751488849f, -0.000897597057705f, -0.000314256996835f, -0.000314256996835f,
        -0.000314256996835f, -0.001527942804748f, -0.000121760530512f, 0.000191123989093f, 0.010193185932466f, 0.058637519197110f,
        0.058637519197110f, 0.010193185932466f, 0.000191123989093f, -0.000121760530512f, -0.001527942804748f, -0.000314256996835f,
        -0.000897597057705f, -0.000121760530512f, 0.000946363683751f, 0.007113577630288f, 0.000437956841058f, -0.000372823835211f,
        -0.000372823835211f, 0.000437956841058f, 0.007113577630288f, 0.000946363683751f, -0.000121760530512f, -0.000897597057705f,
        -0.000562751488849f, 0.000191123989093f, 0.007113577630288f, 0.044592622228814f, 0.000222278879007f, -0.162864473015945f,
        -0.162864473015945f, 0.000222278879007f, 0.044592622228814f, 0.007113577630288f, 0.000191123989093f, -0.000562751488849f,
        -0.000176807273646f, 0.010193185932466f, 0.000437956841058f, 0.000222278879007f, -0.000913092543974f, -0.017071696107902f,
        -0.017071696107902f, -0.000913092543974f, 0.000222278879007f, 0.000437956841058f, 0.010193185932466f, -0.000176807273646f,
        0.001864627368902f, 0.058637519197110f, -0.000372823835211f, -0.162864473015945f, -0.017071696107902f, 0.414660099370354f,
        0.414660099370354f, -0.017071696107902f, -0.162864473015945f, -0.000372823835211f, 0.058637519197110f, 0.001864627368902f,
        0.001864627368902f, 0.058637519197110f, -0.000372823835211f, -0.162864473015945f, -0.017071696107902f, 0.414660099370354f,
        0.414660099370354f, -0.017071696107902f, -0.162864473015945f, -0.000372823835211f, 0.058637519197110f, 0.001864627368902f,
        -0.000176807273646f, 0.010193185932466f, 0.000437956841058f, 0.000222278879007f, -0.000913092543974f, -0.017071696107902f,
        -0.017071696107902f, -0.000913092543974f, 0.000222278879007f, 0.000437956841058f, 0.010193185932466f, -0.000176807273646f,
        -0.000562751488849f, 0.000191123989093f, 0.007113577630288f, 0.044592622228814f, 0.000222278879007f, -0.162864473015945f,
        -0.162864473015945f, 0.000222278879007f, 0.044592622228814f, 0.007113577630288f, 0.000191123989093f, -0.000562751488849f,
        -0.000897597057705f, -0.000121760530512f, 0.000946363683751f, 0.007113577630288f, 0.000437956841058f, -0.000372823835211f,
        -0.000372823835211f, 0.000437956841058f, 0.007113577630288f, 0.000946363683751f, -0.000121760530512f, -0.000897597057705f,
        -0.000314256996835f, -0.001527942804748f, -0.000121760530512f, 0.000191123989093f, 0.010193185932466f, 0.058637519197110f,
        0.058637519197110f, 0.010193185932466f, 0.000191123989093f, -0.000121760530512f, -0.001527942804748f, -0.000314256996835f,
        -0.000314256996835f, -0.000314256996835f, -0.000897597057705f, -0.000562751488849f, -0.000176807273646f, 0.001864627368902f,
        0.001864627368902f, -0.000176807273646f, -0.000562751488849f, -0.000897597057705f, -0.000314256996835f, -0.000314256996835f,
    ];

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
        if (Speed >= 6 && d >= 0.5f && _epfIterationsFor(d) > 0)
        {
            _original = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                _original[c] = new float[_width * _height];
                for (int y = 0; y < _height; y++)
                {
                    _xyb[c].AsSpan(y * _blocksX * 8, _width).CopyTo(_original[c].AsSpan(y * _width, _width));
                }
            }
        }

        if (Speed >= 7 && _frame is null)
        {
            // Patches need the whole image: libjxl looks for none in chunks.
            FindPatches();
        }

        if (Speed >= 5)
        {
            // InitialQuantField on the image before Gaborish, at a lower distance when Gaborish is off.
            float iqfDistance = _gaborish ? d : d * 0.62f;
            if (_bordered is not null)
            {
                _aq = JxlAdaptiveQuantization.Compute(_bordered, _borderStride, _borderRows, iqfDistance, _parallel).Crop(_borderLeft / 8, _borderTop / 8, _blocksX, _blocksY);
            }
            else
            {
                _aq = JxlAdaptiveQuantization.Compute(_xyb, _blocksX * 8, _blocksY * 8, iqfDistance, _parallel);
            }

            ComputeGlobalScaleAndQuant(quantDc, 0.39f / d, 0);

            if (_gaborish && _bordered is not null)
            {
                JxlAdaptiveQuantization.GaborishInverse(_bordered, _borderStride, _borderRows, _parallel);
                int iw = _blocksX * 8;
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < _blocksY * 8; y++)
                    {
                        _bordered[c].AsSpan(((y + _borderTop) * _borderStride) + _borderLeft, iw).CopyTo(_xyb[c].AsSpan(y * iw, iw));
                    }
                }
            }
            else if (_gaborish)
            {
                JxlAdaptiveQuantization.GaborishInverse(_xyb, _blocksX * 8, _blocksY * 8, _parallel);
            }

            // Per 64×64 tile, as libjxl's process_tile: the chroma-from-luma factors on DCT8 first at effort 7, the block
            // sizes, the quantization field over them, then the factors again for the chosen blocks.
            float inv = (float)(1.0 * GlobalScaleDenom / _globalScale);
            float[] field = (float[])_aq.QuantField.Clone();
            JxlAcStrategySearch search = new(_xyb, _blocksX, _blocksY, _aq.QuantField, _aq.Mask1x1, d, Speed, _strategy, _isFirst);
            Parallel.For(0, _tilesX * _tilesY, _parallel, tile =>
            {
                int tx = tile % _tilesX;
                int ty = tile / _tilesX;
                int x0 = tx * TileBlocks, y0 = ty * TileBlocks;
                int w = Math.Min(TileBlocks, _blocksX - x0), h = Math.Min(TileBlocks, _blocksY - y0);
                if (Speed >= 7)
                {
                    ChromaFromLumaTile(tx, ty, useDct8: true);
                }

                search.ProcessTile(x0, y0, w, h, _yToX[tile] / (float)ColorFactor, 1f + (_yToB[tile] / (float)ColorFactor));
                AdjustQuantField(field, d, x0, y0, w, h);
                for (int y = y0; y < y0 + h; y++)
                {
                    for (int x = x0; x < x0 + w; x++)
                    {
                        int at = (y * _blocksX) + x;
                        _quantField[at] = ClampQuant((field[at] * inv) + 0.5f);
                    }
                }

                ChromaFromLumaTile(tx, ty, useDct8: false);
            });
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

        // ComputeChromacityAdjustments: the X scale from the distance asked for, both raised for pixelized chroma at effort 7.
        _xQmScale = 3;
        foreach (float step in (ReadOnlySpan<float>)[2.5f, 5.5f, 9.5f])
        {
            if (_originalDistance > step)
            {
                _xQmScale++;
            }
        }

        (float dx, float db, float exposedBlue) = _pixelStats;
        int xAdd = dx >= 0.026f ? 3 : dx >= 0.022f ? 2 : dx >= 0.015f ? 1 : 0;
        int bAdd = exposedBlue >= 0.13f ? 1 : 0;
        _xQmScale = Math.Max(_xQmScale, 2 + xAdd);
        _bQmScale = 2 + (db > 0.38f ? 2 + bAdd : db > 0.33f ? 1 + bAdd : db > 0.28f ? bAdd : 0);
        _epfIterations = _epfIterationsFor(d);
    }

    /// <summary>
    /// libjxl's AdjustQuantField: each varblock larger than 8×8 takes the largest quantization value of its blocks, moved towards
    /// their mean above distance 1.54 for varblocks of four blocks or more.
    /// </summary>
    private void AdjustQuantField(float[] field, float distance, int x0, int y0, int w, int h)
    {
        float mixer = 1.0f;
        const float Limit = 1.54138f;
        const float Mul = 0.56391f;
        if (distance > Limit)
        {
            mixer = MathF.Max(0, mixer - ((distance - Limit) * Mul));
        }

        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int at = (y * _blocksX) + x;
                if (!_isFirst[at])
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
    /// Chooses a 64×64 tile's chroma-from-luma factors (CfLHeuristics::ComputeTile): over the tile's varblocks, the factors that
    /// best predict X's and B's HF coefficients from Y's, each scaled by its step and weight. With <paramref name="useDct8"/>
    /// (effort 7, before the block sizes) every block is a DCT8 at step 1; otherwise the chosen varblocks at their quantization
    /// values, with fast least squares at efforts 5 and 6 and Newton steps at 7.
    /// </summary>
    private void ChromaFromLumaTile(int tx, int ty, bool useDct8)
    {
        JxlDequantMatrices matrices = JxlDequantMatrices.Defaults;
        bool fast = !useDct8 && Speed <= 6;
        int stride = _blocksX * 8;
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
                if (!useDct8 && !_isFirst[at])
                {
                    continue;
                }

                JxlTransformType t = useDct8 ? JxlTransformType.Dct8 : (JxlTransformType)_strategy[at];
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
                float q = useDct8 ? 1 : Scale * 128 * _quantField[at];
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

        int tile = (ty * _tilesX) + tx;
        _yToX[tile] = (sbyte)JxlChromaFromLuma.FindBestMultiplier(yx.ToArray(), xs.ToArray(), 0f, fast);
        _yToB[tile] = (sbyte)JxlChromaFromLuma.FindBestMultiplier(yb.ToArray(), bs.ToArray(), 1f, fast);
    }

    /// <summary>
    /// cjxl -p: the HF split into three passes by frequency (libjxl's progressive_passes_dc_vlf_lf_full_ac: each varblock's
    /// coefficients within twice its LF size, then three times, then the rest), and, unless the frame is coded at half size
    /// (libjxl refuses both), the LF image moved into an LF frame: lossy Modular XYB, squeezed, at 2% of the distance.
    /// </summary>
    private void MakeProgressive()
    {
        _passes = 3;
        ReadOnlySpan<int> limits = [2, 3, 8];
        _passCoeffs = new int[3][][][];
        for (int p = 0; p < 3; p++)
        {
            _passCoeffs[p] = new int[_groups][][];
        }

        for (int g = 0; g < _groups; g++)
        {
            for (int p = 0; p < 3; p++)
            {
                _passCoeffs[p][g] = [new int[_coeffs[g][0].Length], new int[_coeffs[g][1].Length], new int[_coeffs[g][2].Length]];
            }

            (int x0, int y0, int gw, int gh) = GroupRect(g);
            int offset = 0;
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    int at = ((y0 + y) * _blocksX) + x0 + x;
                    if (!_isFirst[at])
                    {
                        continue;
                    }

                    (int w8, int h8) = JxlTransforms8.Size((JxlTransformType)_strategy[at]);
                    int xs = Math.Max(w8, h8), ys = Math.Min(w8, h8);
                    int size = w8 * h8 * 64;
                    int done = 1;
                    for (int p = 0; p < 3; p++)
                    {
                        int n = limits[p];
                        for (int c = 0; c < 3; c++)
                        {
                            int[] src = _coeffs[g][c];
                            int[] dst = _passCoeffs[p][g][c];
                            for (int cy = 0; cy < ys * n; cy++)
                            {
                                for (int cx = 0; cx < xs * n; cx++)
                                {
                                    if (cx < xs * done && cy < ys * done)
                                    {
                                        continue;
                                    }

                                    int pos = offset + (cy * xs * 8) + cx;
                                    dst[pos] = src[pos];
                                }
                            }
                        }

                        done = n;
                    }

                    offset += size;
                }
            }
        }

        if (_upsampling != 1)
        {
            return;
        }

        // The LF frame: Y, X and B - Y of the LF image, quantized by enc_modular's XYB factors (65536, 4096 and 4096 when squeezed).
        int lw = _blocksX, lh = _blocksY, n2 = lw * lh;
        float[] stored = [128.0f / 65536, 128.0f / 4096, 128.0f / 4096];
        int[] cy2 = new int[n2], cx2 = new int[n2], cb2 = new int[n2];
        for (int i = 0; i < n2; i++)
        {
            float x = _lf[0][i], y = _lf[1][i], b = _lf[2][i];
            cy2[i] = (int)((y * 4096.0f) + (y < 0 ? -0.5f : 0.5f));
            cx2[i] = (int)((x * 65536.0f) + (x < 0 ? -0.5f : 0.5f));
            cb2[i] = (int)((b * 4096.0) + 0.5f) - cy2[i];
        }

        List<JxlChannel> channels = [new JxlChannel(lw, lh, 0, 0) { Data = cy2 }, new JxlChannel(lw, lh, 0, 0) { Data = cx2 }, new JxlChannel(lw, lh, 0, 0) { Data = cb2 }];
        for (int i = 0; i < _extraCount; i++)
        {
            channels.Add(new JxlChannel(lw, lh, 0, 0));
        }

        float distance = MathF.Max(0.05f * 0.02f, _settings.Distance * 0.02f);
        List<byte[]> sections = JxlModularEncoder.EncodeLossyXyb(channels, distance, Math.Min(9, Speed + 1), lw, lh, _settings.Parallelism, stored);
        JxlBitWriter w = new();
        w.WriteBool(false);
        w.Write(1, 2); // LF frame
        w.Write(1, 1); // Modular
        w.WriteU64(0);
        w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 1); // upsampling
        for (int i = 0; i < _extraCount; i++)
        {
            w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 1);
        }

        w.Write(1, 2); // group size shift
        w.WriteU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 3), 1); // one pass
        w.WriteU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 0), 1); // LF level 1
        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0); // no name
        w.WriteBool(false); // loop filter: Gaborish off, no EPF
        w.WriteBool(false);
        w.Write(0, 2);
        w.WriteNoExtensions();
        w.WriteNoExtensions();
        w.WriteBool(false); // no TOC permutation
        w.ZeroPadToByte();
        JxlU32 tocSize = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        foreach (byte[] sec in sections)
        {
            w.WriteU32(tocSize, (uint)sec.Length);
        }

        w.ZeroPadToByte();
        foreach (byte[] sec in sections)
        {
            w.Append(sec);
        }

        _lfFrame = w.ToArray();
    }

    /// <summary>
    /// Turns the alpha and black planes into the frame's extra channels: integers of their bit depth, box-averaged to the frame
    /// size when it is coded at half size (libjxl's ec_resampling), then their Modular streams and tree.
    /// </summary>
    private void PrepareExtraChannels(JxlLinearImage image, JxlEncodeInput header)
    {
        List<(int[] Data, int Bits)> planes = [];
        if (header.Alpha is not null)
        {
            planes.Add((header.Alpha, header.AlphaBitDepth.BitsPerSample));
        }

        if (image.Black is float[] black)
        {
            int max = (1 << header.BlackBitDepth.BitsPerSample) - 1;
            planes.Add(([.. black.Select(v => (int)MathF.Round(Math.Clamp(v, 0, 1) * max))], header.BlackBitDepth.BitsPerSample));
        }

        _extraCount = planes.Count;
        if (planes.Count == 0)
        {
            return;
        }

        List<JxlChannel> channels = [];
        foreach ((int[] data, _) in planes)
        {
            JxlChannel ch = new(_width, _height, 0, 0);
            if (_upsampling == 1)
            {
                data.CopyTo(ch.Data, 0);
            }
            else
            {
                int fw = image.Width, fh = image.Height;
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        float sum = 0;
                        int cnt = 0;
                        for (int iy = 0; iy < 2 && (2 * y) + iy < fh; iy++)
                        {
                            for (int ix = 0; ix < 2 && (2 * x) + ix < fw; ix++)
                            {
                                sum += data[(((2 * y) + iy) * fw) + (2 * x) + ix];
                                cnt++;
                            }
                        }

                        ch.Data[(y * _width) + x] = (int)((sum / cnt) + 0.5f);
                    }
                }
            }

            channels.Add(ch);
        }

        float[] distances = [.. planes.Select((p, i) => i == 0 && header.Alpha is not null ? _settings.AlphaDistance : 0f)];
        (_extraStreams, _extraTree) = JxlModularEncoder.PrepareExtraChannels(channels, [.. planes.Select(p => p.Bits)], distances, Speed, _width, _height, _settings.Parallelism, _settings.Progressive ? 3 : 1);
    }

    /// <summary>
    /// libjxl's FindBestPatchDictionary at effort 7: text-like patches, else from distance 3 dots, packed into a reference-only
    /// Modular frame (RoundtripPatchFrame: XYB quantized by factors from the distance, coded losslessly as Y, X and B − Y), whose
    /// reconstruction is then taken out of the image where each patch goes.
    /// </summary>
    private void FindPatches()
    {
        float d = _settings.Distance;
        int stride = _blocksX * 8;
        List<JxlPatchCandidate> info = JxlPatchSearch.FindTextLike(_xyb, stride, _width, _height, _parallel);
        if (info.Count == 0 && d >= 3)
        {
            info = JxlPatchSearch.FindDots(_xyb, stride, _blocksY * 8, _width, _height, _parallel);
        }

        if (info.Count == 0)
        {
            return;
        }

        info = [.. info.OrderByDescending(p => p.Width * p.Height)];
        (float[][] reference, int rw, int rh, (int X, int Y)[] places) = JxlPatchSearch.Pack(info);

        // The quantization: libjxl's factors, through the binary16 LF dequantization the frame carries (DequantMatricesSetCustomDC).
        float[] factors = [65536.0f * (1.0f / (1.0f + (23.0f * d))), 4096.0f * (1.0f / (1.0f + (14.0f * d))), 4096.0f * (1.0f / (1.0f + (14.0f * d)))];
        float[] stored = new float[3];
        float[] inv = new float[3];
        for (int c = 0; c < 3; c++)
        {
            stored[c] = (float)BitConverter.UInt16BitsToHalf(JxlBitWriter.F16Bits(1.0f / factors[c] * 128.0f));
            inv[c] = 1.0f / (stored[c] * (1.0f / 128.0f));
        }

        int n = rw * rh;
        int[] cy = new int[n], cx = new int[n], cb = new int[n];
        for (int i = 0; i < n; i++)
        {
            float x = reference[0][i], y = reference[1][i], b = reference[2][i];
            cy[i] = (int)((y * inv[1]) + (y < 0 ? -0.5f : 0.5f));
            cx[i] = (int)((x * inv[0]) + (x < 0 ? -0.5f : 0.5f));
            cb[i] = (int)((b * (double)inv[2]) + 0.5f) - cy[i];
        }

        // As the decoder reconstructs it (ModularXyb).
        float mx = stored[0] / 128, my = stored[1] / 128, mb = stored[2] / 128;
        _patchReference = [new float[n], new float[n], new float[n]];
        for (int i = 0; i < n; i++)
        {
            _patchReference[0][i] = cx[i] * mx;
            _patchReference[1][i] = cy[i] * my;
            _patchReference[2][i] = (cb[i] + cy[i]) * mb;
        }

        _patchWidth = rw;
        JxlPatchBlending[] add = [new JxlPatchBlending(JxlPatchBlendMode.Add, 0, false), .. Enumerable.Repeat(new JxlPatchBlending(JxlPatchBlendMode.None, 0, false), _extraCount)];
        _patches = [.. info.Select((p, i) => new JxlPatch(3, places[i].X, places[i].Y, p.Width, p.Height, [.. p.Positions.Select(t => new JxlPatchTarget(t.X, t.Y, add))]))];

        int groupShift = JxlModularEncoder.GroupShift(rw, rh);
        List<JxlChannel> channels = [new JxlChannel(rw, rh, 0, 0) { Data = cy }, new JxlChannel(rw, rh, 0, 0) { Data = cx }, new JxlChannel(rw, rh, 0, 0) { Data = cb }];

        // The extra channels are in every frame: zero here, as libjxl's placeholders.
        for (int i = 0; i < _extraCount; i++)
        {
            channels.Add(new JxlChannel(rw, rh, 0, 0));
        }

        List<byte[]> sections = JxlModularEncoder.Encode(channels, 3, 8, Speed, rw, rh, groupShift, _settings.Parallelism, stored);
        JxlBitWriter w = new();
        w.WriteBool(false);
        w.Write(2, 2); // reference only
        w.Write(1, 1); // Modular
        w.WriteU64(0);
        w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 1); // upsampling
        for (int i = 0; i < _extraCount; i++)
        {
            w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 1);
        }

        w.Write((ulong)groupShift, 2);
        w.WriteBool(true); // the frame's own size
        JxlU32 dim = new(0, 8, 256, 11, 2304, 14, 18688, 30);
        w.WriteU32(dim, (uint)rw);
        w.WriteU32(dim, (uint)rh);
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 0), 3); // saved as reference 3
        w.WriteBool(true); // before the color transform
        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0); // no name
        w.WriteBool(false); // loop filter: Gaborish off, no EPF
        w.WriteBool(false);
        w.Write(0, 2);
        w.WriteNoExtensions();
        w.WriteNoExtensions();
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

        _patchFrame = w.ToArray();

        // PatchDictionaryEncoder::SubtractFrom.
        AddPatches(_xyb, stride, -1);
    }

    /// <summary>Adds (<paramref name="sign"/> 1) or takes out (−1) each patch's reconstruction where it goes in XYB planes of <paramref name="stride"/>.</summary>
    private void AddPatches(float[][] planes, int stride, int sign)
    {
        if (_patches is null)
        {
            return;
        }

        foreach (JxlPatch p in _patches)
        {
            foreach (JxlPatchTarget t in p.Targets)
            {
                for (int y = 0; y < p.Height; y++)
                {
                    for (int x = 0; x < p.Width; x++)
                    {
                        int at = ((t.Y + y) * stride) + t.X + x;
                        int from = ((p.Y0 + y) * _patchWidth) + p.X0 + x;
                        for (int c = 0; c < 3; c++)
                        {
                            planes[c][at] += sign * _patchReference![c][from];
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Writes the patch dictionary (PatchDictionaryEncoder::Encode): per patch its reference frame, place and size, how many
    /// times it occurs, and each occurrence's position (the first, then offsets) and blend mode (add).
    /// </summary>
    private void WritePatches(JxlBitWriter w)
    {
        List<JxlToken> tokens = [new JxlToken(0, (uint)_patches!.Length)];
        foreach (JxlPatch p in _patches)
        {
            tokens.Add(new JxlToken(1, (uint)p.Reference));
            tokens.Add(new JxlToken(3, (uint)p.X0));
            tokens.Add(new JxlToken(3, (uint)p.Y0));
            tokens.Add(new JxlToken(2, (uint)(p.Width - 1)));
            tokens.Add(new JxlToken(2, (uint)(p.Height - 1)));
            tokens.Add(new JxlToken(7, (uint)(p.Targets.Length - 1)));
            for (int j = 0; j < p.Targets.Length; j++)
            {
                if (j == 0)
                {
                    tokens.Add(new JxlToken(4, (uint)p.Targets[j].X));
                    tokens.Add(new JxlToken(4, (uint)p.Targets[j].Y));
                }
                else
                {
                    tokens.Add(new JxlToken(6, PackSigned(p.Targets[j].X - p.Targets[j - 1].X)));
                    tokens.Add(new JxlToken(6, PackSigned(p.Targets[j].Y - p.Targets[j - 1].Y)));
                }

                foreach (JxlPatchBlending b in p.Targets[j].Blending)
                {
                    tokens.Add(new JxlToken(5, (uint)b.Mode));
                }
            }
        }

        List<List<JxlToken>> streams = [tokens];
        JxlEntropyCode code = JxlEntropyCode.Write(w, new JxlHistogramParams { Rle = true }, 10, streams);
        code.WriteTokens(w, tokens);
    }

    private static uint PackSigned(int v) => v >= 0 ? (uint)v << 1 : ((uint)(-(v + 1)) << 1) | 1;

    /// <summary>The EPF iterations libjxl's LoopFilterFromParams gives a distance: one each from 0.7, 1.5 and 4.</summary>
    private static int _epfIterationsFor(float distance)
    {
        int n = 0;
        foreach (float threshold in (ReadOnlySpan<float>)[0.7f, 1.5f, 4.0f])
        {
            if (distance >= threshold)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Chooses each block's EPF sharpness at efforts 6 and 7 (libjxl's ComputeARHeuristics): the frame is decoded with every
    /// candidate sharpness (0, 2 and 7, or 0 and 4 above distance 4.5) and each block's masked error against the image before
    /// Gaborish is measured; each block takes the best candidate unless its top or left neighbour's is as good, then, with
    /// how often each value followed each pair of neighbours, the choice is made again weighting the errors by that
    /// frequency, with no smoothing slightly favoured.
    /// </summary>
    private void ChooseEpfSharpness(JxlEncodeInput header)
    {
        if (_original is null)
        {
            return;
        }

        float d = _settings.Distance;
        float clamped = MathF.Min(5.0f, d);
        byte[] steps = d > 4.5f ? [0, 4] : [0, 2, 7];
        float[][] reconstructed = JxlImageDecoder.DecodeXyb(Codestream(header), _settings.Parallelism);
        JxlLoopFilter filter = new() { Gaborish = _gaborish, EpfIterations = _epfIterations };
        float sigmaBase = filter.QuantMul * 65536f / _globalScale;
        float[][] errors = new float[8][];
        float[] mask = _aq!.Mask1x1;
        JxlParallel parallel = new(Math.Max(1, _settings.Parallelism), CancellationToken.None);
        foreach (byte val in steps)
        {
            float[][] planes = [.. reconstructed.Select(p => (float[])p.Clone())];
            if (_gaborish)
            {
                JxlFilters.Gaborish(planes, _width, _height, _width, filter.GaborishWeights, parallel);
            }

            float sharp = filter.SharpLut[val];
            JxlFilters.Epf(planes, _width, _height, _width, filter, (bx, by) => sigmaBase / _quantField[(by * _blocksX) + bx] * sharp, parallel);
            AddPatches(planes, _width, 1);
            if (_noise is not null && _upsampling == 1)
            {
                // libjxl's reconstruction renders the noise too, as a first frame.
                JxlFeatures.RenderNoise(new JxlPlanes(_width, _height, planes), _noise, GroupDim, 0, 0, 0, 1);
            }
            float[] error = new float[_blocksX * _blocksY];
            Parallel.For(0, _blocksY, _parallel, by =>
            {
                for (int bx = 0; bx < _blocksX; bx++)
                {
                    error[(by * _blocksX) + bx] = BlockL2Distance(_original, planes, mask, bx, by);
                }
            });
            errors[val] = error;
        }

        const float FavorNoSmoothing = 0.99f;
        for (int by = 0; by < _blocksY; by++)
        {
            for (int bx = 0; bx < _blocksX; bx++)
            {
                int at = (by * _blocksX) + bx;
                byte top = by > 0 ? _epfSharpness[at - _blocksX] : (byte)0;
                byte left = bx > 0 ? _epfSharpness[at - 1] : (byte)0;
                float topError = errors[top][at];
                float leftError = errors[left][at];
                byte best = 0;
                float bestError = float.MaxValue;
                foreach (byte val in steps)
                {
                    float e = errors[val][at];
                    if (val == 0)
                    {
                        e *= FavorNoSmoothing;
                    }

                    if (e < bestError)
                    {
                        best = val;
                        bestError = e;
                    }
                }

                _epfSharpness[at] = bestError < MathF.Min(topError, leftError) ? best : topError < leftError ? top : left;
            }
        }

        // libjxl weighs each candidate again by how often it followed the same neighbours, but as an integer quotient of a count
        // by a larger total, which is always 0: only no smoothing's own factor is left.
        const float C3Base = 0.98017198824148288f;
        const float C3Clamp = 0.85970338919928291f;
        float c3 = MathF.Max(C3Clamp, MathF.Pow(C3Base, clamped));
        for (int by = 0; by < _blocksY; by++)
        {
            for (int bx = 0; bx < _blocksX; bx++)
            {
                int at = (by * _blocksX) + bx;
                byte top = by > 0 ? _epfSharpness[at - _blocksX] : (byte)0;
                byte left = bx > 0 ? _epfSharpness[at - 1] : (byte)0;
                byte best = 0;
                float bestError = float.MaxValue;
                foreach (byte val in steps)
                {
                    float e = val == 0 ? errors[val][at] * c3 : errors[val][at];
                    if (e < bestError)
                    {
                        best = val;
                        bestError = e;
                    }
                }

                _epfSharpness[at] = best;
            }
        }

    }

    /// <summary>libjxl's ComputeBlockL2Distance: a block's squared XYB error weighted by the squared 1×1 mask, X counting most.</summary>
    private float BlockL2Distance(float[][] a, float[][] b, float[] mask, int bx, int by)
    {
        int x0 = bx * 8, y0 = by * 8;
        int x1 = Math.Min(x0 + 8, _width), y1 = Math.Min(y0 + 8, _height);
        float e0 = 0, e1 = 0, e2 = 0;
        int maskStride = _blocksX * 8;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                float m = mask[(y * maskStride) + x];
                float m2 = m * m;
                int at = (y * _width) + x;
                float d0 = a[0][at] - b[0][at];
                float d1 = a[1][at] - b[1][at];
                float d2 = a[2][at] - b[2][at];
                e0 += m2 * d0 * d0;
                e1 += m2 * d1 * d1;
                e2 += m2 * d2 * d2;
            }
        }

        return (float)((12.339445295782363 * e0) + (1.0 * e1) + (0.2 * e2));
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
                    if (!_isFirst[at])
                    {
                        continue;
                    }

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
                // The lowest frequencies quantize to zero (libjxl's inverse weights are zero there): the LF image carries them.
                if (x < cw / 8 && y < ch / 8)
                {
                    output[row + x] = 0;
                    continue;
                }

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
                int t = _strategy[at];
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

    /// <summary>
    /// Chooses the coefficient orders (ComputeUsedOrders and ComputeCoeffOrder): for each transform order in use (up to 32×32,
    /// and not on images under 5×5 blocks from effort 4), positions sorted by how often they are zero in a sample of the
    /// varblocks, ties kept in the natural order; an order equal to the natural one is not sent. Efforts 1 to 3 customize DCT8
    /// alone.
    /// </summary>
    private void ComputeOrders()
    {
        _passOrders = new int[_passes][][];
        _passUsed = new int[_passes];
        for (int p = 0; p < _passes; p++)
        {
            _passOrders[p] = new int[NumOrders * 3][];
            ComputeOrders(p);
        }
    }

    /// <summary>libjxl's ComputeCoeffOrder for one pass, from that pass's coefficients.</summary>
    private void ComputeOrders(int pass)
    {
        int[][][] coeffs = _passCoeffs[pass];
        uint used;
        uint customize;
        if (Speed <= 3)
        {
            used = 1;
            customize = 1;
        }
        else
        {
            used = 0;
            customize = 0;
            foreach (sbyte t in _strategy)
            {
                int ord = JxlTransforms8.OrderId((JxlTransformType)t);
                used |= 1u << ord;
                if (ord <= 6)
                {
                    customize |= 1u << ord;
                }
            }

            if (_blocksX < 5 && _blocksY < 5)
            {
                customize = 0;
            }
        }

        // DCT8 alone is sampled at half the varblocks, which libjxl reads as if the skipped ones were not there.
        long[][][] numZeros = new long[NumOrders][][];
        if (customize != 0)
        {
            double fraction = customize == 1 ? 0.5 : 1.0;
            ulong threshold = (ulong)((ulong.MaxValue >> 32) * fraction);
            ulong s0 = 0x94D049BB133111EB, s1 = 0xBF58476D1CE4E5B9;
            for (int g = 0; g < _groups; g++)
            {
                (int gx0, int gy0, int gw, int gh) = GroupRect(g);
                int offset = 0;
                for (int by = gy0; by < gy0 + gh; by++)
                {
                    for (int bx = gx0; bx < gx0 + gw; bx++)
                    {
                        int at = (by * _blocksX) + bx;
                        if (!_isFirst[at])
                        {
                            continue;
                        }

                        ulong a = s0;
                        ulong b = s1;
                        ulong bits = a + b;
                        s0 = b;
                        a ^= a << 23;
                        a ^= b ^ (a >> 18) ^ (b >> 5);
                        s1 = a;
                        if ((bits >> 32) > threshold)
                        {
                            continue;
                        }

                        JxlTransformType t = (JxlTransformType)_strategy[at];
                        (int w8, int h8) = JxlTransforms8.Size(t);
                        int size = w8 * h8 * 64;
                        int ord = JxlTransforms8.OrderId(t);
                        numZeros[ord] ??= [new long[size], new long[size], new long[size]];
                        int cx = Math.Max(w8, h8);
                        int cy = Math.Min(w8, h8);
                        for (int c = 0; c < 3; c++)
                        {
                            int[] p = coeffs[g][c];
                            long[] zeros = numZeros[ord][c];
                            for (int k = 0; k < size; k++)
                            {
                                if (offset + k < p.Length && p[offset + k] == 0)
                                {
                                    zeros[k]++;
                                }
                            }

                            for (int iy = 0; iy < cy; iy++)
                            {
                                for (int ix = 0; ix < cx; ix++)
                                {
                                    zeros[(iy * 8 * cx) + ix] = -1;
                                }
                            }
                        }

                        offset += size;
                    }
                }
            }
        }

        for (int ord = 0; ord < NumOrders; ord++)
        {
            if ((used & (1u << ord)) == 0 || (customize & (1u << ord)) == 0)
            {
                continue;
            }

            int[] natural = JxlHfPass.Natural(ord);
            int sz = natural.Length;
            float invSqrt = 1.0f / MathF.Sqrt(sz);
            bool nondefault = false;
            int[][] orders = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                long[] zeros = numZeros[ord]?[c] ?? new long[sz];
                (ulong Key, int Pos)[] items = new (ulong, int)[sz];
                for (int i = 0; i < sz; i++)
                {
                    int pos = natural[i];
                    float count = (zeros[pos] * invSqrt) + 0.1f;
                    ulong q = count <= 0 ? 0 : (ulong)count;
                    items[i] = ((q << 16) | (uint)i, pos);
                }

                Array.Sort(items, (x, y) => x.Key.CompareTo(y.Key));
                orders[c] = new int[sz];
                for (int i = 0; i < sz; i++)
                {
                    orders[c][i] = items[i].Pos;
                    nondefault |= orders[c][i] != natural[i];
                }
            }

            if (!nondefault)
            {
                customize &= ~(1u << ord);
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                _passOrders[pass][(ord * 3) + c] = orders[c];
            }
        }

        _passUsed[pass] = (int)customize;
        _usedThisChunk = (int)used;
    }

    /// <summary>Gets the order of a channel for an order id: the chosen one when it is sent, else the natural order.</summary>
    private int[] Order(int orderId, int c, int pass) => (_passUsed[pass] & (1 << orderId)) != 0 ? _passOrders[pass][(orderId * 3) + c] : JxlHfPass.Natural(orderId);

    /// <summary>Tokenizes a pass group's coefficients (TokenizeCoefficients), Y, X then B for each varblock.</summary>
    private List<JxlToken> TokenizeGroup(int g, int pass)
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
                if (!_isFirst[at])
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
                    ReadOnlySpan<int> block = _passCoeffs[pass][g][c].AsSpan(offsets[c], size);
                    offsets[c] += size;
                    int[] order = Order(orderId, c, pass);
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
        w.WriteU64((_patches is null ? 0UL : 2UL) | (_noise is null ? 0UL : 1UL) | (_lfFrame is null ? 0UL : 0x20UL)); // patches, noise, LF frame
        if (_lfFrame is null)
        {
            w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), (uint)_upsampling);
            for (int i = 0; i < _extraCount; i++)
            {
                w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), (uint)_upsampling);
            }
        }

        w.Write((ulong)_xQmScale, 3);
        w.Write((ulong)_bQmScale, 3);
        w.WriteU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 3), (uint)_passes);
        if (_passes == 3)
        {
            // libjxl's progressive_passes_dc_vlf_lf_full_ac: no shifts; the first pass for 4× and up, the second for 2×.
            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 1), 2);
            w.Write(0, 2);
            w.Write(0, 2);
            w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 4);
            w.WriteU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0), 2);
            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 0, 3), 0);
            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 0, 3), 1);
        }

        w.WriteBool(false); // no crop
        for (int i = 0; i <= _extraCount; i++)
        {
            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 2), 0); // replace
        }

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
            // With an LF frame the LF groups carry no LF image.
            List<JxlChannel> dc = _lfFrame is null ? LfChannels(g) : [];
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
        // With extra channels their own tree takes stream 0 and the group streams (above 3 per LF group).
        List<JxlTreeNode> tree = [];
        int Add(List<JxlTreeNode> sub)
        {
            int root = tree.Count;
            foreach (JxlTreeNode n in sub)
            {
                tree.Add(n.Property < 0 ? n : n with { Left = n.Left + root, Right = n.Right + root });
            }

            return root;
        }

        if (_extraTree is null)
        {
            tree.Add(new JxlTreeNode { Property = 1, SplitValue = acmStream0 - 1, Left = 1, Right = 1 + acmTree.Count });
            Add(acmTree);
            Add(dcTree);
        }
        else
        {
            tree.Add(default);
            int high = Add(_extraTree);
            int mid = tree.Count;
            tree.Add(default);
            int acm = Add(acmTree);
            int low = tree.Count;
            tree.Add(default);
            int dc = Add(dcTree);
            int whole = Add(_extraTree);
            tree[0] = new JxlTreeNode { Property = 1, SplitValue = 3 * _lfGroups, Left = high, Right = mid };
            tree[mid] = new JxlTreeNode { Property = 1, SplitValue = acmStream0 - 1, Left = acm, Right = low };
            tree[low] = new JxlTreeNode { Property = 1, SplitValue = 0, Left = dc, Right = whole };
            tree = Prune(tree);
        }

        List<List<JxlToken>> treeTokens = [[]];
        List<int> multipliers = [];
        (int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] context) = JxlModularEncoder.TokenizeTree(tree, treeTokens[0], multipliers);
        int[] multiplier = [.. multipliers];
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
        List<int> widths = [.. streams.Select(s => s.Channels.Select(ch => ch.Width).DefaultIfEmpty(0).Max())];

        // The extra channels' tokens, per stream id.
        Dictionary<int, List<JxlToken>> extraTokens = [];
        if (_extraStreams is not null)
        {
            int[] ids = [.. Enumerable.Range(0, _extraStreams.Length).Where(i => _extraStreams[i].Channels.Count > 0)];
            List<JxlToken>[] parts = new List<JxlToken>[ids.Length];
            Parallel.For(0, ids.Length, _parallel, k =>
            {
                JxlEncodeImage img = _extraStreams[ids[k]];
                List<JxlToken> t = [];
                for (int c = 0; c < img.CodedPrefix(GroupDim); c++)
                {
                    if (img.Channels[c].Width > 0 && img.Channels[c].Height > 0)
                    {
                        t.AddRange(JxlModularEncoder.Tokenize(img.Channels[c], c, ids[k], property, value, left, right, predictor, context, usesWp, multiplier));
                    }
                }

                parts[k] = t;
            });
            for (int k = 0; k < ids.Length; k++)
            {
                extraTokens[ids[k]] = parts[k];
                modularTokens.Add(parts[k]);
                JxlEncodeImage img = _extraStreams[ids[k]];
                widths.Add(img.Channels.Take(img.CodedPrefix(GroupDim)).Select(ch => ch.Width).DefaultIfEmpty(0).Max());
            }
        }


        // The HF tokens of every pass group, pass after pass.
        List<JxlToken>[] acTokens = new List<JxlToken>[_groups * _passes];
        Parallel.For(0, _groups * _passes, _parallel, i => acTokens[i] = TokenizeGroup(i % _groups, i / _groups));

        // LF global.
        JxlBitWriter global = new();
        if (_patches is not null)
        {
            WritePatches(global);
        }

        foreach (float v in _noise ?? [])
        {
            global.Write((ulong)Math.Round(v * 1024.0f, MidpointRounding.AwayFromZero), 10);
        }

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
        if (_extraStreams is not null)
        {
            JxlModularEncoder.WriteStream(global, _extraStreams[0], code, extraTokens.GetValueOrDefault(0, []));
        }

        // LF groups.
        JxlBitWriter[] lfGroups = new JxlBitWriter[_lfGroups];
        Parallel.For(0, _lfGroups, _parallel, g =>
        {
            JxlBitWriter w = new();
            if (_lfFrame is null)
            {
                w.Write((ulong)_lfPrecision, 2);
                WriteStream(w, code, modularTokens[g]);
            }

            if (_extraStreams is not null && extraTokens.TryGetValue(1 + _lfGroups + g, out List<JxlToken>? lfTokens))
            {
                // The extra channels squeezed 3 or more both ways.
                JxlModularEncoder.WriteStream(w, _extraStreams[1 + _lfGroups + g], code, lfTokens);
            }

            (_, _, int bw, int bh) = LfGroupRect(g);
            w.Write((ulong)(metaCounts[g] - 1), CeilLog2((long)bw * bh));
            WriteStream(w, code, modularTokens[_lfGroups + g]);
            lfGroups[g] = w;
        });

        // HF global: the default matrices, one HF preset, then per pass the orders and the HF distributions.
        JxlBitWriter hf = new();
        hf.WriteBool(true);
        hf.Write(0, CeilLog2(_groups));

        // HistogramParams for the HF tokens: at most four clusters above effort 3 (libjxl's kFastest), then fast clustering.
        JxlHistogramParams acp = new()
        {
            Clustering = JxlHistogramParams.ClusteringKind.Fast,
            UintMethod = JxlHistogramParams.UintKind.None,
            AnsStrategy = JxlHistogramParams.AnsKind.Approximate,
            MaxHistograms = effort <= 2 ? 4 : int.MaxValue,
        };
        JxlEntropyCode[] acCodes = new JxlEntropyCode[_passes];
        for (int pass = 0; pass < _passes; pass++)
        {
            hf.WriteU32(new JxlU32(0x5F, 0, 0x13, 0, 0, 0, 0, 13), (uint)_passUsed[pass]);
            if (_passUsed[pass] != 0)
            {
                WriteOrders(hf, pass);
            }

            List<List<JxlToken>> ac = [.. acTokens.Skip(pass * _groups).Take(_groups)];
            acCodes[pass] = JxlEntropyCode.Write(hf, acp, (NonZeroBuckets + ZeroDensityContexts) * _blockClusters, ac);
        }

        // The extra channels not squeezed go with the last pass; squeezed ones with the pass of their shift.
        JxlBitWriter[] passGroups = new JxlBitWriter[_groups * _passes];
        Parallel.For(0, _groups * _passes, _parallel, i =>
        {
            int g = i % _groups, pass = i / _groups;
            JxlBitWriter w = new();
            acCodes[pass].WriteTokens(w, acTokens[i]);
            int id = 1 + (3 * _lfGroups) + 17 + i;
            if (_extraStreams is not null && extraTokens.TryGetValue(id, out List<JxlToken>? groupTokens))
            {
                JxlModularEncoder.WriteStream(w, _extraStreams[id], code, groupTokens);
            }

            passGroups[i] = w;
        });

        if (_groups == 1 && _passes == 1)
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

    /// <summary>
    /// Drops the decisions a merged tree can no longer take the other way (the extra channels' tree tests the stream id too),
    /// which libjxl's decoder refuses: each node's split must lie inside the range its ancestors leave for the property.
    /// </summary>
    private static List<JxlTreeNode> Prune(List<JxlTreeNode> tree)
    {
        List<JxlTreeNode> output = [];
        Dictionary<int, (long Low, long High)> ranges = [];

        int Visit(int node)
        {
            JxlTreeNode n = tree[node];
            while (n.Property >= 0)
            {
                (long low, long high) = ranges.TryGetValue(n.Property, out var r) ? r : (int.MinValue, int.MaxValue);
                if (n.SplitValue < low)
                {
                    n = tree[n.Left];
                }
                else if (n.SplitValue >= high)
                {
                    n = tree[n.Right];
                }
                else
                {
                    break;
                }
            }

            int at = output.Count;
            output.Add(n);
            if (n.Property < 0)
            {
                return at;
            }

            bool had = ranges.TryGetValue(n.Property, out var saved);
            (long lo, long hi) = had ? saved : (int.MinValue, int.MaxValue);
            ranges[n.Property] = (n.SplitValue + 1, hi);
            int left = Visit(n.Left);
            ranges[n.Property] = (lo, n.SplitValue);
            int right = Visit(n.Right);
            if (had)
            {
                ranges[n.Property] = saved;
            }
            else
            {
                ranges.Remove(n.Property);
            }

            output[at] = n with { Left = left, Right = right };
            return at;
        }

        Visit(0);
        return output;
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
                if (!_isFirst[at])
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

    /// <summary>Writes the sent coefficient orders as Lehmer codes against the natural order (EncodeCoeffOrders), the LLF skipped.</summary>
    private void WriteOrders(JxlBitWriter w, int pass) => WriteOrders(w, _passOrders[pass], _passUsed[pass]);

    /// <summary>Writes the coefficient orders sent (<paramref name="used"/>) as Lehmer codes of their permutation of the natural order.</summary>
    private static void WriteOrders(JxlBitWriter w, int[][] orders, int used)
    {
        List<JxlToken> tokens = [];
        for (int ord = 0; ord < NumOrders; ord++)
        {
            if ((used & (1 << ord)) == 0)
            {
                continue;
            }

            int[] natural = JxlHfPass.Natural(ord);
            int size = natural.Length;
            int skip = size / 64;
            int[] lut = new int[size];
            for (int i = 0; i < size; i++)
            {
                lut[natural[i]] = i;
            }

            for (int c = 0; c < 3; c++)
            {
                int[] order = orders[(ord * 3) + c];
                int[] perm = new int[size];
                for (int i = 0; i < size; i++)
                {
                    perm[i] = lut[order[i]];
                }

                // Lehmer code: for each entry, how many of the values not yet taken are below it (a Fenwick tree counts them).
                int[] lehmer = new int[size];
                int[] tree = new int[size + 1];
                for (int i = 0; i < size; i++)
                {
                    int taken = 0;
                    for (int k = perm[i]; k > 0; k -= k & -k)
                    {
                        taken += tree[k];
                    }

                    lehmer[i] = perm[i] - taken;
                    for (int k = perm[i] + 1; k <= size; k += k & -k)
                    {
                        tree[k]++;
                    }
                }

                int end = size;
                while (end > skip && lehmer[end - 1] == 0)
                {
                    end--;
                }

                tokens.Add(new JxlToken(OrderContext((uint)size), (uint)(end - skip)));
                uint last = 0;
                for (int i = skip; i < end; i++)
                {
                    tokens.Add(new JxlToken(OrderContext(last), (uint)lehmer[i]));
                    last = (uint)lehmer[i];
                }
            }
        }

        List<List<JxlToken>> streams = [tokens];
        JxlHistogramParams p = new() { Rle = true };
        JxlEntropyCode code = JxlEntropyCode.Write(w, p, 8, streams);
        code.WriteTokens(w, streams[0]);
    }

    private static int OrderContext(uint x) => Math.Min(JxlEntropyDecoder.CeilLog2Plus1(x), 7);
}
