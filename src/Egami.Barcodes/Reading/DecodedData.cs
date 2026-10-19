using System.Text;

using Egami.Barcodes.Gs1Data;

namespace Egami.Barcodes.Reading;

/// <summary>What a symbol's codewords decoded to, before it is placed in the image.</summary>
internal sealed record class DecodedData(BarcodeFormat Format, string Text, byte[] Bytes)
{
    public int? Eci { get; init; }

    public IReadOnlyList<Gs1ElementString>? Gs1 { get; init; }

    public int ErrorsCorrected { get; init; }

    public bool Mirrored { get; init; }
}

/// <summary>Collects decoded bytes in segments with their character sets and turns them into text.</summary>
internal sealed class TextBuilder
{
    private readonly List<byte> _bytes = [];
    private readonly StringBuilder _text = new();
    private readonly List<byte> _pending = [];
    private Encoding? _encoding;

    /// <summary>Gets the first ECI named, if any.</summary>
    public int? Eci { get; private set; }

    /// <summary>Gets or sets a value indicating whether the data is GS1 (FNC1 first).</summary>
    public bool Gs1 { get; set; }

    /// <summary>Gets or sets the encoding to guess for bytes without an ECI: null for ISO 8859-1 unless they are clearly UTF-8.</summary>
    public Encoding? Fallback { get; set; }

    public byte[] Bytes => [.. _bytes];

    public void Append(byte b)
    {
        _bytes.Add(b);
        _pending.Add(b);
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            Append(b);
        }
    }

    /// <summary>Appends text that is the same in every character set: digits, alphanumerics, Kanji converted already.</summary>
    public void AppendText(string text, Encoding? encoding = null)
    {
        Flush();
        _text.Append(text);
        foreach (byte b in (encoding ?? Encoding.Latin1).GetBytes(text))
        {
            _bytes.Add(b);
        }
    }

    /// <summary>Switches the character set for the bytes that follow, by ECI number.</summary>
    public void SetEci(int eci)
    {
        Flush();
        Eci ??= eci;
        _encoding = EncodingFor(eci);
    }

    public string Text()
    {
        Flush();
        return _text.ToString();
    }

    /// <summary>Returns the text for the result: GS1 data in its human-readable form when it parses, else the text.</summary>
    public (string Text, IReadOnlyList<Gs1ElementString>? Elements) Result()
    {
        string text = Text();
        if (Gs1 && Gs1Raw.TryParse(text, out IReadOnlyList<Gs1ElementString>? elements))
        {
            return (string.Concat(elements), elements);
        }

        return (text, null);
    }

    private void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        byte[] bytes = [.. _pending];
        _pending.Clear();
        Encoding encoding = _encoding ?? Fallback ?? (LooksLikeUtf8(bytes) ? Encoding.UTF8 : Encoding.Latin1);
        _text.Append(encoding.GetString(bytes));
    }

    /// <summary>Returns whether bytes are valid UTF-8 with at least one multi-byte character.</summary>
    internal static bool LooksLikeUtf8(ReadOnlySpan<byte> bytes)
    {
        bool multibyte = false;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            int extra = b < 0x80 ? 0 : b is >= 0xC2 and <= 0xDF ? 1 : b is >= 0xE0 and <= 0xEF ? 2 : b is >= 0xF0 and <= 0xF4 ? 3 : -1;
            if (extra < 0 || i + extra >= bytes.Length + (extra == 0 ? 1 : 0))
            {
                return false;
            }

            for (int k = 1; k <= extra; k++)
            {
                if ((bytes[i + k] & 0xC0) != 0x80)
                {
                    return false;
                }
            }

            multibyte |= extra > 0;
            i += extra;
        }

        return multibyte;
    }

    /// <summary>The character set an ECI names (ISO 8859 parts as code pages 28591 on, Shift JIS, UTF-8 and the common ones), or ISO 8859-1.</summary>
    internal static Encoding EncodingFor(int eci)
    {
        try
        {
            return eci switch
            {
                26 => Encoding.UTF8,
                27 or 170 => Encoding.ASCII,
                25 => Encoding.BigEndianUnicode,
                33 => Encoding.Unicode,
                34 => Encoding.UTF32,
                >= 4 and <= 18 and not 14 => CodePagesEncodingProvider.Instance.GetEncoding(28588 + eci) ?? Encoding.Latin1,
                20 => CodePagesEncodingProvider.Instance.GetEncoding(932) ?? Encoding.Latin1,
                21 => CodePagesEncodingProvider.Instance.GetEncoding(1250) ?? Encoding.Latin1,
                22 => CodePagesEncodingProvider.Instance.GetEncoding(1251) ?? Encoding.Latin1,
                23 => CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1,
                24 => CodePagesEncodingProvider.Instance.GetEncoding(1256) ?? Encoding.Latin1,
                28 => CodePagesEncodingProvider.Instance.GetEncoding(950) ?? Encoding.Latin1,
                29 => CodePagesEncodingProvider.Instance.GetEncoding(936) ?? Encoding.Latin1,
                30 => CodePagesEncodingProvider.Instance.GetEncoding(949) ?? Encoding.Latin1,
                _ => Encoding.Latin1,
            };
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            return Encoding.Latin1;
        }
    }
}

/// <summary>Parses GS1 data as a symbol holds it: AIs and values, a GS (29) after each variable-length field.</summary>
internal static class Gs1Raw
{
    public static bool TryParse(string raw, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<Gs1ElementString>? elements)
    {
        elements = null;
        var list = new List<Gs1ElementString>();
        int at = 0;
        while (at < raw.Length)
        {
            if (raw[at] == '\x1D')
            {
                at++;
                continue;
            }

            // The longest AI that GS1 defines at this point, 2 to 4 digits.
            Gs1AiSpec? spec = null;
            string ai = "";
            for (int length = 2; length <= 4 && at + length <= raw.Length; length++)
            {
                string candidate = raw.Substring(at, length);
                if (Gs1Dictionary.Find(candidate) is Gs1AiSpec found)
                {
                    (spec, ai) = (found, candidate);
                    break;
                }
            }

            if (spec is null)
            {
                return false;
            }

            at += ai.Length;
            int end = raw.IndexOf('\x1D', at);
            if (spec.PredefinedLength)
            {
                int length = spec.Components.Sum(c => c.Length);
                end = Math.Min(raw.Length, at + length);
            }
            else if (end < 0)
            {
                end = raw.Length;
            }

            if (!Gs1ElementString.TryCreate(ai, raw[at..end], out Gs1ElementString? element, out _))
            {
                return false;
            }

            list.Add(element);
            at = end;
        }

        elements = list;
        return list.Count > 0;
    }
}
