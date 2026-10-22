using Egami.Video.Audio.Codecs.Vorbis;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// Vorbis I, decode only: every stream the specification allows (floors 0 and 1, residues 0 to 2, channel coupling, all block
/// sizes), written from the Vorbis I specification and within 2^-22 of libvorbis on the test files. Frames come out as planar
/// float in the canonical channel order (Vorbis puts center second); the first block's overlap and the end padding are trimmed.
/// Read from Ogg, WebM, Matroska and MP4, where the track's codec private data holds the three header packets, Xiph-laced.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-vorbis"/>
/// </example>
public sealed class VorbisCodec : AudioCodec
{
    private VorbisCodec()
    {
    }

    /// <summary>Gets the Vorbis codec.</summary>
    public static VorbisCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "Vorbis";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Vorbis;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-vorbis"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new VorbisTrackDecoder(track, options ?? AudioDecoderOptions.Default, context);
    }
}
