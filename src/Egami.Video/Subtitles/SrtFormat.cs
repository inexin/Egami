using System.Globalization;
using System.Text;

using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>
/// SubRip (.srt): blocks of a number, a timing line (<c>00:00:01,000 --&gt; 00:00:02,500</c>) and text lines, separated by blank
/// lines. The text takes <c>b</c>, <c>i</c>, <c>u</c>, <c>s</c> and <c>font</c> tags, and players also honor a leading
/// <c>{\anN}</c>. There is no spec; the reader repairs what real files get wrong and reports each fix.
/// </summary>
internal static class SrtFormat
{
    public static bool LooksLikeSrt(string text)
    {
        List<TextLine> lines = SubtitleText.Lines(text.Length > 4096 ? text[..4096] : text);
        int i = 0;
        while (i < lines.Count && SubtitleText.IsBlank(lines[i].Of(text)))
        {
            i++;
        }

        return i < lines.Count && (TryTiming(lines[i].Of(text), out _, out _, out _) || (IsNumber(lines[i].Of(text)) && i + 1 < lines.Count && TryTiming(lines[i + 1].Of(text), out _, out _, out _)));
    }

    public static SubtitleDocument Read(string text, SubtitleLog log)
    {
        SubtitleDocument document = new(SubtitleFormat.Srt);
        List<TextLine> lines = SubtitleText.Lines(text);
        int leadingStart = 0;
        string header = "";
        int i = 0;
        while (i < lines.Count)
        {
            ReadOnlySpan<char> line = lines[i].Of(text);
            if (SubtitleText.IsBlank(line))
            {
                i++;
                continue;
            }

            int timing;
            if (IsNumber(line) && i + 1 < lines.Count && TryTiming(lines[i + 1].Of(text), out _, out _, out _))
            {
                timing = i + 1;
            }
            else if (TryTiming(line, out _, out _, out _))
            {
                timing = i;
                log.Repaired(i + 1, "the cue has no number.");
            }
            else
            {
                log.Repaired(i + 1, "a line that is not part of a cue was skipped.");
                i++;
                continue;
            }

            TryTiming(lines[timing].Of(text), out MediaTime start, out MediaTime end, out bool dot);
            if (dot)
            {
                log.Repaired(timing + 1, "the timing uses '.' where SRT has ','.");
            }

            int end2 = timing + 1;
            while (end2 < lines.Count && !SubtitleText.IsBlank(lines[end2].Of(text)))
            {
                if (TryTiming(lines[end2].Of(text), out _, out _, out _))
                {
                    // The next cue starts without a blank line: its number, if any, is the line before its timing.
                    if (end2 - 1 > timing && IsNumber(lines[end2 - 1].Of(text)))
                    {
                        end2--;
                    }

                    log.Repaired(end2 + 1, "a blank line is missing before the next cue.");
                    break;
                }

                end2++;
            }

            int blockStart = lines[i].Start;
            int last = end2 - 1;
            string rawText = end2 > timing + 1 ? text[lines[timing + 1].Start..(lines[last].Start + lines[last].Length)] : "";
            string block = text[blockStart..lines[last].Next];
            string leading = text[leadingStart..blockStart];
            if (document.Cues.Count == 0)
            {
                (header, leading) = (leading, "");
            }

            SubtitleCue cue = new(start, end, ParseText(rawText, out SubtitleAlignment? alignment)) { Alignment = alignment };
            cue.Id = timing > i ? line.Trim().ToString() : null;
            cue.MarkRead(SubtitleFormat.Srt, rawText, block, leading, document.Cues.Count);
            document.Cues.Add(cue);
            leadingStart = lines[last].Next;
            i = end2;
        }

        if (document.Cues.Count == 0)
        {
            header = text;
            leadingStart = text.Length;
        }

        document.MarkRead(SubtitleFormat.Srt, header, text[leadingStart..]);
        return document;
    }

    public static string Write(SubtitleDocument document, string newLine, SubtitleLog log)
    {
        StringBuilder output = new();
        bool raw = document.HeaderUnchanged(SubtitleFormat.Srt);
        if (raw)
        {
            output.Append(document.Source!.Header);
        }

        CheckHeader(document, log);
        for (int index = 0; index < document.Cues.Count; index++)
        {
            SubtitleCue cue = document.Cues[index];
            string number = (index + 1).ToString(CultureInfo.InvariantCulture);
            CueSource? source = cue.Source is { Format: SubtitleFormat.Srt } s ? s : null;
            if (source is not null && output.Length > 0)
            {
                SubtitleWriting.EnsureNewLine(output, newLine);
                output.Append(source.Leading);
            }

            if (source is not null && cue.IsUnchanged(SubtitleFormat.Srt) && (cue.Id == number || cue.Id is null || source.Index == index))
            {
                output.Append(source.RawBlock);
                continue;
            }

            SubtitleWriting.EnsureBlankLine(output, newLine);
            output.Append(number).Append(newLine);
            output.Append(SubtitleText.FormatTime(cue.Start, SubtitleText.Milliseconds, ',', 2)).Append(" --> ")
                .Append(SubtitleText.FormatTime(cue.End, SubtitleText.Milliseconds, ',', 2)).Append(newLine);
            SubtitleWriting.CheckTimes(cue, index, log);
            string body = cue.TextUnchanged(SubtitleFormat.Srt) ? cue.RawText! : WriteText(document, cue, index, log);
            if (body.Length > 0)
            {
                output.Append(body.ReplaceLineEndings(newLine)).Append(newLine);
            }
        }

        if (document.Source is { Format: SubtitleFormat.Srt })
        {
            output.Append(document.Source!.Trailing);
        }
        else if (document.Cues.Count > 0)
        {
            output.Append(newLine);
        }

        return output.ToString();
    }

    private static void CheckHeader(SubtitleDocument document, SubtitleLog log)
    {
        if (document.Regions.Count > 0)
        {
            log.Dropped("WebVTT regions", 0);
        }

        if (document.StyleSheets.Count > 0)
        {
            log.Dropped("WebVTT style sheets", 0);
        }

        if (document.Attachments.Count > 0)
        {
            log.Dropped("embedded fonts", 0);
        }
    }

    /// <summary>Reads digits only, ignoring surrounding white space: an SRT cue number.</summary>
    private static bool IsNumber(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> t = line.Trim();
        return t.Length is > 0 and < 10 && !t.ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>Reads <c>start --&gt; end</c>, leniently: 1 or more digits per field, hours optional, ',' or '.' before the fraction.</summary>
    internal static bool TryTiming(ReadOnlySpan<char> line, out MediaTime start, out MediaTime end, out bool dot)
    {
        start = end = default;
        dot = false;
        int at = 0;
        Skip(line, ref at);
        if (!TryTime(line, ref at, out start, ref dot))
        {
            return false;
        }

        Skip(line, ref at);
        if (!line[at..].StartsWith("-->", StringComparison.Ordinal))
        {
            return false;
        }

        at += 3;
        Skip(line, ref at);
        return TryTime(line, ref at, out end, ref dot) && (at == line.Length || line[at] is ' ' or '\t');
    }

    private static void Skip(ReadOnlySpan<char> s, ref int at)
    {
        while (at < s.Length && s[at] is ' ' or '\t')
        {
            at++;
        }
    }

    private static bool TryTime(ReadOnlySpan<char> s, ref int at, out MediaTime time, ref bool dot)
    {
        time = default;
        Span<long> parts = stackalloc long[3];
        int count = 0;
        bool negative = at < s.Length && s[at] == '-';
        if (negative)
        {
            at++;
        }

        while (true)
        {
            if (SubtitleText.Digits(s, ref at, out long value) == 0)
            {
                return false;
            }

            parts[count++] = value;
            if (count < 3 && at < s.Length && s[at] == ':')
            {
                at++;
                continue;
            }

            break;
        }

        if (count < 2)
        {
            return false;
        }

        long ms = 0;
        if (at < s.Length && s[at] is ',' or '.')
        {
            dot |= s[at] == '.';
            at++;
            int start = at;
            int digits = SubtitleText.Digits(s, ref at, out long fraction);
            if (digits == 0)
            {
                return false;
            }

            // Two digits are hundredths and one is tenths, as players read them; past three, round to the millisecond.
            ms = digits switch
            {
                1 => fraction * 100,
                2 => fraction * 10,
                3 => fraction,
                _ => (long)Math.Round(double.Parse(s[start..at], CultureInfo.InvariantCulture) / Math.Pow(10, digits - 3)),
            };
        }

        long hours = count == 3 ? parts[0] : 0, minutes = parts[count - 2], seconds = parts[count - 1];
        long total = checked((((((hours * 60) + minutes) * 60) + seconds) * 1000) + ms);
        time = new MediaTime(negative ? -total : total, SubtitleText.Milliseconds);
        return true;
    }

    /// <summary>Reads SRT text with its tags into spans; a leading <c>{\anN}</c> becomes the alignment.</summary>
    internal static List<SubtitleSpan> ParseText(string raw, out SubtitleAlignment? alignment)
    {
        alignment = null;
        List<SubtitleSpan> spans = [];
        StringBuilder text = new();
        int bold = 0, italic = 0, underline = 0, strike = 0;
        List<(string? Face, Rgba8? Color, double? Size)> fonts = [];
        string? otherTags = null;

        void Flush()
        {
            if (text.Length == 0 && otherTags is null)
            {
                return;
            }

            (string? face, Rgba8? color, double? size) = (null, null, null);
            foreach ((string? f, Rgba8? c, double? z) in fonts)
            {
                (face, color, size) = (f ?? face, c ?? color, z ?? size);
            }

            SubtitleWriting.Append(spans, new SubtitleSpan(text.ToString())
            {
                Bold = bold > 0,
                Italic = italic > 0,
                Underline = underline > 0,
                Strikeout = strike > 0,
                Font = face,
                Color = color,
                FontSize = size,
                OtherTags = otherTags,
            });
            text.Clear();
            otherTags = null;
        }

        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '<' && raw.IndexOf('>', i) is int close and > 0 && TryTag(raw.AsSpan(i + 1, close - i - 1), out string name, out bool closing, out string attributes))
            {
                Flush();
                int delta = closing ? -1 : 1;
                switch (name)
                {
                    case "b":
                        bold = Math.Max(0, bold + delta);
                        break;
                    case "i":
                        italic = Math.Max(0, italic + delta);
                        break;
                    case "u":
                        underline = Math.Max(0, underline + delta);
                        break;
                    case "s":
                        strike = Math.Max(0, strike + delta);
                        break;
                    default:
                        if (closing)
                        {
                            if (fonts.Count > 0)
                            {
                                fonts.RemoveAt(fonts.Count - 1);
                            }
                        }
                        else
                        {
                            fonts.Add(FontAttributes(attributes));
                        }

                        break;
                }

                i = close + 1;
                continue;
            }

            if (c == '{' && i + 1 < raw.Length && raw[i + 1] == '\\' && raw.IndexOf('}', i) is int end and > 0)
            {
                ReadOnlySpan<char> tags = raw.AsSpan(i + 1, end - i - 1);
                if (alignment is null && tags.Length == 4 && tags.StartsWith("\\an") && tags[3] is >= '1' and <= '9')
                {
                    alignment = (SubtitleAlignment)(tags[3] - '0');
                }
                else
                {
                    Flush();
                    otherTags = (otherTags ?? "") + tags.ToString();
                }

                i = end + 1;
                continue;
            }

            if (c is '\r' or '\n')
            {
                text.Append('\n');
                i += c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            text.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    private static bool TryTag(ReadOnlySpan<char> inner, out string name, out bool closing, out string attributes)
    {
        closing = inner.StartsWith('/');
        ReadOnlySpan<char> rest = (closing ? inner[1..] : inner).TrimStart();
        int length = 0;
        while (length < rest.Length && char.IsAsciiLetter(rest[length]))
        {
            length++;
        }

        name = rest[..length].ToString().ToLowerInvariant();
        attributes = rest[length..].ToString();
        bool known = name is "b" or "i" or "u" or "s" or "font";
        return known && (length == rest.Length || char.IsWhiteSpace(rest[length]) || (closing && rest[length..].IsWhiteSpace()));
    }

    private static (string? Face, Rgba8? Color, double? Size) FontAttributes(string attributes)
    {
        string? face = null;
        Rgba8? color = null;
        double? size = null;
        int i = 0;
        while (i < attributes.Length)
        {
            while (i < attributes.Length && char.IsWhiteSpace(attributes[i]))
            {
                i++;
            }

            int nameStart = i;
            while (i < attributes.Length && attributes[i] != '=' && !char.IsWhiteSpace(attributes[i]))
            {
                i++;
            }

            string name = attributes[nameStart..i].ToLowerInvariant();
            if (i >= attributes.Length || attributes[i] != '=')
            {
                i++;
                continue;
            }

            i++;
            string value;
            if (i < attributes.Length && attributes[i] is '"' or '\'')
            {
                char quote = attributes[i];
                int close = attributes.IndexOf(quote, i + 1);
                close = close < 0 ? attributes.Length : close;
                value = attributes[(i + 1)..close];
                i = close + 1;
            }
            else
            {
                int valueStart = i;
                while (i < attributes.Length && !char.IsWhiteSpace(attributes[i]))
                {
                    i++;
                }

                value = attributes[valueStart..i];
            }

            switch (name)
            {
                case "color":
                    color = SubtitleWriting.ParseCssColor(value);
                    break;
                case "face":
                    face = value;
                    break;
                case "size" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z):
                    size = z;
                    break;
            }
        }

        return (face, color, size);
    }

    /// <summary>Writes spans as SRT text, with tags nested in one order (font, b, i, u, s) and a leading <c>{\anN}</c>.</summary>
    internal static string WriteText(SubtitleDocument document, SubtitleCue cue, int index, SubtitleLog log)
    {
        StringBuilder output = new();
        SubtitleAlignment? alignment = SubtitleWriting.EffectiveAlignment(cue);
        if (alignment is { } a && a != SubtitleAlignment.BottomCenter)
        {
            output.Append("{\\an").Append((int)a).Append('}');
        }

        SubtitleWriting.CheckCue(document, cue, index, log, SubtitleFormat.Srt);
        List<string> open = [];
        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.IsDrawing)
            {
                continue;
            }

            string? font = FontTag(span);
            List<string> wanted = [];
            if (font is not null)
            {
                wanted.Add(font);
            }

            if (span.Bold)
            {
                wanted.Add("b");
            }

            if (span.Italic)
            {
                wanted.Add("i");
            }

            if (span.Underline)
            {
                wanted.Add("u");
            }

            if (span.Strikeout)
            {
                wanted.Add("s");
            }

            int keep = 0;
            while (keep < open.Count && keep < wanted.Count && open[keep] == wanted[keep])
            {
                keep++;
            }

            for (int k = open.Count - 1; k >= keep; k--)
            {
                output.Append("</").Append(open[k].StartsWith("font", StringComparison.Ordinal) ? "font" : open[k]).Append('>');
            }

            open.RemoveRange(keep, open.Count - keep);
            for (int k = keep; k < wanted.Count; k++)
            {
                output.Append('<').Append(wanted[k]).Append('>');
                open.Add(wanted[k]);
            }

            output.Append(span.Text);
        }

        for (int k = open.Count - 1; k >= 0; k--)
        {
            output.Append("</").Append(open[k].StartsWith("font", StringComparison.Ordinal) ? "font" : open[k]).Append('>');
        }

        return output.ToString();
    }

    private static string? FontTag(SubtitleSpan span)
    {
        if (span.Color is null && span.Font is null && span.FontSize is null)
        {
            return null;
        }

        StringBuilder tag = new("font");
        if (span.Font is { } face)
        {
            tag.Append(" face=\"").Append(face).Append('"');
        }

        if (span.FontSize is { } size)
        {
            tag.Append(" size=\"").Append(SubtitleText.Number(size)).Append('"');
        }

        if (span.Color is { } color)
        {
            tag.Append(CultureInfo.InvariantCulture, $" color=\"#{color.R:x2}{color.G:x2}{color.B:x2}\"");
        }

        return tag.ToString();
    }
}
