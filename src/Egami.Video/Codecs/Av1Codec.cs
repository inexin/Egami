using Egami.Video.Codecs.Av1;

namespace Egami.Video.Codecs;

/// <summary>
/// AV1, as WebM, Matroska, MP4, IVF and raw OBU files carry it, decoded by Egami's AV1 decoder in the Main, High and Professional
/// profiles: 8, 10 and 12 bit, 4:0:0, 4:2:0, 4:2:2 and 4:4:4, film grain, scaled references and scalable streams, every frame equal
/// to libaom's sample for sample. A packet is a temporal unit. Frames come out at their own size (a stream may change size at a
/// key frame) as 8-bit planes, or 16-bit planes holding 10 or 12 bits; a monochrome stream gives 4:0:0 planes. Hidden frames give
/// no frame; a frame shown later with show_existing_frame comes out then. Decode only.
/// </summary>
/// <remarks>
/// <para>
/// The frame's color comes from the sequence header; the container's (<see cref="VideoTrackInfo.Color"/>) fills only what the
/// header leaves unspecified. Content light level and mastering display metadata OBUs give <see cref="VideoFrame.Hdr"/> (held
/// until the stream sends new ones; the track's <see cref="VideoTrackInfo.Hdr"/> before any), and an ITU-T T.35 metadata OBU
/// with HDR10+ gives <see cref="VideoFrame.Hdr10Plus"/>. <see cref="VideoDecoderOptions.ApplyFilmGrain"/> turns film grain off.
/// </para>
/// <para>
/// A track's av1C (<see cref="TrackInfo.CodecPrivate"/>) supplies the sequence header when the first unit, or the first after a
/// seek, has none. Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through the Egami context's
/// diagnostics as <c>Av1.CorruptFrame</c> (the unit's data is broken), <c>Av1.MissingReference</c> (a unit that needs frames
/// from before the first key frame) or <c>Av1.KeyframeFlagMismatch</c> (the container's keyframe flag disagrees with the unit,
/// which wins: a keyframe is a unit starting with a shown key frame). After a broken unit, decoding resumes at the next shown key
/// frame; in <see cref="DecodeErrorMode.Conceal"/> mode the units in between show the last good frame again.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1"/>
/// </example>
public sealed class Av1Codec : VideoCodec
{
    private Av1Codec()
    {
    }

    /// <summary>Gets the AV1 codec.</summary>
    public static Av1Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AV1";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Av1;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "AV1 decodes at full size only; the scale denominator must be 1.");
        }

        return new Av1VideoDecoder(track, options, context);
    }
}
