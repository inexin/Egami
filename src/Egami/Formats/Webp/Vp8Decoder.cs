using System.Buffers;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Decodes one VP8 key frame (RFC 6386), the payload of a WebP VP8 chunk, into 4:2:0 planes a macroblock row at a time: the frame
/// header, segments, intra modes, coefficient tokens over 1 to 8 partitions, the inverse transforms and the loop filter. A row is
/// final once the row below it has been filtered, so the decoder holds about two macroblock rows of planes. The state a stream of
/// frames needs (probabilities, segment map, filter deltas, reference slots) lives in <see cref="Vp8StreamState"/>; this decoder
/// only reads key frames, and an inter frame throws <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class Vp8Decoder : IDisposable
{
    /// <summary>The rows above a macroblock row that its loop filter may change: kept back until that row is filtered.</summary>
    private const int Tail = 8;

    private const int ChromaTail = 4;

    private static readonly byte[] Bands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];
    private static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    private static readonly byte[] Cat3 = [173, 148, 140];
    private static readonly byte[] Cat4 = [176, 155, 140, 135];
    private static readonly byte[] Cat5 = [180, 157, 141, 134, 130];
    private static readonly byte[] Cat6 = [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    // The 4 × 4 mode tree of RFC 6386 section 11.2, as pairs of children: positive is the next pair, otherwise minus the mode.
    private static readonly sbyte[] BModeTree = [-Vp8Dsp.BDc, 2, -Vp8Dsp.BTm, 4, -Vp8Dsp.BVe, 6, 8, 12, -Vp8Dsp.BHe, 10, -Vp8Dsp.BRd, -Vp8Dsp.BVr, -Vp8Dsp.BLd, 14, -Vp8Dsp.BVl, 16, -Vp8Dsp.BHd, -Vp8Dsp.BHu];

    private readonly byte[] _data;
    private readonly Vp8StreamState _state;
    private Vp8BoolDecoder _modes;
    private readonly Vp8BoolDecoder[] _partitions;
    private readonly int _partitionMask;

    // Frame header.
    private readonly bool _segmentation;
    private readonly bool _updateMap;
    private readonly bool _useSkip;
    private readonly int _skipProbability;
    private readonly int _filterType;
    private readonly short[] _quant = new short[4 * 6];
    private readonly FilterInfo[] _filterInfo = new FilterInfo[8];

    // Contexts: per column of the row above and per row of the macroblock to the left.
    private readonly byte[] _modesAbove;
    private readonly byte[] _modesLeft = new byte[4];
    private readonly byte[] _nzAbove;
    private readonly byte[] _nzLeft = new byte[9];

    // Reconstruction: the work buffers with their borders, and the unfiltered bottom rows of the macroblock row above.
    private readonly byte[] _workY = new byte[Vp8Dsp.Stride * 17];
    private readonly byte[] _workU = new byte[Vp8Dsp.Stride * 9];
    private readonly byte[] _workV = new byte[Vp8Dsp.Stride * 9];
    private readonly byte[] _topY;
    private readonly byte[] _topU;
    private readonly byte[] _topV;
    private readonly short[] _coefficients = new short[384];
    private readonly byte[] _codes = new byte[24];
    private readonly byte[] _subModes = new byte[16];

    // The planes of the rows in progress: the tail of the row above, then the current macroblock row.
    private readonly byte[] _rowsY;
    private readonly byte[] _rowsU;
    private readonly byte[] _rowsV;
    private readonly MacroblockFilter[] _rowFilter;
    private Snapshot? _snapshot;
    private bool _disposed;

    private Vp8Decoder(byte[] data, int offset, int length, Vp8StreamState state, Vp8FrameHeader header)
    {
        _data = data;
        _state = state;
        Width = header.Width;
        Height = header.Height;
        MbWidth = (Width + 15) >> 4;
        MbHeight = (Height + 15) >> 4;

        int firstStart = offset + 10;
        _modes = new Vp8BoolDecoder(data, firstStart, header.FirstPartitionSize);
        ref Vp8BoolDecoder br = ref _modes;
        br.GetValue(1); // color space
        br.GetValue(1); // clamping type: Egami always clamps
        _segmentation = br.GetValue(1) != 0;
        if (_segmentation)
        {
            _updateMap = br.GetValue(1) != 0;
            if (br.GetValue(1) != 0)
            {
                state.AbsoluteSegmentValues = br.GetValue(1) != 0;
                for (int s = 0; s < 4; s++)
                {
                    state.SegmentQuantizer[s] = br.GetValue(1) != 0 ? br.GetSignedValue(7) : 0;
                }

                for (int s = 0; s < 4; s++)
                {
                    state.SegmentFilterLevel[s] = br.GetValue(1) != 0 ? br.GetSignedValue(6) : 0;
                }
            }

            if (_updateMap)
            {
                for (int s = 0; s < 3; s++)
                {
                    state.SegmentProbabilities[s] = (byte)(br.GetValue(1) != 0 ? br.GetValue(8) : 255);
                }
            }
        }

        bool simple = br.GetValue(1) != 0;
        int level = br.GetValue(6);
        int sharpness = br.GetValue(3);
        bool useDeltas = br.GetValue(1) != 0;
        if (useDeltas && br.GetValue(1) != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (br.GetValue(1) != 0)
                {
                    state.ReferenceFilterDeltas[i] = br.GetSignedValue(6);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (br.GetValue(1) != 0)
                {
                    state.ModeFilterDeltas[i] = br.GetSignedValue(6);
                }
            }
        }

        // libwebp, the reference for what a WebP looks like, turns the filter off when the frame level is 0.
        _filterType = level == 0 ? 0 : simple ? 1 : 2;

        int partitions = 1 << br.GetValue(2);
        _partitionMask = partitions - 1;
        _partitions = new Vp8BoolDecoder[partitions];
        int tokensStart = firstStart + header.FirstPartitionSize;
        int end = offset + length;
        int sizes = tokensStart;
        int partStart = tokensStart + (3 * (partitions - 1));
        if (partStart > end)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, -1, "The VP8 partition sizes run past the end of the frame.");
        }

        for (int p = 0; p < partitions - 1; p++)
        {
            int size = data[sizes] | (data[sizes + 1] << 8) | (data[sizes + 2] << 16);
            size = Math.Min(size, end - partStart);
            _partitions[p] = new Vp8BoolDecoder(data, partStart, size);
            partStart += size;
            sizes += 3;
        }

        _partitions[partitions - 1] = new Vp8BoolDecoder(data, partStart, end - partStart);

        ParseQuantizers(ref br);
        br.GetValue(1); // refresh entropy probabilities: a still has one frame
        ParseProbabilities(ref br);
        _useSkip = br.GetValue(1) != 0;
        _skipProbability = _useSkip ? br.GetValue(8) : 0;
        if (_filterType > 0)
        {
            ComputeFilterStrengths(level, sharpness, useDeltas);
        }

        _modesAbove = new byte[MbWidth * 4];
        _nzAbove = new byte[MbWidth * 9];
        _topY = new byte[MbWidth * 16];
        _topU = new byte[MbWidth * 8];
        _topV = new byte[MbWidth * 8];
        _rowsY = ArrayPool<byte>.Shared.Rent(StrideY * (Tail + 16));
        _rowsU = ArrayPool<byte>.Shared.Rent(StrideC * (ChromaTail + 8));
        _rowsV = ArrayPool<byte>.Shared.Rent(StrideC * (ChromaTail + 8));
        _rowFilter = new MacroblockFilter[MbWidth];
    }

    /// <summary>Gets the frame width.</summary>
    internal int Width { get; }

    /// <summary>Gets the frame height.</summary>
    internal int Height { get; }

    internal int MbWidth { get; }

    internal int MbHeight { get; }

    /// <summary>Gets the next macroblock row <see cref="DecodeRow"/> decodes.</summary>
    internal int NextMbRow { get; private set; }

    /// <summary>Gets the luma rows final so far: rows before this one have been handed out by <see cref="DecodeRow"/>.</summary>
    internal int RowsDone { get; private set; }

    /// <summary>
    /// Gets or sets how many bytes of the payload have arrived, for an incremental decode; <see cref="int.MaxValue"/> (the default)
    /// once all of it has. A row that reads past it is undone and <see cref="DecodeRow"/> throws
    /// <see cref="Streaming.NeedsInputException"/>, to be called again when more has arrived.
    /// </summary>
    internal int Arrived { get; set; } = int.MaxValue;

    /// <summary>Gets the payload bytes that must have arrived before the first row can be decoded: the frame header and first partition, and the partition sizes.</summary>
    internal static int HeaderBytes(ReadOnlySpan<byte> payload)
    {
        int tag = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        return 10 + (tag >> 5) + 21;
    }

    /// <summary>Gets whether any partition read past its end.</summary>
    internal bool PastEnd
    {
        get
        {
            if (_modes.PastEnd)
            {
                return true;
            }

            foreach (Vp8BoolDecoder p in _partitions)
            {
                if (p.PastEnd)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Returns the bytes the decoder holds for a frame of this width: two macroblock rows of planes and its contexts.</summary>
    internal static long WorkingBytes(int width)
    {
        int mbw = (width + 15) >> 4;
        return ((long)mbw * 16 * (Tail + 16)) + (2L * mbw * 8 * (ChromaTail + 8)) + ((long)mbw * 64);
    }

    private int StrideY => MbWidth * 16;

    private int StrideC => MbWidth * 8;

    /// <summary>Reads a VP8 frame header (RFC 6386 section 9.1) from the first 10 bytes of the payload.</summary>
    internal static Vp8FrameHeader ReadHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, -1, "The VP8 frame header is cut short.");
        }

        int tag = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        if ((tag & 1) != 0)
        {
            throw new NotSupportedException("The VP8 data is an inter frame. WebP holds key frames only; VP8 video belongs to Egami.Video.");
        }

        int version = (tag >> 1) & 7;
        int firstPartition = tag >> 5;
        if (version > 3)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 0, $"The VP8 version {version} is unknown.");
        }

        if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 3, "The VP8 start code is wrong.");
        }

        int width = (payload[6] | (payload[7] << 8)) & 0x3FFF;
        int height = (payload[8] | (payload[9] << 8)) & 0x3FFF;
        if (width == 0 || height == 0)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 6, $"The VP8 image size {width}×{height} is invalid.");
        }

        if (firstPartition > payload.Length - 10)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 0, "The first VP8 partition runs past the end of the frame.");
        }

        return new Vp8FrameHeader(width, height, firstPartition);
    }

    /// <summary>Starts decoding the frame in <paramref name="data"/>[<paramref name="offset"/>..+<paramref name="length"/>].</summary>
    internal static Vp8Decoder Create(byte[] data, int offset, int length, Vp8StreamState? state = null)
    {
        Vp8FrameHeader header = ReadHeader(data.AsSpan(offset, length));
        return new Vp8Decoder(data, offset, length, state ?? new Vp8StreamState(), header);
    }

    /// <summary>
    /// Decodes the next macroblock row and filters it, then calls <paramref name="emit"/> for the rows that became final: luma rows
    /// from <see cref="RowsDone"/> on, with the chroma rows that go with them. Returns false once every row is out.
    /// </summary>
    internal bool DecodeRow(RowSink emit)
    {
        if (RowsDone >= Height)
        {
            return false;
        }

        int mby = NextMbRow;
        if (mby < MbHeight)
        {
            bool incremental = Arrived != int.MaxValue;
            if (incremental)
            {
                _snapshot ??= new Snapshot(this);
                _snapshot.Save(this, mby);
            }

            DecodeMacroblockRow(mby);
            if (incremental && (_modes.Position > Arrived || _partitions[mby & _partitionMask].Position > Arrived))
            {
                // The row read bytes that have not arrived: undo it and wait.
                _snapshot!.Restore(this, mby);
                throw new Streaming.NeedsInputException();
            }

            NextMbRow++;
        }

        // Final now: the tail of the row above, and the current row but its last rows (which the row below may still filter).
        bool last = mby >= MbHeight - 1;
        int lumaEnd = last ? Height : Math.Min(Height, (mby * 16) + 16 - Tail);
        int chromaHeight = (Height + 1) >> 1;
        int chromaEnd = last ? chromaHeight : Math.Min(chromaHeight, (mby * 8) + 8 - ChromaTail);
        int chromaStart = (RowsDone + 1) >> 1;
        emit(this, RowsDone, lumaEnd, chromaStart, chromaEnd);
        RowsDone = lumaEnd;
        if (!last)
        {
            // The current row's last rows become the tail.
            Array.Copy(_rowsY, StrideY * 16, _rowsY, 0, StrideY * Tail);
            Array.Copy(_rowsU, StrideC * 8, _rowsU, 0, StrideC * ChromaTail);
            Array.Copy(_rowsV, StrideC * 8, _rowsV, 0, StrideC * ChromaTail);
        }
        else
        {
            RowsDone = Height;
        }

        return true;
    }

    /// <summary>Gets luma row <paramref name="y"/>, one of the rows the current call to <see cref="RowSink"/> hands out.</summary>
    internal ReadOnlySpan<byte> RowY(int y) => _rowsY.AsSpan(RowIndex(y, 16, Tail) * StrideY, Width);

    /// <summary>Gets chroma row <paramref name="y"/> of U.</summary>
    internal ReadOnlySpan<byte> RowU(int y) => _rowsU.AsSpan(RowIndex(y, 8, ChromaTail) * StrideC, (Width + 1) >> 1);

    /// <summary>Gets chroma row <paramref name="y"/> of V.</summary>
    internal ReadOnlySpan<byte> RowV(int y) => _rowsV.AsSpan(RowIndex(y, 8, ChromaTail) * StrideC, (Width + 1) >> 1);

    /// <summary>Returns where row <paramref name="y"/> sits in the row buffer: the tail of the row above, then the current macroblock row.</summary>
    private int RowIndex(int y, int mbSize, int tail)
    {
        int current = (NextMbRow - 1) * mbSize;
        return y >= current ? tail + (y - current) : tail - (current - y);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_rowsY);
            ArrayPool<byte>.Shared.Return(_rowsU);
            ArrayPool<byte>.Shared.Return(_rowsV);
        }
    }

    private void ParseQuantizers(ref Vp8BoolDecoder br)
    {
        int baseQ = br.GetValue(7);
        int dqy1Dc = br.GetOptionalSigned(4);
        int dqy2Dc = br.GetOptionalSigned(4);
        int dqy2Ac = br.GetOptionalSigned(4);
        int dquvDc = br.GetOptionalSigned(4);
        int dquvAc = br.GetOptionalSigned(4);
        for (int s = 0; s < 4; s++)
        {
            int q = baseQ;
            if (_segmentation)
            {
                q = _state.SegmentQuantizer[s] + (_state.AbsoluteSegmentValues ? 0 : baseQ);
            }

            Span<short> m = _quant.AsSpan(s * 6, 6);
            m[0] = Vp8Tables.DcTable[Math.Clamp(q + dqy1Dc, 0, 127)];
            m[1] = Vp8Tables.AcTable[Math.Clamp(q, 0, 127)];
            m[2] = (short)(Vp8Tables.DcTable[Math.Clamp(q + dqy2Dc, 0, 127)] * 2);

            // x × 155 / 100 for every step in the table, at least 8.
            m[3] = (short)Math.Max(8, (Vp8Tables.AcTable[Math.Clamp(q + dqy2Ac, 0, 127)] * 101581) >> 16);
            m[4] = Vp8Tables.DcTable[Math.Clamp(q + dquvDc, 0, 117)];
            m[5] = Vp8Tables.AcTable[Math.Clamp(q + dquvAc, 0, 127)];
        }
    }

    private void ParseProbabilities(ref Vp8BoolDecoder br)
    {
        Span<byte> probabilities = _state.CoefficientProbabilities;
        ReadOnlySpan<byte> update = Vp8Tables.CoeffUpdateProbs;
        for (int i = 0; i < probabilities.Length; i++)
        {
            if (br.GetBit(update[i]) != 0)
            {
                probabilities[i] = (byte)br.GetValue(8);
            }
        }
    }

    private void ComputeFilterStrengths(int frameLevel, int sharpness, bool useDeltas)
    {
        for (int s = 0; s < 4; s++)
        {
            int baseLevel = frameLevel;
            if (_segmentation)
            {
                baseLevel = _state.SegmentFilterLevel[s] + (_state.AbsoluteSegmentValues ? 0 : frameLevel);
            }

            for (int i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                int level = baseLevel;
                if (useDeltas)
                {
                    level += _state.ReferenceFilterDeltas[0];
                    if (i4x4 != 0)
                    {
                        level += _state.ModeFilterDeltas[0];
                    }
                }

                level = Math.Clamp(level, 0, 63);
                ref FilterInfo info = ref _filterInfo[(s * 2) + i4x4];
                if (level == 0)
                {
                    info = default;
                    continue;
                }

                int interior = level;
                if (sharpness > 0)
                {
                    interior >>= sharpness > 4 ? 2 : 1;
                    interior = Math.Min(interior, 9 - sharpness);
                }

                interior = Math.Max(interior, 1);
                info = new FilterInfo((2 * level) + interior, interior, level >= 40 ? 2 : level >= 15 ? 1 : 0);
            }
        }
    }

    private void DecodeMacroblockRow(int mby)
    {
        ref Vp8BoolDecoder tokens = ref _partitions[mby & _partitionMask];
        Array.Clear(_modesLeft);
        Array.Clear(_nzLeft);

        // The left edge of the row: 129 down the left, and the corner 129 below the first row or 127 in it.
        Span<byte> wy = _workY;
        Span<byte> wu = _workU;
        Span<byte> wv = _workV;
        const int Y0 = Vp8Dsp.Stride + 8;
        const int C0 = Vp8Dsp.Stride + 8;
        for (int j = 0; j < 16; j++)
        {
            wy[Y0 + (j * Vp8Dsp.Stride) - 1] = 129;
        }

        for (int j = 0; j < 8; j++)
        {
            wu[C0 + (j * Vp8Dsp.Stride) - 1] = 129;
            wv[C0 + (j * Vp8Dsp.Stride) - 1] = 129;
        }

        if (mby > 0)
        {
            wy[Y0 - Vp8Dsp.Stride - 1] = wu[C0 - Vp8Dsp.Stride - 1] = wv[C0 - Vp8Dsp.Stride - 1] = 129;
        }
        else
        {
            wy.Slice(Y0 - Vp8Dsp.Stride - 1, 16 + 4 + 1).Fill(127);
            wu.Slice(C0 - Vp8Dsp.Stride - 1, 8 + 1).Fill(127);
            wv.Slice(C0 - Vp8Dsp.Stride - 1, 8 + 1).Fill(127);
        }

        for (int mbx = 0; mbx < MbWidth; mbx++)
        {
            Macroblock mb = ParseModes(mbx);
            bool anyNonZero = false;
            if (!mb.Skip)
            {
                anyNonZero = ParseResiduals(ref tokens, mbx, mb);
            }
            else
            {
                Array.Clear(_codes);
                _nzLeft.AsSpan(0, 8).Clear();
                _nzAbove.AsSpan(mbx * 9, 8).Clear();
                if (!mb.Intra4x4)
                {
                    _nzLeft[8] = 0;
                    _nzAbove[(mbx * 9) + 8] = 0;
                }
            }

            if (_filterType > 0)
            {
                FilterInfo info = _filterInfo[(mb.Segment * 2) + (mb.Intra4x4 ? 1 : 0)];
                _rowFilter[mbx] = new MacroblockFilter(info, mb.Intra4x4 || anyNonZero);
            }

            Reconstruct(mbx, mby, mb);
        }

        if (_filterType > 0)
        {
            for (int mbx = 0; mbx < MbWidth; mbx++)
            {
                FilterMacroblock(mbx, mby);
            }
        }
    }

    private Macroblock ParseModes(int mbx)
    {
        ref Vp8BoolDecoder br = ref _modes;
        int segment = 0;
        if (_updateMap)
        {
            ReadOnlySpan<byte> p = _state.SegmentProbabilities;
            segment = br.GetBit(p[0]) == 0 ? br.GetBit(p[1]) : 2 + br.GetBit(p[2]);
        }

        bool skip = _useSkip && br.GetBit(_skipProbability) != 0;
        Span<byte> above = _modesAbove.AsSpan(mbx * 4, 4);
        bool intra4x4 = br.GetBit(145) == 0;
        int ymode;
        if (!intra4x4)
        {
            ymode = br.GetBit(156) != 0 ? (br.GetBit(128) != 0 ? 3 : 2) : (br.GetBit(163) != 0 ? 1 : 0);

            // The implied 4 × 4 modes of a 16 × 16 macroblock, for its neighbours' contexts.
            byte implied = ymode switch { 0 => Vp8Dsp.BDc, 1 => Vp8Dsp.BVe, 2 => Vp8Dsp.BHe, _ => Vp8Dsp.BTm };
            above.Fill(implied);
            _modesLeft.AsSpan().Fill(implied);
        }
        else
        {
            ymode = -1;
            ReadOnlySpan<byte> probs = Vp8Tables.BModeProbs;
            for (int y = 0; y < 4; y++)
            {
                int left = _modesLeft[y];
                for (int x = 0; x < 4; x++)
                {
                    int at = ((above[x] * 10) + left) * 9;
                    int i = 0;
                    do
                    {
                        i = BModeTree[i + br.GetBit(probs[at + (i >> 1)])];
                    }
                    while (i > 0);
                    left = -i;
                    above[x] = (byte)left;
                    _subModes[(y * 4) + x] = (byte)left;
                }

                _modesLeft[y] = (byte)left;
            }
        }

        int uvmode = br.GetBit(142) == 0 ? 0 : br.GetBit(114) == 0 ? 1 : br.GetBit(183) != 0 ? 3 : 2;
        return new Macroblock(segment, skip, intra4x4, ymode, uvmode);
    }

    /// <summary>Reads a macroblock's coefficients; returns whether any is non-zero, as the loop filter sees it.</summary>
    private bool ParseResiduals(ref Vp8BoolDecoder br, int mbx, Macroblock mb)
    {
        Span<short> coefficients = _coefficients;
        coefficients.Clear();
        Span<byte> nzTop = _nzAbove.AsSpan(mbx * 9, 9);
        Span<byte> nzLeft = _nzLeft;
        ReadOnlySpan<short> q = _quant.AsSpan(mb.Segment * 6, 6);
        bool any = false;
        int first;
        int type;
        if (!mb.Intra4x4)
        {
            Span<short> dc = stackalloc short[16];
            dc.Clear();
            int ctx = nzTop[8] + nzLeft[8];
            int nz = GetCoefficients(ref br, 1, ctx, q[2], q[3], 0, dc);
            nzTop[8] = nzLeft[8] = (byte)(nz > 0 ? 1 : 0);
            if (nz > 1)
            {
                Vp8Dsp.InverseWalshHadamard(dc, coefficients);
            }
            else
            {
                short dc0 = (short)((dc[0] + 3) >> 3);
                for (int i = 0; i < 256; i += 16)
                {
                    coefficients[i] = dc0;
                }
            }

            first = 1;
            type = 0;
        }
        else
        {
            first = 0;
            type = 3;
        }

        for (int y = 0; y < 4; y++)
        {
            int l = nzLeft[y];
            for (int x = 0; x < 4; x++)
            {
                int block = (y * 4) + x;
                Span<short> c = coefficients.Slice(block * 16, 16);
                int nz = GetCoefficients(ref br, type, l + nzTop[x], q[0], q[1], first, c);
                l = nz > first ? 1 : 0;
                nzTop[x] = (byte)l;
                _codes[block] = Code(nz, c[0]);
                any |= _codes[block] != 0;
            }

            nzLeft[y] = (byte)l;
        }

        for (int plane = 0; plane < 2; plane++)
        {
            int topAt = 4 + (plane * 2);
            int leftAt = 4 + (plane * 2);
            for (int y = 0; y < 2; y++)
            {
                int l = nzLeft[leftAt + y];
                for (int x = 0; x < 2; x++)
                {
                    int block = 16 + (plane * 4) + (y * 2) + x;
                    Span<short> c = coefficients.Slice(block * 16, 16);
                    int nz = GetCoefficients(ref br, 2, l + nzTop[topAt + x], q[4], q[5], 0, c);
                    l = nz > 0 ? 1 : 0;
                    nzTop[topAt + x] = (byte)l;
                    _codes[block] = Code(nz, c[0]);
                    any |= _codes[block] != 0;
                }

                nzLeft[leftAt + y] = (byte)l;
            }
        }

        return any;
    }

    /// <summary>Returns how a block is transformed: 0 not at all, 1 DC only, 2 or 3 in full.</summary>
    private static byte Code(int nz, short dc) => (byte)(nz > 3 ? 3 : nz > 1 ? 2 : dc != 0 ? 1 : 0);

    /// <summary>
    /// Reads one block's tokens (RFC 6386 section 13) into <paramref name="output"/>, dequantized and in raster order, starting at
    /// coefficient <paramref name="n"/>; returns the position after the last token read (16 if the block ran to its end).
    /// </summary>
    private int GetCoefficients(ref Vp8BoolDecoder br, int type, int ctx, short dcQuant, short acQuant, int n, Span<short> output)
    {
        // The bit state lives in locals for the whole block: this loop is where most of a lossy decode's time goes.
        br.Take(out ulong value, out int bits, out uint range);
        ref byte probs = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_state.CoefficientProbabilities);
        ref byte bands = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(Bands);
        int typeAt = type * 8 * 3 * 11;
        int p = typeAt + (((Unsafe.Add(ref bands, n) * 3) + ctx) * 11);
        int result = 16;
        for (; n < 16; n++)
        {
            if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref probs, p)) == 0)
            {
                result = n;
                break;
            }

            bool done = false;
            while (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref probs, p + 1)) == 0)
            {
                n++;
                if (n == 16)
                {
                    done = true;
                    break;
                }

                p = typeAt + (Unsafe.Add(ref bands, n) * 3 * 11);
            }

            if (done)
            {
                break;
            }

            int v;
            int next = typeAt + (Unsafe.Add(ref bands, n + 1) * 3 * 11);
            if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref probs, p + 2)) == 0)
            {
                v = 1;
                p = next + 11;
            }
            else
            {
                v = LargeValue(ref br, ref value, ref bits, ref range, ref Unsafe.Add(ref probs, p));
                p = next + 22;
            }

            int sign = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 0x80);
            int coefficient = (sign != 0 ? -v : v) * (n > 0 ? acQuant : dcQuant);

            // Stored as 16 bits, as libwebp stores it, so even absurd values come out the same.
            output[Zigzag[n]] = (short)coefficient;
        }

        br.Put(value, bits, range);
        return result;
    }

    private static int LargeValue(ref Vp8BoolDecoder br, ref ulong value, ref int bits, ref uint range, ref byte p)
    {
        if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 3)) == 0)
        {
            return Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 4)) == 0
                ? 2 : 3 + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 5));
        }

        if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 6)) == 0)
        {
            if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 7)) == 0)
            {
                return 5 + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 159);
            }

            int high = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 165);
            return 7 + (2 * high) + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 145);
        }

        int bit1 = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 8));
        int bit0 = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 9 + bit1));
        int cat = (2 * bit1) + bit0;
        ReadOnlySpan<byte> table = cat switch { 0 => Cat3, 1 => Cat4, 2 => Cat5, _ => Cat6 };
        int v = 0;
        foreach (byte prob in table)
        {
            v += v + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, prob);
        }

        return v + 3 + (8 << cat);
    }

    /// <summary>Predicts the macroblock, adds its residuals in the work buffers and copies it, unfiltered, into the current row.</summary>
    private void Reconstruct(int mbx, int mby, Macroblock mb)
    {
        Span<byte> wy = _workY;
        Span<byte> wu = _workU;
        Span<byte> wv = _workV;
        const int S = Vp8Dsp.Stride;
        const int Y0 = S + 8;
        const int C0 = S + 8;

        // The left samples (and the corner) from the macroblock just reconstructed.
        if (mbx > 0)
        {
            for (int j = -1; j < 16; j++)
            {
                wy.Slice(Y0 + (j * S) + 12, 4).CopyTo(wy.Slice(Y0 + (j * S) - 4, 4));
            }

            for (int j = -1; j < 8; j++)
            {
                wu.Slice(C0 + (j * S) + 4, 4).CopyTo(wu.Slice(C0 + (j * S) - 4, 4));
                wv.Slice(C0 + (j * S) + 4, 4).CopyTo(wv.Slice(C0 + (j * S) - 4, 4));
            }
        }

        if (mby > 0)
        {
            _topY.AsSpan(mbx * 16, 16).CopyTo(wy.Slice(Y0 - S, 16));
            _topU.AsSpan(mbx * 8, 8).CopyTo(wu.Slice(C0 - S, 8));
            _topV.AsSpan(mbx * 8, 8).CopyTo(wv.Slice(C0 - S, 8));
        }

        ReadOnlySpan<short> coefficients = _coefficients;
        if (mb.Intra4x4)
        {
            // The four pixels above and right of the macroblock, repeated down the right edge for the blocks below the first row.
            Span<byte> topRight = wy.Slice(Y0 - S + 16, 4);
            if (mby > 0)
            {
                if (mbx >= MbWidth - 1)
                {
                    topRight.Fill(_topY[(mbx * 16) + 15]);
                }
                else
                {
                    _topY.AsSpan((mbx + 1) * 16, 4).CopyTo(topRight);
                }
            }

            for (int k = 1; k <= 3; k++)
            {
                topRight.CopyTo(wy.Slice(Y0 + (((k * 4) - 1) * S) + 16, 4));
            }

            for (int n = 0; n < 16; n++)
            {
                int at = Y0 + ((n >> 2) * 4 * S) + ((n & 3) * 4);
                Vp8Dsp.PredictSubblock(_subModes[n], wy, at);
                AddResidual(_codes[n], coefficients.Slice(n * 16, 16), wy, at);
            }
        }
        else
        {
            Vp8Dsp.PredictBlock(EdgeMode(mb.YMode, mbx, mby), wy, Y0, 16);
            for (int n = 0; n < 16; n++)
            {
                AddResidual(_codes[n], coefficients.Slice(n * 16, 16), wy, Y0 + ((n >> 2) * 4 * S) + ((n & 3) * 4));
            }
        }

        Vp8Dsp.BlockMode chroma = EdgeMode(mb.UvMode, mbx, mby);
        Vp8Dsp.PredictBlock(chroma, wu, C0, 8);
        Vp8Dsp.PredictBlock(chroma, wv, C0, 8);
        for (int n = 0; n < 4; n++)
        {
            int at = C0 + ((n >> 1) * 4 * S) + ((n & 1) * 4);
            AddResidual(_codes[16 + n], coefficients.Slice((16 + n) * 16, 16), wu, at);
            AddResidual(_codes[20 + n], coefficients.Slice((20 + n) * 16, 16), wv, at);
        }

        // Unfiltered bottom rows for the macroblock row below, and the pixels into the current row.
        if (mby < MbHeight - 1)
        {
            wy.Slice(Y0 + (15 * S), 16).CopyTo(_topY.AsSpan(mbx * 16, 16));
            wu.Slice(C0 + (7 * S), 8).CopyTo(_topU.AsSpan(mbx * 8, 8));
            wv.Slice(C0 + (7 * S), 8).CopyTo(_topV.AsSpan(mbx * 8, 8));
        }

        for (int j = 0; j < 16; j++)
        {
            wy.Slice(Y0 + (j * S), 16).CopyTo(_rowsY.AsSpan(((Tail + j) * StrideY) + (mbx * 16), 16));
        }

        for (int j = 0; j < 8; j++)
        {
            wu.Slice(C0 + (j * S), 8).CopyTo(_rowsU.AsSpan(((ChromaTail + j) * StrideC) + (mbx * 8), 8));
            wv.Slice(C0 + (j * S), 8).CopyTo(_rowsV.AsSpan(((ChromaTail + j) * StrideC) + (mbx * 8), 8));
        }
    }

    private static void AddResidual(byte code, ReadOnlySpan<short> coefficients, Span<byte> buffer, int at)
    {
        if (code == 1)
        {
            Vp8Dsp.InverseTransformDcAdd(coefficients[0], buffer, at);
        }
        else if (code > 1)
        {
            Vp8Dsp.InverseTransformAdd(coefficients, buffer, at);
        }
    }

    /// <summary>Maps a 16 × 16 or chroma mode to its prediction, using the DC variants at the frame's top and left edges.</summary>
    private static Vp8Dsp.BlockMode EdgeMode(int mode, int mbx, int mby)
    {
        if (mode != 0)
        {
            return (Vp8Dsp.BlockMode)mode;
        }

        return mbx == 0 ? (mby == 0 ? Vp8Dsp.BlockMode.DcNoTopLeft : Vp8Dsp.BlockMode.DcNoLeft) : (mby == 0 ? Vp8Dsp.BlockMode.DcNoTop : Vp8Dsp.BlockMode.Dc);
    }

    /// <summary>Filters one macroblock in the row buffer (RFC 6386 section 15): its left edge, inner vertical edges, top edge, inner horizontal edges.</summary>
    private void FilterMacroblock(int mbx, int mby)
    {
        MacroblockFilter f = _rowFilter[mbx];
        int limit = f.Info.Limit;
        if (limit == 0)
        {
            return;
        }

        int sy = StrideY;
        int at = (Tail * sy) + (mbx * 16);
        int interior = f.Info.Interior;
        int hev = f.Info.HevThreshold;
        Vp8FilterSimd.Kind edge = _filterType == 1 ? Vp8FilterSimd.Kind.Simple : Vp8FilterSimd.Kind.MacroblockEdge;
        Vp8FilterSimd.Kind inner = _filterType == 1 ? Vp8FilterSimd.Kind.Simple : Vp8FilterSimd.Kind.Inner;
        bool chroma = _filterType == 2;
        int sc = StrideC;
        int ac = (ChromaTail * sc) + (mbx * 8);
        if (mbx > 0)
        {
            LumaEdge(vertical: true, at, edge, limit + 4, interior, hev);
            if (chroma)
            {
                ChromaEdge(vertical: true, ac, edge, limit + 4, interior, hev);
            }
        }

        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                LumaEdge(vertical: true, at + k, inner, limit, interior, hev);
            }

            if (chroma)
            {
                ChromaEdge(vertical: true, ac + 4, inner, limit, interior, hev);
            }
        }

        if (mby > 0)
        {
            LumaEdge(vertical: false, at, edge, limit + 4, interior, hev);
            if (chroma)
            {
                ChromaEdge(vertical: false, ac, edge, limit + 4, interior, hev);
            }
        }

        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                LumaEdge(vertical: false, at + (k * sy), inner, limit, interior, hev);
            }

            if (chroma)
            {
                ChromaEdge(vertical: false, ac + (4 * sc), inner, limit, interior, hev);
            }
        }
    }

    /// <summary>Filters one luma edge 16 pixels long: a vertical edge (between columns) or a horizontal one (between rows).</summary>
    private void LumaEdge(bool vertical, int at, Vp8FilterSimd.Kind kind, int threshold, int interior, int hev)
    {
        Span<byte> y = _rowsY;
        int stride = StrideY;
        if (vertical ? Vp8FilterSimd.Columns : Vp8FilterSimd.Rows)
        {
            if (vertical)
            {
                Vp8FilterSimd.VerticalEdge16(y, at, stride, kind, threshold, interior, hev);
            }
            else
            {
                Vp8FilterSimd.HorizontalEdge16(y, at, stride, kind, threshold, interior, hev);
            }

            return;
        }

        int step = vertical ? 1 : stride;
        int along = vertical ? stride : 1;
        switch (kind)
        {
            case Vp8FilterSimd.Kind.Simple:
                Vp8Dsp.SimpleFilter(y, at, step, along, 16, threshold);
                break;
            case Vp8FilterSimd.Kind.MacroblockEdge:
                Vp8Dsp.MacroblockEdgeFilter(y, at, step, along, 16, threshold, interior, hev);
                break;
            default:
                Vp8Dsp.InnerEdgeFilter(y, at, step, along, 16, threshold, interior, hev);
                break;
        }
    }

    /// <summary>Filters one edge of U and of V, 8 pixels long each.</summary>
    private void ChromaEdge(bool vertical, int at, Vp8FilterSimd.Kind kind, int threshold, int interior, int hev)
    {
        Span<byte> u = _rowsU;
        Span<byte> v = _rowsV;
        int stride = StrideC;
        if (vertical ? Vp8FilterSimd.Columns : Vp8FilterSimd.Rows)
        {
            if (vertical)
            {
                Vp8FilterSimd.VerticalEdge8(u, v, at, stride, kind, threshold, interior, hev);
            }
            else
            {
                Vp8FilterSimd.HorizontalEdge8(u, v, at, stride, kind, threshold, interior, hev);
            }

            return;
        }

        int step = vertical ? 1 : stride;
        int along = vertical ? stride : 1;
        if (kind == Vp8FilterSimd.Kind.MacroblockEdge)
        {
            Vp8Dsp.MacroblockEdgeFilter(u, at, step, along, 8, threshold, interior, hev);
            Vp8Dsp.MacroblockEdgeFilter(v, at, step, along, 8, threshold, interior, hev);
        }
        else
        {
            Vp8Dsp.InnerEdgeFilter(u, at, step, along, 8, threshold, interior, hev);
            Vp8Dsp.InnerEdgeFilter(v, at, step, along, 8, threshold, interior, hev);
        }
    }

    /// <summary>What a macroblock row changes, kept so an incremental decode can undo a row that ran out of bytes.</summary>
    private sealed class Snapshot(Vp8Decoder decoder)
    {
        private readonly byte[] _modesAbove = new byte[decoder._modesAbove.Length];
        private readonly byte[] _nzAbove = new byte[decoder._nzAbove.Length];
        private readonly byte[] _topY = new byte[decoder._topY.Length];
        private readonly byte[] _topU = new byte[decoder._topU.Length];
        private readonly byte[] _topV = new byte[decoder._topV.Length];
        private readonly byte[] _tailY = new byte[decoder.StrideY * Tail];
        private readonly byte[] _tailU = new byte[decoder.StrideC * ChromaTail];
        private readonly byte[] _tailV = new byte[decoder.StrideC * ChromaTail];
        private Vp8BoolDecoder _modes;
        private Vp8BoolDecoder _tokens;

        internal void Save(Vp8Decoder d, int mby)
        {
            d._modesAbove.CopyTo(_modesAbove, 0);
            d._nzAbove.CopyTo(_nzAbove, 0);
            d._topY.CopyTo(_topY, 0);
            d._topU.CopyTo(_topU, 0);
            d._topV.CopyTo(_topV, 0);
            Array.Copy(d._rowsY, _tailY, _tailY.Length);
            Array.Copy(d._rowsU, _tailU, _tailU.Length);
            Array.Copy(d._rowsV, _tailV, _tailV.Length);
            _modes = d._modes;
            _tokens = d._partitions[mby & d._partitionMask];
        }

        internal void Restore(Vp8Decoder d, int mby)
        {
            _modesAbove.CopyTo(d._modesAbove, 0);
            _nzAbove.CopyTo(d._nzAbove, 0);
            _topY.CopyTo(d._topY, 0);
            _topU.CopyTo(d._topU, 0);
            _topV.CopyTo(d._topV, 0);
            Array.Copy(_tailY, d._rowsY, _tailY.Length);
            Array.Copy(_tailU, d._rowsU, _tailU.Length);
            Array.Copy(_tailV, d._rowsV, _tailV.Length);
            d._modes = _modes;
            d._partitions[mby & d._partitionMask] = _tokens;
        }
    }

    /// <summary>Receives the rows a call to <see cref="DecodeRow"/> made final: luma rows [from, to) and chroma rows [chromaFrom, chromaTo).</summary>
    internal delegate void RowSink(Vp8Decoder decoder, int from, int to, int chromaFrom, int chromaTo);

    private readonly record struct Macroblock(int Segment, bool Skip, bool Intra4x4, int YMode, int UvMode);

    /// <summary>A filter strength: the edge limit, the interior limit and the high-variance threshold; a limit of 0 means no filtering.</summary>
    private readonly record struct FilterInfo(int Limit, int Interior, int HevThreshold);

    private readonly record struct MacroblockFilter(FilterInfo Info, bool Inner);
}

/// <summary>The size and first partition of a VP8 frame, from its header.</summary>
internal readonly record struct Vp8FrameHeader(int Width, int Height, int FirstPartitionSize);

/// <summary>
/// The decoder state that lasts from frame to frame in a VP8 stream: coefficient probabilities, segment values and probabilities,
/// filter deltas, and slots for the last, golden and alternate reference frames. A WebP still uses one frame of it; Egami.Video
/// will carry it across a WebM stream's frames.
/// </summary>
internal sealed class Vp8StreamState
{
    internal Vp8StreamState()
    {
        Vp8Tables.DefaultCoeffProbs.CopyTo(CoefficientProbabilities);
    }

    internal byte[] CoefficientProbabilities { get; } = new byte[4 * 8 * 3 * 11];

    internal bool AbsoluteSegmentValues { get; set; }

    internal int[] SegmentQuantizer { get; } = new int[4];

    internal int[] SegmentFilterLevel { get; } = new int[4];

    internal byte[] SegmentProbabilities { get; } = [255, 255, 255];

    internal int[] ReferenceFilterDeltas { get; } = new int[4];

    internal int[] ModeFilterDeltas { get; } = new int[4];

    /// <summary>Gets or sets the last, golden and alternate reference frames; unused by key frames.</summary>
    internal YuvRaster<byte>?[] References { get; } = new YuvRaster<byte>?[3];
}
