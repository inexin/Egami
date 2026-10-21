using System.Text.Json;

using Egami.Analysis;
using Egami.Cli.Host;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Cli.Tests;

/// <summary>Stage C: compare, stats, frames, metadata, color, text, overlay and montage.</summary>
public sealed class MoreCommandTests : IDisposable
{
    private const string Duck = "jpeg/duck-420.jpg";
    private const string Png = "pngsuite/basn6a08.png";
    private const string Animation = "gif/loop-3.gif";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void CompareExitsWithOneOnADifference()
    {
        string duck = _tool.Copy(Duck);
        Expect(0, _tool.Run("convert", duck, "-o", "low.jpg", "--quality", "40"));
        ToolRun same = _tool.Run("compare", duck, duck);
        Assert.True(same.Code == 0, same.Error);
        Assert.Equal("0\n", same.Text.Lines());

        ToolRun differ = _tool.Run("compare", duck, "low.jpg");
        Assert.Equal(ExitCodes.Failed, differ.Code);
        using Raster a = Raster.Load(_tool.PathOf(duck));
        using Raster b = Raster.Load(_tool.PathOf("low.jpg"));
        CompareResult expected = Raster.Compare(a, b);
        Assert.Equal(expected.Overall.Rmse.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "\n", differ.Text.Lines());

        Expect(0, _tool.Run("compare", duck, "low.jpg", "--metric", "psnr", "--threshold", "25"));
        Expect(ExitCodes.Failed, _tool.Run("compare", duck, "low.jpg", "--metric", "psnr", "--threshold", "60"));
        Expect(0, _tool.Run("compare", duck, "low.jpg", "--metric", "ssim", "--threshold", "0.8"));

        Expect(ExitCodes.Failed, _tool.Run("compare", duck, "low.jpg", "--diff", "diff.png"));
        Assert.Equal((320, 240), Size(Raster.Probe(_tool.PathOf("diff.png"))));

        ToolRun json = _tool.Run("compare", duck, "low.jpg", "--json");
        using JsonDocument document = JsonDocument.Parse(json.Text);
        Assert.False(document.RootElement.GetProperty("identical").GetBoolean());
        Assert.True(document.RootElement.GetProperty("overall").GetProperty("ssim").GetDouble() < 1);
    }

    [Fact]
    public void CompareRefusesDifferentSizes()
    {
        string duck = _tool.Copy(Duck);
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("compare", duck, png);
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("differ in size", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void StatsMatchTheLibrary()
    {
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("stats", png, "--json", "--all");
        Assert.Equal(0, run.Code);
        using JsonDocument document = JsonDocument.Parse(run.Text);
        JsonElement file = document.RootElement.GetProperty("files")[0];
        using Raster image = Raster.Load(_tool.PathOf(png));
        RasterStatistics stats = image.Statistics();
        Assert.Equal(stats.Red.Mean, file.GetProperty("channels").GetProperty("red").GetProperty("mean").GetDouble(), 9);
        Assert.Equal(image.CountColors(), file.GetProperty("colors").GetInt64());
        Assert.Equal(image.Signature(), file.GetProperty("signature").GetString());
        Assert.False(file.GetProperty("opaque").GetBoolean());
    }

    [Fact]
    public void FramesSplitAndJoinRoundTrip()
    {
        string gif = _tool.Copy(Animation);
        ToolRun split = _tool.Run("frames", "split", gif, "-o", "f/{frame:00}.png", "--delays", "f/delays.txt");
        Assert.Equal(0, split.Code);
        using Raster original = Raster.Load(_tool.PathOf(gif));
        Assert.Equal(original.FrameCount, Directory.GetFiles(_tool.PathOf("f"), "*.png").Length);

        ToolRun join = _tool.Run("frames", "join", "f/*.png", "-o", "back.gif", "--delays", "f/delays.txt", "--loop", "3");
        Assert.Equal(0, join.Code);
        using Raster back = Raster.Load(_tool.PathOf("back.gif"));
        Assert.Equal(original.FrameCount, back.FrameCount);
        Assert.Equal(3, back.Metadata.LoopCount);
        Assert.True(Raster.Compare(original, back).IsIdentical);
        Assert.Equal(original.Signature(), back.Signature());

        Expect(ExitCodes.Usage, _tool.Run("frames", "split", gif, "-o", "same.png"));
    }

    [Fact]
    public void FramesJoinTakesADelayOrRate()
    {
        _tool.Copy(Png, "a.png");
        _tool.Copy(Png, "b.png");
        Expect(0, _tool.Run("frames", "join", "a.png", "b.png", "-o", "anim.png", "--fps", "25"));
        using Raster<Rgba8> animation = Raster.Load<Rgba8>(_tool.PathOf("anim.png"));
        Assert.Equal(2, animation.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(40), animation.Frames[1].Metadata.Delay);
        Expect(ExitCodes.Usage, _tool.Run("frames", "join", "a.png", "b.png", "-o", "x.gif", "--delay", "soon"));
        _tool.Copy(Duck, "c.jpg");
        Expect(ExitCodes.Usage, _tool.Run("frames", "join", "a.png", "c.jpg", "-o", "x.gif"));
    }

    [Fact]
    public void MetadataStripCopyAndShow()
    {
        string tagged = _tool.Copy("metadata/synthetic/im-exif.png", "tagged.png");
        ToolRun show = _tool.Run("metadata", "show", tagged, "--json");
        Assert.Equal(0, show.Code);
        using (JsonDocument document = JsonDocument.Parse(show.Text))
        {
            Assert.Contains("exif", document.RootElement.GetProperty("files")[0].GetProperty("metadata").GetProperty("blocks").EnumerateArray().Select(b => b.GetString()));
        }

        Expect(0, _tool.Run("metadata", "strip", tagged, "-o", "bare.png"));
        using (Raster bare = Raster.Load(_tool.PathOf("bare.png")))
        {
            Assert.Null(bare.Metadata.Exif);
            using Raster source = Raster.Load(_tool.PathOf(tagged));
            Assert.True(Raster.Compare(source, bare).IsIdentical);
        }

        Expect(0, _tool.Run("metadata", "copy", "bare.png", "--from", tagged, "--only", "exif", "-o", "again.png"));
        using (Raster again = Raster.Load(_tool.PathOf("again.png")))
        {
            Assert.NotNull(again.Metadata.Exif);
        }

        Expect(ExitCodes.Usage, _tool.Run("metadata", "strip", tagged, "-o", "x.png", "--keep", "gps"));
    }

    [Fact]
    public void JpegStripsWithoutDecoding()
    {
        string jpeg = _tool.Copy("metadata/synthetic/iptc-latin1.jpg", "tagged.jpg");
        Expect(0, _tool.Run("metadata", "strip", jpeg, "-o", "bare.jpg"));
        using MemoryStream expected = new();
        using (FileStream source = File.OpenRead(_tool.PathOf(jpeg)))
        {
            Formats.Jpeg.JpegTransform.Apply(source, expected, new Formats.Jpeg.JpegTransformOptions { CopyMetadata = false });
        }

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(_tool.PathOf("bare.jpg")));
    }

    [Fact]
    public void ColorConvertsProfiles()
    {
        string png = _tool.Copy(Png);
        Expect(0, _tool.Run("color", png, "-o", "p3.png", "--to", "display-p3", "--intent", "relative-colorimetric"));
        using Raster expected = Raster.Load(_tool.PathOf(png));
        expected.Apply(ops => ops.ConvertColor(ColorProfile.DisplayP3, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric }), TestContext.Current.CancellationToken);
        using MemoryStream bytes = new();
        expected.Save(bytes, PngFormat.Info);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(_tool.PathOf("p3.png")));
    }

    [Fact]
    public void TextAndOverlayDrawOnTheImage()
    {
        string duck = _tool.Copy(Duck);
        Expect(0, _tool.Run("text", duck, "(c) 2026 Egami", "-o", "text.png", "--anchor", "bottom-left", "--color", "yellow"));
        using Raster original = Raster.Load(_tool.PathOf(duck));
        using Raster text = Raster.Load(_tool.PathOf("text.png"));
        Assert.False(Raster.Compare(original, text).IsIdentical);
        CompareResult top = Raster.Compare(original, text, new CompareOptions { Region = new Rect(0, 0, 320, 120) });
        Assert.True(top.IsIdentical);

        // The same text twice gives the same bytes: the bundled font makes it independent of the machine.
        Expect(0, _tool.Run("text", duck, "(c) 2026 Egami", "-o", "again.png", "--anchor", "bottom-left", "--color", "yellow"));
        Assert.Equal(File.ReadAllBytes(_tool.PathOf("text.png")), File.ReadAllBytes(_tool.PathOf("again.png")));

        string logo = _tool.Copy(Png, "logo.png");
        Expect(0, _tool.Run("overlay", duck, logo, "-o", "overlay.png", "--anchor", "top-left", "--margin", "4"));
        using Raster expected = Raster.Load(_tool.PathOf(duck));
        using Raster mark = Raster.Load(_tool.PathOf(logo));
        expected.Apply(ops => ops.DrawImage(mark, new Point(4, 4), Operations.BlendMode.SourceOver), TestContext.Current.CancellationToken);
        using MemoryStream bytes = new();
        expected.Save(bytes, PngFormat.Info);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(_tool.PathOf("overlay.png")));
    }

    [Fact]
    public void MontageLaysOutAGrid()
    {
        for (int i = 0; i < 5; i++)
        {
            _tool.Copy(Png, $"t{i}.png");
        }

        ToolRun run = _tool.Run("montage", "t*.png", "-o", "sheet.png", "--tile", "40x40", "--columns", "3", "--spacing", "5", "--labels");
        Assert.Equal(0, run.Code);
        RasterInfo sheet = Raster.Probe(_tool.PathOf("sheet.png"));
        Assert.Equal((3 * 40) + (2 * 5), sheet.Width);
        Assert.True(sheet.Height > (2 * 40) + 5);
    }

    [Fact]
    public void GifSaysWhatItCannotWrite()
    {
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("convert", png, "-o", "x.svg");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("can read SVG but not write it", run.Error, StringComparison.Ordinal);
        Assert.NotNull(GifFormat.Info.Writer);
    }

    private static (int, int) Size(RasterInfo info) => (info.Width, info.Height);

    private static void Expect(int code, ToolRun run) => Assert.True(run.Code == code, $"exit {run.Code}, expected {code}: {run.Error}");
}
