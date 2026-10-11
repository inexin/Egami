using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

using Egami.Formats.Jpeg;
using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>
/// Phase 2.1.3 task 4: the XMP model. Every property ExifTool reads from the corpus packets matches (language alternatives,
/// structures in each RDF form, arrays of structures, resources), edits keep what they do not touch, and hostile XML is refused.
/// </summary>
public class XmpTests
{
    // ExifTool's group names for the corpus's namespaces.
    private static readonly Dictionary<string, string> Groups = new(StringComparer.Ordinal)
    {
        ["dc"] = XmpNamespaces.DublinCore,
        ["xmp"] = XmpNamespaces.XmpBasic,
        ["xmpRights"] = XmpNamespaces.XmpRights,
        ["photoshop"] = XmpNamespaces.Photoshop,
        ["tiff"] = XmpNamespaces.Tiff,
        ["exif"] = XmpNamespaces.Exif,
        ["iptcCore"] = XmpNamespaces.IptcCore,
        ["iptcExt"] = XmpNamespaces.IptcExtension,
        ["xmpNote"] = "http://ns.adobe.com/xmp/note/",
        ["egami"] = "http://egami.example/ns/1.0/",
    };

    // ExifTool tag names that are not the property's local name.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["ExifImageWidth"] = "PixelXDimension",
        ["ExifImageHeight"] = "PixelYDimension",
    };

    public static TheoryData<string> FilesWithXmp() =>
        [.. MetadataCorpus.Files().Where(f => !f.Contains("/im-", StringComparison.Ordinal) && MetadataCorpus.Dump(f).EnumerateObject().Any(p => p.Name.StartsWith("XMP-", StringComparison.Ordinal) && !p.Name.StartsWith("XMP-x:", StringComparison.Ordinal)))];

    [Theory]
    [MemberData(nameof(FilesWithXmp))]
    public void EveryPropertyMatchesExifTool(string file)
    {
        XmpData xmp = Packet(file);
        Assert.False(xmp.IsDamaged, string.Join(" ", xmp.Warnings));
        HashSet<XmpName> seen = [];
        foreach (JsonProperty p in MetadataCorpus.Dump(file).EnumerateObject())
        {
            int colon = p.Name.IndexOf(':', StringComparison.Ordinal);
            if (!p.Name.StartsWith("XMP-", StringComparison.Ordinal) || p.Name.StartsWith("XMP-x:", StringComparison.Ordinal))
            {
                continue;
            }

            string ns = Groups[p.Name[4..colon]];
            string tag = p.Name[(colon + 1)..];
            string? language = null;
            int dash = tag.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                (tag, language) = (tag[..dash], tag[(dash + 1)..]);
            }

            XmpName name = Find(xmp, ns, Aliases.GetValueOrDefault(tag, tag));
            seen.Add(name);
            Assert.True(xmp.TryGet(name, out object? value), p.Name);
            if (language is not null)
            {
                Assert.Equal(p.Value.ToString(), ((XmpArray)value).GetLanguage(language));
            }
            else
            {
                Match(p.Value, value, p.Name);
            }
        }

        Assert.Equal(xmp.Names.Select(n => n.ToString()).Order(StringComparer.Ordinal), seen.Select(n => n.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TypedGettersReadTheCorpus()
    {
        XmpData elements = Packet("synthetic/xmp-elements.jpg");
        Assert.Equal("Mandarin duck", elements.Title);
        Assert.Equal("Mandarinand", ((XmpArray)Get(elements, XmpNamespaces.DublinCore, "title")).GetLanguage("nb-no"));
        Assert.Equal("A duck on water, æøå — 水", elements.Description);
        Assert.Equal("Public domain", elements.Rights);
        Assert.Equal(["Adrian Pingstone", "Egami"], elements.Creators);
        Assert.Equal(["duck", "bird", "water"], elements.Keywords);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 9, 30, 15, TimeSpan.FromHours(2)), elements.CreateDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero), elements.ModifyDate);
        Assert.Equal(ImageOrientation.Normal, elements.Orientation);

        XmpData attributes = Packet("synthetic/xmp-attributes.jpg");
        Assert.Equal("Egami corpus", attributes.CreatorTool);
        Assert.Equal(4, attributes.Rating);
        Assert.Equal("Green", attributes.Label);
        Assert.Equal("Bergen", attributes.City);
        Assert.Equal("Norway", attributes.Country);
        Assert.Equal("Egami", attributes.Credit);
        Assert.Equal("Synthetic", attributes.Source);
        Assert.Equal("Attributes form", attributes.Headline);
        Assert.Empty(attributes.Keywords);
        Assert.Null(attributes.Title);

        XmpData structures = Packet("synthetic/xmp-structures.jpg");
        XmpStruct contact = (XmpStruct)Get(structures, XmpNamespaces.IptcCore, "CreatorContactInfo");
        Assert.Equal("Bergen", contact.GetText(new XmpName(XmpNamespaces.IptcCore, "CiAdrCity")));
        XmpArray locations = (XmpArray)Get(structures, XmpNamespaces.IptcExtension, "LocationCreated");
        Assert.Equal(XmpArrayKind.Bag, locations.Kind);
        Assert.Equal("Oslo", ((XmpStruct)locations[1]).GetText(new XmpName(XmpNamespaces.IptcExtension, "City")));
        XmpStruct point = (XmpStruct)Get(structures, "http://egami.example/ns/1.0/", "Point");
        Assert.True(point.TryGetRational(new XmpName("http://egami.example/ns/1.0/", "X"), out double x));
        Assert.Equal(0.25, x);
    }

    [Theory]
    [MemberData(nameof(FilesWithXmp))]
    public void UneditedPacketsWriteBackAndEditedOnesReadBack(string file)
    {
        byte[] original = PacketBytes(file);
        XmpData xmp = XmpData.Parse(original);
        Assert.Equal(original, xmp.ToBytes().ToArray());

        // Edit one property; the rest, unknown namespaces and qualifiers included, reads back the same.
        xmp.Label = "Edited";
        byte[] written = xmp.ToBytes().ToArray();
        XmlDocument document = new() { XmlResolver = null };
        document.LoadXml(Encoding.UTF8.GetString(written).TrimStart('\uFEFF'));
        XmpData again = XmpData.Parse(written);
        Assert.False(again.IsDamaged);
        Assert.Equal("Edited", again.Label);
        XmpData expected = XmpData.Parse(original);
        expected.Label = "Edited";
        Assert.Equal(Describe(expected), Describe(again));

        // At least 2 KB of padding before the trailer, so other tools can edit in place.
        string text = Encoding.UTF8.GetString(written);
        Assert.EndsWith("<?xpacket end=\"w\"?>", text, StringComparison.Ordinal);
        int closing = text.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal);
        Assert.True(text.Length - closing - "</x:xmpmeta><?xpacket end=\"w\"?>".Length >= 2000);
    }

    [Fact]
    public void PrefixesAreTheFilesOwn()
    {
        XmpData xmp = Packet("synthetic/xmp-structures.jpg");
        xmp.Title = "New";
        string text = Encoding.UTF8.GetString(xmp.ToBytes().Span);
        Assert.Contains("xmlns:egami=\"http://egami.example/ns/1.0/\"", text, StringComparison.Ordinal);
        Assert.Contains("<egami:Note>Unknown namespace, kept through edits</egami:Note>", text, StringComparison.Ordinal);
        Assert.Contains("rdf:resource=\"https://creativecommons.org/publicdomain/zero/1.0/\"", text, StringComparison.Ordinal);
        Assert.Contains("xmlns:dc=\"http://purl.org/dc/elements/1.1/\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SettersBuildAPacketFromNothing()
    {
        XmpData xmp = new()
        {
            Title = "Harbour",
            Description = "Boats & <nets>",
            Creators = ["A", "B"],
            Keywords = ["k1"],
            Rating = 5,
            CreateDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-7)),
            Orientation = ImageOrientation.Rotate90,
        };
        XmpArray title = (XmpArray)Get(xmp, XmpNamespaces.DublinCore, "title");
        title.SetLanguage("de", "Hafen");
        XmpStruct contact = new();
        contact.Set(new XmpName(XmpNamespaces.IptcCore, "CiAdrCity"), "Bergen");
        xmp.Set(new XmpName(XmpNamespaces.IptcCore, "CreatorContactInfo"), contact);

        XmpData read = XmpData.Parse(xmp.ToBytes().Span);
        Assert.Equal("Harbour", read.Title);
        Assert.Equal("Hafen", ((XmpArray)Get(read, XmpNamespaces.DublinCore, "title")).GetLanguage("de"));
        Assert.Equal("Boats & <nets>", read.Description);
        Assert.Equal(["A", "B"], read.Creators);
        Assert.Equal(["k1"], read.Keywords);
        Assert.Equal(5, read.Rating);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-7)), read.CreateDate);
        Assert.Equal(ImageOrientation.Rotate90, read.Orientation);
        Assert.Equal("Bergen", ((XmpStruct)Get(read, XmpNamespaces.IptcCore, "CreatorContactInfo")).GetText(new XmpName(XmpNamespaces.IptcCore, "CiAdrCity")));

        // Null removes; an edit inside a nested container marks the packet edited.
        read.Title = null;
        Assert.Null(XmpData.Parse(read.ToBytes().Span).Title);
        XmpData nested = XmpData.Parse(xmp.ToBytes().Span);
        ((XmpStruct)Get(nested, XmpNamespaces.IptcCore, "CreatorContactInfo")).Set(new XmpName(XmpNamespaces.IptcCore, "CiAdrCity"), "Oslo");
        Assert.Contains("Oslo", Encoding.UTF8.GetString(nested.ToBytes().Span), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => nested.Rating = 6);
        Assert.Throws<ArgumentException>(() => nested.Set(new XmpName(XmpNamespaces.Rdf, "li"), "x"));
    }

    [Fact]
    public void QualifiersSurviveAnEdit()
    {
        const string Packet = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:q="http://q.example/">
             <dc:source rdf:parseType="Resource"><rdf:value>Scan</rdf:value><q:by>Lab</q:by></dc:source>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            """;
        XmpData xmp = XmpData.Parse(Encoding.UTF8.GetBytes(Packet));
        Assert.Equal("Scan", xmp.GetText(new XmpName(XmpNamespaces.DublinCore, "source")));
        xmp.Label = "x";
        string text = Encoding.UTF8.GetString(xmp.ToBytes().Span);
        Assert.Contains("<rdf:value>Scan</rdf:value>", text, StringComparison.Ordinal);
        Assert.Contains("<q:by>Lab</q:by>", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">&e;</x:xmpmeta>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]><l>&c;</l>")]
    [InlineData("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF")]
    [InlineData("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>")]
    [InlineData("not xml at all")]
    public void HostileOrBrokenPacketsAreDamage(string packet)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(packet);
        XmpData xmp = XmpData.Parse(bytes);
        Assert.True(xmp.IsDamaged);
        Assert.Empty(xmp.Names);
        Assert.Single(xmp.Warnings);
        Assert.Equal(bytes, xmp.ToBytes().ToArray());
    }

    [Fact]
    public void DeepNestingIsDamage()
    {
        StringBuilder deep = new("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:a=\"http://a/\">");
        for (int i = 0; i < 500; i++)
        {
            deep.Append("<a:p rdf:parseType=\"Resource\">");
        }

        for (int i = 0; i < 500; i++)
        {
            deep.Append("</a:p>");
        }

        deep.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");
        Assert.True(XmpData.Parse(Encoding.UTF8.GetBytes(deep.ToString())).IsDamaged);
        Assert.True(XmpData.Parse(new byte[100], SafetyLimits.Default with { MaxMetadataBytes = 50 }).IsDamaged);
    }

    [Theory]
    [InlineData("2026", 2026, 1, 1, 0, 0, 0, 0)]
    [InlineData("2026-10", 2026, 10, 1, 0, 0, 0, 0)]
    [InlineData("2026-10-11", 2026, 10, 11, 0, 0, 0, 0)]
    [InlineData("2026-10-11T09:30+02:00", 2026, 10, 11, 9, 30, 0, 120)]
    [InlineData("2026-10-11T09:30:15Z", 2026, 10, 11, 9, 30, 15, 0)]
    [InlineData("2026-10-11T09:30:15.5-05:30", 2026, 10, 11, 9, 30, 15, -330)]
    public void DatesReadEveryXmpForm(string text, int y, int mo, int d, int h, int mi, int s, int offsetMinutes)
    {
        XmpData xmp = new();
        xmp.Set(new XmpName(XmpNamespaces.XmpBasic, "CreateDate"), text);
        DateTimeOffset expected = new(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes));
        Assert.Equal(text.EndsWith(".5-05:30", StringComparison.Ordinal) ? expected.AddMilliseconds(500) : expected, xmp.CreateDate);
    }

    [Theory]
    [InlineData("26-10-11")]
    [InlineData("2026-13")]
    [InlineData("2026-02-30")]
    [InlineData("2026-10-11T25:00")]
    [InlineData("2026-10-11T09:30+2")]
    public void BadDatesReadAsNull(string text)
    {
        XmpData xmp = new();
        xmp.Set(new XmpName(XmpNamespaces.XmpBasic, "CreateDate"), text);
        Assert.Null(xmp.CreateDate);
    }

    /// <summary>The corpus file's main XMP packet; for extended XMP, its extended part's properties are added.</summary>
    internal static XmpData Packet(string file)
    {
        XmpData xmp = XmpData.Parse(PacketBytes(file));
        using Raster raster = Raster.Load(MetadataCorpus.Bytes(file));
        if (raster.Metadata.FormatDetails is JpegDetails { ExtendedXmp.IsEmpty: false } jpeg)
        {
            XmpData extended = XmpData.Parse(jpeg.ExtendedXmp.Span);
            foreach (XmpName name in extended.Names)
            {
                extended.TryGet(name, out object? value);
                switch (value)
                {
                    case string s: xmp.Set(name, s); break;
                    case XmpArray a: xmp.Set(name, a); break;
                    case XmpStruct st: xmp.Set(name, st); break;
                }
            }
        }

        return xmp;
    }

    /// <summary>The corpus file's main XMP packet bytes: from the metadata for JPEG and GIF, from the iTXt chunk for PNG.</summary>
    internal static byte[] PacketBytes(string file)
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes(file));
        if (!raster.Metadata.Xmp.IsEmpty)
        {
            return raster.Metadata.Xmp.ToArray();
        }

        return Encoding.UTF8.GetBytes(raster.Metadata.Text.First(t => t.Key == "XML:com.adobe.xmp").Value);
    }

    private static object Get(XmpStruct xmp, string ns, string name) => xmp.TryGet(new XmpName(ns, name), out object? value) ? value : throw new KeyNotFoundException(name);

    // ExifTool capitalizes the first letter of local names.
    private static XmpName Find(XmpStruct xmp, string ns, string tag) =>
        xmp.Names.FirstOrDefault(n => n.Namespace == ns && string.Equals(n.Name, tag, StringComparison.OrdinalIgnoreCase) && n.Name[1..] == tag[1..]) is { Name: not null } found
            ? found
            : throw new KeyNotFoundException($"{ns} {tag}");

    private static void Match(JsonElement expected, object ours, string where)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Array:
                {
                    XmpArray array = Assert.IsType<XmpArray>(ours);
                    JsonElement[] items = [.. expected.EnumerateArray()];
                    Assert.Equal(items.Length, array.Count);
                    for (int i = 0; i < items.Length; i++)
                    {
                        Match(items[i], array[i], $"{where}[{i}]");
                    }

                    break;
                }

            case JsonValueKind.Object:
                {
                    XmpStruct structure = ours is XmpArray { Count: 1 } single ? Assert.IsType<XmpStruct>(single[0]) : Assert.IsType<XmpStruct>(ours);
                    foreach (JsonProperty field in expected.EnumerateObject())
                    {
                        XmpName name = structure.Names.First(n => string.Equals(n.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                        structure.TryGet(name, out object? value);
                        Match(field.Value, value!, $"{where}.{field.Name}");
                    }

                    Assert.Equal(expected.EnumerateObject().Count(), structure.Count);
                    break;
                }

            default:
                {
                    string text = ours switch
                    {
                        XmpArray { Kind: XmpArrayKind.Alternative } alt => alt.GetLanguage("x-default")!,
                        XmpArray { Count: 1 } one => (string)one[0],
                        _ => Assert.IsType<string>(ours),
                    };
                    if (expected.ValueKind == JsonValueKind.Number)
                    {
                        Assert.Equal(expected.GetDouble(), double.Parse(text, CultureInfo.InvariantCulture), 9);
                    }
                    else
                    {
                        // ExifTool shows XMP dates with colons, 2026:10:11 09:30:15+02:00.
                        string want = expected.ToString();
                        Assert.True(want == text || Digits(want) == Digits(text), $"{where}: {want} vs {text}");
                    }

                    break;
                }
        }
    }

    private static string Digits(string s) => string.Concat(s.Where(char.IsAsciiDigit));

    private static string Describe(object value) => value switch
    {
        XmpStruct s => "{" + string.Join(",", s.Names.Select(n => $"{n}={(s.TryGet(n, out object? v) ? Describe(v) : "")}")) + "}",
        XmpArray a => a.Kind + "[" + string.Join(",", Enumerable.Range(0, a.Count).Select(i => $"{a.LanguageAt(i)}:{Describe(a[i])}")) + "]",
        _ => value.ToString()!,
    };
}
