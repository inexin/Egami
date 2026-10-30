using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>The settings of one VP8 encode.</summary>
internal sealed record Vp8EncoderOptions
{
    /// <summary>Gets the save to count a still key frame's macroblock rows to, or <see langword="null"/>.</summary>
    internal SaveReporter? Report { get; init; }

    /// <summary>Gets the quality, 0 to 100, on libwebp's curve.</summary>
    internal int Quality { get; init; } = 75;

    /// <summary>Gets the effort, 0 to 6: 0–1 choose 16 × 16 modes by error, 2–3 add 4 × 4 modes, 4 chooses by rate and distortion, 5–6 add trellis quantization.</summary>
    internal int Effort { get; init; } = 4;

    /// <summary>Gets the spatial noise shaping strength, 0 to 100: how much coarser busy segments are quantized than flat ones.</summary>
    internal int NoiseShaping { get; init; } = 50;

    /// <summary>Gets the loop filter strength, 0 to 100.</summary>
    internal int FilterStrength { get; init; } = 60;

    /// <summary>Gets the number of segments, 1 to 4.</summary>
    internal int Segments { get; init; } = 4;

    /// <summary>Gets the number of token partitions, 1, 2, 4 or 8; macroblock rows take turns. One is smallest; more let decoders work in parallel.</summary>
    internal int Partitions { get; init; } = 1;

    /// <summary>Gets a quantizer index, 0 to 127, that replaces the quality's and turns segments off; null to use the quality.</summary>
    internal int? Quantizer { get; init; }

    /// <summary>
    /// Gets whether the frame belongs to a video stream: its probabilities carry over to the next frame (unless
    /// <see cref="RefreshEntropy"/> is false), key frames set the loop filter's reference and mode deltas, and the frame is not
    /// padded to an even size.
    /// </summary>
    internal bool Video { get; init; }

    /// <summary>Gets whether a video frame's probabilities carry over to the next frame; false is error resilient.</summary>
    internal bool RefreshEntropy { get; init; } = true;

    /// <summary>Gets whether the frame is shown; a hidden video frame only updates the references.</summary>
    internal bool Show { get; init; } = true;

    /// <summary>Gets a loop filter level, 0 to 63, that replaces the one the encoder works out; null to work it out.</summary>
    internal int? FilterLevel { get; init; }
}

/// <summary>
/// 4:2:0 planes for the VP8 encoder, padded to whole macroblocks by repeating the last column and row: luma
/// <see cref="Stride"/> × <see cref="PaddedHeight"/>, chroma half that each way.
/// </summary>
internal sealed class Vp8Planes(int width, int height)
{
    internal int Width { get; } = width;

    internal int Height { get; } = height;

    internal int Stride { get; } = ((width + 15) >> 4) * 16;

    internal int PaddedHeight { get; } = ((height + 15) >> 4) * 16;

    internal int ChromaStride => Stride >> 1;

    internal byte[] Y { get; } = new byte[((width + 15) >> 4) * 16 * ((height + 15) >> 4) * 16];

    internal byte[] U { get; } = new byte[((width + 15) >> 4) * 8 * ((height + 15) >> 4) * 8];

    internal byte[] V { get; } = new byte[((width + 15) >> 4) * 8 * ((height + 15) >> 4) * 8];

    /// <summary>Repeats the last real column and row of each plane into the padding.</summary>
    internal void Pad()
    {
        PadPlane(Y, Stride, PaddedHeight, Width, Height);
        PadPlane(U, ChromaStride, PaddedHeight >> 1, (Width + 1) >> 1, (Height + 1) >> 1);
        PadPlane(V, ChromaStride, PaddedHeight >> 1, (Width + 1) >> 1, (Height + 1) >> 1);
    }

    private static void PadPlane(byte[] plane, int stride, int rows, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            plane.AsSpan((y * stride) + w, stride - w).Fill(plane[(y * stride) + w - 1]);
        }

        for (int y = h; y < rows; y++)
        {
            plane.AsSpan((h - 1) * stride, stride).CopyTo(plane.AsSpan(y * stride, stride));
        }
    }
}

/// <summary>
/// The VP8 key-frame encoder (RFC 6386), after libwebp's design (BSD; see THIRD-PARTY-NOTICES): macroblocks are sorted into
/// segments by how busy they are, each segment gets a quantizer on libwebp's quality curve, modes are chosen per macroblock by
/// error or by rate and distortion, and tokens are recorded for the whole frame so the probabilities can be chosen before the
/// first token is written.
/// </summary>
[SkipLocalsInit]
internal sealed partial class Vp8Encoder
{
    private const int S = Vp8Dsp.Stride;
    private const int UOff = 16;
    private const int VOff = 24;
    private const int C0 = S + 8;
    private const int MaxPartition0 = (1 << 19) - 1;

    private static readonly int[] ScanY = BuildScanY();
    private static readonly int[] ScanUv = [UOff, UOff + 4, UOff + (4 * S), UOff + (4 * S) + 4, VOff, VOff + 4, VOff + (4 * S), VOff + (4 * S) + 4];

    private readonly Vp8Planes _src;
    private Vp8EncoderOptions _options;
    private readonly int _mbw;
    private readonly int _mbh;
    private readonly Segment[] _dqm = [new(), new(), new(), new()];
    private int _segmentCount;
    private bool _updateMap;
    private readonly byte[] _segmentProbs = [255, 255, 255];
    private int _baseQuant;
    private int _dqUvAc;
    private int _dqUvDc;
    private int _uvAlpha;
    private int _filterLevel;
    private int _maxI4HeaderBits = 256 * 16 * 16;
    private readonly long _mbHeaderLimit;

    // Per macroblock: segment, 16 × 16 flag and mode, chroma mode, 4 × 4 modes, all-zero flag, susceptibility.
    private readonly byte[] _segment;
    private readonly bool[] _isI16;
    private readonly byte[] _yMode;
    private readonly byte[] _uvMode;
    private readonly byte[] _modes;
    private readonly bool[] _zero;
    private readonly byte[] _alpha;

    public Vp8Encoder(Vp8Planes source, Vp8EncoderOptions options)
    {
        _src = source;
        _options = options;
        _mbw = source.Stride >> 4;
        _mbh = source.PaddedHeight >> 4;
        int count = _mbw * _mbh;
        _segment = new byte[count];
        _isI16 = new bool[count];
        _yMode = new byte[count];
        _uvMode = new byte[count];
        _modes = new byte[count * 16];
        _zero = new bool[count];
        _alpha = new byte[count];
        _mbHeaderLimit = 256L * 510 * 8 * 1024 / count;
        _topY = new byte[_mbw * 16];
        _topU = new byte[_mbw * 8];
        _topV = new byte[_mbw * 8];
        _topNz = new byte[_mbw * 9];
        _modesAbove = new byte[_mbw * 4];
        _topDerr = new sbyte[_mbw * 4];
        _tokenStart = new int[count + 1];
    }

    private int Effort => _options.Effort;

    /// <summary>Encodes the planes and returns the payload of a VP8 chunk.</summary>
    internal byte[] Encode() => Encode(_options);

    /// <summary>
    /// Encodes the planes as a key frame with <paramref name="options"/> and returns the frame; a stream's encoder is kept and
    /// called again for each of its key frames.
    /// </summary>
    internal byte[] Encode(Vp8EncoderOptions options)
    {
        _options = options;
        _inter = null;
        Vp8Tables.DefaultCoeffProbs.CopyTo(_baseProbs);
        _maxI4HeaderBits = 256 * 16 * 16;
        foreach (Segment m in _dqm)
        {
            m.Alpha = 0;
            m.Beta = 0;
        }

        _segmentCount = options.Quantizer is null ? Math.Clamp(_options.Segments, 1, 4) : 1;
        if (_segmentCount > 1 && Effort > 0)
        {
            Analyze();
        }
        else
        {
            _segmentCount = 1;
            _uvAlpha = MidAlpha;
            Array.Clear(_segment);
        }

        SetSegmentParams();
        SetSegmentProbabilities();
        options.Report?.Plan(_mbh);
        try
        {
            return EncodeFrame();
        }
        finally
        {
            ReleaseTokens();
        }
    }

    private byte[] EncodeFrame()
    {
        while (true)
        {
            if (_inter is null)
            {
                MainLoop();
            }
            else
            {
                InterLoop();
            }

            FinalizeProbabilities();
            AdjustFilterStrength();
            using Vp8BoolEncoder first = _inter is null ? WritePartition0() : WriteInterPartition0();
            ReadOnlySpan<byte> p0 = first.Finish();
            if (p0.Length > MaxPartition0)
            {
                if (_maxI4HeaderBits == 0)
                {
                    throw new NotSupportedException("The image needs more mode data than one VP8 frame header holds; encode it lossless or smaller.");
                }

                _maxI4HeaderBits >>= 1;
                continue;
            }

            Vp8BoolEncoder[] partitions = EmitTokens();
            try
            {
                _parts = new byte[partitions.Length][];
                for (int i = 0; i < partitions.Length; i++)
                {
                    _parts[i] = partitions[i].Finish().ToArray();
                }
            }
            finally
            {
                foreach (Vp8BoolEncoder partition in partitions)
                {
                    partition.Dispose();
                }
            }

            return Assemble(p0, _parts);
        }
    }

    private int PartitionCount => _options.Partitions is 2 or 4 or 8 ? _options.Partitions : 1;

    // The token partitions of the frame encoded last, kept so its header can be written again with another filter level.
    private byte[][] _parts = [];

    /// <summary>
    /// Writes the frame encoded last again with loop filter level <paramref name="level"/>; only for a frame without segments, an
    /// inter frame or a key frame with a fixed quantizer.
    /// </summary>
    internal byte[] WithFilterLevel(int level)
    {
        _filterLevel = level;
        for (int s = 0; s < 4; s++)
        {
            _dqm[s].FStrength = level;
        }

        using Vp8BoolEncoder first = _inter is null ? WritePartition0() : WriteInterPartition0();
        return Assemble(first.Finish(), _parts);
    }

    /// <summary>Gets the loop filter level of the frame encoded last.</summary>
    internal int FilterLevel => _filterLevel;

    /// <summary>Gets the base quantizer index of the frame encoded last.</summary>
    internal int BaseQuantizer => _baseQuant;

    private byte[] Assemble(ReadOnlySpan<byte> p0, byte[][] parts)
    {
        bool key = _inter is null;
        int header = key ? 10 : 3;
        int total = header + p0.Length + (3 * (parts.Length - 1));
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        // An even size, as libwebp writes it: some readers lose their place after an odd VP8 chunk's padding byte. A video frame
        // has no chunk.
        byte[] frame = new byte[_options.Video ? total : total + (total & 1)];
        uint tag = ((uint)p0.Length << 5) | (_options.Show ? 1u << 4 : 0) | (key ? 0u : 1u);
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        if (key)
        {
            frame[3] = 0x9D;
            frame[4] = 0x01;
            frame[5] = 0x2A;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)_src.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)_src.Height);
        }

        p0.CopyTo(frame.AsSpan(header));
        int at = header + p0.Length;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            frame[at++] = (byte)parts[i].Length;
            frame[at++] = (byte)(parts[i].Length >> 8);
            frame[at++] = (byte)(parts[i].Length >> 16);
        }

        foreach (byte[] part in parts)
        {
            part.CopyTo(frame.AsSpan(at));
            at += part.Length;
        }

        return frame;
    }

    private static int[] BuildScanY()
    {
        int[] scan = new int[16];
        for (int n = 0; n < 16; n++)
        {
            scan[n] = ((n & 3) * 4) + ((n >> 2) * 4 * S);
        }

        return scan;
    }

    // --- Analysis ----------------------------------------------------------------------------------------------------------

    private const int MaxAlpha = 255;
    private const int MidAlpha = 64;
    private const int MinUvAlpha = 30;
    private const int MaxUvAlpha = 100;

    /// <summary>
    /// Measures each macroblock's susceptibility from the DCT histogram of its DC and TrueMotion residuals (luma and chroma,
    /// against the source's own neighbours), then sorts the macroblocks into segments by k-means over those values.
    /// </summary>
    private void Analyze()
    {
        Span<int> alphas = stackalloc int[MaxAlpha + 1];
        alphas.Clear();
        long uvSum = 0;
        int[] lumaBlocks = ScanY;
        int[] chromaBlocks = ScanUv;
        for (int mby = 0; mby < _mbh; mby++)
        {
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                Import(mbx, mby);
                SourceContext(mbx, mby);
                MakePredictions(mbx, mby);
                int best = -1;
                foreach (int mode in (ReadOnlySpan<int>)[0, 3])
                {
                    best = Math.Max(best, Vp8EncDsp.Alpha(_yuvIn, _predY16[mode], lumaBlocks));
                }

                int bestUv = -1;
                foreach (int mode in (ReadOnlySpan<int>)[0, 3])
                {
                    bestUv = Math.Max(bestUv, Vp8EncDsp.Alpha(_yuvIn, _predUv[mode], chromaBlocks));
                }

                int mixed = ((3 * best) + bestUv + 2) >> 2;
                int final = Math.Clamp(MaxAlpha - mixed, 0, MaxAlpha);
                alphas[final]++;
                _alpha[(mby * _mbw) + mbx] = (byte)final;
                uvSum += bestUv;
            }
        }

        _uvAlpha = (int)(uvSum / (_mbw * _mbh));
        AssignSegments(alphas);
    }

    private void AssignSegments(ReadOnlySpan<int> alphas)
    {
        int nb = _segmentCount;
        Span<int> centers = stackalloc int[4];
        Span<int> map = stackalloc int[MaxAlpha + 1];
        Span<int> accum = stackalloc int[4];
        Span<int> distAccum = stackalloc int[4];
        int minA = 0;
        while (minA <= MaxAlpha && alphas[minA] == 0)
        {
            minA++;
        }

        int maxA = MaxAlpha;
        while (maxA > minA && alphas[maxA] == 0)
        {
            maxA--;
        }

        int range = maxA - minA;
        for (int k = 0, n = 1; k < nb; k++, n += 2)
        {
            centers[k] = minA + (n * range / (2 * nb));
        }

        int weightedAverage = 0;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            accum.Clear();
            distAccum.Clear();
            int n = 0;
            for (int a = minA; a <= maxA; a++)
            {
                if (alphas[a] != 0)
                {
                    while (n + 1 < nb && Math.Abs(a - centers[n + 1]) < Math.Abs(a - centers[n]))
                    {
                        n++;
                    }

                    map[a] = n;
                    distAccum[n] += a * alphas[a];
                    accum[n] += alphas[a];
                }
            }

            int displaced = 0, total = 0;
            weightedAverage = 0;
            for (n = 0; n < nb; n++)
            {
                if (accum[n] != 0)
                {
                    int center = (distAccum[n] + (accum[n] / 2)) / accum[n];
                    displaced += Math.Abs(centers[n] - center);
                    centers[n] = center;
                    weightedAverage += center * accum[n];
                    total += accum[n];
                }
            }

            weightedAverage = (weightedAverage + (total / 2)) / total;
            if (displaced < 5)
            {
                break;
            }
        }

        for (int i = 0; i < _alpha.Length; i++)
        {
            _segment[i] = (byte)map[_alpha[i]];
        }

        int min = centers[0], max = centers[0];
        for (int n = 1; n < nb; n++)
        {
            min = Math.Min(min, centers[n]);
            max = Math.Max(max, centers[n]);
        }

        if (max == min)
        {
            max = min + 1;
        }

        for (int n = 0; n < nb; n++)
        {
            _dqm[n].Alpha = Math.Clamp(255 * (centers[n] - weightedAverage) / (max - min), -127, 127);
            _dqm[n].Beta = Math.Clamp(255 * (centers[n] - min) / (max - min), 0, 255);
        }
    }

    // --- Quantizers and filter strengths -----------------------------------------------------------------------------------

    private static readonly ushort[] AcTable2 = BuildAcTable2();

    /// <summary>Maps the quality to a quantizer per segment, modulated by each segment's susceptibility, as libwebp does.</summary>
    private void SetSegmentParams()
    {
        double amp = 0.9 * _options.NoiseShaping / 100.0 / 128.0;
        double q100 = _options.Quality / 100.0;
        double linear = q100 < 0.75 ? q100 * (2.0 / 3.0) : (2.0 * q100) - 1.0;
        double cBase = Math.Cbrt(linear);
        for (int i = 0; i < _segmentCount; i++)
        {
            double expn = 1.0 - (amp * _dqm[i].Alpha);
            double c = Math.Pow(cBase, expn);
            _dqm[i].Quant = Math.Clamp((int)(127.0 * (1.0 - c)), 0, 127);
        }

        if (_options.Quantizer is int fixedQuant)
        {
            _dqm[0].Quant = Math.Clamp(fixedQuant, 0, 127);
        }

        _baseQuant = _dqm[0].Quant;
        for (int i = _segmentCount; i < 4; i++)
        {
            _dqm[i].Quant = _baseQuant;
        }

        int dqUvAc = (_uvAlpha - MidAlpha) * (6 - (-4)) / (MaxUvAlpha - MinUvAlpha);
        dqUvAc = dqUvAc * _options.NoiseShaping / 100;
        _dqUvAc = Math.Clamp(dqUvAc, -4, 6);
        _dqUvDc = Math.Clamp(-4 * _options.NoiseShaping / 100, -15, 15);

        // Filter strengths: from the AC step, weaker for the flatter segments.
        int level0 = 5 * _options.FilterStrength;
        for (int i = 0; i < 4; i++)
        {
            Segment m = _dqm[i];
            int qstep = Vp8Tables.AcTable[m.Quant] >> 2;
            int baseStrength = FilterStrengthFromDelta(qstep);
            int f = baseStrength * level0 / (256 + m.Beta);
            m.FStrength = f < 2 ? 0 : Math.Min(f, 63);
        }

        if (_segmentCount > 1)
        {
            SimplifySegments();
        }

        SetupMatrices();
    }

    /// <summary>Merges segments with the same quantizer and filter strength.</summary>
    private void SimplifySegments()
    {
        Span<int> map = [0, 1, 2, 3];
        int final = 1;
        for (int s1 = 1; s1 < _segmentCount; s1++)
        {
            int s2 = 0;
            for (; s2 < final; s2++)
            {
                if (_dqm[s1].Quant == _dqm[s2].Quant && _dqm[s1].FStrength == _dqm[s2].FStrength)
                {
                    break;
                }
            }

            map[s1] = s2;
            if (s2 == final)
            {
                if (final != s1)
                {
                    _dqm[final].CopyFrom(_dqm[s1]);
                }

                final++;
            }
        }

        if (final < _segmentCount)
        {
            for (int i = 0; i < _segment.Length; i++)
            {
                _segment[i] = (byte)map[_segment[i]];
            }

            for (int i = final; i < 4; i++)
            {
                _dqm[i].CopyFrom(_dqm[final - 1]);
            }

            _segmentCount = final;
        }
    }

    private void SetupMatrices()
    {
        int tlambdaScale = Effort >= 4 ? _options.NoiseShaping : 0;
        for (int i = 0; i < 4; i++)
        {
            Segment m = _dqm[i];
            int q = m.Quant;
            int qI4 = m.Y1.Set(Vp8Tables.DcTable[q], Vp8Tables.AcTable[q], 0);
            int qI16 = m.Y2.Set(Vp8Tables.DcTable[q] * 2, AcTable2[q], 1);
            int qUv = m.Uv.Set(Vp8Tables.DcTable[Math.Clamp(q + _dqUvDc, 0, 117)], Vp8Tables.AcTable[Math.Clamp(q + _dqUvAc, 0, 127)], 2);
            m.LambdaI4 = Math.Max(1, (3 * qI4 * qI4) >> 7);
            m.LambdaI16 = Math.Max(1, 3 * qI16 * qI16);
            m.LambdaUv = Math.Max(1, (3 * qUv * qUv) >> 6);
            m.LambdaMode = Math.Max(1, (qI4 * qI4) >> 7);
            m.LambdaTrellisI4 = Math.Max(1, (7 * qI4 * qI4) >> 3);
            m.LambdaTrellisI16 = Math.Max(1, (qI16 * qI16) >> 2);
            m.LambdaTrellisUv = Math.Max(1, (qUv * qUv) << 1);
            m.TLambda = Math.Max(1, (tlambdaScale * qI4) >> 5);
            if (tlambdaScale == 0)
            {
                m.TLambda = 0;
            }

            m.MinDisto = 20 * m.Y1.Q[0];
            m.MaxEdge = 0;
            m.I4Penalty = 1000L * qI4 * qI4;
        }
    }

    /// <summary>The smallest filter level that smooths an edge step of <paramref name="delta"/>: at sharpness 0, which Egami always writes, the step itself.</summary>
    private static int FilterStrengthFromDelta(int delta) => Math.Min(delta, 63);

    /// <summary>Raises each segment's filter level to smooth the steps left between blocky macroblocks.</summary>
    private void AdjustFilterStrength()
    {
        int maxLevel = 0;
        if (_inter is not null)
        {
            // An inter frame starts from the quantizer's level alone: the edges of its intra macroblocks say little about it.
            maxLevel = _dqm[0].FStrength;
        }
        else if (_options.FilterStrength > 0)
        {
            for (int s = 0; s < _segmentCount; s++)
            {
                Segment m = _dqm[s];
                int delta = (m.MaxEdge * m.Y2.Q[1]) >> 3;
                m.FStrength = Math.Max(m.FStrength, FilterStrengthFromDelta(delta));
                maxLevel = Math.Max(maxLevel, m.FStrength);
            }
        }

        _filterLevel = maxLevel;
        if (_options.FilterLevel is int level)
        {
            for (int s = 0; s < 4; s++)
            {
                _dqm[s].FStrength = level;
            }

            _filterLevel = level;
        }
    }

    private static ushort[] BuildAcTable2()
    {
        ushort[] table = new ushort[128];
        for (int i = 0; i < 128; i++)
        {
            table[i] = (ushort)Math.Max(8, (Vp8Tables.AcTable[i] * 101581) >> 16);
        }

        return table;
    }

    private void SetSegmentProbabilities()
    {
        if (_segmentCount <= 1)
        {
            _updateMap = false;
            return;
        }

        Span<int> p = stackalloc int[4];
        p.Clear();
        foreach (byte s in _segment)
        {
            p[s]++;
        }

        _segmentProbs[0] = Probability(p[0] + p[1], p[2] + p[3]);
        _segmentProbs[1] = Probability(p[0], p[1]);
        _segmentProbs[2] = Probability(p[2], p[3]);
        _updateMap = _segmentProbs[0] != 255 || _segmentProbs[1] != 255 || _segmentProbs[2] != 255;
        if (!_updateMap)
        {
            Array.Clear(_segment);
        }

        static byte Probability(int a, int b) => (byte)(a + b == 0 ? 255 : ((255 * a) + ((a + b) / 2)) / (a + b));
    }

    // --- Writing -----------------------------------------------------------------------------------------------------------

    private Vp8BoolEncoder WritePartition0()
    {
        Vp8BoolEncoder bw = new(Math.Max(1024, _segment.Length));
        bw.PutBitUniform(0); // color space
        bw.PutBitUniform(0); // clamping type
        if (bw.PutBitUniform(_segmentCount > 1 ? 1 : 0) != 0)
        {
            bw.PutBitUniform(_updateMap ? 1 : 0);
            bw.PutBitUniform(1); // update the segment data
            bw.PutBitUniform(1); // absolute values
            for (int s = 0; s < 4; s++)
            {
                bw.PutOptionalSigned(_dqm[s].Quant, 7);
            }

            for (int s = 0; s < 4; s++)
            {
                bw.PutOptionalSigned(_dqm[s].FStrength, 6);
            }

            if (_updateMap)
            {
                for (int s = 0; s < 3; s++)
                {
                    if (bw.PutBitUniform(_segmentProbs[s] != 255 ? 1 : 0) != 0)
                    {
                        bw.PutValue(_segmentProbs[s], 8);
                    }
                }
            }
        }

        WriteFilterHeader(bw, key: true);
        bw.PutValue(int.Log2(PartitionCount), 2);
        WriteQuantizers(bw);
        bw.PutBitUniform(_options.Video && _options.RefreshEntropy ? 1 : 0); // refresh entropy probabilities
        WriteCoefficientProbabilities(bw);
        WriteModes(bw);
        return bw;
    }

    // The loop filter deltas of a video stream, as libvpx sets them: intra macroblocks and the golden and alternate references
    // filter a little harder or softer, and by mode B_PRED, ZERO, the other vectors and SPLIT.
    private static ReadOnlySpan<sbyte> ReferenceFilterDeltas => [2, 0, -2, -2];

    private static ReadOnlySpan<sbyte> ModeFilterDeltas => [4, -2, 2, 4];

    /// <summary>Writes the filter type, level and sharpness, and in a video stream the deltas (a key frame sets them, later frames keep them).</summary>
    private void WriteFilterHeader(Vp8BoolEncoder bw, bool key)
    {
        bw.PutBitUniform(0); // normal filter
        bw.PutValue(_filterLevel, 6);
        bw.PutValue(0, 3); // sharpness
        if (bw.PutBitUniform(_options.Video ? 1 : 0) != 0 && bw.PutBitUniform(key ? 1 : 0) != 0)
        {
            foreach (sbyte delta in ReferenceFilterDeltas)
            {
                bw.PutOptionalSigned(delta, 6);
            }

            foreach (sbyte delta in ModeFilterDeltas)
            {
                bw.PutOptionalSigned(delta, 6);
            }
        }
    }

    private void WriteQuantizers(Vp8BoolEncoder bw)
    {
        bw.PutValue(_baseQuant, 7);
        bw.PutOptionalSigned(0, 4); // y1 dc
        bw.PutOptionalSigned(0, 4); // y2 dc
        bw.PutOptionalSigned(0, 4); // y2 ac
        bw.PutOptionalSigned(_dqUvDc, 4);
        bw.PutOptionalSigned(_dqUvAc, 4);
    }

    /// <summary>Writes the coefficient probability updates against the frame's starting probabilities, and the skip probability.</summary>
    private void WriteCoefficientProbabilities(Vp8BoolEncoder bw)
    {
        ReadOnlySpan<byte> update = Vp8Tables.CoeffUpdateProbs;
        for (int i = 0; i < Vp8Costs.ProbabilityCount; i++)
        {
            if (bw.PutBit(_finalProbs[i] != _baseProbs[i] ? 1 : 0, update[i]) != 0)
            {
                bw.PutValue(_finalProbs[i], 8);
            }
        }

        if (bw.PutBitUniform(_useSkip ? 1 : 0) != 0)
        {
            bw.PutValue(_skipProb, 8);
        }
    }

    private void WriteModes(Vp8BoolEncoder bw)
    {
        Span<byte> above = stackalloc byte[_mbw * 4];
        Span<byte> left = stackalloc byte[4];
        above.Clear();
        ReadOnlySpan<byte> probs = Vp8Tables.BModeProbs;
        for (int mby = 0; mby < _mbh; mby++)
        {
            left.Clear();
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                int mb = (mby * _mbw) + mbx;
                if (_updateMap)
                {
                    int s = _segment[mb];
                    if (bw.PutBit(s >= 2 ? 1 : 0, _segmentProbs[0]) != 0)
                    {
                        bw.PutBit(s & 1, _segmentProbs[2]);
                    }
                    else
                    {
                        bw.PutBit(s & 1, _segmentProbs[1]);
                    }
                }

                if (_useSkip)
                {
                    bw.PutBit(_zero[mb] ? 1 : 0, _skipProb);
                }

                Span<byte> top = above.Slice(mbx * 4, 4);
                if (bw.PutBit(_isI16[mb] ? 1 : 0, 145) != 0)
                {
                    int m = _yMode[mb];
                    if (bw.PutBit(m >= 2 ? 1 : 0, 156) != 0)
                    {
                        bw.PutBit(m == 3 ? 1 : 0, 128);
                    }
                    else
                    {
                        bw.PutBit(m == 1 ? 1 : 0, 163);
                    }

                    byte implied = ImpliedSubMode(m);
                    top.Fill(implied);
                    left.Fill(implied);
                }
                else
                {
                    ReadOnlySpan<byte> modes = _modes.AsSpan(mb * 16, 16);
                    for (int y = 0; y < 4; y++)
                    {
                        int l = left[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int mode = modes[(y * 4) + x];
                            int at = ((top[x] * 10) + l) * 9;
                            foreach ((int node, int bit) in Vp8Costs.Intra4Paths[mode])
                            {
                                bw.PutBit(bit, probs[at + node]);
                            }

                            top[x] = (byte)mode;
                            l = mode;
                        }

                        left[y] = (byte)l;
                    }
                }

                int uv = _uvMode[mb];
                if (bw.PutBit(uv != 0 ? 1 : 0, 142) != 0 && bw.PutBit(uv != 1 ? 1 : 0, 114) != 0)
                {
                    bw.PutBit(uv == 3 ? 1 : 0, 183);
                }
            }
        }
    }

    private static byte ImpliedSubMode(int mode) => mode switch { 0 => Vp8Dsp.BDc, 1 => Vp8Dsp.BVe, 2 => Vp8Dsp.BHe, _ => Vp8Dsp.BTm };

    /// <summary>One segment's quantizer, filter strength and the rate-distortion weights that follow from them.</summary>
    private sealed class Segment
    {
        internal int Quant { get; set; }

        internal int FStrength { get; set; }

        internal int Alpha { get; set; }

        internal int Beta { get; set; }

        internal Vp8Matrix Y1 { get; private set; } = new();

        internal Vp8Matrix Y2 { get; private set; } = new();

        internal Vp8Matrix Uv { get; private set; } = new();

        internal int LambdaI4 { get; set; }

        internal int LambdaI16 { get; set; }

        internal int LambdaUv { get; set; }

        internal int LambdaMode { get; set; }

        internal int LambdaTrellisI4 { get; set; }

        internal int LambdaTrellisI16 { get; set; }

        internal int LambdaTrellisUv { get; set; }

        internal int TLambda { get; set; }

        internal int MinDisto { get; set; }

        internal int MaxEdge { get => _maxEdge; set => _maxEdge = value; }

        private int _maxEdge;

        /// <summary>Raises <see cref="MaxEdge"/> to <paramref name="value"/>; safe from several threads.</summary>
        internal void RaiseMaxEdge(int value)
        {
            int seen = Volatile.Read(ref _maxEdge);
            while (value > seen)
            {
                int was = Interlocked.CompareExchange(ref _maxEdge, value, seen);
                if (was == seen)
                {
                    return;
                }

                seen = was;
            }
        }

        internal long I4Penalty { get; set; }

        internal void CopyFrom(Segment other)
        {
            Quant = other.Quant;
            FStrength = other.FStrength;
            Alpha = other.Alpha;
            Beta = other.Beta;
        }
    }
}
