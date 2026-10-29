using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Decodes the macroblocks of one slice (H.264 7.3.4, 7.3.5 and clause 8) into a picture: parsing with CAVLC or CABAC, motion
/// vector prediction, intra and inter prediction, and the residual. The deblocking filter runs on the whole picture afterwards.
/// </summary>
internal sealed partial class H264SliceDecoder
{
    private readonly H264BitReader _bits = new();
    private readonly CabacEngine _engine = new();
    private readonly byte[] _ctx = new byte[460];
    private readonly H264Inter _inter = new();

    // Coefficients of the macroblock being decoded, in raster order within each block.
    private readonly int[] _coef = new int[256];
    private readonly int[] _dcY = new int[16];
    private readonly int[] _coefC = new int[2 * 8 * 16];
    private readonly int[] _dcC = new int[2 * 8];

    // Motion of the macroblock being decoded: sub_mb_type and the direct prediction per 8x8 block.
    private readonly int[] _subType = new int[4];
    private readonly int[] _directRef = new int[2];
    private readonly int[] _directMv = new int[2 * 16];
    private readonly sbyte[] _directRefBlock = new sbyte[2 * 16];

    private H264SliceHeader _h = null!;
    private H264Sps _sps = null!;
    private H264Pps _pps = null!;
    private H264Picture _pic = null!;
    private H264MbState _mb = null!;
    private H264Picture[] _list0 = [];
    private H264Picture[] _list1 = [];
    private int[] _implicitWeights = [];
    private int _slice;
    private bool _cabac;
    private int _widthMbs;
    private int _mbCount;
    private int _chromaArrayType;
    private int _bitDepthY;
    private int _bitDepthC;
    private int _qpBdOffsetY;
    private int _qpBdOffsetC;

    // Whether the macroblock is a field macroblock (in a field picture, or a field pair of an MBAFF frame), and its scans.
    private bool _fieldMb;
    private byte[] _scan4 = H264ScalingLists.Zigzag4x4;
    private byte[] _scan8 = H264ScalingLists.Zigzag8x8;

    // The active reference indices per list: a field macroblock of an MBAFF frame indexes the fields of the frame
    // references, twice as many (8.4.2.1).
    private int _refCount0;
    private int _refCount1;

    // The first CABAC contexts of significant_coeff_flag and last_significant_coeff_flag for 4x4 blocks (Table 9-34).
    private int _sigBase = 105;
    private int _lastBase = 166;
    private int _sigBase8 = 402;
    private int _lastBase8 = 417;
    private byte[] _sigInc8 = Significant8x8;

    private static readonly byte[] Significant8x8 = H264Tables.Significant8x8.ToArray();
    private static readonly byte[] Significant8x8Field = H264Tables.Significant8x8Field.ToArray();

    // The picture whose motion arrays and intra flags the macroblock reads and writes: the picture, or the MBAFF window.
    private H264Picture _mo = null!;

    // The macroblock being decoded.
    private int _mbAddr;
    private int _mbX;
    private int _mbY;

    private int _qp;
    private bool _prevQpDeltaNonZero;
    private H264MbKind _kind;
    private int _partShape;
    private int _partPred0;
    private int _partPred1;
    private int _i16Mode;
    private int _cbp;
    private bool _t8x8;
    private int _lumaCoded;
    private int _chromaDcCoded;
    private int _chromaAcCoded;

    // The neighbouring macroblocks of the current one that are available, as Neighbor* bits.
    private int _neighbors;

    // The QPY whose chroma QPs are kept, and those QPs.
    private int _chromaQpOf;
    private sbyte _chromaQp0;
    private sbyte _chromaQp1;

    // The picture's motion arrays, per list.
    private sbyte[] _refIdx0 = [];
    private sbyte[] _refIdx1 = [];
    private int[] _mv0 = [];
    private int[] _mv1 = [];

    // Blocks per row of 4x4 blocks, and the first block of the current macroblock.
    private int _w4;
    private int _b0;

    private const int NeighborA = 1;
    private const int NeighborB = 2;
    private const int NeighborC = 4;
    private const int NeighborD = 8;

    /// <summary>Gets the number of macroblocks the last call decoded.</summary>
    internal int Decoded { get; private set; }

    /// <summary>Gets the address of the last macroblock decoded, or -1.</summary>
    internal int LastMb { get; private set; } = -1;

    /// <summary>Gets or sets what runs when the last macroblock of a row is decoded, with the row.</summary>
    internal Action<int>? RowFinished { get; set; }

    /// <summary>Decodes a slice. A broken slice throws <see cref="CorruptDataException"/>; the macroblocks before the damage stay.</summary>
    internal void Decode(H264SliceHeader h, byte[] padded, int length, H264Picture pic, H264MbState mb, int slice, H264Picture[] list0, H264Picture[] list1, int[] implicitWeights)
    {
        _h = h;
        _sps = h.Sps;
        _pps = h.Pps;
        _pic = pic;
        _mb = mb;
        _slice = slice;
        _list0 = list0;
        _list1 = list1;
        _implicitWeights = implicitWeights;
        _cabac = _pps.Cabac;
        _widthMbs = _sps.WidthInMbs;
        _mbCount = _sps.WidthInMbs * h.PicHeightInMbs;
        _chromaArrayType = _sps.ChromaArrayType;
        _bitDepthY = _sps.BitDepthLuma;
        _bitDepthC = _sps.BitDepthChroma;
        _qpBdOffsetY = 6 * (_bitDepthY - 8);
        _qpBdOffsetC = 6 * (_bitDepthC - 8);
        _qp = h.SliceQp;
        _refCount0 = h.NumRefIdxL0;
        _refCount1 = h.NumRefIdxL1;
        _prevQpDeltaNonZero = false;
        SetFieldMb(h.FieldPic);
        _inter.Setup(pic, h, list0, list1, implicitWeights);
        _mo = pic;
        _refIdx0 = pic.RefIdx[0];
        _refIdx1 = pic.RefIdx[1];
        _mv0 = pic.Mv[0];
        _mv1 = pic.Mv[1];
        _w4 = _mb.W4;
        _mbaff = _mixedLeft = _mixedTop = false;
        Decoded = 0;
        LastMb = -1;
        _mbAddr = -2;
        _chromaQpOf = -1000;
        SetColocated();
        if (h.Mbaff)
        {
            StartMbaff(pic, mb, list0, list1, implicitWeights);
            DecodeMbaffSlice(h, padded, length);
            return;
        }

        int mbAddr = h.FirstMb;
        if (_cabac)
        {
            InitContexts();
            _engine.Start(padded, (int)((h.DataBitPosition + 7) >> 3), length);
            while (true)
            {
                CheckAddress(mbAddr);
                bool skip = !h.IsI && DecodeSkipFlag(mbAddr);
                if (skip)
                {
                    DecodeSkip(mbAddr);
                }
                else
                {
                    DecodeMacroblock(mbAddr);
                }

                if (_engine.Overrun)
                {
                    throw H264Errors.Corrupt("A CABAC slice runs past its data.");
                }

                if (_engine.Terminate() != 0)
                {
                    break;
                }

                mbAddr++;
            }
        }
        else
        {
            _bits.Start(padded, length, (int)h.DataBitPosition);
            bool more = true;
            while (more)
            {
                if (!h.IsI)
                {
                    int run = (int)_bits.Ue((uint)(_mbCount - mbAddr), "mb_skip_run");
                    for (int i = 0; i < run; i++)
                    {
                        CheckAddress(mbAddr);
                        DecodeSkip(mbAddr);
                        mbAddr++;
                    }

                    if (run > 0 && !_bits.MoreRbspData())
                    {
                        break;
                    }
                }

                CheckAddress(mbAddr);
                DecodeMacroblock(mbAddr);
                if (_bits.Overrun)
                {
                    throw H264Errors.Corrupt("A CAVLC slice runs past its data.");
                }

                more = _bits.MoreRbspData();
                mbAddr++;
            }
        }
    }

    /// <summary>Makes the macroblocks that follow field or frame macroblocks: their scans and CABAC significance contexts.</summary>
    private void SetFieldMb(bool field)
    {
        _fieldMb = field;
        _scan4 = field ? H264ScalingLists.Field4x4 : H264ScalingLists.Zigzag4x4;
        _scan8 = field ? H264ScalingLists.Field8x8 : H264ScalingLists.Zigzag8x8;
        _sigBase = field ? 277 : 105;
        _lastBase = field ? 338 : 166;
        _sigBase8 = field ? 436 : 402;
        _lastBase8 = field ? 451 : 417;
        _sigInc8 = field ? Significant8x8Field : Significant8x8;
    }

    private void CheckAddress(int mbAddr)
    {
        if (mbAddr >= _mbCount)
        {
            throw H264Errors.Corrupt("A slice has more macroblocks than the picture.");
        }

        if (_mb.Slice[mbAddr] >= 0)
        {
            throw H264Errors.Corrupt($"Macroblock {mbAddr} is coded twice.");
        }
    }

    /// <summary>Makes a macroblock current: the next one steps its column, others divide.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetAddress(int mbAddr)
    {
        if (mbAddr == _mbAddr)
        {
            return;
        }

        if (mbAddr == _mbAddr + 1 && _mbX + 1 < _widthMbs)
        {
            _mbX++;
            _b0 += 4;
        }
        else
        {
            _mbX = mbAddr % _widthMbs;
            _mbY = mbAddr / _widthMbs;
            _b0 = (_mbY * 4 * _w4) + (_mbX * 4);
        }

        _mbAddr = mbAddr;
    }

    private void Begin(int mbAddr)
    {
        SetAddress(mbAddr);

        // Which neighbouring macroblocks (6.4.9: A left, B above, C above right, D above left) are in this slice.
        _neighbors = (_mbX > 0 && Available(mbAddr - 1) ? NeighborA : 0)
            | (_mbY > 0 && Available(mbAddr - _widthMbs) ? NeighborB : 0)
            | (_mbY > 0 && _mbX < _widthMbs - 1 && Available(mbAddr - _widthMbs + 1) ? NeighborC : 0)
            | (_mbY > 0 && _mbX > 0 && Available(mbAddr - _widthMbs - 1) ? NeighborD : 0);
        _mb.Slice[mbAddr] = _slice;
        _mb.Transform8x8[mbAddr] = false;
        _mb.Cbp[mbAddr] = 0;
        _mb.CbfDc[mbAddr] = 0;
        _mb.Direct8x8[mbAddr] = 0;
        _mb.ChromaPredMode[mbAddr] = 0;
        _mb.QpDeltaNonZero[mbAddr] = false;
        _t8x8 = false;
        _cbp = 0;
        _lumaCoded = 0;
        _chromaDcCoded = 0;
        _chromaAcCoded = 0;
    }

    private void Finish()
    {
        int addr = _mbAddr;
        _mb.Kind[addr] = _kind;
        _mb.Cbp[addr] = (byte)_cbp;
        _mb.Transform8x8[addr] = _t8x8;
        int qp = _kind == H264MbKind.IPcm ? 0 : _qp;
        _mb.Qp[addr] = (sbyte)qp;
        if (qp != _chromaQpOf)
        {
            // QPC changes only with QPY.
            _chromaQpOf = qp;
            _chromaQp0 = (sbyte)ChromaQp(qp, _pps.ChromaQpIndexOffset);
            _chromaQp1 = (sbyte)ChromaQp(qp, _pps.SecondChromaQpIndexOffset);
        }

        _mb.QpC[0][addr] = _chromaQp0;
        _mb.QpC[1][addr] = _chromaQp1;
        _mo.MbIntra[addr] = H264MbState.IsIntra(_kind);
        _mb.FlatInside[addr] = IsFlatInside();
        if (_h.NalRefIdc != 0 && !H264MbState.IsIntra(_kind))
        {
            StoreColZero();
        }
        Decoded++;
        LastMb = addr;
        if (_mbX == _widthMbs - 1)
        {
            RowFinished?.Invoke(_mbY);
        }
    }

    /// <summary>Whether the macroblock has one motion and no luma coefficients (<see cref="H264MbState.FlatInside"/>).</summary>
    private bool IsFlatInside()
    {
        bool oneMotion = _kind switch
        {
            H264MbKind.PSkip => true,
            H264MbKind.Inter => _partShape == 0,
            H264MbKind.BSkip or H264MbKind.BDirect => _directUniform == 1,
            _ => false,
        };
        if (!oneMotion)
        {
            return false;
        }

        int w4 = _w4, b = Block(0, 0);
        byte[] nonzero = _mb.Nonzero;
        if (b + (3 * w4) + 4 > nonzero.Length)
        {
            return false;
        }

        ref byte n = ref MemoryMarshal.GetArrayDataReference(nonzero);
        return (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref n, b)) | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref n, b + w4))
            | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref n, b + (2 * w4))) | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref n, b + (3 * w4)))) == 0;
    }

    /// <summary>QPC (Table 8-15) for a QPY and a chroma offset, without the bit depth offset.</summary>
    private int ChromaQp(int qpY, int offset)
    {
        int qpi = Math.Clamp(qpY + offset, -_qpBdOffsetC, 51);
        return qpi < 0 ? qpi : H264Tables.ChromaQp[qpi];
    }

    /// <summary>Gets the 4x4 block index in the picture of block (bx, by) of the current macroblock.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Block(int bx, int by) => _b0 + (by * _w4) + bx;

    /// <summary>Gets whether a macroblock address is in the picture and already decoded in this slice.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Available(int addr) => addr >= 0 && _mb.Slice[addr] == _slice;

    private int LeftMb => _mbX > 0 ? _mbAddr - 1 : -1;

    private int TopMb => _mbY > 0 ? _mbAddr - _widthMbs : -1;

    private void DecodeSkip(int mbAddr)
    {
        if (!_h.IsB)
        {
            DecodePSkip(mbAddr);
            return;
        }

        Begin(mbAddr);
        _kind = _h.IsB ? H264MbKind.BSkip : H264MbKind.PSkip;
        _prevQpDeltaNonZero = false;
        ClearMbBlocks(intra: false);
        if (_kind == H264MbKind.PSkip)
        {
            PredictPSkip();
        }
        else
        {
            _mb.Direct8x8[mbAddr] = 15;
            PredictDirect(15);
            WriteDirect(15);
        }

        PredictInter(15);
        Finish();
    }

    /// <summary>
    /// P_Skip as one routine: <see cref="Begin"/>, <see cref="PredictPSkip"/> and <see cref="Finish"/> with what a skipped
    /// macroblock makes constant folded in. One motion for the whole macroblock, no coefficients, QP unchanged.
    /// </summary>
    private void DecodePSkip(int mbAddr)
    {
        SetAddress(mbAddr);
        _neighbors = (_mbX > 0 && Available(mbAddr - 1) ? NeighborA : 0)
            | (_mbY > 0 && Available(mbAddr - _widthMbs) ? NeighborB : 0)
            | (_mbY > 0 && _mbX < _widthMbs - 1 && Available(mbAddr - _widthMbs + 1) ? NeighborC : 0)
            | (_mbY > 0 && _mbX > 0 && Available(mbAddr - _widthMbs - 1) ? NeighborD : 0);
        _kind = H264MbKind.PSkip;
        _prevQpDeltaNonZero = false;
        _t8x8 = false;
        _cbp = 0;
        _lumaCoded = 0;
        _chromaDcCoded = 0;
        _chromaAcCoded = 0;
        ClearMbBlocks(intra: false);

        // The motion vector (8.4.1.1): zero unless A and B are both there and neither is a zero vector to reference 0.
        _decodedMask = 0;
        bool availableA = Neighbor(0, -1, 0, out int refA, out int mvA);
        bool availableB = Neighbor(0, 0, -1, out int refB, out int mvB);
        int mv = 0;
        if (availableA && availableB && !(refA == 0 && mvA == 0) && !(refB == 0 && mvB == 0))
        {
            bool availableC = Neighbor(0, 4, -1, out int refC, out int mvC);
            if (!availableC)
            {
                availableC = Neighbor(0, -1, -1, out refC, out mvC);
            }

            mv = Median(availableA, availableB, availableC, refA, refB, refC, mvA, mvB, mvC, 0);
        }

        // Rows of four blocks: list 0 takes reference 0 and the vector, list 1 nothing.
        int w4 = _w4, b0 = _b0;
        int id = _list0.Length > 0 ? _list0[0].Id : -1;
        sbyte[] ref0 = _refIdx0, ref1 = _refIdx1;
        int[] ids0 = _mo.RefPicId[0], ids1 = _mo.RefPicId[1], mv0 = _mv0, mv1 = _mv1;
        bool[] colZero = _mo.ColZero;
        int last = b0 + (3 * w4) + 4;
        if (b0 < 0 || last > Math.Min(Math.Min(ref0.Length, ref1.Length), Math.Min(Math.Min(ids0.Length, ids1.Length), Math.Min(Math.Min(mv0.Length, mv1.Length), colZero.Length))))
        {
            throw H264Errors.Corrupt("A macroblock lies outside the picture.");
        }

        Vector128<int> mvRow = Vector128.Create(mv), idRow = Vector128.Create(id), none = Vector128.Create(-1);
        for (int b = b0; b < last; b += w4)
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ref0), b)), 0u);
            Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ref1), b)), uint.MaxValue);
            idRow.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(ids0), (nuint)b);
            none.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(ids1), (nuint)b);
            mvRow.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(mv0), (nuint)b);
            Vector128<int>.Zero.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(mv1), (nuint)b);
        }

        _decodedMask = 0xFFFF;
        _inter.Predict(_mbX * 4, _mbY * 4, 4, 4);

        int addr = _mbAddr;
        int qp = _qp;
        if (qp != _chromaQpOf)
        {
            _chromaQpOf = qp;
            _chromaQp0 = (sbyte)ChromaQp(qp, _pps.ChromaQpIndexOffset);
            _chromaQp1 = (sbyte)ChromaQp(qp, _pps.SecondChromaQpIndexOffset);
        }

        _mb.Slice[addr] = _slice;
        _mb.Kind[addr] = H264MbKind.PSkip;
        _mb.Cbp[addr] = 0;
        _mb.Transform8x8[addr] = false;
        _mb.CbfDc[addr] = 0;
        _mb.Direct8x8[addr] = 0;
        _mb.ChromaPredMode[addr] = 0;
        _mb.QpDeltaNonZero[addr] = false;
        _mb.Qp[addr] = (sbyte)qp;
        _mb.QpC[0][addr] = _chromaQp0;
        _mb.QpC[1][addr] = _chromaQp1;
        _mo.MbIntra[addr] = false;
        _mb.FlatInside[addr] = true;
        if (_h.NalRefIdc != 0)
        {
            // Every block has reference 0 and the same vector.
            bool zero = (uint)(MvX(mv) + 1) <= 2 && (uint)(MvY(mv) + 1) <= 2;
            ref bool c = ref MemoryMarshal.GetArrayDataReference(colZero);
            for (int b = b0; b < last; b += w4)
            {
                Unsafe.WriteUnaligned(ref Unsafe.As<bool, byte>(ref Unsafe.Add(ref c, b)), zero ? 0x01010101u : 0u);
            }
        }

        Decoded++;
        LastMb = addr;
        if (_mbX == _widthMbs - 1)
        {
            RowFinished?.Invoke(_mbY);
        }
    }

    /// <summary>Clears the coefficient counts, mvd and intra modes of the current macroblock's blocks.</summary>
    private void ClearMbBlocks(bool intra)
    {
        int w4 = _w4;
        int b = Block(0, 0);
        byte[] nzLuma = _mb.NzLuma, nonzero = _mb.Nonzero, mvd0 = _mb.Mvd[0], mvd1 = _mb.Mvd[1];
        sbyte[] intraModes = _mb.IntraModes;
        if (b + (3 * w4) + 4 > Math.Min(nzLuma.Length, Math.Min(nonzero.Length, intraModes.Length)) || ((b + (3 * w4)) * 2) + 8 > Math.Min(mvd0.Length, mvd1.Length))
        {
            throw H264Errors.Corrupt("A macroblock lies outside the picture.");
        }

        for (int y = 0; y < 4; y++, b += w4)
        {
            // A row of four blocks: four bytes of counts and modes, eight of mvd.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(nzLuma), b), 0u);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(nonzero), b), 0u);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mvd0), b * 2), 0ul);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(mvd1), b * 2), 0ul);
            if (intra)
            {
                // Intra modes are read only from Intra4x4 and Intra8x8 macroblocks, which write their own.
                Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(intraModes), b)), uint.MaxValue);
                _mo.RefIdx[0].AsSpan(b, 4).Fill(-1);
                _mo.RefIdx[1].AsSpan(b, 4).Fill(-1);
                _mo.RefPicId[0].AsSpan(b, 4).Fill(-1);
                _mo.RefPicId[1].AsSpan(b, 4).Fill(-1);
                _mo.Mv[0].AsSpan(b, 4).Clear();
                _mo.Mv[1].AsSpan(b, 4).Clear();
            }
        }

        if (_chromaArrayType != 0)
        {
            int cw = _mb.ChromaW4, rows = _chromaArrayType == 1 ? 2 : 4;
            int c = (_mbY * rows * cw) + (_mbX * 2);
            byte[] nzCb = _mb.NzCb, nzCr = _mb.NzCr;
            if (c + ((rows - 1) * cw) + 2 > Math.Min(nzCb.Length, nzCr.Length))
            {
                throw H264Errors.Corrupt("A macroblock lies outside the picture.");
            }

            for (int y = 0; y < rows; y++, c += cw)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(nzCb), c), (ushort)0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(nzCr), c), (ushort)0);
            }
        }
    }

    private void DecodeMacroblock(int mbAddr)
    {
        Begin(mbAddr);
        int type = ReadMbType();
        int intraType = _h.Type switch
        {
            H264SliceType.I => type,
            H264SliceType.P => type - 5,
            _ => type - 23,
        };

        if (intraType >= 0)
        {
            DecodeIntraMacroblock(intraType);
        }
        else
        {
            DecodeInterMacroblock(type);
        }

        Finish();
    }

    /// <summary>Predicts the inter macroblock from the motion stored for it, block by block in its partitions.</summary>
    private void PredictInter(int directMask)
    {
        int x0 = _mbX * 4, y0 = _mbY * 4;
        if (_kind is H264MbKind.PSkip || (_kind == H264MbKind.Inter && _partShape == 0))
        {
            _inter.Predict(x0, y0, 4, 4);
            return;
        }

        if (_kind == H264MbKind.Inter && _partShape is 1 or 2)
        {
            int w = _partShape == 1 ? 4 : 2, hgt = _partShape == 1 ? 2 : 4;
            _inter.Predict(x0, y0, w, hgt);
            _inter.Predict(x0 + (4 - w), y0 + (4 - hgt), w, hgt);
            return;
        }

        if (_kind is H264MbKind.BSkip or H264MbKind.BDirect && (_directUniform >= 0 ? _directUniform == 1 : _inter.Uniform(x0, y0)))
        {
            _inter.Predict(x0, y0, 4, 4);
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            int bx = x0 + ((i & 1) * 2), by = y0 + ((i >> 1) * 2);
            int shape = (directMask & (1 << i)) != 0 ? (_sps.Direct8x8Inference ? 0 : 3) : _h.IsB ? H264Tables.BSubShape[_subType[i]] : _subType[i];
            switch (shape)
            {
                case 0:
                    _inter.Predict(bx, by, 2, 2);
                    break;
                case 1:
                    _inter.Predict(bx, by, 2, 1);
                    _inter.Predict(bx, by + 1, 2, 1);
                    break;
                case 2:
                    _inter.Predict(bx, by, 1, 2);
                    _inter.Predict(bx + 1, by, 1, 2);
                    break;
                default:
                    _inter.Predict(bx, by, 1, 1);
                    _inter.Predict(bx + 1, by, 1, 1);
                    _inter.Predict(bx, by + 1, 1, 1);
                    _inter.Predict(bx + 1, by + 1, 1, 1);
                    break;
            }
        }
    }

    private void DecodeIntraMacroblock(int type)
    {
        ClearMbBlocks(intra: true);
        if (type == 25)
        {
            _kind = H264MbKind.IPcm;
            DecodePcm();
            _cbp = 0x2F;
            _prevQpDeltaNonZero = false;
            int w4 = _w4;
            int b = Block(0, 0);
            for (int y = 0; y < 4; y++, b += w4)
            {
                _mb.NzLuma.AsSpan(b, 4).Fill(16);
                _mb.Nonzero.AsSpan(b, 4).Fill(1);
            }

            if (_chromaArrayType != 0)
            {
                int cw = _mb.ChromaW4, rows = _chromaArrayType == 1 ? 2 : 4;
                int c = (_mbY * rows * cw) + (_mbX * 2);
                for (int y = 0; y < rows; y++, c += cw)
                {
                    _mb.NzCb.AsSpan(c, 2).Fill(16);
                    _mb.NzCr.AsSpan(c, 2).Fill(16);
                }
            }

            _mb.CbfDc[_mbAddr] = 7;
            return;
        }

        if (type == 0)
        {
            if (_pps.Transform8x8Mode)
            {
                _t8x8 = ReadTransform8x8Flag();
            }

            _kind = _t8x8 ? H264MbKind.I8x8 : H264MbKind.I4x4;
            ReadIntraNxNModes();
        }
        else
        {
            _kind = H264MbKind.I16x16;
            _i16Mode = (type - 1) % 4;
            _cbp = ((((type - 1) / 4) % 3) << 4) | (type >= 13 ? 15 : 0);
        }

        // Written now: CABAC chroma mode contexts read it for later macroblocks, and the kind decides intra availability.
        _mb.Kind[_mbAddr] = _kind;
        if (_chromaArrayType is 1 or 2)
        {
            _mb.ChromaPredMode[_mbAddr] = (byte)ReadChromaPredMode();
        }

        if (_kind != H264MbKind.I16x16)
        {
            _cbp = ReadCbp(intra: true);
        }

        _mb.Cbp[_mbAddr] = (byte)_cbp;
        if (_cbp != 0 || _kind == H264MbKind.I16x16)
        {
            ReadQpDelta();
            ReadResidual();
        }
        else
        {
            _prevQpDeltaNonZero = false;
        }

        ReconstructIntra();
    }

    private void DecodeInterMacroblock(int type)
    {
        ClearMbBlocks(intra: false);
        _mb.Kind[_mbAddr] = H264MbKind.Inter;
        bool noSubBelow8x8 = true;
        int directMask = 0;
        if (_h.IsB)
        {
            if (type == 0)
            {
                _kind = H264MbKind.BDirect;
                directMask = 15;
                noSubBelow8x8 = _sps.Direct8x8Inference;
            }
            else if (type == 22)
            {
                _kind = H264MbKind.Inter;
                _partShape = 3;
                for (int i = 0; i < 4; i++)
                {
                    _subType[i] = ReadSubMbType();
                    if (_subType[i] == 0)
                    {
                        directMask |= 1 << i;
                        noSubBelow8x8 &= _sps.Direct8x8Inference;
                    }
                    else
                    {
                        noSubBelow8x8 &= H264Tables.BSubShape[_subType[i]] == 0;
                    }
                }
            }
            else
            {
                _kind = H264MbKind.Inter;
                if (type <= 3)
                {
                    _partShape = 0;
                    _partPred0 = type;
                }
                else
                {
                    _partShape = type % 2 == 0 ? 1 : 2;
                    _partPred0 = H264Tables.BPartPred[((type - 4) / 2) * 2];
                    _partPred1 = H264Tables.BPartPred[(((type - 4) / 2) * 2) + 1];
                }
            }
        }
        else
        {
            _kind = H264MbKind.Inter;
            _partPred0 = _partPred1 = 1;
            if (type <= 2)
            {
                _partShape = type;
            }
            else
            {
                _partShape = 3;
                for (int i = 0; i < 4; i++)
                {
                    _subType[i] = ReadSubMbType();
                    noSubBelow8x8 &= _subType[i] == 0;
                }
            }
        }

        _mb.Kind[_mbAddr] = _kind;
        _mb.Direct8x8[_mbAddr] = (byte)directMask;
        if (directMask != 0)
        {
            PredictDirect(directMask);
        }

        if (_kind == H264MbKind.BDirect)
        {
            WriteDirect(15);
        }
        else
        {
            ReadMotion(type == 4 && !_h.IsB, directMask);
        }

        _cbp = ReadCbp(intra: false);
        _mb.Cbp[_mbAddr] = (byte)_cbp;
        if ((_cbp & 15) != 0 && _pps.Transform8x8Mode && noSubBelow8x8)
        {
            _t8x8 = ReadTransform8x8Flag();
        }

        _mb.Transform8x8[_mbAddr] = _t8x8;
        if (_cbp != 0)
        {
            ReadQpDelta();
            ReadResidual();
        }
        else
        {
            _prevQpDeltaNonZero = false;
        }

        PredictInter(directMask);
        ReconstructInterResidual();
    }

    private void ReadQpDelta()
    {
        int delta = _cabac ? ReadQpDeltaCabac() : _bits.Se();
        int limit = 26 + (_qpBdOffsetY / 2);
        if (delta < -limit || delta > limit - 1)
        {
            throw H264Errors.Corrupt($"mb_qp_delta {delta} is out of range.");
        }

        _prevQpDeltaNonZero = delta != 0;
        _mb.QpDeltaNonZero[_mbAddr] = delta != 0;
        _qp = ((_qp + delta + 52 + (2 * _qpBdOffsetY)) % (52 + _qpBdOffsetY)) - _qpBdOffsetY;
    }

    private void DecodePcm()
    {
        int luma = _pic.Origin + (_mbY * 16 * _pic.Stride) + (_mbX * 16);
        if (_cabac)
        {
            CabacEngine.RawReader raw = new(_engine.Data, _engine.Position);
            ReadPcmSamples(raw.Read, luma);
            _engine.Start(_engine.Data, raw.BytePosition, _engine.End);
        }
        else
        {
            while ((_bits.Position & 7) != 0)
            {
                _bits.Skip(1);
            }

            ReadPcmSamples(_bits.Read, luma);
        }
    }

    private void ReadPcmSamples(Func<int, int> read, int luma)
    {
        if (_pic.Narrow)
        {
            ReadPcmSamples<byte>(read, luma);
        }
        else
        {
            ReadPcmSamples<ushort>(read, luma);
        }
    }

    private void ReadPcmSamples<T>(Func<int, int> read, int luma)
        where T : unmanaged
    {
        T[] y0 = _pic.Y<T>();
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                H264Samples.Set(y0, luma + (y * _pic.Stride) + x, read(_bitDepthY));
            }
        }

        if (_chromaArrayType == 0)
        {
            return;
        }

        int w = _sps.MbWidthC, hgt = _sps.MbHeightC;
        int chroma = _pic.ChromaOrigin + (_mbY * hgt * _pic.ChromaStride) + (_mbX * w);
        foreach (T[] plane in new[] { _pic.Cb<T>(), _pic.Cr<T>() })
        {
            for (int y = 0; y < hgt; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    H264Samples.Set(plane, chroma + (y * _pic.ChromaStride) + x, read(_bitDepthC));
                }
            }
        }
    }
}
