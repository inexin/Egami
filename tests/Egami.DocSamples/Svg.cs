using Egami.Drawing;
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
}
