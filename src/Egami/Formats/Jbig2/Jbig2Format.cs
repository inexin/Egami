using Egami.Metadata;

namespace Egami.Formats.Jbig2;

/// <summary>
/// The JBIG2 bi-level format (ITU-T T.88), read only, registered in <see cref="EgamiContext.Default"/>. Standalone files are
/// detected by their header; the embedded form a PDF holds has none, so it loads only with the format named, as in
/// <see cref="Raster.Load(ReadOnlyMemory{byte}, FormatInfo, EgamiContext?, ReaderOptions?)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-load"/>
/// </example>
public static class Jbig2Format
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature and reader. It has no writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "JBIG2",
        MimeTypes = ["image/x-jbig2"],
        Extensions = ["jb2", "jbig2"],
        SignatureLength = 8,
        Matches = header => header.Length >= 8 && header[..8].SequenceEqual(Jbig2Stream.Id),
        Reader = Jbig2Reader.Instance,
    };
}

/// <summary>Options for reading JBIG2.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-embedded"/>
/// </example>
public sealed record Jbig2ReaderOptions : ReaderOptions
{
    /// <summary>
    /// Gets the parsed globals stream (a PDF's JBIG2Globals) whose dictionaries the page's segments refer to, or
    /// <see langword="null"/> (the default) for none.
    /// </summary>
    public Jbig2Globals? Globals { get; init; }

    /// <summary>Gets the page Load returns, by its index in the file, or <see langword="null"/> (the default) for the first.</summary>
    public int? Page { get; init; }
}

/// <summary>
/// A parsed JBIG2 globals stream: the symbol dictionaries, pattern dictionaries and code tables a PDF shares between the JBIG2
/// images of a document. Parsing decodes them once; the object is immutable and safe to share across threads, so every page
/// that refers to it reuses the decoded symbols.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-embedded"/>
/// </example>
public sealed class Jbig2Globals
{
    private Jbig2Globals(Jbig2Stream stream, Dictionary<Jbig2Segment, object> results)
    {
        Stream = stream;
        Results = results;
    }

    /// <summary>Gets the number of segments in the globals stream.</summary>
    public int SegmentCount => Stream.Segments.Count;

    internal Jbig2Stream Stream { get; }

    internal IReadOnlyDictionary<Jbig2Segment, object> Results { get; }

    /// <summary>Parses a globals stream and decodes its dictionaries.</summary>
    /// <param name="data">The globals stream's bytes, as the PDF's JBIG2Globals stream holds them after its filters.</param>
    /// <param name="context">The context whose safety limits apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The parsed globals.</returns>
    /// <exception cref="CorruptDataException">The data breaks T.88's rules.</exception>
    /// <exception cref="LimitExceededException">The dictionaries break a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-embedded"/>
    /// </example>
    public static Jbig2Globals Parse(ReadOnlySpan<byte> data, EgamiContext? context = null)
    {
        context ??= EgamiContext.Default;
        if (data.Length > context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, data.Length);
        }

        byte[] bytes = data.ToArray();
        try
        {
            Jbig2Stream stream = Jbig2Stream.Parse(bytes, bytes.Length, embedded: true, Jbig2Reader.MaxSegments);
            Jbig2Budget budget = new(context.Limits);
            Jbig2Decoder decoder = new(stream, globals: null, budget, strict: true);
            return new Jbig2Globals(stream, decoder.DecodeDictionaries());
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or Jbig2MissingSegmentException)
        {
            throw new CorruptDataException(Jbig2Format.Info.Name, -1, $"The JBIG2 globals are damaged: {e.Message}", e);
        }
    }
}

/// <summary>How a JBIG2 stream lays out its segments (T.88 Annex D).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-details"/>
/// </example>
public enum Jbig2Organisation
{
    /// <summary>A standalone file, each segment's header followed by its data (D.1).</summary>
    Sequential,

    /// <summary>A standalone file, every segment header first and the data after (D.2).</summary>
    RandomAccess,

    /// <summary>Segments without a file header, as a PDF embeds them (D.3).</summary>
    Embedded,
}

/// <summary>How a page combines regions with what lies under them (T.88 7.4.8.5).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-details"/>
/// </example>
public enum Jbig2CombinationOperator
{
    /// <summary>OR (0).</summary>
    Or = 0,

    /// <summary>AND (1).</summary>
    And = 1,

    /// <summary>XOR (2).</summary>
    Xor = 2,

    /// <summary>XNOR (3).</summary>
    Xnor = 3,
}

/// <summary>The kinds of region a JBIG2 page is made of.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-details"/>
/// </example>
[Flags]
public enum Jbig2RegionKinds
{
    /// <summary>No regions: the page is its default pixel.</summary>
    None = 0,

    /// <summary>Generic regions, coded pixel by pixel (arithmetic or MMR).</summary>
    Generic = 1,

    /// <summary>Refinement regions.</summary>
    Refinement = 2,

    /// <summary>Text regions, drawn from symbol dictionaries.</summary>
    Text = 4,

    /// <summary>Halftone regions, drawn from pattern dictionaries.</summary>
    Halftone = 8,
}

/// <summary>Details only JBIG2 has, kept per page in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jbig2.cs" region="jbig2-details"/>
/// </example>
public sealed class Jbig2Details : FormatDetails
{
    /// <summary>Gets or sets the page's index in the stream, counting from 0.</summary>
    public int PageIndex { get; set; }

    /// <summary>Gets or sets how the stream lays out its segments.</summary>
    public Jbig2Organisation Organisation { get; set; }

    /// <summary>Gets or sets whether the page refers to dictionaries in <see cref="Jbig2ReaderOptions.Globals"/>.</summary>
    public bool UsesGlobals { get; set; }

    /// <summary>Gets or sets whether the page is coded in stripes (end of stripe segments).</summary>
    public bool Striped { get; set; }

    /// <summary>Gets or sets the most rows a stripe may have, or 0 when the page is not striped.</summary>
    public int MaxStripeSize { get; set; }

    /// <summary>Gets or sets the page's pixel before any region is drawn: 0 white or 1 black.</summary>
    public int DefaultPixel { get; set; }

    /// <summary>Gets or sets the page's default combination operator.</summary>
    public Jbig2CombinationOperator CombinationOperator { get; set; }

    /// <summary>Gets or sets the kinds of region the page is made of.</summary>
    public Jbig2RegionKinds Regions { get; set; }

    /// <summary>Gets or sets whether the page information says the page is eventually lossless.</summary>
    public bool Lossless { get; set; }

    /// <summary>Gets the comments of the stream's and the page's comment extension segments, as name and value pairs.</summary>
    public IList<KeyValuePair<string, string>> Comments { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        Jbig2Details copy = new()
        {
            PageIndex = PageIndex,
            Organisation = Organisation,
            UsesGlobals = UsesGlobals,
            Striped = Striped,
            MaxStripeSize = MaxStripeSize,
            DefaultPixel = DefaultPixel,
            CombinationOperator = CombinationOperator,
            Regions = Regions,
            Lossless = Lossless,
        };
        foreach (KeyValuePair<string, string> comment in Comments)
        {
            copy.Comments.Add(comment);
        }

        return copy;
    }
}
