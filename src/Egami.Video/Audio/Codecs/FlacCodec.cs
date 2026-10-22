using Egami.Video.Audio.Codecs.Flac;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// FLAC (RFC 9639), lossless: decodes every stream the format allows (fixed and LPC prediction, every stereo mode, block sizes
/// up to 65535, 4 to 32 bits per sample) into planar integer frames, checking the frame CRCs and STREAMINFO's MD5; encodes at
/// levels 0 to 8 with the reference encoder's meaning, default 5. Read and written in .flac files, Matroska and MP4.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
/// </example>
public sealed class FlacCodec : AudioCodec
{
    /// <summary>The vendor string written in Vorbis comments.</summary>
    internal const string Vendor = "Egami.Video";

    private FlacCodec()
    {
    }

    /// <summary>Gets the FLAC codec.</summary>
    public static FlacCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "FLAC";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Flac;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new FlacDecoder(track, options ?? AudioDecoderOptions.Default, context);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
    /// </example>
    public override IAudioEncoder CreateEncoder(AudioEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new FlacEncoder(options, context);
    }
}
