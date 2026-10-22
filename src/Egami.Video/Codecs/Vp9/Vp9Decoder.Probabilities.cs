namespace Egami.Video.Codecs.Vp9;

/// <summary>The compressed header's probability updates and the backward adaptation after each frame (vp9_entropy*.c).</summary>
internal sealed partial class Vp9Decoder<T>
{
    private const int DiffUpdateProb = 252;
    private const int MvUpdateProb = 252;
    private const int ModeMvCountSat = 20;

    private static ReadOnlySpan<int> CountToUpdateFactor => [0, 6, 12, 19, 25, 32, 38, 44, 51, 57, 64, 70, 76, 83, 89, 96, 102, 108, 115, 121, 128];

    private void ReadCompressedHeader(Vp9FrameHeader h, int start)
    {
        Vp9BoolDecoder r = default;
        if (r.Init(_data, start, h.HeaderSizeInBytes))
        {
            throw new CorruptDataException("VP9", start, "The compressed header's marker bit is set.");
        }

        byte[] p = _fc.P;
        _txMode = h.Lossless ? 0 : ReadTxMode(ref r);
        if (_txMode == Vp9Tables.TxModeSelect)
        {
            for (int i = 0; i < 2; i++)
            {
                DiffUpdate(ref r, p, Vp9FrameContext.Tx8x8 + i);
            }

            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    DiffUpdate(ref r, p, Vp9FrameContext.Tx16x16 + (i * 2) + j);
                }
            }

            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    DiffUpdate(ref r, p, Vp9FrameContext.Tx32x32 + (i * 3) + j);
                }
            }
        }

        int maxTx = Vp9Tables.TxModeToBiggestTxSize[_txMode];
        for (int tx = 0; tx <= maxTx; tx++)
        {
            if (r.ReadBit() == 0)
            {
                continue;
            }

            for (int plane = 0; plane < 2; plane++)
            {
                for (int reference = 0; reference < 2; reference++)
                {
                    int baseOffset = Vp9FrameContext.CoefOffset(tx, plane, reference);
                    for (int band = 0; band < 6; band++)
                    {
                        for (int ctx = 0; ctx < (band == 0 ? 3 : 6); ctx++)
                        {
                            for (int node = 0; node < 3; node++)
                            {
                                DiffUpdate(ref r, p, baseOffset + (band * 18) + (ctx * 3) + node);
                            }
                        }
                    }
                }
            }
        }

        for (int i = 0; i < 3; i++)
        {
            DiffUpdate(ref r, p, Vp9FrameContext.Skip + i);
        }

        _referenceMode = Vp9Tables.SingleReference;
        if (!h.FrameIsIntraOnly)
        {
            for (int i = 0; i < 7 * 3; i++)
            {
                DiffUpdate(ref r, p, Vp9FrameContext.InterMode + i);
            }

            if (h.InterpFilter == Vp9Tables.Switchable)
            {
                for (int i = 0; i < 4 * 2; i++)
                {
                    DiffUpdate(ref r, p, Vp9FrameContext.SwitchableInterp + i);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                DiffUpdate(ref r, p, Vp9FrameContext.IntraInter + i);
            }

            _referenceMode = ReadReferenceMode(ref r);
            if (_referenceMode != Vp9Tables.SingleReference)
            {
                SetupCompoundReferenceMode();
            }

            if (_referenceMode == Vp9Tables.ReferenceModeSelect)
            {
                for (int i = 0; i < 5; i++)
                {
                    DiffUpdate(ref r, p, Vp9FrameContext.CompInter + i);
                }
            }

            if (_referenceMode != Vp9Tables.CompoundReference)
            {
                for (int i = 0; i < 5; i++)
                {
                    DiffUpdate(ref r, p, Vp9FrameContext.SingleRef + (i * 2));
                    DiffUpdate(ref r, p, Vp9FrameContext.SingleRef + (i * 2) + 1);
                }
            }

            if (_referenceMode != Vp9Tables.SingleReference)
            {
                for (int i = 0; i < 5; i++)
                {
                    DiffUpdate(ref r, p, Vp9FrameContext.CompRef + i);
                }
            }

            for (int i = 0; i < 4 * 9; i++)
            {
                DiffUpdate(ref r, p, Vp9FrameContext.YMode + i);
            }

            for (int i = 0; i < 16 * 3; i++)
            {
                DiffUpdate(ref r, p, Vp9FrameContext.Partition + i);
            }

            ReadMvProbs(ref r, p, h.AllowHighPrecisionMv);
        }

        if (r.HasError)
        {
            throw new CorruptDataException("VP9", start, "The compressed header is cut off.");
        }

        h.TxMode = _txMode;
        h.ReferenceMode = _referenceMode;
    }

    private static int ReadTxMode(ref Vp9BoolDecoder r)
    {
        int txMode = r.ReadLiteral(2);
        if (txMode == Vp9Tables.Allow32x32)
        {
            txMode += r.ReadBit();
        }

        return txMode;
    }

    private int ReadReferenceMode(ref Vp9BoolDecoder r)
    {
        bool allowed = _refSignBias[2] != _refSignBias[1] || _refSignBias[3] != _refSignBias[1];
        if (!allowed)
        {
            return Vp9Tables.SingleReference;
        }

        return r.ReadBit() != 0
            ? (r.ReadBit() != 0 ? Vp9Tables.ReferenceModeSelect : Vp9Tables.CompoundReference)
            : Vp9Tables.SingleReference;
    }

    private void SetupCompoundReferenceMode()
    {
        if (_refSignBias[Vp9Tables.LastFrame] == _refSignBias[Vp9Tables.GoldenFrame])
        {
            _compFixedRef = Vp9Tables.AltrefFrame;
            _compVarRef[0] = Vp9Tables.LastFrame;
            _compVarRef[1] = Vp9Tables.GoldenFrame;
        }
        else if (_refSignBias[Vp9Tables.LastFrame] == _refSignBias[Vp9Tables.AltrefFrame])
        {
            _compFixedRef = Vp9Tables.GoldenFrame;
            _compVarRef[0] = Vp9Tables.LastFrame;
            _compVarRef[1] = Vp9Tables.AltrefFrame;
        }
        else
        {
            _compFixedRef = Vp9Tables.LastFrame;
            _compVarRef[0] = Vp9Tables.GoldenFrame;
            _compVarRef[1] = Vp9Tables.AltrefFrame;
        }
    }

    private static void ReadMvProbs(ref Vp9BoolDecoder r, byte[] p, bool allowHp)
    {
        UpdateMvProbs(ref r, p, Vp9FrameContext.MvJoints, 3);
        for (int i = 0; i < 2; i++)
        {
            int c = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
            UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvSign, 1);
            UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvClasses, 10);
            UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvClass0, 1);
            UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvBits, 10);
        }

        for (int i = 0; i < 2; i++)
        {
            int c = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
            for (int j = 0; j < 2; j++)
            {
                UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvClass0Fp + (j * 3), 3);
            }

            UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvFp, 3);
        }

        if (allowHp)
        {
            for (int i = 0; i < 2; i++)
            {
                int c = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
                UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvClass0Hp, 1);
                UpdateMvProbs(ref r, p, c + Vp9FrameContext.MvHp, 1);
            }
        }
    }

    private static void UpdateMvProbs(ref Vp9BoolDecoder r, byte[] p, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (r.Read(MvUpdateProb) != 0)
            {
                p[offset + i] = (byte)((r.ReadLiteral(7) << 1) | 1);
            }
        }
    }

    /// <summary>Reads one probability's optional update (vp9_diff_update_prob): a sub-exponential delta, remapped around the old value.</summary>
    internal static void DiffUpdate(ref Vp9BoolDecoder r, byte[] p, int index)
    {
        if (r.Read(DiffUpdateProb) != 0)
        {
            int delta = DecodeTermSubexp(ref r);
            p[index] = (byte)InvRemapProb(delta, p[index]);
        }
    }

    internal static int DecodeTermSubexp(ref Vp9BoolDecoder r)
    {
        if (r.ReadBit() == 0)
        {
            return r.ReadLiteral(4);
        }

        if (r.ReadBit() == 0)
        {
            return r.ReadLiteral(4) + 16;
        }

        if (r.ReadBit() == 0)
        {
            return r.ReadLiteral(5) + 32;
        }

        int v = r.ReadLiteral(7);
        const int m = (1 << 8) - 191;
        return (v < m ? v : (v << 1) - m + r.ReadBit()) + 64;
    }

    private static int InvRemapProb(int v, int m)
    {
        v = Vp9Tables.InvMapTable[Math.Min(v, Vp9Tables.InvMapTable.Length - 1)];
        m--;
        return (m << 1) <= 255 ? 1 + InvRecenterNonneg(v, m) : 255 - InvRecenterNonneg(v, 255 - 1 - m);
    }

    private static int InvRecenterNonneg(int v, int m)
    {
        if (v > 2 * m)
        {
            return v;
        }

        return (v & 1) != 0 ? m - ((v + 1) >> 1) : m + (v >> 1);
    }

    // Backward adaptation.

    private void AdaptCoefProbs(Vp9FrameHeader h, int contextIdx)
    {
        int updateFactor, countSat = 24;
        if (h.FrameIsIntraOnly)
        {
            updateFactor = 112;
        }
        else if (_lastFrameKey)
        {
            updateFactor = 128;
        }
        else
        {
            updateFactor = 112;
        }

        byte[] p = _fc.P, pre = _contexts[contextIdx].P;
        int[] coef = _counts.Coef, eob = _counts.EobBranch;
        for (int tx = 0; tx < 4; tx++)
        {
            for (int plane = 0; plane < 2; plane++)
            {
                for (int reference = 0; reference < 2; reference++)
                {
                    int probBase = Vp9FrameContext.CoefOffset(tx, plane, reference);
                    int countBase = ((((tx * 2) + plane) * 2) + reference) * 36;
                    for (int band = 0; band < 6; band++)
                    {
                        for (int ctx = 0; ctx < (band == 0 ? 3 : 6); ctx++)
                        {
                            int c = countBase + (band * 6) + ctx;
                            int n0 = coef[(c * 4) + 0], n1 = coef[(c * 4) + 1], n2 = coef[(c * 4) + 2], neob = coef[(c * 4) + 3];
                            int i = probBase + (band * 18) + (ctx * 3);
                            p[i] = MergeProbs(pre[i], neob, eob[c] - neob, countSat, updateFactor);
                            p[i + 1] = MergeProbs(pre[i + 1], n0, n1 + n2, countSat, updateFactor);
                            p[i + 2] = MergeProbs(pre[i + 2], n1, n2, countSat, updateFactor);
                        }
                    }
                }
            }
        }
    }

    private void AdaptModeProbs(Vp9FrameHeader h, int contextIdx)
    {
        byte[] p = _fc.P, pre = _contexts[contextIdx].P;
        Vp9Counts c = _counts;
        for (int i = 0; i < 4; i++)
        {
            p[Vp9FrameContext.IntraInter + i] = ModeMvMergeProbs(pre[Vp9FrameContext.IntraInter + i], c.IntraInter[i * 2], c.IntraInter[(i * 2) + 1]);
        }

        for (int i = 0; i < 5; i++)
        {
            p[Vp9FrameContext.CompInter + i] = ModeMvMergeProbs(pre[Vp9FrameContext.CompInter + i], c.CompInter[i * 2], c.CompInter[(i * 2) + 1]);
        }

        for (int i = 0; i < 5; i++)
        {
            p[Vp9FrameContext.CompRef + i] = ModeMvMergeProbs(pre[Vp9FrameContext.CompRef + i], c.CompRef[i * 2], c.CompRef[(i * 2) + 1]);
        }

        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                int k = (i * 2) + j;
                p[Vp9FrameContext.SingleRef + k] = ModeMvMergeProbs(pre[Vp9FrameContext.SingleRef + k], c.SingleRef[k * 2], c.SingleRef[(k * 2) + 1]);
            }
        }

        for (int i = 0; i < 7; i++)
        {
            TreeMergeProbs(Vp9Tables.InterModeTree, pre.AsSpan(Vp9FrameContext.InterMode + (i * 3)), c.InterMode.AsSpan(i * 4), p.AsSpan(Vp9FrameContext.InterMode + (i * 3)));
        }

        for (int i = 0; i < 4; i++)
        {
            TreeMergeProbs(Vp9Tables.IntraModeTree, pre.AsSpan(Vp9FrameContext.YMode + (i * 9)), c.YMode.AsSpan(i * 10), p.AsSpan(Vp9FrameContext.YMode + (i * 9)));
        }

        for (int i = 0; i < 10; i++)
        {
            TreeMergeProbs(Vp9Tables.IntraModeTree, pre.AsSpan(Vp9FrameContext.UVMode + (i * 9)), c.UVMode.AsSpan(i * 10), p.AsSpan(Vp9FrameContext.UVMode + (i * 9)));
        }

        for (int i = 0; i < 16; i++)
        {
            TreeMergeProbs(Vp9Tables.PartitionTree, pre.AsSpan(Vp9FrameContext.Partition + (i * 3)), c.Partition.AsSpan(i * 4), p.AsSpan(Vp9FrameContext.Partition + (i * 3)));
        }

        if (h.InterpFilter == Vp9Tables.Switchable)
        {
            for (int i = 0; i < 4; i++)
            {
                TreeMergeProbs(Vp9Tables.SwitchableInterpTree, pre.AsSpan(Vp9FrameContext.SwitchableInterp + (i * 2)), c.SwitchableInterp.AsSpan(i * 3), p.AsSpan(Vp9FrameContext.SwitchableInterp + (i * 2)));
            }
        }

        if (_txMode == Vp9Tables.TxModeSelect)
        {
            for (int i = 0; i < 2; i++)
            {
                ReadOnlySpan<int> t8 = c.Tx8x8.AsSpan(i * 2);
                p[Vp9FrameContext.Tx8x8 + i] = ModeMvMergeProbs(pre[Vp9FrameContext.Tx8x8 + i], t8[0], t8[1]);

                ReadOnlySpan<int> t16 = c.Tx16x16.AsSpan(i * 3);
                int b16 = Vp9FrameContext.Tx16x16 + (i * 2);
                p[b16] = ModeMvMergeProbs(pre[b16], t16[0], t16[1] + t16[2]);
                p[b16 + 1] = ModeMvMergeProbs(pre[b16 + 1], t16[1], t16[2]);

                ReadOnlySpan<int> t32 = c.Tx32x32.AsSpan(i * 4);
                int b32 = Vp9FrameContext.Tx32x32 + (i * 3);
                p[b32] = ModeMvMergeProbs(pre[b32], t32[0], t32[1] + t32[2] + t32[3]);
                p[b32 + 1] = ModeMvMergeProbs(pre[b32 + 1], t32[1], t32[2] + t32[3]);
                p[b32 + 2] = ModeMvMergeProbs(pre[b32 + 2], t32[2], t32[3]);
            }
        }

        for (int i = 0; i < 3; i++)
        {
            p[Vp9FrameContext.Skip + i] = ModeMvMergeProbs(pre[Vp9FrameContext.Skip + i], c.Skip[i * 2], c.Skip[(i * 2) + 1]);
        }
    }

    private void AdaptMvProbs(Vp9FrameHeader h, int contextIdx)
    {
        byte[] p = _fc.P, pre = _contexts[contextIdx].P;
        Vp9Counts c = _counts;
        TreeMergeProbs(Vp9Tables.MvJointTree, pre.AsSpan(Vp9FrameContext.MvJoints), c.MvJoints, p.AsSpan(Vp9FrameContext.MvJoints));
        for (int i = 0; i < 2; i++)
        {
            int o = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
            int k = i * Vp9Counts.MvCompSize;
            int[] mc = c.MvComp;
            p[o + Vp9FrameContext.MvSign] = ModeMvMergeProbs(pre[o + Vp9FrameContext.MvSign], mc[k + Vp9Counts.MvSign], mc[k + Vp9Counts.MvSign + 1]);
            TreeMergeProbs(Vp9Tables.MvClassTree, pre.AsSpan(o + Vp9FrameContext.MvClasses), mc.AsSpan(k + Vp9Counts.MvClasses), p.AsSpan(o + Vp9FrameContext.MvClasses));
            TreeMergeProbs(Vp9Tables.MvClass0Tree, pre.AsSpan(o + Vp9FrameContext.MvClass0), mc.AsSpan(k + Vp9Counts.MvClass0), p.AsSpan(o + Vp9FrameContext.MvClass0));
            for (int j = 0; j < 10; j++)
            {
                int b = k + Vp9Counts.MvBits + (j * 2);
                p[o + Vp9FrameContext.MvBits + j] = ModeMvMergeProbs(pre[o + Vp9FrameContext.MvBits + j], mc[b], mc[b + 1]);
            }

            for (int j = 0; j < 2; j++)
            {
                TreeMergeProbs(Vp9Tables.MvFpTree, pre.AsSpan(o + Vp9FrameContext.MvClass0Fp + (j * 3)), mc.AsSpan(k + Vp9Counts.MvClass0Fp + (j * 4)), p.AsSpan(o + Vp9FrameContext.MvClass0Fp + (j * 3)));
            }

            TreeMergeProbs(Vp9Tables.MvFpTree, pre.AsSpan(o + Vp9FrameContext.MvFp), mc.AsSpan(k + Vp9Counts.MvFp), p.AsSpan(o + Vp9FrameContext.MvFp));
            if (h.AllowHighPrecisionMv)
            {
                p[o + Vp9FrameContext.MvClass0Hp] = ModeMvMergeProbs(pre[o + Vp9FrameContext.MvClass0Hp], mc[k + Vp9Counts.MvClass0Hp], mc[k + Vp9Counts.MvClass0Hp + 1]);
                p[o + Vp9FrameContext.MvHp] = ModeMvMergeProbs(pre[o + Vp9FrameContext.MvHp], mc[k + Vp9Counts.MvHp], mc[k + Vp9Counts.MvHp + 1]);
            }
        }
    }

    private static byte GetProb(long num, long den)
    {
        int p = (int)(((num * 256) + (den >> 1)) / den);
        return (byte)Math.Clamp(p, 1, 255);
    }

    private static byte WeightedProb(int prob1, int prob2, int factor) => (byte)(((prob1 * (256 - factor)) + (prob2 * factor) + 128) >> 8);

    private static byte MergeProbs(byte preProb, int ct0, int ct1, int countSat, int maxUpdateFactor)
    {
        int den = ct0 + ct1;
        byte prob = den == 0 ? (byte)128 : GetProb((uint)ct0, (uint)den);
        int count = Math.Min(den, countSat);
        int factor = maxUpdateFactor * count / countSat;
        return WeightedProb(preProb, prob, factor);
    }

    private static byte ModeMvMergeProbs(byte preProb, int ct0, int ct1)
    {
        int den = ct0 + ct1;
        if (den == 0)
        {
            return preProb;
        }

        int count = Math.Min(den, ModeMvCountSat);
        int factor = CountToUpdateFactor[count];
        return WeightedProb(preProb, GetProb((uint)ct0, (uint)den), factor);
    }

    /// <summary>Adapts the probabilities of a tree from the leaf counts (vpx_tree_merge_probs).</summary>
    private static void TreeMergeProbs(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> pre, ReadOnlySpan<int> counts, Span<byte> probs) =>
        TreeMergeNode(0, tree, pre, counts, probs);

    private static int TreeMergeNode(int i, ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> pre, ReadOnlySpan<int> counts, Span<byte> probs)
    {
        int l = tree[i];
        int leftCount = l <= 0 ? counts[-l] : TreeMergeNode(l, tree, pre, counts, probs);
        int r = tree[i + 1];
        int rightCount = r <= 0 ? counts[-r] : TreeMergeNode(r, tree, pre, counts, probs);
        probs[i >> 1] = ModeMvMergeProbs(pre[i >> 1], leftCount, rightCount);
        return leftCount + rightCount;
    }
}
