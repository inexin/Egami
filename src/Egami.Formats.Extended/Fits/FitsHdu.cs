namespace Egami.Formats.Extended.Fits;

/// <summary>What a FITS HDU (header and data unit) holds.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-file"/>
/// </example>
public enum FitsHduKind
{
    /// <summary>The primary HDU, first in every file; it may hold an image or no data.</summary>
    Primary,

    /// <summary>An <c>IMAGE</c> extension.</summary>
    Image,

    /// <summary>A tile-compressed image: a binary table with <c>ZIMAGE = T</c>, as fpack writes.</summary>
    CompressedImage,

    /// <summary>A binary table (<c>BINTABLE</c>), skipped as data.</summary>
    BinaryTable,

    /// <summary>An ASCII table (<c>TABLE</c>), skipped as data.</summary>
    AsciiTable,

    /// <summary>Any other extension, or random groups, skipped as data.</summary>
    Other,
}

/// <summary>
/// One HDU of a FITS file: its header, and for an image, its BITPIX and axes. A tile-compressed image reports the image's own
/// BITPIX and axes (<c>ZBITPIX</c>, <c>ZNAXISn</c>), not the table's.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="fits-file"/>
/// </example>
public sealed class FitsHdu
{
    private FitsHdu(int index, FitsHduKind kind, FitsHeader header, int bitpix, long[] axes)
    {
        Index = index;
        Kind = kind;
        Header = header;
        Bitpix = bitpix;
        Axes = axes;
    }

    /// <summary>Gets the HDU's place in the file, from 0 for the primary HDU.</summary>
    public int Index { get; }

    /// <summary>Gets what the HDU holds.</summary>
    public FitsHduKind Kind { get; }

    /// <summary>Gets the header cards.</summary>
    public FitsHeader Header { get; }

    /// <summary>Gets BITPIX, the stored sample type: 8, 16, 32 or 64-bit integers, or -32 or -64 for floats.</summary>
    public int Bitpix { get; }

    /// <summary>Gets the length of each axis, <c>NAXIS1</c> (the row length) first.</summary>
    public IReadOnlyList<long> Axes { get; }

    /// <summary>Gets whether the HDU holds an image Egami reads: at least two axes, none of them 0.</summary>
    public bool IsImage => Kind is FitsHduKind.Primary or FitsHduKind.Image or FitsHduKind.CompressedImage && Axes.Count >= 2 && Planes > 0 && Axes[0] > 0 && Axes[1] > 0;

    /// <summary>Gets the tile compression (<c>ZCMPTYPE</c>, such as <c>RICE_1</c>), or <see langword="null"/>.</summary>
    public string? Compression { get; private init; }

    /// <summary>Gets the image's width, <c>NAXIS1</c>, or 0 when it holds no image.</summary>
    public int Width => IsImage ? (int)Axes[0] : 0;

    /// <summary>Gets the image's height, <c>NAXIS2</c>, or 0 when it holds no image.</summary>
    public int Height => IsImage ? (int)Axes[1] : 0;

    /// <summary>Gets the planes of a cube: the product of the axes after the second, 1 for a plain image.</summary>
    public long Planes
    {
        get
        {
            long planes = 1;
            for (int i = 2; i < Axes.Count; i++)
            {
                planes *= Axes[i];
            }

            return planes;
        }
    }

    /// <summary>Gets where the header starts in the file.</summary>
    internal long HeaderStart { get; private init; }

    /// <summary>Gets where the data starts in the file.</summary>
    internal long DataStart { get; private init; }

    /// <summary>Gets the length of the data as the header gives it, without the padding to a whole block.</summary>
    internal long DataLength { get; private init; }

    /// <summary>Gets the length of the data padded to a whole block.</summary>
    internal long PaddedLength => (DataLength + FitsHeaderReader.Block - 1) / FitsHeaderReader.Block * FitsHeaderReader.Block;

    /// <summary>Gets <c>BSCALE</c>.</summary>
    internal double Scale { get; private init; } = 1;

    /// <summary>Gets <c>BZERO</c>.</summary>
    internal double Zero { get; private init; }

    /// <summary>Gets <c>BLANK</c>, the stored integer that marks a missing pixel, or <see langword="null"/>.</summary>
    internal long? Blank { get; private init; }

    /// <summary>Gets the table's own header values a tile-compressed image needs.</summary>
    internal FitsTable? Table { get; private init; }

    /// <summary>Makes the HDU from its header, checking the structural keywords.</summary>
    internal static FitsHdu Create(int index, FitsHeader header, long headerStart, long dataStart, EgamiInput input)
    {
        FitsHduKind kind = FitsHduKind.Primary;
        if (index > 0)
        {
            kind = header.GetString("XTENSION")?.Trim() switch
            {
                "IMAGE" or "IUEIMAGE" => FitsHduKind.Image,
                "BINTABLE" or "A3DTABLE" => header.GetLogical("ZIMAGE") == true ? FitsHduKind.CompressedImage : FitsHduKind.BinaryTable,
                "TABLE" => FitsHduKind.AsciiTable,
                _ => FitsHduKind.Other,
            };
        }

        int bitpix = Bitpix0(header, "BITPIX", headerStart, input);
        long[] axes = Axes0(header, "NAXIS", headerStart, input);
        long pcount = header.GetInteger("PCOUNT") ?? 0;
        long gcount = header.GetInteger("GCOUNT") ?? 1;
        if (pcount < 0 || gcount < 0)
        {
            throw input.Corrupt(headerStart, $"PCOUNT {pcount} or GCOUNT {gcount} is negative.");
        }

        bool groups = kind == FitsHduKind.Primary && axes.Length > 0 && axes[0] == 0 && header.GetLogical("GROUPS") == true;
        long length;
        try
        {
            long count = axes.Length == 0 ? 0 : 1;
            for (int i = groups ? 1 : 0; i < axes.Length; i++)
            {
                count = checked(count * axes[i]);
            }

            length = checked(Math.Abs(bitpix) / 8 * gcount * (pcount + count));
        }
        catch (OverflowException)
        {
            throw input.Corrupt(headerStart, "The HDU's data is larger than any file can hold.");
        }

        if (groups)
        {
            kind = FitsHduKind.Other;
        }

        bool compressed = kind == FitsHduKind.CompressedImage;
        FitsHeader own = compressed ? ImageHeader(header) : header;
        int imageBitpix = compressed ? Bitpix0(header, "ZBITPIX", headerStart, input) : bitpix;
        return new FitsHdu(index, kind, own, imageBitpix, compressed ? Axes0(header, "ZNAXIS", headerStart, input) : axes)
        {
            Compression = compressed ? header.GetString("ZCMPTYPE")?.Trim() : null,
            Table = compressed ? FitsTable.Read(header, axes, pcount, headerStart, input) : null,
            HeaderStart = headerStart,
            DataStart = dataStart,
            DataLength = length,
            Scale = own.GetReal("BSCALE") ?? 1,
            Zero = own.GetReal("BZERO") ?? 0,
            Blank = imageBitpix > 0 ? own.GetInteger("BLANK") : null,
        };
    }

    /// <summary>
    /// Returns a tile-compressed image's own header: the table's cards with the table structure dropped and the <c>Z</c>
    /// keywords of the image put back under their own names, as the tiled-image convention keeps them.
    /// </summary>
    private static FitsHeader ImageHeader(FitsHeader table)
    {
        List<FitsCard> cards = [];
        foreach (FitsCard card in table)
        {
            string k = card.Keyword;
            if (k is "XTENSION" or "BITPIX" or "PCOUNT" or "GCOUNT" or "TFIELDS" or "THEAP" or "ZIMAGE" or "ZTENSION" or "ZPCOUNT" or "ZGCOUNT"
                || k.StartsWith("NAXIS", StringComparison.Ordinal) || k.StartsWith("TTYPE", StringComparison.Ordinal) || k.StartsWith("TFORM", StringComparison.Ordinal)
                || k.StartsWith("TUNIT", StringComparison.Ordinal) || k.StartsWith("TDIM", StringComparison.Ordinal))
            {
                continue;
            }

            cards.Add(k switch
            {
                "ZBITPIX" => card with { Keyword = "BITPIX" },
                "ZNAXIS" => card with { Keyword = "NAXIS" },
                "ZSIMPLE" => card with { Keyword = "SIMPLE" },
                "ZEXTEND" => card with { Keyword = "EXTEND" },
                "ZBLOCKED" => card with { Keyword = "BLOCKED" },
                "ZHECKSUM" => card with { Keyword = "CHECKSUM" },
                "ZDATASUM" => card with { Keyword = "DATASUM" },
                _ when k.StartsWith("ZNAXIS", StringComparison.Ordinal) => card with { Keyword = "NAXIS" + k[6..] },
                _ => card,
            });
        }

        return new FitsHeader(cards);
    }

    private static int Bitpix0(FitsHeader header, string keyword, long at, EgamiInput input)
    {
        long? bitpix = header.GetInteger(keyword);
        if (bitpix is not (8 or 16 or 32 or 64 or -32 or -64))
        {
            throw input.Corrupt(at, $"{keyword} is {(bitpix?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing")}; FITS allows 8, 16, 32, 64, -32 and -64.");
        }

        return (int)bitpix;
    }

    private static long[] Axes0(FitsHeader header, string keyword, long at, EgamiInput input)
    {
        long? naxis = header.GetInteger(keyword);
        if (naxis is not (>= 0 and <= 999))
        {
            throw input.Corrupt(at, $"{keyword} is {(naxis?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing")}; FITS allows 0 to 999.");
        }

        long[] axes = new long[(int)naxis];
        for (int i = 0; i < axes.Length; i++)
        {
            long? length = header.GetInteger(keyword + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (length is not >= 0)
            {
                throw input.Corrupt(at, $"{keyword}{i + 1} is missing or negative.");
            }

            axes[i] = length.Value;
        }

        if (axes.Length >= 2 && (axes[0] > int.MaxValue || axes[1] > int.MaxValue))
        {
            throw input.Corrupt(at, $"The image is {axes[0]}×{axes[1]}, larger than Egami reads.");
        }

        return axes;
    }
}
