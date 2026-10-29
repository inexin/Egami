using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// Parses the slice segment data of an intra picture (H.265 7.3.8) with CABAC and reconstructs it block by block: SAO syntax,
/// the coding quadtree, coding units (intra modes, PCM, transquant bypass), the transform tree, QP derivation (8.6.1) and residual
/// coding (7.3.8.11), each transform block predicted (8.4.4.2) and its residual added as soon as it is parsed.
/// </summary>
internal sealed partial class HevcSliceDecoder
{
    /// <summary>The chroma QP mapping of 4:2:0 for qPi from 30 to 43 (Table 8-10).</summary>
    private static readonly byte[] ChromaQpTable = [29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37];

    /// <summary>The 4:2:2 intra mode mapping for chroma (Table 8-3).</summary>
    private static readonly byte[] Mode422 = [0, 1, 2, 2, 2, 2, 3, 5, 7, 8, 10, 12, 13, 15, 17, 18, 19, 20, 21, 22, 23, 23, 24, 24, 25, 25, 26, 27, 27, 28, 28, 29, 29, 30, 31];

    private readonly HevcPicture _picture;
    private readonly HevcSps _sps;
    private readonly HevcPps _pps;
    private readonly HevcLayout _layout;
    private readonly CabacEngine _cabac = new();
    private readonly HevcIntra _intra = new();
    private readonly byte[] _contexts = new byte[HevcContexts.Count];
    private readonly int[] _statCoeff = new int[4];
    private readonly byte[] _dsContexts = new byte[HevcContexts.Count];
    private readonly int[] _dsStatCoeff = new int[4];
    private readonly int[] _coefficients = new int[32 * 32];
    private readonly int[] _residual = new int[32 * 32];
    private readonly int[] _lumaResidual = new int[32 * 32];
    private readonly int[] _scratch = new int[32 * 32];
    private readonly int _log2MinCuQpDeltaSize;
    private readonly int _log2MinCuChromaQpOffsetSize;

    // The slice and slice segment being decoded.
    private HevcSliceHeader _slice = null!;
    private int _sliceAddrRs;
    private bool _dsSaved;
    private byte[] _data = [];
    private int _dataEnd;

    // QP state (8.6.1).
    private int _qpY;
    private int _qgX = -1;
    private int _qgY = -1;
    private int _lastQpY;
    private int _qpYPred;
    private bool _firstQgInGroup;
    private bool _isCuQpDeltaCoded;
    private int _cuQpDeltaVal;
    private bool _isCuChromaQpOffsetCoded;
    private int _cuQpOffsetCb;
    private int _cuQpOffsetCr;

    // The coding unit being decoded.
    private bool _cuBypass;
    private int _cuX;
    private int _cuY;
    private int _cuLog2;
    private readonly int[] _lumaModes = new int[4];
    private readonly int[] _chromaModes = new int[4];
    private readonly int[] _chromaModeSyntax = new int[4];
    private bool _intraSplit;
    private readonly int[] _resScaleVal = new int[3];

    internal HevcSliceDecoder(HevcPicture picture)
    {
        _picture = picture;
        _sps = picture.Sps;
        _pps = picture.Pps;
        _layout = picture.Layout;
        _log2MinCuQpDeltaSize = _sps.CtbLog2Size - _pps.DiffCuQpDeltaDepth;
        _log2MinCuChromaQpOffsetSize = _sps.CtbLog2Size - _pps.DiffCuChromaQpOffsetDepth;
    }

    /// <summary>
    /// Decodes a chain of coding tree blocks: each piece is one substream of a slice segment, from its entry point (or, without
    /// one, where the previous substream ended). Returns false when the chain stopped because another chain failed.
    /// </summary>
    internal bool DecodeChain(HevcChain chain, HevcChainSet set)
    {
        _dsSaved = false;
        int widthCtbs = _sps.WidthInCtbs;
        HevcChain wppSource = chain.Above ?? chain;
        bool wait = set.Shared && chain.Above is not null;
        foreach ((HevcSegment segment, int substream, int firstTs, int endTs) in chain.Pieces)
        {
            HevcSliceHeader header = segment.Header;
            if (header.SliceType != HevcSliceHeader.I)
            {
                throw HevcErrors.Unsupported("The picture has P or B slices (inter prediction).");
            }

            _slice = segment.Slice;
            _sliceAddrRs = segment.SliceAddrRs;
            byte[] rbsp = segment.Nal.Rbsp;
            int[] starts = segment.Starts;
            int start = substream < starts.Length ? starts[substream] : _cabac.Position;
            StartCabac(rbsp, start, substream + 1 < starts.Length ? starts[substream + 1] : rbsp.Length);

            // Contexts at the start of the substream (9.3.1, 9.3.2.2 to 9.3.2.4).
            int ctbAddrRs = _layout.TsToRs[firstTs];
            bool firstInTile = firstTs == 0 || _layout.TileId[firstTs] != _layout.TileId[firstTs - 1];
            bool rowStart = _pps.EntropyCodingSync && FirstInTileRow(ctbAddrRs, firstTs);
            if (firstInTile)
            {
                InitializeContexts();
            }
            else if (rowStart)
            {
                if (wait && !wppSource.WaitFor(Math.Min(2, chain.TileWidth), set))
                {
                    return false;
                }

                SyncWpp(ctbAddrRs, wppSource);
            }
            else if (substream == 0 && header.Dependent && _dsSaved)
            {
                Array.Copy(_dsContexts, _contexts, _contexts.Length);
                Array.Copy(_dsStatCoeff, _statCoeff, 4);
            }
            else
            {
                InitializeContexts();
            }

            if (substream > 0 || !header.Dependent || firstInTile || rowStart)
            {
                _firstQgInGroup = true;
            }

            for (int ctbAddrTs = firstTs; ctbAddrTs < endTs; ctbAddrTs++)
            {
                ctbAddrRs = _layout.TsToRs[ctbAddrTs];
                if (wait)
                {
                    // Intra prediction reads the CTB above-right: the row above must be two CTBs ahead.
                    int column = (ctbAddrRs % widthCtbs) - _layout.ColumnBoundaries[ColumnOf(ctbAddrTs)];
                    if (!chain.Above!.WaitFor(Math.Min(column + 2, chain.TileWidth), set))
                    {
                        return false;
                    }
                }

                int xCtb = (ctbAddrRs % widthCtbs) << _sps.CtbLog2Size, yCtb = (ctbAddrRs / widthCtbs) << _sps.CtbLog2Size;
                _picture.CtbSlice[ctbAddrRs] = _sliceAddrRs;
                _picture.CtbSliceHeader[ctbAddrRs] = _slice;
                CodingTreeUnit(xCtb, yCtb, ctbAddrRs, ctbAddrTs);

                // Storage for wavefront synchronization after the second CTB of a row (of a tile).
                if (_pps.EntropyCodingSync && IsSecondInRow(ctbAddrRs, ctbAddrTs))
                {
                    Array.Copy(_contexts, chain.WppContexts, _contexts.Length);
                    Array.Copy(_statCoeff, chain.WppStatCoeff, 4);
                }

                chain.Advance(set.Shared);
                bool end = _cabac.Terminate() == 1;
                if (ctbAddrTs + 1 < endTs)
                {
                    if (end)
                    {
                        throw HevcErrors.Corrupt("A slice segment ends before the next one starts.");
                    }

                    if (_cabac.Overrun)
                    {
                        throw HevcErrors.Corrupt("A slice segment's data ends before its last coding tree block.");
                    }
                }
                else if (endTs == segment.EndTs)
                {
                    if (!end)
                    {
                        throw HevcErrors.Corrupt("A slice segment runs past the start of the next one.");
                    }
                }
                else if (end || _cabac.Terminate() != 1)
                {
                    throw HevcErrors.Corrupt("A substream does not end with end_of_subset_one_bit.");
                }
            }

            if (_pps.DependentSliceSegmentsEnabled && endTs == segment.EndTs)
            {
                Array.Copy(_contexts, _dsContexts, _contexts.Length);
                Array.Copy(_statCoeff, _dsStatCoeff, 4);
                _dsSaved = true;
            }
        }

        return true;
    }

    /// <summary>Gets whether the CTB is the first of a CTB row in its tile.</summary>
    private bool FirstInTileRow(int ctbAddrRs, int ctbAddrTs) =>
        ctbAddrRs % _sps.WidthInCtbs == 0 || _layout.TileId[ctbAddrTs] != _layout.TileId[_layout.RsToTs[ctbAddrRs - 1]];

    private int ColumnOf(int ctbAddrTs) => _layout.TileId[ctbAddrTs] % (_layout.ColumnBoundaries.Length - 1);

    private bool IsSecondInRow(int ctbAddrRs, int ctbAddrTs)
    {
        int widthCtbs = _sps.WidthInCtbs;
        if (ctbAddrRs % widthCtbs == 1)
        {
            return true;
        }

        return ctbAddrRs % widthCtbs > 1 && _layout.TileId[ctbAddrTs] != _layout.TileId[_layout.RsToTs[ctbAddrRs - 2]]
            && _layout.TileId[ctbAddrTs] == _layout.TileId[_layout.RsToTs[ctbAddrRs - 1]];
    }

    private void InitializeContexts()
    {
        // initType (9-7): 0 for I slices; P and B slices swap 1 and 2 with cabac_init_flag.
        int initType = _slice.SliceType == HevcSliceHeader.I ? 0 : (_slice.SliceType == HevcSliceHeader.P) != _slice.CabacInit ? 1 : 2;
        HevcContexts.Initialize(_contexts, _slice.SliceQp, initType);
        Array.Clear(_statCoeff);
    }

    /// <summary>At the first CTB of a row with wavefronts: the contexts after the CTB above-right, when it is available.</summary>
    private void SyncWpp(int ctbAddrRs, HevcChain source)
    {
        int widthCtbs = _sps.WidthInCtbs;
        int size = 1 << _sps.CtbLog2Size;
        int x0 = (ctbAddrRs % widthCtbs) * size, y0 = (ctbAddrRs / widthCtbs) * size;
        int xN = x0 + size, yN = y0 - size;

        // Availability before the current CTB is marked: the CTB above-right must be decoded, in the same slice and tile.
        bool available = false;
        if (yN >= 0 && xN < _sps.Width)
        {
            int ctbN = (yN >> _sps.CtbLog2Size) * widthCtbs + (xN >> _sps.CtbLog2Size);
            available = _picture.CtbSlice[ctbN] == _sliceAddrRs
                && _layout.TileId[_layout.RsToTs[ctbN]] == _layout.TileId[_layout.RsToTs[ctbAddrRs]];
        }

        if (available)
        {
            Array.Copy(source.WppContexts, _contexts, _contexts.Length);
            Array.Copy(source.WppStatCoeff, _statCoeff, 4);
        }
        else
        {
            InitializeContexts();
        }
    }

    /// <summary>The RBSP positions where the substreams start: the slice data, then each entry point.</summary>
    internal static int[] SubstreamStarts(HevcNal nal, HevcSliceHeader header)
    {
        int[] starts = new int[header.EntryPoints.Length + 1];
        starts[0] = header.DataStart;
        if (header.EntryPoints.Length == 0)
        {
            return starts;
        }

        // Entry points count escaped bytes from the first byte of the slice data; convert to RBSP positions.
        int escapedStart = header.DataStart;
        foreach (int removed in nal.Removed)
        {
            if (removed - CountBefore(nal.Removed, removed) < header.DataStart)
            {
                escapedStart++;
            }
        }

        int escaped = escapedStart;
        for (int i = 0; i < header.EntryPoints.Length; i++)
        {
            escaped += header.EntryPoints[i];
            int before = 0;
            foreach (int removed in nal.Removed)
            {
                if (removed < escaped)
                {
                    before++;
                }
            }

            starts[i + 1] = escaped - before;
        }

        return starts;

        static int CountBefore(int[] removed, int position) => removed.Count(r => r < position);
    }

    private void CodingTreeUnit(int xCtb, int yCtb, int ctbAddrRs, int ctbAddrTs)
    {
        if (_slice.SaoLuma || _slice.SaoChroma)
        {
            Sao(xCtb >> _sps.CtbLog2Size, yCtb >> _sps.CtbLog2Size, ctbAddrRs, ctbAddrTs);
        }

        CodingQuadtree(xCtb, yCtb, _sps.CtbLog2Size, 0);
    }

    // ---------------------------------------------------------------- SAO syntax (7.3.8.3)

    private void Sao(int rx, int ry, int ctbAddrRs, int ctbAddrTs)
    {
        int widthCtbs = _sps.WidthInCtbs;
        ref HevcSaoParameters sao = ref _picture.Sao[ctbAddrRs];
        if (rx > 0)
        {
            bool leftInSlice = ctbAddrRs > _sliceAddrRs;
            bool leftInTile = _layout.TileId[ctbAddrTs] == _layout.TileId[_layout.RsToTs[ctbAddrRs - 1]];
            if (leftInSlice && leftInTile && _cabac.Decode(ref _contexts[HevcContexts.SaoMerge]) == 1)
            {
                sao = _picture.Sao[ctbAddrRs - 1];
                return;
            }
        }

        if (ry > 0)
        {
            bool upInSlice = ctbAddrRs - widthCtbs >= _sliceAddrRs;
            bool upInTile = _layout.TileId[ctbAddrTs] == _layout.TileId[_layout.RsToTs[ctbAddrRs - widthCtbs]];
            if (upInSlice && upInTile && _cabac.Decode(ref _contexts[HevcContexts.SaoMerge]) == 1)
            {
                sao = _picture.Sao[ctbAddrRs - widthCtbs];
                return;
            }
        }

        sao = default;
        Span<int> abs = stackalloc int[4];
        int components = _sps.ChromaArrayType != 0 ? 3 : 1;
        for (int c = 0; c < components; c++)
        {
            if ((c == 0 && !_slice.SaoLuma) || (c > 0 && !_slice.SaoChroma))
            {
                continue;
            }

            int type;
            if (c == 2)
            {
                type = sao.TypeCb;
            }
            else
            {
                type = _cabac.Decode(ref _contexts[HevcContexts.SaoTypeIdx]) == 0 ? 0 : _cabac.Bypass() == 0 ? 1 : 2;
            }

            if (type == 0)
            {
                continue;
            }

            int bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
            int cMax = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;
            for (int i = 0; i < 4; i++)
            {
                int v = 0;
                while (v < cMax && _cabac.Bypass() == 1)
                {
                    v++;
                }

                abs[i] = v;
            }

            int scale = c == 0 ? _pps.Log2SaoOffsetScaleLuma : _pps.Log2SaoOffsetScaleChroma;
            HevcSaoParameters.OffsetSet offsets = default;
            int saoClass;
            if (type == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    offsets[i] = (abs[i] != 0 && _cabac.Bypass() == 1 ? -abs[i] : abs[i]) << scale;
                }

                saoClass = (int)_cabac.Bypass(5);
            }
            else
            {
                offsets[0] = abs[0] << scale;
                offsets[1] = abs[1] << scale;
                offsets[2] = -(abs[2] << scale);
                offsets[3] = -(abs[3] << scale);
                saoClass = c == 2 ? sao.ClassCb : (int)_cabac.Bypass(2);
            }

            switch (c)
            {
                case 0:
                    (sao.TypeY, sao.ClassY, sao.OffsetsY) = (type, saoClass, offsets);
                    break;
                case 1:
                    (sao.TypeCb, sao.ClassCb, sao.OffsetsCb) = (type, saoClass, offsets);
                    break;
                default:
                    (sao.TypeCr, sao.ClassCr, sao.OffsetsCr) = (type, saoClass, offsets);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- coding quadtree and coding unit (7.3.8.4, 7.3.8.5)

    private void CodingQuadtree(int x0, int y0, int log2CbSize, int depth)
    {
        int size = 1 << log2CbSize;
        bool split;
        if (x0 + size <= _sps.Width && y0 + size <= _sps.Height && log2CbSize > _sps.MinCbLog2Size)
        {
            int ctxInc = 0;
            if (_picture.Available(x0, y0, x0 - 1, y0) && _picture.CtDepth[(y0 >> 2) * _picture.Width4 + ((x0 - 1) >> 2)] > depth)
            {
                ctxInc++;
            }

            if (_picture.Available(x0, y0, x0, y0 - 1) && _picture.CtDepth[((y0 - 1) >> 2) * _picture.Width4 + (x0 >> 2)] > depth)
            {
                ctxInc++;
            }

            split = _cabac.Decode(ref _contexts[HevcContexts.SplitCuFlag + ctxInc]) == 1;
        }
        else
        {
            split = log2CbSize > _sps.MinCbLog2Size;
        }

        if (_pps.CuQpDeltaEnabled && log2CbSize >= _log2MinCuQpDeltaSize)
        {
            _isCuQpDeltaCoded = false;
            _cuQpDeltaVal = 0;
        }

        if (_slice.CuChromaQpOffsetEnabled && log2CbSize >= _log2MinCuChromaQpOffsetSize)
        {
            _isCuChromaQpOffsetCoded = false;
        }

        if (split)
        {
            int half = size >> 1;
            for (int i = 0; i < 4; i++)
            {
                int x = x0 + ((i & 1) * half), y = y0 + ((i >> 1) * half);
                if (x < _sps.Width && y < _sps.Height)
                {
                    CodingQuadtree(x, y, log2CbSize - 1, depth + 1);
                }
            }
        }
        else
        {
            // The quantization group of the coding unit (8.6.1): a new one starts the QP prediction again.
            int mask = (1 << _log2MinCuQpDeltaSize) - 1;
            int xQg = x0 & ~mask, yQg = y0 & ~mask;
            if (xQg != _qgX || yQg != _qgY || _firstQgInGroup)
            {
                StartQuantizationGroup(xQg, yQg);
            }

            CodingUnit(x0, y0, log2CbSize, depth);
        }
    }

    /// <summary>Derives qPY_PRED for the quantization group starting at (xQg, yQg) (8.6.1).</summary>
    private void StartQuantizationGroup(int xQg, int yQg)
    {
        (_qgX, _qgY) = (xQg, yQg);
        int previous = _firstQgInGroup ? _slice.SliceQp : _lastQpY;
        _firstQgInGroup = false;
        int ctbMask = ~((1 << _sps.CtbLog2Size) - 1);
        int qpA = previous, qpB = previous;
        if (_picture.Available(xQg, yQg, xQg - 1, yQg) && ((xQg - 1) & ctbMask) == (xQg & ctbMask))
        {
            qpA = _picture.QpY[(yQg >> 2) * _picture.Width4 + ((xQg - 1) >> 2)];
        }

        if (_picture.Available(xQg, yQg, xQg, yQg - 1) && ((yQg - 1) & ctbMask) == (yQg & ctbMask))
        {
            qpB = _picture.QpY[((yQg - 1) >> 2) * _picture.Width4 + (xQg >> 2)];
        }

        _qpYPred = (qpA + qpB + 1) >> 1;
        _qpY = _qpYPred;
    }

    /// <summary>Sets QpY from qPY_PRED and CuQpDeltaVal (8-283).</summary>
    private void UpdateQp()
    {
        int offset = _sps.QpBdOffsetLuma;
        _qpY = ((_qpYPred + _cuQpDeltaVal + 52 + (2 * offset)) % (52 + offset)) - offset;
    }

    private void CodingUnit(int x0, int y0, int log2CbSize, int depth)
    {
        int size = 1 << log2CbSize;
        _cuX = x0;
        _cuY = y0;
        _cuLog2 = log2CbSize;
        _cuBypass = _pps.TransquantBypassEnabled && _cabac.Decode(ref _contexts[HevcContexts.CuTransquantBypass]) == 1;
        UpdateQp();

        // Coding tree depth for the split_cu_flag contexts of later blocks.
        FillBlocks(_picture.CtDepth, x0, y0, size, size, (byte)depth);
        byte flags = (byte)(_cuBypass ? HevcPicture.Bypass : 0);
        int partNxN = 0;
        if (log2CbSize == _sps.MinCbLog2Size)
        {
            partNxN = _cabac.Decode(ref _contexts[HevcContexts.PartMode]) == 0 ? 1 : 0;
            if (partNxN == 1 && log2CbSize == 3 && _sps.MinTbLog2Size > 2)
            {
                throw HevcErrors.Corrupt("An 8x8 coding unit is split in four, but the smallest transform is 8x8.");
            }
        }

        _intraSplit = partNxN == 1;
        bool pcm = false;
        if (!_intraSplit && _sps.PcmEnabled && log2CbSize >= _sps.Log2MinPcmCbSize && log2CbSize <= _sps.Log2MaxPcmCbSize)
        {
            pcm = _cabac.Terminate() == 1;
        }

        MarkEdges(x0, y0, size, size);
        if (pcm)
        {
            flags |= HevcPicture.Pcm;
            FillBlocks(_picture.IntraMode, x0, y0, size, size, (byte)1);
            PcmSample(x0, y0, log2CbSize);
        }
        else
        {
            IntraModes(x0, y0, log2CbSize);
            int maxTrafoDepth = _sps.MaxTransformHierarchyDepthIntra + (_intraSplit ? 1 : 0);
            TransformTree(x0, y0, x0, y0, log2CbSize, 0, 0, maxTrafoDepth, true, true, true, true);
        }

        FillFlags(x0, y0, size, flags);
        FillBlocks(_picture.QpY, x0, y0, size, size, (sbyte)_qpY);
        _lastQpY = _qpY;
    }

    private void PcmSample(int x0, int y0, int log2CbSize)
    {
        CabacEngine.RawReader raw = new(_data, _cabac.Position);
        int size = 1 << log2CbSize;
        int components = _sps.ChromaArrayType != 0 ? 3 : 1;
        for (int c = 0; c < components; c++)
        {
            int w = c == 0 ? size : size / _sps.SubWidthC, h = c == 0 ? size : size / _sps.SubHeightC;
            int xc = c == 0 ? x0 : x0 / _sps.SubWidthC, yc = c == 0 ? y0 : y0 / _sps.SubHeightC;
            int pcmDepth = c == 0 ? _sps.PcmBitDepthLuma : _sps.PcmBitDepthChroma;
            int depth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
            ushort[] plane = _picture.Planes[c];
            int stride = _picture.Widths[c];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    plane[((yc + y) * stride) + xc + x] = (ushort)(raw.Read(pcmDepth) << (depth - pcmDepth));
                }
            }
        }

        _cabac.Start(_data, raw.BytePosition, _dataEnd);
    }

    /// <summary>prev_intra_luma_pred_flag, mpm_idx, rem_intra_luma_pred_mode and intra_chroma_pred_mode, and the modes (8.4.2, 8.4.3).</summary>
    private void IntraModes(int x0, int y0, int log2CbSize)
    {
        int parts = _intraSplit ? 4 : 1;
        int pbSize = _intraSplit ? 1 << (log2CbSize - 1) : 1 << log2CbSize;
        Span<bool> prev = stackalloc bool[4];
        Span<int> mpm = stackalloc int[3];
        for (int i = 0; i < parts; i++)
        {
            prev[i] = _cabac.Decode(ref _contexts[HevcContexts.PrevIntraLumaPred]) == 1;
        }

        for (int i = 0; i < parts; i++)
        {
            int xPb = x0 + ((i & 1) * pbSize), yPb = y0 + ((i >> 1) * pbSize);
            int a = NeighbourMode(xPb, yPb, xPb - 1, yPb);

            // The neighbour above in another CTB row counts as DC (8-?), so line buffers stay one CTB row tall.
            int b = yPb - 1 < ((yPb >> _sps.CtbLog2Size) << _sps.CtbLog2Size) ? 1 : NeighbourMode(xPb, yPb, xPb, yPb - 1);
            if (a == b)
            {
                if (a < 2)
                {
                    (mpm[0], mpm[1], mpm[2]) = (0, 1, 26);
                }
                else
                {
                    (mpm[0], mpm[1], mpm[2]) = (a, 2 + ((a + 29) % 32), 2 + ((a - 2 + 1) % 32));
                }
            }
            else
            {
                (mpm[0], mpm[1]) = (a, b);
                mpm[2] = a != 0 && b != 0 ? 0 : a != 1 && b != 1 ? 1 : 26;
            }

            int mode;
            if (prev[i])
            {
                int index = _cabac.Bypass() == 0 ? 0 : _cabac.Bypass() == 0 ? 1 : 2;
                mode = mpm[index];
            }
            else
            {
                mode = (int)_cabac.Bypass(5);
                mpm.Sort();
                for (int k = 0; k < 3; k++)
                {
                    if (mode >= mpm[k])
                    {
                        mode++;
                    }
                }
            }

            _lumaModes[i] = mode;
            FillBlocks(_picture.IntraMode, xPb, yPb, pbSize, pbSize, (byte)mode);
        }

        if (_sps.ChromaArrayType == 0)
        {
            return;
        }

        int chromaParts = _sps.ChromaArrayType == 3 ? parts : 1;
        for (int i = 0; i < chromaParts; i++)
        {
            int syntax = _cabac.Decode(ref _contexts[HevcContexts.IntraChromaPredMode]) == 0 ? 4 : (int)_cabac.Bypass(2);
            int luma = _lumaModes[i];
            int mode = syntax == 4 ? luma : syntax switch { 0 => 0, 1 => 26, 2 => 10, _ => 1 };
            if (syntax != 4 && mode == luma)
            {
                mode = 34;
            }

            _chromaModeSyntax[i] = syntax;
            _chromaModes[i] = _sps.ChromaArrayType == 2 ? Mode422[mode] : mode;
        }
    }

    /// <summary>candIntraPredModeX (8.4.2): the neighbour's luma mode, or DC when it is unavailable or PCM.</summary>
    private int NeighbourMode(int xCurr, int yCurr, int xN, int yN)
    {
        if (!_picture.Available(xCurr, yCurr, xN, yN))
        {
            return 1;
        }

        int index = (yN >> 2) * _picture.Width4 + (xN >> 2);
        return _picture.IntraMode[index];
    }

    // ---------------------------------------------------------------- transform tree and unit (7.3.8.8, 7.3.8.10)

    private void TransformTree(int x0, int y0, int xBase, int yBase, int log2TrafoSize, int trafoDepth, int blkIdx, int maxTrafoDepth,
        bool parentCb0, bool parentCb1, bool parentCr0, bool parentCr1)
    {
        bool split;
        if (log2TrafoSize <= _sps.MaxTbLog2Size && log2TrafoSize > _sps.MinTbLog2Size && trafoDepth < maxTrafoDepth && !(_intraSplit && trafoDepth == 0))
        {
            split = _cabac.Decode(ref _contexts[HevcContexts.SplitTransform + 5 - log2TrafoSize]) == 1;
        }
        else
        {
            split = log2TrafoSize > _sps.MaxTbLog2Size || (_intraSplit && trafoDepth == 0);
        }

        int chroma = _sps.ChromaArrayType;
        bool cb0 = false, cb1 = false, cr0 = false, cr1 = false;
        if ((log2TrafoSize > 2 && chroma != 0) || chroma == 3)
        {
            if (trafoDepth == 0 || parentCb0 || parentCb1)
            {
                cb0 = Cbf(trafoDepth);
                if (chroma == 2 && (!split || log2TrafoSize == 3))
                {
                    cb1 = Cbf(trafoDepth);
                }
            }

            if (trafoDepth == 0 || parentCr0 || parentCr1)
            {
                cr0 = Cbf(trafoDepth);
                if (chroma == 2 && (!split || log2TrafoSize == 3))
                {
                    cr1 = Cbf(trafoDepth);
                }
            }
        }
        else if (chroma != 0 && log2TrafoSize == 2)
        {
            // 4x4 luma blocks of 4:2:0 and 4:2:2: the chroma flags are the parent's, used at blkIdx 3.
            (cb0, cb1, cr0, cr1) = (parentCb0, parentCb1, parentCr0, parentCr1);
        }

        if (split)
        {
            int half = 1 << (log2TrafoSize - 1);
            for (int i = 0; i < 4; i++)
            {
                TransformTree(x0 + ((i & 1) * half), y0 + ((i >> 1) * half), x0, y0, log2TrafoSize - 1, trafoDepth + 1, i, maxTrafoDepth, cb0, cb1, cr0, cr1);
            }
        }
        else
        {
            bool cbfLuma = _cabac.Decode(ref _contexts[HevcContexts.CbfLuma + (trafoDepth == 0 ? 1 : 0)]) == 1;
            MarkTransformEdges(x0, y0, 1 << log2TrafoSize);
            TransformUnit(x0, y0, xBase, yBase, log2TrafoSize, trafoDepth, blkIdx, cbfLuma, cb0, cb1, cr0, cr1);
        }
    }

    private bool Cbf(int trafoDepth) => _cabac.Decode(ref _contexts[HevcContexts.CbfChroma + trafoDepth]) == 1;

    private void TransformUnit(int x0, int y0, int xBase, int yBase, int log2TrafoSize, int trafoDepth, int blkIdx, bool cbfLuma,
        bool cb0, bool cb1, bool cr0, bool cr1)
    {
        int chroma = _sps.ChromaArrayType;
        bool cbfChroma = cb0 || cb1 || cr0 || cr1;
        if (cbfLuma || cbfChroma)
        {
            if (_pps.CuQpDeltaEnabled && !_isCuQpDeltaCoded)
            {
                int abs = 0;
                while (abs < 5 && _cabac.Decode(ref _contexts[HevcContexts.CuQpDeltaAbs + (abs == 0 ? 0 : 1)]) == 1)
                {
                    abs++;
                }

                if (abs == 5)
                {
                    int k = 0;
                    while (_cabac.Bypass() == 1)
                    {
                        if (++k > 31)
                        {
                            throw HevcErrors.Corrupt("cu_qp_delta_abs is too long.");
                        }
                    }

                    abs += (int)((1u << k) - 1 + _cabac.Bypass(k));
                }

                _isCuQpDeltaCoded = true;
                _cuQpDeltaVal = abs != 0 && _cabac.Bypass() == 1 ? -abs : abs;
                int offset = _sps.QpBdOffsetLuma;
                if (_cuQpDeltaVal < -(26 + (offset / 2)) || _cuQpDeltaVal > 25 + (offset / 2))
                {
                    throw HevcErrors.Corrupt($"CuQpDeltaVal is {_cuQpDeltaVal}.");
                }

                UpdateQp();
            }

            if (_slice.CuChromaQpOffsetEnabled && cbfChroma && !_cuBypass && !_isCuChromaQpOffsetCoded)
            {
                bool flag = _cabac.Decode(ref _contexts[HevcContexts.CuChromaQpOffsetFlag]) == 1;
                int index = 0;
                if (flag && _pps.CbQpOffsetList.Length > 1)
                {
                    while (index < _pps.CbQpOffsetList.Length - 1 && _cabac.Decode(ref _contexts[HevcContexts.CuChromaQpOffsetIdx]) == 1)
                    {
                        index++;
                    }
                }

                _isCuChromaQpOffsetCoded = true;
                (_cuQpOffsetCb, _cuQpOffsetCr) = flag ? (_pps.CbQpOffsetList[index], _pps.CrQpOffsetList[index]) : (0, 0);
            }
        }

        // Luma: predict, then add the residual.
        int lumaMode = _lumaModes[PartIndex(x0, y0)];
        int log2 = log2TrafoSize;
        _intra.Predict(_picture, 0, x0, y0, log2, lumaMode, _sps.ImplicitRdpcm && _cuBypass);
        bool lumaHasResidual = false;
        if (cbfLuma)
        {
            Residual(x0, y0, log2, 0, lumaMode);
            lumaHasResidual = true;
        }

        if (chroma == 0)
        {
            return;
        }

        bool crossComponent = _pps.CrossComponentPrediction && cbfLuma && _chromaModeSyntax[ChromaPartIndex(x0, y0)] == 4;
        if (_pps.CrossComponentPrediction && crossComponent && lumaHasResidual)
        {
            Array.Copy(_residual, _lumaResidual, 1 << (2 * log2));
        }

        if (log2TrafoSize > 2 || chroma == 3)
        {
            int log2C = chroma == 3 ? log2TrafoSize : log2TrafoSize - 1;
            int xC = x0 / _sps.SubWidthC, yC = y0 / _sps.SubHeightC;
            for (int c = 1; c <= 2; c++)
            {
                int resScale = crossComponent ? CrossComponentPrediction(c - 1) : 0;
                int mode = _chromaModes[ChromaPartIndex(x0, y0)];
                for (int t = 0; t < (chroma == 2 ? 2 : 1); t++)
                {
                    bool cbf = c == 1 ? (t == 0 ? cb0 : cb1) : (t == 0 ? cr0 : cr1);
                    ChromaBlock(c, xC, yC + (t << log2C), log2C, mode, cbf, resScale);
                }
            }
        }
        else if (blkIdx == 3)
        {
            int log2C = log2TrafoSize;
            int xC = xBase / _sps.SubWidthC, yC = yBase / _sps.SubHeightC;
            for (int c = 1; c <= 2; c++)
            {
                int mode = _chromaModes[0];
                for (int t = 0; t < (chroma == 2 ? 2 : 1); t++)
                {
                    bool cbf = c == 1 ? (t == 0 ? cb0 : cb1) : (t == 0 ? cr0 : cr1);
                    ChromaBlock(c, xC, yC + (t << log2C), log2C, mode, cbf, 0);
                }
            }
        }
    }

    private void ChromaBlock(int c, int xC, int yC, int log2C, int mode, bool cbf, int resScale)
    {
        _intra.Predict(_picture, c, xC, yC, log2C, mode, _sps.ImplicitRdpcm && _cuBypass);
        if (cbf)
        {
            Residual(xC, yC, log2C, c, mode, resScale);
        }
        else if (resScale != 0)
        {
            // Cross-component prediction adds the scaled luma residual even when the chroma block codes none.
            Array.Clear(_residual, 0, 1 << (2 * log2C));
            AddCrossComponent(log2C, resScale);
            HevcTransform.Add(_picture.Planes[c], (yC * _picture.Widths[c]) + xC, _picture.Widths[c], _residual, log2C, _sps.BitDepthChroma);
        }
    }

    /// <summary>cross_comp_pred (7.3.8.12): log2_res_scale_abs_plus1 and res_scale_sign_flag, giving ResScaleVal.</summary>
    private int CrossComponentPrediction(int c)
    {
        int abs = 0;
        while (abs < 4 && _cabac.Decode(ref _contexts[HevcContexts.Log2ResScaleAbs + (4 * c) + abs]) == 1)
        {
            abs++;
        }

        if (abs == 0)
        {
            return 0;
        }

        bool negative = _cabac.Decode(ref _contexts[HevcContexts.ResScaleSign + c]) == 1;
        int value = 1 << (abs - 1);
        return negative ? -value : value;
    }

    private void AddCrossComponent(int log2, int resScale)
    {
        int count = 1 << (2 * log2);
        int shiftUp = _sps.BitDepthChroma, shiftDown = _sps.BitDepthLuma;
        for (int i = 0; i < count; i++)
        {
            _residual[i] += (resScale * ((_lumaResidual[i] << shiftUp) >> shiftDown)) >> 3;
        }
    }

    /// <summary>The PU of the coding unit (x, y) lies in: 0 for 2Nx2N, 0 to 3 for NxN.</summary>
    private int PartIndex(int x, int y)
    {
        if (!_intraSplit)
        {
            return 0;
        }

        int half = 1 << (_cuLog2 - 1);
        return (x - _cuX >= half ? 1 : 0) + (y - _cuY >= half ? 2 : 0);
    }

    private int ChromaPartIndex(int x, int y) => _sps.ChromaArrayType == 3 ? PartIndex(x, y) : 0;

    // ---------------------------------------------------------------- per-block maps

    private void FillBlocks<T>(T[] map, int x0, int y0, int width, int height, T value)
    {
        int x4 = x0 >> 2, y4 = y0 >> 2;
        int w4 = Math.Min((x0 + width + 3) >> 2, _picture.Width4) - x4, h4 = Math.Min((y0 + height + 3) >> 2, _picture.Height4) - y4;
        for (int y = 0; y < h4; y++)
        {
            map.AsSpan(((y4 + y) * _picture.Width4) + x4, w4).Fill(value);
        }
    }

    private void FillFlags(int x0, int y0, int size, byte flags)
    {
        int x4 = x0 >> 2, y4 = y0 >> 2;
        int w4 = Math.Min((x0 + size + 3) >> 2, _picture.Width4) - x4, h4 = Math.Min((y0 + size + 3) >> 2, _picture.Height4) - y4;
        for (int y = 0; y < h4; y++)
        {
            for (int x = 0; x < w4; x++)
            {
                _picture.Flags[((y4 + y) * _picture.Width4) + x4 + x] |= flags;
            }
        }
    }

    /// <summary>Marks the left and top edges of a coding or prediction block for the deblocking filter.</summary>
    private void MarkEdges(int x0, int y0, int width, int height) => MarkTransformEdges(x0, y0, Math.Max(width, height));

    /// <summary>Marks a transform block's left and top edges.</summary>
    private void MarkTransformEdges(int x0, int y0, int size)
    {
        int x4 = x0 >> 2, y4 = y0 >> 2;
        int w4 = Math.Min((x0 + size + 3) >> 2, _picture.Width4) - x4, h4 = Math.Min((y0 + size + 3) >> 2, _picture.Height4) - y4;
        for (int y = 0; y < h4; y++)
        {
            _picture.Flags[((y4 + y) * _picture.Width4) + x4] |= HevcPicture.EdgeLeft;
        }

        for (int x = 0; x < w4; x++)
        {
            _picture.Flags[(y4 * _picture.Width4) + x4 + x] |= HevcPicture.EdgeTop;
        }
    }

    /// <summary>Gets Qp'Y or Qp'Cb / Qp'Cr for a component (8.6.1).</summary>
    private int Qp(int c)
    {
        if (c == 0)
        {
            return _qpY + _sps.QpBdOffsetLuma;
        }

        int offset = c == 1 ? _pps.CbQpOffset + _slice.CbQpOffset + _cuQpOffsetCb : _pps.CrQpOffset + _slice.CrQpOffset + _cuQpOffsetCr;
        int qpi = Math.Clamp(_qpY + offset, -_sps.QpBdOffsetChroma, 57);
        int qpc = _sps.ChromaArrayType == 1 ? ChromaQp(qpi) : Math.Min(qpi, 51);
        return qpc + _sps.QpBdOffsetChroma;
    }

    /// <summary>QpC from qPi for 4:2:0 (Table 8-10).</summary>
    internal static int ChromaQp(int qpi) => qpi < 30 ? qpi : qpi > 43 ? qpi - 6 : ChromaQpTable[qpi - 30];

    private void StartCabac(byte[] data, int start, int end)
    {
        (_data, _dataEnd) = (data, end);
        _cabac.Start(data, start, end);
    }
}
