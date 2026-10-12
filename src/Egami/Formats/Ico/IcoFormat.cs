using Egami.Metadata;
using Egami.Operations;

namespace Egami.Formats.Ico;

/// <summary>
/// The Windows icon format, registered in <see cref="EgamiContext.Default"/>. An icon holds several images (entries), each a PNG
/// file or a BMP without its file header. Load returns the best entry, LoadAll every entry, and Save writes one entry per raster
/// or a set resized to <see cref="IcoWriterOptions.Sizes"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="ico-load"/>
/// </example>
public static class IcoFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "ICO",
        MimeTypes = ["image/x-icon", "image/vnd.microsoft.icon"],
        Extensions = ["ico"],
        SignatureLength = IcoDirectory.SignatureLength,
        Matches = header => IcoDirectory.Matches(header, IcoDirectory.IconType),
        Reader = IcoReader.Icon,
        Writer = IcoWriter.Icon,
    };
}

/// <summary>
/// The Windows cursor format, registered in <see cref="EgamiContext.Default"/>: an icon whose entries also carry a hotspot, the
/// pixel that points. Read and written as <see cref="IcoFormat"/>, with <see cref="IcoDetails.Hotspot"/> and
/// <see cref="IcoWriterOptions.Hotspot"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="cur-save"/>
/// </example>
public static class CurFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "CUR",
        MimeTypes = ["image/x-win-bitmap"],
        Extensions = ["cur"],
        SignatureLength = IcoDirectory.SignatureLength,
        Matches = header => IcoDirectory.Matches(header, IcoDirectory.CursorType),
        Reader = IcoReader.Cursor,
        Writer = IcoWriter.Cursor,
    };
}

/// <summary>Options for reading ICO and CUR.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="ico-entry"/>
/// </example>
public sealed record IcoReaderOptions : ReaderOptions
{
    /// <summary>
    /// Gets the entry Load returns, by its index in the file's directory, or <see langword="null"/> (the default) for the best:
    /// the largest, then the deepest (a PNG entry counts as 32 bits), then the first. With <see cref="ReaderOptions.MinimumSize"/>
    /// and no entry named, the smallest entry at least that big is chosen before any reduction.
    /// </summary>
    public int? Entry { get; init; }
}

/// <summary>
/// Options for writing ICO and CUR. Each raster becomes one entry at its own size, or one entry per size in <see cref="Sizes"/>;
/// entries are 8-bit RGBA, sorted smallest first. EXIF, XMP and IPTC are not written; an ICC profile goes into PNG entries.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="ico-save"/>
/// </example>
public sealed class IcoWriterOptions : WriterOptions
{
    private readonly IReadOnlyList<int>? _sizes;

    /// <summary>Gets the default options.</summary>
    public static new IcoWriterOptions Default { get; } = new();

    /// <summary>Gets the sizes of a favicon: 16, 32 and 48.</summary>
    public static IReadOnlyList<int> FaviconSizes { get; } = [16, 32, 48];

    /// <summary>Gets the sizes Windows' icon guidance asks for: 16, 24, 32, 48, 64 and 256.</summary>
    public static IReadOnlyList<int> WindowsSizes { get; } = [16, 24, 32, 48, 64, 256];

    /// <summary>
    /// Gets the square sizes to write, 1 to 256, or <see langword="null"/> (the default) to write each raster at its own size, a
    /// raster larger than 256 fitted down to 256 with a warning. Each size comes from a raster of exactly that size when one is
    /// given, otherwise from the largest raster resized with <see cref="Filter"/>; a raster that is not square is fitted inside
    /// the square and centred on transparency.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is outside 1 to 256.</exception>
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
                    ArgumentOutOfRangeException.ThrowIfLessThan(size, 1, nameof(value));
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256, nameof(value));
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

    /// <summary>Gets how each entry is stored. Defaults to <see cref="IcoEntryEncoding.Auto"/>.</summary>
    public IcoEntryEncoding Encoding { get; init; }

    /// <summary>Gets the filter that resizes rasters to <see cref="Sizes"/>, as for Resize. Defaults to <see cref="ResampleFilter.Bicubic"/>.</summary>
    public ResampleFilter Filter { get; init; }

    /// <summary>
    /// Gets a cursor's hotspot at the raster's own size, scaled to each entry and kept inside it, or <see langword="null"/> (the
    /// default) for the raster's <see cref="IcoDetails.Hotspot"/>, else the top-left pixel. Icons ignore it.
    /// </summary>
    public Point? Hotspot { get; init; }
}

/// <summary>How an icon entry's pixels are stored.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="ico-save"/>
/// </example>
public enum IcoEntryEncoding
{
    /// <summary>
    /// For writing: PNG for 256 × 256 entries and 32-bit BMP with an AND mask below, as Microsoft's icon guidance asks; an entry
    /// written at the size of a raster loaded from an icon keeps that entry's encoding. Never reported for an entry read.
    /// </summary>
    Auto,

    /// <summary>A whole PNG file.</summary>
    Png,

    /// <summary>A BMP without its file header: the color rows, then a 1-bit AND mask that marks transparent pixels.</summary>
    Bmp,
}

/// <summary>One entry of an icon's directory, as read from the entry's own header.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="BitsPerPixel">The bits a pixel: 1, 4, 8, 16, 24 or 32 for BMP entries; the PNG's bit depth times its channels for PNG entries.</param>
/// <param name="Encoding">How the pixels are stored: <see cref="IcoEntryEncoding.Png"/> or <see cref="IcoEntryEncoding.Bmp"/>.</param>
/// <param name="Hotspot">The cursor's hotspot, or <see langword="null"/> in an icon.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="ico-sizes"/>
/// </example>
public sealed record IcoEntry(int Width, int Height, int BitsPerPixel, IcoEntryEncoding Encoding, Point? Hotspot);

/// <summary>Details only ICO and CUR have, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ico.cs" region="ico-sizes"/>
/// </example>
public sealed class IcoDetails : FormatDetails
{
    /// <summary>Gets or sets the entry's index in the directory.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets how the entry was stored, which the writer keeps when it writes a raster at its own size.</summary>
    public IcoEntryEncoding Encoding { get; set; } = IcoEntryEncoding.Png;

    /// <summary>Gets or sets the entry's bits a pixel, as <see cref="IcoEntry.BitsPerPixel"/>.</summary>
    public int BitsPerPixel { get; set; } = 32;

    /// <summary>Gets or sets the cursor's hotspot, or <see langword="null"/> for an icon.</summary>
    public Point? Hotspot { get; set; }

    /// <summary>Gets or sets the whole directory, every entry that could be read, so the sizes are known without decoding them.</summary>
    public IReadOnlyList<IcoEntry> Entries { get; set; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new IcoDetails { Index = Index, Encoding = Encoding, BitsPerPixel = BitsPerPixel, Hotspot = Hotspot, Entries = [.. Entries] };
    }
}
