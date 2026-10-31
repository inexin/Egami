using System.Runtime.Intrinsics;

namespace Egami.Formats.Jxl;

/// <summary>
/// Decodes a VarDCT frame's color (Annex I): the LF coefficients and HF metadata of each LF group, the HF coefficients of each
/// pass group, then dequantization, chroma from luma, adaptive LF smoothing and the inverse transforms. Works over the whole
/// frame at once; the result is the X, Y and B planes (or Cb, Y and Cr for recompressed JPEGs) at block-padded size.
/// Translated from jxl-oxide's jxl-vardct and jxl-render crates (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlVarDct
{
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

    private readonly JxlImageHeader _image;
    private readonly JxlFrameHeader _frame;
    private readonly JxlVarDctGlobal _global;
    private readonly float[] _lfDequant;
    private readonly SafetyLimits _limits;
    private readonly int _groupDim;
    private readonly int[] _hs = new int[3];
    private readonly int[] _vs = new int[3];
    private readonly bool _hasH;
    private readonly bool _hasV;

    // Frame-wide state, at luma block resolution unless shifted.
    private readonly sbyte[] _blockType;
    private readonly int[] _hfMul;
    private readonly float[] _epfSigma;
    private readonly int[] _xFromY;
    private readonly int[] _bFromY;
    private readonly int _tiles;
    private readonly float[][] _lf = new float[3][];
    private readonly int[][] _lfQuant = new int[3][];
    private readonly int[][] _coeffs = new int[3][];
    private JxlHfGlobal? _hf;

    internal JxlVarDct(JxlImageHeader image, JxlFrameHeader frame, JxlVarDctGlobal global, float[] lfDequant, SafetyLimits limits)
    {
        _image = image;
        _frame = frame;
        _global = global;
        _lfDequant = lfDequant;
        _limits = limits;
        _groupDim = frame.GroupDim;
        (long w, long h) = frame.CodedSize;
        Width = (int)w;
        Height = (int)h;
        int[] up = frame.YCbCr ? frame.JpegUpsampling : [0, 0, 0];
        _hasH = up.Any(v => v is 1 or 2);
        _hasV = up.Any(v => v is 1 or 3);
        for (int c = 0; c < 3; c++)
        {
            (bool hh, bool vv) = up[c] switch { 0 => (_hasH, _hasV), 1 => (false, false), 2 => (false, _hasV), _ => (_hasH, false) };
            _hs[c] = hh ? 1 : 0;
            _vs[c] = vv ? 1 : 0;
        }

        BlocksX = (int)Ceil(Width, 8);
        BlocksY = (int)Ceil(Height, 8);
        if (_hasH)
        {
            BlocksX = (int)Ceil(BlocksX, 2) * 2;
        }

        if (_hasV)
        {
            BlocksY = (int)Ceil(BlocksY, 2) * 2;
        }

        long blocks = (long)BlocksX * BlocksY;
        if (blocks * 64 * 3 * 4 > limits.MaxTotalBytes * 8)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, blocks * 64);
        }

        _blockType = new sbyte[blocks];
        Array.Fill(_blockType, (sbyte)-1);
        _hfMul = new int[blocks];
        _epfSigma = new float[blocks];
        _tiles = (int)Ceil(Width, 64);
        int tileRows = (int)Ceil(Height, 64);
        _xFromY = new int[(long)_tiles * tileRows];
        _bFromY = new int[(long)_tiles * tileRows];
        for (int c = 0; c < 3; c++)
        {
            _lf[c] = new float[(long)PlaneBlocksX(c) * PlaneBlocksY(c)];
            _lfQuant[c] = new int[_lf[c].Length];
            _coeffs[c] = new int[_lf[c].Length * 64];
        }
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Gets the luma width in blocks, rounded up to even when chroma is subsampled horizontally.</summary>
    internal int BlocksX { get; }

    internal int BlocksY { get; }

    /// <summary>Gets whether a channel is subsampled; chroma from luma applies only when none is (equal JPEG sampling factors, such as all 1×2, are not).</summary>
    internal bool Subsampled => _hs.Any(s => s != 0) || _vs.Any(s => s != 0);

    internal int HShift(int c) => _hs[c];

    internal int VShift(int c) => _vs[c];

    internal int PlaneBlocksX(int c) => _hs[c] == 1 ? BlocksX / 2 : BlocksX;

    internal int PlaneBlocksY(int c) => _vs[c] == 1 ? BlocksY / 2 : BlocksY;

    /// <summary>Gets the per-block EPF sigma, frame-wide.</summary>
    internal float[] EpfSigma => _epfSigma;

    /// <summary>Gets the dequantization matrices (for JPEG reconstruction).</summary>
    internal JxlHfGlobal? HfGlobal => _hf;

    internal JxlVarDctGlobal Global => _global;

    /// <summary>Gets a plane's quantized coefficients, frame-wide (for JPEG reconstruction).</summary>
    internal int[] Coefficients(int c) => _coeffs[c];

    /// <summary>Gets a plane's quantized LF (DC) coefficients, one a block.</summary>
    internal int[] LfQuant(int c) => _lfQuant[c];

    /// <summary>Gets the X from Y chroma-from-luma factors, one a 64×64 tile.</summary>
    internal int[] XFromY => _xFromY;

    /// <summary>Gets the B from Y factors, one a 64×64 tile.</summary>
    internal int[] BFromY => _bFromY;

    /// <summary>Gets the number of 64×64 tiles in a row.</summary>
    internal int Tiles => _tiles;

    /// <summary>Decodes the VarDCT part of LF group <paramref name="index"/>: the LF coefficients, then the HF metadata.</summary>
    internal void DecodeLfGroupCoefficients(JxlBitReader r, int index, JxlTree? tree, int numLf)
    {
        (int lx, int ly, int lfw, int lfh) = LfGroupRect(index);
        int extraPrecision = (int)r.ReadBits(2);
        int bw = (int)Ceil(lfw, 8);
        int bh = (int)Ceil(lfh, 8);
        int[] order = [1, 0, 2];
        List<JxlChannel> channels = [];
        foreach (int c in order)
        {
            channels.Add(new JxlChannel(ShiftSize(bw, _hasH, _hs[c] == 1), ShiftSize(bh, _hasV, _vs[c] == 1), _hs[c], _vs[c]));
        }

        JxlModularImage image = JxlModularImage.Read(r, channels, tree, _image.BitDepth.BitsPerSample, _limits);
        image.Decode(r, 1 + index, 0, image.Channels.Count);
        image.Inverse(r);
        double scaleInv = (double)_global.GlobalScale * _global.QuantLf;
        for (int k = 0; k < 3; k++)
        {
            int c = order[k];
            JxlChannel q = image.Channels[k];
            float scale = (float)(_lfDequant[c] * (1 << (9 - extraPrecision)) / scaleInv);
            int stride = PlaneBlocksX(c);
            int x0 = (lx * _groupDim) >> _hs[c];
            int y0 = (ly * _groupDim) >> _vs[c];
            for (int y = 0; y < q.Height && y0 + y < PlaneBlocksY(c); y++)
            {
                for (int x = 0; x < q.Width && x0 + x < stride; x++)
                {
                    int v = q.Data[(y * q.Width) + x];
                    _lfQuant[c][((y0 + y) * stride) + x0 + x] = v;
                    _lf[c][((y0 + y) * stride) + x0 + x] = v * scale;
                }
            }
        }
    }

    /// <summary>Decodes the HF metadata of LF group <paramref name="index"/> (I.2.2): chroma from luma, transforms, HF multipliers and EPF sharpness.</summary>
    internal void DecodeHfMetadata(JxlBitReader r, int index, JxlTree? tree, int numLf)
    {
        (int lx, int ly, int lfw, int lfh) = LfGroupRect(index);
        int bw = (int)Ceil(lfw, 8);
        int bh = (int)Ceil(lfh, 8);
        if (_hasH)
        {
            bw = (int)Ceil(bw, 2) * 2;
        }

        if (_hasV)
        {
            bh = (int)Ceil(bh, 2) * 2;
        }

        int bits = JxlEntropyDecoder.CeilLog2Plus1((uint)(bw * bh) - 1);
        int count = 1 + (int)r.ReadBits(bw * bh == 1 ? 0 : bits);
        int tw = (int)Ceil(lfw, 64);
        int th = (int)Ceil(lfh, 64);
        List<JxlChannel> channels = [new(tw, th, 0, 0), new(tw, th, 0, 0), new(count, 2, 0, 0), new(bw, bh, 0, 0)];
        JxlModularImage image = JxlModularImage.Read(r, channels, tree, _image.BitDepth.BitsPerSample, _limits);
        image.Decode(r, 1 + (2 * numLf) + index, 0, image.Channels.Count);
        image.Inverse(r);
        JxlChannel xfy = image.Channels[0];
        JxlChannel bfy = image.Channels[1];
        JxlChannel info = image.Channels[2];
        JxlChannel sharpness = image.Channels[3];
        int tx0 = lx * _groupDim / 8;
        int ty0 = ly * _groupDim / 8;
        for (int y = 0; y < th; y++)
        {
            for (int x = 0; x < tw; x++)
            {
                int at = ((ty0 + y) * _tiles) + tx0 + x;
                if (at < _xFromY.Length && tx0 + x < _tiles)
                {
                    _xFromY[at] = xfy.Data[(y * tw) + x];
                    _bFromY[at] = bfy.Data[(y * tw) + x];
                }
            }
        }

        JxlLoopFilter lf = _frame.LoopFilter;
        bool epf = lf.EpfIterations > 0;
        float sigmaBase = lf.QuantMul * 65536f / _global.GlobalScale;
        int bx0 = lx * _groupDim;
        int by0 = ly * _groupDim;
        int next = 0;
        for (int y = 0; y < bh; y++)
        {
            for (int x = 0; x < bw;)
            {
                int at = ((by0 + y) * BlocksX) + bx0 + x;
                if (_blockType[at] != -1)
                {
                    x++;
                    continue;
                }

                if (next >= info.Width)
                {
                    throw r.Corrupt("The block list does not fill the LF group.");
                }

                int type = info.Data[next];
                int hfMul = info.Data[info.Width + next] + 1;
                next++;
                if (type < 0 || type > (int)JxlTransformType.Dct128x256 || hfMul <= 0)
                {
                    throw r.Corrupt($"A varblock has transform {type} or HF multiplier {hfMul}.");
                }

                JxlTransformType t = (JxlTransformType)type;
                (int dw, int dh) = JxlTransforms8.Size(t);
                if ((x % 32) + dw > 32 || (y % 32) + dh > 32 || x + dw > bw || y + dh > bh)
                {
                    throw r.Corrupt("A varblock crosses a group border or leaves the LF group.");
                }

                float sigma = sigmaBase / hfMul;
                for (int dy = 0; dy < dh; dy++)
                {
                    for (int dx = 0; dx < dw; dx++)
                    {
                        int b = ((by0 + y + dy) * BlocksX) + bx0 + x + dx;
                        if (_blockType[b] != -1)
                        {
                            throw r.Corrupt("Varblocks overlap.");
                        }

                        _blockType[b] = dx == 0 && dy == 0 ? (sbyte)type : (sbyte)-2;
                        _hfMul[b] = hfMul;
                        if (epf)
                        {
                            int s = sharpness.Data[((y + dy) * bw) + x + dx];
                            if ((uint)s >= 8)
                            {
                                throw r.Corrupt($"An EPF sharpness of {s} is out of range.");
                            }

                            _epfSigma[b] = sigma * lf.SharpLut[s];
                        }
                    }
                }

                x += dw;
            }
        }
    }

    /// <summary>Reads HF global (I.2.4, I.3).</summary>
    internal void ReadHfGlobal(JxlBitReader r, JxlTree? tree, int numLf, int numGroups)
    {
        JxlDequantMatrices matrices = JxlDequantMatrices.Read(r, tree, _image.BitDepth.BitsPerSample, 1 + (3 * numLf), _limits);
        int presets = 1 + (int)r.ReadBits(numGroups <= 1 ? 0 : JxlEntropyDecoder.CeilLog2Plus1((uint)numGroups - 1));
        JxlHfPass[] passes = new JxlHfPass[_frame.Passes];
        for (int p = 0; p < passes.Length; p++)
        {
            passes[p] = JxlHfPass.Read(r, presets, _global.BlockClusters);
        }

        _hf = new JxlHfGlobal { Matrices = matrices, Presets = presets, Passes = passes };
    }

    /// <summary>Decodes the HF coefficients of pass group <paramref name="group"/> in pass <paramref name="pass"/> (I.3.3).</summary>
    internal void DecodePassGroup(JxlBitReader r, int pass, int group)
    {
        JxlHfGlobal hf = _hf ?? throw r.Corrupt("A pass group comes before HF global.");
        JxlHfPass hfPass = hf.Passes[pass];
        int groupsPerRow = (int)Ceil(Width, _groupDim);
        int gx = group % groupsPerRow;
        int gy = group / groupsPerRow;
        int gb = _groupDim / 8;
        int bx0 = gx * gb;
        int by0 = gy * gb;
        int gbw = Math.Min(gb, BlocksX - bx0);
        int gbh = Math.Min(gb, BlocksY - by0);
        if (gbw <= 0 || gbh <= 0)
        {
            return;
        }

        int shift = _frame.Shifts.Length > pass ? _frame.Shifts[pass] : 0;
        int presets = hf.Presets;
        int presetBits = presets <= 1 ? 0 : JxlEntropyDecoder.CeilLog2Plus1((uint)presets - 1);
        int preset = (int)r.ReadBits(presetBits);
        if (preset >= presets)
        {
            throw r.Corrupt($"The pass group selects HF preset {preset} of {presets}.");
        }

        int nbc = _global.BlockClusters;
        int ctxSize = 495 * nbc;
        JxlEntropyDecoder decoder = hfPass.Decoder.Clone();
        ReadOnlySpan<byte> clusters = decoder.Clusters.Slice(ctxSize * preset, ctxSize);
        decoder.Begin(r);
        int[][] lfThresholds = _global.LfThresholds;
        int[] qfThresholds = _global.QfThresholds;
        int lfMul = (lfThresholds[0].Length + 1) * (lfThresholds[1].Length + 1) * (lfThresholds[2].Length + 1);
        int hfMulCount = qfThresholds.Length + 1;
        int[][] nonZeros = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            nonZeros[c] = new int[ShiftSize(gbw, _hasH, _hs[c] == 1)];
        }

        ReadOnlySpan<int> channelOrder = [1, 0, 2];
        for (int y = 0; y < gbh; y++)
        {
            for (int x = 0; x < gbw; x++)
            {
                int at = ((by0 + y) * BlocksX) + bx0 + x;
                int type = _blockType[at];
                if (type < 0)
                {
                    continue;
                }

                JxlTransformType t = (JxlTransformType)type;
                (int w8, int h8) = JxlTransforms8.Size(t);
                int numBlocks = w8 * h8;
                int logBlocks = System.Numerics.BitOperations.Log2((uint)numBlocks);
                int orderId = JxlTransforms8.OrderId(t);
                int qf = _hfMul[at];
                int lfIndex = 0;
                foreach (int c2 in (ReadOnlySpan<int>)[0, 2, 1])
                {
                    int[] thresholds = lfThresholds[c2];
                    lfIndex *= thresholds.Length + 1;
                    int q = _lfQuant[c2][(((by0 + y) >> _vs[c2]) * PlaneBlocksX(c2)) + ((bx0 + x) >> _hs[c2])];
                    foreach (int threshold in thresholds)
                    {
                        if (q > threshold)
                        {
                            lfIndex++;
                        }
                    }
                }

                int hfIndex = 0;
                foreach (int threshold in qfThresholds)
                {
                    if (qf > threshold)
                    {
                        hfIndex++;
                    }
                }

                for (int k = 0; k < 3; k++)
                {
                    int c = channelOrder[k];
                    int chIdx = (k * 13) + orderId;
                    int sx = x >> _hs[c];
                    int sy = y >> _vs[c];
                    if (_hs[c] != 0 || _vs[c] != 0)
                    {
                        if (sx << _hs[c] != x || sy << _vs[c] != y)
                        {
                            continue;
                        }

                        if (_blockType[((by0 + sy) * BlocksX) + bx0 + sx] < 0)
                        {
                            continue;
                        }
                    }

                    int blockCtx = _global.BlockContextMap[(((chIdx * hfMulCount) + hfIndex) * lfMul) + lfIndex];
                    int[] row = nonZeros[c];
                    int predicted = sy == 0 ? (sx == 0 ? 32 : row[sx - 1]) : sx == 0 ? row[sx] : (row[sx] + row[sx - 1] + 1) >> 1;
                    int nzIndex = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nzCtx = blockCtx + (nzIndex * nbc);
                    uint nz = decoder.ReadUintClustered(r, clusters[nzCtx], 0);
                    if (nz > (uint)(63 << logBlocks))
                    {
                        throw r.Corrupt("A varblock has too many nonzero coefficients.");
                    }

                    int nzValue = (int)((nz + (uint)numBlocks - 1) >> logBlocks);
                    for (int dx = 0; dx < w8 && sx + dx < row.Length; dx++)
                    {
                        row[sx + dx] = nzValue;
                    }

                    if (nz == 0)
                    {
                        continue;
                    }

                    int[] plane = _coeffs[c];
                    int stride = PlaneBlocksX(c) * 8;
                    int px0 = ((bx0 >> _hs[c]) + sx) * 8;
                    int py0 = ((by0 >> _vs[c]) + sy) * 8;
                    int prevNonzero = nz <= (uint)(numBlocks * 4) ? 1 : 0;
                    int[] order = hfPass.Order(orderId, c);
                    int orderWidth = JxlHfPass.OrderWidth(orderId);
                    bool transposed = JxlTransforms8.Transposed(t);
                    int coeffBase = (blockCtx * 458) + (37 * nbc);
                    for (int i = numBlocks; i < order.Length; i++)
                    {
                        int ctx = (((CoeffNumNonzeroContext[(int)((nz - 1) >> logBlocks)] + CoeffFreqContext[(i - numBlocks) >> logBlocks]) * 2) + prevNonzero);
                        if (ctx >= 458)
                        {
                            throw r.Corrupt("Too many zeros in a varblock's HF coefficients.");
                        }

                        uint u = decoder.ReadUintClustered(r, clusters[coeffBase + ctx], 0);
                        if (u == 0)
                        {
                            prevNonzero = 0;
                            continue;
                        }

                        int coeff = JxlHeaders.UnpackSigned(u) << shift;
                        int pos = order[i];
                        int dx = pos % orderWidth;
                        int dy = pos / orderWidth;
                        if (transposed)
                        {
                            (dx, dy) = (dy, dx);
                        }

                        plane[((py0 + dy) * stride) + px0 + dx] += coeff;
                        prevNonzero = 1;
                        if (--nz == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        decoder.Finish(r);
    }

    /// <summary>Gets the LF image alone (1:8, X, Y and B, cropped to the frame), after chroma from luma and smoothing.</summary>
    internal float[][] LfImage()
    {
        PrepareLf(null);
        return PreparedLfImage();
    }

    /// <summary>Gets the LF image (1:8) as <see cref="Reconstruct"/> left it: chroma from luma and smoothing already applied.</summary>
    internal float[][] PreparedLfImage()
    {
        int width = (int)Ceil(Width, 8);
        int height = (int)Ceil(Height, 8);
        float[][] planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                _lf[c].AsSpan(y * PlaneBlocksX(c), width).CopyTo(planes[c].AsSpan(y * width));
            }
        }

        return planes;
    }

    /// <summary>
    /// Reconstructs the color planes: LF (from the LF groups, or <paramref name="lfFrame"/>), dequantized HF, chroma from luma
    /// and the inverse transforms. Returns the three planes at block-padded size, row stride <see cref="PlaneBlocksX"/> × 8.
    /// </summary>
    internal float[][] Reconstruct(float[][]? lfFrame, JxlParallel parallel)
    {
        PrepareLf(lfFrame);
        float[][] planes = new float[3][];
        JxlHfGlobal? hf = _hf;
        float[]? oim = _image.OpsinInverse;
        float[] quantBias = oim is null ? [1 - 0.05465007330715401f, 1 - 0.07005449891748593f, 1 - 0.049935103337343655f] : [oim[12], oim[13], oim[14]];
        float numerator = oim is null ? 0.145f : oim[15];
        float[] qmScale = [MathF.Pow(0.8f, _frame.XqmScale - 2), 1, MathF.Pow(0.8f, _frame.BqmScale - 2)];
        for (int c = 0; c < 3; c++)
        {
            int stride = PlaneBlocksX(c) * 8;
            float[] plane = new float[(long)stride * PlaneBlocksY(c) * 8];
            planes[c] = plane;
            if (hf is null)
            {
                continue;
            }

            int[] coeffs = _coeffs[c];
            ForEachVarblock(c, parallel, (bx, by, t, hfMul) =>
            {
                (int w8, int h8) = JxlTransforms8.Size(t);
                int width = w8 * 8;
                float mul = 65536f / (_global.GlobalScale * (float)hfMul) * qmScale[c];
                float[] matrix = hf.Matrices.Get(c, t);
                int o = (by * 8 * stride) + (bx * 8);
                float bias = quantBias[c];
                for (int y = 0; y < h8 * 8; y++)
                {
                    int row = o + (y * stride);
                    int x = 0;
                    if (Vector256.IsHardwareAccelerated)
                    {
                        // The same operations as below, eight coefficients at a time (a division by 0 is in the lane not chosen).
                        Vector256<float> vBias = Vector256.Create(bias);
                        Vector256<float> vNumerator = Vector256.Create(numerator);
                        Vector256<float> vMul = Vector256.Create(mul);
                        for (; x + 8 <= width; x += 8)
                        {
                            Vector256<float> q = Vector256.ConvertToSingle(Vector256.Create(coeffs.AsSpan(row + x, 8)));
                            Vector256<float> small = Vector256.LessThanOrEqual(Vector256.Abs(q), Vector256<float>.One);
                            q = Vector256.ConditionalSelect(small, q * vBias, q - (vNumerator / q));
                            (q * Vector256.Create(matrix.AsSpan((y * width) + x, 8)) * vMul).CopyTo(plane.AsSpan(row + x, 8));
                        }
                    }

                    for (; x < width; x++)
                    {
                        float q = coeffs[row + x];
                        q = MathF.Abs(q) <= 1 ? q * bias : q - (numerator / q);
                        plane[row + x] = q * matrix[(y * width) + x] * mul;
                    }
                }
            });
        }

        if (hf is not null && !Subsampled)
        {
            ChromaFromLumaHf(planes, parallel);
        }

        for (int c = 0; c < 3; c++)
        {
            int stride = PlaneBlocksX(c) * 8;
            float[] plane = planes[c];
            float[] lf = _lf[c];
            int lfStride = PlaneBlocksX(c);
            ForEachVarblock(c, parallel, (bx, by, t, _) =>
            {
                int o = (by * 8 * stride) + (bx * 8);
                if (JxlTransforms8.IsSmall(t))
                {
                    plane[o] = lf[(by * lfStride) + bx];
                }
                else
                {
                    (int bw, int bh) = JxlTransforms8.Size(t);
                    for (int y = 0; y < bh; y++)
                    {
                        for (int x = 0; x < bw; x++)
                        {
                            plane[o + (y * stride) + x] = lf[((by + y) * lfStride) + bx + x];
                        }
                    }

                    JxlDct.Dct2D(plane, o, stride, bw, bh, forward: true);
                    int logW = System.Numerics.BitOperations.Log2((uint)bw);
                    int logH = System.Numerics.BitOperations.Log2((uint)bh);
                    for (int y = 0; y < bh; y++)
                    {
                        for (int x = 0; x < bw; x++)
                        {
                            plane[o + (y * stride) + x] /= JxlDct.Scale(y, 5 - logH) * JxlDct.Scale(x, 5 - logW);
                        }
                    }
                }

                JxlDct.Inverse(t, plane, o, stride);
            });
        }

        return planes;
    }

    /// <summary>Fills the LF coefficients from the LF frame, or applies chroma from luma and adaptive smoothing to the coded ones.</summary>
    private void PrepareLf(float[][]? lfFrame)
    {
        if (lfFrame is not null)
        {
            for (int c = 0; c < 3; c++)
            {
                int stride = PlaneBlocksX(c);
                int lfw = (int)Ceil(Width, 8);
                for (int y = 0; y < PlaneBlocksY(c); y++)
                {
                    for (int x = 0; x < stride; x++)
                    {
                        int sx = Math.Min(x, lfw - 1);
                        int sy = Math.Min(y, (int)Ceil(Height, 8) - 1);
                        _lf[c][(y * stride) + x] = lfFrame[c][(sy * lfw) + sx];
                    }
                }
            }
        }
        else
        {
            if (!Subsampled)
            {
                float kx = _global.BaseCorrelationX + ((_global.XFactorLf - 128) / (float)_global.ColourFactor);
                float kb = _global.BaseCorrelationB + ((_global.BFactorLf - 128) / (float)_global.ColourFactor);
                for (int i = 0; i < _lf[1].Length; i++)
                {
                    float yv = _lf[1][i];
                    _lf[0][i] += kx * yv;
                    _lf[2][i] += kb * yv;
                }
            }

            if ((_frame.Flags & 0x80) == 0)
            {
                SmoothLf();
            }
        }
    }

    private static int ShiftSize(int size, bool has, bool subsampled) => !has ? size : subsampled ? (size + 1) / 2 : ((size + 1) / 2) * 2;

    private static long Ceil(long a, long b) => (a + b - 1) / b;

    private (int Lx, int Ly, int W, int H) LfGroupRect(int index)
    {
        int lfDim = _groupDim * 8;
        int perRow = (int)Ceil(Width, lfDim);
        int lx = index % perRow;
        int ly = index / perRow;
        return (lx, ly, Math.Min(lfDim, Width - (lx * lfDim)), Math.Min(lfDim, Height - (ly * lfDim)));
    }

    /// <summary>Calls <paramref name="action"/> for each varblock a channel codes, at that channel's block coordinates.</summary>
    private void ForEachVarblock(int c, JxlParallel parallel, Action<int, int, JxlTransformType, int> action)
    {
        int hs = _hs[c];
        int vs = _vs[c];

        // Each varblock writes only its own area, so rows of blocks run on several threads.
        parallel.For(BlocksY, by =>
        {
            for (int bx = 0; bx < BlocksX; bx++)
            {
                int at = (by * BlocksX) + bx;
                int type = _blockType[at];
                if (type < 0)
                {
                    continue;
                }

                int sbx = bx >> hs;
                int sby = by >> vs;
                if (hs != 0 || vs != 0)
                {
                    if (sbx << hs != bx || sby << vs != by)
                    {
                        continue;
                    }

                    // The block at the shifted position, within the same group, must start a varblock too.
                    int gb = _groupDim / 8;
                    int gx0 = bx / gb * gb;
                    int gy0 = by / gb * gb;
                    int local = ((gy0 + ((by - gy0) >> vs)) * BlocksX) + gx0 + ((bx - gx0) >> hs);
                    if (_blockType[local] < 0)
                    {
                        continue;
                    }

                    sbx = (gx0 >> hs) + ((bx - gx0) >> hs);
                    sby = (gy0 >> vs) + ((by - gy0) >> vs);
                }

                action(sbx, sby, (JxlTransformType)type, _hfMul[at]);
            }
        }, minimumPerThread: 8);
    }

    private void ChromaFromLumaHf(float[][] planes, JxlParallel parallel)
    {
        int stride = BlocksX * 8;
        int height = BlocksY * 8;
        float[] px = planes[0];
        float[] py = planes[1];
        float[] pb = planes[2];
        parallel.For(height, y =>
        {
            int tileRow = Math.Min(y / 64, (_xFromY.Length / _tiles) - 1) * _tiles;
            for (int x0 = 0; x0 < stride; x0 += 64)
            {
                // One tile's factors for its 64 columns (the last tile also takes any columns past it).
                int tile = tileRow + Math.Min(x0 / 64, _tiles - 1);
                float kx = _global.BaseCorrelationX + (_xFromY[tile] / (float)_global.ColourFactor);
                float kb = _global.BaseCorrelationB + (_bFromY[tile] / (float)_global.ColourFactor);
                int at = (y * stride) + x0;
                int end = Math.Min(stride, x0 + 64) - x0;
                int x = 0;
                if (Vector256.IsHardwareAccelerated)
                {
                    Vector256<float> vkx = Vector256.Create(kx);
                    Vector256<float> vkb = Vector256.Create(kb);
                    for (; x + 8 <= end; x += 8)
                    {
                        Vector256<float> yv = Vector256.Create(py.AsSpan(at + x, 8));
                        (Vector256.Create(px.AsSpan(at + x, 8)) + (vkx * yv)).CopyTo(px.AsSpan(at + x, 8));
                        (Vector256.Create(pb.AsSpan(at + x, 8)) + (vkb * yv)).CopyTo(pb.AsSpan(at + x, 8));
                    }
                }

                for (; x < end; x++)
                {
                    float yv = py[at + x];
                    px[at + x] += kx * yv;
                    pb[at + x] += kb * yv;
                }
            }
        });
    }

    /// <summary>Adaptive LF smoothing (I.5.3), on the LF image as a whole.</summary>
    private void SmoothLf()
    {
        const float ScaleSelf = 0.052262735f;
        const float ScaleSide = 0.2034514f;
        const float ScaleDiag = 0.03348292f;
        int width = BlocksX;
        int height = BlocksY;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        double scaleInv = (double)_global.GlobalScale * _global.QuantLf;
        float[] lfScale = [(float)(512.0 * _lfDequant[0] / scaleInv), (float)(512.0 * _lfDequant[1] / scaleInv), (float)(512.0 * _lfDequant[2] / scaleInv)];
        float[][] ud = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] g = _lf[c];
            ud[c] = new float[width * (height - 2)];
            for (int y = 0; y < height - 2; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    ud[c][(y * width) + x] = g[(y * width) + x] + g[((y + 2) * width) + x];
                }
            }
        }

        Span<float> self = stackalloc float[3];
        Span<float> wa = stackalloc float[3];
        Span<float> prev = stackalloc float[3];
        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            int urow = (y - 1) * width;
            for (int c = 0; c < 3; c++)
            {
                prev[c] = _lf[c][row];
            }

            for (int x = 1; x < width - 1; x++)
            {
                float gap = 0.5f;
                for (int c = 0; c < 3; c++)
                {
                    float[] g = _lf[c];
                    float s = g[row + x];
                    float side = prev[c] + g[row + x + 1] + ud[c][urow + x];
                    float diag = ud[c][urow + x - 1] + ud[c][urow + x + 1];
                    self[c] = s;
                    wa[c] = (s * ScaleSelf) + (side * ScaleSide) + (diag * ScaleDiag);
                    gap = MathF.Max(gap, MathF.Abs(wa[c] - s) / lfScale[c]);
                }

                float gapScale = MathF.Max(0, 3 - (4 * gap));
                for (int c = 0; c < 3; c++)
                {
                    _lf[c][row + x] = ((wa[c] - self[c]) * gapScale) + self[c];
                    prev[c] = self[c];
                }
            }
        }
    }
}
