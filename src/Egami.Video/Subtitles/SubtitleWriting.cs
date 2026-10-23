using System.Globalization;
using System.Text;

using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>What the subtitle readers and writers share: span merging, colors, placement and the drop reports.</summary>
internal static class SubtitleWriting
{
    /// <summary>Adds a span, merging it into the last one when they look the same and the new one starts nothing of its own.</summary>
    public static void Append(List<SubtitleSpan> spans, SubtitleSpan span)
    {
        if (spans.Count > 0 && spans[^1] is { } last && last.SameLook(span) && last.IsDrawing == span.IsDrawing && last.Ruby is null
            && span.Ruby is null && span.Timestamp is null && span.OtherTags is null)
        {
            spans[^1] = last with { Text = last.Text + span.Text };
            return;
        }

        if (span.Text.Length > 0 || span.OtherTags is not null || span.Timestamp is not null)
        {
            spans.Add(span);
        }
    }

    public static void EnsureNewLine(StringBuilder output, string newLine)
    {
        if (output.Length > 0 && output[^1] is not ('\n' or '\r'))
        {
            output.Append(newLine);
        }
    }

    /// <summary>Ends the output with an empty line, unless it is empty or already does.</summary>
    public static void EnsureBlankLine(StringBuilder output, string newLine)
    {
        if (output.Length == 0)
        {
            return;
        }

        EnsureNewLine(output, newLine);
        int at = output.Length - 1;
        at -= at > 0 && output[at] == '\n' && output[at - 1] == '\r' ? 2 : 1;
        if (at >= 0 && output[at] is not ('\n' or '\r'))
        {
            output.Append(newLine);
        }
    }

    /// <summary>Reads a CSS-style color: <c>#rrggbb</c>, <c>#rgb</c>, <c>rrggbb</c>, or one of the 16 HTML color names.</summary>
    public static Rgba8? ParseCssColor(string value)
    {
        ReadOnlySpan<char> v = value.AsSpan().Trim();
        ReadOnlySpan<char> hex = v.StartsWith('#') ? v[1..] : v;
        if (hex.Length is 6 or 3 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
        {
            if (hex.Length == 3)
            {
                rgb = ((rgb >> 8) * 0x110000) | (((rgb >> 4) & 0xF) * 0x1100) | ((rgb & 0xF) * 0x11);
            }

            return new Rgba8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }

        uint? named = v.ToString().ToLowerInvariant() switch
        {
            "white" => 0xFFFFFF,
            "silver" => 0xC0C0C0,
            "gray" or "grey" => 0x808080,
            "black" => 0x000000,
            "red" => 0xFF0000,
            "maroon" => 0x800000,
            "yellow" => 0xFFFF00,
            "olive" => 0x808000,
            "lime" => 0x00FF00,
            "green" => 0x008000,
            "aqua" or "cyan" => 0x00FFFF,
            "teal" => 0x008080,
            "blue" => 0x0000FF,
            "navy" => 0x000080,
            "fuchsia" or "magenta" => 0xFF00FF,
            "purple" => 0x800080,
            _ => null,
        };
        return named is { } n ? new Rgba8((byte)(n >> 16), (byte)(n >> 8), (byte)n, 255) : null;
    }

    /// <summary>Gets where a cue sits: its own alignment, or one derived from its WebVTT settings.</summary>
    public static SubtitleAlignment? EffectiveAlignment(SubtitleCue cue)
    {
        if (cue.Alignment is { } a)
        {
            return a;
        }

        if (cue.WebVtt is not { } vtt || vtt.Vertical != WebVttVertical.Horizontal)
        {
            return null;
        }

        if (PositionOf(cue, (100, 100)) is not null)
        {
            // A box placed by position and line percentages: its anchor point is the alignment.
            int r = vtt.LineAlign switch
            {
                WebVttLineAlign.End => 0,
                WebVttLineAlign.Center => 1,
                _ => 2,
            };
            int c = vtt.PositionAlign switch
            {
                WebVttPositionAlign.LineLeft => 0,
                WebVttPositionAlign.LineRight => 2,
                _ => 1,
            };
            SubtitleAlignment placed = (SubtitleAlignment)((r * 3) + c + 1);
            return placed == SubtitleAlignment.BottomCenter ? null : placed;
        }

        int row = vtt.Line switch
        {
            null => 0,
            double line when vtt.SnapToLines => line < 0 ? 0 : 2,
            double percent => percent < 34 ? 2 : percent > 66 ? 0 : 1,
        };
        int column = vtt.Align switch
        {
            WebVttAlign.Left or WebVttAlign.Start => 0,
            WebVttAlign.Right or WebVttAlign.End => 2,
            _ => 1,
        };
        SubtitleAlignment result = (SubtitleAlignment)((row * 3) + column + 1);
        return result == SubtitleAlignment.BottomCenter ? null : result;
    }

    /// <summary>
    /// Gets a cue's exact place in script pixels: its own, or one from WebVTT position and line percentages of a
    /// <paramref name="playRes"/> script.
    /// </summary>
    public static SubtitlePosition? PositionOf(SubtitleCue cue, (double Width, double Height) playRes)
    {
        if (cue.Position is { } own)
        {
            return own;
        }

        return cue.WebVtt is { Position: { } x, Line: { } y, SnapToLines: false, Vertical: WebVttVertical.Horizontal }
            ? new SubtitlePosition(Math.Round(x * playRes.Width / 100, 3), Math.Round(y * playRes.Height / 100, 3))
            : null;
    }

    /// <summary>Reports times SRT and WebVTT cannot write, before zero.</summary>
    public static void CheckTimes(SubtitleCue cue, int index, SubtitleLog log)
    {
        if (cue.Start < MediaTime.Zero || cue.End < MediaTime.Zero)
        {
            log.Dropped("times before zero (they were written as zero)", index + 1);
        }
    }

    /// <summary>Reports, once per kind, what a cue has that <paramref name="target"/> cannot store.</summary>
    public static void CheckCue(SubtitleDocument document, SubtitleCue cue, int index, SubtitleLog log, SubtitleFormat target)
    {
        int number = index + 1;
        bool ass = target is SubtitleFormat.Ass or SubtitleFormat.Ssa;
        bool vtt = target == SubtitleFormat.WebVtt;
        if (!ass)
        {
            if (cue.Style is { } style && style != "Default" && document.Styles.Count > 0)
            {
                log.Dropped("ASS styles", number);
            }

            if (cue.Layer != 0)
            {
                log.Dropped("ASS layers", number);
            }

            if (cue.MarginLeft != 0 || cue.MarginRight != 0 || cue.MarginVertical != 0)
            {
                log.Dropped("ASS margins", number);
            }

            if (!string.IsNullOrEmpty(cue.Effect))
            {
                log.Dropped("ASS effects", number);
            }

            if (PositionOf(cue, (100, 100)) is not null && !vtt)
            {
                log.Dropped("exact positions", number);
            }
        }

        if (target == SubtitleFormat.Srt && cue.WebVtt is { } settings && (settings.Vertical != WebVttVertical.Horizontal || settings.Region is not null
            || (settings.Position is not null && PositionOf(cue, (100, 100)) is null) || settings.Size != 100))
        {
            log.Dropped("WebVTT cue settings", number);
        }

        string?[] voices = [.. cue.Spans.Select(s => s.Voice).Where(v => v is not null).Distinct()];
        if (target == SubtitleFormat.Srt ? cue.Actor is not null || voices.Length > 0 : ass && cue.Actor is null && voices.Length > 1)
        {
            log.Dropped("voices", number);
        }

        foreach (SubtitleSpan span in cue.Spans)
        {
            if (span.OtherTags is not null && !ass)
            {
                log.Dropped("ASS override tags", number);
            }

            if (span.IsDrawing && !ass)
            {
                log.Dropped("ASS drawings", number);
            }

            if (span.Timestamp is not null && target == SubtitleFormat.Srt)
            {
                log.Dropped("karaoke timing", number);
            }

            if (!vtt && (span.Classes.Count > 0 || span.Language is not null))
            {
                log.Dropped("WebVTT classes and languages", number);
            }

            if (!vtt && span.Ruby is not null)
            {
                log.Dropped("ruby text", number);
            }

            if (vtt && (span.Color is not null || span.Font is not null || span.FontSize is not null || span.Strikeout))
            {
                log.Dropped("colors, fonts and strikeout", number);
            }

            if (span.Color is { A: < 255 } && !ass)
            {
                log.Dropped("transparent colors", number);
            }
        }
    }
}
