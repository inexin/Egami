using Egami.Video.Audio.Codecs.Mp3;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// MPEG audio (ISO/IEC 11172-3 and 13818-3), decode only: Layers I, II and III (MP3) of MPEG-1, MPEG-2 and MPEG-2.5, every
/// rate from 8 to 48 kHz, mono, stereo, joint and dual channel, free-format bit rates, and the CRC-16 when frames carry it.
/// Frames come out as planar float, one per MPEG frame (1152 samples, 576 for MPEG-2 and 2.5 Layer III, 384 for Layer I).
/// Read from .mp3 files and from MP3 and MP2 tracks in AVI, Matroska and MP4. The decoder is translated from minimp3.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-mp3"/>
/// </example>
public sealed class Mp3Codec : AudioCodec
{
    private Mp3Codec()
    {
    }

    /// <summary>Gets the MPEG audio codec.</summary>
    public static Mp3Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "MP3";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Mp3;

    /// <summary>Returns whether the track is MPEG audio: <see cref="CodecIds.Mp3"/>, <see cref="CodecIds.Mp2"/> or <see cref="CodecIds.Mp1"/>.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether the codec decodes it.</returns>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == TrackKind.Audio && track.CodecId is CodecIds.Mp3 or CodecIds.Mp2 or CodecIds.Mp1;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-mp3"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new Mp3Decoder(track, options ?? AudioDecoderOptions.Default, context);
    }
}
