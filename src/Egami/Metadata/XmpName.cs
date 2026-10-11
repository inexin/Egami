namespace Egami.Metadata;

/// <summary>
/// An XMP property name: a namespace URI and a local name, such as dc:title. Prefixes vary between files, so Egami never matches
/// on them; <see cref="XmpNamespaces"/> holds the URIs of the common schemas.
/// </summary>
/// <param name="Namespace">The namespace URI, such as <see cref="XmpNamespaces.DublinCore"/>.</param>
/// <param name="Name">The local name, such as "title".</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-values"/>
/// </example>
public readonly record struct XmpName(string Namespace, string Name)
{
    /// <inheritdoc/>
    public override string ToString() => $"{{{Namespace}}}{Name}";
}

/// <summary>The namespace URIs of the XMP schemas apps use most.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-values"/>
/// </example>
public static class XmpNamespaces
{
    /// <summary>Dublin Core (dc): title, description, creator, subject (keywords), rights.</summary>
    public const string DublinCore = "http://purl.org/dc/elements/1.1/";

    /// <summary>XMP Basic (xmp): CreateDate, ModifyDate, CreatorTool, Rating, Label.</summary>
    public const string XmpBasic = "http://ns.adobe.com/xap/1.0/";

    /// <summary>XMP Rights Management (xmpRights): Marked, WebStatement, UsageTerms.</summary>
    public const string XmpRights = "http://ns.adobe.com/xap/1.0/rights/";

    /// <summary>XMP Media Management (xmpMM): DocumentID, InstanceID, History.</summary>
    public const string XmpMediaManagement = "http://ns.adobe.com/xap/1.0/mm/";

    /// <summary>Photoshop (photoshop): Headline, City, Country, Credit, Source.</summary>
    public const string Photoshop = "http://ns.adobe.com/photoshop/1.0/";

    /// <summary>TIFF properties (tiff): Orientation, Make, Model, ImageWidth.</summary>
    public const string Tiff = "http://ns.adobe.com/tiff/1.0/";

    /// <summary>EXIF properties (exif): PixelXDimension, SubjectArea, DateTimeOriginal.</summary>
    public const string Exif = "http://ns.adobe.com/exif/1.0/";

    /// <summary>EXIF 2.3 and later properties, in CIPA's namespace (exifEX): LensModel, BodySerialNumber.</summary>
    public const string ExifCipa = "http://cipa.jp/exif/1.0/";

    /// <summary>IPTC Core (Iptc4xmpCore): CreatorContactInfo, Location, SubjectCode.</summary>
    public const string IptcCore = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";

    /// <summary>IPTC Extension (Iptc4xmpExt): LocationCreated, PersonInImage, ArtworkOrObject.</summary>
    public const string IptcExtension = "http://iptc.org/std/Iptc4xmpExt/2008-02-29/";

    /// <summary>Camera Raw settings (crs).</summary>
    public const string CameraRaw = "http://ns.adobe.com/camera-raw-settings/1.0/";

    /// <summary>RDF, the syntax XMP is written in; its names are structure, not properties.</summary>
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>The xml: namespace, which holds xml:lang.</summary>
    public const string Xml = "http://www.w3.org/XML/1998/namespace";

    /// <summary>The prefixes Adobe's toolkit uses for these namespaces, for a packet written without one.</summary>
    internal static string? DefaultPrefix(string ns) => ns switch
    {
        DublinCore => "dc",
        XmpBasic => "xmp",
        XmpRights => "xmpRights",
        XmpMediaManagement => "xmpMM",
        Photoshop => "photoshop",
        Tiff => "tiff",
        Exif => "exif",
        ExifCipa => "exifEX",
        IptcCore => "Iptc4xmpCore",
        IptcExtension => "Iptc4xmpExt",
        CameraRaw => "crs",
        "http://ns.adobe.com/xmp/note/" => "xmpNote",
        "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#" => "stEvt",
        "http://ns.adobe.com/xap/1.0/sType/ResourceRef#" => "stRef",
        "http://ns.adobe.com/xap/1.0/g/img/" => "xmpGImg",
        "http://ns.adobe.com/lightroom/1.0/" => "lr",
        "http://ns.adobe.com/xmp/1.0/DynamicMedia/" => "xmpDM",
        "http://ns.google.com/photos/1.0/camera/" => "GCamera",
        "http://ns.google.com/photos/1.0/image/" => "GImage",
        _ => null,
    };
}
