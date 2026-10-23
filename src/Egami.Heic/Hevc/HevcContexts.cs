namespace Egami.Heic.Hevc;

/// <summary>
/// The context variables of an I slice (H.265 9.3.2.2): their indices in one state array and their initValues for initType 0
/// (Tables 9-5 to 9-37), and the StatCoeff variables of persistent Rice adaptation, which are stored and synchronized with them.
/// </summary>
internal static class HevcContexts
{
    internal const int SaoMerge = 0;
    internal const int SaoTypeIdx = SaoMerge + 1;
    internal const int SplitCuFlag = SaoTypeIdx + 1;
    internal const int CuTransquantBypass = SplitCuFlag + 3;
    internal const int PartMode = CuTransquantBypass + 1;
    internal const int PrevIntraLumaPred = PartMode + 1;
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
    internal const int Count = CuChromaQpOffsetIdx + 1;

    /// <summary>The initValues for initType 0, in the order of the indices above.</summary>
    private static readonly byte[] InitValues =
    [
        153, // sao_merge_left_flag and sao_merge_up_flag
        200, // sao_type_idx_luma and sao_type_idx_chroma
        139, 141, 157, // split_cu_flag
        154, // cu_transquant_bypass_flag
        184, // part_mode
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
        111, 111, 125, 110, 110, 94, 124, 108, 124, 107, 125, 141, 179, 153, 125, 107, 125, 141, 179, 153, 125, 107, 125, 141,
        179, 153, 125, 140, 139, 182, 182, 152, 136, 152, 136, 153, 136, 139, 111, 136, 139, 111, 141, 111, // sig_coeff_flag
        140, 92, 137, 138, 140, 152, 138, 139, 153, 74, 149, 92, 139, 107, 122, 152, 140, 179, 166, 182, 140, 227, 122, 197, // greater1
        138, 153, 136, 167, 152, 152, // coeff_abs_level_greater2_flag
        154, 154, 154, 154, 154, 154, 154, 154, // log2_res_scale_abs_plus1
        154, 154, // res_scale_sign_flag
        154, // cu_chroma_qp_offset_flag
        154, // cu_chroma_qp_offset_idx
    ];

    /// <summary>Initializes every context for SliceQpY (9.3.2.2).</summary>
    internal static void Initialize(byte[] states, int sliceQp)
    {
        int qp = Math.Clamp(sliceQp, 0, 51);
        for (int i = 0; i < Count; i++)
        {
            int init = InitValues[i];
            int m = ((init >> 4) * 5) - 45, n = ((init & 15) << 3) - 16;
            int pre = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            int mps = pre <= 63 ? 0 : 1;
            states[i] = (byte)(((mps == 1 ? pre - 64 : 63 - pre) << 1) | mps);
        }
    }

    static HevcContexts()
    {
        if (InitValues.Length != Count)
        {
            throw new InvalidOperationException("HEVC context table size mismatch.");
        }
    }
}
