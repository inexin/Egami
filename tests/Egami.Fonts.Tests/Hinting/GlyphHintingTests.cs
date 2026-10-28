using Egami.Fonts.Hinting;

namespace Egami.Fonts.Tests.Hinting;

/// <summary>
/// The glyph loader, FontHinting.Full's hinted outlines and FontMetrics.GridFitted (2.7.5 tasks 16 to 19), and the probe fonts
/// egami-compare writes (tests/fonts/hinting): glyph programs that report the interpreter's answers by moving their top points 4
/// pixels up for each set bit, the same fonts GDI and GDI+ were measured with.
/// </summary>
public class GlyphHintingTests
{
    private const int ProbePpem = 16;

    // GDI+ and GDI as Egami answers for them: their GETINFO and the virtual grid (mode B), which keeps the probes' SHPIX moves.
    private static readonly InterpreterTarget PlusAsWritten = InterpreterTarget.GdiPlus;
    private static readonly InterpreterTarget GdiAsWritten = InterpreterTarget.Gdi;

    // A probe font's bit glyphs: 'height 8 px' is a set bit, as the harness reads them on Windows.
    private static long Bits(Typeface face, InterpreterTarget target, int first, int count)
    {
        var hinter = new GlyphHinter(face, face.Glyf!, face.HintingData, target);
        long scale = ((long)ProbePpem * 64 << 16) / face.UnitsPerEm;
        long value = 0;
        for (int bit = 0; bit < count; bit++)
        {
            HintedGlyph g = hinter.Hint(face.GetGlyph('!' + first + bit), ProbePpem, scale) ?? throw new InvalidOperationException("Not hinted.");
            value |= g.Y.Max() >= 6 ? 1L << bit : 0;
        }

        return value;
    }

    [Fact]
    public void ProbeFontReportsTheMeasuredAnswers()
    {
        // The order of the info font's quantities: GETINFO (21 bits), MPPEM y (10), MPPEM x (12), MPS (12), GC x (14), GC y (14).
        Typeface face = TestFonts.Load("hinting/probe-info.ttf");
        Assert.Equal(0x2C2A, Bits(face, PlusAsWritten, 0, 21));
        Assert.Equal(0x46C2A, Bits(face, GdiAsWritten, 0, 21));
        Assert.Equal(16, Bits(face, PlusAsWritten, 21, 10));
        Assert.Equal(16, Bits(face, PlusAsWritten, 31, 12));
        Assert.Equal(12, Bits(face, PlusAsWritten, 43, 12));
        Assert.Equal(192, Bits(face, PlusAsWritten, 55, 14));
        Assert.Equal(256, Bits(face, PlusAsWritten, 69, 14));
    }

    [Fact]
    public void MeasureProbeShowsTheSignAndTheFlag()
    {
        // Each glyph moves point 3 right 1 px with SHPIX, then MD[0] and MD[1] measure from points pushed 0 then 3 (+4096), as on
        // Windows: MD[0] = -192 (the moved outline), MD[1] = -128 (the original).
        Typeface face = TestFonts.Load("hinting/probe-measure.ttf");
        Assert.Equal(-192, Bits(face, PlusAsWritten, 0, 13) - 4096);
        Assert.Equal(-128, Bits(face, PlusAsWritten, 13, 13) - 4096);
    }

    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("Roboto-Regular.ttf")]
    public void EveryGlyphHintsAtSmallSizes(string file)
    {
        Typeface face = TestFonts.Load(file);
        Assert.True(face.CanRunInstructions);
        foreach (int ppem in new[] { 9, 13, 16, 24 })
        {
            int hinted = 0;
            for (ushort g = 0; g < face.GlyphCount; g++)
            {
                HintedGlyph? h = face.GetHintedGlyph(g, ppem, TrueTypeTarget.GdiPlus);
                if (h is not null)
                {
                    hinted++;
                    Assert.All(h.X, x => Assert.InRange(x, -4 * ppem, 8 * ppem));
                    Assert.Equal(h.Advance, MathF.Round(h.Advance * 64) / 64);
                }
            }

            Assert.Equal(face.GlyphCount, hinted);
        }
    }

    [Fact]
    public void CompositeGlyphsAssembleTheirHintedComponents()
    {
        // Roboto's Aacute is a composite of A and acute; hinted, its points are the A's points plus the accent's.
        Typeface face = TestFonts.Load("Roboto-Regular.ttf");
        ushort aacute = face.GetGlyph('Á'), a = face.GetGlyph('A');
        var components = new List<int>();
        face.Glyf!.GetComponents(aacute, components);
        Assert.Contains(a, components.Select(c => (ushort)c));
        HintedGlyph composite = face.GetHintedGlyph(aacute, 16, TrueTypeTarget.GdiPlus)!, plain = face.GetHintedGlyph(a, 16, TrueTypeTarget.GdiPlus)!;
        Assert.True(composite.X.Length > plain.X.Length);
        Assert.Equal(plain.Advance, composite.Advance);
        Assert.True(composite.Y.Max() > plain.Y.Max() + 1);
    }

    [Fact]
    public void FacesWithoutInstructionsDoNotHint()
    {
        Assert.False(TestFonts.Load("SourceSans3-Regular.otf").CanRunInstructions);
        Assert.False(TestFonts.Load("derived/NotoSans-VF-subset.ttf").CanRunInstructions);
        Assert.Null(TestFonts.Load("SourceSans3-Regular.otf").GetHintedGlyph(5, 16, TrueTypeTarget.GdiPlus));
    }

    [Fact]
    public void GridFittedAdvancesAreWholePixelsAndMeasuringMatchesShaping()
    {
        Typeface face = TestFonts.Load("DejaVuSans.ttf");
        var design = new Font(face, 13);
        Font fitted = design with { Metrics = FontMetrics.GridFitted };
        GlyphRun run = fitted.Shape("Hamburgefonstiv");
        Assert.All(run.Positions.ToArray(), p => Assert.Equal(MathF.Round(p.X), p.X));
        Assert.NotEqual(design.MeasureText("Hamburgefonstiv").Advance, fitted.MeasureText("Hamburgefonstiv").Advance);
        Assert.Equal(fitted.MeasureText("Hamburgefonstiv").Advance, run.Advance);

        // A face that cannot be hinted keeps its design advances, rounded.
        var cff = new Font(TestFonts.Load("SourceSans3-Regular.otf"), 13) { Metrics = FontMetrics.GridFitted };
        Assert.All(cff.Shape("Hamburg").Positions.ToArray(), p => Assert.Equal(MathF.Round(p.X), p.X));
    }

    [Fact]
    public void CompatibleWidthsComeFromHdmxLtshOrAMonochromeRun()
    {
        // Roboto has hdmx: its widths at a ppem it holds are hdmx's bytes.
        Typeface roboto = TestFonts.Load("Roboto-Regular.ttf");
        int ppem = roboto.HintingData.Hdmx.Keys.First();
        ushort a = roboto.GetGlyph('a');
        Assert.Equal(roboto.HintingData.Hdmx[ppem][a], roboto.GetCompatibleAdvance(a, ppem));

        // DejaVu has neither hdmx nor LTSH: the width of a monochrome run of the glyph's instructions, whole pixels.
        Typeface dejaVu = TestFonts.Load("DejaVuSans.ttf");
        Assert.Empty(dejaVu.HintingData.Hdmx);
        for (ushort g = 1; g < 100; g++)
        {
            float? w = dejaVu.GetCompatibleAdvance(g, 13);
            Assert.NotNull(w);
            Assert.Equal(MathF.Round(w.Value), w.Value);
        }

        // Shaping with compatible metrics uses them, whole pixels.
        var font = new Font(dejaVu, 13) { Metrics = FontMetrics.Compatible };
        Assert.All(font.Shape("Hamburgefonstiv").Positions.ToArray(), p => Assert.Equal(MathF.Round(p.X), p.X));
        Assert.Null(TestFonts.Load("SourceSans3-Regular.otf").GetCompatibleAdvance(5, 13));
    }

    [Fact]
    public void GdiPlusKeepsDesignAdvancesWhenARunWouldWidenTooMuch()
    {
        // A glyph whose hinted advance is wider than its design: a few copies keep whole-pixel hinted steps, but once the run would
        // be more than 2.1 pixels wider than designed, GDI+ (and GridFitted for it) lays it out with design advances.
        Typeface face = TestFonts.Load("DejaVuSans.ttf");
        var font = new Font(face, 13) { Hinting = FontHinting.Full, Metrics = FontMetrics.GridFitted, Kerning = false };
        float scale = 13f / face.UnitsPerEm;
        // (Not f: repeated, it forms ligatures.)
        char c = "abcdeghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".First(ch =>
        {
            ushort g = face.GetGlyph(ch);
            float d = face.GetAdvance(g) * scale, h = MathF.Round(face.GetHintedGlyph(g, 13, TrueTypeTarget.GdiPlus)!.Advance);
            return h - d is > 0.2f and < 0.5f;
        });
        ushort glyph = face.GetGlyph(c);
        float design = face.GetAdvance(glyph) * scale, hinted = MathF.Round(face.GetHintedGlyph(glyph, 13, TrueTypeTarget.GdiPlus)!.Advance);
        int few = (int)(2.1f / (hinted - design)), many = few + 3;
        Assert.Equal(few * hinted, font.MeasureText(new string(c, few)).Advance, 3);
        Assert.Equal(many * design, font.MeasureText(new string(c, many)).Advance, 2);

        // Other targets keep whole pixels however long the run.
        Assert.Equal(many * hinted, (font with { HintingTarget = TrueTypeTarget.FreeType }).MeasureText(new string(c, many)).Advance, 0);
    }

    [Fact]
    public void NewPropertiesValidateAndKeepFontSmall()
    {
        var font = new Font(TestFonts.Load("DejaVuSans.ttf"), 12);
        Assert.Equal(TrueTypeTarget.GdiPlus, font.HintingTarget);
        Assert.Equal(FontMetrics.Design, font.Metrics);
        Assert.Throws<ArgumentOutOfRangeException>(() => font with { HintingTarget = (TrueTypeTarget)9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => font with { Metrics = (FontMetrics)9 });
        Assert.Equal(40, System.Runtime.CompilerServices.Unsafe.SizeOf<Font>());
    }
}
