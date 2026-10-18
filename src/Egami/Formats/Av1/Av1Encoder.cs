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
}

/// <summary>How an AV1 frame is encoded.</summary>
internal sealed class Av1EncoderSettings
{
    /// <summary>Gets or sets whether every sample is kept exactly: quantizer 0 and the Walsh-Hadamard transform.</summary>
    internal bool Lossless { get; init; }

    /// <summary>Gets or sets base_q_idx, 1 to 255, for lossy frames.</summary>
    internal int QIndex { get; init; } = 100;

    /// <summary>Gets or sets the block size the fixed partition splits to (<see cref="Bs"/>).</summary>
    internal int BlockSize { get; init; } = Bs.B16x16;

    /// <summary>Gets or sets how many threads tiles may use.</summary>
    internal int Parallelism { get; init; } = 1;

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
    private Av1Encoder(byte[] stream, Av1SequenceHeader sequence, Av1Frame frame)
    {
        Stream = stream;
        Sequence = sequence;
        Reconstruction = frame;
    }

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
        Av1BitWriter bits = new();
        Av1ObuWriter.FrameHeader(bits, sequence, parameters);
        Av1FrameHeader header = Av1FrameHeader.Parse(bits.ToArray(), sequence, 0, 0, "AV1");
        Av1Frame frame = new(header, "AV1", reconstruct: true) { Parallelism = Math.Max(1, settings.Parallelism), CancellationToken = settings.CancellationToken };
        Av1EncoderSource source = new(input, frame);
        Av1FrameDecisions decisions = new(frame);
        int tiles = header.TileCols * header.TileRows;
        frame.For(tiles, tile => new Av1TileEncoder(frame, tile / header.TileCols, tile % header.TileCols, source, decisions, settings).Encode());
        MarkCdef(frame);
        frame.ApplyFilters();

        byte[][] tileData = new byte[tiles][];
        frame.For(tiles, tile => tileData[tile] = new Av1TileWriter(frame, tile / header.TileCols, tile % header.TileCols, decisions).Write());
        parameters.TileSizeBytes = Av1ObuWriter.TileSizeBytesFor(tileData);
        List<byte> stream = [];
        Av1ObuWriter.Obu(stream, Av1ObuType.SequenceHeader, Av1ObuWriter.SequenceHeader(sequence));
        Av1ObuWriter.Obu(stream, Av1ObuType.Frame, Av1ObuWriter.Frame(sequence, parameters, tileData));
        return new Av1Encoder([.. stream], sequence, frame);
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
            BaseQIdx = settings.Lossless ? 0 : Math.Clamp(settings.QIndex, 1, 255),
            LoopFilterDeltaEnabled = false,
        };
        if (sequence.Monochrome)
        {
            f.LrType[0] = Av1RestorationType.None;
        }

        return f;
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
