using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>Phase 2.1.3 task 2: IPTC-IIM datasets against ExifTool's reading of the corpus, byte-identical write-back, and edits.</summary>
public class IptcTests
{
    // ExifTool's names for the datasets the corpus uses.
    private static readonly Dictionary<string, IptcKey> Names = new(StringComparer.Ordinal)
    {
        ["CodedCharacterSet"] = IptcKey.CodedCharacterSet,
        ["EnvelopeRecordVersion"] = new(1, 0),
        ["ApplicationRecordVersion"] = new(2, 0),
        ["ObjectName"] = IptcKey.ObjectName,
        ["Urgency"] = IptcKey.Urgency,
        ["Category"] = IptcKey.Category,
        ["SupplementalCategories"] = IptcKey.SupplementalCategory,
        ["Keywords"] = IptcKey.Keywords,
        ["SpecialInstructions"] = IptcKey.SpecialInstructions,
        ["DateCreated"] = IptcKey.DateCreated,
        ["TimeCreated"] = IptcKey.TimeCreated,
        ["By-line"] = IptcKey.Byline,
        ["By-lineTitle"] = IptcKey.BylineTitle,
        ["City"] = IptcKey.City,
        ["Sub-location"] = IptcKey.SubLocation,
        ["Province-State"] = IptcKey.ProvinceState,
        ["Country-PrimaryLocationCode"] = IptcKey.CountryCode,
        ["Country-PrimaryLocationName"] = IptcKey.Country,
        ["OriginalTransmissionReference"] = IptcKey.OriginalTransmissionReference,
        ["Headline"] = IptcKey.Headline,
        ["Credit"] = IptcKey.Credit,
        ["Source"] = IptcKey.Source,
        ["CopyrightNotice"] = IptcKey.CopyrightNotice,
        ["Caption-Abstract"] = IptcKey.Caption,
        ["Writer-Editor"] = IptcKey.CaptionWriter,
    };

    public static TheoryData<string> JpegsWithIptc() =>
        [.. MetadataCorpus.Files().Where(f => f.EndsWith(".jpg", StringComparison.Ordinal) && MetadataCorpus.Dump(f).EnumerateObject().Any(p => p.Name.StartsWith("IPTC:", StringComparison.Ordinal)))];

    [Theory]
    [MemberData(nameof(JpegsWithIptc))]
    public void EveryDatasetMatchesExifTool(string file)
    {
        byte[] iim = IimFromJpeg(MetadataCorpus.Bytes(file));
        IptcData iptc = IptcData.Parse(iim);
        Assert.False(iptc.IsDamaged);
        Assert.True(iim.AsSpan().SequenceEqual(iptc.ToBytes().Span));

        JsonProperty[] expected = [.. MetadataCorpus.Dump(file).EnumerateObject().Where(p => p.Name.StartsWith("IPTC:", StringComparison.Ordinal))];
        int values = 0;
        foreach (JsonProperty p in expected)
        {
            IptcKey key = Names[p.Name["IPTC:".Length..]];
            string[] want = p.Value.ValueKind == JsonValueKind.Array ? [.. p.Value.EnumerateArray().Select(v => v.ToString())] : [p.Value.ToString()];
            values += want.Length;
            if (key.Record == 1 || key.Dataset == 0)
            {
                continue;
            }

            string[] got = [.. iptc.GetAll(key)];
            if (key == IptcKey.DateCreated || key == IptcKey.TimeCreated)
            {
                // ExifTool shows 20261011 as 2026:10:11 and 093015+0200 as 09:30:15+02:00.
                (want, got) = ([.. want.Select(Digits)], [.. got.Select(Digits)]);
            }

            Assert.Equal(want, got);
        }

        Assert.Equal(values, iptc.Entries.Count);
    }

    [Fact]
    public void TypedGettersReadTheCorpus()
    {
        IptcData iptc = IptcData.Parse(IimFromJpeg(MetadataCorpus.Bytes("synthetic/iptc-utf8.jpg")));
        Assert.True(iptc.IsUtf8);
        Assert.Equal(["duck", "bird", "vann"], iptc.Keywords);
        Assert.Equal(["Adrian Pingstone", "Egami"], iptc.Bylines);
        Assert.Equal("A mandarin duck, æøå — 水鳥", iptc.Caption);
        Assert.Equal("Duck on water", iptc.Headline);
        Assert.Equal("Public domain", iptc.Copyright);
        Assert.Equal("Egami", iptc.Credit);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 9, 30, 15, TimeSpan.FromHours(2)), iptc.DateCreated);

        IptcData latin = IptcData.Parse(IimFromJpeg(MetadataCorpus.Bytes("synthetic/iptc-latin1.jpg")));
        Assert.False(latin.IsUtf8);
        Assert.Equal("Tromsø", latin.Get(IptcKey.City));
        Assert.Equal(1400, IptcData.Parse(IimFromJpeg(MetadataCorpus.Bytes("synthetic/iptc-large.jpg"))).Keywords.Count);
    }

    [Fact]
    public void EditsRebuildInRecordOrder()
    {
        IptcData iptc = new();
        iptc.Set(IptcKey.Headline, "Harbour");
        iptc.Add(IptcKey.Keywords, "one");
        iptc.Add(IptcKey.Keywords, "two");
        iptc.Set(IptcKey.ObjectName, "slug");
        IptcData read = IptcData.Parse(iptc.ToBytes().Span);
        Assert.False(read.IsDamaged);
        Assert.False(read.IsUtf8);
        Assert.Equal([new IptcKey(2, 0), IptcKey.ObjectName, IptcKey.Keywords, IptcKey.Keywords, IptcKey.Headline], read.Entries.Select(e => e.Key));
        Assert.Equal(["one", "two"], read.Keywords);

        // Set replaces every value; Remove drops them all.
        iptc.Set(IptcKey.Keywords, "only");
        Assert.Equal(["only"], IptcData.Parse(iptc.ToBytes().Span).Keywords);
        Assert.True(iptc.Remove(IptcKey.Keywords));
        Assert.False(iptc.Remove(IptcKey.Keywords));
        Assert.Empty(IptcData.Parse(iptc.ToBytes().Span).Keywords);
    }

    [Fact]
    public void TextThatIsNotAsciiMakesTheBlockUtf8()
    {
        // A Latin-1 block (no 1:90) keeps reading the same after a UTF-8 value joins it.
        IptcData iptc = IptcData.Parse(IimFromJpeg(MetadataCorpus.Bytes("synthetic/iptc-latin1.jpg")));
        iptc.Add(IptcKey.Keywords, "水");
        IptcData read = IptcData.Parse(iptc.ToBytes().Span);
        Assert.True(read.IsUtf8);
        Assert.Equal("Tromsø", read.Get(IptcKey.City));
        Assert.Equal("Fjord, æøå ÆØÅ", read.Caption);
        Assert.Equal(["sjø", "水"], read.Keywords);
        Assert.Equal([0x1B, 0x25, 0x47], read.Entries.First(e => e.Key == IptcKey.CodedCharacterSet).RawValue.ToArray());
        Assert.Equal(new IptcKey(1, 0), read.Entries[0].Key);
    }

    [Fact]
    public void SetEnforcesTheSpecificationsCaps()
    {
        IptcData iptc = new();
        Assert.Throws<ArgumentException>(() => iptc.Add(IptcKey.Keywords, new string('k', 65)));
        Assert.Throws<ArgumentException>(() => iptc.Add(IptcKey.Keywords, new string('ø', 33)));   // 66 bytes in UTF-8
        iptc.Add(IptcKey.Keywords, new string('k', 64));
        Assert.Throws<ArgumentException>(() => iptc.Set(IptcKey.Caption, new string('c', 2001)));
        Assert.Throws<ArgumentException>(() => iptc.Add(IptcKey.Headline, "two headlines"));
        Assert.Throws<ArgumentException>(() => iptc.Set(IptcKey.CodedCharacterSet, "x"));

        // Reading never enforces them: a long value is kept, with a warning.
        byte[] longKeyword = Dataset(2, 25, Encoding.ASCII.GetBytes(new string('k', 80)));
        IptcData read = IptcData.Parse(longKeyword);
        Assert.Equal(80, read.Keywords[0].Length);
        Assert.False(read.IsDamaged);
        Assert.Single(read.Warnings);
    }

    [Fact]
    public void ExtendedDatasetsReadAndWrite()
    {
        // A dataset over 32,767 bytes (an unknown record 2 dataset, so no cap) uses the extended length form.
        IptcData iptc = new();
        iptc.Set(new IptcKey(2, 202), new string('p', 40_000));
        byte[] bytes = iptc.ToBytes().ToArray();
        Assert.Equal(0x80, bytes[7 + 3]);   // after 2:0 (7 bytes), the second dataset's length starts with the extended flag
        Assert.Equal(40_000, IptcData.Parse(bytes).Get(new IptcKey(2, 202))!.Length);
    }

    [Fact]
    public void DamagedBlocksNeverThrow()
    {
        byte[] good = Dataset(2, 5, "name"u8.ToArray());
        Assert.False(IptcData.Parse([.. good, 0, 0, 0]).IsDamaged);                    // padding
        Assert.True(IptcData.Parse([.. good, 0x1C, 2]).IsDamaged);                   // cut short
        Assert.True(IptcData.Parse([0x1C, 2, 120, 0x10, 0, 1, 2]).IsDamaged);        // runs past the end
        Assert.True(IptcData.Parse([.. good, 0x42]).IsDamaged);                      // not a dataset
        Assert.True(IptcData.Parse([0x1C, 2, 120, 0x80, 0x09, 1]).IsDamaged);        // extended length too long
        IptcData capped = IptcData.Parse([.. good, .. good], SafetyLimits.Default with { MaxMetadataBytes = 5 });
        Assert.True(capped.IsDamaged);
        Assert.Single(capped.Entries);

        IptcData damaged = IptcData.Parse([.. good, 0x42]);
        Assert.Equal("name", damaged.Get(IptcKey.ObjectName));
        Assert.Equal([.. good, 0x42], damaged.ToBytes().ToArray());
    }

    [Fact]
    public void TypedSettersWriteTheDatasets()
    {
        IptcData iptc = new()
        {
            Keywords = ["a", "b"],
            Bylines = ["Photographer"],
            Caption = "Caption",
            Headline = "Headline",
            Copyright = "(c) 2026",
            Credit = "Egami",
            DateCreated = new DateTimeOffset(2026, 10, 11, 9, 30, 15, TimeSpan.FromHours(-5.5)),
        };
        IptcData read = IptcData.Parse(iptc.ToBytes().Span);
        Assert.Equal(["a", "b"], read.Keywords);
        Assert.Equal(["Photographer"], read.Bylines);
        Assert.Equal(("Caption", "Headline", "(c) 2026", "Egami"), (read.Caption, read.Headline, read.Copyright, read.Credit));
        Assert.Equal("093015-0530", read.Get(IptcKey.TimeCreated));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 9, 30, 15, TimeSpan.FromHours(-5.5)), read.DateCreated);

        read.Keywords = [];
        read.Caption = null;
        read.DateCreated = null;
        Assert.Empty(read.Keywords);
        Assert.Null(read.Caption);
        Assert.Null(read.Get(IptcKey.TimeCreated));

        // A keyword over the cap leaves the old ones in place.
        read.Keywords = ["kept"];
        Assert.Throws<ArgumentException>(() => read.Keywords = ["ok", new string('k', 65)]);
        Assert.Equal(["kept"], read.Keywords);
    }

    [Fact]
    public void DatesAndCopies()
    {
        IptcData iptc = new();
        Assert.Null(iptc.DateCreated);
        iptc.Set(IptcKey.DateCreated, "20240229");
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero), iptc.DateCreated);
        iptc.Set(IptcKey.TimeCreated, "235959-0530");
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 23, 59, 59, new TimeSpan(-5, -30, 0)), iptc.DateCreated);
        iptc.Set(IptcKey.DateCreated, "20230229");
        Assert.Null(iptc.DateCreated);

        IptcData copy = iptc.Copy();
        copy.Set(IptcKey.City, "Oslo");
        Assert.Null(iptc.Get(IptcKey.City));
        Assert.Equal("Oslo", copy.Get(IptcKey.City));
    }

    /// <summary>The IIM datasets of a JPEG's Photoshop resource 0x0404, with its APP13 segments joined.</summary>
    internal static byte[] IimFromJpeg(byte[] jpeg)
    {
        List<byte> block = [];
        int i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(i + 2));
            ReadOnlySpan<byte> payload = jpeg.AsSpan(i + 4, length - 2);
            if (jpeg[i + 1] == 0xED && payload.StartsWith("Photoshop 3.0\0"u8))
            {
                block.AddRange(payload[14..]);
            }

            i += 2 + length;
        }

        ReadOnlySpan<byte> resources = [.. block];
        for (int at = 0; at + 12 <= resources.Length;)
        {
            int id = BinaryPrimitives.ReadUInt16BigEndian(resources[(at + 4)..]);
            int nameLength = resources[at + 6];
            int skip = 6 + ((nameLength + 2) & ~1);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(resources[(at + skip)..]);
            if (id == 0x0404)
            {
                return resources.Slice(at + skip + 4, size).ToArray();
            }

            at += skip + 4 + ((size + 1) & ~1);
        }

        return [];
    }

    private static byte[] Dataset(byte record, byte dataset, byte[] value) => [0x1C, record, dataset, (byte)(value.Length >> 8), (byte)value.Length, .. value];

    private static string Digits(string s) => string.Concat(s.Where(c => char.IsAsciiDigit(c) || c is '+' or '-'));

}
