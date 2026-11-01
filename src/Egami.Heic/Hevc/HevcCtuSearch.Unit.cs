namespace Egami.Heic.Hevc;

/// <summary>The coding unit: its partitioning (2Nx2N or NxN), the luma and chroma decisions, its rate, and the block maps it leaves.</summary>
internal sealed partial class HevcCtuSearch
{
    private readonly byte[] _contextsAtUnit = new byte[HevcContexts.Count];

    /// <summary>Decides, reconstructs and commits the coding unit at (x0, y0); gives its cost J.</summary>
    private HevcCodingUnit CodingUnit(int x0, int y0, int log2, int depth, out double cost)
    {
        // The quantization group of the coding unit (8.6.1): a new one starts the QP prediction again.
        int mask = (1 << _log2MinCuQpDeltaSize) - 1;
        int xQg = x0 & ~mask, yQg = y0 & ~mask;
        if (xQg != _qp.QgX || yQg != _qp.QgY || _qp.FirstQgInGroup)
        {
            StartQuantizationGroup(xQg, yQg);
        }

        int qp = _slice.SliceQp;
        if (_pps.CuQpDeltaEnabled)
        {
            int offset = _sps.QpBdOffsetLuma;
            qp = _writer.IsCuQpDeltaCoded ? _qp.QpY : Math.Clamp(QgQp(x0, y0), _qp.QpYPred - 26 - (offset / 2), _qp.QpYPred + 25 + (offset / 2));
        }

        int size = 1 << log2;
        bool tryNxN = log2 == _sps.MinCbLog2Size && _profile.NxN;
        if (tryNxN)
        {
            Save(_partBefore, x0, y0, size);
        }

        HevcCodingUnit cu = Partition(x0, y0, log2, depth, false, qp, out cost);
        if (!tryNxN)
        {
            return cu;
        }

        Save(_partBest, x0, y0, size);
        QpState after = _qp;
        Restore(_partBefore, x0, y0, size);
        HevcCodingUnit split = Partition(x0, y0, log2, depth, true, qp, out double splitCost);
        if (splitCost < cost)
        {
            cost = splitCost;
            return split;
        }

        Restore(_partBest, x0, y0, size);
        _qp = after;
        return cu;
    }

    /// <summary>Decides one partitioning of a coding unit, codes and commits it, and gives its cost.</summary>
    private HevcCodingUnit Partition(int x0, int y0, int log2, int depth, bool nxn, int qp, out double cost)
    {
        HevcCodingUnit cu = new() { X = x0, Y = y0, Log2 = log2, Depth = depth, NxN = nxn, Bypass = _settings.Lossless, Qp = qp };
        Array.Copy(_bins.Contexts, _contextsAtUnit, HevcContexts.Count);
        bool qpDeltaCoded = _writer.IsCuQpDeltaCoded;
        double lambda = Lambda(qp);

        // Luma: each prediction unit's mode and transform tree.
        HevcTransformNode root;
        if (nxn)
        {
            root = new HevcTransformNode { Split = true, Children = new HevcTransformNode[4] };
            int half = 1 << (log2 - 1);
            for (int i = 0; i < 4; i++)
            {
                root.Children[i] = LumaPredictionUnit(cu, i, x0 + ((i & 1) * half), y0 + ((i >> 1) * half), log2 - 1, 1, qp, lambda);
            }
        }
        else
        {
            root = LumaPredictionUnit(cu, 0, x0, y0, log2, 0, qp, lambda);
        }

        cu.Tree = root;

        // Chroma: the modes, coded on the luma transform tree.
        if (_chroma != 0)
        {
            Chroma(cu, qp, lambda);
        }

        // The unit's rate from the contexts it started with, and cu_qp_delta when it is the group's first with coefficients.
        bool coefficients = HasCoefficients(root);
        if (_pps.CuQpDeltaEnabled && coefficients && !qpDeltaCoded)
        {
            cu.QpDelta = qp - _qp.QpYPred;
            _qp.QpY = qp;
        }

        Array.Copy(_contextsAtUnit, _bins.Contexts, HevcContexts.Count);
        _writer.IsCuQpDeltaCoded = qpDeltaCoded;
        _bins.Cost = 0;
        _writer.CodingUnit(cu);
        long bits = _bins.Cost;

        if (!_pps.CuQpDeltaEnabled)
        {
            _qp.QpY = _slice.SliceQp;
        }

        cu.Qp = _qp.QpY;
        _qp.LastQpY = _qp.QpY;
        double distortion = Distortion(0, x0, y0, log2);
        for (int c = 1; c < _picture.Planes.Length; c++)
        {
            distortion += ChromaWeight(qp, c) * Distortion(c, x0 / _sps.SubWidthC, y0 / _sps.SubHeightC, log2, true);
        }

        cost = distortion + (lambda * bits / Nal.CabacCost.Bit);
        Commit(cu);
        return cu;
    }

    /// <summary>The sum of squared differences between the source and the reconstruction of a block.</summary>
    private long Distortion(int c, int x, int y, int log2, bool chroma = false)
    {
        int width = 1 << log2, height = 1 << log2;
        if (chroma)
        {
            width >>= _sps.SubWidthC - 1;
            height >>= _sps.SubHeightC - 1;
        }

        return Sse(_source.Planes[c], _picture.Planes[c], (y * _picture.Widths[c]) + x, _picture.Widths[c], width, height);
    }

    /// <summary>Fills the picture's block maps for a decided coding unit, as the decoder does when it decodes it.</summary>
    private void Commit(HevcCodingUnit cu)
    {
        int size = 1 << cu.Log2;
        FillBlocks(_picture.CtDepth, cu.X, cu.Y, size, size, (byte)cu.Depth);
        MarkTransformEdges(cu.X, cu.Y, size);
        int parts = cu.NxN ? 4 : 1, pbSize = cu.NxN ? size >> 1 : size;
        for (int i = 0; i < parts; i++)
        {
            FillBlocks(_picture.IntraMode, cu.X + ((i & 1) * pbSize), cu.Y + ((i >> 1) * pbSize), pbSize, pbSize, (byte)cu.LumaModes[i]);
        }

        MarkTree(cu.Tree, cu.X, cu.Y, cu.Log2);
        if (cu.Bypass)
        {
            FillFlags(cu.X, cu.Y, size, HevcPicture.Bypass);
        }

        FillBlocks(System.Runtime.CompilerServices.Unsafe.As<byte[]>(_picture.QpY), cu.X, cu.Y, size, size, (byte)(sbyte)cu.Qp);
    }

    private void MarkTree(HevcTransformNode node, int x0, int y0, int log2)
    {
        if (node.Split)
        {
            int half = 1 << (log2 - 1);
            for (int i = 0; i < 4; i++)
            {
                MarkTree(node.Children![i], x0 + ((i & 1) * half), y0 + ((i >> 1) * half), log2 - 1);
            }

            return;
        }

        MarkTransformEdges(x0, y0, 1 << log2);
        if (node.CbfLuma)
        {
            FillFlags(x0, y0, 1 << log2, HevcPicture.Coded);
        }
    }

    private void FillBlocks(byte[] map, int x0, int y0, int width, int height, byte value)
    {
        int x4 = x0 >> 2, y4 = y0 >> 2;
        int w4 = Math.Min((x0 + width + 3) >> 2, _picture.Width4) - x4, h4 = Math.Min((y0 + height + 3) >> 2, _picture.Height4) - y4;
        for (int y = 0; y < h4 && w4 > 0; y++)
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

    /// <summary>Marks a transform block's left and top edges for the deblocking filter.</summary>
    private void MarkTransformEdges(int x0, int y0, int size)
    {
        int x4 = x0 >> 2, y4 = y0 >> 2;
        int w4 = Math.Min((x0 + size + 3) >> 2, _picture.Width4) - x4, h4 = Math.Min((y0 + size + 3) >> 2, _picture.Height4) - y4;
        for (int y = 0; y < h4; y++)
        {
            _picture.Flags[((y4 + y) * _picture.Width4) + x4] |= HevcPicture.EdgeLeft | HevcPicture.TransformLeft;
        }

        for (int x = 0; x < w4; x++)
        {
            _picture.Flags[(y4 * _picture.Width4) + x4 + x] |= HevcPicture.EdgeTop | HevcPicture.TransformTop;
        }
    }
}
