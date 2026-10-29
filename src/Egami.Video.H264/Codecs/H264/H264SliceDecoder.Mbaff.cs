using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// MBAFF frames (7.3.4, 6.4.10.1, 6.4.12.2): macroblock pairs, each two frame macroblocks or a top and a bottom field
/// macroblock. A macroblock is decoded in a window of its own, the size of the frame: the macroblock at its place in its
/// picture (the frame, or the field of a field macroblock), with its neighbours A, B, C and D and their blocks around it.
/// Before a macroblock is decoded the window is filled from the frame with the
/// neighbouring blocks that 6.4.12.2 names, their motion converted between frame and field units (8.4.1.3.1, 9.3.3.1.1.7), so
/// the rest of the slice decoder reads its neighbours as in any frame; after, the macroblock is copied into the frame.
/// </summary>
internal sealed partial class H264SliceDecoder
{
    // The windows a macroblock is decoded in: one for frame macroblocks beside field ones, and one per field parity for field
    // macroblocks. A field window keeps what its macroblocks leave, so a field macroblock beside field pairs finds its
    // neighbours there as in a field picture; only neighbours in frame pairs are copied in. A frame macroblock whose
    // neighbours are all frame macroblocks is decoded in the frame's own state, as in a progressive frame.
    private H264MbState _win = null!;
    private H264Picture _winMo = null!;
    private readonly H264MbState[] _fieldWin = new H264MbState[2];
    private readonly H264Picture[] _fieldWinMo = new H264Picture[2];

    // Whether the current macroblock is decoded in a window (and copied into the frame after), and whether its left and top
    // neighbours were copied in, so their context conditions, intra availability and D motion are the ones worked out then.
    private bool _windowed;
    private bool _mixedLeft;
    private bool _mixedTop;

    // Per list and block row 1 to 3 of the left column: the motion at (-1, 4 by - 1), D of a partition starting at row by.
    private readonly sbyte[] _leftDRef = new sbyte[8];
    private readonly int[] _leftDMv = new int[8];

    // Precomputed context conditions for neighbours in other macroblocks: ref_idx (per list, A rows then B columns), the
    // Intra4x4/8x8 mode (A rows then B columns, -1 for unavailable), and coded_block_pattern's condTermFlagA per 8x8 row.
    private readonly bool[] _refCond = new bool[16];
    private readonly sbyte[] _modeCond = new sbyte[8];
    private readonly int[] _cbpCondA = new int[2];

    // Which rows of the left neighbour (bit y) may be used for intra prediction, luma and chroma, once worked out.
    private bool _leftIntraKnown;
    private int _leftIntraLuma;
    private int _leftIntraChroma;

    private bool _mbaff;
    private H264Picture _frame = null!;
    private H264MbState _real = null!;
    private H264Picture[] _frameList0 = [];
    private H264Picture[] _frameList1 = [];
    private int[] _frameImplicit = [];
    private readonly H264Picture[][] _fieldList0 = [[], []];
    private readonly H264Picture[][] _fieldList1 = [[], []];
    private readonly int[][] _fieldImplicit = [[], []];

    // The current macroblock: its MBAFF address, its raster index in the frame, and its pair's.
    private int _mbaffAddr;
    private int _realAddr;
    private int _pairTop;
    private bool _bottom;

    // The current pair's place, and the top macroblocks of the pairs left, above, above left and above right of it (-1 when
    // outside the picture or not in the slice).
    private int _pairX;
    private int _pairY;
    private int _pairLeft;
    private int _pairAbove;
    private int _pairAboveLeft;
    private int _pairAboveRight;

    // The pair whose neighbouring pairs were last worked out (-1 at the start of a slice).
    private int _positionPair = -1;

    // Per raster index in the frame: the index of its macroblock's first 4x4 block in the frame's block arrays, rw4 a row.
    private int[] _blockBase = [];
    private int _rw4;

    // Which of the eight cases of Table 6-4 a left neighbour is in: field macroblock (4), bottom macroblock (2), field left pair (1).
    private int _leftCase;

    // Per case and row y of a left neighbour (xN -1, yN y): bit 0 whether it is in the left pair's bottom macroblock, the rest
    // its row yM there; for luma (maxH 16) and for 4:2:0 chroma (maxH 8).
    private static readonly byte[] LeftLuma = LeftTable(16);
    private static readonly byte[] LeftChroma8 = LeftTable(8);

    /// <summary>Sets up an MBAFF slice: the frame's state, the field reference lists, and the window as the decoder's arrays.</summary>
    private void StartMbaff(H264Picture frame, H264MbState mb, H264Picture[] list0, H264Picture[] list1, int[] implicitWeights)
    {
        _mbaff = true;
        _positionPair = -1;
        _frame = frame;
        _real = mb;
        _frameList0 = list0;
        _frameList1 = list1;
        _frameImplicit = implicitWeights;
        for (int parity = 0; parity < 2; parity++)
        {
            // A field macroblock's references (8.2.4.2.5 for MBAFF): each frame's field of its parity, then the other.
            _fieldList0[parity] = SplitList(list0, parity);
            _fieldList1[parity] = SplitList(list1, parity);
            _fieldImplicit[parity] = implicitWeights.Length == 0 ? [] : H264Decoder.ImplicitWeights(frame.Field(parity), _fieldList0[parity], _fieldList1[parity]);
        }

        if (_win is null || _win.WidthMbs != mb.WidthMbs || _win.HeightMbs != mb.HeightMbs || _win.ChromaH4 != mb.ChromaH4)
        {
            // Each window has as many macroblocks and blocks as the frame.
            _win = new H264MbState(mb.WidthMbs, mb.HeightMbs, frame.ChromaFormatIdc);
            _winMo = new H264Picture(mb.WidthMbs, mb.HeightMbs, 0, narrow: true);
            for (int parity = 0; parity < 2; parity++)
            {
                _fieldWin[parity] = new H264MbState(mb.WidthMbs, mb.HeightMbs, frame.ChromaFormatIdc);
                _fieldWinMo[parity] = new H264Picture(mb.WidthMbs, mb.HeightMbs, 0, narrow: true);
            }
        }

        // What the field windows hold from earlier slices and pictures is not available.
        _fieldWin[0].Clear();
        _fieldWin[1].Clear();

        int rw4 = mb.WidthMbs * 4;
        _rw4 = rw4;
        if (_blockBase.Length != mb.WidthMbs * mb.HeightMbs)
        {
            _blockBase = new int[mb.WidthMbs * mb.HeightMbs];
            for (int i = 0; i < _blockBase.Length; i++)
            {
                _blockBase[i] = ((i / mb.WidthMbs) * 4 * rw4) + ((i % mb.WidthMbs) * 4);
            }
        }

        UseState(_win, _winMo);
    }

    /// <summary>Makes a window, or the frame's own state, the arrays the decoder reads and writes.</summary>
    private void UseState(H264MbState mb, H264Picture mo)
    {
        if (ReferenceEquals(_mb, mb))
        {
            return;
        }

        _mb = mb;
        _mo = mo;
        _refIdx0 = mo.RefIdx[0];
        _refIdx1 = mo.RefIdx[1];
        _mv0 = mo.Mv[0];
        _mv1 = mo.Mv[1];
        _w4 = mb.W4;
    }

    private static H264Picture[] SplitList(H264Picture[] frames, int parity)
    {
        H264Picture[] fields = new H264Picture[frames.Length * 2];
        for (int i = 0; i < frames.Length; i++)
        {
            fields[2 * i] = frames[i].Field(parity);
            fields[(2 * i) + 1] = frames[i].Field(1 - parity);
        }

        return fields;
    }

    /// <summary>Decodes the macroblock pairs of an MBAFF slice from first_mb_in_slice (a top macroblock).</summary>
    private void DecodeMbaffSlice(H264SliceHeader h, byte[] padded, int length)
    {
        int mbAddr = h.FirstMb;
        int count = _sps.WidthInMbs * _sps.HeightInMbs;
        bool field = false;
        if (_cabac)
        {
            InitContexts();
            _engine.Start(padded, (int)((h.DataBitPosition + 7) >> 3), length);
            bool bottomSkipKnown = false, bottomSkip = false;
            while (true)
            {
                CheckMbaffAddress(mbAddr, count);
                bool top = (mbAddr & 1) == 0;
                if (top)
                {
                    field = InferField(mbAddr);
                }

                bool skip;
                if (!top && bottomSkipKnown)
                {
                    skip = bottomSkip;
                    bottomSkipKnown = false;
                }
                else
                {
                    skip = !h.IsI && SkipFlagMbaff(mbAddr, field);
                }

                if (skip)
                {
                    if (top)
                    {
                        // The bottom macroblock's mb_skip_flag comes next, and its mb_field_decoding_flag when it is coded,
                        // which this skipped macroblock takes too.
                        bottomSkip = SkipFlagMbaff(mbAddr + 1, field);
                        bottomSkipKnown = true;
                        if (!bottomSkip)
                        {
                            field = ReadFieldFlag(mbAddr);
                        }
                    }

                    PrepareMbaff(mbAddr, field);
                    DecodeSkip(_mbAddr);
                    StoreMbaff();
                }
                else
                {
                    if (top)
                    {
                        field = ReadFieldFlag(mbAddr);
                    }

                    PrepareMbaff(mbAddr, field);
                    DecodeMacroblock(_mbAddr);
                    StoreMbaff();
                }

                if (_engine.Overrun)
                {
                    throw H264Errors.Corrupt("A CABAC slice runs past its data.");
                }

                if (!top && _engine.Terminate() != 0)
                {
                    break;
                }

                mbAddr++;
            }

            return;
        }

        _bits.Start(padded, length, (int)h.DataBitPosition);
        bool more = true;
        while (more)
        {
            if (!h.IsI)
            {
                int run = (int)_bits.Ue((uint)(count - mbAddr), "mb_skip_run");
                for (int i = 0; i < run; i++)
                {
                    CheckMbaffAddress(mbAddr, count);
                    if ((mbAddr & 1) == 0)
                    {
                        field = InferField(mbAddr);
                        if (i == run - 1 && _bits.MoreRbspData())
                        {
                            // The bottom macroblock is coded: its mb_field_decoding_flag, next in the stream, is the pair's.
                            field = _bits.Flag();
                        }
                    }

                    PrepareMbaff(mbAddr, field);
                    DecodeSkip(_mbAddr);
                    StoreMbaff();
                    mbAddr++;
                }

                if (run > 0 && !_bits.MoreRbspData())
                {
                    break;
                }
            }

            CheckMbaffAddress(mbAddr, count);
            if ((mbAddr & 1) == 0)
            {
                // A bottom macroblock has its pair's flag: from the top macroblock, or read ahead when that was skipped.
                field = _bits.Flag();
            }

            PrepareMbaff(mbAddr, field);
            DecodeMacroblock(_mbAddr);
            StoreMbaff();
            if (_bits.Overrun)
            {
                throw H264Errors.Corrupt("A CAVLC slice runs past its data.");
            }

            more = _bits.MoreRbspData();
            mbAddr++;
        }
    }

    private void CheckMbaffAddress(int mbAddr, int count)
    {
        if (mbAddr >= count)
        {
            throw H264Errors.Corrupt("A slice has more macroblocks than the picture.");
        }

        int pair = mbAddr >> 1, raster = pair == _positionPair ? _pairTop : RasterOf(mbAddr & ~1);
        if (_real.Slice[raster + ((mbAddr & 1) * _widthMbs)] >= 0)
        {
            throw H264Errors.Corrupt($"Macroblock {mbAddr} is coded twice.");
        }
    }

    /// <summary>Gets the raster index in the frame of the macroblock at an MBAFF address.</summary>
    private int RasterOf(int mbAddr)
    {
        int pair = mbAddr >> 1, w = _sps.WidthInMbs;
        return (((2 * (pair / w)) + (mbAddr & 1)) * w) + (pair % w);
    }

    /// <summary>Gets the raster index of the top macroblock of a pair, or -1 when the pair is outside the picture or not available.</summary>
    private int PairTop(int pairX, int pairY)
    {
        int w = _sps.WidthInMbs;
        if (pairX < 0 || pairX >= w || pairY < 0)
        {
            return -1;
        }

        int top = (2 * pairY * w) + pairX;
        return _real.Slice[top] == _slice ? top : -1;
    }

    /// <summary>
    /// The inferred mb_field_decoding_flag (7.4.4) of a pair before (or without) its own: the left pair's, else the pair
    /// above's, else frame.
    /// </summary>
    private bool InferField(int mbAddr)
    {
        int pair = mbAddr >> 1, w = _sps.WidthInMbs;
        int left = PairTop((pair % w) - 1, pair / w), above = PairTop(pair % w, (pair / w) - 1);
        return left >= 0 ? _real.FieldMb[left] : above >= 0 && _real.FieldMb[above];
    }

    private bool ReadFieldFlag(int mbAddr)
    {
        if (!_cabac)
        {
            return _bits.Flag();
        }

        int pair = mbAddr >> 1, w = _sps.WidthInMbs;
        int left = PairTop((pair % w) - 1, pair / w), above = PairTop(pair % w, (pair / w) - 1);
        int inc = (left >= 0 && _real.FieldMb[left] ? 1 : 0) + (above >= 0 && _real.FieldMb[above] ? 1 : 0);
        return Bin(70 + inc) != 0;
    }

    private bool SkipFlagMbaff(int mbAddr, bool field)
    {
        // Only A and B (6.4.10.1) matter here, found in the frame without filling the window.
        SetPosition(mbAddr, field);
        int left = Locate(-1, 0, 16, out _), top = Locate(0, -1, 16, out _);
        int inc = (InSlice(left) && !H264MbState.IsSkip(_real.Kind[left]) ? 1 : 0) + (InSlice(top) && !H264MbState.IsSkip(_real.Kind[top]) ? 1 : 0);
        return Bin((_h.IsB ? 24 : 11) + inc) != 0;
    }

    private bool InSlice(int mb) => mb >= 0 && _real.Slice[mb] == _slice;

    /// <summary>Sets the current MBAFF macroblock's address, field decoding and the neighbouring pairs, which Locate reads.</summary>
    private void SetPosition(int mbAddr, bool field)
    {
        int w = _sps.WidthInMbs, pair = mbAddr >> 1;
        if (pair != _positionPair)
        {
            // Both macroblocks of a pair have the same neighbouring pairs, decoded before either.
            int pairY = pair / w, pairX = pair - (pairY * w);
            _positionPair = pair;
            _pairX = pairX;
            _pairY = pairY;
            _pairTop = (2 * pairY * w) + pairX;
            _pairLeft = PairTop(pairX - 1, pairY);
            _pairAbove = PairTop(pairX, pairY - 1);
            _pairAboveLeft = PairTop(pairX - 1, pairY - 1);
            _pairAboveRight = PairTop(pairX + 1, pairY - 1);
        }

        _mbaffAddr = mbAddr;
        _bottom = (mbAddr & 1) != 0;
        _realAddr = _pairTop + (_bottom ? w : 0);
        _real.FieldMb[_pairTop] = _real.FieldMb[_pairTop + w] = field;
        _leftCase = (field ? 4 : 0) | (_bottom ? 2 : 0) | (_pairLeft >= 0 && _real.FieldMb[_pairLeft] ? 1 : 0);
        if (_fieldMb != field)
        {
            SetFieldMb(field);
        }
    }

    /// <summary>The left neighbour rows of Table 6-4 (xN less than 0, yN 0 to maxH - 1) for each of the eight cases.</summary>
    private static byte[] LeftTable(int maxH)
    {
        byte[] table = new byte[8 * 16];
        for (int c = 0; c < 8; c++)
        {
            bool field = (c & 4) != 0, top = (c & 2) == 0, fieldX = (c & 1) != 0;
            for (int yN = 0; yN < maxH; yN++)
            {
                int mbN, yM;
                if (!field)
                {
                    (mbN, yM) = top ? (fieldX ? (yN & 1, yN >> 1) : (0, yN)) : fieldX ? (yN & 1, (yN + maxH) >> 1) : (1, yN);
                }
                else if (!fieldX)
                {
                    mbN = yN < maxH / 2 ? 0 : 1;
                    yM = ((yN << 1) + (top ? 0 : 1)) - (mbN * maxH);
                }
                else
                {
                    (mbN, yM) = (top ? 0 : 1, yN);
                }

                table[(c * 16) + yN] = (byte)(mbN | (yM << 1));
            }
        }

        return table;
    }

    /// <summary>The macroblock holding left neighbour row y (Table 6-4) from a <see cref="LeftTable"/>, or -1, and its row yM there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftAt(int y, byte[] table, out int yM)
    {
        int e = table[(_leftCase * 16) + y];
        yM = e >> 1;
        return _pairLeft < 0 ? -1 : _pairLeft + ((e & 1) * _widthMbs);
    }

    /// <summary>
    /// Makes an MBAFF macroblock current: its picture (the frame, or the field of a field macroblock) and position, its
    /// references, and the window with its neighbours.
    /// </summary>
    private void PrepareMbaff(int mbAddr, bool field)
    {
        SetPosition(mbAddr, field);
        int w = _sps.WidthInMbs, pairX = _pairX, pairY = _pairY;
        int parity = _bottom ? 1 : 0;
        _mbX = pairX;
        _mbY = field ? pairY : (2 * pairY) + parity;
        _refCount0 = _h.NumRefIdxL0 << (field ? 1 : 0);
        _refCount1 = _h.NumRefIdxL1 << (field ? 1 : 0);
        H264Picture pic = field ? _frame.Field(parity) : _frame;
        H264Picture[] list0 = field ? _fieldList0[parity] : _frameList0;
        if (!ReferenceEquals(_pic, pic) || !ReferenceEquals(_list0, list0))
        {
            // Stored only when they change, which spares the write barriers of most macroblocks.
            _pic = pic;
            _list0 = list0;
            _list1 = field ? _fieldList1[parity] : _frameList1;
            _implicitWeights = field ? _fieldImplicit[parity] : _frameImplicit;
            _colShortTerm = _list1.Length > 0 && !_list1[0].IsLongTermReference;
        }

        bool[] fieldMb = _real.FieldMb;
        if (field)
        {
            UseState(_fieldWin[parity], _fieldWinMo[parity]);
            _windowed = true;
        }
        else
        {
            // A frame macroblock among frame pairs reads its neighbours in the frame as they are: its left pair, and for a
            // top macroblock the pairs above (a bottom one's C is never available, and its B is its own pair's top).
            bool direct = (_pairLeft < 0 || !fieldMb[_pairLeft])
                && (_bottom || ((_pairAbove < 0 || !fieldMb[_pairAbove]) && (_pairAboveLeft < 0 || !fieldMb[_pairAboveLeft])
                    && (_pairAboveRight < 0 || !fieldMb[_pairAboveRight])));
            if (direct)
            {
                UseState(_real, _frame);
            }
            else
            {
                UseState(_win, _winMo);
            }

            _windowed = !direct;
        }

        _inter.SetMacroblock(_pic, _list0, _list1, _implicitWeights, _mo, _w4);
        _mbAddr = (_mbY * w) + _mbX;
        _b0 = (_mbY * 4 * _w4) + (_mbX * 4);
        _leftIntraKnown = false;
        if (!_windowed)
        {
            _mixedLeft = _mixedTop = false;
        }
        else if (!field)
        {
            // The frame window keeps nothing between macroblocks: every neighbour is copied in.
            _mixedLeft = _mixedTop = true;
            _mb.Slice[_mbAddr] = -1;
            LoadLeft();
            LoadTop();
            LoadCorner(16);
            LoadCorner(-1);
        }
        else
        {
            // A field window has the neighbours in field pairs already; those in frame pairs are copied in, converted.
            _mixedLeft = _pairLeft >= 0 && !fieldMb[_pairLeft];
            _mixedTop = _pairAbove >= 0 && !fieldMb[_pairAbove];
            _mb.Slice[_mbAddr] = -1;
            if (_mixedLeft)
            {
                LoadLeft();
            }

            if (_mixedTop)
            {
                LoadTop();
            }

            if (_pairAboveRight >= 0 && !fieldMb[_pairAboveRight])
            {
                LoadCorner(16);
            }

            if (_pairAboveLeft >= 0 && !fieldMb[_pairAboveLeft])
            {
                LoadCorner(-1);
            }
        }

        // Which neighbours are there (6.4.9 as 6.4.10.1 finds them).
        int cur = _mbAddr;
        _neighbors = (_mbX > 0 && Available(cur - 1) ? NeighborA : 0) | (_mbY > 0 && Available(cur - w) ? NeighborB : 0)
            | (_mbY > 0 && _mbX < w - 1 && Available(cur - w + 1) ? NeighborC : 0) | (_mbY > 0 && _mbX > 0 && Available(cur - w - 1) ? NeighborD : 0);
    }

    /// <summary>
    /// The neighbouring location (xN, yN) of the current macroblock (6.4.12.2, Table 6-4): the raster index of the macroblock
    /// holding it, or -1 when it is not available, and its row yM there. maxH is 16 for luma, the chroma height for chroma.
    /// </summary>
    private int Locate(int xN, int yN, int maxH, out int yM)
    {
        int w = _sps.WidthInMbs;
        bool frame = !_fieldMb, top = !_bottom;
        yM = yN;
        if (yN >= 0 && xN >= 0 && xN < 16)
        {
            return _realAddr;
        }

        int x, mbN;
        if (xN < 0 && yN < 0)
        {
            if (frame)
            {
                if (top)
                {
                    return Mb(_pairAboveLeft, 1);
                }

                // Beside a field pair, the bottom field's row above the macroblock's middle (6.4.12.2).
                x = _pairLeft;
                if (x >= 0 && _real.FieldMb[x])
                {
                    yM = (yN + maxH) >> 1;
                    return Mb(x, 1);
                }

                return Mb(x, 0);
            }

            x = _pairAboveLeft;
            if (top)
            {
                if (x >= 0 && !_real.FieldMb[x])
                {
                    yM = 2 * yN;
                    return Mb(x, 1);
                }

                return Mb(x, 0);
            }

            return Mb(x, 1);
        }

        if (xN < 0)
        {
            x = _pairLeft;
            if (x < 0)
            {
                return -1;
            }

            bool fieldX = _real.FieldMb[x];
            if (frame)
            {
                if (top)
                {
                    if (!fieldX)
                    {
                        return x;
                    }

                    yM = yN >> 1;
                    return Mb(x, yN & 1);
                }

                if (!fieldX)
                {
                    return Mb(x, 1);
                }

                yM = (yN + maxH) >> 1;
                return Mb(x, yN & 1);
            }

            if (!fieldX)
            {
                mbN = yN < maxH / 2 ? 0 : 1;
                yM = ((yN << 1) + (top ? 0 : 1)) - (mbN * maxH);
                return Mb(x, mbN);
            }

            return Mb(x, top ? 0 : 1);
        }

        if (xN < 16)
        {
            if (frame)
            {
                return top ? Mb(_pairAbove, 1) : _pairTop;
            }

            x = _pairAbove;
            if (top && x >= 0 && !_real.FieldMb[x])
            {
                yM = 2 * yN;
                return Mb(x, 1);
            }

            return Mb(x, top ? 0 : 1);
        }

        if (frame)
        {
            return top ? Mb(_pairAboveRight, 1) : -1;
        }

        x = _pairAboveRight;
        if (top && x >= 0 && !_real.FieldMb[x])
        {
            yM = 2 * yN;
            return Mb(x, 1);
        }

        return Mb(x, top ? 0 : 1);

        int Mb(int pairTop, int bottom) => pairTop < 0 ? -1 : pairTop + (bottom * w);
    }

    /// <summary>
    /// Copies the left neighbour A of 6.4.10.1 into the window, with the blocks left of the macroblock (their motion in the
    /// current macroblock's units), their context conditions, coded_block_pattern's condTermFlagA and the D motion of the left
    /// column.
    /// </summary>
    private void LoadLeft()
    {
        H264MbState win = _mb, real = _real;
        byte[] left = LeftLuma;
        int rw4 = _rw4, lists = _h.IsB ? 2 : 1;
        LoadMb(_mbX > 0 ? _mbAddr - 1 : -1, LeftAt(0, left, out _));
        for (int by = 0; by < 4; by++)
        {
            int mb = LeftAt(4 * by, left, out int yM);
            SetConditions(mb, LoadBlock(mb, 3, yM >> 2, _b0 + (by * _w4) - 1, lists), 3, yM >> 2, by, true);
            if ((by & 1) == 0)
            {
                // coded_block_pattern's condTermFlagA for the left 8x8 blocks (9.3.3.1.1.4).
                int b8 = ((yM >> 3) * 2) + 1;
                _cbpCondA[by >> 1] = mb < 0 ? 0
                    : real.Kind[mb] == H264MbKind.IPcm ? 0
                    : !H264MbState.IsSkip(real.Kind[mb]) && ((real.Cbp[mb] >> b8) & 1) != 0 ? 0
                    : 1;
            }

            if (by > 0)
            {
                int dl = LeftAt((4 * by) - 1, left, out int yDl);
                int dSrc = dl < 0 ? -1 : _blockBase[dl] + ((yDl >> 2) * rw4) + 3;
                for (int list = 0; list < lists; list++)
                {
                    LoadMotion(dl, dSrc, list, out _leftDRef[(list * 4) + by], out _leftDMv[(list * 4) + by]);
                }
            }
        }

        if (_chromaArrayType is 1 or 2)
        {
            int rows = _chromaArrayType == 1 ? 2 : 4, rcw = real.ChromaW4;
            byte[] leftC = rows == 2 ? LeftChroma8 : LeftLuma;
            int at = ChromaBlock(0, 0);
            for (int cy = 0; cy < rows; cy++)
            {
                int mb = LeftAt(4 * cy, leftC, out int yM);
                int dst = at + (cy * win.ChromaW4) - 1;
                if (mb >= 0)
                {
                    int src = ((((mb / _sps.WidthInMbs) * rows) + (yM >> 2)) * rcw) + ((mb % _sps.WidthInMbs) * 2) + 1;
                    win.NzCb[dst] = real.NzCb[src];
                    win.NzCr[dst] = real.NzCr[src];
                }
            }
        }
    }

    /// <summary>Copies the top neighbour B of 6.4.10.1 into the window, with the blocks above the macroblock and their conditions.</summary>
    private void LoadTop()
    {
        H264MbState win = _mb, real = _real;
        int w = _widthMbs, lists = _h.IsB ? 2 : 1;
        int b = Locate(0, -1, 16, out int yB);
        LoadMb(_mbY > 0 ? _mbAddr - w : -1, b);
        int topRow = ((yB + 16) >> 2) & 3;
        for (int bx = 0; bx < 4; bx++)
        {
            SetConditions(b, LoadBlock(b, bx, topRow, _b0 - _w4 + bx, lists), bx, topRow, bx, false);
        }

        if (_chromaArrayType is 1 or 2 && b >= 0)
        {
            // The row above: the same macroblock and row yM as luma's (Table 6-4 does not depend on maxH there).
            int rows = _chromaArrayType == 1 ? 2 : 4, maxH = rows * 4, rcw = real.ChromaW4;
            int src = ((((b / _sps.WidthInMbs) * rows) + (((yB + maxH) >> 2) % rows)) * rcw) + ((b % _sps.WidthInMbs) * 2);
            int dst = ChromaBlock(0, 0) - win.ChromaW4;
            win.NzCb[dst] = real.NzCb[src];
            win.NzCr[dst] = real.NzCr[src];
            win.NzCb[dst + 1] = real.NzCb[src + 1];
            win.NzCr[dst + 1] = real.NzCr[src + 1];
        }
    }

    /// <summary>Copies neighbour C (xN 16) or D (xN -1) of 6.4.10.1 into the window, with its block next to the macroblock's corner.</summary>
    private void LoadCorner(int xN)
    {
        int w = _widthMbs, lists = _h.IsB ? 2 : 1;
        int mb = Locate(xN, -1, 16, out int yM);
        if (xN > 0)
        {
            LoadMb(_mbY > 0 && _mbX < w - 1 ? _mbAddr - w + 1 : -1, mb);
            LoadBlock(mb, 0, ((yM + 16) >> 2) & 3, _b0 - _w4 + 4, lists);
        }
        else
        {
            LoadMb(_mbY > 0 && _mbX > 0 ? _mbAddr - w - 1 : -1, mb);
            LoadBlock(mb, 3, ((yM + 16) >> 2) & 3, _b0 - _w4 - 1, lists);
        }
    }

    /// <summary>
    /// Works out, for an intra macroblock, which rows of left samples are in a macroblock it may predict from (8.3.1.2: inter
    /// ones are not available with constrained intra prediction).
    /// </summary>
    private void FindLeftIntra()
    {
        _leftIntraKnown = true;
        _leftIntraLuma = _leftIntraChroma = 0;
        int chromaRows = _chromaArrayType == 0 ? 0 : _chromaArrayType == 1 ? 8 : 16;
        byte[] leftC = chromaRows == 8 ? LeftChroma8 : LeftLuma;
        for (int y = 0; y < 16; y++)
        {
            _leftIntraLuma |= LeftRowIntra(LeftAt(y, LeftLuma, out _)) ? 1 << y : 0;
            if (y < chromaRows)
            {
                _leftIntraChroma |= LeftRowIntra(LeftAt(y, leftC, out _)) ? 1 << y : 0;
            }
        }
    }

    private bool LeftRowIntra(int mb) =>
        mb >= 0 && _real.Slice[mb] == _slice && (!_pps.ConstrainedIntraPred || H264MbState.IsIntra(_real.Kind[mb]));

    /// <summary>Copies a neighbouring macroblock's macroblock-level state into a window slot, or marks the slot unavailable.</summary>
    private void LoadMb(int slot, int mb)
    {
        H264MbState win = _mb, real = _real;
        if (slot < 0)
        {
            return;
        }

        if (mb < 0 || real.Slice[mb] != _slice)
        {
            win.Slice[slot] = -1;
            return;
        }

        win.Slice[slot] = _slice;
        win.Kind[slot] = real.Kind[mb];
        win.Cbp[slot] = real.Cbp[mb];
        win.Transform8x8[slot] = real.Transform8x8[mb];
        win.CbfDc[slot] = real.CbfDc[mb];
        win.Direct8x8[slot] = real.Direct8x8[mb];
        win.ChromaPredMode[slot] = real.ChromaPredMode[mb];
        win.QpDeltaNonZero[slot] = real.QpDeltaNonZero[mb];
        _mo.MbIntra[slot] = _frame.MbIntra[mb];
    }

    /// <summary>
    /// Copies block (bx, by) of a neighbouring macroblock into window block <paramref name="dst"/>, motion converted for the
    /// first <paramref name="lists"/> lists (a P slice's neighbours have no list 1 motion that anything reads), and returns its
    /// index in the frame, or -1 when the macroblock is not available.
    /// </summary>
    private int LoadBlock(int mb, int bx, int by, int dst, int lists)
    {
        H264MbState win = _mb, real = _real;
        H264Picture mo = _mo;
        if (!InSlice(mb))
        {
            return -1;
        }

        int src = _blockBase[mb] + (by * _rw4) + bx;
        win.NzLuma[dst] = real.NzLuma[src];
        win.IntraModes[dst] = real.IntraModes[src];
        bool neighborField = real.FieldMb[mb];
        for (int list = 0; list < lists; list++)
        {
            int r = _frame.RefIdx[list][src];
            int m = 0;
            if (r >= 0)
            {
                m = _frame.Mv[list][src];
                if (_fieldMb && !neighborField)
                {
                    r *= 2;
                    m = PackMv(MvX(m), MvY(m) / 2);
                }
                else if (!_fieldMb && neighborField)
                {
                    r >>= 1;
                    m = PackMv(MvX(m), MvY(m) * 2);
                }
            }

            mo.RefIdx[list][dst] = (sbyte)r;
            mo.Mv[list][dst] = m;
            mo.RefPicId[list][dst] = _frame.RefPicId[list][src];
            byte[] from = real.Mvd[list], to = win.Mvd[list];
            byte mx = from[src * 2], my = from[(src * 2) + 1];
            if (_fieldMb != neighborField)
            {
                // 9.3.3.1.1.7: a field neighbour's vertical mvd doubles for a frame macroblock, a frame one's halves for a field one.
                my = (byte)(_fieldMb ? my >> 1 : Math.Min(my * 2, 255));
            }

            to[dst * 2] = mx;
            to[(dst * 2) + 1] = my;
        }

        return src;
    }

    /// <summary>
    /// The motion of frame block src, in macroblock mb (-1 when not available), in the current macroblock's units (8.4.1.3.1):
    /// a frame neighbour of a field macroblock has its vertical vector halved and its reference index doubled, a field
    /// neighbour of a frame macroblock the reverse.
    /// </summary>
    private void LoadMotion(int mb, int src, int list, out sbyte refIdx, out int mv)
    {
        refIdx = -1;
        mv = 0;
        if (src < 0)
        {
            return;
        }

        int r = _frame.RefIdx[list][src];
        int m = _frame.Mv[list][src];
        if (r >= 0)
        {
            bool neighborField = _real.FieldMb[mb];
            if (_fieldMb && !neighborField)
            {
                r *= 2;
                m = PackMv(MvX(m), MvY(m) / 2);
            }
            else if (!_fieldMb && neighborField)
            {
                r >>= 1;
                m = PackMv(MvX(m), MvY(m) * 2);
            }
        }

        refIdx = (sbyte)r;
        mv = r >= 0 ? m : 0;
    }

    /// <summary>
    /// Works out the context conditions a neighbouring block in another macroblock gives: ref_idx's condTermFlag (9.3.3.1.1.6)
    /// per list and the predicted intra mode (8.3.1.1), which depend on the macroblock that block is in.
    /// </summary>
    private void SetConditions(int mb, int src, int bx, int by, int index, bool left)
    {
        H264MbState real = _real;
        int o = left ? 0 : 4;
        bool available = src >= 0;
        for (int list = 0; list < 2; list++)
        {
            bool cond = false;
            if (available)
            {
                H264MbKind kind = real.Kind[mb];
                bool direct = (real.Direct8x8[mb] & (1 << (((by >> 1) * 2) + (bx >> 1)))) != 0;
                int r = _frame.RefIdx[list][src];
                if (!H264MbState.IsIntra(kind) && !H264MbState.IsSkip(kind) && kind != H264MbKind.BDirect && !direct && r >= 0)
                {
                    // A field neighbour of a frame macroblock counts from 2 (refIdxZeroFlagN).
                    cond = !_fieldMb && real.FieldMb[mb] ? r > 1 : r > 0;
                }
            }

            _refCond[(list * 8) + o + index] = cond;
        }

        sbyte mode = -1;
        if (available)
        {
            H264MbKind kind = real.Kind[mb];
            mode = !H264MbState.IsIntra(kind) ? (sbyte)(_pps.ConstrainedIntraPred ? -1 : 2)
                : kind is H264MbKind.I4x4 or H264MbKind.I8x8 ? real.IntraModes[src]
                : (sbyte)2;
        }

        _modeCond[o + index] = mode;
    }

    /// <summary>Copies the current macroblock from the window into the frame.</summary>
    private void StoreMbaff()
    {
        if (!_windowed)
        {
            // Decoded in the frame's own state.
            return;
        }

        H264MbState win = _mb, real = _real;
        H264Picture winMo = _mo;
        int addr = _realAddr;
        real.Slice[addr] = win.Slice[_mbAddr];
        real.Kind[addr] = win.Kind[_mbAddr];
        real.Qp[addr] = win.Qp[_mbAddr];
        real.QpC[0][addr] = win.QpC[0][_mbAddr];
        real.QpC[1][addr] = win.QpC[1][_mbAddr];
        real.Cbp[addr] = win.Cbp[_mbAddr];
        real.ChromaPredMode[addr] = win.ChromaPredMode[_mbAddr];
        real.Transform8x8[addr] = win.Transform8x8[_mbAddr];
        real.CbfDc[addr] = win.CbfDc[_mbAddr];
        real.Direct8x8[addr] = win.Direct8x8[_mbAddr];
        real.QpDeltaNonZero[addr] = win.QpDeltaNonZero[_mbAddr];
        real.FlatInside[addr] = win.FlatInside[_mbAddr];
        _frame.MbIntra[addr] = winMo.MbIntra[_mbAddr];
        int w = _sps.WidthInMbs, rw4 = _rw4, w4 = _w4;
        int s0 = _b0, d0 = _blockBase[addr];
        int last = Math.Min(Math.Min(win.NzLuma.Length, real.NzLuma.Length), Math.Min(winMo.Mv[0].Length, _frame.Mv[0].Length));
        if (s0 + (3 * w4) + 4 > last || d0 + (3 * rw4) + 4 > last)
        {
            throw H264Errors.Corrupt("A macroblock lies outside the picture.");
        }

        // All blocks of what the deblocking filter and later pictures' co-located motion read; of what only neighbours read,
        // the right column and the bottom row, the only blocks a neighbour in another window takes (ColZero of an MBAFF
        // frame is never read: a picture co-located with it works colZeroFlag out itself).
        CopyBlocks(win.Nonzero, s0, w4, real.Nonzero, d0, rw4);
        CopyEdges<byte>(win.NzLuma, s0, w4, real.NzLuma, d0, rw4);
        CopyEdges<sbyte>(win.IntraModes, s0, w4, real.IntraModes, d0, rw4);
        for (int list = 0; list < 2; list++)
        {
            CopyBlocks(winMo.RefIdx[list], s0, w4, _frame.RefIdx[list], d0, rw4);
            CopyBlocks(winMo.Mv[list], s0, w4, _frame.Mv[list], d0, rw4);
            CopyBlocks(winMo.RefPicId[list], s0, w4, _frame.RefPicId[list], d0, rw4);
            CopyEdges<ushort>(MemoryMarshal.Cast<byte, ushort>(win.Mvd[list].AsSpan()), s0, w4, MemoryMarshal.Cast<byte, ushort>(real.Mvd[list].AsSpan()), d0, rw4);
        }

        if (_chromaArrayType is 1 or 2)
        {
            // The right column and the bottom row, as for luma.
            int rows = _chromaArrayType == 1 ? 2 : 4, wcw = win.ChromaW4, rcw = real.ChromaW4;
            int cs = ChromaBlock(0, 0), cd = ((addr / w) * rows * rcw) + ((addr % w) * 2);
            byte[] fromCb = win.NzCb, fromCr = win.NzCr, toCb = real.NzCb, toCr = real.NzCr;
            for (int y = 0; y < rows - 1; y++)
            {
                toCb[cd + (y * rcw) + 1] = fromCb[cs + (y * wcw) + 1];
                toCr[cd + (y * rcw) + 1] = fromCr[cs + (y * wcw) + 1];
            }

            int ls = cs + ((rows - 1) * wcw), ld = cd + ((rows - 1) * rcw);
            toCb[ld] = fromCb[ls];
            toCb[ld + 1] = fromCb[ls + 1];
            toCr[ld] = fromCr[ls];
            toCr[ld + 1] = fromCr[ls + 1];
        }
    }

    /// <summary>Copies a macroblock's 4x4 blocks between layouts of sStride and dStride blocks a row; both lie in their arrays.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyBlocks<T>(T[] from, int s, int sStride, T[] to, int d, int dStride)
        where T : unmanaged
    {
        Copy4(from, s, to, d);
        Copy4(from, s + sStride, to, d + dStride);
        Copy4(from, s + (2 * sStride), to, d + (2 * dStride));
        Copy4(from, s + (3 * sStride), to, d + (3 * dStride));
    }

    /// <summary>Copies a macroblock's right column and bottom row of 4x4 blocks between layouts, as <see cref="CopyBlocks"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyEdges<T>(Span<T> from, int s, int sStride, Span<T> to, int d, int dStride)
        where T : unmanaged
    {
        Debug.Assert((uint)s + (3 * sStride) + 4 <= (uint)from.Length && (uint)d + (3 * dStride) + 4 <= (uint)to.Length, "Both macroblocks lie in their arrays.");
        ref T src = ref MemoryMarshal.GetReference(from), dst = ref MemoryMarshal.GetReference(to);
        Unsafe.Add(ref dst, d + 3) = Unsafe.Add(ref src, s + 3);
        Unsafe.Add(ref dst, d + dStride + 3) = Unsafe.Add(ref src, s + sStride + 3);
        Unsafe.Add(ref dst, d + (2 * dStride) + 3) = Unsafe.Add(ref src, s + (2 * sStride) + 3);
        Unsafe.CopyBlockUnaligned(
            ref Unsafe.As<T, byte>(ref Unsafe.Add(ref dst, d + (3 * dStride))),
            ref Unsafe.As<T, byte>(ref Unsafe.Add(ref src, s + (3 * sStride))),
            (uint)(4 * Unsafe.SizeOf<T>()));
    }

    /// <summary>Copies four elements from one array to another; the caller has checked that both runs lie in their arrays.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy4<T>(T[] from, int s, T[] to, int d)
        where T : unmanaged
    {
        Debug.Assert((uint)s + 4 <= (uint)from.Length && (uint)d + 4 <= (uint)to.Length, "Both runs lie in their arrays.");
        Unsafe.CopyBlockUnaligned(
            ref Unsafe.As<T, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(to), d)),
            ref Unsafe.As<T, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(from), s)),
            (uint)(4 * Unsafe.SizeOf<T>()));
    }
}
