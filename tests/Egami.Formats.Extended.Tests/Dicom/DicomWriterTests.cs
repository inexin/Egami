using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Extended.Dicom;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dicom;

/// <summary>
/// DICOM writing (PS3.5 and PS3.10): transcoding opened files into every written transfer syntax and back through Egami's
/// reader, Secondary Capture images from rasters, the file meta information, offset tables and UIDs.
/// </summary>
public class DicomWriterTests
{
    private const string Explicit = "1.2.840.10008.1.2.1";
    private const string BigEndian = "1.2.840.10008.1.2.2";
    private const string Implicit = "1.2.840.10008.1.2";

    private static readonly EgamiContext Context = new EgamiContext().WithDicom();

    private static readonly DicomTransferSyntax[] Lossless =
    [
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
        DicomTransferSyntax.RleLossless,
        DicomTransferSyntax.JpegLossless,
        DicomTransferSyntax.JpegLsLossless,
        DicomTransferSyntax.JpegXlLossless,
    ];

    public static TheoryData<string> LosslessUids() => [.. Lossless.Select(s => s.Uid)];

    [Theory]
    [MemberData(nameof(LosslessUids))]
    public void KeepsSignedTwelveBitFramesExactly(string uid)
    {
        int[][] frames = [Ramp(48, 40, 12, true, 1), Ramp(48, 40, 12, true, 2)];
        byte[] source = Mono(48, 40, 16, 12, 11, true, frames).File(Explicit);
        DicomFile original = DicomFile.Load(source);

        DicomFile written = Transcode(original, Syntax(uid));

        Assert.Equal(uid, written.TransferSyntax.Uid);
        Assert.Equal(2, written.FrameCount);
        Assert.Equal(frames[0], Samples(written, 0));
        Assert.Equal(frames[1], Samples(written, 1));
        AssertKept(original, written);
        Assert.Null(written.Dataset[new DicomTag(0x0028, 0x2110)]);
        Assert.Equal(original.Dataset.GetString("SOPInstanceUID"), written.Dataset.GetString("SOPInstanceUID"));
    }

    [Theory]
    [MemberData(nameof(LosslessUids))]
    public void KeepsRgbFramesExactly(string uid)
    {
        int[] rgb = Ramp(33, 21, 8, false, 3, components: 3);
        DicomFile original = DicomFile.Load(Colour(33, 21, rgb, "RGB").File(Explicit));

        DicomFile written = Transcode(original, Syntax(uid));

        Assert.Equal(rgb, Samples(written, 0));
        Assert.Equal("RGB", written.Dataset.GetString("PhotometricInterpretation"));
        using Raster before = original.LoadFrame(0, null, Context), after = written.LoadFrame(0, null, Context);
        Assert.Equal(Pixels<Rgb8>(before), Pixels<Rgb8>(after));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void KeepsUnsignedFullDepthGrayExactly(int bits)
    {
        int[] gray = Ramp(64, 17, bits, false, 4);
        DicomFile original = DicomFile.Load(Mono(64, 17, bits, bits, bits - 1, false, [gray]).File(Implicit));
        foreach (DicomTransferSyntax syntax in Lossless)
        {
            Assert.Equal(gray, Samples(Transcode(original, syntax), 0));
        }
    }

    [Fact]
    public void WritesFourByFourSamplingAsFullChroma()
    {
        // YBR_FULL_422 native data (two luma samples, then Cb and Cr) decodes with each chroma pair given both pixels.
        byte[] data = [10, 20, 100, 200, 30, 40, 110, 210, 50, 60, 120, 220, 70, 80, 130, 230];
        DicomFile original = DicomFile.Load(Colour(2, 4, null, "YBR_FULL_422").Native(data, "OB").File(Explicit));

        DicomFile written = Transcode(original, DicomTransferSyntax.RleLossless);

        Assert.Equal("YBR_FULL", written.Dataset.GetString("PhotometricInterpretation"));
        Assert.Equal(Samples(original, 0), Samples(written, 0));
    }

    [Theory]
    [InlineData("1.2.840.10008.1.2.1", "YBR_FULL")]
    [InlineData("1.2.840.10008.1.2.4.80", "YBR_FULL")]
    [InlineData("1.2.840.10008.1.2.4.110", null)]
    public void DecodesJpegFramesForAnotherSyntax(string uid, string? photometric)
    {
        // A baseline JPEG frame in YBR_FULL_422 decodes to full-resolution YCbCr, which lossless syntaxes keep as YBR_FULL.
        using Raster<Rgb8> picture = Raster.Create<Rgb8>(40, 24);
        int[] smooth = Smooth(40, 24, 3, 255);
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                int i = ((y * 40) + x) * 3;
                picture.Row(y)[x] = new Rgb8((byte)smooth[i], (byte)smooth[i + 1], (byte)smooth[i + 2]);
            }
        }

        using MemoryStream jpeg = new();
        picture.Save(jpeg, Egami.Formats.Jpeg.JpegFormat.Info, new Egami.Formats.Jpeg.JpegWriterOptions { Quality = 95, Subsampling = Egami.Formats.Jpeg.ChromaSubsampling.Subsample422 });
        DicomFile original = DicomFile.Load(Colour(40, 24, null, "YBR_FULL_422").Encapsulated([], jpeg.ToArray()).File("1.2.840.10008.1.2.4.50"));

        if (photometric is null)
        {
            Assert.Throws<NotSupportedException>(() => original.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.FromUid(uid) }));
            return;
        }

        DicomFile written = Transcode(original, DicomTransferSyntax.FromUid(uid), new DicomWriterOptions { TransferSyntax = Lossless.Single(s => s.Uid == uid) });

        Assert.Equal(photometric, written.Dataset.GetString("PhotometricInterpretation"));
        Assert.Equal(Samples(original, 0), Samples(written, 0));
        using Raster before = original.LoadFrame(0, null, Context), after = written.LoadFrame(0, null, Context);
        Assert.InRange(MeanError(before, after), 0, 0.5);
    }

    [Fact]
    public void CodesNearLosslessJpegLsWithinItsBound()
    {
        int[] gray = Ramp(50, 30, 12, false, 5);
        DicomFile original = DicomFile.Load(Mono(50, 30, 16, 12, 11, false, [gray]).File(Explicit));

        DicomFile written = Transcode(original, DicomTransferSyntax.JpegLsNearLossless, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.JpegLsNearLossless, NearLossless = 3 });

        int[] back = Samples(written, 0);
        Assert.InRange(gray.Zip(back, (a, b) => Math.Abs(a - b)).Max(), 1, 3);
        AssertLossy(original, written, "ISO_14495_1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodesBaselineJpegCloseToTheSource(bool colour)
    {
        int[] samples = Smooth(64, 48, colour ? 3 : 1, 255);
        DicomImageBuilder image = colour ? Colour(64, 48, samples, "RGB") : Mono(64, 48, 8, 8, 7, false, [samples]);
        DicomFile original = DicomFile.Load(image.File(Explicit));

        DicomFile written = Transcode(original, DicomTransferSyntax.JpegBaseline);

        Assert.Equal(colour ? "YBR_FULL_422" : "MONOCHROME2", written.Dataset.GetString("PhotometricInterpretation"));
        using Raster before = original.LoadFrame(0, null, Context), after = written.LoadFrame(0, null, Context);
        Assert.InRange(MeanError(before, after), 0.01, 2.5);
        AssertLossy(original, written, "ISO_10918_1");
    }

    [Fact]
    public void CodesTwelveBitJpegExtendedCloseToTheSource()
    {
        int[] gray = Smooth(64, 48, 1, 4000);
        DicomFile original = DicomFile.Load(Mono(64, 48, 16, 10, 9, false, [gray.Select(v => v / 4).ToArray()]).File(Explicit));

        DicomFile written = Transcode(original, DicomTransferSyntax.JpegExtended);

        Assert.Equal(12, written.Dataset.GetNumber("BitsStored"));
        Assert.Equal(11, written.Dataset.GetNumber("HighBit"));
        int[] before = Samples(original, 0), after = Samples(written, 0);
        Assert.InRange(before.Zip(after, (a, b) => Math.Abs(a - b)).Average(), 0, 2);
    }

    [Fact]
    public void CodesLossyJpegXlCloseToTheSource()
    {
        int[] rgb = Smooth(40, 40, 3, 255);
        DicomFile original = DicomFile.Load(Colour(40, 40, rgb, "RGB").File(Explicit));

        DicomFile written = Transcode(original, DicomTransferSyntax.JpegXl);

        Assert.InRange(rgb.Zip(Samples(written, 0), (a, b) => Math.Abs(a - b)).Average(), 0, 3);
        AssertLossy(original, written, "ISO_18181_1");
    }

    [Fact]
    public void RefusesWhatALossyOrRgbSyntaxCannotHold()
    {
        DicomFile signed = DicomFile.Load(Mono(8, 8, 16, 12, 11, true, [Ramp(8, 8, 12, true, 1)]).File(Explicit));
        DicomFile deep = DicomFile.Load(Mono(8, 8, 16, 16, 15, false, [Ramp(8, 8, 16, false, 1)]).File(Explicit));
        DicomFile ybr = DicomFile.Load(Colour(8, 8, Ramp(8, 8, 8, false, 1, 3), "YBR_FULL").File(Explicit));

        Assert.Throws<NotSupportedException>(() => signed.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.JpegBaseline }));
        Assert.Throws<NotSupportedException>(() => deep.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.JpegExtended }));
        Assert.Throws<NotSupportedException>(() => deep.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.JpegXl }));
        Assert.Throws<NotSupportedException>(() => ybr.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.JpegXlLossless }));
        Assert.Throws<NotSupportedException>(() => deep.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.ExplicitVRBigEndian }));
        Assert.Throws<NotSupportedException>(() => deep.Save(Stream.Null, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.FromUid("1.2.840.10008.1.2.4.90") }));
    }

    [Fact]
    public void KeepsEveryElementOfABigEndianFile()
    {
        int[] gray = Ramp(6, 5, 16, false, 9);
        DicomImageBuilder image = Mono(6, 5, 16, 16, 15, false, [gray])
            .Add(0x0008, 0x1140, b => b.Sequence(0x0008, 0x1140, true, false, item => item.Text(0x0008, 0x1150, "UI", "1.2.3.4").Text(0x0008, 0x1155, "UI", "1.2.3.4.5")))
            .Add(0x0018, 0x1310, b => b.UInt16(0x0018, 0x1310, "US", 1, 2, 3, 4))
            .Add(0x0028, 0x1050, b => b.Text(0x0028, 0x1050, "DS", "100.5"))
            .Add(0x0009, 0x0010, b => b.Text(0x0009, 0x0010, "LO", "EGAMI TEST"))
            .Add(0x0009, 0x1001, b => b.UInt32(0x0009, 0x1001, "UL", 0xDEADBEEF));
        DicomFile original = DicomFile.Load(image.File(BigEndian));

        DicomFile written = Transcode(original, DicomTransferSyntax.ExplicitVRLittleEndian);

        Assert.Equal(gray, Samples(written, 0));
        Assert.Equal([1L, 2, 3, 4], written.Dataset[new DicomTag(0x0018, 0x1310)]!.GetIntegers());
        Assert.Equal([0xDEADBEEFL], written.Dataset[new DicomTag(0x0009, 0x1001)]!.GetIntegers());
        Assert.Equal("1.2.3.4.5", written.Dataset["ReferencedImageSequence"]!.Items[0].GetString("ReferencedSOPInstanceUID"));
        Assert.Equal("EGAMI TEST", written.Dataset[new DicomTag(0x0009, 0x0010)]!.GetString());
        Assert.Equal(100.5, written.Dataset.GetNumber("WindowCenter"));
    }

    [Fact]
    public void CopiesPixelDataAlreadyInTheTargetSyntax()
    {
        DicomFile original = DicomFile.Load(Mono(16, 16, 16, 12, 11, false, [Ramp(16, 16, 12, false, 1), Ramp(16, 16, 12, false, 2)]).File(Explicit));
        DicomFile rle = Transcode(original, DicomTransferSyntax.RleLossless);

        DicomFile again = Transcode(rle, DicomTransferSyntax.RleLossless);

        IReadOnlyList<ReadOnlyMemory<byte>> before = rle.Dataset[DicomTag.PixelData]!.Fragments!.Fragments;
        IReadOnlyList<ReadOnlyMemory<byte>> after = again.Dataset[DicomTag.PixelData]!.Fragments!.Fragments;
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.True(before[i].Span.SequenceEqual(after[i].Span));
        }
    }

    [Theory]
    [InlineData(DicomOffsetTable.Basic)]
    [InlineData(DicomOffsetTable.Extended)]
    [InlineData(DicomOffsetTable.None)]
    public void WritesTheOffsetTableAskedFor(DicomOffsetTable table)
    {
        int[][] frames = [.. Enumerable.Range(0, 3).Select(f => Ramp(20, 10, 16, false, f + 1))];
        DicomFile original = DicomFile.Load(Mono(20, 10, 16, 16, 15, false, frames).File(Explicit));

        DicomFile written = Transcode(original, DicomTransferSyntax.JpegLsLossless, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.JpegLsLossless, OffsetTable = table });

        DicomFragments fragments = written.Dataset[DicomTag.PixelData]!.Fragments!;
        Assert.Equal(3, fragments.Fragments.Count);
        Assert.Equal(table == DicomOffsetTable.Basic ? 3 : 0, fragments.BasicOffsetTable.Count);
        Assert.Equal(table == DicomOffsetTable.Extended ? 3 : 0, fragments.ExtendedOffsetTable.Count);
        if (table == DicomOffsetTable.Extended)
        {
            Assert.Equal((ulong)fragments.Fragments[0].Length + 8, fragments.ExtendedOffsetTable[1]);
            Assert.Equal((ulong)fragments.Fragments[2].Length, fragments.ExtendedOffsetTableLengths[2]);
        }

        for (int f = 0; f < 3; f++)
        {
            Assert.Equal(frames[f], Samples(written, f));
        }
    }

    [Fact]
    public void WritesTheFileMetaInformation()
    {
        DicomFile original = DicomFile.Load(Mono(4, 4, 8, 8, 7, false, [Ramp(4, 4, 8, false, 1)]).Text(0x0008, 0x0016, "UI", "1.2.840.10008.5.1.4.1.1.7")
            .Text(0x0008, 0x0018, "UI", "1.2.3.99").File(Implicit));
        using MemoryStream output = new();
        original.Save(output, new DicomWriterOptions { TransferSyntax = DicomTransferSyntax.DeflatedExplicitVRLittleEndian });
        byte[] data = output.ToArray();

        Assert.True(data.AsSpan(0, 128).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal("DICM"u8.ToArray(), data[128..132]);
        Assert.Equal("UL"u8.ToArray(), data[136..138]);
        uint groupLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(140));
        DicomFile written = DicomFile.Load(data);
        Assert.Equal(groupLength, (uint)written.FileMetaInformation.Where(e => e.Tag.Element != 0).Sum(e => e.RawValue.Length + (DicomVRIsLong(e.VR) ? 12 : 8)));
        Assert.Equal("1.2.840.10008.5.1.4.1.1.7", written.FileMetaInformation.GetString("MediaStorageSOPClassUID"));
        Assert.Equal("1.2.3.99", written.FileMetaInformation.GetString("MediaStorageSOPInstanceUID"));
        Assert.Equal(DicomTransferSyntax.DeflatedExplicitVRLittleEndian.Uid, written.FileMetaInformation.GetString("TransferSyntaxUID"));
        Assert.Equal(DicomUid.ImplementationClassUid, written.FileMetaInformation.GetString("ImplementationClassUID"));
        Assert.StartsWith("EGAMI", written.FileMetaInformation.GetString("ImplementationVersionName"), StringComparison.Ordinal);
        Assert.Equal([0L, 1L], written.FileMetaInformation["FileMetaInformationVersion"]!.RawValue.ToArray().Select(b => (long)b));
        Assert.Empty(written.Warnings);
    }

    [Fact]
    public void GeneratesUuidDerivedUids()
    {
        HashSet<string> seen = [];
        for (int i = 0; i < 1000; i++)
        {
            string uid = DicomUid.Generate();
            Assert.StartsWith("2.25.", uid, StringComparison.Ordinal);
            Assert.InRange(uid.Length, 6, 64);
            Assert.True(uid[5..].All(char.IsAsciiDigit));
            Assert.True(uid.Length == 6 || uid[5] != '0', "No leading zero (PS3.5 9.1).");
            Assert.True(seen.Add(uid));
        }

        Assert.True(System.Numerics.BigInteger.Parse(DicomUid.ImplementationClassUid[5..], System.Globalization.CultureInfo.InvariantCulture) < System.Numerics.BigInteger.Pow(2, 128));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WritesSecondaryCaptureImagesOfRasters(bool colour, bool wide)
    {
        using Raster<Rgba16> picture = Raster.Create<Rgba16>(37, 23);
        for (int y = 0; y < 23; y++)
        {
            Span<Rgba16> row = picture.Row(y);
            for (int x = 0; x < 37; x++)
            {
                row[x] = new Rgba16((ushort)(x * 1700), (ushort)(y * 2800), (ushort)((x + y) * 1100), 40000);
            }
        }

        using Raster raster = (colour, wide) switch
        {
            (false, false) => picture.As<Gray8>(),
            (false, true) => picture.As<Gray16>(),
            (true, false) => picture.As<Rgb8>(),
            _ => picture.As<Rgb16>(),
        };
        using MemoryStream output = new();
        raster.Save(output, DicomFormat.Info, new DicomWriterOptions
        {
            TransferSyntax = DicomTransferSyntax.JpegLsLossless,
            Attributes = new Dictionary<string, string> { ["PatientName"] = "Ødegård^Åse", ["PatientID"] = "E-42", ["StudyDescription"] = "Egami test", ["Rows"] = "1" }
                .Where(p => p.Key != "Rows").ToDictionary(),
        });

        DicomFile file = DicomFile.Load(output.ToArray());
        Assert.Equal("1.2.840.10008.5.1.4.1.1.7", file.Dataset.GetString("SOPClassUID"));
        Assert.Equal("ISO_IR 192", file.Dataset.GetString("SpecificCharacterSet"));
        Assert.Equal("Ødegård", file.Dataset["PatientName"]!.GetPersonName()!.FamilyName);
        Assert.Equal("E-42", file.Dataset.GetString("PatientID"));
        Assert.Equal("WSD", file.Dataset.GetString("ConversionType"));
        Assert.Equal(colour ? "RGB" : "MONOCHROME2", file.Dataset.GetString("PhotometricInterpretation"));
        Assert.Equal(wide ? 16 : 8, file.Dataset.GetNumber("BitsStored"));
        Assert.Equal(file.Dataset.GetString("SOPInstanceUID"), file.FileMetaInformation.GetString("MediaStorageSOPInstanceUID"));
        Assert.Empty(file.Warnings);

        using Raster back = Raster.Load(output.ToArray(), Context, new DicomReaderOptions { Values = DicomValues.Stored });
        int[] expected = (colour, wide) switch
        {
            (false, false) => [.. Pixels<Gray8>(raster).Select(p => (int)p.L)],
            (false, true) => [.. Pixels<Gray16>(raster).Select(p => (int)p.L)],
            (true, false) => [.. Pixels<Rgb8>(raster).SelectMany(p => new int[] { p.R, p.G, p.B })],
            _ => [.. Pixels<Rgb16>(raster).SelectMany(p => new int[] { p.R, p.G, p.B })],
        };
        Assert.Equal(expected, Samples(file, 0));
    }

    [Fact]
    public void RefusesAttributesTheImageSets()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(2, 2);
        Assert.Throws<ArgumentException>(() => raster.Save(Stream.Null, DicomFormat.Info, new DicomWriterOptions { Attributes = new Dictionary<string, string> { ["Rows"] = "3" } }));
        Assert.Throws<ArgumentException>(() => raster.Save(Stream.Null, DicomFormat.Info, new DicomWriterOptions { Attributes = new Dictionary<string, string> { ["NoSuchKeyword"] = "x" } }));
        Assert.Throws<ArgumentException>(() => raster.Save(Stream.Null, DicomFormat.Info, new DicomWriterOptions { Attributes = new Dictionary<string, string> { ["InstanceNumber"] = "1", ["ReferencedImageSequence"] = "x" } }));
    }

    [Fact]
    public void TakesNumbersAndTagsAsText()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(2, 2);
        using MemoryStream output = new();
        raster.Save(output, DicomFormat.Info, new DicomWriterOptions
        {
            Attributes = new Dictionary<string, string> { ["DimensionIndexPointer"] = "(0018,1063)", ["WindowCenter"] = "40", ["NumberOfStages"] = "4", ["ImagePositionPatient"] = "1.5\\-2\\3" },
        });

        DicomFile file = DicomFile.Load(output.ToArray());
        Assert.Equal([new DicomTag(0x0018, 0x1063)], file.Dataset["DimensionIndexPointer"]!.GetTags());
        Assert.Equal(40, file.Dataset.GetNumber("WindowCenter"));
        Assert.Equal([4L], file.Dataset["NumberOfStages"]!.GetIntegers());
        Assert.Equal([1.5, -2, 3], file.Dataset["ImagePositionPatient"]!.GetNumbers());
    }

    [Fact]
    public void ChecksTheFrameSizeBeforeReadingRows()
    {
        // Found by fuzzing: Columns given as a UL far past 65535 overflowed the row size instead of breaking a limit.
        byte[] file = new DicomImageBuilder(2, 2, 8).Add(0x0028, 0x0011, b => b.UInt32(0x0028, 0x0011, "UL", 1_500_000_000)).Native(new byte[4], "OB").File(Explicit);

        Assert.Throws<LimitExceededException>(() => Raster.Load(file, Context));
    }

    [Theory]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 1, 1 })]
    [InlineData(new byte[] { 1, 2, 2 })]
    [InlineData(new byte[] { 1, 2, 3, 3, 3, 4 })]
    public void PacksBitsThatUnpackToTheRow(byte[] row) => AssertPacks(row);

    [Fact]
    public void PacksLongRunsAndLiterals()
    {
        AssertPacks([.. Enumerable.Repeat((byte)7, 300)]);
        AssertPacks([.. Enumerable.Range(0, 300).Select(i => (byte)i)]);
        AssertPacks([.. Enumerable.Range(0, 1000).Select(i => (byte)(i % 7 < 3 ? 9 : i))]);
        Random random = new(5);
        byte[] noise = new byte[4096];
        random.NextBytes(noise);
        AssertPacks(noise);
    }

    private static void AssertPacks(byte[] row)
    {
        byte[] packed = new byte[row.Length + ((row.Length + 127) / 128) + 2];
        int length = DicomRle.Pack(row, packed);
        Assert.InRange(length, 1, row.Length + ((row.Length + 127) / 128));
        byte[] back = new byte[row.Length];
        Assert.Equal(row.Length, DicomRle.Unpack(packed.AsSpan(0, length), back));
        Assert.Equal(row, back);
    }

    private static bool DicomVRIsLong(DicomVR vr) => DicomStreamWriter.IsLong(vr);

    private static DicomTransferSyntax Syntax(string uid) => Lossless.Single(s => s.Uid == uid);

    private static DicomFile Transcode(DicomFile file, DicomTransferSyntax syntax, DicomWriterOptions? options = null)
    {
        using MemoryStream output = new();
        file.Save(output, options ?? new DicomWriterOptions { TransferSyntax = syntax });
        DicomFile written = DicomFile.Load(output.ToArray(), null, new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.Empty(written.Warnings);
        return written;
    }

    /// <summary>Returns a frame's stored values, decoded strictly.</summary>
    private static int[] Samples(DicomFile file, int frame)
    {
        using EgamiInput input = new(Stream.Null, Context, default);
        DicomPixelDecoder decoder = new(DicomImageInfo.Read(file), input, new ReaderOptions { Recovery = RecoveryMode.Strict }, (name, message) => Assert.Fail($"{name}: {message}"));
        return decoder.Decode(frame).Samples;
    }

    /// <summary>Checks that every element but the pixel data and the Image Pixel elements a coding may change is kept as it was.</summary>
    private static void AssertKept(DicomFile original, DicomFile written)
    {
        foreach (DicomElement element in original.Dataset)
        {
            if (element.Tag == DicomTag.PixelData || element.Tag.Value is 0x00280004 or 0x00280006)
            {
                continue;
            }

            DicomElement? kept = written.Dataset[element.Tag];
            Assert.NotNull(kept);
            Assert.True(element.RawValue.Span.SequenceEqual(kept.RawValue.Span), $"{element.Tag} changed.");
        }
    }

    private static void AssertLossy(DicomFile original, DicomFile written, string method)
    {
        Assert.Equal("01", written.Dataset.GetString("LossyImageCompression"));
        Assert.Equal(method, written.Dataset.GetString("LossyImageCompressionMethod"));
        Assert.True(written.Dataset.GetNumber("LossyImageCompressionRatio") > 1);
        Assert.NotEqual(original.Dataset.GetString("SOPInstanceUID"), written.Dataset.GetString("SOPInstanceUID"));
        Assert.Equal(written.Dataset.GetString("SOPInstanceUID"), written.FileMetaInformation.GetString("MediaStorageSOPInstanceUID"));
    }

    private static DicomImageBuilder Mono(int columns, int rows, int allocated, int stored, int high, bool signed, int[][] frames)
    {
        int shift = high + 1 - stored;
        int mask = (1 << stored) - 1;
        DicomImageBuilder image = new DicomImageBuilder(rows, columns, allocated, stored, high, signed, frames: frames.Length)
            .Text(0x0008, 0x0016, "UI", "1.2.840.10008.5.1.4.1.1.7")
            .Text(0x0008, 0x0018, "UI", "1.2.826.0.1.3680043.2.1125.1")
            .Text(0x0010, 0x0010, "PN", "Doe^Jane")
            .Text(0x0028, 0x1051, "DS", "400");
        if (allocated == 8)
        {
            return image.Native([.. frames.SelectMany(f => f).Select(v => (byte)((v & mask) << shift))], "OB");
        }

        return image.Native16([.. frames.SelectMany(f => f).Select(v => (ushort)((v & mask) << shift))]);
    }

    private static DicomImageBuilder Colour(int columns, int rows, int[]? samples, string photometric)
    {
        DicomImageBuilder image = new DicomImageBuilder(rows, columns, 8, samples: 3, photometric: photometric)
            .Text(0x0008, 0x0016, "UI", "1.2.840.10008.5.1.4.1.1.7")
            .Text(0x0008, 0x0018, "UI", "1.2.826.0.1.3680043.2.1125.2");
        return samples is null ? image : image.Native([.. samples.Select(v => (byte)v)], "OB");
    }

    /// <summary>Returns values that use the whole range, with runs and noise so every coder has work.</summary>
    private static int[] Ramp(int width, int height, int bits, bool signed, int seed, int components = 1)
    {
        Random random = new(seed);
        int max = (1 << bits) - 1;
        int min = signed ? -(1 << (bits - 1)) : 0;
        int[] values = new int[width * height * components];
        for (int i = 0; i < values.Length; i++)
        {
            int x = i / components % width;
            values[i] = x < width / 4 ? (signed ? min : 0) : x < width / 2 ? random.Next(min, min + max + 1) : min + (int)((long)(i * 7919) % (max + 1));
        }

        values[0] = min;
        values[^1] = min + max;
        return values;
    }

    /// <summary>Returns a smooth picture, as lossy codecs expect.</summary>
    private static int[] Smooth(int width, int height, int components, int max)
    {
        int[] values = new int[width * height * components];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < components; c++)
                {
                    double v = 0.5 + (0.4 * Math.Sin((x + (c * 7)) / 9.0) * Math.Cos((y - (c * 3)) / 7.0));
                    values[(((y * width) + x) * components) + c] = (int)Math.Round(v * max);
                }
            }
        }

        return values;
    }

    private static T[] Pixels<T>(Raster raster)
        where T : unmanaged, IPixelType<T>
    {
        using Raster<T> typed = raster.As<T>();
        return [.. Enumerable.Range(0, typed.Height).SelectMany(y => typed.Row(y).ToArray())];
    }

    private static double MeanError(Raster first, Raster second)
    {
        Rgb8[] a = Pixels<Rgb8>(first), b = Pixels<Rgb8>(second);
        return a.Zip(b, (p, q) => (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3.0).Average();
    }
}
