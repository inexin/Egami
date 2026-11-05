using System.Globalization;
using System.Text;

namespace Egami.Formats.Extended.Fits;

/// <summary>
/// Reads FITS headers, written from the FITS Standard 4.0: 80-character cards in 2880-byte blocks up to the <c>END</c> card, with
/// fixed and free-format values, quoted strings with doubled quotes, <c>CONTINUE</c> long strings and <c>HIERARCH</c> keywords.
/// </summary>
internal static class FitsHeaderReader
{
    internal const int Block = 2880;
    internal const int CardLength = 80;

    /// <summary>The most header blocks one HDU may have, 28.8 MB of cards.</summary>
    internal const int MaxBlocks = 10000;

    /// <summary>Returns whether the data starts as a FITS primary header: <c>SIMPLE  =</c> and the value <c>T</c>.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 11 || !header.StartsWith("SIMPLE  ="u8))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = header[9..].TrimStart((byte)' ');
        return rest.Length > 0 && rest[0] == (byte)'T';
    }

    /// <summary>
    /// Reads one HDU's header from <paramref name="input"/>, which stands at a block boundary, and returns the HDU; the input is
    /// left at the start of its data. Returns <see langword="null"/> when the input ends before the first card, or when an
    /// extension's first card is not <c>XTENSION</c>, which marks what follows the last HDU as not FITS.
    /// </summary>
    internal static FitsHdu? Read(EgamiInput input, int index)
    {
        long start = input.Position;
        byte[] block = new byte[Block];
        List<FitsCard> cards = [];
        for (int b = 0; ; b++)
        {
            int got = ReadBlock(input, block);
            if (got < Block)
            {
                if (b == 0 && index > 0)
                {
                    return null;
                }

                throw input.Corrupt(input.Position, "The FITS header ends before its END card.");
            }

            if (b == 0)
            {
                ReadOnlySpan<byte> first = block.AsSpan(0, 8);
                if (index == 0 ? !Matches(block) : !first.SequenceEqual("XTENSION"u8))
                {
                    if (index > 0)
                    {
                        return null;
                    }

                    throw input.Corrupt(start, "The FITS signature is wrong.");
                }
            }

            for (int c = 0; c < Block; c += CardLength)
            {
                ReadOnlySpan<byte> card = block.AsSpan(c, CardLength);
                if (card.StartsWith("END"u8) && card[3..].IndexOfAnyExcept((byte)' ') < 0)
                {
                    FitsHdu hdu = FitsHdu.Create(index, new FitsHeader(cards), start, input.Position, input);
                    return hdu;
                }

                Add(cards, card);
            }

            if (b + 1 >= MaxBlocks)
            {
                throw input.Corrupt(start, $"The FITS header has more than {MaxBlocks} blocks without an END card.");
            }

            input.CheckMemory((long)(b + 2) * Block * 4);
        }
    }

    /// <summary>Reads a whole block, or as much as is left.</summary>
    private static int ReadBlock(EgamiInput input, byte[] block)
    {
        int got = 0;
        while (got < block.Length)
        {
            int read = input.Read(block.AsSpan(got));
            if (read == 0)
            {
                break;
            }

            got += read;
        }

        return got;
    }

    /// <summary>Parses a card and adds it, joining a <c>CONTINUE</c> card to the long string before it.</summary>
    internal static void Add(List<FitsCard> cards, ReadOnlySpan<byte> card)
    {
        string text = Encoding.Latin1.GetString(card);
        string keyword = text[..8].TrimEnd();
        if (keyword == "CONTINUE" && cards.Count > 0 && cards[^1].Value is string previous && previous.EndsWith('&'))
        {
            (object? more, string? comment) = ParseValue(text[8..]);
            if (more is string next)
            {
                FitsCard last = cards[^1];
                string? joined = last.Comment is null ? comment : comment is null ? last.Comment : last.Comment + " " + comment;
                cards[^1] = last with { Value = previous[..^1] + next, Comment = joined };
                return;
            }
        }

        if (keyword == "HIERARCH")
        {
            int equals = text.IndexOf('=', 9);
            if (equals > 0)
            {
                (object? value, string? comment) = ParseValue(text[(equals + 1)..]);
                cards.Add(new FitsCard(text[9..equals].Trim(), value, comment));
                return;
            }
        }

        if (text.Length >= 10 && text[8] == '=' && text[9] == ' ')
        {
            (object? value, string? comment) = ParseValue(text[10..]);
            cards.Add(new FitsCard(keyword, value, comment));
            return;
        }

        // Commentary cards (COMMENT, HISTORY, a blank keyword) and any other card without a value indicator.
        string rest = text[8..].Trim();
        if (keyword.Length == 0 && rest.Length == 0)
        {
            return;
        }

        cards.Add(new FitsCard(keyword, null, rest.Length == 0 ? null : rest));
    }

    /// <summary>Parses the value and comment that follow a value indicator.</summary>
    internal static (object? Value, string? Comment) ParseValue(string field)
    {
        int i = 0;
        while (i < field.Length && field[i] == ' ')
        {
            i++;
        }

        if (i == field.Length)
        {
            return (null, null);
        }

        object? value;
        int after;
        if (field[i] == '\'')
        {
            StringBuilder s = new();
            int j = i + 1;
            while (j < field.Length)
            {
                if (field[j] == '\'')
                {
                    if (j + 1 < field.Length && field[j + 1] == '\'')
                    {
                        s.Append('\'');
                        j += 2;
                        continue;
                    }

                    break;
                }

                s.Append(field[j]);
                j++;
            }

            // Trailing spaces in a string are not significant; leading ones are.
            value = s.ToString().TrimEnd();
            after = Math.Min(j + 1, field.Length);
        }
        else
        {
            int slash = field.IndexOf('/', i);
            string token = (slash < 0 ? field[i..] : field[i..slash]).Trim();
            after = slash < 0 ? field.Length : slash;
            value = Typed(token);
        }

        string? comment = null;
        int at = field.IndexOf('/', after);
        if (at >= 0)
        {
            comment = field[(at + 1)..].Trim();
            if (comment.Length == 0)
            {
                comment = null;
            }
        }

        return (value, comment);
    }

    private static object? Typed(string token)
    {
        if (token.Length == 0)
        {
            return null;
        }

        if (token == "T")
        {
            return true;
        }

        if (token == "F")
        {
            return false;
        }

        if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
        {
            return l;
        }

        string real = token.Replace('D', 'E').Replace('d', 'e');
        if (double.TryParse(real, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        // A complex number or anything else is kept as its text.
        return token;
    }
}
