using System.Text.Json;

using Egami.Cli.Host;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Formats.Qoi;
using Egami.Operations;

namespace Egami.Cli.Tests;

/// <summary>Stage A: the everyday commands give what the same library calls give, with the right exit codes.</summary>
public sealed class EverydayCommandTests : IDisposable
{
    private const string Duck = "jpeg/duck-420.jpg";
    private const string Png = "pngsuite/basn6a08.png";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void ConvertWritesWhatSaveWrites()
    {
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("convert", png, "-o", "out.qoi");
        Assert.Equal(0, run.Code);
        using Raster image = Raster.Load(_tool.PathOf(png));
        using MemoryStream expected = new();
        image.Save(expected, QoiFormat.Info);
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(_tool.PathOf("out.qoi")));
    }

    [Fact]
    public void ConvertTurnsAGifIntoAnAnimatedCursor()
    {
        string gif = _tool.Copy("gif/loop-3.gif");
        ToolRun run = _tool.Run("convert", gif, "-o", "spin.ani", "--set", "ani.sizes=32,48", "--set", "ani.hotspot=4,4", "--set", "ani.title=Spinner");
        Assert.True(run.Code == 0, run.Error);
        using Raster source = Raster.Load(_tool.PathOf(gif));
        using Raster cursor = Raster.Load(_tool.PathOf("spin.ani"));
        Assert.Equal("ANI", cursor.Metadata.FormatName);
        Assert.Equal(source.FrameCount, cursor.FrameCount);
        Assert.Equal(2, Raster.Probe(_tool.PathOf("spin.ani")).ImageCount);
        Assert.Equal("Spinner", ((Egami.Formats.Ani.AniDetails)cursor.Metadata.FormatDetails!).Title);
    }

    [Fact]
    public void ResizeIsTheLibrarysTranscode()
    {
        string duck = _tool.Copy(Duck);
        Assert.Equal(0, _tool.Run("resize", duck, "-o", "fit.png", "--size", "100x100").Code);
        Assert.Equal(0, _tool.Run("resize", duck, "-o", "half.png", "--size", "50%", "--filter", "lanczos3").Code);
        // The decoder shrinks to no less than one and a half times the result: 150 x 113 for 100 x 75.
        Assert.Equal(Transcode(duck, ops => ops.Fit(100, 100), new ReaderOptions { MinimumSize = new Size(150, 113) }), File.ReadAllBytes(_tool.PathOf("fit.png")));
        // Halving leaves no factor of two to decode at, so the whole image is decoded and the filter does all the work.
        Assert.Equal(Transcode(duck, ops => ops.Resize(160, 120, ResampleFilter.Lanczos3), new ReaderOptions { MinimumSize = new Size(320, 240) }), File.ReadAllBytes(_tool.PathOf("half.png")));
    }

    [Fact]
    public void ResizeCoverAndPadFillTheBox()
    {
        string duck = _tool.Copy(Duck);
        Assert.Equal(0, _tool.Run("resize", duck, "-o", "cover.png", "--size", "100x100", "--fit", "cover", "--gravity", "top").Code);
        Assert.Equal(0, _tool.Run("resize", duck, "-o", "pad.png", "--size", "100x100", "--fit", "pad", "--background", "white").Code);
        Assert.Equal(Transcode(duck, ops => ops.Cover(100, 100, Anchor.Top), new ReaderOptions { MinimumSize = new Size(200, 150) }), File.ReadAllBytes(_tool.PathOf("cover.png")));
        using Raster pad = Raster.Load(_tool.PathOf("pad.png"));
        Assert.Equal((100, 100), (pad.Width, pad.Height));
    }

    [Fact]
    public void ThumbnailShrinksTurnsUprightAndStrips()
    {
        string duck = _tool.Copy(Duck);
        ToolRun run = _tool.Run("thumbnail", duck, "-o", "thumbs/", "--size", "64");
        Assert.Equal(0, run.Code);
        RasterInfo info = Raster.Probe(_tool.PathOf("thumbs/duck-420.jpg"));
        Assert.Equal((64, 48), (info.Width, info.Height));

        // Never enlarged.
        Assert.Equal(0, _tool.Run("thumbnail", duck, "-o", "big.png", "--size", "1000").Code);
        Assert.Equal(320, Raster.Probe(_tool.PathOf("big.png")).Width);
    }

    [Fact]
    public void CropRotateAndFlipMatchTheLibrary()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("crop", png, "--rect", "4,4,16,8", "-o", "crop.png").Code);
        Assert.Equal(0, _tool.Run("rotate", png, "30", "-o", "rotate.png").Code);
        Assert.Equal(0, _tool.Run("flip", png, "vertical", "-o", "flip.png").Code);
        Assert.Equal(Derive(png, ops => ops.Crop(4, 4, 16, 8)), File.ReadAllBytes(_tool.PathOf("crop.png")));
        Assert.Equal(Derive(png, ops => ops.Rotate(30)), File.ReadAllBytes(_tool.PathOf("rotate.png")));
        Assert.Equal(Derive(png, ops => ops.FlipVertical()), File.ReadAllBytes(_tool.PathOf("flip.png")));
    }

    [Fact]
    public void JpegRotatesLosslessly()
    {
        string duck = _tool.Copy(Duck);
        Assert.Equal(0, _tool.Run("rotate", duck, "90", "-o", "turned.jpg").Code);
        using MemoryStream expected = new();
        using (FileStream source = File.OpenRead(_tool.PathOf(duck)))
        {
            JpegTransform.Apply(source, expected, new JpegTransformOptions { Operation = JpegTransformOperation.Rotate90, Edges = JpegTransformEdges.Strict });
        }

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(_tool.PathOf("turned.jpg")));

        // --reencode decodes instead.
        Assert.Equal(0, _tool.Run("rotate", duck, "90", "-o", "decoded.jpg", "--reencode").Code);
        Assert.NotEqual(expected.ToArray(), File.ReadAllBytes(_tool.PathOf("decoded.jpg")));
    }

    [Fact]
    public void StandardInputAndOutput()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(Tool.Images, Png));
        ToolRun run = _tool.Run(["convert", "-", "-o", "-", "--format", "qoi"], png);
        Assert.Equal(0, run.Code);
        using Raster image = Raster.Load(png);
        using MemoryStream expected = new();
        image.Save(expected, QoiFormat.Info);
        Assert.Equal(expected.ToArray(), run.Bytes);

        // Without --format, standard output keeps the input's format.
        ToolRun same = _tool.Run(["convert", "-", "-o", "-"], png);
        Assert.Equal(PngFormat.Info, Raster.Probe(same.Bytes).Format);
    }

    [Fact]
    public void WriterSettingsReachTheWriter()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("convert", png, "-o", "fast.png", "--set", "png.compression-level=1").Code);
        Assert.Equal(0, _tool.Run("convert", png, "-o", "small.png", "--set", "compression-level=9").Code);
        Assert.Equal(Save(png, PngFormat.Info, new PngWriterOptions { CompressionLevel = 1 }), File.ReadAllBytes(_tool.PathOf("fast.png")));
        Assert.Equal(Save(png, PngFormat.Info, new PngWriterOptions { CompressionLevel = 9 }), File.ReadAllBytes(_tool.PathOf("small.png")));

        ToolRun quality = _tool.Run("convert", png, "-o", "q.png", "--quality", "50");
        Assert.Equal(0, quality.Code);
        Assert.Contains("PNG has no 'quality' setting", quality.Error, StringComparison.Ordinal);

        ToolRun unknown = _tool.Run("convert", png, "-o", "x.png", "--set", "png.speed=3");
        Assert.Equal(ExitCodes.Usage, unknown.Code);
        Assert.Contains("egami formats png", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToConvertsColorsWhileReading()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("convert", png, "-o", "p3.png", "--to", "display-p3").Code);
        using Raster expected = Raster.Load(_tool.PathOf(png), options: new ReaderOptions { ConvertColorTo = ColorProfile.DisplayP3 });
        using MemoryStream bytes = new();
        expected.Save(bytes, PngFormat.Info);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(_tool.PathOf("p3.png")));
    }

    [Fact]
    public void ExitCodesTellFailuresApart()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(ExitCodes.Usage, _tool.Run("resize", png, "-o", "x.png").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("resize", png, "-o", "x.png", "--size", "big").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", png).Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("frobnicate").Code);
        Assert.Equal(ExitCodes.Failed, _tool.Run("convert", "missing.png", "-o", "x.png").Code);
        File.WriteAllText(_tool.PathOf("junk.png"), "not an image");
        ToolRun corrupt = _tool.Run("convert", "junk.png", "-o", "x.png");
        Assert.Equal(ExitCodes.Failed, corrupt.Code);
        Assert.Contains("junk.png", corrupt.Error, StringComparison.Ordinal);

        ToolRun limited = _tool.Run("convert", png, "-o", "x.png", "--limit", "pixels=100");
        Assert.Equal(ExitCodes.Limit, limited.Code);
        Assert.Contains("--limit", limited.Error, StringComparison.Ordinal);

        _tool.Environment["EGAMI_LIMITS"] = "pixels=100";
        Assert.Equal(ExitCodes.Limit, _tool.Run("convert", png, "-o", "y.png").Code);
    }

    [Fact]
    public void InputsAreNeverReplacedUnlessAsked()
    {
        string png = _tool.Copy(Png);
        byte[] before = File.ReadAllBytes(_tool.PathOf(png));
        ToolRun refused = _tool.Run("convert", png, "-o", png, "--set", "compression-level=1");
        Assert.Equal(ExitCodes.Usage, refused.Code);
        Assert.Equal(before, File.ReadAllBytes(_tool.PathOf(png)));

        Assert.Equal(0, _tool.Run("convert", png, "--in-place", "--set", "compression-level=1").Code);
        Assert.NotEqual(before, File.ReadAllBytes(_tool.PathOf(png)));
    }

    [Fact]
    public void InfoDescribesWithoutDecoding()
    {
        string duck = _tool.Copy(Duck);
        string png = _tool.Copy(Png);
        ToolRun text = _tool.Run("info", duck, png);
        Assert.Equal(0, text.Code);
        Assert.Equal("duck-420.jpg: JPEG 320x240, RGB 8-bit\nbasn6a08.png: PNG 32x32, RGBA 8-bit, metadata\n", text.Text.Lines());

        ToolRun json = _tool.Run("info", "*.png", "--json", "--all");
        Assert.Equal(0, json.Code);
        using JsonDocument document = JsonDocument.Parse(json.Text);
        JsonElement file = document.RootElement.GetProperty("files")[0];
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("PNG", file.GetProperty("format").GetString());
        Assert.Equal(32, file.GetProperty("width").GetInt32());
        Assert.Equal("rgba", file.GetProperty("pixels").GetProperty("channels").GetString());
        Assert.Equal(JsonValueKind.Object, file.GetProperty("metadata").ValueKind);
    }

    [Fact]
    public void FormatsListsWriterSettings()
    {
        ToolRun list = _tool.Run("formats");
        Assert.Contains("PNG", list.Text, StringComparison.Ordinal);
        ToolRun png = _tool.Run("formats", "png");
        Assert.Contains("png.compression-level <int>  default 5", png.Text, StringComparison.Ordinal);
        Assert.Contains("png.palette.max-colors", png.Text, StringComparison.Ordinal);
        ToolRun json = _tool.Run("formats", "jpg", "--json");
        using JsonDocument document = JsonDocument.Parse(json.Text);
        Assert.Contains(document.RootElement.GetProperty("settings").EnumerateArray(), s => s.GetProperty("key").GetString() == "jpeg.quality");
    }

    private byte[] Transcode(string input, Action<RasterOps> ops, ReaderOptions? read = null)
    {
        using MemoryStream output = new();
        using (FileStream source = File.OpenRead(_tool.PathOf(input)))
        {
            Raster.Transcode(source, output, PngFormat.Info, ops, read);
        }

        return output.ToArray();
    }

    private byte[] Derive(string input, Action<RasterOps> ops)
    {
        using Raster image = Raster.Load(_tool.PathOf(input));
        image.Apply(ops);
        using MemoryStream output = new();
        image.Save(output, PngFormat.Info);
        return output.ToArray();
    }

    private byte[] Save(string input, FormatInfo format, WriterOptions options)
    {
        using Raster image = Raster.Load(_tool.PathOf(input));
        using MemoryStream output = new();
        image.Save(output, format, options);
        return output.ToArray();
    }
}
