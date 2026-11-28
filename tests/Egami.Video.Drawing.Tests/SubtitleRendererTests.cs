using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Editing;
using Egami.Video.Subtitles;
using Egami.Video.Subtitles.Rendering;

namespace Egami.Video.Drawing.Tests;

public class SubtitleRendererTests
{
    internal static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "fonts");

    internal static SubtitleRenderOptions Options => new() { FontFolders = [Fonts], UseSystemFonts = false };

    internal static SubtitleDocument Ass(string events, string style = "Default,DejaVu Sans,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,20,20,40,1", string info = "")
    {
        return SubtitleDocument.Parse($"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1920
            PlayResY: 1080
            ScaledBorderAndShadow: yes
            {info}

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: {style}

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            {events}
            """);
    }

    internal static YuvVideoFrame Frame(ChromaFormat chroma, int bitDepth, double seconds, ColorCodePoints? color = null, int width = 640, int height = 360)
    {
        YuvColor yuv = color?.MatrixCoefficients == 9 ? YuvColor.Bt2020Limited : YuvColor.Bt709Limited;
        YuvRaster raster = bitDepth == 8
            ? YuvRaster.Create(width, height, chroma, yuv)
            : YuvRaster.Create(width, height, chroma, bitDepth, yuv);
        Fill(raster);
        return new YuvVideoFrame(raster, MediaTime.FromSeconds(seconds), color: color);
    }

    /// <summary>Black in limited range: luma 16, chroma 128, at the raster's depth.</summary>
    private static void Fill(YuvRaster raster)
    {
        int shift = raster.BitDepth - 8;
        if (raster is YuvRaster<byte> bytes)
        {
            Fill(bytes, (byte)16, (byte)128);
        }
        else
        {
            Fill((YuvRaster<ushort>)raster, (ushort)(16 << shift), (ushort)(128 << shift));
        }
    }

    private static void Fill<T>(YuvRaster<T> raster, T luma, T chroma)
        where T : unmanaged
    {
        for (int y = 0; y < raster.Y.Height; y++)
        {
            raster.Y.Row(y).Fill(luma);
        }

        foreach (YuvPlane<T>? plane in new[] { raster.U, raster.V })
        {
            for (int y = 0; plane is not null && y < plane.Height; y++)
            {
                plane.Row(y).Fill(chroma);
            }
        }
    }

    internal static int MaxLuma(YuvRaster raster)
    {
        int max = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                max = Math.Max(max, raster is YuvRaster<byte> b ? b.Y.Row(y)[x] : ((YuvRaster<ushort>)raster).Y.Row(y)[x]);
            }
        }

        return max;
    }

    /// <summary>The rows that have any luma above black.</summary>
    internal static (int Top, int Bottom) InkRows(YuvRaster raster, int black)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                int v = raster is YuvRaster<byte> b ? b.Y.Row(y)[x] : ((YuvRaster<ushort>)raster).Y.Row(y)[x];
                if (v > black + 4)
                {
                    top = top < 0 ? y : top;
                    bottom = y;
                    break;
                }
            }
        }

        return (top, bottom);
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv420, 8)]
    [InlineData(ChromaFormat.Yuv422, 10)]
    [InlineData(ChromaFormat.Yuv444, 12)]
    [InlineData(ChromaFormat.Yuv420, 10)]
    public void DrawsWhiteTextIntoThePlanesAtTheFramesDepth(ChromaFormat chroma, int bitDepth)
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Hello world");
        using var renderer = new SubtitleRenderer(document, Options);
        using YuvVideoFrame frame = Frame(chroma, bitDepth, 2);
        renderer.Draw(frame);

        // Limited-range white is 235 at 8 bits, scaled for deeper samples; the text sits at the bottom, above the margin.
        Assert.Equal(235 << (bitDepth - 8), MaxLuma(frame.Raster));
        (int top, int bottom) = InkRows(frame.Raster, 16 << (bitDepth - 8));
        Assert.InRange(bottom, 300, 345);
        Assert.InRange(top, 280, bottom);
    }

    [Fact]
    public void LeavesFramesWithoutCuesAlone()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Hello world");
        using var renderer = new SubtitleRenderer(document, Options);
        using YuvVideoFrame before = Frame(ChromaFormat.Yuv420, 8, 0.5);
        using YuvVideoFrame after = Frame(ChromaFormat.Yuv420, 8, 3);
        renderer.Draw(before);
        renderer.Draw(after);
        Assert.Equal(16, MaxLuma(before.Raster));
        Assert.Equal(16, MaxLuma(after.Raster));
    }

    [Theory]
    [InlineData(16, 0.5807)]
    [InlineData(18, 0.75)]
    public void PutsHdrWhiteAtTheReferenceLevel(byte transfer, double signal)
    {
        // 203 cd/m² white: PQ's signal 0.58, HLG's 0.75 (ITU-R BT.2408).
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,HDR");
        using var renderer = new SubtitleRenderer(document, Options);
        using YuvVideoFrame frame = Frame(ChromaFormat.Yuv420, 10, 2, new ColorCodePoints(9, transfer, 9, false));
        renderer.Draw(frame);
        int expected = (int)Math.Round((16 + (219 * signal)) * 4);
        Assert.InRange(MaxLuma(frame.Raster), expected - 2, expected + 2);
    }

    [Fact]
    public void DrawsOntoRgbaRasters()
    {
        SubtitleDocument document = SubtitleDocument.Parse("1\n00:00:01,000 --> 00:00:03,000\n<i>Hello</i> world\n");
        using var renderer = new SubtitleRenderer(document, Options with { ForceStyle = s => s with { FontName = "DejaVu Sans" } });
        using Raster<Rgba8> raster = Raster.Create(1280, 720, new Rgba8(0, 0, 255, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        int white = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                white += p.R == 255 && p.G == 255 && p.B == 255 ? 1 : 0;
            }
        }

        Assert.True(white > 200, $"{white} white pixels");
    }

    [Fact]
    public void WrapsLongLinesEvenly()
    {
        // Thirty words at 48 pixels cannot fit 1880 pixels: two lines, and wrap style 0 makes them about as wide.
        string text = string.Join(' ', Enumerable.Repeat("word", 30));
        SubtitleDocument document = Ass($"Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{text}");
        using var renderer = new SubtitleRenderer(document, Options);
        using Raster<Rgba8> raster = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        List<(int Left, int Right)> lines = Lines(raster);
        Assert.Equal(2, lines.Count);
        Assert.InRange(Math.Abs((lines[0].Right - lines[0].Left) - (lines[1].Right - lines[1].Left)), 0, 200);
    }

    [Fact]
    public void BreaksAtHardBreaksAndAligns()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\\an7}One\\NTwo\\NThree");
        using var renderer = new SubtitleRenderer(document, Options);
        using Raster<Rgba8> raster = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        List<(int Left, int Right)> lines = Lines(raster);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.InRange(l.Left, 14, 24));
    }

    [Fact]
    public void DrawsTheOpaqueBoxOfBorderStyleThree()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Boxed",
            "Default,DejaVu Sans,48,&H00FFFFFF,&H000000FF,&H000000FF,&H00000000,0,0,0,0,100,100,0,0,3,4,0,2,20,20,40,1");
        using var renderer = new SubtitleRenderer(document, Options);
        using Raster<Rgba8> raster = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        int red = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                red += p.R == 255 && p.G == 0 && p.B == 0 ? 1 : 0;
            }
        }

        Assert.True(red > 2000, $"{red} red box pixels");
    }

    [Fact]
    public void ReportsMissingFontsAndDrawsWithAnother()
    {
        var warnings = new List<MediaWarning>();
        var context = MediaContext.Default with { Warnings = warnings.Add };
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Fallback",
            "Default,No Such Font,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,20,20,40,1");
        using var renderer = new SubtitleRenderer(document, Options, context);
        using Raster<Rgba8> raster = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));
        Assert.Contains(warnings, w => w.Code == "subtitle-font-missing" && w.Message.Contains("No Such Font", StringComparison.Ordinal));
        Assert.True(Lines(raster).Count == 1);
    }

    [Fact]
    public void ForceStyleChangesEveryStyle()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Big");
        using var small = new SubtitleRenderer(document, Options);
        using var big = new SubtitleRenderer(document, Options with { ForceStyle = s => s with { FontSize = 96 } });
        using Raster<Rgba8> a = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> b = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        small.Draw(a, MediaTime.FromSeconds(2));
        big.Draw(b, MediaTime.FromSeconds(2));
        Assert.True(Lines(b)[0].Right - Lines(b)[0].Left > 1.8 * (Lines(a)[0].Right - Lines(a)[0].Left));
    }

    [Fact]
    public void ReportsFeaturesItDoesNotDrawYet()
    {
        var warnings = new List<MediaWarning>();
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\\pos(100,100)\\blur2}Placed");
        using var renderer = new SubtitleRenderer(document, Options, MediaContext.Default with { Warnings = warnings.Add });
        Assert.Contains(warnings, w => w.Code == "subtitle-not-drawn" && w.Message.Contains("\\pos", StringComparison.Ordinal));
    }

    [Fact]
    public void BurnInFilterCopiesEachFrame()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Filtered");
        using var filter = new SubtitleBurnIn(document, Options);
        using YuvVideoFrame input = Frame(ChromaFormat.Yuv420, 8, 2);
        filter.Send(input);
        filter.Flush();
        Assert.True(filter.TryReceive(out YuvVideoFrame? output));
        using (output)
        {
            Assert.Equal(16, MaxLuma(input.Raster));
            Assert.Equal(235, MaxLuma(output.Raster));
            Assert.Equal(input.Timestamp, output.Timestamp);
        }

        Assert.False(filter.TryReceive(out _));
    }

    [Fact]
    public void EffectDrawsAtTheFramesTime()
    {
        SubtitleDocument document = Ass("Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Edited");
        using var renderer = new SubtitleRenderer(document, Options);
        IFrameEffect effect = renderer.AsEffect();
        using Raster<Rgba8> raster = Raster.Create(640, 360, new Rgba8(0, 0, 0, 255));
        effect.Apply(raster, new FrameEffectContext(MediaTime.FromSeconds(10), MediaTime.FromSeconds(2), 0.5));
        Assert.Single(Lines(raster));
    }

    [Fact]
    public void WebVttGetsTheDefaultBox()
    {
        SubtitleDocument document = SubtitleDocument.Parse("WEBVTT\n\n00:01.000 --> 00:03.000\nCaption\n");
        using var renderer = new SubtitleRenderer(document, Options with { ForceStyle = s => s with { FontName = "DejaVu Sans" } });
        using Raster<Rgba8> raster = Raster.Create(640, 360, new Rgba8(255, 255, 255, 255));
        renderer.Draw(raster, MediaTime.FromSeconds(2));

        // White under 80% black is 51.
        int box = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                box += p.R is >= 50 and <= 52 ? 1 : 0;
            }
        }

        Assert.True(box > 500, $"{box} box pixels");
    }

    /// <summary>The horizontal extent of each run of rows with anything drawn on a black raster.</summary>
    internal static List<(int Left, int Right)> Lines(Raster<Rgba8> raster)
    {
        Rgba8 background = raster.Row(0)[0];
        var lines = new List<(int, int)>();
        int left = int.MaxValue, right = -1;
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            int l = -1, r = -1;
            for (int x = 0; x < row.Length; x++)
            {
                if (Math.Abs(row[x].R - background.R) + Math.Abs(row[x].G - background.G) + Math.Abs(row[x].B - background.B) > 30)
                {
                    l = l < 0 ? x : l;
                    r = x;
                }
            }

            if (l >= 0)
            {
                left = Math.Min(left, l);
                right = Math.Max(right, r);
            }
            else if (right >= 0)
            {
                lines.Add((left, right));
                left = int.MaxValue;
                right = -1;
            }
        }

        if (right >= 0)
        {
            lines.Add((left, right));
        }

        return lines;
    }
}
