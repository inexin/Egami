namespace Egami.Metafiles;

/// <summary>How <see cref="Metafile"/> reads a file: which limits and diagnostics apply.</summary>
public sealed record class MetafileLoadOptions
{
    /// <summary>Gets the defaults: the default context.</summary>
    public static MetafileLoadOptions Default { get; } = new();

    /// <summary>
    /// Gets the context whose limits bound the load (file size, inflated size of EMZ and WMZ) and whose diagnostics receive what
    /// the load skipped, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.
    /// </summary>
    public EgamiContext? Context { get; init; }

    internal EgamiContext EffectiveContext => Context ?? EgamiContext.Default;
}
