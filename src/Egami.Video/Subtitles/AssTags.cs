using System.Globalization;
using System.Text;

using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>
/// ASS event text: override blocks (<c>{\b1\c&amp;H0000FF&amp;}</c>) and the <c>\N</c>, <c>\n</c> and <c>\h</c> escapes.
/// <c>b</c>, <c>i</c>, <c>u</c>, <c>s</c>, <c>c</c>/<c>1c</c>, <c>fn</c>, <c>fs</c>, <c>k</c> become span fields and
/// <c>pos</c>, <c>an</c>, <c>a</c> cue fields; every other tag stays, as written, in <see cref="SubtitleSpan.OtherTags"/>.
/// </summary>
internal static class AssTags
{
    /// <summary>The longest karaoke a cue can count, in centiseconds: up to the latest time a file may give.</summary>
    private const long KaraokeLimit = SubtitleText.MaxHours * 360_000;

    /// <summary>
    /// Reads an event's text into spans. <paramref name="lookOf"/> gives a style's bold, italic, underline and strikeout as a
    /// span (the event's own style for <see langword="null"/>): spans start from it, and <c>\r</c> goes back to it, so a span's
    /// flags say how the text looks.
    /// </summary>
    public static List<SubtitleSpan> Parse(string text, MediaTime start, Func<string?, SubtitleSpan> lookOf, out SubtitleAlignment? alignment, out SubtitlePosition? position)
    {
        alignment = null;
        position = null;
        List<SubtitleSpan> spans = [];
        SubtitleSpan look = lookOf(null);
        StringBuilder run = new();
        StringBuilder? other = null;
        MediaTime? timestamp = null;
        long karaoke = 0;
        bool drawing = false;

        void Flush()
        {
            if (run.Length > 0 || other is not null || timestamp is not null)
            {
                SubtitleWriting.Append(spans, look with { Text = run.ToString(), OtherTags = other?.ToString(), Timestamp = timestamp, IsDrawing = drawing && run.Length > 0 });
            }

            run.Clear();
            other = null;
            timestamp = null;
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{' && text.IndexOf('}', i + 1) is int close and > 0)
            {
                Flush();
                ReadOnlySpan<char> block = text.AsSpan(i + 1, close - i - 1);
                int at = 0;

                // Text before the first backslash is a comment, which the block keeps.
                int firstTag = block.IndexOf('\\');
                if (firstTag != 0)
                {
                    (other ??= new()).Append(firstTag < 0 ? block : block[..firstTag]);
                    at = firstTag < 0 ? block.Length : firstTag;
                }

                while (at < block.Length)
                {
                    int end = TagEnd(block, at + 1);
                    string tag = block[(at + 1)..end].ToString();
                    if (!Apply(tag, lookOf, ref look, ref alignment, ref position, ref drawing))
                    {
                        if (tag.Length > 1 && tag[0] == 'k' && long.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long centiseconds))
                        {
                            timestamp = start + new MediaTime(karaoke, SubtitleText.Centiseconds);
                            karaoke = Math.Min(karaoke + Math.Min(centiseconds, KaraokeLimit), KaraokeLimit);
                        }
                        else
                        {
                            (other ??= new()).Append('\\').Append(tag);
                        }
                    }

                    at = end;
                }

                i = close + 1;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'N' or 'n' or 'h')
            {
                run.Append(text[i + 1] == 'h' ? '\u00A0' : '\n');
                i += 2;
                continue;
            }

            run.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    /// <summary>Finds where a tag ends: at the next backslash outside parentheses, so <c>\t(\b1)</c> stays one tag.</summary>
    private static int TagEnd(ReadOnlySpan<char> block, int at)
    {
        int depth = 0;
        while (at < block.Length)
        {
            char c = block[at];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == '\\' && depth == 0)
            {
                break;
            }

            at++;
        }

        return at;
    }

    /// <summary>Applies a tag that has a field; returns false for the others.</summary>
    private static bool Apply(string tag, Func<string?, SubtitleSpan> lookOf, ref SubtitleSpan look, ref SubtitleAlignment? alignment, ref SubtitlePosition? position, ref bool drawing)
    {
        if (tag.Length == 2 && tag[0] is 'b' or 'i' or 'u' or 's' && tag[1] is '0' or '1')
        {
            bool on = tag[1] == '1';
            look = tag[0] switch
            {
                'b' => look with { Bold = on },
                'i' => look with { Italic = on },
                'u' => look with { Underline = on },
                _ => look with { Strikeout = on },
            };
            return true;
        }

        if (tag.StartsWith("1c", StringComparison.Ordinal) || (tag.StartsWith('c') && (tag.Length == 1 || tag[1] == '&')))
        {
            string value = tag[(tag[0] == '1' ? 2 : 1)..];
            if (value.Length == 0)
            {
                look = look with { Color = null };
                return true;
            }

            if (AssFormat.ParseColor(value) is { } color && value.EndsWith('&'))
            {
                // A \c color sets only red, green and blue; \1a sets the alpha, which stays an other tag.
                look = look with { Color = new Rgba8(color.R, color.G, color.B, 255) };
                return true;
            }

            return false;
        }

        if (tag.StartsWith("fn", StringComparison.Ordinal))
        {
            look = look with { Font = tag.Length > 2 ? tag[2..].Trim() : null };
            return true;
        }

        if (tag.StartsWith("fs", StringComparison.Ordinal) && (tag.Length == 2 || char.IsAsciiDigit(tag[2])))
        {
            if (tag.Length == 2)
            {
                look = look with { FontSize = null };
                return true;
            }

            if (double.TryParse(tag.AsSpan(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double size))
            {
                look = look with { FontSize = size };
                return true;
            }

            return false;
        }

        if (tag.StartsWith("pos(", StringComparison.Ordinal) && tag.EndsWith(')') && tag[4..^1].Split(',') is [string x, string y]
            && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double px)
            && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
        {
            position ??= new SubtitlePosition(px, py);
            return true;
        }

        if (tag.Length == 3 && tag.StartsWith("an", StringComparison.Ordinal) && tag[2] is >= '1' and <= '9')
        {
            alignment ??= (SubtitleAlignment)(tag[2] - '0');
            return true;
        }

        if (tag.Length > 1 && tag[0] == 'a' && int.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int legacy) && legacy is > 0 and < 12)
        {
            alignment ??= AssFormat.FromLegacyAlignment(legacy);
            return true;
        }

        if (tag.Length > 1 && tag[0] == 'p' && int.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int scale))
        {
            // The drawing mode stays an other tag; the text after it is drawing commands.
            drawing = scale > 0;
            return false;
        }

        if (tag.StartsWith('r') && !tag.StartsWith("rnd", StringComparison.Ordinal))
        {
            // \r goes back to the event's style, \rName to that style.
            look = lookOf(tag.Length > 1 ? tag[1..] : null);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Writes a cue's spans as ASS text: the cue's \an and \pos first, then each span's other tags and the changes from the
    /// look before it, starting from the style's (<paramref name="lookOf"/>, as for <see cref="Parse"/>).
    /// </summary>
    public static string Write(SubtitleCue cue, (double Width, double Height) playRes, Func<string?, SubtitleSpan> lookOf)
    {
        StringBuilder output = new();
        StringBuilder tags = new();
        SubtitleAlignment? alignment = SubtitleWriting.EffectiveAlignment(cue);
        if (alignment is { } a)
        {
            tags.Append("\\an").Append((int)a);
        }

        if (SubtitleWriting.PositionOf(cue, playRes) is { } p)
        {
            tags.Append(CultureInfo.InvariantCulture, $"\\pos({SubtitleText.Number(p.X)},{SubtitleText.Number(p.Y)})");
        }

        SubtitleSpan state = lookOf(null);
        for (int s = 0; s < cue.Spans.Count; s++)
        {
            SubtitleSpan span = cue.Spans[s];
            if (span.OtherTags is { } other)
            {
                tags.Append(other);
                if (LastReset(other) is { } name)
                {
                    state = lookOf(name.Length > 0 ? name : null);
                }
            }

            if (span.Timestamp is { } time)
            {
                MediaTime next = cue.Spans.Skip(s + 1).Select(x => x.Timestamp).FirstOrDefault(t => t is not null) ?? cue.End;
                long duration = Math.Max(0, (next - time).Rescale(SubtitleText.Centiseconds).Ticks);
                tags.Append("\\k").Append(duration.ToString(CultureInfo.InvariantCulture));
            }

            Toggle(tags, 'b', state.Bold, span.Bold);
            Toggle(tags, 'i', state.Italic, span.Italic);
            Toggle(tags, 'u', state.Underline, span.Underline);
            Toggle(tags, 's', state.Strikeout, span.Strikeout);
            if (!Nullable.Equals(state.Color, span.Color))
            {
                tags.Append("\\c").Append(span.Color is { } color ? AssFormat.FormatColor(color, alpha: false) : "");
            }

            if (state.Font != span.Font)
            {
                tags.Append("\\fn").Append(span.Font);
            }

            if (!Nullable.Equals(state.FontSize, span.FontSize))
            {
                tags.Append("\\fs").Append(span.FontSize is { } size ? SubtitleText.Number(size) : "");
            }

            if (tags.Length > 0)
            {
                output.Append('{').Append(tags).Append('}');
                tags.Clear();
            }

            state = span;
            output.Append(span.IsDrawing ? span.Text : span.Text.Replace("\n", "\\N").Replace("{", "(").Replace("}", ")"));
        }

        if (tags.Length > 0)
        {
            output.Append('{').Append(tags).Append('}');
        }

        return output.ToString();
    }

    /// <summary>The style name of the last <c>\r</c> tag in some tags (empty for the event's own style), or <see langword="null"/>.</summary>
    private static string? LastReset(string tags)
    {
        string? name = null;
        int at = 0;
        while ((at = tags.IndexOf("\\r", at, StringComparison.Ordinal)) >= 0)
        {
            int end = TagEnd(tags, at + 1);
            string tag = tags[(at + 1)..end];
            if (!tag.StartsWith("rnd", StringComparison.Ordinal))
            {
                name = tag[1..];
            }

            at = end;
        }

        return name;
    }

    private static void Toggle(StringBuilder tags, char tag, bool was, bool now)
    {
        if (was != now)
        {
            tags.Append('\\').Append(tag).Append(now ? '1' : '0');
        }
    }
}
