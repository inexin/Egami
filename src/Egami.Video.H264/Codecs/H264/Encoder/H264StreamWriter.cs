using Egami.Colors;
using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>What the encoder's parameter sets say about the stream.</summary>
internal sealed record H264StreamConfig
{
    /// <summary>Gets the picture width in samples.</summary>
    public required int Width { get; init; }

    /// <summary>Gets the picture height in samples.</summary>
    public required int Height { get; init; }

    /// <summary>Gets profile_idc: 66 (Constrained Baseline), 77 (Main) or 100 (High).</summary>
    public required int ProfileIdc { get; init; }

    /// <summary>Gets level_idc (ten times the level; 9 for level 1b is not used).</summary>
    public required int LevelIdc { get; init; }

    /// <summary>Gets whether slices are coded with CABAC.</summary>
    public required bool Cabac { get; init; }

    /// <summary>Gets whether the picture parameter set allows the 8x8 transform.</summary>
    public bool Transform8x8 { get; init; }

    /// <summary>Gets max_num_ref_frames.</summary>
    public int MaxRefFrames { get; init; } = 1;

    /// <summary>Gets whether P slices carry explicit weights (weighted_pred_flag): every profile but Constrained Baseline.</summary>
    public bool WeightedPred => ProfileIdc != 66;

    /// <summary>Gets max_num_reorder_frames: how many frames come before one they are shown after.</summary>
    public int ReorderFrames { get; init; }

    /// <summary>Gets the frame rate, written as VUI timing.</summary>
    public required Rational FrameRate { get; init; }

    /// <summary>Gets the color code points, written in the VUI.</summary>
    public required ColorCodePoints Color { get; init; }

    /// <summary>Gets the slices of each picture: bands of whole macroblock rows, as even as they divide.</summary>
    public int Slices { get; init; } = 1;

    /// <summary>Gets how many slices are coded at once; the stream is the same for any number.</summary>
    public int Parallelism { get; init; } = 1;

    /// <summary>Gets the width in macroblocks.</summary>
    public int WidthMbs => (Width + 15) >> 4;

    /// <summary>Gets the height in macroblocks.</summary>
    public int HeightMbs => (Height + 15) >> 4;

    /// <summary>Gets max_dec_frame_buffering: the frames a decoder holds.</summary>
    public int DecFrameBuffering => Math.Max(MaxRefFrames, ReorderFrames);
}

/// <summary>
/// Writes the encoder's NAL units (7.3): sequence and picture parameter sets with the VUI, SEI of HDR metadata, slice headers,
/// and the avcC record (ISO/IEC 14496-15 5.3.3.1) or Annex B framing that carries them.
/// </summary>
internal static class H264StreamWriter
{
    /// <summary>log2_max_frame_num_minus4 + 4.</summary>
    internal const int FrameNumBits = 8;

    /// <summary>log2_max_pic_order_cnt_lsb_minus4 + 4.</summary>
    internal const int PocBits = 8;

    /// <summary>pic_init_qp_minus26 + 26: the QP slice_qp_delta counts from.</summary>
    internal const int InitQp = 26;

    /// <summary>The constraint flags byte: Constrained Baseline sets constraint_set0 and 1, Main constraint_set1.</summary>
    internal static int ConstraintFlags(int profileIdc) => profileIdc switch { 66 => 0xC0, 77 => 0x40, _ => 0 };

    /// <summary>Makes a NAL unit: the header byte and the escaped RBSP.</summary>
    internal static byte[] Nal(int refIdc, int type, NalBitWriter rbsp)
    {
        byte[] escaped = NalFraming.Escape(rbsp.ToArray());
        byte[] nal = new byte[escaped.Length + 1];
        nal[0] = (byte)((refIdc << 5) | type);
        escaped.CopyTo(nal, 1);
        return nal;
    }

    /// <summary>seq_parameter_set_rbsp (7.3.2.1.1) with vui_parameters (E.1.1).</summary>
    internal static byte[] Sps(H264StreamConfig c)
    {
        NalBitWriter w = new(64);
        w.U(8, (uint)c.ProfileIdc);
        w.U(8, (uint)ConstraintFlags(c.ProfileIdc));
        w.U(8, (uint)c.LevelIdc);
        w.Ue(0);
        if (c.ProfileIdc == 100)
        {
            w.Ue(1); // chroma_format_idc 4:2:0
            w.Ue(0); // bit_depth_luma_minus8
            w.Ue(0); // bit_depth_chroma_minus8
            w.Flag(false); // qpprime_y_zero_transform_bypass_flag
            w.Flag(false); // seq_scaling_matrix_present_flag
        }

        w.Ue(FrameNumBits - 4);
        w.Ue(0); // pic_order_cnt_type
        w.Ue(PocBits - 4);
        w.Ue((uint)c.MaxRefFrames);
        w.Flag(false); // gaps_in_frame_num_value_allowed_flag
        w.Ue((uint)(c.WidthMbs - 1));
        w.Ue((uint)(c.HeightMbs - 1));
        w.Flag(true); // frame_mbs_only_flag
        w.Flag(true); // direct_8x8_inference_flag
        int cropRight = (c.WidthMbs * 16) - c.Width, cropBottom = (c.HeightMbs * 16) - c.Height;
        w.Flag(cropRight != 0 || cropBottom != 0);
        if (cropRight != 0 || cropBottom != 0)
        {
            // 4:2:0 crops in units of two samples.
            w.Ue(0);
            w.Ue((uint)(cropRight / 2));
            w.Ue(0);
            w.Ue((uint)(cropBottom / 2));
        }

        w.Flag(true); // vui_parameters_present_flag
        Vui(w, c);
        w.TrailingBits();
        return Nal(3, 7, w);
    }

    private static void Vui(NalBitWriter w, H264StreamConfig c)
    {
        w.Flag(false); // aspect_ratio_info_present_flag
        w.Flag(false); // overscan_info_present_flag
        w.Flag(true); // video_signal_type_present_flag
        w.U(3, 5); // video_format: unspecified
        w.Flag(c.Color.FullRange);
        w.Flag(true); // colour_description_present_flag
        w.U(8, c.Color.Primaries);
        w.U(8, c.Color.TransferCharacteristics);
        w.U(8, c.Color.MatrixCoefficients);
        w.Flag(false); // chroma_loc_info_present_flag
        w.Flag(true); // timing_info_present_flag

        // A tick is half a frame (E.2.1): time_scale counts ticks of two per frame.
        w.U(32, (uint)c.FrameRate.Denominator);
        w.U(32, (uint)(2L * c.FrameRate.Numerator));
        w.Flag(true); // fixed_frame_rate_flag
        w.Flag(false); // nal_hrd_parameters_present_flag
        w.Flag(false); // vcl_hrd_parameters_present_flag
        w.Flag(false); // pic_struct_present_flag
        w.Flag(true); // bitstream_restriction_flag
        w.Flag(true); // motion_vectors_over_pic_boundaries_flag
        w.Ue(0); // max_bytes_per_pic_denom
        w.Ue(0); // max_bits_per_mb_denom
        w.Ue(15); // log2_max_mv_length_horizontal
        w.Ue(15); // log2_max_mv_length_vertical
        w.Ue((uint)c.ReorderFrames);
        w.Ue((uint)c.DecFrameBuffering);
    }

    /// <summary>pic_parameter_set_rbsp (7.3.2.2).</summary>
    internal static byte[] Pps(H264StreamConfig c)
    {
        NalBitWriter w = new(16);
        w.Ue(0); // pic_parameter_set_id
        w.Ue(0); // seq_parameter_set_id
        w.Flag(c.Cabac);
        w.Flag(false); // bottom_field_pic_order_in_frame_present_flag
        w.Ue(0); // num_slice_groups_minus1
        w.Ue(0); // num_ref_idx_l0_default_active_minus1
        w.Ue(0); // num_ref_idx_l1_default_active_minus1
        w.Flag(c.WeightedPred);
        w.U(2, 0); // weighted_bipred_idc
        w.Se(InitQp - 26);
        w.Se(0); // pic_init_qs_minus26
        w.Se(0); // chroma_qp_index_offset
        w.Flag(true); // deblocking_filter_control_present_flag
        w.Flag(false); // constrained_intra_pred_flag
        w.Flag(false); // redundant_pic_cnt_present_flag
        if (c.ProfileIdc == 100)
        {
            w.Flag(c.Transform8x8);
            w.Flag(false); // pic_scaling_matrix_present_flag
            w.Se(0); // second_chroma_qp_index_offset
        }

        w.TrailingBits();
        return Nal(3, 8, w);
    }

    /// <summary>An SEI NAL unit of mastering display colour volume (137) and content light level (144), or null without either.</summary>
    internal static byte[]? HdrSei(HdrMetadata? hdr)
    {
        if (hdr is null)
        {
            return null;
        }

        NalBitWriter w = new(64);
        bool any = false;
        if (hdr.MasteringPrimaries is { } p && hdr.MasteringMaxLuminance is { } max)
        {
            Payload(w, 137, 24);
            foreach (Chromaticity xy in (ReadOnlySpan<Chromaticity>)[p.Green, p.Blue, p.Red, p.White])
            {
                w.U(16, Units(xy.X, 0.00002, 50000));
                w.U(16, Units(xy.Y, 0.00002, 50000));
            }

            w.U(32, Units(max, 0.0001, uint.MaxValue));
            w.U(32, Units(hdr.MasteringMinLuminance ?? 0, 0.0001, uint.MaxValue));
            any = true;
        }

        if (hdr.MaxContentLightLevel is not null || hdr.MaxFrameAverageLightLevel is not null)
        {
            Payload(w, 144, 4);
            w.U(16, Units(hdr.MaxContentLightLevel ?? 0, 1, 65535));
            w.U(16, Units(hdr.MaxFrameAverageLightLevel ?? 0, 1, 65535));
            any = true;
        }

        if (!any)
        {
            return null;
        }

        w.TrailingBits();
        return Nal(0, 6, w);
    }

    private static void Payload(NalBitWriter w, int type, int size)
    {
        w.U(8, (uint)type);
        w.U(8, (uint)size);
    }

    private static uint Units(double value, double unit, double max) => (uint)Math.Clamp(Math.Round(value / unit), 0, max);

    /// <summary>
    /// Writes slice_header (7.3.3) of a frame slice: <paramref name="type"/> is coded with 5 added (every slice of the picture
    /// has it); <paramref name="refs"/> are list 0's active references and <paramref name="refs1"/> counts list 1's. A
    /// reference picture's marking keeps it as a short-term reference: an IDR picture's, by the sliding window, or, with
    /// <paramref name="unmark"/>, by marking those pictures (difference_of_pic_nums_minus1 each) unused first.
    /// </summary>
    internal static void SliceHeader(NalBitWriter w, H264StreamConfig c, int firstMb, H264SliceType type, bool idr, int idrPicId, int frameNum, int pocLsb, int qp,
        int cabacInitIdc = 0, ReadOnlySpan<H264RefPlanes> refs = default, int refs1 = 1, bool directSpatial = true, bool referenced = true, ReadOnlySpan<int> unmark = default)
    {
        w.Ue((uint)firstMb);
        w.Ue((uint)type + 5);
        w.Ue(0); // pic_parameter_set_id
        w.U(FrameNumBits, (uint)(frameNum & ((1 << FrameNumBits) - 1)));
        if (idr)
        {
            w.Ue((uint)idrPicId);
        }

        w.U(PocBits, (uint)(pocLsb & ((1 << PocBits) - 1)));
        if (type == H264SliceType.B)
        {
            w.Flag(directSpatial);
        }

        if (type is H264SliceType.P or H264SliceType.B)
        {
            // num_ref_idx_active_override_flag: the picture parameter set's default is one reference in each list.
            bool b = type == H264SliceType.B;
            bool active = refs.Length > 1 || (b && refs1 > 1);
            w.Flag(active);
            if (active)
            {
                w.Ue((uint)(refs.Length - 1));
                if (b)
                {
                    w.Ue((uint)(refs1 - 1));
                }
            }

            w.Flag(false); // ref_pic_list_modification_flag_l0
            if (type == H264SliceType.B)
            {
                w.Flag(false); // ref_pic_list_modification_flag_l1
            }
        }

        if (type == H264SliceType.P && c.WeightedPred)
        {
            PredWeightTable(w, refs);
        }

        if (idr)
        {
            w.Flag(false); // no_output_of_prior_pics_flag
            w.Flag(false); // long_term_reference_flag
        }
        else if (referenced)
        {
            w.Flag(!unmark.IsEmpty); // adaptive_ref_pic_marking_mode_flag
            foreach (int difference in unmark)
            {
                w.Ue(1); // memory_management_control_operation: a short-term picture unused
                w.Ue((uint)difference);
            }

            if (!unmark.IsEmpty)
            {
                w.Ue(0);
            }
        }

        if (c.Cabac && type != H264SliceType.I)
        {
            w.Ue((uint)cabacInitIdc);
        }

        w.Se(qp - InitQp);
        w.Ue(0); // disable_deblocking_filter_idc
        w.Se(0); // slice_alpha_c0_offset_div2
        w.Se(0); // slice_beta_offset_div2
    }

    /// <summary>pred_weight_table (7.3.3.2) of list 0: each reference's luma and chroma weights where they are not the default.</summary>
    private static void PredWeightTable(NalBitWriter w, ReadOnlySpan<H264RefPlanes> refs)
    {
        w.Ue((uint)H264RefPlanes.Denominator); // luma_log2_weight_denom
        w.Ue((uint)H264RefPlanes.Denominator); // chroma_log2_weight_denom
        (int, int) unit = (1 << H264RefPlanes.Denominator, 0);
        foreach (H264RefPlanes r in refs)
        {
            bool luma = r.Weighted && r.LumaWeight != unit;
            w.Flag(luma);
            if (luma)
            {
                w.Se(r.LumaWeight.Weight);
                w.Se(r.LumaWeight.Offset);
            }

            bool chroma = r.Weighted && (r.CbWeight != unit || r.CrWeight != unit);
            w.Flag(chroma);
            if (chroma)
            {
                w.Se(r.CbWeight.Weight);
                w.Se(r.CbWeight.Offset);
                w.Se(r.CrWeight.Weight);
                w.Se(r.CrWeight.Offset);
            }
        }
    }

    /// <summary>The AVCDecoderConfigurationRecord of a stream's parameter sets.</summary>
    internal static byte[] AvcC(H264StreamConfig c, byte[] sps, byte[] pps)
    {
        List<byte> b =
        [
            1, (byte)c.ProfileIdc, (byte)ConstraintFlags(c.ProfileIdc), (byte)c.LevelIdc, 0xFF, 0xE1,
            (byte)(sps.Length >> 8), (byte)sps.Length,
        ];
        b.AddRange(sps);
        b.Add(1);
        b.Add((byte)(pps.Length >> 8));
        b.Add((byte)pps.Length);
        b.AddRange(pps);
        if (c.ProfileIdc is 100 or 110 or 122 or 144)
        {
            b.Add(0xFC | 1); // chroma_format 4:2:0
            b.Add(0xF8); // bit_depth_luma_minus8
            b.Add(0xF8); // bit_depth_chroma_minus8
            b.Add(0); // numOfSequenceParameterSetExt
        }

        return [.. b];
    }

    /// <summary>Joins NAL units into an access unit: each after a 4-byte size, or after a start code for Annex B.</summary>
    internal static byte[] AccessUnit(IReadOnlyList<byte[]> nals, bool annexB)
    {
        int size = 0;
        foreach (byte[] nal in nals)
        {
            size += nal.Length + 4;
        }

        byte[] data = new byte[size];
        int at = 0;
        foreach (byte[] nal in nals)
        {
            if (annexB)
            {
                data[at + 3] = 1;
            }
            else
            {
                data[at] = (byte)(nal.Length >> 24);
                data[at + 1] = (byte)(nal.Length >> 16);
                data[at + 2] = (byte)(nal.Length >> 8);
                data[at + 3] = (byte)nal.Length;
            }

            nal.CopyTo(data, at + 4);
            at += nal.Length + 4;
        }

        return data;
    }
}
