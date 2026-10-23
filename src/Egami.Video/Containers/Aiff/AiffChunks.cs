using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Aiff;

/// <summary>The COMM chunk: channels, sample frames, bits per sample, rate, and AIFF-C's compression type.</summary>
internal sealed record AiffCommon(int Channels, long Frames, int Bits, double SampleRate, string Compression);

/// <summary>The shared chunk readers and writers of AIFF and AIFF-C.</summary>
internal static class AiffChunks
{
    /// <summary>The AIFF-C version timestamp the FVER chunk holds (AIFF-C draft, version 1, 26 May 1990).</summary>
    public const uint AifcVersion1 = 0xA2805140;

    /// <summary>The text chunks and the tag names they map to.</summary>
    public static readonly (string Chunk, string Tag)[] TextChunks = [("NAME", "TITLE"), ("AUTH", "ARTIST"), ("(c) ", "COPYRIGHT"), ("ANNO", "COMMENT")];

    /// <summary>Reads a COMM body; AIFF's has 18 bytes, AIFF-C's adds the compression type and name.</summary>
    public static AiffCommon? ReadCommon(ReadOnlySpan<byte> body, bool aifc)
    {
        if (body.Length < 18)
        {
            return null;
        }

        string compression = aifc && body.Length >= 22 ? Encoding.ASCII.GetString(body.Slice(18, 4)) : "NONE";
        return new AiffCommon(
            BinaryPrimitives.ReadInt16BigEndian(body),
            BinaryPrimitives.ReadUInt32BigEndian(body[2..]),
            BinaryPrimitives.ReadInt16BigEndian(body[6..]),
            ReadExtended(body.Slice(8, 10)),
            compression);
    }

    /// <summary>Reads an 80-bit IEEE 754 extended float (sign, 15-bit exponent, 64-bit mantissa with its integer bit).</summary>
    public static double ReadExtended(ReadOnlySpan<byte> value)
    {
        int exponent = BinaryPrimitives.ReadUInt16BigEndian(value) & 0x7FFF;
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(value[2..]);
        if ((exponent == 0 && mantissa == 0) || exponent == 0x7FFF)
        {
            return 0;
        }

        double magnitude = Math.ScaleB(mantissa, exponent - 16383 - 63);
        return (value[0] & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>Writes a positive whole number as an 80-bit extended float.</summary>
    public static void WriteExtended(long value, Span<byte> destination)
    {
        destination[..10].Clear();
        if (value <= 0)
        {
            return;
        }

        int shift = System.Numerics.BitOperations.LeadingZeroCount((ulong)value);
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(16383 + 63 - shift));
        BinaryPrimitives.WriteUInt64BigEndian(destination[2..], (ulong)value << shift);
    }

    /// <summary>Reads a Pascal string (a count byte, the text, and a pad byte when the whole is odd); returns the bytes used.</summary>
    public static int ReadPascal(ReadOnlySpan<byte> data, out string text)
    {
        if (data.IsEmpty)
        {
            text = "";
            return 0;
        }

        int length = Math.Min(data[0], data.Length - 1);
        text = Text(data.Slice(1, length));
        int used = 1 + data[0];
        return used + (used & 1);
    }

    /// <summary>Writes a Pascal string, padded to an even length, cut to 255 bytes.</summary>
    public static void WritePascal(Stream destination, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int length = Math.Min(bytes.Length, 255);
        destination.WriteByte((byte)length);
        destination.Write(bytes, 0, length);
        if (((1 + length) & 1) != 0)
        {
            destination.WriteByte(0);
        }
    }

    /// <summary>Decodes chunk text: UTF-8 when it is valid, else Latin-1 (old files use Mac encodings, which UTF-8 rejects).</summary>
    public static string Text(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        bytes = end < 0 ? bytes : bytes[..end];
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).Trim();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes).Trim();
        }
    }

    /// <summary>Reads a MARK body: marker id to its sample frame and name.</summary>
    public static List<(int Id, long Frame, string Name)> ReadMarkers(ReadOnlySpan<byte> body)
    {
        List<(int, long, string)> markers = [];
        if (body.Length < 2)
        {
            return markers;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(body);
        int p = 2;
        for (int i = 0; i < count && p + 6 < body.Length; i++)
        {
            int id = BinaryPrimitives.ReadInt16BigEndian(body[p..]);
            long frame = BinaryPrimitives.ReadUInt32BigEndian(body[(p + 2)..]);
            p += 6 + ReadPascal(body[(p + 6)..], out string name);
            markers.Add((id, frame, name));
        }

        return markers;
    }

    /// <summary>Builds a MARK body from chapters, with sample-frame positions at the track's rate.</summary>
    public static byte[] WriteMarkers(IReadOnlyList<ChapterInfo> chapters, int sampleRate)
    {
        using MemoryStream body = new();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)Math.Min(chapters.Count, short.MaxValue));
        body.Write(header[..2]);
        for (int i = 0; i < chapters.Count && i < short.MaxValue; i++)
        {
            long frame = Math.Clamp(chapters[i].Start.Rescale(new Rational(1, sampleRate)).Ticks, 0, uint.MaxValue);
            BinaryPrimitives.WriteInt16BigEndian(header, (short)(i + 1));
            BinaryPrimitives.WriteUInt32BigEndian(header[2..], (uint)frame);
            body.Write(header);
            WritePascal(body, chapters[i].Title ?? "");
        }

        return body.ToArray();
    }
}
