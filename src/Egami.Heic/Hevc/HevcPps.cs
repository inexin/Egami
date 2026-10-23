using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>A picture parameter set (H.265 7.3.2.3) with its range extension.</summary>
internal sealed class HevcPps
{
    /// <summary>Gets pps_pic_parameter_set_id.</summary>
    internal int Id { get; private set; }

    /// <summary>Gets pps_seq_parameter_set_id.</summary>
    internal int SpsId { get; private set; }

    /// <summary>Gets dependent_slice_segments_enabled_flag.</summary>
    internal bool DependentSliceSegmentsEnabled { get; private set; }

    /// <summary>Gets output_flag_present_flag.</summary>
    internal bool OutputFlagPresent { get; private set; }

    /// <summary>Gets num_extra_slice_header_bits.</summary>
    internal int NumExtraSliceHeaderBits { get; private set; }

    /// <summary>Gets sign_data_hiding_enabled_flag.</summary>
    internal bool SignDataHiding { get; private set; }

    /// <summary>Gets cabac_init_present_flag.</summary>
    internal bool CabacInitPresent { get; private set; }

    /// <summary>Gets num_ref_idx_l0_default_active_minus1 + 1.</summary>
    internal int NumRefIdxL0Default { get; private set; }

    /// <summary>Gets num_ref_idx_l1_default_active_minus1 + 1.</summary>
    internal int NumRefIdxL1Default { get; private set; }

    /// <summary>Gets init_qp_minus26.</summary>
    internal int InitQpMinus26 { get; private set; }

    /// <summary>Gets constrained_intra_pred_flag.</summary>
    internal bool ConstrainedIntraPred { get; private set; }

    /// <summary>Gets transform_skip_enabled_flag.</summary>
    internal bool TransformSkipEnabled { get; private set; }

    /// <summary>Gets cu_qp_delta_enabled_flag.</summary>
    internal bool CuQpDeltaEnabled { get; private set; }

    /// <summary>Gets diff_cu_qp_delta_depth.</summary>
    internal int DiffCuQpDeltaDepth { get; private set; }

    /// <summary>Gets pps_cb_qp_offset.</summary>
    internal int CbQpOffset { get; private set; }

    /// <summary>Gets pps_cr_qp_offset.</summary>
    internal int CrQpOffset { get; private set; }

    /// <summary>Gets pps_slice_chroma_qp_offsets_present_flag.</summary>
    internal bool SliceChromaQpOffsetsPresent { get; private set; }

    /// <summary>Gets weighted_pred_flag.</summary>
    internal bool WeightedPred { get; private set; }

    /// <summary>Gets weighted_bipred_flag.</summary>
    internal bool WeightedBipred { get; private set; }

    /// <summary>Gets transquant_bypass_enabled_flag.</summary>
    internal bool TransquantBypassEnabled { get; private set; }

    /// <summary>Gets tiles_enabled_flag.</summary>
    internal bool TilesEnabled { get; private set; }

    /// <summary>Gets entropy_coding_sync_enabled_flag.</summary>
    internal bool EntropyCodingSync { get; private set; }

    /// <summary>Gets num_tile_columns_minus1 + 1.</summary>
    internal int TileColumns { get; private set; } = 1;

    /// <summary>Gets num_tile_rows_minus1 + 1.</summary>
    internal int TileRows { get; private set; } = 1;

    /// <summary>Gets uniform_spacing_flag.</summary>
    internal bool UniformSpacing { get; private set; } = true;

    /// <summary>Gets column_width_minus1 + 1 of each column but the last, when not uniform.</summary>
    internal int[] ColumnWidths { get; private set; } = [];

    /// <summary>Gets row_height_minus1 + 1 of each row but the last, when not uniform.</summary>
    internal int[] RowHeights { get; private set; } = [];

    /// <summary>Gets loop_filter_across_tiles_enabled_flag.</summary>
    internal bool LoopFilterAcrossTiles { get; private set; } = true;

    /// <summary>Gets pps_loop_filter_across_slices_enabled_flag.</summary>
    internal bool LoopFilterAcrossSlices { get; private set; }

    /// <summary>Gets deblocking_filter_control_present_flag.</summary>
    internal bool DeblockingControlPresent { get; private set; }

    /// <summary>Gets deblocking_filter_override_enabled_flag.</summary>
    internal bool DeblockingOverrideEnabled { get; private set; }

    /// <summary>Gets pps_deblocking_filter_disabled_flag.</summary>
    internal bool DeblockingDisabled { get; private set; }

    /// <summary>Gets pps_beta_offset_div2.</summary>
    internal int BetaOffsetDiv2 { get; private set; }

    /// <summary>Gets pps_tc_offset_div2.</summary>
    internal int TcOffsetDiv2 { get; private set; }

    /// <summary>Gets the PPS's scaling lists, or null when it codes none (then the SPS's apply).</summary>
    internal HevcScalingList? ScalingList { get; private set; }

    /// <summary>Gets lists_modification_present_flag.</summary>
    internal bool ListsModificationPresent { get; private set; }

    /// <summary>Gets slice_segment_header_extension_present_flag.</summary>
    internal bool SliceHeaderExtensionPresent { get; private set; }

    /// <summary>Gets Log2MaxTransformSkipSize (2 unless the range extension sets it).</summary>
    internal int Log2MaxTransformSkipSize { get; private set; } = 2;

    /// <summary>Gets cross_component_prediction_enabled_flag.</summary>
    internal bool CrossComponentPrediction { get; private set; }

    /// <summary>Gets chroma_qp_offset_list_enabled_flag.</summary>
    internal bool ChromaQpOffsetListEnabled { get; private set; }

    /// <summary>Gets diff_cu_chroma_qp_offset_depth.</summary>
    internal int DiffCuChromaQpOffsetDepth { get; private set; }

    /// <summary>Gets cb_qp_offset_list (index 0 is entry 1 of the syntax).</summary>
    internal int[] CbQpOffsetList { get; private set; } = [];

    /// <summary>Gets cr_qp_offset_list.</summary>
    internal int[] CrQpOffsetList { get; private set; } = [];

    /// <summary>Gets log2_sao_offset_scale_luma.</summary>
    internal int Log2SaoOffsetScaleLuma { get; private set; }

    /// <summary>Gets log2_sao_offset_scale_chroma.</summary>
    internal int Log2SaoOffsetScaleChroma { get; private set; }

    internal static HevcPps Read(NalBitReader r)
    {
        HevcPps pps = new()
        {
            Id = (int)r.Ue("pps_pic_parameter_set_id", 63),
            SpsId = (int)r.Ue("pps_seq_parameter_set_id", 15),
            DependentSliceSegmentsEnabled = r.Flag("dependent_slice_segments_enabled_flag"),
            OutputFlagPresent = r.Flag("output_flag_present_flag"),
            NumExtraSliceHeaderBits = (int)r.U(3, "num_extra_slice_header_bits"),
            SignDataHiding = r.Flag("sign_data_hiding_enabled_flag"),
            CabacInitPresent = r.Flag("cabac_init_present_flag"),
            NumRefIdxL0Default = (int)r.Ue("num_ref_idx_l0_default_active_minus1", 14) + 1,
            NumRefIdxL1Default = (int)r.Ue("num_ref_idx_l1_default_active_minus1", 14) + 1,
            InitQpMinus26 = r.Se("init_qp_minus26", -26 - 48, 25),
            ConstrainedIntraPred = r.Flag("constrained_intra_pred_flag"),
            TransformSkipEnabled = r.Flag("transform_skip_enabled_flag"),
            CuQpDeltaEnabled = r.Flag("cu_qp_delta_enabled_flag"),
        };
        if (pps.CuQpDeltaEnabled)
        {
            pps.DiffCuQpDeltaDepth = (int)r.Ue("diff_cu_qp_delta_depth", 3);
        }

        pps.CbQpOffset = r.Se("pps_cb_qp_offset", -12, 12);
        pps.CrQpOffset = r.Se("pps_cr_qp_offset", -12, 12);
        pps.SliceChromaQpOffsetsPresent = r.Flag("pps_slice_chroma_qp_offsets_present_flag");
        pps.WeightedPred = r.Flag("weighted_pred_flag");
        pps.WeightedBipred = r.Flag("weighted_bipred_flag");
        pps.TransquantBypassEnabled = r.Flag("transquant_bypass_enabled_flag");
        pps.TilesEnabled = r.Flag("tiles_enabled_flag");
        pps.EntropyCodingSync = r.Flag("entropy_coding_sync_enabled_flag");
        if (pps.TilesEnabled)
        {
            pps.TileColumns = (int)r.Ue("num_tile_columns_minus1", 1023) + 1;
            pps.TileRows = (int)r.Ue("num_tile_rows_minus1", 1023) + 1;
            pps.UniformSpacing = r.Flag("uniform_spacing_flag");
            if (!pps.UniformSpacing)
            {
                pps.ColumnWidths = new int[pps.TileColumns - 1];
                for (int i = 0; i < pps.ColumnWidths.Length; i++)
                {
                    pps.ColumnWidths[i] = (int)r.Ue("column_width_minus1", 1 << 16) + 1;
                }

                pps.RowHeights = new int[pps.TileRows - 1];
                for (int i = 0; i < pps.RowHeights.Length; i++)
                {
                    pps.RowHeights[i] = (int)r.Ue("row_height_minus1", 1 << 16) + 1;
                }
            }

            pps.LoopFilterAcrossTiles = r.Flag("loop_filter_across_tiles_enabled_flag");
        }

        pps.LoopFilterAcrossSlices = r.Flag("pps_loop_filter_across_slices_enabled_flag");
        pps.DeblockingControlPresent = r.Flag("deblocking_filter_control_present_flag");
        if (pps.DeblockingControlPresent)
        {
            pps.DeblockingOverrideEnabled = r.Flag("deblocking_filter_override_enabled_flag");
            pps.DeblockingDisabled = r.Flag("pps_deblocking_filter_disabled_flag");
            if (!pps.DeblockingDisabled)
            {
                pps.BetaOffsetDiv2 = r.Se("pps_beta_offset_div2", -6, 6);
                pps.TcOffsetDiv2 = r.Se("pps_tc_offset_div2", -6, 6);
            }
        }

        if (r.Flag("pps_scaling_list_data_present_flag"))
        {
            pps.ScalingList = HevcScalingList.Read(r);
        }

        pps.ListsModificationPresent = r.Flag("lists_modification_present_flag");
        r.Ue("log2_parallel_merge_level_minus2");
        pps.SliceHeaderExtensionPresent = r.Flag("slice_segment_header_extension_present_flag");
        if (r.Flag("pps_extension_present_flag"))
        {
            bool range = r.Flag("pps_range_extension_flag");
            bool multilayer = r.Flag("pps_multilayer_extension_flag");
            bool other = false;
            for (int i = 0; i < 6; i++)
            {
                other |= r.Flag($"pps_extension_6bits[{i}]");
            }

            if (range)
            {
                if (pps.TransformSkipEnabled)
                {
                    pps.Log2MaxTransformSkipSize = (int)r.Ue("log2_max_transform_skip_block_size_minus2", 3) + 2;
                }

                pps.CrossComponentPrediction = r.Flag("cross_component_prediction_enabled_flag");
                pps.ChromaQpOffsetListEnabled = r.Flag("chroma_qp_offset_list_enabled_flag");
                if (pps.ChromaQpOffsetListEnabled)
                {
                    pps.DiffCuChromaQpOffsetDepth = (int)r.Ue("diff_cu_chroma_qp_offset_depth", 3);
                    int length = (int)r.Ue("chroma_qp_offset_list_len_minus1", 5) + 1;
                    pps.CbQpOffsetList = new int[length];
                    pps.CrQpOffsetList = new int[length];
                    for (int i = 0; i < length; i++)
                    {
                        pps.CbQpOffsetList[i] = r.Se("cb_qp_offset_list[i]", -12, 12);
                        pps.CrQpOffsetList[i] = r.Se("cr_qp_offset_list[i]", -12, 12);
                    }
                }

                pps.Log2SaoOffsetScaleLuma = (int)r.Ue("log2_sao_offset_scale_luma", 6);
                pps.Log2SaoOffsetScaleChroma = (int)r.Ue("log2_sao_offset_scale_chroma", 6);
            }

            if (multilayer || other)
            {
                while (r.MoreRbspData())
                {
                    r.Flag("pps_extension_data_flag");
                }
            }
        }

        r.TrailingBits();
        return pps;
    }
}

/// <summary>
/// The CTB layout a PPS gives a picture of an SPS (H.265 6.5.1, 6.5.2): tile column and row boundaries, the conversions between
/// raster and tile scan, each CTB's tile, and the minimum transform block scan order used by intra availability.
/// </summary>
internal sealed class HevcLayout
{
    internal HevcLayout(HevcSps sps, HevcPps pps)
    {
        int columns = pps.TileColumns, rows = pps.TileRows;
        int widthCtbs = sps.WidthInCtbs, heightCtbs = sps.HeightInCtbs;
        if (columns > widthCtbs || rows > heightCtbs)
        {
            throw HevcErrors.Corrupt($"The PPS has {columns} x {rows} tiles for {widthCtbs} x {heightCtbs} coding tree blocks.");
        }

        int[] columnWidth = new int[columns], rowHeight = new int[rows];
        if (pps.UniformSpacing)
        {
            for (int i = 0; i < columns; i++)
            {
                columnWidth[i] = (((i + 1) * widthCtbs) / columns) - ((i * widthCtbs) / columns);
            }

            for (int j = 0; j < rows; j++)
            {
                rowHeight[j] = (((j + 1) * heightCtbs) / rows) - ((j * heightCtbs) / rows);
            }
        }
        else
        {
            int used = 0;
            for (int i = 0; i < columns - 1; i++)
            {
                columnWidth[i] = pps.ColumnWidths[i];
                used += columnWidth[i];
            }

            columnWidth[columns - 1] = widthCtbs - used;
            used = 0;
            for (int j = 0; j < rows - 1; j++)
            {
                rowHeight[j] = pps.RowHeights[j];
                used += rowHeight[j];
            }

            rowHeight[rows - 1] = heightCtbs - used;
            if (columnWidth[columns - 1] <= 0 || rowHeight[rows - 1] <= 0)
            {
                throw HevcErrors.Corrupt("The tile columns or rows are wider than the picture.");
            }
        }

        ColumnBoundaries = new int[columns + 1];
        for (int i = 0; i < columns; i++)
        {
            ColumnBoundaries[i + 1] = ColumnBoundaries[i] + columnWidth[i];
        }

        RowBoundaries = new int[rows + 1];
        for (int j = 0; j < rows; j++)
        {
            RowBoundaries[j + 1] = RowBoundaries[j] + rowHeight[j];
        }

        int count = widthCtbs * heightCtbs;
        RsToTs = new int[count];
        TsToRs = new int[count];
        TileId = new int[count];
        for (int rs = 0; rs < count; rs++)
        {
            int x = rs % widthCtbs, y = rs / widthCtbs;
            int tileX = 0, tileY = 0;
            for (int i = 0; i < columns; i++)
            {
                if (x >= ColumnBoundaries[i])
                {
                    tileX = i;
                }
            }

            for (int j = 0; j < rows; j++)
            {
                if (y >= RowBoundaries[j])
                {
                    tileY = j;
                }
            }

            int ts = 0;
            for (int i = 0; i < tileX; i++)
            {
                ts += rowHeight[tileY] * columnWidth[i];
            }

            for (int j = 0; j < tileY; j++)
            {
                ts += widthCtbs * rowHeight[j];
            }

            ts += ((y - RowBoundaries[tileY]) * columnWidth[tileX]) + x - ColumnBoundaries[tileX];
            RsToTs[rs] = ts;
            TsToRs[ts] = rs;
            TileId[ts] = (tileY * columns) + tileX;
        }

        WidthInCtbs = widthCtbs;
    }

    /// <summary>Gets colBd: the first CTB column of each tile column, and the picture width in CTBs last.</summary>
    internal int[] ColumnBoundaries { get; }

    /// <summary>Gets rowBd: the first CTB row of each tile row, and the picture height in CTBs last.</summary>
    internal int[] RowBoundaries { get; }

    /// <summary>Gets CtbAddrRsToTs.</summary>
    internal int[] RsToTs { get; }

    /// <summary>Gets CtbAddrTsToRs.</summary>
    internal int[] TsToRs { get; }

    /// <summary>Gets TileId, by tile-scan address.</summary>
    internal int[] TileId { get; }

    /// <summary>Gets PicWidthInCtbsY.</summary>
    internal int WidthInCtbs { get; }
}
