using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>A sequence parameter set (H.265 7.3.2.2) and the variables derived from it (7.4.3.2).</summary>
internal sealed class HevcSps
{
    /// <summary>Gets sps_seq_parameter_set_id.</summary>
    internal int Id { get; private set; }

    /// <summary>Gets the profile, tier and level.</summary>
    internal HevcProfileTierLevel ProfileTierLevel { get; private set; } = null!;

    /// <summary>Gets chroma_format_idc: 0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4.</summary>
    internal int ChromaFormatIdc { get; private set; }

    /// <summary>Gets ChromaArrayType (equal to chroma_format_idc, as separate colour planes are not supported).</summary>
    internal int ChromaArrayType => ChromaFormatIdc;

    /// <summary>Gets SubWidthC.</summary>
    internal int SubWidthC => ChromaFormatIdc is 1 or 2 ? 2 : 1;

    /// <summary>Gets SubHeightC.</summary>
    internal int SubHeightC => ChromaFormatIdc == 1 ? 2 : 1;

    /// <summary>Gets pic_width_in_luma_samples.</summary>
    internal int Width { get; private set; }

    /// <summary>Gets pic_height_in_luma_samples.</summary>
    internal int Height { get; private set; }

    /// <summary>Gets the conformance window in luma samples: left, right, top, bottom.</summary>
    internal (int Left, int Right, int Top, int Bottom) Conformance { get; private set; }

    /// <summary>Gets BitDepthY.</summary>
    internal int BitDepthLuma { get; private set; }

    /// <summary>Gets BitDepthC.</summary>
    internal int BitDepthChroma { get; private set; }

    /// <summary>Gets log2_max_pic_order_cnt_lsb_minus4 + 4.</summary>
    internal int Log2MaxPocLsb { get; private set; }

    /// <summary>Gets MinCbLog2SizeY.</summary>
    internal int MinCbLog2Size { get; private set; }

    /// <summary>Gets CtbLog2SizeY.</summary>
    internal int CtbLog2Size { get; private set; }

    /// <summary>Gets MinTbLog2SizeY.</summary>
    internal int MinTbLog2Size { get; private set; }

    /// <summary>Gets MaxTbLog2SizeY.</summary>
    internal int MaxTbLog2Size { get; private set; }

    /// <summary>Gets max_transform_hierarchy_depth_intra.</summary>
    internal int MaxTransformHierarchyDepthIntra { get; private set; }

    /// <summary>Gets max_transform_hierarchy_depth_inter.</summary>
    internal int MaxTransformHierarchyDepthInter { get; private set; }

    /// <summary>Gets scaling_list_enabled_flag.</summary>
    internal bool ScalingListEnabled { get; private set; }

    /// <summary>Gets the SPS's scaling lists (the default ones when none is coded), or null when scaling lists are off.</summary>
    internal HevcScalingList? ScalingList { get; private set; }

    /// <summary>Gets amp_enabled_flag.</summary>
    internal bool AmpEnabled { get; private set; }

    /// <summary>Gets sample_adaptive_offset_enabled_flag.</summary>
    internal bool SaoEnabled { get; private set; }

    /// <summary>Gets pcm_enabled_flag.</summary>
    internal bool PcmEnabled { get; private set; }

    /// <summary>Gets PcmBitDepthY.</summary>
    internal int PcmBitDepthLuma { get; private set; }

    /// <summary>Gets PcmBitDepthC.</summary>
    internal int PcmBitDepthChroma { get; private set; }

    /// <summary>Gets Log2MinIpcmCbSizeY.</summary>
    internal int Log2MinPcmCbSize { get; private set; }

    /// <summary>Gets Log2MaxIpcmCbSizeY.</summary>
    internal int Log2MaxPcmCbSize { get; private set; }

    /// <summary>Gets pcm_loop_filter_disabled_flag.</summary>
    internal bool PcmLoopFilterDisabled { get; private set; }

    /// <summary>Gets sps_max_dec_pic_buffering_minus1 + 1 of the highest sub-layer: the decoded picture buffer's size.</summary>
    internal int MaxDecPicBuffering { get; private set; } = 1;

    /// <summary>Gets sps_max_num_reorder_pics of the highest sub-layer.</summary>
    internal int MaxNumReorder { get; private set; }

    /// <summary>Gets sps_max_latency_increase_plus1 of the highest sub-layer (0: no latency limit).</summary>
    internal int MaxLatencyIncreasePlus1 { get; private set; }

    /// <summary>Gets SpsMaxLatencyPictures (7-9), or 0 for no limit.</summary>
    internal int MaxLatencyPictures => MaxLatencyIncreasePlus1 == 0 ? 0 : MaxNumReorder + MaxLatencyIncreasePlus1 - 1;

    /// <summary>Gets the short-term reference picture sets.</summary>
    internal List<HevcShortTermRps> ShortTermRps { get; } = [];

    /// <summary>Gets long_term_ref_pics_present_flag.</summary>
    internal bool LongTermRefsPresent { get; private set; }

    /// <summary>Gets num_long_term_ref_pics_sps.</summary>
    internal int NumLongTermRefPicsSps { get; private set; }

    /// <summary>Gets used_by_curr_pic_lt_sps_flag of each long-term picture the SPS lists.</summary>
    internal bool[] UsedByCurrPicLtSps { get; private set; } = [];

    /// <summary>Gets lt_ref_pic_poc_lsb_sps of each long-term picture the SPS lists.</summary>
    internal int[] LtRefPicPocLsbSps { get; private set; } = [];

    /// <summary>Gets sps_temporal_mvp_enabled_flag.</summary>
    internal bool TemporalMvpEnabled { get; private set; }

    /// <summary>Gets strong_intra_smoothing_enabled_flag.</summary>
    internal bool StrongIntraSmoothing { get; private set; }

    /// <summary>Gets the VUI, or null.</summary>
    internal HevcVui? Vui { get; private set; }

    /// <summary>Gets transform_skip_rotation_enabled_flag.</summary>
    internal bool TransformSkipRotation { get; private set; }

    /// <summary>Gets transform_skip_context_enabled_flag.</summary>
    internal bool TransformSkipContext { get; private set; }

    /// <summary>Gets implicit_rdpcm_enabled_flag.</summary>
    internal bool ImplicitRdpcm { get; private set; }

    /// <summary>Gets explicit_rdpcm_enabled_flag.</summary>
    internal bool ExplicitRdpcm { get; private set; }

    /// <summary>Gets extended_precision_processing_flag.</summary>
    internal bool ExtendedPrecision { get; private set; }

    /// <summary>Gets intra_smoothing_disabled_flag.</summary>
    internal bool IntraSmoothingDisabled { get; private set; }

    /// <summary>Gets high_precision_offsets_enabled_flag.</summary>
    internal bool HighPrecisionOffsets { get; private set; }

    /// <summary>Gets persistent_rice_adaptation_enabled_flag.</summary>
    internal bool PersistentRiceAdaptation { get; private set; }

    /// <summary>Gets cabac_bypass_alignment_enabled_flag.</summary>
    internal bool CabacBypassAlignment { get; private set; }

    /// <summary>Gets PicWidthInCtbsY.</summary>
    internal int WidthInCtbs { get; private set; }

    /// <summary>Gets PicHeightInCtbsY.</summary>
    internal int HeightInCtbs { get; private set; }

    /// <summary>Gets PicWidthInMinCbsY.</summary>
    internal int WidthInMinCbs => Width >> MinCbLog2Size;

    /// <summary>Gets PicHeightInMinCbsY.</summary>
    internal int HeightInMinCbs => Height >> MinCbLog2Size;

    /// <summary>Gets QpBdOffsetY.</summary>
    internal int QpBdOffsetLuma => 6 * (BitDepthLuma - 8);

    /// <summary>Gets QpBdOffsetC.</summary>
    internal int QpBdOffsetChroma => 6 * (BitDepthChroma - 8);

    /// <summary>Gets the width after the conformance window.</summary>
    internal int OutputWidth => Width - Conformance.Left - Conformance.Right;

    /// <summary>Gets the height after the conformance window.</summary>
    internal int OutputHeight => Height - Conformance.Top - Conformance.Bottom;

    internal static HevcSps Read(NalBitReader r)
    {
        HevcSps sps = new();
        r.U(4, "sps_video_parameter_set_id");
        int maxSubLayersMinus1 = (int)r.U(3, "sps_max_sub_layers_minus1");
        if (maxSubLayersMinus1 > 6)
        {
            throw HevcErrors.Corrupt($"sps_max_sub_layers_minus1 is {maxSubLayersMinus1}, more than 6.");
        }

        r.Flag("sps_temporal_id_nesting_flag");
        sps.ProfileTierLevel = HevcProfileTierLevel.Read(r, true, maxSubLayersMinus1);
        sps.Id = (int)r.Ue("sps_seq_parameter_set_id", 15);
        sps.ChromaFormatIdc = (int)r.Ue("chroma_format_idc", 3);
        if (sps.ChromaFormatIdc == 3 && r.Flag("separate_colour_plane_flag"))
        {
            throw HevcErrors.Unsupported("The stream codes the colour planes separately (separate_colour_plane_flag).");
        }

        sps.Width = (int)r.Ue("pic_width_in_luma_samples", 1 << 16);
        sps.Height = (int)r.Ue("pic_height_in_luma_samples", 1 << 16);
        if (sps.Width == 0 || sps.Height == 0)
        {
            throw HevcErrors.Corrupt($"The picture is {sps.Width} x {sps.Height}.");
        }

        if (r.Flag("conformance_window_flag"))
        {
            int left = (int)r.Ue("conf_win_left_offset", 1 << 16) * sps.SubWidthC;
            int right = (int)r.Ue("conf_win_right_offset", 1 << 16) * sps.SubWidthC;
            int top = (int)r.Ue("conf_win_top_offset", 1 << 16) * sps.SubHeightC;
            int bottom = (int)r.Ue("conf_win_bottom_offset", 1 << 16) * sps.SubHeightC;
            if (left + right >= sps.Width || top + bottom >= sps.Height)
            {
                throw HevcErrors.Corrupt("The conformance window leaves no picture.");
            }

            sps.Conformance = (left, right, top, bottom);
        }

        sps.BitDepthLuma = (int)r.Ue("bit_depth_luma_minus8", 8) + 8;
        sps.BitDepthChroma = (int)r.Ue("bit_depth_chroma_minus8", 8) + 8;
        sps.Log2MaxPocLsb = (int)r.Ue("log2_max_pic_order_cnt_lsb_minus4", 12) + 4;
        bool orderingInfo = r.Flag("sps_sub_layer_ordering_info_present_flag");
        for (int i = orderingInfo ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
        {
            // The highest sub-layer's values are the ones a decoder of every sub-layer keeps.
            sps.MaxDecPicBuffering = (int)r.Ue("sps_max_dec_pic_buffering_minus1[i]", 15) + 1;
            sps.MaxNumReorder = (int)r.Ue("sps_max_num_reorder_pics[i]", (uint)(sps.MaxDecPicBuffering - 1));
            sps.MaxLatencyIncreasePlus1 = (int)r.Ue("sps_max_latency_increase_plus1[i]", uint.MaxValue - 1);
        }

        sps.MinCbLog2Size = (int)r.Ue("log2_min_luma_coding_block_size_minus3", 3) + 3;
        sps.CtbLog2Size = sps.MinCbLog2Size + (int)r.Ue("log2_diff_max_min_luma_coding_block_size", 3);
        if (sps.CtbLog2Size is < 4 or > 6)
        {
            throw HevcErrors.Corrupt($"The coding tree blocks are {1 << sps.CtbLog2Size} samples; HEVC allows 16 to 64.");
        }

        sps.MinTbLog2Size = (int)r.Ue("log2_min_luma_transform_block_size_minus2", 3) + 2;
        sps.MaxTbLog2Size = sps.MinTbLog2Size + (int)r.Ue("log2_diff_max_min_luma_transform_block_size", 3);
        if (sps.MinTbLog2Size >= sps.MinCbLog2Size || sps.MaxTbLog2Size > Math.Min(sps.CtbLog2Size, 5))
        {
            throw HevcErrors.Corrupt("The transform block sizes do not fit the coding block sizes.");
        }

        if ((sps.Width & ((1 << sps.MinCbLog2Size) - 1)) != 0 || (sps.Height & ((1 << sps.MinCbLog2Size) - 1)) != 0)
        {
            throw HevcErrors.Corrupt($"The picture size {sps.Width} x {sps.Height} is not a multiple of the smallest coding block.");
        }

        sps.MaxTransformHierarchyDepthInter = (int)r.Ue("max_transform_hierarchy_depth_inter", (uint)(sps.CtbLog2Size - sps.MinTbLog2Size));
        sps.MaxTransformHierarchyDepthIntra = (int)r.Ue("max_transform_hierarchy_depth_intra", (uint)(sps.CtbLog2Size - sps.MinTbLog2Size));
        sps.ScalingListEnabled = r.Flag("scaling_list_enabled_flag");
        if (sps.ScalingListEnabled)
        {
            sps.ScalingList = r.Flag("sps_scaling_list_data_present_flag") ? HevcScalingList.Read(r) : HevcScalingList.Default;
        }

        sps.AmpEnabled = r.Flag("amp_enabled_flag");
        sps.SaoEnabled = r.Flag("sample_adaptive_offset_enabled_flag");
        sps.PcmEnabled = r.Flag("pcm_enabled_flag");
        if (sps.PcmEnabled)
        {
            sps.PcmBitDepthLuma = (int)r.U(4, "pcm_sample_bit_depth_luma_minus1") + 1;
            sps.PcmBitDepthChroma = (int)r.U(4, "pcm_sample_bit_depth_chroma_minus1") + 1;
            if (sps.PcmBitDepthLuma > sps.BitDepthLuma || sps.PcmBitDepthChroma > sps.BitDepthChroma)
            {
                throw HevcErrors.Corrupt("The PCM bit depth is more than the bit depth.");
            }

            sps.Log2MinPcmCbSize = (int)r.Ue("log2_min_pcm_luma_coding_block_size_minus3", 2) + 3;
            sps.Log2MaxPcmCbSize = sps.Log2MinPcmCbSize + (int)r.Ue("log2_diff_max_min_pcm_luma_coding_block_size", 2);
            if (sps.Log2MaxPcmCbSize > Math.Min(sps.CtbLog2Size, 5) || sps.Log2MinPcmCbSize < sps.MinCbLog2Size)
            {
                throw HevcErrors.Corrupt("The PCM block sizes do not fit the coding block sizes.");
            }

            sps.PcmLoopFilterDisabled = r.Flag("pcm_loop_filter_disable_flag");
        }

        int rpsCount = (int)r.Ue("num_short_term_ref_pic_sets", 64);
        for (int i = 0; i < rpsCount; i++)
        {
            sps.ShortTermRps.Add(HevcShortTermRps.Read(r, i, sps.ShortTermRps, rpsCount));
        }

        sps.LongTermRefsPresent = r.Flag("long_term_ref_pics_present_flag");
        if (sps.LongTermRefsPresent)
        {
            sps.NumLongTermRefPicsSps = (int)r.Ue("num_long_term_ref_pics_sps", 32);
            sps.UsedByCurrPicLtSps = new bool[sps.NumLongTermRefPicsSps];
            sps.LtRefPicPocLsbSps = new int[sps.NumLongTermRefPicsSps];
            for (int i = 0; i < sps.NumLongTermRefPicsSps; i++)
            {
                sps.LtRefPicPocLsbSps[i] = (int)r.U(sps.Log2MaxPocLsb, "lt_ref_pic_poc_lsb_sps");
                sps.UsedByCurrPicLtSps[i] = r.Flag("used_by_curr_pic_lt_sps_flag[i]");
            }
        }

        sps.TemporalMvpEnabled = r.Flag("sps_temporal_mvp_enabled_flag");
        sps.StrongIntraSmoothing = r.Flag("strong_intra_smoothing_enable_flag");
        if (r.Flag("vui_parameters_present_flag"))
        {
            sps.Vui = HevcVui.Read(r, sps.SubWidthC, sps.SubHeightC, maxSubLayersMinus1);
        }

        if (r.Flag("sps_extension_present_flag"))
        {
            bool range = r.Flag("sps_range_extension_flag");
            bool multilayer = r.Flag("sps_multilayer_extension_flag");
            bool other = false;
            for (int i = 0; i < 6; i++)
            {
                other |= r.Flag($"sps_extension_6bits[{i}]");
            }

            if (range)
            {
                sps.TransformSkipRotation = r.Flag("transform_skip_rotation_enabled_flag");
                sps.TransformSkipContext = r.Flag("transform_skip_context_enabled_flag");
                sps.ImplicitRdpcm = r.Flag("implicit_rdpcm_enabled_flag");
                sps.ExplicitRdpcm = r.Flag("explicit_rdpcm_enabled_flag");
                sps.ExtendedPrecision = r.Flag("extended_precision_processing_flag");
                sps.IntraSmoothingDisabled = r.Flag("intra_smoothing_disabled_flag");
                sps.HighPrecisionOffsets = r.Flag("high_precision_offsets_enabled_flag");
                sps.PersistentRiceAdaptation = r.Flag("persistent_rice_adaptation_enabled_flag");
                sps.CabacBypassAlignment = r.Flag("cabac_bypass_alignment_enabled_flag");
            }

            if (multilayer || other)
            {
                while (r.MoreRbspData())
                {
                    r.Flag("sps_extension_data_flag");
                }
            }
        }

        r.TrailingBits();
        sps.WidthInCtbs = (sps.Width + (1 << sps.CtbLog2Size) - 1) >> sps.CtbLog2Size;
        sps.HeightInCtbs = (sps.Height + (1 << sps.CtbLog2Size) - 1) >> sps.CtbLog2Size;
        return sps;
    }

    /// <summary>Throws when the SPS uses something 2.9.4 does not decode (design question 4).</summary>
    internal void CheckSupported()
    {
        if (BitDepthLuma > 12 || BitDepthChroma > 12)
        {
            throw HevcErrors.Unsupported($"The stream is {Math.Max(BitDepthLuma, BitDepthChroma)}-bit.");
        }

        if (ExtendedPrecision)
        {
            throw HevcErrors.Unsupported("The stream uses extended precision processing (a 16-bit profile).");
        }

        if (CabacBypassAlignment)
        {
            throw HevcErrors.Unsupported("The stream aligns CABAC bypass bins (a High Throughput profile).");
        }
    }
}
