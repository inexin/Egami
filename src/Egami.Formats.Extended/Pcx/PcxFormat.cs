using Egami.Metadata;

namespace Egami.Formats.Extended.Pcx;

/// <summary>
/// PCX, ZSoft's PC Paintbrush format: a 128-byte header, then run-length coded scanlines of one to four color planes. Registered
/// with <see cref="ExtendedContextExtensions.WithPcx"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class PcxFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "PCX",
        MimeTypes = ["image/vnd.zbrush.pcx", "image/x-pcx"],
        Extensions = ["pcx"],
        SignatureLength = PcxReader.SignatureLength,
        Matches = PcxReader.Matches,
        Reader = new PcxReader(),
        Writer = new PcxWriter(),
    };
}

/// <summary>Details only PCX has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="details"/>
/// </example>
public sealed class PcxDetails : FormatDetails
{
    /// <summary>Gets or sets the version byte: 0 (PC Paintbrush 2.5), 2 or 3 (2.8), 4 (for Windows) or 5 (3.0 and later).</summary>
    public int Version { get; set; } = 5;

    /// <summary>Gets or sets the bits per pixel in each plane: 1, 2, 4 or 8.</summary>
    public int BitsPerPixel { get; set; } = 8;

    /// <summary>Gets or sets the number of color planes: 1 to 4.</summary>
    public int Planes { get; set; } = 1;

    /// <summary>Gets or sets the palette of an indexed image, or empty for 24-bit and 32-bit images.</summary>
    public ReadOnlyMemory<Pixels.Rgb8> Palette { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new PcxDetails { Version = Version, BitsPerPixel = BitsPerPixel, Planes = Planes, Palette = Palette.ToArray() };
    }
}
