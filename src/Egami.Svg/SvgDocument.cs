using System.Numerics;

using Egami.Drawing;
using Egami.Svg.Reading;
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
    private readonly SvgXmlContent? _source;
    private Dictionary<string, SvgElement>? _ids;
    private int _idsVersion = -1;
    private (Vector2 Size, RectF? ViewBox) _size;
    private int _sizeVersion = -1;

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
