namespace Egami.Fonts.Cff;

/// <summary>
/// The CFF standard strings, the Standard and Expert encodings and the Expert charsets, from Adobe Technical Note #5176,
/// appendices A to C.
/// </summary>
internal static class CffStandard
{
    public const int StandardStringCount = 391;

    /// <summary>The last SID of the ISOAdobe charset (zcaron).</summary>
    public const int IsoAdobeLastSid = 228;

    private static readonly string[] s_strings =
    [
        ".notdef", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quoteright", "parenleft",
        "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five", "six",
        "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D", "E", "F",
        "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "bracketleft",
        "backslash", "bracketright", "asciicircum", "underscore", "quoteleft", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k",
        "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar", "braceright", "asciitilde",
        "exclamdown", "cent", "sterling", "fraction", "yen", "florin", "section", "currency", "quotesingle", "quotedblleft",
        "guillemotleft", "guilsinglleft", "guilsinglright", "fi", "fl", "endash", "dagger", "daggerdbl", "periodcentered",
        "paragraph", "bullet", "quotesinglbase", "quotedblbase", "quotedblright", "guillemotright", "ellipsis", "perthousand",
        "questiondown", "grave", "acute", "circumflex", "tilde", "macron", "breve", "dotaccent", "dieresis", "ring", "cedilla",
        "hungarumlaut", "ogonek", "caron", "emdash", "AE", "ordfeminine", "Lslash", "Oslash", "OE", "ordmasculine", "ae", "dotlessi",
        "lslash", "oslash", "oe", "germandbls", "onesuperior", "logicalnot", "mu", "trademark", "Eth", "onehalf", "plusminus",
        "Thorn", "onequarter", "divide", "brokenbar", "degree", "thorn", "threequarters", "twosuperior", "registered", "minus", "eth",
        "multiply", "threesuperior", "copyright", "Aacute", "Acircumflex", "Adieresis", "Agrave", "Aring", "Atilde", "Ccedilla",
        "Eacute", "Ecircumflex", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Ntilde", "Oacute",
        "Ocircumflex", "Odieresis", "Ograve", "Otilde", "Scaron", "Uacute", "Ucircumflex", "Udieresis", "Ugrave", "Yacute",
        "Ydieresis", "Zcaron", "aacute", "acircumflex", "adieresis", "agrave", "aring", "atilde", "ccedilla", "eacute",
        "ecircumflex", "edieresis", "egrave", "iacute", "icircumflex", "idieresis", "igrave", "ntilde", "oacute", "ocircumflex",
        "odieresis", "ograve", "otilde", "scaron", "uacute", "ucircumflex", "udieresis", "ugrave", "yacute", "ydieresis", "zcaron",
        "exclamsmall", "Hungarumlautsmall", "dollaroldstyle", "dollarsuperior", "ampersandsmall", "Acutesmall", "parenleftsuperior",
        "parenrightsuperior", "twodotenleader", "onedotenleader", "zerooldstyle", "oneoldstyle", "twooldstyle", "threeoldstyle",
        "fouroldstyle", "fiveoldstyle", "sixoldstyle", "sevenoldstyle", "eightoldstyle", "nineoldstyle", "commasuperior",
        "threequartersemdash", "periodsuperior", "questionsmall", "asuperior", "bsuperior", "centsuperior", "dsuperior", "esuperior",
        "isuperior", "lsuperior", "msuperior", "nsuperior", "osuperior", "rsuperior", "ssuperior", "tsuperior", "ff", "ffi", "ffl",
        "parenleftinferior", "parenrightinferior", "Circumflexsmall", "hyphensuperior", "Gravesmall", "Asmall", "Bsmall", "Csmall",
        "Dsmall", "Esmall", "Fsmall", "Gsmall", "Hsmall", "Ismall", "Jsmall", "Ksmall", "Lsmall", "Msmall", "Nsmall", "Osmall",
        "Psmall", "Qsmall", "Rsmall", "Ssmall", "Tsmall", "Usmall", "Vsmall", "Wsmall", "Xsmall", "Ysmall", "Zsmall",
        "colonmonetary", "onefitted", "rupiah", "Tildesmall", "exclamdownsmall", "centoldstyle", "Lslashsmall", "Scaronsmall",
        "Zcaronsmall", "Dieresissmall", "Brevesmall", "Caronsmall", "Dotaccentsmall", "Macronsmall", "figuredash", "hypheninferior",
        "Ogoneksmall", "Ringsmall", "Cedillasmall", "questiondownsmall", "oneeighth", "threeeighths", "fiveeighths", "seveneighths",
        "onethird", "twothirds", "zerosuperior", "foursuperior", "fivesuperior", "sixsuperior", "sevensuperior", "eightsuperior",
        "ninesuperior", "zeroinferior", "oneinferior", "twoinferior", "threeinferior", "fourinferior", "fiveinferior", "sixinferior",
        "seveninferior", "eightinferior", "nineinferior", "centinferior", "dollarinferior", "periodinferior", "commainferior",
        "Agravesmall", "Aacutesmall", "Acircumflexsmall", "Atildesmall", "Adieresissmall", "Aringsmall", "AEsmall", "Ccedillasmall",
        "Egravesmall", "Eacutesmall", "Ecircumflexsmall", "Edieresissmall", "Igravesmall", "Iacutesmall", "Icircumflexsmall",
        "Idieresissmall", "Ethsmall", "Ntildesmall", "Ogravesmall", "Oacutesmall", "Ocircumflexsmall", "Otildesmall",
        "Odieresissmall", "OEsmall", "Oslashsmall", "Ugravesmall", "Uacutesmall", "Ucircumflexsmall", "Udieresissmall",
        "Yacutesmall", "Thornsmall", "Ydieresissmall", "001.000", "001.001", "001.002", "001.003", "Black", "Bold", "Book", "Light",
        "Medium", "Regular", "Roman", "Semibold",
    ];

    // Standard Encoding codes 161 and up with their SIDs; codes 32 to 126 map to SIDs 1 to 95.
    private static readonly (byte Code, ushort Sid)[] s_highEncoding =
    [
        (161, 96), (162, 97), (163, 98), (164, 99), (165, 100), (166, 101), (167, 102), (168, 103), (169, 104), (170, 105),
        (171, 106), (172, 107), (173, 108), (174, 109), (175, 110), (177, 111), (178, 112), (179, 113), (180, 114), (182, 115),
        (183, 116), (184, 117), (185, 118), (186, 119), (187, 120), (188, 121), (189, 122), (191, 123), (193, 124), (194, 125),
        (195, 126), (196, 127), (197, 128), (198, 129), (199, 130), (200, 131), (202, 132), (203, 133), (205, 134), (206, 135),
        (207, 136), (208, 137), (225, 138), (227, 139), (232, 140), (233, 141), (234, 142), (235, 143), (241, 144), (245, 145),
        (248, 146), (249, 147), (250, 148), (251, 149),
    ];

    // Expert Encoding (appendix B) as (code, SID) runs: each run gives consecutive codes consecutive SIDs. Code 175 is
    // Macronsmall (SID 313), as pdf.js has it; FreeType's table repeats Dotaccentsmall (312) there, which leaves Macronsmall
    // without a code.
    private static readonly (byte Code, ushort Sid, byte Length)[] s_expertEncoding =
    [
        (32, 1, 1), (33, 229, 2), (36, 231, 8), (44, 13, 3), (47, 99, 1), (48, 239, 10), (58, 27, 2), (60, 249, 4), (65, 253, 5),
        (73, 258, 1), (76, 259, 4), (82, 263, 3), (86, 266, 1), (87, 109, 2), (89, 267, 3), (93, 270, 34), (161, 304, 3),
        (166, 307, 5), (172, 312, 1), (175, 313, 1), (178, 314, 2), (182, 316, 3), (188, 158, 1), (189, 155, 1), (190, 163, 1),
        (191, 319, 7), (200, 326, 1), (201, 150, 1), (202, 164, 1), (203, 169, 1), (204, 327, 52),
    ];

    // The Expert and ExpertSubset charsets (appendix C) as (first SID, count) runs from glyph 1.
    private static readonly (ushort Sid, byte Count)[] s_expertCharset =
    [
        (1, 1), (229, 10), (13, 3), (99, 1), (239, 10), (27, 2), (249, 18), (109, 2), (267, 37), (304, 15), (158, 1), (155, 1),
        (163, 1), (319, 7), (326, 1), (150, 1), (164, 1), (169, 1), (327, 52),
    ];

    private static readonly (ushort Sid, byte Count)[] s_expertSubsetCharset =
    [
        (1, 1), (231, 2), (235, 4), (13, 3), (99, 1), (239, 10), (27, 2), (249, 3), (253, 14), (109, 2), (267, 4), (272, 1),
        (300, 3), (305, 1), (314, 2), (158, 1), (155, 1), (163, 1), (320, 6), (326, 1), (150, 1), (164, 1), (169, 1), (327, 20),
    ];

    private static string[]? s_standardEncodingNames;
    private static string[]? s_expertEncodingNames;
    private static Dictionary<string, int>? s_sids;

    internal static int StringTableLength => s_strings.Length;

    /// <summary>Gets the SID the Expert Encoding gives a character code, or 0 (.notdef).</summary>
    public static int ExpertEncodingSid(int code)
    {
        foreach ((byte first, ushort sid, byte length) in s_expertEncoding)
        {
            if (code >= first && code < first + length)
            {
                return sid + code - first;
            }
        }

        return 0;
    }

    /// <summary>Gets the SIDs of a predefined charset: 1 Expert, 2 ExpertSubset; glyph 0 is .notdef.</summary>
    public static ushort[] PredefinedCharset(int which)
    {
        var sids = new List<ushort> { 0 };
        foreach ((ushort first, byte count) in which == 1 ? s_expertCharset : s_expertSubsetCharset)
        {
            for (int k = 0; k < count; k++)
            {
                sids.Add((ushort)(first + k));
            }
        }

        return [.. sids];
    }

    /// <summary>Gets the 256 glyph names of the Standard Encoding.</summary>
    public static string[] StandardEncodingNames => s_standardEncodingNames ??= EncodingNames(StandardEncodingSid);

    /// <summary>Gets the 256 glyph names of the Expert Encoding.</summary>
    public static string[] ExpertEncodingNames => s_expertEncodingNames ??= EncodingNames(ExpertEncodingSid);

    /// <summary>Gets the SID of a standard string, or -1.</summary>
    public static int SidOf(string name)
    {
        Dictionary<string, int> map = Volatile.Read(ref s_sids) ?? BuildSids();
        return map.TryGetValue(name, out int sid) ? sid : -1;
    }

    private static Dictionary<string, int> BuildSids()
    {
        var map = new Dictionary<string, int>(s_strings.Length, StringComparer.Ordinal);
        for (int sid = s_strings.Length - 1; sid >= 0; sid--)
        {
            map[s_strings[sid]] = sid;
        }

        Volatile.Write(ref s_sids, map);
        return map;
    }

    private static string[] EncodingNames(Func<int, int> sidOf)
    {
        var names = new string[256];
        for (int code = 0; code < 256; code++)
        {
            names[code] = s_strings[sidOf(code)];
        }

        return names;
    }

    public static string GetStandardString(int sid) => (uint)sid < (uint)s_strings.Length ? s_strings[sid] : string.Empty;

    /// <summary>Gets the SID the Standard Encoding gives a character code, or 0 (.notdef).</summary>
    public static int StandardEncodingSid(int code)
    {
        if (code is >= 32 and <= 126)
        {
            return code - 31;
        }

        foreach ((byte c, ushort sid) in s_highEncoding)
        {
            if (c == code)
            {
                return sid;
            }
        }

        return 0;
    }
}
