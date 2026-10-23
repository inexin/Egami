namespace Egami.Formats.Webp;

/// <summary>
/// What a VP8 inter frame is coded against and how hard the encoder searches: the reference frames, the probabilities the frame
/// starts from, the header's reference flags, and the search settings that follow from the effort.
/// </summary>
internal sealed class Vp8InterFrame
{
    /// <summary>Gets the reference frames the search may use, by slot (1 last, 2 golden, 3 alternate); null where a slot is not used.</summary>
    internal Vp8FrameBuffer?[] References { get; } = new Vp8FrameBuffer?[4];

    /// <summary>Gets the probabilities the frame starts from: the stream's after the frame before.</summary>
    internal required Vp8StreamState State { get; init; }

    /// <summary>Gets the modes of the frame before, whose vectors seed the search; null when there are none.</summary>
    internal Vp8ModeInfo? Previous { get; init; }

    internal bool RefreshLast { get; init; } = true;

    internal bool RefreshGolden { get; init; }

    internal bool RefreshAlternate { get; init; }

    /// <summary>Gets what goes into the golden frame when it is not refreshed: 0 nothing, 1 the last frame, 2 the alternate.</summary>
    internal int CopyToGolden { get; init; }

    /// <summary>Gets what goes into the alternate frame when it is not refreshed: 0 nothing, 1 the last frame, 2 the golden.</summary>
    internal int CopyToAlternate { get; init; }

    internal bool GoldenSignBias { get; init; }

    internal bool AlternateSignBias { get; init; }

    /// <summary>Gets how far the full-pixel search goes from its best start, in pixels.</summary>
    internal int SearchRange { get; init; } = 16;

    /// <summary>Gets whether vectors are refined to quarter pixels; else to half pixels.</summary>
    internal bool QuarterPixel { get; init; } = true;

    /// <summary>Gets how many of the best 16 × 16 candidates by prediction error are coded in full to compare them.</summary>
    internal int Candidates { get; init; } = 2;

    /// <summary>Gets which split layouts are tried: 0 none, 1 16 × 8, 8 × 16 and 8 × 8, 2 those and 4 × 4.</summary>
    internal int Split { get; init; }

    /// <summary>
    /// Gets how much worse than the best vector's a 16 × 16 intra prediction may be for the macroblock to try the intra modes
    /// (its squared error at most this many times the vector's); 0 tries them everywhere.
    /// </summary>
    internal int IntraGate { get; init; } = 8;

    /// <summary>
    /// Gets how small, in quarters of the squared quantizer step, the best vector's squared error may be for the macroblock to
    /// skip the intra modes: below it the residual mostly quantizes away and intra rarely wins. 0 never skips them so.
    /// </summary>
    internal int IntraFloor { get; init; }

    /// <summary>
    /// Gets how close, in quarters, the golden or alternate frame's near vectors must come to the frames before's best prediction
    /// error for the macroblock to search that frame (its error under this many quarters of theirs); 0 searches every frame.
    /// </summary>
    internal int ReferenceGate { get; init; }

    /// <summary>Gets the loop filter search's first step, 0 for no search (the quantizer's level).</summary>
    internal int FilterStep { get; init; } = 2;

    /// <summary>Gets whether inter residuals are quantized with the trellis; else level by level.</summary>
    internal bool Trellis { get; init; }

    /// <summary>Gets the settings for an effort from 0 (fastest) to 10 (smallest).</summary>
    internal static (int Range, bool Quarter, int Candidates, int Split, bool Trellis, int IntraGate, int IntraFloor, int ReferenceGate, int FilterStep) Search(int effort) => effort switch
    {
        0 => (8, false, 1, 0, false, 2, 32, 4, 0),
        1 => (16, true, 1, 1, false, 2, 32, 4, 0),
        2 => (16, true, 1, 1, false, 4, 32, 4, 1),
        3 => (16, true, 2, 1, false, 8, 32, 4, 1),
        4 => (32, true, 2, 1, false, 8, 32, 4, 1),
        5 => (32, true, 2, 2, false, 8, 16, 4, 2),
        6 => (32, true, 2, 2, true, 8, 16, 4, 2),
        7 => (48, true, 4, 2, true, 16, 8, 0, 4),
        8 => (64, true, 4, 2, true, 16, 8, 0, 8),
        9 => (64, true, 6, 2, true, 0, 0, 0, 8),
        _ => (64, true, 8, 2, true, 0, 0, 0, 8),
    };
}
