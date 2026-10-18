namespace Egami.Formats.Av1;

/// <summary>The frame types of the AV1 specification, section 6.8.2.</summary>
internal enum Av1FrameType
{
    Key = 0,
    Inter = 1,
    IntraOnly = 2,
    Switch = 3,
}

/// <summary>The transform modes (TxMode) of section 6.8.21.</summary>
internal enum Av1TxMode
{
    Only4x4 = 0,
    Largest = 1,
    Select = 2,
}

/// <summary>A loop restoration type as lr_type codes it (section 6.10.15): none, switchable, Wiener or self-guided.</summary>
internal enum Av1RestorationType
{
    None = 0,
    Switchable = 1,
    Wiener = 2,
    SelfGuided = 3,
}

/// <summary>A frame's film grain parameters (section 5.9.30), with the AR coefficients and offsets as coded.</summary>
internal sealed record Av1FilmGrain
{
    internal bool ApplyGrain { get; init; }

    internal bool UpdateGrain { get; init; }

    internal int GrainSeed { get; init; }

    internal (int Value, int Scaling)[] PointsY { get; init; } = [];

    internal bool ChromaScalingFromLuma { get; init; }

    internal (int Value, int Scaling)[] PointsCb { get; init; } = [];

    internal (int Value, int Scaling)[] PointsCr { get; init; } = [];

    internal int GrainScalingMinus8 { get; init; }

    internal int ArCoeffLag { get; init; }

    internal int[] ArCoeffsYPlus128 { get; init; } = [];

    internal int[] ArCoeffsCbPlus128 { get; init; } = [];

    internal int[] ArCoeffsCrPlus128 { get; init; } = [];

    internal int ArCoeffShiftMinus6 { get; init; }

    internal int GrainScaleShift { get; init; }

    internal int CbMult { get; init; }

    internal int CbLumaMult { get; init; }

    internal int CbOffset { get; init; }

    internal int CrMult { get; init; }

    internal int CrLumaMult { get; init; }

    internal int CrOffset { get; init; }

    internal bool OverlapFlag { get; init; }

    internal bool ClipToRestrictedRange { get; init; }
}

/// <summary>
/// The uncompressed header of a key or intra-only frame (AV1 specification sections 5.9 and 7.20 to 7.21), with the variables
/// it derives: frame and superres size, tiles, quantizer, segmentation, delta q and loop filter, lossless state, loop filter,
/// CDEF, loop restoration, transform mode and film grain. Inter and switch frames need the reference state of the inter
/// decoder (sub-step 2.9.5); reading one throws <see cref="UnsupportedFormatException"/>.
/// </summary>
internal sealed class Av1FrameHeader
{
    /// <summary>The value of primary_ref_frame that means none.</summary>
    internal const int PrimaryRefNone = 7;

    private static readonly int[] FeatureBits = [8, 6, 6, 6, 6, 3, 0, 0];
    private static readonly bool[] FeatureSigned = [true, true, true, true, true, false, false, false];
    private static readonly int[] FeatureMax = [255, 63, 63, 63, 63, 7, 0, 0];

    internal Av1SequenceHeader Sequence { get; private init; } = null!;

    internal bool ShowExistingFrame { get; private set; }

    internal int FrameToShowMapIdx { get; private set; }

    internal Av1FrameType FrameType { get; private set; }

    internal bool FrameIsIntra => FrameType is Av1FrameType.Key or Av1FrameType.IntraOnly;

    internal bool ShowFrame { get; private set; }

    internal bool ShowableFrame { get; private set; }

    internal bool ErrorResilientMode { get; private set; }

    internal bool DisableCdfUpdate { get; private set; }

    internal bool AllowScreenContentTools { get; private set; }

    internal bool ForceIntegerMv { get; private set; }

    internal int CurrentFrameId { get; private set; }

    internal bool FrameSizeOverrideFlag { get; private set; }

    internal int OrderHint { get; private set; }

    internal int PrimaryRefFrame { get; private set; }

    internal int RefreshFrameFlags { get; private set; }

    internal int FrameWidth { get; private set; }

    internal int FrameHeight { get; private set; }

    internal int UpscaledWidth { get; private set; }

    internal int RenderWidth { get; private set; }

    internal int RenderHeight { get; private set; }

    internal bool UseSuperres { get; private set; }

    internal int SuperresDenom { get; private set; } = 8;

    internal int MiCols { get; private set; }

    internal int MiRows { get; private set; }

    internal bool AllowIntrabc { get; private set; }

    internal bool DisableFrameEndUpdateCdf { get; private set; }

    // tile_info
    internal bool UniformTileSpacing { get; private set; }

    internal int TileColsLog2 { get; private set; }

    internal int TileRowsLog2 { get; private set; }

    internal int TileCols { get; private set; }

    internal int TileRows { get; private set; }

    /// <summary>Gets where each tile column starts, in mode info units, with MiCols at the end.</summary>
    internal int[] MiColStarts { get; private set; } = [];

    /// <summary>Gets where each tile row starts, in mode info units, with MiRows at the end.</summary>
    internal int[] MiRowStarts { get; private set; } = [];

    internal int ContextUpdateTileId { get; private set; }

    internal int TileSizeBytes { get; private set; }

    // quantization_params
    internal int BaseQIdx { get; private set; }

    internal int DeltaQYDc { get; private set; }

    internal int DeltaQUDc { get; private set; }

    internal int DeltaQUAc { get; private set; }

    internal int DeltaQVDc { get; private set; }

    internal int DeltaQVAc { get; private set; }

    internal bool UsingQmatrix { get; private set; }

    internal int QmY { get; private set; } = 15;

    internal int QmU { get; private set; } = 15;

    internal int QmV { get; private set; } = 15;

    // segmentation_params
    internal bool SegmentationEnabled { get; private set; }

    internal bool SegmentationUpdateMap { get; private set; }

    internal bool SegmentationTemporalUpdate { get; private set; }

    internal bool SegmentationUpdateData { get; private set; }

    /// <summary>Gets FeatureEnabled[segment][feature].</summary>
    internal bool[,] FeatureEnabled { get; } = new bool[8, 8];

    /// <summary>Gets FeatureData[segment][feature], clipped.</summary>
    internal int[,] FeatureData { get; } = new int[8, 8];

    internal bool SegIdPreSkip { get; private set; }

    internal int LastActiveSegId { get; private set; }

    // delta_q_params and delta_lf_params
    internal bool DeltaQPresent { get; private set; }

    internal int DeltaQRes { get; private set; }

    internal bool DeltaLfPresent { get; private set; }

    internal int DeltaLfRes { get; private set; }

    internal bool DeltaLfMulti { get; private set; }

    /// <summary>Gets LosslessArray[segment].</summary>
    internal bool[] LosslessArray { get; } = new bool[8];

    /// <summary>Gets each segment's qindex, ignoring delta q.</summary>
    internal int[] SegQIdx { get; } = new int[8];

    internal bool CodedLossless { get; private set; }

    internal bool AllLossless { get; private set; }

    // loop_filter_params
    internal int[] LoopFilterLevel { get; } = new int[4];

    internal int LoopFilterSharpness { get; private set; }

    internal bool LoopFilterDeltaEnabled { get; private set; }

    internal bool LoopFilterDeltaUpdate { get; private set; }

    internal int[] LoopFilterRefDeltas { get; } = [1, 0, 0, 0, -1, 0, -1, -1];

    internal int[] LoopFilterModeDeltas { get; } = new int[2];

    // cdef_params
    internal int CdefDamping { get; private set; } = 3;

    internal int CdefBits { get; private set; }

    internal int[] CdefYPriStrength { get; } = new int[8];

    internal int[] CdefYSecStrength { get; } = new int[8];

    internal int[] CdefUvPriStrength { get; } = new int[8];

    internal int[] CdefUvSecStrength { get; } = new int[8];

    // lr_params
    internal Av1RestorationType[] LrType { get; } = new Av1RestorationType[3];

    internal bool UsesLr { get; private set; }

    /// <summary>Gets LoopRestorationSize for luma and for chroma, in samples.</summary>
    internal int[] LoopRestorationSize { get; } = [256, 256, 256];

    internal Av1TxMode TxMode { get; private set; }

    internal bool ReferenceSelect { get; private set; }

    internal bool SkipModePresent { get; private set; }

    internal bool AllowWarpedMotion { get; private set; }

    internal bool ReducedTxSet { get; private set; }

    internal Av1FilmGrain FilmGrain { get; private set; } = new();

    /// <summary>Gets how many bits of the OBU payload the header took (before byte alignment).</summary>
    internal int BitLength { get; private set; }

    /// <summary>Gets the number of planes: 1 for monochrome, else 3.</summary>
    internal int NumPlanes => Sequence.Monochrome ? 1 : 3;

    /// <summary>Reads a frame header OBU's payload, or the header that starts a frame OBU's.</summary>
    /// <param name="payload">The OBU payload.</param>
    /// <param name="sequence">The active sequence header.</param>
    /// <param name="temporalId">The OBU's temporal_id.</param>
    /// <param name="spatialId">The OBU's spatial_id.</param>
    /// <param name="formatName">The format named in errors.</param>
    internal static Av1FrameHeader Parse(ReadOnlySpan<byte> payload, Av1SequenceHeader sequence, int temporalId, int spatialId, string formatName)
    {
        Av1FrameHeader header = new() { Sequence = sequence };
        Av1BitReader r = new(payload, formatName);
        header.Read(ref r, temporalId, spatialId, formatName);
        header.BitLength = r.Position;
        return header;
    }

    private void Read(ref Av1BitReader r, int temporalId, int spatialId, string formatName)
    {
        Av1SequenceHeader s = Sequence;
        int idLen = s.FrameIdNumbersPresent ? s.AdditionalFrameIdLength + s.DeltaFrameIdLength : 0;
        if (s.ReducedStillPictureHeader)
        {
            FrameType = Av1FrameType.Key;
            ShowFrame = true;
            ErrorResilientMode = true;
        }
        else
        {
            ShowExistingFrame = r.Flag();
            if (ShowExistingFrame)
            {
                FrameToShowMapIdx = (int)r.Bits(3);
                if (s.DecoderModelInfoPresent && !s.EqualPictureInterval)
                {
                    r.Bits(s.FramePresentationTimeLength);
                }

                if (s.FrameIdNumbersPresent)
                {
                    r.Bits(idLen); // display_frame_id
                }

                return;
            }

            FrameType = (Av1FrameType)r.Bits(2);
            ShowFrame = r.Flag();
            if (ShowFrame && s.DecoderModelInfoPresent && !s.EqualPictureInterval)
            {
                r.Bits(s.FramePresentationTimeLength); // frame_presentation_time
            }

            ShowableFrame = ShowFrame ? FrameType != Av1FrameType.Key : r.Flag();
            ErrorResilientMode = FrameType == Av1FrameType.Switch || (FrameType == Av1FrameType.Key && ShowFrame) || r.Flag();
        }

        if (!FrameIsIntra)
        {
            throw new UnsupportedFormatException($"The AV1 stream has an {(FrameType == Av1FrameType.Inter ? "inter" : "switch")} frame; Egami decodes intra frames only so far.");
        }

        DisableCdfUpdate = r.Flag();
        AllowScreenContentTools = s.ForceScreenContentTools == 2 ? r.Flag() : s.ForceScreenContentTools == 1;
        ForceIntegerMv = AllowScreenContentTools && (s.ForceIntegerMv == 2 ? r.Flag() : s.ForceIntegerMv == 1);
        ForceIntegerMv |= FrameIsIntra;
        if (s.FrameIdNumbersPresent)
        {
            CurrentFrameId = (int)r.Bits(idLen);
        }

        FrameSizeOverrideFlag = FrameType == Av1FrameType.Switch || (!s.ReducedStillPictureHeader && r.Flag());
        OrderHint = (int)r.Bits(s.OrderHintBits);
        PrimaryRefFrame = FrameIsIntra || ErrorResilientMode ? PrimaryRefNone : (int)r.Bits(3);
        if (s.DecoderModelInfoPresent && r.Flag())
        {
            // buffer_removal_time for each operating point this frame belongs to.
            foreach (Av1OperatingPoint point in s.OperatingPoints)
            {
                if (point.DecoderModelPresent)
                {
                    bool inTemporal = ((point.Idc >> temporalId) & 1) != 0, inSpatial = ((point.Idc >> (spatialId + 8)) & 1) != 0;
                    if (point.Idc == 0 || (inTemporal && inSpatial))
                    {
                        r.Bits(s.BufferRemovalTimeLength);
                    }
                }
            }
        }

        RefreshFrameFlags = FrameType == Av1FrameType.Switch || (FrameType == Av1FrameType.Key && ShowFrame) ? 0xFF : (int)r.Bits(8);
        if (FrameType == Av1FrameType.IntraOnly && RefreshFrameFlags == 0xFF)
        {
            throw new CorruptDataException(formatName, -1, "An AV1 intra-only frame refreshes every reference frame, which the specification forbids.");
        }

        if (RefreshFrameFlags != 0xFF && ErrorResilientMode && s.EnableOrderHint)
        {
            for (int i = 0; i < 8; i++)
            {
                r.Bits(s.OrderHintBits); // ref_order_hint[i]
            }
        }

        FrameSize(ref r);
        RenderSize(ref r);
        if (AllowScreenContentTools && UpscaledWidth == FrameWidth)
        {
            AllowIntrabc = r.Flag();
        }

        DisableFrameEndUpdateCdf = s.ReducedStillPictureHeader || DisableCdfUpdate || r.Flag();
        TileInfo(ref r);
        QuantizationParams(ref r);
        SegmentationParams(ref r);
        DeltaParams(ref r);
        Lossless();
        LoopFilterParams(ref r);
        CdefParams(ref r);
        LrParams(ref r);
        TxMode = CodedLossless ? Av1TxMode.Only4x4 : r.Flag() ? Av1TxMode.Select : Av1TxMode.Largest;

        // frame_reference_mode, skip_mode_params, allow_warped_motion and global_motion_params read nothing in intra frames.
        ReducedTxSet = r.Flag();
        FilmGrainParams(ref r, formatName);
    }

    private void FrameSize(ref Av1BitReader r)
    {
        Av1SequenceHeader s = Sequence;
        if (FrameSizeOverrideFlag)
        {
            FrameWidth = (int)r.Bits(s.FrameWidthBits) + 1;
            FrameHeight = (int)r.Bits(s.FrameHeightBits) + 1;
        }
        else
        {
            FrameWidth = s.MaxFrameWidth;
            FrameHeight = s.MaxFrameHeight;
        }

        // superres_params
        UseSuperres = s.EnableSuperres && r.Flag();
        SuperresDenom = UseSuperres ? (int)r.Bits(3) + 9 : 8;
        UpscaledWidth = FrameWidth;
        // libaom and dav1d keep a downscaled frame at least 16 wide (or the whole width when it is narrower).
        FrameWidth = Math.Max(((UpscaledWidth * 8) + (SuperresDenom / 2)) / SuperresDenom, Math.Min(16, UpscaledWidth));

        // compute_image_size
        MiCols = 2 * ((FrameWidth + 7) >> 3);
        MiRows = 2 * ((FrameHeight + 7) >> 3);
    }

    private void RenderSize(ref Av1BitReader r)
    {
        if (r.Flag())
        {
            RenderWidth = (int)r.Bits(16) + 1;
            RenderHeight = (int)r.Bits(16) + 1;
        }
        else
        {
            RenderWidth = UpscaledWidth;
            RenderHeight = FrameHeight;
        }
    }

    private void TileInfo(ref Av1BitReader r)
    {
        bool sb128 = Sequence.Use128x128Superblock;
        int sbCols = sb128 ? (MiCols + 31) >> 5 : (MiCols + 15) >> 4;
        int sbRows = sb128 ? (MiRows + 31) >> 5 : (MiRows + 15) >> 4;
        int sbShift = sb128 ? 5 : 4;
        int sbSize = sbShift + 2;
        int maxTileWidthSb = 4096 >> sbSize;
        int maxTileAreaSb = (4096 * 2304) >> (2 * sbSize);
        int minLog2TileCols = TileLog2(maxTileWidthSb, sbCols);
        int maxLog2TileCols = TileLog2(1, Math.Min(sbCols, 64));
        int maxLog2TileRows = TileLog2(1, Math.Min(sbRows, 64));
        int minLog2Tiles = Math.Max(minLog2TileCols, TileLog2(maxTileAreaSb, sbRows * sbCols));
        List<int> cols = [], rows = [];
        UniformTileSpacing = r.Flag();
        if (UniformTileSpacing)
        {
            TileColsLog2 = minLog2TileCols;
            while (TileColsLog2 < maxLog2TileCols && r.Flag())
            {
                TileColsLog2++;
            }

            int tileWidthSb = (sbCols + (1 << TileColsLog2) - 1) >> TileColsLog2;
            for (int startSb = 0; startSb < sbCols; startSb += tileWidthSb)
            {
                cols.Add(startSb << sbShift);
            }

            int minLog2TileRows = Math.Max(minLog2Tiles - TileColsLog2, 0);
            TileRowsLog2 = minLog2TileRows;
            while (TileRowsLog2 < maxLog2TileRows && r.Flag())
            {
                TileRowsLog2++;
            }

            int tileHeightSb = (sbRows + (1 << TileRowsLog2) - 1) >> TileRowsLog2;
            for (int startSb = 0; startSb < sbRows; startSb += tileHeightSb)
            {
                rows.Add(startSb << sbShift);
            }
        }
        else
        {
            int widestTileSb = 0;
            for (int startSb = 0; startSb < sbCols;)
            {
                cols.Add(startSb << sbShift);
                int maxWidth = Math.Min(sbCols - startSb, maxTileWidthSb);
                int sizeSb = (int)r.NonSymmetric((uint)maxWidth) + 1;
                widestTileSb = Math.Max(sizeSb, widestTileSb);
                startSb += sizeSb;
            }

            int maxAreaSb = minLog2Tiles > 0 ? (sbRows * sbCols) >> (minLog2Tiles + 1) : sbRows * sbCols;
            int maxTileHeightSb = Math.Max(maxAreaSb / widestTileSb, 1);
            for (int startSb = 0; startSb < sbRows;)
            {
                rows.Add(startSb << sbShift);
                int maxHeight = Math.Min(sbRows - startSb, maxTileHeightSb);
                startSb += (int)r.NonSymmetric((uint)maxHeight) + 1;
            }

            TileColsLog2 = TileLog2(1, cols.Count);
            TileRowsLog2 = TileLog2(1, rows.Count);
        }

        TileCols = cols.Count;
        TileRows = rows.Count;
        cols.Add(MiCols);
        rows.Add(MiRows);
        MiColStarts = [.. cols];
        MiRowStarts = [.. rows];
        if (TileColsLog2 > 0 || TileRowsLog2 > 0)
        {
            ContextUpdateTileId = (int)r.Bits(TileRowsLog2 + TileColsLog2);
            TileSizeBytes = (int)r.Bits(2) + 1;
        }
    }

    /// <summary>Gets the smallest k for which blockSize shifted left by k reaches target (tile_log2).</summary>
    internal static int TileLog2(int blockSize, int target)
    {
        int k = 0;
        while ((blockSize << k) < target)
        {
            k++;
        }

        return k;
    }

    private void QuantizationParams(ref Av1BitReader r)
    {
        BaseQIdx = (int)r.Bits(8);
        DeltaQYDc = ReadDeltaQ(ref r);
        if (NumPlanes > 1)
        {
            bool diffUvDelta = Sequence.SeparateUvDeltaQ && r.Flag();
            DeltaQUDc = ReadDeltaQ(ref r);
            DeltaQUAc = ReadDeltaQ(ref r);
            (DeltaQVDc, DeltaQVAc) = diffUvDelta ? (ReadDeltaQ(ref r), ReadDeltaQ(ref r)) : (DeltaQUDc, DeltaQUAc);
        }

        UsingQmatrix = r.Flag();
        if (UsingQmatrix)
        {
            QmY = (int)r.Bits(4);
            QmU = (int)r.Bits(4);
            QmV = Sequence.SeparateUvDeltaQ ? (int)r.Bits(4) : QmU;
        }

        static int ReadDeltaQ(ref Av1BitReader r) => r.Flag() ? r.Signed(7) : 0;
    }

    private void SegmentationParams(ref Av1BitReader r)
    {
        SegmentationEnabled = r.Flag();
        if (SegmentationEnabled)
        {
            // An intra frame's primary_ref_frame is none: the map and data are always sent.
            SegmentationUpdateMap = true;
            SegmentationUpdateData = true;
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    bool enabled = r.Flag();
                    int value = 0;
                    if (enabled)
                    {
                        int limit = FeatureMax[j];
                        value = FeatureSigned[j] ? Math.Clamp(r.Signed(1 + FeatureBits[j]), -limit, limit) : Math.Clamp((int)r.Bits(FeatureBits[j]), 0, limit);
                    }

                    FeatureEnabled[i, j] = enabled;
                    FeatureData[i, j] = value;
                }
            }
        }

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                if (FeatureEnabled[i, j])
                {
                    LastActiveSegId = i;
                    SegIdPreSkip |= j >= 5;
                }
            }
        }
    }

    private void DeltaParams(ref Av1BitReader r)
    {
        if (BaseQIdx > 0)
        {
            DeltaQPresent = r.Flag();
        }

        if (DeltaQPresent)
        {
            DeltaQRes = (int)r.Bits(2);
            if (!AllowIntrabc)
            {
                DeltaLfPresent = r.Flag();
            }

            if (DeltaLfPresent)
            {
                DeltaLfRes = (int)r.Bits(2);
                DeltaLfMulti = r.Flag();
            }
        }
    }

    private void Lossless()
    {
        CodedLossless = true;
        for (int segment = 0; segment < 8; segment++)
        {
            int qindex = SegmentationEnabled && FeatureEnabled[segment, 0] ? Math.Clamp(BaseQIdx + FeatureData[segment, 0], 0, 255) : BaseQIdx;
            SegQIdx[segment] = qindex;
            LosslessArray[segment] = qindex == 0 && DeltaQYDc == 0 && DeltaQUAc == 0 && DeltaQUDc == 0 && DeltaQVAc == 0 && DeltaQVDc == 0;
            CodedLossless &= LosslessArray[segment];
        }

        AllLossless = CodedLossless && FrameWidth == UpscaledWidth;
    }

    private void LoopFilterParams(ref Av1BitReader r)
    {
        // setup_past_independence: the deltas start enabled, at their defaults.
        LoopFilterDeltaEnabled = true;
        if (CodedLossless || AllowIntrabc)
        {
            return;
        }

        LoopFilterLevel[0] = (int)r.Bits(6);
        LoopFilterLevel[1] = (int)r.Bits(6);
        if (NumPlanes > 1 && (LoopFilterLevel[0] != 0 || LoopFilterLevel[1] != 0))
        {
            LoopFilterLevel[2] = (int)r.Bits(6);
            LoopFilterLevel[3] = (int)r.Bits(6);
        }

        LoopFilterSharpness = (int)r.Bits(3);
        LoopFilterDeltaEnabled = r.Flag();
        if (LoopFilterDeltaEnabled)
        {
            LoopFilterDeltaUpdate = r.Flag();
            if (LoopFilterDeltaUpdate)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (r.Flag())
                    {
                        LoopFilterRefDeltas[i] = r.Signed(7);
                    }
                }

                for (int i = 0; i < 2; i++)
                {
                    if (r.Flag())
                    {
                        LoopFilterModeDeltas[i] = r.Signed(7);
                    }
                }
            }
        }
    }

    private void CdefParams(ref Av1BitReader r)
    {
        if (CodedLossless || AllowIntrabc || !Sequence.EnableCdef)
        {
            return;
        }

        CdefDamping = (int)r.Bits(2) + 3;
        CdefBits = (int)r.Bits(2);
        for (int i = 0; i < 1 << CdefBits; i++)
        {
            CdefYPriStrength[i] = (int)r.Bits(4);
            CdefYSecStrength[i] = Secondary((int)r.Bits(2));
            if (NumPlanes > 1)
            {
                CdefUvPriStrength[i] = (int)r.Bits(4);
                CdefUvSecStrength[i] = Secondary((int)r.Bits(2));
            }
        }

        static int Secondary(int coded) => coded == 3 ? 4 : coded;
    }

    private void LrParams(ref Av1BitReader r)
    {
        if (AllLossless || AllowIntrabc || !Sequence.EnableRestoration)
        {
            return;
        }

        bool usesChromaLr = false;
        for (int i = 0; i < NumPlanes; i++)
        {
            LrType[i] = (Av1RestorationType)r.Bits(2);
            if (LrType[i] != Av1RestorationType.None)
            {
                UsesLr = true;
                usesChromaLr |= i > 0;
            }
        }

        if (UsesLr)
        {
            int shift = (int)r.Bits(1);
            if (Sequence.Use128x128Superblock)
            {
                shift++;
            }
            else if (shift != 0)
            {
                shift += (int)r.Bits(1);
            }

            LoopRestorationSize[0] = 64 << shift;
            int uvShift = Sequence.SubsamplingX == 1 && Sequence.SubsamplingY == 1 && usesChromaLr ? (int)r.Bits(1) : 0;
            LoopRestorationSize[1] = LoopRestorationSize[0] >> uvShift;
            LoopRestorationSize[2] = LoopRestorationSize[0] >> uvShift;
        }
    }

    private void FilmGrainParams(ref Av1BitReader r, string formatName)
    {
        Av1SequenceHeader s = Sequence;
        if (!s.FilmGrainParamsPresent || (!ShowFrame && !ShowableFrame) || !r.Flag())
        {
            return;
        }

        int seed = (int)r.Bits(16);

        // update_grain is read in inter frames only.
        (int, int)[] pointsY = Points(ref r, 14, formatName);
        bool fromLuma = !s.Monochrome && r.Flag();
        (int, int)[] pointsCb = [], pointsCr = [];
        if (!s.Monochrome && !fromLuma && !(s.SubsamplingX == 1 && s.SubsamplingY == 1 && pointsY.Length == 0))
        {
            pointsCb = Points(ref r, 10, formatName);
            pointsCr = Points(ref r, 10, formatName);
        }

        int scaling = (int)r.Bits(2);
        int lag = (int)r.Bits(2);
        int numPosLuma = 2 * lag * (lag + 1);
        int numPosChroma = numPosLuma + (pointsY.Length > 0 ? 1 : 0);
        int[] arY = pointsY.Length > 0 ? Coefficients(ref r, numPosLuma) : [];
        int[] arCb = fromLuma || pointsCb.Length > 0 ? Coefficients(ref r, numPosChroma) : [];
        int[] arCr = fromLuma || pointsCr.Length > 0 ? Coefficients(ref r, numPosChroma) : [];
        int arShift = (int)r.Bits(2);
        int scaleShift = (int)r.Bits(2);
        (int cbMult, int cbLuma, int cbOffset) = pointsCb.Length > 0 ? ((int)r.Bits(8), (int)r.Bits(8), (int)r.Bits(9)) : (0, 0, 0);
        (int crMult, int crLuma, int crOffset) = pointsCr.Length > 0 ? ((int)r.Bits(8), (int)r.Bits(8), (int)r.Bits(9)) : (0, 0, 0);
        FilmGrain = new Av1FilmGrain
        {
            ApplyGrain = true,
            UpdateGrain = true,
            GrainSeed = seed,
            PointsY = pointsY,
            ChromaScalingFromLuma = fromLuma,
            PointsCb = pointsCb,
            PointsCr = pointsCr,
            GrainScalingMinus8 = scaling,
            ArCoeffLag = lag,
            ArCoeffsYPlus128 = arY,
            ArCoeffsCbPlus128 = arCb,
            ArCoeffsCrPlus128 = arCr,
            ArCoeffShiftMinus6 = arShift,
            GrainScaleShift = scaleShift,
            CbMult = cbMult,
            CbLumaMult = cbLuma,
            CbOffset = cbOffset,
            CrMult = crMult,
            CrLumaMult = crLuma,
            CrOffset = crOffset,
            OverlapFlag = r.Flag(),
            ClipToRestrictedRange = r.Flag(),
        };

        static (int, int)[] Points(ref Av1BitReader r, int max, string formatName)
        {
            int n = (int)r.Bits(4);
            if (n > max)
            {
                throw new CorruptDataException(formatName, -1, $"An AV1 film grain table has {n} points; at most {max} are allowed.");
            }

            (int, int)[] points = new (int, int)[n];
            for (int i = 0; i < n; i++)
            {
                points[i] = ((int)r.Bits(8), (int)r.Bits(8));
                if (i > 0 && points[i].Item1 <= points[i - 1].Item1)
                {
                    // Section 6.8.20: each point's value is above the one before.
                    throw new CorruptDataException(formatName, -1, "An AV1 film grain table's point values do not increase.");
                }
            }

            return points;
        }

        static int[] Coefficients(ref Av1BitReader r, int n)
        {
            int[] values = new int[n];
            for (int i = 0; i < n; i++)
            {
                values[i] = (int)r.Bits(8);
            }

            return values;
        }
    }

    /// <summary>
    /// Lists the header's fields under the specification's names, the coded elements and the variables they set, in the form
    /// Egami.Video's CodecHeaders uses; list entries carry an index. Fields a frame does not code are left out.
    /// </summary>
    internal List<(string Name, long Value)> Describe()
    {
        List<(string, long)> f = [];
        f.Add(("show_existing_frame", ShowExistingFrame ? 1 : 0));
        if (ShowExistingFrame)
        {
            f.Add(("frame_to_show_map_idx", FrameToShowMapIdx));
            return f;
        }

        f.Add(("frame_type", (int)FrameType));
        f.Add(("show_frame", ShowFrame ? 1 : 0));
        f.Add(("showable_frame", ShowableFrame ? 1 : 0));
        f.Add(("error_resilient_mode", ErrorResilientMode ? 1 : 0));
        f.Add(("disable_cdf_update", DisableCdfUpdate ? 1 : 0));
        f.Add(("allow_screen_content_tools", AllowScreenContentTools ? 1 : 0));
        f.Add(("force_integer_mv", ForceIntegerMv ? 1 : 0));
        f.Add(("frame_size_override_flag", FrameSizeOverrideFlag ? 1 : 0));
        f.Add(("order_hint", OrderHint));
        f.Add(("primary_ref_frame", PrimaryRefFrame));
        f.Add(("refresh_frame_flags", RefreshFrameFlags));
        f.Add(("FrameWidth", FrameWidth));
        f.Add(("UpscaledWidth", UpscaledWidth));
        f.Add(("FrameHeight", FrameHeight));
        f.Add(("RenderWidth", RenderWidth));
        f.Add(("RenderHeight", RenderHeight));
        f.Add(("use_superres", UseSuperres ? 1 : 0));
        f.Add(("SuperresDenom", SuperresDenom));
        f.Add(("allow_intrabc", AllowIntrabc ? 1 : 0));
        f.Add(("disable_frame_end_update_cdf", DisableFrameEndUpdateCdf ? 1 : 0));
        f.Add(("uniform_tile_spacing_flag", UniformTileSpacing ? 1 : 0));
        f.Add(("TileColsLog2", TileColsLog2));
        f.Add(("TileRowsLog2", TileRowsLog2));
        f.Add(("TileCols", TileCols));
        f.Add(("TileRows", TileRows));
        int sbShift = Sequence.Use128x128Superblock ? 5 : 4;
        for (int i = 0; i <= TileCols; i++)
        {
            // In superblocks, rounded up at the frame's edge.
            f.Add(($"MiColStartSb[{i}]", (MiColStarts[i] + (1 << sbShift) - 1) >> sbShift));
        }

        for (int i = 0; i <= TileRows; i++)
        {
            f.Add(($"MiRowStartSb[{i}]", (MiRowStarts[i] + (1 << sbShift) - 1) >> sbShift));
        }

        f.Add(("context_update_tile_id", ContextUpdateTileId));
        if (TileColsLog2 > 0 || TileRowsLog2 > 0)
        {
            f.Add(("TileSizeBytes", TileSizeBytes));
        }

        f.Add(("base_q_idx", BaseQIdx));
        f.Add(("DeltaQYDc", DeltaQYDc));
        if (NumPlanes > 1)
        {
            f.Add(("DeltaQUDc", DeltaQUDc));
            f.Add(("DeltaQUAc", DeltaQUAc));
            f.Add(("DeltaQVDc", DeltaQVDc));
            f.Add(("DeltaQVAc", DeltaQVAc));
        }

        f.Add(("using_qmatrix", UsingQmatrix ? 1 : 0));
        if (UsingQmatrix)
        {
            f.Add(("qm_y", QmY));
            f.Add(("qm_u", QmU));
            f.Add(("qm_v", QmV));
        }

        f.Add(("segmentation_enabled", SegmentationEnabled ? 1 : 0));
        if (SegmentationEnabled)
        {
            f.Add(("segmentation_update_map", SegmentationUpdateMap ? 1 : 0));
            f.Add(("segmentation_temporal_update", SegmentationTemporalUpdate ? 1 : 0));
            f.Add(("segmentation_update_data", SegmentationUpdateData ? 1 : 0));
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 5; j++)
                {
                    f.Add(($"FeatureData[{i}][{j}]", FeatureData[i, j]));
                }

                f.Add(($"SegRefFrame[{i}]", FeatureEnabled[i, 5] ? FeatureData[i, 5] : -1));
                f.Add(($"SegSkip[{i}]", FeatureEnabled[i, 6] ? 1 : 0));
                f.Add(($"SegGlobalMv[{i}]", FeatureEnabled[i, 7] ? 1 : 0));
            }

            f.Add(("SegIdPreSkip", SegIdPreSkip ? 1 : 0));
            f.Add(("LastActiveSegId", LastActiveSegId));
        }

        for (int i = 0; i < 8; i++)
        {
            f.Add(($"LosslessArray[{i}]", LosslessArray[i] ? 1 : 0));
            f.Add(($"SegQIdx[{i}]", SegQIdx[i]));
        }

        f.Add(("delta_q_present", DeltaQPresent ? 1 : 0));
        f.Add(("delta_q_res", DeltaQRes));
        f.Add(("delta_lf_present", DeltaLfPresent ? 1 : 0));
        f.Add(("delta_lf_res", DeltaLfRes));
        f.Add(("delta_lf_multi", DeltaLfMulti ? 1 : 0));
        f.Add(("CodedLossless", CodedLossless ? 1 : 0));
        // The loop filter and CDEF fields are listed only when the frame codes them.
        bool filtered = !CodedLossless && !AllowIntrabc;
        for (int i = 0; filtered && i < (NumPlanes > 1 ? 4 : 2); i++)
        {
            f.Add(($"loop_filter_level[{i}]", LoopFilterLevel[i]));
        }

        if (filtered)
        {
            f.Add(("loop_filter_sharpness", LoopFilterSharpness));
            f.Add(("loop_filter_delta_enabled", LoopFilterDeltaEnabled ? 1 : 0));
            f.Add(("loop_filter_delta_update", LoopFilterDeltaUpdate ? 1 : 0));
            for (int i = 0; i < 8; i++)
            {
                f.Add(($"loop_filter_ref_deltas[{i}]", LoopFilterRefDeltas[i]));
            }

            for (int i = 0; i < 2; i++)
            {
                f.Add(($"loop_filter_mode_deltas[{i}]", LoopFilterModeDeltas[i]));
            }
        }

        if (filtered && Sequence.EnableCdef)
        {
            f.Add(("CdefDamping", CdefDamping));
            f.Add(("cdef_bits", CdefBits));
            for (int i = 0; i < 1 << CdefBits; i++)
            {
                f.Add(($"cdef_y_pri_strength[{i}]", CdefYPriStrength[i]));
                f.Add(($"cdef_y_sec_strength[{i}]", CdefYSecStrength[i]));
                if (NumPlanes > 1)
                {
                    f.Add(($"cdef_uv_pri_strength[{i}]", CdefUvPriStrength[i]));
                    f.Add(($"cdef_uv_sec_strength[{i}]", CdefUvSecStrength[i]));
                }
            }
        }

        for (int i = 0; i < NumPlanes; i++)
        {
            f.Add(($"lr_type[{i}]", (int)LrType[i]));
        }

        if (UsesLr)
        {
            f.Add(("LoopRestorationSizeLog2[0]", Log2(LoopRestorationSize[0])));
            if (NumPlanes > 1 && (LrType[1] != Av1RestorationType.None || LrType[2] != Av1RestorationType.None))
            {
                f.Add(("LoopRestorationSizeLog2[1]", Log2(LoopRestorationSize[1])));
            }
        }

        f.Add(("TxMode", (int)TxMode));
        f.Add(("reference_select", ReferenceSelect ? 1 : 0));
        f.Add(("skip_mode_present", SkipModePresent ? 1 : 0));
        f.Add(("allow_warped_motion", AllowWarpedMotion ? 1 : 0));
        f.Add(("reduced_tx_set", ReducedTxSet ? 1 : 0));
        Av1FilmGrain g = FilmGrain;
        f.Add(("apply_grain", g.ApplyGrain ? 1 : 0));
        if (g.ApplyGrain)
        {
            f.Add(("update_grain", g.UpdateGrain ? 1 : 0));
            f.Add(("grain_seed", g.GrainSeed));
            AddPoints("y", g.PointsY);
            f.Add(("chroma_scaling_from_luma", g.ChromaScalingFromLuma ? 1 : 0));
            AddPoints("cb", g.PointsCb);
            AddPoints("cr", g.PointsCr);
            f.Add(("grain_scaling_minus_8", g.GrainScalingMinus8));
            f.Add(("ar_coeff_lag", g.ArCoeffLag));
            AddList("ar_coeffs_y_plus_128", g.ArCoeffsYPlus128);
            AddList("ar_coeffs_cb_plus_128", g.ArCoeffsCbPlus128);
            AddList("ar_coeffs_cr_plus_128", g.ArCoeffsCrPlus128);
            f.Add(("ar_coeff_shift_minus_6", g.ArCoeffShiftMinus6));
            f.Add(("grain_scale_shift", g.GrainScaleShift));
            if (g.PointsCb.Length > 0)
            {
                f.Add(("cb_mult", g.CbMult));
                f.Add(("cb_luma_mult", g.CbLumaMult));
                f.Add(("cb_offset", g.CbOffset));
            }

            if (g.PointsCr.Length > 0)
            {
                f.Add(("cr_mult", g.CrMult));
                f.Add(("cr_luma_mult", g.CrLumaMult));
                f.Add(("cr_offset", g.CrOffset));
            }

            f.Add(("overlap_flag", g.OverlapFlag ? 1 : 0));
            f.Add(("clip_to_restricted_range", g.ClipToRestrictedRange ? 1 : 0));
        }

        return f;

        void AddPoints(string plane, (int Value, int Scaling)[] points)
        {
            f.Add(($"num_{plane}_points", points.Length));
            for (int i = 0; i < points.Length; i++)
            {
                f.Add(($"point_{plane}_value[{i}]", points[i].Value));
                f.Add(($"point_{plane}_scaling[{i}]", points[i].Scaling));
            }
        }

        void AddList(string name, int[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                f.Add(($"{name}[{i}]", values[i]));
            }
        }

        static int Log2(int value) => 31 - int.LeadingZeroCount(value);
    }
}
