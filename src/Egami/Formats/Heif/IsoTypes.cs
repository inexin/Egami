namespace Egami.Formats.Heif;

/// <summary>Four-character codes as big-endian numbers, as ISO BMFF stores box and item types; the public type is <see cref="Iso.FourCC"/>.</summary>
internal static class IsoTypes
{
    internal const uint Ftyp = 0x66747970;
    internal const uint Meta = 0x6D657461;
    internal const uint Hdlr = 0x68646C72;
    internal const uint Pitm = 0x7069746D;
    internal const uint Iloc = 0x696C6F63;
    internal const uint Iinf = 0x69696E66;
    internal const uint Infe = 0x696E6665;
    internal const uint Iref = 0x69726566;
    internal const uint Iprp = 0x69707270;
    internal const uint Ipco = 0x6970636F;
    internal const uint Ipma = 0x69706D61;
    internal const uint Idat = 0x69646174;
    internal const uint Grpl = 0x6772706C;
    internal const uint Uuid = 0x75756964;
    internal const uint Mime = 0x6D696D65;
    internal const uint Uri = 0x75726920;
    internal const uint Pict = 0x70696374;

    /// <summary>Gets the code of four ASCII characters.</summary>
    internal static uint Of(ReadOnlySpan<char> text) => (uint)text[0] << 24 | (uint)text[1] << 16 | (uint)text[2] << 8 | text[3];

    /// <summary>Gets the code as text, with characters outside printable ASCII escaped.</summary>
    internal static string ToString(uint code)
    {
        Span<char> text = stackalloc char[16];
        int n = 0;
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte b = (byte)(code >> shift);
            if (b is >= 0x20 and < 0x7F)
            {
                text[n++] = (char)b;
            }
            else
            {
                text[n++] = '\\';
                text[n++] = 'x';
                text[n++] = "0123456789abcdef"[b >> 4];
                text[n++] = "0123456789abcdef"[b & 15];
            }
        }

        return new string(text[..n]);
    }
}
