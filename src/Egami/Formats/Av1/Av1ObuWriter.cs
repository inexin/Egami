namespace Egami.Formats.Av1;

/// <summary>
/// What an encoder chose for one key frame, as the uncompressed header of section 5.9 codes it: the fields the header writes;
/// the variables the specification derives from them are what <see cref="Av1FrameHeader.Parse"/> reads back.
/// </summary>
internal sealed class Av1FrameParameters
{
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
    /// Writes a shown key frame's uncompressed header (section 5.9) into <paramref name="w"/>, without trailing bits or byte
    /// alignment: a frame OBU follows it with byte_alignment and the tile group, a frame header OBU with trailing_bits.
    /// </summary>
    internal static void FrameHeader(Av1BitWriter w, Av1SequenceHeader s, Av1FrameParameters f)
    {
        int numPlanes = s.Monochrome ? 1 : 3;
        if (!s.ReducedStillPictureHeader)
        {
            w.Flag(false); // show_existing_frame
            w.Bits((int)Av1FrameType.Key, 2);
            w.Flag(true); // show_frame; a shown key frame is error resilient by definition
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

        bool overrideSize = f.Width != s.MaxFrameWidth || f.Height != s.MaxFrameHeight;
        if (!s.ReducedStillPictureHeader)
        {
            w.Flag(overrideSize);
            w.Bits(0, s.OrderHintBits);
        }
        else if (overrideSize)
        {
            throw new ArgumentException("A reduced still picture header codes the sequence's size only.", nameof(f));
        }

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
        if (screenContent)
        {
            w.Flag(f.AllowIntrabc);
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

        // segmentation_params: an intra frame sends the map and the data.
        w.Flag(f.SegmentationEnabled);
        if (f.SegmentationEnabled)
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
                    ReadOnlySpan<int> defaults = [1, 0, 0, 0, -1, 0, -1, -1];
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
                        w.Flag(f.LoopFilterModeDeltas[i] != 0);
                        if (f.LoopFilterModeDeltas[i] != 0)
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

        w.Flag(f.ReducedTxSet);
        if (s.FilmGrainParamsPresent)
        {
            w.Flag(false); // apply_grain
        }

        static int Secondary(int strength) => strength == 4 ? 3 : strength;
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
    internal static void Obu(List<byte> output, Av1ObuType type, ReadOnlySpan<byte> payload)
    {
        output.Add((byte)(((int)type << 3) | 2));
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
