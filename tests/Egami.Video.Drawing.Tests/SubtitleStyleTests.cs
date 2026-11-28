using Egami.Colors;
using Egami.Fonts;
using Egami.Pixels;
using Egami.Video.Subtitles;
using Egami.Video.Subtitles.Rendering;

using static Egami.Video.Drawing.Tests.SubtitleRendererTests;

namespace Egami.Video.Drawing.Tests;

/// <summary>Each part of a style, the placement rules and the frame formats, one at a time.</summary>
public class SubtitleStyleTests
{
    private const string Format = "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";

    /// <summary>A style line: DejaVu Sans 48, white, no outline or shadow, bottom centre, margins 20, 20, 40, with fields changed.</summary>
    private static string Style(
        string font = "DejaVu Sans", double size = 48, string primary = "&H00FFFFFF", string outlineColor = "&H00000000", string back = "&H00000000",
        int bold = 0, int italic = 0, int underline = 0, int strikeout = 0, double scaleX = 100, double scaleY = 100, double spacing = 0,
        int borderStyle = 1, double outline = 0, double shadow = 0, int alignment = 2, int marginL = 20, int marginR = 20, int marginV = 40)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Default,{font},{size},{primary},&H000000FF,{outlineColor},{back},{bold},{italic},{underline},{strikeout},{scaleX},{scaleY},{spacing},0,{borderStyle},{outline},{shadow},{alignment},{marginL},{marginR},{marginV},1");
    }

    private static SubtitleDocument Cue(string text, string style) => Ass($"Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{text}", style);

    private static Raster<Rgba8> Render(SubtitleDocument document, int width = 1920, int height = 1080, SubtitleRenderOptions? options = null, MediaContext? context = null, Rgba8? background = null)
    {
        using var renderer = new SubtitleRenderer(document, options ?? Options, context);
        Raster<Rgba8> raster = Raster.Create(width, height, background ?? new Rgba8(0, 0, 0, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        return raster;
    }

    /// <summary>The box around every pixel that differs from the background, or null when nothing was drawn.</summary>
    private static (int Left, int Top, int Right, int Bottom)? Ink(Raster<Rgba8> raster, int threshold = 30)
    {
        Rgba8 background = raster.Row(0)[0];
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                if (Math.Abs(row[x].R - background.R) + Math.Abs(row[x].G - background.G) + Math.Abs(row[x].B - background.B) > threshold)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? null : (left, top, right + 1, bottom + 1);
    }

    private static (int Left, int Top, int Right, int Bottom) InkOf(Raster<Rgba8> raster) => Ink(raster) ?? throw new InvalidOperationException("Nothing was drawn.");

    private static int Count(Raster<Rgba8> raster, Func<Rgba8, bool> match)
    {
        int count = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                count += match(p) ? 1 : 0;
            }
        }

        return count;
    }

    private static bool Same(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void FontNameChoosesTheFamily()
    {
        using Raster<Rgba8> dejaVu = Render(Cue("Typeface", Style(font: "DejaVu Sans")));
        using Raster<Rgba8> noto = Render(Cue("Typeface", Style(font: "Noto Sans")));
        using Raster<Rgba8> roboto = Render(Cue("Typeface", Style(font: "Roboto")));
        Assert.False(Same(dejaVu, noto));
        Assert.False(Same(noto, roboto));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(96)]
    public void FontSizeIsTheLineHeight(double size)
    {
        // An ASS size is the height of the font's line (Win ascent plus descent): "H" is about 0.6 of it in DejaVu Sans.
        using Raster<Rgba8> raster = Render(Cue("HHHH", Style(size: size)));
        (_, int top, _, int bottom) = InkOf(raster);
        Assert.InRange(bottom - top, size * 0.55, size * 0.68);
    }

    [Fact]
    public void BoldUsesTheBoldFace()
    {
        using Raster<Rgba8> regular = Render(Cue("Bold text", Style(font: "Noto Sans")));
        using Raster<Rgba8> bold = Render(Cue("Bold text", Style(font: "Noto Sans", bold: -1)));
        int light = Count(regular, p => p.R > 128), heavy = Count(bold, p => p.R > 128);
        Assert.True(heavy > light * 1.2, $"{heavy} bold pixels against {light}");
    }

    [Fact]
    public void ItalicUsesTheItalicFace()
    {
        using Raster<Rgba8> upright = Render(Cue("Italic text", Style(font: "Noto Sans")));
        using Raster<Rgba8> italic = Render(Cue("Italic text", Style(font: "Noto Sans", italic: -1)));
        Assert.False(Same(upright, italic));
        Assert.True(InkOf(italic).Right - InkOf(italic).Left < InkOf(upright).Right - InkOf(upright).Left);
    }

    [Fact]
    public void UnderlineDrawsBelowTheBaseline()
    {
        using Raster<Rgba8> plain = Render(Cue("nnnn", Style()));
        using Raster<Rgba8> underlined = Render(Cue("nnnn", Style(underline: -1)));
        Assert.True(InkOf(underlined).Bottom > InkOf(plain).Bottom + 2);
    }

    [Fact]
    public void StrikeoutDrawsThroughTheLetters()
    {
        // Strikeout fills the gap between the n's at the middle of the x-height, and changes nothing above or below the letters.
        using Raster<Rgba8> plain = Render(Cue("nnnn", Style()));
        using Raster<Rgba8> struck = Render(Cue("nnnn", Style(strikeout: -1)));
        Assert.Equal(InkOf(plain).Top, InkOf(struck).Top);
        Assert.Equal(InkOf(plain).Bottom, InkOf(struck).Bottom);
        Assert.True(Count(struck, p => p.R > 128) > Count(plain, p => p.R > 128) + 50);
    }

    [Theory]
    [InlineData("&H000000FF", 255, 0, 0)]
    [InlineData("&H0000FF00", 0, 255, 0)]
    [InlineData("&H00FF0000", 0, 0, 255)]
    public void PrimaryColourIsBgr(string color, byte r, byte g, byte b)
    {
        using Raster<Rgba8> raster = Render(Cue("Colour", Style(primary: color)));
        Assert.True(Count(raster, p => p.R == r && p.G == g && p.B == b) > 200);
    }

    [Fact]
    public void ColourAlphaIsTransparency()
    {
        // &H80 is half transparent: white over black comes out about mid gray, never full white.
        using Raster<Rgba8> raster = Render(Cue("Faded", Style(primary: "&H80FFFFFF")));
        Assert.Equal(0, Count(raster, p => p.R > 140));
        Assert.True(Count(raster, p => p.R is >= 120 and <= 135) > 200);
    }

    [Fact]
    public void SpanColoursOverrideTheStyle()
    {
        using Raster<Rgba8> raster = Render(Cue("white {\\c&H0000FF&}red", Style()));
        Assert.True(Count(raster, p => p.R == 255 && p.G == 0 && p.B == 0) > 100);
        Assert.True(Count(raster, p => p.R == 255 && p.G == 255 && p.B == 255) > 100);
    }

    [Fact]
    public void ScaleXStretchesAcross()
    {
        using Raster<Rgba8> normal = Render(Cue("Stretch", Style()));
        using Raster<Rgba8> wide = Render(Cue("Stretch", Style(scaleX: 200)));
        double ratio = (double)(InkOf(wide).Right - InkOf(wide).Left) / (InkOf(normal).Right - InkOf(normal).Left);
        Assert.InRange(ratio, 1.9, 2.1);
        Assert.Equal(InkOf(normal).Bottom - InkOf(normal).Top, InkOf(wide).Bottom - InkOf(wide).Top);
    }

    [Fact]
    public void ScaleYStretchesUp()
    {
        using Raster<Rgba8> normal = Render(Cue("HHHH", Style()));
        using Raster<Rgba8> tall = Render(Cue("HHHH", Style(scaleY: 200)));
        double ratio = (double)(InkOf(tall).Bottom - InkOf(tall).Top) / (InkOf(normal).Bottom - InkOf(normal).Top);
        Assert.InRange(ratio, 1.9, 2.1);
        Assert.InRange(InkOf(tall).Right - InkOf(tall).Left, InkOf(normal).Right - InkOf(normal).Left - 1, InkOf(normal).Right - InkOf(normal).Left + 1);
    }

    [Fact]
    public void SpacingAddsBetweenLetters()
    {
        // Ten letters, nine gaps of 10 script pixels more (the script is 1:1 with the frame).
        using Raster<Rgba8> normal = Render(Cue("IIIIIIIIII", Style()));
        using Raster<Rgba8> spaced = Render(Cue("IIIIIIIIII", Style(spacing: 10)));
        int grown = InkOf(spaced).Right - InkOf(spaced).Left - (InkOf(normal).Right - InkOf(normal).Left);
        Assert.InRange(grown, 88, 92);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void AlignmentsFollowTheNumpad(int alignment)
    {
        using Raster<Rgba8> raster = Render(Cue("Aligned", Style(alignment: alignment, marginL: 100, marginR: 100, marginV: 100)));
        (int left, int top, int right, int bottom) = InkOf(raster);
        int column = (alignment - 1) % 3, row = (alignment - 1) / 3;
        switch (column)
        {
            case 0:
                Assert.InRange(left, 100, 110);
                break;
            case 1:
                Assert.InRange((left + right) / 2, 950, 970);
                break;
            default:
                Assert.InRange(right, 1810, 1820);
                break;
        }

        switch (row)
        {
            case 0:
                Assert.InRange(bottom, 960, 980);
                break;
            case 1:
                Assert.InRange((top + bottom) / 2, 520, 560);
                break;
            default:
                Assert.InRange(top, 100, 125);
                break;
        }
    }

    [Fact]
    public void AlignmentOverrideTagMovesTheCue()
    {
        using Raster<Rgba8> raster = Render(Cue("{\\an9}Corner", Style(marginL: 100, marginR: 100, marginV: 100)));
        Assert.InRange(InkOf(raster).Right, 1810, 1820);
        Assert.InRange(InkOf(raster).Top, 100, 125);
    }

    [Fact]
    public void MarginsMoveTheText()
    {
        using Raster<Rgba8> near = Render(Cue("Margin", Style(alignment: 1, marginL: 50, marginV: 50)));
        using Raster<Rgba8> far = Render(Cue("Margin", Style(alignment: 1, marginL: 300, marginV: 200)));
        Assert.InRange(InkOf(far).Left - InkOf(near).Left, 249, 251);
        Assert.InRange(InkOf(near).Bottom - InkOf(far).Bottom, 149, 151);
    }

    [Fact]
    public void CueMarginsReplaceTheStyles()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,300,0,200,,Margin", Style(alignment: 1, marginL: 50, marginV: 50));
        using Raster<Rgba8> style = Render(Cue("Margin", Style(alignment: 1, marginL: 50, marginV: 50)));
        using Raster<Rgba8> cue = Render(document);
        Assert.InRange(InkOf(cue).Left - InkOf(style).Left, 249, 251);
    }

    [Fact]
    public void MarginsBoundTheLineWidth()
    {
        // The same words wrap into more lines between wide margins.
        string text = string.Join(' ', Enumerable.Repeat("words", 20));
        using Raster<Rgba8> wide = Render(Cue(text, Style(marginL: 20, marginR: 20)));
        using Raster<Rgba8> narrow = Render(Cue(text, Style(marginL: 600, marginR: 600)));
        Assert.True(Lines(narrow).Count > Lines(wide).Count);
        Assert.All(Lines(narrow), l => Assert.InRange(l.Left, 590, 1330));
    }

    [Fact]
    public void PlayResScalesToTheFrame()
    {
        // The same script at 640x360 drawn on 1280x720 and 1920x1080: everything scales with the frame.
        SubtitleDocument document = SubtitleDocument.Parse($"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            {Format}
            Style: {Style(size: 24, alignment: 7, marginL: 40, marginV: 30)}

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,HHHH
            """);
        using Raster<Rgba8> small = Render(document, 1280, 720);
        using Raster<Rgba8> large = Render(document, 1920, 1080);
        Assert.InRange(InkOf(small).Left, 80, 84);
        Assert.InRange(InkOf(large).Left, 120, 126);
        double ratio = (double)(InkOf(large).Bottom - InkOf(large).Top) / (InkOf(small).Bottom - InkOf(small).Top);
        Assert.InRange(ratio, 1.4, 1.6);
    }

    [Fact]
    public void OutlineSurroundsTheLetters()
    {
        using Raster<Rgba8> plain = Render(Cue("Outline", Style()), background: new Rgba8(0, 0, 255, 255));
        using Raster<Rgba8> outlined = Render(Cue("Outline", Style(outline: 6, outlineColor: "&H000000FF")), background: new Rgba8(0, 0, 255, 255));
        Assert.InRange(InkOf(plain).Left - InkOf(outlined).Left, 5, 7);
        Assert.InRange(InkOf(outlined).Right - InkOf(plain).Right, 5, 7);
        Assert.True(Count(outlined, p => p.R == 255 && p.G == 0 && p.B == 0) > 500);
    }

    [Fact]
    public void ShadowFallsDownAndRight()
    {
        using Raster<Rgba8> plain = Render(Cue("Shadow", Style()), background: new Rgba8(0, 0, 255, 255));
        using Raster<Rgba8> shadowed = Render(Cue("Shadow", Style(shadow: 8, back: "&H0000FF00")), background: new Rgba8(0, 0, 255, 255));
        Assert.Equal(InkOf(plain).Left, InkOf(shadowed).Left);
        Assert.InRange(InkOf(shadowed).Right - InkOf(plain).Right, 7, 9);
        Assert.InRange(InkOf(shadowed).Bottom - InkOf(plain).Bottom, 7, 9);
        Assert.True(Count(shadowed, p => p.G == 255 && p.R == 0) > 300);
    }

    [Fact]
    public void BoxIsInTheOutlineColourWithItsShadowInTheBackColour()
    {
        // BorderStyle 3: the box is OutlineColour and its shadow BackColour (libass and VSFilter), padded by the outline width.
        using Raster<Rgba8> raster = Render(Cue("Boxed", Style(borderStyle: 3, outline: 10, shadow: 12, outlineColor: "&H000000FF", back: "&H0000FF00")));
        using Raster<Rgba8> plain = Render(Cue("Boxed", Style()));
        int red = Count(raster, p => p.R == 255 && p.G == 0 && p.B == 0), green = Count(raster, p => p.G == 255 && p.R == 0 && p.B == 0);
        Assert.True(red > 5000, $"{red} box pixels");
        Assert.True(green > 1000, $"{green} shadow pixels");
        Assert.True(InkOf(raster).Left < InkOf(plain).Left - 9);
    }

    [Fact]
    public void BoxOfAWebVttCueIsEightyPercentBlack()
    {
        SubtitleDocument document = SubtitleDocument.Parse("WEBVTT\n\n00:01.000 --> 00:03.000\nCaption here\n");
        using Raster<Rgba8> raster = Render(document, 1280, 720, Options with { ForceStyle = s => s with { FontName = "DejaVu Sans" } }, background: new Rgba8(255, 255, 255, 255));
        (int left, int top, int right, int bottom) = InkOf(raster);
        Assert.InRange((left + right) / 2, 630, 650);
        Assert.True(bottom > 650);

        // The text is 5% of the frame high: 36 pixels of line at 720p.
        Assert.InRange(bottom - top, 34, 46);
        Assert.True(Count(raster, p => p.R is >= 50 and <= 52) > 2000);
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv420, 8)]
    [InlineData(ChromaFormat.Yuv422, 8)]
    [InlineData(ChromaFormat.Yuv444, 8)]
    [InlineData(ChromaFormat.Yuv420, 10)]
    [InlineData(ChromaFormat.Yuv422, 10)]
    [InlineData(ChromaFormat.Yuv444, 10)]
    [InlineData(ChromaFormat.Yuv420, 12)]
    [InlineData(ChromaFormat.Yuv422, 12)]
    [InlineData(ChromaFormat.Yuv444, 12)]
    public void RedTextGetsRedChromaInEveryLayout(ChromaFormat chroma, int bitDepth)
    {
        // BT.709 limited red: Y 63, Cb 102, Cr 240 at 8 bits.
        SubtitleDocument document = Cue("RED", Style(primary: "&H000000FF", size: 96));
        using var renderer = new SubtitleRenderer(document, Options);
        using YuvVideoFrame frame = Frame(chroma, bitDepth, 2);
        renderer.Draw(frame);
        int shift = bitDepth - 8;
        Assert.InRange(MaxLuma(frame.Raster), (63 << shift) - (2 << shift), (63 << shift) + (2 << shift));
        int maxCr = 0, minCb = int.MaxValue;
        YuvRaster raster = frame.Raster;
        int chromaHeight = raster is YuvRaster<byte> b8 ? b8.V!.Height : ((YuvRaster<ushort>)raster).V!.Height;
        for (int y = 0; y < chromaHeight; y++)
        {
            if (raster is YuvRaster<byte> bytes)
            {
                foreach (byte v in bytes.V!.Row(y))
                {
                    maxCr = Math.Max(maxCr, v);
                }

                foreach (byte u in bytes.U!.Row(y))
                {
                    minCb = Math.Min(minCb, u);
                }
            }
            else
            {
                var words = (YuvRaster<ushort>)raster;
                foreach (ushort v in words.V!.Row(y))
                {
                    maxCr = Math.Max(maxCr, v);
                }

                foreach (ushort u in words.U!.Row(y))
                {
                    minCb = Math.Min(minCb, u);
                }
            }
        }

        Assert.InRange(maxCr, (240 << shift) - (3 << shift), (240 << shift) + (2 << shift));
        Assert.InRange(minCb, (102 << shift) - (2 << shift), (102 << shift) + (3 << shift));
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv420, 16, 0.5807)]
    [InlineData(ChromaFormat.Yuv444, 16, 0.5807)]
    [InlineData(ChromaFormat.Yuv422, 18, 0.75)]
    [InlineData(ChromaFormat.Yuv444, 18, 0.75)]
    public void HdrWhiteIsTwoHundredThreeNitsAtTwelveBits(ChromaFormat chroma, byte transfer, double signal)
    {
        SubtitleDocument document = Cue("HDR", Style(size: 96));
        using var renderer = new SubtitleRenderer(document, Options);
        using YuvVideoFrame frame = Frame(chroma, 12, 2, new ColorCodePoints(9, transfer, 9, false));
        renderer.Draw(frame);
        int expected = (int)Math.Round((16 + (219 * signal)) * 16);
        Assert.InRange(MaxLuma(frame.Raster), expected - 8, expected + 8);
    }

    [Fact]
    public void FallbackFontsDrawWhatTheStyleFontLacks()
    {
        // Noto Sans has no Hebrew; with the Hebrew font as a fallback the letters are drawn from it instead of as missing boxes.
        var hebrew = new FontCollection();
        hebrew.AddDirectory(Path.Combine(AppContext.BaseDirectory, "hebrew"));
        var noto = new FontCollection();
        noto.AddFile(Path.Combine(SubtitleRendererTests.Fonts, "NotoSans-Regular.ttf"));
        var only = new SubtitleRenderOptions { Fonts = noto, UseSystemFonts = false };
        SubtitleDocument document = Cue("שלום עולם", Style(font: "Noto Sans"));
        using Raster<Rgba8> without = Render(document, options: only);
        using Raster<Rgba8> with = Render(document, options: only with { Fallback = hebrew });
        Assert.NotNull(Ink(with));
        Assert.False(Same(without, with));
    }

    [Fact]
    public void MissingFontsWarnOnce()
    {
        var warnings = new List<MediaWarning>();
        SubtitleDocument document = Ass("""
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,One
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Two {\fnNo Such Font}three
            """, Style(font: "No Such Font"));
        using Raster<Rgba8> raster = Render(document, context: MediaContext.Default with { Warnings = warnings.Add });
        Assert.Single(warnings, w => w.Code == "subtitle-font-missing");
        Assert.NotNull(Ink(raster));
    }

    [Fact]
    public void MissingFontFoldersWarn()
    {
        var warnings = new List<MediaWarning>();
        using Raster<Rgba8> raster = Render(Cue("Folder", Style()), options: Options with { FontFolders = [SubtitleRendererTests.Fonts, Path.Combine(SubtitleRendererTests.Fonts, "no-such-folder")] }, context: MediaContext.Default with { Warnings = warnings.Add });
        Assert.Contains(warnings, w => w.Code == "subtitle-font-folder");
    }

    [Fact]
    public void FontsFromACollectionNeedNoFolders()
    {
        var fonts = new FontCollection();
        fonts.AddDirectory(SubtitleRendererTests.Fonts);
        var warnings = new List<MediaWarning>();
        using Raster<Rgba8> raster = Render(Cue("Collection", Style(font: "Noto Sans")), options: new SubtitleRenderOptions { Fonts = fonts, UseSystemFonts = false }, context: MediaContext.Default with { Warnings = warnings.Add });
        Assert.Empty(warnings);
        Assert.NotNull(Ink(raster));
    }

    [Fact]
    public void ACueIsDrawnTheSameOnEveryFrame()
    {
        SubtitleDocument document = Ass("""
            Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,Kept
            Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,{\an8}Joins
            """, Style());
        using var renderer = new SubtitleRenderer(document, Options);
        using Raster<Rgba8> first = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> again = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> joined = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> after = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        renderer.Draw(first, MediaTime.FromSeconds(1.5));
        renderer.Draw(again, MediaTime.FromSeconds(2.5));
        renderer.Draw(joined, MediaTime.FromSeconds(3.5));
        renderer.Draw(after, MediaTime.FromSeconds(4.5));
        Assert.True(Same(first, again));
        Assert.Equal(2, Lines(joined).Count);
        Assert.True(Same(first, after));
    }

    [Fact]
    public void SeekingBackDrawsEarlierCuesAgain()
    {
        SubtitleDocument document = Ass("""
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Early
            Dialogue: 0,0:00:08.00,0:00:09.00,Default,,0,0,0,,Late
            """, Style());
        using var renderer = new SubtitleRenderer(document, Options);
        using Raster<Rgba8> early = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> late = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> back = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        renderer.Draw(early, MediaTime.FromSeconds(1.5));
        renderer.Draw(late, MediaTime.FromSeconds(8.5));
        renderer.Draw(back, MediaTime.FromSeconds(1.5));
        Assert.False(Same(early, late));
        Assert.True(Same(early, back));
    }

    [Fact]
    public void TheSameCueDrawsOnFramesOfOtherSizes()
    {
        SubtitleDocument document = Cue("Resized", Style());
        using var renderer = new SubtitleRenderer(document, Options);
        using Raster<Rgba8> small = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> large = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        renderer.Draw(small, MediaTime.FromSeconds(2));
        renderer.Draw(large, MediaTime.FromSeconds(2));
        Assert.True(InkOf(large).Right - InkOf(large).Left > 2.5 * (InkOf(small).Right - InkOf(small).Left));
    }

    [Fact]
    public void ForceStyleChangesColourFontAndAlignment()
    {
        SubtitleDocument document = Cue("Forced", Style());
        using Raster<Rgba8> raster = Render(document, options: Options with
        {
            ForceStyle = s => s with { PrimaryColor = new Rgba8(255, 0, 0, 255), FontName = "Noto Sans", Alignment = SubtitleAlignment.TopLeft },
        });
        Assert.True(Count(raster, p => p.R == 255 && p.G == 0 && p.B == 0) > 100);
        Assert.True(InkOf(raster).Top < 200);
        Assert.True(InkOf(raster).Left < 100);
    }

    [Fact]
    public void SrtGetsFfmpegsDefaultLook()
    {
        // Arial 16 at 384x288 (DejaVu Sans here), white with a thin black outline, bottom centre above a 10-pixel margin.
        SubtitleDocument document = SubtitleDocument.Parse("1\n00:00:01,000 --> 00:00:03,000\nHello\n");
        using Raster<Rgba8> raster = Render(document, 1920, 1080, Options with { ForceStyle = s => s with { FontName = "DejaVu Sans" } }, background: new Rgba8(0, 0, 255, 255));
        (int left, _, int right, int bottom) = InkOf(raster);
        Assert.InRange((left + right) / 2, 950, 970);
        Assert.InRange(bottom, 1010, 1045);
        Assert.True(Count(raster, p => p.R < 10 && p.G < 10 && p.B < 10) > 50);
    }

    [Fact]
    public void RgbaRasterKeepsItsAlphaAndBackgroundOutsideTheText()
    {
        using Raster<Rgba8> raster = Render(Cue("Alpha", Style()), 640, 360, background: new Rgba8(10, 20, 30, 255));
        Assert.Equal(new Rgba8(10, 20, 30, 255), raster.Row(0)[0]);
        Assert.Equal(0, Count(raster, p => p.A != 255));
        Assert.NotNull(Ink(raster));
    }

    [Fact]
    public void RgbaRasterAtATimeWithoutCuesIsUnchanged()
    {
        using Raster<Rgba8> raster = Raster.Create(640, 360, new Rgba8(10, 20, 30, 255));
        using var renderer = new SubtitleRenderer(Cue("Later", Style()), Options);
        renderer.Draw(raster, MediaTime.FromSeconds(10));
        Assert.Null(Ink(raster, 0));
    }

    [Fact]
    public void FilterHasNoLatencyAndPassesFramesInOrder()
    {
        using var filter = new SubtitleBurnIn(Cue("Ordered", Style()), Options);
        Assert.Equal(MediaTime.Zero, filter.Latency);
        using YuvVideoFrame a = Frame(ChromaFormat.Yuv420, 8, 0.5);
        using YuvVideoFrame b = Frame(ChromaFormat.Yuv420, 8, 2);
        filter.Send(a);
        filter.Send(b);
        Assert.True(filter.TryReceive(out YuvVideoFrame? first));
        Assert.True(filter.TryReceive(out YuvVideoFrame? second));
        using (first)
        using (second)
        {
            Assert.Equal(a.Timestamp, first.Timestamp);
            Assert.Equal(16, MaxLuma(first.Raster));
            Assert.Equal(b.Timestamp, second.Timestamp);
            Assert.Equal(235, MaxLuma(second.Raster));
        }

        filter.Flush();
        Assert.False(filter.TryReceive(out _));
    }

    [Fact]
    public void FilterResetDropsQueuedFrames()
    {
        using var filter = new SubtitleBurnIn(Cue("Dropped", Style()), Options);
        using YuvVideoFrame frame = Frame(ChromaFormat.Yuv420, 8, 2);
        filter.Send(frame);
        filter.Reset();
        Assert.False(filter.TryReceive(out _));
        filter.Send(frame);
        Assert.True(filter.TryReceive(out YuvVideoFrame? output));
        output.Dispose();
    }

    [Fact]
    public void FilterOverARendererLeavesTheRendererToItsOwner()
    {
        using var renderer = new SubtitleRenderer(Cue("Shared", Style()), Options);
        using (var filter = new SubtitleBurnIn(renderer))
        {
            Assert.Same(renderer, filter.Renderer);
        }

        using Raster<Rgba8> raster = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        Assert.NotNull(Ink(raster));
    }

    [Fact]
    public void DisposedRendererThrows()
    {
        var renderer = new SubtitleRenderer(Cue("Gone", Style()), Options);
        renderer.Dispose();
        using Raster<Rgba8> raster = Raster.Create(64, 64, new Rgba8(0, 0, 0, 255));
        Assert.Throws<ObjectDisposedException>(() => renderer.Draw(raster, MediaTime.FromSeconds(2)));
    }
}
