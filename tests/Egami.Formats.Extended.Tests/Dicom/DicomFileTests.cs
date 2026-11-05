using System.Text;

using Egami.Formats;
using Egami.Formats.Extended.Dicom;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dicom;

/// <summary>DICOM Part 10 parsing (PS3.5, PS3.10) against files written by hand with <see cref="DicomBuilder"/>.</summary>
public class DicomFileTests
{
    private const string Implicit = "1.2.840.10008.1.2";
    private const string Explicit = "1.2.840.10008.1.2.1";
    private const string Deflated = "1.2.840.10008.1.2.1.99";
    private const string BigEndian = "1.2.840.10008.1.2.2";
    private const string Jpeg2000 = "1.2.840.10008.1.2.4.90";

    [Fact]
    public void ReadsExplicitLittleEndianValuesByVR()
    {
        DicomBuilder d = new();
        d.Text(0x0008, 0x0020, "DA", "20261105")
            .Text(0x0008, 0x002A, "DT", "20261105093015.5+0100")
            .Text(0x0008, 0x0030, "TM", "093015.25")
            .Text(0x0008, 0x0060, "CS", "CT")
            .Text(0x0010, 0x0010, "PN", "Doe^John^Q^Dr.^Jr")
            .Double(0x0018, 0x9087, 1000.5)
            .UInt16(0x0028, 0x0010, "US", 512)
            .UInt16(0x0028, 0x0011, "US", 256)
            .Text(0x0028, 0x0030, "DS", "0.5\\0.25")
            .Text(0x0028, 0x1050, "DS", "40 ");
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray()));

        Assert.True(file.HasPreamble);
        Assert.Same(DicomTransferSyntax.ExplicitVRLittleEndian, file.TransferSyntax);
        Assert.Equal("Explicit VR Little Endian", file.TransferSyntax.Name);
        Assert.Equal(Explicit, file.FileMetaInformation[DicomTag.TransferSyntaxUid]!.GetString());
        Assert.Empty(file.Warnings);

        DicomDataset data = file.Dataset;
        Assert.Equal(new DateOnly(2026, 11, 5), data["StudyDate"]!.GetDate());
        Assert.Equal(new TimeOnly(9, 30, 15, 250), data["StudyTime"]!.GetTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 30, 15, 500, TimeSpan.FromHours(1)), data["AcquisitionDateTime"]!.GetDateTime());
        Assert.Equal("CT", data.GetString("Modality"));
        DicomPersonName name = data["PatientName"]!.GetPersonName()!;
        Assert.Equal(("Doe", "John", "Q", "Dr.", "Jr"), (name.FamilyName, name.GivenName, name.MiddleName, name.NamePrefix, name.NameSuffix));
        Assert.Equal([0.5, 0.25], data["PixelSpacing"]!.GetNumbers());
        Assert.Equal(["0.5", "0.25"], data["PixelSpacing"]!.GetStrings());
        Assert.Equal(512, data.GetNumber("Rows"));
        Assert.Equal([256L], data["Columns"]!.GetIntegers());
        Assert.Equal(40, data.GetNumber("WindowCenter"));
        Assert.Equal(1000.5, data.GetNumber("DiffusionBValue"));
        Assert.Equal(DicomVR.US, data["Rows"]!.VR);
        Assert.Null(data["PatientID"]);
        Assert.Equal("(0028,0010) US Rows: 512", data["Rows"]!.ToString());
        Assert.Throws<InvalidOperationException>(() => data["Rows"]!.GetDate());
        Assert.Throws<KeyNotFoundException>(() => data["NotAKeyword"]);
    }

    [Fact]
    public void ReadsImplicitVRWithTheDictionarysVR()
    {
        DicomBuilder d = new(explicitVR: false);
        d.Text(0x0008, 0x0060, "CS", "MR")
            .Text(0x0009, 0x0010, "LO", "ACME 1.0")
            .Bytes(0x0009, 0x1001, "UN", [1, 2, 3, 4])
            .UInt16(0x0028, 0x0103, "US", 1)
            .UInt16(0x0028, 0x0106, "US", 0xFF38)
            .UInt16(0x0028, 0x0107, "US", 1000);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Implicit, d.ToArray()));

        Assert.False(file.TransferSyntax.IsExplicitVR);
        DicomDataset data = file.Dataset;
        Assert.Equal(DicomVR.CS, data["Modality"]!.VR);
        Assert.Equal(DicomVR.LO, data[new DicomTag(0x0009, 0x0010)]!.VR);
        Assert.Equal("ACME 1.0", data[new DicomTag(0x0009, 0x0010)]!.GetString());
        Assert.Equal(DicomVR.UN, data[new DicomTag(0x0009, 0x1001)]!.VR);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, data[new DicomTag(0x0009, 0x1001)]!.RawValue.ToArray());

        // US or SS: Pixel Representation 1 makes the pixel value range signed.
        Assert.Equal(DicomVR.SS, data["SmallestImagePixelValue"]!.VR);
        Assert.Equal(-200, data.GetNumber("SmallestImagePixelValue"));
        Assert.Equal(1000, data.GetNumber("LargestImagePixelValue"));
    }

    [Fact]
    public void ReadsExplicitBigEndian()
    {
        DicomBuilder d = new(bigEndian: true);
        d.UInt16(0x0028, 0x0010, "US", 0x0102)
            .UInt32(0x0028, 0x9001, "UL", 0x01020304)
            .Double(0x0018, 0x9087, -2.5)
            .UInt16(0x7FE0, 0x0010, "OW", 0x0102, 0x0304);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(BigEndian, d.ToArray()));

        Assert.True(file.TransferSyntax.IsBigEndian);
        Assert.Equal(0x0102, file.Dataset.GetNumber("Rows"));
        Assert.Equal(0x01020304, file.Dataset.GetNumber("DataPointRows"));
        Assert.Equal(-2.5, file.Dataset.GetNumber("DiffusionBValue"));
        DicomElement pixels = file.Dataset[DicomTag.PixelData]!;
        Assert.Equal([0x0102L, 0x0304L], pixels.GetIntegers());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, pixels.RawValue.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsDeflatedFiles(bool zlib)
    {
        DicomBuilder d = new();
        d.Text(0x0008, 0x0060, "CS", "OT").Text(0x0010, 0x0010, "PN", new string('x', 4000));
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Deflated, d.ToArray(), deflate: true, zlib: zlib));

        Assert.True(file.TransferSyntax.IsDeflated);
        Assert.Equal("OT", file.Dataset.GetString("Modality"));
        Assert.Equal(4000, file.Dataset.GetString("PatientName")!.Length);
        Assert.Equal(zlib, file.Warnings.Any(w => w.Id == "Dicom.ZlibHeader"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadsSequencesOfDefinedAndUndefinedLength(bool undefinedSequence, bool undefinedItems)
    {
        DicomBuilder d = new();
        d.Text(0x0008, 0x0060, "CS", "CT")
            .Sequence(0x0008, 0x1140, undefinedSequence, undefinedItems,
                item => item.Text(0x0008, 0x1150, "UI", "1.2.3").Text(0x0008, 0x1155, "UI", "4.5.6"),
                item => item.Text(0x0008, 0x1150, "UI", "7.8")
                    .Sequence(0x0040, 0xA730, undefinedSequence, undefinedItems, inner => inner.Text(0x0040, 0xA160, "UT", "nested")))
            .UInt16(0x0028, 0x0010, "US", 8);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray()));

        Assert.Empty(file.Warnings);
        DicomElement sequence = file.Dataset["ReferencedImageSequence"]!;
        Assert.Equal(DicomVR.SQ, sequence.VR);
        Assert.Equal(2, sequence.Items.Count);
        Assert.Equal("4.5.6", sequence.Items[0].GetString("ReferencedSOPInstanceUID"));
        Assert.Equal("7.8", sequence.Items[1].GetString("ReferencedSOPClassUID"));
        Assert.Equal("nested", sequence.Items[1]["ContentSequence"]!.Items[0].GetString("TextValue"));
        Assert.Equal(8, file.Dataset.GetNumber("Rows"));
        Assert.Equal("(0008,1140) SQ ReferencedImageSequence: 2 items", sequence.ToString());
    }

    [Fact]
    public void ReadsAnUnknownElementOfUndefinedLengthAsAnImplicitSequence()
    {
        // PS3.5 6.2.2: UN of undefined length holds a sequence in implicit VR little endian.
        DicomBuilder inner = new(explicitVR: false);
        inner.Sequence(0x0009, 0x1010, true, true, item => item.Text(0x0008, 0x0060, "CS", "US"));
        byte[] implicitSequence = inner.ToArray();

        DicomBuilder d = new();
        d.Text(0x0009, 0x0010, "LO", "ACME")
            .Header(0x0009, 0x1010, "UN", 0xFFFFFFFF)
            .Append(implicitSequence[8..])
            .UInt16(0x0028, 0x0010, "US", 4);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray()));

        DicomElement element = file.Dataset[new DicomTag(0x0009, 0x1010)]!;
        Assert.Equal(DicomVR.SQ, element.VR);
        Assert.Equal("US", element.Items[0].GetString("Modality"));
        Assert.Equal(4, file.Dataset.GetNumber("Rows"));
    }

    [Fact]
    public void KeepsEncapsulatedPixelDataAsFragments()
    {
        byte[] first = [0xFF, 0xD8, 1, 2], second = [3, 4], third = [5, 6, 0xFF, 0xD9];
        byte[] table = new byte[16];
        BitConverter.TryWriteBytes(table.AsSpan(0), 0UL);
        BitConverter.TryWriteBytes(table.AsSpan(8), 20UL);
        DicomBuilder d = new();
        d.UInt16(0x0028, 0x0008, "US", 2)
            .Bytes(0x7FE0, 0x0001, "OV", table)
            .Bytes(0x7FE0, 0x0002, "OV", [.. BitConverter.GetBytes(10UL), .. BitConverter.GetBytes(12UL)])
            .Encapsulated([0, 20], first, second, third);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Jpeg2000, d.ToArray()));

        Assert.True(file.TransferSyntax.IsEncapsulated);
        Assert.StartsWith("JPEG 2000", file.TransferSyntax.Name, StringComparison.Ordinal);
        DicomFragments fragments = file.Dataset[DicomTag.PixelData]!.Fragments!;
        Assert.Equal([0u, 20u], fragments.BasicOffsetTable);
        Assert.Equal([0UL, 20UL], fragments.ExtendedOffsetTable);
        Assert.Equal([10UL, 12UL], fragments.ExtendedOffsetTableLengths);
        Assert.Equal([first, second, third], fragments.Fragments.Select(f => f.ToArray()));
        Assert.Empty(file.Warnings);
    }

    [Fact]
    public void DecodesTheJapaneseExampleOfPs35AnnexH()
    {
        // PS3.5 H.3.1, example 1: ISO 2022 IR 87, JIS X 0208 kanji and hiragana between escapes.
        byte[] value = Bytes("59 61 6d 61 64 61 5e 54 61 72 6f 75 3d 1b 24 42 3b 33 45 44 1b 28 42 5e 1b 24 42 42 40 4f 3a 1b 28 42 3d"
            + " 1b 24 42 24 64 24 5e 24 40 1b 28 42 5e 1b 24 42 24 3f 24 6d 24 26 1b 28 42");
        Assert.Equal("Yamada^Tarou=山田^太郎=やまだ^たろう", Name("\\ISO 2022 IR 87", value).ToString());
    }

    [Fact]
    public void DecodesTheKoreanExampleOfPs35AnnexI()
    {
        // PS3.5 I.2: ISO 2022 IR 149, KS X 1001 in G1.
        byte[] value = Bytes("48 6f 6e 67 5e 47 69 6c 64 6f 6e 67 3d 1b 24 29 43 fb f3 5e 1b 24 29 43 d1 ce d4 d7 3d 1b 24 29 43 c8 ab 5e"
            + " 1b 24 29 43 b1 e6 b5 bf");
        DicomPersonName name = Name("\\ISO 2022 IR 149", value);
        Assert.Equal("Hong^Gildong=洪^吉洞=홍^길동", name.ToString());
        Assert.Equal("洪^吉洞", name.Ideographic);
    }

    [Theory]
    [InlineData("ISO_IR 192", "57 61 6e 67 5e 58 69 61 6f 44 6f 6e 67 3d e7 8e 8b 5e e5 b0 8f e6 9d b1 3d", "Wang^XiaoDong=王^小東")]
    [InlineData("GB18030", "57 61 6e 67 5e 58 69 61 6f 44 6f 6e 67 3d cd f5 5e d0 a1 b6 ab 3d", "Wang^XiaoDong=王^小东")]
    [InlineData("ISO_IR 100", "44 fc 72 65 72 5e 47 fc 6e 74 65 72", "Dürer^Günter")]
    [InlineData("ISO_IR 144", "bb ee da e1", "Люкс")]
    [InlineData("", "41 42", "AB")]
    public void DecodesSingleCharacterSets(string set, string hex, string expected)
    {
        Assert.Equal(expected, Name(set, Bytes(hex)).ToString());
    }

    [Fact]
    public void ReadsABareImplicitDatasetWithAWarning()
    {
        DicomBuilder d = new(explicitVR: false);
        d.Text(0x0008, 0x0016, "UI", "1.2.840.10008.5.1.4.1.1.2").Text(0x0008, 0x0060, "CS", "CT").UInt16(0x0028, 0x0010, "US", 64);
        DicomFile file = DicomFile.Load(d.ToArray());

        Assert.False(file.HasPreamble);
        Assert.Empty(file.FileMetaInformation);
        Assert.Same(DicomTransferSyntax.ImplicitVRLittleEndian, file.TransferSyntax);
        Assert.Equal(64, file.Dataset.GetNumber("Rows"));
        Assert.Contains(file.Warnings, w => w.Id == "Dicom.NoPreamble");
    }

    [Fact]
    public void ReadsAnImplicitDatasetWhoseMetaSaysExplicit()
    {
        DicomBuilder d = new(explicitVR: false);
        d.Text(0x0008, 0x0060, "CS", "CT").UInt16(0x0028, 0x0010, "US", 64);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray()));

        Assert.Equal(64, file.Dataset.GetNumber("Rows"));
        Assert.Contains(file.Warnings, w => w.Id == "Dicom.TransferSyntaxMismatch");
    }

    [Fact]
    public void ReadsTheTransferSyntaxAndCharacterSetWhateverTheirVR()
    {
        // A fuzz find: a damaged file gave the transfer syntax UID the VR OB, and the parser asked it for text it could not give.
        DicomBuilder meta = new();
        meta.Bytes(0x0002, 0x0010, "OB", Encoding.ASCII.GetBytes(Explicit + "\0"));
        DicomBuilder d = new();
        d.Bytes(0x0008, 0x0005, "OB", "ISO_IR 192"u8.ToArray()).Text(0x0010, 0x0010, "PN", "Äsa", Encoding.UTF8);
        byte[] data = [.. new byte[128], .. "DICM"u8, .. meta.ToArray(), .. d.ToArray()];

        DicomFile file = DicomFile.Load(data);
        Assert.Same(DicomTransferSyntax.ExplicitVRLittleEndian, file.TransferSyntax);
        Assert.Equal("Äsa", file.Dataset.GetString("PatientName"));
    }

    [Fact]
    public void KeepsWhatWasReadBeforeATruncation()
    {
        DicomBuilder d = new();
        d.Text(0x0008, 0x0060, "CS", "CT").Text(0x0010, 0x0010, "PN", "Doe^Jane");
        byte[] data = DicomBuilder.Part10(Explicit, d.ToArray());
        byte[] cut = data[..^3];

        DicomFile file = DicomFile.Load(cut);
        Assert.Equal("CT", file.Dataset.GetString("Modality"));
        Assert.Equal("Doe^J", file.Dataset.GetString("PatientName"));
        Assert.Contains(file.Warnings, w => w.Id == "Dicom.Truncated");

        CorruptDataException e = Assert.Throws<CorruptDataException>(() => DicomFile.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        Assert.Contains("says it has", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsASequenceThatEndsWithTheFile()
    {
        DicomBuilder d = new();
        d.Text(0x0008, 0x0060, "CS", "CT").Sequence(0x0008, 0x1140, true, true, item => item.Text(0x0008, 0x1150, "UI", "1.2"));
        byte[] data = DicomBuilder.Part10(Explicit, d.ToArray());

        DicomFile file = DicomFile.Load(data.AsMemory(0, data.Length - 16));
        Assert.Equal("1.2", file.Dataset["ReferencedImageSequence"]!.Items[0].GetString("ReferencedSOPClassUID"));
        Assert.Contains(file.Warnings, w => w.Id == "Dicom.MissingDelimiter");
    }

    [Fact]
    public void RefusesSequencesNestedTooDeep()
    {
        Action<DicomBuilder> nest = _ => { };
        for (int i = 0; i < DicomParser.MaxDepth + 2; i++)
        {
            Action<DicomBuilder> inner = nest;
            nest = b => b.Sequence(0x0040, 0xA730, true, true, inner);
        }

        DicomBuilder d = new();
        nest(d);
        Assert.Throws<CorruptDataException>(() => DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray())));
    }

    [Fact]
    public void KeepsTheLoadsMemoryBudget()
    {
        DicomBuilder d = new();
        for (int i = 0; i < 20000; i++)
        {
            d.Bytes(0x0009, (ushort)(0x1000 + (i % 0xE000)), "OB", []);
        }

        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 1 << 20 } };
        Assert.Throws<LimitExceededException>(() => DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray()), tight));
    }

    [Fact]
    public void RefusesDataThatIsNotDicom()
    {
        Assert.Throws<CorruptDataException>(() => DicomFile.Load(Encoding.ASCII.GetBytes("This is a text file, not DICOM at all.")));
    }

    [Fact]
    public void DetectsAndProbesWhenRegistered()
    {
        DicomBuilder d = new();
        d.UInt16(0x0028, 0x0002, "US", 1).Text(0x0028, 0x0008, "IS", "1")
            .UInt16(0x0028, 0x0010, "US", 30).UInt16(0x0028, 0x0011, "US", 40).UInt16(0x0028, 0x0100, "US", 16);
        byte[] data = DicomBuilder.Part10(Explicit, d.ToArray());
        EgamiContext context = new EgamiContext().WithDicom();

        RasterInfo info = Raster.Probe(data, context);
        Assert.Equal("DICOM", info.Format.Name);
        Assert.Equal((40, 30), (info.Width, info.Height));
        Assert.Equal(Gray16.Layout, info.Layout);
        Assert.Throws<NotSupportedException>(() => Raster.Load(data, context));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(data, new EgamiContext()));
    }

    [Fact]
    public void NamesTagsAndUidsFromPs36()
    {
        Assert.Equal(new DicomTag(0x0010, 0x0010), DicomTag.FromKeyword("PatientName"));
        Assert.Equal("PatientName", new DicomTag(0x0010, 0x0010).Keyword);
        Assert.Equal("Patient's Name", new DicomTag(0x0010, 0x0010).Name);
        Assert.Equal("OverlayData", new DicomTag(0x6002, 0x3000).Keyword);
        Assert.Null(new DicomTag(0x0009, 0x1001).Keyword);
        Assert.True(new DicomTag(0x0009, 0x1001).IsPrivate);
        Assert.True(new DicomTag(0x0008, 0x0001).IsRetired);
        Assert.False(DicomTag.TryFromKeyword("NoSuchKeyword", out _));
        Assert.Equal("(7FE0,0010)", DicomTag.PixelData.ToString());
        Assert.True(new DicomTag(0x0008, 0x0005) < new DicomTag(0x0010, 0x0010));
        Assert.StartsWith("DICOM PS3.6 20", DicomFile.DictionaryEdition, StringComparison.Ordinal);
        Assert.Equal("Deflated Explicit VR Little Endian", DicomTransferSyntax.FromUid(Deflated + "\0").Name);
        Assert.False(DicomTransferSyntax.FromUid("1.2.3.4").IsKnown);
    }

    public static TheoryData<string> Seeds()
    {
        return [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "images", "dicom"), "*.dcm").Select(f => Path.GetFileName(f)).Order()];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ReadsTheFuzzSeedsCleanly(string name)
    {
        // The seeds are written by a Python script of their own, so they check this reader against a second writer.
        DicomFile file = DicomFile.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "dicom", name)),
            options: new ReaderOptions { Recovery = RecoveryMode.Strict });

        Assert.All(file.Warnings, w => Assert.Equal("Dicom.NoPreamble", w.Id));
        Assert.NotEmpty(file.Dataset);
        foreach (DicomElement element in file.Dataset)
        {
            Assert.NotEmpty(element.ToString());
        }
    }

    private static DicomPersonName Name(string characterSet, byte[] value)
    {
        DicomBuilder d = new();
        if (characterSet.Length > 0)
        {
            d.Text(0x0008, 0x0005, "CS", characterSet);
        }

        d.Raw(0x0010, 0x0010, "PN", value);
        DicomFile file = DicomFile.Load(DicomBuilder.Part10(Explicit, d.ToArray()));
        Assert.DoesNotContain(file.Warnings, w => w.Id == "Dicom.CharacterSet");
        return file.Dataset["PatientName"]!.GetPersonName()!;
    }

    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16))];
}
