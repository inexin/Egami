using System.Buffers.Binary;
using System.Numerics;

using Egami.Video.Audio;

namespace Egami.Video.Containers;

/// <summary>
/// Apple's AudioChannelLayout (Core Audio Format specification, "Channel Layout Chunk"), as AIFF-C's CHAN and CAF's chan chunks
/// store it, big-endian: a layout tag, a channel bitmap and channel descriptions. The bitmap has the bit values of WAV's channel
/// mask. The reader takes the bitmap and the common fixed layouts; the writer stores the bitmap.
/// </summary>
internal static class CoreAudioLayout
{
    /// <summary>kAudioChannelLayoutTag_UseChannelBitmap: the layout is the bitmap.</summary>
    private const uint UseChannelBitmap = 1 << 16;

    /// <summary>The fixed layouts read, by tag (number in the top 16 bits, channel count in the low 16), all in WAV's channel order.</summary>
    private static readonly Dictionary<uint, Speakers> Tags = new()
    {
        [(100u << 16) | 1] = ChannelLayout.Mono.Speakers,
        [(101u << 16) | 2] = ChannelLayout.Stereo.Speakers,
        [(108u << 16) | 4] = ChannelLayout.Quad.Speakers,
        [(113u << 16) | 3] = ChannelLayout.Surround30.Speakers,
        [(115u << 16) | 4] = ChannelLayout.Surround30.Speakers | Speakers.BackCenter,
        [(117u << 16) | 5] = ChannelLayout.Surround50.Speakers,
        [(121u << 16) | 6] = ChannelLayout.Surround51.Speakers,
        [(125u << 16) | 7] = ChannelLayout.Surround51.Speakers | Speakers.BackCenter,
    };

    /// <summary>Returns the layout a chunk body gives for a track of <paramref name="channels"/>, or null when it gives none this reader knows.</summary>
    public static ChannelLayout? Read(ReadOnlySpan<byte> body, int channels)
    {
        if (body.Length < 12 || channels is < 1 or > ChannelLayout.MaxChannels)
        {
            return null;
        }

        uint tag = BinaryPrimitives.ReadUInt32BigEndian(body);
        Speakers speakers = tag == UseChannelBitmap
            ? (Speakers)(BinaryPrimitives.ReadUInt32BigEndian(body[4..]) & 0x3FFFF)
            : Tags.GetValueOrDefault(tag);
        if (speakers == Speakers.None || BitOperations.PopCount((uint)speakers) != channels)
        {
            return null;
        }

        ChannelLayout layout = new(speakers, channels);
        return layout.IsPositioned ? layout : null;
    }

    /// <summary>Builds a chunk body that stores a positioned layout as a bitmap, or null for an unpositioned one.</summary>
    public static byte[]? Write(ChannelLayout layout)
    {
        if (!layout.IsPositioned || layout.Speakers == Speakers.None)
        {
            return null;
        }

        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(body, UseChannelBitmap);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)layout.Speakers);
        return body;
    }
}
