namespace Egami.Formats.Av1;

/// <summary>The OBU types of the AV1 specification, section 6.2.2.</summary>
internal enum Av1ObuType
{
    SequenceHeader = 1,
    TemporalDelimiter = 2,
    FrameHeader = 3,
    TileGroup = 4,
    Metadata = 5,
    Frame = 6,
    RedundantFrameHeader = 7,
    TileList = 8,
    Padding = 15,
}

/// <summary>An OBU of a low-overhead (Section 5) stream: its header fields and where its payload lies.</summary>
internal readonly record struct Av1Obu(Av1ObuType Type, int TemporalId, int SpatialId, bool HasExtension, int PayloadStart, int PayloadLength)
{
    /// <summary>
    /// Lists the OBUs of a low-overhead bitstream (section 5.2): each with obu_size, except that the last may run to the end.
    /// Throws <see cref="CorruptDataException"/> on a forbidden bit, a broken size or an OBU that runs past the data.
    /// </summary>
    internal static List<Av1Obu> Split(ReadOnlySpan<byte> data, string formatName)
    {
        List<Av1Obu> obus = [];
        int at = 0;
        while (at < data.Length)
        {
            int start = at;
            byte header = data[at++];
            if ((header & 0x80) != 0)
            {
                throw new CorruptDataException(formatName, start, "An AV1 OBU header has its forbidden bit set.");
            }

            Av1ObuType type = (Av1ObuType)((header >> 3) & 15);
            bool extension = (header & 4) != 0;
            bool hasSize = (header & 2) != 0;
            int temporal = 0, spatial = 0;
            if (extension)
            {
                if (at >= data.Length)
                {
                    throw new CorruptDataException(formatName, start, "An AV1 OBU header is cut off.") { IsTruncation = true };
                }

                temporal = data[at] >> 5;
                spatial = (data[at] >> 3) & 3;
                at++;
            }

            long size = data.Length - at;
            if (hasSize)
            {
                (ulong value, int length) = Av1BitReader.Leb128(data[at..]);
                if (length < 0)
                {
                    throw new CorruptDataException(formatName, at, "An AV1 OBU's size is cut off or over 32 bits.") { IsTruncation = true };
                }

                at += length;
                size = (long)value;
            }

            if (size > data.Length - at)
            {
                throw new CorruptDataException(formatName, start, $"An AV1 OBU of {size} bytes runs past the end of the data.") { IsTruncation = true };
            }

            obus.Add(new Av1Obu(type, temporal, spatial, extension, at, (int)size));
            at += (int)size;
        }

        return obus;
    }
}

/// <summary>An operating point of a sequence header: which layers it decodes, and its level and tier.</summary>
internal readonly record struct Av1OperatingPoint(int Idc, int Level, int Tier, bool DecoderModelPresent = false);

/// <summary>
/// The sequence header OBU of the AV1 specification, section 5.5: profile, operating points, frame size bounds, the coding tools
/// the sequence enables, and the color configuration (bit depth, monochrome, subsampling, CICP and range).
/// </summary>
internal sealed record Av1SequenceHeader
{
    /// <summary>The CICP value of an unspecified primary, transfer or matrix.</summary>
    internal const int Unspecified = 2;

    internal int Profile { get; init; }

    internal bool StillPicture { get; init; }

    internal bool ReducedStillPictureHeader { get; init; }

    internal bool TimingInfoPresent { get; init; }

    internal bool DecoderModelInfoPresent { get; init; }

    internal int BufferDelayLength { get; init; }

    internal int BufferRemovalTimeLength { get; init; }

    internal int FramePresentationTimeLength { get; init; }

    internal bool EqualPictureInterval { get; init; }

    internal Av1OperatingPoint[] OperatingPoints { get; init; } = [];

    internal int FrameWidthBits { get; init; }

    internal int FrameHeightBits { get; init; }

    internal int MaxFrameWidth { get; init; }

    internal int MaxFrameHeight { get; init; }

    internal bool FrameIdNumbersPresent { get; init; }

    internal int DeltaFrameIdLength { get; init; }

    internal int AdditionalFrameIdLength { get; init; }

    internal bool Use128x128Superblock { get; init; }

    internal bool EnableFilterIntra { get; init; }

    internal bool EnableIntraEdgeFilter { get; init; }

    internal bool EnableInterintraCompound { get; init; }

    internal bool EnableMaskedCompound { get; init; }

    internal bool EnableWarpedMotion { get; init; }

    internal bool EnableDualFilter { get; init; }

    internal bool EnableOrderHint { get; init; }

    internal bool EnableJntComp { get; init; }

    internal bool EnableRefFrameMvs { get; init; }

    /// <summary>Gets seq_force_screen_content_tools: 0, 1, or 2 to let each frame choose.</summary>
    internal int ForceScreenContentTools { get; init; }

    /// <summary>Gets seq_force_integer_mv: 0, 1, or 2 to let each frame choose.</summary>
    internal int ForceIntegerMv { get; init; }

    internal int OrderHintBits { get; init; }

    internal bool EnableSuperres { get; init; }

    internal bool EnableCdef { get; init; }

    internal bool EnableRestoration { get; init; }

    internal int BitDepth { get; init; }

    internal bool Monochrome { get; init; }

    internal int ColorPrimaries { get; init; } = Unspecified;

    internal int TransferCharacteristics { get; init; } = Unspecified;

    internal int MatrixCoefficients { get; init; } = Unspecified;

    internal bool ColorDescriptionPresent { get; init; }

    internal bool FullRange { get; init; }

    internal int SubsamplingX { get; init; }

    internal int SubsamplingY { get; init; }

    /// <summary>Gets chroma_sample_position: 0 unknown, 1 vertical (left), 2 colocated (top-left).</summary>
    internal int ChromaSamplePosition { get; init; }

    internal bool SeparateUvDeltaQ { get; init; }

    internal bool FilmGrainParamsPresent { get; init; }

    /// <summary>Gets the chroma format the color configuration describes.</summary>
    internal Pixels.ChromaFormat Chroma => Monochrome ? Pixels.ChromaFormat.Yuv400
        : SubsamplingX == 1 && SubsamplingY == 1 ? Pixels.ChromaFormat.Yuv420
        : SubsamplingX == 1 ? Pixels.ChromaFormat.Yuv422 : Pixels.ChromaFormat.Yuv444;

    /// <summary>Lists the header's fields under the specification's names, as <see cref="Av1FrameHeader.Describe"/> does.</summary>
    internal List<(string Name, long Value)> Describe()
    {
        List<(string, long)> f =
        [
            ("seq_profile", Profile),
            ("still_picture", StillPicture ? 1 : 0),
            ("reduced_still_picture_header", ReducedStillPictureHeader ? 1 : 0),
            ("timing_info_present_flag", TimingInfoPresent ? 1 : 0),
            ("decoder_model_info_present_flag", DecoderModelInfoPresent ? 1 : 0),
            ("operating_points_cnt_minus_1", OperatingPoints.Length - 1),
        ];
        for (int i = 0; i < OperatingPoints.Length; i++)
        {
            f.Add(($"operating_point_idc[{i}]", OperatingPoints[i].Idc));
            f.Add(($"seq_level_idx[{i}]", OperatingPoints[i].Level));
            f.Add(($"seq_tier[{i}]", OperatingPoints[i].Tier));
        }

        f.AddRange(
        [
            ("frame_width_bits_minus_1", FrameWidthBits - 1),
            ("frame_height_bits_minus_1", FrameHeightBits - 1),
            ("max_frame_width_minus_1", MaxFrameWidth - 1),
            ("max_frame_height_minus_1", MaxFrameHeight - 1),
            ("frame_id_numbers_present_flag", FrameIdNumbersPresent ? 1 : 0),
            ("use_128x128_superblock", Use128x128Superblock ? 1 : 0),
            ("enable_filter_intra", EnableFilterIntra ? 1 : 0),
            ("enable_intra_edge_filter", EnableIntraEdgeFilter ? 1 : 0),
            ("enable_interintra_compound", EnableInterintraCompound ? 1 : 0),
            ("enable_masked_compound", EnableMaskedCompound ? 1 : 0),
            ("enable_warped_motion", EnableWarpedMotion ? 1 : 0),
            ("enable_dual_filter", EnableDualFilter ? 1 : 0),
            ("enable_order_hint", EnableOrderHint ? 1 : 0),
            ("enable_jnt_comp", EnableJntComp ? 1 : 0),
            ("enable_ref_frame_mvs", EnableRefFrameMvs ? 1 : 0),
            ("seq_force_screen_content_tools", ForceScreenContentTools),
            ("seq_force_integer_mv", ForceIntegerMv),
            ("OrderHintBits", OrderHintBits),
            ("enable_superres", EnableSuperres ? 1 : 0),
            ("enable_cdef", EnableCdef ? 1 : 0),
            ("enable_restoration", EnableRestoration ? 1 : 0),
            ("BitDepth", BitDepth),
            ("mono_chrome", Monochrome ? 1 : 0),
            ("color_description_present_flag", ColorDescriptionPresent ? 1 : 0),
            ("color_primaries", ColorPrimaries),
            ("transfer_characteristics", TransferCharacteristics),
            ("matrix_coefficients", MatrixCoefficients),
            ("color_range", FullRange ? 1 : 0),
            ("subsampling_x", SubsamplingX),
            ("subsampling_y", SubsamplingY),
            ("chroma_sample_position", ChromaSamplePosition),
            ("separate_uv_delta_q", SeparateUvDeltaQ ? 1 : 0),
            ("film_grain_params_present", FilmGrainParamsPresent ? 1 : 0),
        ]);
        return f;
    }

    /// <summary>Finds the first sequence header OBU in a low-overhead stream and reads it, or returns <see langword="null"/>.</summary>
    internal static Av1SequenceHeader? Find(ReadOnlySpan<byte> stream, string formatName)
    {
        foreach (Av1Obu obu in Av1Obu.Split(stream, formatName))
        {
            if (obu.Type == Av1ObuType.SequenceHeader)
            {
                return Parse(stream.Slice(obu.PayloadStart, obu.PayloadLength), formatName);
            }
        }

        return null;
    }

    /// <summary>Reads a sequence header OBU's payload (section 5.5.1).</summary>
    internal static Av1SequenceHeader Parse(ReadOnlySpan<byte> payload, string formatName)
    {
        Av1BitReader r = new(payload, formatName);
        int profile = (int)r.Bits(3);
        if (profile > 2)
        {
            throw new CorruptDataException(formatName, 0, $"The AV1 sequence header's profile is {profile}; only 0 to 2 exist.");
        }

        bool still = r.Flag();
        bool reduced = r.Flag();
        if (reduced && !still)
        {
            throw new CorruptDataException(formatName, 0, "The AV1 sequence header has a reduced still picture header but is not a still picture.");
        }

        bool timing = false, decoderModel = false, equalInterval = false;
        int bufferDelayLength = 0, removalTimeLength = 0, presentationTimeLength = 0;
        Av1OperatingPoint[] points;
        if (reduced)
        {
            points = [new Av1OperatingPoint(0, (int)r.Bits(5), 0)];
        }
        else
        {
            timing = r.Flag();
            if (timing)
            {
                r.Bits(32); // num_units_in_display_tick
                r.Bits(32); // time_scale
                equalInterval = r.Flag();
                if (equalInterval)
                {
                    r.Uvlc(); // num_ticks_per_picture_minus_1
                }

                decoderModel = r.Flag();
                if (decoderModel)
                {
                    bufferDelayLength = (int)r.Bits(5) + 1;
                    r.Bits(32); // num_units_in_decoding_tick
                    removalTimeLength = (int)r.Bits(5) + 1;
                    presentationTimeLength = (int)r.Bits(5) + 1;
                }
            }

            bool initialDisplayDelay = r.Flag();
            points = new Av1OperatingPoint[r.Bits(5) + 1];
            for (int i = 0; i < points.Length; i++)
            {
                int idc = (int)r.Bits(12);
                int level = (int)r.Bits(5);
                int tier = level > 7 ? (int)r.Bits(1) : 0;
                bool modelPresent = decoderModel && r.Flag();
                if (modelPresent)
                {
                    r.Bits(bufferDelayLength); // decoder_buffer_delay
                    r.Bits(bufferDelayLength); // encoder_buffer_delay
                    r.Flag(); // low_delay_mode_flag
                }

                if (initialDisplayDelay && r.Flag())
                {
                    r.Bits(4); // initial_display_delay_minus_1
                }

                points[i] = new Av1OperatingPoint(idc, level, tier, modelPresent);
            }
        }

        int widthBits = (int)r.Bits(4) + 1;
        int heightBits = (int)r.Bits(4) + 1;
        int maxWidth = (int)r.Bits(widthBits) + 1;
        int maxHeight = (int)r.Bits(heightBits) + 1;
        bool frameIds = !reduced && r.Flag();
        int deltaFrameIdLength = 0, additionalFrameIdLength = 0;
        if (frameIds)
        {
            deltaFrameIdLength = (int)r.Bits(4) + 2;
            additionalFrameIdLength = (int)r.Bits(3) + 1;
        }

        bool sb128 = r.Flag();
        bool filterIntra = r.Flag();
        bool intraEdge = r.Flag();
        bool interintra = false, masked = false, warped = false, dualFilter = false, orderHint = false, jntComp = false, refFrameMvs = false;
        int screenContent = 2, integerMv = 2, orderHintBits = 0;
        if (!reduced)
        {
            interintra = r.Flag();
            masked = r.Flag();
            warped = r.Flag();
            dualFilter = r.Flag();
            orderHint = r.Flag();
            if (orderHint)
            {
                jntComp = r.Flag();
                refFrameMvs = r.Flag();
            }

            screenContent = r.Flag() ? 2 : (int)r.Bits(1);
            integerMv = screenContent > 0 ? (r.Flag() ? 2 : (int)r.Bits(1)) : 2;
            if (orderHint)
            {
                orderHintBits = (int)r.Bits(3) + 1;
            }
        }

        bool superres = r.Flag();
        bool cdef = r.Flag();
        bool restoration = r.Flag();

        // color_config (section 5.5.2)
        bool highBitDepth = r.Flag();
        int bitDepth = profile == 2 && highBitDepth ? (r.Flag() ? 12 : 10) : highBitDepth ? 10 : 8;
        bool mono = profile != 1 && r.Flag();
        bool description = r.Flag();
        int primaries = Unspecified, transfer = Unspecified, matrix = Unspecified;
        if (description)
        {
            primaries = (int)r.Bits(8);
            transfer = (int)r.Bits(8);
            matrix = (int)r.Bits(8);
        }

        bool fullRange;
        int ssx, ssy, position = 0;
        bool separateUv = false;
        if (mono)
        {
            fullRange = r.Flag();
            (ssx, ssy) = (1, 1);
        }
        else if (primaries == 1 && transfer == 13 && matrix == 0)
        {
            // sRGB with the identity matrix: full range 4:4:4 (the specification allows it in profile 1 and 12-bit profile 2).
            fullRange = true;
            (ssx, ssy) = (0, 0);
            separateUv = r.Flag();
        }
        else
        {
            fullRange = r.Flag();
            if (profile == 0)
            {
                (ssx, ssy) = (1, 1);
            }
            else if (profile == 1)
            {
                (ssx, ssy) = (0, 0);
            }
            else if (bitDepth == 12)
            {
                ssx = (int)r.Bits(1);
                ssy = ssx == 1 ? (int)r.Bits(1) : 0;
            }
            else
            {
                (ssx, ssy) = (1, 0);
            }

            if (ssx == 1 && ssy == 1)
            {
                position = (int)r.Bits(2);
            }

            separateUv = r.Flag();
        }

        bool filmGrain = r.Flag();
        return new Av1SequenceHeader
        {
            Profile = profile,
            StillPicture = still,
            ReducedStillPictureHeader = reduced,
            TimingInfoPresent = timing,
            DecoderModelInfoPresent = decoderModel,
            BufferDelayLength = bufferDelayLength,
            BufferRemovalTimeLength = removalTimeLength,
            FramePresentationTimeLength = presentationTimeLength,
            EqualPictureInterval = equalInterval,
            OperatingPoints = points,
            FrameWidthBits = widthBits,
            FrameHeightBits = heightBits,
            MaxFrameWidth = maxWidth,
            MaxFrameHeight = maxHeight,
            FrameIdNumbersPresent = frameIds,
            DeltaFrameIdLength = deltaFrameIdLength,
            AdditionalFrameIdLength = additionalFrameIdLength,
            Use128x128Superblock = sb128,
            EnableFilterIntra = filterIntra,
            EnableIntraEdgeFilter = intraEdge,
            EnableInterintraCompound = interintra,
            EnableMaskedCompound = masked,
            EnableWarpedMotion = warped,
            EnableDualFilter = dualFilter,
            EnableOrderHint = orderHint,
            EnableJntComp = jntComp,
            EnableRefFrameMvs = refFrameMvs,
            ForceScreenContentTools = screenContent,
            ForceIntegerMv = integerMv,
            OrderHintBits = orderHintBits,
            EnableSuperres = superres,
            EnableCdef = cdef,
            EnableRestoration = restoration,
            BitDepth = bitDepth,
            Monochrome = mono,
            ColorDescriptionPresent = description,
            ColorPrimaries = primaries,
            TransferCharacteristics = transfer,
            MatrixCoefficients = matrix,
            FullRange = fullRange,
            SubsamplingX = ssx,
            SubsamplingY = ssy,
            ChromaSamplePosition = position,
            SeparateUvDeltaQ = separateUv,
            FilmGrainParamsPresent = filmGrain,
        };
    }
}
