namespace Egami.Video.Codecs;

/// <summary>
/// What <see cref="Ffv1Codec.Verify(string, MediaContext?, CancellationToken)"/> found: how much it checked and every CRC that did
/// not match. A version 3 stream with slice CRCs is checked whole; versions 0 and 1, and version 3 written without slice CRCs,
/// have only what can be checked without decoding (the configuration record and the slice layout).
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-ffv1-verify"/>
/// </example>
public sealed record Ffv1VerifyResult
{
    /// <summary>Gets the number of frames checked.</summary>
    public required int Frames { get; init; }

    /// <summary>Gets the number of slices whose CRC was checked.</summary>
    public required long Slices { get; init; }

    /// <summary>Gets whether the stream's slices carry CRCs (version 3 with error detection on), so every slice was checked.</summary>
    public required bool HasSliceCrcs { get; init; }

    /// <summary>Gets whether the configuration record's CRC matched, or <see langword="null"/> when the stream has none (versions 0 and 1).</summary>
    public bool? ConfigurationCrcOk { get; init; }

    /// <summary>Gets every problem found, in file order.</summary>
    public IReadOnlyList<Ffv1VerifyError> Errors { get; init; } = [];

    /// <summary>Gets whether nothing was found wrong.</summary>
    public bool IsIntact => Errors.Count == 0 && ConfigurationCrcOk != false;
}

/// <summary>One problem <see cref="Ffv1Codec.Verify(string, MediaContext?, CancellationToken)"/> found.</summary>
/// <param name="Frame">The frame's index in the track, from 0.</param>
/// <param name="Timestamp">The frame's timestamp.</param>
/// <param name="Slice">The slice's index in the frame, from 0, or -1 when the frame's slices could not be found.</param>
/// <param name="Problem">What is wrong, as a sentence.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-ffv1-verify"/>
/// </example>
public sealed record Ffv1VerifyError(int Frame, MediaTime Timestamp, int Slice, string Problem);
