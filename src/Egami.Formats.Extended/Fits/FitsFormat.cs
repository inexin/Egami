using System.Collections;
using System.Globalization;

using Egami.Metadata;

namespace Egami.Formats.Extended.Fits;

/// <summary>
/// FITS (Flexible Image Transport System, standard 4.0), astronomy's image format: headers of 80-character keyword cards in
/// 2880-byte blocks, then big-endian 8, 16, 32 or 64-bit integer or 32 or 64-bit float data. Load gives a viewable picture of
/// the first image, stretched into 0 to 1 (<see cref="FitsReaderOptions.Stretch"/>); LoadAll gives every image of every HDU and
/// every plane of a cube. <see cref="FitsFile"/> gives the header cards and the physical values themselves. Registered with
/// <see cref="ExtendedContextExtensions.WithFits"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-load"/>
/// </example>
public static class FitsFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "FITS",
        MimeTypes = ["image/fits", "application/fits"],
        Extensions = ["fits", "fit", "fts"],
        SignatureLength = 30,
        Matches = FitsHeaderReader.Matches,
        Reader = new FitsReader(),
        Writer = new FitsWriter(),
    };
}

/// <summary>How Load turns a FITS image's physical values into a picture.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-stretch"/>
/// </example>
public enum FitsStretch
{
    /// <summary>No stretch: the physical values themselves, as float, with missing pixels NaN.</summary>
    None,

    /// <summary>
    /// Linear from the smallest to the largest value, 0 to 1: <c>DATAMIN</c> and <c>DATAMAX</c> when the header has them,
    /// otherwise the image's own minimum and maximum. The default.
    /// </summary>
    MinMax,

    /// <summary>
    /// Linear between IRAF's zscale limits, astronomers' usual view: a line fitted to a sorted sample of up to 1000 pixels gives
    /// the range around the median, which shows faint detail and clips bright stars.
    /// </summary>
    ZScale,

    /// <summary>
    /// The inverse hyperbolic sine of the min-max values, <c>asinh(x / 0.1) / asinh(1 / 0.1)</c>: faint values rise steeply and
    /// bright ones compress, as in Lupton's colour images.
    /// </summary>
    Asinh,
}

/// <summary>Options for reading FITS.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-stretch"/>
/// </example>
public sealed record FitsReaderOptions : ReaderOptions
{
    private readonly int? _image;

    /// <summary>Gets the default options.</summary>
    public static new FitsReaderOptions Default { get; } = new();

    /// <summary>Gets how the physical values become a picture. Defaults to <see cref="FitsStretch.MinMax"/>.</summary>
    public FitsStretch Stretch { get; init; } = FitsStretch.MinMax;

    /// <summary>
    /// Gets which image to read, from 0, in the order LoadAll gives them: each image HDU in turn, and each plane of a cube.
    /// <see langword="null"/> (the default) reads the first.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? Image
    {
        get => _image;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The image index cannot be negative.");
            }

            _image = value;
        }
    }

    /// <summary>
    /// Gets whether a cube of three planes reads as one RGB image (red, green, blue) rather than three gray ones, all three
    /// stretched alike. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Rgb { get; init; }
}

/// <summary>Options for writing FITS.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-save"/>
/// </example>
public sealed class FitsWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new FitsWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the BITPIX to write: 8 (unsigned bytes), 16 (unsigned 16-bit, stored with <c>BZERO</c> 32768) or -32 (float), or
    /// <see langword="null"/> (the default) for the one that fits the pixel type: 8 for 8-bit pixels, 16 for 16-bit ones and -32
    /// for float.
    /// </summary>
    public int? Bitpix { get; init; }

    /// <summary>
    /// Gets keyword cards to add to the header, after those of a raster read from FITS (<see cref="FitsDetails.Header"/>). A card
    /// with a keyword the structure needs, such as <c>BITPIX</c> or <c>NAXIS1</c>, is ignored.
    /// </summary>
    public IReadOnlyList<FitsCard> Keywords { get; init; } = [];
}

/// <summary>
/// One header card: a keyword, its value and its comment. The value is a <see cref="string"/>, a <see cref="bool"/>, a
/// <see cref="long"/>, a <see cref="double"/>, or <see langword="null"/> for a card without one (<c>COMMENT</c>,
/// <c>HISTORY</c>, a blank keyword). A value the reader does not type, such as a complex number, is kept as its text.
/// </summary>
/// <param name="Keyword">The keyword, upper case, at most 8 characters (longer ones are <c>HIERARCH</c> keywords).</param>
/// <param name="Value">The value.</param>
/// <param name="Comment">The comment, or the text of a <c>COMMENT</c> or <c>HISTORY</c> card.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-file"/>
/// </example>
public sealed record FitsCard(string Keyword, object? Value, string? Comment = null)
{
    /// <summary>Returns the card as FITS writes it: <c>KEYWORD = value / comment</c>.</summary>
    /// <returns>The card's text.</returns>
    public override string ToString()
    {
        string value = Value switch
        {
            null => "",
            string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
            bool b => b ? "T" : "F",
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "",
        };
        return Value is null ? $"{Keyword} {Comment}".TrimEnd() : $"{Keyword} = {value}" + (Comment is null ? "" : $" / {Comment}");
    }
}

/// <summary>
/// The cards of one FITS header, in order, with typed lookups by keyword. Long string values continued over <c>CONTINUE</c>
/// cards are joined into one card.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-file"/>
/// </example>
public sealed class FitsHeader : IReadOnlyList<FitsCard>
{
    private readonly FitsCard[] _cards;

    /// <summary>Creates a header of the given cards.</summary>
    /// <param name="cards">The cards, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cards"/> is <see langword="null"/>.</exception>
    public FitsHeader(IEnumerable<FitsCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _cards = [.. cards];
    }

    /// <summary>Gets the number of cards.</summary>
    public int Count => _cards.Length;

    /// <summary>Gets a card by its place.</summary>
    /// <param name="index">The place, from 0.</param>
    /// <returns>The card.</returns>
    public FitsCard this[int index] => _cards[index];

    /// <summary>Returns the first card with the keyword, or <see langword="null"/>.</summary>
    /// <param name="keyword">The keyword, in any case.</param>
    /// <returns>The card, or <see langword="null"/>.</returns>
    public FitsCard? Find(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        foreach (FitsCard card in _cards)
        {
            if (string.Equals(card.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            {
                return card;
            }
        }

        return null;
    }

    /// <summary>Returns whether a card has the keyword.</summary>
    /// <param name="keyword">The keyword, in any case.</param>
    /// <returns>Whether it is there.</returns>
    public bool Contains(string keyword) => Find(keyword) is not null;

    /// <summary>Returns a keyword's string value, or <see langword="null"/> when it is missing or not a string.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>The value.</returns>
    public string? GetString(string keyword) => Find(keyword)?.Value as string;

    /// <summary>Returns a keyword's integer value, or <see langword="null"/> when it is missing or not an integer.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>The value.</returns>
    public long? GetInteger(string keyword) => Find(keyword)?.Value as long?;

    /// <summary>Returns a keyword's number value, integer or real, or <see langword="null"/> when it is missing or not a number.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>The value.</returns>
    public double? GetReal(string keyword) => Find(keyword)?.Value switch
    {
        long l => l,
        double d => d,
        _ => null,
    };

    /// <summary>Returns a keyword's logical value, or <see langword="null"/> when it is missing or not logical.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <returns>The value.</returns>
    public bool? GetLogical(string keyword) => Find(keyword)?.Value as bool?;

    /// <summary>Returns the cards in order.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<FitsCard> GetEnumerator() => ((IEnumerable<FitsCard>)_cards).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Details only FITS has, kept in <see cref="RasterMetadata.FormatDetails"/>: the HDU's header and how its values became the
/// picture. The writer writes the header's own keywords (all but the structural ones) back.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-stretch"/>
/// </example>
public sealed class FitsDetails : FormatDetails
{
    /// <summary>Gets or sets the header of the HDU the image came from (for a tile-compressed image, the image's own keywords).</summary>
    public FitsHeader Header { get; set; } = new([]);

    /// <summary>Gets or sets the HDU the image came from, from 0 for the primary HDU.</summary>
    public int Hdu { get; set; }

    /// <summary>Gets or sets the plane of a cube the image is, from 0.</summary>
    public int Plane { get; set; }

    /// <summary>Gets or sets the stored BITPIX: 8, 16, 32, 64, -32 or -64.</summary>
    public int Bitpix { get; set; }

    /// <summary>Gets or sets the tile compression (<c>ZCMPTYPE</c>, such as <c>RICE_1</c>), or <see langword="null"/> for none.</summary>
    public string? Compression { get; set; }

    /// <summary>Gets or sets the stretch Load applied.</summary>
    public FitsStretch Stretch { get; set; }

    /// <summary>Gets or sets the physical value the stretch put at 0 (NaN when there was no stretch).</summary>
    public double Low { get; set; } = double.NaN;

    /// <summary>Gets or sets the physical value the stretch put at 1 (NaN when there was no stretch).</summary>
    public double High { get; set; } = double.NaN;

    /// <inheritdoc/>
    public override FormatDetails Copy() => (FitsDetails)MemberwiseClone();
}
