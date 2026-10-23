namespace Egami.Video.Codecs.H264;

/// <summary>The macroblock syntax elements, read with CAVLC (9.1, 9.2) or CABAC (9.3).</summary>
internal sealed partial class H264SliceDecoder
{
    /// <summary>Initialises the CABAC contexts for the slice (9.3.1.1).</summary>
    private void InitContexts()
    {
        int column = _h.IsI ? 0 : 1 + _h.CabacInitIdc;
        int qp = Math.Clamp(_h.SliceQp, 0, 51);
        ReadOnlySpan<sbyte> init = H264Tables.CabacInit;
        for (int i = 0; i < _ctx.Length; i++)
        {
            int m = init[(i * 8) + (column * 2)], n = init[(i * 8) + (column * 2) + 1];
            int pre = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            _ctx[i] = (byte)(pre <= 63 ? (63 - pre) << 1 : ((pre - 64) << 1) | 1);
        }
    }

    private int Bin(int ctx) => _engine.Decode(ref _ctx[ctx]);

    private bool DecodeSkipFlag(int mbAddr)
    {
        _mbAddr = mbAddr;
        _mbX = mbAddr % _widthMbs;
        _mbY = mbAddr / _widthMbs;
        int left = LeftMb, top = TopMb;
        int inc = (Available(left) && !H264MbState.IsSkip(_mb.Kind[left]) ? 1 : 0) + (Available(top) && !H264MbState.IsSkip(_mb.Kind[top]) ? 1 : 0);
        return Bin((_h.IsB ? 24 : 11) + inc) != 0;
    }

    private int ReadMbType()
    {
        if (!_cabac)
        {
            uint max = _h.Type switch { H264SliceType.I => 25u, H264SliceType.P => 30u, _ => 48u };
            return (int)_bits.Ue(max, "mb_type");
        }

        int left = LeftMb, top = TopMb;
        switch (_h.Type)
        {
            case H264SliceType.I:
            {
                int inc = (Available(left) && _mb.Kind[left] is not (H264MbKind.I4x4 or H264MbKind.I8x8) ? 1 : 0)
                    + (Available(top) && _mb.Kind[top] is not (H264MbKind.I4x4 or H264MbKind.I8x8) ? 1 : 0);
                if (Bin(3 + inc) == 0)
                {
                    return 0;
                }

                if (_engine.Terminate() != 0)
                {
                    return 25;
                }

                int luma = Bin(3 + 3);
                int chroma = Bin(3 + 4);
                if (chroma != 0)
                {
                    chroma += Bin(3 + 5);
                }

                int mode = Bin(3 + 6) << 1;
                mode |= Bin(3 + 7);
                return 1 + mode + (4 * chroma) + (12 * luma);
            }

            case H264SliceType.P:
                if (Bin(14) != 0)
                {
                    return 5 + ReadIntraSuffix(17);
                }

                if (Bin(15) != 0)
                {
                    return Bin(17) != 0 ? 1 : 2;
                }

                return Bin(16) != 0 ? 3 : 0;
            default:
            {
                int inc = (Available(left) && _mb.Kind[left] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0)
                    + (Available(top) && _mb.Kind[top] is not (H264MbKind.BSkip or H264MbKind.BDirect) ? 1 : 0);
                if (Bin(27 + inc) == 0)
                {
                    return 0;
                }

                if (Bin(27 + 3) == 0)
                {
                    return 1 + Bin(27 + 5);
                }

                int bits = Bin(27 + 4) << 3;
                bits |= Bin(27 + 5) << 2;
                bits |= Bin(27 + 5) << 1;
                bits |= Bin(27 + 5);
                if (bits < 8)
                {
                    return bits + 3;
                }

                switch (bits)
                {
                    case 13:
                        return 23 + ReadIntraSuffix(32);
                    case 14:
                        return 11;
                    case 15:
                        return 22;
                }

                bits = (bits << 1) | Bin(27 + 5);
                return bits - 4;
            }
        }
    }

    /// <summary>Reads the intra mb_type suffix of a P or B slice (Table 9-36 with ctxIdxOffset 17 or 32).</summary>
    private int ReadIntraSuffix(int offset)
    {
        if (Bin(offset) == 0)
        {
            return 0;
        }

        if (_engine.Terminate() != 0)
        {
            return 25;
        }

        int luma = Bin(offset + 1);
        int chroma = Bin(offset + 2);
        if (chroma != 0)
        {
            chroma += Bin(offset + 2);
        }

        int mode = Bin(offset + 3) << 1;
        mode |= Bin(offset + 3);
        return 1 + mode + (4 * chroma) + (12 * luma);
    }

    private int ReadSubMbType()
    {
        if (!_cabac)
        {
            return (int)_bits.Ue(_h.IsB ? 12u : 3u, "sub_mb_type");
        }

        if (!_h.IsB)
        {
            if (Bin(21) != 0)
            {
                return 0;
            }

            if (Bin(22) == 0)
            {
                return 1;
            }

            return Bin(23) != 0 ? 2 : 3;
        }

        if (Bin(36) == 0)
        {
            return 0;
        }

        if (Bin(37) == 0)
        {
            return 1 + Bin(39);
        }

        int type = 3;
        if (Bin(38) != 0)
        {
            if (Bin(39) != 0)
            {
                return 11 + Bin(39);
            }

            type += 4;
        }

        type += 2 * Bin(39);
        type += Bin(39);
        return type;
    }

    private bool ReadTransform8x8Flag()
    {
        if (!_cabac)
        {
            return _bits.Flag();
        }

        int left = LeftMb, top = TopMb;
        int inc = (Available(left) && _mb.Transform8x8[left] ? 1 : 0) + (Available(top) && _mb.Transform8x8[top] ? 1 : 0);
        return Bin(399 + inc) != 0;
    }

    /// <summary>Reads the Intra4x4 or Intra8x8 prediction modes and derives each block's mode (8.3.1.1, 8.3.2.1).</summary>
    private void ReadIntraNxNModes()
    {
        int step = _t8x8 ? 4 : 1;
        for (int blk = 0; blk < 16; blk += step)
        {
            int xy = H264Tables.BlockXY[blk];
            int bx = xy & 3, by = xy >> 2;
            bool usePredicted;
            int rem = 0;
            if (_cabac)
            {
                usePredicted = Bin(68) != 0;
                if (!usePredicted)
                {
                    rem = Bin(69);
                    rem |= Bin(69) << 1;
                    rem |= Bin(69) << 2;
                }
            }
            else
            {
                usePredicted = _bits.Flag();
                if (!usePredicted)
                {
                    rem = _bits.Read(3);
                }
            }

            int predicted = PredictedIntraMode(bx, by);
            int mode = usePredicted ? predicted : rem < predicted ? rem : rem + 1;
            int b = Block(bx, by);
            if (_t8x8)
            {
                _mb.IntraModes[b] = _mb.IntraModes[b + 1] = (sbyte)mode;
                _mb.IntraModes[b + _mb.W4] = _mb.IntraModes[b + _mb.W4 + 1] = (sbyte)mode;
            }
            else
            {
                _mb.IntraModes[b] = (sbyte)mode;
            }
        }
    }

    /// <summary>predIntra4x4PredMode / predIntra8x8PredMode from the blocks left of and above (bx, by).</summary>
    private int PredictedIntraMode(int bx, int by)
    {
        int a = NeighborIntraMode(bx - 1, by, out bool availableA);
        int b = NeighborIntraMode(bx, by - 1, out bool availableB);
        return !availableA || !availableB ? 2 : Math.Min(a, b);
    }

    private int NeighborIntraMode(int bx, int by, out bool available)
    {
        int addr = bx < 0 ? LeftMb : by < 0 ? TopMb : _mbAddr;
        available = Available(addr);
        if (!available)
        {
            return 2;
        }

        if (addr != _mbAddr)
        {
            H264MbKind kind = _mb.Kind[addr];
            if (!H264MbState.IsIntra(kind))
            {
                // An inter neighbour counts as unavailable with constrained intra prediction, else as DC.
                available = !_pps.ConstrainedIntraPred;
                return 2;
            }

            if (kind is not (H264MbKind.I4x4 or H264MbKind.I8x8))
            {
                return 2;
            }
        }

        return _mb.IntraModes[Block(bx, by)];
    }

    private int ReadChromaPredMode()
    {
        if (!_cabac)
        {
            return (int)_bits.Ue(3, "intra_chroma_pred_mode");
        }

        int left = LeftMb, top = TopMb;
        int inc = (ChromaModeCond(left) ? 1 : 0) + (ChromaModeCond(top) ? 1 : 0);
        if (Bin(64 + inc) == 0)
        {
            return 0;
        }

        if (Bin(64 + 3) == 0)
        {
            return 1;
        }

        return Bin(64 + 3) == 0 ? 2 : 3;
    }

    private bool ChromaModeCond(int addr) =>
        Available(addr) && H264MbState.IsIntra(_mb.Kind[addr]) && _mb.Kind[addr] != H264MbKind.IPcm && _mb.ChromaPredMode[addr] != 0;

    private int ReadCbp(bool intra)
    {
        if (!_cabac)
        {
            bool mono = _chromaArrayType is 0 or 3;
            int code = (int)_bits.Ue(mono ? 15u : 47u, "coded_block_pattern");
            return mono ? (intra ? H264Tables.CbpIntraMono[code] : H264Tables.CbpInterMono[code])
                : (intra ? H264Tables.CbpIntra[code] : H264Tables.CbpInter[code]);
        }

        int left = LeftMb, top = TopMb;
        bool availableLeft = Available(left), availableTop = Available(top);
        int cbp = 0;
        for (int b8 = 0; b8 < 4; b8++)
        {
            int bx = b8 & 1, by = b8 >> 1;
            int condA = bx > 0 ? (((cbp >> (b8 - 1)) & 1) == 0 ? 1 : 0)
                : availableLeft ? (((_mb.Cbp[left] >> (b8 + 1)) & 1) == 0 ? 1 : 0) : 0;
            int condB = by > 0 ? (((cbp >> (b8 - 2)) & 1) == 0 ? 1 : 0)
                : availableTop ? (((_mb.Cbp[top] >> (b8 + 2)) & 1) == 0 ? 1 : 0) : 0;
            cbp |= Bin(73 + condA + (2 * condB)) << b8;
        }

        if (_chromaArrayType is 1 or 2)
        {
            int chromaA = availableLeft ? _mb.Cbp[left] >> 4 : 0, chromaB = availableTop ? _mb.Cbp[top] >> 4 : 0;
            if (Bin(77 + (chromaA != 0 ? 1 : 0) + (chromaB != 0 ? 2 : 0)) != 0)
            {
                cbp |= (1 + Bin(77 + 4 + (chromaA == 2 ? 1 : 0) + (chromaB == 2 ? 2 : 0))) << 4;
            }
        }

        return cbp;
    }

    private int ReadQpDeltaCabac()
    {
        if (Bin(60 + (_prevQpDeltaNonZero ? 1 : 0)) == 0)
        {
            return 0;
        }

        int k = 1;
        int ctx = 62;
        while (Bin(ctx) != 0)
        {
            ctx = 63;
            if (++k > 200)
            {
                throw H264Errors.Corrupt("mb_qp_delta is too long.");
            }
        }

        return (k & 1) != 0 ? (k + 1) >> 1 : -(k >> 1);
    }

    /// <summary>Reads ref_idx_lX for the partition whose top-left 4x4 block is (bx, by) in the macroblock.</summary>
    private int ReadRefIdx(int list, int bx, int by, int count)
    {
        if (!_cabac)
        {
            // te(v): one inverted bit when the range is 0 to 1.
            return count == 2 ? (_bits.Flag() ? 0 : 1) : (int)_bits.Ue((uint)(count - 1), "ref_idx");
        }

        int inc = (RefIdxCond(list, bx - 1, by) ? 1 : 0) + (RefIdxCond(list, bx, by - 1) ? 2 : 0);
        if (Bin(54 + inc) == 0)
        {
            return 0;
        }

        int value = 1;
        int ctx = 54 + 4;
        while (Bin(ctx) != 0)
        {
            ctx = 54 + 5;
            if (++value >= 32)
            {
                throw H264Errors.Corrupt("ref_idx is too large.");
            }
        }

        return value;
    }

    /// <summary>condTermFlagN of ref_idx (9.3.3.1.1.6): the neighbour has refIdx above 0 in the list and is not skipped or direct.</summary>
    private bool RefIdxCond(int list, int bx, int by)
    {
        int addr = bx < 0 ? LeftMb : by < 0 ? TopMb : _mbAddr;
        if (!Available(addr))
        {
            return false;
        }

        H264MbKind kind = _mb.Kind[addr];
        if (H264MbState.IsIntra(kind) || H264MbState.IsSkip(kind) || kind == H264MbKind.BDirect)
        {
            return false;
        }

        int nx = bx & 3, ny = by & 3;
        if ((_mb.Direct8x8[addr] & (1 << ((ny >> 1) * 2 + (nx >> 1)))) != 0)
        {
            return false;
        }

        return _pic.RefIdx[list][Block(bx, by)] > 0;
    }

    /// <summary>Reads one mvd_lX component for the partition whose top-left 4x4 block is (bx, by).</summary>
    private int ReadMvd(int list, int bx, int by, int component)
    {
        if (!_cabac)
        {
            return _bits.Se();
        }

        int sum = MvdAbs(list, bx - 1, by, component) + MvdAbs(list, bx, by - 1, component);
        int ctx = component == 0 ? 40 : 47;
        if (Bin(ctx + (sum < 3 ? 0 : sum > 32 ? 2 : 1)) == 0)
        {
            return 0;
        }

        int prefix = 1;
        int inc = 3;
        while (prefix < 9 && Bin(ctx + inc) != 0)
        {
            prefix++;
            if (inc < 6)
            {
                inc++;
            }
        }

        int value = prefix;
        if (prefix >= 9)
        {
            // UEG3 suffix.
            int k = 3;
            while (_engine.Bypass() != 0)
            {
                value += 1 << k;
                if (++k > 24)
                {
                    throw H264Errors.Corrupt("An mvd suffix is too long.");
                }
            }

            value += (int)_engine.Bypass(k);
        }

        return _engine.Bypass() != 0 ? -value : value;
    }

    private int MvdAbs(int list, int bx, int by, int component)
    {
        int addr = bx < 0 ? LeftMb : by < 0 ? TopMb : _mbAddr;
        return Available(addr) ? _mb.Mvd[list][(Block(bx, by) * 2) + component] : 0;
    }
}
