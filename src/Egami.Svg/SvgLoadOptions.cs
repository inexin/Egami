namespace Egami.Svg;

/// <summary>How <see cref="SvgDocument"/> reads a file: what it may fetch, how strict it is, and which limits and diagnostics apply.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="resolver"/>
/// </example>
public sealed record class SvgLoadOptions
{
    /// <summary>Gets the defaults: nothing outside the file, lenient, 96 pixels per inch, the default context.</summary>
    public static SvgLoadOptions Default { get; } = new();

    /// <summary>
    /// Gets the resolver that opens files the document refers to (images, style sheets, fonts, other SVG files), or
    /// <see langword="null"/>, the default, to open nothing outside the file. <c>data:</c> URIs always work. It gets an absolute
    /// URI (relative ones are resolved against <see cref="BaseUri"/>) and returns a stream it may own, or <see langword="null"/>
    /// to refuse. Egami never opens a network connection itself; <see cref="SvgResolvers"/> has a ready resolver for files in
    /// one folder.
    /// </summary>
    public Func<Uri, Stream?>? Resolver { get; init; }

    /// <summary>
    /// Gets the URI relative references are resolved against, or <see langword="null"/> to use the file's own location when it
    /// was loaded from a path.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Gets a value indicating whether anything a lenient load would skip (an attribute it cannot read, an element in error, a
    /// missing reference) throws an <see cref="SvgException"/> with its line and column instead. <see langword="false"/> by
    /// default, as browsers and resvg read SVG.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Gets the resolution that turns physical units (in, cm, mm, pt, pc) into pixels; 96 by default, as CSS defines.</summary>
    public float Dpi
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Dpi must be a positive number.");
    } = 96;

    /// <summary>
    /// Gets the context whose limits bound the load (file size, element count and nesting, entity expansion) and whose
    /// diagnostics receive what a lenient load skipped, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.
    /// </summary>
    public EgamiContext? Context { get; init; }

    internal EgamiContext EffectiveContext => Context ?? EgamiContext.Default;
}
