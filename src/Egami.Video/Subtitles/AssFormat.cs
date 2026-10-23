using System.Globalization;
using System.Text;

using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>
/// Advanced SubStation Alpha (v4.00+) read and write, SubStation Alpha (v4.00) read: <c>[Script Info]</c>, styles, events,
/// embedded fonts and pictures, and the override tags that have a span field. Other sections and tags are kept as written.
/// </summary>
internal static class AssFormat
{
    internal static readonly string[] StyleFields =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "OutlineColour", "BackColour", "Bold", "Italic", "Underline",
        "StrikeOut", "ScaleX", "ScaleY", "Spacing", "Angle", "BorderStyle", "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV", "Encoding",
    ];

    internal static readonly string[] EventFields = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    private static readonly string[] SsaStyleFields =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "TertiaryColour", "BackColour", "Bold", "Italic", "BorderStyle",
        "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV", "AlphaLevel", "Encoding",
    ];

    private static readonly string[] SsaEventFields = ["Marked", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    /// <summary>Gets whether a script is SSA (v4.00) rather than ASS: its ScriptType, or a <c>[V4 Styles]</c> section.</summary>
    public static bool IsSsa(string text)
    {
        foreach (TextLine line in SubtitleText.Lines(text.Length > 8192 ? text[..8192] : text))
        {
            ReadOnlySpan<char> t = line.Of(text).Trim();
            if (t.StartsWith("ScriptType:", StringComparison.OrdinalIgnoreCase))
            {
                return t[11..].Trim().Equals("v4.00", StringComparison.OrdinalIgnoreCase);
            }

            if (t.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (t.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) || t.Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    public static SubtitleDocument Read(string text, SubtitleFormat format, SubtitleLog log)
    {
        SubtitleDocument document = new(format);
        bool ssa = format == SubtitleFormat.Ssa;
        List<TextLine> lines = SubtitleText.Lines(text);
        string[] styleFormat = ssa ? SsaStyleFields : StyleFields;
        string[] eventFormat = ssa ? SsaEventFields : EventFields;
        string section = "";
        List<string> extra = [];
        StringBuilder? extraSection = null;
        (string Name, bool Font, StringBuilder Data)? attachment = null;
        int leadingStart = 0, firstEvent = -1;
        string header = "";

        void EndAttachment()
        {
            if (attachment is { } a)
            {
                document.Attachments.Add(new SubtitleAttachment(a.Name, Uudecode(a.Data.ToString())) { IsFont = a.Font });
                attachment = null;
            }
        }

        void EndExtra()
        {
            if (extraSection is not null)
            {
                extra.Add(extraSection.ToString().TrimEnd('\r', '\n'));
                extraSection = null;
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Of(text).ToString();
            string t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                EndAttachment();
                EndExtra();
                section = t[1..^1].Trim().ToLowerInvariant();
                if (section is not ("script info" or "v4+ styles" or "v4 styles" or "v4 styles+" or "events" or "fonts" or "graphics"))
                {
                    extraSection = new StringBuilder(line).Append('\n');
                }

                continue;
            }

            if (extraSection is not null)
            {
                extraSection.Append(line).Append('\n');
                continue;
            }

            if (t.Length == 0 || (t.StartsWith(';') && section != "fonts" && section != "graphics") || t.StartsWith("!:", StringComparison.Ordinal))
            {
                continue;
            }

            switch (section)
            {
                case "script info":
                    int colon = t.IndexOf(':', StringComparison.Ordinal);
                    if (colon <= 0)
                    {
                        log.Repaired(i + 1, "a [Script Info] line has no ':' and was skipped.");
                        break;
                    }

                    string key = t[..colon].Trim();
                    if (!key.Equals("ScriptType", StringComparison.OrdinalIgnoreCase))
                    {
                        document.ScriptInfo.Add(new KeyValuePair<string, string>(key, t[(colon + 1)..].Trim()));
                    }

                    break;
                case "v4+ styles" or "v4 styles" or "v4 styles+":
                    if (TryField(t, "Format", out string formatLine))
                    {
                        styleFormat = [.. formatLine.Split(',').Select(f => f.Trim())];
                    }
                    else if (TryField(t, "Style", out string style))
                    {
                        document.Styles.Add(ParseStyle(style, styleFormat, ssa));
                    }
                    else
                    {
                        log.Repaired(i + 1, "a line in the styles section is not a Format or Style line and was skipped.");
                    }

                    break;
                case "events":
                    if (TryField(t, "Format", out string events))
                    {
                        eventFormat = [.. events.Split(',').Select(f => f.Trim())];
                        break;
                    }

                    bool comment = TryField(line.TrimStart(), "Comment", out string body);
                    if (!comment && !TryField(line.TrimStart(), "Dialogue", out body))
                    {
                        break;
                    }

                    if (ParseEvent(body, eventFormat, i, log) is not { } cue)
                    {
                        break;
                    }

                    cue.IsComment = comment;
                    int blockStart = lines[i].Start;
                    string leading = text[leadingStart..blockStart];
                    if (firstEvent < 0)
                    {
                        (header, leading, firstEvent) = (leading, "", i);
                    }

                    cue.MarkRead(format, FieldText(body, eventFormat), text[blockStart..lines[i].Next], leading, document.Cues.Count);
                    document.Cues.Add(cue);
                    leadingStart = lines[i].Next;
                    break;
                case "fonts" or "graphics":
                    bool font = section == "fonts";
                    if (TryField(t, font ? "fontname" : "filename", out string name))
                    {
                        EndAttachment();
                        attachment = (name, font, new StringBuilder());
                    }
                    else
                    {
                        attachment?.Data.Append(t);
                    }

                    break;
            }
        }

        EndAttachment();
        EndExtra();
        if (firstEvent < 0)
        {
            (header, leadingStart) = (text, text.Length);
        }

        document.MarkRead(format, header, text[leadingStart..], [.. extra]);
        document.Source = document.Source! with { EventFormat = eventFormat };
        return document;
    }

    private static bool TryField(string line, string name, out string value)
    {
        value = "";
        if (line.Length > name.Length && line.StartsWith(name, StringComparison.OrdinalIgnoreCase) && line[name.Length] == ':')
        {
            value = line[(name.Length + 1)..].TrimStart();
            return true;
        }

        return false;
    }

    private static string[] SplitFields(string body, int count)
    {
        string[] fields = body.Split(',', count);
        return fields.Length == count ? fields : [.. fields, .. Enumerable.Repeat("", count - fields.Length)];
    }

    private static string FieldText(string body, string[] format)
    {
        return SplitFields(body, format.Length)[^1];
    }

    private static SubtitleCue? ParseEvent(string body, string[] format, int line, SubtitleLog log)
    {
        string[] fields = SplitFields(body, format.Length);
        MediaTime start = default, end = default;
        bool hasStart = false, hasEnd = false;
        SubtitleCue cue = new(default, default, "");
        string text = "";
        for (int f = 0; f < format.Length; f++)
        {
            string value = f == format.Length - 1 ? fields[f] : fields[f].Trim();
            switch (format[f].ToLowerInvariant())
            {
                case "layer":
                    cue.Layer = Int(value);
                    break;
                case "start":
                    hasStart = TryTime(value, out start);
                    break;
                case "end":
                    hasEnd = TryTime(value, out end);
                    break;
                case "style":
                    cue.Style = value.Length > 0 ? value : null;
                    break;
                case "name":
                    cue.Actor = value.Length > 0 ? value : null;
                    break;
                case "marginl":
                    cue.MarginLeft = Int(value);
                    break;
                case "marginr":
                    cue.MarginRight = Int(value);
                    break;
                case "marginv":
                    cue.MarginVertical = Int(value);
                    break;
                case "effect":
                    cue.Effect = value.Length > 0 ? value : null;
                    break;
                case "text":
                    text = value;
                    break;
            }
        }

        if (!hasStart || !hasEnd)
        {
            log.Repaired(line + 1, "an event's start or end time cannot be read; the event was skipped.");
            return null;
        }

        cue.Start = start;
        cue.End = end;
        cue.Spans.AddRange(AssTags.Parse(text, start, out SubtitleAlignment? alignment, out SubtitlePosition? position));
        cue.Alignment = alignment;
        cue.Position = position;
        return cue;
    }

    private static int Int(string value)
    {
        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : 0;
    }

    private static double Number(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;
    }

    /// <summary>Reads <c>H:MM:SS.cc</c>, leniently: any digit counts, the fraction rounded to centiseconds.</summary>
    internal static bool TryTime(string value, out MediaTime time)
    {
        time = default;
        ReadOnlySpan<char> s = value.AsSpan().Trim();
        int at = 0;
        long hours = 0, minutes, seconds;
        if (SubtitleText.Digits(s, ref at, out long a) == 0 || at >= s.Length || s[at] != ':')
        {
            return false;
        }

        at++;
        if (SubtitleText.Digits(s, ref at, out long b) == 0)
        {
            return false;
        }

        if (at < s.Length && s[at] == ':')
        {
            at++;
            if (SubtitleText.Digits(s, ref at, out long c) == 0)
            {
                return false;
            }

            (hours, minutes, seconds) = (a, b, c);
        }
        else
        {
            (minutes, seconds) = (a, b);
        }

        long centi = 0;
        if (at < s.Length && s[at] is '.' or ',')
        {
            at++;
            int start = at;
            int digits = SubtitleText.Digits(s, ref at, out long fraction);
            centi = digits switch
            {
                0 => 0,
                1 => fraction * 10,
                2 => fraction,
                _ => (long)Math.Round(double.Parse(s[start..at], CultureInfo.InvariantCulture) / Math.Pow(10, digits - 2)),
            };
        }

        if (at != s.Length)
        {
            return false;
        }

        time = new MediaTime(checked((((((hours * 60) + minutes) * 60) + seconds) * 100) + centi), SubtitleText.Centiseconds);
        return true;
    }

    internal static string FormatTime(MediaTime time) => SubtitleText.FormatTime(time, SubtitleText.Centiseconds, '.', 1);

    private static SubtitleStyle ParseStyle(string body, string[] format, bool ssa)
    {
        string[] fields = SplitFields(body, format.Length);
        SubtitleStyle style = new(fields[0].Trim());
        for (int f = 1; f < format.Length; f++)
        {
            string v = fields[f].Trim();
            style = format[f].ToLowerInvariant() switch
            {
                "name" => style with { Name = v },
                "fontname" => style with { FontName = v },
                "fontsize" => style with { FontSize = Number(v, style.FontSize) },
                "primarycolour" => style with { PrimaryColor = ParseColor(v) ?? style.PrimaryColor },
                "secondarycolour" => style with { SecondaryColor = ParseColor(v) ?? style.SecondaryColor },
                "outlinecolour" or "tertiarycolour" => style with { OutlineColor = ParseColor(v) ?? style.OutlineColor },
                "backcolour" => style with { BackColor = ParseColor(v) ?? style.BackColor },
                "bold" => style with { Bold = Int(v) != 0 },
                "italic" => style with { Italic = Int(v) != 0 },
                "underline" => style with { Underline = Int(v) != 0 },
                "strikeout" => style with { Strikeout = Int(v) != 0 },
                "scalex" => style with { ScaleX = Number(v, 100) },
                "scaley" => style with { ScaleY = Number(v, 100) },
                "spacing" => style with { Spacing = Number(v, 0) },
                "angle" => style with { Angle = Number(v, 0) },
                "borderstyle" => style with { BorderStyle = Int(v) },
                "outline" => style with { Outline = Number(v, 0) },
                "shadow" => style with { Shadow = Number(v, 0) },
                "alignment" => style with { Alignment = ssa ? FromLegacyAlignment(Int(v)) : (SubtitleAlignment)Math.Clamp(Int(v), 1, 9) },
                "marginl" => style with { MarginLeft = Int(v) },
                "marginr" => style with { MarginRight = Int(v) },
                "marginv" => style with { MarginVertical = Int(v) },
                "encoding" => style with { Encoding = Int(v) },
                _ => style,
            };
        }

        return style;
    }

    /// <summary>SSA's alignment (1-3 bottom, +4 top, +8 middle) as a keypad position.</summary>
    internal static SubtitleAlignment FromLegacyAlignment(int value)
    {
        int column = Math.Clamp(value & 3, 1, 3);
        int row = (value & 8) != 0 ? 1 : (value & 4) != 0 ? 2 : 0;
        return (SubtitleAlignment)((row * 3) + column);
    }

    /// <summary>Reads an ASS color, <c>&amp;HAABBGGRR</c> in hex or as a decimal number, with alpha turned to opacity.</summary>
    internal static Rgba8? ParseColor(string value)
    {
        ReadOnlySpan<char> v = value.AsSpan().Trim().TrimEnd('&');
        long n;
        if (v.StartsWith("&H", StringComparison.OrdinalIgnoreCase) || v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(v[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n))
            {
                return null;
            }
        }
        else if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
        {
            return null;
        }

        uint u = (uint)n;
        return new Rgba8((byte)u, (byte)(u >> 8), (byte)(u >> 16), (byte)(255 - (u >> 24)));
    }

    internal static string FormatColor(Rgba8 color, bool alpha)
    {
        return alpha
            ? string.Create(CultureInfo.InvariantCulture, $"&H{255 - color.A:X2}{color.B:X2}{color.G:X2}{color.R:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"&H{color.B:X2}{color.G:X2}{color.R:X2}&");
    }

    /// <summary>Decodes the ASS form of an embedded file: each 4 characters (minus 33) are 24 bits; a short end group holds 1 or 2 bytes.</summary>
    internal static byte[] Uudecode(string data)
    {
        List<byte> bytes = new(data.Length * 3 / 4);
        int i = 0;
        while (i < data.Length)
        {
            int count = Math.Min(4, data.Length - i);
            int value = 0;
            for (int k = 0; k < 4; k++)
            {
                value = (value << 6) | (k < count ? (data[i + k] - 33) & 0x3F : 0);
            }

            bytes.Add((byte)(value >> 16));
            if (count > 2)
            {
                bytes.Add((byte)(value >> 8));
            }

            if (count > 3)
            {
                bytes.Add((byte)value);
            }

            i += count;
        }

        return [.. bytes];
    }

    internal static IEnumerable<string> Uuencode(ReadOnlySpan<byte> data)
    {
        StringBuilder text = new();
        for (int i = 0; i < data.Length; i += 3)
        {
            int count = Math.Min(3, data.Length - i);
            int value = data[i] << 16 | (count > 1 ? data[i + 1] << 8 : 0) | (count > 2 ? data[i + 2] : 0);
            for (int k = 0; k < count + 1; k++)
            {
                text.Append((char)(((value >> (18 - (6 * k))) & 0x3F) + 33));
            }
        }

        string all = text.ToString();
        List<string> lines = [];
        for (int i = 0; i < all.Length; i += 80)
        {
            lines.Add(all.Substring(i, Math.Min(80, all.Length - i)));
        }

        return lines;
    }

    public static string Write(SubtitleDocument document, string newLine, SubtitleLog log)
    {
        StringBuilder output = new();
        bool raw = document.HeaderUnchanged(SubtitleFormat.Ass);
        string[] format = raw && document.Source!.EventFormat is { } own ? own : EventFields;
        if (raw)
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
            CueSource? source = cue.Source is { Format: SubtitleFormat.Ass } s ? s : null;
            if (source is not null && index > 0)
            {
                SubtitleWriting.EnsureNewLine(output, newLine);
                output.Append(source.Leading);
            }

            if (source is not null && cue.IsUnchanged(SubtitleFormat.Ass))
            {
                output.Append(source.RawBlock);
                continue;
            }

            SubtitleWriting.EnsureNewLine(output, newLine);
            SubtitleWriting.CheckCue(document, cue, index, log, SubtitleFormat.Ass);
            output.Append(cue.IsComment ? "Comment: " : "Dialogue: ");
            string text = cue.TextUnchanged(SubtitleFormat.Ass) ? cue.RawText! : AssTags.Write(cue, document.PlayRes);
            string actor = cue.Actor ?? cue.Spans.Select(s => s.Voice).FirstOrDefault(v => v is not null) ?? "";
            for (int f = 0; f < format.Length; f++)
            {
                if (f > 0)
                {
                    output.Append(',');
                }

                output.Append(format[f].ToLowerInvariant() switch
                {
                    "layer" => cue.Layer.ToString(CultureInfo.InvariantCulture),
                    "marked" => "Marked=0",
                    "start" => FormatTime(cue.Start),
                    "end" => FormatTime(cue.End),
                    "style" => (cue.Style ?? "Default").Replace(',', ';'),
                    "name" => actor.Replace(',', ';').ReplaceLineEndings(" "),
                    "marginl" => cue.MarginLeft.ToString(CultureInfo.InvariantCulture),
                    "marginr" => cue.MarginRight.ToString(CultureInfo.InvariantCulture),
                    "marginv" => cue.MarginVertical.ToString(CultureInfo.InvariantCulture),
                    "effect" => (cue.Effect ?? "").Replace(',', ';'),
                    "text" => text.ReplaceLineEndings("\\N"),
                    _ => "",
                });
            }

            output.Append(newLine);
        }

        if (raw)
        {
            SubtitleWriting.EnsureNewLine(output, newLine);
            output.Append(document.Source!.Trailing);
        }

        return output.ToString();
    }

    private static void WriteHeader(SubtitleDocument document, StringBuilder output, string newLine, SubtitleLog log)
    {
        output.Append("[Script Info]").Append(newLine);
        output.Append("; Script generated by Egami.Video").Append(newLine);
        output.Append("ScriptType: v4.00+").Append(newLine);
        if (document.ScriptInfo.Count == 0)
        {
            output.Append("PlayResX: 384").Append(newLine).Append("PlayResY: 288").Append(newLine).Append("ScaledBorderAndShadow: yes").Append(newLine);
        }

        foreach (KeyValuePair<string, string> pair in document.ScriptInfo)
        {
            output.Append(pair.Key).Append(": ").Append(pair.Value.ReplaceLineEndings(" ")).Append(newLine);
        }

        if (document.Source is { Format: SubtitleFormat.Ass or SubtitleFormat.Ssa } source)
        {
            foreach (string section in source.ExtraSections)
            {
                output.Append(newLine).Append(section.ReplaceLineEndings(newLine)).Append(newLine);
            }
        }

        output.Append(newLine).Append("[V4+ Styles]").Append(newLine);
        output.Append("Format: ").Append(string.Join(", ", StyleFields)).Append(newLine);
        IEnumerable<SubtitleStyle> styles = document.Styles.Count > 0 ? document.Styles : [new SubtitleStyle("Default")];
        foreach (SubtitleStyle style in styles)
        {
            static string flag(bool on) => on ? "-1" : "0";
            output.Append("Style: ").Append(string.Join(',',
                style.Name.Replace(',', ';'), style.FontName.Replace(',', ';'), SubtitleText.Number(style.FontSize), FormatColor(style.PrimaryColor, true),
                FormatColor(style.SecondaryColor, true), FormatColor(style.OutlineColor, true), FormatColor(style.BackColor, true), flag(style.Bold),
                flag(style.Italic), flag(style.Underline), flag(style.Strikeout), SubtitleText.Number(style.ScaleX), SubtitleText.Number(style.ScaleY),
                SubtitleText.Number(style.Spacing), SubtitleText.Number(style.Angle), style.BorderStyle.ToString(CultureInfo.InvariantCulture),
                SubtitleText.Number(style.Outline), SubtitleText.Number(style.Shadow), ((int)style.Alignment).ToString(CultureInfo.InvariantCulture),
                style.MarginLeft.ToString(CultureInfo.InvariantCulture), style.MarginRight.ToString(CultureInfo.InvariantCulture),
                style.MarginVertical.ToString(CultureInfo.InvariantCulture), style.Encoding.ToString(CultureInfo.InvariantCulture))).Append(newLine);
        }

        foreach (bool fonts in (bool[])[true, false])
        {
            SubtitleAttachment[] files = [.. document.Attachments.Where(a => a.IsFont == fonts)];
            if (files.Length == 0)
            {
                continue;
            }

            output.Append(newLine).Append(fonts ? "[Fonts]" : "[Graphics]").Append(newLine);
            foreach (SubtitleAttachment file in files)
            {
                output.Append(fonts ? "fontname: " : "filename: ").Append(file.Name).Append(newLine);
                foreach (string line in Uuencode(file.Data.Span))
                {
                    output.Append(line).Append(newLine);
                }
            }
        }

        output.Append(newLine).Append("[Events]").Append(newLine);
        output.Append("Format: ").Append(string.Join(", ", EventFields)).Append(newLine);
        if (document.Regions.Count > 0)
        {
            log.Dropped("WebVTT regions", 0);
        }

        if (document.StyleSheets.Count > 0)
        {
            log.Dropped("WebVTT style sheets", 0);
        }
    }
}
