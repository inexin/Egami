namespace Egami.Video.Codecs;

/// <summary>
/// FFV1's expert encoder settings (<see cref="VideoEncoderOptions.Ffv1"/>, <see cref="VideoWriterOptions.Ffv1"/>). The defaults are
/// the archival profile: version 3, the range coder with a custom state table, slice CRCs on, and a slice grid picked from the
/// frame size.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-ffv1"/>
/// </example>
public sealed record Ffv1EncoderOptions
{
    /// <summary>Gets the default settings.</summary>
    public static Ffv1EncoderOptions Default { get; } = new();

    /// <summary>
    /// Gets the number of slices each frame is cut into, 1 to 1024, or <see langword="null"/> to pick it from the frame size: 1
    /// up to 352×288, 4 up to 1280×720, 16 up to 2560×1440 and 24 above. Slices are coded and decoded in parallel. Frames
    /// larger than 352×288 need at least 4 (RFC 9043 section 5).
    /// </summary>
    public int? Slices { get; init; }

    /// <summary>Gets whether every slice ends in a CRC, so damage is found and <see cref="Ffv1Codec.Verify(string, MediaContext?, CancellationToken)"/> can check the file. Default on.</summary>
    public bool SliceCrc { get; init; } = true;

    /// <summary>Gets the entropy coder. Default <see cref="Ffv1Coder.RangeCustomTable"/>.</summary>
    public Ffv1Coder Coder { get; init; } = Ffv1Coder.RangeCustomTable;

    /// <summary>
    /// Gets the context model, or <see langword="null"/> to pick it from <see cref="VideoEncoderOptions.Effort"/>: the small
    /// model up to effort 6 (the default is 4), the large one from 7 on. With every frame a keyframe the small model codes
    /// smaller files, as its fewer contexts adapt within a frame; the large one pays off over longer runs of non-keyframes.
    /// </summary>
    public Ffv1ContextModel? ContextModel { get; init; }

    /// <summary>
    /// Gets the bits per sample to code, 8 to 16, or <see langword="null"/> for the planes' own depth. A smaller depth than the
    /// planes' takes the samples' high bits, which suits planes decoded from a 9, 11, 13, 14 or 15-bit source, held shifted up in
    /// 10, 12 or 16 bits, so the file keeps the source depth.
    /// </summary>
    public int? BitsPerSample { get; init; }
}

/// <summary>The entropy coder FFV1 codes samples with (RFC 9043 section 4.2.3).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-ffv1"/>
/// </example>
public enum Ffv1Coder
{
    /// <summary>The range coder with a custom state transition table: the smallest files.</summary>
    RangeCustomTable,

    /// <summary>The range coder with the default state transition table.</summary>
    RangeDefaultTable,

    /// <summary>Golomb-Rice codes with run mode: faster, larger files, and 8 bits per sample at most.</summary>
    GolombRice,
}

/// <summary>How many neighbours FFV1's contexts look at.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-ffv1"/>
/// </example>
public enum Ffv1ContextModel
{
    /// <summary>Three neighbour differences: fewer contexts, which adapt quickly on small frames.</summary>
    Small,

    /// <summary>Five neighbour differences: more contexts, usually smaller files on large frames.</summary>
    Large,
}
