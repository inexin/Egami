namespace Egami.Nal;

/// <summary>
/// The start of vui_parameters() that H.264 (E.1.1) and HEVC (H.265 E.2.1) share: the sample aspect ratio, overscan, video
/// signal type with its colour description, and chroma sample location. Each codec's VUI derives from this and reads its own
/// fields after <see cref="ReadCommon"/>.
/// </summary>
internal abstract class NalVui
{
    /// <summary>The sample aspect ratios of aspect_ratio_idc 1 to 16 (H.264 Table E-1, H.265 Table E-1), width then height.</summary>
    private static readonly byte[] SampleAspectRatios =
    [
        1, 1, 12, 11, 10, 11, 16, 11, 40, 33, 24, 11, 20, 11, 32, 11, 80, 33, 18, 11, 15, 11, 64, 33, 160, 99, 4, 3, 3, 2, 2, 1,
    ];

    /// <summary>Gets the sample aspect ratio's width, or 0 when unspecified (absent, idc 0, a reserved idc or a zero term).</summary>
    internal int SarWidth { get; private set; }

    /// <summary>Gets the sample aspect ratio's height, or 0 when unspecified.</summary>
    internal int SarHeight { get; private set; }

    /// <summary>Gets video_format (5, unspecified, when absent).</summary>
    internal int VideoFormat { get; private set; } = 5;

    /// <summary>Gets video_full_range_flag (false when absent).</summary>
    internal bool FullRange { get; private set; }

    /// <summary>Gets colour_primaries (2, unspecified, when absent).</summary>
    internal int ColourPrimaries { get; private set; } = 2;

    /// <summary>Gets transfer_characteristics (2 when absent).</summary>
    internal int TransferCharacteristics { get; private set; } = 2;

    /// <summary>Gets matrix_coeffs (2 when absent).</summary>
    internal int MatrixCoefficients { get; private set; } = 2;

    /// <summary>Gets chroma_sample_loc_type_top_field, or -1 when absent.</summary>
    internal int ChromaSampleLocation { get; private set; } = -1;

    /// <summary>Gets chroma_sample_loc_type_bottom_field, or -1 when absent.</summary>
    internal int ChromaSampleLocationBottom { get; private set; } = -1;

    /// <summary>Reads the shared fields, from aspect_ratio_info_present_flag to chroma_sample_loc_type_bottom_field.</summary>
    protected void ReadCommon(NalBitReader r)
    {
        if (r.Flag("aspect_ratio_info_present_flag"))
        {
            int idc = (int)r.U(8, "aspect_ratio_idc");
            if (idc == 255)
            {
                SetSampleAspectRatio((int)r.U(16, "sar_width"), (int)r.U(16, "sar_height"));
            }
            else if (idc is >= 1 and <= 16)
            {
                SetSampleAspectRatio(SampleAspectRatios[(idc - 1) * 2], SampleAspectRatios[((idc - 1) * 2) + 1]);
            }
        }

        if (r.Flag("overscan_info_present_flag"))
        {
            r.Flag("overscan_appropriate_flag");
        }

        if (r.Flag("video_signal_type_present_flag"))
        {
            VideoFormat = (int)r.U(3, "video_format");
            FullRange = r.Flag("video_full_range_flag");
            if (r.Flag("colour_description_present_flag"))
            {
                ColourPrimaries = (int)r.U(8, "colour_primaries");
                TransferCharacteristics = (int)r.U(8, "transfer_characteristics");
                MatrixCoefficients = (int)r.U(8, "matrix_coeffs");
            }
        }

        if (r.Flag("chroma_loc_info_present_flag"))
        {
            ChromaSampleLocation = (int)r.Ue("chroma_sample_loc_type_top_field", 5);
            ChromaSampleLocationBottom = (int)r.Ue("chroma_sample_loc_type_bottom_field", 5);
        }
    }

    private void SetSampleAspectRatio(int width, int height)
    {
        // A zero term leaves the ratio unspecified (E.2.1 asks for both to be non-zero when either is).
        if (width > 0 && height > 0)
        {
            SarWidth = width;
            SarHeight = height;
        }
    }
}
