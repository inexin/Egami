using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Svg.Tests;

/// <summary>Task 13: SvgCanvas writes canvas calls as SVG that Egami.Svg reads back.</summary>
public class SvgCanvasTests
{
    private static readonly Lazy<Typeface> s_noto = new(() => Typeface.FromFile(FontPath("NotoSans-Regular.ttf")));

    private static readonly Lazy<SvgFontOptions> s_fonts = new(() =>
    {
        FontCollection fonts = new();
        foreach (string file in (string[])["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSans-Italic.ttf", "shaping/NotoSansHebrew-Regular.ttf"])
        {
            fonts.AddFile(FontPath(file));
        }

        return new SvgFontOptions { Collection = fonts, UseSystemFonts = false, DefaultFamily = "Noto Sans" };
    });

    private static Typeface Noto => s_noto.Value;

    [Fact]
    public void FillsAndStrokesArePaths()
    {
        (string text, SvgDocument doc) = Write(c =>
        {
            c.FillRect(new RectF(10, 10, 30, 20), new SolidPaint(new RgbaF32(1, 0, 0, 0.5f)));
            c.StrokePath(Path.Line(new Vector2(0, 0), new Vector2(50, 50)), new Stroke(2) { Cap = LineCap.Round, Join = LineJoin.Bevel, Dash = new DashPattern([4, 2], 1) }, new Rgba8(0, 128, 255, 255));
            c.StrokeRect(new RectF(1, 1, 5, 5), Stroke.Hairline, new Rgba8(0, 0, 0, 255));
            Path evenOdd = new PathBuilder().AddPath(Path.Rect(new RectF(0, 0, 10, 10))).AddPath(Path.Rect(new RectF(2, 2, 4, 4))).ToPath(FillRule.EvenOdd);
            c.FillPath(evenOdd, new Rgba8(0, 255, 0, 255));
        });

        List<SvgElement> paths = Elements(doc, "path");
        Assert.Equal(4, paths.Count);
        Assert.Equal(Path.Rect(new RectF(10, 10, 30, 20)).ToSvg(), paths[0].Attributes["d"]);
        Assert.Equal("#ff0000", paths[0].Attributes["fill"]);
        Assert.Equal("0.5", paths[0].Attributes["fill-opacity"]);

        SvgElement line = paths[1];
        Assert.Equal("none", line.Attributes["fill"]);
        Assert.Equal("#0080ff", line.Attributes["stroke"]);
        Assert.Equal("2", line.Attributes["stroke-width"]);
        Assert.Equal("round", line.Attributes["stroke-linecap"]);
        Assert.Equal("bevel", line.Attributes["stroke-linejoin"]);
        Assert.Equal("4 2", line.Attributes["stroke-dasharray"]);
        Assert.Equal("1", line.Attributes["stroke-dashoffset"]);

        Assert.Equal("non-scaling-stroke", paths[2].Attributes["vector-effect"]);
        Assert.Equal("evenodd", paths[3].Attributes["fill-rule"]);
        Assert.Contains("viewBox=\"0 0 100 80\"", text, StringComparison.Ordinal);
        Assert.Equal("100", doc.Root.Attributes["width"]);
    }

    [Fact]
    public void TransformsAreWrittenPerElement()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            c.FillRect(new RectF(0, 0, 1, 1), new Rgba8(0, 0, 0, 255));
            c.Translate(5, -0.00001f);
            c.FillRect(new RectF(0, 0, 1, 1), new Rgba8(0, 0, 0, 255));
            c.Scale(2, 3);
            c.FillRect(new RectF(0, 0, 1, 1), new Rgba8(0, 0, 0, 255));
        });

        List<SvgElement> paths = Elements(doc, "path");
        Assert.Null(paths[0].Attributes["transform"]);
        Assert.Equal("translate(5 0)", paths[1].Attributes["transform"]);
        Assert.Equal("matrix(2 0 0 3 5 0)", paths[2].Attributes["transform"]);
    }

    [Fact]
    public void NumbersFollowThePrecision()
    {
        (string text, _) = Write(c => c.FillRect(new RectF(0.123456f, -0.0001f, 10.5f, 3), new Rgba8(0, 0, 0, 255)), new SvgWriteOptions { Precision = 1 });
        Assert.Contains("0.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-0 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.12", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactWritesNoIndentation()
    {
        (string text, _) = Write(c => c.FillRect(new RectF(0, 0, 5, 5), new Rgba8(0, 0, 0, 255)), new SvgWriteOptions { Compact = true });
        Assert.DoesNotContain("\n", text.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void EqualGradientsAreSharedInDefs()
    {
        LinearGradient linear = new(new Vector2(0, 0), new Vector2(100, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 0.5f)) { Tile = TileMode.Mirror };
        (_, SvgDocument doc) = Write(c =>
        {
            c.FillRect(new RectF(0, 0, 50, 50), linear);
            c.FillRect(new RectF(50, 0, 50, 50), linear with { });
            c.FillCircle(new Vector2(50, 50), 20, new RadialGradient(new Vector2(50, 50), 20, new RgbaF32(1, 1, 1, 1), new RgbaF32(0, 0, 0, 1)));
            c.FillCircle(new Vector2(50, 50), 20, new ConicalGradient(new Vector2(45, 50), 0, new Vector2(50, 50), 20, [new GradientStop(0, new RgbaF32(1, 1, 0, 1)), new GradientStop(1, new RgbaF32(0, 1, 0, 1))]));
        });

        SvgElement gradient = Assert.Single(Elements(doc, "linearGradient"));
        Assert.Equal("userSpaceOnUse", gradient.Attributes["gradientUnits"]);
        Assert.Equal("reflect", gradient.Attributes["spreadMethod"]);
        // The alpha changes between the stops: seven stops between them mix it premultiplied, as Egami does.
        Assert.Equal(9, gradient.Elements("stop").Count());
        Assert.Equal("0.5", gradient.Elements("stop").Last().Attributes["stop-opacity"]);
        Assert.Equal("0.9375", gradient.Elements("stop").ElementAt(1).Attributes["stop-opacity"]);
        List<SvgElement> paths = Elements(doc, "path");
        Assert.Equal($"url(#{gradient.Id})", paths[0].Attributes["fill"]);
        Assert.Equal(paths[0].Attributes["fill"], paths[1].Attributes["fill"]);

        List<SvgElement> radial = Elements(doc, "radialGradient");
        Assert.Equal(2, radial.Count);
        Assert.Equal("45", radial[1].Attributes["fx"]);
        Assert.Empty(Elements(doc, "image"));
    }

    [Fact]
    public void LinearLightGradientsGetStopsMixedInLinearLight()
    {
        (_, SvgDocument doc) = Write(c => c.FillRect(new RectF(0, 0, 100, 80), new LinearGradient(Vector2.Zero, new Vector2(100, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1)) { Interpolation = GradientInterpolation.LinearSrgb }));
        List<SvgElement> stops = [.. Assert.Single(Elements(doc, "linearGradient")).Elements("stop")];
        Assert.Equal(9, stops.Count);
        Assert.Equal("0.5", stops[4].Attributes["offset"]);
        Assert.Equal("#bc00bc", stops[4].Attributes["stop-color"]);
    }

    [Fact]
    public void GradientsSvgCannotExpressBecomeImages()
    {
        List<EgamiDiagnostic> diagnostics = [];
        SvgWriteOptions options = new() { Context = new EgamiContext { Diagnostics = diagnostics.Add, DiagnosticLevel = DiagnosticLevel.Info } };
        (_, SvgDocument doc) = Write(c =>
        {
            c.FillRect(new RectF(10, 10, 40, 40), new SweepGradient(new Vector2(30, 30), [new GradientStop(0, new RgbaF32(1, 0, 0, 1)), new GradientStop(1, new RgbaF32(0, 0, 1, 1))]));
            c.FillRect(new RectF(50, 10, 40, 40), new ConicalGradient(new Vector2(20, 20), 5, new Vector2(80, 30), 10, [new GradientStop(0, new RgbaF32(1, 0, 0, 1)), new GradientStop(1, new RgbaF32(0, 0, 1, 1))]));
            c.FillRect(new RectF(0, 50, 40, 20), new LinearGradient(new Vector2(0, 0), new Vector2(40, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1)) { Interpolation = GradientInterpolation.Oklab });
            c.FillRect(new RectF(40, 50, 40, 20), NoisePaint.Fractal(0.05f, 0.05f, 2));
        }, options);

        List<SvgElement> images = Elements(doc, "image");
        Assert.Equal(4, images.Count);
        Assert.Empty(Elements(doc, "path"));
        foreach (SvgElement image in images)
        {
            using Raster decoded = DecodeHref(image);
            Assert.True(decoded.Width > 0);
        }

        // The sweep gradient's image covers its rectangle: 40 device pixels plus the outset, within the clip.
        Assert.InRange(float.Parse(images[0].Attributes["width"]!, System.Globalization.CultureInfo.InvariantCulture), 40, 44);
        Assert.Contains(diagnostics, d => d.Id == "Svg.FallbackImage" && d.Level == DiagnosticLevel.Info);
    }

    [Fact]
    public void ClipsAreBalancedGroups()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            for (int i = 0; i < 60; i++)
            {
                c.Save();
                c.ClipRect(new RectF(i * 0.1f, 0, 100, 80));
                if (i % 3 == 0)
                {
                    c.ClipPath(Path.Oval(new RectF(0, 0, 90, 70)), ClipOp.Difference);
                }

                c.FillRect(new RectF(0, 0, 10, 10), new Rgba8(0, 0, 0, 255));
            }

            c.RestoreToCount(30);
            c.ClipRoundRect(new RoundRect(new RectF(0, 0, 50, 50), 5));
            c.FillRect(new RectF(0, 0, 10, 10), new Rgba8(0, 0, 0, 255));

            // Left open: dispose closes them.
            c.Save();
            c.ClipRect(new RectF(0, 0, 20, 20));
        });

        // Clips nest no groups: each clipPath's path is clipped by the one before, and each element names the last.
        Assert.Empty(Elements(doc, "g"));
        List<SvgElement> clips = Elements(doc, "clipPath");
        Assert.Equal(60 + 20 + 1 + 1, clips.Count);
        Assert.Null(clips[0].Elements("path").Single().Attributes["clip-path"]);
        Assert.Equal($"url(#{clips[0].Id})", clips[1].Elements("path").Single().Attributes["clip-path"]);
        List<SvgElement> drawn = [.. Elements(doc, "path").Where(p => p.Parent?.Name != "clipPath")];
        Assert.Equal(61, drawn.Count);
        Assert.Equal($"url(#{clips[1].Id})", drawn[0].Attributes["clip-path"]);
        Assert.Equal($"url(#{clips[^2].Id})", drawn[^1].Attributes["clip-path"]);

        // Chains are cut at 16 clipPaths, so readers that limit how deep references nest draw them all.
        Dictionary<string, SvgElement> byId = clips.ToDictionary(c => c.Id!, StringComparer.Ordinal);
        foreach (SvgElement clip in clips)
        {
            int depth = 1;
            for (SvgElement at = clip; at.Elements("path").SingleOrDefault()?.Attributes["clip-path"] is string url; at = byId[url[5..^1]])
            {
                depth++;
            }

            Assert.True(depth <= 16, $"{clip.Id} chains {depth} clipPaths.");
        }
    }

    [Fact]
    public void TransformedElementsAreClippedInDeviceCoordinates()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            c.ClipRect(new RectF(10, 10, 50, 50));
            c.Scale(2);
            c.FillRect(new RectF(0, 0, 20, 20), new Rgba8(0, 0, 0, 255));
        });

        // A clip-path on the scaled path would be read in its scaled coordinates: a group without a transform carries it.
        SvgElement path = Assert.Single(Elements(doc, "path"), p => p.Parent?.Name != "clipPath");
        Assert.Null(path.Attributes["clip-path"]);
        Assert.Equal("matrix(2 0 0 2 0 0)", path.Attributes["transform"]);
        Assert.Null(path.Parent!.Attributes["transform"]);
        Assert.NotNull(path.Parent.Attributes["clip-path"]);
    }

    [Fact]
    public void EqualClipsAreShared()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            for (int i = 0; i < 5; i++)
            {
                c.Save();
                c.ClipRect(new RectF(10, 10, 50, 50));
                c.FillRect(new RectF(0, 0, 100, 100), new Rgba8(0, 0, 0, 255));
                c.Restore();
            }
        });

        SvgElement clip = Assert.Single(Elements(doc, "clipPath"));
        Assert.All(Elements(doc, "path").Where(p => p.Parent?.Name != "clipPath"), p => Assert.Equal($"url(#{clip.Id})", p.Attributes["clip-path"]));
    }

    [Fact]
    public void DeepNestingStaysWellFormed()
    {
        (string text, SvgDocument doc) = Write(c =>
        {
            for (int i = 0; i < 200; i++)
            {
                if (i % 2 == 0)
                {
                    c.SaveLayer(0.99f);
                }
                else
                {
                    c.Save();
                }

                c.ClipRect(new RectF(0, 0, 100 - (i * 0.1f), 80));
            }

            c.FillRect(new RectF(0, 0, 10, 10), new Rgba8(0, 0, 0, 255));
        });

        Assert.Single(Elements(doc, "path"), p => p.Parent?.Name != "clipPath");
        Assert.Equal(100, Elements(doc, "g").Count);
        Assert.EndsWith("</svg>", text.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void LayersAreGroups()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            c.SaveLayer(new LayerOptions { Opacity = 0.5f, BlendMode = BlendMode.Multiply });
            c.FillRect(new RectF(0, 0, 50, 50), new Rgba8(255, 0, 0, 255));
            c.Restore();

            c.SaveLayer(new LayerOptions { Bounds = new RectF(0, 0, 40, 40), BlendMode = BlendMode.Plus, Isolated = false });
            c.FillRect(new RectF(0, 0, 50, 50), new Rgba8(255, 0, 0, 255));
            c.Restore();

            c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillCircle(new Vector2(25, 25), 20, new Rgba8(255, 255, 255, 255))) });
            c.FillRect(new RectF(0, 0, 50, 50), new Rgba8(0, 0, 255, 255));
            c.Restore();

            c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillCircle(new Vector2(25, 25), 20, new Rgba8(0, 0, 0, 128)), SoftMaskKind.Alpha) });
            c.FillRect(new RectF(0, 0, 50, 50), new Rgba8(0, 0, 255, 255));
            c.Restore();

            c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillCircle(new Vector2(25, 25), 20, new Rgba8(0, 0, 0, 255)), backdrop: new RgbaF32(1, 1, 1, 1)) });
            c.FillRect(new RectF(0, 0, 50, 50), new Rgba8(0, 0, 255, 255));
            c.Restore();
        });

        List<SvgElement> groups = Elements(doc, "g").Where(g => g.Parent?.Name is "svg").ToList();
        Assert.Equal("0.5", groups[0].Attributes["opacity"]);
        Assert.Equal("mix-blend-mode:multiply;isolation:isolate", groups[0].Attributes["style"]);
        Assert.NotNull(groups[1].Attributes["clip-path"]);
        Assert.Equal("mix-blend-mode:plus-lighter", groups[1].Attributes["style"]);
        Assert.Null(groups[1].Elements("path").Single().Attributes["clip-path"]);

        List<SvgElement> masks = Elements(doc, "mask");
        Assert.Equal(3, masks.Count);
        Assert.Equal("userSpaceOnUse", masks[0].Attributes["maskUnits"]);
        Assert.Equal($"url(#{masks[0].Id})", groups[2].Attributes["mask"]);
        Assert.Contains(masks[1].Descendants(), e => e.Attributes["filter"] is not null);
        Assert.Single(Elements(doc, "feColorMatrix"));

        // The white backdrop is a rectangle under the mask's drawing.
        List<SvgElement> backdrop = [.. masks[2].Descendants().Where(e => e.Name == "path")];
        Assert.Equal(2, backdrop.Count);
        Assert.Equal("#ffffff", backdrop[0].Attributes["fill"]);
    }

    [Fact]
    public void FilteredLayersBecomeOneImage()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            c.FillRect(new RectF(0, 0, 10, 10), new Rgba8(0, 0, 0, 255));
            c.SaveLayer(new LayerOptions { Filter = o => o.Blur(3) });
            c.Save();
            c.ClipRect(new RectF(10, 10, 60, 60));
            c.FillCircle(new Vector2(40, 40), 20, new Rgba8(255, 0, 0, 255));
            c.DrawText("x", new Vector2(10, 70), new Font(Noto, 12), new Rgba8(0, 0, 0, 255));
            c.SaveLayer(0.5f);
            c.FillRect(new RectF(30, 30, 10, 10), new Rgba8(0, 255, 0, 255));
            c.Restore();
            c.Restore();
            c.Restore();
            c.FillRect(new RectF(90, 0, 10, 10), new Rgba8(0, 0, 0, 255));
        });

        SvgElement image = Assert.Single(Elements(doc, "image"));
        Assert.Equal(2, Elements(doc, "path").Count);
        Assert.Empty(Elements(doc, "g"));
        using Raster raster = DecodeHref(image);
        Assert.Equal(100, raster.Width);
        using Raster<Rgba8> rgba = raster.As<Rgba8>();
        Assert.True(rgba.Row(40)[40].A > 200);
    }

    [Fact]
    public void LayersLeftOpenAreFinishedByDispose()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            c.SaveLayer(0.5f);
            c.ClipRect(new RectF(5, 5, 50, 50));
            c.SaveLayer(new LayerOptions { Backdrop = o => o.Blur(2) });
            c.Save();
            c.FillRect(new RectF(0, 0, 30, 30), new Rgba8(255, 0, 0, 255));
            c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.Clear(new Rgba8(255, 255, 255, 255)), transfer: v => 1 - v) });
        });

        SvgElement layer = Assert.Single(Elements(doc, "g"));
        Assert.Equal("0.5", layer.Attributes["opacity"]);
        SvgElement image = Assert.Single(layer.Elements("image"));
        Assert.NotNull(image.Attributes["clip-path"]);
    }

    [Fact]
    public void ImagesAreDataUrisDefinedOnce()
    {
        using Raster<Rgba8> image = Raster.Create(8, 6, new Rgba8(10, 200, 30, 255));
        image.Row(0)[0] = new Rgba8(255, 0, 0, 128);
        (_, SvgDocument doc) = Write(c =>
        {
            c.DrawImage(image, new Vector2(5, 5));
            c.DrawImage(image, new RectF(0, 0, 16, 12), new SamplingOptions { Filter = ResampleFilter.Nearest }, new SolidPaint(new RgbaF32(1, 1, 1, 1)) { Opacity = 0.5f });
            c.DrawImage(image, new RectF(2, 2, 4, 2), new RectF(50, 50, 20, 10));
        });

        SvgElement definition = Assert.Single(Elements(doc, "image"));
        Assert.StartsWith("data:image/png;base64,", Href(definition), StringComparison.Ordinal);
        using (Raster decoded = DecodeHref(definition))
        using (Raster<Rgba8> rgba = decoded.As<Rgba8>())
        {
            Assert.Equal(8, rgba.Width);
            Assert.Equal(new Rgba8(255, 0, 0, 128), rgba.Row(0)[0]);
            Assert.Equal(new Rgba8(10, 200, 30, 255), rgba.Row(5)[7]);
        }

        List<SvgElement> uses = Elements(doc, "use");
        Assert.Equal(3, uses.Count);
        Assert.All(uses, u => Assert.Equal("#" + definition.Id, Href(u)));
        Assert.Equal("translate(5 5)", uses[0].Attributes["transform"]);
        Assert.Equal("matrix(2 0 0 2 0 0)", uses[1].Attributes["transform"]);
        Assert.Equal("0.5", uses[1].Attributes["opacity"]);
        Assert.Contains("pixelated", uses[1].Attributes["style"], StringComparison.Ordinal);
        // A part of the image: placed so the part fills the destination, and clipped to it.
        SvgElement crop = uses[2].Parent!;
        Assert.Equal("matrix(5 0 0 5 40 40)", uses[2].Attributes["transform"]);
        Assert.Equal(Path.Rect(new RectF(50, 50, 20, 10)).ToSvg(), doc.FindById(crop.Attributes["clip-path"]![5..^1])!.Elements("path").Single().Attributes["d"]);
    }

    [Fact]
    public void JpegIsUsedForOpaqueImagesOnly()
    {
        using Raster<Rgba8> opaque = Raster.Create(4, 4, new Rgba8(10, 200, 30, 255));
        using Raster<Rgba8> clear = Raster.Create(4, 4, new Rgba8(10, 200, 30, 100));
        (_, SvgDocument doc) = Write(c =>
        {
            c.DrawImage(opaque, Vector2.Zero);
            c.DrawImage(clear, Vector2.Zero);
        }, new SvgWriteOptions { ImageFormat = SvgImageFormat.Jpeg });

        List<SvgElement> images = Elements(doc, "image");
        Assert.StartsWith("data:image/jpeg;base64,", Href(images[0]), StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", Href(images[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void EncodedPngKeepsItsBytes()
    {
        using Raster<Rgba8> image = Raster.Create(3, 3, new Rgba8(1, 2, 3, 255));
        using MemoryStream png = new();
        image.Save(png, "png");
        EncodedImage encoded = EncodedImage.Load(png.ToArray());
        (_, SvgDocument doc) = Write(c => c.DrawImage(encoded, new RectF(0, 0, 30, 30)));
        string href = Href(Assert.Single(Elements(doc, "image")));
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png.ToArray()), href);
    }

    [Fact]
    public void PatternsForImageAndPicturePaints()
    {
        using Raster<Rgba8> tile = Raster.Create(4, 4, new Rgba8(200, 0, 0, 255));
        PictureRecorder recorder = new(new RectF(0, 0, 10, 10));
        recorder.FillCircle(new Vector2(5, 5), 4, new Rgba8(0, 0, 200, 255));
        Picture picture = recorder.Finish();
        (_, SvgDocument doc) = Write(c =>
        {
            c.FillRect(new RectF(0, 0, 40, 40), new ImagePaint(tile, TileMode.Repeat, TileMode.Mirror) { LocalMatrix = Matrix3x2.CreateScale(2) });
            c.FillRect(new RectF(0, 0, 8, 8), new ImagePaint(tile) { LocalMatrix = Matrix3x2.CreateScale(2) });
            c.FillRect(new RectF(40, 0, 40, 40), new PicturePaint(picture));
            c.FillRect(new RectF(0, 40, 40, 40), new ImagePaint(tile));
        });

        List<SvgElement> patterns = Elements(doc, "pattern");
        Assert.Equal(3, patterns.Count);
        Assert.Equal("4", patterns[0].Attributes["width"]);
        Assert.Equal("8", patterns[0].Attributes["height"]);
        Assert.Equal(2, patterns[0].Elements("use").Count());
        Assert.Equal("matrix(2 0 0 2 0 0)", patterns[0].Attributes["patternTransform"]);
        Assert.Single(patterns[1].Elements("use"));
        Assert.Single(Elements(doc, "g"), g => g.Id?.StartsWith("pic", StringComparison.Ordinal) == true);

        // The clamped image reaches past its edges: drawn as an image.
        Assert.Equal(2, Elements(doc, "image").Count);
    }

    [Fact]
    public void PicturesAreDefinedOnceAndUsed()
    {
        PictureRecorder recorder = new(new RectF(-5, -5, 10, 10));
        recorder.FillCircle(Vector2.Zero, 5, new Rgba8(0, 128, 0, 255));
        recorder.StrokeRect(new RectF(-4, -4, 8, 8), new Stroke(1), new Rgba8(0, 0, 0, 255));
        Picture picture = recorder.Finish();
        (_, SvgDocument doc) = Write(c =>
        {
            c.DrawPicture(picture, Matrix3x2.CreateTranslation(20, 20));
            c.DrawPicture(picture, Matrix3x2.CreateTranslation(60, 20), new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Opacity = 0.5f });
        });

        SvgElement definition = Assert.Single(Elements(doc, "g"), g => g.Id is not null);
        Assert.Equal(2, definition.Elements("path").Count());
        Assert.Equal("translate(-5 -5)", definition.Attributes["transform"]);
        List<SvgElement> uses = Elements(doc, "use");
        Assert.Equal(2, uses.Count);
        Assert.Equal("translate(60 20)", uses[1].Attributes["transform"]);
        Assert.Equal("0.5", uses[1].Attributes["opacity"]);
    }

    [Fact]
    public void GlyphOutlinesAreWrittenOnceAndReused()
    {
        Font font = new(Noto, 20);
        (_, SvgDocument doc) = Write(c =>
        {
            c.DrawText("abba", new Vector2(10, 30), font, new Rgba8(0, 0, 0, 255));
            c.DrawText("ab", new Vector2(10, 60), font with { }, new Rgba8(255, 0, 0, 255));
            c.DrawText("ab", new Vector2(10, 60), new Font(Noto, 33), new Rgba8(255, 0, 0, 255));
        });

        List<SvgElement> glyphs = Elements(doc, "path").Where(p => p.Id?.StartsWith("gl", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, glyphs.Count);
        List<SvgElement> uses = Elements(doc, "use");
        Assert.Equal(8, uses.Count);
        Assert.Equal(Href(uses[0]), Href(uses[3]));

        // A glyph is in font units: the group scales it to the size.
        SvgElement group = uses[0].Parent!;
        float scale = 20f / Noto.UnitsPerEm;
        Assert.Equal($"matrix({scale.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture)} 0 0 {scale.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture)} 0 0)", group.Attributes["transform"]);
        Assert.Equal("#000000", group.Attributes["fill"]);
    }

    [Fact]
    public void GradientTextIsAPath()
    {
        (_, SvgDocument doc) = Write(c => c.DrawText("Hi", new Vector2(10, 30), new Font(Noto, 20), new LinearGradient(Vector2.Zero, new Vector2(40, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1))));
        SvgElement path = Assert.Single(Elements(doc, "path"));
        Assert.StartsWith("url(#", path.Attributes["fill"], StringComparison.Ordinal);
    }

    [Fact]
    public void TextModeWritesTextElements()
    {
        (_, SvgDocument doc) = Write(
            c =>
            {
                c.DrawText("Office fit", new Vector2(10, 30), new Font(Noto, 20), new Rgba8(0, 0, 0, 255));
                c.DrawText("Grad", new Vector2(10, 60), new Font(Noto, 20) { Synthesis = FontSynthesis.Bold | FontSynthesis.Italic }, new LinearGradient(Vector2.Zero, new Vector2(40, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1)));
            },
            new SvgWriteOptions { Text = SvgTextMode.Text });

        List<SvgElement> texts = Elements(doc, "text");
        Assert.Equal(2, texts.Count);
        Assert.Equal("Office fit", texts[0].Text);
        Assert.Equal("'Noto Sans'", texts[0].Attributes["font-family"]);
        Assert.Equal("20", texts[0].Attributes["font-size"]);
        Assert.Equal("preserve", texts[0].Attributes.Get("space", SvgNamespaces.Xml));
        List<SvgElement> spans = [.. texts[0].Elements("tspan")];
        Assert.Equal(7, spans.Count); // O, the ffi ligature, c, e, space, the fi ligature, t
        Assert.Equal("10", spans[0].Attributes["x"]);
        Assert.Equal("30", spans[0].Attributes["y"]);
        // Synthetic bold and italic as Egami draws them: a stroke and a slant about the baseline.
        Assert.Null(texts[1].Attributes["font-weight"]);
        Assert.Null(texts[1].Attributes["font-style"]);
        Assert.Equal("round", texts[1].Attributes["stroke-linejoin"]);
        Assert.Equal(texts[1].Attributes["fill"], texts[1].Attributes["stroke"]);
        Assert.StartsWith("matrix(1 0 -0.2493", texts[1].Attributes["transform"], StringComparison.Ordinal);
        Assert.StartsWith("url(#", texts[1].Attributes["fill"], StringComparison.Ordinal);
        Assert.Empty(Elements(doc, "style"));
    }

    [Fact]
    public void RightToLeftTextIsOneChunkAnchoredAtItsEnd()
    {
        Typeface hebrew = Typeface.FromFile(FontPath("shaping/NotoSansHebrew-Regular.ttf"));
        (_, SvgDocument doc) = Write(c => c.DrawText("שלום", new Vector2(10, 30), new Font(hebrew, 20), new Rgba8(0, 0, 0, 255)), new SvgWriteOptions { Text = SvgTextMode.Text });
        SvgElement span = Assert.Single(Assert.Single(Elements(doc, "text")).Elements("tspan"));
        Assert.Equal("שלום", span.Text);
        Assert.Equal("end", span.Attributes["text-anchor"]);
        Assert.True(float.Parse(span.Attributes["x"]!, System.Globalization.CultureInfo.InvariantCulture) > 30);
    }

    [Fact]
    public void EmbedSubsetWritesFontFace()
    {
        (string text, SvgDocument doc) = Write(
            c => c.DrawText("Embedded", new Vector2(10, 30), new Font(Noto, 20), new Rgba8(0, 0, 0, 255)),
            new SvgWriteOptions { Text = SvgTextMode.Text, EmbedSubset = true });

        SvgElement style = Assert.Single(Elements(doc, "style"));
        Assert.Contains("@font-face{font-family:'egami-font-1';src:url(data:font/ttf;base64,", style.Text, StringComparison.Ordinal);
        Assert.Equal("'egami-font-1', 'Noto Sans'", Assert.Single(Elements(doc, "text")).Attributes["font-family"]);

        // The subset is a font Egami reads back, holding the text's glyphs.
        int start = style.Text.IndexOf("base64,", StringComparison.Ordinal) + 7;
        byte[] data = Convert.FromBase64String(style.Text[start..style.Text.IndexOf(')', start)]);
        Typeface subset = Typeface.FromMemory(data);
        Assert.True(subset.TryGetGlyph('E', out _));
        Assert.True(text.Length < 200_000);
    }

    [Fact]
    public void TransformedRunsUseTransformedGlyphs()
    {
        Path arc = Path.Arc(new RectF(10, 10, 80, 60), 180, 180);
        (_, SvgDocument doc) = Write(c => c.DrawTextOnPath("arc", arc, new Font(Noto, 12), new Rgba8(0, 0, 0, 255)));
        List<SvgElement> uses = Elements(doc, "use");
        Assert.Equal(3, uses.Count);
        Assert.All(uses, u => Assert.StartsWith("matrix(", u.Attributes["transform"], StringComparison.Ordinal));
    }

    [Fact]
    public void BlurIsAFilterAndMeshesAreImages()
    {
        Mesh mesh = new(MeshMode.Triangles, [new Vector2(0, 0), new Vector2(50, 0), new Vector2(0, 50)], [new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 1, 0, 1), new RgbaF32(0, 0, 1, 1)]);
        (_, SvgDocument doc) = Write(c =>
        {
            c.FillCircle(new Vector2(50, 40), 20, new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Blur = new MaskBlur(4) });
            c.DrawMesh(mesh, new SolidPaint(new RgbaF32(1, 1, 1, 1)));
            c.FillCircle(new Vector2(50, 40), 10, new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Blur = new MaskBlur(4, BlurStyle.Outer) });
        });

        SvgElement blur = Assert.Single(Elements(doc, "feGaussianBlur"));
        Assert.Equal("4", blur.Attributes["stdDeviation"]);
        Assert.Equal(2, Elements(doc, "image").Count);
    }

    [Fact]
    public void BlendModesUseCssOrApproximate()
    {
        List<EgamiDiagnostic> diagnostics = [];
        (_, SvgDocument doc) = Write(
            c =>
            {
                c.Clear(new Rgba8(255, 255, 255, 255));
                c.FillRect(new RectF(0, 0, 10, 10), new SolidPaint(new RgbaF32(1, 0, 0, 1)) { BlendMode = BlendMode.Screen });
                c.FillRect(new RectF(0, 0, 10, 10), new SolidPaint(new RgbaF32(1, 0, 0, 1)) { BlendMode = BlendMode.Xor });
                c.FillRect(new RectF(0, 0, 10, 10), new SolidPaint(new RgbaF32(1, 0, 0, 1)) { BlendMode = BlendMode.Destination });
            },
            new SvgWriteOptions { Context = new EgamiContext { Diagnostics = diagnostics.Add } });

        List<SvgElement> paths = Elements(doc, "path");
        Assert.Equal(3, paths.Count);
        Assert.Equal("M0 0 L100 0 L100 80 L0 80 Z", paths[0].Attributes["d"]);
        Assert.Equal("mix-blend-mode:screen", paths[1].Attributes["style"]);
        Assert.Null(paths[2].Attributes["style"]);
        Assert.Contains(diagnostics, d => d.Id == "Svg.Approximated");
    }

    [Fact]
    public void UrlAnnotationsAreLinks()
    {
        (_, SvgDocument doc) = Write(c =>
        {
            c.DrawAnnotation(new RectF(10, 10, 20, 10), Annotation.Url("https://example.com/?a=1&b=2"));
            c.DrawAnnotation(new RectF(10, 10, 20, 10), Annotation.NamedDestination("here"));
        });

        SvgElement link = Assert.Single(Elements(doc, "a"));
        Assert.Equal("https://example.com/?a=1&b=2", Href(link));
        Assert.Equal("0", Assert.Single(link.Elements("path")).Attributes["fill-opacity"]);
    }

    [Fact]
    public void LatticesAndAtlasesUseTheImageOnce()
    {
        using Raster<Rgba8> image = Raster.Create(24, 24, new Rgba8(60, 90, 160, 255));
        (_, SvgDocument doc) = Write(c =>
        {
            c.DrawImageNinePatch(image, new Rect(8, 8, 8, 8), new RectF(0, 0, 60, 40));
            c.DrawAtlas(image, [new SpriteTransform(1, 0, 70, 10), new SpriteTransform(1, 0, 80, 10)], [new RectF(0, 0, 8, 8), new RectF(8, 8, 8, 8)]);
        });

        Assert.Single(Elements(doc, "image"));
        Assert.Equal(11, Elements(doc, "use").Count);
    }

    [Fact]
    public void TheStreamIsLeftOpenAndOptionsAreChecked()
    {
        using MemoryStream stream = new();
        using (SvgCanvas canvas = new(stream, 10, 10))
        {
            Assert.Same(SvgWriteOptions.Default, canvas.Options);
        }

        Assert.True(stream.CanWrite);
        Assert.True(stream.Length > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgWriteOptions { Precision = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgWriteOptions { FallbackScale = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgWriteOptions { Text = (SvgTextMode)5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgWriteOptions { ImageFormat = (SvgImageFormat)5 });
        Assert.Throws<ArgumentNullException>(() => new SvgCanvas(null!, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgCanvas(new MemoryStream(), 0, 10));
    }

    [Fact]
    public void TextIsEscaped()
    {
        (string text, SvgDocument doc) = Write(
            c => c.DrawText("a<b & \"c\"\u0001", new Vector2(10, 30), new Font(Noto, 12), new Rgba8(0, 0, 0, 255)),
            new SvgWriteOptions { Text = SvgTextMode.Text });
        Assert.Contains(">&lt;</tspan>", text, StringComparison.Ordinal);
        Assert.Contains(">&amp;</tspan>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("", text, StringComparison.Ordinal);
        Assert.StartsWith("a<b & \"c\"", Assert.Single(Elements(doc, "text")).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SvgTextMode.Outlines)]
    [InlineData(SvgTextMode.Text)]
    public void ASceneUsingMostFeaturesReadsBack(SvgTextMode mode)
    {
        (_, SvgDocument doc) = Write(c => Scene(c, mode.ToString()), new SvgWriteOptions { Text = mode, EmbedSubset = true }, 400, 300, "Scene" + mode);
        string? folder = Environment.GetEnvironmentVariable("EGAMI_SVG_OUT");
        if (!string.IsNullOrEmpty(folder) && mode == SvgTextMode.Outlines)
        {
            using Raster<Rgba8> raster = Raster.Create(400, 300, new Rgba8(0, 0, 0, 0));
            using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
            {
                Scene(canvas, "Egami");
            }

            raster.Save(System.IO.Path.Combine(folder, "SceneEgami.png"));
        }

        Assert.NotEmpty(Elements(doc, "path"));
        Assert.Equal(mode == SvgTextMode.Text, Elements(doc, "text").Count > 0);
    }

    /// <summary>A scene with most kinds of call, for the structure tests and for looking at in browsers and Inkscape.</summary>
    internal static void Scene(Canvas c, string caption)
    {
        c.Clear(new Rgba8(250, 250, 245, 255));
        GradientStop[] rainbow = [new(0, new RgbaF32(1, 0, 0, 1)), new(0.5f, new RgbaF32(1, 1, 0, 1)), new(1, new RgbaF32(0, 0.4f, 1, 1))];
        c.FillRoundRect(new RoundRect(new RectF(10, 10, 120, 60), 10), new LinearGradient(new Vector2(10, 0), new Vector2(130, 0), rainbow));
        c.FillCircle(new Vector2(180, 40), 30, new ConicalGradient(new Vector2(170, 30), 8, new Vector2(180, 40), 30, rainbow));
        c.FillCircle(new Vector2(250, 40), 30, new RadialGradient(new Vector2(250, 40), 15, rainbow) { Tile = TileMode.Mirror });
        c.FillCircle(new Vector2(320, 40), 30, new SweepGradient(new Vector2(320, 40), rainbow));
        c.StrokePath(Path.Oval(new RectF(10, 80, 120, 50)), new Stroke(4) { Dash = new DashPattern([12, 6]), Cap = LineCap.Round }, new Rgba8(40, 40, 120, 255));

        using Raster<Rgba8> checker = Raster.Create(8, 8, new Rgba8(255, 255, 255, 255));
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if ((x / 4 + y / 4) % 2 == 0)
                {
                    checker.Row(y)[x] = new Rgba8(30, 30, 30, 255);
                }
            }
        }

        c.Save();
        c.ClipPath(Path.Oval(new RectF(150, 80, 80, 50)));
        c.FillRect(new RectF(150, 80, 80, 50), new ImagePaint(checker, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Nearest }) { LocalMatrix = Matrix3x2.CreateScale(2) });
        c.Restore();

        c.SaveLayer(new LayerOptions { Opacity = 0.6f });
        c.FillCircle(new Vector2(270, 105), 25, new Rgba8(255, 0, 0, 255));
        c.FillCircle(new Vector2(295, 105), 25, new Rgba8(0, 0, 255, 255));
        c.Restore();

        c.SaveLayer(new LayerOptions { BlendMode = BlendMode.Multiply });
        c.FillRect(new RectF(330, 80, 60, 50), new Rgba8(255, 200, 0, 255));
        c.Restore();

        c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillRect(new RectF(10, 140, 120, 50), new LinearGradient(new Vector2(10, 0), new Vector2(130, 0), new RgbaF32(1, 1, 1, 1), new RgbaF32(0, 0, 0, 1)))) });
        c.FillRect(new RectF(10, 140, 120, 50), new Rgba8(0, 150, 80, 255));
        c.Restore();

        c.FillCircle(new Vector2(180, 165), 20, new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Blur = new MaskBlur(5) });
        c.Save();
        c.Translate(220, 140);
        c.Rotate(10);
        c.DrawImage(checker, new RectF(0, 0, 48, 48), new SamplingOptions { Filter = ResampleFilter.Nearest });
        c.Restore();

        c.Save();
        c.ClipRect(new RectF(240, 195, 40, 20));
        c.Translate(260, 205);
        c.Rotate(30);
        c.FillRect(new RectF(-30, -4, 60, 8), new Rgba8(0, 120, 120, 255));
        c.Restore();
        c.DrawImage(checker, new RectF(2, 2, 4, 4), new RectF(220, 195, 16, 16), new SamplingOptions { Filter = ResampleFilter.Nearest });

        PictureRecorder recorder = new(new RectF(-10, -10, 20, 20));
        recorder.FillPath(Path.Star(Vector2.Zero, 10, 4, 5), new Rgba8(220, 120, 0, 255));
        Picture star = recorder.Finish();
        for (int i = 0; i < 4; i++)
        {
            c.DrawPicture(star, Matrix3x2.CreateRotation(i * 0.3f) * Matrix3x2.CreateTranslation(300 + (i * 22), 165));
        }

        c.SaveLayer(new LayerOptions { Filter = o => o.Blur(2) });
        c.FillRect(new RectF(290, 190, 90, 20), new Rgba8(120, 0, 160, 255));
        c.Restore();

        c.DrawText(caption + ": Office fié Øre", new Vector2(10, 240), new Font(s_noto.Value, 26), new Rgba8(20, 20, 20, 255));
        c.DrawText("Gradient", new Vector2(10, 280), new Font(s_noto.Value, 30) { Synthesis = FontSynthesis.Bold }, new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), rainbow));
        c.DrawAnnotation(new RectF(200, 260, 100, 30), Annotation.Url("https://example.com"));
    }

    /// <summary>Phase 2.6.5 task 6: an ink paint is written as its appearance, the same as the RGB paint of that colour.</summary>
    [Fact]
    public void InkPaintsAreWrittenAsTheirAppearance()
    {
        Colors.InkColor red = Colors.InkColor.Spot("PANTONE 186 C", 0.8f, Colors.InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
        SolidPaint ink = new(red) { Overprint = Overprint.NonZero };
        (string inkText, _) = Write(c => c.FillRect(new RectF(10, 10, 50, 30), ink));
        (string rgbText, _) = Write(c => c.FillRect(new RectF(10, 10, 50, 30), new SolidPaint(ink.Color)));
        Assert.Equal(rgbText, inkText);
    }

    internal static (string Text, SvgDocument Document) Write(Action<Canvas> draw, SvgWriteOptions? options = null, float width = 100, float height = 80, string? name = null)
    {
        using MemoryStream stream = new();
        using (SvgCanvas canvas = new(stream, width, height, options))
        {
            draw(canvas);
        }

        string text = Encoding.UTF8.GetString(stream.ToArray());
        string? folder = Environment.GetEnvironmentVariable("EGAMI_SVG_OUT");
        if (!string.IsNullOrEmpty(folder))
        {
            name ??= new System.Diagnostics.StackTrace().GetFrames().Select(f => f.GetMethod()).First(m => m?.DeclaringType == typeof(SvgCanvasTests) && m.Name != nameof(Write))?.Name ?? "out";
            Directory.CreateDirectory(folder);
            File.WriteAllText(System.IO.Path.Combine(folder, name + ".svg"), text);
        }

        // The test fonts, not the machine's. Egami.Svg does not read @font-face, so an embedded subset's family falls back to the
        // next one: that is the only warning allowed.
        if (!text.Contains("@font-face", StringComparison.Ordinal))
        {
            return (text, SvgDocument.Parse(text, new SvgLoadOptions { Strict = true, Fonts = s_fonts.Value }));
        }

        SvgDocument document = SvgDocument.Parse(text, new SvgLoadOptions { Fonts = s_fonts.Value });
        Assert.All(document.Warnings, w => Assert.True(w.Id == "Svg.FontNotFound" && w.Message.Contains("\"egami-font-", StringComparison.Ordinal), w.ToString()));
        return (text, document);
    }

    internal static List<SvgElement> Elements(SvgDocument doc, string name) => [.. doc.Root.Descendants().Where(e => e.Name == name)];

    internal static string Href(SvgElement e) => e.Attributes.Get("href", SvgNamespaces.XLink) ?? throw new InvalidOperationException("No href.");

    internal static Raster DecodeHref(SvgElement image)
    {
        string href = Href(image);
        return Raster.Load(Convert.FromBase64String(href[(href.IndexOf(',', StringComparison.Ordinal) + 1)..]));
    }

    internal static string FontPath(string file) => System.IO.Path.Combine(TestSvg.Root, "tests", "fonts", file);
}
