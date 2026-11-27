namespace Egami.Barcodes;

/// <summary>How <see cref="Barcode.AustraliaPost(string, AustraliaPostOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-australia-post"/>
/// </example>
public sealed record class AustraliaPostOptions
{
    /// <summary>Gets the defaults.</summary>
    public static AustraliaPostOptions Default { get; } = new();

    /// <summary>
    /// Gets the format, its Format Control Code; <see langword="null"/>, the default, takes <see cref="AustraliaPostFormat.Standard"/>
    /// for a DPID alone and the shorter customer barcode that holds any customer information.
    /// </summary>
    public AustraliaPostFormat? Format { get; init; }

    /// <summary>
    /// Gets the encoding table of the customer information; <see langword="null"/>, the default, takes
    /// <see cref="AustraliaPostEncoding.N"/> for digits and <see cref="AustraliaPostEncoding.C"/> for anything else.
    /// </summary>
    public AustraliaPostEncoding? Encoding { get; init; }
}

/// <summary>An Australia Post 4-State barcode's format, by its Format Control Code (FCC).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-australia-post"/>
/// </example>
public enum AustraliaPostFormat
{
    /// <summary>The Standard Customer Barcode (FCC 11): the DPID alone, 37 bars.</summary>
    Standard = 11,

    /// <summary>Customer Barcode 2 (FCC 59): the DPID and 16 bars of customer information, 52 bars.</summary>
    Customer2 = 59,

    /// <summary>Customer Barcode 3 (FCC 62): the DPID and 31 bars of customer information, 67 bars.</summary>
    Customer3 = 62,

    /// <summary>Australia Post's Reply Paid barcode (FCC 45), for its internal use: the DPID alone, 37 bars.</summary>
    ReplyPaid = 45,

    /// <summary>Australia Post's routing barcode (FCC 87), for its internal use: the DPID alone, 37 bars.</summary>
    Routing = 87,

    /// <summary>Australia Post's redirection barcode (FCC 92), for its internal use: the DPID alone, 37 bars.</summary>
    Redirection = 92,
}

/// <summary>The encoding table of an Australia Post barcode's customer information.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-australia-post"/>
/// </example>
public enum AustraliaPostEncoding
{
    /// <summary>The N table: digits, two bars each, so 8 in Customer Barcode 2 and 15 in Customer Barcode 3.</summary>
    N,

    /// <summary>
    /// The C table: the letters A to Z and a to z, digits, space and <c>#</c>, three bars each, so 5 in Customer Barcode 2 and
    /// 10 in Customer Barcode 3.
    /// </summary>
    C,
}
