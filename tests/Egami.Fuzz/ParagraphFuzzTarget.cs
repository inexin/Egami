using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The paragraph fuzz target (Phase 2.8.2): the first eight bytes pick the paragraph's options (alignment, justification, line
/// breaking, fill, writing mode, line limits, width, spacing, spans), the rest is UTF-16 text, U+FFFC standing for placeholders.
/// The paragraph is laid out, flowed line by line at varying widths, hit tested, caret-walked and selected, its decorations and
/// outlines taken, and its break opportunities enumerated; every result must be in range and finite. Egami.Fonts.Tests compiles
/// this file too and feeds it its robustness cases and random inputs.
/// </summary>
internal static class ParagraphFuzzTarget
{
    private const int MaxChars = 2048;

    public static void Run(byte[] data, IReadOnlyList<Typeface> faces, Action<TextLayout>? draw = null)
    {
        byte[] head = new byte[8];
        data.AsSpan(0, Math.Min(8, data.Length)).CopyTo(head);
        ReadOnlySpan<byte> body = data.AsSpan(Math.Min(8, data.Length));
        string text = new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(body[..(Math.Min(body.Length, MaxChars * 2) & ~1)]));
        Run(text, head, faces, draw);
    }

    public static void Run(string text, ReadOnlySpan<byte> head, IReadOnlyList<Typeface> faces, Action<TextLayout>? draw = null)
    {
        FontCollection collection = new();
        foreach (Typeface face in faces)
        {
            collection.Add(face);
        }

        FontFamily[] families = [.. collection.Families];
        byte a = head[0], b = head[1], lines = head[2], widthCode = head[3], spacing = head[4], seed = head[5], tabs = head[6], misc = head[7];
        LineBreakOptions breaking = new(
            (Egami.Fonts.Layout.WordBreak)((a >> 3) % 3),
            (OverflowWrap)((a >> 5) % 3),
            (LineBreakStrictness)((b >> 2) % 5),
            (misc & 3) switch { 1 => "ja", 2 => "zh", 3 => "ko", _ => null });
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle
            {
                FontFamilies = families,
                Size = 6 + (misc >> 2),
                LetterSpacing = (sbyte)spacing / 8f,
                WordSpacing = (seed & 0x80) != 0 ? (sbyte)spacing / 4f : null,
                LineHeight = (misc & 0x40) != 0 ? LineHeight.Multiple(0.5f + ((seed & 7) / 2f)) : null,
            },
            Align = (TextAlign)(a % 6),
            AlignLast = (TextAlignLast)((a >> 6) % 7),
            Justify = (TextJustify)((b >> 5) % 4),
            Direction = (a & 0x80) != 0 ? TextDirection.RightToLeft : TextDirection.Auto,
            LineBreaking = breaking,
            LineFill = (b & 0x10) != 0 ? LineFill.Balanced : LineFill.Greedy,
            WritingMode = (WritingMode)(b % 3),
            TextOrientation = (TextOrientation)((seed >> 3) % 3),
            TrimEdges = (b & 0x80) != 0,
            MaxLines = lines is 0 or > 200 ? null : 1 + (lines % 8),
            MaxHeight = lines > 200 ? lines - 200 : null,
            Ellipsis = (seed & 0x40) != 0 ? "\u2026" : (seed & 0x20) != 0 ? "..." : null,
            Hyphen = (tabs & 1) != 0 ? "-" : "\u2010",
            TabStops = (tabs & 2) != 0 ? [new TabStop(tabs, (TabAlignment)((tabs >> 2) % 4)), new TabStop(tabs * 2.5f, TabAlignment.Decimal)] : null,
        };

        Paragraph paragraph = Paragraph.Create(Rich(text, seed, families), style);

        float width = widthCode == 255 ? float.PositiveInfinity : widthCode * 3f;
        TextLayout layout = paragraph.Layout(width);
        CheckLayout(layout, text.Length);
        CheckInteraction(layout, text.Length, seed);
        CheckFlow(paragraph, text.Length, seed);
        CheckBreaks(text, breaking);
        draw?.Invoke(layout);
    }

    /// <summary>Rich text: the text cut at a few seeded points into spans of a few styles, U+FFFC as placeholders.</summary>
    private static RichText Rich(string text, byte seed, FontFamily[] families)
    {
        Random random = new(seed);
        TextStyle[] styles =
        [
            new() { Size = 4 + random.Next(40), Decoration = new TextDecoration { Lines = TextDecorationLines.Underline | TextDecorationLines.Overline, Style = (TextDecorationStyle)random.Next(5) } },
            new() { BaselineShift = random.Next(-20, 20), Background = new Rgba8(255, 255, 0, 255), LetterSpacing = random.Next(-5, 10) },
            new() { FontFamilies = [families[^1]], Shadows = [new TextShadow(new Vector2(1, 2), random.Next(4), default)], Decoration = new TextDecoration { Lines = TextDecorationLines.LineThrough } },
            new() { LineHeight = LineHeight.Absolute(random.Next(60)), Language = "ja" },
        ];
        int[] cuts = [.. Enumerable.Range(0, random.Next(5)).Select(_ => random.Next(text.Length + 1)).Order()];
        RichTextBuilder builder = new();
        int start = 0;
        for (int i = 0; i <= cuts.Length; i++)
        {
            int end = i < cuts.Length ? cuts[i] : text.Length;
            bool styled = random.Next(3) > 0;
            if (styled)
            {
                builder.Push(styles[random.Next(styles.Length)]);
            }

            int from = start;
            for (int k = start; k < end; k++)
            {
                if (text[k] == '\uFFFC')
                {
                    builder.Append(text.AsSpan(from, k - from));
                    builder.AddPlaceholder(new Placeholder(random.Next(40), random.Next(40), (PlaceholderAlignment)random.Next(6), random.Next(-10, 10)));
                    from = k + 1;
                }
            }

            builder.Append(text.AsSpan(from, end - from));
            if (styled)
            {
                builder.Pop();
            }

            start = end;
        }

        return builder.ToRichText();
    }

    private static void CheckLayout(TextLayout layout, int length)
    {
        Finite(layout.Width, layout.Height, layout.LongestLine, layout.MinIntrinsicWidth, layout.MaxIntrinsicWidth);
        Check(layout.MinIntrinsicWidth <= layout.MaxIntrinsicWidth + 0.01f, "min-content wider than max-content");
        int next = 0;
        foreach (TextLine line in layout.Lines)
        {
            Check(line.Start == next, $"line starts at {line.Start}, not {next}");
            Check(line.Length >= 0 && line.Start + line.Length <= length, "line past the text");
            Finite(line.Top, line.Ascent, line.Descent, line.Left, line.Width, line.Baseline);
            foreach (LayoutRun run in line.Runs)
            {
                Finite(run.X, run.Advance);
                Check(run.Start >= line.Start && run.Start + run.Length <= length, "run outside its line");
                if (run.Glyphs is GlyphRun glyphs)
                {
                    foreach (Vector2 p in glyphs.Positions)
                    {
                        Finite(p.X, p.Y);
                    }
                }
            }

            next = line.Start + line.Length;
        }

        Check(layout.HiddenRange is not null || next == length || layout.Lines.Count == 0, $"lines end at {next} of {length}");
        foreach (TextBounds box in layout.PlaceholderRects)
        {
            Finite(box.Left, box.Top, box.Right, box.Bottom);
        }

        TextBounds ink = layout.InkBounds;
        Finite(ink.Left, ink.Top, ink.Right, ink.Bottom);
        foreach (DecorationShape shape in layout.Decorations)
        {
            Finite(shape.Top, shape.Thickness);
            foreach (DecorationSegment s in shape.Segments)
            {
                Finite(s.Left, s.Right);
            }
        }

        layout.ToPath(new FiniteSink());
        foreach (DecorationShape shape in layout.Decorations)
        {
            shape.AddTo(new FiniteSink());
        }
    }

    private static void CheckInteraction(TextLayout layout, int length, byte seed)
    {
        Random random = new(seed + 1);
        float w = float.IsFinite(layout.Width) ? layout.Width : 1000;
        for (int i = 0; i < 24; i++)
        {
            Vector2 point = new((float)((random.NextDouble() * (w + 40)) - 20), (float)((random.NextDouble() * (layout.Height + 40)) - 20));
            TextPosition hit = layout.HitTest(point);
            Check(hit.Index >= 0 && hit.Index <= length, $"hit {hit.Index} outside the text");
        }

        int step = Math.Max(1, length / 256);
        for (int index = 0; index <= length; index += step)
        {
            foreach (TextAffinity affinity in new[] { TextAffinity.Downstream, TextAffinity.Upstream })
            {
                Caret caret = layout.GetCaret(new TextPosition(index, affinity));
                Finite(caret.Primary.X, caret.Primary.Top, caret.Primary.Bottom);
            }

            _ = layout.GetLineAt(index);
            TextSegment word = layout.GetWordBoundary(index);
            Check(word.Start >= 0 && word.Start + word.Length <= length, "word boundary outside the text");
            TextSegment line = layout.GetLineBoundary(index);
            Check(line.Start >= 0 && line.Start + line.Length <= length, "line boundary outside the text");
        }

        for (int i = 0; i < 8; i++)
        {
            TextPosition position = new(random.Next(length + 1), random.Next(2) == 0 ? TextAffinity.Downstream : TextAffinity.Upstream);
            foreach (CaretMove move in Enum.GetValues<CaretMove>())
            {
                TextPosition moved = layout.MoveCaret(position, move);
                Check(moved.Index >= 0 && moved.Index <= length, $"{move} moved to {moved.Index}");
            }

            int from = random.Next(length + 1), to = random.Next(length + 1);
            foreach (SelectionHeight height in Enum.GetValues<SelectionHeight>())
            {
                foreach (TextBounds r in layout.GetSelectionRects(Math.Min(from, to), Math.Max(from, to), height))
                {
                    Finite(r.Left, r.Top, r.Right, r.Bottom);
                }
            }
        }
    }

    private static void CheckFlow(Paragraph paragraph, int length, byte seed)
    {
        Random random = new(seed + 2);
        LineFlow flow = paragraph.CreateFlow();
        int next = 0, count = 0;
        while (flow.NextLine(random.Next(4) == 0 ? 0 : random.Next(400)) is TextLine line)
        {
            Check(line.Start == next, $"flowed line starts at {line.Start}, not {next}");
            next = line.Start + line.Length;
            Check(++count <= length + 2, "flow does not end");
        }

        Check(flow.IsComplete, "flow stopped early");
    }

    private static void CheckBreaks(string text, LineBreakOptions options)
    {
        int previous = 0;
        bool any = false;
        foreach (LineBreak b in LineBreaks.Enumerate(text, options))
        {
            Check(b.Index > previous && b.Index <= text.Length, $"break at {b.Index} after {previous}");
            previous = b.Index;
            any = true;
        }

        Check(text.Length == 0 ? !any : previous == text.Length, "breaks do not end at the text's end");
    }

    private static void Finite(params ReadOnlySpan<float> values)
    {
        foreach (float v in values)
        {
            Check(float.IsFinite(v), "a coordinate is not finite");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FiniteSink : IGlyphOutlineSink
    {
        public void MoveTo(float x, float y) => Finite(x, y);

        public void LineTo(float x, float y) => Finite(x, y);

        public void QuadTo(float cx, float cy, float x, float y) => Finite(cx, cy, x, y);

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Finite(c1x, c1y, c2x, c2y, x, y);

        public void Close()
        {
        }
    }
}
