using System.Buffers.Binary;

namespace Egami.Video.Containers.Avi;

/// <summary>Four-character codes as little-endian 32-bit values, the way RIFF stores them.</summary>
internal static class FourCC
{
    public const uint Riff = 'R' | ('I' << 8) | ('F' << 16) | ('F' << 24);
    public const uint List = 'L' | ('I' << 8) | ('S' << 16) | ('T' << 24);
    public const uint Avi = 'A' | ('V' << 8) | ('I' << 16) | (' ' << 24);
    public const uint Avix = 'A' | ('V' << 8) | ('I' << 16) | ('X' << 24);
    public const uint Hdrl = 'h' | ('d' << 8) | ('r' << 16) | ('l' << 24);
    public const uint Avih = 'a' | ('v' << 8) | ('i' << 16) | ('h' << 24);
    public const uint Strl = 's' | ('t' << 8) | ('r' << 16) | ('l' << 24);
    public const uint Strh = 's' | ('t' << 8) | ('r' << 16) | ('h' << 24);
    public const uint Strf = 's' | ('t' << 8) | ('r' << 16) | ('f' << 24);
    public const uint Strn = 's' | ('t' << 8) | ('r' << 16) | ('n' << 24);
    public const uint Indx = 'i' | ('n' << 8) | ('d' << 16) | ('x' << 24);
    public const uint Vprp = 'v' | ('p' << 8) | ('r' << 16) | ('p' << 24);
    public const uint Odml = 'o' | ('d' << 8) | ('m' << 16) | ('l' << 24);
    public const uint Dmlh = 'd' | ('m' << 8) | ('l' << 16) | ('h' << 24);
    public const uint Movi = 'm' | ('o' << 8) | ('v' << 16) | ('i' << 24);
    public const uint Rec = 'r' | ('e' << 8) | ('c' << 16) | (' ' << 24);
    public const uint Idx1 = 'i' | ('d' << 8) | ('x' << 16) | ('1' << 24);
    public const uint Junk = 'J' | ('U' << 8) | ('N' << 16) | ('K' << 24);
    public const uint Info = 'I' | ('N' << 8) | ('F' << 16) | ('O' << 24);
    public const uint Vids = 'v' | ('i' << 8) | ('d' << 16) | ('s' << 24);
    public const uint Auds = 'a' | ('u' << 8) | ('d' << 16) | ('s' << 24);
    public const uint Txts = 't' | ('x' << 8) | ('t' << 16) | ('s' << 24);
    public const uint Mjpg = 'M' | ('J' << 8) | ('P' << 16) | ('G' << 24);

    public static uint Read(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    public static uint FromString(string text) => (uint)(text[0] | (text[1] << 8) | (text[2] << 16) | (text[3] << 24));

    public static string ToString(uint code) =>
        string.Create(4, code, static (span, c) =>
        {
            for (int i = 0; i < 4; i++)
            {
                char ch = (char)((c >> (8 * i)) & 0xFF);
                span[i] = ch is >= ' ' and <= '~' ? ch : '?';
            }
        });

    /// <summary>Returns the stream number of a movi chunk id such as "01wb", or -1 when it is not one.</summary>
    public static int StreamNumber(uint code)
    {
        int d0 = (int)(code & 0xFF) - '0';
        int d1 = (int)((code >> 8) & 0xFF) - '0';
        if (d0 is < 0 or > 9 || d1 is < 0 or > 9)
        {
            return -1;
        }

        char c2 = (char)((code >> 16) & 0xFF);
        char c3 = (char)((code >> 24) & 0xFF);
        return char.IsAsciiLetter(c2) && char.IsAsciiLetter(c3) ? (d0 * 10) + d1 : -1;
    }

    /// <summary>Returns the stream number of an OpenDML standard index chunk id "ix##", or -1.</summary>
    public static int IndexStreamNumber(uint code)
    {
        if ((code & 0xFFFF) != ('i' | ('x' << 8)))
        {
            return -1;
        }

        int d0 = (int)((code >> 16) & 0xFF) - '0';
        int d1 = (int)((code >> 24) & 0xFF) - '0';
        return d0 is < 0 or > 9 || d1 is < 0 or > 9 ? -1 : (d0 * 10) + d1;
    }

    /// <summary>Makes a movi chunk id for a stream: two digits then a two-letter type such as "dc" or "wb".</summary>
    public static uint ChunkId(int stream, string type) =>
        (uint)(('0' + (stream / 10)) | (('0' + (stream % 10)) << 8) | (type[0] << 16) | (type[1] << 24));

    /// <summary>Whether a code looks like a plausible chunk id: four printable ASCII characters.</summary>
    public static bool IsPrintable(uint code)
    {
        for (int i = 0; i < 4; i++)
        {
            uint ch = (code >> (8 * i)) & 0xFF;
            if (ch is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>RIFF INFO chunk ids and the Matroska tag names they map to.</summary>
    public static readonly (string Chunk, string Tag)[] InfoTags =
    [
        ("INAM", "TITLE"), ("IART", "ARTIST"), ("ICMT", "COMMENT"), ("ICOP", "COPYRIGHT"), ("ICRD", "DATE"), ("IGNR", "GENRE"),
        ("ISFT", "ENCODER"), ("IENG", "ENGINEER"), ("IKEY", "KEYWORDS"), ("ILNG", "LANGUAGE"), ("ISBJ", "SUBJECT"),
        ("ISRC", "SOURCE"), ("IPRD", "ALBUM"), ("ITCH", "TECHNICIAN"),
    ];
}
