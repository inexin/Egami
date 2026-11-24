using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Formats;
using Egami.Pixels;
using Egami.Svg;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Svg
{
    private const string Icon = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <title>Save</title>
          <rect id="body" x="3" y="3" width="18" height="18" rx="2" fill="#2a6"/>
        </svg>
        """;

    [Fact]
    public void LoadADocument()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Icon);
        try
        {
            #region load
            // .svg and .svgz alike; nothing outside the file is opened unless a resolver allows it.
            SvgDocument document = SvgDocument.Load(path);
            Console.WriteLine($"{document.Title}: {document.Root.Descendants().Count()} elements");

            // What a lenient load skipped is in Warnings, and in the context's diagnostics.
            foreach (EgamiDiagnostic warning in document.Warnings)
            {
                Console.WriteLine($"{warning.Id}: {warning.Message}");
            }
            #endregion
            Assert.Equal("Save", document.Title);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EditTheTree()
    {
        #region edit
        SvgDocument document = SvgDocument.Parse(Icon);

        // Elements, attributes and text are an editable tree, like XDocument's.
        SvgElement body = document.FindById("body")!;
        body.Attributes["fill"] = "#c33";
        body.Attributes.Set("href", "#other", SvgNamespaces.XLink, "xlink");

        SvgElement dot = new("circle");
        dot.Attributes["cx"] = "12";
        dot.Attributes["cy"] = "12";
        dot.Attributes["r"] = "3";
        document.Root.Children.Add(dot);
        document.Root.Children.Add(new SvgComment(" added in code "));

        foreach (SvgElement element in document.Root.Elements())
        {
            Console.WriteLine($"{element.Name}: {element.Attributes.Count} attributes");
        }
        #endregion
        Assert.Equal("#c33", body.Attributes["fill"]);
        Assert.Contains(document.Root.Children, c => c is SvgTextContent);
        Assert.Contains(document.Root.Children, c => c is SvgProcessingInstruction or SvgComment);
    }

    [Fact]
    public void CreateADocument()
    {
        #region create
        // A document made in code: an svg root and its content.
        SvgElement root = new("svg");
        root.Attributes["viewBox"] = "0 0 100 100";
        SvgElement circle = new("circle");
        circle.Attributes["cx"] = "50";
        circle.Attributes["cy"] = "50";
        circle.Attributes["r"] = "40";
        root.Children.Add(circle);
        SvgDocument document = new(root);
        #endregion
        Assert.Same(root, document.Root);
    }

    [Fact]
    public void SaveADocument()
    {
        SvgDocument document = SvgDocument.Parse(Icon);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.svgz");
        try
        {
            #region save
            // Numbers and paths set in code are written with Precision decimals; what was read keeps its text.
            document.FindById("body")!.Attributes.Set("width", 17.666666);
            document.Save(path, new SvgSaveOptions { Precision = 2, Gzip = true });

            using var stream = new MemoryStream();
            document.Save(stream);
            #endregion
            Assert.Equal("17.67", SvgDocument.Load(path).FindById("body")!.Attributes["width"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rasterize()
    {
        SvgDocument document = SvgDocument.Parse(Icon);
        #region rasterize
        // At the document's own size, scaled, or at a target width (the height keeps the aspect ratio).
        using Raster<Rgba8> icon = document.Rasterize<Rgba8>();
        using Raster<Rgba8> large = document.Rasterize<Rgba8>(new SvgRenderOptions { Width = 512 });
        using Raster<Rgba8> onWhite = document.Rasterize<Rgba8>(new SvgRenderOptions { Scale = 2, Background = new RgbaF32(1, 1, 1, 1) });
        #endregion
        Assert.Equal((24, 24), (icon.Width, icon.Height));
        Assert.Equal(512, large.Height);
        Assert.Equal(48, onWhite.Width);
    }

    [Fact]
    public void LoadThroughRaster()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Icon);
        try
        {
            #region svg-format
            // SVG as an Egami format: Raster.Load draws a file at its own size, Raster.Probe reads the size without drawing.
            EgamiContext context = EgamiContext.Default.WithFormat(SvgFormat.Info);
            RasterInfo info = Raster.Probe(path, context);
            using Raster<Rgba8> icon = Raster.Load<Rgba8>(path, context);

            // Twice the size, for a high-density screen.
            using Raster<Rgba8> sharp = Raster.Load<Rgba8>(path, context, new SvgDecodeOptions { Scale = 2 });
            #endregion
            Assert.Equal((24, 24), (info.Width, info.Height));
            Assert.Equal(24, icon.Width);
            Assert.Equal(48, sharp.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DrawOnACanvas()
    {
        SvgDocument document = SvgDocument.Parse(Icon);
        using Raster<Rgba8> page = Raster.Create<Rgba8>(300, 200);
        using RasterCanvas<Rgba8> canvas = page.CreateCanvas();
        #region draw
        // Fitted into a box by the root's preserveAspectRatio; a PDF page or SvgCanvas receives the same calls.
        document.Draw(canvas, new RectF(10, 10, 180, 180));

        // One element of a sprite sheet, fitted into its own box.
        document.Draw(canvas, "body", new RectF(200, 10, 48, 48));
        #endregion
    }

    [Fact]
    public void WalkTheRenderTree()
    {
        SvgDocument document = SvgDocument.Parse(Icon);
        #region render-tree
        // The resolved tree: groups, paths with their paints and strokes, images; styles applied and units in pixels.
        static void Walk(SvgNode node, int depth)
        {
            string what = node switch
            {
                SvgPath path => $"path {path.Path.Bounds} fill {path.Fill}",
                SvgGroup group => $"group of {group.Children.Count}",
                SvgImage image => $"image {image.Destination}",
                _ => node.GetType().Name,
            };
            Console.WriteLine($"{new string(' ', depth * 2)}{node.Id} {what} opacity {node.Opacity}");
            if (node is SvgGroup g)
            {
                foreach (SvgNode child in g.Children)
                {
                    Walk(child, depth + 1);
                }
            }
        }

        Walk(document.RenderTree, 0);
        #endregion
    }

    [Fact]
    public void LoadStrictly()
    {
        #region strict
        // Strict turns everything a lenient load would skip into an exception with the line and column.
        try
        {
            SvgDocument.Parse("<svg>\n  <rect width='10' height='10'/>\n</svg>", new SvgLoadOptions { Strict = true });
        }
        catch (SvgException e)
        {
            Console.WriteLine($"Line {e.Line}, column {e.Column}: {e.Problem}");
        }
        #endregion
    }

    [Fact]
    public void AllowFilesInOneFolder()
    {
        string folder = System.IO.Path.GetTempPath();
        #region resolver
        // Images, style sheets and fonts the file refers to open only through a resolver; data: URIs always work.
        var options = new SvgLoadOptions
        {
            Resolver = SvgResolvers.SameDirectory(folder),
            Dpi = 96,
            Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 64L << 20 } },
        };
        SvgDocument document = SvgDocument.Parse(Icon, options);
        #endregion
        Assert.NotNull(document.Root);
    }

    private const string Sheet = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <symbol id="plus" viewBox="0 0 24 24"><path d="M11 4h2v16h-2zM4 11h16v2H4z" fill="currentColor"/></symbol>
          <symbol id="dot" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" fill="currentColor"/></symbol>
        </svg>
        """;

    [Fact]
    public void SvgToPng()
    {
        string input = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.svg");
        string output = System.IO.Path.ChangeExtension(input, ".png");
        File.WriteAllText(input, Icon);
        try
        {
            #region svg-to-png
            // An SVG drawn 256 pixels wide and saved as PNG; the height keeps the aspect ratio.
            SvgDocument document = SvgDocument.Load(input);
            using Raster<Rgba8> raster = document.Rasterize<Rgba8>(new SvgRenderOptions { Width = 256 });
            raster.Save(output);
            #endregion
            Assert.Equal(256, Raster.Probe(output).Height);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void RecolorAnIcon()
    {
        #region recolor
        // Every fill and stroke of a one-color icon set to a brand color, then drawn: edits redraw at once.
        SvgDocument icon = SvgDocument.Parse(Icon);
        foreach (SvgElement element in icon.Root.Descendants())
        {
            foreach (string paint in (string[])["fill", "stroke"])
            {
                if (element.Attributes[paint] is string value && value != "none")
                {
                    element.Attributes[paint] = "#7b2ff7";
                }
            }
        }

        using Raster<Rgba8> branded = icon.Rasterize<Rgba8>(new SvgRenderOptions { Width = 64 });
        #endregion
        Assert.Equal("#7b2ff7", icon.FindById("body")!.Attributes["fill"]);
    }

    [Fact]
    public void IconsFromASpriteSheet()
    {
        #region sprite-sheet
        // Each symbol of a sprite sheet drawn into its own 32-pixel cell of a toolbar strip.
        SvgDocument sheet = SvgDocument.Parse(Sheet);
        string[] icons = ["plus", "dot", "plus"];
        using Raster<Rgba8> strip = Raster.Create<Rgba8>(32 * icons.Length, 32);
        using (RasterCanvas<Rgba8> canvas = strip.CreateCanvas())
        {
            for (int i = 0; i < icons.Length; i++)
            {
                sheet.Draw(canvas, icons[i], new RectF(32 * i, 0, 32, 32));
            }
        }
        #endregion
        Assert.Equal(96, strip.Width);
    }

    [Fact]
    public void TextWithItsOwnFonts()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        #region fonts
        // A server passes its own fonts and turns the machine's off, so text renders the same everywhere.
        Egami.Fonts.FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        SvgLoadOptions options = new()
        {
            Fonts = new SvgFontOptions { Collection = fonts, UseSystemFonts = false, DefaultFamily = "Noto Sans" },
        };
        SvgDocument label = SvgDocument.Parse(
            "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='40'><text x='60' y='26' text-anchor='middle'>Label</text></svg>",
            options);
        using Raster<Rgba8> raster = label.Rasterize<Rgba8>(new SvgRenderOptions { TextAsPaths = false });
        #endregion
        Assert.Empty(label.Warnings);
    }

    [Fact]
    public void DrawToSvg()
    {
        using MemoryStream file = new();
        #region svg-canvas
        // Anything drawn on a Canvas can be written as SVG: gradients, clips, images and pictures go once into defs.
        using (SvgCanvas canvas = new(file, 200, 120, new SvgWriteOptions { Precision = 2, ImageFormat = SvgImageFormat.Png }))
        {
            canvas.FillRoundRect(new RoundRect(new RectF(10, 10, 180, 100), 12), new LinearGradient(new Vector2(10, 0), new Vector2(190, 0), new RgbaF32(0.1f, 0.4f, 0.9f, 1), new RgbaF32(0.9f, 0.2f, 0.5f, 1)));
            canvas.SaveLayer(0.5f);
            canvas.StrokePath(Egami.Drawing.Path.Oval(new RectF(30, 30, 140, 60)), new Stroke(6) { Dash = new DashPattern([10, 5]) }, new Rgba8(255, 255, 255, 255));
            canvas.Restore();

            // A sweep gradient has no SVG form: it is drawn by Egami and embedded as a PNG (Svg.FallbackImage).
            canvas.FillCircle(new Vector2(100, 60), 20, new SweepGradient(new Vector2(100, 60), [new GradientStop(0, new RgbaF32(1, 1, 0, 1)), new GradientStop(1, new RgbaF32(0, 1, 1, 1))]));
        }

        // Disposing writes the file; the stream stays open.
        #endregion
        Assert.True(file.Length > 0);
        Assert.Contains("linearGradient", System.Text.Encoding.UTF8.GetString(file.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTextToSvg()
    {
        Typeface face = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf"));
        using MemoryStream file = new();
        #region svg-canvas-text
        // By default text is glyph outlines, each written once and reused, so the file needs no fonts.
        // SvgTextMode.Text writes text elements instead, searchable and selectable; EmbedSubset adds the glyphs used as a font.
        SvgWriteOptions options = new() { Text = SvgTextMode.Text, EmbedSubset = true };
        using (SvgCanvas canvas = new(file, 300, 60, options))
        {
            canvas.DrawText("Hello, SVG", new Vector2(10, 40), new Font(face, 28), new Rgba8(20, 20, 20, 255));
        }
        #endregion
        Assert.Contains("@font-face", System.Text.Encoding.UTF8.GetString(file.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void TextFlowedIntoAShape()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts");
        Egami.Fonts.FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(folder, "NotoSans-Regular.ttf"));
        SvgLoadOptions options = new() { Fonts = new SvgFontOptions { Collection = fonts, UseSystemFonts = false } };
        #region wrapped-text
        // SVG 2 text that wraps: inside a circle (as Inkscape writes flowed text), or in lines 160 units long.
        SvgDocument card = SvgDocument.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" width="400" height="200" font-family="Noto Sans" font-size="14">
              <circle id="area" cx="100" cy="100" r="90" fill="#eef"/>
              <text style="shape-inside: url(#area); shape-padding: 6px; text-align: center">
                Text flows into the circle line by line, each line as wide as the circle is at that height.
              </text>
              <text x="220" y="40" style="inline-size: 160px">Wrapped at 160 units, as a paragraph fills a column.</text>
            </svg>
            """, options);
        using Raster<Rgba8> raster = card.Rasterize<Rgba8>();
        #endregion
        Assert.Empty(card.Warnings);
    }

    [Fact]
    public void SvgGlyphsOnAnyCanvas()
    {
        Egami.Fonts.Font font = new(Egami.Fonts.Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf")), 24);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(200, 50);
        #region svg-glyphs
        // Emoji and other glyphs from an OpenType SVG table draw in color on a canvas given the renderer.
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas(new CanvasOptions { ColorGlyphRenderer = SvgGlyphRenderer.Instance }))
        {
            canvas.DrawText("Hello", new System.Numerics.Vector2(10, 35), font, new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }
        #endregion
        Assert.Equal(200, raster.Width);
    }

    [Fact]
    public void DrawAnAnimationAtATime()
    {
        SvgDocument document = SvgDocument.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="20">
              <circle cx="10" cy="10" r="8" fill="#2a6">
                <animate attributeName="cx" values="10;90;10" dur="2s" repeatCount="indefinite"/>
              </circle>
            </svg>
            """);
        #region animation
        // What the SMIL animations do: here a 2 s loop that never ends.
        SvgAnimationInfo info = document.Animation;
        Console.WriteLine($"Animated: {info.HasAnimations}, loop: {info.LoopPeriod}, ends: {info.Duration?.ToString() ?? "never"}");

        // The document half a second in: the circle has moved, the element tree has not changed.
        using Raster<Rgba8> frame = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(0.5) });
        SvgGroup tree = document.RenderTreeAt(TimeSpan.FromSeconds(0.5));
        #endregion
        Assert.Equal(TimeSpan.FromSeconds(2), info.LoopPeriod);
        Assert.Null(info.Duration);
        Assert.Equal(100, frame.Width);
        Assert.NotSame(document.RenderTree, tree);
    }

    [Fact]
    public void DrawACssAnimationAtATime()
    {
        #region css-animation
        // CSS animations run as SMIL ones do: from when the document loads, drawn at the time you ask for.
        SvgDocument document = SvgDocument.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <style>
                @keyframes spin { to { rotate: 360deg } }
                @keyframes pulse { 50% { fill: orange; scale: 1.2 } }
                .spinner { transform-origin: 50px 50px; animation: spin 2s linear infinite, pulse 1s ease-in-out infinite }
              </style>
              <rect class="spinner" x="30" y="30" width="40" height="40" fill="teal"/>
            </svg>
            """);
        SvgAnimationInfo info = document.Animation;                      // loops every 2 s
        using Raster<Rgba8> frame = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(0.25) });
        #endregion
        Assert.True(info.HasAnimations);
        Assert.Equal(TimeSpan.FromSeconds(2), info.LoopPeriod);
        Assert.Equal(100, frame.Width);
    }

    private const string Bouncing = """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="20">
          <circle cx="10" cy="10" r="8" fill="#2a6">
            <animate attributeName="cx" values="10;90;10" dur="2s" repeatCount="indefinite"/>
          </circle>
        </svg>
        """;

    [Fact]
    public void SaveAnAnimationAsAGif()
    {
        SvgDocument document = SvgDocument.Parse(Bouncing);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.gif");
        try
        {
            #region rasterize-animation
            // One loop (2 s) at 25 frames a second; frames that look the same merge into one longer frame.
            using Raster<Rgba8> animation = document.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 25 });
            animation.Save(path);   // loops forever, as the SVG does
            #endregion
            Assert.Equal(50, animation.Frames.Count);
            Assert.Equal(0, animation.Metadata.LoopCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StreamAnAnimationFrameByFrame()
    {
        SvgDocument document = SvgDocument.Parse(Bouncing);
        int count = 0;
        #region render-frames
        // One frame at a time, for a video encoder: a constant 30 frames a second for 4 seconds.
        SvgAnimationOptions options = new() { FrameRate = 30, Length = TimeSpan.FromSeconds(4), MergeDuplicates = false };
        foreach (SvgFrame<Rgba8> frame in document.RenderFrames<Rgba8>(options))
        {
            using Raster<Rgba8> picture = frame.Raster;   // yours to dispose
            Console.WriteLine($"{frame.Time} for {frame.Duration}");
            count++;
        }
        #endregion
        Assert.Equal(120, count);
    }

    [Fact]
    public void LoadAnAnimatedSvg()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Bouncing);
        try
        {
            #region load-animation
            // With a frame rate, Raster.Load gives every frame, for the animation tools (Retime, ChangeSpeed, DrawAnimation).
            EgamiContext context = EgamiContext.Default.WithFormat(SvgFormat.Info);
            using Raster<Rgba8> animation = Raster.Load<Rgba8>(path, context, new SvgDecodeOptions { AnimationFrameRate = 10 });

            // Or one moment of it, half a second in.
            using Raster<Rgba8> moment = Raster.Load<Rgba8>(path, context, new SvgDecodeOptions { Time = TimeSpan.FromSeconds(0.5) });
            #endregion
            Assert.Equal(20, animation.Frames.Count);
            Assert.Single(moment.Frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FreezeAMoment()
    {
        SvgDocument document = SvgDocument.Parse(Bouncing);
        using MemoryStream stream = new();
        #region freeze
        // A static SVG of the moment half a second in: the circle where it is then, no animation left.
        SvgDocument still = document.Freeze(TimeSpan.FromSeconds(0.5));
        still.Save(stream);
        #endregion
        Assert.False(still.Animation.HasAnimations);
        Assert.Equal("50", still.Root.Descendants().First(e => e.Name == "circle").Attributes["cx"]);
    }
}
