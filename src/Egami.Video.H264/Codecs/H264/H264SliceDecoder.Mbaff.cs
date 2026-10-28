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
    private H264MbState _win = null!;
    private H264Picture _winMo = null!;

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

    /// <summary>Sets up an MBAFF slice: the frame's state, the field reference lists, and the window as the decoder's arrays.</summary>
    private void StartMbaff(H264Picture frame, H264MbState mb, H264Picture[] list0, H264Picture[] list1, int[] implicitWeights)
    {
        _mbaff = true;
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
            // The window's slots are its first five macroblocks; its blocks are as many as the frame's.
            _win = new H264MbState(mb.WidthMbs, mb.HeightMbs, frame.ChromaFormatIdc);
            _winMo = new H264Picture(mb.WidthMbs, mb.HeightMbs, 0, narrow: true);
        }

        _mb = _win;
        _mo = _winMo;
        _refIdx0 = _winMo.RefIdx[0];
        _refIdx1 = _winMo.RefIdx[1];
        _mv0 = _winMo.Mv[0];
        _mv1 = _winMo.Mv[1];
        _w4 = _win.W4;
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

        if (_real.Slice[RasterOf(mbAddr)] >= 0)
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
        PrepareMbaff(mbAddr, field);
        int left = LeftMb, top = TopMb;
        int inc = (Available(left) && !H264MbState.IsSkip(_mb.Kind[left]) ? 1 : 0) + (Available(top) && !H264MbState.IsSkip(_mb.Kind[top]) ? 1 : 0);
        return Bin((_h.IsB ? 24 : 11) + inc) != 0;
    }

    /// <summary>
    /// Makes an MBAFF macroblock current: its picture (the frame, or the field of a field macroblock) and position, its
    /// references, and the window with its neighbours.
    /// </summary>
    private void PrepareMbaff(int mbAddr, bool field)
    {
        int w = _sps.WidthInMbs, pair = mbAddr >> 1, pairX = pair % w, pairY = pair / w;
        _mbaffAddr = mbAddr;
        _bottom = (mbAddr & 1) != 0;
        _realAddr = RasterOf(mbAddr);
        _pairTop = (2 * pairY * w) + pairX;
        _real.FieldMb[_pairTop] = _real.FieldMb[_pairTop + w] = field;
        int parity = _bottom ? 1 : 0;
        _pic = field ? _frame.Field(parity) : _frame;
        _mbX = pairX;
        _mbY = field ? pairY : (2 * pairY) + parity;
        SetFieldMb(field);
        _refCount0 = _h.NumRefIdxL0 << (field ? 1 : 0);
        _refCount1 = _h.NumRefIdxL1 << (field ? 1 : 0);
        _list0 = field ? _fieldList0[parity] : _frameList0;
        _list1 = field ? _fieldList1[parity] : _frameList1;
        _implicitWeights = field ? _fieldImplicit[parity] : _frameImplicit;
        _colShortTerm = _list1.Length > 0 && !_list1[0].IsLongTermReference;
        _inter.SetMacroblock(_pic, _list0, _list1, _implicitWeights, _winMo, _w4);
        _mbAddr = (_mbY * w) + _mbX;
        _b0 = (_mbY * 4 * _w4) + (_mbX * 4);

        LoadWindow(pairX, pairY, field);
    }

    /// <summary>
    /// The neighbouring location (xN, yN) of the current macroblock (6.4.12.2, Table 6-4): the raster index of the macroblock
    /// holding it, or -1 when it is not available, and its row yM there. maxH is 16 for luma, the chroma height for chroma.
    /// </summary>
    private int Locate(int xN, int yN, int maxH, out int yM)
    {
        int w = _sps.WidthInMbs, pair = _mbaffAddr >> 1, pairX = pair % w, pairY = pair / w;
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
                    return Mb(PairTop(pairX - 1, pairY - 1), 1);
                }

                // Beside a field pair, the bottom field's row above the macroblock's middle (6.4.12.2).
                x = PairTop(pairX - 1, pairY);
                if (x >= 0 && _real.FieldMb[x])
                {
                    yM = (yN + maxH) >> 1;
                    return Mb(x, 1);
                }

                return Mb(x, 0);
            }

            x = PairTop(pairX - 1, pairY - 1);
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
            x = PairTop(pairX - 1, pairY);
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
                return top ? Mb(PairTop(pairX, pairY - 1), 1) : _pairTop;
            }

            x = PairTop(pairX, pairY - 1);
            if (top && x >= 0 && !_real.FieldMb[x])
            {
                yM = 2 * yN;
                return Mb(x, 1);
            }

            return Mb(x, top ? 0 : 1);
        }

        if (frame)
        {
            return top ? Mb(PairTop(pairX + 1, pairY - 1), 1) : -1;
        }

        x = PairTop(pairX + 1, pairY - 1);
        if (top && x >= 0 && !_real.FieldMb[x])
        {
            yM = 2 * yN;
            return Mb(x, 1);
        }

        return Mb(x, top ? 0 : 1);

        int Mb(int pairTop, int bottom) => pairTop < 0 ? -1 : pairTop + (bottom * w);
    }

    /// <summary>
    /// Fills the window with the current macroblock's neighbours: the macroblocks A, B, C and D of 6.4.10.1 and, per 4x4 block,
    /// the blocks left of and above the macroblock with their motion in the current macroblock's units.
    /// </summary>
    private void LoadWindow(int pairX, int pairY, bool field)
    {
        H264MbState win = _win, real = _real;
        // The slots of the neighbours where the rest of the decoder looks for them (LeftMb, TopMb and the corners).
        int cur = _mbAddr, w = _widthMbs;
        win.Slice[cur] = -1;
        LoadMb(_mbX > 0 ? cur - 1 : -1, Locate(-1, 0, 16, out _));
        LoadMb(_mbY > 0 ? cur - w : -1, Locate(0, -1, 16, out _));
        LoadMb(_mbY > 0 && _mbX < w - 1 ? cur - w + 1 : -1, Locate(16, -1, 16, out _));
        LoadMb(_mbY > 0 && _mbX > 0 ? cur - w - 1 : -1, Locate(-1, -1, 16, out _));
        int rw4 = _sps.WidthInMbs * 4;

        // The left column (-1, 4 by) and the top row (4 bx, -1), with C at (16, -1) and D at (-1, -1).
        for (int by = 0; by < 4; by++)
        {
            int mb = Locate(-1, 4 * by, 16, out int yM);
            LoadBlock(mb, 3, yM >> 2, _b0 + (by * _w4) - 1, rw4);
            SetConditions(mb, 3, yM >> 2, by, true);
            if (by > 0)
            {
                int d = Locate(-1, (4 * by) - 1, 16, out int yD);
                for (int list = 0; list < 2; list++)
                {
                    LoadMotion(d, 3, yD >> 2, rw4, list, out _leftDRef[(list * 4) + by], out _leftDMv[(list * 4) + by]);
                }
            }
        }

        for (int bx = 0; bx < 4; bx++)
        {
            int mb = Locate(4 * bx, -1, 16, out int yM);
            LoadBlock(mb, bx, ((yM + 16) >> 2) & 3, _b0 - _w4 + bx, rw4);
            SetConditions(mb, bx, ((yM + 16) >> 2) & 3, bx, false);
        }

        int c = Locate(16, -1, 16, out int yC);
        LoadBlock(c, 0, ((yC + 16) >> 2) & 3, _b0 - _w4 + 4, rw4);
        int dd = Locate(-1, -1, 16, out int yDd);
        LoadBlock(dd, 3, ((yDd + 16) >> 2) & 3, _b0 - _w4 - 1, rw4);

        _leftIntraKnown = false;

        // coded_block_pattern's condTermFlagA for the left 8x8 blocks (9.3.3.1.1.4).
        for (int row = 0; row < 2; row++)
        {
            int mb = Locate(-1, 8 * row, 16, out int yM);
            int b8 = ((yM >> 3) * 2) + 1;
            _cbpCondA[row] = mb < 0 ? 0
                : real.Kind[mb] == H264MbKind.IPcm ? 0
                : !H264MbState.IsSkip(real.Kind[mb]) && ((real.Cbp[mb] >> b8) & 1) != 0 ? 0
                : 1;
        }

        if (_chromaArrayType is 1 or 2)
        {
            int rows = _chromaArrayType == 1 ? 2 : 4, maxH = rows * 4, rcw = real.ChromaW4;
            int at = ChromaBlock(0, 0);
            for (int cy = 0; cy < rows; cy++)
            {
                int mb = Locate(-1, 4 * cy, maxH, out int yM);
                int dst = at + (cy * win.ChromaW4) - 1;
                if (mb >= 0)
                {
                    int src = ((((mb / _sps.WidthInMbs) * rows) + (yM >> 2)) * rcw) + ((mb % _sps.WidthInMbs) * 2) + 1;
                    win.NzCb[dst] = real.NzCb[src];
                    win.NzCr[dst] = real.NzCr[src];
                }
            }

            for (int cx = 0; cx < 2; cx++)
            {
                int mb = Locate(4 * cx, -1, maxH, out int yM);
                int dst = at - win.ChromaW4 + cx;
                if (mb >= 0)
                {
                    int src = ((((mb / _sps.WidthInMbs) * rows) + (((yM + maxH) >> 2) % rows)) * rcw) + ((mb % _sps.WidthInMbs) * 2) + cx;
                    win.NzCb[dst] = real.NzCb[src];
                    win.NzCr[dst] = real.NzCr[src];
                }
            }
        }

        // Which neighbours are there (6.4.9 as 6.4.10.1 finds them).
        _neighbors = (_mbX > 0 && Available(cur - 1) ? NeighborA : 0) | (_mbY > 0 && Available(cur - w) ? NeighborB : 0)
            | (_mbY > 0 && _mbX < w - 1 && Available(cur - w + 1) ? NeighborC : 0) | (_mbY > 0 && _mbX > 0 && Available(cur - w - 1) ? NeighborD : 0);
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
        for (int y = 0; y < 16; y++)
        {
            _leftIntraLuma |= LeftRowIntra(Locate(-1, y, 16, out _)) ? 1 << y : 0;
            if (y < chromaRows)
            {
                _leftIntraChroma |= LeftRowIntra(Locate(-1, y, chromaRows, out _)) ? 1 << y : 0;
            }
        }
    }

    private bool LeftRowIntra(int mb) =>
        mb >= 0 && _real.Slice[mb] == _slice && (!_pps.ConstrainedIntraPred || H264MbState.IsIntra(_real.Kind[mb]));

    /// <summary>Copies a neighbouring macroblock's macroblock-level state into a window slot, or marks the slot unavailable.</summary>
    private void LoadMb(int slot, int mb)
    {
        H264MbState win = _win, real = _real;
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
        _winMo.MbIntra[slot] = _frame.MbIntra[mb];
    }

    /// <summary>Copies block (bx, by) of a neighbouring macroblock into window block <paramref name="dst"/>, motion converted.</summary>
    private void LoadBlock(int mb, int bx, int by, int dst, int rw4)
    {
        H264MbState win = _win, real = _real;
        if (mb < 0 || real.Slice[mb] != _slice)
        {
            return;
        }

        int src = BlockOf(mb, bx, by, rw4);
        win.NzLuma[dst] = real.NzLuma[src];
        win.IntraModes[dst] = real.IntraModes[src];
        bool neighborField = real.FieldMb[mb];
        for (int list = 0; list < 2; list++)
        {
            LoadMotion(mb, bx, by, rw4, list, out _winMo.RefIdx[list][dst], out _winMo.Mv[list][dst]);
            _winMo.RefPicId[list][dst] = _frame.RefPicId[list][src];
            byte mx = real.Mvd[list][src * 2], my = real.Mvd[list][(src * 2) + 1];
            if (_fieldMb != neighborField)
            {
                // 9.3.3.1.1.7: a field neighbour's vertical mvd doubles for a frame macroblock, a frame one's halves for a field one.
                my = (byte)(_fieldMb ? my >> 1 : Math.Min(my * 2, 255));
            }

            win.Mvd[list][dst * 2] = mx;
            win.Mvd[list][(dst * 2) + 1] = my;
        }
    }

    /// <summary>
    /// The motion of block (bx, by) of macroblock mb in the current macroblock's units (8.4.1.3.1): a frame neighbour of a field
    /// macroblock has its vertical vector halved and its reference index doubled, a field neighbour of a frame macroblock the
    /// reverse.
    /// </summary>
    private void LoadMotion(int mb, int bx, int by, int rw4, int list, out sbyte refIdx, out int mv)
    {
        refIdx = -1;
        mv = 0;
        if (mb < 0 || _real.Slice[mb] != _slice)
        {
            return;
        }

        int src = BlockOf(mb, bx, by, rw4);
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
    private void SetConditions(int mb, int bx, int by, int index, bool left)
    {
        H264MbState real = _real;
        int o = left ? 0 : 4;
        bool available = mb >= 0 && real.Slice[mb] == _slice;
        int rw4 = _sps.WidthInMbs * 4;
        int src = available ? BlockOf(mb, bx, by, rw4) : 0;
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

    /// <summary>Gets the index in the frame's block arrays, rw4 blocks a row, of block (bx, by) of the macroblock at a raster index.</summary>
    private int BlockOf(int mb, int bx, int by, int rw4) => ((((mb / _sps.WidthInMbs) * 4) + by) * rw4) + ((mb % _sps.WidthInMbs) * 4) + bx;

    /// <summary>Copies the current macroblock from the window into the frame.</summary>
    private void StoreMbaff()
    {
        H264MbState win = _win, real = _real;
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
        _frame.MbIntra[addr] = _winMo.MbIntra[_mbAddr];
        int w = _sps.WidthInMbs, rw4 = w * 4;
        int dst0 = ((addr / w) * 4 * rw4) + ((addr % w) * 4);
        for (int y = 0; y < 4; y++)
        {
            int s = _b0 + (y * _w4), d = dst0 + (y * rw4);
            win.NzLuma.AsSpan(s, 4).CopyTo(real.NzLuma.AsSpan(d, 4));
            win.Nonzero.AsSpan(s, 4).CopyTo(real.Nonzero.AsSpan(d, 4));
            win.IntraModes.AsSpan(s, 4).CopyTo(real.IntraModes.AsSpan(d, 4));
            _winMo.ColZero.AsSpan(s, 4).CopyTo(_frame.ColZero.AsSpan(d, 4));
            for (int list = 0; list < 2; list++)
            {
                _winMo.RefIdx[list].AsSpan(s, 4).CopyTo(_frame.RefIdx[list].AsSpan(d, 4));
                _winMo.Mv[list].AsSpan(s, 4).CopyTo(_frame.Mv[list].AsSpan(d, 4));
                _winMo.RefPicId[list].AsSpan(s, 4).CopyTo(_frame.RefPicId[list].AsSpan(d, 4));
                win.Mvd[list].AsSpan(s * 2, 8).CopyTo(real.Mvd[list].AsSpan(d * 2, 8));
            }
        }

        if (_chromaArrayType is 1 or 2)
        {
            int rows = _chromaArrayType == 1 ? 2 : 4, wcw = win.ChromaW4, rcw = real.ChromaW4;
            int s0 = ChromaBlock(0, 0), d0 = ((addr / w) * rows * rcw) + ((addr % w) * 2);
            for (int y = 0; y < rows; y++)
            {
                win.NzCb.AsSpan(s0 + (y * wcw), 2).CopyTo(real.NzCb.AsSpan(d0 + (y * rcw), 2));
                win.NzCr.AsSpan(s0 + (y * wcw), 2).CopyTo(real.NzCr.AsSpan(d0 + (y * rcw), 2));
            }
        }
    }
}
