using System.Text;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// Decodes text values by a dataset's Specific Character Set (0008,0005), as PS3.3 C.12.1.1.2 and PS3.5 section 6.1 define it:
/// one single-byte or multi-byte encoding, or ISO 2022 code extension, where escape sequences switch the G0 and G1 sets within a
/// value and every delimiter resets them. Unknown terms fall back to Latin-1, which keeps every byte.
/// </summary>
internal sealed class DicomCharacterSet
{
    private const byte Escape = 0x1B;

    private static readonly Encoding Latin1 = System.Text.Encoding.Latin1;

    /// <summary>Gets the default repertoire (ISO-IR 6), read as Latin-1 so that undeclared 8-bit text is kept.</summary>
    public static DicomCharacterSet Default { get; } = new([], Latin1, null, null);

    private readonly Encoding _single;
    private readonly Code? _g0;
    private readonly Code? _g1;

    private DicomCharacterSet(string[] terms, Encoding single, Code? g0, Code? g1)
    {
        Terms = terms;
        _single = single;
        _g0 = g0;
        _g1 = g1;
    }

    /// <summary>Gets the terms as the file wrote them.</summary>
    public string[] Terms { get; }

    /// <summary>Gets whether the set uses ISO 2022 escapes.</summary>
    public bool Extended => _g0 is not null || _g1 is not null;

    /// <summary>Returns the set the values of (0008,0005) name; <paramref name="unknown"/> is the first term not understood.</summary>
    public static DicomCharacterSet From(string[] terms, out string? unknown)
    {
        unknown = null;
        if (terms.Length == 0 || (terms.Length == 1 && terms[0].Length == 0))
        {
            return Default;
        }

        string first = terms[0];
        if (terms.Length == 1 && !first.StartsWith("ISO 2022", StringComparison.Ordinal))
        {
            Encoding? encoding = Single(first);
            if (encoding is null)
            {
                unknown = first;
                encoding = Latin1;
            }

            return new DicomCharacterSet(terms, encoding, null, null);
        }

        // Code extension: the first value sets the initial G0 and G1; later values are only reached by escapes.
        foreach (string term in terms.Skip(1))
        {
            if (term.Length > 0 && Initial(term) is null)
            {
                unknown ??= term;
            }
        }

        Code? g1 = first.Length == 0 ? null : Initial(first);
        if (first.Length > 0 && g1 is null && first != "ISO 2022 IR 6")
        {
            unknown ??= first;
        }

        Code g0 = first == "ISO 2022 IR 13" ? Code.JisRoman : Code.Ascii;
        if (g1 is Code.JisRoman)
        {
            g1 = Code.JisKatakana;
        }

        return new DicomCharacterSet(terms, Latin1, g0, g1 ?? Code.None);
    }

    /// <summary>Decodes a text value.</summary>
    public string Decode(ReadOnlySpan<byte> value)
    {
        if (!Extended)
        {
            return _single.GetString(value);
        }

        StringBuilder text = new(value.Length);
        Code g0 = _g0!.Value;
        Code g1 = _g1!.Value;
        int start = 0;
        int i = 0;
        while (i < value.Length)
        {
            byte b = value[i];
            if (b == Escape && TryEscape(value[(i + 1)..], out int length, out Code code, out bool toG1))
            {
                Flush(text, value[start..i], g0, g1);
                if (toG1)
                {
                    g1 = code;
                }
                else
                {
                    g0 = code;
                }

                i += 1 + length;
                start = i;
                continue;
            }

            // Delimiters reset G0 to the initial set (PS3.5 6.1.2.5.3); in a two-byte 7-bit set they are part of a character.
            if (!IsDouble(g0) && b is 0x5C or 0x3D or 0x5E or 0x0D or 0x0A or 0x09 or 0x0C)
            {
                Flush(text, value[start..(i + 1)], g0, g1);
                g0 = _g0.Value;
                g1 = _g1.Value;
                start = i + 1;
            }
            else if (IsDouble(g0) && b < 0x80)
            {
                i++;
            }

            i++;
        }

        Flush(text, value[start..Math.Min(value.Length, i)], g0, g1);
        return text.ToString();
    }

    private static void Flush(StringBuilder text, ReadOnlySpan<byte> run, Code g0, Code g1)
    {
        int i = 0;
        while (i < run.Length)
        {
            bool high = run[i] >= 0x80;
            int end = i;
            while (end < run.Length && (run[end] >= 0x80) == high)
            {
                end++;
            }

            ReadOnlySpan<byte> part = run[i..end];
            text.Append(high ? DecodeHigh(part, g1) : DecodeLow(part, g0));
            i = end;
        }
    }

    private static string DecodeLow(ReadOnlySpan<byte> part, Code g0)
    {
        switch (g0)
        {
            case Code.JisX0208:
            case Code.JisX0212:
                // Seven-bit two-byte codes: as EUC-JP, which is the same codes with the high bits set (JIS X 0212 after 0x8F).
                byte[] euc = new byte[part.Length + (part.Length / 2)];
                int n = 0;
                for (int i = 0; i + 1 < part.Length; i += 2)
                {
                    if (g0 == Code.JisX0212)
                    {
                        euc[n++] = 0x8F;
                    }

                    euc[n++] = (byte)(part[i] | 0x80);
                    euc[n++] = (byte)(part[i + 1] | 0x80);
                }

                return CodePage(20932).GetString(euc, 0, n);
            case Code.JisRoman:
                return CodePage(932).GetString(part);
            default:
                return Latin1.GetString(part);
        }
    }

    private static string DecodeHigh(ReadOnlySpan<byte> part, Code g1)
    {
        return g1 switch
        {
            Code.JisKatakana => CodePage(932).GetString(part),
            Code.KsX1001 => CodePage(51949).GetString(part),
            Code.Gb2312 => CodePage(936).GetString(part),
            Code.Latin1 => Latin1.GetString(part),
            Code.Latin2 => CodePage(28592).GetString(part),
            Code.Latin3 => CodePage(28593).GetString(part),
            Code.Latin4 => CodePage(28594).GetString(part),
            Code.Cyrillic => CodePage(28595).GetString(part),
            Code.Arabic => CodePage(28596).GetString(part),
            Code.Greek => CodePage(28597).GetString(part),
            Code.Hebrew => CodePage(28598).GetString(part),
            Code.Latin5 => CodePage(28599).GetString(part),
            Code.Latin9 => CodePage(28605).GetString(part),
            Code.Thai => CodePage(874).GetString(part),
            _ => Latin1.GetString(part),
        };
    }

    /// <summary>Reads an escape sequence (the bytes after ESC) PS3.3 table C.12-3 and C.12-4 list.</summary>
    private static bool TryEscape(ReadOnlySpan<byte> s, out int length, out Code code, out bool toG1)
    {
        (length, code, toG1) = s switch
        {
            [0x28, 0x42, ..] => (2, Code.Ascii, false),        // ESC ( B
            [0x28, 0x4A, ..] => (2, Code.JisRoman, false),     // ESC ( J
            [0x29, 0x49, ..] => (2, Code.JisKatakana, true),   // ESC ) I
            [0x24, 0x42, ..] => (2, Code.JisX0208, false),     // ESC $ B
            [0x24, 0x28, 0x44, ..] => (3, Code.JisX0212, false), // ESC $ ( D
            [0x24, 0x29, 0x43, ..] => (3, Code.KsX1001, true), // ESC $ ) C
            [0x24, 0x29, 0x41, ..] => (3, Code.Gb2312, true),  // ESC $ ) A
            [0x2D, 0x41, ..] => (2, Code.Latin1, true),        // ESC - A
            [0x2D, 0x42, ..] => (2, Code.Latin2, true),
            [0x2D, 0x43, ..] => (2, Code.Latin3, true),
            [0x2D, 0x44, ..] => (2, Code.Latin4, true),
            [0x2D, 0x4C, ..] => (2, Code.Cyrillic, true),
            [0x2D, 0x47, ..] => (2, Code.Arabic, true),
            [0x2D, 0x46, ..] => (2, Code.Greek, true),
            [0x2D, 0x48, ..] => (2, Code.Hebrew, true),
            [0x2D, 0x4D, ..] => (2, Code.Latin5, true),
            [0x2D, 0x62, ..] => (2, Code.Latin9, true),
            [0x2D, 0x54, ..] => (2, Code.Thai, true),
            _ => (0, Code.None, false),
        };
        return length > 0;
    }

    /// <summary>Returns the set an ISO 2022 term puts in G1 (or JIS X 0201 for IR 13), or null.</summary>
    private static Code? Initial(string term)
    {
        return term switch
        {
            "ISO 2022 IR 6" => Code.None,
            "ISO 2022 IR 13" => Code.JisRoman,
            "ISO 2022 IR 87" => Code.None,
            "ISO 2022 IR 159" => Code.None,
            "ISO 2022 IR 149" => Code.KsX1001,
            "ISO 2022 IR 58" => Code.Gb2312,
            "ISO 2022 IR 100" => Code.Latin1,
            "ISO 2022 IR 101" => Code.Latin2,
            "ISO 2022 IR 109" => Code.Latin3,
            "ISO 2022 IR 110" => Code.Latin4,
            "ISO 2022 IR 144" => Code.Cyrillic,
            "ISO 2022 IR 127" => Code.Arabic,
            "ISO 2022 IR 126" => Code.Greek,
            "ISO 2022 IR 138" => Code.Hebrew,
            "ISO 2022 IR 148" => Code.Latin5,
            "ISO 2022 IR 203" => Code.Latin9,
            "ISO 2022 IR 166" => Code.Thai,
            _ => null,
        };
    }

    /// <summary>Returns the encoding of a term without code extension, or null.</summary>
    private static Encoding? Single(string term)
    {
        return term switch
        {
            "ISO_IR 6" => Latin1,
            "ISO_IR 100" => Latin1,
            "ISO_IR 101" => CodePage(28592),
            "ISO_IR 109" => CodePage(28593),
            "ISO_IR 110" => CodePage(28594),
            "ISO_IR 144" => CodePage(28595),
            "ISO_IR 127" => CodePage(28596),
            "ISO_IR 126" => CodePage(28597),
            "ISO_IR 138" => CodePage(28598),
            "ISO_IR 148" => CodePage(28599),
            "ISO_IR 203" => CodePage(28605),
            "ISO_IR 13" => CodePage(932),
            "ISO_IR 166" => CodePage(874),
            "ISO_IR 192" => System.Text.Encoding.UTF8,
            "GB18030" => CodePage(54936),
            "GBK" => CodePage(936),
            _ => null,
        };
    }

    private static bool IsDouble(Code code) => code is Code.JisX0208 or Code.JisX0212;

    private static Encoding CodePage(int codePage)
    {
        return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? System.Text.Encoding.GetEncoding(codePage);
    }

    private enum Code
    {
        None,
        Ascii,
        JisRoman,
        JisKatakana,
        JisX0208,
        JisX0212,
        KsX1001,
        Gb2312,
        Latin1,
        Latin2,
        Latin3,
        Latin4,
        Cyrillic,
        Arabic,
        Greek,
        Hebrew,
        Latin5,
        Latin9,
        Thai,
    }
}
