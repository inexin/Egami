namespace Egami.Metadata;

/// <summary>
/// An IPTC-IIM dataset: its record and dataset numbers, as "2:25" for keywords. The named keys cover the datasets news, stock and
/// photo software read and write; any other pair works too.
/// </summary>
/// <param name="Record">The record number: 1 for the envelope, 2 for the application record that holds the descriptive text.</param>
/// <param name="Dataset">The dataset number within the record.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="iptc-values"/>
/// </example>
public readonly record struct IptcKey(byte Record, byte Dataset)
{
    /// <summary>The coded character set (1:90); ESC % G means UTF-8. Egami writes it when a value is not plain ASCII.</summary>
    public static readonly IptcKey CodedCharacterSet = new(1, 90);

    /// <summary>A short name for the object, such as a slug or file name (2:5, up to 64 bytes).</summary>
    public static readonly IptcKey ObjectName = new(2, 5);

    /// <summary>The editorial urgency, "1" (most urgent) to "8" (2:10, 1 byte).</summary>
    public static readonly IptcKey Urgency = new(2, 10);

    /// <summary>The subject category, a three-letter code (2:15, up to 3 bytes; deprecated by IPTC but still written).</summary>
    public static readonly IptcKey Category = new(2, 15);

    /// <summary>A supplemental category; repeatable (2:20, up to 32 bytes each).</summary>
    public static readonly IptcKey SupplementalCategory = new(2, 20);

    /// <summary>A keyword; repeatable (2:25, up to 64 bytes each).</summary>
    public static readonly IptcKey Keywords = new(2, 25);

    /// <summary>Instructions for editors, such as embargoes (2:40, up to 256 bytes).</summary>
    public static readonly IptcKey SpecialInstructions = new(2, 40);

    /// <summary>When the content was created, as CCYYMMDD (2:55, 8 bytes).</summary>
    public static readonly IptcKey DateCreated = new(2, 55);

    /// <summary>The time the content was created, as HHMMSS±HHMM (2:60, 11 bytes).</summary>
    public static readonly IptcKey TimeCreated = new(2, 60);

    /// <summary>A creator's name; repeatable (2:80, up to 32 bytes each).</summary>
    public static readonly IptcKey Byline = new(2, 80);

    /// <summary>A creator's job title; repeatable (2:85, up to 32 bytes each).</summary>
    public static readonly IptcKey BylineTitle = new(2, 85);

    /// <summary>The city shown (2:90, up to 32 bytes).</summary>
    public static readonly IptcKey City = new(2, 90);

    /// <summary>The place within the city (2:92, up to 32 bytes).</summary>
    public static readonly IptcKey SubLocation = new(2, 92);

    /// <summary>The province or state (2:95, up to 32 bytes).</summary>
    public static readonly IptcKey ProvinceState = new(2, 95);

    /// <summary>The ISO 3166 country code, such as "NOR" (2:100, up to 3 bytes).</summary>
    public static readonly IptcKey CountryCode = new(2, 100);

    /// <summary>The country name (2:101, up to 64 bytes).</summary>
    public static readonly IptcKey Country = new(2, 101);

    /// <summary>The original transmission reference, a job identifier (2:103, up to 32 bytes).</summary>
    public static readonly IptcKey OriginalTransmissionReference = new(2, 103);

    /// <summary>A headline (2:105, up to 256 bytes).</summary>
    public static readonly IptcKey Headline = new(2, 105);

    /// <summary>The provider credited (2:110, up to 32 bytes).</summary>
    public static readonly IptcKey Credit = new(2, 110);

    /// <summary>The original owner of the content (2:115, up to 32 bytes).</summary>
    public static readonly IptcKey Source = new(2, 115);

    /// <summary>The copyright notice (2:116, up to 128 bytes).</summary>
    public static readonly IptcKey CopyrightNotice = new(2, 116);

    /// <summary>The caption or description (2:120, up to 2,000 bytes).</summary>
    public static readonly IptcKey Caption = new(2, 120);

    /// <summary>Who wrote the caption; repeatable (2:122, up to 32 bytes each).</summary>
    public static readonly IptcKey CaptionWriter = new(2, 122);

    internal static readonly IptcKey EnvelopeRecordVersion = new(1, 0);
    internal static readonly IptcKey RecordVersion = new(2, 0);

    /// <summary>Gets the longest value the IIM 4.2 specification allows for this dataset, in bytes, or <see langword="null"/> when Egami sets no cap.</summary>
    public int? MaxLength => (Record, Dataset) switch
    {
        (2, 5) => 64,
        (2, 10) => 1,
        (2, 15) => 3,
        (2, 20) => 32,
        (2, 25) => 64,
        (2, 40) => 256,
        (2, 55) => 8,
        (2, 60) => 11,
        (2, 80) => 32,
        (2, 85) => 32,
        (2, 90) => 32,
        (2, 92) => 32,
        (2, 95) => 32,
        (2, 100) => 3,
        (2, 101) => 64,
        (2, 103) => 32,
        (2, 105) => 256,
        (2, 110) => 32,
        (2, 115) => 32,
        (2, 116) => 128,
        (2, 120) => 2000,
        (2, 122) => 32,
        _ => null,
    };

    /// <summary>Gets whether the dataset may appear more than once (keywords, bylines and the other lists); unknown datasets may.</summary>
    public bool IsRepeatable => (Record, Dataset) switch
    {
        (1, _) => false,
        (2, 4) or (2, 12) or (2, 20) or (2, 25) or (2, 80) or (2, 85) or (2, 118) or (2, 122) => true,
        (2, _) => MaxLength is null && Dataset != 0,
        _ => true,
    };

    /// <inheritdoc/>
    public override string ToString() => $"{Record}:{Dataset}";
}
