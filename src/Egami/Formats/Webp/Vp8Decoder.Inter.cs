namespace Egami.Formats.Webp;

/// <summary>
/// The inter-frame half of <see cref="Vp8Decoder"/> (RFC 6386 sections 9.7 to 9.11 and 16 to 18): the reference update flags and
/// mode probabilities of the frame header, each macroblock's reference, mode and motion vectors with the near vector search, and
/// motion-compensated prediction. Vectors are held as libvpx holds them, in eighths of a luma pixel in 16-bit fields, so its
/// clamps and roundings carry over unchanged.
/// </summary>
internal sealed partial class Vp8Decoder
{
    private readonly Vp8VideoDecoder? _video;

    /// <summary>Gets the frame tag: key frame, version, shown, size.</summary>
    internal Vp8FrameHeader Header { get; }

    internal bool UpdateSegmentData { get; }

    internal bool FilterSimple { get; }

    internal int FilterLevel { get; }

    internal int Sharpness { get; }

    internal bool FilterDeltas { get; }

    internal bool UpdateFilterDeltas { get; }

    /// <summary>Gets the quantizer indices: the base, then the Y1 DC, Y2 DC, Y2 AC, UV DC and UV AC deltas.</summary>
    internal int[] Quantizers { get; private set; } = [];

    internal bool SegmentationEnabled => _segmentation;

    internal bool UpdateSegmentMap => _updateMap;

    internal int Partitions => _partitionMask + 1;

    internal bool UseSkip => _useSkip;

    internal int SkipProbability => _skipProbability;

    internal bool RefreshGolden { get; private set; }

    internal bool RefreshAlternate { get; private set; }

    /// <summary>Gets what goes into the golden frame when it is not refreshed: 0 nothing, 1 the last frame, 2 the alternate.</summary>
    internal int CopyToGolden { get; private set; }

    /// <summary>Gets what goes into the alternate frame when it is not refreshed: 0 nothing, 1 the last frame, 2 the golden.</summary>
    internal int CopyToAlternate { get; private set; }

    internal bool RefreshEntropy { get; }

    internal bool RefreshLast { get; }

    internal int ProbIntra { get; private set; }

    internal int ProbLast { get; private set; }

    internal int ProbGolden { get; private set; }

    private void ParseReferenceUpdates(ref Vp8BoolDecoder br)
    {
        RefreshGolden = br.GetValue(1) != 0;
        RefreshAlternate = br.GetValue(1) != 0;
        CopyToGolden = RefreshGolden ? 0 : br.GetValue(2);
        CopyToAlternate = RefreshAlternate ? 0 : br.GetValue(2);
        _state.SignBias[2] = br.GetValue(1) != 0;
        _state.SignBias[3] = br.GetValue(1) != 0;
    }

    private void ParseInterProbabilities(ref Vp8BoolDecoder br)
    {
        ProbIntra = br.GetValue(8);
        ProbLast = br.GetValue(8);
        ProbGolden = br.GetValue(8);
        if (br.GetValue(1) != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                _state.YModeProbabilities[i] = (byte)br.GetValue(8);
            }
        }

        if (br.GetValue(1) != 0)
        {
            for (int i = 0; i < 3; i++)
            {
                _state.UvModeProbabilities[i] = (byte)br.GetValue(8);
            }
        }

        Span<byte> mv = _state.MvProbabilities;
        ReadOnlySpan<byte> update = Vp8InterTables.MvUpdateProbs;
        for (int i = 0; i < mv.Length; i++)
        {
            if (br.GetBit(update[i]) != 0)
            {
                int v = br.GetValue(7);
                mv[i] = (byte)(v != 0 ? v << 1 : 1);
            }
        }
    }

    /// <summary>
    /// The filter strengths of a video frame, as libvpx works them out: the segment's level clamped to 0 to 63, then the reference
    /// delta, and for B_PRED, ZERO, the other vectors and split their mode deltas, clamped again; [segment, reference, mode index].
    /// </summary>
    private void ComputeVideoFilterStrengths(int frameLevel, int sharpness, bool useDeltas)
    {
        bool key = Header.KeyFrame;
        for (int s = 0; s < 4; s++)
        {
            int segmentLevel = frameLevel;
            if (_segmentation)
            {
                segmentLevel = Math.Clamp(_state.SegmentFilterLevel[s] + (_state.AbsoluteSegmentValues ? 0 : frameLevel), 0, 63);
            }

            for (int reference = 0; reference < 4; reference++)
            {
                for (int mode = 0; mode < 4; mode++)
                {
                    int level = segmentLevel;
                    if (useDeltas)
                    {
                        level += _state.ReferenceFilterDeltas[reference];
                        if (reference > 0 ? mode > 0 : mode == 0)
                        {
                            level += _state.ModeFilterDeltas[mode];
                        }

                        level = Math.Clamp(level, 0, 63);
                    }

                    _filterInfo[(((s * 4) + reference) * 4) + mode] = Strength(level, sharpness, key);
                }
            }
        }
    }

    private static FilterInfo Strength(int level, int sharpness, bool key)
    {
        if (level == 0)
        {
            return default;
        }

        int interior = level;
        if (sharpness > 0)
        {
            interior >>= sharpness > 4 ? 2 : 1;
            interior = Math.Min(interior, 9 - sharpness);
        }

        interior = Math.Max(interior, 1);
        int hev = key ? (level >= 40 ? 2 : level >= 15 ? 1 : 0) : (level >= 40 ? 3 : level >= 20 ? 2 : level >= 15 ? 1 : 0);
        return new FilterInfo((2 * level) + interior, interior, hev);
    }

    /// <summary>Keeps a key-frame macroblock's modes for the dump and the frames' bookkeeping.</summary>
    private void RecordIntra(int mbx, int mby, Macroblock mb)
    {
        Vp8ModeInfo info = _video!.Modes;
        int here = info.Index(mbx, mby);
        info.Reference[here] = 0;
        info.Mode[here] = (byte)mb.Mode;
        info.UvMode[here] = (byte)mb.UvMode;
        info.Mv[here] = 0;
        info.NeedsClamp[here] = 0;
        if (mb.Intra4x4)
        {
            _subModes.CopyTo(info.SubModes, here * 16);
        }
    }

    /// <summary>Reads an inter frame's macroblock header after its segment and skip flag (RFC 6386 section 19.3).</summary>
    private Macroblock ParseInterModes(ref Vp8BoolDecoder br, int mbx, int mby, int segment, bool skip)
    {
        Vp8ModeInfo info = _video!.Modes;
        int here = info.Index(mbx, mby);
        info.NeedsClamp[here] = 0;
        if (br.GetBit(ProbIntra) == 0)
        {
            ReadOnlySpan<byte> yp = _state.YModeProbabilities;
            ReadOnlySpan<sbyte> tree = Vp8InterTables.YModeTree;
            int i = 0;
            do
            {
                i = tree[i + br.GetBit(yp[i >> 1])];
            }
            while (i > 0);
            int mode = -i;
            if (mode == Vp8ModeInfo.BPred)
            {
                ReadOnlySpan<byte> bp = Vp8InterTables.BModeProbs;
                for (int n = 0; n < 16; n++)
                {
                    int j = 0;
                    do
                    {
                        j = BModeTree[j + br.GetBit(bp[j >> 1])];
                    }
                    while (j > 0);
                    _subModes[n] = (byte)-j;
                }

                _subModes.CopyTo(info.SubModes, here * 16);
            }

            ReadOnlySpan<byte> up = _state.UvModeProbabilities;
            int uv = br.GetBit(up[0]) == 0 ? 0 : br.GetBit(up[1]) == 0 ? 1 : br.GetBit(up[2]) == 0 ? 2 : 3;
            info.Reference[here] = 0;
            info.Mode[here] = (byte)mode;
            info.UvMode[here] = (byte)uv;
            info.Mv[here] = 0;
            return new Macroblock(segment, skip, mode, uv);
        }

        int reference = 1;
        if (br.GetBit(ProbLast) != 0)
        {
            reference = 2 + br.GetBit(ProbGolden);
        }

        info.Reference[here] = (byte)reference;
        info.UvMode[here] = 0;
        int interMode = ReadVectors(ref br, info, here, mbx, mby, reference);
        info.Mode[here] = (byte)interMode;
        return new Macroblock(segment, skip, interMode, 0, reference);
    }

    /// <summary>Runs the near vector search, reads the macroblock's vector mode and vectors, and keeps them; returns the mode.</summary>
    private int ReadVectors(ref Vp8BoolDecoder br, Vp8ModeInfo info, int here, int mbx, int mby, int reference)
    {
        int above = here - info.Stride;
        int left = here - 1;
        int aboveLeft = above - 1;
        bool[] bias = _state.SignBias;
        Span<int> near = [0, 0, 0, 0];
        Span<int> count = [0, 0, 0, 0];
        int n = 0;

        // The above, left and above-left neighbours, weighted 2, 2 and 1; vectors from frames of the other sign bias are turned round.
        if (info.Reference[above] != 0)
        {
            int mv = info.Mv[above];
            if (mv != 0)
            {
                near[++n] = Bias(mv, bias[info.Reference[above]] != bias[reference]);
            }

            count[n] += 2;
        }

        if (info.Reference[left] != 0)
        {
            int mv = info.Mv[left];
            if (mv != 0)
            {
                int biased = Bias(mv, bias[info.Reference[left]] != bias[reference]);
                if (biased != near[n])
                {
                    near[++n] = biased;
                }

                count[n] += 2;
            }
            else
            {
                count[0] += 2;
            }
        }

        if (info.Reference[aboveLeft] != 0)
        {
            int mv = info.Mv[aboveLeft];
            if (mv != 0)
            {
                int biased = Bias(mv, bias[info.Reference[aboveLeft]] != bias[reference]);
                if (biased != near[n])
                {
                    near[++n] = biased;
                }

                count[n] += 1;
            }
            else
            {
                count[0] += 1;
            }
        }

        ReadOnlySpan<byte> contexts = Vp8InterTables.ModeContexts;
        if (br.GetBit(contexts[count[0] * 4]) == 0)
        {
            info.Mv[here] = 0;
            return Vp8ModeInfo.Zero;
        }

        // Three distinct vectors: the above-left one counts for the nearest when they are equal. Then the larger count is nearest.
        if (count[3] > 0 && near[n] == near[1])
        {
            count[1]++;
        }

        if (count[2] > count[1])
        {
            (count[1], count[2]) = (count[2], count[1]);
            (near[1], near[2]) = (near[2], near[1]);
        }

        int toLeft = -(mbx * 16) << 3;
        int toRight = ((MbWidth - 1 - mbx) * 16) << 3;
        int toTop = -(mby * 16) << 3;
        int toBottom = ((MbHeight - 1 - mby) * 16) << 3;
        if (br.GetBit(contexts[(count[1] * 4) + 1]) == 0)
        {
            info.Mv[here] = ClampNear(near[1], toLeft, toRight, toTop, toBottom);
            return Vp8ModeInfo.Nearest;
        }

        if (br.GetBit(contexts[(count[2] * 4) + 2]) == 0)
        {
            info.Mv[here] = ClampNear(near[2], toLeft, toRight, toTop, toBottom);
            return Vp8ModeInfo.Near;
        }

        int best = ClampNear(near[count[1] >= count[0] ? 1 : 0], toLeft, toRight, toTop, toBottom);
        int splits = (info.Mode[above] == Vp8ModeInfo.Split ? 2 : 0) + (info.Mode[left] == Vp8ModeInfo.Split ? 2 : 0) + (info.Mode[aboveLeft] == Vp8ModeInfo.Split ? 1 : 0);
        int lo = toLeft - 128, hi = toRight + 128, top = toTop - 128, bottom = toBottom + 128;
        if (br.GetBit(contexts[(splits * 4) + 3]) == 0)
        {
            int row = (short)((ReadMvComponent(ref br, 0) * 2) + Vp8ModeInfo.Row(best));
            int col = (short)((ReadMvComponent(ref br, Vp8InterTables.MvProbCount) * 2) + Vp8ModeInfo.Col(best));
            info.Mv[here] = Vp8ModeInfo.Pack(row, col);
            info.NeedsClamp[here] = OutOfBounds(row, col, lo, hi, top, bottom);
            return Vp8ModeInfo.New;
        }

        ReadSplit(ref br, info, here, above, left, best, lo, hi, top, bottom);
        return Vp8ModeInfo.Split;
    }

    private void ReadSplit(ref Vp8BoolDecoder br, Vp8ModeInfo info, int here, int above, int left, int best, int lo, int hi, int top, int bottom)
    {
        int layout = 3;
        if (br.GetBit(110) != 0)
        {
            layout = 2;
            if (br.GetBit(111) != 0)
            {
                layout = br.GetBit(150);
            }
        }

        int[] sub = info.SubMv;
        int at = here * 16;
        int parts = Vp8InterTables.SplitParts[layout];
        int fill = Vp8InterTables.SplitFillCount[layout];
        ReadOnlySpan<byte> probs = Vp8InterTables.SubMvRefProbs;
        byte clamp = 0;
        for (int j = 0; j < parts; j++)
        {
            int k = Vp8InterTables.SplitOffset[(layout * 16) + j];
            int leftMv = (k & 3) != 0 ? sub[at + k - 1] : info.Mode[left] != Vp8ModeInfo.Split ? info.Mv[left] : sub[(left * 16) + k + 3];
            int aboveMv = (k >> 2) != 0 ? sub[at + k - 4] : info.Mode[above] != Vp8ModeInfo.Split ? info.Mv[above] : sub[(above * 16) + k + 12];
            int context = ((aboveMv == 0 ? 1 : 0) << 2) | ((leftMv == 0 ? 1 : 0) << 1) | (leftMv == aboveMv ? 1 : 0);
            ReadOnlySpan<byte> p = probs.Slice(context * 3, 3);
            int mv;
            if (br.GetBit(p[0]) == 0)
            {
                mv = leftMv;
            }
            else if (br.GetBit(p[1]) == 0)
            {
                mv = aboveMv;
            }
            else if (br.GetBit(p[2]) == 0)
            {
                mv = 0;
            }
            else
            {
                int row = (short)((ReadMvComponent(ref br, 0) * 2) + Vp8ModeInfo.Row(best));
                int col = (short)((ReadMvComponent(ref br, Vp8InterTables.MvProbCount) * 2) + Vp8ModeInfo.Col(best));
                mv = Vp8ModeInfo.Pack(row, col);
            }

            clamp |= OutOfBounds(Vp8ModeInfo.Row(mv), Vp8ModeInfo.Col(mv), lo, hi, top, bottom);
            ReadOnlySpan<byte> blocks = Vp8InterTables.SplitFill.Slice((layout * 16) + (j * fill), fill);
            foreach (byte b in blocks)
            {
                sub[at + b] = mv;
            }
        }

        info.Partitioning[here] = (byte)layout;
        info.NeedsClamp[here] = clamp;
        info.Mv[here] = sub[at + 15];
    }

    /// <summary>Reads one motion vector component (RFC 6386 section 17.1) in quarter pixels, with probabilities at <paramref name="at"/>.</summary>
    private int ReadMvComponent(ref Vp8BoolDecoder br, int at)
    {
        ReadOnlySpan<byte> p = _state.MvProbabilities.AsSpan(at, Vp8InterTables.MvProbCount);
        int x;
        if (br.GetBit(p[0]) != 0)
        {
            // The long form: bits 0 to 2, then 9 down to 4, then bit 3, which is implied when no higher bit is set.
            x = 0;
            for (int i = 0; i < 3; i++)
            {
                x += br.GetBit(p[9 + i]) << i;
            }

            for (int i = 9; i > 3; i--)
            {
                x += br.GetBit(p[9 + i]) << i;
            }

            if ((x & 0xFFF0) == 0 || br.GetBit(p[9 + 3]) != 0)
            {
                x += 8;
            }
        }
        else
        {
            ReadOnlySpan<sbyte> tree = Vp8InterTables.SmallMvTree;
            int i = 0;
            do
            {
                i = tree[i + br.GetBit(p[2 + (i >> 1)])];
            }
            while (i > 0);
            x = -i;
        }

        return x != 0 && br.GetBit(p[1]) != 0 ? -x : x;
    }

    private static int Bias(int mv, bool invert) => invert ? Vp8ModeInfo.Pack(-Vp8ModeInfo.Row(mv), -Vp8ModeInfo.Col(mv)) : mv;

    /// <summary>Clamps a near vector to at most 16 pixels past the frame's macroblock-aligned edges (libvpx's vp8_clamp_mv2).</summary>
    private static int ClampNear(int mv, int toLeft, int toRight, int toTop, int toBottom)
    {
        int row = Vp8ModeInfo.Row(mv);
        int col = Vp8ModeInfo.Col(mv);
        col = col < toLeft - 128 ? toLeft - 128 : col > toRight + 128 ? toRight + 128 : col;
        row = row < toTop - 128 ? toTop - 128 : row > toBottom + 128 ? toBottom + 128 : row;
        return Vp8ModeInfo.Pack((short)row, (short)col);
    }

    private static byte OutOfBounds(int row, int col, int lo, int hi, int top, int bottom) =>
        (byte)(col < lo || col > hi || row < top || row > bottom ? 1 : 0);

    /// <summary>
    /// Predicts an inter macroblock from its reference into the frame being decoded at <paramref name="dy"/> and
    /// <paramref name="dc"/>, luma at 16 × 16, 8 × 8 or 4 × 4 as it is split and chroma at 8 × 8 or 4 × 4. Vectors that reach far
    /// past the frame are clamped as libvpx clamps them.
    /// </summary>
    private void PredictInter(int mbx, int mby, Row c, int dy, int dc)
    {
        Vp8ModeInfo info = _video!.Modes;
        int here = info.Index(mbx, mby);
        Vp8FrameBuffer frame = _video.Reference(info.Reference[here]);
        bool sixTap = Header.Version == 0;
        bool fullPixel = Header.Version == 3;
        bool clamp = info.NeedsClamp[here] != 0;
        int toLeft = -(mbx * 16) << 3;
        int toRight = ((MbWidth - 1 - mbx) * 16) << 3;
        int toTop = -(mby * 16) << 3;
        int toBottom = ((MbHeight - 1 - mby) * 16) << 3;
        int x = mbx * 16;
        int y = mby * 16;
        int chromaWidth = frame.AlignedWidth / 2;
        int chromaHeight = frame.AlignedHeight / 2;
        if (info.Mode[here] != Vp8ModeInfo.Split)
        {
            int row = Vp8ModeInfo.Row(info.Mv[here]);
            int col = Vp8ModeInfo.Col(info.Mv[here]);
            if (clamp)
            {
                ClampToBorder(ref row, ref col, toLeft, toRight, toTop, toBottom);
            }

            Vp8InterPredict.Predict(frame.Y, frame.OriginY, frame.StrideY, frame.AlignedWidth, frame.AlignedHeight, Vp8FrameBuffer.Border, x, y, 16, 16, row, col, sixTap, RowsY, dy, StrideY);

            // Chroma moves half as far, rounded away from zero, in eighths of a chroma pixel.
            row = (short)((short)(row + (1 | (row >> 31))) / 2);
            col = (short)((short)(col + (1 | (col >> 31))) / 2);
            if (fullPixel)
            {
                row &= ~7;
                col &= ~7;
            }

            if ((2 * col) < toLeft - (19 << 3) || (2 * col) > toRight + (18 << 3) || (2 * row) < toTop - (19 << 3) || (2 * row) > toBottom + (18 << 3))
            {
                // libvpx predicts nothing here and keeps what the frame buffer held.
                return;
            }

            Vp8InterPredict.PredictChroma(frame.U, frame.V, frame.OriginC, frame.StrideC, chromaWidth, chromaHeight, Vp8FrameBuffer.ChromaBorder, x / 2, y / 2, row, col, sixTap, RowsU, RowsV, dc, StrideC);
            return;
        }

        int[] sub = info.SubMv;
        int at = here * 16;
        int size = info.Partitioning[here] < 3 ? 8 : 4;
        for (int b = 0; b < 16; b++)
        {
            int bx = (b & 3) * 4;
            int by = (b >> 2) * 4;
            if (size == 8 && ((b & 1) != 0 || (b & 4) != 0))
            {
                continue;
            }

            int row = Vp8ModeInfo.Row(sub[at + b]);
            int col = Vp8ModeInfo.Col(sub[at + b]);
            if (clamp)
            {
                ClampToBorder(ref row, ref col, toLeft, toRight, toTop, toBottom);
            }

            Vp8InterPredict.Predict(frame.Y, frame.OriginY, frame.StrideY, frame.AlignedWidth, frame.AlignedHeight, Vp8FrameBuffer.Border, x + bx, y + by, size, size, row, col, sixTap, RowsY, dy + (by * StrideY) + bx, StrideY);
        }

        // Each 4 × 4 chroma block moves by the average of the four luma blocks it covers.
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                int b = at + (i * 8) + (j * 2);
                int rows = Vp8ModeInfo.Row(sub[b]) + Vp8ModeInfo.Row(sub[b + 1]) + Vp8ModeInfo.Row(sub[b + 4]) + Vp8ModeInfo.Row(sub[b + 5]);
                int cols = Vp8ModeInfo.Col(sub[b]) + Vp8ModeInfo.Col(sub[b + 1]) + Vp8ModeInfo.Col(sub[b + 4]) + Vp8ModeInfo.Col(sub[b + 5]);
                int row = (short)((rows + 4 + ((rows >> 31) * 8)) / 8);
                int col = (short)((cols + 4 + ((cols >> 31) * 8)) / 8);
                if (fullPixel)
                {
                    row &= ~7;
                    col &= ~7;
                }

                if (clamp)
                {
                    col = (2 * col) < toLeft - (19 << 3) ? (short)((toLeft - (16 << 3)) >> 1) : col;
                    col = (2 * col) > toRight + (18 << 3) ? (short)((toRight + (16 << 3)) >> 1) : col;
                    row = (2 * row) < toTop - (19 << 3) ? (short)((toTop - (16 << 3)) >> 1) : row;
                    row = (2 * row) > toBottom + (18 << 3) ? (short)((toBottom + (16 << 3)) >> 1) : row;
                }

                int cx = j * 4;
                int cy = i * 4;
                Vp8InterPredict.Predict(frame.U, frame.OriginC, frame.StrideC, chromaWidth, chromaHeight, Vp8FrameBuffer.ChromaBorder, (x / 2) + cx, (y / 2) + cy, 4, 4, row, col, sixTap, RowsU, dc + (cy * StrideC) + cx, StrideC);
                Vp8InterPredict.Predict(frame.V, frame.OriginC, frame.StrideC, chromaWidth, chromaHeight, Vp8FrameBuffer.ChromaBorder, (x / 2) + cx, (y / 2) + cy, 4, 4, row, col, sixTap, RowsV, dc + (cy * StrideC) + cx, StrideC);
            }
        }
    }

    /// <summary>
    /// Limits a luma vector that reaches so far past the frame that no frame pixel is used (19 pixels past the top or left, 18 past
    /// the bottom or right) to 16 pixels, whole: the prediction is the same (libvpx's clamp_mv_to_umv_border).
    /// </summary>
    private static void ClampToBorder(ref int row, ref int col, int toLeft, int toRight, int toTop, int toBottom)
    {
        if (col < toLeft - (19 << 3))
        {
            col = (short)(toLeft - (16 << 3));
        }
        else if (col > toRight + (18 << 3))
        {
            col = (short)(toRight + (16 << 3));
        }

        if (row < toTop - (19 << 3))
        {
            row = (short)(toTop - (16 << 3));
        }
        else if (row > toBottom + (18 << 3))
        {
            row = (short)(toBottom + (16 << 3));
        }
    }
}
