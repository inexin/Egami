using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class ParagraphLayout
{
    [Fact]
    public void LineBreakOpportunities()
    {
        #region line-breaks
        // Where lines may end: after spaces and hyphens, and always at the end; a line must end after a newline.
        List<int> breaks = [];
        foreach (LineBreak b in LineBreaks.Enumerate("A well-known fact.\nNext"))
        {
            breaks.Add(b.Index);                       // 2, 7, 13, 19 (mandatory), 23
        }

        // CSS switches: break-all lets letters break like ideographs.
        LineBreakOptions breakAll = new(WordBreak: WordBreak.BreakAll);
        int count = 0;
        foreach (LineBreak b in LineBreaks.Enumerate("word", breakAll))
        {
            count++;                                   // 4: after every letter
        }
        #endregion

        Assert.Equal([2, 7, 13, 19, 23], breaks);
        Assert.Equal(4, count);
    }
    [Fact]
    public void RichTextParagraph()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        #region rich-text
        // Font families come from a collection; later families cover what the first lacks.
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSansArabic-Regular.ttf"));
        fonts.TryGetFamily("Noto Sans", out FontFamily latin);
        fonts.TryGetFamily("Noto Sans Arabic", out FontFamily arabic);

        // Styles nest: the inner span keeps the outer one's size and adds a color and an underline.
        RichText text = new RichTextBuilder()
            .Append("Peace is ")
            .Push(new TextStyle { Size = 20 })
            .Append("\u0633\u0644\u0627\u0645 ")
            .Push(new TextStyle { Color = new Rgba8(200, 30, 30, 255), Decoration = new TextDecoration() })
            .Append("in Arabic")
            .Pop()
            .Pop()
            .AddPlaceholder(new Placeholder(16, 16, PlaceholderAlignment.Middle))
            .ToRichText();

        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [latin, arabic], Size = 16, LineHeight = LineHeight.Multiple(1.4f) },
            Align = TextAlign.Justify,
            LineBreaking = new LineBreakOptions(WordBreak: WordBreak.Normal),
        };

        // Shaped once: lay it out at any width later.
        Paragraph paragraph = Paragraph.Create(text, style);
        #endregion

        Assert.Equal(text.Text, paragraph.Text);
        Assert.Equal(2, text.Spans.Count);
    }

    [Fact]
    public void LayoutAtAWidth()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        fonts.TryGetFamily("Noto Sans", out FontFamily family);
        #region layout
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [family], Size = 16 } };
        Paragraph paragraph = Paragraph.Create("A paragraph is shaped once and laid out at any width.", style);

        // Each width gives an immutable layout: lines of glyph runs, positioned from the box's top-left corner.
        TextLayout narrow = paragraph.Layout(120);
        TextLayout wide = paragraph.Layout(400);
        foreach (TextLine line in narrow.Lines)
        {
            string text = paragraph.Text[line.Start..line.End];   // the line's text, its trailing space included
            float baseline = line.Baseline;                        // y of the baseline
            foreach (LayoutRun run in line.Runs)
            {
                GlyphRun? glyphs = run.Glyphs;                     // glyphs at their layout positions
            }
        }

        // No wrapping: the box is as wide as the longest line.
        TextLayout unbounded = paragraph.Layout(float.PositiveInfinity);
        #endregion

        Assert.True(narrow.Lines.Count > wide.Lines.Count);
        Assert.Single(unbounded.Lines);
    }

    [Fact]
    public void FlowAcrossColumns()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        fonts.TryGetFamily("Noto Sans", out FontFamily family);
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [family], Size = 12 } };
        Paragraph paragraph = Paragraph.Create(string.Join(' ', Enumerable.Repeat("Text flows from one column into the next.", 8)), style);
        #region flow
        // One line at a time, each at its own width: two columns of four lines, the second wrapping around an image.
        LineFlow flow = paragraph.CreateFlow();
        List<TextLine> first = [], second = [];
        while (first.Count < 4 && flow.NextLine(150) is TextLine line)
        {
            first.Add(line);
        }

        while (flow.NextLine(second.Count < 2 ? 90 : 150) is TextLine line)
        {
            second.Add(line);                                     // narrower beside the image
        }
        #endregion

        Assert.Equal(4, first.Count);
        Assert.True(flow.IsComplete);
    }

    [Fact]
    public void CaretsAndSelection()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        fonts.TryGetFamily("Noto Sans", out FontFamily family);
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [family] } };
        TextLayout layout = Paragraph.Create("Click anywhere in this text to place a caret.", style).Layout(160);
        #region carets
        // A click: the nearest caret position, then where to draw the caret.
        TextPosition position = layout.HitTest(new System.Numerics.Vector2(40, 30));
        Caret caret = layout.GetCaret(position);
        CaretLine bar = caret.Primary;                     // x, top and bottom; Secondary at a bidi boundary

        // Arrow keys: logical moves, and line moves that keep the column.
        TextPosition right = layout.MoveCaret(position, CaretMove.NextGrapheme);
        TextPosition down = layout.MoveCaret(position, CaretMove.LineDown, preferredX: bar.X);

        // A selection: rectangles to fill, one per visually contiguous piece on each line.
        IReadOnlyList<TextBounds> highlight = layout.GetSelectionRects(position.Index, down.Index);
        Egami.Fonts.Unicode.TextSegment word = layout.GetWordBoundary(position.Index); // double-click
        #endregion

        Assert.True(highlight.Count >= 1);
        Assert.True(right.Index > position.Index);
        Assert.True(word.Length > 0);
    }

    [Fact]
    public void Decorations()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        fonts.TryGetFamily("Noto Sans", out FontFamily family);
        #region decorations
        TextStyle underlined = new()
        {
            FontFamilies = [family],
            Decoration = new TextDecoration { Lines = TextDecorationLines.Underline, Style = TextDecorationStyle.Wavy },
        };
        TextLayout layout = Paragraph.Create("Typography, quickly", new ParagraphStyle { DefaultStyle = underlined }).Layout(400);

        foreach (DecorationShape line in layout.Decorations)
        {
            // Top and Thickness place the line; Segments skip the descenders of y, p, g and q.
            System.Console.WriteLine($"{line.Line} at {line.Top}: {line.Segments.Count} pieces");
        }

        // Or everything as outlines, to fill with any path renderer.
        PathRecorder path = new();
        layout.ToPath(path);
        #endregion

        Assert.True(layout.Decorations[0].Segments.Count > 1);
        Assert.True(path.Count > 0);
    }

    [Fact]
    public void DrawParagraphs()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        fonts.TryGetFamily("Noto Sans", out FontFamily family);
        using Raster<Rgba8> image = Raster.Create<Rgba8>(320, 200);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        #region draw
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle
            {
                FontFamilies = [family],
                Size = 18,
                Color = new Rgba8(30, 30, 60, 255),
                Shadows = [new TextShadow(new System.Numerics.Vector2(1, 1), 2, new Rgba8(0, 0, 0, 80))],
            },
            Align = TextAlign.Justify,
        };

        // One call: wrap into a box, centred across the lines.
        TextLayout boxed = canvas.DrawText("Paragraphs wrap, justify and drop shadows.", new RectF(10, 10, 300, 80), style, VerticalAlign.Center);

        // Or lay out once and draw where needed, skipping lines outside the visible part.
        TextLayout layout = Paragraph.Create("Laid out once, drawn many times.", style).Layout(300);
        canvas.DrawTextLayout(layout, new System.Numerics.Vector2(10, 110), visibleRect: new RectF(0, 0, 320, 200));

        // Inline placeholders (an icon, a badge) are drawn by a painter called with each one's box.
        canvas.DrawTextLayout(layout, new System.Numerics.Vector2(10, 130), placeholderPainter: (c, index, box) => c.FillRoundRect(new RoundRect(box, 3), new Rgba8(200, 60, 40, 255)));

        // The same text as one path: glyphs and decorations.
        Egami.Drawing.Path outline = layout.ToPath(new System.Numerics.Vector2(10, 150));
        #endregion

        Assert.True(boxed.Lines.Count >= 1);
        Assert.False(outline.IsEmpty);
    }

    private static FontFamily Noto()
    {
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf"));
        return fonts.Families[0];
    }

    [Fact]
    public void CaptionInABox()
    {
        FontFamily family = Noto();
        using Raster<Rgba8> image = Raster.Create<Rgba8>(400, 300);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        #region caption-box
        // A caption wrapped into the bottom of a photo, centred, on a translucent band as tall as the lines.
        RectF box = new(20, 200, 360, 80);
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [family], Size = 15, Color = Rgba8.White },
            Align = TextAlign.Center,
            MaxLines = 3,
        };
        TextLayout layout = Paragraph.Create("Harbour at dawn, seen from the old lighthouse; the fishing boats leave before the ferries.", style).Layout(box.Width);
        float top = box.Y + box.Height - layout.Height;
        canvas.FillRect(new RectF(box.X - 8, top - 6, box.Width + 16, layout.Height + 12), new SolidPaint(new Rgba8(0, 0, 0, 140)));
        canvas.DrawTextLayout(layout, new System.Numerics.Vector2(box.X, top));
        #endregion

        Assert.True(layout.Lines.Count >= 2);
    }

    [Fact]
    public void JustifiedArticleColumn()
    {
        FontFamily family = Noto();
        using Raster<Rgba8> image = Raster.Create<Rgba8>(300, 600);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        string[] paragraphs =
        [
            "Typesetting a column means choosing where lines end. Greedy filling takes as many words as fit; balanced filling evens the lines out.",
            "Justified text stretches the spaces of every line but the last, and hyphenates at soft hyphens (ex\u00ADam\u00ADple) when a word would leave a gap.",
        ];
        #region article-column
        // A justified column: each paragraph balanced, indented by its first line, with space between paragraphs.
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [family], Size = 13, LineHeight = LineHeight.Multiple(1.4f) },
            Align = TextAlign.Justify,
            LineFill = LineFill.Balanced,
        };
        float y = 10;
        foreach (string text in paragraphs)
        {
            TextLayout layout = Paragraph.Create(text, style).Layout(260);
            canvas.DrawTextLayout(layout, new System.Numerics.Vector2(20, y));
            y += layout.Height + 8;
        }
        #endregion

        Assert.True(y > 100);
    }

    [Fact]
    public void KashidaJustifiedArabic()
    {
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansArabic-Regular.ttf"));
        FontFamily arabic = fonts.Families[0];
        using Raster<Rgba8> image = Raster.Create<Rgba8>(300, 200);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        string text = "\u0644\u0645\u0651\u0627 \u0643\u0627\u0646 \u0627\u0644\u0627\u0639\u062A\u0631\u0627\u0641 \u0628\u0627\u0644\u0643\u0631\u0627\u0645\u0629 \u0627\u0644\u0645\u062A\u0623\u0635\u0644\u0629 "
            + "\u0641\u064A \u062C\u0645\u064A\u0639 \u0623\u0639\u0636\u0627\u0621 \u0627\u0644\u0623\u0633\u0631\u0629 \u0627\u0644\u0628\u0634\u0631\u064A\u0629 \u0648\u0628\u062D\u0642\u0648\u0642\u0647\u0645 \u0627\u0644\u0645\u062A\u0633\u0627\u0648\u064A\u0629";
        #region kashida
        // Justified Arabic lengthens words with tatweels (kashida) before it widens spaces, as Word does.
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [arabic], Size = 16 },
            Align = TextAlign.Justify,
            Justify = TextJustify.Kashida,
            Kashida = KashidaLevel.Medium,             // up to two tatweels a word; Low one, High four
        };
        TextLayout layout = Paragraph.Create(text, style).Layout(260);
        canvas.DrawTextLayout(layout, new System.Numerics.Vector2(20, 10));
        #endregion

        Assert.True(layout.Lines.Count >= 2);
        Assert.Equal(260, layout.Lines[0].Width, 2);
    }

    [Fact]
    public void ChatBubble()
    {
        FontFamily text = Noto();
        FontCollection emojiFonts = new();
        emojiFonts.AddFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoColorEmoji-COLRv1-subset.ttf"));
        FontFamily emoji = emojiFonts.Families[0];
        using Raster<Rgba8> image = Raster.Create<Rgba8>(320, 140);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        #region chat-bubble
        // A chat bubble: at most two lines, cut with an ellipsis; the bubble shrinks to the longest line, so short messages get
        // short bubbles. Emoji come from the second family, character by character.
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [text, emoji], Size = 15, Color = Rgba8.White },
            MaxLines = 2,
        };
        TextLayout message = Paragraph.Create("See you at the caf\u00E9 at eight? I will bring the maps, the snacks and the long list of places we still have not seen \U0001F389", style).Layout(240);
        RoundRect bubble = new(new RectF(12, 12, message.LongestLine + 24, message.Height + 16), 14);
        canvas.FillRoundRect(bubble, new SolidPaint(new Rgba8(0, 120, 255, 255)));
        canvas.DrawTextLayout(message, new System.Numerics.Vector2(24, 20));
        bool cut = message.DidExceed;                       // true: offer "show more"
        #endregion

        Assert.True(cut);
        Assert.True(message.Lines[^1].Ellipsized);
    }

    [Fact]
    public void JapaneseVerticalText()
    {
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansCJK-layout-subset.otf"));
        FontFamily family = fonts.Families[0];
        using Raster<Rgba8> image = Raster.Create<Rgba8>(240, 320);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        #region vertical-japanese
        // Japanese set vertically, columns right to left: the width given to Layout is the column height, and DrawText into a
        // rectangle puts the first column at its right edge.
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [family], Size = 18, Language = "ja" },
            WritingMode = WritingMode.VerticalRl,
            LineBreaking = new LineBreakOptions(Strictness: LineBreakStrictness.Strict),
        };
        TextLayout layout = canvas.DrawText("\u8AF8\u56FD\u9593\u306E\u53CB\u597D\u95A2\u4FC2\u306E\u767A\u5C55\u3092\u4FC3\u9032\u3059\u308B\u3053\u3068\u304C\u809D\u8981\u3067\u3042\u308B\u306E\u3067\u3001\u56FD\u969B\u9023\u5408\u306E\u8AF8\u56FD\u6C11\u306F\u3001\u56FD\u9023\u61B2\u7AE0\u306B\u304A\u3044\u3066\u3001\u57FA\u672C\u7684\u4EBA\u6A29\u3001\u4EBA\u9593\u306E\u5C0A\u53B3\u53CA\u3073\u4FA1\u5024\u4E26\u3073\u306B\u7537\u5973\u306E\u540C\u6A29\u306B\u3064\u3044\u3066\u306E\u4FE1\u5FF5\u3092\u518D\u78BA\u8A8D\u3057", new RectF(10, 10, 220, 300), style);
        #endregion

        Assert.True(layout.Lines.Count >= 3);
        Assert.Equal(300, layout.Height);
    }

    [Fact]
    public void TwoColumnPage()
    {
        FontFamily family = Noto();
        string article = string.Join(" ", Enumerable.Repeat("Flowing text continues from the foot of one column to the head of the next, line by line, at whatever width each place allows.", 6));
        using PictureRecorder page = new(new RectF(0, 0, 595, 842));
        #region two-columns
        // One article flowed down two columns of a page: LineFlow hands out one line at a time at the width asked for, so the
        // first column can be narrowed beside a picture and the rest carries on in the second. On Egap's PDF canvas the same
        // calls write searchable text.
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [family], Size = 10 }, Align = TextAlign.Justify };
        LineFlow flow = Paragraph.Create(article, style).CreateFlow();
        (float X, float Top, float Bottom)[] columns = [(40, 60, 300), (310, 60, 300)];
        foreach ((float x, float top, float bottom) in columns)
        {
            float y = top;
            while (!flow.IsComplete)
            {
                float width = x == 40 && y < 160 ? 150 : 245;      // beside a picture at the top of column one
                TextLine line = flow.NextLine(width)!;
                if (y + line.Height > bottom)
                {
                    flow.Rewind(line.Start);                        // that line goes to the next column
                    break;
                }

                foreach (LayoutRun run in line.Runs)
                {
                    if (run.Glyphs is { Count: > 0 } glyphs)
                    {
                        page.DrawGlyphRun(glyphs, new System.Numerics.Vector2(x, y), new SolidPaint(Rgba8.Black));
                    }
                }

                y += line.Height;
            }
        }
        #endregion

        Assert.True(flow.IsComplete);
    }

    private sealed class PathRecorder : IGlyphOutlineSink
    {
        public int Count { get; private set; }

        public void MoveTo(float x, float y) => Count++;

        public void LineTo(float x, float y) => Count++;

        public void QuadTo(float cx, float cy, float x, float y) => Count++;

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Count++;

        public void Close() => Count++;
    }
}
