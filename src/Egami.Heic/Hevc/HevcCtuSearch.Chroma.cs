namespace Egami.Heic.Hevc;

/// <summary>
/// Chroma: each chroma prediction block's intra_chroma_pred_mode by rate-distortion cost, its blocks coded on the luma transform
/// tree as the decoder places them (at half size for 4:2:0, two stacked squares for 4:2:2, once for four 4x4 luma blocks).
/// </summary>
internal sealed partial class HevcCtuSearch
{
    private void Chroma(HevcCodingUnit cu, int qp, double lambda)
    {
        bool parts = _chroma == 3 && cu.NxN;
        if (!parts)
        {
            ChromaPart(cu, 0, cu.Tree, cu.X, cu.Y, cu.X, cu.Y, cu.Log2, 0, 0, qp, lambda);
            return;
        }

        // 4:4:4 NxN: each prediction unit has its own chroma mode, coded in its quarter of the tree.
        int half = 1 << (cu.Log2 - 1);
        for (int i = 0; i < 4; i++)
        {
            int x = cu.X + ((i & 1) * half), y = cu.Y + ((i >> 1) * half);
            ChromaPart(cu, i, cu.Tree.Children![i], x, y, cu.X, cu.Y, cu.Log2 - 1, 1, i, qp, lambda);
        }

        // The root's flags, coded before its quarters: any chroma coefficients below it.
        HevcTransformNode root = cu.Tree;
        root.Cb0 = root.Cr0 = false;
        foreach (HevcTransformNode child in root.Children!)
        {
            root.Cb0 |= child.Cb0;
            root.Cr0 |= child.Cr0;
        }
    }

    /// <summary>Tries the chroma modes on one part of the tree and keeps the best.</summary>
    private void ChromaPart(HevcCodingUnit cu, int part, HevcTransformNode node, int x, int y, int xBase, int yBase, int log2, int depth, int blkIdx, int qp,
        double lambda)
    {
        ReadOnlySpan<int> all = [4, 0, 1, 2, 3];
        ReadOnlySpan<int> few = [4, 0];
        ReadOnlySpan<int> syntaxes = cu.Bypass && !_profile.AllChromaModes ? [4] : _profile.AllChromaModes ? all : few;
        int size = 1 << log2;
        double bestCost = double.MaxValue;
        int bestSyntax = 4;
        HevcChromaChoice bestChoice = default;
        if (syntaxes.Length > 1)
        {
            _chromaBefore.Capture(x, y, size, false, true, false);
            Array.Copy(_bins.Contexts, _chromaBefore.Contexts, HevcContexts.Count);
        }

        for (int i = 0; i < syntaxes.Length; i++)
        {
            int syntax = syntaxes[i];
            if (i > 0)
            {
                _chromaBefore.Restore();
                Array.Copy(_chromaBefore.Contexts, _bins.Contexts, HevcContexts.Count);
            }

            cu.ChromaSyntax[part] = syntax;
            int mode = HevcSyntaxWriter.ChromaPredictionMode(syntax, cu.LumaModes[part], _chroma);
            long saved = _bins.Cost;
            _bins.Cost = 0;
            _writer.ChromaMode(syntax);
            double cost = lambda * _bins.Cost / Nal.CabacCost.Bit;
            _bins.Cost = saved;
            cost += ChromaTree(cu, node, x, y, xBase, yBase, log2, depth, blkIdx, mode, qp, lambda);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestSyntax = syntax;
                bestChoice = HevcChromaChoice.Take(node);
                if (i + 1 < syntaxes.Length)
                {
                    _chromaBest.Capture(x, y, size, false, true, false);
                    Array.Copy(_bins.Contexts, _chromaBest.Contexts, HevcContexts.Count);
                }
            }
        }

        if (cu.ChromaSyntax[part] != bestSyntax)
        {
            _chromaBest.Restore();
            Array.Copy(_chromaBest.Contexts, _bins.Contexts, HevcContexts.Count);
            bestChoice.Put(node);
        }

        cu.ChromaSyntax[part] = bestSyntax;
    }

    /// <summary>
    /// Codes the chroma blocks of a transform tree node for a chroma mode, sets its chroma flags and levels, and gives the chroma
    /// J: weighted distortion plus lambda times the residual and coded block flag rates.
    /// </summary>
    private double ChromaTree(HevcCodingUnit cu, HevcTransformNode node, int x, int y, int xBase, int yBase, int log2, int depth, int blkIdx, int mode, int qp,
        double lambda)
    {
        double cost = 0;
        if (node.Split && (log2 > 3 || _chroma == 3))
        {
            int half = 1 << (log2 - 1);
            bool cb = false, cr = false;
            for (int i = 0; i < 4; i++)
            {
                HevcTransformNode child = node.Children![i];
                cost += ChromaTree(cu, child, x + ((i & 1) * half), y + ((i >> 1) * half), x, y, log2 - 1, depth + 1, i, mode, qp, lambda);
                cb |= child.Cb0 || child.Cb1;
                cr |= child.Cr0 || child.Cr1;
            }

            // A split node of 4:2:2 above 8x8 codes one flag per component for both halves.
            (node.Cb0, node.Cb1, node.Cr0, node.Cr1) = (cb, false, cr, false);
            return cost + ChromaFlagsCost(node, log2, depth, lambda);
        }

        // A transform unit, or an 8x8 node split into 4x4 luma blocks whose chroma is coded once (with the fourth).
        int log2C = _chroma == 3 ? log2 : log2 == 2 ? 2 : log2 - 1;
        if (node.Split)
        {
            log2C = 2;
        }

        int xC = x / _sps.SubWidthC, yC = y / _sps.SubHeightC;
        HevcTransformNode target = node.Split ? node.Children![3] : node;
        for (int c = 1; c <= 2; c++)
        {
            double weight = ChromaWeight(qp, c);
            for (int t = 0; t < (_chroma == 2 ? 2 : 1); t++)
            {
                BlockResult result = CodeBlock(c, xC, yC + (t << log2C), log2C, mode, qp, cu.Bypass, lambda, weight);
                int block = HevcTransformNode.Block(c, t);
                target.Levels[block] = result.Levels;
                target.TransformSkip[block] = result.TransformSkip;
                bool cbf = result.Levels is not null;
                SetChromaFlag(node, c, t, cbf);
                cost += result.Distortion + (lambda * result.Bits / Nal.CabacCost.Bit);
            }
        }

        if (node.Split)
        {
            // The 4x4 luma blocks inherit the flags (7.3.8.8): each reads them as its parent's.
            foreach (HevcTransformNode child in node.Children!)
            {
                (child.Cb0, child.Cb1, child.Cr0, child.Cr1) = (node.Cb0, node.Cb1, node.Cr0, node.Cr1);
            }
        }

        return cost + ChromaFlagsCost(node, log2, depth, lambda);
    }

    private static void SetChromaFlag(HevcTransformNode node, int c, int t, bool cbf)
    {
        switch (c * 2 + t)
        {
            case 2:
                node.Cb0 = cbf;
                break;
            case 3:
                node.Cb1 = cbf;
                break;
            case 4:
                node.Cr0 = cbf;
                break;
            default:
                node.Cr1 = cbf;
                break;
        }
    }

    /// <summary>The rate of a node's cbf_cb and cbf_cr flags (counted as if coded, the contexts left as they were).</summary>
    private double ChromaFlagsCost(HevcTransformNode node, int log2, int depth, double lambda)
    {
        int context = HevcContexts.CbfChroma + depth;
        byte state = _bins.Contexts[context];
        long cost = 0;
        bool pair = _chroma == 2 && (!node.Split || log2 == 3);
        cost += Nal.CabacCost.Update(ref _bins.Contexts[context], node.Cb0 ? 1 : 0);
        if (pair)
        {
            cost += Nal.CabacCost.Update(ref _bins.Contexts[context], node.Cb1 ? 1 : 0);
        }

        cost += Nal.CabacCost.Update(ref _bins.Contexts[context], node.Cr0 ? 1 : 0);
        if (pair)
        {
            cost += Nal.CabacCost.Update(ref _bins.Contexts[context], node.Cr1 ? 1 : 0);
        }

        _bins.Contexts[context] = state;
        return lambda * cost / Nal.CabacCost.Bit;
    }

    /// <summary>A node's chroma flags and levels, kept while other chroma modes are tried on it.</summary>
    private readonly record struct HevcChromaChoice(HevcChromaChoice.Snapshot Root)
    {
        internal static HevcChromaChoice Take(HevcTransformNode node) => new(Snapshot.Of(node));

        internal void Put(HevcTransformNode node) => Root.Into(node);

        internal sealed record Snapshot(bool Cb0, bool Cb1, bool Cr0, bool Cr1, short[]?[] Levels, bool[] Skips, Snapshot[]? Children)
        {
            internal static Snapshot Of(HevcTransformNode node) => new(node.Cb0, node.Cb1, node.Cr0, node.Cr1, [.. node.Levels], [.. node.TransformSkip],
                node.Children is null ? null : [.. node.Children.Select(Of)]);

            internal void Into(HevcTransformNode node)
            {
                (node.Cb0, node.Cb1, node.Cr0, node.Cr1) = (Cb0, Cb1, Cr0, Cr1);
                for (int b = 1; b < 5; b++)
                {
                    node.Levels[b] = Levels[b];
                    node.TransformSkip[b] = Skips[b];
                }

                for (int i = 0; Children is not null && i < 4; i++)
                {
                    Children[i].Into(node.Children![i]);
                }
            }
        }
    }
}
