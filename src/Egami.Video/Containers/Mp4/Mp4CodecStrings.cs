using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// The RFC 6381 codecs parameter of a track as MP4 writes it, for HLS CODECS and DASH @codecs: avc1 and hvc1 from their
/// configurations (ISO/IEC 14496-15 annex E), av01 from av1C (AV1 ISOBMFF binding), vp09 and vp08 from vpcC, mp4a.40.n from
/// the AAC object type, and the sample entry's type for the rest (Opus, fLaC, PCM).
/// </summary>
internal static class Mp4CodecStrings
{
    public static string Of(TrackInfo track)
    {
        using BoxWriter w = new();
        Mp4SampleEntries.Write(w, track);
        byte[] entry = w.ToArray();
        string type = entry.Length >= 8 ? Encoding.Latin1.GetString(entry, 4, 4) : track.CodecId;
        if (track is AudioTrackInfo audio)
        {
            return audio.CodecId switch
            {
                CodecIds.Aac => $"mp4a.40.{AacObjectType(audio.CodecPrivate.Span)}",
                CodecIds.Mp3 => "mp4a.6b",
                CodecIds.Opus => "opus",
                _ => type,
            };
        }

        if (track is not VideoTrackInfo)
        {
            return type;
        }

        // A visual sample entry's boxes start after its 8-byte header and 78 bytes of fields.
        ReadOnlySpan<byte> config = Child(entry, 86, type switch { "avc1" => "avcC", "hvc1" => "hvcC", "av01" => "av1C", "vp09" or "vp08" => "vpcC", _ => "esds" });
        return type switch
        {
            "avc1" when config.Length >= 4 => $"avc1.{config[1]:x2}{config[2]:x2}{config[3]:x2}",
            "hvc1" when config.Length >= 13 => Hevc(config),
            "av01" when config.Length >= 3 => Av1(config),
            "vp09" or "vp08" when config.Length >= 7 => string.Create(CultureInfo.InvariantCulture, $"{type}.{config[4]:D2}.{config[5]:D2}.{config[6] >> 4:D2}"),
            "mp4v" when track.CodecId == CodecIds.MotionJpeg => "mp4v.6c",
            "mp4v" => "mp4v.20",
            _ => type,
        };
    }

    private static string Hevc(ReadOnlySpan<byte> c)
    {
        int space = c[1] >> 6, tier = (c[1] >> 5) & 1, profile = c[1] & 0x1F;
        uint compatible = BinaryPrimitives.ReadUInt32BigEndian(c[2..]);
        uint reversed = 0;
        for (int i = 0; i < 32; i++)
        {
            reversed |= ((compatible >> i) & 1) << (31 - i);
        }

        StringBuilder s = new("hvc1.");
        if (space > 0)
        {
            s.Append((char)('A' + space - 1));
        }

        s.Append(CultureInfo.InvariantCulture, $"{profile}.{reversed:X}.{(tier == 1 ? 'H' : 'L')}{c[12]}");
        int constraints = 6;
        while (constraints > 0 && c[6 + constraints - 1] == 0)
        {
            constraints--;
        }

        for (int i = 0; i < constraints; i++)
        {
            s.Append(CultureInfo.InvariantCulture, $".{c[6 + i]:X2}");
        }

        return s.ToString();
    }

    private static string Av1(ReadOnlySpan<byte> c)
    {
        int profile = c[1] >> 5, level = c[1] & 0x1F;
        bool tier = (c[2] & 0x80) != 0, high = (c[2] & 0x40) != 0, twelve = (c[2] & 0x20) != 0;
        int depth = twelve ? 12 : high ? 10 : 8;
        return string.Create(CultureInfo.InvariantCulture, $"av01.{profile}.{level:D2}{(tier ? 'H' : 'M')}.{depth:D2}");
    }

    /// <summary>Gets the audio object type of an AudioSpecificConfig, 2 (AAC LC) when it is missing.</summary>
    private static int AacObjectType(ReadOnlySpan<byte> config)
    {
        if (config.Length == 0)
        {
            return 2;
        }

        int type = config[0] >> 3;
        return type == 31 && config.Length >= 2 ? 32 + (((config[0] & 7) << 3) | (config[1] >> 5)) : type;
    }

    /// <summary>Finds a child box among the boxes from <paramref name="at"/>; returns its content, empty when it is not there.</summary>
    private static ReadOnlySpan<byte> Child(byte[] entry, int at, string type)
    {
        while (at + 8 <= entry.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(at));
            if (size < 8 || at + size > entry.Length)
            {
                break;
            }

            if (Encoding.Latin1.GetString(entry, at + 4, 4) == type)
            {
                return entry.AsSpan(at + 8, size - 8);
            }

            at += size;
        }

        return [];
    }
}
