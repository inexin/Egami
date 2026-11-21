using System.Diagnostics;

namespace Egami.Heic.Hevc;

/// <summary>
/// The inter syntax of P and B slices (H.265 7.3.8.5, 7.3.8.6, 7.3.8.8, 7.3.8.9), the inverse of the decoder's: cu_skip_flag,
/// pred_mode_flag, part_mode, each prediction unit's merge or motion vector syntax, rqt_root_cbf and the inter transform tree.
/// </summary>
internal sealed partial class HevcSyntaxWriter
{
    /// <summary>
    /// Gets or sets whether an inter unit's transform tree writes only its split and coded block flags, not the transform units: for
    /// the search, whose blocks counted their residuals as they were coded.
    /// </summary>
    internal bool FlagsOnly { get; set; }

    partial void IntraInInterSlice(HevcCodingUnit cu)
    {
        if (_slice.SliceType == HevcSliceHeader.I)
        {
            return;
        }

        SkipFlag(cu.X, cu.Y, false);
        Bins.Encode(HevcContexts.PredModeFlag, 1);
    }

    partial void InterCodingUnit(HevcCodingUnit cu, ref bool inter)
    {
        if (!cu.Inter)
        {
            return;
        }

        inter = true;
        _cu = cu;
        if (_pps.TransquantBypassEnabled)
        {
            Bins.Encode(HevcContexts.CuTransquantBypass, cu.Bypass ? 1 : 0);
        }

        SkipFlag(cu.X, cu.Y, cu.Skip);
        if (cu.Skip)
        {
            MergeIndex(cu.Units[0].MergeIdx);
            return;
        }

        Bins.Encode(HevcContexts.PredModeFlag, 0);
        InterPartMode(cu);
        for (int i = 0; i < cu.UnitCount; i++)
        {
            PredictionUnit(cu.Units[i]);
        }

        if (!(cu.PartMode == HevcCodingUnit.Part2Nx2N && cu.Units[0].Merge))
        {
            Bins.Encode(HevcContexts.RqtRootCbf, cu.RootCbf ? 1 : 0);
        }
        else
        {
            Debug.Assert(cu.RootCbf, "A merged 2Nx2N unit without a residual is skipped.");
        }

        if (cu.RootCbf)
        {
            InterTransformTree(cu, cu.Tree, cu.X, cu.Y, cu.Log2, 0, 0, true, true, true, true);
        }
    }

    /// <summary>cu_skip_flag, its context from the skip flags of the units to the left and above.</summary>
    internal void SkipFlag(int x0, int y0, bool skip)
    {
        int ctxInc = 0;
        if (_picture.Available(x0, y0, x0 - 1, y0) && _picture.SkipFlags[((y0 >> 2) * _picture.Width4) + ((x0 - 1) >> 2)] != 0)
        {
            ctxInc++;
        }

        if (_picture.Available(x0, y0, x0, y0 - 1) && _picture.SkipFlags[(((y0 - 1) >> 2) * _picture.Width4) + (x0 >> 2)] != 0)
        {
            ctxInc++;
        }

        Bins.Encode(HevcContexts.CuSkipFlag + ctxInc, skip ? 1 : 0);
    }

    /// <summary>part_mode of an inter unit (Table 9-43): 2Nx2N, 2NxN or Nx2N, without asymmetric partitions.</summary>
    private void InterPartMode(HevcCodingUnit cu)
    {
        Bins.Encode(HevcContexts.PartMode, cu.PartMode == HevcCodingUnit.Part2Nx2N ? 1 : 0);
        if (cu.PartMode == HevcCodingUnit.Part2Nx2N)
        {
            return;
        }

        Bins.Encode(HevcContexts.PartMode + 1, cu.PartMode == HevcCodingUnit.Part2NxN ? 1 : 0);
        if (cu.Log2 == _sps.MinCbLog2Size && cu.Log2 > 3)
        {
            // An Nx2N unit of the smallest size above 8x8 says it is not NxN.
            if (cu.PartMode == HevcCodingUnit.PartNx2N)
            {
                Bins.Encode(HevcContexts.PartMode + 2, 1);
            }
        }
        else if (cu.Log2 > _sps.MinCbLog2Size && _sps.AmpEnabled)
        {
            Bins.Encode(HevcContexts.PartMode + 3, 1);
        }
    }

    /// <summary>prediction_unit (7.3.8.6) of a P slice: merge_flag and merge_idx, or ref_idx_l0, mvd_coding and mvp_l0_flag.</summary>
    private void PredictionUnit(in HevcPredictionUnit pu)
    {
        Bins.Encode(HevcContexts.MergeFlag, pu.Merge ? 1 : 0);
        if (pu.Merge)
        {
            MergeIndex(pu.MergeIdx);
            return;
        }

        Debug.Assert(_slice.SliceType == HevcSliceHeader.P, "B slices come with PR C.");
        if (_slice.NumRefIdxL0 > 1)
        {
            RefIdx(pu.RefIdx0, _slice.NumRefIdxL0 - 1);
        }

        MvdCoding(pu.Mvd0);
        Bins.Encode(HevcContexts.MvpFlag, pu.MvpFlag0);
    }

    /// <summary>merge_idx: truncated rice with cMax MaxNumMergeCand - 1, the first bin context-coded, the rest bypass.</summary>
    internal void MergeIndex(int mergeIdx)
    {
        int cMax = _slice.MaxNumMergeCand - 1;
        if (cMax == 0)
        {
            return;
        }

        Bins.Encode(HevcContexts.MergeIdx, mergeIdx > 0 ? 1 : 0);
        for (int i = 1; i < cMax && i <= mergeIdx; i++)
        {
            Bins.Bypass(mergeIdx > i ? 1 : 0);
        }
    }

    /// <summary>ref_idx_lX: truncated rice with cMax num_ref_idx_active_minus1, two context-coded bins then bypass ones.</summary>
    internal void RefIdx(int refIdx, int cMax)
    {
        for (int i = 0; i < cMax && i <= refIdx; i++)
        {
            int bin = refIdx > i ? 1 : 0;
            if (i < 2)
            {
                Bins.Encode(HevcContexts.RefIdx + i, bin);
            }
            else
            {
                Bins.Bypass(bin);
            }
        }
    }

    /// <summary>mvd_coding (7.3.8.9) of a packed difference.</summary>
    internal void MvdCoding(int mvd)
    {
        int x = (short)mvd, y = mvd >> 16;
        int ax = Math.Abs(x), ay = Math.Abs(y);
        Bins.Encode(HevcContexts.AbsMvdGreater0, ax > 0 ? 1 : 0);
        Bins.Encode(HevcContexts.AbsMvdGreater0, ay > 0 ? 1 : 0);
        if (ax > 0)
        {
            Bins.Encode(HevcContexts.AbsMvdGreater1, ax > 1 ? 1 : 0);
        }

        if (ay > 0)
        {
            Bins.Encode(HevcContexts.AbsMvdGreater1, ay > 1 ? 1 : 0);
        }

        MvdComponent(x, ax);
        MvdComponent(y, ay);
    }

    private void MvdComponent(int value, int abs)
    {
        if (abs == 0)
        {
            return;
        }

        if (abs > 1)
        {
            ExpGolombBypass((uint)(abs - 2), 1);
        }

        Bins.Bypass(value < 0 ? 1 : 0);
    }

    /// <summary>
    /// Writes an inter unit's transform tree (7.3.8.8): split_transform_flag up to max_transform_hierarchy_depth_inter (a split
    /// inferred for a partitioned unit when that is 0), the chroma flags, and cbf_luma, inferred 1 at the root without chroma.
    /// </summary>
    internal void InterTransformTree(HevcCodingUnit cu, HevcTransformNode node, int x0, int y0, int log2TrafoSize, int trafoDepth, int blkIdx,
        bool parentCb0, bool parentCb1, bool parentCr0, bool parentCr1)
    {
        _cu = cu;
        bool split = node.Split;
        int maxDepth = _sps.MaxTransformHierarchyDepthInter;
        bool interSplit = maxDepth == 0 && cu.PartMode != HevcCodingUnit.Part2Nx2N && trafoDepth == 0;
        if (log2TrafoSize <= _sps.MaxTbLog2Size && log2TrafoSize > _sps.MinTbLog2Size && trafoDepth < maxDepth && !interSplit)
        {
            Bins.Encode(HevcContexts.SplitTransform + 5 - log2TrafoSize, split ? 1 : 0);
        }
        else
        {
            Debug.Assert(split == (log2TrafoSize > _sps.MaxTbLog2Size || interSplit), "An inferred split_transform_flag is kept.");
        }

        int chroma = _sps.ChromaArrayType;
        if ((log2TrafoSize > 2 && chroma != 0) || chroma == 3)
        {
            if (trafoDepth == 0 || parentCb0 || parentCb1)
            {
                Cbf(trafoDepth, node.Cb0);
                if (chroma == 2 && (!split || log2TrafoSize == 3))
                {
                    Cbf(trafoDepth, node.Cb1);
                }
            }

            if (trafoDepth == 0 || parentCr0 || parentCr1)
            {
                Cbf(trafoDepth, node.Cr0);
                if (chroma == 2 && (!split || log2TrafoSize == 3))
                {
                    Cbf(trafoDepth, node.Cr1);
                }
            }
        }

        if (split)
        {
            int half = 1 << (log2TrafoSize - 1);
            for (int i = 0; i < 4; i++)
            {
                InterTransformTree(cu, node.Children![i], x0 + ((i & 1) * half), y0 + ((i >> 1) * half), log2TrafoSize - 1, trafoDepth + 1, i,
                    node.Cb0, node.Cb1, node.Cr0, node.Cr1);
            }

            return;
        }

        if (trafoDepth != 0 || node.Cb0 || node.Cb1 || node.Cr0 || node.Cr1)
        {
            Bins.Encode(HevcContexts.CbfLuma + (trafoDepth == 0 ? 1 : 0), node.CbfLuma ? 1 : 0);
        }
        else
        {
            Debug.Assert(node.CbfLuma, "cbf_luma of a whole inter tree without chroma is inferred 1.");
        }

        if (!FlagsOnly)
        {
            TransformUnit(cu, node, x0, y0, log2TrafoSize, blkIdx);
        }
    }
}
