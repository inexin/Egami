using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;
using Egami.Svg.Reading;
using Egami.Svg.Rendering;
using Egami.Svg.Resolving;
using Egami.Svg.Values;

namespace Egami.Svg;

/// <summary>
/// An SVG document: an editable element tree read from an .svg or .svgz file, which saves back to SVG and draws through
/// Egami.Drawing's canvas.
/// </summary>
/// <remarks>
/// Loading is lenient, as browsers and resvg read SVG: what cannot be used is skipped and reported through the context's
/// diagnostics, and the rest is kept. <see cref="SvgLoadOptions.Strict"/> throws instead. Nothing outside the file is opened
/// unless <see cref="SvgLoadOptions.Resolver"/> allows it.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="load"/>
/// </example>
public sealed class SvgDocument
{
    /// <summary>
    /// The canvas options Rasterize uses by default: curves flattened to 0.02 pixel instead of the canvas's 0.1, so the edges of
    /// large round shapes keep their exact coverage, as resvg's and browsers' do.
    /// </summary>
    private static readonly CanvasOptions s_canvasOptions = new() { Tolerance = 0.02f };

    private readonly SvgXmlContent? _source;
    private Dictionary<string, SvgElement>? _ids;
    private int _idsVersion = -1;
    private (Vector2 Size, RectF? ViewBox) _size;
    private int _sizeVersion = -1;
    private readonly Lock _treeLock = new();
    private SvgGroup? _tree;
    private int _treeVersion = -1;
    private IReadOnlyList<string> _treeLanguages = ["en"];

    /// <summary>Initializes a new instance of the <see cref="SvgDocument"/> class around a root element made in code.</summary>
    /// <param name="root">The root, an <c>svg</c> element without a parent or another document.</param>
    /// <param name="options">How references, units and limits work, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> already has a parent or a document.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="create"/>
    /// </example>
    public SvgDocument(SvgElement root, SvgLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Parent is not null || root.OwnerDocument is not null)
        {
            throw new ArgumentException("The root element already belongs to an element or a document.", nameof(root));
        }

        Root = root;
        root.OwnerDocument = this;
        Options = options ?? SvgLoadOptions.Default;
        Warnings = [];
    }

    private SvgDocument(SvgXmlContent source, SvgLoadOptions options, IReadOnlyList<EgamiDiagnostic> warnings)
        : this(source.Root, options)
    {
        _source = source;
        Warnings = warnings;
    }

    /// <summary>Gets the root <c>svg</c> element.</summary>
    public SvgElement Root { get; }

    /// <summary>Gets the options the document was loaded with.</summary>
    public SvgLoadOptions Options { get; }

    /// <summary>Gets what the load skipped, as also sent to the context's diagnostics; empty for a document made in code.</summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings { get; }

    /// <summary>
    /// Gets the document's size in pixels: the root's width and height, a percentage (or a missing side) taken of the viewBox,
    /// or of 100 by 100 when there is no viewBox, as resvg sizes documents. Physical units use <see cref="SvgLoadOptions.Dpi"/>.
    /// </summary>
    public Vector2 Size => Measure().Size;

    /// <summary>Gets the root's viewBox, or <see langword="null"/> when it has none (or an invalid one).</summary>
    public RectF? ViewBox => Measure().ViewBox;

    /// <summary>Gets the text of the root's first <c>title</c> child, or <see langword="null"/>.</summary>
    public string? Title => Root.Elements("title").FirstOrDefault()?.Text.Trim();

    /// <summary>Gets the text of the root's first <c>desc</c> child, or <see langword="null"/>.</summary>
    public string? Description => Root.Elements("desc").FirstOrDefault()?.Text.Trim();

    /// <summary>Gets a value indicating whether the root svg element has no namespace, so elements in none are read as SVG.</summary>
    internal bool NoNamespace => Root.Namespace is null;

    /// <summary>Gets a number that changes whenever the tree is edited.</summary>
    internal int Version { get; private set; }

    /// <summary>Reads an .svg or .svgz file.</summary>
    /// <param name="path">The file. Relative references resolve against its folder (they open only through a resolver).</param>
    /// <param name="options">How to read it, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="SvgException">The file is not well-formed XML, is not SVG, breaks a limit, or, in strict mode, has an error.</exception>
    /// <exception cref="LimitExceededException">The file is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="load"/>
    /// </example>
    public static SvgDocument Load(string path, SvgLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= SvgLoadOptions.Default;
        if (options.BaseUri is null)
        {
            options = options with { BaseUri = new Uri(System.IO.Path.GetFullPath(path)) };
        }

        using FileStream stream = File.OpenRead(path);
        return Load(stream, options);
    }

    /// <summary>Reads an .svg or .svgz stream from its current position.</summary>
    /// <param name="stream">The stream; it is read to the end of the document and left open.</param>
    /// <param name="options">How to read it, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="SvgException">The data is not well-formed XML, is not SVG, breaks a limit, or, in strict mode, has an error.</exception>
    /// <exception cref="LimitExceededException">The data is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="resolver"/>
    /// </example>
    public static SvgDocument Load(Stream stream, SvgLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= SvgLoadOptions.Default;
        EgamiContext context = options.EffectiveContext;
        SvgLimits limits = new(context.Limits);
        SvgReport report = new(options.Strict);
        SvgXmlContent content = SvgXmlReader.Read(stream, limits, report);
        CheckRoot(content.Root, report);
        (Vector2 Size, RectF? ViewBox) size = SvgRootSize.Resolve(content.Root, options.Dpi, report);
        SvgDocument document = new(content, options, report.Warnings) { _size = size, _sizeVersion = 0 };

        // Styles are applied and references resolved at load, so what cannot be drawn is reported (or thrown, in strict mode) here.
        document._tree = new SvgResolver(document, report).Resolve();
        document._treeVersion = document.Version;
        report.Deliver(context);
        return document;
    }

    /// <summary>Reads SVG text.</summary>
    /// <param name="text">The SVG markup.</param>
    /// <param name="options">How to read it, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="SvgException">The text is not well-formed XML, is not SVG, breaks a limit, or, in strict mode, has an error.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="strict"/>
    /// </example>
    public static SvgDocument Parse(string text, SvgLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(text));
        return Load(stream, options);
    }

    /// <summary>
    /// Gets the render tree: the elements resolved for drawing (styles applied, units in pixels, use expanded, shapes as paths),
    /// in the document's pixels (<see cref="Size"/>). It is built at load and again on first use after an edit; what an edit
    /// leaves undrawable is reported to the context's diagnostics then. It is immutable and safe to read from many threads.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-tree"/>
    /// </example>
    public SvgGroup RenderTree => Tree(null);

    /// <summary>
    /// Draws the document on a canvas: at its own size from the canvas's origin, or fitted into <paramref name="box"/> by the
    /// root's preserveAspectRatio. Drawing calls only the public canvas API, so a raster canvas, a PDF page, SvgCanvas or a
    /// PictureRecorder receive the same calls. It never throws on content.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="box">The rectangle to fit the document into, in the canvas's coordinates, or <see langword="null"/>.</param>
    /// <param name="options">Languages, text as paths and filter scale, or <see langword="null"/> for the defaults.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="draw"/>
    /// </example>
    public void Draw(Canvas canvas, RectF? box = null, SvgRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        options ??= SvgRenderOptions.Default;
        SvgGroup tree = Tree(options.Languages);
        int count = canvas.Save();
        if (box is RectF target)
        {
            Vector2 size = Size;
            if (!(size.X > 0 && size.Y > 0 && target.Width > 0 && target.Height > 0))
            {
                canvas.RestoreToCount(count);
                return;
            }

            canvas.Concat(RootAspect().Transform(new RectF(0, 0, size.X, size.Y), target));
        }

        SvgRenderer.Draw(canvas, tree, options);
        canvas.RestoreToCount(count);
    }

    /// <summary>
    /// Draws one element and what it holds, such as a <c>symbol</c> or <c>g</c> of a sprite sheet: fitted into
    /// <paramref name="box"/> (a symbol's viewBox, else the element's bounding box, centered and scaled to fit), or, without a
    /// box, in the coordinates of its parent.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="id">The element's id.</param>
    /// <param name="box">The rectangle to fit the element into, or <see langword="null"/>.</param>
    /// <param name="options">Languages, text as paths and filter scale, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">No element has the id.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="draw"/>
    /// </example>
    public void Draw(Canvas canvas, string id, RectF? box = null, SvgRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(id);
        options ??= SvgRenderOptions.Default;
        SvgElement element = FindById(id) ?? throw new ArgumentException("No element has the id " + id + ".", nameof(id));
        SvgReport report = new(strict: false);
        SvgNode? node = new SvgResolver(this, report, options.Languages).ResolveElement(element);
        report.Deliver(Options.EffectiveContext);
        if (node is null)
        {
            return;
        }

        int count = canvas.Save();
        if (box is RectF target)
        {
            RectF source = element.Name == "symbol" && element.Attributes["viewBox"] is string vb && SvgViewBox.TryParse(vb, out RectF viewBox) && viewBox.Width > 0 && viewBox.Height > 0
                ? viewBox
                : SvgResolver.Transformed(node);
            if (!(source.Width > 0 && source.Height > 0 && target.Width > 0 && target.Height > 0))
            {
                canvas.RestoreToCount(count);
                return;
            }

            canvas.Concat(SvgAspectRatio.Default.Transform(source, target));
        }

        SvgRenderer.Draw(canvas, node, options);
        canvas.RestoreToCount(count);
    }

    /// <summary>
    /// Draws the document into a new raster: at its own size times <see cref="SvgRenderOptions.Scale"/>, or at a target width
    /// and/or height (the other side keeps the aspect ratio), over <see cref="SvgRenderOptions.Background"/>.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the raster.</typeparam>
    /// <param name="options">The size, background and canvas options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="LimitExceededException">The raster would break the context's size limits.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="rasterize"/>
    /// </example>
    public Raster<TPixel> Rasterize<TPixel>(SvgRenderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        options ??= SvgRenderOptions.Default;
        Vector2 size = Size;
        double w, h;
        if (options.Width is int width && options.Height is int height)
        {
            (w, h) = (width, height);
        }
        else if (options.Width is int onlyWidth)
        {
            (w, h) = (onlyWidth, Math.Ceiling(onlyWidth * (double)size.Y / size.X));
        }
        else if (options.Height is int onlyHeight)
        {
            (w, h) = (Math.Ceiling(onlyHeight * (double)size.X / size.Y), onlyHeight);
        }
        else
        {
            (w, h) = (Math.Ceiling(size.X * (double)options.Scale), Math.Ceiling(size.Y * (double)options.Scale));
        }

        w = Math.Max(1, w);
        h = Math.Max(1, h);
        EgamiContext context = options.Context ?? Options.EffectiveContext;
        SafetyLimits limits = context.Limits;
        if (w > limits.MaxWidth)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxWidth), limits.MaxWidth, (long)w);
        }

        if (h > limits.MaxHeight)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxHeight), limits.MaxHeight, (long)h);
        }

        if (w * h > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, (long)(w * h));
        }

        Raster<TPixel> raster = Raster.Create<TPixel>((int)w, (int)h, context);
        using RasterCanvas<TPixel> canvas = raster.CreateCanvas(options.CanvasOptions ?? s_canvasOptions);
        if (options.Background.A > 0)
        {
            canvas.Clear(options.Background);
        }

        if (size.X > 0 && size.Y > 0)
        {
            canvas.Scale((float)(w / size.X), (float)(h / size.Y));
            SvgRenderer.Draw(canvas, Tree(options.Languages), options);
        }

        return raster;
    }

    /// <summary>
    /// Writes the document as SVG: the element tree with everything read from the file (unknown elements, foreign namespaces,
    /// comments, the DOCTYPE), so a load and save without edits keeps the document's meaning; attribute order and whitespace
    /// inside tags may change, and entities are written out.
    /// </summary>
    /// <param name="stream">The stream to write to; it is left open.</param>
    /// <param name="options">How to write, or <see langword="null"/> for <see cref="SvgSaveOptions.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="save"/>
    /// </example>
    public void Save(Stream stream, SvgSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        SvgXmlWriter.Write(this, stream, options ?? SvgSaveOptions.Default, _source);
    }

    /// <summary>Writes the document to a file, as <see cref="Save(Stream, SvgSaveOptions?)"/> does.</summary>
    /// <param name="path">The file; it is replaced if it exists. Use <see cref="SvgSaveOptions.Gzip"/> for .svgz.</param>
    /// <param name="options">How to write, or <see langword="null"/> for <see cref="SvgSaveOptions.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="save"/>
    /// </example>
    public void Save(string path, SvgSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.Create(path);
        Save(stream, options);
    }

    /// <summary>Finds the element with an <c>id</c>; the first in file order when several share it.</summary>
    /// <param name="id">The id, without a <c>#</c>.</param>
    /// <returns>The element, or <see langword="null"/>.</returns>
    public SvgElement? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_ids is null || _idsVersion != Version)
        {
            Dictionary<string, SvgElement> ids = new(StringComparer.Ordinal);
            if (Root.Id is string rootId)
            {
                ids.TryAdd(rootId, Root);
            }

            foreach (SvgElement element in Root.Descendants())
            {
                if (element.Id is string elementId)
                {
                    ids.TryAdd(elementId, element);
                }
            }

            _ids = ids;
            _idsVersion = Version;
        }

        return _ids.GetValueOrDefault(id);
    }

    /// <summary>The render tree for a set of reader languages, rebuilt after an edit or for other languages.</summary>
    private SvgGroup Tree(IReadOnlyList<string>? languages)
    {
        lock (_treeLock)
        {
            languages ??= _treeLanguages;
            if (_tree is null || _treeVersion != Version || !languages.SequenceEqual(_treeLanguages))
            {
                SvgReport report = new(strict: false);
                _tree = new SvgResolver(this, report, languages).Resolve();
                _treeVersion = Version;
                _treeLanguages = languages;
                report.Deliver(Options.EffectiveContext);
            }

            return _tree;
        }
    }

    private SvgAspectRatio RootAspect() =>
        Root.Attributes["preserveAspectRatio"] is string text && SvgAspectRatio.TryParse(text, out SvgAspectRatio aspect) ? aspect : SvgAspectRatio.Default;

    private (Vector2 Size, RectF? ViewBox) Measure()
    {
        if (_sizeVersion != Version)
        {
            _size = SvgRootSize.Resolve(Root, Options.Dpi, null);
            _sizeVersion = Version;
        }

        return _size;
    }

    /// <summary>Marks the render tree stale after an edit.</summary>
    internal void Touch() => Version++;

    private static void CheckRoot(SvgElement root, SvgReport report)
    {
        if (!string.Equals(root.Name, "svg", StringComparison.Ordinal))
        {
            throw new SvgException(root.Line, root.Column, $"The root element is <{root.Name}>, not <svg>.");
        }

        if (root.Namespace is null)
        {
            report.Warn("Svg.NoNamespace", "The root svg element has no namespace; its elements are read as SVG anyway.", root);
        }
        else if (!root.IsSvg)
        {
            throw new SvgException(root.Line, root.Column, $"The root svg element is in the namespace {root.Namespace}, not SVG's.");
        }
    }
}
