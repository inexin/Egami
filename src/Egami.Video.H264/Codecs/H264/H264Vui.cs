using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>The H.264 VUI (E.1.1): the fields shared with HEVC, then timing, HRD and bitstream restrictions.</summary>
internal sealed class H264Vui : NalVui
{
    internal uint NumUnitsInTick { get; private set; }

    internal uint TimeScale { get; private set; }

    internal bool FixedFrameRate { get; private set; }

    internal bool PicStructPresent { get; private set; }

    internal int CpbRemovalDelayLength { get; private set; } = 24;

    internal int DpbOutputDelayLength { get; private set; } = 24;

    internal int TimeOffsetLength { get; private set; } = 24;

    internal bool HrdPresent { get; private set; }

    internal bool BitstreamRestriction { get; private set; }

    internal int MaxNumReorderFrames { get; private set; }

    internal int MaxDecFrameBuffering { get; private set; }

    /// <summary>Gets the frame rate the timing info gives (two ticks a frame, E.2.1), or null when it gives none.</summary>
    internal Rational? FrameRate => NumUnitsInTick > 0 && TimeScale > 0 ? new Rational(TimeScale, 2L * NumUnitsInTick) : null;

    internal static H264Vui Read(NalBitReader r)
    {
        H264Vui v = new();
        v.ReadCommon(r);
        try
        {
            if (r.Flag("timing_info_present_flag"))
            {
                v.NumUnitsInTick = r.U(32, "num_units_in_tick");
                v.TimeScale = r.U(32, "time_scale");
                v.FixedFrameRate = r.Flag("fixed_frame_rate_flag");
            }

            bool nal = r.Flag("nal_hrd_parameters_present_flag");
            if (nal)
            {
                v.ReadHrd(r);
            }

            bool vcl = r.Flag("vcl_hrd_parameters_present_flag");
            if (vcl)
            {
                v.ReadHrd(r);
            }

            v.HrdPresent = nal || vcl;
            if (v.HrdPresent)
            {
                r.Flag("low_delay_hrd_flag");
            }

            v.PicStructPresent = r.Flag("pic_struct_present_flag");
            if (r.Flag("bitstream_restriction_flag"))
            {
                r.Flag("motion_vectors_over_pic_boundaries_flag");
                r.Ue("max_bytes_per_pic_denom");
                r.Ue("max_bits_per_mb_denom");
                r.Ue("log2_max_mv_length_horizontal");
                r.Ue("log2_max_mv_length_vertical");
                int reorder = (int)r.Ue("max_num_reorder_frames", 16);
                int buffering = (int)r.Ue("max_dec_frame_buffering", 16);
                v.MaxNumReorderFrames = Math.Min(reorder, buffering);
                v.MaxDecFrameBuffering = buffering;
                v.BitstreamRestriction = true;
            }
        }
        catch (CorruptDataException)
        {
            // Some encoders cut the VUI short; what was read before the end stays.
        }

        return v;
    }

    private void ReadHrd(NalBitReader r)
    {
        int count = (int)r.Ue("cpb_cnt_minus1", 31) + 1;
        r.U(4, "bit_rate_scale");
        r.U(4, "cpb_size_scale");
        for (int i = 0; i < count; i++)
        {
            r.Ue("bit_rate_value_minus1");
            r.Ue("cpb_size_value_minus1");
            r.Flag("cbr_flag");
        }

        r.U(5, "initial_cpb_removal_delay_length_minus1");
        CpbRemovalDelayLength = (int)r.U(5, "cpb_removal_delay_length_minus1") + 1;
        DpbOutputDelayLength = (int)r.U(5, "dpb_output_delay_length_minus1") + 1;
        TimeOffsetLength = (int)r.U(5, "time_offset_length");
    }
}
