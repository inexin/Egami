using Egami.Nal;

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

    /// <summary>Gets no_output_of_prior_pics_flag (IRAP pictures).</summary>
    internal bool NoOutputOfPriorPics { get; private set; }

    /// <summary>Gets pic_output_flag (1 when the PPS does not send it).</summary>
    internal bool PicOutput { get; private set; } = true;

    /// <summary>Gets slice_pic_order_cnt_lsb (0 for an IDR picture).</summary>
    internal int PocLsb { get; private set; }

    /// <summary>Gets the short-term reference picture set the slice uses (empty for an IDR picture).</summary>
    internal HevcShortTermRps ShortTermRps { get; private set; } = HevcShortTermRps.Empty;

    /// <summary>Gets the long-term pictures of the slice (7.4.7.1): PocLsbLt, UsedByCurrPicLt, and DeltaPocMsbCycleLt or -1 when not sent.</summary>
    internal (int PocLsb, bool Used, int MsbCycle)[] LongTerm { get; private set; } = [];

    /// <summary>Gets slice_temporal_mvp_enabled_flag.</summary>
    internal bool TemporalMvp { get; private set; }

    /// <summary>Gets num_ref_idx_l0_active_minus1 + 1 (0 for an I slice).</summary>
    internal int NumRefIdxL0 { get; private set; }

    /// <summary>Gets num_ref_idx_l1_active_minus1 + 1 (0 unless a B slice).</summary>
    internal int NumRefIdxL1 { get; private set; }

    /// <summary>Gets list_entry_l0, or null without ref_pic_list_modification_flag_l0.</summary>
    internal int[]? ListEntryL0 { get; private set; }

    /// <summary>Gets list_entry_l1, or null without ref_pic_list_modification_flag_l1.</summary>
    internal int[]? ListEntryL1 { get; private set; }

    /// <summary>Gets NumPicTotalCurr (7-55).</summary>
    internal int NumPicTotalCurr { get; private set; }

    /// <summary>Gets mvd_l1_zero_flag.</summary>
    internal bool MvdL1Zero { get; private set; }

    /// <summary>Gets collocated_from_l0_flag (1 when not sent).</summary>
    internal bool CollocatedFromL0 { get; private set; } = true;

    /// <summary>Gets collocated_ref_idx.</summary>
    internal int CollocatedRefIdx { get; private set; }

    /// <summary>Gets the explicit weights (pred_weight_table), or null.</summary>
    internal HevcPredWeights? Weights { get; private set; }

    /// <summary>Gets MaxNumMergeCand (5 - five_minus_max_num_merge_cand).</summary>
    internal int MaxNumMergeCand { get; private set; }

    /// <summary>
    /// Reads a slice segment header. For a dependent slice segment, the fields of the slice it continues come from
    /// <paramref name="independent"/>, the previous independent slice segment's header.
    /// </summary>
    internal static HevcSliceHeader Read(NalBitReader r, HevcNal nal, Func<int, (HevcSps Sps, HevcPps Pps)> parameterSets, HevcSliceHeader? independent)
    {
        HevcSliceHeader h = new() { FirstSliceSegmentInPic = r.Flag("first_slice_segment_in_pic_flag") };
        if (nal.IsIrap)
        {
            h.NoOutputOfPriorPics = r.Flag("no_output_of_prior_pics_flag");
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
        PicOutput = s.PicOutput;
        PocLsb = s.PocLsb;
        ShortTermRps = s.ShortTermRps;
        LongTerm = s.LongTerm;
        TemporalMvp = s.TemporalMvp;
        NumRefIdxL0 = s.NumRefIdxL0;
        NumRefIdxL1 = s.NumRefIdxL1;
        ListEntryL0 = s.ListEntryL0;
        ListEntryL1 = s.ListEntryL1;
        NumPicTotalCurr = s.NumPicTotalCurr;
        MvdL1Zero = s.MvdL1Zero;
        CollocatedFromL0 = s.CollocatedFromL0;
        CollocatedRefIdx = s.CollocatedRefIdx;
        Weights = s.Weights;
        MaxNumMergeCand = s.MaxNumMergeCand;
    }

    private void ReadSliceFields(NalBitReader r, HevcNal nal, HevcSps sps, HevcPps pps)
    {
        for (int i = 0; i < pps.NumExtraSliceHeaderBits; i++)
        {
            r.Flag("slice_reserved_flag[]");
        }

        SliceType = (int)r.Ue("slice_type", 2);
        if (pps.OutputFlagPresent)
        {
            PicOutput = r.Flag("pic_output_flag");
        }

        bool temporalMvp = false;
        int numPicTotalCurr = 0;
        if (!nal.IsIdr)
        {
            PocLsb = (int)r.U(sps.Log2MaxPocLsb, "slice_pic_order_cnt_lsb");
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

            ShortTermRps = rps;
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

                LongTerm = new (int, bool, int)[numLtSps + numLtPics];
                int cycleSum = 0;
                for (int i = 0; i < numLtSps + numLtPics; i++)
                {
                    bool used;
                    int pocLsb;
                    if (i < numLtSps)
                    {
                        int index = ltBits > 0 ? (int)r.U(ltBits, "lt_idx_sps[i]") : 0;
                        if (index >= sps.NumLongTermRefPicsSps)
                        {
                            throw HevcErrors.Corrupt($"lt_idx_sps is {index}.");
                        }

                        used = sps.UsedByCurrPicLtSps[index];
                        pocLsb = sps.LtRefPicPocLsbSps[index];
                    }
                    else
                    {
                        pocLsb = (int)r.U(sps.Log2MaxPocLsb, "poc_lsb_lt");
                        used = r.Flag("used_by_curr_pic_lt_flag");
                    }

                    // 7-52: DeltaPocMsbCycleLt adds up from the first SPS entry and from the first slice entry, absent ones as 0.
                    bool msb = r.Flag("delta_poc_msb_present_flag");
                    int delta = msb ? (int)r.Ue("delta_poc_msb_cycle_lt[i]", 1u << 24) : 0;
                    cycleSum = i == 0 || i == numLtSps ? delta : cycleSum + delta;
                    LongTerm[i] = (pocLsb, used, msb ? cycleSum : -1);
                    numPicTotalCurr += used ? 1 : 0;
                }
            }

            if (sps.TemporalMvpEnabled)
            {
                temporalMvp = r.Flag("slice_temporal_mvp_enabled_flag");
            }
        }

        TemporalMvp = temporalMvp;
        NumPicTotalCurr = numPicTotalCurr;

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
                    ListEntryL0 = new int[numRefIdxL0];
                    for (int i = 0; i < numRefIdxL0; i++)
                    {
                        ListEntryL0[i] = (int)r.U(bits, "list_entry_l0");
                    }
                }

                if (SliceType == B && r.Flag("ref_pic_list_modification_flag_l1"))
                {
                    ListEntryL1 = new int[numRefIdxL1];
                    for (int i = 0; i < numRefIdxL1; i++)
                    {
                        ListEntryL1[i] = (int)r.U(bits, "list_entry_l1");
                    }
                }
            }

            if (SliceType == B)
            {
                MvdL1Zero = r.Flag("mvd_l1_zero_flag");
            }

            if (pps.CabacInitPresent)
            {
                CabacInit = r.Flag("cabac_init_flag");
            }

            if (temporalMvp)
            {
                bool fromL0 = SliceType != B || r.Flag("collocated_from_l0_flag");
                CollocatedFromL0 = fromL0;
                if ((fromL0 && numRefIdxL0 > 1) || (!fromL0 && numRefIdxL1 > 1))
                {
                    CollocatedRefIdx = (int)r.Ue("collocated_ref_idx", (uint)((fromL0 ? numRefIdxL0 : numRefIdxL1) - 1));
                }
            }

            if ((pps.WeightedPred && SliceType == P) || (pps.WeightedBipred && SliceType == B))
            {
                Weights = HevcPredWeights.Read(r, sps, numRefIdxL0, SliceType == B ? numRefIdxL1 : 0);
            }

            MaxNumMergeCand = 5 - (int)r.Ue("five_minus_max_num_merge_cand", 4);
        }

        NumRefIdxL0 = numRefIdxL0;
        NumRefIdxL1 = numRefIdxL1;

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
}

/// <summary>pred_weight_table() (H.265 7.3.6.3): per list and reference index, the weights and offsets of luma, Cb and Cr.</summary>
internal sealed class HevcPredWeights
{
    /// <summary>Gets luma_log2_weight_denom.</summary>
    internal int LumaLog2Denom { get; private set; }

    /// <summary>Gets ChromaLog2WeightDenom.</summary>
    internal int ChromaLog2Denom { get; private set; }

    /// <summary>Gets, per list, per reference index, LumaWeight, luma offset, then the weight and offset of Cb and of Cr (7-56 to 7-58).</summary>
    internal int[][] Values { get; } = [[], []];

    /// <summary>Reads the table; offsets are scaled to the bit depth unless high_precision_offsets_enabled_flag is set.</summary>
    internal static HevcPredWeights Read(NalBitReader r, HevcSps sps, int numL0, int numL1)
    {
        HevcPredWeights w = new() { LumaLog2Denom = (int)r.Ue("luma_log2_weight_denom", 7) };
        bool chroma = sps.ChromaArrayType != 0;
        w.ChromaLog2Denom = w.LumaLog2Denom;
        if (chroma)
        {
            w.ChromaLog2Denom += r.Se("delta_chroma_log2_weight_denom", -w.LumaLog2Denom, 7 - w.LumaLog2Denom);
        }

        int wpOffsetBdShiftY = sps.HighPrecisionOffsets ? 0 : sps.BitDepthLuma - 8;
        int wpOffsetBdShiftC = sps.HighPrecisionOffsets ? 0 : sps.BitDepthChroma - 8;
        int halfRangeC = sps.HighPrecisionOffsets ? 1 << (sps.BitDepthChroma - 1) : 128;
        int halfRangeY = sps.HighPrecisionOffsets ? 1 << (sps.BitDepthLuma - 1) : 128;
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

            int[] values = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                int o = i * 6;
                values[o] = 1 << w.LumaLog2Denom;
                values[o + 2] = values[o + 4] = 1 << w.ChromaLog2Denom;
                if (luma[i])
                {
                    values[o] += r.Se($"delta_luma_weight_{l}[i]", -128, 127);
                    values[o + 1] = r.Se($"luma_offset_{l}[i]", -halfRangeY, halfRangeY - 1) << wpOffsetBdShiftY;
                }

                if (chromaFlags[i])
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int weight = (1 << w.ChromaLog2Denom) + r.Se($"delta_chroma_weight_{l}[i]", -128, 127);
                        int delta = r.Se($"delta_chroma_offset_{l}[i]", -4 * halfRangeC, (4 * halfRangeC) - 1);

                        // 7-56: the offset is predicted from the weight.
                        int offset = Math.Clamp(delta - ((halfRangeC * weight) >> w.ChromaLog2Denom) + halfRangeC, -halfRangeC, halfRangeC - 1);
                        values[o + 2 + (2 * j)] = weight;
                        values[o + 3 + (2 * j)] = offset << wpOffsetBdShiftC;
                    }
                }
            }

            w.Values[list] = values;
        }

        return w;
    }
}
