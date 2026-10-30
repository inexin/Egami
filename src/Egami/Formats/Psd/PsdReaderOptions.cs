namespace Egami.Formats.Psd;

/// <summary>Which image Load gives for a PSD or PSB file.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-composite"/>
/// </example>
public enum PsdComposite
{
    /// <summary>
    /// The composite Photoshop stored, or the layers flattened when the file holds only a placeholder (saved without Maximize
    /// Compatibility). The default.
    /// </summary>
    Auto,

    /// <summary>Always the composite Photoshop stored, the placeholder too, with the <c>Psd.NoComposite</c> warning.</summary>
    Stored,

    /// <summary>Always the layers flattened by Egami, as <see cref="PsdDocument.Flatten"/> does.</summary>
    Flatten,
}

/// <summary>Options for reading PSD and PSB, on top of the general <see cref="ReaderOptions"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-composite"/>
/// </example>
public sealed record PsdReaderOptions : ReaderOptions
{
    private readonly PsdComposite _composite;

    /// <summary>Gets the default options.</summary>
    public static new PsdReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets which image Load, Probe and the row readers give: by default the stored composite, or the layers flattened when the
    /// file has no real one. Flattening reads the layers one at a time, as they come in the file. Lab, bitmap, indexed and
    /// multichannel files always give the stored composite; asked to flatten, with the <c>Psd.FlattenUnsupported</c> warning.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public PsdComposite Composite
    {
        get => _composite;
        init => _composite = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }
}
