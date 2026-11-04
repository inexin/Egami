using System.Buffers.Binary;

using Egami.Formats.Extended.Raw;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// RAW Build Plan tasks 8 and 9: maker files built in code. Nikon's initial predictors, and RAF's container with the
/// Fujifilm compressed codec round-tripped through a test-only encoder built from the specification's section 10.
/// </summary>
public class RawMakerTests
{
    private static EgamiContext Context => TestImages.Context;

    private static readonly RawReaderOptions Mosaic = new() { Output = RawOutput.Mosaic };

    [Fact]
    public void NikonInitialPredictorsFollowTheSpecOrder()
    {
        // Lossless 12-bit (tree 2), every difference 0: each pixel is its initial predictor. The spec's order is (even row,
        // even column), (even, odd), (odd, even), (odd, odd); the two middle values differ so the other order fails here.
        byte[] meta = new byte[600];
        meta[0] = 0x46;
        meta[1] = 0x30;
        ushort[] seeds = [100, 200, 300, 400];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(2 + (2 * i)), seeds[i]);
        }

        // Tree 2's code for symbol 0 is 11110; eight samples give 40 bits (and a size that is not the uncompressed one).
        BitWriter bits = new();
        for (int i = 0; i < 8; i++)
        {
            bits.Write(0b11110, 5);
        }

        byte[] file = NikonFile(meta, bits.ToArray());

        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal([100, 200, 100, 200], Enumerable.Range(0, 4).Select(x => (int)mosaic.Row(0)[x].L));
        Assert.Equal([300, 400, 300, 400], Enumerable.Range(0, 4).Select(x => (int)mosaic.Row(1)[x].L));
    }

    [Theory]
    [InlineData(true, 768, 12, 14)]
    [InlineData(true, 792, 18, 14)]
    [InlineData(false, 1536, 6, 16)]
    [InlineData(false, 792, 12, 12)]
    public void FujiCompressedRoundTrips(bool xtrans, int width, int height, int bits)
    {
        ushort[] samples = Scene(width, height, bits);
        byte[] file = Raf(width, height, bits, xtrans, FujiEncoder.Encode(samples, width, height, xtrans, bits));
        Assert.Equal("RAW", Raster.Probe(file, Context).Format.Name);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal((width, height), (mosaic.Width, mosaic.Height));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.True(samples[(y * width) + x] == mosaic.Row(y)[x].L, $"({x}, {y}): expected {samples[(y * width) + x]}, got {mosaic.Row(y)[x].L}.");
            }
        }

        RawDetails details = (RawDetails)mosaic.Metadata.FormatDetails!;
        Assert.Equal(xtrans ? 6 : 2, details.CfaWidth);
    }

    [Fact]
    public void RafWithAByteCountPastTheEndReadsStrictly()
    {
        // Review finding: the X-H2S records a strip byte count past the end of the file, which is not damage.
        const int Width = 768, Height = 6;
        ushort[] samples = Scene(Width, Height, 14);
        byte[] file = Raf(Width, Height, 14, true, FujiEncoder.Encode(samples, Width, Height, true, 14), countExtra: 64);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = RecoveryMode.Strict });
        Assert.Equal(samples[^1], mosaic.Row(Height - 1)[Width - 1].L);
        Assert.DoesNotContain(mosaic.Metadata.Warnings, w => w.Id.EndsWith("DataDamaged", StringComparison.Ordinal));
    }

    [Fact]
    public void UncompressedRafReads()
    {
        const int Width = 48, Height = 12;
        ushort[] samples = Scene(Width, Height, 14);
        byte[] data = new byte[Width * Height * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 * i), samples[i]);
        }

        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Raf(Width, Height, 14, true, data), Context, Mosaic);
        Assert.Equal(samples[(5 * Width) + 7], mosaic.Row(5)[7].L);
        Assert.Equal(samples[^1], mosaic.Row(Height - 1)[Width - 1].L);
    }

    [Fact]
    public void RafWithAnotherXTransPhaseIsRefusedWhenCompressed()
    {
        const int Width = 768, Height = 6;
        byte[] stream = FujiEncoder.Encode(Scene(Width, Height, 14), Width, Height, true, 14);
        byte[] shifted = [.. Enumerable.Range(0, 36).Select(i => FujiDecoder.XTrans[((i / 6) * 6) + ((i + 1) % 6)])];
        byte[] file = Raf(Width, Height, 14, true, stream, shifted);
        Assert.ThrowsAny<NotSupportedException>(() => Raster.Load<Gray16>(file, Context, Mosaic).Dispose());
    }

    [Theory]
    [InlineData(RecoveryMode.Strict)]
    [InlineData(RecoveryMode.Lenient)]
    public void CompressedRafCutAfterItsHeaderIsCorrupt(RecoveryMode recovery)
    {
        // Review finding: the strip table and q bases were read past the end of a stream cut after its 16-byte header.
        const int Width = 768, Height = 6;
        byte[] stream = FujiEncoder.Encode(Scene(Width, Height, 14), Width, Height, true, 14);
        byte[] file = Raf(Width, Height, 14, true, stream[..16]);
        Assert.Throws<CorruptDataException>(() => Raster.Load<Gray16>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = recovery }).Dispose());
    }

    [Fact]
    public void RafStripSizesThatOverflowAreMissingStrips()
    {
        // Review finding: 32-bit strip sizes summed in an int wrapped to negative offsets.
        const int Width = 1536, Height = 6;
        byte[] stream = FujiEncoder.Encode(Scene(Width, Height, 14), Width, Height, true, 14);
        BinaryPrimitives.WriteUInt32BigEndian(stream.AsSpan(16), 0xFFFFFFF0);
        byte[] file = Raf(Width, Height, 14, true, stream);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = RecoveryMode.Lenient });
        Assert.Contains(mosaic.Metadata.Warnings, w => w.Id.EndsWith("DataDamaged", StringComparison.Ordinal));
    }

    [Fact]
    public void PentaxCodesKeepOnlyTheirLengthsBits()
    {
        // Review finding: a 16-bit code with bits above its length indexed past the lookup table.
        byte[] table = new byte[14 + (3 * 12)];
        for (int c = 0; c < 12; c++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(14 + (2 * c)), 0xFFFF);
            table[14 + (2 * 12) + c] = (byte)(c + 1);
        }

        Assert.NotNull(MakerDecoders.PentaxCodes(new PentaxTable(table, false)));
    }

    [Fact]
    public void OlympusStreamHeaderOfLengthZeroIsInvalid()
    {
        // Review finding: tag 0x0653 = 0 read the byte before the stream.
        Assert.Throws<InvalidDataException>(() => MakerDecoders.OlympusStart(new byte[16], 0));
        Assert.Equal(5, MakerDecoders.OlympusStart(new byte[] { 0, 0, 1, 0, 0 }, 3));
    }

    [Fact]
    public void TruncatedRw2DecodesWithBlackForTheMissingPart()
    {
        // Review finding: a last Panasonic block shorter than its 0x1FF8 split crashed instead of reading as black.
        TiffBuilder.Ifd ifd0 = new() { OffsetsTag = 0x0118, CountsTag = 0xC6FF };
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "Panasonic"), TiffTag.Ascii(272, "DMC-TEST"), TiffTag.Short(0x0002, 64), TiffTag.Short(0x0003, 64),
            TiffTag.Short(0x000A, 12), TiffTag.Short(0x002D, 4)]);
        ifd0.Units.Add([.. Enumerable.Range(0, 100).Select(i => (byte)(i * 37))]);
        byte[] file = TiffBuilder.Write(ifd0);
        file[2] = 0x55;
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = RecoveryMode.Lenient });
        Assert.Equal((64, 64), (mosaic.Width, mosaic.Height));
        Assert.Equal(0, mosaic.Row(63)[63].L);
    }

    [Fact]
    public void OldPanasonicFileWithSixteenBitWordsDecodes()
    {
        // Gate finding: a .RAW without tag 0x0118 (DMC-FZ50, DMC-LX1, Digilux 3) holding 16-bit words was read as RawFormat 4.
        const int w = 20, h = 4;
        byte[] data = new byte[2 * w * h];
        for (int i = 0; i < w * h; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 * i), (ushort)(OldValue(i) << 4));
        }

        AssertOldPanasonic(OldPanasonicFile(w, h, data), w, h);
    }

    [Fact]
    public void PanasonicZerosFillFromStoredValuesOnly()
    {
        // Gate finding: zeros (mapped defective pixels) filled in place on several threads could read a neighbour another
        // thread had just filled, so files with neighbouring zeros decoded differently from run to run. Each fill now takes
        // the mean of the stored non-zero samples of its colour two pixels away, whatever the thread count.
        const int w = 20, h = 8;
        static int Value(int x, int y) => 100 + (10 * x) + (50 * y);
        (int X, int Y)[] zeros = [(2, 2), (4, 2), (2, 4)];
        byte[] data = new byte[2 * w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 * ((y * w) + x)), (ushort)(zeros.Contains((x, y)) ? 0 : Value(x, y) << 4));
            }
        }

        byte[] file = OldPanasonicFile(w, h, data);
        using Raster<Gray16> many = Raster.Load<Gray16>(file, Context, Mosaic);
        using Raster<Gray16> one = Raster.Load<Gray16>(file, Context with { MaxParallelism = 1 }, Mosaic);
        Assert.Equal((Value(0, 2) + Value(2, 0)) / 2, many.Row(2)[2].L);
        Assert.Equal((Value(6, 2) + Value(4, 0) + Value(4, 4)) / 3, many.Row(2)[4].L);
        Assert.Equal((Value(4, 4) + Value(2, 6) + Value(0, 4)) / 3, many.Row(4)[2].L);
        Assert.Equal(Enumerable.Range(0, h).SelectMany(y => many.Row(y).ToArray()), Enumerable.Range(0, h).SelectMany(y => one.Row(y).ToArray()));
    }

    [Fact]
    public void OldPanasonicFileWithTwelveBitPackingDecodes()
    {
        // Gate finding: a .RAW without tag 0x0118 (DMC-FZ8) holding 12-bit values with a control byte after every ten.
        const int w = 20, h = 3;
        int rowBytes = (w * 3 / 2) + ((w + 2) / 10);
        byte[] data = new byte[rowBytes * h];
        for (int y = 0; y < h; y++)
        {
            int p = y * rowBytes;
            for (int x = 0; x < w; x += 2)
            {
                int a = OldValue((y * w) + x), b = OldValue((y * w) + x + 1);
                data[p] = (byte)a;
                data[p + 1] = (byte)((a >> 8) | (b << 4));
                data[p + 2] = (byte)(b >> 4);
                p += (x + 2) % 10 == 0 ? 4 : 3;
            }
        }

        AssertOldPanasonic(OldPanasonicFile(w, h, data), w, h);
    }

    [Fact]
    public void OldPanasonicWhiteBalanceFromRedAndBlueOnly()
    {
        // Gate finding: the Leica Digilux 2 gives only tags 0x0011 and 0x0012 (red, blue; green 256), as Adobe reads them.
        const int w = 20, h = 4;
        byte[] file = OldPanasonicFile(w, h, new byte[2 * w * h], TiffTag.Short(0x0011, 410), TiffTag.Short(0x0012, 373));
        using Raster mosaic = Raster.Load(file, Context, Mosaic);
        RawDetails details = (RawDetails)mosaic.Metadata.FormatDetails!;
        Assert.Equal([256 / 410.0, 1, 256 / 373.0], details.AsShotNeutral);
    }

    [Theory]
    [InlineData(3, 0, 0, 0, new double[] { 15, 15, 15, 15 })]
    [InlineData(4, 128, 129, 130, new double[] { 143, 144, 144, 145 })]
    public void PanasonicEncodingsThreeAndFourTakeTheBlackTagsPlusFifteen(int encoding, int red, int green, int blue, double[] black)
    {
        // RAW follow-up: Adobe DNG Converter's black for these encodings is the file's black tags plus 15. It stores that for the
        // GH4, GH5 and D-LUX 4, and subtracts it from the DMC-L10's values instead, which put a black of 0 in the camera table.
        TiffBuilder.Ifd ifd0 = new() { OffsetsTag = 0x0118, CountsTag = 0xC6FF };
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "Panasonic"), TiffTag.Ascii(272, "DMC-L10"), TiffTag.Short(0x0002, 64), TiffTag.Short(0x0003, 64),
            TiffTag.Short(0x0009, 1), TiffTag.Short(0x000A, 12), TiffTag.Short(0x002D, (ushort)encoding), TiffTag.Short(0x001C, (ushort)red),
            TiffTag.Short(0x001D, (ushort)green), TiffTag.Short(0x001E, (ushort)blue)]);
        ifd0.Units.Add(new byte[0x4000]);
        byte[] file = TiffBuilder.Write(ifd0);
        file[2] = 0x55;
        using Raster mosaic = Raster.Load(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = RecoveryMode.Lenient });
        Assert.Equal(black, ((RawDetails)mosaic.Metadata.FormatDetails!).BlackLevels);
    }

    [Theory]
    [InlineData("Canon EOS 7D", 1_000_000, 2, 2, 6)]
    [InlineData("Canon EOS 7D", 2_000_003, 2, 2, 6)]
    [InlineData("Canon EOS 50D", 1_000_005, 2, 1, 8)]
    [InlineData("Canon EOS 50D", 1_000_006, 2, 1, 4)]
    [InlineData("Canon EOS 5D Mark II", 1_000_005, 2, 1, 4)]
    [InlineData("Canon EOS 5D Mark II", 1_000_006, 2, 1, 8)]
    [InlineData("Canon EOS 5D Mark II", 2_001_002, 2, 1, 4)]
    [InlineData("Canon EOS-1D Mark IV", 1_001_000, 2, 1, 4)]
    [InlineData("Canon EOS 60D", 1_000_000, 2, 1, 4)]
    public void CanonSrawHueIsAdobes(string model, long firmware, int h, int v, int hue)
    {
        // RAW follow-up: measured on Adobe DNG Converter's DNGs of the CC0 samples with only their firmware string changed. The
        // 7D mRAW's earlier hue of 16 gave its develop a 2 % blue cast.
        Assert.Equal(hue, MakerParser.CanonSrawHue(model, firmware, h, v));
    }

    [Fact]
    public void CanonSrawTakesTheTablesReducedExposure()
    {
        // RAW follow-up: Adobe moves an sRAW file's baseline exposure with the white level it picks by ISO; the table keeps the
        // pair it measured, which differs from the full-resolution exposure (70D: +0.25 against -0.09).
        RawCameraEntry camera = RawCameraTable.Find("Canon", "Canon EOS 70D")!;
        Assert.Equal((13000.0, 0.25, -0.09), (camera.ReducedWhite, camera.ReducedExposure, camera.Profile.BaselineExposure));
    }

    [Theory]
    [InlineData("rwl", "image/x-leica-rwl")]
    [InlineData("rw2", "image/x-panasonic-rw2")]
    public void PanasonicAndLeicaExtensionsAreRaw(string extension, string mime)
    {
        Assert.Contains(extension, RawFormat.Info.Extensions);
        Assert.Contains(mime, RawFormat.Info.MimeTypes);
    }

    /// <summary>A 12-bit test value for pixel <paramref name="i"/>, never 0 (Panasonic's mark for a defective pixel).</summary>
    private static int OldValue(int i) => (i * 97 % 4000) + 1;

    private static void AssertOldPanasonic(byte[] file, int width, int height)
    {
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal((width, height), (mosaic.Width, mosaic.Height));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal(OldValue((y * width) + x), mosaic.Row(y)[x].L);
            }
        }
    }

    /// <summary>An old Panasonic file: magic 0x55, the sensor size in tags 2 and 3, StripOffsets and no tag 0x0118.</summary>
    private static byte[] OldPanasonicFile(int width, int height, byte[] data, params TiffTag[] extra)
    {
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "Panasonic"), TiffTag.Ascii(272, "DMC-TEST"), TiffTag.Short(0x0002, width), TiffTag.Short(0x0003, height),
            TiffTag.Short(0x000A, 12), .. extra]);
        ifd0.Units.Add(data);
        byte[] file = TiffBuilder.Write(ifd0);
        file[2] = 0x55;
        return file;
    }

    [Fact]
    public void NikonCurveWithMorePointsThanValuesIsNotADivisionByZero()
    {
        // Review finding: a stepped curve (0x44, 0x20) with more points than sample values made its step 0.
        byte[] meta = new byte[600];
        meta[0] = 0x44;
        meta[1] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(10), 5000);
        Exception? error = Record.Exception(() => Raster.Load<Gray16>(NikonFile(meta, [0, 0, 0, 0, 0, 0, 0, 0, 0]), Context, Mosaic).Dispose());
        Assert.True(error is null or CorruptDataException, error?.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LossyNefConvertsToDngCodesWithItsCurveAsLinearization(bool monotonic)
    {
        // A lossy NEF (0x44 0x10) with a 64-point curve, every difference 0 so each pixel is its initial predictor's curve
        // value. Its DNG stores the codes and the curve as LinearizationTable, which gives the same values back (egami-compare
        // checks the develops on real files); a curve that falls is kept applied, as a table of it would not be monotonic.
        byte[] meta = new byte[600];
        meta[0] = 0x44;
        meta[1] = 0x10;
        ushort[] seeds = [10, 20, 30, 40];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(2 + (2 * i)), seeds[i]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(10), 64);
        for (int i = 0; i < 64; i++)
        {
            int value = monotonic || i != 25 ? i * i : 0;
            BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(12 + (2 * i)), (ushort)value);
        }

        BitWriter bits = new();
        for (int i = 0; i < 8; i++)
        {
            bits.Write(0b11110, 5);
        }

        byte[] file = NikonFile(meta, bits.ToArray());
        using MemoryStream dng = new();
        RawFormat.ConvertToDng(new MemoryStream(file), dng, null, Context);

        using Raster<Gray16> source = Raster.Load<Gray16>(file, Context, Mosaic);
        using Raster<Gray16> stored = Raster.Load<Gray16>(dng.ToArray(), Context, Mosaic);
        Assert.Equal([100, 400, 100, 400, 900, 1600, 900, 1600], Values(source));
        Assert.Equal(monotonic ? [10, 20, 10, 20, 30, 40, 30, 40] : Values(source), Values(stored));

        static int[] Values(Raster<Gray16> r) => [.. Enumerable.Range(0, r.Height).SelectMany(y => r.Row(y).ToArray().Select(p => (int)p.L))];
    }

    [Fact]
    public void LosslessJpegHuffmanTableWithTooManyCodesIsInvalid()
    {
        // Fuzz finding: three codes of length 1 overran the table's lookup.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC4, 0, 23, 0x00, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0xFF, 0xD9];
        Assert.Throws<InvalidDataException>(() => LosslessJpeg.Open(jpeg, 0, jpeg.Length));
    }

    [Fact]
    public void MakerNotesLargerThanTheFirstReadAreRead()
    {
        // Gate finding: an IFD entry whose count was larger than the bytes read so far was dropped, so the E-M10's 1.4 MB maker
        // note (and its white balance) went missing in a streamed load. Here the note's metadata block is padded to 300 KB.
        byte[] meta = new byte[300_000];
        meta[0] = 0x46;
        meta[1] = 0x30;
        BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(2), 100);
        BitWriter bits = new();
        for (int i = 0; i < 8; i++)
        {
            bits.Write(0b11110, 5);
        }

        using MemoryStream stream = new(NikonFile(meta, bits.ToArray()));
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(stream, Context, Mosaic);
        Assert.Equal(100, mosaic.Row(0)[0].L);
    }

    [Theory]
    [InlineData(new byte[] { 0, 2, 0, 2, 2, 1, 1, 0 }, "BGGR")]
    [InlineData(new byte[] { 2, 0, 2, 0, 0, 1, 1, 2 }, "RGGB")]
    [InlineData(new byte[] { 0, 3, 0, 2, 2, 1, 1, 0 }, null)]
    public void ExifCfaPatternGivesPentaxsPattern(byte[] value, string? expected)
    {
        // Gate finding: the K-5's PEF is BGGR, which only EXIF's CFAPattern (0xA302) says.
        TiffBuilder.Ifd ifd = new();
        ifd.Tags.Add(TiffTag.Undefined(0xA302, value));
        byte[] tiff = TiffBuilder.Write(ifd);
        RawIfd? exif = new RawTiff(new RawSource(tiff, tiff.Length), false).ReadIfd(8);
        Assert.Equal(expected, MakerParser.ExifCfa(exif)?.Name);
    }

    [Fact]
    public void IfdOffsetsBeyondAnArrayAreNoIfd()
    {
        // Fuzz finding: a SubIFDs value read as long.MaxValue overflowed the IFD reader's bounds check.
        byte[] tiff = TiffBuilder.Write(new TiffBuilder.Ifd { Tags = { TiffTag.Long(256, 1) } });
        RawTiff file = new(new RawSource(tiff, tiff.Length), false);
        Assert.Null(file.ReadIfd(long.MaxValue));
        Assert.Null(file.ReadIfd(uint.MaxValue));
        Assert.NotNull(file.ReadIfd(8));
    }

    [Fact]
    public void EntriesASmallFileOnlyClaimsRentNoMoreThanItHolds()
    {
        // Review finding: an IFD entry that claims 1.5 GB in a 200 KB file made the streamed reader rent the whole claim
        // before finding the file short. (A file within the first 64 KB read is safe: that read reaches its end.)
        DngFile dng = new DngFile { Width = 16, Height = 16 }.Fill((_, _, _) => 20000);
        dng.ExtraIfd0.Add(TiffTag.Undefined(0xC5FF, new byte[8]));
        byte[] file = dng.Build();
        int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at));
        int entry = Enumerable.Range(0, count).Select(i => at + 2 + (i * 12)).First(e => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(e)) == 0xC5FF);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 4), 1_500_000_000);
        Array.Resize(ref file, 200_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        // A stream that is not a MemoryStream, so the reader streams it rather than reading the array in place.
        using (BufferedStream stream = new(new MemoryStream(file)))
        {
            Assert.Equal(16, Raster.Probe(stream, Context).Width);
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64L << 20, "the reader rented the entry's claimed size");
    }

    [Theory]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(0x7FFFFFFEu)]
    public void HeadersWithFarIfdOffsetsAreNotRaw(uint first)
    {
        // Fuzz finding: an IFD offset near 4 GB wrapped the header sniffing's arithmetic.
        byte[] header = new byte[64];
        "II*\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), first);
        Assert.False(RawDetect.Matches(header));
    }

    /// <summary>A sensor-like test scene: smooth gradients, an edge and some noise, within <paramref name="bits"/>.</summary>
    private static ushort[] Scene(int width, int height, int bits)
    {
        int max = (1 << bits) - 1;
        Random random = new(width + height + bits);
        ushort[] s = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double v = (0.2 + (0.5 * x / width) + (x > width / 2 ? 0.2 : 0)) * max;
                s[(y * width) + x] = (ushort)Math.Clamp((int)(v + random.Next(-40, 41)), 0, max);
            }
        }

        s[3] = 0;
        s[4] = (ushort)max;
        return s;
    }

    /// <summary>A 4 × 2 NEF: a Nikon maker note with tag 0x0096 (<paramref name="meta"/>) and Huffman-coded <paramref name="data"/>.</summary>
    private static byte[] NikonFile(byte[] meta, byte[] data)
    {
        TiffBuilder.Ifd raw = new();
        raw.Tags.AddRange([TiffTag.Long(254, 0), TiffTag.Long(256, 4), TiffTag.Long(257, 2), TiffTag.Short(258, 12), TiffTag.Short(259, 34713),
            TiffTag.Short(262, 32803), TiffTag.Short(277, 1), TiffTag.Long(278, 2), TiffTag.Short(33421, 2, 2), TiffTag.Bytes(33422, 0, 1, 1, 2)]);
        raw.Units.Add(data);

        byte[] note = [.. "Nikon\0\x02\x10\0\0"u8, .. NikonNoteTiff(meta)];
        TiffBuilder.Ifd exif = new();
        exif.Tags.Add(TiffTag.Undefined(0x927C, note));
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "NIKON CORPORATION"), TiffTag.Ascii(272, "NIKON TEST"), TiffTag.Long(256, 4), TiffTag.Long(257, 2)]);
        ifd0.SubIfds.Add(raw);
        ifd0.Exif = exif;
        return TiffBuilder.Write(ifd0);
    }

    /// <summary>The maker note's own TIFF (offsets from its start) with tag 0x0096.</summary>
    private static byte[] NikonNoteTiff(byte[] meta)
    {
        TiffBuilder.Ifd note = new();
        note.Tags.Add(TiffTag.Undefined(0x0096, meta));
        return TiffBuilder.Write(note);
    }

    /// <summary>A RAF: header, meta directory with the X-Trans cells (stored last first) and a CFA-section TIFF.</summary>
    private static byte[] Raf(int width, int height, int bits, bool xtrans, byte[] data, byte[]? pattern = null, int countExtra = 0)
    {
        List<byte> meta = [0, 0, 0, 0];
        int records = 0;
        void Record(int tag, byte[] value)
        {
            meta.AddRange([(byte)(tag >> 8), (byte)tag, (byte)(value.Length >> 8), (byte)value.Length, .. value]);
            records++;
        }

        Record(0x0100, [(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        if (xtrans)
        {
            Record(0x0131, [.. (pattern ?? FujiDecoder.XTrans).Reverse()]);
        }

        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meta), (uint)records);

        // The CFA section: IFD0 with 0xF000 (type IFD) pointing at the raw IFD; the strip follows at 128.
        byte[] cfa = new byte[128 + data.Length];
        "II*\0"u8.CopyTo(cfa);
        BinaryPrimitives.WriteUInt32LittleEndian(cfa.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(cfa.AsSpan(8), 1);
        Entry(cfa, 10, 0xF000, 13, 26);
        int at = 26;
        BinaryPrimitives.WriteUInt16LittleEndian(cfa.AsSpan(at), 5);
        Entry(cfa, at + 2, 0xF001, 4, width);
        Entry(cfa, at + 14, 0xF002, 4, height);
        Entry(cfa, at + 26, 0xF003, 4, bits);
        Entry(cfa, at + 38, 0xF007, 4, 128);
        Entry(cfa, at + 50, 0xF008, 4, data.Length + countExtra);
        data.CopyTo(cfa, 128);

        byte[] header = new byte[108];
        "FUJIFILMCCD-RAW "u8.CopyTo(header);
        "X-TEST"u8.CopyTo(header.AsSpan(28));
        "0100"u8.CopyTo(header.AsSpan(60));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(92), 108);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(96), (uint)meta.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(100), (uint)(108 + meta.Count));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(104), (uint)cfa.Length);
        return [.. header, .. meta, .. cfa];
    }

    private static void Entry(byte[] b, int at, int tag, int type, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), (uint)value);
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _count;

        public void Write(long value, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_count == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = _count = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            List<byte> bytes = [.. _bytes];
            if (_count > 0)
            {
                bytes.Add((byte)(_current << (8 - _count)));
            }

            return [.. bytes, 0, 0, 0, 0];
        }
    }

    /// <summary>
    /// A lossless Fujifilm compressed encoder for tests, the specification's section 10: each cell, in the decoder's order,
    /// gets the decoder's prediction and context, and the residual is written as the decoder will read it.
    /// </summary>
    private static class FujiEncoder
    {
        private const int Block = 768;
        private const int R0 = 0, G0 = 5, B0 = 13;
        private static readonly (int A, int B)[] Passes = [(R0 + 2, G0 + 2), (G0 + 3, B0 + 2), (R0 + 3, G0 + 4), (G0 + 5, B0 + 3), (R0 + 4, G0 + 6), (G0 + 7, B0 + 4)];

        public static byte[] Encode(ushort[] samples, int width, int height, bool xtrans, int bits)
        {
            int blocks = (width + Block - 1) / Block, lines = height / 6;
            List<byte[]> strips = [];
            for (int b = 0; b < blocks; b++)
            {
                strips.Add(Strip(samples, width, b, lines, xtrans, bits));
            }

            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(header, 0x4953);
            header[2] = 1;
            header[3] = (byte)(xtrans ? 16 : 0);
            header[4] = (byte)bits;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5), (ushort)height);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(7), (ushort)(blocks * Block));
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(9), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(11), Block);
            header[13] = (byte)blocks;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)lines);
            byte[] table = new byte[((4 * blocks) + 15) & ~15];
            for (int b = 0; b < blocks; b++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 * b), (uint)strips[b].Length);
            }

            return [.. header, .. table, .. strips.SelectMany(s => s)];
        }

        private static byte[] Strip(ushort[] samples, int width, int strip, int lines, bool xtrans, int bits)
        {
            int w = xtrans ? 512 : 384, stride = w + 2, max = (1 << bits) - 1, total = max + 1;
            int maxBits = 4 * (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max)), rawBits = bits;
            int[] q = new int[(2 * max) + 1];
            for (int v = -max; v <= max; v++)
            {
                q[v + max] = v <= -276 ? -4 : v <= -67 ? -3 : v <= -18 ? -2 : v < 0 ? -1 : v == 0 ? 0 : v < 18 ? 1 : v < 67 ? 2 : v < 276 ? 3 : 4;
            }

            int[] cells = new int[18 * stride];
            int[] a = new int[6 * 41], n = new int[6 * 41];
            Array.Fill(a, Math.Max(2, (total + 32) >> 6));
            Array.Fill(n, 1);
            int?[] target = new int?[18 * w];
            BitWriter writer = new();
            int Get(int line, int p) => cells[(line * stride) + p + 1];
            void Set(int line, int p, int v) => cells[(line * stride) + p + 1] = v;
            for (int line = 0; line < lines; line++)
            {
                // Which sample each cell of this line's colour lines holds (the decoder's copy, inverted).
                Array.Clear(target);
                for (int r = 0; r < 6; r++)
                {
                    for (int x = 0; x < Block; x++)
                    {
                        int gx = (strip * Block) + x;
                        int colour = xtrans ? FujiDecoder.XTrans[(r * 6) + (x % 6)] : (((r & 1) << 1) | (x & 1)) switch { 0 => 0, 3 => 2, _ => 1 };
                        int source = colour switch { 0 => R0 + 2 + (r / 2), 1 => G0 + 2 + r, _ => B0 + 2 + (r / 2) };
                        int idx = xtrans ? (2 * (x / 3)) + (x % 3 == 0 ? 0 : 1) : x / 2;
                        target[(source * w) + idx] = gx < width ? samples[(((line * 6) + r) * width) + gx] : 0;
                    }
                }

                for (int k = 0; k < 6; k++)
                {
                    (int la, int lb) = Passes[k];
                    foreach (int l in new[] { la, lb })
                    {
                        Set(l, -1, Get(l - 1, 0));
                        Set(l, w, Get(l - 1, w - 1));
                    }

                    for (int i = 0; i < (w / 2) + 4; i++)
                    {
                        if (i < w / 2)
                        {
                            Cell(la, 2 * i, k);
                            Cell(lb, 2 * i, k);
                        }

                        if (i >= 4 && i - 4 < w / 2)
                        {
                            Cell(la, (2 * (i - 4)) + 1, k);
                            Cell(lb, (2 * (i - 4)) + 1, k);
                        }
                    }
                }

                foreach ((int from, int to) in new[] { (R0 + 3, R0), (R0 + 4, R0 + 1), (G0 + 6, G0), (G0 + 7, G0 + 1), (B0 + 3, B0), (B0 + 4, B0 + 1) })
                {
                    Array.Copy(cells, from * stride, cells, to * stride, stride);
                }
            }

            return writer.ToArray();

            void Cell(int line, int p, int k)
            {
                int nn = Get(line - 1, p), nw = Get(line - 1, p - 1), ne = Get(line - 1, p + 1), n2 = Get(line - 2, p);
                int pred, v1, v2;
                bool even = (p & 1) == 0;
                if (even)
                {
                    int dnw = Math.Abs(nw - nn), dnn = Math.Abs(n2 - nn), dne = Math.Abs(ne - nn);
                    int s = dnw > dnn && dnw > dne ? n2 + ne + (2 * nn) : dne > dnw && dne > dnn ? n2 + nw + (2 * nn) : ne + nw + (2 * nn);
                    pred = s >> 2;
                    v1 = nn - n2;
                    v2 = nw - nn;
                    if (target[(line * w) + p] is null)
                    {
                        Assert.True(xtrans, "Only X-Trans has predictor-only cells.");
                        Set(line, p, pred);
                        return;
                    }
                }
                else
                {
                    int wc = Get(line, p - 1), ec = Get(line, p + 1);
                    pred = (nn > nw && nn > ne) || (nn < nw && nn < ne) ? (wc + ec + (2 * nn)) >> 2 : (wc + ec) >> 1;
                    v1 = nn - nw;
                    v2 = nw - wc;
                }

                int value = target[(line * w) + p] ?? throw new InvalidOperationException("An odd cell has no sample.");
                int g = (9 * q[v1 + max]) + q[v2 + max];
                int ctx = ((even ? 0 : 3) + (k % 3)) * 41 + Math.Abs(g);
                int e = g >= 0 ? value - pred : pred - value;
                if (e < -(total / 2))
                {
                    e += total;
                }
                else if (e >= total / 2)
                {
                    e -= total;
                }

                int code = e >= 0 ? 2 * e : (-2 * e) - 1;
                int kk = 0;
                while (kk < 15 && n[ctx] << kk < a[ctx])
                {
                    kk++;
                }

                int threshold = maxBits - rawBits - 1;
                if (code >> kk < threshold)
                {
                    writer.Write(0, code >> kk);
                    writer.Write(1, 1);
                    writer.Write(code & ((1 << kk) - 1), kk);
                }
                else
                {
                    writer.Write(0, threshold);
                    writer.Write(1, 1);
                    writer.Write(code - 1, rawBits);
                }

                a[ctx] += Math.Abs(e);
                if (n[ctx] == 64)
                {
                    a[ctx] >>= 1;
                    n[ctx] >>= 1;
                }

                n[ctx]++;
                Set(line, p, value);
            }
        }
    }
}
