using System.Buffers.Binary;

using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// Writes the NAL units an intra picture of <see cref="HevcEncoder"/> needs (H.265 7.3): the VPS, SPS (with its VUI) and PPS for
/// <see cref="HevcEncoderSettings"/>, the slice segment header of the picture's one IDR slice, and the hvcC record that carries the
/// parameter sets in a HEIF item (ISO/IEC 14496-15, 8.3.3.1). The profile is the smallest one the settings fit: Main, Main 10, or a
/// format range extensions profile (Table A.2) for 12 bits, 4:0:0, 4:2:2 and 4:4:4.
/// </summary>
internal static class HevcParameterWriter
{
    /// <summary>The levels' general_level_idc and MaxLumaPs (Table A.8).</summary>
    private static readonly (int Idc, int MaxLumaPs)[] Levels =
    [
        (30, 36864), (60, 122880), (63, 245760), (90, 552960), (93, 983040), (120, 2228224), (150, 8912896), (180, 35651584),
    ];

    /// <summary>The profile, compatibility flags and constraint flags (the 48 bits after them) the settings need.</summary>
    internal static (int Profile, uint Compatibility, ulong Constraints) Profile(HevcEncoderSettings s)
    {
        // progressive_source_flag, frame_only_constraint_flag: bits 47 and 44 of the 48 constraint bits.
        const ulong Progressive = (1UL << 47) | (1UL << 44);
        if (s.ChromaFormat == 1 && s.BitDepth == 8)
        {
            // Main, which a Main 10 decoder also reads.
            return (1, (1u << 1) | (1u << 2), Progressive);
        }

        if (s.ChromaFormat == 1 && s.BitDepth == 10)
        {
            return (2, 1u << 2, Progressive);
        }

        // Format range extensions (Table A.2): max_12bit, max_10bit, max_8bit, max_422chroma, max_420chroma,
        // max_monochrome, intra, one_picture_only and lower_bit_rate constraint flags, bits 43 down to 35.
        bool max10 = s.BitDepth <= 10 && s.ChromaFormat != 0 && s.ChromaFormat != 1;
        bool max8 = s.BitDepth <= 8 && (s.ChromaFormat == 3 || s.ChromaFormat == 0);
        bool max422 = s.ChromaFormat != 3;
        bool max420 = s.ChromaFormat is 0 or 1;
        bool mono = s.ChromaFormat == 0;
        ulong flags = Progressive | (1UL << 43) | (max10 || max8 ? 1UL << 42 : 0) | (max8 ? 1UL << 41 : 0) | (max422 ? 1UL << 40 : 0)
            | (max420 ? 1UL << 39 : 0) | (mono ? 1UL << 38 : 0) | (1UL << 35);
        return (4, 1u << 4, flags);
    }

    /// <summary>The lowest level whose MaxLumaPs and side length (A.4.1) hold the coded picture.</summary>
    internal static int Level(HevcEncoderSettings s)
    {
        long samples = (long)s.CodedWidth * s.CodedHeight;
        foreach ((int idc, int max) in Levels)
        {
            double side = Math.Sqrt(max * 8.0);
            if (samples <= max && s.CodedWidth <= side && s.CodedHeight <= side)
            {
                return idc;
            }
        }

        return 186;
    }

    /// <summary>Writes the VPS as a NAL unit (escaped, with its header).</summary>
    internal static byte[] Vps(HevcEncoderSettings s)
    {
        NalBitWriter w = new();
        w.U(4, 0); // vps_video_parameter_set_id
        w.Flag(true); // vps_base_layer_internal_flag
        w.Flag(true); // vps_base_layer_available_flag
        w.U(6, 0); // vps_max_layers_minus1
        w.U(3, 0); // vps_max_sub_layers_minus1
        w.Flag(true); // vps_temporal_id_nesting_flag
        w.U(16, 0xFFFF); // vps_reserved_0xffff_16bits
        ProfileTierLevel(w, s);
        w.Flag(true); // vps_sub_layer_ordering_info_present_flag
        w.Ue(0); // vps_max_dec_pic_buffering_minus1
        w.Ue(0); // vps_max_num_reorder_pics
        w.Ue(0); // vps_max_latency_increase_plus1
        w.U(6, 0); // vps_max_layer_id
        w.Ue(0); // vps_num_layer_sets_minus1
        w.Flag(false); // vps_timing_info_present_flag
        w.Flag(false); // vps_extension_flag
        w.TrailingBits();
        return Nal(HevcNal.Vps, w.ToArray());
    }

    /// <summary>Writes the SPS as a NAL unit.</summary>
    internal static byte[] Sps(HevcEncoderSettings s)
    {
        NalBitWriter w = new();
        w.U(4, 0); // sps_video_parameter_set_id
        w.U(3, 0); // sps_max_sub_layers_minus1
        w.Flag(true); // sps_temporal_id_nesting_flag
        ProfileTierLevel(w, s);
        w.Ue(0); // sps_seq_parameter_set_id
        w.Ue((uint)s.ChromaFormat);
        if (s.ChromaFormat == 3)
        {
            w.Flag(false); // separate_colour_plane_flag
        }

        w.Ue((uint)s.CodedWidth);
        w.Ue((uint)s.CodedHeight);
        int right = s.CodedWidth - s.Width, bottom = s.CodedHeight - s.Height;
        w.Flag(right != 0 || bottom != 0); // conformance_window_flag
        if (right != 0 || bottom != 0)
        {
            w.Ue(0);
            w.Ue((uint)(right / s.SubWidthC));
            w.Ue(0);
            w.Ue((uint)(bottom / s.SubHeightC));
        }

        w.Ue((uint)(s.BitDepth - 8)); // bit_depth_luma_minus8
        w.Ue((uint)(s.BitDepth - 8)); // bit_depth_chroma_minus8
        w.Ue(0); // log2_max_pic_order_cnt_lsb_minus4
        w.Flag(true); // sps_sub_layer_ordering_info_present_flag
        w.Ue(0); // sps_max_dec_pic_buffering_minus1
        w.Ue(0); // sps_max_num_reorder_pics
        w.Ue(0); // sps_max_latency_increase_plus1
        w.Ue(0); // log2_min_luma_coding_block_size_minus3: 8x8
        w.Ue((uint)(s.CtbLog2Size - 3)); // log2_diff_max_min_luma_coding_block_size
        w.Ue(0); // log2_min_luma_transform_block_size_minus2: 4x4
        w.Ue((uint)(Math.Min(s.CtbLog2Size, 5) - 2)); // log2_diff_max_min_luma_transform_block_size: up to 32x32
        w.Ue((uint)s.MaxTransformDepth); // max_transform_hierarchy_depth_inter
        w.Ue((uint)s.MaxTransformDepth); // max_transform_hierarchy_depth_intra
        w.Flag(false); // scaling_list_enabled_flag
        w.Flag(false); // amp_enabled_flag
        w.Flag(s.Sao && !s.Lossless); // sample_adaptive_offset_enabled_flag
        w.Flag(false); // pcm_enabled_flag
        w.Ue(0); // num_short_term_ref_pic_sets
        w.Flag(false); // long_term_ref_pics_present_flag
        w.Flag(false); // sps_temporal_mvp_enabled_flag
        w.Flag(true); // strong_intra_smoothing_enabled_flag
        w.Flag(true); // vui_parameters_present_flag
        Vui(w, s);
        w.Flag(false); // sps_extension_present_flag
        w.TrailingBits();
        return Nal(HevcNal.Sps, w.ToArray());
    }

    /// <summary>Writes the PPS as a NAL unit.</summary>
    internal static byte[] Pps(HevcEncoderSettings s)
    {
        NalBitWriter w = new();
        w.Ue(0); // pps_pic_parameter_set_id
        w.Ue(0); // pps_seq_parameter_set_id
        w.Flag(false); // dependent_slice_segments_enabled_flag
        w.Flag(false); // output_flag_present_flag
        w.U(3, 0); // num_extra_slice_header_bits
        w.Flag(s.SignHiding && !s.Lossless); // sign_data_hiding_enabled_flag
        w.Flag(false); // cabac_init_present_flag
        w.Ue(0); // num_ref_idx_l0_default_active_minus1
        w.Ue(0); // num_ref_idx_l1_default_active_minus1
        w.Se(s.Lossless ? 0 : s.Qp - 26); // init_qp_minus26: the slice's QP, so slice_qp_delta is 0
        w.Flag(false); // constrained_intra_pred_flag
        w.Flag(s.TransformSkip && !s.Lossless); // transform_skip_enabled_flag
        bool qpDelta = s.AdaptiveQp && !s.Lossless;
        w.Flag(qpDelta); // cu_qp_delta_enabled_flag
        if (qpDelta)
        {
            w.Ue((uint)(s.CtbLog2Size - s.QpGroupLog2)); // diff_cu_qp_delta_depth
        }

        int chromaOffset = s.ChromaFormat == 0 || s.Lossless ? 0 : s.ChromaQpOffset;
        w.Se(chromaOffset); // pps_cb_qp_offset
        w.Se(chromaOffset); // pps_cr_qp_offset
        w.Flag(false); // pps_slice_chroma_qp_offsets_present_flag
        w.Flag(false); // weighted_pred_flag
        w.Flag(false); // weighted_bipred_flag
        w.Flag(s.Lossless); // transquant_bypass_enabled_flag
        w.Flag(false); // tiles_enabled_flag
        w.Flag(Wavefronts(s)); // entropy_coding_sync_enabled_flag
        w.Flag(false); // pps_loop_filter_across_slices_enabled_flag
        bool deblockingOff = !s.Deblocking || s.Lossless;
        w.Flag(deblockingOff); // deblocking_filter_control_present_flag
        if (deblockingOff)
        {
            w.Flag(false); // deblocking_filter_override_enabled_flag
            w.Flag(true); // pps_deblocking_filter_disabled_flag
        }

        w.Flag(false); // pps_scaling_list_data_present_flag
        w.Flag(false); // lists_modification_present_flag
        w.Ue(0); // log2_parallel_merge_level_minus2
        w.Flag(false); // slice_segment_header_extension_present_flag
        w.Flag(false); // pps_extension_present_flag
        w.TrailingBits();
        return Nal(HevcNal.Pps, w.ToArray());
    }

    /// <summary>Gets whether the picture codes its CTB rows as wavefront substreams: when it has more than one row.</summary>
    internal static bool Wavefronts(HevcEncoderSettings s) => s.Wavefronts && s.CodedHeight > 1 << s.CtbLog2Size;

    /// <summary>
    /// Writes the slice segment header of the picture's IDR slice (an RBSP, to byte_alignment()), with the entry points of its
    /// substreams after the first: their sizes in bytes as the NAL unit holds them, emulation prevention included.
    /// </summary>
    internal static byte[] SliceHeader(HevcEncoderSettings s, bool sao, ReadOnlySpan<int> substreamSizes)
    {
        NalBitWriter w = new();
        w.U(1, 0); // forbidden_zero_bit
        w.U(6, HevcNal.IdrNLp);
        w.U(6, 0); // nuh_layer_id
        w.U(3, 1); // nuh_temporal_id_plus1
        w.Flag(true); // first_slice_segment_in_pic_flag
        w.Flag(false); // no_output_of_prior_pics_flag
        w.Ue(0); // slice_pic_parameter_set_id
        w.Ue(HevcSliceHeader.I); // slice_type
        if (s.Sao && !s.Lossless)
        {
            w.Flag(sao); // slice_sao_luma_flag
            if (s.ChromaFormat != 0)
            {
                w.Flag(sao); // slice_sao_chroma_flag
            }
        }

        w.Se(0); // slice_qp_delta
        if (Wavefronts(s))
        {
            w.Ue((uint)Math.Max(substreamSizes.Length - 1, 0)); // num_entry_point_offsets
            if (substreamSizes.Length > 1)
            {
                int max = 1;
                for (int i = 0; i < substreamSizes.Length - 1; i++)
                {
                    max = Math.Max(max, substreamSizes[i]);
                }

                int bits = Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(max - 1)));
                w.Ue((uint)(bits - 1)); // offset_len_minus1
                for (int i = 0; i < substreamSizes.Length - 1; i++)
                {
                    w.U(bits, (uint)(substreamSizes[i] - 1)); // entry_point_offset_minus1
                }
            }
        }

        w.ByteAlignment();
        return w.ToArray();
    }

    /// <summary>
    /// Joins a slice segment header (from <see cref="SliceHeader"/>, its NAL unit header included) and the substreams of its data
    /// into one escaped NAL unit. The substreams are escaped on their own: the header ends with byte_alignment(), whose last byte
    /// is never 0, so no emulation prevention byte spans the two, and each substream ends with byte_alignment() too.
    /// </summary>
    internal static byte[] SliceNal(HevcEncoderSettings s, bool sao, IReadOnlyList<byte[]> substreams)
    {
        // The data's substreams escaped as one run, so a 00 00 at the end of one and 03 at the start of the next is escaped too;
        // the entry points then count each substream's escaped bytes.
        int total = 0;
        foreach (byte[] substream in substreams)
        {
            total += substream.Length;
        }

        byte[] data = new byte[total];
        Span<int> sizes = substreams.Count <= 256 ? stackalloc int[substreams.Count] : new int[substreams.Count];
        int at = 0;
        for (int i = 0; i < substreams.Count; i++)
        {
            substreams[i].CopyTo(data, at);
            sizes[i] = substreams[i].Length;
            at += substreams[i].Length;
        }

        List<int> inserted = [];
        byte[] escaped = NalFraming.Escape(data, inserted);
        int k = 0, end = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            end += sizes[i];
            int count = 0;
            while (k < inserted.Count && inserted[k] < end)
            {
                count++;
                k++;
            }

            sizes[i] += count;
        }

        byte[] header = NalFraming.Escape(SliceHeader(s, sao, sizes));
        return [.. header, .. escaped];
    }

    /// <summary>Writes the hvcC record (the property's content after its box header) for the parameter set NAL units.</summary>
    internal static byte[] ConfigurationRecord(HevcEncoderSettings s, byte[] vps, byte[] sps, byte[] pps)
    {
        (int profile, uint compatibility, ulong constraints) = Profile(s);

        // Profile() keeps flag j in bit j, as the SPS writes them one by one; hvcC carries the 32 flags in bitstream order, flag 0
        // in the top bit (ISO/IEC 14496-15, 8.3.3.1), so the word is reversed before it is written big-endian.
        uint reversed = 0;
        for (int j = 0; j < 32; j++)
        {
            reversed |= ((compatibility >> j) & 1) << (31 - j);
        }

        compatibility = reversed;
        List<byte> record =
        [
            1, // configurationVersion
            (byte)profile, // general_profile_space 0, general_tier_flag 0, general_profile_idc
            (byte)(compatibility >> 24), (byte)(compatibility >> 16), (byte)(compatibility >> 8), (byte)compatibility,
            (byte)(constraints >> 40), (byte)(constraints >> 32), (byte)(constraints >> 24), (byte)(constraints >> 16),
            (byte)(constraints >> 8), (byte)constraints,
            (byte)Level(s),
            0xF0, 0x00, // reserved, min_spatial_segmentation_idc 0
            0xFC, // reserved, parallelismType 0
            (byte)(0xFC | s.ChromaFormat),
            (byte)(0xF8 | (s.BitDepth - 8)),
            (byte)(0xF8 | (s.BitDepth - 8)),
            0, 0, // avgFrameRate
            0x0F, // constantFrameRate 0, numTemporalLayers 1, temporalIdNested 1, lengthSizeMinusOne 3
            3, // numOfArrays
        ];
        foreach ((int type, byte[] nal) in (ReadOnlySpan<(int, byte[])>)[(HevcNal.Vps, vps), (HevcNal.Sps, sps), (HevcNal.Pps, pps)])
        {
            record.Add((byte)(0x80 | type)); // array_completeness 1
            record.Add(0);
            record.Add(1); // numNalus
            record.Add((byte)(nal.Length >> 8));
            record.Add((byte)nal.Length);
            record.AddRange(nal);
        }

        return [.. record];
    }

    /// <summary>Writes NAL units as item data: each after a 4-byte big-endian size.</summary>
    internal static byte[] LengthPrefixed(params ReadOnlySpan<byte[]> nals)
    {
        int length = 0;
        foreach (byte[] nal in nals)
        {
            length += 4 + nal.Length;
        }

        byte[] data = new byte[length];
        int at = 0;
        foreach (byte[] nal in nals)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), nal.Length);
            nal.CopyTo(data, at + 4);
            at += 4 + nal.Length;
        }

        return data;
    }

    private static void ProfileTierLevel(NalBitWriter w, HevcEncoderSettings s)
    {
        (int profile, uint compatibility, ulong constraints) = Profile(s);
        w.U(2, 0); // general_profile_space
        w.Flag(false); // general_tier_flag
        w.U(5, (uint)profile);
        for (int j = 0; j < 32; j++)
        {
            w.Flag((compatibility & (1u << j)) != 0);
        }

        w.U(16, (uint)(constraints >> 32));
        w.U(32, (uint)constraints);
        w.U(8, (uint)Level(s));
    }

    private static void Vui(NalBitWriter w, HevcEncoderSettings s)
    {
        w.Flag(false); // aspect_ratio_info_present_flag
        w.Flag(false); // overscan_info_present_flag
        w.Flag(true); // video_signal_type_present_flag
        w.U(3, 5); // video_format: unspecified
        w.Flag(s.FullRange);
        w.Flag(true); // colour_description_present_flag
        w.U(8, (uint)s.ColourPrimaries);
        w.U(8, (uint)s.TransferCharacteristics);
        w.U(8, (uint)s.MatrixCoefficients);
        w.Flag(false); // chroma_loc_info_present_flag
        w.Flag(false); // neutral_chroma_indication_flag
        w.Flag(false); // field_seq_flag
        w.Flag(false); // frame_field_info_present_flag
        w.Flag(false); // default_display_window_flag
        w.Flag(false); // vui_timing_info_present_flag
        w.Flag(false); // bitstream_restriction_flag
    }

    /// <summary>A NAL unit: its two-byte header (nuh_layer_id 0, TemporalId 0) and the escaped RBSP.</summary>
    private static byte[] Nal(int type, byte[] rbsp) => [(byte)(type << 1), 1, .. NalFraming.Escape(rbsp)];
}
