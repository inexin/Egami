using Egami.Video.Audio.Codecs.Pcm;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// Uncompressed PCM: integer samples of 8 (unsigned), 16, 24 or 32 bits, little-endian (<see cref="Instance"/>, WAV's) or
/// big-endian (<see cref="BigEndian"/>, AIFF's and QuickTime's twos), and 32 or 64-bit float (<see cref="FloatingPoint"/>). Each
/// instance decodes all three codec ids; it encodes its own. Decoded frames are interleaved, in the stored sample type: 24-bit
/// samples come as <see cref="SampleFormat.S32"/> with <see cref="AudioFrame.ValidBits"/> 24.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
/// </example>
public sealed class PcmCodec : AudioCodec
{
    private PcmCodec(string codecId, string name)
    {
        CodecId = codecId;
        Name = name;
    }

    /// <summary>Gets the little-endian integer PCM codec (<see cref="CodecIds.PcmLittleEndian"/>).</summary>
    public static PcmCodec Instance { get; } = new(CodecIds.PcmLittleEndian, "PCM");

    /// <summary>Gets the big-endian integer PCM codec (<see cref="CodecIds.PcmBigEndian"/>).</summary>
    public static PcmCodec BigEndian { get; } = new(CodecIds.PcmBigEndian, "PCM big-endian");

    /// <summary>Gets the float PCM codec (<see cref="CodecIds.PcmFloat"/>).</summary>
    public static PcmCodec FloatingPoint { get; } = new(CodecIds.PcmFloat, "PCM float");

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    public override string CodecId { get; }

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == TrackKind.Audio && track.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new PcmDecoder(track, context);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
    /// </example>
    public override IAudioEncoder CreateEncoder(AudioEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new PcmEncoder(CodecId, options, context);
    }
}
