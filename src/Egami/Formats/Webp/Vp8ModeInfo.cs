namespace Egami.Formats.Webp;

/// <summary>
/// What a VP8 frame's mode partition said about each macroblock, kept for the near vector search of the macroblocks after it and
/// for tests: the reference, mode, segment, skip flag and motion vectors. The arrays have a border row above and a border column
/// on the left that read as intra macroblocks with no vector, so the search needs no edge checks; macroblock (x, y) is at
/// <c>(y + 1) × Stride + x + 1</c>.
/// </summary>
internal sealed class Vp8ModeInfo
{
    /// <summary>The modes, numbered as libvpx numbers them: the 16 × 16 intra modes, then B_PRED, then the inter modes.</summary>
    internal const int Dc = 0, BPred = 4, Nearest = 5, Near = 6, Zero = 7, New = 8, Split = 9;

    internal Vp8ModeInfo(int mbWidth, int mbHeight)
    {
        MbWidth = mbWidth;
        MbHeight = mbHeight;
        Stride = mbWidth + 1;
        int count = Stride * (mbHeight + 1);
        Reference = new byte[count];
        Mode = new byte[count];
        UvMode = new byte[count];
        Skip = new byte[count];
        NeedsClamp = new byte[count];
        Partitioning = new byte[count];
        Mv = new int[count];
        SubMv = new int[count * 16];
        SubModes = new byte[count * 16];
    }

    internal int MbWidth { get; }

    internal int MbHeight { get; }

    internal int Stride { get; }

    /// <summary>Gets each macroblock's reference: 0 intra, 1 last, 2 golden, 3 alternate.</summary>
    internal byte[] Reference { get; }

    internal byte[] Mode { get; }

    internal byte[] UvMode { get; }

    /// <summary>Gets whether each macroblock has no coefficients: flagged as skipped, or with every block empty.</summary>
    internal byte[] Skip { get; }

    /// <summary>Gets whether a vector of the macroblock reaches more than 16 pixels past the frame (libvpx's need_to_clamp_mvs).</summary>
    internal byte[] NeedsClamp { get; }

    /// <summary>Gets each split macroblock's layout: 0 16 × 8, 1 8 × 16, 2 8 × 8, 3 4 × 4.</summary>
    internal byte[] Partitioning { get; }

    /// <summary>Gets each macroblock's vector, packed by <see cref="Pack"/>, in eighths of a luma pixel; a split macroblock's last block's.</summary>
    internal int[] Mv { get; }

    /// <summary>Gets a split macroblock's 16 block vectors.</summary>
    internal int[] SubMv { get; }

    /// <summary>Gets a B_PRED macroblock's 16 block modes.</summary>
    internal byte[] SubModes { get; }

    internal int Index(int mbx, int mby) => ((mby + 1) * Stride) + mbx + 1;

    /// <summary>Packs a vector as libvpx's int_mv holds it: the row in the low 16 bits, the column in the high.</summary>
    internal static int Pack(int row, int col) => (ushort)row | (col << 16);

    internal static int Row(int mv) => (short)mv;

    internal static int Col(int mv) => mv >> 16;
}
