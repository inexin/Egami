namespace Egami.Formats.Av1;

/// <summary>
/// What an encoder chose for one frame, as the uncompressed header of section 5.9 codes it: the fields the header writes; the
/// variables the specification derives from them are what <see cref="Av1FrameHeader.Parse"/> reads back. The defaults make a
/// shown key frame.
/// </summary>
internal sealed class Av1FrameParameters
{
    /// <summary>Gets or sets frame_type.</summary>
    internal Av1FrameType FrameType { get; set; } = Av1FrameType.Key;

    /// <summary>Gets or sets show_frame.</summary>
    internal bool ShowFrame { get; set; } = true;

    /// <summary>Gets or sets showable_frame, which a hidden frame writes (a shown one implies it).</summary>
    internal bool ShowableFrame { get; set; }

    /// <summary>Gets or sets error_resilient_mode, which a frame other than a shown key frame or a switch frame writes.</summary>
    internal bool ErrorResilientMode { get; set; }

    /// <summary>Gets or sets order_hint, in the sequence's order hint bits.</summary>
    internal int OrderHint { get; set; }

    /// <summary>Gets or sets primary_ref_frame: the reference (0 for LAST to 6 for ALTREF) whose CDFs and state load, or 7 for none.</summary>
    internal int PrimaryRefFrame { get; set; } = 7;

    /// <summary>Gets or sets refresh_frame_flags, which a frame other than a shown key frame or a switch frame writes.</summary>
    internal int RefreshFrameFlags { get; set; } = 0xFF;

    /// <summary>Gets each slot's order hint, which an error resilient frame that keeps references writes (ref_order_hint).</summary>
    internal int[] RefOrderHints { get; } = new int[8];

    /// <summary>Gets ref_frame_idx: the slot each of LAST to ALTREF reads.</summary>
    internal int[] RefFrameIdx { get; } = new int[7];

    internal bool AllowHighPrecisionMv { get; set; }

    /// <summary>Gets or sets interpolation_filter: <see cref="Interp.Switchable"/>, or one filter for the frame.</summary>
    internal int InterpolationFilter { get; set; } = Interp.Switchable;

    internal bool IsMotionModeSwitchable { get; set; }

    internal bool UseRefFrameMvs { get; set; }

    internal bool ReferenceSelect { get; set; }

    /// <summary>Gets or sets whether skip_mode_present is written: the frame's references allow skip mode (skipModeAllowed).</summary>
    internal bool SkipModeAllowed { get; set; }

    internal bool SkipModePresent { get; set; }

    internal bool AllowWarpedMotion { get; set; }

    /// <summary>Gets each reference's global motion type (<see cref="GlobalMotion"/>), LAST at 1.</summary>
    internal int[] GmType { get; } = new int[8];

    /// <summary>Gets each reference's global motion parameters, LAST at 1.</summary>
    internal int[][] GmParams { get; } = [.. Enumerable.Range(0, 8).Select(_ => Av1FrameHeader.DefaultWarpParams())];

    /// <summary>Gets or sets the primary reference frame's global motion parameters the frame's are coded against, LAST at 1; null for the defaults.</summary>
    internal int[][]? PreviousGmParams { get; set; }

    /// <summary>Gets or sets segmentation_update_map, which a frame with a primary reference frame writes.</summary>
    internal bool SegmentationUpdateMap { get; set; } = true;

    /// <summary>Gets or sets segmentation_update_data, which a frame with a primary reference frame writes.</summary>
    internal bool SegmentationUpdateData { get; set; } = true;

    /// <summary>Gets or sets the loop filter deltas the frame starts from: the primary reference frame's, or null for the defaults.</summary>
    internal int[]? PreviousLoopFilterRefDeltas { get; set; }

    /// <summary>Gets or sets the mode deltas the frame starts from, with <see cref="PreviousLoopFilterRefDeltas"/>.</summary>
    internal int[]? PreviousLoopFilterModeDeltas { get; set; }

    /// <summary>Gets or sets the reference slots the header is read back against, for a frame of a stream; null for a still.</summary>
    internal Av1Frame?[]? Slots { get; set; }

    /// <summary>Gets whether the frame is a key or intra-only frame.</summary>
    internal bool FrameIsIntra => FrameType is Av1FrameType.Key or Av1FrameType.IntraOnly;

    /// <summary>Gets or sets the frame's width; a full header writes it as a frame size override when it differs from the sequence's.</summary>
    internal int Width { get; set; }

    /// <summary>Gets or sets the frame's height.</summary>
    internal int Height { get; set; }

    internal bool DisableCdfUpdate { get; set; }

    internal bool AllowScreenContentTools { get; set; }

    internal bool ForceIntegerMv { get; set; }

    internal bool AllowIntrabc { get; set; }

    /// <summary>Gets or sets disable_frame_end_update_cdf, which a full header writes (a reduced one implies it).</summary>
    internal bool DisableFrameEndUpdateCdf { get; set; } = true;

    /// <summary>Gets or sets whether tiles are spaced uniformly; the log2 counts then give the layout.</summary>
    internal bool UniformTileSpacing { get; set; } = true;

    internal int TileColsLog2 { get; set; }

    internal int TileRowsLog2 { get; set; }

    /// <summary>Gets or sets the tile column widths in superblocks when the spacing is not uniform.</summary>
    internal int[] TileColWidthsSb { get; set; } = [];

    /// <summary>Gets or sets the tile row heights in superblocks when the spacing is not uniform.</summary>
    internal int[] TileRowHeightsSb { get; set; } = [];

    internal int ContextUpdateTileId { get; set; }

    /// <summary>Gets or sets how many bytes each tile size takes, 1 to 4.</summary>
    internal int TileSizeBytes { get; set; } = 4;

    internal int BaseQIdx { get; set; }

    internal int DeltaQYDc { get; set; }

    internal int DeltaQUDc { get; set; }

    internal int DeltaQUAc { get; set; }

    internal int DeltaQVDc { get; set; }

    internal int DeltaQVAc { get; set; }

    internal bool UsingQmatrix { get; set; }

    internal int QmY { get; set; } = 15;

    internal int QmU { get; set; } = 15;

    internal int QmV { get; set; } = 15;

    internal bool SegmentationEnabled { get; set; }

    /// <summary>Gets FeatureEnabled[segment][feature].</summary>
    internal bool[,] FeatureEnabled { get; } = new bool[8, 8];

    /// <summary>Gets FeatureData[segment][feature].</summary>
    internal int[,] FeatureData { get; } = new int[8, 8];

    internal bool DeltaQPresent { get; set; }

    internal int DeltaQRes { get; set; }

    internal bool DeltaLfPresent { get; set; }

    internal int DeltaLfRes { get; set; }

    internal bool DeltaLfMulti { get; set; }

    /// <summary>Gets loop_filter_level[0 to 3]: luma vertical and horizontal edges, then U and V.</summary>
    internal int[] LoopFilterLevel { get; } = new int[4];

    internal int LoopFilterSharpness { get; set; }

    internal bool LoopFilterDeltaEnabled { get; set; } = true;

    /// <summary>Gets or sets the reference deltas to send, or null to send none (loop_filter_delta_update 0).</summary>
    internal int[]? LoopFilterRefDeltas { get; set; }

    /// <summary>Gets or sets the mode deltas to send with <see cref="LoopFilterRefDeltas"/>.</summary>
    internal int[] LoopFilterModeDeltas { get; set; } = new int[2];

    internal int CdefDamping { get; set; } = 3;

    internal int CdefBits { get; set; }

    internal int[] CdefYPriStrength { get; } = new int[8];

    /// <summary>Gets the luma secondary strengths, 0, 1, 2 or 4.</summary>
    internal int[] CdefYSecStrength { get; } = new int[8];

    internal int[] CdefUvPriStrength { get; } = new int[8];

    internal int[] CdefUvSecStrength { get; } = new int[8];

    internal Av1RestorationType[] LrType { get; } = new Av1RestorationType[3];

    /// <summary>Gets or sets log2 of the luma restoration unit size minus 6: 0 (64), 1 (128) or 2 (256); 1 or 2 with 128x128 superblocks.</summary>
    internal int LrUnitShift { get; set; } = 2;

    /// <summary>Gets or sets whether 4:2:0 chroma restoration units are half the luma size.</summary>
    internal bool LrUvShift { get; set; }

    internal bool TxModeSelect { get; set; }

    internal bool ReducedTxSet { get; set; }

    /// <summary>Gets each segment's qindex, as the header derives it.</summary>
    internal int SegmentQIndex(int segment) => SegmentationEnabled && FeatureEnabled[segment, 0]
        ? Math.Clamp(BaseQIdx + FeatureData[segment, 0], 0, 255) : BaseQIdx;

    /// <summary>Gets CodedLossless: every segment at qindex 0 with no delta (a monochrome frame codes no chroma deltas).</summary>
    internal bool CodedLossless(bool monochrome)
    {
        if (DeltaQYDc != 0 || (!monochrome && (DeltaQUDc != 0 || DeltaQUAc != 0 || DeltaQVDc != 0 || DeltaQVAc != 0)))
        {
            return false;
        }

        for (int segment = 0; segment < 8; segment++)
        {
            if (SegmentQIndex(segment) != 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Writes AV1 OBUs (section 5): the sequence header from an <see cref="Av1SequenceHeader"/>, a key frame's uncompressed header
/// from <see cref="Av1FrameParameters"/>, and the tile group and OBU framing around the tiles' data. Each written header reads
/// back through <see cref="Av1SequenceHeader.Parse"/> and <see cref="Av1FrameHeader.Parse"/> with the same fields.
/// </summary>
internal static class Av1ObuWriter
{
    private static readonly int[] FeatureBits = [8, 6, 6, 6, 6, 3, 0, 0];
    private static readonly bool[] FeatureSigned = [true, true, true, true, true, false, false, false];

    /// <summary>Writes a sequence header OBU's payload (section 5.5), with its trailing bits.</summary>
    internal static byte[] SequenceHeader(Av1SequenceHeader s)
    {
        Av1BitWriter w = new();
        w.Bits(s.Profile, 3);
        w.Flag(s.StillPicture);
        w.Flag(s.ReducedStillPictureHeader);
        if (s.ReducedStillPictureHeader)
        {
            w.Bits(s.OperatingPoints[0].Level, 5);
        }
        else
        {
            w.Flag(false); // timing_info_present_flag
            w.Flag(false); // initial_display_delay_present_flag
            w.Bits(s.OperatingPoints.Length - 1, 5);
            foreach (Av1OperatingPoint point in s.OperatingPoints)
            {
                w.Bits(point.Idc, 12);
                w.Bits(point.Level, 5);
                if (point.Level > 7)
                {
                    w.Bits(point.Tier, 1);
                }
            }
        }

        w.Bits(s.FrameWidthBits - 1, 4);
        w.Bits(s.FrameHeightBits - 1, 4);
        w.Bits(s.MaxFrameWidth - 1, s.FrameWidthBits);
        w.Bits(s.MaxFrameHeight - 1, s.FrameHeightBits);
        if (!s.ReducedStillPictureHeader)
        {
            w.Flag(false); // frame_id_numbers_present_flag
        }

        w.Flag(s.Use128x128Superblock);
        w.Flag(s.EnableFilterIntra);
        w.Flag(s.EnableIntraEdgeFilter);
        if (!s.ReducedStillPictureHeader)
        {
            w.Flag(s.EnableInterintraCompound);
            w.Flag(s.EnableMaskedCompound);
            w.Flag(s.EnableWarpedMotion);
            w.Flag(s.EnableDualFilter);
            w.Flag(s.EnableOrderHint);
            if (s.EnableOrderHint)
            {
                w.Flag(s.EnableJntComp);
                w.Flag(s.EnableRefFrameMvs);
            }

            w.Flag(s.ForceScreenContentTools == 2);
            if (s.ForceScreenContentTools != 2)
            {
                w.Bits(s.ForceScreenContentTools, 1);
            }

            if (s.ForceScreenContentTools > 0)
            {
                w.Flag(s.ForceIntegerMv == 2);
                if (s.ForceIntegerMv != 2)
                {
                    w.Bits(s.ForceIntegerMv, 1);
                }
            }

            if (s.EnableOrderHint)
            {
                w.Bits(s.OrderHintBits - 1, 3);
            }
        }

        w.Flag(s.EnableSuperres);
        w.Flag(s.EnableCdef);
        w.Flag(s.EnableRestoration);

        // color_config (section 5.5.2)
        w.Flag(s.BitDepth > 8);
        if (s.Profile == 2 && s.BitDepth > 8)
        {
            w.Flag(s.BitDepth == 12);
        }

        if (s.Profile != 1)
        {
            w.Flag(s.Monochrome);
        }

        w.Flag(s.ColorDescriptionPresent);
        if (s.ColorDescriptionPresent)
        {
            w.Bits(s.ColorPrimaries, 8);
            w.Bits(s.TransferCharacteristics, 8);
            w.Bits(s.MatrixCoefficients, 8);
        }

        if (s.Monochrome)
        {
            w.Flag(s.FullRange);
        }
        else if (s.ColorPrimaries == 1 && s.TransferCharacteristics == 13 && s.MatrixCoefficients == 0)
        {
            w.Flag(s.SeparateUvDeltaQ);
        }
        else
        {
            w.Flag(s.FullRange);
            if (s.Profile == 2 && s.BitDepth == 12)
            {
                w.Bits(s.SubsamplingX, 1);
                if (s.SubsamplingX == 1)
                {
                    w.Bits(s.SubsamplingY, 1);
                }
            }

            if (s.SubsamplingX == 1 && s.SubsamplingY == 1)
            {
                w.Bits(s.ChromaSamplePosition, 2);
            }

            w.Flag(s.SeparateUvDeltaQ);
        }

        w.Flag(s.FilmGrainParamsPresent);
        w.TrailingBits();
        return w.ToArray();
    }

    /// <summary>
    /// Writes a frame's uncompressed header (section 5.9) into <paramref name="w"/>, without trailing bits or byte alignment: a
    /// frame OBU follows it with byte_alignment and the tile group, a frame header OBU with trailing_bits. The sequence has no
    /// frame ids or decoder model, and inter frames name their references in full.
    /// </summary>
    internal static void FrameHeader(Av1BitWriter w, Av1SequenceHeader s, Av1FrameParameters f)
    {
        int numPlanes = s.Monochrome ? 1 : 3;
        bool intra = f.FrameIsIntra;
        bool shownKey = f.FrameType == Av1FrameType.Key && f.ShowFrame;
        bool errorResilient = f.FrameType == Av1FrameType.Switch || shownKey || f.ErrorResilientMode;
        if (!s.ReducedStillPictureHeader)
        {
            w.Flag(false); // show_existing_frame
            w.Bits((int)f.FrameType, 2);
            w.Flag(f.ShowFrame);
            if (!f.ShowFrame)
            {
                w.Flag(f.ShowableFrame);
            }

            if (f.FrameType != Av1FrameType.Switch && !shownKey)
            {
                w.Flag(f.ErrorResilientMode);
            }
        }
        else if (!shownKey)
        {
            throw new ArgumentException("A reduced still picture header codes one shown key frame.", nameof(f));
        }

        w.Flag(f.DisableCdfUpdate);
        bool screenContent = s.ForceScreenContentTools == 2 ? f.AllowScreenContentTools : s.ForceScreenContentTools == 1;
        if (s.ForceScreenContentTools == 2)
        {
            w.Flag(f.AllowScreenContentTools);
        }

        if (screenContent && s.ForceIntegerMv == 2)
        {
            w.Flag(f.ForceIntegerMv);
        }

        bool forceIntegerMv = intra || (screenContent && (s.ForceIntegerMv == 2 ? f.ForceIntegerMv : s.ForceIntegerMv == 1));
        bool overrideSize = f.FrameType == Av1FrameType.Switch || f.Width != s.MaxFrameWidth || f.Height != s.MaxFrameHeight;
        if (!s.ReducedStillPictureHeader)
        {
            if (f.FrameType != Av1FrameType.Switch)
            {
                w.Flag(overrideSize);
            }

            w.Bits(f.OrderHint, s.OrderHintBits);
            if (!intra && !errorResilient)
            {
                w.Bits(f.PrimaryRefFrame, 3);
            }

            if (f.FrameType != Av1FrameType.Switch && !shownKey)
            {
                w.Bits(f.RefreshFrameFlags, 8);
            }

            if ((!intra || f.RefreshFrameFlags != 0xFF) && errorResilient && s.EnableOrderHint)
            {
                for (int i = 0; i < 8; i++)
                {
                    w.Bits(f.RefOrderHints[i], s.OrderHintBits);
                }
            }
        }
        else if (overrideSize)
        {
            throw new ArgumentException("A reduced still picture header codes the sequence's size only.", nameof(f));
        }

        if (intra)
        {
            FrameSize(w, s, f, overrideSize);
            if (screenContent)
            {
                w.Flag(f.AllowIntrabc);
            }
        }
        else
        {
            if (s.EnableOrderHint)
            {
                w.Flag(false); // frame_refs_short_signaling
            }

            for (int i = 0; i < 7; i++)
            {
                w.Bits(f.RefFrameIdx[i], 3);
            }

            if (overrideSize && !errorResilient)
            {
                for (int i = 0; i < 7; i++)
                {
                    w.Flag(false); // found_ref
                }
            }

            FrameSize(w, s, f, overrideSize);
            if (!forceIntegerMv)
            {
                w.Flag(f.AllowHighPrecisionMv);
            }

            w.Flag(f.InterpolationFilter == Interp.Switchable);
            if (f.InterpolationFilter != Interp.Switchable)
            {
                w.Bits(f.InterpolationFilter, 2);
            }

            w.Flag(f.IsMotionModeSwitchable);
            if (!errorResilient && s.EnableRefFrameMvs)
            {
                w.Flag(f.UseRefFrameMvs);
            }
        }

        if (!s.ReducedStillPictureHeader && !f.DisableCdfUpdate)
        {
            w.Flag(f.DisableFrameEndUpdateCdf);
        }

        TileInfo(w, s, f);

        // quantization_params
        w.Bits(f.BaseQIdx, 8);
        DeltaQ(w, f.DeltaQYDc);
        if (numPlanes > 1)
        {
            bool diffUv = f.DeltaQUDc != f.DeltaQVDc || f.DeltaQUAc != f.DeltaQVAc;
            if (diffUv && !s.SeparateUvDeltaQ)
            {
                throw new ArgumentException("Different U and V quantizer deltas need separate_uv_delta_q.", nameof(f));
            }

            if (s.SeparateUvDeltaQ)
            {
                w.Flag(diffUv);
            }

            DeltaQ(w, f.DeltaQUDc);
            DeltaQ(w, f.DeltaQUAc);
            if (diffUv)
            {
                DeltaQ(w, f.DeltaQVDc);
                DeltaQ(w, f.DeltaQVAc);
            }
        }

        w.Flag(f.UsingQmatrix);
        if (f.UsingQmatrix)
        {
            w.Bits(f.QmY, 4);
            w.Bits(f.QmU, 4);
            if (s.SeparateUvDeltaQ)
            {
                w.Bits(f.QmV, 4);
            }
        }

        // segmentation_params: a frame without a primary reference frame sends the map and the data.
        bool primary = !intra && !errorResilient && f.PrimaryRefFrame != 7;
        w.Flag(f.SegmentationEnabled);
        if (f.SegmentationEnabled && primary)
        {
            w.Flag(f.SegmentationUpdateMap);
            if (f.SegmentationUpdateMap)
            {
                w.Flag(false); // segmentation_temporal_update
            }

            w.Flag(f.SegmentationUpdateData);
        }

        if (f.SegmentationEnabled && (!primary || f.SegmentationUpdateData))
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    w.Flag(f.FeatureEnabled[i, j]);
                    if (f.FeatureEnabled[i, j])
                    {
                        if (FeatureSigned[j])
                        {
                            w.Signed(f.FeatureData[i, j], 1 + FeatureBits[j]);
                        }
                        else
                        {
                            w.Bits(f.FeatureData[i, j], FeatureBits[j]);
                        }
                    }
                }
            }
        }

        // delta_q_params and delta_lf_params
        if (f.BaseQIdx > 0)
        {
            w.Flag(f.DeltaQPresent);
        }

        if (f.BaseQIdx > 0 && f.DeltaQPresent)
        {
            w.Bits(f.DeltaQRes, 2);
            if (!f.AllowIntrabc)
            {
                w.Flag(f.DeltaLfPresent);
                if (f.DeltaLfPresent)
                {
                    w.Bits(f.DeltaLfRes, 2);
                    w.Flag(f.DeltaLfMulti);
                }
            }
        }

        bool codedLossless = f.CodedLossless(s.Monochrome);
        bool allLossless = codedLossless;

        // loop_filter_params
        if (!codedLossless && !f.AllowIntrabc)
        {
            w.Bits(f.LoopFilterLevel[0], 6);
            w.Bits(f.LoopFilterLevel[1], 6);
            if (numPlanes > 1 && (f.LoopFilterLevel[0] != 0 || f.LoopFilterLevel[1] != 0))
            {
                w.Bits(f.LoopFilterLevel[2], 6);
                w.Bits(f.LoopFilterLevel[3], 6);
            }

            w.Bits(f.LoopFilterSharpness, 3);
            w.Flag(f.LoopFilterDeltaEnabled);
            if (f.LoopFilterDeltaEnabled)
            {
                w.Flag(f.LoopFilterRefDeltas is not null);
                if (f.LoopFilterRefDeltas is int[] refDeltas)
                {
                    ReadOnlySpan<int> defaults = f.PreviousLoopFilterRefDeltas ?? [1, 0, 0, 0, -1, 0, -1, -1];
                    ReadOnlySpan<int> modeDefaults = f.PreviousLoopFilterModeDeltas ?? [0, 0];
                    for (int i = 0; i < 8; i++)
                    {
                        w.Flag(refDeltas[i] != defaults[i]);
                        if (refDeltas[i] != defaults[i])
                        {
                            w.Signed(refDeltas[i], 7);
                        }
                    }

                    for (int i = 0; i < 2; i++)
                    {
                        w.Flag(f.LoopFilterModeDeltas[i] != modeDefaults[i]);
                        if (f.LoopFilterModeDeltas[i] != modeDefaults[i])
                        {
                            w.Signed(f.LoopFilterModeDeltas[i], 7);
                        }
                    }
                }
            }
        }

        // cdef_params
        if (!codedLossless && !f.AllowIntrabc && s.EnableCdef)
        {
            w.Bits(f.CdefDamping - 3, 2);
            w.Bits(f.CdefBits, 2);
            for (int i = 0; i < 1 << f.CdefBits; i++)
            {
                w.Bits(f.CdefYPriStrength[i], 4);
                w.Bits(Secondary(f.CdefYSecStrength[i]), 2);
                if (numPlanes > 1)
                {
                    w.Bits(f.CdefUvPriStrength[i], 4);
                    w.Bits(Secondary(f.CdefUvSecStrength[i]), 2);
                }
            }
        }

        // lr_params
        if (!allLossless && !f.AllowIntrabc && s.EnableRestoration)
        {
            bool usesLr = false, usesChromaLr = false;
            for (int i = 0; i < numPlanes; i++)
            {
                w.Bits((int)f.LrType[i], 2);
                if (f.LrType[i] != Av1RestorationType.None)
                {
                    usesLr = true;
                    usesChromaLr |= i > 0;
                }
            }

            if (usesLr)
            {
                if (s.Use128x128Superblock)
                {
                    if (f.LrUnitShift is not (1 or 2))
                    {
                        throw new ArgumentException("With 128x128 superblocks the restoration units are 128 or 256 samples.", nameof(f));
                    }

                    w.Bits(f.LrUnitShift - 1, 1);
                }
                else
                {
                    w.Flag(f.LrUnitShift > 0);
                    if (f.LrUnitShift > 0)
                    {
                        w.Bits(f.LrUnitShift - 1, 1);
                    }
                }

                if (s.SubsamplingX == 1 && s.SubsamplingY == 1 && usesChromaLr)
                {
                    w.Flag(f.LrUvShift);
                }
            }
        }

        // read_tx_mode
        if (!codedLossless)
        {
            w.Flag(f.TxModeSelect);
        }

        if (!intra)
        {
            w.Flag(f.ReferenceSelect);
        }

        if (f.SkipModeAllowed)
        {
            w.Flag(f.SkipModePresent);
        }

        if (!intra && !errorResilient && s.EnableWarpedMotion)
        {
            w.Flag(f.AllowWarpedMotion);
        }

        w.Flag(f.ReducedTxSet);
        if (!intra)
        {
            GlobalMotionParams(w, f, !forceIntegerMv && f.AllowHighPrecisionMv);
        }

        if (s.FilmGrainParamsPresent && (f.ShowFrame || f.ShowableFrame))
        {
            w.Flag(false); // apply_grain
        }

        static int Secondary(int strength) => strength == 4 ? 3 : strength;
    }

    /// <summary>Writes frame_size, superres_params (off) and render_size (the frame's own).</summary>
    private static void FrameSize(Av1BitWriter w, Av1SequenceHeader s, Av1FrameParameters f, bool overrideSize)
    {
        if (overrideSize)
        {
            w.Bits(f.Width - 1, s.FrameWidthBits);
            w.Bits(f.Height - 1, s.FrameHeightBits);
        }

        if (s.EnableSuperres)
        {
            w.Flag(false); // use_superres
        }

        w.Flag(false); // render_and_frame_size_different
    }

    /// <summary>Writes global_motion_params, each reference's against the primary reference frame's.</summary>
    private static void GlobalMotionParams(Av1BitWriter w, Av1FrameParameters f, bool allowHp)
    {
        const int alphaMax = 1 << 12, alphaPrecDiff = Av1FrameHeader.WarpPrecBits - 15;
        for (int frame = Ref.Last; frame <= Ref.Alt; frame++)
        {
            int[] prev = f.PreviousGmParams?[frame] ?? Av1FrameHeader.DefaultWarpParams();
            int type = f.GmType[frame];
            int[] p = f.GmParams[frame];
            w.Flag(type != GlobalMotion.Identity);
            if (type != GlobalMotion.Identity)
            {
                w.Flag(type == GlobalMotion.RotZoom);
                if (type != GlobalMotion.RotZoom)
                {
                    w.Flag(type == GlobalMotion.Translation);
                }
            }

            if (type >= GlobalMotion.RotZoom)
            {
                SignedRefSubexpFin(w, alphaMax + 1, (prev[2] >> alphaPrecDiff) - (1 << 15), (p[2] >> alphaPrecDiff) - (1 << 15));
                SignedRefSubexpFin(w, alphaMax + 1, prev[3] >> alphaPrecDiff, p[3] >> alphaPrecDiff);
            }

            if (type >= GlobalMotion.Affine)
            {
                SignedRefSubexpFin(w, alphaMax + 1, prev[4] >> alphaPrecDiff, p[4] >> alphaPrecDiff);
                SignedRefSubexpFin(w, alphaMax + 1, (prev[5] >> alphaPrecDiff) - (1 << 15), (p[5] >> alphaPrecDiff) - (1 << 15));
            }

            if (type >= GlobalMotion.Translation)
            {
                int hp = allowHp ? 0 : 1;
                int transBits = type == GlobalMotion.Translation ? 9 - hp : 12;
                int precDiff = type == GlobalMotion.Translation ? Av1FrameHeader.WarpPrecBits - 3 + hp : Av1FrameHeader.WarpPrecBits - 6;
                SignedRefSubexpFin(w, (1 << transBits) + 1, prev[0] >> precDiff, p[0] >> precDiff);
                SignedRefSubexpFin(w, (1 << transBits) + 1, prev[1] >> precDiff, p[1] >> precDiff);
            }
        }
    }

    /// <summary>aom_wb_write_signed_primitive_refsubexpfin with k = 3: a value in [-(n - 1), n - 1] coded against a reference.</summary>
    internal static void SignedRefSubexpFin(Av1BitWriter w, int n, int reference, int value)
    {
        reference += n - 1;
        value += n - 1;
        int scaledN = (2 * n) - 1;
        if (value < 0 || value >= scaledN)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A global motion parameter is out of range.");
        }

        int v = (reference << 1) <= scaledN ? Recenter(reference, value) : Recenter(scaledN - 1 - reference, scaledN - 1 - value);
        const int k = 3;
        int i = 0, mk = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (scaledN <= mk + (3 * a))
            {
                w.NonSymmetric((uint)(v - mk), (uint)(scaledN - mk));
                return;
            }

            bool more = v >= mk + a;
            w.Flag(more);
            if (!more)
            {
                w.Bits(v - mk, b);
                return;
            }

            i++;
            mk += a;
        }

        static int Recenter(int r, int v) => v > r << 1 ? v : v >= r ? (v - r) << 1 : ((r - v) << 1) - 1;
    }

    /// <summary>Writes a show_existing_frame header (section 5.9.1) as a frame header OBU's payload, with its trailing bits.</summary>
    internal static byte[] ShowExistingFrame(int slot)
    {
        Av1BitWriter w = new();
        w.Flag(true);
        w.Bits(slot, 3);
        w.TrailingBits();
        return w.ToArray();
    }

    /// <summary>
    /// Builds a frame OBU's payload (section 5.10): the header, byte_alignment, then one tile group with every tile, each but the
    /// last after its size in <see cref="Av1FrameParameters.TileSizeBytes"/> little-endian bytes.
    /// </summary>
    internal static byte[] Frame(Av1SequenceHeader s, Av1FrameParameters f, IReadOnlyList<byte[]> tiles)
    {
        Av1BitWriter w = new();
        FrameHeader(w, s, f);
        w.ByteAlign();
        if (tiles.Count > 1)
        {
            w.Flag(false); // tile_start_and_end_present_flag
            w.ByteAlign();
        }

        List<byte> payload = [.. w.ToArray()];
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i < tiles.Count - 1)
            {
                int size = tiles[i].Length - 1;
                if (f.TileSizeBytes < 4 && size >= 1 << (8 * f.TileSizeBytes))
                {
                    throw new ArgumentException($"A tile of {tiles[i].Length} bytes does not fit a {f.TileSizeBytes}-byte size.", nameof(f));
                }

                for (int b = 0; b < f.TileSizeBytes; b++)
                {
                    payload.Add((byte)(size >> (8 * b)));
                }
            }

            payload.AddRange(tiles[i]);
        }

        return [.. payload];
    }

    /// <summary>Frames a payload as an OBU with obu_has_size_field set and no extension (section 5.3).</summary>
    internal static void Obu(List<byte> output, Av1ObuType type, ReadOnlySpan<byte> payload) => Obu(output, type, payload, null);

    /// <summary>
    /// Frames a payload as an OBU with obu_has_size_field set and, when <paramref name="spatialId"/> is given, the extension header
    /// (temporal_id 0 and that spatial_id) that the frames of a stream with spatial layers carry (section 5.3.3).
    /// </summary>
    internal static void Obu(List<byte> output, Av1ObuType type, ReadOnlySpan<byte> payload, int? spatialId)
    {
        output.Add((byte)(((int)type << 3) | (spatialId is null ? 0 : 4) | 2));
        if (spatialId is int id)
        {
            output.Add((byte)((id & 3) << 3));
        }

        Av1BitWriter.Leb128(output, (ulong)payload.Length);
        foreach (byte b in payload)
        {
            output.Add(b);
        }
    }

    /// <summary>Gets the smallest TileSizeBytes that holds every tile's size but the last.</summary>
    internal static int TileSizeBytesFor(IReadOnlyList<byte[]> tiles)
    {
        int largest = 0;
        for (int i = 0; i < tiles.Count - 1; i++)
        {
            largest = Math.Max(largest, tiles[i].Length - 1);
        }

        return largest < 1 << 8 ? 1 : largest < 1 << 16 ? 2 : largest < 1 << 24 ? 3 : 4;
    }

    private static void TileInfo(Av1BitWriter w, Av1SequenceHeader s, Av1FrameParameters f)
    {
        bool sb128 = s.Use128x128Superblock;
        int miCols = 2 * ((f.Width + 7) >> 3), miRows = 2 * ((f.Height + 7) >> 3);
        int sbCols = sb128 ? (miCols + 31) >> 5 : (miCols + 15) >> 4;
        int sbRows = sb128 ? (miRows + 31) >> 5 : (miRows + 15) >> 4;
        int sbSize = (sb128 ? 5 : 4) + 2;
        int maxTileWidthSb = 4096 >> sbSize;
        int maxTileAreaSb = (4096 * 2304) >> (2 * sbSize);
        int minLog2TileCols = Av1FrameHeader.TileLog2(maxTileWidthSb, sbCols);
        int maxLog2TileCols = Av1FrameHeader.TileLog2(1, Math.Min(sbCols, 64));
        int maxLog2TileRows = Av1FrameHeader.TileLog2(1, Math.Min(sbRows, 64));
        int minLog2Tiles = Math.Max(minLog2TileCols, Av1FrameHeader.TileLog2(maxTileAreaSb, sbRows * sbCols));
        w.Flag(f.UniformTileSpacing);
        int colsLog2, rowsLog2;
        if (f.UniformTileSpacing)
        {
            colsLog2 = Math.Clamp(f.TileColsLog2, minLog2TileCols, maxLog2TileCols);
            for (int i = minLog2TileCols; i < maxLog2TileCols; i++)
            {
                w.Flag(i < colsLog2);
                if (i >= colsLog2)
                {
                    break;
                }
            }

            int minLog2TileRows = Math.Max(minLog2Tiles - colsLog2, 0);
            rowsLog2 = Math.Clamp(f.TileRowsLog2, minLog2TileRows, maxLog2TileRows);
            for (int i = minLog2TileRows; i < maxLog2TileRows; i++)
            {
                w.Flag(i < rowsLog2);
                if (i >= rowsLog2)
                {
                    break;
                }
            }

            if (colsLog2 != f.TileColsLog2 || rowsLog2 != f.TileRowsLog2)
            {
                throw new ArgumentException($"The frame cannot have {1 << f.TileColsLog2} x {1 << f.TileRowsLog2} uniform tiles.", nameof(f));
            }
        }
        else
        {
            int widestTileSb = 0, start = 0;
            foreach (int width in f.TileColWidthsSb)
            {
                int maxWidth = Math.Min(sbCols - start, maxTileWidthSb);
                w.NonSymmetric((uint)(width - 1), (uint)maxWidth);
                widestTileSb = Math.Max(widestTileSb, width);
                start += width;
            }

            int maxAreaSb = minLog2Tiles > 0 ? (sbRows * sbCols) >> (minLog2Tiles + 1) : sbRows * sbCols;
            int maxTileHeightSb = Math.Max(maxAreaSb / widestTileSb, 1);
            start = 0;
            foreach (int height in f.TileRowHeightsSb)
            {
                int maxHeight = Math.Min(sbRows - start, maxTileHeightSb);
                w.NonSymmetric((uint)(height - 1), (uint)maxHeight);
                start += height;
            }

            colsLog2 = Av1FrameHeader.TileLog2(1, f.TileColWidthsSb.Length);
            rowsLog2 = Av1FrameHeader.TileLog2(1, f.TileRowHeightsSb.Length);
        }

        if (colsLog2 > 0 || rowsLog2 > 0)
        {
            w.Bits(f.ContextUpdateTileId, rowsLog2 + colsLog2);
            w.Bits(f.TileSizeBytes - 1, 2);
        }
    }

    private static void DeltaQ(Av1BitWriter w, int delta)
    {
        w.Flag(delta != 0);
        if (delta != 0)
        {
            w.Signed(delta, 7);
        }
    }
}
