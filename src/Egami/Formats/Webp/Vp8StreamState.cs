namespace Egami.Formats.Webp;

/// <summary>
/// The decoder state that lasts from frame to frame in a VP8 stream: the entropy context (coefficient, intra mode and motion vector
/// probabilities), segment values, probabilities and map, filter deltas and the golden and alternate sign biases. A WebP still
/// uses one frame of it; <see cref="Vp8VideoDecoder"/> carries it across a stream's frames.
/// </summary>
internal sealed class Vp8StreamState
{
    private readonly byte[] _saved = new byte[EntropyBytes];

    internal Vp8StreamState()
    {
        ResetForKeyFrame();
    }

    private static int EntropyBytes => (4 * 8 * 3 * 11) + 4 + 3 + (2 * Vp8InterTables.MvProbCount);

    internal byte[] CoefficientProbabilities { get; } = new byte[4 * 8 * 3 * 11];

    /// <summary>Gets the inter-frame 16 × 16 mode probabilities.</summary>
    internal byte[] YModeProbabilities { get; } = new byte[4];

    /// <summary>Gets the inter-frame chroma mode probabilities.</summary>
    internal byte[] UvModeProbabilities { get; } = new byte[3];

    /// <summary>Gets the motion vector probabilities, the row component's 19 then the column's.</summary>
    internal byte[] MvProbabilities { get; } = new byte[2 * Vp8InterTables.MvProbCount];

    /// <summary>Gets or sets the last key frame's clamping type: 1 when the encoder promises no reconstruction leaves 0 to 255.</summary>
    internal int ClampingType { get; set; }

    internal bool AbsoluteSegmentValues { get; set; }

    internal int[] SegmentQuantizer { get; } = new int[4];

    internal int[] SegmentFilterLevel { get; } = new int[4];

    internal byte[] SegmentProbabilities { get; } = [255, 255, 255];

    internal int[] ReferenceFilterDeltas { get; } = new int[4];

    internal int[] ModeFilterDeltas { get; } = new int[4];

    /// <summary>Gets whether the golden (index 2) and alternate (3) frames' vectors point the other way; 0 and 1 stay false.</summary>
    internal bool[] SignBias { get; } = new bool[4];

    /// <summary>Gets or sets each macroblock's segment, kept while frames do not update the map; null for a still.</summary>
    internal byte[]? SegmentMap { get; set; }

    /// <summary>Returns to the state a key frame starts from: default probabilities, no segment values, no deltas, no sign bias.</summary>
    internal void ResetForKeyFrame()
    {
        Vp8Tables.DefaultCoeffProbs.CopyTo(CoefficientProbabilities);
        Vp8InterTables.DefaultYModeProbs.CopyTo(YModeProbabilities);
        Vp8InterTables.DefaultUvModeProbs.CopyTo(UvModeProbabilities);
        Vp8InterTables.DefaultMvProbs.CopyTo(MvProbabilities);
        AbsoluteSegmentValues = false;
        Array.Clear(SegmentQuantizer);
        Array.Clear(SegmentFilterLevel);
        Array.Clear(ReferenceFilterDeltas);
        Array.Clear(ModeFilterDeltas);
        Array.Clear(SignBias);
    }

    /// <summary>Makes this state a copy of <paramref name="other"/>, the segment map included.</summary>
    internal void CopyFrom(Vp8StreamState other)
    {
        other.CoefficientProbabilities.CopyTo(CoefficientProbabilities, 0);
        other.YModeProbabilities.CopyTo(YModeProbabilities, 0);
        other.UvModeProbabilities.CopyTo(UvModeProbabilities, 0);
        other.MvProbabilities.CopyTo(MvProbabilities, 0);
        ClampingType = other.ClampingType;
        AbsoluteSegmentValues = other.AbsoluteSegmentValues;
        other.SegmentQuantizer.CopyTo(SegmentQuantizer, 0);
        other.SegmentFilterLevel.CopyTo(SegmentFilterLevel, 0);
        other.SegmentProbabilities.CopyTo(SegmentProbabilities, 0);
        other.ReferenceFilterDeltas.CopyTo(ReferenceFilterDeltas, 0);
        other.ModeFilterDeltas.CopyTo(ModeFilterDeltas, 0);
        other.SignBias.CopyTo(SignBias, 0);
        if (other.SegmentMap is null)
        {
            SegmentMap = null;
        }
        else
        {
            if (SegmentMap is null || SegmentMap.Length != other.SegmentMap.Length)
            {
                SegmentMap = new byte[other.SegmentMap.Length];
            }

            other.SegmentMap.CopyTo(SegmentMap, 0);
        }
    }

    /// <summary>Keeps the entropy context, for a frame whose probability updates apply to it alone (refresh_entropy_probs 0).</summary>
    internal void SaveEntropy()
    {
        Span<byte> s = _saved;
        CoefficientProbabilities.CopyTo(s);
        s = s[CoefficientProbabilities.Length..];
        YModeProbabilities.CopyTo(s);
        s = s[4..];
        UvModeProbabilities.CopyTo(s);
        MvProbabilities.CopyTo(s[3..]);
    }

    /// <summary>Puts back the entropy context <see cref="SaveEntropy"/> kept.</summary>
    internal void RestoreEntropy()
    {
        ReadOnlySpan<byte> s = _saved;
        s[..CoefficientProbabilities.Length].CopyTo(CoefficientProbabilities);
        s = s[CoefficientProbabilities.Length..];
        s[..4].CopyTo(YModeProbabilities);
        s = s[4..];
        s[..3].CopyTo(UvModeProbabilities);
        s.Slice(3, MvProbabilities.Length).CopyTo(MvProbabilities);
    }
}
