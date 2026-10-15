using Egami.Fonts.Parsing;

namespace Egami.Fonts.Tables;

/// <summary>
/// The glyph names of a 'post' table: version 1 (the Macintosh standard order), 2 (indices into that order or the table's own
/// Pascal strings) and 2.5 (offsets into that order). Versions 3 and 4 have none.
/// </summary>
internal static class PostNames
{
    /// <summary>The 258 glyph names of the Macintosh standard order (OpenType 'post' table specification).</summary>
    public static readonly string[] MacStandard =
    [
        ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand",
        "quotesingle", "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two",
        "three", "four", "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y",
        "Z", "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d", "e", "f", "g",
        "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar",
        "braceright", "asciitilde", "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute",
        "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis",
        "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute", "ograve", "ocircumflex", "odieresis", "otilde", "uacute",
        "ugrave", "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph", "germandbls",
        "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash", "infinity", "plusminus",
        "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation", "product", "pi", "integral", "ordfeminine",
        "ordmasculine", "Omega", "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal",
        "Delta", "guillemotleft", "guillemotright", "ellipsis", "nonbreakingspace", "Agrave", "Atilde", "Otilde", "OE", "oe",
        "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge", "ydieresis",
        "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl", "periodcentered",
        "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute", "Edieresis", "Egrave", "Iacute",
        "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex", "apple", "Ograve", "Uacute", "Ucircumflex", "Ugrave",
        "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek", "caron",
        "Lslash", "lslash", "Scaron", "scaron", "Zcaron", "zcaron", "brokenbar", "Eth", "eth", "Yacute", "yacute", "Thorn", "thorn",
        "minus", "multiply", "onesuperior", "twosuperior", "threesuperior", "onehalf", "onequarter", "threequarters", "franc",
        "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla", "Cacute", "cacute", "Ccaron", "ccaron", "dcroat",
    ];

    /// <summary>
    /// Reads the names of the first <paramref name="glyphCount"/> glyphs; a glyph the table does not name, or names with an index
    /// past its strings, gets null. Returns null for versions without names.
    /// </summary>
    /// <param name="data">The post table.</param>
    /// <param name="offset">The table's offset in the file, for error messages.</param>
    /// <param name="glyphCount">The face's glyph count.</param>
    /// <param name="damaged">Set when an index or string runs past the table.</param>
    public static string?[]? Read(ReadOnlySpan<byte> data, long offset, int glyphCount, out bool damaged)
    {
        damaged = false;
        var r = new FontReader(data, offset);
        uint version = r.ReadUInt32();
        switch (version)
        {
            case 0x00010000:
                {
                    var names = new string?[glyphCount];
                    for (int g = 0; g < glyphCount && g < MacStandard.Length; g++)
                    {
                        names[g] = MacStandard[g];
                    }

                    return names;
                }

            case 0x00020000:
                {
                    // A table naming more glyphs than the font has belongs to another font (a subset that kept its source's
                    // post table): none of its names can be trusted, as FreeType decides too.
                    r.Skip(28);
                    if (r.ReadUInt16() > glyphCount)
                    {
                        damaged = true;
                        return null;
                    }

                    return ReadVersion2(data, offset, glyphCount, ref damaged);
                }

            case 0x00025000:
                {
                    r.Skip(28);
                    int count = r.ReadUInt16();
                    var names = new string?[glyphCount];
                    for (int g = 0; g < count && g < glyphCount; g++)
                    {
                        if (r.Remaining < 1)
                        {
                            damaged = true;
                            break;
                        }

                        int index = g + (sbyte)r.ReadByte();
                        if ((uint)index < (uint)MacStandard.Length)
                        {
                            names[g] = MacStandard[index];
                        }
                        else
                        {
                            damaged = true;
                        }
                    }

                    return names;
                }

            default:
                return null;
        }
    }

    private static string?[] ReadVersion2(ReadOnlySpan<byte> data, long offset, int glyphCount, ref bool damaged)
    {
        var r = new FontReader(data, offset);
        r.Skip(32);
        int count = r.ReadUInt16();
        int named = Math.Min(count, r.Remaining / 2);
        damaged |= named < count;
        var indices = new ushort[named];
        for (int g = 0; g < named; g++)
        {
            indices[g] = r.ReadUInt16();
        }

        // The table's own strings follow the indices, one Pascal string each, numbered from 258.
        var strings = new List<string>();
        while (r.Remaining > 0)
        {
            int length = r.ReadByte();
            if (length > r.Remaining)
            {
                damaged = true;
                break;
            }

            strings.Add(System.Text.Encoding.Latin1.GetString(r.ReadBytes(length)));
        }

        var names = new string?[glyphCount];
        for (int g = 0; g < named && g < glyphCount; g++)
        {
            int index = indices[g];
            if (index < MacStandard.Length)
            {
                names[g] = MacStandard[index];
            }
            else if (index - MacStandard.Length < strings.Count)
            {
                names[g] = strings[index - MacStandard.Length];
            }
            else
            {
                damaged = true;
            }
        }

        return names;
    }
}
