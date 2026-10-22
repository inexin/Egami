using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Heif;

namespace Egami.Tests.Formats.Heif;

/// <summary>The HEIF container reader on files built box by box: each box form, each iloc and ipma form, and broken files.</summary>
public class HeifFileTests
{
    [Fact]
    public void ReadsTheItemModel()
    {
        byte[] file = File(
            Box("ftyp", A("avif"), U32(0), A("mif1"), A("miaf")),
            Meta(
                Hdlr("pict"),
                Full("pitm", 0, 0, U16(1)),
                Iinf(Infe(1, "av01", "Color"), Infe(2, "av01", "Alpha", hidden: true), Infe(3, "mime", "XMP", A("application/rdf+xml\0").ToArray())),
                Iloc(0, 4, 4, 0, 0, (1, 0, [(100, 10)]), (2, 0, [(110, 5)]), (3, 0, [(115, 3)])),
                Full("iref", 0, 0, Box("auxl", U16(2), U16(1), U16(1)), Box("cdsc", U16(3), U16(1), U16(1))),
                Box("iprp", Box("ipco", Box("ispe", U32(0), U32(64), U32(48)), Box("abcd", U32(7))),
                    Full("ipma", 0, 0, U32(2), U16(1), [2, 0x81, 0x02], U16(2), [1, 0x01]))),
            Box("mdat", new byte[200]));
        HeifFile heif = HeifFile.Parse(file, "AVIF");

        Assert.Equal(IsoTypes.Of("avif"), heif.MajorBrand);
        Assert.True(heif.HasBrand(IsoTypes.Of("miaf")));
        Assert.Equal(IsoTypes.Pict, heif.Handler);
        Assert.Equal(1u, heif.PrimaryItemId);
        Assert.Equal([1u, 2u, 3u], heif.Items.Select(i => i.Id));
        Assert.True(heif.Item(2)!.Hidden);
        Assert.Equal("application/rdf+xml", heif.Item(3)!.ContentType);
        Assert.Equal([1u], heif.Item(2)!.ReferencesOf(IsoTypes.Of("auxl")));
        Assert.Equal([(IsoTypes.Of("ispe"), true), (IsoTypes.Of("abcd"), false)], heif.PropertiesOf(heif.Item(1)!).Select(p => (p.Property.Type, p.Essential)));
        Assert.Equal(10, heif.ItemData(heif.Item(1)!).Length);
        Assert.Empty(heif.Problems);

        heif.MarkUnsupportedEssential(t => t == IsoTypes.Of("ispe"));
        Assert.Equal(0u, heif.Item(1)!.UnsupportedEssential);
        heif.MarkUnsupportedEssential(t => t == IsoTypes.Of("abcd"));
        Assert.Equal(IsoTypes.Of("ispe"), heif.Item(1)!.UnsupportedEssential);
    }

    [Theory]
    [InlineData(1, 4, 4, 0, 0)]
    [InlineData(1, 8, 8, 8, 4)]
    [InlineData(2, 4, 8, 4, 8)]
    [InlineData(0, 8, 4, 0, 0)]
    public void ReadsEveryIlocForm(int version, int offsetSize, int lengthSize, int baseSize, int indexSize)
    {
        byte[] mdat = Box("mdat", [.. Enumerable.Range(0, 64).Select(i => (byte)i)]);
        byte[] Build(long at) => File(
            Box("ftyp", A("mif1"), U32(0)),
            Meta(Hdlr("pict"), Full("pitm", 0, 0, U16(7)), Iinf(Infe(7, "av01", "")),
                Iloc(version, offsetSize, lengthSize, baseSize, indexSize, (7, 0, [(at + 2, 2), (at + 6, 3)]))),
            mdat);

        // The meta box's size does not depend on the offsets, so the data's place is known from a first build.
        byte[] file = Build(Build(0).Length - 64);
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        HeifItem item = heif.Item(7)!;
        Assert.Equal(2, item.Extents.Count);
        Assert.Equal([2, 3, 6, 7, 8], heif.ItemData(item).ToArray());
    }

    [Fact]
    public void ReadsIdatItemsAndLargeAndOpenEndedBoxes()
    {
        byte[] idat = [9, 8, 7, 6, 5];
        byte[] file = File(
            Box("ftyp", A("mif1"), U32(0)),
            LargeBox("meta", [0, 0, 0, 0], Hdlr("pict"), Full("pitm", 0, 0, U16(1)), Iinf(Infe(1, "grid", "")),
                Iloc(1, 4, 4, 0, 0, (1, 1, [(1, 3)])), Box("idat", idat)),
            OpenBox("mdat", new byte[16]));
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.Equal(1, heif.Item(1)!.ConstructionMethod);
        Assert.Equal([8, 7, 6], heif.ItemData(heif.Item(1)!).ToArray());
        Assert.Equal(5, heif.Idat.Length);
        Assert.Equal(3, heif.Boxes.Count);
        Assert.Equal(file.Length, heif.Boxes[^1].EndIndex);
    }

    [Fact]
    public void ReadsEntityGroupsAndLongIds()
    {
        byte[] file = File(
            Box("ftyp", A("mif1"), U32(0)),
            Meta(Hdlr("pict"), Full("pitm", 1, 0, U32(70000)), Iinf(Infe(70000, "av01", "", version: 3), Infe(70001, "av01", "", version: 3)),
                Full("iref", 1, 0, Box("thmb", U32(70001), U16(1), U32(70000))),
                Box("grpl", Full("altr", 0, 0, U32(5), U32(2), U32(70000), U32(70001))),
                Box("iprp", Box("ipco", Box("ispe", U32(0), U32(1), U32(1))), Full("ipma", 1, 1, U32(1), U32(70001), [1], U16(0x8001)))));
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.Equal(70000u, heif.PrimaryItemId);
        Assert.Equal([70000u], heif.Item(70001)!.ReferencesOf(IsoTypes.Of("thmb")));
        HeifEntityGroup group = Assert.Single(heif.Groups);
        Assert.Equal((IsoTypes.Of("altr"), 5u), (group.Type, group.Id));
        Assert.Equal([70000u, 70001u], group.Entities);
        Assert.Equal(new HeifAssociation(1, true), Assert.Single(heif.Item(70001)!.Associations));
    }

    [Fact]
    public void SkipsWhatItCanAndSaysSo()
    {
        byte[] file = File(
            Box("ftyp", A("mif1"), U32(0)),
            Meta(Hdlr("pict"), Full("pitm", 0, 0, U16(1)), Iinf(Infe(1, "av01", ""), Full("infe", 0, 0, U16(2), U16(0), A("old\0"))),
                Iloc(0, 4, 4, 0, 0, (1, 0, [(0, 1)]), (9, 0, [(0, 1)]))),
            Meta(Hdlr("pict")));
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.Equal([1u], heif.Items.Select(i => i.Id));
        Assert.Equal([9u], heif.UnlistedItemIds);
        Assert.Equal(["Heif.OldItemEntry", "Heif.UnknownLocatedItem", "Heif.SecondMeta"], heif.Problems.Select(p => p.Id));
    }

    public static TheoryData<string, string> Broken() => new()
    {
        { "not ftyp first", Hex(Box("mdat", new byte[4])) },
        { "box past its end", Hex(Box("ftyp", A("mif1"), U32(0))[..^2]) },
        { "size smaller than its header", Hex([0, 0, 0, 4, (byte)'f', (byte)'t', (byte)'y', (byte)'p']) },
        { "no hdlr", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Full("pitm", 0, 0, U16(1))))) },
        { "duplicate item", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Hdlr("pict"), Iinf(Infe(1, "av01", ""), Infe(1, "av01", ""))))) },
        { "iloc field size 3", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Hdlr("pict"), Iinf(Infe(1, "av01", "")), Full("iloc", 0, 0, [0x34, 0x00], U16(0))))) },
        { "iloc cut off", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Hdlr("pict"), Iinf(Infe(1, "av01", "")), Full("iloc", 0, 0, [0x44, 0x00], U16(1), U16(1))))) },
        { "property index past ipco", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Hdlr("pict"), Iinf(Infe(1, "av01", "")),
            Box("iprp", Box("ipco"), Full("ipma", 0, 0, U32(1), U16(1), [1, 1]))))) },
        { "two ipma entries for an item", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Hdlr("pict"), Iinf(Infe(1, "av01", "")),
            Box("iprp", Box("ipco"), Full("ipma", 0, 0, U32(2), U16(1), [0], U16(1), [0]))))) },
        { "group larger than its box", Hex(File(Box("ftyp", A("mif1"), U32(0)), Meta(Hdlr("pict"), Box("grpl", Full("altr", 0, 0, U32(1), U32(1000)))))) },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void BrokenFilesFailAsCorrupt(string what, string hex)
    {
        CorruptDataException error = Assert.Throws<CorruptDataException>(() => HeifFile.Parse(Convert.FromHexString(hex), "AVIF"));
        Assert.Equal("AVIF", error.FormatName);
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void ExtentsPastTheFileFailWhenRead()
    {
        byte[] file = File(Box("ftyp", A("mif1"), U32(0)),
            Meta(Hdlr("pict"), Iinf(Infe(1, "av01", ""), Infe(2, "av01", "")), Iloc(1, 4, 4, 0, 0, (1, 0, [(1000, 4)]), (2, 2, [(0, 4)]))));
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.True(Assert.Throws<CorruptDataException>(() => heif.ItemData(heif.Item(1)!)).IsTruncation);
        Assert.Contains("construction method 2", Assert.Throws<CorruptDataException>(() => heif.ItemData(heif.Item(2)!)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SurvivesEveryTruncationAndByteFlip()
    {
        byte[] file = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "avif", "duck-grid-4x1-alpha.avif"));
        int meta = HeaderBefore(file, "meta");
        int end = meta + BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(meta - 8));
        for (int length = 0; length < end; length++)
        {
            Read(file[..length]);
        }

        Random random = new(2902);
        for (int i = 0; i < 2000; i++)
        {
            byte[] mutated = (byte[])file.Clone();
            mutated[random.Next(meta - 8, end)] ^= (byte)(1 << random.Next(8));
            Read(mutated);
        }

        static void Read(byte[] data)
        {
            try
            {
                HeifFile heif = HeifFile.Parse(data, "AVIF");
                foreach (HeifItem item in heif.Items.Where(i => i.Located))
                {
                    heif.ItemData(item);
                }
            }
            catch (CorruptDataException)
            {
            }
        }
    }

    private static int HeaderBefore(byte[] file, string type) => file.AsSpan().IndexOf(Encoding.ASCII.GetBytes(type)) + 4;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static byte[] File(params byte[][] boxes) => [.. boxes.SelectMany(b => b)];

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] U32(int value) => U32((uint)value);

    private static byte[] A(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Box(string type, params byte[][] parts)
    {
        List<byte> content = [];
        foreach (byte[] part in parts)
        {
            content.AddRange(part);
        }

        return [.. U32(8 + content.Count), .. Encoding.ASCII.GetBytes(type), .. content];
    }

    private static byte[] LargeBox(string type, params byte[][] parts)
    {
        byte[] content = [.. parts.SelectMany(p => p)];
        return [0, 0, 0, 1, .. Encoding.ASCII.GetBytes(type), .. U32(0), .. U32(16 + content.Length), .. content];
    }

    private static byte[] OpenBox(string type, byte[] content) => [0, 0, 0, 0, .. Encoding.ASCII.GetBytes(type), .. content];

    private static byte[] Full(string type, int version, int flags, params byte[][] parts) =>
        Box(type, [[(byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], .. parts]);

    private static byte[] Meta(params byte[][] children) => Full("meta", 0, 0, children);

    private static byte[] Hdlr(string handler) => Full("hdlr", 0, 0, U32(0), Encoding.ASCII.GetBytes(handler), new byte[12], [0]);

    private static byte[] Iinf(params byte[][] entries) => Full("iinf", 0, 0, [U16(entries.Length), .. entries]);

    private static byte[] Infe(uint id, string type, string name, byte[]? extra = null, bool hidden = false, int version = 2) =>
        Full("infe", version, hidden ? 1 : 0, version == 2 ? U16((int)id) : U32(id), U16(0), Encoding.ASCII.GetBytes(type),
            Encoding.ASCII.GetBytes(name + "\0"), extra ?? []);

    private static byte[] Iloc(int version, int offsetSize, int lengthSize, int baseSize, int indexSize, params (uint Id, int Method, (long Offset, long Length)[] Extents)[] items)
    {
        List<byte> body = [(byte)((offsetSize << 4) | lengthSize), (byte)((baseSize << 4) | (version > 0 ? indexSize : 0))];
        body.AddRange(version < 2 ? U16(items.Length) : U32(items.Length));
        foreach ((uint id, int method, (long Offset, long Length)[] extents) in items)
        {
            body.AddRange(version < 2 ? U16((int)id) : U32(id));
            if (version > 0)
            {
                body.AddRange(U16(method));
            }

            body.AddRange(U16(0));
            body.AddRange(Sized(baseSize == 0 ? 0 : 8, baseSize));
            body.AddRange(U16(extents.Length));
            for (int e = 0; e < extents.Length; e++)
            {
                if (version > 0)
                {
                    body.AddRange(Sized(e, indexSize));
                }

                // With a base offset, the offsets are relative to it.
                body.AddRange(Sized(extents[e].Offset - (baseSize == 0 ? 0 : 8), offsetSize));
                body.AddRange(Sized(extents[e].Length, lengthSize));
            }
        }

        return Full("iloc", version, 0, [.. body]);
    }

    private static byte[] Sized(long value, int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            bytes[size - 1 - i] = (byte)(value >> (8 * i));
        }

        return bytes;
    }
}
