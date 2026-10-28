using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>A sequence parameter set (H.264 7.3.2.1.1), with the values the decoder derives from it.</summary>
internal sealed class H264Sps
{
    /// <summary>The largest picture side in macroblocks the decoder accepts (level 6.2 allows 8192 / 16 * 2 for one side).</summary>
    internal const int MaxSideInMbs = 1024;

    internal int ProfileIdc { get; private set; }

    internal int ConstraintFlags { get; private set; }

    internal int LevelIdc { get; private set; }

    internal int Id { get; private set; }

    /// <summary>Gets chroma_format_idc: 0 monochrome, 1 4:2:0, 2 4:2:2 (3, 4:4:4, is refused).</summary>
    internal int ChromaFormatIdc { get; private set; } = 1;

    internal int BitDepthLuma { get; private set; } = 8;

    internal int BitDepthChroma { get; private set; } = 8;

    /// <summary>Gets the scaling lists the SPS gives (fall-back rule A applied), or null when it gives none (Flat_4x4/8x8).</summary>
    internal H264ScalingLists? ScalingLists { get; private set; }

    internal int Log2MaxFrameNum { get; private set; }

    internal int PocType { get; private set; }

    internal int Log2MaxPocLsb { get; private set; }

    internal bool DeltaPicOrderAlwaysZero { get; private set; }

    internal int OffsetForNonRefPic { get; private set; }

    internal int OffsetForTopToBottomField { get; private set; }

    internal int[] OffsetForRefFrame { get; private set; } = [];

    internal int MaxNumRefFrames { get; private set; }

    internal bool GapsInFrameNumAllowed { get; private set; }

    internal int WidthInMbs { get; private set; }

    internal int HeightInMbs { get; private set; }

    internal bool FrameMbsOnly { get; private set; }

    /// <summary>Gets mb_adaptive_frame_field_flag: frame pictures may mix frame and field macroblock pairs (MBAFF).</summary>
    internal bool MbAdaptiveFrameField { get; private set; }

    internal bool Direct8x8Inference { get; private set; }

    internal int CropLeft { get; private set; }

    internal int CropRight { get; private set; }

    internal int CropTop { get; private set; }

    internal int CropBottom { get; private set; }

    internal H264Vui? Vui { get; private set; }

    /// <summary>Gets the raw bytes the SPS was read from, to tell a repeated SPS from a changed one.</summary>
    internal byte[] Rbsp { get; private set; } = [];

    internal int ChromaArrayType => ChromaFormatIdc;

    internal int SubWidthC => ChromaFormatIdc == 3 ? 1 : 2;

    internal int SubHeightC => ChromaFormatIdc == 1 ? 2 : 1;

    /// <summary>Gets the chroma block's width in samples (MbWidthC), 0 for monochrome.</summary>
    internal int MbWidthC => ChromaFormatIdc == 0 ? 0 : 16 / SubWidthC;

    /// <summary>Gets the chroma block's height in samples (MbHeightC), 0 for monochrome.</summary>
    internal int MbHeightC => ChromaFormatIdc == 0 ? 0 : 16 / SubHeightC;

    internal int Width => WidthInMbs * 16;

    internal int Height => HeightInMbs * 16;

    internal int CroppedWidth => Width - ((CropLeft + CropRight) * CropUnitX);

    internal int CroppedHeight => Height - ((CropTop + CropBottom) * CropUnitY);

    internal int CropUnitX => ChromaFormatIdc == 0 ? 1 : SubWidthC;

    internal int CropUnitY => (ChromaFormatIdc == 0 ? 1 : SubHeightC) * (FrameMbsOnly ? 1 : 2);

    internal int MaxFrameNum => 1 << Log2MaxFrameNum;

    /// <summary>Gets the DPB size in frames: the VUI's max_dec_frame_buffering, else the level's MaxDpbMbs (A.3.1 h).</summary>
    internal int DpbFrames
    {
        get
        {
            int fromLevel = Math.Clamp(MaxDpbMbs(LevelIdc, (ConstraintFlags & 0x10) != 0) / (WidthInMbs * HeightInMbs), 1, 16);
            int frames = Vui is { BitstreamRestriction: true } v ? v.MaxDecFrameBuffering : fromLevel;
            return Math.Clamp(Math.Max(frames, MaxNumRefFrames), 1, 16);
        }
    }

    /// <summary>Gets max_num_reorder_frames, or -1 when the VUI does not limit it.</summary>
    internal int MaxNumReorderFrames
    {
        get
        {
            if (Vui is { BitstreamRestriction: true } v)
            {
                return v.MaxNumReorderFrames;
            }

            // Intra-only profiles and the Baseline family never reorder (A.2.1, A.2.8 to A.2.11 when constraint_set3 is set).
            bool intraOnly = (ProfileIdc is 44 or 66 or 83 or 88) || ((ProfileIdc is 100 or 110 or 122 or 244) && (ConstraintFlags & 0x10) != 0 && ProfileIdc != 100);
            return intraOnly ? 0 : -1;
        }
    }

    internal static H264Sps Read(byte[] rbsp, SafetyLimits limits)
    {
        NalBitReader r = new(rbsp, 1);
        H264Sps s = new() { Rbsp = rbsp };
        s.ProfileIdc = (int)r.U(8, "profile_idc");
        s.ConstraintFlags = (int)r.U(8, "constraint_set_flags");
        s.LevelIdc = (int)r.U(8, "level_idc");
        s.Id = (int)r.Ue("seq_parameter_set_id", 31);
        if (s.ProfileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            s.ChromaFormatIdc = (int)r.Ue("chroma_format_idc", 3);
            if (s.ChromaFormatIdc == 3)
            {
                if (r.Flag("separate_colour_plane_flag"))
                {
                    throw H264Errors.Unsupported("The stream codes its colour planes separately (4:4:4).");
                }

                throw H264Errors.Unsupported("The stream is 4:4:4.");
            }

            s.BitDepthLuma = (int)r.Ue("bit_depth_luma_minus8", 6) + 8;
            s.BitDepthChroma = (int)r.Ue("bit_depth_chroma_minus8", 6) + 8;
            if (s.BitDepthLuma > 10 || s.BitDepthChroma > 10)
            {
                throw H264Errors.Unsupported($"The stream has {Math.Max(s.BitDepthLuma, s.BitDepthChroma)} bits per sample.");
            }

            if (r.Flag("qpprime_y_zero_transform_bypass_flag"))
            {
                throw H264Errors.Unsupported("The stream allows lossless macroblocks.");
            }

            if (r.Flag("seq_scaling_matrix_present_flag"))
            {
                s.ScalingLists = H264ScalingLists.Read(r, s.ChromaFormatIdc == 3 ? 12 : 8, null, true);
            }
        }

        s.Log2MaxFrameNum = (int)r.Ue("log2_max_frame_num_minus4", 12) + 4;
        s.PocType = (int)r.Ue("pic_order_cnt_type", 2);
        if (s.PocType == 0)
        {
            s.Log2MaxPocLsb = (int)r.Ue("log2_max_pic_order_cnt_lsb_minus4", 12) + 4;
        }
        else if (s.PocType == 1)
        {
            s.DeltaPicOrderAlwaysZero = r.Flag("delta_pic_order_always_zero_flag");
            s.OffsetForNonRefPic = r.Se("offset_for_non_ref_pic", -(1 << 30), (1 << 30) - 1);
            s.OffsetForTopToBottomField = r.Se("offset_for_top_to_bottom_field", -(1 << 30), (1 << 30) - 1);
            int cycle = (int)r.Ue("num_ref_frames_in_pic_order_cnt_cycle", 255);
            s.OffsetForRefFrame = new int[cycle];
            for (int i = 0; i < cycle; i++)
            {
                s.OffsetForRefFrame[i] = r.Se("offset_for_ref_frame", -(1 << 30), (1 << 30) - 1);
            }
        }

        s.MaxNumRefFrames = (int)r.Ue("max_num_ref_frames", 16);
        s.GapsInFrameNumAllowed = r.Flag("gaps_in_frame_num_value_allowed_flag");
        s.WidthInMbs = (int)r.Ue("pic_width_in_mbs_minus1", MaxSideInMbs - 1) + 1;
        int mapUnits = (int)r.Ue("pic_height_in_map_units_minus1", MaxSideInMbs - 1) + 1;
        s.FrameMbsOnly = r.Flag("frame_mbs_only_flag");
        if (!s.FrameMbsOnly)
        {
            s.MbAdaptiveFrameField = r.Flag("mb_adaptive_frame_field_flag");
        }

        // A map unit is a macroblock pair when fields may be coded (7-18).
        s.HeightInMbs = s.FrameMbsOnly ? mapUnits : mapUnits * 2;
        if (s.HeightInMbs > MaxSideInMbs)
        {
            throw H264Errors.Corrupt($"The picture is {s.HeightInMbs} macroblocks high.");
        }

        s.Direct8x8Inference = r.Flag("direct_8x8_inference_flag");
        if (r.Flag("frame_cropping_flag"))
        {
            s.CropLeft = (int)r.Ue("frame_crop_left_offset", 8192);
            s.CropRight = (int)r.Ue("frame_crop_right_offset", 8192);
            s.CropTop = (int)r.Ue("frame_crop_top_offset", 8192);
            s.CropBottom = (int)r.Ue("frame_crop_bottom_offset", 8192);
            if (s.CroppedWidth <= 0 || s.CroppedHeight <= 0)
            {
                throw H264Errors.Corrupt($"The cropping window leaves no picture of {s.Width}x{s.Height}.");
            }
        }

        if (r.Flag("vui_parameters_present_flag"))
        {
            s.Vui = H264Vui.Read(r);
        }

        CheckSize(limits, s.Width, s.Height);
        return s;
    }

    /// <summary>Refuses a picture larger than the Egami context's safety limits allow.</summary>
    internal static void CheckSize(SafetyLimits limits, int width, int height)
    {
        if (width > limits.MaxWidth)
        {
            throw new LimitExceededException(nameof(limits.MaxWidth), limits.MaxWidth, width);
        }

        if (height > limits.MaxHeight)
        {
            throw new LimitExceededException(nameof(limits.MaxHeight), limits.MaxHeight, height);
        }

        if ((long)width * height > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(limits.MaxPixels), limits.MaxPixels, (long)width * height);
        }
    }

    /// <summary>MaxDpbMbs of Table A-1 for a level_idc (level 1b is 11 with constraint_set3 in Baseline and Main).</summary>
    private static int MaxDpbMbs(int level, bool set3) => level switch
    {
        9 => 396,
        10 => 396,
        11 => set3 ? 396 : 900,
        12 or 13 or 20 => 2376,
        21 => 4752,
        22 or 30 => 8100,
        31 => 18000,
        32 => 20480,
        40 or 41 => 32768,
        42 => 34816,
        50 => 110400,
        51 or 52 => 184320,
        _ => 696320,
    };
}
