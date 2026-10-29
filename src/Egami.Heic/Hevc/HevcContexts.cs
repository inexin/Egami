namespace Egami.Heic.Hevc;

/// <summary>
/// The context variables (H.265 9.3.2.2): their indices in one state array and their initValues for initTypes 0 to 2
/// (Tables 9-5 to 9-37; HM's ContextTables.h, BSD-3, was compared where the tables' layout was unclear), and the StatCoeff variables of persistent Rice adaptation, which are stored and synchronized with them.
/// </summary>
internal static class HevcContexts
{
    internal const int SaoMerge = 0;
    internal const int SaoTypeIdx = SaoMerge + 1;
    internal const int SplitCuFlag = SaoTypeIdx + 1;
    internal const int CuTransquantBypass = SplitCuFlag + 3;
    internal const int PartMode = CuTransquantBypass + 1;
    internal const int PrevIntraLumaPred = PartMode + 4;
    internal const int IntraChromaPredMode = PrevIntraLumaPred + 1;
    internal const int SplitTransform = IntraChromaPredMode + 1;
    internal const int CbfLuma = SplitTransform + 3;
    internal const int CbfChroma = CbfLuma + 2;
    internal const int CuQpDeltaAbs = CbfChroma + 5;
    internal const int TransformSkip = CuQpDeltaAbs + 2;
    internal const int LastXPrefix = TransformSkip + 2;
    internal const int LastYPrefix = LastXPrefix + 18;
    internal const int CodedSubBlock = LastYPrefix + 18;
    internal const int SigCoeff = CodedSubBlock + 4;
    internal const int Greater1 = SigCoeff + 44;
    internal const int Greater2 = Greater1 + 24;
    internal const int Log2ResScaleAbs = Greater2 + 6;
    internal const int ResScaleSign = Log2ResScaleAbs + 8;
    internal const int CuChromaQpOffsetFlag = ResScaleSign + 2;
    internal const int CuChromaQpOffsetIdx = CuChromaQpOffsetFlag + 1;
    internal const int CuSkipFlag = CuChromaQpOffsetIdx + 1;
    internal const int PredModeFlag = CuSkipFlag + 3;
    internal const int MergeFlag = PredModeFlag + 1;
    internal const int MergeIdx = MergeFlag + 1;
    internal const int InterPredIdc = MergeIdx + 1;
    internal const int RefIdx = InterPredIdc + 5;
    internal const int AbsMvdGreater0 = RefIdx + 2;
    internal const int AbsMvdGreater1 = AbsMvdGreater0 + 1;
    internal const int MvpFlag = AbsMvdGreater1 + 1;
    internal const int RqtRootCbf = MvpFlag + 1;
    internal const int ExplicitRdpcm = RqtRootCbf + 1;
    internal const int ExplicitRdpcmDir = ExplicitRdpcm + 2;
    internal const int Count = ExplicitRdpcmDir + 2;

    /// <summary>The initValues for initType 0, in the order of the indices above.</summary>
    private static readonly byte[] InitType0 =
    [
        153, // sao_merge_left_flag and sao_merge_up_flag
        200, // sao_type_idx_luma and sao_type_idx_chroma
        139, 141, 157, // split_cu_flag
        154, // cu_transquant_bypass_flag
        184, 154, 154, 154, // part_mode
        184, // prev_intra_luma_pred_flag
        63, // intra_chroma_pred_mode
        153, 138, 138, // split_transform_flag
        111, 141, // cbf_luma
        94, 138, 182, 154, 154, // cbf_cb and cbf_cr
        154, 154, // cu_qp_delta_abs
        139, 139, // transform_skip_flag, luma and chroma
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_x_prefix
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_y_prefix
        91, 171, 134, 141, // coded_sub_block_flag
        111, 111, 125, 110, 110, 94, 124, 108, 124, 107, 125, 141, 179, 153, 125, 107, 125, 141, 179, 153, 125, 107, 125, 141, 179, 153, 125, 140, 139, 182, 182, 152, 136, 152, 136, 153, 136, 139, 111, 136, 139, 111, 141, 111, // sig_coeff_flag: 27 luma, 15 chroma, then the transform-skip ones of luma and chroma
        140, 92, 137, 138, 140, 152, 138, 139, 153, 74, 149, 92, 139, 107, 122, 152, 140, 179, 166, 182, 140, 227, 122, 197, // coeff_abs_level_greater1_flag
        138, 153, 136, 167, 152, 152, // coeff_abs_level_greater2_flag
        154, 154, 154, 154, 154, 154, 154, 154, // log2_res_scale_abs_plus1
        154, 154, // res_scale_sign_flag
        154, // cu_chroma_qp_offset_flag
        154, // cu_chroma_qp_offset_idx
        154, 154, 154, // cu_skip_flag
        154, // pred_mode_flag
        154, // merge_flag
        154, // merge_idx
        154, 154, 154, 154, 154, // inter_pred_idc
        154, 154, // ref_idx_l0 and ref_idx_l1
        154, // abs_mvd_greater0_flag
        154, // abs_mvd_greater1_flag
        154, // mvp_l0_flag and mvp_l1_flag
        154, // rqt_root_cbf
        154, 154, // explicit_rdpcm_flag, luma and chroma
        154, 154, // explicit_rdpcm_dir_flag, luma and chroma
    ];

    /// <summary>The initValues for initType 1, in the order of the indices above.</summary>
    private static readonly byte[] InitType1 =
    [
        153, // sao_merge_left_flag and sao_merge_up_flag
        185, // sao_type_idx_luma and sao_type_idx_chroma
        107, 139, 126, // split_cu_flag
        154, // cu_transquant_bypass_flag
        154, 139, 154, 154, // part_mode
        154, // prev_intra_luma_pred_flag
        152, // intra_chroma_pred_mode
        124, 138, 94, // split_transform_flag
        153, 111, // cbf_luma
        149, 107, 167, 154, 154, // cbf_cb and cbf_cr
        154, 154, // cu_qp_delta_abs
        139, 139, // transform_skip_flag, luma and chroma
        125, 110, 94, 110, 95, 79, 125, 111, 110, 78, 110, 111, 111, 95, 94, 108, 123, 108, // last_sig_coeff_x_prefix
        125, 110, 94, 110, 95, 79, 125, 111, 110, 78, 110, 111, 111, 95, 94, 108, 123, 108, // last_sig_coeff_y_prefix
        121, 140, 61, 154, // coded_sub_block_flag
        155, 154, 139, 153, 139, 123, 123, 63, 153, 166, 183, 140, 136, 153, 154, 166, 183, 140, 136, 153, 154, 166, 183, 140, 136, 153, 154, 170, 153, 123, 123, 107, 121, 107, 121, 167, 151, 183, 140, 151, 183, 140, 140, 140, // sig_coeff_flag: 27 luma, 15 chroma, then the transform-skip ones of luma and chroma
        154, 196, 196, 167, 154, 152, 167, 182, 182, 134, 149, 136, 153, 121, 136, 137, 169, 194, 166, 167, 154, 167, 137, 182, // coeff_abs_level_greater1_flag
        107, 167, 91, 122, 107, 167, // coeff_abs_level_greater2_flag
        154, 154, 154, 154, 154, 154, 154, 154, // log2_res_scale_abs_plus1
        154, 154, // res_scale_sign_flag
        154, // cu_chroma_qp_offset_flag
        154, // cu_chroma_qp_offset_idx
        197, 185, 201, // cu_skip_flag
        149, // pred_mode_flag
        110, // merge_flag
        122, // merge_idx
        95, 79, 63, 31, 31, // inter_pred_idc
        153, 153, // ref_idx_l0 and ref_idx_l1
        140, // abs_mvd_greater0_flag
        198, // abs_mvd_greater1_flag
        168, // mvp_l0_flag and mvp_l1_flag
        79, // rqt_root_cbf
        139, 139, // explicit_rdpcm_flag, luma and chroma
        139, 139, // explicit_rdpcm_dir_flag, luma and chroma
    ];

    /// <summary>The initValues for initType 2, in the order of the indices above.</summary>
    private static readonly byte[] InitType2 =
    [
        153, // sao_merge_left_flag and sao_merge_up_flag
        160, // sao_type_idx_luma and sao_type_idx_chroma
        107, 139, 126, // split_cu_flag
        154, // cu_transquant_bypass_flag
        154, 139, 154, 154, // part_mode
        183, // prev_intra_luma_pred_flag
        152, // intra_chroma_pred_mode
        224, 167, 122, // split_transform_flag
        153, 111, // cbf_luma
        149, 92, 167, 154, 154, // cbf_cb and cbf_cr
        154, 154, // cu_qp_delta_abs
        139, 139, // transform_skip_flag, luma and chroma
        125, 110, 124, 110, 95, 94, 125, 111, 111, 79, 125, 126, 111, 111, 79, 108, 123, 93, // last_sig_coeff_x_prefix
        125, 110, 124, 110, 95, 94, 125, 111, 111, 79, 125, 126, 111, 111, 79, 108, 123, 93, // last_sig_coeff_y_prefix
        121, 140, 61, 154, // coded_sub_block_flag
        170, 154, 139, 153, 139, 123, 123, 63, 124, 166, 183, 140, 136, 153, 154, 166, 183, 140, 136, 153, 154, 166, 183, 140, 136, 153, 154, 170, 153, 138, 138, 122, 121, 122, 121, 167, 151, 183, 140, 151, 183, 140, 140, 140, // sig_coeff_flag: 27 luma, 15 chroma, then the transform-skip ones of luma and chroma
        154, 196, 167, 167, 154, 152, 167, 182, 182, 134, 149, 136, 153, 121, 136, 122, 169, 208, 166, 167, 154, 152, 167, 182, // coeff_abs_level_greater1_flag
        107, 167, 91, 107, 107, 167, // coeff_abs_level_greater2_flag
        154, 154, 154, 154, 154, 154, 154, 154, // log2_res_scale_abs_plus1
        154, 154, // res_scale_sign_flag
        154, // cu_chroma_qp_offset_flag
        154, // cu_chroma_qp_offset_idx
        197, 185, 201, // cu_skip_flag
        134, // pred_mode_flag
        154, // merge_flag
        137, // merge_idx
        95, 79, 63, 31, 31, // inter_pred_idc
        153, 153, // ref_idx_l0 and ref_idx_l1
        169, // abs_mvd_greater0_flag
        198, // abs_mvd_greater1_flag
        168, // mvp_l0_flag and mvp_l1_flag
        79, // rqt_root_cbf
        139, 139, // explicit_rdpcm_flag, luma and chroma
        139, 139, // explicit_rdpcm_dir_flag, luma and chroma
    ];

    /// <summary>Initializes every context for SliceQpY and initType (9.3.2.2): 0 for I slices, 1 and 2 for P and B slices.</summary>
    internal static void Initialize(byte[] states, int sliceQp, int initType = 0)
    {
        int qp = Math.Clamp(sliceQp, 0, 51);
        byte[] values = initType == 0 ? InitType0 : initType == 1 ? InitType1 : InitType2;
        for (int i = 0; i < Count; i++)
        {
            int init = values[i];
            int m = ((init >> 4) * 5) - 45, n = ((init & 15) << 3) - 16;
            int pre = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            int mps = pre <= 63 ? 0 : 1;
            states[i] = (byte)(((mps == 1 ? pre - 64 : 63 - pre) << 1) | mps);
        }
    }

    static HevcContexts()
    {
        if (InitType0.Length != Count || InitType1.Length != Count || InitType2.Length != Count)
        {
            throw new InvalidOperationException("HEVC context table size mismatch.");
        }
    }
}
