using System.Diagnostics.CodeAnalysis;

using Egami.Video.Audio.Codecs;

namespace Egami.Video.Audio;

/// <summary>
/// An audio codec: which tracks it decodes and how to create its decoder and encoder, the audio twin of
/// <see cref="Video.Codecs.VideoCodec"/>. Register a new one with <see cref="MediaContext.WithCodec(AudioCodec)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
/// </example>
public abstract class AudioCodec
{
    /// <summary>Gets the codec's name, such as "FLAC".</summary>
    public abstract string Name { get; }

    /// <summary>Gets the Matroska codec id that encoders write (<see cref="CodecIds"/>).</summary>
    public abstract string CodecId { get; }

    /// <summary>Gets whether <see cref="CreateEncoder"/> is supported.</summary>
    public virtual bool CanEncode => false;

    /// <summary>Returns whether this codec decodes the track. By default, whether the track's codec id is <see cref="CodecId"/>.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether the codec decodes it.</returns>
    public virtual bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Kind == TrackKind.Audio && string.Equals(track.CodecId, CodecId, StringComparison.Ordinal);
    }

    /// <summary>Creates a decoder for a track.</summary>
    /// <param name="track">The track, as the demuxer describes it.</param>
    /// <param name="options">How to decode, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context frames are allocated from.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="UnsupportedFormatException">The track's setup is one the codec cannot decode.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
    /// </example>
    public abstract IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context);

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The sample rate, layout, bit depth and level.</param>
    /// <param name="context">The context.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="NotSupportedException">The codec cannot encode, or cannot encode these settings.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
    /// </example>
    public virtual IAudioEncoder CreateEncoder(AudioEncoderOptions options, MediaContext context) =>
        throw new NotSupportedException($"{Name} cannot encode.");

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>How an audio decoder decodes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
/// </example>
public sealed record AudioDecoderOptions
{
    /// <summary>Gets the default options.</summary>
    public static AudioDecoderOptions Default { get; } = new();

    /// <summary>
    /// Gets what to do with a packet that cannot be decoded: throw (the default), drop it and report it, or report it and put
    /// silence of its length in its place (<see cref="DecodeErrorMode.Conceal"/>). <see cref="AudioReader"/> sets this from
    /// <see cref="AudioReaderOptions.Errors"/>.
    /// </summary>
    public DecodeErrorMode Errors { get; init; }

    /// <summary>
    /// Gets whether checksums in the stream are checked (FLAC's frame CRCs and the STREAMINFO MD5 of the whole decoded
    /// stream). A mismatch is reported through the Egami context's diagnostics and handled as <see cref="Errors"/> says. On by
    /// default; turning it off saves the MD5's time.
    /// </summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>
    /// Gets whether to keep the samples a packet decodes past the duration its container gives it (the padding at the end of an
    /// Opus stream, which Ogg's last granule position and Matroska's DiscardPadding mark). Off by default, so they are trimmed.
    /// <see cref="AudioReader"/> sets this from <see cref="AudioReaderOptions.KeepAllSamples"/>.
    /// </summary>
    public bool KeepPadding { get; init; }
}

/// <summary>What an audio encoder makes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
/// </example>
public sealed record AudioEncoderOptions
{
    /// <summary>Gets the sample rate in hertz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Gets the channels.</summary>
    public required ChannelLayout Layout { get; init; }

    /// <summary>
    /// Gets the bits per sample stored, or 0 for the codec's default: 16 for FLAC and integer PCM, 32 for float PCM. FLAC takes 4
    /// to 32; integer PCM 8, 16, 24 or 32; float PCM 32 or 64.
    /// </summary>
    public int BitsPerSample { get; init; }

    /// <summary>Gets the compression level, or <see langword="null"/> for the codec's default (FLAC: 0 to 8, default 5).</summary>
    public int? Level { get; init; }

    /// <summary>Gets the samples per packet, or 0 for the codec's default (FLAC: 1152 at levels 0 to 2, else 4096; PCM: 4096).</summary>
    public int FrameSize { get; init; }

    /// <summary>
    /// Gets how Opus codes, or <see langword="null"/> for <see cref="OpusEncoderOptions.Default"/>. Opus ignores
    /// <see cref="FrameSize"/> and <see cref="Level"/>; the frame duration and effort are set here.
    /// </summary>
    public OpusEncoderOptions? Opus { get; init; }
}

/// <summary>
/// Turns packets into audio frames with the same send and receive loop as <see cref="Video.Codecs.IVideoDecoder"/>: after
/// each <see cref="Send"/> call <see cref="TryReceive"/> until it returns <see langword="false"/>; at the end call
/// <see cref="Flush"/> and receive what is left.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
/// </example>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Gives the decoder a packet. The decoder copies what it keeps, so the caller may dispose the packet afterwards.</summary>
    /// <param name="packet">The packet.</param>
    /// <exception cref="CorruptDataException">The packet cannot be decoded and the options say to throw.</exception>
    void Send(EncodedPacket packet);

    /// <summary>
    /// Tells the decoder a packet of the given length was lost. Codecs with concealment (Opus) fill the gap from what came
    /// before, or from in-band error correction in the next packet; the others put silence there.
    /// </summary>
    /// <param name="duration">How long the lost packet was.</param>
    void SendLoss(MediaTime duration);

    /// <summary>Tells the decoder no more packets come, so it releases every frame it holds.</summary>
    void Flush();

    /// <summary>Takes the next decoded frame, in time order.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out AudioFrame? frame);

    /// <summary>Drops every packet and frame in flight, for example after a seek.</summary>
    void Reset();
}

/// <summary>
/// Turns audio frames into packets, the mirror of <see cref="IAudioDecoder"/>. Frames may have any length and any sample
/// format; the encoder converts them to its own and cuts packets of <see cref="FrameSize"/> samples.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-encode"/>
/// </example>
public interface IAudioEncoder : IDisposable
{
    /// <summary>Gets the track the packets belong to, to hand to a muxer. Some fields (FLAC's MD5 and total samples) are final only after <see cref="Flush"/>.</summary>
    AudioTrackInfo Track { get; }

    /// <summary>Gets the samples per packet; the last packet may be shorter.</summary>
    int FrameSize { get; }

    /// <summary>
    /// Encodes a frame of the encoder's sample rate and layout. Its samples are converted to the encoder's format; the caller
    /// keeps ownership of the frame. Packet timestamps count samples from the first frame's timestamp.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentException">The frame's rate or layout differs from the encoder's.</exception>
    void Send(AudioFrame frame);

    /// <summary>Tells the encoder no more frames come, so it encodes the samples it holds and finishes the track's setup.</summary>
    void Flush();

    /// <summary>Takes the next packet.</summary>
    /// <param name="packet">The packet, which the caller must dispose.</param>
    /// <returns>Whether a packet was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet);
}

/// <summary>Creates audio decoders from the codecs registered in a <see cref="MediaContext"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
/// </example>
public static class AudioCodecs
{
    /// <summary>Gets the PCM codec for little-endian integer samples (WAV's).</summary>
    public static AudioCodec Pcm => PcmCodec.Instance;

    /// <summary>Gets the FLAC codec.</summary>
    public static AudioCodec Flac => FlacCodec.Instance;

    /// <summary>Gets the ALAC (Apple Lossless) codec.</summary>
    public static AudioCodec Alac => AlacCodec.Instance;

    /// <summary>Gets the Opus codec.</summary>
    public static AudioCodec Opus => OpusCodec.Instance;

    /// <summary>Gets the Vorbis codec (decode only).</summary>
    public static AudioCodec Vorbis => VorbisCodec.Instance;

    /// <summary>Gets the MPEG audio codec: MP3, MP2 and MP1 (decode only).</summary>
    public static AudioCodec Mp3 => Mp3Codec.Instance;

    /// <summary>Creates a decoder for a track.</summary>
    /// <param name="track">The track.</param>
    /// <param name="options">How to decode, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The decoder. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered codec decodes the track.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
    /// </example>
    public static IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        context ??= MediaContext.Default;
        AudioCodec codec = context.FindAudioCodec(track)
            ?? throw new UnsupportedFormatException($"No registered codec decodes '{track.CodecId}' (track {track.Number}).");
        return codec.CreateDecoder(track, options, context);
    }

    /// <summary>Finds the registered audio codec with a codec id.</summary>
    /// <param name="codecId">The Matroska codec id, such as <see cref="CodecIds.Flac"/>.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="UnsupportedFormatException">No codec with that id is registered.</exception>
    public static AudioCodec Get(string codecId, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(codecId);
        context ??= MediaContext.Default;
        return context.AudioCodecs.FirstOrDefault(c => string.Equals(c.CodecId, codecId, StringComparison.Ordinal))
            ?? throw new UnsupportedFormatException($"No audio codec '{codecId}' is registered.");
    }
}
