namespace Egami.Heic.Hevc;

/// <summary>profile_tier_level() (H.265 7.3.3): the general profile, tier and level.</summary>
internal sealed class HevcProfileTierLevel
{
    /// <summary>Gets general_profile_idc: 1 Main, 2 Main 10, 3 Main Still Picture, 4 format range extensions, 5 High Throughput.</summary>
    internal int ProfileIdc { get; private set; }

    /// <summary>Gets the general_profile_compatibility_flag bits, flag j at bit j.</summary>
    internal uint Compatibility { get; private set; }

    /// <summary>Gets general_tier_flag.</summary>
    internal bool Tier { get; private set; }

    /// <summary>Gets general_level_idc.</summary>
    internal int LevelIdc { get; private set; }

    /// <summary>Gets whether the profile, or a compatible one, is <paramref name="idc"/>.</summary>
    internal bool Is(int idc) => ProfileIdc == idc || (Compatibility & (1u << idc)) != 0;

    internal static HevcProfileTierLevel Read(HevcBitReader r, bool profilePresent, int maxSubLayersMinus1)
    {
        HevcProfileTierLevel ptl = new();
        if (profilePresent)
        {
            (ptl.ProfileIdc, ptl.Compatibility, ptl.Tier) = ReadProfileTier(r, "general_");
        }

        ptl.LevelIdc = (int)r.U(8, "general_level_idc");
        bool[] profilePresentFlags = new bool[maxSubLayersMinus1], levelPresent = new bool[maxSubLayersMinus1];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresentFlags[i] = r.Flag("sub_layer_profile_present_flag[i]");
            levelPresent[i] = r.Flag("sub_layer_level_present_flag[i]");
        }

        if (maxSubLayersMinus1 > 0)
        {
            for (int i = maxSubLayersMinus1; i < 8; i++)
            {
                r.U(2, "reserved_zero_2bits");
            }
        }

        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresentFlags[i])
            {
                ReadProfileTier(r, "sub_layer_");
            }

            if (levelPresent[i])
            {
                r.U(8, "sub_layer_level_idc[i]");
            }
        }

        return ptl;
    }

    /// <summary>Reads profile_space through inbld_flag; the constraint flags' layout depends on the profile (H.265 7.3.3).</summary>
    private static (int Idc, uint Compatibility, bool Tier) ReadProfileTier(HevcBitReader r, string prefix)
    {
        r.U(2, prefix + "profile_space");
        bool tier = r.Flag(prefix + "tier_flag");
        int idc = (int)r.U(5, prefix + "profile_idc");
        uint compatibility = 0;
        for (int j = 0; j < 32; j++)
        {
            if (r.Flag(prefix + "profile_compatibility_flag[][j]"))
            {
                compatibility |= 1u << j;
            }
        }

        bool Is(int p) => idc == p || (compatibility & (1u << p)) != 0;
        r.Flag(prefix + "progressive_source_flag");
        r.Flag(prefix + "interlaced_source_flag");
        r.Flag(prefix + "non_packed_constraint_flag");
        r.Flag(prefix + "frame_only_constraint_flag");
        if (Is(4) || Is(5))
        {
            r.Flag(prefix + "max_12bit_constraint_flag");
            r.Flag(prefix + "max_10bit_constraint_flag");
            r.Flag(prefix + "max_8bit_constraint_flag");
            r.Flag(prefix + "max_422chroma_constraint_flag");
            r.Flag(prefix + "max_420chroma_constraint_flag");
            r.Flag(prefix + "max_monochrome_constraint_flag");
            r.Flag(prefix + "intra_constraint_flag");
            r.Flag(prefix + "one_picture_only_constraint_flag");
            r.Flag(prefix + "lower_bit_rate_constraint_flag");
            r.U(16, prefix + "reserved_zero_34bits[0..15]");
            r.U(16, prefix + "reserved_zero_34bits[16..31]");
            r.U(2, prefix + "reserved_zero_34bits[32..33]");
        }
        else if (Is(2))
        {
            r.U(7, prefix + "reserved_zero_7bits");
            r.Flag(prefix + "one_picture_only_constraint_flag");
            r.U(16, prefix + "reserved_zero_35bits[0..15]");
            r.U(16, prefix + "reserved_zero_35bits[16..31]");
            r.U(3, prefix + "reserved_zero_35bits[32..34]");
        }
        else
        {
            r.U(16, prefix + "reserved_zero_43bits[0..15]");
            r.U(16, prefix + "reserved_zero_43bits[16..31]");
            r.U(11, prefix + "reserved_zero_43bits[32..42]");
        }

        r.Flag(idc is >= 1 and <= 5 || Is(1) || Is(2) || Is(3) || Is(4) || Is(5) ? prefix + "inbld_flag" : prefix + "reserved_zero_bit");
        return (idc, compatibility, tier);
    }
}

/// <summary>A video parameter set (H.265 7.3.2.1). Intra decoding needs nothing from it; it is parsed to check the stream.</summary>
internal sealed class HevcVps
{
    /// <summary>Gets vps_video_parameter_set_id.</summary>
    internal int Id { get; private set; }

    internal static HevcVps Read(HevcBitReader r)
    {
        HevcVps vps = new() { Id = (int)r.U(4, "vps_video_parameter_set_id") };
        r.Flag("vps_base_layer_internal_flag");
        r.Flag("vps_base_layer_available_flag");
        r.U(6, "vps_max_layers_minus1");
        int maxSubLayersMinus1 = (int)r.U(3, "vps_max_sub_layers_minus1");
        if (maxSubLayersMinus1 > 6)
        {
            throw HevcErrors.Corrupt($"vps_max_sub_layers_minus1 is {maxSubLayersMinus1}, more than 6.");
        }

        r.Flag("vps_temporal_id_nesting_flag");
        r.U(16, "vps_reserved_0xffff_16bits");
        HevcProfileTierLevel.Read(r, true, maxSubLayersMinus1);
        bool orderingInfo = r.Flag("vps_sub_layer_ordering_info_present_flag");
        for (int i = orderingInfo ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
        {
            r.Ue("vps_max_dec_pic_buffering_minus1[i]");
            r.Ue("vps_max_num_reorder_pics[i]");
            r.Ue("vps_max_latency_increase_plus1[i]");
        }

        int maxLayerId = (int)r.U(6, "vps_max_layer_id");
        uint layerSets = r.Ue("vps_num_layer_sets_minus1", 1023);
        for (int i = 1; i <= layerSets; i++)
        {
            for (int j = 0; j <= maxLayerId; j++)
            {
                r.Flag("layer_id_included_flag[opsIdx][i]");
            }
        }

        if (r.Flag("vps_timing_info_present_flag"))
        {
            r.U(32, "vps_num_units_in_tick");
            r.U(32, "vps_time_scale");
            if (r.Flag("vps_poc_proportional_to_timing_flag"))
            {
                r.Ue("vps_num_ticks_poc_diff_one_minus1");
            }

            uint hrdCount = r.Ue("vps_num_hrd_parameters", 1024);
            for (int i = 0; i < hrdCount; i++)
            {
                r.Ue("hrd_layer_set_idx[i]");
                bool common = i == 0 || r.Flag("cprms_present_flag[i]");
                HevcVui.ReadHrd(r, common, maxSubLayersMinus1);
            }
        }

        if (r.Flag("vps_extension_flag"))
        {
            while (r.MoreRbspData())
            {
                r.Flag("vps_extension_data_flag");
            }
        }

        r.TrailingBits();
        return vps;
    }
}

/// <summary>vui_parameters() (H.265 E.2.1): the colour description and the rest, read past.</summary>
internal sealed class HevcVui
{
    /// <summary>Gets video_full_range_flag (false when absent).</summary>
    internal bool FullRange { get; private set; }

    /// <summary>Gets colour_primaries (2, unspecified, when absent).</summary>
    internal int ColourPrimaries { get; private set; } = 2;

    /// <summary>Gets transfer_characteristics (2 when absent).</summary>
    internal int TransferCharacteristics { get; private set; } = 2;

    /// <summary>Gets matrix_coeffs (2 when absent).</summary>
    internal int MatrixCoefficients { get; private set; } = 2;

    /// <summary>Gets chroma_sample_loc_type_top_field, or -1 when absent.</summary>
    internal int ChromaSampleLocation { get; private set; } = -1;

    internal static HevcVui Read(HevcBitReader r, int subWidthC, int subHeightC, int maxSubLayersMinus1)
    {
        HevcVui vui = new();
        if (r.Flag("aspect_ratio_info_present_flag") && r.U(8, "aspect_ratio_idc") == 255)
        {
            r.U(16, "sar_width");
            r.U(16, "sar_height");
        }

        if (r.Flag("overscan_info_present_flag"))
        {
            r.Flag("overscan_appropriate_flag");
        }

        if (r.Flag("video_signal_type_present_flag"))
        {
            r.U(3, "video_format");
            vui.FullRange = r.Flag("video_full_range_flag");
            if (r.Flag("colour_description_present_flag"))
            {
                vui.ColourPrimaries = (int)r.U(8, "colour_primaries");
                vui.TransferCharacteristics = (int)r.U(8, "transfer_characteristics");
                vui.MatrixCoefficients = (int)r.U(8, "matrix_coeffs");
            }
        }

        if (r.Flag("chroma_loc_info_present_flag"))
        {
            vui.ChromaSampleLocation = (int)r.Ue("chroma_sample_loc_type_top_field", 5);
            r.Ue("chroma_sample_loc_type_bottom_field", 5);
        }

        r.Flag("neutral_chroma_indication_flag");
        r.Flag("field_seq_flag");
        r.Flag("frame_field_info_present_flag");
        if (r.Flag("default_display_window_flag"))
        {
            r.Ue("def_disp_win_left_offset");
            r.Ue("def_disp_win_right_offset");
            r.Ue("def_disp_win_top_offset");
            r.Ue("def_disp_win_bottom_offset");
        }

        if (r.Flag("vui_timing_info_present_flag"))
        {
            r.U(32, "vui_num_units_in_tick");
            r.U(32, "vui_time_scale");
            if (r.Flag("vui_poc_proportional_to_timing_flag"))
            {
                r.Ue("vui_num_ticks_poc_diff_one_minus1");
            }

            if (r.Flag("vui_hrd_parameters_present_flag"))
            {
                ReadHrd(r, true, maxSubLayersMinus1);
            }
        }

        if (r.Flag("bitstream_restriction_flag"))
        {
            r.Flag("tiles_fixed_structure_flag");
            r.Flag("motion_vectors_over_pic_boundaries_flag");
            r.Flag("restricted_ref_pic_lists_flag");
            r.Ue("min_spatial_segmentation_idc", 4095);
            r.Ue("max_bytes_per_pic_denom");
            r.Ue("max_bits_per_min_cu_denom");
            r.Ue("log2_max_mv_length_horizontal");
            r.Ue("log2_max_mv_length_vertical");
        }

        _ = (subWidthC, subHeightC);
        return vui;
    }

    /// <summary>Reads hrd_parameters() (H.265 E.2.2), which intra decoding does not use.</summary>
    internal static void ReadHrd(HevcBitReader r, bool commonInfPresent, int maxSubLayersMinus1)
    {
        bool nal = false, vcl = false, subPic = false;
        if (commonInfPresent)
        {
            nal = r.Flag("nal_hrd_parameters_present_flag");
            vcl = r.Flag("vcl_hrd_parameters_present_flag");
            if (nal || vcl)
            {
                subPic = r.Flag("sub_pic_hrd_params_present_flag");
                if (subPic)
                {
                    r.U(8, "tick_divisor_minus2");
                    r.U(5, "du_cpb_removal_delay_increment_length_minus1");
                    r.Flag("sub_pic_cpb_params_in_pic_timing_sei_flag");
                    r.U(5, "dpb_output_delay_du_length_minus1");
                }

                r.U(4, "bit_rate_scale");
                r.U(4, "cpb_size_scale");
                if (subPic)
                {
                    r.U(4, "cpb_size_du_scale");
                }

                r.U(5, "initial_cpb_removal_delay_length_minus1");
                r.U(5, "au_cpb_removal_delay_length_minus1");
                r.U(5, "dpb_output_delay_length_minus1");
            }
        }

        for (int i = 0; i <= maxSubLayersMinus1; i++)
        {
            bool fixedGeneral = r.Flag("fixed_pic_rate_general_flag");
            bool fixedWithinCvs = fixedGeneral || r.Flag("fixed_pic_rate_within_cvs_flag");
            bool lowDelay = false;
            if (fixedWithinCvs)
            {
                r.Ue("elemental_duration_in_tc_minus1");
            }
            else
            {
                lowDelay = r.Flag("low_delay_hrd_flag");
            }

            uint cpbCount = lowDelay ? 0 : r.Ue("cpb_cnt_minus1", 31);
            for (int k = 0; k < 2; k++)
            {
                if ((k == 0 && nal) || (k == 1 && vcl))
                {
                    for (int j = 0; j <= cpbCount; j++)
                    {
                        r.Ue("bit_rate_value_minus1");
                        r.Ue("cpb_size_value_minus1");
                        if (subPic)
                        {
                            r.Ue("cpb_size_du_value_minus1");
                            r.Ue("bit_rate_du_value_minus1");
                        }

                        r.Flag("cbr_flag");
                    }
                }
            }
        }
    }
}

/// <summary>
/// A scaling list set (H.265 7.3.4, 7.4.5) as the scaling factors m[x][y] it gives, for each transform size (4 to 32) and each
/// matrixId (intra Y, Cb, Cr, then inter Y, Cb, Cr), in raster order (index y * size + x).
/// </summary>
internal sealed class HevcScalingList
{
    /// <summary>The default 8x8 (and larger) intra list, in up-right diagonal scan order (Table 7-6).</summary>
    private static readonly byte[] DefaultIntra =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 16, 17, 16, 17, 18, 17, 18, 18, 17, 18, 21, 19, 20, 21, 20, 19, 21, 24, 22, 22, 24,
        24, 22, 22, 24, 25, 25, 27, 30, 27, 25, 25, 29, 31, 35, 35, 31, 29, 36, 41, 44, 41, 36, 47, 54, 54, 47, 65, 70, 65, 88, 88, 115,
    ];

    /// <summary>The default 8x8 (and larger) inter list, in up-right diagonal scan order (Table 7-6).</summary>
    private static readonly byte[] DefaultInter =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 17, 17, 17, 17, 18, 18, 18, 18, 18, 18, 20, 20, 20, 20, 20, 20, 20, 24, 24, 24, 24,
        24, 24, 24, 24, 25, 25, 25, 25, 25, 25, 25, 28, 28, 28, 28, 28, 28, 33, 33, 33, 33, 33, 41, 41, 41, 41, 54, 54, 54, 71, 71, 91,
    ];

    // The coded lists, in diagonal scan order (16 values for 4x4, 64 for the others), and the DC values of 16x16 and 32x32.
    private readonly byte[][][] _lists = new byte[4][][];
    private readonly int[,] _dc = new int[4, 6];

    private HevcScalingList()
    {
        for (int size = 0; size < 4; size++)
        {
            _lists[size] = new byte[6][];
        }
    }

    /// <summary>Gets the default lists (used when scaling lists are on but none is coded).</summary>
    internal static HevcScalingList Default { get; } = CreateDefault();

    /// <summary>Gets the factors by sizeId (transform size 4 &lt;&lt; sizeId) and matrixId, each in raster order.</summary>
    internal int[][][] Factors { get; private set; } = [];

    internal static HevcScalingList Read(HevcBitReader r)
    {
        HevcScalingList list = new();
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            int count = Math.Min(64, 1 << (4 + (sizeId << 1)));
            for (int matrixId = 0; matrixId < 6; matrixId += sizeId == 3 ? 3 : 1)
            {
                if (!r.Flag("scaling_list_pred_mode_flag"))
                {
                    uint delta = r.Ue("scaling_list_pred_matrix_id_delta", (uint)(sizeId == 3 ? matrixId / 3 : matrixId));
                    if (delta == 0)
                    {
                        list._lists[sizeId][matrixId] = DefaultList(sizeId, matrixId);
                        list._dc[sizeId, matrixId] = 16;
                    }
                    else
                    {
                        int reference = matrixId - ((int)delta * (sizeId == 3 ? 3 : 1));
                        list._lists[sizeId][matrixId] = list._lists[sizeId][reference];
                        list._dc[sizeId, matrixId] = list._dc[sizeId, reference];
                    }
                }
                else
                {
                    int next = 8;
                    if (sizeId > 1)
                    {
                        next = r.Se("scaling_list_dc_coef_minus8", -7, 247) + 8;
                        list._dc[sizeId, matrixId] = next;
                    }

                    byte[] coefficients = new byte[count];
                    for (int i = 0; i < count; i++)
                    {
                        int delta = r.Se("scaling_list_delta_coef", -128, 127);
                        next = (next + delta + 256) % 256;
                        coefficients[i] = (byte)next;
                    }

                    list._lists[sizeId][matrixId] = coefficients;
                }
            }
        }

        list.Derive();
        return list;
    }

    private static HevcScalingList CreateDefault()
    {
        HevcScalingList list = new();
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            for (int matrixId = 0; matrixId < 6; matrixId++)
            {
                list._lists[sizeId][matrixId] = DefaultList(sizeId, matrixId);
                list._dc[sizeId, matrixId] = 16;
            }
        }

        list.Derive();
        return list;
    }

    private static byte[] DefaultList(int sizeId, int matrixId) =>
        sizeId == 0 ? [.. Enumerable.Repeat((byte)16, 16)] : matrixId < 3 ? DefaultIntra : DefaultInter;

    /// <summary>Derives ScalingFactor (H.265 7.4.5) for every size and matrixId, the 32x32 chroma ones (4:4:4) from 16x16's lists.</summary>
    private void Derive()
    {
        int[][][] factors = new int[4][][];
        byte[] diag4 = HevcScan.Diagonal(2), diag8 = HevcScan.Diagonal(3);
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            int size = 4 << sizeId;
            factors[sizeId] = new int[6][];
            for (int matrixId = 0; matrixId < 6; matrixId++)
            {
                // 32x32 chroma (only used for 4:4:4): the 16x16 list and DC of the same matrixId.
                int source = sizeId == 3 && matrixId % 3 != 0 ? 2 : sizeId;
                byte[] coefficients = _lists[source][matrixId];
                int dc = _dc[source, matrixId];
                int[] m = new int[size * size];
                if (sizeId == 0)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        m[(diag4[(2 * i) + 1] * 4) + diag4[2 * i]] = coefficients[i];
                    }
                }
                else
                {
                    int ratio = size / 8;
                    for (int i = 0; i < 64; i++)
                    {
                        int x0 = diag8[2 * i] * ratio, y0 = diag8[(2 * i) + 1] * ratio;
                        for (int y = 0; y < ratio; y++)
                        {
                            for (int x = 0; x < ratio; x++)
                            {
                                m[((y0 + y) * size) + x0 + x] = coefficients[i];
                            }
                        }
                    }

                    if (sizeId > 1)
                    {
                        m[0] = dc;
                    }
                }

                factors[sizeId][matrixId] = m;
            }
        }

        Factors = factors;
    }
}

/// <summary>A short-term reference picture set (H.265 7.3.7, 7.4.8): the POC deltas and whether each is used now.</summary>
internal sealed class HevcShortTermRps
{
    /// <summary>Gets the negative POC deltas, nearest first, then the positive ones, nearest first.</summary>
    internal int[] Deltas { get; private set; } = [];

    /// <summary>Gets used_by_curr_pic for each delta.</summary>
    internal bool[] Used { get; private set; } = [];

    /// <summary>Gets NumNegativePics.</summary>
    internal int NumNegative { get; private set; }

    /// <summary>Gets how many of the pictures the current picture uses.</summary>
    internal int NumUsed => Used.Count(u => u);

    /// <summary>Reads st_ref_pic_set(idx), predicting from <paramref name="sets"/> when inter_ref_pic_set_prediction_flag is set.</summary>
    internal static HevcShortTermRps Read(HevcBitReader r, int idx, IReadOnlyList<HevcShortTermRps> sets, int numInSps)
    {
        HevcShortTermRps rps = new();
        bool predicted = idx != 0 && r.Flag("inter_ref_pic_set_prediction_flag");
        if (predicted)
        {
            uint deltaIdx = idx == numInSps ? r.Ue("delta_idx_minus1", (uint)(idx - 1)) + 1 : 1;
            HevcShortTermRps reference = sets[idx - (int)deltaIdx];
            bool sign = r.Flag("delta_rps_sign");
            int deltaRps = (sign ? -1 : 1) * (int)(r.Ue("abs_delta_rps_minus1", 32767) + 1);
            List<(int Delta, bool Used)> pictures = [];
            for (int j = 0; j <= reference.Deltas.Length; j++)
            {
                bool used = r.Flag("used_by_curr_pic_flag");
                bool useDelta = used || r.Flag("use_delta_flag");
                if (useDelta)
                {
                    int delta = deltaRps + (j < reference.Deltas.Length ? reference.Deltas[j] : 0);
                    pictures.Add((delta, used));
                }
            }

            // Negative deltas nearest first, then positive ones nearest first (7-61, 7-62).
            List<(int Delta, bool Used)> negative = [.. pictures.Where(p => p.Delta < 0).OrderByDescending(p => p.Delta)];
            List<(int Delta, bool Used)> positive = [.. pictures.Where(p => p.Delta > 0).OrderBy(p => p.Delta)];
            if (pictures.Any(p => p.Delta == 0))
            {
                throw HevcErrors.Corrupt("A predicted reference picture set has a POC delta of 0.");
            }

            rps.NumNegative = negative.Count;
            rps.Deltas = [.. negative.Select(p => p.Delta), .. positive.Select(p => p.Delta)];
            rps.Used = [.. negative.Select(p => p.Used), .. positive.Select(p => p.Used)];
        }
        else
        {
            int negativeCount = (int)r.Ue("num_negative_pics", 16), positiveCount = (int)r.Ue("num_positive_pics", 16);
            rps.NumNegative = negativeCount;
            rps.Deltas = new int[negativeCount + positiveCount];
            rps.Used = new bool[negativeCount + positiveCount];
            int poc = 0;
            for (int j = 0; j < negativeCount; j++)
            {
                poc -= (int)r.Ue("delta_poc_s0_minus1", 32767) + 1;
                rps.Deltas[j] = poc;
                rps.Used[j] = r.Flag("used_by_curr_pic_s0_flag");
            }

            poc = 0;
            for (int j = 0; j < positiveCount; j++)
            {
                poc += (int)r.Ue("delta_poc_s1_minus1", 32767) + 1;
                rps.Deltas[negativeCount + j] = poc;
                rps.Used[negativeCount + j] = r.Flag("used_by_curr_pic_s1_flag");
            }
        }

        return rps;
    }
}
