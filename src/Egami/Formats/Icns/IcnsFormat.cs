using Egami.Metadata;
using Egami.Operations;

namespace Egami.Formats.Icns;

/// <summary>
/// Apple's icon format, registered in <see cref="EgamiContext.Default"/>. An icon family holds several images (entries) of
/// different sizes: PNG or JPEG 2000 files, run-length encoded ARGB or RGB with an 8-bit mask, or the old 1, 4 and 8-bit icons
/// with a 1-bit mask. Load returns the best entry, LoadAll every entry, and Save writes one PNG entry per raster or a set resized
/// to <see cref="IcnsWriterOptions.Sizes"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Icns.cs" region="icns-load"/>
/// </example>
public static class IcnsFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "ICNS",
        MimeTypes = ["image/icns"],
        Extensions = ["icns"],
        SignatureLength = 8,
        Matches = IcnsDirectory.Matches,
        Reader = IcnsReader.Instance,
        Writer = IcnsWriter.Instance,
    };
}

/// <summary>Options for reading ICNS.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Icns.cs" region="icns-entry"/>
/// </example>
public sealed record IcnsReaderOptions : ReaderOptions
{
    /// <summary>
    /// Gets the entry Load returns, by its index among the file's image entries (<see cref="IcnsDetails.Entries"/>), or
    /// <see langword="null"/> (the default) for the best: the largest, then the deepest, then the first. With
    /// <see cref="ReaderOptions.MinimumSize"/> and no entry named, the smallest entry at least that big is chosen before any
    /// reduction.
    /// </summary>
    public int? Entry { get; init; }
}

/// <summary>
/// Options for writing ICNS. Each raster becomes one PNG entry, or one entry per size in <see cref="Sizes"/>; entries are 8-bit
/// RGBA, sorted smallest first, and a table of contents comes first as macOS writes it. EXIF, XMP and IPTC are not written.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Icns.cs" region="icns-save"/>
/// </example>
public sealed class IcnsWriterOptions : WriterOptions
{
    private readonly IReadOnlyList<int>? _sizes;

    /// <summary>Gets the default options.</summary>
    public static new IcnsWriterOptions Default { get; } = new();

    /// <summary>Gets the sizes an app icon has on macOS: 16, 32, 64, 128, 256, 512 and 1024, the ones each point size needs at 1× and 2×.</summary>
    public static IReadOnlyList<int> MacSizes { get; } = [16, 32, 64, 128, 256, 512, 1024];

    /// <summary>
    /// Gets the square sizes to write, each one of <see cref="MacSizes"/>, or <see langword="null"/> (the default) to write each
    /// raster at the largest of those sizes that it fills, so a raster of one of those sizes is written as it is. Each size comes
    /// from a raster of exactly that size when one is given, otherwise from the largest raster resized with
    /// <see cref="Filter"/>; a raster that is not square is fitted inside the square and centred on transparency.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is not one of <see cref="MacSizes"/>.</exception>
    /// <exception cref="ArgumentException">The list is empty or names a size twice.</exception>
    public IReadOnlyList<int>? Sizes
    {
        get => _sizes;
        init
        {
            if (value is not null)
            {
                if (value.Count == 0)
                {
                    throw new ArgumentException("Sizes needs at least one size; use null to write each raster at its own size.", nameof(value));
                }

                foreach (int size in value)
                {
                    if (!MacSizes.Contains(size))
                    {
                        throw new ArgumentOutOfRangeException(nameof(value), size, "An ICNS entry is 16, 32, 64, 128, 256, 512 or 1024 pixels square.");
                    }
                }

                if (value.Distinct().Count() != value.Count)
                {
                    throw new ArgumentException("Sizes names a size twice.", nameof(value));
                }

                value = [.. value];
            }

            _sizes = value;
        }
    }

    /// <summary>Gets the filter that resizes rasters to their entries, as for Resize. Defaults to <see cref="ResampleFilter.Bicubic"/>.</summary>
    public ResampleFilter Filter { get; init; }
}

/// <summary>How an ICNS entry's pixels are stored.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Icns.cs" region="icns-sizes"/>
/// </example>
public enum IcnsEntryEncoding
{
    /// <summary>A whole PNG file.</summary>
    Png,

    /// <summary>A whole JPEG 2000 file or codestream.</summary>
    Jpeg2000,

    /// <summary>Run-length encoded alpha, red, green and blue planes after an <c>ARGB</c> tag.</summary>
    Argb,

    /// <summary>Run-length encoded red, green and blue planes, with alpha from the matching 8-bit mask entry when there is one.</summary>
    Rgb,

    /// <summary>The old 1, 4 or 8-bit icons on the Mac OS system palette, with alpha from the matching 1-bit mask.</summary>
    Indexed,
}

/// <summary>One image entry of an icon family.</summary>
/// <param name="Type">The entry's four-character type, such as <c>ic08</c> or <c>il32</c>.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="BitsPerPixel">1, 4, 8, 24 (RGB without a mask) or 32.</param>
/// <param name="Encoding">How the pixels are stored.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Icns.cs" region="icns-sizes"/>
/// </example>
public sealed record IcnsEntry(string Type, int Width, int Height, int BitsPerPixel, IcnsEntryEncoding Encoding);

/// <summary>Details only ICNS has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Icns.cs" region="icns-sizes"/>
/// </example>
public sealed class IcnsDetails : FormatDetails
{
    /// <summary>Gets or sets the entry's index in <see cref="Entries"/>.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the entry's four-character type.</summary>
    public string Type { get; set; } = "";

    /// <summary>Gets or sets how the entry was stored.</summary>
    public IcnsEntryEncoding Encoding { get; set; } = IcnsEntryEncoding.Png;

    /// <summary>Gets or sets the entry's bits a pixel, as <see cref="IcnsEntry.BitsPerPixel"/>.</summary>
    public int BitsPerPixel { get; set; } = 32;

    /// <summary>Gets or sets every image entry of the file, so the sizes are known without decoding them. Masks are not listed.</summary>
    public IReadOnlyList<IcnsEntry> Entries { get; set; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new IcnsDetails { Index = Index, Type = Type, Encoding = Encoding, BitsPerPixel = BitsPerPixel, Entries = [.. Entries] };
    }
}
