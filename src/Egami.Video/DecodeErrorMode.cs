namespace Egami.Video;

/// <summary>What a decoder or <see cref="VideoReader"/> does with a frame it cannot decode.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-options"/>
/// </example>
public enum DecodeErrorMode
{
    /// <summary>Throws <see cref="CorruptDataException"/>. The default for decoders made directly, so callers see every problem.</summary>
    Throw,

    /// <summary>
    /// Reports the frame through the Egami context's diagnostics and drops it, then, for codecs whose frames depend on earlier ones
    /// (VP8), every frame up to the next keyframe, so no wrong picture is ever shown. The default for <see cref="VideoReader"/>.
    /// </summary>
    Skip,

    /// <summary>
    /// Reports the frame and keeps going, filling what could not be read from the previous frame (VP8: the damaged macroblocks;
    /// mid-gray when there is no previous frame). Codecs with nothing to fill from (Motion JPEG) drop the frame, as in
    /// <see cref="Skip"/>.
    /// </summary>
    Conceal,
}
