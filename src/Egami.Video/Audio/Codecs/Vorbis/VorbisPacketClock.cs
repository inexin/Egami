using Egami.Video.Containers.Ogg;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>
/// The samples each Vorbis packet of a track adds, from the setup header's modes: what Matroska and the muxers need to cut the
/// end of a stream (DiscardPadding) where Ogg has its granule positions.
/// </summary>
internal sealed class VorbisPacketClock
{
    private readonly VorbisDecoder _decoder;
    private int _previous;

    private VorbisPacketClock(VorbisDecoder decoder) => _decoder = decoder;

    /// <summary>Returns a clock for the track's Xiph-laced headers, or null when they cannot be read.</summary>
    public static VorbisPacketClock? Create(ReadOnlySpan<byte> codecPrivate)
    {
        try
        {
            return OggCodecs.XiphUnlace(codecPrivate) is { Count: 3 } headers ? new VorbisPacketClock(new VorbisDecoder(headers)) : null;
        }
        catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException)
        {
            return null;
        }
    }

    /// <summary>Returns the samples the packet adds (0 for the first after a reset).</summary>
    public int Next(ReadOnlySpan<byte> packet) => _decoder.PacketSamples(packet, ref _previous);

    /// <summary>Starts again, as the decoder does after a seek.</summary>
    public void Reset() => _previous = 0;
}
