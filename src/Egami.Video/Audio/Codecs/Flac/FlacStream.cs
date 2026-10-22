using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>FLAC's STREAMINFO block (RFC 9639 section 8.2).</summary>
internal sealed record FlacStreamInfo(
    int MinBlockSize,
    int MaxBlockSize,
    int MinFrameSize,
    int MaxFrameSize,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long TotalSamples,
    byte[] Md5)
{
    public const int Size = 34;

    public static FlacStreamInfo? Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
        {
            return null;
        }

        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(b[10..]);
        int rate = (int)(packed >> 44);
        int channels = (int)((packed >> 41) & 7) + 1;
        int bits = (int)((packed >> 36) & 31) + 1;
        long total = (long)(packed & 0xFFFFFFFFF);
        return new FlacStreamInfo(
            BinaryPrimitives.ReadUInt16BigEndian(b),
            BinaryPrimitives.ReadUInt16BigEndian(b[2..]),
            (b[4] << 16) | (b[5] << 8) | b[6],
            (b[7] << 16) | (b[8] << 8) | b[9],
            rate,
            channels,
            bits,
            total,
            b.Slice(18, 16).ToArray());
    }

    public byte[] Write()
    {
        byte[] b = new byte[Size];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)MinBlockSize);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)MaxBlockSize);
        b[4] = (byte)(MinFrameSize >> 16);
        b[5] = (byte)(MinFrameSize >> 8);
        b[6] = (byte)MinFrameSize;
        b[7] = (byte)(MaxFrameSize >> 16);
        b[8] = (byte)(MaxFrameSize >> 8);
        b[9] = (byte)MaxFrameSize;
        ulong packed = ((ulong)SampleRate << 44) | ((ulong)(Channels - 1) << 41) | ((ulong)(BitsPerSample - 1) << 36) | ((ulong)TotalSamples & 0xFFFFFFFFF);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(10), packed);
        Md5.AsSpan(0, 16).CopyTo(b.AsSpan(18));
        return b;
    }
}

/// <summary>A FLAC metadata block: its type and body.</summary>
internal readonly record struct FlacBlock(int Type, byte[] Body)
{
    public const int StreamInfo = 0;
    public const int Padding = 1;
    public const int SeekTable = 3;
    public const int VorbisComment = 4;
    public const int Picture = 6;
}

/// <summary>Reads and writes FLAC metadata blocks, Vorbis comments, pictures and seek tables.</summary>
internal static class FlacMetadata
{
    /// <summary>Splits "fLaC" and the metadata blocks after it (as Matroska's CodecPrivate holds them) into blocks.</summary>
    public static List<FlacBlock> ReadBlocks(ReadOnlySpan<byte> data)
    {
        List<FlacBlock> blocks = [];
        int p = data.StartsWith("fLaC"u8) ? 4 : 0;
        while (p + 4 <= data.Length)
        {
            int type = data[p] & 0x7F;
            bool last = (data[p] & 0x80) != 0;
            int length = (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
            if (length > data.Length - p - 4)
            {
                break;
            }

            blocks.Add(new FlacBlock(type, data.Slice(p + 4, length).ToArray()));
            p += 4 + length;
            if (last)
            {
                break;
            }
        }

        return blocks;
    }

    /// <summary>Writes "fLaC" and blocks, the last one flagged.</summary>
    public static byte[] WriteBlocks(IReadOnlyList<FlacBlock> blocks)
    {
        using MemoryStream s = new();
        s.Write("fLaC"u8);
        for (int i = 0; i < blocks.Count; i++)
        {
            int length = blocks[i].Body.Length;
            s.WriteByte((byte)(blocks[i].Type | (i == blocks.Count - 1 ? 0x80 : 0)));
            s.WriteByte((byte)(length >> 16));
            s.WriteByte((byte)(length >> 8));
            s.WriteByte((byte)length);
            s.Write(blocks[i].Body);
        }

        return s.ToArray();
    }

    /// <summary>Reads a VORBIS_COMMENT block (little-endian lengths) into tags with upper-case names.</summary>
    public static void ReadComments(ReadOnlySpan<byte> b, Dictionary<string, string> tags)
    {
        if (b.Length < 8)
        {
            return;
        }

        long vendor = BinaryPrimitives.ReadUInt32LittleEndian(b);
        if (vendor > b.Length - 8)
        {
            return;
        }

        int p = 4 + (int)vendor;
        long count = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
        p += 4;
        for (long i = 0; i < count && p + 4 <= b.Length; i++)
        {
            long length = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
            p += 4;
            if (length > b.Length - p)
            {
                break;
            }

            string comment = Encoding.UTF8.GetString(b.Slice(p, (int)length));
            p += (int)length;
            int eq = comment.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                string key = comment[..eq].ToUpperInvariant();
                string value = comment[(eq + 1)..];
                tags[key] = tags.TryGetValue(key, out string? existing) ? existing + "; " + value : value;
            }
        }
    }

    /// <summary>Returns the layout a WAVEFORMATEXTENSIBLE_CHANNEL_MASK tag names, or null.</summary>
    public static ChannelLayout? LayoutFromTags(IReadOnlyDictionary<string, string> tags, int channels)
    {
        if (!tags.TryGetValue("WAVEFORMATEXTENSIBLE_CHANNEL_MASK", out string? text))
        {
            return null;
        }

        text = text.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? text[2..] : text, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint mask))
        {
            return null;
        }

        return Containers.Wav.WavChunks.LayoutFromMask(mask, channels);
    }

    /// <summary>Builds a VORBIS_COMMENT block.</summary>
    public static byte[] WriteComments(string vendor, IReadOnlyDictionary<string, string> tags)
    {
        using MemoryStream s = new();
        Span<byte> n = stackalloc byte[4];
        byte[] v = Encoding.UTF8.GetBytes(vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)v.Length);
        s.Write(n);
        s.Write(v);
        BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)tags.Count);
        s.Write(n);
        foreach ((string key, string value) in tags)
        {
            byte[] c = Encoding.UTF8.GetBytes(key + "=" + value);
            BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)c.Length);
            s.Write(n);
            s.Write(c);
        }

        return s.ToArray();
    }

    /// <summary>Reads a PICTURE block as an attachment; the front cover is named cover.*, as Matroska's convention says.</summary>
    public static AttachmentInfo? ReadPicture(ReadOnlySpan<byte> b, int index)
    {
        try
        {
            int p = 0;
            uint type = BinaryPrimitives.ReadUInt32BigEndian(b);
            p += 4;
            string mime = Encoding.ASCII.GetString(Field(b, ref p));
            string description = Encoding.UTF8.GetString(Field(b, ref p));
            p += 16;
            ReadOnlySpan<byte> data = Field(b, ref p);
            string extension = mime switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin",
            };
            string name = type == 3 && index == 0 ? "cover" + extension : $"picture-{index}-{type}{extension}";
            return new AttachmentInfo(name, mime.Length > 0 ? mime : "application/octet-stream", data.ToArray(), description.Length > 0 ? description : null);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Builds a PICTURE block for an image attachment: type 3 (front cover) for cover art, else 0 (other).</summary>
    public static byte[] WritePicture(AttachmentInfo attachment)
    {
        using MemoryStream s = new();
        Span<byte> n = stackalloc byte[4];
        static void U32(uint value, Stream stream, Span<byte> scratch)
        {
            BinaryPrimitives.WriteUInt32BigEndian(scratch, value);
            stream.Write(scratch);
        }

        byte[] mime = Encoding.ASCII.GetBytes(attachment.MediaType);
        byte[] description = Encoding.UTF8.GetBytes(attachment.Description ?? "");
        U32(attachment.IsCoverArt ? 3u : 0u, s, n);
        U32((uint)mime.Length, s, n);
        s.Write(mime);
        U32((uint)description.Length, s, n);
        s.Write(description);
        s.Write(new byte[16]); // width, height, depth and colors: 0 for unknown
        U32((uint)attachment.Data.Length, s, n);
        s.Write(attachment.Data.Span);
        return s.ToArray();
    }

    /// <summary>Reads a SEEKTABLE block's points (sample number, byte offset from the first frame), placeholders left out.</summary>
    public static List<(long Sample, long Offset)> ReadSeekTable(ReadOnlySpan<byte> b)
    {
        List<(long, long)> points = [];
        for (int p = 0; p + 18 <= b.Length; p += 18)
        {
            ulong sample = BinaryPrimitives.ReadUInt64BigEndian(b[p..]);
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(b[(p + 8)..]);
            if (sample != ulong.MaxValue && sample < long.MaxValue && offset < long.MaxValue)
            {
                points.Add(((long)sample, (long)offset));
            }
        }

        return points;
    }

    /// <summary>Builds a SEEKTABLE block.</summary>
    public static byte[] WriteSeekTable(IReadOnlyList<(long Sample, long Offset, int Samples)> points)
    {
        byte[] b = new byte[18 * points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(18 * i), (ulong)points[i].Sample);
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan((18 * i) + 8), (ulong)points[i].Offset);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan((18 * i) + 16), (ushort)points[i].Samples);
        }

        return b;
    }

    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> b, ref int p)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
        p += 4;
        if (length > b.Length - p)
        {
            throw new ArgumentOutOfRangeException(nameof(b));
        }

        ReadOnlySpan<byte> field = b.Slice(p, (int)length);
        p += (int)length;
        return field;
    }
}

/// <summary>FLAC's CRC-8 (polynomial x^8 + x^2 + x + 1) and CRC-16 (x^16 + x^15 + x^2 + 1), both starting at zero.</summary>
internal static class FlacCrc
{
    private static readonly byte[] s_crc8 = Table8();
    private static readonly ushort[] s_crc16 = Table16();

    /// <summary>Gets the CRC-16 table, for callers that run the CRC a byte at a time.</summary>
    public static ushort[] Crc16Table => s_crc16;

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc = s_crc8[crc ^ b];
        }

        return crc;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data, ushort crc = 0)
    {
        ushort[] table = s_crc16;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ table[(crc >> 8) ^ b]);
        }

        return crc;
    }

    private static byte[] Table8()
    {
        byte[] t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
            }

            t[i] = (byte)c;
        }

        return t;
    }

    private static ushort[] Table16()
    {
        ushort[] t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            }

            t[i] = (ushort)c;
        }

        return t;
    }
}
