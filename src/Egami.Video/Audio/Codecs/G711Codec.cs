using Egami.Video.Audio.Codecs.G711;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// G.711 (ITU-T G.711) μ-law and A-law, decode only: one byte per sample, expanded to 16-bit samples by the standard's tables.
/// Read from WAV (format tags 7 and 6), AVI, AIFF-C (ulaw, alaw) and MP4/MOV. Each instance decodes both laws. Frames come out
/// interleaved as <see cref="SampleFormat.S16"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-aiff"/>
/// </example>
public sealed class G711Codec : AudioCodec
{
    private G711Codec(string codecId, string name)
    {
        CodecId = codecId;
        Name = name;
    }

    /// <summary>Gets the μ-law codec (<see cref="CodecIds.G711MuLaw"/>).</summary>
    public static G711Codec MuLaw { get; } = new(CodecIds.G711MuLaw, "G.711 μ-law");

    /// <summary>Gets the A-law codec (<see cref="CodecIds.G711ALaw"/>).</summary>
    public static G711Codec ALaw { get; } = new(CodecIds.G711ALaw, "G.711 A-law");

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    public override string CodecId { get; }

    /// <summary>Returns whether the track is G.711: <see cref="CodecIds.G711MuLaw"/> or <see cref="CodecIds.G711ALaw"/>.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether the codec decodes it.</returns>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == TrackKind.Audio && track.CodecId is CodecIds.G711MuLaw or CodecIds.G711ALaw;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new G711Decoder(track, context);
    }
}
