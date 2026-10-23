using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>A picture parameter set (H.264 7.3.2.2).</summary>
internal sealed class H264Pps
{
    internal int Id { get; private set; }

    internal int SpsId { get; private set; }

    /// <summary>Gets the SPS the PPS was read with: its scaling lists and transform syntax depend on it.</summary>
    internal H264Sps Sps { get; private set; } = null!;

    internal bool Cabac { get; private set; }

    internal bool BottomFieldPicOrderInFramePresent { get; private set; }

    internal int NumRefIdxL0Default { get; private set; }

    internal int NumRefIdxL1Default { get; private set; }

    internal bool WeightedPred { get; private set; }

    internal int WeightedBipredIdc { get; private set; }

    internal int PicInitQp { get; private set; }

    internal int ChromaQpIndexOffset { get; private set; }

    internal int SecondChromaQpIndexOffset { get; private set; }

    internal bool DeblockingFilterControlPresent { get; private set; }

    internal bool ConstrainedIntraPred { get; private set; }

    internal bool RedundantPicCntPresent { get; private set; }

    internal bool Transform8x8Mode { get; private set; }

    /// <summary>Gets the scaling lists in force for pictures that use this PPS: its own, else the SPS's, else flat.</summary>
    internal H264ScalingLists ScalingLists { get; private set; } = H264ScalingLists.Flat;

    /// <summary>Gets the dequantisation tables for <see cref="ScalingLists"/>, built when first needed.</summary>
    internal H264Dequant Dequant => field ??= new H264Dequant(ScalingLists);

    internal byte[] Rbsp { get; private set; } = [];

    /// <summary>Reads only pps_id and sps_id, so the decoder can find the SPS the rest depends on.</summary>
    internal static (int PpsId, int SpsId) ReadIds(byte[] rbsp)
    {
        NalBitReader r = new(rbsp, 1);
        int pps = (int)r.Ue("pic_parameter_set_id", 255);
        int sps = (int)r.Ue("seq_parameter_set_id", 31);
        return (pps, sps);
    }

    internal static H264Pps Read(byte[] rbsp, H264Sps sps)
    {
        NalBitReader r = new(rbsp, 1);
        H264Pps p = new() { Rbsp = rbsp, Sps = sps };
        p.Id = (int)r.Ue("pic_parameter_set_id", 255);
        p.SpsId = (int)r.Ue("seq_parameter_set_id", 31);
        p.Cabac = r.Flag("entropy_coding_mode_flag");
        p.BottomFieldPicOrderInFramePresent = r.Flag("bottom_field_pic_order_in_frame_present_flag");
        if (r.Ue("num_slice_groups_minus1", 7) > 0)
        {
            throw H264Errors.Unsupported("The stream uses slice groups (FMO).");
        }

        p.NumRefIdxL0Default = (int)r.Ue("num_ref_idx_l0_default_active_minus1", 31) + 1;
        p.NumRefIdxL1Default = (int)r.Ue("num_ref_idx_l1_default_active_minus1", 31) + 1;
        p.WeightedPred = r.Flag("weighted_pred_flag");
        p.WeightedBipredIdc = (int)r.U(2, "weighted_bipred_idc");
        if (p.WeightedBipredIdc == 3)
        {
            throw H264Errors.Corrupt("weighted_bipred_idc is 3.");
        }

        int qpBdOffset = 6 * (sps.BitDepthLuma - 8);
        p.PicInitQp = r.Se("pic_init_qp_minus26", -(26 + qpBdOffset), 25) + 26;
        r.Se("pic_init_qs_minus26", -26, 25);
        p.ChromaQpIndexOffset = r.Se("chroma_qp_index_offset", -12, 12);
        p.SecondChromaQpIndexOffset = p.ChromaQpIndexOffset;
        p.DeblockingFilterControlPresent = r.Flag("deblocking_filter_control_present_flag");
        p.ConstrainedIntraPred = r.Flag("constrained_intra_pred_flag");
        p.RedundantPicCntPresent = r.Flag("redundant_pic_cnt_present_flag");
        p.ScalingLists = sps.ScalingLists ?? H264ScalingLists.Flat;
        if (r.MoreRbspData())
        {
            p.Transform8x8Mode = r.Flag("transform_8x8_mode_flag");
            if (r.Flag("pic_scaling_matrix_present_flag"))
            {
                int count = 6 + ((sps.ChromaFormatIdc != 3 ? 2 : 6) * (p.Transform8x8Mode ? 1 : 0));
                p.ScalingLists = H264ScalingLists.Read(r, count, sps.ScalingLists, sps.ScalingLists is null);
            }

            p.SecondChromaQpIndexOffset = r.Se("second_chroma_qp_index_offset", -12, 12);
        }

        return p;
    }
}
