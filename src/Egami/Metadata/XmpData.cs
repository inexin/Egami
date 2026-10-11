namespace Egami.Metadata;

/// <summary>
/// A parsed XMP packet: its properties by namespace URI and local name, each a simple value, an <see cref="XmpArray"/> or an
/// <see cref="XmpStruct"/>, with typed getters and setters for what apps ask for most (Dublin Core, XMP Basic, Photoshop and
/// TIFF properties). An unedited packet writes back byte for byte; an edited one is written as canonical RDF/XML with padding,
/// keeping every property it does not know, qualifiers included.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-values"/>
/// </example>
public sealed class XmpData : XmpStruct, IXmpOwner
{
    private readonly List<string> _warnings = [];
    private byte[] _original = [];
    private byte[]? _written;
    private bool _edited;

    /// <summary>Creates an empty packet, to fill with the setters.</summary>
    public XmpData()
    {
        Owner = this;
        _edited = true;
    }

    /// <summary>
    /// Gets whether the packet could not be read: XML that is not well-formed, no rdf:RDF, or more data than allowed. It then has no
    /// properties, and its original bytes are written back unless properties are set.
    /// </summary>
    public bool IsDamaged { get; private set; }

    /// <summary>Gets what was wrong with the packet when read, as sentences.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Gets or sets the title (dc:title), the x-default item of its language alternatives. Setting <see langword="null"/> removes it.</summary>
    public string? Title
    {
        get => GetText(Dc("title"));
        set => SetLanguageText(Dc("title"), value);
    }

    /// <summary>Gets or sets the description or caption (dc:description), the x-default item. Setting <see langword="null"/> removes it.</summary>
    public string? Description
    {
        get => GetText(Dc("description"));
        set => SetLanguageText(Dc("description"), value);
    }

    /// <summary>Gets or sets the rights statement (dc:rights), the x-default item. Setting <see langword="null"/> removes it.</summary>
    public string? Rights
    {
        get => GetText(Dc("rights"));
        set => SetLanguageText(Dc("rights"), value);
    }

    /// <summary>Gets or sets the creators (dc:creator), in order. Setting an empty list removes them.</summary>
    public IReadOnlyList<string> Creators
    {
        get => Texts(Dc("creator"));
        set => SetList(Dc("creator"), XmpArrayKind.Sequence, value);
    }

    /// <summary>Gets or sets the keywords (dc:subject). Setting an empty list removes them.</summary>
    public IReadOnlyList<string> Keywords
    {
        get => Texts(Dc("subject"));
        set => SetList(Dc("subject"), XmpArrayKind.Bag, value);
    }

    /// <summary>Gets or sets when the content was created (xmp:CreateDate). Setting <see langword="null"/> removes it.</summary>
    public DateTimeOffset? CreateDate
    {
        get => TryGetDate(Basic("CreateDate"), out DateTimeOffset date) ? date : null;
        set => SetOrRemove(Basic("CreateDate"), value is DateTimeOffset date ? XmpDates.Format(date) : null);
    }

    /// <summary>Gets or sets when the file was last changed (xmp:ModifyDate). Setting <see langword="null"/> removes it.</summary>
    public DateTimeOffset? ModifyDate
    {
        get => TryGetDate(Basic("ModifyDate"), out DateTimeOffset date) ? date : null;
        set => SetOrRemove(Basic("ModifyDate"), value is DateTimeOffset date ? XmpDates.Format(date) : null);
    }

    /// <summary>Gets or sets the software that made the file (xmp:CreatorTool). Setting <see langword="null"/> removes it.</summary>
    public string? CreatorTool
    {
        get => GetText(Basic("CreatorTool"));
        set => SetOrRemove(Basic("CreatorTool"), value);
    }

    /// <summary>Gets or sets the rating (xmp:Rating): -1 for rejected, 0 for unrated, 1 to 5 stars. Setting <see langword="null"/> removes it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not -1 to 5.</exception>
    public int? Rating
    {
        get => TryGetRational(Basic("Rating"), out double rating) && rating is >= -1 and <= 5 ? (int)Math.Round(rating) : null;
        set => SetOrRemove(Basic("Rating"), value is int rating
            ? rating is >= -1 and <= 5 ? rating.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(value), value, "The rating must be -1 to 5.")
            : null);
    }

    /// <summary>Gets or sets the color label (xmp:Label), such as "Red". Setting <see langword="null"/> removes it.</summary>
    public string? Label
    {
        get => GetText(Basic("Label"));
        set => SetOrRemove(Basic("Label"), value);
    }

    /// <summary>Gets or sets the headline (photoshop:Headline). Setting <see langword="null"/> removes it.</summary>
    public string? Headline
    {
        get => GetText(Ps("Headline"));
        set => SetOrRemove(Ps("Headline"), value);
    }

    /// <summary>Gets or sets the city shown (photoshop:City). Setting <see langword="null"/> removes it.</summary>
    public string? City
    {
        get => GetText(Ps("City"));
        set => SetOrRemove(Ps("City"), value);
    }

    /// <summary>Gets or sets the country shown (photoshop:Country). Setting <see langword="null"/> removes it.</summary>
    public string? Country
    {
        get => GetText(Ps("Country"));
        set => SetOrRemove(Ps("Country"), value);
    }

    /// <summary>Gets or sets the provider credited (photoshop:Credit). Setting <see langword="null"/> removes it.</summary>
    public string? Credit
    {
        get => GetText(Ps("Credit"));
        set => SetOrRemove(Ps("Credit"), value);
    }

    /// <summary>Gets or sets the original owner (photoshop:Source). Setting <see langword="null"/> removes it.</summary>
    public string? Source
    {
        get => GetText(Ps("Source"));
        set => SetOrRemove(Ps("Source"), value);
    }

    /// <summary>
    /// Gets or sets the orientation (tiff:Orientation), the XMP copy of EXIF's; <see langword="null"/> when absent or not 1 to 8.
    /// Setting <see langword="null"/> removes it.
    /// </summary>
    public ImageOrientation? Orientation
    {
        get => TryGetInt(new XmpName(XmpNamespaces.Tiff, "Orientation"), out long value) && value is >= 1 and <= 8 ? (ImageOrientation)value : null;
        set => SetOrRemove(new XmpName(XmpNamespaces.Tiff, "Orientation"), value is ImageOrientation o && o is >= ImageOrientation.Normal and <= ImageOrientation.Rotate270
            ? ((int)o).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value is null ? null : throw new ArgumentOutOfRangeException(nameof(value), value, "The orientation must be one of the EXIF values 1 to 8."));
    }

    /// <summary>Gets whether properties changed since the packet was read, so <see cref="ToBytes"/> writes it again.</summary>
    internal bool IsEdited => _edited;

    internal Dictionary<string, string> Prefixes { get; } = [];

    internal string? About { get; set; }

    internal string? Toolkit { get; set; }

    /// <summary>
    /// Reads an XMP packet (RDF/XML, with or without its xpacket wrapper). Never throws: a packet that is not well-formed or is
    /// over <see cref="SafetyLimits.MaxMetadataBytes"/> is marked damaged and keeps its bytes. DTDs are refused and nothing is
    /// resolved, so external entities and entity expansion cannot run.
    /// </summary>
    /// <param name="packet">The packet's bytes, UTF-8 or UTF-16.</param>
    /// <param name="limits">The limits whose <see cref="SafetyLimits.MaxMetadataBytes"/> caps the packet. Defaults to <see cref="SafetyLimits.Default"/>.</param>
    /// <returns>The parsed packet; <see cref="IsDamaged"/> says whether it was readable.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-parse"/>
    /// </example>
    public static XmpData Parse(ReadOnlySpan<byte> packet, SafetyLimits? limits = null)
    {
        byte[] original = packet.ToArray();
        XmpData data = new() { _original = original, _edited = false };
        if (packet.Length > (limits ?? SafetyLimits.Default).MaxMetadataBytes)
        {
            data.Damaged("The XMP packet is over the metadata limit.");
            return data;
        }

        if (XmpParser.Read(original, data) is string problem)
        {
            data.Fields.Clear();
            data.Damaged(problem);
        }

        return data;
    }

    /// <summary>Returns the packet: the original bytes when unedited, otherwise written as canonical RDF/XML with padding.</summary>
    /// <returns>The bytes.</returns>
    public ReadOnlyMemory<byte> ToBytes() => _edited ? _written ??= XmpWriter.Write(this) : _original;

    /// <summary>Returns an independent copy.</summary>
    /// <returns>The copy.</returns>
    public XmpData Copy()
    {
        XmpData copy = new() { _original = _original, _edited = _edited, IsDamaged = IsDamaged, About = About, Toolkit = Toolkit };
        CopyFieldsTo(copy);
        copy._edited = _edited;
        foreach ((string ns, string prefix) in Prefixes)
        {
            copy.Prefixes[ns] = prefix;
        }

        copy._warnings.AddRange(_warnings);
        return copy;
    }

    void IXmpOwner.Touch()
    {
        _edited = true;
        _written = null;
    }

    private static XmpName Dc(string name) => new(XmpNamespaces.DublinCore, name);

    private static XmpName Basic(string name) => new(XmpNamespaces.XmpBasic, name);

    private static XmpName Ps(string name) => new(XmpNamespaces.Photoshop, name);

    private IReadOnlyList<string> Texts(XmpName name) => Find(name)?.Value switch
    {
        XmpArray array => array.Texts,
        string s => [s],
        _ => [],
    };

    private void SetLanguageText(XmpName name, string? text)
    {
        if (text is null)
        {
            Remove(name);
        }
        else if (Find(name)?.Value is XmpArray { Kind: XmpArrayKind.Alternative } alt)
        {
            alt.SetLanguage("x-default", text);
        }
        else
        {
            XmpArray created = new(XmpArrayKind.Alternative);
            created.SetLanguage("x-default", text);
            Set(name, created);
        }
    }

    private void SetList(XmpName name, XmpArrayKind kind, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            Remove(name);
        }
        else
        {
            Set(name, XmpArray.Of(kind, items));
        }
    }

    private void SetOrRemove(XmpName name, string? value)
    {
        if (value is null)
        {
            Remove(name);
        }
        else
        {
            Set(name, value);
        }
    }

    private void Damaged(string warning)
    {
        IsDamaged = true;
        _warnings.Add(warning);
    }
}
