namespace Egami.Fonts;

/// <summary>A four-byte OpenType tag, such as the table tag <c>glyf</c> or the feature tag <c>kern</c>.</summary>
/// <param name="Value">The tag's four bytes, first byte in the high bits.</param>
public readonly record struct FontTag(uint Value)
{
    /// <summary>Initializes a tag from its text, such as <c>"OS/2"</c>; shorter text is padded with spaces.</summary>
    /// <param name="tag">One to four characters in the range 0x20 to 0x7E.</param>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is empty, longer than four characters or not printable ASCII.</exception>
    public FontTag(string tag)
        : this(Parse(tag))
    {
    }

    /// <summary>Returns the tag as four characters, such as <c>"OS/2"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(Value >> (24 - (8 * i)));
            chars[i] = b is >= 0x20 and <= 0x7E ? (char)b : '?';
        }

        return new string(chars);
    }

    private static uint Parse(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length is < 1 or > 4)
        {
            throw new ArgumentException("A tag has one to four characters.", nameof(tag));
        }

        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            char c = i < tag.Length ? tag[i] : ' ';
            if (c is < (char)0x20 or > (char)0x7E)
            {
                throw new ArgumentException("A tag holds printable ASCII only.", nameof(tag));
            }

            value = (value << 8) | c;
        }

        return value;
    }
}

/// <summary>The table tags Egami.Fonts reads.</summary>
internal static class Tags
{
    public static readonly FontTag Cmap = new("cmap");
    public static readonly FontTag Head = new("head");
    public static readonly FontTag Hhea = new("hhea");
    public static readonly FontTag Hmtx = new("hmtx");
    public static readonly FontTag Maxp = new("maxp");
    public static readonly FontTag Name = new("name");
    public static readonly FontTag OS2 = new("OS/2");
    public static readonly FontTag Post = new("post");
    public static readonly FontTag Glyf = new("glyf");
    public static readonly FontTag Loca = new("loca");
    public static readonly FontTag Cff = new("CFF ");
    public static readonly FontTag Cff2 = new("CFF2");
    public static readonly FontTag Kern = new("kern");
    public static readonly FontTag Gpos = new("GPOS");
    public static readonly FontTag Gsub = new("GSUB");
    public static readonly FontTag Fvar = new("fvar");
    public static readonly FontTag Cvt = new("cvt ");
    public static readonly FontTag Fpgm = new("fpgm");
    public static readonly FontTag Prep = new("prep");
    public static readonly FontTag Gasp = new("gasp");
    public static readonly FontTag KernFeature = new("kern");
    public static readonly FontTag Dflt = new("DFLT");
    public static readonly FontTag Latn = new("latn");
}
