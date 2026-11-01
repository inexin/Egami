using System.Runtime.CompilerServices;

namespace Egami.Heic.Hevc;

/// <summary>
/// Decides and reconstructs the coding tree units of a picture, one CTB row at a time on one thread: the coding quadtree, each
/// coding unit's partitioning, intra modes and transform tree, by rate-distortion cost J = D + lambda R with the rate counted by
/// <see cref="HevcSyntaxWriter"/> on contexts that follow the bitstream as the decoder's would. The reconstruction is the
/// decoder's own (<see cref="HevcIntra"/>, <see cref="HevcTransform"/>), written into the picture, whose block maps it fills as
/// the decoder does; so the final bitstream decodes to it exactly.
/// </summary>
[SkipLocalsInit]
internal sealed partial class HevcCtuSearch
{
    private readonly HevcEncoderSettings _settings;
    private readonly HevcEncoderProfile _profile;
    private readonly HevcPicture _picture;
    private readonly HevcSps _sps;
    private readonly HevcPps _pps;
    private readonly HevcSliceHeader _slice;
    private readonly HevcSource _source;
    private readonly int[] _qgQp;
    private readonly HevcIntra _intra = new();
    private readonly HevcBins _bins = new();
    private readonly HevcSyntaxWriter _writer;
    private readonly int _log2MinCuQpDeltaSize;
    private readonly int _chroma;

    // Working buffers of one block.
    private readonly int[] _residual = new int[32 * 32];
    private readonly int[] _coefficients = new int[32 * 32];
    private readonly int[] _scaled = new int[32 * 32];
    private readonly int[] _inverse = new int[32 * 32];
    private readonly short[] _scratch = new short[32 * 32];
    private readonly short[] _levels = new short[32 * 32];
    private readonly short[] _levelsTs = new short[32 * 32];
    private readonly ushort[] _prediction = new ushort[32 * 32];
    private readonly byte[] _contextsSaved = new byte[HevcContexts.Count];

    // The snapshots of the coding quadtree search, two per depth (before, and the best so far), and of the transform search.
    private readonly HevcSnapshot[] _before = new HevcSnapshot[5];
    private readonly HevcSnapshot[] _best = new HevcSnapshot[5];
    private readonly HevcSnapshot[] _tuBefore = new HevcSnapshot[7];
    private readonly HevcSnapshot[] _tuBest = new HevcSnapshot[7];
    private readonly HevcSnapshot _partBefore;
    private readonly HevcSnapshot _partBest;
    private readonly HevcSnapshot _chromaBefore;
    private readonly HevcSnapshot _chromaBest;

    // QP state (8.6.1), as the decoder keeps it.
    private QpState _qp;

    internal HevcCtuSearch(HevcEncoderSettings settings, HevcEncoderProfile profile, HevcPicture picture, HevcSliceHeader slice, HevcSource source, int[] qgQp)
    {
        _settings = settings;
        _profile = profile;
        _picture = picture;
        _sps = picture.Sps;
        _pps = picture.Pps;
        _slice = slice;
        _source = source;
        _qgQp = qgQp;
        _writer = new HevcSyntaxWriter(picture, slice, _bins);
        _log2MinCuQpDeltaSize = _sps.CtbLog2Size - _pps.DiffCuQpDeltaDepth;
        _chroma = _sps.ChromaArrayType;
        for (int i = 0; i < _before.Length; i++)
        {
            _before[i] = new HevcSnapshot(picture);
            _best[i] = new HevcSnapshot(picture);
        }

        for (int i = 0; i < _tuBefore.Length; i++)
        {
            _tuBefore[i] = new HevcSnapshot(picture);
            _tuBest[i] = new HevcSnapshot(picture);
        }

        _partBefore = new HevcSnapshot(picture);
        _partBest = new HevcSnapshot(picture);
        _chromaBefore = new HevcSnapshot(picture);
        _chromaBest = new HevcSnapshot(picture);
    }

    /// <summary>Gets the contexts the rate estimates use, for wavefront synchronization between rows.</summary>
    internal byte[] Contexts => _bins.Contexts;

    /// <summary>Starts a CTB row (a substream): the contexts from the row above's second CTB, or initialized; the QP prediction again.</summary>
    internal void StartRow(byte[]? contexts)
    {
        if (contexts is null)
        {
            HevcContexts.Initialize(_bins.Contexts, _slice.SliceQp);
        }
        else
        {
            Array.Copy(contexts, _bins.Contexts, HevcContexts.Count);
        }

        _qp = new QpState { FirstQgInGroup = true, QgX = -1, QgY = -1, LastQpY = _slice.SliceQp, QpY = _slice.SliceQp };
    }

    /// <summary>Decides and reconstructs a CTB; returns its coding units in decoding order.</summary>
    internal List<HevcCodingUnit> CodeCtu(int ctbAddrRs)
    {
        int widthCtbs = _sps.WidthInCtbs;
        int xCtb = (ctbAddrRs % widthCtbs) << _sps.CtbLog2Size, yCtb = (ctbAddrRs / widthCtbs) << _sps.CtbLog2Size;
        List<HevcCodingUnit> cus = [];

        // SAO syntax comes first in the CTB; its bins are counted once the SAO search has run, so here the contexts skip it.
        Quadtree(xCtb, yCtb, _sps.CtbLog2Size, 0, cus);
        return cus;
    }

    private double Lambda(int qp) => 0.57 * Math.Pow(2, (qp + _sps.QpBdOffsetLuma - 12) / 3.0);

    /// <summary>The weight of chroma distortion: 2^((QpY - QpC) / 3), so chroma is weighed with its own QP's lambda.</summary>
    private double ChromaWeight(int qpY, int c)
    {
        int qpc = Qp(c, qpY) - _sps.QpBdOffsetChroma;
        return Math.Pow(2, (qpY - qpc) / 3.0);
    }

    /// <summary>Gets Qp'Y or Qp'Cb / Qp'Cr for a component (8.6.1).</summary>
    private int Qp(int c, int qpY)
    {
        if (c == 0)
        {
            return qpY + _sps.QpBdOffsetLuma;
        }

        int offset = c == 1 ? _pps.CbQpOffset + _slice.CbQpOffset : _pps.CrQpOffset + _slice.CrQpOffset;
        int qpi = Math.Clamp(qpY + offset, -_sps.QpBdOffsetChroma, 57);
        int qpc = _chroma == 1 ? HevcSliceDecoder.ChromaQp(qpi) : Math.Min(qpi, 51);
        return qpc + _sps.QpBdOffsetChroma;
    }

    // ---------------------------------------------------------------- the coding quadtree

    private double Quadtree(int x0, int y0, int log2, int depth, List<HevcCodingUnit> cus)
    {
        int size = 1 << log2;
        bool inside = x0 + size <= _sps.Width && y0 + size <= _sps.Height;
        bool canSplit = log2 > _sps.MinCbLog2Size;
        bool mustSplit = canSplit && (!inside || log2 > _profile.MaxCuLog2);
        bool canStop = inside && !mustSplit;
        canSplit &= mustSplit || log2 > _profile.MinCuLog2;
        bool flagCoded = inside && log2 > _sps.MinCbLog2Size;

        if (_pps.CuQpDeltaEnabled && log2 >= _log2MinCuQpDeltaSize)
        {
            _writer.IsCuQpDeltaCoded = false;
        }

        double stopCost = double.MaxValue;
        int start = cus.Count;
        HevcSnapshot before = _before[depth], best = _best[depth];
        if (canStop && canSplit)
        {
            Save(before, x0, y0, size);
        }

        bool coefficients = true;
        if (canStop)
        {
            long bits = _bins.Cost;
            if (flagCoded)
            {
                _bins.Cost = 0;
                _writer.SplitFlag(x0, y0, depth, false);
                bits = _bins.Cost;
            }
            else
            {
                bits = 0;
            }

            HevcCodingUnit cu = CodingUnit(x0, y0, log2, depth, out double cost);
            cus.Add(cu);
            stopCost = cost + Rate(bits, cu.Qp);
            coefficients = HasCoefficients(cu.Tree);
        }

        if (!canSplit || (canStop && _profile.EarlyStop && !coefficients && !mustSplit))
        {
            return stopCost;
        }

        if (canStop)
        {
            Save(best, x0, y0, size);
            Restore(before, x0, y0, size);
        }

        List<HevcCodingUnit> stopCus = canStop ? cus.GetRange(start, cus.Count - start) : [];
        cus.RemoveRange(start, cus.Count - start);
        double splitCost = 0;
        if (flagCoded)
        {
            _bins.Cost = 0;
            _writer.SplitFlag(x0, y0, depth, true);
            splitCost = Rate(_bins.Cost, QgQp(x0, y0));
        }

        int half = size >> 1;
        for (int i = 0; i < 4 && splitCost < stopCost; i++)
        {
            int x = x0 + ((i & 1) * half), y = y0 + ((i >> 1) * half);
            if (x < _sps.Width && y < _sps.Height)
            {
                splitCost += Quadtree(x, y, log2 - 1, depth + 1, cus);
            }
        }

        if (splitCost < stopCost)
        {
            return splitCost;
        }

        Restore(best, x0, y0, size);
        cus.RemoveRange(start, cus.Count - start);
        cus.AddRange(stopCus);
        return stopCost;
    }

    private static bool HasCoefficients(HevcTransformNode node)
    {
        if (node.Split)
        {
            foreach (HevcTransformNode child in node.Children!)
            {
                if (HasCoefficients(child))
                {
                    return true;
                }
            }

            return node.Cb0 || node.Cb1 || node.Cr0 || node.Cr1;
        }

        return node.CbfLuma || node.Cb0 || node.Cb1 || node.Cr0 || node.Cr1;
    }

    /// <summary>The cost in distortion units of a rate in 1/32768 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double Rate(long bits, int qp) => Lambda(qp) * bits / Nal.CabacCost.Bit;

    /// <summary>The QP the adaptive quantizer gives the quantization group of (x, y).</summary>
    private int QgQp(int x, int y)
    {
        if (!_pps.CuQpDeltaEnabled)
        {
            return _slice.SliceQp;
        }

        int width = (_sps.Width + (1 << _log2MinCuQpDeltaSize) - 1) >> _log2MinCuQpDeltaSize;
        return _qgQp[((y >> _log2MinCuQpDeltaSize) * width) + (x >> _log2MinCuQpDeltaSize)];
    }

    /// <summary>Derives qPY_PRED for the quantization group starting at (xQg, yQg) (8.6.1).</summary>
    private void StartQuantizationGroup(int xQg, int yQg)
    {
        (_qp.QgX, _qp.QgY) = (xQg, yQg);
        int previous = _qp.FirstQgInGroup ? _slice.SliceQp : _qp.LastQpY;
        _qp.FirstQgInGroup = false;
        int ctbMask = ~((1 << _sps.CtbLog2Size) - 1);
        int qpA = previous, qpB = previous;
        if (_picture.Available(xQg, yQg, xQg - 1, yQg) && ((xQg - 1) & ctbMask) == (xQg & ctbMask))
        {
            qpA = _picture.QpY[((yQg >> 2) * _picture.Width4) + ((xQg - 1) >> 2)];
        }

        if (_picture.Available(xQg, yQg, xQg, yQg - 1) && ((yQg - 1) & ctbMask) == (yQg & ctbMask))
        {
            qpB = _picture.QpY[(((yQg - 1) >> 2) * _picture.Width4) + (xQg >> 2)];
        }

        _qp.QpYPred = (qpA + qpB + 1) >> 1;
        _qp.QpY = _qp.QpYPred;
    }

    // ---------------------------------------------------------------- snapshots

    private void Save(HevcSnapshot snapshot, int x0, int y0, int size)
    {
        snapshot.Capture(x0, y0, size, true, true);
        Array.Copy(_bins.Contexts, snapshot.Contexts, HevcContexts.Count);
        snapshot.Qp = _qp;
        snapshot.QpDeltaCoded = _writer.IsCuQpDeltaCoded;
    }

    private void Restore(HevcSnapshot snapshot, int x0, int y0, int size)
    {
        snapshot.Restore();
        Array.Copy(snapshot.Contexts, _bins.Contexts, HevcContexts.Count);
        _qp = snapshot.Qp;
        _writer.IsCuQpDeltaCoded = snapshot.QpDeltaCoded;
    }

    /// <summary>The QP state of 8.6.1 the search carries along, saved and restored with the snapshots.</summary>
    internal struct QpState
    {
        internal int QpY;
        internal int QgX;
        internal int QgY;
        internal int LastQpY;
        internal int QpYPred;
        internal bool FirstQgInGroup;
    }
}
