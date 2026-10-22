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
/// The uncompressed header of a frame (AV1 specification sections 5.9 and 7.20 to 7.21, as libaom's decodeframe.c reads it), with
/// the variables it derives: frame and superres size, the reference frames and their order hints, tiles, quantizer,
/// segmentation, delta q and loop filter, lossless state, loop filter, CDEF, loop restoration, transform mode, reference mode,
/// skip mode, global motion and film grain. Inter and switch frames read the reference slots' saved state; without slots
/// (an AVIF still), reading one throws <see cref="UnsupportedFormatException"/>.
/// </summary>
internal sealed class Av1FrameHeader
{
    /// <summary>The value of primary_ref_frame that means none.</summary>
    internal const int PrimaryRefNone = 7;

    /// <summary>The fractional bits of warp and global motion parameters (WARPEDMODEL_PREC_BITS).</summary>
    internal const int WarpPrecBits = 16;

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

    /// <summary>Gets the reference slot each of LAST_FRAME to ALTREF_FRAME uses (ref_frame_idx).</summary>
    internal int[] RefFrameIdx { get; } = new int[7];

    /// <summary>Gets each reference frame's frame, indexed by reference frame (1 to 7); null for an intra frame.</summary>
    internal Av1Frame?[] RefFrames { get; } = new Av1Frame?[8];

    /// <summary>Gets each reference frame's order hint, indexed by reference frame (OrderHints).</summary>
    internal int[] OrderHints { get; } = new int[8];

    /// <summary>Gets whether each reference frame comes later in display order (RefFrameSignBias).</summary>
    internal bool[] RefFrameSignBias { get; } = new bool[8];

    internal bool AllowHighPrecisionMv { get; private set; }

    /// <summary>Gets the frame's interpolation filter (<see cref="Interp"/>); <see cref="Interp.Switchable"/> codes it per block.</summary>
    internal int InterpolationFilter { get; private set; }

    internal bool IsMotionModeSwitchable { get; private set; }

    internal bool UseRefFrameMvs { get; private set; }

    /// <summary>Gets the two reference frames skip mode predicts from (SkipModeFrame).</summary>
    internal int[] SkipModeFrame { get; } = new int[2];

    /// <summary>Gets each reference frame's global motion type (GmType).</summary>
    internal int[] GmType { get; } = new int[8];

    /// <summary>Gets each reference frame's global motion parameters (gm_params), six per frame.</summary>
    internal int[][] GmParams { get; } = [.. Enumerable.Range(0, 8).Select(_ => DefaultWarpParams())];

    /// <summary>Gets the frame the CDFs, segmentation, loop filter deltas and global motion are loaded from, or null.</summary>
    internal Av1Frame? PrimaryRef { get; private set; }

    /// <summary>Gets the identity warp's parameters.</summary>
    internal static int[] DefaultWarpParams() => [0, 0, 1 << WarpPrecBits, 0, 0, 1 << WarpPrecBits];

    /// <summary>The header of the grey frame that stands in for a missing reference: the sequence's largest frame, a key frame.</summary>
    internal static Av1FrameHeader Missing(Av1SequenceHeader sequence, int orderHint)
    {
        Av1FrameHeader h = new()
        {
            Sequence = sequence,
            FrameType = Av1FrameType.Key,
            OrderHint = orderHint,
            FrameWidth = sequence.MaxFrameWidth,
            UpscaledWidth = sequence.MaxFrameWidth,
            FrameHeight = sequence.MaxFrameHeight,
            RenderWidth = sequence.MaxFrameWidth,
            RenderHeight = sequence.MaxFrameHeight,
            MiCols = 2 * ((sequence.MaxFrameWidth + 7) >> 3),
            MiRows = 2 * ((sequence.MaxFrameHeight + 7) >> 3),
            MiColStarts = [0, 2 * ((sequence.MaxFrameWidth + 7) >> 3)],
            MiRowStarts = [0, 2 * ((sequence.MaxFrameHeight + 7) >> 3)],
        };
        return h;
    }

    /// <summary>Gets the signed distance between two order hints (get_relative_dist).</summary>
    internal int RelativeDist(int a, int b)
    {
        if (!Sequence.EnableOrderHint)
        {
            return 0;
        }

        int diff = a - b;
        int m = 1 << (Sequence.OrderHintBits - 1);
        return (diff & (m - 1)) - (diff & m);
    }

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
    /// <param name="slots">The eight reference slots, for a stream; null for a still, which then decodes intra frames only.</param>
    internal static Av1FrameHeader Parse(ReadOnlySpan<byte> payload, Av1SequenceHeader sequence, int temporalId, int spatialId, string formatName,
        Av1Frame?[]? slots = null)
    {
        Av1FrameHeader header = new() { Sequence = sequence };
        Av1BitReader r = new(payload, formatName);
        header.Read(ref r, temporalId, spatialId, formatName, slots);
        header.BitLength = r.Position;
        return header;
    }

    private void Read(ref Av1BitReader r, int temporalId, int spatialId, string formatName, Av1Frame?[]? slots)
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

        if (!FrameIsIntra && slots is null)
        {
            throw new UnsupportedFormatException($"The AV1 stream has an {(FrameType == Av1FrameType.Inter ? "inter" : "switch")} frame; an AVIF still decodes intra frames only.");
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

        if ((!FrameIsIntra || RefreshFrameFlags != 0xFF) && ErrorResilientMode && s.EnableOrderHint)
        {
            for (int i = 0; i < 8; i++)
            {
                int hint = (int)r.Bits(s.OrderHintBits); // ref_order_hint[i]
                if (slots is not null && (slots[i] is null || slots[i]!.Header.OrderHint != hint))
                {
                    // libaom puts a neutral grey frame in place of a missing reference rather than failing.
                    slots[i] = Av1Frame.Grey(s, hint, formatName);
                }
            }
        }

        if (FrameIsIntra)
        {
            FrameSize(ref r);
            RenderSize(ref r);
            if (AllowScreenContentTools && UpscaledWidth == FrameWidth)
            {
                AllowIntrabc = r.Flag();
            }
        }
        else
        {
            ReadReferences(ref r, slots!, formatName);
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
        ReferenceSelect = !FrameIsIntra && r.Flag();
        SkipModePresent = SkipModeAllowed() && r.Flag();
        AllowWarpedMotion = !FrameIsIntra && !ErrorResilientMode && s.EnableWarpedMotion && r.Flag();
        ReducedTxSet = r.Flag();
        if (!FrameIsIntra)
        {
            GlobalMotionParams(ref r);
        }

        FilmGrainParams(ref r, formatName);
    }

    /// <summary>
    /// Reads an inter or switch frame's references (frame_refs_short_signaling, ref_frame_idx, frame_size_with_refs and the
    /// motion fields that follow) and sets up their order hints and sign bias.
    /// </summary>
    private void ReadReferences(ref Av1BitReader r, Av1Frame?[] slots, string formatName)
    {
        Av1SequenceHeader s = Sequence;
        bool shortSignaling = s.EnableOrderHint && r.Flag();
        if (shortSignaling)
        {
            int last = (int)r.Bits(3), gold = (int)r.Bits(3);
            if (slots[last] is null || slots[gold] is null)
            {
                throw MissingReference(formatName);
            }

            SetFrameRefs(slots, last, gold, formatName);
        }

        for (int i = 0; i < 7; i++)
        {
            if (!shortSignaling)
            {
                RefFrameIdx[i] = (int)r.Bits(3);
            }

            if (s.FrameIdNumbersPresent)
            {
                r.Bits(s.DeltaFrameIdLength); // delta_frame_id_minus_1
            }

            Av1Frame reference = slots[RefFrameIdx[i]] ?? throw MissingReference(formatName);
            if (reference.Sequence.BitDepth != s.BitDepth || reference.Sequence.SubsamplingX != s.SubsamplingX
                || reference.Sequence.SubsamplingY != s.SubsamplingY || reference.Sequence.Monochrome != s.Monochrome)
            {
                throw new CorruptDataException(formatName, -1, "An AV1 inter frame refers to a frame of another bit depth or chroma format.");
            }

            RefFrames[Ref.Last + i] = reference;
        }

        if (FrameSizeOverrideFlag && !ErrorResilientMode)
        {
            // frame_size_with_refs
            bool found = false;
            for (int i = 0; i < 7 && !found; i++)
            {
                if (r.Flag())
                {
                    Av1FrameHeader reference = RefFrames[Ref.Last + i]!.Header;
                    UpscaledWidth = reference.UpscaledWidth;
                    FrameWidth = UpscaledWidth;
                    FrameHeight = reference.FrameHeight;
                    RenderWidth = reference.RenderWidth;
                    RenderHeight = reference.RenderHeight;
                    found = true;
                }
            }

            if (found)
            {
                SuperresParams(ref r);
            }
            else
            {
                FrameSize(ref r);
                RenderSize(ref r);
            }
        }
        else
        {
            FrameSize(ref r);
            RenderSize(ref r);
        }

        for (int i = Ref.Last; i <= Ref.Alt; i++)
        {
            Av1FrameHeader reference = RefFrames[i]!.Header;
            if (2 * FrameWidth < reference.UpscaledWidth || 2 * FrameHeight < reference.FrameHeight
                || FrameWidth > 16 * reference.UpscaledWidth || FrameHeight > 16 * reference.FrameHeight)
            {
                throw new CorruptDataException(formatName, -1, "An AV1 reference frame is more than twice or under a sixteenth the frame's size.");
            }
        }

        AllowHighPrecisionMv = !ForceIntegerMv && r.Flag();
        InterpolationFilter = r.Flag() ? Interp.Switchable : (int)r.Bits(2);
        IsMotionModeSwitchable = r.Flag();
        UseRefFrameMvs = !ErrorResilientMode && s.EnableRefFrameMvs && r.Flag();
        for (int i = Ref.Last; i <= Ref.Alt; i++)
        {
            int hint = RefFrames[i]!.Header.OrderHint;
            OrderHints[i] = hint;
            RefFrameSignBias[i] = s.EnableOrderHint && RelativeDist(hint, OrderHint) > 0;
        }

        if (PrimaryRefFrame != PrimaryRefNone)
        {
            PrimaryRef = RefFrames[Ref.Last + PrimaryRefFrame];
        }
    }

    private static CorruptDataException MissingReference(string formatName) =>
        new(formatName, -1, "An AV1 inter frame refers to an empty reference slot.") { IsMissingReference = true };

    /// <summary>Chooses the references of short signaling from the LAST and GOLDEN slots and the order hints (av1_set_frame_refs).</summary>
    private void SetFrameRefs(Av1Frame?[] slots, int lastIdx, int goldIdx, string formatName)
    {
        int curSortIdx = 1 << (Sequence.OrderHintBits - 1);
        (int MapIdx, int SortIdx)[] info = new (int, int)[8];
        int lastSortIdx = -1, goldSortIdx = -1;
        for (int i = 0; i < 8; i++)
        {
            int sortIdx = slots[i] is null ? -1 : curSortIdx + RelativeDist(slots[i]!.Header.OrderHint, OrderHint);
            info[i] = (i, sortIdx);
            lastSortIdx = i == lastIdx ? sortIdx : lastSortIdx;
            goldSortIdx = i == goldIdx ? sortIdx : goldSortIdx;
        }

        if (lastSortIdx == -1 || lastSortIdx >= curSortIdx || goldSortIdx == -1 || goldSortIdx >= curSortIdx)
        {
            throw new CorruptDataException(formatName, -1, "An AV1 inter frame names a later frame as LAST or GOLDEN.");
        }

        // A stable sort by sort index, then slot.
        Array.Sort(info, (a, b) => a.SortIdx != b.SortIdx ? a.SortIdx.CompareTo(b.SortIdx) : a.MapIdx.CompareTo(b.MapIdx));
        int fwdStart = 0, fwdEnd = 7;
        for (int i = 0; i < 8; i++)
        {
            if (info[i].SortIdx == -1)
            {
                fwdStart++;
                continue;
            }

            if (info[i].SortIdx >= curSortIdx)
            {
                fwdEnd = i - 1;
                break;
            }
        }

        int bwdStart = fwdEnd + 1, bwdEnd = 7;
        Span<bool> set = stackalloc bool[7];
        if (bwdStart <= bwdEnd)
        {
            RefFrameIdx[Ref.Alt - Ref.Last] = info[bwdEnd--].MapIdx;
            set[Ref.Alt - Ref.Last] = true;
        }

        if (bwdStart <= bwdEnd)
        {
            RefFrameIdx[Ref.Bwd - Ref.Last] = info[bwdStart++].MapIdx;
            set[Ref.Bwd - Ref.Last] = true;
        }

        if (bwdStart <= bwdEnd)
        {
            RefFrameIdx[Ref.Alt2 - Ref.Last] = info[bwdStart].MapIdx;
            set[Ref.Alt2 - Ref.Last] = true;
        }

        for (int i = fwdStart; i <= fwdEnd; i++)
        {
            if (info[i].MapIdx == lastIdx)
            {
                RefFrameIdx[0] = info[i].MapIdx;
                set[0] = true;
            }

            if (info[i].MapIdx == goldIdx)
            {
                RefFrameIdx[Ref.Golden - Ref.Last] = info[i].MapIdx;
                set[Ref.Golden - Ref.Last] = true;
            }
        }

        ReadOnlySpan<int> order = [Ref.Last2, Ref.Last3, Ref.Bwd, Ref.Alt2, Ref.Alt];
        int k = 0;
        for (; k < order.Length; k++)
        {
            int frame = order[k] - Ref.Last;
            if (set[frame])
            {
                continue;
            }

            while (fwdStart <= fwdEnd && (info[fwdEnd].MapIdx == lastIdx || info[fwdEnd].MapIdx == goldIdx))
            {
                fwdEnd--;
            }

            if (fwdStart > fwdEnd)
            {
                break;
            }

            RefFrameIdx[frame] = info[fwdEnd--].MapIdx;
            set[frame] = true;
        }

        for (; k < order.Length; k++)
        {
            int frame = order[k] - Ref.Last;
            if (!set[frame])
            {
                RefFrameIdx[frame] = info[fwdStart].MapIdx;
            }
        }
    }

    /// <summary>Gets whether skip mode may be coded, and sets <see cref="SkipModeFrame"/> (av1_setup_skip_mode_allowed).</summary>
    private bool SkipModeAllowed()
    {
        if (!Sequence.EnableOrderHint || FrameIsIntra || !ReferenceSelect)
        {
            return false;
        }

        int forwardHint = -1, backwardHint = int.MaxValue, forwardIdx = -1, backwardIdx = -1;
        for (int i = 0; i < 7; i++)
        {
            int hint = OrderHints[Ref.Last + i];
            if (RelativeDist(hint, OrderHint) < 0)
            {
                if (forwardHint == -1 || RelativeDist(hint, forwardHint) > 0)
                {
                    forwardHint = hint;
                    forwardIdx = i;
                }
            }
            else if (RelativeDist(hint, OrderHint) > 0 && (backwardHint == int.MaxValue || RelativeDist(hint, backwardHint) < 0))
            {
                backwardHint = hint;
                backwardIdx = i;
            }
        }

        if (forwardIdx < 0)
        {
            return false;
        }

        if (backwardIdx < 0)
        {
            // Forward prediction only: the second nearest forward reference.
            int secondHint = -1;
            for (int i = 0; i < 7; i++)
            {
                int hint = OrderHints[Ref.Last + i];
                if (RelativeDist(hint, forwardHint) < 0 && (secondHint == -1 || RelativeDist(hint, secondHint) > 0))
                {
                    secondHint = hint;
                    backwardIdx = i;
                }
            }

            if (secondHint == -1)
            {
                return false;
            }
        }

        SkipModeFrame[0] = Ref.Last + Math.Min(forwardIdx, backwardIdx);
        SkipModeFrame[1] = Ref.Last + Math.Max(forwardIdx, backwardIdx);
        return true;
    }

    /// <summary>Reads global_motion_params, each frame's against the primary reference frame's (read_global_motion_params).</summary>
    private void GlobalMotionParams(ref Av1BitReader r)
    {
        const int alphaMax = 1 << 12, alphaPrecDiff = WarpPrecBits - 15, alphaDecodeFactor = 1 << alphaPrecDiff;
        for (int frame = Ref.Last; frame <= Ref.Alt; frame++)
        {
            int[] prev = PrimaryRef?.Header.GmParams[frame] ?? DefaultWarpParams();
            int type = GlobalMotion.Identity;
            if (r.Flag())
            {
                type = r.Flag() ? GlobalMotion.RotZoom : r.Flag() ? GlobalMotion.Translation : GlobalMotion.Affine;
            }

            int[] p = DefaultWarpParams();
            GmType[frame] = type;
            if (type >= GlobalMotion.RotZoom)
            {
                p[2] = (SignedRefSubexpFin(ref r, alphaMax + 1, (prev[2] >> alphaPrecDiff) - (1 << 15)) * alphaDecodeFactor) + (1 << WarpPrecBits);
                p[3] = SignedRefSubexpFin(ref r, alphaMax + 1, prev[3] >> alphaPrecDiff) * alphaDecodeFactor;
            }

            if (type >= GlobalMotion.Affine)
            {
                p[4] = SignedRefSubexpFin(ref r, alphaMax + 1, prev[4] >> alphaPrecDiff) * alphaDecodeFactor;
                p[5] = (SignedRefSubexpFin(ref r, alphaMax + 1, (prev[5] >> alphaPrecDiff) - (1 << 15)) * alphaDecodeFactor) + (1 << WarpPrecBits);
            }
            else
            {
                p[4] = -p[3];
                p[5] = p[2];
            }

            if (type >= GlobalMotion.Translation)
            {
                int hp = AllowHighPrecisionMv ? 0 : 1;
                int transBits = type == GlobalMotion.Translation ? 9 - hp : 12;
                int precDiff = type == GlobalMotion.Translation ? WarpPrecBits - 3 + hp : WarpPrecBits - 6;
                p[0] = SignedRefSubexpFin(ref r, (1 << transBits) + 1, prev[0] >> precDiff) * (1 << precDiff);
                p[1] = SignedRefSubexpFin(ref r, (1 << transBits) + 1, prev[1] >> precDiff) * (1 << precDiff);
            }

            GmParams[frame] = p;
        }
    }

    /// <summary>aom_rb_read_signed_primitive_refsubexpfin with k = 3: a value in [-(n - 1), n - 1] coded against a reference.</summary>
    private static int SignedRefSubexpFin(ref Av1BitReader r, int n, int reference)
    {
        reference += n - 1;
        int scaledN = (2 * n) - 1;
        int value = (reference << 1) <= scaledN
            ? InverseRecenter(reference, SubexpFin(ref r, scaledN))
            : scaledN - 1 - InverseRecenter(scaledN - 1 - reference, SubexpFin(ref r, scaledN));
        return value - (n - 1);

        static int InverseRecenter(int r, int v) => v > 2 * r ? v : (v & 1) != 0 ? r - ((v + 1) >> 1) : r + (v >> 1);

        static int SubexpFin(ref Av1BitReader r, int n)
        {
            const int k = 3;
            int i = 0, mk = 0;
            while (true)
            {
                int b = i != 0 ? k + i - 1 : k;
                int a = 1 << b;
                if (n <= mk + (3 * a))
                {
                    return (int)r.NonSymmetric((uint)(n - mk)) + mk;
                }

                if (!r.Flag())
                {
                    return (int)r.Bits(b) + mk;
                }

                i++;
                mk += a;
            }
        }
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

        UpscaledWidth = FrameWidth;
        SuperresParams(ref r);
    }

    /// <summary>Reads superres_params and computes the downscaled width and the mode info size (compute_image_size).</summary>
    private void SuperresParams(ref Av1BitReader r)
    {
        Av1SequenceHeader s = Sequence;
        UseSuperres = s.EnableSuperres && r.Flag();
        SuperresDenom = UseSuperres ? (int)r.Bits(3) + 9 : 8;
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
        if (SegmentationEnabled && PrimaryRef is not null)
        {
            SegmentationUpdateMap = r.Flag();
            SegmentationTemporalUpdate = SegmentationUpdateMap && r.Flag();
            SegmentationUpdateData = r.Flag();
            if (!SegmentationUpdateData)
            {
                // load_previous: the primary reference frame's features.
                Array.Copy(PrimaryRef.Header.FeatureEnabled, FeatureEnabled, 64);
                Array.Copy(PrimaryRef.Header.FeatureData, FeatureData, 64);
            }
        }
        else if (SegmentationEnabled)
        {
            // Without a primary reference frame the map and data are always sent.
            SegmentationUpdateMap = true;
            SegmentationUpdateData = true;
        }

        if (SegmentationEnabled && SegmentationUpdateData)
        {
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

        if (PrimaryRef is not null)
        {
            // load_previous: the deltas carry over from the primary reference frame.
            PrimaryRef.Header.LoopFilterRefDeltas.CopyTo(LoopFilterRefDeltas, 0);
            PrimaryRef.Header.LoopFilterModeDeltas.CopyTo(LoopFilterModeDeltas, 0);
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
        if (FrameType == Av1FrameType.Inter && !r.Flag())
        {
            // update_grain is 0: the parameters of a reference frame, with this frame's seed (load_grain_params).
            int refIdx = (int)r.Bits(3);
            if (Array.IndexOf(RefFrameIdx, refIdx) < 0)
            {
                throw new CorruptDataException(formatName, -1, "An AV1 frame takes film grain from a slot that is not one of its references.");
            }

            FilmGrain = RefFrames[Ref.Last + Array.IndexOf(RefFrameIdx, refIdx)]!.Header.FilmGrain with { GrainSeed = seed };
            return;
        }

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
