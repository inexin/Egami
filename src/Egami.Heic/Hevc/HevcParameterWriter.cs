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
        if (s.LevelIdc is int given)
        {
            return given;
        }

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
        w.Ue((uint)s.MaxDecPicBufferingMinus1); // vps_max_dec_pic_buffering_minus1
        w.Ue((uint)s.MaxNumReorderPics); // vps_max_num_reorder_pics
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
        w.Ue((uint)(s.Log2MaxPocLsb - 4)); // log2_max_pic_order_cnt_lsb_minus4
        w.Flag(true); // sps_sub_layer_ordering_info_present_flag
        w.Ue((uint)s.MaxDecPicBufferingMinus1); // sps_max_dec_pic_buffering_minus1
        w.Ue((uint)s.MaxNumReorderPics); // sps_max_num_reorder_pics
        w.Ue(0); // sps_max_latency_increase_plus1
        w.Ue(0); // log2_min_luma_coding_block_size_minus3: 8x8
        w.Ue((uint)(s.CtbLog2Size - 3)); // log2_diff_max_min_luma_coding_block_size
        w.Ue(0); // log2_min_luma_transform_block_size_minus2: 4x4
        w.Ue((uint)(Math.Min(s.CtbLog2Size, 5) - 2)); // log2_diff_max_min_luma_transform_block_size: up to 32x32
        w.Ue((uint)(s.MaxTransformDepthInter ?? s.MaxTransformDepth)); // max_transform_hierarchy_depth_inter
        w.Ue((uint)s.MaxTransformDepth); // max_transform_hierarchy_depth_intra
        w.Flag(false); // scaling_list_enabled_flag
        w.Flag(false); // amp_enabled_flag
        w.Flag(s.Sao && !s.Lossless); // sample_adaptive_offset_enabled_flag
        w.Flag(false); // pcm_enabled_flag
        w.Ue(0); // num_short_term_ref_pic_sets
        w.Flag(false); // long_term_ref_pics_present_flag
        w.Flag(s.TemporalMvp); // sps_temporal_mvp_enabled_flag
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
        w.Se(s.Lossless ? 0 : (s.PpsQp ?? s.Qp) - 26); // init_qp_minus26: a still picture's QP, so slice_qp_delta is 0
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
    /// Writes the slice segment header of the picture's one slice (an RBSP, to byte_alignment()), with the entry points of its
    /// substreams after the first: their sizes in bytes as the NAL unit holds them, emulation prevention included. Without
    /// <paramref name="picture"/> the slice is an IDR picture's I slice, as a still picture has.
    /// </summary>
    internal static byte[] SliceHeader(HevcEncoderSettings s, bool sao, ReadOnlySpan<int> substreamSizes, HevcSliceInfo? picture = null)
    {
        int nalType = picture?.NalType ?? HevcNal.IdrNLp;
        NalBitWriter w = new();
        w.U(1, 0); // forbidden_zero_bit
        w.U(6, (uint)nalType);
        w.U(6, 0); // nuh_layer_id
        w.U(3, 1); // nuh_temporal_id_plus1
        w.Flag(true); // first_slice_segment_in_pic_flag
        if (nalType is >= HevcNal.BlaWLp and <= 23)
        {
            w.Flag(false); // no_output_of_prior_pics_flag
        }

        w.Ue(0); // slice_pic_parameter_set_id
        w.Ue((uint)(picture?.SliceType ?? HevcSliceHeader.I)); // slice_type
        if (picture is not null && nalType is not (HevcNal.IdrWRadl or HevcNal.IdrNLp))
        {
            w.U(s.Log2MaxPocLsb, (uint)(picture.Poc & ((1 << s.Log2MaxPocLsb) - 1))); // slice_pic_order_cnt_lsb
            w.Flag(false); // short_term_ref_pic_set_sps_flag: the set is coded here
            ShortTermRefPicSet(w, picture);
            if (s.TemporalMvp)
            {
                w.Flag(picture.TemporalMvp); // slice_temporal_mvp_enabled_flag
            }
        }

        if (s.Sao && !s.Lossless)
        {
            w.Flag(sao); // slice_sao_luma_flag
            if (s.ChromaFormat != 0)
            {
                w.Flag(sao); // slice_sao_chroma_flag
            }
        }

        if (picture is not null && picture.SliceType != HevcSliceHeader.I)
        {
            // The PPS's default is one reference a list.
            bool b = picture.SliceType == HevcSliceHeader.B;
            bool overridden = picture.NumRefIdxL0 != 1 || (b && picture.NumRefIdxL1 != 1);
            w.Flag(overridden); // num_ref_idx_active_override_flag
            if (overridden)
            {
                w.Ue((uint)(picture.NumRefIdxL0 - 1)); // num_ref_idx_l0_active_minus1
                if (b)
                {
                    w.Ue((uint)(picture.NumRefIdxL1 - 1)); // num_ref_idx_l1_active_minus1
                }
            }

            if (b)
            {
                w.Flag(false); // mvd_l1_zero_flag
            }

            if (picture.TemporalMvp && s.TemporalMvp)
            {
                if (b)
                {
                    w.Flag(picture.CollocatedFromL0); // collocated_from_l0_flag
                }

                if ((picture.CollocatedFromL0 ? picture.NumRefIdxL0 : picture.NumRefIdxL1) > 1)
                {
                    w.Ue((uint)picture.CollocatedRefIdx); // collocated_ref_idx
                }
            }

            w.Ue((uint)(5 - picture.MaxNumMergeCand)); // five_minus_max_num_merge_cand
        }

        w.Se(s.Lossless ? 0 : s.Qp - (s.PpsQp ?? s.Qp)); // slice_qp_delta
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
    internal static byte[] SliceNal(HevcEncoderSettings s, bool sao, IReadOnlyList<byte[]> substreams, HevcSliceInfo? picture = null)
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

        byte[] header = NalFraming.Escape(SliceHeader(s, sao, sizes, picture));
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

    /// <summary>
    /// Writes st_ref_pic_set(num_short_term_ref_pic_sets) of a slice header (7.3.7): with no sets in the SPS it is never predicted
    /// from another, so it lists the pictures before and after the current one in output order as POC differences, each marked
    /// as used by the current picture or only kept for later ones.
    /// </summary>
    private static void ShortTermRefPicSet(NalBitWriter w, HevcSliceInfo picture)
    {
        w.Ue((uint)picture.Before.Length); // num_negative_pics
        w.Ue((uint)picture.After.Length); // num_positive_pics
        int previous = 0;
        foreach ((int delta, bool used) in picture.Before)
        {
            w.Ue((uint)(previous - delta - 1)); // delta_poc_s0_minus1: the differences fall
            w.Flag(used); // used_by_curr_pic_s0_flag
            previous = delta;
        }

        previous = 0;
        foreach ((int delta, bool used) in picture.After)
        {
            w.Ue((uint)(delta - previous - 1)); // delta_poc_s1_minus1: the differences rise
            w.Flag(used); // used_by_curr_pic_s1_flag
            previous = delta;
        }
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
        w.Flag(s.Timing is not null); // vui_timing_info_present_flag
        if (s.Timing is (uint ticks, uint scale))
        {
            w.U(32, ticks); // vui_num_units_in_tick
            w.U(32, scale); // vui_time_scale
            w.Flag(false); // vui_poc_proportional_to_timing_flag
            w.Flag(false); // vui_hrd_parameters_present_flag
        }

        w.Flag(false); // bitstream_restriction_flag
    }

    /// <summary>A NAL unit: its two-byte header (nuh_layer_id 0, TemporalId 0) and the escaped RBSP.</summary>
    private static byte[] Nal(int type, byte[] rbsp) => [(byte)(type << 1), 1, .. NalFraming.Escape(rbsp)];
}

/// <summary>
/// What a video picture's slice header says beyond a still picture's (<see cref="HevcParameterWriter.SliceHeader"/>): its NAL unit
/// type, slice type, picture order count and short-term reference picture set.
/// </summary>
internal sealed record HevcSliceInfo
{
    /// <summary>Gets nal_unit_type: <see cref="HevcNal.IdrNLp"/>, <see cref="HevcNal.TrailR"/> or <see cref="HevcNal.TrailN"/>.</summary>
    internal required int NalType { get; init; }

    /// <summary>Gets slice_type: <see cref="HevcSliceHeader.I"/>, P or B.</summary>
    internal int SliceType { get; init; } = HevcSliceHeader.I;

    /// <summary>Gets the picture order count (its low bits are written).</summary>
    internal int Poc { get; init; }

    /// <summary>
    /// Gets the reference set's pictures before this one in output order: POC differences, negative and falling, and whether
    /// this picture predicts from each (else it is only kept for later pictures).
    /// </summary>
    internal (int Delta, bool Used)[] Before { get; init; } = [];

    /// <summary>Gets the reference set's pictures after this one: POC differences, positive and rising, and whether this picture predicts from each.</summary>
    internal (int Delta, bool Used)[] After { get; init; } = [];

    /// <summary>Gets num_ref_idx_l0_active_minus1 + 1 of a P or B slice.</summary>
    internal int NumRefIdxL0 { get; init; } = 1;

    /// <summary>Gets num_ref_idx_l1_active_minus1 + 1 of a B slice.</summary>
    internal int NumRefIdxL1 { get; init; } = 1;

    /// <summary>Gets slice_temporal_mvp_enabled_flag (written when the SPS enables temporal motion vector prediction).</summary>
    internal bool TemporalMvp { get; init; }

    /// <summary>Gets collocated_from_l0_flag of a B slice (a P slice's collocated picture is always in list 0).</summary>
    internal bool CollocatedFromL0 { get; init; } = true;

    /// <summary>Gets collocated_ref_idx.</summary>
    internal int CollocatedRefIdx { get; init; }

    /// <summary>Gets MaxNumMergeCand, 1 to 5.</summary>
    internal int MaxNumMergeCand { get; init; } = 5;
}
