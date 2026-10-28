using Egami.Fonts.Hinting;

namespace Egami.Fonts.Tests.Hinting;

/// <summary>The hinting tables (2.7.5 task 11), and the font and control value programs of real fonts running clean.</summary>
public class HintingDataTests
{
    private static Typeface Face(string file) => Typeface.FromFile(TestFonts.FontPath(file));

    [Fact]
    public void ReadsTheTablesOfAHintedFont()
    {
        HintingData data = HintingData.Read(Face("Roboto-Regular.ttf"));
        Assert.True(data.HasHinting);
        Assert.Equal(444, data.Fpgm.Length);
        Assert.Equal(329, data.Prep.Length);
        Assert.Equal(42, data.Cvt.Length);
        Assert.NotEmpty(data.Hdmx);
        Assert.NotEmpty(data.Ltsh);
        Assert.True(data.MaxFunctionDefs > 0);
        Assert.True(data.MaxStackElements > 0);
        Assert.True(data.IntegerPpem);
        Assert.True(data.GaspVersion >= 0);
        Assert.NotNull(data.GaspFor(16));
        Assert.Equal(data.Hdmx.Values.First().Length, Face("Roboto-Regular.ttf").GlyphCount);
    }

    [Fact]
    public void FontsWithoutInstructionsHaveNone()
    {
        Assert.False(HintingData.Read(Face("SourceSans3-Regular.otf")).HasHinting);
        Assert.False(HintingData.Read(Face("test_glyphs-glyf_colr_1.ttf")).HasHinting);
    }

    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("DejaVuSansMono.ttf")]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("NotoSerif-Regular.ttf")]
    [InlineData("Roboto-Regular.ttf")]
    public void FontAndControlValueProgramsRunAtEverySize(string file)
    {
        Typeface face = Face(file);
        HintingData data = HintingData.Read(face);
        foreach (InterpreterTarget target in new[] { InterpreterTarget.Monochrome, InterpreterTarget.GdiPlus, InterpreterTarget.Gdi, InterpreterTarget.FreeType })
        {
            var t = new TrueTypeInterpreter(data, target);
            for (int ppem = 6; ppem <= 72; ppem++)
            {
                long scale = ((long)ppem * 64 << 16) / face.UnitsPerEm;
                Assert.True(t.SetSize(ppem, scale), $"{file} at {ppem} ppem for {target}");
            }
        }
    }
}
