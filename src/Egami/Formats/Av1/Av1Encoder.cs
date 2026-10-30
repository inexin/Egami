using Egami.Pixels;

namespace Egami.Formats.Av1;

/// <summary>The planes an AV1 frame is encoded from: samples at the bit depth, each plane row by row.</summary>
internal sealed class Av1EncoderInput
{
    internal required int Width { get; init; }

    internal required int Height { get; init; }

    internal required int BitDepth { get; init; }

    /// <summary>Gets the chroma format: 4:0:0 has one plane, the others three.</summary>
    internal required ChromaFormat Chroma { get; init; }

    /// <summary>Gets the planes, Y (or G) then U and V (or B and R), each <c>width</c> samples a row at its subsampled size.</summary>
    internal required ushort[][] Planes { get; init; }

    /// <summary>Gets the CICP color primaries the sequence header names.</summary>
    internal int ColorPrimaries { get; init; } = 2;

    internal int TransferCharacteristics { get; init; } = 2;

    internal int MatrixCoefficients { get; init; } = 2;

    internal bool FullRange { get; init; }

    /// <summary>Gets the AV1 chroma_sample_position of 4:2:0 planes: 0 unknown, 1 vertical (left), 2 colocated (top left).</summary>
    internal int ChromaSamplePosition { get; init; }
}

/// <summary>How an AV1 frame is encoded.</summary>
internal sealed class Av1EncoderSettings
{
    /// <summary>Gets or sets whether every sample is kept exactly: quantizer 0 and the Walsh-Hadamard transform.</summary>
    internal bool Lossless { get; init; }

    /// <summary>Gets or sets base_q_idx, 0 to 255; 0 codes the frame losslessly in its own planes.</summary>
    internal int QIndex { get; init; } = 100;

    /// <summary>Gets or sets how hard the search works, 0 to 10 (<see cref="Av1SearchProfile"/>).</summary>
    internal int Effort { get; init; } = 4;

    /// <summary>Gets or sets whether screen content tools (palettes) are on; null detects screen content (<see cref="Av1ScreenContent"/>).</summary>
    internal bool? ScreenContent { get; init; }

    /// <summary>Gets or sets whether intra block copy is on, with screen content tools; null decides with <see cref="Av1ScreenContent"/>.</summary>
    internal bool? IntraBlockCopy { get; init; }

    /// <summary>Gets or sets how many threads tiles may use.</summary>
    internal int Parallelism { get; init; } = 1;

    /// <summary>Gets whether the frame is one of a stream's (<see cref="Av1StreamEncoder"/>), not a still picture.</summary>
    internal bool Stream { get; init; }

    /// <summary>Gets whether later frames predict from the frame; one that is not searches with fewer tools (<see cref="Av1SearchProfile.For"/>).</summary>
    internal bool Reference { get; init; } = true;

    internal CancellationToken CancellationToken { get; init; }
}

/// <summary>The source planes, padded to the frame's superblock-aligned planes by repeating the last column and row.</summary>
internal sealed class Av1EncoderSource
{
    internal Av1EncoderSource(Av1EncoderInput input, Av1Frame frame)
    {
        int planes = frame.Header.NumPlanes;
        Planes = new ushort[planes][];
        Widths = new int[planes];
        Heights = new int[planes];
        for (int plane = 0; plane < planes; plane++)
        {
            int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
            int w = (input.Width + ssx) >> ssx, h = (input.Height + ssy) >> ssy;
            int stride = frame.Strides[plane];
            int rows = frame.Planes![plane].Length / stride;
            ushort[] padded = new ushort[stride * rows];
            ushort[] source = input.Planes[plane];
            for (int y = 0; y < rows; y++)
            {
                ReadOnlySpan<ushort> row = source.AsSpan(Math.Min(y, h - 1) * w, w);
                Span<ushort> target = padded.AsSpan(y * stride, stride);
                row.CopyTo(target);
                target[w..].Fill(row[w - 1]);
            }

            Planes[plane] = padded;
            Widths[plane] = w;
            Heights[plane] = h;
        }
    }

    /// <summary>Gets each plane, at the frame's stride.</summary>
    internal ushort[][] Planes { get; }

    /// <summary>Gets the columns of 16x16 luma cells in <see cref="RdScale"/>.</summary>
    internal int RdScaleColumns { get; private set; }

    /// <summary>
    /// Gets each 16x16 luma cell's rate-distortion multiplier scale, after libaom's av1_set_mb_ssim_rdmult_scaling
    /// (encoder_utils.c), which tune IQ uses: an exponential fit of the mean variance of its 8x8 blocks, divided by the frame's
    /// geometric mean, so flat cells spend more bits for their quality and busy ones fewer.
    /// </summary>
    internal double[] RdScale { get; private set; } = [];

    /// <summary>Computes <see cref="RdScale"/> from the luma plane.</summary>
    internal void ComputeRdScale(int miRows, int miCols, int bitDepth)
    {
        int cols = (miCols + 3) / 4, rows = (miRows + 3) / 4;
        double[] scale = new double[cols * rows];
        ushort[] luma = Planes[0];
        int stride = luma.Length / (((miRows * 4) + 63) & ~63);
        double logSum = 0;
        int shift = 2 * (bitDepth - 8);
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                double variance = 0;
                int count = 0;
                for (int mr = row * 4; mr < miRows && mr < (row + 1) * 4; mr += 2)
                {
                    for (int mc = col * 4; mc < miCols && mc < (col + 1) * 4; mc += 2)
                    {
                        long sum = 0, squares = 0;
                        for (int y = 0; y < 8; y++)
                        {
                            int at = (((mr * 4) + y) * stride) + (mc * 4);
                            for (int x = 0; x < 8; x++)
                            {
                                int v = luma[at + x];
                                sum += v;
                                squares += v * v;
                            }
                        }

                        variance += (double)((squares - ((sum * sum) >> 6)) >> shift) / 64;
                        count++;
                    }
                }

                double v8 = (67.035434 * (1 - Math.Exp(-0.0021489 * (variance / count)))) + 17.492222;
                scale[(row * cols) + col] = v8;
                logSum += Math.Log(v8);
            }
        }

        double mean = Math.Exp(logSum / scale.Length);
        for (int i = 0; i < scale.Length; i++)
        {
            scale[i] /= mean;
        }

        (RdScale, RdScaleColumns) = (scale, cols);
    }

    /// <summary>Gets each superblock's quantizer before its delta is rounded, in raster order, when the frame codes deltas.</summary>
    internal int[]? SuperblockQIndices { get; set; }

    /// <summary>Gets the superblock columns of <see cref="SuperblockQIndices"/>.</summary>
    internal int SuperblockColumns { get; set; }

    /// <summary>Gets the bordered luma of each reference frame an inter frame searches, LAST at 1.</summary>
    internal Av1ReferencePlane?[] ReferencePlanes { get; } = new Av1ReferencePlane?[8];

    /// <summary>Gets or sets the reference frames an inter block searches, each a different frame, nearest first.</summary>
    internal int[] SearchReferences { get; set; } = [];

    /// <summary>Gets the motion pre-pass of an inter frame: each 16x16 luma cell's vector against each searched reference.</summary>
    internal Av1MotionPrepass? Prepass { get; set; }

    /// <summary>Gets the hashes of the luma blocks, when the frame allows intra block copy.</summary>
    internal Av1BlockHash? Hash { get; set; }

    /// <summary>Gets each plane's width inside the image.</summary>
    internal int[] Widths { get; }

    /// <summary>Gets each plane's height inside the image.</summary>
    internal int[] Heights { get; }
}

/// <summary>
/// The AV1 intra encoder (Phase 2.9.6): one key frame from a set of planes, as a low-overhead stream of a sequence header OBU and
/// a frame OBU, the way an AVIF image item holds it. Internal beside the decoder until Egami.Video adds inter frames (design
/// question 2). The frame is decided tile by tile (<see cref="Av1TileEncoder"/>), reconstructed with the decoder's own code as it
/// goes, filtered with the decoder's loop filters, then written (<see cref="Av1TileWriter"/>); <see cref="Reconstruction"/> is
/// what every conforming decoder outputs.
/// </summary>
internal sealed class Av1Encoder
{
    private Av1Encoder(byte[] stream, Av1SequenceHeader sequence, Av1Frame frame, double estimatedBits, double tileBits)
    {
        Stream = stream;
        Sequence = sequence;
        Reconstruction = frame;
        EstimatedBits = estimatedBits;
        TileBits = tileBits;
    }

    /// <summary>Gets the bits the search estimated the tiles' symbols at, each superblock from the CDFs as it began.</summary>
    internal double EstimatedBits { get; }

    /// <summary>Gets the bits the tiles' data takes.</summary>
    internal double TileBits { get; }

    /// <summary>Gets the stream: the sequence header OBU, then the frame OBU.</summary>
    internal byte[] Stream { get; }

    /// <summary>Gets the sequence header, for the av1C property.</summary>
    internal Av1SequenceHeader Sequence { get; }

    /// <summary>Gets the frame as a decoder reconstructs it, its planes filtered.</summary>
    internal Av1Frame Reconstruction { get; }

    /// <summary>Gets the sequence header OBU alone, as av1C's configOBUs holds it.</summary>
    internal byte[] SequenceHeaderObu
    {
        get
        {
            List<byte> obu = [];
            Av1ObuWriter.Obu(obu, Av1ObuType.SequenceHeader, Av1ObuWriter.SequenceHeader(Sequence));
            return [.. obu];
        }
    }

    /// <summary>Encodes one frame.</summary>
    internal static Av1Encoder Encode(Av1EncoderInput input, Av1EncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        Av1SequenceHeader sequence = SequenceFor(input);
        Av1FrameParameters parameters = ParametersFor(input, sequence, settings);
        (byte[] payload, Av1Frame frame, double estimated, double tileBits) = EncodeFrame(input, settings, sequence, parameters, [], []);
        List<byte> stream = [];
        Av1ObuWriter.Obu(stream, Av1ObuType.SequenceHeader, Av1ObuWriter.SequenceHeader(sequence));
        Av1ObuWriter.Obu(stream, Av1ObuType.Frame, payload);
        return new Av1Encoder([.. stream], sequence, frame, estimated, tileBits);
    }

    /// <summary>
    /// Encodes one frame of a sequence as its parameters say; an inter frame reads its slots (<see cref="Av1FrameParameters.Slots"/>)
    /// and searches <paramref name="searchReferences"/> in their bordered luma, <paramref name="referencePlanes"/> (LAST at 1).
    /// Returns the frame OBU's payload and the frame as a decoder reconstructs it, with the CDFs it saves for the frames after it.
    /// </summary>
    internal static (byte[] Payload, Av1Frame Frame, double EstimatedBits, double TileBits) EncodeFrame(Av1EncoderInput input, Av1EncoderSettings settings,
        Av1SequenceHeader sequence, Av1FrameParameters parameters, Av1ReferencePlane?[] referencePlanes, int[] searchReferences)
    {
        Av1BitWriter bits = new();
        Av1ObuWriter.FrameHeader(bits, sequence, parameters);
        Av1FrameHeader header = Av1FrameHeader.Parse(bits.ToArray(), sequence, 0, 0, "AV1", parameters.Slots is null ? null : (Av1Frame?[])parameters.Slots.Clone());
        Av1Frame frame = new(header, "AV1", reconstruct: true, compact: false)
        {
            Parallelism = Math.Max(1, settings.Parallelism),
            CancellationToken = settings.CancellationToken,
            Showable = header.ShowableFrame,
        };
        if (header.UseRefFrameMvs)
        {
            Av1MotionField.Setup(frame);
        }

        Av1EncoderSource source = new(input, frame);
        referencePlanes.CopyTo(source.ReferencePlanes, 0);
        source.SearchReferences = searchReferences;

        if (header.DeltaQPresent)
        {
            int sbCols = (header.MiCols + 15) >> 4, sbRows = (header.MiRows + 15) >> 4;
            source.SuperblockQIndices = Av1VarianceBoost.SuperblockQIndices(source.Planes[0], frame.Strides[0], sbCols, sbRows, header.BaseQIdx, sequence.BitDepth);
            source.SuperblockColumns = sbCols;
        }

        if (!header.FrameIsIntra && searchReferences.Length > 0)
        {
            source.Prepass = Av1MotionPrepass.Build(frame, source.Planes[0], frame.Strides[0], source.ReferencePlanes, searchReferences);
        }

        if (header.AllowIntrabc)
        {
            source.Hash = new Av1BlockHash(source.Planes[0], frame.Strides[0], header.MiCols * 4, header.MiRows * 4);
        }

        if (!header.CodedLossless)
        {
            source.ComputeRdScale(header.MiRows, header.MiCols, sequence.BitDepth);
        }
        Av1FrameDecisions decisions = new(frame);
        int tiles = header.TileCols * header.TileRows;
        Av1TileRows[] tileRows = Search(frame, source, decisions, settings);
        MarkCdef(frame);

        Av1FilterSearch.Search(frame, source, sequence, parameters, settings.Effort, Av1Rate.RdMultiplier(Math.Max(parameters.BaseQIdx, 1), sequence.BitDepth),
            settings.Reference);

        byte[][] tileData = new byte[tiles][];
        Av1FrameContext?[] contexts = new Av1FrameContext?[tiles];
        frame.For(tiles, tile =>
        {
            Av1TileWriter writer = new(frame, tile / frame.Header.TileCols, tile % frame.Header.TileCols, decisions);
            tileData[tile] = writer.Write();
            contexts[tile] = writer.Context;
        });
        parameters.TileSizeBytes = Av1ObuWriter.TileSizeBytesFor(tileData);
        if (!frame.Header.DisableFrameEndUpdateCdf)
        {
            // The CDFs the context update tile ends with, which the frames that load this one start from.
            Av1FrameContext saved = contexts[frame.Header.ContextUpdateTileId]!.Clone();
            saved.ResetCounters();
            frame.SavedContext = saved;
        }

        return (Av1ObuWriter.Frame(sequence, parameters, tileData), frame, tileRows.Sum(t => t.EstimatedRate) / (double)Av1CostSink.BitCost,
            tileData.Sum(t => t.Length) * 8.0);
    }

    /// <summary>
    /// Decides and reconstructs every tile, the superblock rows of all tiles taken in order by up to the frame's parallelism of
    /// workers, each row a wavefront behind the one above it (<see cref="Av1TileRows"/>); the decisions do not depend on how many
    /// workers there are or which takes which row.
    /// </summary>
    private static Av1TileRows[] Search(Av1Frame frame, Av1EncoderSource source, Av1FrameDecisions decisions, Av1EncoderSettings settings)
    {
        Av1FrameHeader header = frame.Header;
        int tiles = header.TileCols * header.TileRows;
        Av1TileRows[] rows = new Av1TileRows[tiles];
        int[] firstJob = new int[tiles + 1];
        for (int tile = 0; tile < tiles; tile++)
        {
            rows[tile] = new Av1TileRows(frame, tile / header.TileCols, tile % header.TileCols);
            firstJob[tile + 1] = firstJob[tile] + rows[tile].Rows;
        }

        int jobs = firstJob[tiles];
        int workers = Math.Clamp(frame.Parallelism, 1, jobs);
        Av1TileEncoder?[] primary = new Av1TileEncoder?[tiles];
        for (int tile = 0; tile < tiles; tile++)
        {
            primary[tile] = new Av1TileEncoder(frame, rows[tile].TileRow, rows[tile].TileCol, source, decisions, settings);
        }

        int next = -1;
        frame.For(workers, worker =>
        {
            Av1TileEncoder?[] mine = worker == 0 ? primary : new Av1TileEncoder?[tiles];
            for (int job = Interlocked.Increment(ref next); job < jobs; job = Interlocked.Increment(ref next))
            {
                int tile = 0;
                while (firstJob[tile + 1] <= job)
                {
                    tile++;
                }

                Av1TileEncoder encoder = mine[tile] ??= new Av1TileEncoder(frame, rows[tile].TileRow, rows[tile].TileCol, source, decisions, settings, primary[tile]);
                encoder.EncodeRow(rows[tile], job - firstJob[tile]);
            }
        });

        for (int tile = 0; tile < tiles; tile++)
        {
            primary[tile]!.SettleQuantizers();
        }

        return rows;
    }

    /// <summary>
    /// Gets the sequence header of a still picture: profile and color configuration from the input, a reduced still picture
    /// header, 64x64 superblocks, every intra tool the frames may use, and the smallest level that holds the picture.
    /// </summary>
    internal static Av1SequenceHeader SequenceFor(Av1EncoderInput input)
    {
        bool mono = input.Chroma == ChromaFormat.Yuv400;
        (int ssx, int ssy) = input.Chroma switch
        {
            ChromaFormat.Yuv444 => (0, 0),
            ChromaFormat.Yuv422 => (1, 0),
            _ => (1, 1),
        };
        int profile = input.BitDepth == 12 || input.Chroma == ChromaFormat.Yuv422 ? 2 : input.Chroma == ChromaFormat.Yuv444 ? 1 : 0;
        int widthBits = Math.Max(1, 32 - int.LeadingZeroCount(input.Width - 1));
        int heightBits = Math.Max(1, 32 - int.LeadingZeroCount(input.Height - 1));
        Av1SequenceHeader written = new()
        {
            Profile = profile,
            StillPicture = true,
            ReducedStillPictureHeader = true,
            OperatingPoints = [new Av1OperatingPoint(0, Level(input.Width, input.Height), 0)],
            FrameWidthBits = widthBits,
            FrameHeightBits = heightBits,
            MaxFrameWidth = input.Width,
            MaxFrameHeight = input.Height,
            EnableFilterIntra = true,
            EnableIntraEdgeFilter = true,
            ForceScreenContentTools = 2,
            ForceIntegerMv = 2,
            EnableCdef = true,
            EnableRestoration = true,
            BitDepth = input.BitDepth,
            Monochrome = mono,
            ColorDescriptionPresent = true,
            ColorPrimaries = input.ColorPrimaries,
            TransferCharacteristics = input.TransferCharacteristics,
            MatrixCoefficients = input.MatrixCoefficients,
            FullRange = input.FullRange,
            SubsamplingX = ssx,
            SubsamplingY = ssy,
            ChromaSamplePosition = ssx == 1 && ssy == 1 ? input.ChromaSamplePosition : 0,
        };

        // Read back, so the header the frames use is exactly what a decoder sees.
        return Av1SequenceHeader.Parse(Av1ObuWriter.SequenceHeader(written), "AV1");
    }

    /// <summary>
    /// Gets the smallest seq_level_idx whose picture size limits hold the frame (AV1 specification annex A.3), or 31, the level
    /// without limits.
    /// </summary>
    internal static int Level(int width, int height)
    {
        ReadOnlySpan<(int Index, long PicSize, int MaxH, int MaxV)> levels =
        [
            (0, 147456, 2048, 1152), (1, 278784, 2816, 1584), (4, 665856, 4352, 2448), (5, 1065024, 5504, 3096),
            (8, 2359296, 6144, 3456), (12, 8912896, 8192, 4352), (16, 35651584, 16384, 8704),
        ];
        foreach ((int index, long picSize, int maxH, int maxV) in levels)
        {
            if ((long)width * height <= picSize && width <= maxH && height <= maxV)
            {
                return index;
            }
        }

        return 31;
    }

    /// <summary>Gets the frame's parameters: one tile column per 4096 samples, the quantizer, and filters off for now.</summary>
    internal static Av1FrameParameters ParametersFor(Av1EncoderInput input, Av1SequenceHeader sequence, Av1EncoderSettings settings)
    {
        int miCols = 2 * ((input.Width + 7) >> 3), miRows = 2 * ((input.Height + 7) >> 3);
        int sbCols = (miCols + 15) >> 4, sbRows = (miRows + 15) >> 4;
        int minLog2Cols = Av1FrameHeader.TileLog2(4096 >> 6, sbCols);
        int minLog2Tiles = Math.Max(minLog2Cols, Av1FrameHeader.TileLog2((4096 * 2304) >> 12, sbRows * sbCols));
        Av1FrameParameters f = new()
        {
            Width = input.Width,
            Height = input.Height,
            TileColsLog2 = minLog2Cols,
            TileRowsLog2 = Math.Max(minLog2Tiles - minLog2Cols, 0),
            BaseQIdx = settings.Lossless ? 0 : Math.Clamp(settings.QIndex, 0, 255),
            LoopFilterDeltaEnabled = false,
            TxModeSelect = !settings.Lossless && settings.QIndex > 0,
        };
        (bool tools, bool intrabc) = Av1ScreenContent.Detect(input.Planes[0], input.Width, input.Height, input.BitDepth);
        f.AllowScreenContentTools = settings.ScreenContent ?? tools;
        f.AllowIntrabc = f.AllowScreenContentTools && (settings.IntraBlockCopy ?? (intrabc && settings.Effort >= 2));
        if (f.BaseQIdx > 0)
        {
            Tune(f, sequence);

            // Tune IQ's Variance Boost: a quantizer for each superblock.
            f.DeltaQPresent = true;
            f.DeltaQRes = Av1VarianceBoost.DeltaQResLog2(f.BaseQIdx);
        }

        return f;
    }

    /// <summary>
    /// Sets the perceptual quantizer choices of libaom's tune IQ (av1_set_quantizer, av1_quantize.c; the QM formulas of
    /// quant_common.h): chroma finer than luma in 4:2:0 and coarser in 4:4:4 and 4:2:2, and quantization matrices whose
    /// steepness follows the quantizer.
    /// </summary>
    internal static void Tune(Av1FrameParameters f, Av1SequenceHeader sequence)
    {
        int q = f.BaseQIdx;
        if (!sequence.Monochrome)
        {
            (int dc, int ac) = (sequence.SubsamplingX, sequence.SubsamplingY) switch
            {
                (1, 1) => (-Math.Clamp((q / 2) - 14, 0, 16), -Math.Clamp((q / 2) - 14, 0, 16)),
                (1, 0) => (0, Math.Clamp(q / 2, 0, 6)),
                _ => (0, Math.Clamp(q / 2, 0, 24)),
            };
            (f.DeltaQUDc, f.DeltaQUAc, f.DeltaQVDc, f.DeltaQVAc) = (dc, ac, dc, ac);
        }

        // Tune IQ's adaptive loop filter sharpness (picklpf.c).
        f.LoopFilterSharpness = q <= 112 ? 7 : q <= 160 ? 1 : 0;
        f.UsingQmatrix = true;
        f.QmY = AllIntraQmLevel(q);
        f.QmU = f.QmV = sequence.SubsamplingX == 0 && sequence.SubsamplingY == 0 ? Chroma444QmLevel(q + f.DeltaQUAc) : AllIntraQmLevel(q + f.DeltaQUAc);

        static int AllIntraQmLevel(int qindex) => qindex <= 40 ? 10 : qindex <= 100 ? 9 : qindex <= 160 ? 8 : qindex <= 200 ? 7 : qindex <= 220 ? 6 : qindex <= 240 ? 5 : 4;

        static int Chroma444QmLevel(int qindex) => qindex <= 12 ? 10 : qindex <= 24 ? 9 : qindex <= 32 ? 8 : qindex <= 36 ? 7 : qindex <= 44 ? 6
            : qindex <= 48 ? 5 : qindex <= 56 ? 4 : qindex <= 88 ? 3 : 2;
    }

    /// <summary>Gives every 64x64 unit that holds a coded block CDEF strength 0, as the tile writer codes it.</summary>
    private static void MarkCdef(Av1Frame frame)
    {
        Array.Fill(frame.CdefIndex, (sbyte)-1);
        if (frame.Header.CodedLossless || frame.Header.AllowIntrabc || !frame.Sequence.EnableCdef)
        {
            return;
        }

        for (int i = 0; i < frame.Grid.Length; i++)
        {
            if (frame.Grid[i] is Av1BlockInfo { Skip: false } b)
            {
                int rows = Math.Max(1, Av1Tables.MiSizeHigh[b.Size] >> 4), cols = Math.Max(1, Av1Tables.MiSizeWide[b.Size] >> 4);
                for (int y = 0; y < rows && ((b.MiRow >> 4) + y) * 16 < frame.MiRows; y++)
                {
                    for (int x = 0; x < cols && ((b.MiCol >> 4) + x) * 16 < frame.MiCols; x++)
                    {
                        frame.CdefIndex[(((b.MiRow >> 4) + y) * frame.CdefStride) + (b.MiCol >> 4) + x] = 0;
                    }
                }
            }
        }
    }
}
