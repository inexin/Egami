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
}
