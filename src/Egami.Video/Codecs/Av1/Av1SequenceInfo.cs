using Egami.Colors;
using Egami.Pixels;

namespace Egami.Video.Codecs.Av1;

/// <summary>
/// What Egami.Video needs from an AV1 sequence header OBU (section 5.5): the profile, the first operating point's level and tier,
/// the color configuration, and the fields the start of a frame header depends on. <see cref="Parse"/> can also list every coded
/// field under the specification's names.
/// </summary>
internal sealed record Av1SequenceInfo
{
    public int Profile { get; init; }

    public bool ReducedStillPictureHeader { get; init; }

    public bool DecoderModelInfoPresent { get; init; }

    public bool EqualPictureInterval { get; init; }

    public int FramePresentationTimeLength { get; init; }

    public bool FrameIdNumbersPresent { get; init; }

    /// <summary>Gets idLen of the frame header: additional_frame_id_length_minus_1 + delta_frame_id_length_minus_2 + 3.</summary>
    public int FrameIdLength { get; init; }

    public int Level { get; init; }

    public int Tier { get; init; }

    public int MaxFrameWidth { get; init; }

    public int MaxFrameHeight { get; init; }

    public bool HighBitDepth { get; init; }

    public bool TwelveBit { get; init; }

    public int BitDepth => TwelveBit ? 12 : HighBitDepth ? 10 : 8;

    public bool Monochrome { get; init; }

    public int SubsamplingX { get; init; }

    public int SubsamplingY { get; init; }

    /// <summary>Gets chroma_sample_position: 0 unknown, 1 vertical (left), 2 colocated (top-left).</summary>
    public int ChromaSamplePosition { get; init; }

    public int ColorPrimaries { get; init; } = 2;

    public int TransferCharacteristics { get; init; } = 2;

    public int MatrixCoefficients { get; init; } = 2;

    public bool FullRange { get; init; }

    /// <summary>Gets the timing info's frame rate (time_scale / num_units_in_display_tick / ticks per picture), or null when not coded.</summary>
    public Rational? FrameRate { get; init; }

    public ChromaFormat Chroma => Monochrome ? ChromaFormat.Yuv400
        : SubsamplingX == 1 && SubsamplingY == 1 ? ChromaFormat.Yuv420
        : SubsamplingX == 1 ? ChromaFormat.Yuv422 : ChromaFormat.Yuv444;

    public ColorCodePoints Color => new((byte)ColorPrimaries, (byte)TransferCharacteristics, (byte)MatrixCoefficients, FullRange);

    /// <summary>Gets where chroma sits, or null when the header does not say.</summary>
    public ChromaSiting? Siting => ChromaSamplePosition switch
    {
        1 => ChromaSiting.Left,
        2 => ChromaSiting.TopLeft,
        _ => null,
    };

    /// <summary>Reads the first sequence header OBU of low-overhead data, or returns null when there is none.</summary>
    public static Av1SequenceInfo? Find(ReadOnlySpan<byte> obus)
    {
        foreach (Av1Obu obu in Av1Obus.Split(obus))
        {
            if (obu.Type == Av1Obus.SequenceHeader)
            {
                return Parse(obus.Slice(obu.PayloadStart, obu.PayloadLength));
            }
        }

        return null;
    }

    /// <summary>Reads a sequence header OBU's payload, listing its coded fields into <paramref name="fields"/> when given.</summary>
    public static Av1SequenceInfo Parse(ReadOnlySpan<byte> payload, List<CodecHeaderField>? fields = null)
    {
        Av1BitReader r = new(payload, fields);
        int profile = r.Int(3, "seq_profile");
        if (profile > 2)
        {
            throw new CorruptDataException(Av1Obus.FormatName, 0, $"The AV1 sequence header's profile is {profile}; only 0 to 2 exist.");
        }

        r.Flag("still_picture");
        bool reduced = r.Flag("reduced_still_picture_header");
        bool decoderModel = false, equalInterval = false;
        int bufferDelayLength = 0, presentationTimeLength = 0, level, tier = 0;
        Rational? rate = null;
        if (reduced)
        {
            level = r.Int(5, "seq_level_idx[0]");
        }
        else
        {
            bool timing = r.Flag("timing_info_present_flag");
            if (timing)
            {
                long unitsPerTick = r.Bits(32, "num_units_in_display_tick");
                long timeScale = r.Bits(32, "time_scale");
                equalInterval = r.Flag("equal_picture_interval");
                long ticksPerPicture = 1;
                if (equalInterval)
                {
                    ticksPerPicture = r.Uvlc("num_ticks_per_picture_minus_1") + 1;
                }

                if (unitsPerTick > 0 && timeScale > 0 && unitsPerTick * ticksPerPicture <= int.MaxValue && timeScale <= int.MaxValue)
                {
                    rate = new Rational(timeScale, unitsPerTick * ticksPerPicture);
                }

                decoderModel = r.Flag("decoder_model_info_present_flag");
                if (decoderModel)
                {
                    bufferDelayLength = r.Int(5, "buffer_delay_length_minus_1") + 1;
                    r.Bits(32, "num_units_in_decoding_tick");
                    r.Int(5, "buffer_removal_time_length_minus_1");
                    presentationTimeLength = r.Int(5, "frame_presentation_time_length_minus_1") + 1;
                }
            }

            bool initialDisplayDelay = r.Flag("initial_display_delay_present_flag");
            int count = r.Int(5, "operating_points_cnt_minus_1") + 1;
            level = 0;
            for (int i = 0; i < count; i++)
            {
                r.Int(12, $"operating_point_idc[{i}]");
                int l = r.Int(5, $"seq_level_idx[{i}]");
                int t = l > 7 ? r.Int(1, $"seq_tier[{i}]") : 0;
                if (i == 0)
                {
                    (level, tier) = (l, t);
                }

                if (decoderModel && r.Flag($"decoder_model_present_for_this_op[{i}]"))
                {
                    r.Bits(bufferDelayLength, $"decoder_buffer_delay[{i}]");
                    r.Bits(bufferDelayLength, $"encoder_buffer_delay[{i}]");
                    r.Flag($"low_delay_mode_flag[{i}]");
                }

                if (initialDisplayDelay && r.Flag($"initial_display_delay_present_for_this_op[{i}]"))
                {
                    r.Int(4, $"initial_display_delay_minus_1[{i}]");
                }
            }
        }

        int widthBits = r.Int(4, "frame_width_bits_minus_1") + 1;
        int heightBits = r.Int(4, "frame_height_bits_minus_1") + 1;
        int maxWidth = r.Int(widthBits, "max_frame_width_minus_1") + 1;
        int maxHeight = r.Int(heightBits, "max_frame_height_minus_1") + 1;
        bool frameIds = !reduced && r.Flag("frame_id_numbers_present_flag");
        int idLength = 0;
        if (frameIds)
        {
            int delta = r.Int(4, "delta_frame_id_length_minus_2");
            int additional = r.Int(3, "additional_frame_id_length_minus_1");
            idLength = additional + delta + 3;
        }

        r.Flag("use_128x128_superblock");
        r.Flag("enable_filter_intra");
        r.Flag("enable_intra_edge_filter");
        if (!reduced)
        {
            r.Flag("enable_interintra_compound");
            r.Flag("enable_masked_compound");
            r.Flag("enable_warped_motion");
            r.Flag("enable_dual_filter");
            bool orderHint = r.Flag("enable_order_hint");
            if (orderHint)
            {
                r.Flag("enable_jnt_comp");
                r.Flag("enable_ref_frame_mvs");
            }

            int screenContent = r.Flag("seq_choose_screen_content_tools") ? 2 : r.Int(1, "seq_force_screen_content_tools");
            if (screenContent > 0 && !r.Flag("seq_choose_integer_mv"))
            {
                r.Int(1, "seq_force_integer_mv");
            }

            if (orderHint)
            {
                r.Int(3, "order_hint_bits_minus_1");
            }
        }

        r.Flag("enable_superres");
        r.Flag("enable_cdef");
        r.Flag("enable_restoration");

        // color_config (section 5.5.2)
        bool highBitDepth = r.Flag("high_bitdepth");
        bool twelveBit = profile == 2 && highBitDepth && r.Flag("twelve_bit");
        bool mono = profile != 1 && r.Flag("mono_chrome");
        int primaries = 2, transfer = 2, matrix = 2;
        if (r.Flag("color_description_present_flag"))
        {
            primaries = r.Int(8, "color_primaries");
            transfer = r.Int(8, "transfer_characteristics");
            matrix = r.Int(8, "matrix_coefficients");
        }

        bool fullRange;
        int ssx, ssy, position = 0;
        if (mono)
        {
            fullRange = r.Flag("color_range");
            (ssx, ssy) = (1, 1);
        }
        else if (primaries == 1 && transfer == 13 && matrix == 0)
        {
            fullRange = true;
            (ssx, ssy) = (0, 0);
            r.Flag("separate_uv_delta_q");
        }
        else
        {
            fullRange = r.Flag("color_range");
            if (profile == 0)
            {
                (ssx, ssy) = (1, 1);
            }
            else if (profile == 1)
            {
                (ssx, ssy) = (0, 0);
            }
            else if (twelveBit)
            {
                ssx = r.Int(1, "subsampling_x");
                ssy = ssx == 1 ? r.Int(1, "subsampling_y") : 0;
            }
            else
            {
                (ssx, ssy) = (1, 0);
            }

            if (ssx == 1 && ssy == 1)
            {
                position = r.Int(2, "chroma_sample_position");
            }

            r.Flag("separate_uv_delta_q");
        }

        r.Flag("film_grain_params_present");
        return new Av1SequenceInfo
        {
            Profile = profile,
            ReducedStillPictureHeader = reduced,
            DecoderModelInfoPresent = decoderModel,
            EqualPictureInterval = equalInterval,
            FramePresentationTimeLength = presentationTimeLength,
            FrameIdNumbersPresent = frameIds,
            FrameIdLength = idLength,
            Level = level,
            Tier = tier,
            MaxFrameWidth = maxWidth,
            MaxFrameHeight = maxHeight,
            HighBitDepth = highBitDepth,
            TwelveBit = twelveBit,
            Monochrome = mono,
            SubsamplingX = ssx,
            SubsamplingY = ssy,
            ChromaSamplePosition = position,
            ColorPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = fullRange,
            FrameRate = rate,
        };
    }
}
