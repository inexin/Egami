using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Hub 2.8.6 stage B: emphasis marks and tate-chu-yoko (Build Plan tasks 6 to 8).</summary>
public sealed class EmphasisTests
{
    // Noto Sans CJK at 20: every kanji and kana is 20 wide. The subset has no mark glyphs, so the marks fall back to DejaVu Sans.
    private const float Em = 20;

    private static readonly TextEmphasis Dots = new() { Mark = EmphasisMark.Dot };

    private static FontFamily Cjk => ParagraphTests.Family("derived/NotoSansCJK-layout-subset.otf");

    private static FontFamily DejaVu => ParagraphTests.Family("DejaVuSans.ttf");

    private static ParagraphStyle Style(TextEmphasis? emphasis = null, TextCombine? combine = null, WritingMode mode = WritingMode.HorizontalTb) => new()
    {
        DefaultStyle = new TextStyle { FontFamilies = [Cjk, DejaVu], Size = Em, Language = "ja", Emphasis = emphasis, CombineUpright = combine },
        WritingMode = mode,
    };

    private static TextLayout Layout(string text, ParagraphStyle style, float width = float.PositiveInfinity) => Paragraph.Create(text, style).Layout(width);

    private static TextLayout Vertical(string text, TextCombine combine, TextEmphasis? emphasis = null, float height = float.PositiveInfinity) =>
        Layout(text, Style(emphasis, combine, WritingMode.VerticalRl), height);

    private static LayoutRun[] Marks(TextLine line) => [.. line.AnnotationRuns.Where(r => r.Kind == LayoutRunKind.Emphasis)];

    private static int MarkCount(TextLayout layout) => layout.Lines.Sum(l => Marks(l).Sum(r => r.Glyphs!.Count));

    /// <summary>The advance of a mark at half size: a line of just the mark's text in DejaVu Sans.</summary>
    private static float MarkAdvance(string mark) =>
        Paragraph.Create(mark, new ParagraphStyle { DefaultStyle = new TextStyle { FontFamilies = [DejaVu], Size = Em / 2 } }).Layout(float.PositiveInfinity).Lines[0].Width;

    [Fact]
    public void FontsCoverTheTestCharacters()
    {
        TextLine line = Layout("国際価値する、。平年年友好だ123!?abc•◦●○◉◎▲△", Style()).Lines[0];
        Assert.All(line.Runs, r => Assert.DoesNotContain((ushort)0, r.Glyphs!.Glyphs.ToArray()));
    }

    [Fact]
    public void PlainParagraphsHaveNoMarks()
    {
        Assert.Empty(Layout("国際", Style()).Lines[0].AnnotationRuns);
    }

    [Fact]
    public void OneMarkGoesCentredOverEachCharacter()
    {
        TextLayout layout = Layout("国際価値", Style(Dots));
        TextLine line = layout.Lines[0];
        LayoutRun run = Assert.Single(Marks(line));
        Assert.Equal(0, run.Start);
        Assert.Equal(4, run.Length);
        GlyphRun marks = run.Glyphs!;
        Assert.Equal(4, marks.Count);

        // Each piece of the line has its own run of marks: kanji and kana shape apart.
        Assert.Equal([0, 2], Marks(Layout("国際する", Style(Dots)).Lines[0]).Select(r => r.Start));
        Assert.Null(marks.Text);
        Assert.Equal(Em / 2, marks.Font.Size);

        float advance = MarkAdvance("•");
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((i * Em) + (Em / 2), marks.Positions[i].X + (advance / 2), 2);
            Assert.True(marks.Positions[i].Y < line.Baseline - (Em / 2), $"{marks.Positions[i].Y} {line.Baseline}");
        }

        // The text itself is where it was.
        Assert.Equal(4 * Em, line.Width, 3);
    }

    [Fact]
    public void SpacesControlsAndPunctuationGetNoMark()
    {
        Assert.Equal(4, MarkCount(Layout("国際、する。", Style(Dots))));
        Assert.Equal(3, MarkCount(Layout("ab c!", Style(Dots))));
        Assert.Equal(3, MarkCount(Layout("a\tb c", Style(Dots))));
    }

    [Fact]
    public void MarksFollowTheStyleSpans()
    {
        RichText text = new("国際価値", [new StyleSpan(1, 2, new TextStyle { Emphasis = Dots })]);
        TextLayout layout = Paragraph.Create(text, Style()).Layout(float.PositiveInfinity);
        LayoutRun run = Assert.Single(Marks(layout.Lines[0]));
        Assert.Equal(1, run.Start);
        Assert.Equal(2, run.Length);
        Assert.Equal(2, run.Glyphs!.Count);
    }

    [Theory]
    [InlineData(EmphasisMark.Dot, true, "•")]
    [InlineData(EmphasisMark.Dot, false, "◦")]
    [InlineData(EmphasisMark.Circle, true, "●")]
    [InlineData(EmphasisMark.Circle, false, "○")]
    [InlineData(EmphasisMark.DoubleCircle, true, "◉")]
    [InlineData(EmphasisMark.DoubleCircle, false, "◎")]
    [InlineData(EmphasisMark.Triangle, true, "▲")]
    [InlineData(EmphasisMark.Triangle, false, "△")]
    public void EachShapeHasItsMark(EmphasisMark mark, bool filled, string text)
    {
        GlyphRun run = Marks(Layout("国", Style(new TextEmphasis { Mark = mark, Filled = filled })).Lines[0])[0].Glyphs!;
        Assert.Equal(DejaVu.Match(FontStyle.Normal).Typeface.GetGlyph(char.ConvertToUtf32(text, 0)), run.Glyphs[0]);
    }

    [Fact]
    public void CustomMarksTakeTheirFirstGrapheme()
    {
        GlyphRun run = Marks(Layout("国際", Style(new TextEmphasis { Mark = EmphasisMark.Custom, Custom = "、x" })).Lines[0])[0].Glyphs!;
        Assert.Equal(2, run.Count);
        Assert.Equal(Cjk.Match(FontStyle.Normal).Typeface.GetGlyph('、'), run.Glyphs[0]);
    }

    [Fact]
    public void BadEmphasisThrows()
    {
        Assert.Throws<ArgumentException>(() => Paragraph.Create("国", Style(new TextEmphasis { Mark = EmphasisMark.Custom })));
        Assert.Throws<ArgumentException>(() => Paragraph.Create("国", Style(new TextEmphasis { Mark = EmphasisMark.Custom, Custom = "" })));
        Assert.Throws<ArgumentException>(() => Paragraph.Create("国", Style(new TextEmphasis { Mark = (EmphasisMark)9 })));
        Assert.Throws<ArgumentException>(() => Paragraph.Create("国", Style(new TextEmphasis { Position = (EmphasisPosition)9 })));
    }

    [Fact]
    public void MarksGrowTheLineUnlessItsHeightHoldsThem()
    {
        TextLine plain = Layout("国", Style()).Lines[0];
        TextLine over = Layout("国", Style(Dots)).Lines[0];
        Assert.True(over.Ascent > plain.Ascent);
        Assert.Equal(plain.Descent, over.Descent);
        Assert.True(Marks(over)[0].Glyphs!.Bounds.Top >= over.Top - 0.5f);

        TextLine under = Layout("国", Style(Dots with { Position = EmphasisPosition.Under })).Lines[0];
        Assert.Equal(plain.Ascent, under.Ascent);
        Assert.True(under.Descent > plain.Descent);
        Assert.All(Marks(under)[0].Glyphs!.Positions.ToArray(), p => Assert.True(p.Y > under.Baseline));

        ParagraphStyle tall = Style(Dots) with { DefaultStyle = Style(Dots).DefaultStyle with { LineHeight = LineHeight.Absolute(80) } };
        TextLine tallPlain = Layout("国", tall with { DefaultStyle = tall.DefaultStyle with { Emphasis = null } }).Lines[0];
        TextLine tallMarks = Layout("国", tall).Lines[0];
        Assert.Equal(tallPlain.Ascent, tallMarks.Ascent);
        Assert.Equal(tallPlain.Height, tallMarks.Height);
    }

    [Fact]
    public void MarksStandOutsideRubyOnTheSameSide()
    {
        RichText text = new("国際", null, null, [new RubyAnnotation(0, 2, "きょうちょう")]);
        TextLayout ruby = Paragraph.Create(text, Style()).Layout(float.PositiveInfinity);
        TextLayout both = Paragraph.Create(text, Style(Dots)).Layout(float.PositiveInfinity);
        TextLine line = both.Lines[0];
        Assert.True(line.Ascent > ruby.Lines[0].Ascent);
        Assert.True(Marks(line)[0].Glyphs!.Bounds.Bottom <= both.GetRubyBounds(0).Top + 0.5f);

        // Under marks sit on the other side, so ruby over leaves them where they would be without it.
        TextEmphasis below = Dots with { Position = EmphasisPosition.Under };
        TextLayout under = Paragraph.Create(text, Style(below)).Layout(float.PositiveInfinity);
        TextLayout underPlain = Layout("国際", Style(below));
        Assert.Equal(underPlain.Lines[0].Descent, under.Lines[0].Descent, 3);
        Assert.Equal(
            Marks(underPlain.Lines[0])[0].Glyphs!.Positions[0].Y - underPlain.Lines[0].Baseline,
            Marks(under.Lines[0])[0].Glyphs!.Positions[0].Y - under.Lines[0].Baseline,
            3);
    }

    [Fact]
    public void VerticalMarksSitRightForOverAndLeftForUnder()
    {
        TextLayout over = Layout("国際", Style(Dots, mode: WritingMode.VerticalRl));
        TextLine line = over.Lines[0];
        GlyphRun marks = Marks(line)[0].Glyphs!;
        Assert.Equal(2, marks.Count);
        Assert.All(marks.Positions.ToArray(), p => Assert.True(p.X > line.Baseline + (Em / 2), $"{p.X} {line.Baseline}"));

        // Down the line the marks are one character apart.
        Assert.Equal(Em, marks.Positions[1].Y - marks.Positions[0].Y, 2);

        TextLayout under = Layout("国際", Style(Dots with { Position = EmphasisPosition.Under }, mode: WritingMode.VerticalRl));
        Assert.All(Marks(under.Lines[0])[0].Glyphs!.Positions.ToArray(), p => Assert.True(p.X < under.Lines[0].Baseline - (Em / 2), $"{p.X}"));

        TextLine plain = Layout("国際", Style(mode: WritingMode.VerticalRl)).Lines[0];
        Assert.True(line.Height > plain.Height);
    }

    [Fact]
    public void JustifiedTextKeepsEachMarkOnItsCharacter()
    {
        ParagraphStyle style = Style(Dots) with { Align = TextAlign.Justify };
        TextLayout layout = Layout("国際する国際する国際", style, 7.5f * Em);
        TextLine first = layout.Lines[0];
        float[] markX = [.. Marks(first).SelectMany(r => r.Glyphs!.Positions.ToArray()).Select(p => p.X)];
        float advance = MarkAdvance("•");
        float[] textX = [.. first.Runs.SelectMany(r => r.Glyphs!.Positions.ToArray()).Select(p => p.X)];
        Assert.Equal(textX.Length, markX.Length);
        Assert.True(textX[^1] > (textX.Length - 1) * Em, "the line stretched");
        for (int i = 0; i < markX.Length; i++)
        {
            Assert.Equal(textX[i] + (Em / 2), markX[i] + (advance / 2), 2);
        }
    }

    [Fact]
    public void LinesWrapWithTheirOwnMarks()
    {
        TextLayout layout = Layout("国際する国際する", Style(Dots), 3 * Em);
        Assert.True(layout.Lines.Count > 1);
        Assert.Equal(8, MarkCount(layout));
        Assert.All(layout.Lines, l => Assert.Equal(l.Runs.Sum(r => r.Glyphs!.Count), Marks(l).Sum(r => r.Glyphs!.Count)));
    }

    [Fact]
    public void InkBoundsHoldTheMarks()
    {
        TextLayout layout = Layout("国際", Style(Dots));
        Assert.True(layout.InkBounds.Top <= Marks(layout.Lines[0])[0].Glyphs!.Bounds.Top + 0.01f);
    }

    [Fact]
    public void TextCombineValidatesItsDigits()
    {
        Assert.Equal(TextCombineKind.None, TextCombine.None.Kind);
        Assert.Equal(TextCombineKind.All, TextCombine.All.Kind);
        Assert.Equal(3, TextCombine.Digits(3).MaxDigits);
        Assert.Throws<ArgumentOutOfRangeException>(() => TextCombine.Digits(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextCombine.Digits(5));
    }

    [Fact]
    public void DigitsCombineIntoOneEm()
    {
        // 平年12年: the two digits take one em down the line.
        TextLayout combined = Vertical("平年12年", TextCombine.Digits(2));
        Assert.Equal(4 * Em, combined.Lines[0].Width, 3);

        // Without combining the digits go sideways, each its own advance.
        Assert.NotEqual(4 * Em, Vertical("平年12年", TextCombine.None).Lines[0].Width, 3);

        // The unit's glyphs sit side by side across the line, centred on it.
        LayoutRun unit = combined.Lines[0].Runs.Single(r => r.Start == 2);
        Assert.Equal(2, unit.Length);
        GlyphRun glyphs = unit.Glyphs!;
        Assert.Equal(2, glyphs.Count);
        Assert.True(glyphs.Positions[1].X > glyphs.Positions[0].X);
        Assert.Equal(glyphs.Positions[0].Y, glyphs.Positions[1].Y, 3);
        float baseline = combined.Lines[0].Baseline;
        Assert.True(glyphs.Positions[0].X < baseline && baseline <= glyphs.Positions[1].X + 0.01f, $"{glyphs.Positions[0].X} {glyphs.Positions[1].X} {baseline}");
    }

    [Fact]
    public void LongerDigitRunsStayAsTheyAre()
    {
        Assert.NotEqual(4 * Em, Vertical("平年123年", TextCombine.Digits(2)).Lines[0].Width, 3);
        Assert.Equal(4 * Em, Vertical("平年123年", TextCombine.Digits(3)).Lines[0].Width, 3);
    }

    [Fact]
    public void AllCombinesTheStyledText()
    {
        RichText text = new("友好!?だ", [new StyleSpan(2, 2, new TextStyle { CombineUpright = TextCombine.All })]);
        TextLayout layout = Paragraph.Create(text, Style(mode: WritingMode.VerticalRl)).Layout(float.PositiveInfinity);
        Assert.Equal(4 * Em, layout.Lines[0].Width, 3);
    }

    [Fact]
    public void WideUnitsScaleIntoTheEm()
    {
        // Four digits are wider than one em in a font without qwid: they scale down to fit.
        TextLayout layout = Vertical("年1234年", TextCombine.Digits(4));
        Assert.Equal(3 * Em, layout.Lines[0].Width, 3);
        GlyphRun unit = layout.Lines[0].Runs.Single(r => r.Start == 1).Glyphs!;
        Assert.Equal(4, unit.Count);
        Assert.False(unit.Transforms[0].IsTranslation);
        Assert.True(unit.Transforms[0].ScaleCos < 1);
        Assert.True(unit.Transforms[3].Tx - unit.Transforms[0].Tx < Em);
    }

    [Fact]
    public void HorizontalAndSidewaysLinesIgnoreCombining()
    {
        Assert.Equal(Layout("平年12年", Style()).Lines[0].Width, Layout("平年12年", Style(combine: TextCombine.Digits(2))).Lines[0].Width, 3);

        ParagraphStyle sideways = Style(combine: TextCombine.Digits(2), mode: WritingMode.VerticalRl) with { TextOrientation = TextOrientation.Sideways };
        ParagraphStyle sidewaysPlain = Style(mode: WritingMode.VerticalRl) with { TextOrientation = TextOrientation.Sideways };
        Assert.Equal(Layout("平年12年", sidewaysPlain).Lines[0].Width, Layout("平年12年", sideways).Lines[0].Width, 3);
    }

    [Fact]
    public void UnitsBreakAsOneIdeograph()
    {
        // Two em a line: a unit never splits, and lines break either side of it as between ideographs.
        TextLayout layout = Vertical("年12年34年", TextCombine.Digits(2), height: 2 * Em);
        Assert.Equal(3, layout.Lines.Count);
        Assert.Equal([0, 3, 6], layout.Lines.Select(l => l.Start));
        Assert.All(layout.Lines, l => Assert.True(l.Width <= (2 * Em) + 0.01f));
    }

    [Fact]
    public void JustificationSpacesAroundUnitsNotInside()
    {
        ParagraphStyle style = Style(combine: TextCombine.Digits(2), mode: WritingMode.VerticalRl) with { Align = TextAlign.Justify };
        TextLayout layout = Layout("年12年年12年年12年年", style, 7.5f * Em);
        LayoutRun[] units = [.. layout.Lines[0].Runs.Where(r => r.Start is 1 or 5)];
        Assert.Equal(2, units.Length);
        Assert.All(units, u => Assert.Equal(u.Glyphs!.Positions[0].Y, u.Glyphs.Positions[1].Y, 3));
        Assert.Equal(7.5f * Em, layout.Lines[0].Width, 2);
    }

    [Fact]
    public void UnitsTakeOneEmphasisMark()
    {
        Assert.Equal(3, MarkCount(Vertical("年12年", TextCombine.Digits(2), Dots)));
    }
}
