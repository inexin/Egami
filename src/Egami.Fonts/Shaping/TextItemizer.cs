using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// Splits text into runs of one bidi level, one script, one span and one font, shapes each with <see cref="TextShaper.ShapeRun"/>
/// and orders them for display: the itemization behind <see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/>.
/// </summary>
internal static class TextItemizer
{
    private readonly record struct Run(int Start, int Length, int Level, Script Script, int Span, Font Font);

    public static ShapedText Shape(string text, Font font, ShapingOptions options)
    {
        TextSpan[] spans = CheckSpans(text, options.Spans);
        List<ShapedLine> lines = [];
        int lineStart = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            int breakLength = i == text.Length ? 0 : BreakLength(text, i);
            if (i < text.Length && breakLength == 0)
            {
                continue;
            }

            lines.Add(ShapeLine(text, lineStart, i - lineStart, font, options, spans));
            i += Math.Max(breakLength - 1, 0);
            lineStart = i + 1;
        }

        return new ShapedText(text, lines);
    }

    /// <summary>The length of the hard break at <paramref name="i"/>, or 0.</summary>
    private static int BreakLength(string text, int i) => text[i] switch
    {
        '\r' => i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1,
        '\n' or '\u0085' or '\u2028' or '\u2029' => 1,
        _ => 0,
    };

    private static TextSpan[] CheckSpans(string text, IReadOnlyList<TextSpan>? spans)
    {
        if (spans is null || spans.Count == 0)
        {
            return [];
        }

        TextSpan[] sorted = [.. spans.OrderBy(s => s.Start)];
        int end = 0;
        foreach (TextSpan span in sorted)
        {
            if (span.Start < 0 || span.Length < 0 || span.Length > text.Length - span.Start)
            {
                throw new ArgumentOutOfRangeException(nameof(spans), $"The span at {span.Start} with length {span.Length} is not inside the text.");
            }

            if (span.Start < end)
            {
                throw new ArgumentOutOfRangeException(nameof(spans), $"The span at {span.Start} overlaps the span before it.");
            }

            if (span.Font is { } f)
            {
                ArgumentNullException.ThrowIfNull(f.Typeface, nameof(spans));
            }

            end = span.Start + span.Length;
        }

        return sorted;
    }

    private static ShapedLine ShapeLine(string text, int lineStart, int lineLength, Font font, ShapingOptions options, TextSpan[] spans)
    {
        bool vertical = options.Direction == TextDirection.TopToBottom;
        ReadOnlySpan<char> line = text.AsSpan(lineStart, lineLength);
        if (lineLength > 0 && SimpleLine(line, options, spans) is Script simpleScript)
        {
            return ShapeSimpleLine(text, lineStart, lineLength, font, options, simpleScript);
        }

        using BidiResult bidi = Bidi.Resolve(line, vertical ? TextDirection.LeftToRight : options.Direction);
        int paragraphLevel = bidi.ParagraphLevel;
        if (lineLength == 0)
        {
            FontExtents e = font.Extents;
            return new ShapedLine(lineStart, 0, paragraphLevel, [], e.Ascent, e.Descent, font.LineHeight);
        }

        List<ScriptRun> scripts = [];
        if (options.Script is Script fixedScript)
        {
            scripts.Add(new ScriptRun(0, lineLength, fixedScript));
        }
        else
        {
            ScriptItemizer.Itemize(line, scripts);
        }

        // Clusters into runs.
        List<Run> runs = [];
        ReadOnlySpan<byte> levels = bidi.Levels;
        int scriptIndex = 0;
        Font? previousFont = null;
        Run current = default;
        bool open = false;
        foreach (TextSegment cluster in TextSegmenter.Graphemes(line))
        {
            while (scriptIndex + 1 < scripts.Count && cluster.Start >= scripts[scriptIndex].Start + scripts[scriptIndex].Length)
            {
                scriptIndex++;
            }

            // Control characters (tabs and the like) draw nothing: they end the run and give no glyph.
            if (UnicodeData.GetGeneralCategory(TextSegmenter.CodePointAt(line, cluster.Start, out _)) == System.Globalization.UnicodeCategory.Control)
            {
                if (open)
                {
                    runs.Add(current);
                    open = false;
                }

                continue;
            }

            int spanIndex = SpanAt(spans, lineStart + cluster.Start);
            Font spanFont = spanIndex >= 0 && spans[spanIndex].Font is Font f ? f : font;
            Script script = scripts[scriptIndex].Script;
            string? language = spanIndex >= 0 ? spans[spanIndex].Language ?? options.Language : options.Language;
            ReadOnlySpan<char> clusterText = line.Slice(cluster.Start, cluster.Length);
            Font chosen = ChooseFont(clusterText, spanFont, open && current.Span == spanIndex ? previousFont : null, options.Fallback, script, language);
            previousFont = chosen;
            int level = vertical ? 0 : levels[cluster.Start];
            if (open && current.Level == level && current.Script == script && current.Span == spanIndex && current.Font == chosen &&
                current.Start + current.Length == cluster.Start)
            {
                current = current with { Length = current.Length + cluster.Length };
                continue;
            }

            if (open)
            {
                runs.Add(current);
            }

            current = new Run(cluster.Start, cluster.Length, level, script, spanIndex, chosen);
            open = true;
        }

        if (open)
        {
            runs.Add(current);
        }

        // Shape each run, then order the runs for display (UAX #9 rule L2).
        List<(ShapedRun Shaped, int Level)> shaped = [];
        float ascent = 0, descent = 0, lineHeight = 0;
        foreach (Run run in runs)
        {
            TextSpan? span = run.Span >= 0 ? spans[run.Span] : null;
            string? language = span?.Language ?? options.Language;
            List<FontFeature> features = [.. options.Features ?? []];
            features.AddRange(span?.Features ?? []);
            RunProperties properties = new()
            {
                Script = run.Script.IsShared ? null : run.Script,
                Language = language,
                Direction = vertical ? TextDirection.TopToBottom : (run.Level & 1) != 0 ? TextDirection.RightToLeft : TextDirection.LeftToRight,
                Features = features,
                ClusterLevel = options.ClusterLevel,
            };
            GlyphBuffer glyphs = new();
            TextShaper.ShapeRun(text, lineStart + run.Start, run.Length, run.Font, properties, glyphs);
            shaped.Add((new ShapedRun(run.Font, run.Level, run.Script, language, lineStart + run.Start, run.Length, glyphs, vertical), run.Level));
            FontExtents e = run.Font.Extents;
            ascent = Math.Max(ascent, e.Ascent);
            descent = Math.Min(descent, e.Descent);
            lineHeight = Math.Max(lineHeight, run.Font.LineHeight);
        }

        ReorderRuns(shaped);
        return new ShapedLine(lineStart, lineLength, paragraphLevel, [.. shaped.Select(s => s.Shaped)], ascent, descent, lineHeight);
    }

    /// <summary>
    /// The script of a line that itemizes to one left-to-right run in the given font, without resolving bidi or walking graphemes:
    /// no spans, no fallback, a horizontal left-to-right or automatic direction, and only characters below U+0590 (Latin, Greek,
    /// Cyrillic, Armenian: no right-to-left or control character) or General Punctuation other than the bidi controls, of one
    /// script besides the shared ones; else null.
    /// </summary>
    private static Script? SimpleLine(ReadOnlySpan<char> line, ShapingOptions options, TextSpan[] spans)
    {
        if (spans.Length > 0 || options.Script is not null || options.Direction is not (TextDirection.Auto or TextDirection.LeftToRight) ||
            options.Fallback.Families.Count > 0 || options.Fallback.Resolver is not null)
        {
            return null;
        }

        Script script = Script.Common;
        foreach (char c in line)
        {
            if (c >= 0x0590 && !(c is >= (char)0x2000 and <= (char)0x206F && c is not ((>= (char)0x200E and <= (char)0x200F) or (>= (char)0x202A and <= (char)0x202E) or >= (char)0x2066)))
            {
                return null;
            }

            if (c < 0x20 || c is >= (char)0x7F and <= (char)0x9F)
            {
                return null;
            }

            if (c < 0x41)
            {
                continue;
            }

            Script s = UnicodeData.GetScript(c);
            if (s.IsShared || s == script)
            {
                continue;
            }

            if (script != Script.Common)
            {
                return null;
            }

            script = s;
        }

        return script;
    }

    private static ShapedLine ShapeSimpleLine(string text, int lineStart, int lineLength, Font font, ShapingOptions options, Script script)
    {
        RunProperties properties = new()
        {
            Script = script.IsShared ? null : script,
            Language = options.Language,
            Direction = TextDirection.LeftToRight,
            Features = options.Features,
            ClusterLevel = options.ClusterLevel,
        };
        GlyphBuffer glyphs = new();
        TextShaper.ShapeRun(text, lineStart, lineLength, font, properties, glyphs);
        FontExtents e = font.Extents;
        ShapedRun run = new(font, 0, script, options.Language, lineStart, lineLength, glyphs, false);
        return new ShapedLine(lineStart, lineLength, 0, [run], Math.Max(0, e.Ascent), Math.Min(0, e.Descent), font.LineHeight);
    }

    private static int SpanAt(TextSpan[] spans, int index)
    {
        int lo = 0, hi = spans.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (index < spans[mid].Start)
            {
                hi = mid - 1;
            }
            else if (index >= spans[mid].Start + spans[mid].Length)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>Reverses every sequence of runs at or above each odd level, highest first (rule L2).</summary>
    private static void ReorderRuns(List<(ShapedRun Shaped, int Level)> runs)
    {
        if (runs.Count < 2)
        {
            return;
        }

        int highest = runs.Max(r => r.Level);
        int lowestOdd = runs.Min(r => r.Level) | 1;
        for (int level = highest; level >= lowestOdd; level--)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].Level < level)
                {
                    continue;
                }

                int end = i;
                while (end < runs.Count && runs[end].Level >= level)
                {
                    end++;
                }

                runs.Reverse(i, end - i);
                i = end;
            }
        }
    }

    // ---- Font choice ----

    /// <summary>
    /// The font for one grapheme cluster: a shared character (space, punctuation) stays in the previous cluster's font when that maps
    /// it; then the span's font, the fallback families and the resolver, the first that maps every character; emoji presentation
    /// prefers color faces and text presentation outline faces; else the span's font with .notdef.
    /// </summary>
    private static Font ChooseFont(ReadOnlySpan<char> cluster, Font spanFont, Font? previous, FontFallback fallback, Script script, string? language)
    {
        if (previous is Font p && p != spanFont && IsShared(cluster) && Maps(p.Typeface, cluster))
        {
            return p;
        }

        bool emoji = WantsEmoji(cluster);
        bool textPresentation = !emoji && cluster.Contains('\uFE0E');
        if (fallback.Families.Count == 0 && fallback.Resolver is null)
        {
            return spanFont;
        }

        Font? anyMapping = null;
        if (Maps(spanFont.Typeface, cluster))
        {
            if (Preferred(spanFont.Typeface, emoji, textPresentation))
            {
                return spanFont;
            }

            anyMapping = spanFont;
        }

        FontStyle style = spanFont.Typeface.Style;
        foreach (FontFamily family in fallback.Families)
        {
            FontMatch match = family.Match(style);
            if (!Maps(match.Typeface, cluster))
            {
                continue;
            }

            Font candidate = Derive(spanFont, match.Typeface, match.Synthesis);
            if (Preferred(match.Typeface, emoji, textPresentation))
            {
                return candidate;
            }

            anyMapping ??= candidate;
        }

        if (anyMapping is Font found)
        {
            return found;
        }

        if (fallback.Resolver is { } resolver && resolver.TryResolve(cluster, script, language, style, out Typeface? resolved))
        {
            return Derive(spanFont, resolved, FontSynthesis.None);
        }

        return spanFont;
    }

    private static bool Preferred(Typeface face, bool emoji, bool textPresentation) =>
        emoji ? IsColor(face) : !textPresentation || !IsColor(face);

    private static Font Derive(Font font, Typeface face, FontSynthesis synthesis) => new(face, font.Size)
    {
        Kerning = font.Kerning,
        AllowSynthesis = font.AllowSynthesis,
        Synthesis = synthesis,
        Hinting = font.Hinting,
        Subpixel = font.Subpixel,
        Edging = font.Edging,
        OpticalSizing = font.OpticalSizing,
    };

    private static bool IsShared(ReadOnlySpan<char> cluster) =>
        UnicodeData.GetScript(TextSegmenter.CodePointAt(cluster, 0, out _)).IsShared && !WantsEmoji(cluster);

    /// <summary>Whether a cluster asks for emoji presentation (UTS #51): VS16, or a first character with default emoji presentation
    /// and no VS15.</summary>
    internal static bool WantsEmoji(ReadOnlySpan<char> cluster)
    {
        if (cluster.Contains('\uFE0F'))
        {
            return true;
        }

        return !cluster.Contains('\uFE0E') && UnicodeData.IsEmojiPresentation(TextSegmenter.CodePointAt(cluster, 0, out _));
    }

    /// <summary>Whether a face has color glyphs (COLR, sbix or CBDT).</summary>
    private static bool IsColor(Typeface face) => (face.ColorFormats & ~ColorGlyphFormats.Svg) != 0;

    /// <summary>Whether a face maps every character of a cluster that is not default-ignorable.</summary>
    internal static bool Maps(Typeface face, ReadOnlySpan<char> cluster)
    {
        for (int i = 0; i < cluster.Length;)
        {
            int cp = TextSegmenter.CodePointAt(cluster, i, out int size);
            i += size;
            if (!UnicodeData.IsDefaultIgnorable(cp) && !MapsCodePoint(face, cp))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool MapsCodePoint(Typeface face, int codePoint) => face.GetGlyph(codePoint) != 0;
}
