namespace Egami.Heic.Hevc;

/// <summary>A slice segment header (H.265 7.3.6.1) and where the slice data starts.</summary>
internal sealed class HevcSliceHeader
{
    internal const int B = 0;
    internal const int P = 1;
    internal const int I = 2;

    /// <summary>Gets first_slice_segment_in_pic_flag.</summary>
    internal bool FirstSliceSegmentInPic { get; private set; }

    /// <summary>Gets slice_pic_parameter_set_id.</summary>
    internal int PpsId { get; private set; }

    /// <summary>Gets dependent_slice_segment_flag.</summary>
    internal bool Dependent { get; private set; }

    /// <summary>Gets slice_segment_address (in raster scan).</summary>
    internal int SegmentAddress { get; private set; }

    /// <summary>Gets slice_type: 0 B, 1 P, 2 I.</summary>
    internal int SliceType { get; private set; }

    /// <summary>Gets slice_sao_luma_flag.</summary>
    internal bool SaoLuma { get; private set; }

    /// <summary>Gets slice_sao_chroma_flag.</summary>
    internal bool SaoChroma { get; private set; }

    /// <summary>Gets SliceQpY.</summary>
    internal int SliceQp { get; private set; }

    /// <summary>Gets slice_cb_qp_offset.</summary>
    internal int CbQpOffset { get; private set; }

    /// <summary>Gets slice_cr_qp_offset.</summary>
    internal int CrQpOffset { get; private set; }

    /// <summary>Gets cu_chroma_qp_offset_enabled_flag.</summary>
    internal bool CuChromaQpOffsetEnabled { get; private set; }

    /// <summary>Gets slice_deblocking_filter_disabled_flag (the PPS's when not overridden).</summary>
    internal bool DeblockingDisabled { get; private set; }

    /// <summary>Gets slice_beta_offset_div2.</summary>
    internal int BetaOffsetDiv2 { get; private set; }

    /// <summary>Gets slice_tc_offset_div2.</summary>
    internal int TcOffsetDiv2 { get; private set; }

    /// <summary>Gets slice_loop_filter_across_slices_enabled_flag.</summary>
    internal bool LoopFilterAcrossSlices { get; private set; }

    /// <summary>Gets cabac_init_flag.</summary>
    internal bool CabacInit { get; private set; }

    /// <summary>Gets the entry point offsets, in bytes of the escaped slice data (entry_point_offset_minus1 + 1).</summary>
    internal int[] EntryPoints { get; private set; } = [];

    /// <summary>Gets the byte in the RBSP where the slice data starts.</summary>
    internal int DataStart { get; private set; }

    /// <summary>
    /// Reads a slice segment header. For a dependent slice segment, the fields of the slice it continues come from
    /// <paramref name="independent"/>, the previous independent slice segment's header.
    /// </summary>
    internal static HevcSliceHeader Read(HevcBitReader r, HevcNal nal, Func<int, (HevcSps Sps, HevcPps Pps)> parameterSets, HevcSliceHeader? independent)
    {
        HevcSliceHeader h = new() { FirstSliceSegmentInPic = r.Flag("first_slice_segment_in_pic_flag") };
        if (nal.IsIrap)
        {
            r.Flag("no_output_of_prior_pics_flag");
        }

        h.PpsId = (int)r.Ue("slice_pic_parameter_set_id", 63);
        (HevcSps sps, HevcPps pps) = parameterSets(h.PpsId);
        if (!h.FirstSliceSegmentInPic)
        {
            if (pps.DependentSliceSegmentsEnabled)
            {
                h.Dependent = r.Flag("dependent_slice_segment_flag");
            }

            int ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
            int bits = 0;
            while ((1 << bits) < ctbs)
            {
                bits++;
            }

            h.SegmentAddress = (int)r.U(bits, "slice_segment_address");
            if (h.SegmentAddress >= ctbs)
            {
                throw HevcErrors.Corrupt($"slice_segment_address is {h.SegmentAddress}, past the picture's {ctbs} coding tree blocks.");
            }
        }

        if (h.Dependent)
        {
            if (independent is null)
            {
                throw HevcErrors.Corrupt("A dependent slice segment comes first.");
            }

            h.CopySliceFields(independent);
        }
        else
        {
            h.ReadSliceFields(r, nal, sps, pps);
        }

        if (pps.TilesEnabled || pps.EntropyCodingSync)
        {
            int count = (int)r.Ue("num_entry_point_offsets", (uint)(sps.WidthInCtbs * sps.HeightInCtbs));
            if (count > 0)
            {
                int length = (int)r.Ue("offset_len_minus1", 31) + 1;
                h.EntryPoints = new int[count];
                for (int i = 0; i < count; i++)
                {
                    h.EntryPoints[i] = (int)r.U(length, "entry_point_offset_minus1") + 1;
                }
            }
        }

        if (pps.SliceHeaderExtensionPresent)
        {
            int length = (int)r.Ue("slice_segment_header_extension_length", 256);
            for (int i = 0; i < length; i++)
            {
                r.U(8, "slice_segment_header_extension_data_byte");
            }
        }

        r.ByteAlignment();
        h.DataStart = r.BytePosition;
        return h;
    }

    private void CopySliceFields(HevcSliceHeader s)
    {
        SliceType = s.SliceType;
        SaoLuma = s.SaoLuma;
        SaoChroma = s.SaoChroma;
        SliceQp = s.SliceQp;
        CbQpOffset = s.CbQpOffset;
        CrQpOffset = s.CrQpOffset;
        CuChromaQpOffsetEnabled = s.CuChromaQpOffsetEnabled;
        DeblockingDisabled = s.DeblockingDisabled;
        BetaOffsetDiv2 = s.BetaOffsetDiv2;
        TcOffsetDiv2 = s.TcOffsetDiv2;
        LoopFilterAcrossSlices = s.LoopFilterAcrossSlices;
        CabacInit = s.CabacInit;
    }

    private void ReadSliceFields(HevcBitReader r, HevcNal nal, HevcSps sps, HevcPps pps)
    {
        for (int i = 0; i < pps.NumExtraSliceHeaderBits; i++)
        {
            r.Flag("slice_reserved_flag[]");
        }

        SliceType = (int)r.Ue("slice_type", 2);
        if (pps.OutputFlagPresent)
        {
            r.Flag("pic_output_flag");
        }

        bool temporalMvp = false;
        int numPicTotalCurr = 0;
        if (!nal.IsIdr)
        {
            r.U(sps.Log2MaxPocLsb, "slice_pic_order_cnt_lsb");
            HevcShortTermRps rps;
            if (!r.Flag("short_term_ref_pic_set_sps_flag"))
            {
                rps = HevcShortTermRps.Read(r, sps.ShortTermRps.Count, sps.ShortTermRps, sps.ShortTermRps.Count);
            }
            else
            {
                if (sps.ShortTermRps.Count == 0)
                {
                    throw HevcErrors.Corrupt("A slice refers to the SPS's reference picture sets, but it has none.");
                }

                int bits = 0;
                while ((1 << bits) < sps.ShortTermRps.Count)
                {
                    bits++;
                }

                int index = bits > 0 ? (int)r.U(bits, "short_term_ref_pic_set_idx") : 0;
                rps = index < sps.ShortTermRps.Count ? sps.ShortTermRps[index] : throw HevcErrors.Corrupt($"short_term_ref_pic_set_idx is {index}.");
            }

            numPicTotalCurr = rps.NumUsed;
            if (sps.LongTermRefsPresent)
            {
                int numLtSps = sps.NumLongTermRefPicsSps > 0 ? (int)r.Ue("num_long_term_sps", (uint)sps.NumLongTermRefPicsSps) : 0;
                int numLtPics = (int)r.Ue("num_long_term_pics", 32);
                int ltBits = 0;
                while (sps.NumLongTermRefPicsSps > (1 << ltBits))
                {
                    ltBits++;
                }

                for (int i = 0; i < numLtSps + numLtPics; i++)
                {
                    bool used;
                    if (i < numLtSps)
                    {
                        int index = ltBits > 0 ? (int)r.U(ltBits, "lt_idx_sps[i]") : 0;
                        used = index < sps.UsedByCurrPicLtSps.Length && sps.UsedByCurrPicLtSps[index];
                    }
                    else
                    {
                        r.U(sps.Log2MaxPocLsb, "poc_lsb_lt");
                        used = r.Flag("used_by_curr_pic_lt_flag");
                    }

                    if (r.Flag("delta_poc_msb_present_flag"))
                    {
                        r.Ue("delta_poc_msb_cycle_lt[i]");
                    }

                    numPicTotalCurr += used ? 1 : 0;
                }
            }

            if (sps.TemporalMvpEnabled)
            {
                temporalMvp = r.Flag("slice_temporal_mvp_enabled_flag");
            }
        }

        if (sps.SaoEnabled)
        {
            SaoLuma = r.Flag("slice_sao_luma_flag");
            if (sps.ChromaArrayType != 0)
            {
                SaoChroma = r.Flag("slice_sao_chroma_flag");
            }
        }

        int numRefIdxL0 = 0, numRefIdxL1 = 0;
        if (SliceType != I)
        {
            numRefIdxL0 = pps.NumRefIdxL0Default;
            numRefIdxL1 = SliceType == B ? pps.NumRefIdxL1Default : 0;
            if (r.Flag("num_ref_idx_active_override_flag"))
            {
                numRefIdxL0 = (int)r.Ue("num_ref_idx_l0_active_minus1", 14) + 1;
                if (SliceType == B)
                {
                    numRefIdxL1 = (int)r.Ue("num_ref_idx_l1_active_minus1", 14) + 1;
                }
            }

            if (pps.ListsModificationPresent && numPicTotalCurr > 1)
            {
                int bits = 0;
                while ((1 << bits) < numPicTotalCurr)
                {
                    bits++;
                }

                if (r.Flag("ref_pic_list_modification_flag_l0"))
                {
                    for (int i = 0; i < numRefIdxL0; i++)
                    {
                        r.U(bits, "list_entry_l0");
                    }
                }

                if (SliceType == B && r.Flag("ref_pic_list_modification_flag_l1"))
                {
                    for (int i = 0; i < numRefIdxL1; i++)
                    {
                        r.U(bits, "list_entry_l1");
                    }
                }
            }

            if (SliceType == B)
            {
                r.Flag("mvd_l1_zero_flag");
            }

            if (pps.CabacInitPresent)
            {
                CabacInit = r.Flag("cabac_init_flag");
            }

            if (temporalMvp)
            {
                bool fromL0 = SliceType != B || r.Flag("collocated_from_l0_flag");
                if ((fromL0 && numRefIdxL0 > 1) || (!fromL0 && numRefIdxL1 > 1))
                {
                    r.Ue("collocated_ref_idx", 14);
                }
            }

            if ((pps.WeightedPred && SliceType == P) || (pps.WeightedBipred && SliceType == B))
            {
                ReadPredWeightTable(r, sps, numRefIdxL0, SliceType == B ? numRefIdxL1 : 0);
            }

            r.Ue("five_minus_max_num_merge_cand", 4);
        }

        SliceQp = 26 + pps.InitQpMinus26 + r.Se("slice_qp_delta", -128, 127);
        if (SliceQp < -sps.QpBdOffsetLuma || SliceQp > 51)
        {
            throw HevcErrors.Corrupt($"SliceQpY is {SliceQp}.");
        }

        if (pps.SliceChromaQpOffsetsPresent)
        {
            CbQpOffset = r.Se("slice_cb_qp_offset", -12, 12);
            CrQpOffset = r.Se("slice_cr_qp_offset", -12, 12);
        }

        if (pps.ChromaQpOffsetListEnabled)
        {
            CuChromaQpOffsetEnabled = r.Flag("cu_chroma_qp_offset_enabled_flag");
        }

        bool overridden = pps.DeblockingOverrideEnabled && r.Flag("deblocking_filter_override_flag");
        if (overridden)
        {
            DeblockingDisabled = r.Flag("slice_deblocking_filter_disabled_flag");
            if (!DeblockingDisabled)
            {
                BetaOffsetDiv2 = r.Se("slice_beta_offset_div2", -6, 6);
                TcOffsetDiv2 = r.Se("slice_tc_offset_div2", -6, 6);
            }
        }
        else
        {
            DeblockingDisabled = pps.DeblockingDisabled;
            BetaOffsetDiv2 = pps.BetaOffsetDiv2;
            TcOffsetDiv2 = pps.TcOffsetDiv2;
        }

        LoopFilterAcrossSlices = pps.LoopFilterAcrossSlices;
        if (pps.LoopFilterAcrossSlices && (SaoLuma || SaoChroma || !DeblockingDisabled))
        {
            LoopFilterAcrossSlices = r.Flag("slice_loop_filter_across_slices_enabled_flag");
        }
    }

    /// <summary>Reads pred_weight_table() (H.265 7.3.6.3), which intra decoding does not use.</summary>
    private static void ReadPredWeightTable(HevcBitReader r, HevcSps sps, int numL0, int numL1)
    {
        r.Ue("luma_log2_weight_denom", 7);
        bool chroma = sps.ChromaArrayType != 0;
        if (chroma)
        {
            r.Se("delta_chroma_log2_weight_denom");
        }

        for (int list = 0; list < (numL1 > 0 ? 2 : 1); list++)
        {
            int count = list == 0 ? numL0 : numL1;
            string l = list == 0 ? "l0" : "l1";
            bool[] luma = new bool[count], chromaFlags = new bool[count];
            for (int i = 0; i < count; i++)
            {
                luma[i] = r.Flag($"luma_weight_{l}_flag[i]");
            }

            if (chroma)
            {
                for (int i = 0; i < count; i++)
                {
                    chromaFlags[i] = r.Flag($"chroma_weight_{l}_flag[i]");
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (luma[i])
                {
                    r.Se($"delta_luma_weight_{l}[i]");
                    r.Se($"luma_offset_{l}[i]");
                }

                if (chromaFlags[i])
                {
                    for (int j = 0; j < 2; j++)
                    {
                        r.Se($"delta_chroma_weight_{l}[i]");
                        r.Se($"delta_chroma_offset_{l}[i]");
                    }
                }
            }
        }
    }
}
