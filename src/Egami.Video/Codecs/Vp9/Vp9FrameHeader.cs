namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// One VP9 frame's uncompressed header (VP9 bitstream specification section 6.2), as read: the fields that change decoder state
/// (reference slots, loop filter deltas, segmentation data) are kept as their update flags and values, and the decoder applies
/// them. <see cref="Read"/> optionally lists every field under the specification's names for <see cref="CodecHeaders"/>.
/// </summary>
internal sealed class Vp9FrameHeader
{
    internal const int SyncCode = 0x498342;

    public int Profile { get; private set; }

    public bool ShowExistingFrame { get; private set; }

    public int FrameToShowMapIdx { get; private set; }

    /// <summary>Gets whether this is a keyframe (frame_type 0).</summary>
    public bool KeyFrame { get; private set; }

    public bool ShowFrame { get; private set; }

    public bool ErrorResilientMode { get; private set; }

    public bool IntraOnly { get; private set; }

    public int ResetFrameContext { get; private set; }

    /// <summary>Gets whether the frame carries its own color config (keyframes, and intra-only frames above profile 0).</summary>
    public bool HasColorConfig { get; private set; }

    public int BitDepth { get; private set; } = 8;

    public int ColorSpace { get; private set; }

    public int ColorRange { get; private set; }

    public int SubsamplingX { get; private set; } = 1;

    public int SubsamplingY { get; private set; } = 1;

    public int RefreshFrameFlags { get; private set; }

    public int[] RefFrameIdx { get; } = new int[3];

    /// <summary>Gets the sign bias of LAST, GOLDEN and ALTREF, indexed by reference frame (entry 0, intra, is always 0).</summary>
    public int[] RefFrameSignBias { get; } = new int[4];

    /// <summary>Gets which active reference the frame takes its size from, or -1 when the size is coded.</summary>
    public int SizeFromRef { get; private set; } = -1;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int RenderWidth { get; private set; }

    public int RenderHeight { get; private set; }

    public bool AllowHighPrecisionMv { get; private set; }

    public int InterpFilter { get; private set; }

    public bool RefreshFrameContext { get; private set; }

    public bool FrameParallelDecodingMode { get; private set; }

    public int FrameContextIdx { get; private set; }

    public int LoopFilterLevel { get; private set; }

    public int LoopFilterSharpness { get; private set; }

    public bool LoopFilterDeltaEnabled { get; private set; }

    /// <summary>Gets the reference deltas the frame updates: bit i set means <see cref="LoopFilterRefDeltas"/>[i] is new.</summary>
    public int LoopFilterRefDeltaUpdates { get; private set; }

    public int[] LoopFilterRefDeltas { get; } = new int[4];

    public int LoopFilterModeDeltaUpdates { get; private set; }

    public int[] LoopFilterModeDeltas { get; } = new int[2];

    public int BaseQIdx { get; private set; }

    public int DeltaQYDc { get; private set; }

    public int DeltaQUVDc { get; private set; }

    public int DeltaQUVAc { get; private set; }

    public bool Lossless => BaseQIdx == 0 && DeltaQYDc == 0 && DeltaQUVDc == 0 && DeltaQUVAc == 0;

    public bool SegmentationEnabled { get; private set; }

    public bool SegmentationUpdateMap { get; private set; }

    public bool SegmentationTemporalUpdate { get; private set; }

    public bool SegmentationUpdateData { get; private set; }

    public bool SegmentationAbsDelta { get; private set; }

    public byte[] SegmentationTreeProbs { get; } = new byte[7];

    public byte[] SegmentationPredProbs { get; } = new byte[3];

    /// <summary>Gets the segment features the frame sets, bit j of entry i for feature j of segment i (when the data is updated).</summary>
    public int[] FeatureMask { get; } = new int[8];

    public short[] FeatureData { get; } = new short[8 * 4];

    public int TileColsLog2 { get; private set; }

    public int TileRowsLog2 { get; private set; }

    /// <summary>Gets the size of the compressed header in bytes.</summary>
    public int HeaderSizeInBytes { get; private set; }

    /// <summary>Gets the size of the uncompressed header in bytes: the compressed header starts there.</summary>
    public int UncompressedHeaderSize { get; private set; }

    /// <summary>Gets or sets the frame's transform mode, from the compressed header (set by the decoder).</summary>
    public int TxMode { get; set; }

    /// <summary>Gets or sets the frame's reference mode, from the compressed header (set by the decoder).</summary>
    public int ReferenceMode { get; set; }

    /// <summary>Gets whether the frame is a keyframe or intra-only (predicted from nothing).</summary>
    public bool FrameIsIntraOnly => KeyFrame || IntraOnly;

    /// <summary>
    /// Reads the uncompressed header at the start of <paramref name="frame"/>. <paramref name="referenceSize"/> gives the size
    /// of reference slot <c>ref_frame_idx[i]</c> when the frame takes its size from one (found_ref); it may be null when only the
    /// fields are wanted, and the track's size then stands in.
    /// </summary>
    /// <exception cref="CorruptDataException">The header is cut off or breaks the specification's rules.</exception>
    public static Vp9FrameHeader Read(ReadOnlySpan<byte> frame, Func<int, (int Width, int Height)> referenceSize, List<CodecHeaderField>? fields = null)
    {
        Vp9FrameHeader h = new();
        BitReader r = new(frame, fields);
        if (r.Literal("frame_marker", 2) != 2)
        {
            throw new CorruptDataException("VP9", 0, "The frame marker is not 2.");
        }

        int profile = r.Bit("profile_low_bit");
        profile |= r.Bit("profile_high_bit") << 1;
        if (profile == 3)
        {
            profile += r.Bit("reserved_zero");
        }

        if (profile > 3)
        {
            throw new CorruptDataException("VP9", 0, "The frame uses an unknown profile.");
        }

        h.Profile = profile;
        h.ShowExistingFrame = r.Bit("show_existing_frame") != 0;
        if (h.ShowExistingFrame)
        {
            h.FrameToShowMapIdx = r.Literal("frame_to_show_map_idx", 3);
            h.ShowFrame = true;
            h.UncompressedHeaderSize = r.BytesRead;
            return h;
        }

        h.KeyFrame = r.Bit("frame_type") == 0;
        h.ShowFrame = r.Bit("show_frame") != 0;
        h.ErrorResilientMode = r.Bit("error_resilient_mode") != 0;
        if (h.KeyFrame)
        {
            ReadSyncCode(ref r);
            h.ReadColorConfig(ref r);
            h.RefreshFrameFlags = 0xFF;
            h.ReadFrameSize(ref r);
            h.ReadRenderSize(ref r);
        }
        else
        {
            h.IntraOnly = !h.ShowFrame && r.Bit("intra_only") != 0;
            h.ResetFrameContext = h.ErrorResilientMode ? 0 : r.Literal("reset_frame_context", 2);
            if (h.IntraOnly)
            {
                ReadSyncCode(ref r);
                if (profile > 0)
                {
                    h.ReadColorConfig(ref r);
                }
                else
                {
                    // Profile 0 intra-only frames are 8-bit 4:2:0 BT.601 by definition.
                    h.HasColorConfig = true;
                    h.ColorSpace = 1;
                    h.ColorRange = 0;
                }

                h.RefreshFrameFlags = r.Literal("refresh_frame_flags", 8);
                h.ReadFrameSize(ref r);
                h.ReadRenderSize(ref r);
            }
            else
            {
                h.RefreshFrameFlags = r.Literal("refresh_frame_flags", 8);
                for (int i = 0; i < 3; i++)
                {
                    h.RefFrameIdx[i] = r.Literal($"ref_frame_idx[{i}]", 3);
                    h.RefFrameSignBias[Vp9Tables.LastFrame + i] = r.Bit($"ref_frame_sign_bias[{Vp9Tables.LastFrame + i}]");
                }

                for (int i = 0; i < 3; i++)
                {
                    if (r.Bit($"found_ref[{i}]") != 0)
                    {
                        h.SizeFromRef = i;
                        (h.Width, h.Height) = referenceSize(h.RefFrameIdx[i]);
                        break;
                    }
                }

                if (h.SizeFromRef < 0)
                {
                    h.ReadFrameSize(ref r);
                }
                else if (h.Width <= 0 || h.Height <= 0)
                {
                    throw new CorruptDataException("VP9", 0, $"The frame takes its size from reference slot {h.RefFrameIdx[h.SizeFromRef]}, which holds no frame.");
                }

                h.ReadRenderSize(ref r);
                h.AllowHighPrecisionMv = r.Bit("allow_high_precision_mv") != 0;
                h.InterpFilter = r.Bit("is_filter_switchable") != 0
                    ? Vp9Tables.Switchable
                    : LiteralToFilter[r.Literal("raw_interpolation_filter", 2)];
            }
        }

        if (!h.ErrorResilientMode)
        {
            h.RefreshFrameContext = r.Bit("refresh_frame_context") != 0;
            h.FrameParallelDecodingMode = r.Bit("frame_parallel_decoding_mode") != 0;
        }
        else
        {
            h.FrameParallelDecodingMode = true;
        }

        h.FrameContextIdx = r.Literal("frame_context_idx", 2);

        // Loop filter params.
        h.LoopFilterLevel = r.Literal("loop_filter_level", 6);
        h.LoopFilterSharpness = r.Literal("loop_filter_sharpness", 3);
        h.LoopFilterDeltaEnabled = r.Bit("loop_filter_delta_enabled") != 0;
        if (h.LoopFilterDeltaEnabled && r.Bit("loop_filter_delta_update") != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (r.Bit($"update_ref_delta[{i}]") != 0)
                {
                    h.LoopFilterRefDeltaUpdates |= 1 << i;
                    h.LoopFilterRefDeltas[i] = r.SignedLiteral($"loop_filter_ref_deltas[{i}]", 6);
                }
            }

            for (int i = 0; i < 2; i++)
            {
                if (r.Bit($"update_mode_delta[{i}]") != 0)
                {
                    h.LoopFilterModeDeltaUpdates |= 1 << i;
                    h.LoopFilterModeDeltas[i] = r.SignedLiteral($"loop_filter_mode_deltas[{i}]", 6);
                }
            }
        }

        // Quantization params.
        h.BaseQIdx = r.Literal("base_q_idx", 8);
        h.DeltaQYDc = ReadDeltaQ(ref r, "delta_q_y_dc");
        h.DeltaQUVDc = ReadDeltaQ(ref r, "delta_q_uv_dc");
        h.DeltaQUVAc = ReadDeltaQ(ref r, "delta_q_uv_ac");

        // Segmentation params.
        h.SegmentationEnabled = r.Bit("segmentation_enabled") != 0;
        if (h.SegmentationEnabled)
        {
            h.SegmentationUpdateMap = r.Bit("segmentation_update_map") != 0;
            if (h.SegmentationUpdateMap)
            {
                for (int i = 0; i < 7; i++)
                {
                    h.SegmentationTreeProbs[i] = (byte)(r.Bit($"prob_coded[{i}]") != 0 ? r.Literal($"segmentation_tree_probs[{i}]", 8) : 255);
                }

                h.SegmentationTemporalUpdate = r.Bit("segmentation_temporal_update") != 0;
                for (int i = 0; i < 3; i++)
                {
                    h.SegmentationPredProbs[i] = (byte)(h.SegmentationTemporalUpdate && r.Bit($"prob_coded[{i}]") != 0
                        ? r.Literal($"segmentation_pred_prob[{i}]", 8)
                        : 255);
                }
            }

            h.SegmentationUpdateData = r.Bit("segmentation_update_data") != 0;
            if (h.SegmentationUpdateData)
            {
                h.SegmentationAbsDelta = r.Bit("segmentation_abs_or_delta_update") != 0;
                for (int i = 0; i < Vp9Tables.MaxSegments; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        int value = 0;
                        if (r.Bit($"feature_enabled[{i}][{j}]") != 0)
                        {
                            h.FeatureMask[i] |= 1 << j;
                            int max = Vp9Tables.SegFeatureDataMax[j];
                            value = r.Literal($"feature_value[{i}][{j}]", max == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max));
                            value = Math.Min(value, max);
                            if (Vp9Tables.SegFeatureSigned[j] && r.Bit($"feature_sign[{i}][{j}]") != 0)
                            {
                                value = -value;
                            }
                        }

                        h.FeatureData[(i * 4) + j] = (short)value;
                    }
                }
            }
        }

        // Tile info.
        int miCols = (h.Width + 7) >> 3;
        TileLog2Bounds(miCols, out int minLog2, out int maxLog2);
        int cols = minLog2;
        for (int ones = maxLog2 - minLog2; ones > 0 && r.Bit("increment_tile_cols_log2") != 0; ones--)
        {
            cols++;
        }

        if (cols > 6)
        {
            throw new CorruptDataException("VP9", r.BytesRead, $"The frame has 2^{cols} tile columns; at most 64 are allowed.");
        }

        h.TileColsLog2 = cols;
        r.Note("tile_cols_log2", cols);
        int rows = r.Bit("tile_rows_log2");
        if (rows != 0)
        {
            rows += r.Bit("increment_tile_rows_log2");
        }

        h.TileRowsLog2 = rows;
        h.HeaderSizeInBytes = r.Literal("header_size_in_bytes", 16);
        if (h.HeaderSizeInBytes == 0)
        {
            throw new CorruptDataException("VP9", r.BytesRead, "The compressed header size is 0.");
        }

        h.UncompressedHeaderSize = r.BytesRead;
        return h;
    }

    /// <summary>The smallest and largest tile_cols_log2 for a width of <paramref name="miCols"/> 8x8 columns (vp9_get_tile_n_bits).</summary>
    internal static void TileLog2Bounds(int miCols, out int minLog2, out int maxLog2)
    {
        int sb64Cols = (miCols + 7) >> 3;
        minLog2 = 0;
        while ((64 << minLog2) < sb64Cols)
        {
            minLog2++;
        }

        maxLog2 = 1;
        while ((sb64Cols >> maxLog2) >= 4)
        {
            maxLog2++;
        }

        maxLog2--;
    }

    private static ReadOnlySpan<int> LiteralToFilter => [Vp9Tables.EightTapSmooth, Vp9Tables.EightTap, Vp9Tables.EightTapSharp, Vp9Tables.Bilinear];

    private static int ReadDeltaQ(ref BitReader r, string name) => r.Bit($"delta_coded ({name})") != 0 ? r.SignedLiteral(name, 4) : 0;

    private static void ReadSyncCode(ref BitReader r)
    {
        if (r.Literal(null, 24) != SyncCode)
        {
            throw new CorruptDataException("VP9", r.BytesRead, "The frame sync code is not 49 83 42.");
        }
    }

    private void ReadColorConfig(ref BitReader r)
    {
        HasColorConfig = true;
        BitDepth = Profile >= 2 ? (r.Bit("ten_or_twelve_bit") != 0 ? 12 : 10) : 8;
        ColorSpace = r.Literal("color_space", 3);
        if (ColorSpace != 7)
        {
            ColorRange = r.Bit("color_range");
            if (Profile is 1 or 3)
            {
                SubsamplingX = r.Bit("subsampling_x");
                SubsamplingY = r.Bit("subsampling_y");
                if (SubsamplingX == 1 && SubsamplingY == 1)
                {
                    throw new CorruptDataException("VP9", r.BytesRead, "Profiles 1 and 3 do not allow 4:2:0.");
                }

                if (r.Bit("reserved_zero") != 0)
                {
                    throw new CorruptDataException("VP9", r.BytesRead, "A reserved bit is set.");
                }
            }
            else
            {
                SubsamplingX = SubsamplingY = 1;
            }
        }
        else
        {
            ColorRange = 1;
            if (Profile is 1 or 3)
            {
                SubsamplingX = SubsamplingY = 0;
                if (r.Bit("reserved_zero") != 0)
                {
                    throw new CorruptDataException("VP9", r.BytesRead, "A reserved bit is set.");
                }
            }
            else
            {
                throw new CorruptDataException("VP9", r.BytesRead, "Profiles 0 and 2 do not allow RGB (4:4:4).");
            }
        }
    }

    private void ReadFrameSize(ref BitReader r)
    {
        Width = r.Literal("frame_width_minus_1", 16) + 1;
        Height = r.Literal("frame_height_minus_1", 16) + 1;
    }

    private void ReadRenderSize(ref BitReader r)
    {
        RenderWidth = Width;
        RenderHeight = Height;
        if (r.Bit("render_and_frame_size_different") != 0)
        {
            RenderWidth = r.Literal("render_width_minus_1", 16) + 1;
            RenderHeight = r.Literal("render_height_minus_1", 16) + 1;
        }
    }

    /// <summary>Reads the uncompressed header's bits, most significant first, and lists each field when asked.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data, List<CodecHeaderField>? fields)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public readonly int BytesRead => (_bit + 7) >> 3;

        public int Bit(string? name)
        {
            int p = _bit >> 3;
            if (p >= _data.Length)
            {
                throw new CorruptDataException("VP9", p, "The frame header is cut off.");
            }

            int bit = (_data[p] >> (7 - (_bit & 7))) & 1;
            _bit++;
            Note(name, bit);
            return bit;
        }

        public int Literal(string? name, int bits)
        {
            int value = 0;
            for (int i = bits - 1; i >= 0; i--)
            {
                value |= Bit(null) << i;
            }

            Note(name, value);
            return value;
        }

        public int SignedLiteral(string name, int bits)
        {
            int value = Literal(null, bits);
            if (Bit(null) != 0)
            {
                value = -value;
            }

            Note(name, value);
            return value;
        }

        public readonly void Note(string? name, long value)
        {
            if (name is not null)
            {
                fields?.Add(new CodecHeaderField(name, value));
            }
        }
    }
}
