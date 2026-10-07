namespace Egami.Metadata;

/// <summary>The unit of a <see cref="Resolution"/>.</summary>
public enum ResolutionUnit
{
    /// <summary>No unit: the values only give the pixel aspect ratio.</summary>
    None,

    /// <summary>Pixels per inch.</summary>
    PerInch,

    /// <summary>Pixels per centimeter.</summary>
    PerCentimeter,

    /// <summary>Pixels per meter.</summary>
    PerMeter,
}

/// <summary>Physical pixel density.</summary>
/// <param name="X">Horizontal density.</param>
/// <param name="Y">Vertical density.</param>
/// <param name="Unit">The unit of both values.</param>
public readonly record struct Resolution(double X, double Y, ResolutionUnit Unit)
{
    /// <summary>Returns the same density in pixels per inch. A resolution with no unit is returned unchanged.</summary>
    /// <returns>The density in pixels per inch.</returns>
    public Resolution ToPerInch()
    {
        double factor = Unit switch
        {
            ResolutionUnit.PerCentimeter => 2.54,
            ResolutionUnit.PerMeter => 0.0254,
            _ => 1,
        };
        return Unit == ResolutionUnit.None ? this : new Resolution(X * factor, Y * factor, ResolutionUnit.PerInch);
    }
}

/// <summary>What the raster's pixel values mean, as far as the file said.</summary>
public enum ColorSpaceHint
{
    /// <summary>The file did not say. Egami treats the pixels as sRGB.</summary>
    Unknown,

    /// <summary>sRGB.</summary>
    Srgb,

    /// <summary>Linear light.</summary>
    Linear,

    /// <summary>A plain gamma curve, given by <see cref="RasterMetadata.Gamma"/>.</summary>
    Gamma,

    /// <summary>Described by the embedded <see cref="RasterMetadata.IccProfile"/>, which Egami keeps but does not apply.</summary>
    EmbeddedIcc,
}

/// <summary>A text entry, such as a PNG text chunk or a GIF comment.</summary>
/// <param name="Key">The keyword, such as "Title" or "Comment".</param>
/// <param name="Value">The text.</param>
/// <param name="Language">The language tag, if the format gives one.</param>
public sealed record TextEntry(string Key, string Value, string? Language = null);

/// <summary>Format-specific details the reader found, such as the original color type or palette. Each format derives its own.</summary>
public abstract class FormatDetails
{
    /// <summary>Returns a deep copy.</summary>
    /// <returns>The copy.</returns>
    public abstract FormatDetails Copy();
}

/// <summary>
/// Metadata that belongs to the whole raster. It is kept apart from the pixels and survives operations unless an operation makes it wrong.
/// Metadata problems never fail a load: broken blocks are kept as bytes and noted in <see cref="Warnings"/>.
/// </summary>
public sealed class RasterMetadata
{
    /// <summary>Gets or sets the physical pixel density, if known.</summary>
    public Resolution? Resolution { get; set; }

    /// <summary>Gets or sets the raw EXIF block, without any format-specific header. Empty if none.</summary>
    public ReadOnlyMemory<byte> Exif { get; set; }

    /// <summary>Gets or sets the embedded ICC profile. Egami keeps and writes it back but does not apply it. Empty if none.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; set; }

    /// <summary>Gets or sets the XMP packet as raw UTF-8 XML. Empty if none.</summary>
    public ReadOnlyMemory<byte> Xmp { get; set; }

    /// <summary>Gets or sets what the pixel values mean.</summary>
    public ColorSpaceHint ColorSpace { get; set; }

    /// <summary>Gets or sets the file's gamma when <see cref="ColorSpace"/> is <see cref="ColorSpaceHint.Gamma"/>, as the exponent that decodes values to linear light.</summary>
    public double? Gamma { get; set; }

    /// <summary>Gets or sets the name of the format the raster was read from, such as "PNG", or <see langword="null"/>.</summary>
    public string? FormatName { get; set; }

    /// <summary>Gets or sets format-specific details from the reader.</summary>
    public FormatDetails? FormatDetails { get; set; }

    /// <summary>Gets or sets how many times an animation plays. 0 means forever. Ignored for single-frame rasters.</summary>
    public int LoopCount { get; set; }

    /// <summary>Gets the text entries, in file order.</summary>
    public IList<TextEntry> Text { get; } = [];

    /// <summary>Gets the problems a lenient read recovered from, such as a bad checksum after valid pixel data.</summary>
    public IList<string> Warnings { get; } = [];

    /// <summary>Returns a deep copy.</summary>
    /// <returns>The copy.</returns>
    public RasterMetadata Copy()
    {
        RasterMetadata copy = new()
        {
            Resolution = Resolution,
            Exif = Exif.ToArray(),
            IccProfile = IccProfile.ToArray(),
            Xmp = Xmp.ToArray(),
            ColorSpace = ColorSpace,
            Gamma = Gamma,
            FormatName = FormatName,
            FormatDetails = FormatDetails?.Copy(),
            LoopCount = LoopCount,
        };
        foreach (TextEntry entry in Text)
        {
            copy.Text.Add(entry);
        }

        foreach (string warning in Warnings)
        {
            copy.Warnings.Add(warning);
        }

        return copy;
    }
}
