using System.ComponentModel;

using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Interop;
using Egami.SystemDrawing.Text;

namespace Egami.SystemDrawing.Tests;

/// <summary>
/// Font families, fonts, string formats, MeasureString, MeasureCharacterRanges, DrawString and AddString. The rules are GDI+'s as
/// measured on Windows (egami-compare, sysdrawing TextProbes); most tests use DejaVu Sans from tests/fonts so they hold on every
/// machine, and the Arial tests, which compare with GDI+'s own numbers, run where Arial is installed.
/// </summary>
public class TextTests
{
    private const string Sample = "The quick brown fox jumps over the lazy dog";

    private static readonly Lazy<PrivateFontCollection> s_fonts = new(() =>
    {
        PrivateFontCollection fonts = new();
        fonts.AddFontFile(TestFiles.Font("DejaVuSans.ttf"));
        return fonts;
    });

    private static FontFamily DejaVu => new("DejaVu Sans", s_fonts.Value);

    private static Font DejaVu12 => new(DejaVu, 12);

    private static Graphics NewGraphics(out Bitmap bitmap, int width = 10, int height = 10)
    {
        bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        return Graphics.FromImage(bitmap);
    }

    private static bool HasArial => FontFamily.Families.Any(f => f.Name == "Arial");

    [Fact]
    public void FamilyMetricsComeFromTheFace()
    {
        FontFamily family = DejaVu;
        Assert.Equal("DejaVu Sans", family.Name);
        Assert.Equal("DejaVu Sans", family.GetName(0));
        Assert.Equal((2048, 1901, 483, 2384), (family.GetEmHeight(FontStyle.Regular), family.GetCellAscent(FontStyle.Regular), family.GetCellDescent(FontStyle.Regular), family.GetLineSpacing(FontStyle.Regular)));
        Assert.True(family.IsStyleAvailable(FontStyle.Bold));
        Assert.Equal("[FontFamily: Name=DejaVu Sans]", family.ToString());
        Assert.Equal(family, new FontFamily("dejavu sans", s_fonts.Value));
        Assert.Contains(s_fonts.Value.Families, f => f.Name == "DejaVu Sans");
        Assert.Throws<ArgumentException>(() => new FontFamily("No Such Font Family", s_fonts.Value));
    }

    [Fact]
    public void FontSizesAndHeightsConvertAt96Dpi()
    {
        using Font points = DejaVu12;
        Assert.Equal(2384f / 2048 * 16, points.GetHeight(), 4);
        Assert.Equal(2384f / 2048 * 20, points.GetHeight(120), 4);
        Assert.Equal(19, points.Height);
        using Font pixels = new(DejaVu, 16, GraphicsUnit.Pixel);
        Assert.Equal(12, pixels.SizeInPoints, 4);
        Assert.Equal(points.GetHeight(), pixels.GetHeight(120), 4);
        using Font inch = new(DejaVu, 0.25f, FontStyle.Regular, GraphicsUnit.Inch);
        Assert.Equal(18, inch.SizeInPoints, 4);
        using Graphics g = NewGraphics(out Bitmap b);
        using (b)
        {
            g.PageUnit = GraphicsUnit.Millimeter;
            Assert.Equal(points.GetHeight() * 25.4f / 96, points.GetHeight(g), 3);
        }
    }

    [Fact]
    public void FontStylesNamesAndEqualityAreGdiPlus()
    {
        using Font font = new(DejaVu, 10, FontStyle.Bold | FontStyle.Underline | FontStyle.Strikeout);
        Assert.True(font.Bold && font.Underline && font.Strikeout && !font.Italic);
        Assert.Equal((byte)1, font.GdiCharSet);
        Assert.False(font.IsSystemFont);
        Assert.Equal("[Font: Name=DejaVu Sans, Size=10, Units=3, GdiCharSet=1, GdiVerticalFont=False]", font.ToString());
        Assert.Equal(font, new Font(DejaVu, 10, FontStyle.Bold | FontStyle.Underline | FontStyle.Strikeout));
        Assert.Equal(font, font.Clone());
        using Font italic = new(font, FontStyle.Italic);
        Assert.Equal((FontStyle.Italic, 10f), (italic.Style, italic.Size));
        Assert.Throws<ArgumentException>(() => new Font(DejaVu, 0));
        Assert.Throws<ArgumentException>(() => new Font(DejaVu, 10, FontStyle.Regular, GraphicsUnit.Display));
    }

    [Fact]
    public void MissingFamilyFallsBackAndKeepsTheName()
    {
        Assert.SkipWhen(FontFamily.Families.Length == 0, "No fonts are installed.");
        using Font font = new("No Such Font Family", 10);
        Assert.Equal("No Such Font Family", font.OriginalFontName);
        Assert.Equal(FontFamily.GenericSansSerif.Name, font.Name);
    }

    [Fact]
    public void LogFontRoundTrips()
    {
        using Font font = new(DejaVu, 12, FontStyle.Bold | FontStyle.Italic);
        font.ToLogFont(out LOGFONT lf);
        Assert.Equal(-16, lf.lfHeight);
        Assert.Equal(700, lf.lfWeight);
        Assert.Equal((byte)1, lf.lfItalic);
        Assert.Equal("DejaVu Sans", new string(lf.lfFaceName).Split('\0')[0]);
        object boxed = new LOGFONT();
        font.ToLogFont(boxed);
        Assert.Equal(-16, ((LOGFONT)boxed).lfHeight);
        Assert.Throws<PlatformNotSupportedException>(() =>
        {
#pragma warning disable EGSD0001
            return font.ToHfont();
#pragma warning restore EGSD0001
        });
    }

    [Fact]
    public void StringFormatsAreGdiPlus()
    {
        StringFormat d = new();
        Assert.Equal((0, StringAlignment.Near, StringTrimming.Character, HotkeyPrefix.None), ((int)d.FormatFlags, d.Alignment, d.Trimming, d.HotkeyPrefix));
        StringFormat t = StringFormat.GenericTypographic;
        Assert.Equal(StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip, t.FormatFlags);
        Assert.Equal(StringTrimming.None, t.Trimming);
        Assert.Same(StringFormat.GenericDefault, StringFormat.GenericDefault);
        d.SetTabStops(10, [20, 30]);
        Assert.Equal([20f, 30f], d.GetTabStops(out float first));
        Assert.Equal(10, first);
        Assert.Throws<ArgumentException>(() => d.SetTabStops(-1, [5]));
        Assert.Throws<InvalidEnumArgumentException>(() => d.Alignment = (StringAlignment)7);
        Assert.Throws<OverflowException>(() => d.SetMeasurableCharacterRanges([.. Enumerable.Range(0, 40).Select(i => new CharacterRange(i, 1))]));
        StringFormat copy = new(StringFormatFlags.NoWrap | StringFormatFlags.LineLimit, 1033);
        Assert.Equal("[StringFormat, FormatFlags=NoWrap, LineLimit]", copy.ToString());
        Assert.Equal(copy.FormatFlags, ((StringFormat)copy.Clone()).FormatFlags);
    }

    [Fact]
    public void MeasureStringPadsAndWidens()
    {
        using Graphics g = NewGraphics(out Bitmap b);
        using (b)
        {
            using Font font = DejaVu12;
            float em = 16, cell = (1901f + 483) / 2048 * em, line = 2384f / 2048 * em;
            Assert.Equal(SizeF.Empty, g.MeasureString("", font));
            SizeF space = g.MeasureString(" ", font);
            Assert.Equal(em / 3, space.Width, 3);
            Assert.Equal(cell + (em / 8), space.Height, 3);
            SizeF typographic = g.MeasureString(Sample, font, PointF.Empty, StringFormat.GenericTypographic);
            float design = font.ToEgamiFont() with { Kerning = false } is var f ? f.MeasureText(Sample).Advance : 0;
            Assert.Equal(design, typographic.Width, 2);
            Assert.Equal(cell, typographic.Height, 3);
            Assert.Equal((design * 1.03f) + (em / 3), g.MeasureString(Sample, font).Width, 2);
            Assert.Equal((3 * line) + (em / 8), g.MeasureString("one\ntwo\r\nthree", font).Height, 3);
            SizeF wrapped = g.MeasureString(Sample, font, 120);
            Assert.True(wrapped.Width <= 120);
            Assert.Equal(0, ((wrapped.Height - (em / 8)) / line) % 1, 3);
            Assert.True(wrapped.Height > 3 * line);
            SizeF fitted = g.MeasureString(Sample, font, new SizeF(150, 40), StringFormat.GenericDefault, out int chars, out int lines);
            Assert.Equal(2, lines);
            Assert.Equal((2 * line) + (em / 8), fitted.Height, 3);
            Assert.InRange(chars, 20, 42);
            StringFormat trailing = new(StringFormatFlags.MeasureTrailingSpaces);
            Assert.True(g.MeasureString("ab   ", font, PointF.Empty, trailing).Width > g.MeasureString("ab   ", font).Width);
            g.PageUnit = GraphicsUnit.Millimeter;
            Assert.Equal(typographic.Width * 25.4f / 96, g.MeasureString(Sample, font, PointF.Empty, StringFormat.GenericTypographic).Width, 2);
        }
    }

    [Fact]
    public void MeasureCharacterRangesGivesWholePixels()
    {
        using Graphics g = NewGraphics(out Bitmap b);
        using (b)
        {
            using Font font = DejaVu12;
            StringFormat ranges = new();
            ranges.SetMeasurableCharacterRanges([new CharacterRange(0, 3), new CharacterRange(4, 5)]);
            Region[] regions = g.MeasureCharacterRanges(Sample, font, new RectangleF(10, 5, 400, 60), ranges);
            Assert.Equal(2, regions.Length);
            RectangleF the = regions[0].GetBounds(g), quick = regions[1].GetBounds(g);
            Assert.Equal(13, the.X);
            Assert.Equal(5, the.Y);
            Assert.Equal(MathF.Round(the.Width), the.Width);
            Assert.True(quick.X > the.Right);
            Assert.Empty(g.MeasureCharacterRanges(Sample, font, new RectangleF(0, 0, 100, 100), new StringFormat()));
        }
    }

    [Fact]
    public void DrawStringInksWhereItIsLaidOut()
    {
        using Graphics g = NewGraphics(out Bitmap b, 200, 60);
        using (b)
        {
            g.Clear(Color.White);
            using Font font = DejaVu12;
            g.DrawString("Hello", font, Brushes.Black, 10, 10);
            (int x0, int y0, int x1, int y1) = Ink(b);
            Assert.InRange(x0, 11, 15);
            Assert.InRange(y0, 11, 16);
            Assert.InRange(y1, 22, 27);
            float width = g.MeasureString("Hello", font).Width;
            Assert.InRange(x1, 10 + (int)(width * 0.7f), 10 + (int)width);

            // Clipped to the layout rectangle unless NoClip.
            g.Clear(Color.White);
            g.DrawString("Hello", font, Brushes.Black, new RectangleF(10, 10, 20, 8));
            Assert.True(Ink(b).Y1 < 18 && Ink(b).X1 < 30);
            g.Clear(Color.White);
            g.DrawString("Hello", font, Brushes.Black, new RectangleF(10, 10, 20, 8), new StringFormat(StringFormatFlags.NoClip | StringFormatFlags.NoWrap) { Trimming = StringTrimming.None });
            Assert.True(Ink(b).Y1 > 18 && Ink(b).X1 > 30);
            g.Clear(Color.White);
            g.DrawString("", font, Brushes.Black, 10, 10);
            Assert.Equal(-1, Ink(b).X0);
        }
    }

    [Fact]
    public void AlignmentAndHotkeysMoveTheInk()
    {
        using Graphics g = NewGraphics(out Bitmap b, 200, 60);
        using (b)
        {
            using Font font = DejaVu12;
            g.Clear(Color.White);
            g.DrawString("abc", font, Brushes.Black, new RectangleF(0, 0, 200, 60), new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far });
            (int x0, int y0, int x1, int y1) = Ink(b);
            Assert.InRange(x1, 185, 199);
            Assert.InRange(y1, 40, 59);
            g.Clear(Color.White);
            g.DrawString("&File", font, Brushes.Black, 0, 0, new StringFormat { HotkeyPrefix = HotkeyPrefix.Hide });
            int hidden = Ink(b).X1;
            g.Clear(Color.White);
            g.DrawString("&File", font, Brushes.Black, 0, 0);
            Assert.True(Ink(b).X1 > hidden);
        }
    }

    [Fact]
    public void ClearTypeBlendsPerChannelAndAntiAliasInGray()
    {
        using Graphics g = NewGraphics(out Bitmap b, 120, 30);
        using (b)
        {
            using Font font = DejaVu12;
            g.Clear(Color.White);
            g.DrawString("Clear type", font, Brushes.Black, 2, 2);  // SystemDefault: ClearType, as on Windows
            Assert.True(Fringes(b) > 10);
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.DrawString("Clear type", font, Brushes.Black, 2, 2);
            Assert.Equal(0, Fringes(b));
            Assert.NotEqual(-1, Ink(b).X0);
        }

        static int Fringes(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y++)
            {
                for (int x = 0; x < b.Width; x++)
                {
                    Color c = b.GetPixel(x, y);
                    n += Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 30 ? 1 : 0;
                }
            }

            return n;
        }
    }

    [Fact]
    public void ALeadingTabMovesTheTextToTheTabStop()
    {
        using Graphics g = NewGraphics(out Bitmap b, 200, 60);
        using (b)
        {
            using Font font = DejaVu12;
            StringFormat format = new();
            format.SetTabStops(0, [50]);

            // Review finding: a tab first on a line was measured but not drawn, so the text started at the line's left.
            foreach (string text in new[] { "\tabc", "x\n\tabc" })
            {
                format.SetMeasurableCharacterRanges([new CharacterRange(text.IndexOf('a'), 1)]);
                RectangleF a = g.MeasureCharacterRanges(text, font, new RectangleF(0, 0, 200, 60), format)[0].GetBounds(g);
                Assert.InRange(a.X, 50 + (16f / 6) - 1, 50 + (16f / 6) + 1);
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.DrawString(text, font, Brushes.Black, new RectangleF(0, 0, 200, 60), format);
                int left = int.MaxValue;
                for (int y = text.Length > 4 ? 20 : 0; y < 60; y++)
                {
                    for (int x = 0; x < 200; x++)
                    {
                        if (b.GetPixel(x, y).R < 128)
                        {
                            left = Math.Min(left, x);
                        }
                    }
                }

                Assert.InRange(left, 50, 56);
                using GraphicsPath path = new();
                path.AddString(text, DejaVu, 0, 16, new RectangleF(0, 0, 200, 60), format);
                Assert.InRange(path.GetBounds().X, text.Length > 4 ? 0 : 50, 56);
            }
        }
    }

    [Theory]
    [InlineData(StringTrimming.EllipsisCharacter, false)]
    [InlineData(StringTrimming.EllipsisWord, false)]
    [InlineData(StringTrimming.EllipsisPath, false)]
    [InlineData(StringTrimming.EllipsisCharacter, true)]
    [InlineData(StringTrimming.EllipsisWord, true)]
    [InlineData(StringTrimming.EllipsisPath, true)]
    public void EllipsisTrimsDrawMeasureAndPlaceTheEllipsis(StringTrimming trimming, bool tabs)
    {
        // Review finding: the tab fix sliced the text with the ellipsis' character index (-1) and threw.
        using Graphics g = NewGraphics(out Bitmap b, 200, 40);
        using (b)
        {
            using Font font = DejaVu12;
            string text = tabs ? "one\ttwo three four five six seven eight nine ten" : "one two three four five six seven eight nine ten";
            StringFormat format = new(StringFormatFlags.NoWrap) { Trimming = trimming };
            format.SetTabStops(0, [60]);
            format.SetMeasurableCharacterRanges([new CharacterRange(0, 3)]);
            RectangleF box = new(0, 0, 120, 30);
            SizeF size = g.MeasureString(text, font, box.Size, format, out int chars, out _);
            Assert.InRange(size.Width, 1, 120);
            Assert.InRange(chars, 1, text.Length - 1);
            Assert.Single(g.MeasureCharacterRanges(text, font, box, format));
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.DrawString(text, font, Brushes.Black, box, format);
            int right = -1;
            for (int y = 0; y < 30; y++)
            {
                for (int x = 0; x < 200; x++)
                {
                    if (b.GetPixel(x, y).R < 160)
                    {
                        right = Math.Max(right, x);
                    }
                }
            }

            // The ellipsis ends the line inside the box.
            Assert.InRange(right, 60, 120);
        }
    }

    [Fact]
    public void FontCollectionsRefuseFilesThatAreNotFonts()
    {
        // Review finding: Egami.Fonts' own exceptions escaped instead of the ExternalException System.Drawing documents.
        using PrivateFontCollection fonts = new();
        string text = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllText(text, "not a font");
            Assert.Throws<System.Runtime.InteropServices.ExternalException>(() => fonts.AddFontFile(text));
        }
        finally
        {
            File.Delete(text);
        }

        byte[] random = new byte[512];
        new Random(5).NextBytes(random);
        IntPtr memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(random.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(random, 0, memory, random.Length);
            Assert.Throws<System.Runtime.InteropServices.ExternalException>(() => fonts.AddMemoryFont(memory, random.Length));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void AddStringAddsClosedFigures()
    {
        using GraphicsPath path = new();
        path.AddString("Hi", DejaVu, (int)FontStyle.Regular, 28, new PointF(5, 5), StringFormat.GenericDefault);
        Assert.True(path.PointCount > 10);
        RectangleF bounds = path.GetBounds();
        Assert.InRange(bounds.X, 5 + (28f / 6), 5 + (28f / 6) + 5);
        Assert.InRange(bounds.Bottom, 5 + 25, 5 + 28);
        byte[] types = path.PathTypes;
        Assert.Equal((byte)PathPointType.Start, types[0]);
        Assert.NotEqual(0, types[^1] & (byte)PathPointType.CloseSubpath);
    }

    [Fact]
    public void SystemFontsAndConverterAreGdiPlus()
    {
        Assert.SkipWhen(FontFamily.Families.Length == 0, "No fonts are installed.");
        Font menu = SystemFonts.MenuFont;
        Assert.True(menu.IsSystemFont);
        Assert.Equal("MenuFont", menu.SystemFontName);
        Assert.Equal(9, menu.Size);
        Assert.Equal((8.25f, (byte)0), (SystemFonts.DefaultFont.Size, SystemFonts.DefaultFont.GdiCharSet));
        Assert.Null(SystemFonts.GetFontByName("NoSuchFont"));
        FontConverter converter = new();
        using Font font = DejaVu12;
        string text = converter.ConvertToInvariantString(font)!;
        Assert.Equal("DejaVu Sans, 12pt", text);
        Font back = (Font)converter.ConvertFromInvariantString("Arial, 10pt, style=Bold, Italic")!;
        Assert.Equal((10f, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point), (back.Size, back.Style, back.Unit));
    }

    [Fact]
    public void ArialMeasuresAsGdiPlus()
    {
        Assert.SkipUnless(HasArial, "Arial is not installed.");
        using FontFamily arialFamily = new("Arial");
        Assert.Equal((2048, 1854, 434, 2355), (arialFamily.GetEmHeight(FontStyle.Regular), arialFamily.GetCellAscent(FontStyle.Regular), arialFamily.GetCellDescent(FontStyle.Regular), arialFamily.GetLineSpacing(FontStyle.Regular)));
        using Graphics g = NewGraphics(out Bitmap b);
        using (b)
        {
            using Font arial = new("Arial", 12);
            Assert.Equal(18.398436f, arial.GetHeight(), 4);

            // GDI+'s numbers, from the TextProbes references.
            SizeF sample = g.MeasureString(Sample, arial);
            Assert.Equal(331.41922, sample.Width, 0.05);
            Assert.Equal(19.874996, sample.Height, 3);
            Assert.Equal(316.60153, g.MeasureString(Sample, arial, PointF.Empty, StringFormat.GenericTypographic).Width, 0.01);
            Assert.Equal(75.59374, g.MeasureString(Sample, arial, 120).Height, 3);
            g.MeasureString(Sample, arial, new SizeF(150, 40), StringFormat.GenericDefault, out int chars, out int lines);
            Assert.Equal((36, 2), (chars, lines));
            StringFormat ranges = new();
            ranges.SetMeasurableCharacterRanges([new CharacterRange(0, 3), new CharacterRange(4, 5), new CharacterRange(16, 7)]);
            Region[] regions = g.MeasureCharacterRanges(Sample, arial, new RectangleF(10, 5, 400, 60), ranges);
            Assert.Equal(new RectangleF(13, 5, 28, 18), regions[0].GetBounds(g));
            Assert.Equal(new RectangleF(45, 5, 39, 18), regions[1].GetBounds(g));
            Assert.Equal(new RectangleF(137, 5, 52, 18), regions[2].GetBounds(g));
        }
    }

    /// <summary>The box of the pixels darker than mid-gray, or -1s when there are none.</summary>
    private static (int X0, int Y0, int X1, int Y1) Ink(Bitmap b)
    {
        int x0 = -1, y0 = -1, x1 = -1, y1 = -1;
        for (int y = 0; y < b.Height; y++)
        {
            for (int x = 0; x < b.Width; x++)
            {
                if (b.GetPixel(x, y).R < 128)
                {
                    x0 = x0 < 0 ? x : Math.Min(x0, x);
                    y0 = y0 < 0 ? y : y0;
                    x1 = Math.Max(x1, x);
                    y1 = y;
                }
            }
        }

        return (x0, y0, x1, y1);
    }
}
