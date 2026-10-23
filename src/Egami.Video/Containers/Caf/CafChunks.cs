using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Caf;

/// <summary>
/// The desc chunk (CAFAudioDescription): sample rate, format id, format flags, bytes and frames per packet (0 when they vary
/// and the packet table gives them), channels and bits per channel.
/// </summary>
internal sealed record CafDescription(double SampleRate, string FormatId, uint Flags, uint BytesPerPacket, uint FramesPerPacket, uint Channels, uint Bits)
{
    /// <summary>The size of a desc body.</summary>
    public const int Size = 32;

    /// <summary>kCAFLinearPCMFormatFlagIsFloat.</summary>
    public const uint FlagFloat = 1;

    /// <summary>kCAFLinearPCMFormatFlagIsLittleEndian.</summary>
    public const uint FlagLittleEndian = 2;

    public static CafDescription? Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < Size)
        {
            return null;
        }

        return new CafDescription(
            BinaryPrimitives.ReadDoubleBigEndian(body),
            Encoding.Latin1.GetString(body.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(body[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[20..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[24..]),
            BinaryPrimitives.ReadUInt32BigEndian(body[28..]));
    }

    public void Write(Span<byte> body)
    {
        BinaryPrimitives.WriteDoubleBigEndian(body, SampleRate);
        Encoding.Latin1.GetBytes(FormatId, body.Slice(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(body[12..], Flags);
        BinaryPrimitives.WriteUInt32BigEndian(body[16..], BytesPerPacket);
        BinaryPrimitives.WriteUInt32BigEndian(body[20..], FramesPerPacket);
        BinaryPrimitives.WriteUInt32BigEndian(body[24..], Channels);
        BinaryPrimitives.WriteUInt32BigEndian(body[28..], Bits);
    }
}

/// <summary>The CAF chunk readers and writers that are not tied to one direction: the packet table, info, strings and markers.</summary>
internal static class CafChunks
{
    /// <summary>The bytes of a pakt header: packets, valid frames, priming frames and remainder frames.</summary>
    public const int PacketTableHeader = 24;

    /// <summary>kCAFMarkerType_Generic.</summary>
    private const uint GenericMarker = 0;

    /// <summary>kCAF_SMPTE_TimeTypeNone.</summary>
    private const uint NoSmpte = 0;

    /// <summary>The bytes of one CAFMarker: type, frame position, id, SMPTE time and channel.</summary>
    private const int MarkerSize = 28;

    /// <summary>Apple's info keys (CAF specification, "Information Chunk") and the tag names they map to.</summary>
    private static readonly (string Key, string Tag)[] InfoKeys =
    [
        ("title", "TITLE"), ("artist", "ARTIST"), ("album", "ALBUM"), ("track number", "PART_NUMBER"), ("year", "DATE"),
        ("composer", "COMPOSER"), ("lyricist", "LYRICIST"), ("genre", "GENRE"), ("tempo", "BPM"), ("comments", "COMMENT"),
        ("copyright", "COPYRIGHT"), ("recorded date", "DATE_RECORDED"), ("encoding application", "ENCODER"),
        ("key signature", "KEY"), ("time signature", "TIME_SIGNATURE"), ("source encoder", "ENCODED_BY"),
    ];

    /// <summary>Info keys that describe the stream rather than tag it: they are left out of the tags.</summary>
    private static readonly string[] StreamKeys = ["approximate duration in seconds", "nominal bit rate", "channel layout", "source bit depth"];

    /// <summary>Reads a BER variable-length integer from a packet table: 7 bits a byte, high bit set on all but the last.</summary>
    public static bool TryReadVarint(ReadOnlySpan<byte> data, ref int at, out long value)
    {
        value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (at >= data.Length)
            {
                return false;
            }

            byte b = data[at++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    public static void WriteVarint(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[10];
        int n = 0;
        do
        {
            bytes[n++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value != 0);
        for (int i = n - 1; i >= 0; i--)
        {
            stream.WriteByte((byte)(bytes[i] | (i > 0 ? 0x80 : 0)));
        }
    }

    /// <summary>Reads an info body (a count, then key and value strings, each null-terminated UTF-8) into tags.</summary>
    public static void ReadInfo(ReadOnlySpan<byte> body, Dictionary<string, string> tags)
    {
        if (body.Length < 4)
        {
            return;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body);
        int at = 4;
        for (uint i = 0; i < count && at < body.Length; i++)
        {
            string key = ReadString(body, ref at);
            string value = ReadString(body, ref at);
            if (key.Length == 0 || value.Length == 0 || StreamKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string tag = Array.Find(InfoKeys, k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)).Tag
                ?? (string.Equals(key, "encoder", StringComparison.OrdinalIgnoreCase) ? "ENCODER" : key.Trim().Replace(' ', '_').ToUpperInvariant());
            tags.TryAdd(tag, value);
        }
    }

    /// <summary>Builds an info body from tags, or null when there are none.</summary>
    public static byte[]? WriteInfo(IReadOnlyDictionary<string, string> tags)
    {
        List<(string, string)> entries = [];
        foreach ((string name, string value) in tags)
        {
            if (value.Length == 0 || name.Length == 0)
            {
                continue;
            }

            string key = Array.Find(InfoKeys, k => k.Tag == name).Key ?? name.Replace('_', ' ').ToLowerInvariant();
            entries.Add((key, value));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        using MemoryStream body = new();
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, (uint)entries.Count);
        body.Write(count);
        foreach ((string key, string value) in entries)
        {
            body.Write(Encoding.UTF8.GetBytes(key.Replace('\0', ' ')));
            body.WriteByte(0);
            body.Write(Encoding.UTF8.GetBytes(value.Replace('\0', ' ')));
            body.WriteByte(0);
        }

        return body.ToArray();
    }

    /// <summary>Reads a strg body: string id to its text.</summary>
    public static Dictionary<uint, string> ReadStrings(ReadOnlySpan<byte> body)
    {
        Dictionary<uint, string> strings = [];
        if (body.Length < 4)
        {
            return strings;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body);
        long tableEnd = 4 + (count * 12L);
        if (tableEnd > body.Length)
        {
            return strings;
        }

        ReadOnlySpan<byte> data = body[(int)tableEnd..];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = body.Slice(4 + (i * 12), 12);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(entry);
            long offset = BinaryPrimitives.ReadInt64BigEndian(entry[4..]);
            if (offset < 0 || offset >= data.Length)
            {
                continue;
            }

            int at = (int)offset;
            strings.TryAdd(id, ReadString(data, ref at));
        }

        return strings;
    }

    /// <summary>Reads a mark body: each marker's id and sample frame.</summary>
    public static List<(uint Id, double Frame)> ReadMarkers(ReadOnlySpan<byte> body)
    {
        List<(uint, double)> markers = [];
        if (body.Length < 8)
        {
            return markers;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        ReadMarkerList(body[8..], count, markers);
        return markers;
    }

    /// <summary>Reads a regn body: each region's markers, start and end frames from the first and last of them.</summary>
    public static List<(uint Id, double Start, double End)> ReadRegions(ReadOnlySpan<byte> body)
    {
        List<(uint, double, double)> regions = [];
        if (body.Length < 8)
        {
            return regions;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        int at = 8;
        for (uint i = 0; i < count && at + 12 <= body.Length; i++)
        {
            uint markerCount = BinaryPrimitives.ReadUInt32BigEndian(body[(at + 8)..]);
            at += 12;
            if (markerCount > (body.Length - at) / MarkerSize)
            {
                break;
            }

            List<(uint Id, double Frame)> markers = [];
            ReadMarkerList(body[at..], markerCount, markers);
            at += (int)markerCount * MarkerSize;
            if (markers.Count > 0)
            {
                regions.Add((markers[0].Id, markers.Min(m => m.Frame), markers.Max(m => m.Frame)));
            }
        }

        return regions;
    }

    /// <summary>Builds the strg and mark bodies that store chapters as named generic markers.</summary>
    public static (byte[] Strings, byte[] Markers) WriteMarkers(IReadOnlyList<ChapterInfo> chapters, int sampleRate)
    {
        using MemoryStream strings = new();
        using MemoryStream text = new();
        using MemoryStream markers = new();
        Span<byte> field = stackalloc byte[MarkerSize];
        int named = chapters.Count(c => !string.IsNullOrEmpty(c.Title));
        BinaryPrimitives.WriteUInt32BigEndian(field, (uint)named);
        strings.Write(field[..4]);
        BinaryPrimitives.WriteUInt32BigEndian(field, NoSmpte);
        BinaryPrimitives.WriteUInt32BigEndian(field[4..], (uint)chapters.Count);
        markers.Write(field[..8]);
        for (int i = 0; i < chapters.Count; i++)
        {
            uint id = (uint)(i + 1);
            if (!string.IsNullOrEmpty(chapters[i].Title))
            {
                BinaryPrimitives.WriteUInt32BigEndian(field, id);
                BinaryPrimitives.WriteInt64BigEndian(field[4..], text.Length);
                strings.Write(field[..12]);
                text.Write(Encoding.UTF8.GetBytes(chapters[i].Title!.Replace('\0', ' ')));
                text.WriteByte(0);
            }

            field.Clear();
            BinaryPrimitives.WriteUInt32BigEndian(field, GenericMarker);
            BinaryPrimitives.WriteDoubleBigEndian(field[4..], Math.Max(0, chapters[i].Start.Rescale(new Rational(1, sampleRate)).Ticks));
            BinaryPrimitives.WriteUInt32BigEndian(field[12..], id);
            markers.Write(field);
        }

        text.Position = 0;
        text.CopyTo(strings);
        return (strings.ToArray(), markers.ToArray());
    }

    private static void ReadMarkerList(ReadOnlySpan<byte> data, uint count, List<(uint Id, double Frame)> markers)
    {
        for (int i = 0; i < count && (i + 1) * MarkerSize <= data.Length; i++)
        {
            ReadOnlySpan<byte> m = data.Slice(i * MarkerSize, MarkerSize);
            double frame = BinaryPrimitives.ReadDoubleBigEndian(m[4..]);
            if (double.IsFinite(frame) && frame >= 0)
            {
                markers.Add((BinaryPrimitives.ReadUInt32BigEndian(m[12..]), frame));
            }
        }
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int at)
    {
        int end = data[at..].IndexOf((byte)0);
        int length = end < 0 ? data.Length - at : end;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data.Slice(at, length));
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(data.Slice(at, length));
        }

        at += length + (end < 0 ? 0 : 1);
        return text;
    }
}
