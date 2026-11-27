using System.Numerics;

using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;
using Egami.Pixels;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Hub 2.8.6 stage A: ruby annotations (Build Plan tasks 1 to 5).</summary>
public sealed class RubyTests
{
    // Noto Sans CJK at 20: every kanji and kana is 20 wide, so ruby at the default half size is 10 a character.
    private const float Em = 20;

    private static FontFamily Cjk => ParagraphTests.Family("derived/NotoSansCJK-layout-subset.otf");

    private static ParagraphStyle Style(RubyOptions? ruby = null) => new()
    {
        DefaultStyle = new TextStyle { FontFamilies = [Cjk], Size = Em, Language = "ja" },
        Ruby = ruby ?? RubyOptions.Default,
    };

    /// <summary>The ascent plus descent of the ruby's font at half size: its box's height.</summary>
    private static float RubyLineHeight
    {
        get
        {
            TextLine line = Paragraph.Create("く", Style() with { DefaultStyle = Style().DefaultStyle with { Size = Em / 2 } }).Layout(float.PositiveInfinity).Lines[0];
            return line.Ascent + line.Descent;
        }
    }

    private static TextLayout Layout(string text, RubyAnnotation[] ruby, ParagraphStyle? style = null, float width = float.PositiveInfinity) =>
        Paragraph.Create(new RichText(text, null, null, ruby), style ?? Style()).Layout(width);

    private static TextBounds Bounds(TextLayout layout, int annotation) => layout.GetRubyBounds(annotation);

    /// <summary>The pen x of each glyph of a line's text runs, by cluster.</summary>
    private static Dictionary<int, float> GlyphX(TextLine line)
    {
        Dictionary<int, float> x = [];
        foreach (LayoutRun run in line.Runs)
        {
            if (run.Glyphs is GlyphRun glyphs)
            {
                for (int g = 0; g < glyphs.Count; g++)
                {
                    x[glyphs.Clusters[g]] = glyphs.Positions[g].X;
                }
            }
        }

        return x;
    }

    [Fact]
    public void FontCoversTheTestCharacters()
    {
        string all = "国際価値諸間のかちこくさいしょに";
        TextLine line = Paragraph.Create(all, Style()).Layout(float.PositiveInfinity).Lines[0];
        Assert.Equal(all.Length * Em, line.Width, 3);
        Assert.All(line.Runs, r => Assert.DoesNotContain((ushort)0, r.Glyphs!.Glyphs.ToArray()));
    }

    [Fact]
    public void PlainParagraphsHaveNoAnnotationRuns()
    {
        TextLayout layout = Paragraph.Create("国際", Style()).Layout(float.PositiveInfinity);
        Assert.Empty(layout.Lines[0].AnnotationRuns);
        Assert.Empty(layout.Paragraph.RichText.Ruby);
    }

    [Fact]
    public void MonoRubySitsCentredOverItsBase()
    {
        // 国 (くに): ruby as wide as its base.
        TextLayout layout = Layout("の国の", [new RubyAnnotation(1, 1, "くに")]);
        TextLine line = layout.Lines[0];
        Assert.Equal(3 * Em, line.Width, 3);
        LayoutRun ruby = Assert.Single(line.AnnotationRuns);
        Assert.Equal(LayoutRunKind.Ruby, ruby.Kind);
        Assert.Equal(0, ruby.Annotation);
        Assert.Equal((1, 1), (ruby.Start, ruby.Length));
        Assert.Equal(Em, ruby.X, 3);
        Assert.Equal(Em, ruby.Advance, 3);
        Assert.Equal("くに", ruby.Glyphs!.Text);
        Assert.Equal([0, 1], ruby.Glyphs.Clusters.ToArray());

        // Over the base's em box, inside the line box.
        TextBounds box = Bounds(layout, 0);
        Assert.True(box.Bottom <= line.Baseline - 17.5f, $"{box.Bottom} {line.Baseline}");
        Assert.True(box.Top >= line.Top - 1e-3f);
        Assert.Equal(RubyLineHeight, box.Height, 3);
        Assert.Equal(Em / 2, layout.Paragraph.GetStyle(ruby.StyleIndex).Size);
    }

    [Fact]
    public void GroupRubyWiderThanItsBaseSpreadsTheBase()
    {
        // 国際 with six ruby characters (60) over a base of 40: the base spreads 1:2:1 between kanji neighbours.
        RubyAnnotation[] ruby = [new(1, 2, "こくさいこく")];
        TextLayout layout = Layout("間国際間", ruby);
        TextLine line = layout.Lines[0];
        Assert.Equal(100, line.Width, 3);
        Dictionary<int, float> x = GlyphX(line);
        Assert.Equal(0, x[0], 3);
        Assert.Equal(25, x[1], 3);
        Assert.Equal(55, x[2], 3);
        Assert.Equal(80, x[3], 3);
        TextBounds box = Bounds(layout, 0);
        Assert.Equal(20, box.Left, 3);
        Assert.Equal(80, box.Right, 3);
    }

    [Fact]
    public void GroupRubyAtBothLineEdgesPutsTheSpaceBetween()
    {
        TextLayout layout = Layout("国際", [new(0, 2, "こくさいこく")]);
        TextLine line = layout.Lines[0];
        Assert.Equal(60, line.Width, 3);
        Dictionary<int, float> x = GlyphX(line);
        Assert.Equal(0, x[0], 3);
        Assert.Equal(40, x[1], 3);
    }

    [Fact]
    public void WideRubyOverhangsKanaButNotKanji()
    {
        RubyAnnotation[] ruby = [new(1, 2, "こくさいこく")];

        // Over の on each side: half a ruby character (5) each, so the box is 50 and the ruby reaches 5 past it.
        TextLayout kana = Layout("の国際の", ruby);
        Assert.Equal(90, kana.Lines[0].Width, 3);
        TextBounds box = Bounds(kana, 0);
        Assert.Equal(15, box.Left, 3);
        Assert.Equal(75, box.Right, 3);
        Dictionary<int, float> x = GlyphX(kana.Lines[0]);
        Assert.Equal(22.5f, x[1], 3);
        Assert.Equal(47.5f, x[2], 3);

        // Kanji on one side: only the kana side.
        Assert.Equal(95, Layout("間国際の", ruby).Lines[0].Width, 3);

        // Overhang None: the base spreads to the ruby's width.
        Assert.Equal(100, Layout("の国際の", ruby, Style(new RubyOptions { Overhang = RubyOverhang.None })).Lines[0].Width, 3);
    }

    [Fact]
    public void RubyNeverHangsOverAnotherRuby()
    {
        RubyAnnotation[] ruby = [new(0, 1, "こくさ"), new(1, 1, "の")];
        TextLayout layout = Layout("国の", ruby);

        // The first group is 30 wide over 20 with ruby on its kana neighbour: no overhang.
        Assert.Equal(50, layout.Lines[0].Width, 3);
    }

    [Fact]
    public void RubyKeepsItsBaseTogether()
    {
        // Far too narrow for the base: the group still stays on one line.
        TextLayout layout = Layout("国際間", [new(0, 2, "こくさい")], width: 25);
        Assert.Equal(0, layout.Lines[0].Start);
        Assert.Equal(2, layout.Lines[0].Length);
        Assert.Equal(2, layout.Lines[1].Start);
    }

    [Fact]
    public void JukugoKeepsPerCharacterRubyWhenEachFits()
    {
        // 価値 (か, ち): each ruby fits its base, so each sits over its own character.
        RubyAnnotation[] ruby = [new(0, 1, "か"), new(1, 1, "ち")];
        TextLayout layout = Layout("価値", ruby, Style(new RubyOptions { Merge = RubyMerge.Auto }));
        Assert.Equal(40, layout.Lines[0].Width, 3);
        Assert.Equal(5, Bounds(layout, 0).Left, 3);
        Assert.Equal(25, Bounds(layout, 1).Left, 3);
    }

    [Fact]
    public void JukugoJoinsWhenOneRubyIsTooWide()
    {
        RubyAnnotation[] ruby = [new(0, 1, "か"), new(1, 1, "ちちちち")];

        // Joined: 50 of ruby over 40 of base. Separate: 20 + 40.
        Assert.Equal(50, Layout("価値", ruby, Style(new RubyOptions { Merge = RubyMerge.Auto })).Lines[0].Width, 3);
        Assert.Equal(60, Layout("価値", ruby).Lines[0].Width, 3);
        Assert.Equal(50, Layout("価値", ruby, Style(new RubyOptions { Merge = RubyMerge.Collapse })).Lines[0].Width, 3);

        // Joined ruby runs one after the other over the joined base.
        TextLayout joined = Layout("価値", ruby, Style(new RubyOptions { Merge = RubyMerge.Auto }));
        Assert.Equal(0, Bounds(joined, 0).Left, 3);
        Assert.Equal(10, Bounds(joined, 1).Left, 3);
        Assert.Equal(50, Bounds(joined, 1).Right, 3);
    }

    [Fact]
    public void JukugoBreaksBetweenItsCharactersEachKeepingItsRuby()
    {
        RubyAnnotation[] ruby = [new(0, 1, "か"), new(1, 1, "ちちちち")];
        TextLayout auto = Layout("価値", ruby, Style(new RubyOptions { Merge = RubyMerge.Auto }), 45);
        Assert.Equal(2, auto.Lines.Count);
        Assert.Equal(20, auto.Lines[0].Width, 3);
        Assert.Equal(40, auto.Lines[1].Width, 3);
        Assert.Single(auto.Lines[0].AnnotationRuns);
        Assert.Single(auto.Lines[1].AnnotationRuns);
        Assert.Equal(1, auto.Lines[1].AnnotationRuns[0].Annotation);

        // Collapsed groups never break.
        TextLayout collapsed = Layout("価値", ruby, Style(new RubyOptions { Merge = RubyMerge.Collapse }), 45);
        Assert.Single(collapsed.Lines);
    }

    [Fact]
    public void OptimalFillingKeepsRubyBasesWhole()
    {
        string text = string.Concat(Enumerable.Repeat("諸国間の価値", 6));
        List<RubyAnnotation> ruby = [];
        for (int i = 0; i < text.Length; i += 6)
        {
            ruby.Add(new RubyAnnotation(i, 2, "しょこくさ"));
            ruby.Add(new RubyAnnotation(i + 4, 2, "かち"));
        }

        ParagraphStyle style = Style() with { LineFill = LineFill.Optimal, Align = TextAlign.Justify };
        TextLayout layout = Layout(text, [.. ruby], style, 150);
        Assert.True(layout.Lines.Count > 1);
        foreach (TextLine line in layout.Lines)
        {
            Assert.DoesNotContain(ruby, a => a.Start < line.Start && a.End > line.Start);
            Assert.True(line.Width <= 150 + 1e-3f, $"{line.Width}");
        }

        Assert.Equal(ruby.Count, layout.Lines.Sum(l => l.AnnotationRuns.Count));
    }

    [Fact]
    public void RubyGrowsTheLineUnlessItsHeightHoldsIt()
    {
        TextLayout plain = Paragraph.Create("国", Style()).Layout(float.PositiveInfinity);
        TextLayout over = Layout("国", [new(0, 1, "くに")]);
        Assert.True(over.Lines[0].Ascent > plain.Lines[0].Ascent);
        Assert.Equal(plain.Lines[0].Descent, over.Lines[0].Descent);

        // The ruby sits on the base's ascent, and its top is the line box's top.
        Assert.Equal(plain.Lines[0].Ascent + RubyLineHeight, over.Lines[0].Ascent, 3);
        Assert.Equal(over.Lines[0].Top, Bounds(over, 0).Top, 3);

        TextLayout under = Layout("国", [new(0, 1, "くに") { Position = RubyPosition.Under }]);
        Assert.Equal(plain.Lines[0].Ascent, under.Lines[0].Ascent);
        Assert.True(under.Lines[0].Descent > plain.Lines[0].Descent);
        Assert.True(Bounds(under, 0).Top >= under.Lines[0].Baseline);

        // An offset moves the ruby further out and the line grows with it.
        TextLayout offset = Layout("国", [new(0, 1, "くに") { Offset = 4 }]);
        Assert.Equal(over.Lines[0].Ascent + 4, offset.Lines[0].Ascent, 3);
        Assert.Equal(Bounds(over, 0).Bottom - 4 - over.Lines[0].Baseline, Bounds(offset, 0).Bottom - offset.Lines[0].Baseline, 3);

        // A tall line already holds it.
        ParagraphStyle tall = Style() with { DefaultStyle = Style().DefaultStyle with { LineHeight = LineHeight.Absolute(80) } };
        TextLayout tallPlain = Paragraph.Create("国", tall).Layout(float.PositiveInfinity);
        TextLayout tallRuby = Layout("国", [new(0, 1, "くに")], tall);
        Assert.Equal(tallPlain.Lines[0].Ascent, tallRuby.Lines[0].Ascent);
        Assert.Equal(tallPlain.Lines[0].Height, tallRuby.Lines[0].Height);
    }

    [Fact]
    public void VerticalRubySitsRightForOverAndLeftForUnder()
    {
        ParagraphStyle vertical = Style() with { WritingMode = WritingMode.VerticalRl };
        TextLayout over = Layout("の国際の", [new(1, 2, "こくさい")], vertical);
        TextLine line = over.Lines[0];
        TextBounds box = Bounds(over, 0);
        Assert.True(box.Left >= line.Baseline + 8, $"{box.Left} {line.Baseline}");
        Assert.Equal(20, box.Top, 3);
        Assert.Equal(60, box.Bottom, 3);
        LayoutRun run = Assert.Single(line.AnnotationRuns);
        Assert.All(run.Glyphs!.Positions.ToArray(), p => Assert.True(p.X > line.Baseline));

        TextLayout under = Layout("の国際の", [new(1, 2, "こくさい") { Position = RubyPosition.Under }], vertical);
        Assert.True(Bounds(under, 0).Right <= under.Lines[0].Baseline - 8);

        // The line is thicker for the ruby, on its over side.
        TextLayout plain = Paragraph.Create("の国際の", vertical).Layout(float.PositiveInfinity);
        Assert.True(line.Height > plain.Lines[0].Height);
    }

    [Fact]
    public void VerticalWideRubySpreadsTheBaseDownTheLine()
    {
        ParagraphStyle vertical = Style() with { WritingMode = WritingMode.VerticalRl };
        TextLayout layout = Layout("間国際間", [new(1, 2, "こくさいこく")], vertical);
        Assert.Equal(100, layout.Lines[0].Width, 3);
        Assert.Equal(100, layout.Height, 3);
    }

    [Fact]
    public void InterCharacterRubyStandsInAColumnRightOfItsBase()
    {
        RubyAnnotation[] ruby = [new(0, 1, "こく") { Position = RubyPosition.InterCharacter }];
        TextLayout layout = Layout("国間", ruby);
        TextLine line = layout.Lines[0];

        // The base's advance grows by the ruby's column (its size, 10), and the column stands right of the base.
        Assert.Equal(50, line.Width, 3);
        Assert.Equal(30, GlyphX(line)[1], 3);
        TextBounds box = Bounds(layout, 0);
        Assert.Equal(20, box.Left, 3);
        Assert.Equal(30, box.Right, 3);
        Assert.Equal(20, box.Height, 3);

        // In vertical lines inter-character ruby is over ruby.
        TextLayout vertical = Layout("国間", ruby, Style() with { WritingMode = WritingMode.VerticalRl });
        Assert.True(Bounds(vertical, 0).Left > vertical.Lines[0].Baseline);
    }

    [Fact]
    public void AlignmentPlacesShortRuby()
    {
        // One ruby character (10) over a base of 40.
        static float Left(RubyAlign align) => Bounds(Layout("間国際間", [new(1, 2, "く") { Align = align }]), 0).Left;
        Assert.Equal(20, Left(RubyAlign.Start), 3);
        Assert.Equal(50, Left(RubyAlign.End), 3);
        Assert.Equal(35, Left(RubyAlign.Center), 3);
        Assert.Equal(35, Left(RubyAlign.SpaceAround), 3);
        Assert.Equal(35, Left(RubyAlign.SpaceBetween), 3);

        // Two characters: space between puts them at the base's ends; space around 1:2:1.
        TextLayout between = Layout("間国際間", [new(1, 2, "くに") { Align = RubyAlign.SpaceBetween }]);
        Vector2[] b = between.Lines[0].AnnotationRuns[0].Glyphs!.Positions.ToArray();
        Assert.Equal(20, b[0].X, 3);
        Assert.Equal(50, b[1].X, 3);
        TextLayout around = Layout("間国際間", [new(1, 2, "くに")]);
        Vector2[] a = around.Lines[0].AnnotationRuns[0].Glyphs!.Positions.ToArray();
        Assert.Equal(25, a[0].X, 3);
        Assert.Equal(45, a[1].X, 3);
    }

    [Fact]
    public void JustificationLeavesRubyGroupsAlone()
    {
        string text = "諸国間の価値の諸国間の価値の";
        RubyAnnotation[] ruby = [new(4, 2, "かち")];
        ParagraphStyle style = Style() with { Align = TextAlign.Justify };
        TextLayout layout = Layout(text, ruby, style, 210);
        TextLine first = layout.Lines[0];
        Assert.True(layout.Lines.Count > 1);
        Assert.Equal(210, first.Width, 3);
        Dictionary<int, float> x = GlyphX(first);
        Assert.Equal(Em, x[5] - x[4], 3);
        Assert.True(x[1] - x[0] > Em + 0.5f);
        TextBounds box = Bounds(layout, 0);
        Assert.Equal(x[4] + 5, box.Left, 3);
    }

    [Fact]
    public void HyphenationNeverBreaksARubyBase()
    {
        string text = "Typographers traditionally hyphenate justified paragraphs, because otherwise extraordinarily long words leave rivers.";
        int start = text.IndexOf("extraordinarily", StringComparison.Ordinal);
        RubyAnnotation[] ruby = [new(start, "extraordinarily".Length, "very"), new(text.IndexOf("hyphenate", StringComparison.Ordinal), 9, "split")];
        ParagraphStyle style = AutoHyphenTests.Style();
        foreach (float width in new float[] { 90, 110, 130, 150 })
        {
            TextLayout layout = Paragraph.Create(new RichText(text, null, null, ruby), style).Layout(width);
            foreach (TextLine line in layout.Lines)
            {
                Assert.DoesNotContain(ruby, a => a.Start < line.Start && a.End > line.Start);
            }

            Assert.Equal(2, layout.Lines.Sum(l => l.AnnotationRuns.Count));
        }

        // A soft hyphen inside a base is no break either.
        TextLayout soft = Paragraph.Create(new RichText("hy­phen", null, null, [new RubyAnnotation(0, 7, "x")]), style with { Hyphens = Hyphens.Manual }).Layout(10);
        Assert.Single(soft.Lines);
    }

    [Fact]
    public void BidiKeepsARubyBaseInOneRun()
    {
        // A base of a Latin letter and a Hebrew one in a right-to-left paragraph: laid out at the first letter's level, side by side.
        ParagraphStyle style = ParagraphTests.Style("DejaVuSans.ttf") with { Direction = TextDirection.RightToLeft };
        string text = "אב aג ד";
        TextLayout layout = Paragraph.Create(new RichText(text, null, null, [new RubyAnnotation(3, 2, "xy")]), style).Layout(float.PositiveInfinity);
        TextLine line = layout.Lines[0];
        List<LayoutRun> runs = [.. line.Runs];
        int[] baseRuns = [.. Enumerable.Range(0, runs.Count).Where(i => runs[i].Start < 5 && runs[i].Start + runs[i].Length > 3)];
        Assert.Equal(baseRuns.Length, baseRuns[^1] - baseRuns[0] + 1);
        Assert.All(baseRuns, i => Assert.Equal(runs[baseRuns[0]].BidiLevel, runs[i].BidiLevel));
        float left = runs[baseRuns[0]].X, right = runs[baseRuns[^1]].X + runs[baseRuns[^1]].Advance;
        TextBounds box = layout.GetRubyBounds(0);
        Assert.Equal((left + right) / 2, (box.Left + box.Right) / 2, 2);
    }

    [Fact]
    public void LatinRubyOverLatinBase()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        TextLayout layout = Paragraph.Create(new RichText("a word here", null, null, [new RubyAnnotation(2, 4, "gloss")]), style).Layout(float.PositiveInfinity);
        LayoutRun run = Assert.Single(layout.Lines[0].AnnotationRuns);
        Assert.Equal(5, run.Glyphs!.Count);
        Assert.Equal(8, layout.Paragraph.GetStyle(run.StyleIndex).Size);
    }

    [Fact]
    public void HitTestingAndCaretsStayOnTheBase()
    {
        TextLayout layout = Layout("の国際の", [new(1, 2, "こくさいこく")]);
        TextBounds box = Bounds(layout, 0);
        TextPosition hit = layout.HitTest(new Vector2((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2));
        Assert.InRange(hit.Index, 1, 3);
        Assert.Equal(4, layout.MoveCaret(new TextPosition(3), CaretMove.NextGrapheme).Index);
        Assert.Equal(20, layout.GetCaret(new TextPosition(1)).Primary.X, 3);
    }

    [Fact]
    public void RubyStyleInheritsTheBaseAndTakesItsOwn()
    {
        TextStyle red = new() { Color = new Rgba8(255, 0, 0, 255), Decoration = new TextDecoration(), Background = new Rgba8(0, 0, 255, 255) };
        RichText text = new RichTextBuilder()
            .Push(red).Append("国際").Pop()
            .AddRuby(0, 1, "くに")
            .AddRuby(new RubyAnnotation(1, 1, "さい") { Style = new TextStyle { Size = 6, Color = new Rgba8(0, 128, 0, 255) } })
            .ToRichText();
        TextLayout layout = Paragraph.Create(text, Style()).Layout(float.PositiveInfinity);
        IReadOnlyList<LayoutRun> runs = layout.Lines[0].AnnotationRuns;
        TextStyle first = layout.Paragraph.GetStyle(runs[0].StyleIndex);
        TextStyle second = layout.Paragraph.GetStyle(runs[1].StyleIndex);
        Assert.Equal(new Rgba8(255, 0, 0, 255), first.Color);
        Assert.Equal(10, first.Size);
        Assert.Null(first.Decoration);
        Assert.Null(first.Background);
        Assert.Equal(new Rgba8(0, 128, 0, 255), second.Color);
        Assert.Equal(6, second.Size);

        // The size ratio applies where the annotation sets no size.
        TextLayout third = Paragraph.Create(text, Style(new RubyOptions { SizeRatio = 0.4f })).Layout(float.PositiveInfinity);
        Assert.Equal(8, third.Paragraph.GetStyle(third.Lines[0].AnnotationRuns[0].StyleIndex).Size);
    }

    [Fact]
    public void LineLimitsAndEllipsesKeepRubyOnKeptLines()
    {
        string text = "諸国間の価値の諸国間の価値の";
        RubyAnnotation[] ruby = [new(0, 2, "しょこく"), new(11, 2, "かち")];
        TextLayout layout = Layout(text, ruby, Style() with { MaxLines = 1 }, 150);
        Assert.True(layout.DidExceed);
        Assert.True(layout.Lines[0].Ellipsized);
        Assert.False(Bounds(layout, 0).IsEmpty);
        Assert.True(Bounds(layout, 1).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetRubyBounds(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetRubyBounds(-1));
    }

    [Fact]
    public void EmptyRubyTextKeepsItsBaseTogetherAndDrawsNothing()
    {
        TextLayout layout = Layout("国際", [new(0, 2, string.Empty)], width: 25);
        Assert.Single(layout.Lines);
        Assert.Empty(layout.Lines[0].AnnotationRuns);
        Assert.True(Bounds(layout, 0).IsEmpty);
    }

    [Fact]
    public void InkBoundsHoldTheRuby()
    {
        TextLayout plain = Paragraph.Create("国", Style()).Layout(float.PositiveInfinity);
        TextLayout ruby = Layout("国", [new(0, 1, "くに")]);
        Assert.True(ruby.InkBounds.Top < ruby.Lines[0].Baseline - 18);
        Assert.True(plain.InkBounds.Top > plain.Lines[0].Baseline - 18);
    }

    [Fact]
    public void IntrinsicWidthsCountRuby()
    {
        TextLayout layout = Layout("間国際間", [new(1, 2, "こくさいこく")]);
        Assert.Equal(60, layout.MinIntrinsicWidth, 3);
        Assert.Equal(100, layout.MaxIntrinsicWidth, 3);
    }

    [Fact]
    public void BuilderMatchesTheConstructor()
    {
        RichText built = new RichTextBuilder().Append("国際").AddRuby(1, 1, "さい").AddRuby(0, 1, "こく").ToRichText();
        RichText hand = new("国際", null, null, [new RubyAnnotation(0, 1, "こく"), new RubyAnnotation(1, 1, "さい")]);
        Assert.Equal(hand.Ruby, built.Ruby);
        Assert.Equal(0, built.Ruby[0].Start);
        Assert.Equal(2, new RubyAnnotation(1, 1, "x").End);
    }

    [Fact]
    public void InvalidRubyIsRejected()
    {
        static RichText Make(string text, params RubyAnnotation[] ruby) => new(text, null, null, ruby);
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(0, 0, "x")));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(-1, 1, "x")));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(2, 2, "x")));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(0, 2, "x"), new RubyAnnotation(1, 2, "y")));
        Assert.Throws<ArgumentException>(() => Make("a😀b", new RubyAnnotation(0, 2, "x")));
        Assert.Throws<ArgumentException>(() => Make("a😀b", new RubyAnnotation(2, 2, "x")));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(0, 1, "￼")));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(0, 1, "x") { Position = (RubyPosition)7 }));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(0, 1, "x") { Align = (RubyAlign)9 }));
        Assert.Throws<ArgumentException>(() => Make("abc", new RubyAnnotation(0, 1, "x") { Offset = float.NaN }));
        Assert.Throws<ArgumentNullException>(() => Make("abc", new RubyAnnotation(0, 1, null!)));
        Assert.Throws<ArgumentNullException>(() => new RichTextBuilder().AddRuby(0, 1, null!));
        Assert.Throws<ArgumentNullException>(() => new RichTextBuilder().AddRuby(null!));
        Assert.Throws<ArgumentException>(() => new RichTextBuilder().Append("a").AddRuby(0, 2, "x").ToRichText());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RubyOptions { SizeRatio = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RubyOptions { SizeRatio = float.PositiveInfinity });
        Assert.Throws<ArgumentNullException>(() => new ParagraphStyle { Ruby = null! });

        // Adjacent annotations and a surrogate pair kept whole are fine.
        Assert.Equal(2, Make("ab😀", new RubyAnnotation(0, 1, "x"), new RubyAnnotation(1, 3, "y")).Ruby.Count);
    }

    [Fact]
    public void RandomRubyNeverBreaksLayout()
    {
        Random random = new(2861);
        string chars = "諸国間の価値、 a";
        for (int round = 0; round < 200; round++)
        {
            int n = random.Next(1, 30);
            string text = new([.. Enumerable.Range(0, n).Select(_ => chars[random.Next(chars.Length)])]);
            List<RubyAnnotation> ruby = [];
            for (int i = 0; i < n;)
            {
                int length = random.Next(1, 4);
                if (i + length <= n && random.Next(2) == 0)
                {
                    ruby.Add(new RubyAnnotation(i, length, new string('か', random.Next(0, 6)))
                    {
                        Position = (RubyPosition)random.Next(3),
                        Align = (RubyAlign)random.Next(5),
                    });
                }

                i += length;
            }

            ParagraphStyle style = Style(new RubyOptions { Merge = (RubyMerge)random.Next(3), Overhang = (RubyOverhang)random.Next(2) }) with
            {
                WritingMode = (WritingMode)random.Next(3),
                Align = (TextAlign)random.Next(6),
                LineFill = (LineFill)random.Next(3),
                LineBreaking = new LineBreakOptions(OverflowWrap: (OverflowWrap)random.Next(3)),
            };
            TextLayout layout = Layout(text, [.. ruby], style, random.Next(10, 200));
            Assert.Equal(text.Length, layout.Lines.Sum(l => l.Length));
            foreach (TextLine line in layout.Lines)
            {
                Assert.DoesNotContain(ruby, a => a.Start < line.Start && a.End > line.Start);
                Assert.All(line.AnnotationRuns, r => Assert.True(float.IsFinite(r.X) && float.IsFinite(r.Advance)));
            }

            for (int a = 0; a < ruby.Count; a++)
            {
                Assert.True(layout.GetRubyBounds(a) is var b && float.IsFinite(b.Left) && float.IsFinite(b.Bottom));
            }
        }
    }
}
