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

    /// <summary>Gets the loop filter search's first step, 0 for no search (the quantizer's level).</summary>
    internal int FilterStep { get; init; } = 2;

    /// <summary>Gets the settings for an effort from 0 (fastest) to 10 (smallest).</summary>
    internal static (int Range, bool Quarter, int Candidates, int Split, int IntraGate, int FilterStep) Search(int effort) => effort switch
    {
        0 => (8, false, 1, 0, 2, 0),
        1 => (16, true, 1, 0, 2, 0),
        2 => (16, true, 1, 0, 4, 0),
        3 => (16, true, 1, 1, 4, 0),
        4 => (32, true, 2, 1, 8, 2),
        5 => (32, true, 3, 1, 8, 2),
        6 => (48, true, 3, 1, 0, 4),
        7 => (48, true, 3, 2, 0, 4),
        8 => (64, true, 4, 2, 0, 8),
        9 => (64, true, 6, 2, 0, 8),
        _ => (64, true, 8, 2, 0, 8),
    };
}
