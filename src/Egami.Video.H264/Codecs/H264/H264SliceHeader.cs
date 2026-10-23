using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>The slice types (H.264 Table 7-6), slice_type % 5.</summary>
internal enum H264SliceType
{
    P = 0,
    B = 1,
    I = 2,
    SP = 3,
    SI = 4,
}

/// <summary>One memory management control operation (H.264 7.4.3.3).</summary>
internal readonly record struct H264Mmco(int Op, int DifferenceOfPicNums, int LongTermPicNum, int LongTermFrameIdx, int MaxLongTermFrameIdxPlus1);

/// <summary>One reference list modification (H.264 7.4.3.1): modification_of_pic_nums_idc and its value.</summary>
internal readonly record struct H264ListModification(int Idc, int Value);

/// <summary>A slice header (H.264 7.3.3), for frame pictures.</summary>
internal sealed class H264SliceHeader
{
    internal int NalRefIdc { get; private set; }

    internal int NalUnitType { get; private set; }

    internal bool Idr => NalUnitType == 5;

    internal int FirstMb { get; private set; }

    internal H264SliceType Type { get; private set; }

    internal H264Pps Pps { get; private set; } = null!;

    internal H264Sps Sps => Pps.Sps;

    internal int FrameNum { get; private set; }

    internal int IdrPicId { get; private set; }

    internal int PocLsb { get; private set; }

    internal int DeltaPocBottom { get; private set; }

    internal int DeltaPoc0 { get; private set; }

    internal int DeltaPoc1 { get; private set; }

    internal int RedundantPicCnt { get; private set; }

    internal bool DirectSpatialMvPred { get; private set; }

    internal int NumRefIdxL0 { get; private set; }

    internal int NumRefIdxL1 { get; private set; }

    internal H264ListModification[] ModificationsL0 { get; private set; } = [];

    internal H264ListModification[] ModificationsL1 { get; private set; } = [];

    /// <summary>Gets whether a pred_weight_table is present (explicit weighted prediction).</summary>
    internal bool ExplicitWeights { get; private set; }

    internal int LumaLog2WeightDenom { get; private set; }

    internal int ChromaLog2WeightDenom { get; private set; }

    /// <summary>Gets the explicit weights: [list][refIdx * 6 + 0 luma weight, 1 luma offset, 2 Cb weight, 3 Cb offset, 4 Cr weight, 5 Cr offset].</summary>
    internal int[][] Weights { get; } = [new int[32 * 6], new int[32 * 6]];

    internal bool NoOutputOfPriorPics { get; private set; }

    internal bool LongTermReference { get; private set; }

    internal bool AdaptiveRefPicMarking { get; private set; }

    internal H264Mmco[] Mmcos { get; private set; } = [];

    internal int CabacInitIdc { get; private set; }

    internal int SliceQp { get; private set; }

    internal int DisableDeblockingFilterIdc { get; private set; }

    internal int FilterOffsetA { get; private set; }

    internal int FilterOffsetB { get; private set; }

    /// <summary>Gets the bit position of slice_data() in the RBSP.</summary>
    internal long DataBitPosition { get; private set; }

    internal bool IsI => Type == H264SliceType.I;

    internal bool IsB => Type == H264SliceType.B;

    /// <summary>Reads first_mb_in_slice, slice_type and pic_parameter_set_id only, to find the PPS and the picture boundary.</summary>
    internal static (int FirstMb, int SliceType, int PpsId) ReadStart(byte[] rbsp)
    {
        NalBitReader r = new(rbsp, 1);
        int first = (int)r.Ue("first_mb_in_slice");
        int type = (int)r.Ue("slice_type", 9);
        int pps = (int)r.Ue("pic_parameter_set_id", 255);
        return (first, type, pps);
    }

    internal static H264SliceHeader Read(byte[] rbsp, Func<int, H264Pps?> findPps)
    {
        NalBitReader r = new(rbsp, 1);
        H264SliceHeader h = new() { NalRefIdc = (rbsp[0] >> 5) & 3, NalUnitType = rbsp[0] & 31 };
        h.FirstMb = (int)r.Ue("first_mb_in_slice");
        h.Type = (H264SliceType)(r.Ue("slice_type", 9) % 5);
        if (h.Type is H264SliceType.SP or H264SliceType.SI)
        {
            throw H264Errors.Unsupported("The stream has SP or SI slices (Extended profile).");
        }

        int ppsId = (int)r.Ue("pic_parameter_set_id", 255);
        h.Pps = findPps(ppsId) ?? throw H264Errors.Corrupt($"A slice refers to picture parameter set {ppsId}, which the stream has not given.");
        H264Sps sps = h.Sps;
        if (h.FirstMb >= sps.WidthInMbs * sps.HeightInMbs)
        {
            throw H264Errors.Corrupt($"first_mb_in_slice {h.FirstMb} is outside the picture.");
        }

        if (h.Idr && h.Type != H264SliceType.I)
        {
            throw H264Errors.Corrupt("An IDR picture has a P or B slice.");
        }

        h.FrameNum = (int)r.U(sps.Log2MaxFrameNum, "frame_num");
        if (h.Idr)
        {
            h.IdrPicId = (int)r.Ue("idr_pic_id", 65535);
        }

        if (sps.PocType == 0)
        {
            h.PocLsb = (int)r.U(sps.Log2MaxPocLsb, "pic_order_cnt_lsb");
            if (h.Pps.BottomFieldPicOrderInFramePresent)
            {
                h.DeltaPocBottom = r.Se("delta_pic_order_cnt_bottom");
            }
        }
        else if (sps.PocType == 1 && !sps.DeltaPicOrderAlwaysZero)
        {
            h.DeltaPoc0 = r.Se("delta_pic_order_cnt[0]");
            if (h.Pps.BottomFieldPicOrderInFramePresent)
            {
                h.DeltaPoc1 = r.Se("delta_pic_order_cnt[1]");
            }
        }

        if (h.Pps.RedundantPicCntPresent)
        {
            h.RedundantPicCnt = (int)r.Ue("redundant_pic_cnt", 127);
        }

        if (h.IsB)
        {
            h.DirectSpatialMvPred = r.Flag("direct_spatial_mv_pred_flag");
        }

        if (!h.IsI)
        {
            h.NumRefIdxL0 = h.Pps.NumRefIdxL0Default;
            h.NumRefIdxL1 = h.IsB ? h.Pps.NumRefIdxL1Default : 0;
            if (r.Flag("num_ref_idx_active_override_flag"))
            {
                h.NumRefIdxL0 = (int)r.Ue("num_ref_idx_l0_active_minus1", 31) + 1;
                if (h.IsB)
                {
                    h.NumRefIdxL1 = (int)r.Ue("num_ref_idx_l1_active_minus1", 31) + 1;
                }
            }

            // Frames allow 16 references a list (7.4.3).
            if (h.NumRefIdxL0 > 16 || h.NumRefIdxL1 > 16)
            {
                throw H264Errors.Corrupt("A frame slice has more than 16 active references in a list.");
            }

            h.ModificationsL0 = ReadModifications(r);
            if (h.IsB)
            {
                h.ModificationsL1 = ReadModifications(r);
            }
        }

        if ((h.Pps.WeightedPred && h.Type == H264SliceType.P) || (h.Pps.WeightedBipredIdc == 1 && h.IsB))
        {
            h.ReadWeights(r, sps.ChromaArrayType);
        }

        if (h.NalRefIdc != 0)
        {
            h.ReadMarking(r);
        }

        if (h.Pps.Cabac && !h.IsI)
        {
            h.CabacInitIdc = (int)r.Ue("cabac_init_idc", 2);
        }

        int qpBdOffset = 6 * (sps.BitDepthLuma - 8);
        h.SliceQp = h.Pps.PicInitQp + r.Se("slice_qp_delta", -100, 100);
        if (h.SliceQp < -qpBdOffset || h.SliceQp > 51)
        {
            throw H264Errors.Corrupt($"SliceQPY {h.SliceQp} is outside {-qpBdOffset} to 51.");
        }

        if (h.Pps.DeblockingFilterControlPresent)
        {
            h.DisableDeblockingFilterIdc = (int)r.Ue("disable_deblocking_filter_idc", 2);
            if (h.DisableDeblockingFilterIdc != 1)
            {
                h.FilterOffsetA = r.Se("slice_alpha_c0_offset_div2", -6, 6) * 2;
                h.FilterOffsetB = r.Se("slice_beta_offset_div2", -6, 6) * 2;
            }
        }

        h.DataBitPosition = r.BitPosition;
        return h;
    }

    private static H264ListModification[] ReadModifications(NalBitReader r)
    {
        if (!r.Flag("ref_pic_list_modification_flag"))
        {
            return [];
        }

        List<H264ListModification> list = [];
        while (true)
        {
            int idc = (int)r.Ue("modification_of_pic_nums_idc", 5);
            if (idc == 3)
            {
                return [.. list];
            }

            if (idc > 2)
            {
                throw H264Errors.Unsupported("The stream has MVC reference list modifications.");
            }

            list.Add(new H264ListModification(idc, (int)r.Ue(idc == 2 ? "long_term_pic_num" : "abs_diff_pic_num_minus1", 131071)));
            if (list.Count > 32)
            {
                throw H264Errors.Corrupt("A reference list has more than 32 modifications.");
            }
        }
    }

    private void ReadWeights(NalBitReader r, int chromaArrayType)
    {
        ExplicitWeights = true;
        LumaLog2WeightDenom = (int)r.Ue("luma_log2_weight_denom", 7);
        if (chromaArrayType != 0)
        {
            ChromaLog2WeightDenom = (int)r.Ue("chroma_log2_weight_denom", 7);
        }

        for (int list = 0; list < (IsB ? 2 : 1); list++)
        {
            int count = list == 0 ? NumRefIdxL0 : NumRefIdxL1;
            int[] w = Weights[list];
            for (int i = 0; i < count; i++)
            {
                w[(i * 6) + 0] = 1 << LumaLog2WeightDenom;
                w[(i * 6) + 1] = 0;
                if (r.Flag("luma_weight_flag"))
                {
                    w[(i * 6) + 0] = r.Se("luma_weight", -128, 127);
                    w[(i * 6) + 1] = r.Se("luma_offset", -128, 127);
                }

                for (int c = 0; c < 2; c++)
                {
                    w[(i * 6) + 2 + (c * 2)] = 1 << ChromaLog2WeightDenom;
                    w[(i * 6) + 3 + (c * 2)] = 0;
                }

                if (chromaArrayType != 0 && r.Flag("chroma_weight_flag"))
                {
                    for (int c = 0; c < 2; c++)
                    {
                        w[(i * 6) + 2 + (c * 2)] = r.Se("chroma_weight", -128, 127);
                        w[(i * 6) + 3 + (c * 2)] = r.Se("chroma_offset", -128, 127);
                    }
                }
            }
        }
    }

    private void ReadMarking(NalBitReader r)
    {
        if (Idr)
        {
            NoOutputOfPriorPics = r.Flag("no_output_of_prior_pics_flag");
            LongTermReference = r.Flag("long_term_reference_flag");
            return;
        }

        AdaptiveRefPicMarking = r.Flag("adaptive_ref_pic_marking_mode_flag");
        if (!AdaptiveRefPicMarking)
        {
            return;
        }

        List<H264Mmco> ops = [];
        while (true)
        {
            int op = (int)r.Ue("memory_management_control_operation", 6);
            if (op == 0)
            {
                break;
            }

            int diff = 0, longTermPicNum = 0, longTermFrameIdx = 0, maxPlus1 = 0;
            if (op is 1 or 3)
            {
                diff = (int)r.Ue("difference_of_pic_nums_minus1", 131071) + 1;
            }

            if (op == 2)
            {
                longTermPicNum = (int)r.Ue("long_term_pic_num", 131071);
            }

            if (op is 3 or 6)
            {
                longTermFrameIdx = (int)r.Ue("long_term_frame_idx", 15);
            }

            if (op == 4)
            {
                maxPlus1 = (int)r.Ue("max_long_term_frame_idx_plus1", 16);
            }

            ops.Add(new H264Mmco(op, diff, longTermPicNum, longTermFrameIdx, maxPlus1));
            if (ops.Count > 66)
            {
                throw H264Errors.Corrupt("A slice has more than 66 memory management operations.");
            }
        }

        Mmcos = [.. ops];
    }
}
