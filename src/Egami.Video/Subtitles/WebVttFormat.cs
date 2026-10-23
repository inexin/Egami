using System.Globalization;
using System.Text;

namespace Egami.Video.Subtitles;

/// <summary>
/// WebVTT, read with the W3C WebVTT file parsing algorithm (https://www.w3.org/TR/webvtt1/#file-parsing) so a file gives the
/// same cues as in a browser, and written in its canonical form.
/// </summary>
internal static class WebVttFormat
{
    public static SubtitleDocument Read(string text, SubtitleLog log)
    {
        SubtitleDocument document = new(SubtitleFormat.WebVtt);
        List<TextLine> lines = SubtitleText.Lines(text);
        string[] content = [.. lines.Select(l => l.Of(text).ToString().Replace('\0', '\uFFFD'))];
        string first = content.Length > 0 ? content[0] : "";
        if (!first.StartsWith("WEBVTT", StringComparison.Ordinal) || (first.Length > 6 && first[6] is not (' ' or '\t')))
        {
            throw log.Corrupt(1, "The file does not start with the WEBVTT signature.");
        }

        Parser parser = new(content, document);
        StringBuilder header = new(first.Length > 6 ? first[7..] : "");
        int i = 1;
        if (i < content.Length && content[i].Length > 0)
        {
            int start = i;
            i = parser.CollectBlock(i, inHeader: true, out _);
            for (int k = start; k < i && content[k].Length > 0; k++)
            {
                header.Append('\n').Append(content[k]);
            }
        }
        else if (i < content.Length)
        {
            i++;
        }

        document.HeaderText = header.Length > 0 ? header.ToString() : null;
        while (i < content.Length && content[i].Length == 0)
        {
            i++;
        }

        int leadingStart = 0;
        string headerRaw = "";
        while (i < content.Length)
        {
            int start = i;
            i = parser.CollectBlock(i, inHeader: false, out int last);
            if (parser.Cue is { } cue)
            {
                int blockStart = lines[start].Start;
                string block = text[blockStart..lines[last].Next];
                string leading = text[leadingStart..blockStart];
                if (document.Cues.Count == 0)
                {
                    (headerRaw, leading) = (leading, "");
                }

                cue.MarkRead(SubtitleFormat.WebVtt, parser.CueText!, block, leading, document.Cues.Count);
                document.Cues.Add(cue);
                leadingStart = lines[last].Next;
            }

            while (i < content.Length && content[i].Length == 0)
            {
                i++;
            }
        }

        if (document.Cues.Count == 0)
        {
            (headerRaw, leadingStart) = (text, text.Length);
        }

        document.MarkRead(SubtitleFormat.WebVtt, headerRaw, text[leadingStart..]);
        return document;
    }

    /// <summary>The block collector of the W3C algorithm, over lines already split at CR, LF and CR LF.</summary>
    private sealed class Parser(string[] lines, SubtitleDocument document)
    {
        private bool _seenCue;

        public SubtitleCue? Cue { get; private set; }

        public string? CueText { get; private set; }

        /// <summary>Collects a block from line <paramref name="i"/>; returns the line after it and the last line it used.</summary>
        public int CollectBlock(int i, bool inHeader, out int last)
        {
            Cue = null;
            CueText = null;
            int lineCount = 0, previous = i;
            StringBuilder buffer = new();
            bool seenArrow = false, stylesheet = false, region = false;
            SubtitleCue? cue = null;
            last = i;
            while (i < lines.Length)
            {
                string line = lines[i];
                lineCount++;
                i++;
                bool eof = i >= lines.Length;
                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    if (!inHeader && (lineCount == 1 || (lineCount == 2 && !seenArrow)))
                    {
                        seenArrow = true;
                        previous = i;
                        last = i - 1;
                        cue = ParseTimings(line, buffer.ToString(), document);
                        if (cue is not null)
                        {
                            buffer.Clear();
                            _seenCue = true;
                        }
                    }
                    else
                    {
                        i = previous;
                        break;
                    }
                }
                else if (line.Length == 0)
                {
                    break;
                }
                else
                {
                    if (!inHeader && lineCount == 2 && !_seenCue)
                    {
                        string b = buffer.ToString();
                        if (b.StartsWith("STYLE", StringComparison.Ordinal) && IsAsciiWhitespace(b.AsSpan(5)))
                        {
                            stylesheet = true;
                            buffer.Clear();
                        }
                        else if (b.StartsWith("REGION", StringComparison.Ordinal) && IsAsciiWhitespace(b.AsSpan(6)))
                        {
                            region = true;
                            buffer.Clear();
                        }
                    }

                    if (buffer.Length > 0)
                    {
                        buffer.Append('\n');
                    }

                    buffer.Append(line);
                    previous = i;
                    last = i - 1;
                }

                if (eof)
                {
                    break;
                }
            }

            if (cue is not null)
            {
                CueText = buffer.ToString();
                cue.Spans.AddRange(WebVttCueText.ToSpans(WebVttCueText.ParseTree(CueText)));
                Cue = cue;
            }
            else if (stylesheet)
            {
                document.StyleSheets.Add(buffer.ToString());
            }
            else if (region)
            {
                document.Regions.Add(ParseRegion(buffer.ToString()));
            }

            return i;
        }
    }

    private static bool IsAsciiWhitespace(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c is not ('\t' or '\n' or '\f' or '\r' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    private static int SkipWhitespace(string s, int at)
    {
        while (at < s.Length && s[at] is '\t' or '\n' or '\f' or '\r' or ' ')
        {
            at++;
        }

        return at;
    }

    private static SubtitleCue? ParseTimings(string line, string id, SubtitleDocument document)
    {
        int at = SkipWhitespace(line, 0);
        if (!TryTimestamp(line, ref at, out MediaTime start))
        {
            return null;
        }

        at = SkipWhitespace(line, at);
        if (!line.AsSpan(at).StartsWith("-->", StringComparison.Ordinal))
        {
            return null;
        }

        at = SkipWhitespace(line, at + 3);
        if (!TryTimestamp(line, ref at, out MediaTime end))
        {
            return null;
        }

        WebVttCueSettings settings = ParseSettings(line[at..], document);
        return new SubtitleCue(start, end, "") { Id = id.Length > 0 ? id : null, WebVtt = settings.IsDefault ? null : settings };
    }

    public static bool TryTimestamp(string text, out MediaTime time)
    {
        int at = 0;
        return TryTimestamp(text, ref at, out time) && at == text.Length;
    }

    /// <summary>The W3C "collect a WebVTT timestamp": [hours:]minutes:seconds.mmm, minutes and seconds two digits.</summary>
    private static bool TryTimestamp(string s, ref int at, out MediaTime time)
    {
        time = default;
        if (at >= s.Length || !char.IsAsciiDigit(s[at]))
        {
            return false;
        }

        int digits = SubtitleText.Digits(s, ref at, out long value1);
        bool hours = digits != 2 || value1 > 59;
        if (at >= s.Length || s[at] != ':')
        {
            return false;
        }

        at++;
        if (SubtitleText.Digits(s, ref at, out long value2) != 2)
        {
            return false;
        }

        long value3;
        if (hours || (at < s.Length && s[at] == ':'))
        {
            if (at >= s.Length || s[at] != ':')
            {
                return false;
            }

            at++;
            if (SubtitleText.Digits(s, ref at, out value3) != 2)
            {
                return false;
            }
        }
        else
        {
            (value3, value2, value1) = (value2, value1, 0);
        }

        if (at >= s.Length || s[at] != '.')
        {
            return false;
        }

        at++;
        if (SubtitleText.Digits(s, ref at, out long value4) != 3 || value2 > 59 || value3 > 59)
        {
            return false;
        }

        long total = (((value1 * 3600) + (value2 * 60) + value3) * 1000) + value4;
        time = new MediaTime(total, SubtitleText.Milliseconds);
        return true;
    }

    private static WebVttCueSettings ParseSettings(string input, SubtitleDocument document)
    {
        WebVttCueSettings settings = new();
        foreach (string setting in input.Split(['\t', '\n', '\f', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = setting.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == setting.Length - 1)
            {
                continue;
            }

            string name = setting[..colon], value = setting[(colon + 1)..];
            switch (name)
            {
                case "region":
                    settings = settings with { Region = document.Regions.LastOrDefault(r => r.Id == value)?.Id };
                    break;
                case "vertical" when value is "rl" or "lr":
                    settings = settings with { Vertical = value == "rl" ? WebVttVertical.RightToLeft : WebVttVertical.LeftToRight };
                    break;
                case "line":
                    settings = Line(settings, value);
                    break;
                case "position":
                    settings = Position(settings, value);
                    break;
                case "size" when TryPercentage(value, out double size):
                    settings = settings with { Size = size };
                    break;
                case "align":
                    WebVttAlign? align = value switch
                    {
                        "start" => WebVttAlign.Start,
                        "center" => WebVttAlign.Center,
                        "end" => WebVttAlign.End,
                        "left" => WebVttAlign.Left,
                        "right" => WebVttAlign.Right,
                        _ => null,
                    };
                    settings = align is { } a ? settings with { Align = a } : settings;
                    break;
            }
        }

        return settings;
    }

    private static WebVttCueSettings Line(WebVttCueSettings settings, string value)
    {
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        string linepos = comma >= 0 ? value[..comma] : value;
        string? linealign = comma >= 0 ? value[(comma + 1)..] : null;
        if (!linepos.Any(char.IsAsciiDigit))
        {
            return settings;
        }

        bool percent = linepos.EndsWith('%');
        double number;
        if (percent)
        {
            if (!TryPercentage(linepos, out number))
            {
                return settings;
            }
        }
        else
        {
            if (linepos.Any(c => !char.IsAsciiDigit(c) && c is not '-' and not '.') || linepos.LastIndexOf('-') > 0)
            {
                return settings;
            }

            for (int k = 0; k < linepos.Length; k++)
            {
                if (linepos[k] == '.' && (k == 0 || k == linepos.Length - 1 || !char.IsAsciiDigit(linepos[k - 1]) || !char.IsAsciiDigit(linepos[k + 1])))
                {
                    return settings;
                }
            }

            if (!double.TryParse(linepos, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
            {
                return settings;
            }

            // HTML's floating-point rules have no negative zero, and fail past the largest double.
            if (!double.IsFinite(number))
            {
                return settings;
            }

            number = number == 0 ? 0 : number;
        }

        WebVttLineAlign? align = linealign switch
        {
            null => settings.LineAlign,
            "start" => WebVttLineAlign.Start,
            "center" => WebVttLineAlign.Center,
            "end" => WebVttLineAlign.End,
            _ => null,
        };
        return align is { } a ? settings with { Line = number, SnapToLines = !percent, LineAlign = a } : settings;
    }

    private static WebVttCueSettings Position(WebVttCueSettings settings, string value)
    {
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        string colpos = comma >= 0 ? value[..comma] : value;
        string? colalign = comma >= 0 ? value[(comma + 1)..] : null;
        if (!TryPercentage(colpos, out double number))
        {
            return settings;
        }

        WebVttPositionAlign? align = colalign switch
        {
            null => WebVttPositionAlign.Auto,
            "line-left" => WebVttPositionAlign.LineLeft,
            "center" => WebVttPositionAlign.Center,
            "line-right" => WebVttPositionAlign.LineRight,
            _ => null,
        };
        return align is { } a ? settings with { Position = number, PositionAlign = colalign is null ? settings.PositionAlign : a } : settings;
    }

    /// <summary>The W3C "parse a percentage string": digits, an optional fraction, '%', between 0 and 100.</summary>
    private static bool TryPercentage(string value, out double number)
    {
        number = 0;
        if (value.Length < 2 || value[^1] != '%')
        {
            return false;
        }

        ReadOnlySpan<char> body = value.AsSpan(0, value.Length - 1);
        int dot = body.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? body : body[..dot];
        ReadOnlySpan<char> fraction = dot < 0 ? default : body[(dot + 1)..];
        if (whole.IsEmpty || whole.ContainsAnyExceptInRange('0', '9') || (dot >= 0 && (fraction.IsEmpty || fraction.ContainsAnyExceptInRange('0', '9'))))
        {
            return false;
        }

        number = double.Parse(body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return number is >= 0 and <= 100;
    }

    private static SubtitleRegion ParseRegion(string input)
    {
        SubtitleRegion region = new("");
        foreach (string setting in input.Split(['\t', '\n', '\f', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = setting.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == setting.Length - 1)
            {
                continue;
            }

            string name = setting[..colon], value = setting[(colon + 1)..];
            switch (name)
            {
                case "id" when !value.Contains("-->", StringComparison.Ordinal):
                    region = region with { Id = value };
                    break;
                case "width" when TryPercentage(value, out double width):
                    region = region with { Width = width };
                    break;
                case "lines" when !value.AsSpan().ContainsAnyExceptInRange('0', '9'):
                    region = region with { Lines = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long lines) ? lines : long.MaxValue };
                    break;
                case "regionanchor" when Anchor(value) is { } anchor:
                    region = region with { RegionAnchorX = anchor.X, RegionAnchorY = anchor.Y };
                    break;
                case "viewportanchor" when Anchor(value) is { } anchor:
                    region = region with { ViewportAnchorX = anchor.X, ViewportAnchorY = anchor.Y };
                    break;
                case "scroll" when value == "up":
                    region = region with { ScrollUp = true };
                    break;
            }
        }

        return region;
    }

    private static (double X, double Y)? Anchor(string value)
    {
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        return comma >= 0 && TryPercentage(value[..comma], out double x) && TryPercentage(value[(comma + 1)..], out double y) ? (x, y) : null;
    }

    public static string FormatTime(MediaTime time) => SubtitleText.FormatTime(time, SubtitleText.Milliseconds, '.', 2);

    public static string Write(SubtitleDocument document, string newLine, SubtitleLog log)
    {
        StringBuilder output = new();
        if (document.HeaderUnchanged(SubtitleFormat.WebVtt))
        {
            output.Append(document.Source!.Header);
        }
        else
        {
            WriteHeader(document, output, newLine, log);
        }

        for (int index = 0; index < document.Cues.Count; index++)
        {
            SubtitleCue cue = document.Cues[index];
            CueSource? source = cue.Source is { Format: SubtitleFormat.WebVtt } s ? s : null;
            if (source is not null && index > 0)
            {
                SubtitleWriting.EnsureNewLine(output, newLine);
                output.Append(source.Leading);
            }

            if (source is not null && cue.IsUnchanged(SubtitleFormat.WebVtt))
            {
                output.Append(source.RawBlock);
                continue;
            }

            SubtitleWriting.EnsureBlankLine(output, newLine);
            SubtitleWriting.CheckTimes(cue, index, log);
            SubtitleWriting.CheckCue(document, cue, index, log, SubtitleFormat.WebVtt);
            if (cue.Id is { Length: > 0 } id && !id.Contains("-->", StringComparison.Ordinal) && id.AsSpan().IndexOfAny('\r', '\n') < 0)
            {
                output.Append(id).Append(newLine);
            }

            output.Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End));
            string settings = Settings(document, cue);
            if (settings.Length > 0)
            {
                output.Append(' ').Append(settings);
            }

            output.Append(newLine);
            // An ASS speaker becomes a WebVTT voice.
            List<SubtitleSpan> spans = cue.Actor is { } actor && cue.Spans.All(s => s.Voice is null) ? [.. cue.Spans.Select(s => s with { Voice = actor })] : cue.Spans;
            string body = cue.TextUnchanged(SubtitleFormat.WebVtt) ? cue.RawText! : WebVttCueText.Write(spans);

            // A cue's text ends at a blank line or an arrow, so neither may appear in it.
            body = string.Join('\n', body.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0)).Replace("-->", "--&gt;");
            if (body.Length > 0)
            {
                output.Append(body.Replace("\n", newLine)).Append(newLine);
            }
        }

        if (document.Source is { Format: SubtitleFormat.WebVtt } && document.Cues.Count > 0)
        {
            output.Append(document.Source.Trailing);
        }

        return output.ToString();
    }

    private static void WriteHeader(SubtitleDocument document, StringBuilder output, string newLine, SubtitleLog log)
    {
        output.Append("WEBVTT");
        if (document.HeaderText is { Length: > 0 } header)
        {
            string[] lines = [.. header.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0 && !l.Contains("-->", StringComparison.Ordinal))];
            if (lines.Length > 0 && lines[0].Length > 0)
            {
                output.Append(' ').Append(lines[0]);
            }

            foreach (string line in lines.Skip(1))
            {
                output.Append(newLine).Append(line);
            }
        }

        output.Append(newLine);
        foreach (SubtitleRegion region in document.Regions)
        {
            output.Append(newLine).Append("REGION").Append(newLine);
            if (region.Id.Length > 0)
            {
                output.Append("id:").Append(region.Id).Append(' ');
            }

            output.Append("width:").Append(SubtitleText.Number(region.Width)).Append("% lines:").Append(region.Lines.ToString(CultureInfo.InvariantCulture))
                .Append(" regionanchor:").Append(SubtitleText.Number(region.RegionAnchorX)).Append("%,").Append(SubtitleText.Number(region.RegionAnchorY))
                .Append("% viewportanchor:").Append(SubtitleText.Number(region.ViewportAnchorX)).Append("%,").Append(SubtitleText.Number(region.ViewportAnchorY)).Append('%');
            if (region.ScrollUp)
            {
                output.Append(" scroll:up");
            }

            output.Append(newLine);
        }

        foreach (string sheet in document.StyleSheets)
        {
            string css = string.Join(newLine, sheet.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0).Select(l => l.Replace("-->", "- ->")));
            output.Append(newLine).Append("STYLE").Append(newLine).Append(css).Append(newLine);
        }

        if (document.Attachments.Count > 0)
        {
            log.Dropped("embedded fonts", 0);
        }
    }

    /// <summary>Gets a cue's settings text: its own WebVTT settings, or ones derived from its ASS alignment and position.</summary>
    private static string Settings(SubtitleDocument document, SubtitleCue cue)
    {
        WebVttCueSettings settings = cue.WebVtt ?? FromAlignment(document, cue);
        List<string> parts = [];
        if (settings.Region is { } region)
        {
            parts.Add("region:" + region);
        }

        if (settings.Vertical != WebVttVertical.Horizontal)
        {
            parts.Add(settings.Vertical == WebVttVertical.RightToLeft ? "vertical:rl" : "vertical:lr");
        }

        if (settings.Line is { } line)
        {
            string text = SubtitleText.Number(line) + (settings.SnapToLines ? "" : "%");
            parts.Add("line:" + text + settings.LineAlign switch
            {
                WebVttLineAlign.Center => ",center",
                WebVttLineAlign.End => ",end",
                _ => "",
            });
        }

        if (settings.Position is { } position)
        {
            parts.Add("position:" + SubtitleText.Number(position) + "%" + settings.PositionAlign switch
            {
                WebVttPositionAlign.LineLeft => ",line-left",
                WebVttPositionAlign.Center => ",center",
                WebVttPositionAlign.LineRight => ",line-right",
                _ => "",
            });
        }

        if (settings.Size != 100)
        {
            parts.Add("size:" + SubtitleText.Number(settings.Size) + "%");
        }

        if (settings.Align != WebVttAlign.Center)
        {
            parts.Add("align:" + settings.Align.ToString().ToLowerInvariant());
        }

        return string.Join(' ', parts);
    }

    private static WebVttCueSettings FromAlignment(SubtitleDocument document, SubtitleCue cue)
    {
        int a = (int)(cue.Alignment ?? SubtitleAlignment.BottomCenter) - 1;
        int row = a / 3, column = a % 3;
        WebVttAlign align = column switch
        {
            0 => WebVttAlign.Left,
            2 => WebVttAlign.Right,
            _ => WebVttAlign.Center,
        };
        if (cue.Position is { } position)
        {
            (double width, double height) = document.PlayRes;
            return new WebVttCueSettings
            {
                Line = Math.Round(Math.Clamp(position.Y / height * 100, 0, 100), 3),
                SnapToLines = false,
                LineAlign = row switch
                {
                    0 => WebVttLineAlign.End,
                    1 => WebVttLineAlign.Center,
                    _ => WebVttLineAlign.Start,
                },
                Position = Math.Round(Math.Clamp(position.X / width * 100, 0, 100), 3),
                PositionAlign = column switch
                {
                    0 => WebVttPositionAlign.LineLeft,
                    2 => WebVttPositionAlign.LineRight,
                    _ => WebVttPositionAlign.Center,
                },
                Align = align,
            };
        }

        return row switch
        {
            2 => new WebVttCueSettings { Line = 0, Align = align },
            1 => new WebVttCueSettings { Line = 50, SnapToLines = false, LineAlign = WebVttLineAlign.Center, Align = align },
            _ => new WebVttCueSettings { Align = align },
        };
    }
}
