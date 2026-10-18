namespace Egami.Formats.Webp;

/// <summary>
/// The constant tables VP8 inter frames add (RFC 6386 sections 16 to 18): the intra mode probabilities of inter frames, the motion
/// vector probabilities and their update probabilities, the mode contexts of the near vector search, the split tables, and the
/// six-tap and bilinear prediction filters.
/// </summary>
internal static class Vp8InterTables
{
    /// <summary>The default probabilities of the 16 × 16 luma modes in inter frames (section 16.2), DC, V, H, TM against B.</summary>
    internal static ReadOnlySpan<byte> DefaultYModeProbs => [112, 86, 140, 37];

    /// <summary>The default probabilities of the chroma modes in inter frames.</summary>
    internal static ReadOnlySpan<byte> DefaultUvModeProbs => [162, 101, 204];

    /// <summary>The fixed probabilities of the 4 × 4 modes of a B_PRED macroblock in an inter frame (no contexts).</summary>
    internal static ReadOnlySpan<byte> BModeProbs => [120, 90, 79, 133, 87, 85, 80, 111, 151];

    /// <summary>The number of probabilities per motion vector component: is-short, sign, 7 short-tree, 10 long-bit.</summary>
    internal const int MvProbCount = 19;

    /// <summary>The default motion vector probabilities (section 17.2), the row component then the column.</summary>
    internal static ReadOnlySpan<byte> DefaultMvProbs =>
    [
        162, 128, 225, 146, 172, 147, 214, 39, 156, 128, 129, 132, 75, 145, 178, 206, 239, 254, 254,
        164, 128, 204, 170, 119, 235, 140, 230, 228, 128, 130, 130, 74, 148, 180, 203, 236, 254, 254,
    ];

    /// <summary>The probabilities that a frame updates each motion vector probability (section 17.2).</summary>
    internal static ReadOnlySpan<byte> MvUpdateProbs =>
    [
        237, 246, 253, 253, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 250, 250, 252, 254, 254,
        231, 243, 245, 253, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 251, 251, 254, 254, 254,
    ];

    /// <summary>The tree of short vector magnitudes 0 to 7, as pairs of children: positive is the next pair, otherwise minus the value.</summary>
    internal static ReadOnlySpan<sbyte> SmallMvTree => [2, 8, 4, 6, -0, -1, -2, -3, 10, 12, -4, -5, -6, -7];

    /// <summary>The tree of inter-frame 16 × 16 modes: DC, V, H, TM, B (section 16.2).</summary>
    internal static ReadOnlySpan<sbyte> YModeTree => [-0, 2, 4, 6, -1, -2, -3, -4];

    /// <summary>
    /// The probabilities of the motion vector modes given the near vector counts (section 16.3), [count, node]: ZERO, NEAREST,
    /// NEAR, then NEW against SPLIT.
    /// </summary>
    internal static ReadOnlySpan<byte> ModeContexts =>
    [
        7, 1, 1, 143,
        14, 18, 14, 107,
        135, 64, 57, 68,
        60, 56, 128, 65,
        159, 134, 128, 34,
        234, 188, 128, 28,
    ];

    /// <summary>
    /// The probabilities of a split part's vector (LEFT, ABOVE, ZERO, then NEW), by context: above zero × 4 + left zero × 2 +
    /// left equals above.
    /// </summary>
    internal static ReadOnlySpan<byte> SubMvRefProbs =>
    [
        147, 136, 18,
        223, 1, 34,
        106, 145, 1,
        208, 1, 1,
        179, 121, 1,
        223, 1, 34,
        179, 121, 1,
        208, 1, 1,
    ];

    /// <summary>The first 4 × 4 block of each part of the four split layouts: 16 × 8, 8 × 16, 8 × 8 and 4 × 4, [layout, part].</summary>
    internal static ReadOnlySpan<byte> SplitOffset =>
    [
        0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 2, 8, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    /// <summary>The blocks of each split layout's parts in part order, <see cref="SplitFillCount"/> per part, [layout, 16].</summary>
    internal static ReadOnlySpan<byte> SplitFill =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        0, 1, 4, 5, 8, 9, 12, 13, 2, 3, 6, 7, 10, 11, 14, 15,
        0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    /// <summary>The 4 × 4 blocks in one part of each split layout.</summary>
    internal static ReadOnlySpan<byte> SplitFillCount => [8, 8, 4, 1];

    /// <summary>The parts of each split layout.</summary>
    internal static ReadOnlySpan<byte> SplitParts => [2, 2, 4, 16];
}
