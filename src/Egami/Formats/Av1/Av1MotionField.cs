namespace Egami.Formats.Av1;

/// <summary>
/// The temporal motion vectors: each decoded frame keeps one vector per 8x8 block for later frames (av1_copy_frame_mvs), and a
/// frame that uses them projects its references' vectors onto itself before its tiles decode (av1_setup_motion_field), as
/// libaom's mvref_common.c does (BSD 2-clause, see THIRD-PARTY-NOTICES).
/// </summary>
internal static class Av1MotionField
{
    private const int MaxFrameDistance = 31;
    private const int RefMvsLimit = (1 << 12) - 1;

    private static readonly short[] DivMult =
    [
        0, 16384, 8192, 5461, 4096, 3276, 2730, 2340, 2048, 1820, 1638, 1489, 1365, 1260, 1170, 1092, 1024, 963, 910, 862, 819, 780,
        744, 712, 682, 655, 630, 606, 585, 564, 546, 528,
    ];

    /// <summary>av1_get_mv_projection: scales a vector over <paramref name="den"/> frames to <paramref name="num"/> frames.</summary>
    internal static (int Row, int Col) Project(int row, int col, int num, int den)
    {
        den = Math.Min(den, MaxFrameDistance);
        num = num > 0 ? Math.Min(num, MaxFrameDistance) : Math.Max(num, -MaxFrameDistance);
        int r = Round2Signed(row * num * DivMult[den], 14), c = Round2Signed(col * num * DivMult[den], 14);
        const int max = (1 << 14) - 1, min = -(1 << 14) + 1;
        return (Math.Clamp(r, min, max), Math.Clamp(c, min, max));
    }

    private static int Round2Signed(int v, int n) => v < 0 ? -((-v + (1 << (n - 1))) >> n) : (v + (1 << (n - 1))) >> n;

    /// <summary><see cref="Project"/> of one component, with its factor num * DivMult[den] worked out already.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Scaled(int v, int factor) => Math.Clamp(Round2Signed(v * factor, 14), -(1 << 14) + 1, (1 << 14) - 1);

    /// <summary>Divides by 2^<paramref name="n"/>, rounding toward zero.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Toward0(int v, int n) => v >= 0 ? v >> n : -((-v) >> n);

    /// <summary>
    /// av1_copy_frame_mvs over a decoded frame: each 8x8 block's vector toward an earlier reference, taken from its bottom right
    /// 4x4 unit (the last decoded one).
    /// </summary>
    internal static void Save(Av1Frame frame)
    {
        Av1FrameHeader h = frame.Header;
        int rows = (frame.MiRows + 1) >> 1, cols = (frame.MiCols + 1) >> 1;
        sbyte[] refs = new sbyte[rows * cols];
        int[] mvRows = new int[rows * cols], mvCols = new int[rows * cols];
        refs.AsSpan().Fill(-1);
        if (!h.FrameIsIntra)
        {
            for (int r = 0; r < rows; r++)
            {
                int miRow = Math.Min((2 * r) + 1, frame.MiRows - 1);
                for (int c = 0; c < cols; c++)
                {
                    Av1BlockInfo? b = frame.Grid[(miRow * frame.MiCols) + Math.Min((2 * c) + 1, frame.MiCols - 1)];
                    if (b is null)
                    {
                        continue;
                    }

                    for (int list = 0; list < 2; list++)
                    {
                        int rf = list == 0 ? b.RefFrame0 : b.RefFrame1;
                        if (rf <= Ref.Intra || h.RelativeDist(h.OrderHints[rf], h.OrderHint) >= 0)
                        {
                            continue;
                        }

                        (int row, int col) = list == 0 ? (b.MvRow, b.MvCol) : (b.Mv1Row, b.Mv1Col);
                        if (Math.Abs(row) > RefMvsLimit || Math.Abs(col) > RefMvsLimit)
                        {
                            continue;
                        }

                        int at = (r * cols) + c;
                        refs[at] = (sbyte)rf;
                        mvRows[at] = row;
                        mvCols[at] = col;
                    }
                }
            }
        }

        frame.SavedRefFrames = refs;
        frame.SavedMvRows = mvRows;
        frame.SavedMvCols = mvCols;
    }

    /// <summary>av1_setup_motion_field: projects the references' saved vectors onto the frame being decoded.</summary>
    internal static void Setup(Av1Frame frame)
    {
        Av1FrameHeader h = frame.Header;
        int rows = (frame.MiRows + 1) >> 1, cols = (frame.MiCols + 1) >> 1;
        frame.MotionFieldStride = cols;
        frame.MotionFieldRows = new short[rows * cols];
        frame.MotionFieldCols = new short[rows * cols];
        frame.MotionFieldOffsets = new sbyte[rows * cols];
        if (!h.Sequence.EnableOrderHint)
        {
            return;
        }

        int cur = h.OrderHint;
        int refStamp = 2;
        Av1Frame last = h.RefFrames[Ref.Last]!;
        int altOfLast = last.Header.OrderHints[Ref.Alt];
        if (altOfLast != h.OrderHints[Ref.Golden])
        {
            Projection(frame, Ref.Last, 2);
        }

        refStamp--;
        if (h.RelativeDist(h.OrderHints[Ref.Bwd], cur) > 0 && Projection(frame, Ref.Bwd, 0))
        {
            refStamp--;
        }

        if (h.RelativeDist(h.OrderHints[Ref.Alt2], cur) > 0 && Projection(frame, Ref.Alt2, 0))
        {
            refStamp--;
        }

        if (h.RelativeDist(h.OrderHints[Ref.Alt], cur) > 0 && refStamp >= 0 && Projection(frame, Ref.Alt, 0))
        {
            refStamp--;
        }

        if (refStamp >= 0)
        {
            Projection(frame, Ref.Last2, 2);
        }
    }

    /// <summary>motion_field_projection from one reference frame (the start frame) in a direction (0 forward, 2 backward).</summary>
    private static bool Projection(Av1Frame frame, int startFrame, int dir)
    {
        Av1FrameHeader h = frame.Header;
        Av1Frame start = h.RefFrames[startFrame]!;
        Av1FrameHeader s = start.Header;
        if (s.FrameIsIntra || start.MiRows != frame.MiRows || start.MiCols != frame.MiCols || start.SavedRefFrames is null)
        {
            return false;
        }

        int startToCurrent = h.RelativeDist(s.OrderHint, h.OrderHint);
        if (dir == 2)
        {
            startToCurrent = -startToCurrent;
        }

        if (Math.Abs(startToCurrent) > MaxFrameDistance)
        {
            return true;
        }

        // Per reference: its distance, or 0 when its vectors are not projected, and Project's factor num * DivMult[den].
        Span<int> refOffset = stackalloc int[8];
        Span<int> factor = stackalloc int[8];
        refOffset.Clear();
        for (int rf = Ref.Last; rf <= Ref.Alt; rf++)
        {
            int offset = h.RelativeDist(s.OrderHint, s.OrderHints[rf]);
            if (offset > 0 && offset <= MaxFrameDistance)
            {
                refOffset[rf] = offset;
                factor[rf] = startToCurrent * DivMult[offset];
            }
        }

        sbyte[] savedRefs = start.SavedRefFrames;
        int[] savedRows = start.SavedMvRows!, savedCols = start.SavedMvCols!;
        short[] fieldRows = frame.MotionFieldRows!, fieldCols = frame.MotionFieldCols!;
        sbyte[] fieldOffsets = frame.MotionFieldOffsets!;
        int rows = (frame.MiRows + 1) >> 1, cols = (frame.MiCols + 1) >> 1;
        int rowLimit = frame.MiRows >> 1, colLimit = frame.MiCols >> 1;
        int sign = dir == 2 ? -1 : 1;
        for (int blkRow = 0; blkRow < rows; blkRow++)
        {
            int baseRow = (blkRow >> 3) << 3;
            for (int blkCol = 0; blkCol < cols; blkCol++)
            {
                int at = (blkRow * cols) + blkCol;
                int rf = savedRefs[at];
                if (rf <= Ref.Intra || refOffset[rf] == 0)
                {
                    continue;
                }

                int mvRow = savedRows[at], mvCol = savedCols[at];
                int pr = Scaled(mvRow, factor[rf]), pc = Scaled(mvCol, factor[rf]);

                // get_block_position
                int row = blkRow + (sign * Toward0(pr, 6));
                int col = blkCol + (sign * Toward0(pc, 6));
                int baseCol = (blkCol >> 3) << 3;
                if ((uint)row >= (uint)rowLimit || (uint)col >= (uint)colLimit || row < baseRow || row >= baseRow + 8
                    || col < baseCol - 8 || col >= baseCol + 16)
                {
                    continue;
                }

                int to = (row * cols) + col;
                fieldRows[to] = (short)mvRow;
                fieldCols[to] = (short)mvCol;
                fieldOffsets[to] = (sbyte)refOffset[rf];
            }
        }

        return true;
    }
}
