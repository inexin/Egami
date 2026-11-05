using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Extended.Dicom;
using Egami.Formats.Extended.JpegLs;
using Egami.Formats.Extended.Tests.JpegLs;
using Egami.Formats.Jpeg;
using Egami.Formats.Jpeg2000;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dicom;

/// <summary>
/// DICOM pixel data (PS3.5 section 8 and annexes A.4 and G) and the display pipeline (PS3.3 C.11), against files written by
/// hand with <see cref="DicomImageBuilder"/> and expected values worked out from the standard's formulas.
/// </summary>
public class DicomPixelTests
{
    private const string Implicit = "1.2.840.10008.1.2";
    private const string Explicit = "1.2.840.10008.1.2.1";
    private const string BigEndian = "1.2.840.10008.1.2.2";
    private const string Rle = "1.2.840.10008.1.2.5";
    private const string JpegLsLossless = "1.2.840.10008.1.2.4.80";
    private const string JpegBaseline = "1.2.840.10008.1.2.4.50";
    private const string JpegLossless = "1.2.840.10008.1.2.4.70";
    private const string Jpeg2000Lossless = "1.2.840.10008.1.2.4.90";
    private const string JpegXl = "1.2.840.10008.1.2.4.110";
    private const string DeflatedFrames = "1.2.840.10008.1.2.8.1";
    private const string EncapsulatedNative = "1.2.840.10008.1.2.1.98";

    private static readonly EgamiContext Context = new EgamiContext().WithDicom();

    private static readonly DicomReaderOptions Stored = new() { Values = DicomValues.Stored };

    private static readonly int[] FourLevels = [0, 1, 2, 3];

    private static readonly int[] LowLevels = [40, 50, 60];

    private static readonly int[] HighLevels = [140, 150, 160];

    private static string Images => Path.Combine(AppContext.BaseDirectory, "images");

    [Fact]
    public void StretchesTheFrameRangeWhenThereIsNoWindow()
    {
        DicomImageBuilder image = new DicomImageBuilder(2, 2).Native16(100, 200, 300, 400);
        using Raster raster = Raster.Load(image.File(Explicit), Context);
        Assert.Equal([0, 21845, 43690, 65535], Gray16Values(raster));
        DicomDetails details = Assert.IsType<DicomDetails>(raster.Metadata.FormatDetails);
        Assert.True(details.UsedMinMax);
        Assert.False(details.Inverted);
        Assert.Equal("MONOCHROME2", details.PhotometricInterpretation);
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(15)]
    public void TakesTheStoredBitsFromUnderTheHighBitWithTheirSign(int highBit)
    {
        int[] values = [-1, -2048, 2047, 1, 0, -7];
        int shift = highBit - 11;

        // Bits outside the stored ones are set, as overlays in the unused bits once were.
        ushort[] words = [.. values.Select(v => (ushort)((((v & 0xFFF) << shift) | (shift > 0 ? 1 : 0)) | (highBit < 15 ? 0x8000 : 0)))];
        DicomImageBuilder image = new DicomImageBuilder(2, 3, 16, 12, highBit, signed: true).Native16(words);
        using Raster raster = Raster.Load(image.File(Explicit), Context, Stored);
        Assert.Equal([.. values.Select(v => (float)v)], FloatValues(raster));
    }

    [Fact]
    public void GivesModalityValuesThroughTheRescale()
    {
        DicomImageBuilder image = new DicomImageBuilder(1, 4)
            .Text(0x0028, 0x1052, "DS", "-1024")
            .Text(0x0028, 0x1053, "DS", "2")
            .Native16(0, 1, 1024, 2000);
        using Raster raster = Raster.Load(image.File(Explicit), Context, new DicomReaderOptions { Values = DicomValues.Modality });
        Assert.Equal([-1024f, -1022f, 1024f, 2976f], FloatValues(raster));
        DicomDetails details = Assert.IsType<DicomDetails>(raster.Metadata.FormatDetails);
        Assert.Equal((2.0, -1024.0), (details.RescaleSlope, details.RescaleIntercept));
    }

    [Theory]
    [InlineData("LINEAR", 100, 101)]
    [InlineData("LINEAR", 40, 1)]
    [InlineData("LINEAR_EXACT", 100, 51)]
    [InlineData("SIGMOID", 128, 64)]
    public void AppliesTheFilesWindow(string function, double center, double width)
    {
        byte[] stored = [0, 39, 40, 50, 99, 100, 101, 125, 150, 200, 255, 77];
        DicomImageBuilder image = new DicomImageBuilder(2, 6, 8)
            .Text(0x0028, 0x1050, "DS", center.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Text(0x0028, 0x1051, "DS", width.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Text(0x0028, 0x1056, "CS", function)
            .Native(stored, "OB");
        using Raster raster = Raster.Load(image.File(Explicit), Context);
        Assert.Equal([.. stored.Select(x => Window(function, center, width, x, 255))], Gray8Values(raster));
        DicomDetails details = Assert.IsType<DicomDetails>(raster.Metadata.FormatDetails);
        Assert.Equal((center, width), (details.WindowCenter, details.WindowWidth));
    }

    [Fact]
    public void UsesTheWindowAskedForWithTheModalityValues()
    {
        DicomImageBuilder image = new DicomImageBuilder(1, 5, 16, 12)
            .Text(0x0028, 0x1050, "DS", "40\\400")
            .Text(0x0028, 0x1051, "DS", "80\\2000")
            .Text(0x0028, 0x1052, "DS", "-1024")
            .Text(0x0028, 0x1053, "DS", "1")
            .Native16(0, 1000, 1024, 1064, 4095);
        double[] hu = [-1024, -24, 0, 40, 3071];
        using Raster first = Raster.Load(image.File(Explicit), Context);
        Assert.Equal([.. hu.Select(x => Window("LINEAR", 40, 80, x, 65535))], Gray16Values(first));
        using Raster second = Raster.Load(image.File(Explicit), Context, new DicomReaderOptions { VoiIndex = 1 });
        Assert.Equal([.. hu.Select(x => Window("LINEAR", 400, 2000, x, 65535))], Gray16Values(second));
        using Raster asked = Raster.Load(image.File(Explicit), Context,
            new DicomReaderOptions { WindowCenter = 0, WindowWidth = 100, WindowFunction = DicomVoiFunction.LinearExact });
        Assert.Equal([.. hu.Select(x => Window("LINEAR_EXACT", 0, 100, x, 65535))], Gray16Values(asked));
        Assert.Throws<ArgumentException>(() => Raster.Load(image.File(Explicit), Context, new DicomReaderOptions { WindowCenter = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DicomReaderOptions { WindowWidth = 0 });
    }

    [Theory]
    [InlineData("MONOCHROME1", null, true)]
    [InlineData("MONOCHROME1", "IDENTITY", false)]
    [InlineData("MONOCHROME2", "INVERSE", true)]
    [InlineData("MONOCHROME2", null, false)]
    public void InvertsMonochrome1AndInversePresentationShapes(string photometric, string? shape, bool inverted)
    {
        DicomImageBuilder image = new DicomImageBuilder(1, 3, 8, photometric: photometric).Native([0, 100, 200], "OB");
        if (shape is not null)
        {
            image.Text(0x2050, 0x0020, "CS", shape);
        }

        using Raster raster = Raster.Load(image.File(Explicit), Context);
        int[] plain = [0, 128, 255];
        Assert.Equal(inverted ? [.. plain.Select(v => 255 - v)] : plain, Gray8Values(raster));
        Assert.Equal(inverted, Assert.IsType<DicomDetails>(raster.Metadata.FormatDetails).Inverted);
    }

    [Fact]
    public void AppliesTheVoiLutUnlessTheWindowIsPreferred()
    {
        DicomImageBuilder image = new DicomImageBuilder(1, 4, 8)
            .Text(0x0028, 0x1050, "DS", "2")
            .Text(0x0028, 0x1051, "DS", "4")
            .Add(0x0028, 0x3010, b => b.Sequence(0x0028, 0x3010, false, false, item => item
                .UInt16(0x0028, 0x3002, "US", 4, 0, 8)
                .UInt16(0x0028, 0x3006, "US", 10, 20, 30, 255)))
            .Native([0, 1, 2, 3], "OB");
        using Raster lut = Raster.Load(image.File(Explicit), Context);
        Assert.Equal([10, 20, 30, 255], Gray8Values(lut));
        Assert.True(Assert.IsType<DicomDetails>(lut.Metadata.FormatDetails).UsedVoiLut);
        using Raster window = Raster.Load(image.File(Explicit), Context, new DicomReaderOptions { PreferWindow = true });
        Assert.Equal([.. FourLevels.Select(x => Window("LINEAR", 2, 4, x, 255))], Gray8Values(window));
    }

    [Fact]
    public void GivesModalityValuesThroughTheModalityLut()
    {
        DicomImageBuilder image = new DicomImageBuilder(1, 6, 8)
            .Text(0x0028, 0x1052, "DS", "5")
            .Add(0x0028, 0x3000, b => b.Sequence(0x0028, 0x3000, true, true, item => item
                .UInt16(0x0028, 0x3002, "US", 4, 1, 16)
                .UInt16(0x0028, 0x3006, "US", 0, 1000, 2000, 3000)))
            .Native([0, 1, 2, 3, 4, 9], "OB");
        using Raster raster = Raster.Load(image.File(Explicit), Context, new DicomReaderOptions { Values = DicomValues.Modality });

        // Inputs below the first entry take the first; past the last, the last.
        Assert.Equal([0f, 0f, 1000f, 2000f, 3000f, 3000f], FloatValues(raster));
        Assert.True(Assert.IsType<DicomDetails>(raster.Metadata.FormatDetails).UsedModalityLut);
    }

    [Fact]
    public void ReadsBitPackedFramesThatDoNotEndOnAByte()
    {
        // Three frames of 3 × 3 one-bit pixels: 27 bits, the first pixel in the lowest bit of the first byte.
        bool[][] frames = [[true, false, true, false, true, false, true, false, true], [false, false, false, true, true, true, false, false, true], [true, true, false, false, true, true, false, false, false]];
        bool[] bits = [.. frames.SelectMany(f => f)];
        byte[] packed = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                packed[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        DicomImageBuilder image = new DicomImageBuilder(3, 3, 1, frames: 3).Native(packed, "OB");
        byte[] file = image.File(Explicit);
        Assert.Equal(3, Raster.Probe(file, Context).ImageCount);
        using RasterList all = Raster.LoadAll(file, Context, Stored);
        Assert.Equal(3, all.Count);
        for (int f = 0; f < 3; f++)
        {
            Assert.Equal([.. frames[f].Select(b => b ? 1f : 0f)], FloatValues(all[f]));
        }
    }

    [Fact]
    public void ReadsPlanarAndInterleavedColourTheSame()
    {
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
        byte[] planar = [.. Enumerable.Range(0, 3).SelectMany(c => Enumerable.Range(0, 4).Select(p => rgb[(p * 3) + c]))];
        using Raster a = Raster.Load(new DicomImageBuilder(2, 2, 8, samples: 3, photometric: "RGB").Native(rgb, "OB").File(Explicit), Context);
        using Raster b = Raster.Load(new DicomImageBuilder(2, 2, 8, samples: 3, photometric: "RGB", planar: 1).Native(planar, "OB").File(Explicit), Context);
        Assert.IsType<Raster<Rgb8>>(a);
        Assert.Equal(rgb, Bytes(a));
        Assert.Equal(rgb, Bytes(b));
    }

    [Theory]
    [InlineData("YBR_FULL")]
    [InlineData("YBR_FULL_422")]
    public void ConvertsYbrFullToRgb(string photometric)
    {
        (byte Y, byte Cb, byte Cr)[] ybr = [(128, 128, 128), (76, 85, 255), (150, 44, 21), (29, 255, 107)];
        byte[] data = photometric == "YBR_FULL"
            ? [.. ybr.SelectMany(p => new[] { p.Y, p.Cb, p.Cr })]

            // Two luma samples, then the Cb and Cr both pixels share.
            : [ybr[0].Y, ybr[0].Y, ybr[0].Cb, ybr[0].Cr, ybr[1].Y, ybr[1].Y, ybr[1].Cb, ybr[1].Cr];
        if (photometric != "YBR_FULL")
        {
            ybr = [ybr[0], ybr[0], ybr[1], ybr[1]];
        }

        using Raster raster = Raster.Load(new DicomImageBuilder(2, 2, 8, samples: 3, photometric: photometric).Native(data, "OB").File(Explicit), Context);
        byte[] expected = [.. ybr.SelectMany(p => Rgb(p.Y, p.Cb - 128.0, p.Cr - 128.0))];
        Assert.Equal(expected, Bytes(raster));

        using Raster stored = Raster.Load(new DicomImageBuilder(2, 2, 8, samples: 3, photometric: photometric).Native(data, "OB").File(Explicit), Context, Stored);
        Assert.Equal([.. ybr.SelectMany(p => new[] { p.Y, p.Cb, p.Cr })], Bytes(stored));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void ShowsPaletteColour(int bits)
    {
        ushort top = (ushort)((1 << bits) - 1);
        DicomImageBuilder image = new DicomImageBuilder(1, 4, 8, photometric: "PALETTE COLOR")
            .UInt16Lut(0x1101, [3, 10, (ushort)bits]).UInt16Lut(0x1102, [3, 10, (ushort)bits]).UInt16Lut(0x1103, [3, 10, (ushort)bits])
            .Add(0x0028, 0x1201, b => b.UInt16(0x0028, 0x1201, "OW", top, 0, 0))
            .Add(0x0028, 0x1202, b => b.UInt16(0x0028, 0x1202, "OW", 0, top, 0))
            .Add(0x0028, 0x1203, b => b.UInt16(0x0028, 0x1203, "OW", 0, 0, top))
            .Native([10, 11, 12, 200], "OB");
        using Raster raster = Raster.Load(image.File(Explicit), Context);
        if (bits == 8)
        {
            Assert.IsType<Raster<Rgb8>>(raster);
            Assert.Equal([255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255], Bytes(raster));
        }
        else
        {
            Assert.IsType<Raster<Rgb16>>(raster);
            Assert.Equal([65535, 0, 0, 0, 65535, 0, 0, 0, 65535, 0, 0, 65535], MemoryMarshal.Cast<byte, ushort>(Bytes(raster)).ToArray());
        }

        using Raster indices = Raster.Load(image.File(Explicit), Context, Stored);
        Assert.Equal([10f, 11f, 12f, 200f], FloatValues(indices));
    }

    [Fact]
    public void ExpandsSegmentedPalettes()
    {
        // Discrete 0, 1000; linear to 7000 in 6 steps; then an indirect copy of the first segment.
        ushort[] segments = [0, 2, 0, 1000, 1, 6, 7000, 2, 1, 0, 0];
        DicomImageBuilder image = new DicomImageBuilder(1, 10, 8, photometric: "PALETTE COLOR")
            .UInt16Lut(0x1101, [10, 0, 16]).UInt16Lut(0x1102, [10, 0, 16]).UInt16Lut(0x1103, [10, 0, 16])
            .Add(0x0028, 0x1221, b => b.UInt16(0x0028, 0x1221, "OW", segments))
            .Add(0x0028, 0x1222, b => b.UInt16(0x0028, 0x1222, "OW", segments))
            .Add(0x0028, 0x1223, b => b.UInt16(0x0028, 0x1223, "OW", segments))
            .Native([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], "OB");
        using Raster raster = Raster.Load(image.File(Explicit), Context);
        ushort[] red = [.. MemoryMarshal.Cast<byte, ushort>(Bytes(raster)).ToArray().Where((_, i) => i % 3 == 0)];
        Assert.Equal([0, 1000, 2000, 3000, 4000, 5000, 6000, 7000, 0, 1000], red);
    }

    [Theory]
    [InlineData(Implicit)]
    [InlineData(BigEndian)]
    public void ReadsEveryNativeByteOrderTheSame(string syntax)
    {
        ushort[] values = [0, 1, 4095, 2048, 77, 3000];
        DicomImageBuilder image = new DicomImageBuilder(2, 3, 16, 12).Native16(values);
        using Raster raster = Raster.Load(image.File(syntax), Context, Stored);
        Assert.Equal([.. values.Select(v => (float)v)], FloatValues(raster));
    }

    [Theory]
    [InlineData("OW")]
    [InlineData("OB")]
    public void ReadsEightBitSamplesOfBigEndianFilesByTheirVR(string vr)
    {
        // OW is 16-bit words in the file's byte order, so a big-endian file stores 8-bit samples in OW with each pair swapped
        // (pydicom and GDCM read pydicom's SC_rgb_small_odd_big_endian.dcm so); OB is plain bytes. Three RGB pixels, padded to 10.
        byte[] samples = [166, 141, 52, 63, 87, 176, 1, 2, 3];
        byte[] stored = [.. samples, 0];
        if (vr == "OW")
        {
            for (int i = 0; i < stored.Length; i += 2)
            {
                (stored[i], stored[i + 1]) = (stored[i + 1], stored[i]);
            }
        }

        DicomImageBuilder image = new DicomImageBuilder(1, 3, 8, samples: 3, photometric: "RGB").Native(stored, vr);
        using Raster raster = Raster.Load(image.File(BigEndian), Context, Stored);
        Assert.Equal(samples, Bytes(raster));
    }

    [Fact]
    public void ShowsThirtyTwoBitValuesAsUnsigned()
    {
        // Unsigned 32-bit stored values above 2^31 - 1 read as themselves, and 32-bit colour keeps its top 16 bits.
        uint[] gray = [0, 0x80000000, 0xFFFFFFFF, 16];
        byte[] grayData = [.. gray.SelectMany(BitConverter.GetBytes)];
        using (Raster stored = Raster.Load(new DicomImageBuilder(2, 2, 32).Native(grayData, "OW").File(Explicit), Context, Stored))
        {
            Assert.Equal([.. gray.Select(v => (float)v)], FloatValues(stored));
        }

        uint[] rgb = [0xFFFFFFFF, 0x00010000, 0x80000000, 0, 0x0001FFFF, 0xFFFF0000];
        byte[] rgbData = [.. rgb.SelectMany(BitConverter.GetBytes)];
        using Raster colour = Raster.Load(new DicomImageBuilder(1, 2, 32, samples: 3, photometric: "RGB").Native(rgbData, "OW").File(Explicit), Context);
        Assert.Equal([65535, 1, 32768, 0, 1, 65535], MemoryMarshal.Cast<byte, ushort>(Bytes(colour)).ToArray());
    }

    [Fact]
    public void FindsDicomBehindATiffPreamble()
    {
        // PS3.10 section 7.1 lets the preamble hold another format's header; pydicom's MR_small.dcm has a TIFF one ("II* "),
        // which TIFF's signature matched first.
        ushort[] values = [0, 1, 4095, 2048];
        byte[] file = new DicomImageBuilder(2, 2, 16, 12).Native16(values).File(Explicit);
        "II* "u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 525800);
        EgamiContext context = EgamiContext.Default.WithDicom();
        Assert.Equal(DicomFormat.Info, Raster.Probe(file, context).Format);
        using Raster raster = Raster.Load(file, context, Stored);
        Assert.Equal([.. values.Select(v => (float)v)], FloatValues(raster));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    [InlineData(8, 3)]
    [InlineData(32, 1)]
    public void DecodesRleFrames(int bits, int samples)
    {
        Random random = new(bits + samples);
        int rows = 5, columns = 7, frames = 2;
        uint top = bits == 32 ? 0x7FFFFFFF : (1u << bits) - 1;
        uint[][] data = [.. Enumerable.Range(0, frames).Select(f => Enumerable.Range(0, rows * columns * samples)
            .Select(i => i % 5 == 0 ? (uint)random.NextInt64(top + 1L) : (uint)(f * 3)).ToArray())];
        DicomImageBuilder image = new DicomImageBuilder(rows, columns, bits, samples: samples, photometric: samples == 3 ? "RGB" : "MONOCHROME2", frames: frames)
            .Encapsulated([], [.. data.Select(d => DicomImageBuilder.Rle(d, samples, bits / 8))]);
        using RasterList all = Raster.LoadAll(image.File(Rle), Context, Stored);
        for (int f = 0; f < frames; f++)
        {
            if (samples == 1)
            {
                Assert.Equal([.. data[f].Select(v => (float)v)], FloatValues(all[f]));
            }
            else
            {
                Assert.Equal([.. data[f].Select(v => (byte)v)], Bytes(all[f]));
            }
        }
    }

    [Fact]
    public void FindsFramesByTheOffsetTablesOrTheirCodestreams()
    {
        int rows = 6, columns = 9;
        ushort[][] frames = [.. Enumerable.Range(0, 3).Select(f => Enumerable.Range(0, rows * columns).Select(i => (ushort)((i * 37) + (f * 1000))).ToArray())];
        byte[][] codestreams = [.. frames.Select(f => JpegLsTesting.Encode(f, 1, columns, 12))];

        // Each codestream split over two fragments.
        byte[][] fragments = [.. codestreams.SelectMany(c => new[] { c[..(c.Length / 2 & ~1)], c[(c.Length / 2 & ~1)..] })];
        uint[] basic = new uint[3];
        uint at = 0;
        for (int i = 0; i < fragments.Length; i++)
        {
            if (i % 2 == 0)
            {
                basic[i / 2] = at;
            }

            at += 8 + (uint)(fragments[i].Length + (fragments[i].Length % 2));
        }

        foreach (uint[] offsets in (uint[][])[basic, []])
        {
            DicomImageBuilder image = new DicomImageBuilder(rows, columns, 16, 12, frames: 3).Encapsulated(offsets, fragments);
            using RasterList all = Raster.LoadAll(image.File(JpegLsLossless), Context, Stored);
            for (int f = 0; f < 3; f++)
            {
                Assert.Equal([.. frames[f].Select(v => (float)v)], FloatValues(all[f]));
            }
        }

        // The Extended Offset Table, with lengths that leave out a fragment's padding.
        DicomImageBuilder extended = new DicomImageBuilder(rows, columns, 16, 12, frames: 3)
            .Add(0x7FE0, 0x0001, b => b.Bytes(0x7FE0, 0x0001, "OV", [.. basic.SelectMany(o => BitConverter.GetBytes((ulong)o))]))
            .Add(0x7FE0, 0x0002, b => b.Bytes(0x7FE0, 0x0002, "OV", [.. codestreams.SelectMany(c => BitConverter.GetBytes((ulong)c.Length))]))
            .Encapsulated([], fragments);
        DicomFile file = DicomFile.Load(extended.File(JpegLsLossless));
        Assert.Equal(3, file.FrameCount);
        using Raster second = file.LoadFrame(1, Stored);
        Assert.Equal([.. frames[1].Select(v => (float)v)], FloatValues(second));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.LoadFrame(3));
    }

    [Fact]
    public void SignExtendsUnsignedCodestreamsOfSignedData()
    {
        int[] values = [-2048, -1, 0, 1, 2047, -1000];
        ushort[] bits = [.. values.Select(v => (ushort)(v & 0xFFF))];
        DicomImageBuilder image = new DicomImageBuilder(2, 3, 16, 12, signed: true).Encapsulated([], JpegLsTesting.Encode(bits, 1, 3, 12));
        using Raster raster = Raster.Load(image.File(JpegLsLossless), Context, Stored);
        Assert.Equal([.. values.Select(v => (float)v)], FloatValues(raster));

        using MemoryStream lossless = new();
        JpegLosslessWriter.Write(lossless, bits, 1, 3, 12, 1);
        DicomImageBuilder jpeg = new DicomImageBuilder(2, 3, 16, 12, signed: true).Encapsulated([], lossless.ToArray());
        using Raster fromJpeg = Raster.Load(jpeg.File(JpegLossless), Context, Stored);
        Assert.Equal([.. values.Select(v => (float)v)], FloatValues(fromJpeg));
    }

    [Theory]
    [InlineData(12, 12, false, null, "MONOCHROME2")]
    [InlineData(12, 12, true, null, "MONOCHROME2")]
    [InlineData(12, 12, true, "40/400", "MONOCHROME1")]
    [InlineData(16, 16, false, "30000/20000", "MONOCHROME2")]
    [InlineData(8, 8, false, null, "MONOCHROME2")]
    [InlineData(12, 16, false, null, "MONOCHROME2")]
    [InlineData(12, 16, true, "40/400", "MONOCHROME2")]
    [InlineData(12, 14, false, "2000/1000", "MONOCHROME1")]
    public void ShowsGrayJpegFramesAsTheRowByRowPathDoes(int bits, int precision, bool isSigned, string? window, string photometric)
    {
        // A codestream may be deeper than Bits Stored: GDCM codes 12-bit data as 16-bit lossless JPEG.
        const int Columns = 37, Rows = 23;
        ushort[] values = [.. Enumerable.Range(0, Columns * Rows).Select(i => (ushort)((i * 2654435761u >> 7) & ((1u << bits) - 1)))];
        using MemoryStream lossless = new();
        JpegLosslessWriter.Write(lossless, values, 1, Columns, precision, 1);
        DicomImageBuilder image = new DicomImageBuilder(Rows, Columns, bits > 8 ? 16 : 8, bits, signed: isSigned, photometric: photometric)
            .Text(0x0028, 0x1052, "DS", "-1024").Text(0x0028, 0x1053, "DS", "2");
        if (window is not null)
        {
            string[] parts = window.Split('/');
            image.Text(0x0028, 0x1050, "DS", parts[0]).Text(0x0028, 0x1051, "DS", parts[1]);
        }

        byte[] file = image.Encapsulated([], lossless.ToArray()).File(JpegLossless);
        using Raster whole = Raster.Load(file, Context);
        using Raster rows = DicomFile.Load(file).LoadFrame(0, null, Context);
        Assert.Equal(rows.GetType(), whole.GetType());
        Assert.Equal(Bytes(rows), Bytes(whole));
        DicomDetails details = Assert.IsType<DicomDetails>(whole.Metadata.FormatDetails);
        Assert.Equal(window is null, details.UsedMinMax);
        Assert.Equal("DICOM", whole.Metadata.FormatName);
    }

    [Theory]
    [InlineData("jls", 12, false, null)]
    [InlineData("jls", 12, true, "40/400")]
    [InlineData("jls", 8, false, "100/50")]
    [InlineData("jpeg2000/gray12-lossless.j2k", 12, false, null)]
    [InlineData("jpeg2000/gray12-lossless.j2k", 12, true, "40/400")]
    [InlineData("htj2k/gray12-lossless.j2c", 12, false, "2000/1000")]
    [InlineData("htj2k/gray16-lossless.jph", 16, false, null)]
    public void ShowsGrayJpegLsAndJpeg2000FramesAsTheRowByRowPathDoes(string codestream, int bits, bool isSigned, string? window)
    {
        byte[] data;
        int columns, rows;
        if (codestream == "jls")
        {
            columns = 37;
            rows = 23;
            ushort[] values = [.. Enumerable.Range(0, columns * rows).Select(i => (ushort)((i * 2654435761u >> 7) & ((1u << bits) - 1)))];
            data = JpegLsTesting.Encode(values, 1, columns, bits);
        }
        else
        {
            data = File.ReadAllBytes(Path.Combine(Images, codestream));
            var probe = Raster.Probe(data, Context);
            (columns, rows) = (probe.Width, probe.Height);
        }

        DicomImageBuilder image = new DicomImageBuilder(rows, columns, bits > 8 ? 16 : 8, bits, signed: isSigned);
        if (window is not null)
        {
            string[] parts = window.Split('/');
            image.Text(0x0028, 0x1050, "DS", parts[0]).Text(0x0028, 0x1051, "DS", parts[1]);
        }

        byte[] file = image.Encapsulated([], data).File(codestream == "jls" ? JpegLsLossless : Jpeg2000Lossless);
        using Raster whole = Raster.Load(file, Context);
        using Raster byRows = DicomFile.Load(file).LoadFrame(0, null, Context);
        Assert.Equal(byRows.GetType(), whole.GetType());
        Assert.Equal(Bytes(byRows), Bytes(whole));
    }

    [Fact]
    public void DecodesJpegBaselineAsYbrFull422()
    {
        using Raster<Rgb8> photo = Photo(32, 16);
        using MemoryStream jpeg = new();
        photo.Save(jpeg, JpegFormat.Info);
        DicomImageBuilder image = new DicomImageBuilder(16, 32, 8, samples: 3, photometric: "YBR_FULL_422").Encapsulated([], jpeg.ToArray());
        using Raster raster = Raster.Load(image.File(JpegBaseline), Context);
        using Raster<Rgb8> direct = Raster.Load<Rgb8>(jpeg.ToArray());
        byte[] a = Bytes(raster), b = Bytes(direct);
        Assert.Equal(b.Length, a.Length);
        Assert.True(a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 2), "The DICOM load differs from the JPEG load by more than 2 levels.");
    }

    [Fact]
    public void DecodesJpegXlAndJpeg2000()
    {
        using Raster<Rgb8> photo = Photo(24, 12);
        using MemoryStream jxl = new();
        photo.Save(jxl, JxlFormat.Info, new JxlWriterOptions { Lossless = true });
        DicomImageBuilder image = new DicomImageBuilder(12, 24, 8, samples: 3, photometric: "RGB").Encapsulated([], jxl.ToArray());
        using Raster raster = Raster.Load(image.File(JpegXl), Context);
        Assert.Equal(Bytes(photo), Bytes(raster));

        byte[] j2k = File.ReadAllBytes(Path.Combine(Images, "jpeg2000", "gray12-lossless.j2k"));
        SampleImage samples = SampleImage.Load(j2k, Jpeg2000Format.Info);
        DicomImageBuilder wrapped = new DicomImageBuilder(samples.Height, samples.Width, 16, 12).Encapsulated([], j2k);
        using Raster gray = Raster.Load(wrapped.File(Jpeg2000Lossless), Context, Stored);
        Assert.Equal([.. samples.Samples.ToArray().Select(v => (float)v)], FloatValues(gray));
    }

    [Theory]
    [InlineData("1.2.840.10008.1.2.4.201", "gray12-lossless.j2c", 12)]
    [InlineData("1.2.840.10008.1.2.4.203", "gray16-lossless.jph", 16)]
    public void DecodesHighThroughputJpeg2000(string syntax, string name, int bits)
    {
        byte[] ht = File.ReadAllBytes(Path.Combine(Images, "htj2k", name));
        SampleImage samples = SampleImage.Load(ht, Jpeg2000Format.Info);
        DicomImageBuilder wrapped = new DicomImageBuilder(samples.Height, samples.Width, 16, bits).Encapsulated([], ht);
        using Raster gray = Raster.Load(wrapped.File(syntax), Context, Stored);
        Assert.Equal([.. samples.Samples.ToArray().Select(v => (float)v)], FloatValues(gray));
    }

    [Fact]
    public void ReadsDeflatedAndEncapsulatedNativeFrames()
    {
        ushort[][] frames = [[1, 2, 3, 4, 5, 6], [600, 500, 400, 300, 200, 100]];
        byte[][] native = [.. frames.Select(f => DicomImageBuilder.Words(f))];
        byte[][] deflated = [.. native.Select(n =>
        {
            using MemoryStream packed = new();
            using (System.IO.Compression.DeflateStream d = new(packed, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                d.Write(n);
            }

            return packed.ToArray();
        })];
        foreach ((string syntax, byte[][] data) in new[] { (DeflatedFrames, deflated), (EncapsulatedNative, native) })
        {
            DicomImageBuilder image = new DicomImageBuilder(2, 3, frames: 2).Encapsulated([], data);
            using RasterList all = Raster.LoadAll(image.File(syntax), Context, Stored);
            Assert.Equal([.. frames[1].Select(v => (float)v)], FloatValues(all[1]));
        }
    }

    [Fact]
    public void TakesEachFramesWindowFromItsFunctionalGroupsAndTheCineTiming()
    {
        DicomImageBuilder image = new DicomImageBuilder(1, 3, 8, frames: 2)
            .Text(0x0018, 0x1063, "DS", "33.5")
            .Add(0x5200, 0x9229, b => b.Sequence(0x5200, 0x9229, false, false, shared => shared
                .Sequence(0x0028, 0x9145, false, false, t => t.Text(0x0028, 0x1052, "DS", "0").Text(0x0028, 0x1053, "DS", "1"))))
            .Add(0x5200, 0x9230, b => b.Sequence(0x5200, 0x9230, true, true,
                frame => frame.Sequence(0x0028, 0x9132, false, false, voi => voi.Text(0x0028, 0x1050, "DS", "50").Text(0x0028, 0x1051, "DS", "21")),
                frame => frame.Sequence(0x0028, 0x9132, false, false, voi => voi.Text(0x0028, 0x1050, "DS", "150").Text(0x0028, 0x1051, "DS", "41"))))
            .Native([40, 50, 60, 140, 150, 160], "OB");
        using RasterList all = Raster.LoadAll(image.File(Explicit), Context);
        Assert.Equal([.. LowLevels.Select(x => Window("LINEAR", 50, 21, x, 255))], Gray8Values(all[0]));
        Assert.Equal([.. HighLevels.Select(x => Window("LINEAR", 150, 41, x, 255))], Gray8Values(all[1]));
        DicomDetails details = Assert.IsType<DicomDetails>(all[1].Metadata.FormatDetails);
        Assert.Equal((1, 2, TimeSpan.FromMilliseconds(33.5)), (details.Frame, details.FrameCount, details.FrameTime));
    }

    [Fact]
    public void KeepsWhatThereIsOfShortPixelDataUnlessStrict()
    {
        DicomImageBuilder image = new DicomImageBuilder(2, 2).Native(DicomImageBuilder.Words(10, 20, 30));
        using Raster raster = Raster.Load(image.File(Explicit), Context, Stored);
        Assert.Equal([10f, 20f, 30f, 0f], FloatValues(raster));
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Dicom.PixelDataShort");
        Assert.Throws<CorruptDataException>(() => Raster.Load(image.File(Explicit), Context, Stored with { Recovery = RecoveryMode.Strict }));
    }

    [Theory]
    [InlineData("1.2.840.10008.1.2.4.100")]
    [InlineData("1.2.840.10008.1.2.4.94")]
    public void SaysWhichTransferSyntaxesItDoesNotDecode(string syntax)
    {
        DicomImageBuilder image = new DicomImageBuilder(2, 2, 8).Encapsulated([], [0xFF, 0x4F, 0xFF, 0x51]);
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => Raster.Load(image.File(syntax), Context));
        Assert.Contains(syntax, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbesTheLayoutALoadGives()
    {
        (DicomImageBuilder Image, DicomReaderOptions? Options, PixelLayoutInfo Layout)[] cases =
        [
            (new DicomImageBuilder(2, 2, 8).Native([1, 2, 3, 4], "OB"), null, Gray8.Layout),
            (new DicomImageBuilder(2, 2, 16, 12).Native16(1, 2, 3, 4), null, Gray16.Layout),
            (new DicomImageBuilder(2, 2, 16, 12).Native16(1, 2, 3, 4), Stored, GrayF32.Layout),
            (new DicomImageBuilder(1, 2, 8, samples: 3, photometric: "RGB").Native([1, 2, 3, 4, 5, 6], "OB"), null, Rgb8.Layout),
            (new DicomImageBuilder(1, 2, 16, samples: 3, photometric: "RGB").Native16(1, 2, 3, 4, 5, 6), null, Rgb16.Layout),
        ];
        foreach ((DicomImageBuilder image, DicomReaderOptions? options, PixelLayoutInfo layout) in cases)
        {
            byte[] file = image.File(Explicit);
            Assert.Equal(layout, Raster.Probe(file, Context, options).Layout);
            using Raster raster = Raster.Load(file, Context, options);
            Assert.Equal(layout, raster.Layout);
        }
    }

    /// <summary>The VOI functions of PS3.3 C.11.2.1.2, written out here independently of the reader, rounded to a level.</summary>
    private static int Window(string function, double c, double w, double x, double top)
    {
        double y = function switch
        {
            "LINEAR_EXACT" => x <= c - (w / 2) ? 0 : x > c + (w / 2) ? top : (((x - c) / w) + 0.5) * top,
            "SIGMOID" => top / (1 + Math.Exp(-4 * (x - c) / w)),
            _ => w <= 1
                ? (x <= c - 0.5 ? 0 : top)
                : x <= c - 0.5 - ((w - 1) / 2) ? 0 : x > c - 0.5 + ((w - 1) / 2) ? top : ((((x - (c - 0.5)) / (w - 1)) + 0.5) * top),
        };
        return (int)Math.Clamp(Math.Floor(y + 0.5), 0, top);
    }

    /// <summary>Full-range YCbCr to RGB (PS3.3 C.7.6.3.1.2, the JFIF equations).</summary>
    private static byte[] Rgb(double y, double cb, double cr)
    {
        static byte C(double v) => (byte)Math.Clamp(Math.Floor(v + 0.5), 0, 255);
        return [C(y + (1.402 * cr)), C(y - (0.344136 * cb) - (0.714136 * cr)), C(y + (1.772 * cb))];
    }

    private static Raster<Rgb8> Photo(int width, int height)
    {
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                raster.Row(y)[x] = new Rgb8((byte)(x * 8), (byte)(y * 15), (byte)(128 + (32 * Math.Sin(x * 0.3))));
            }
        }

        return raster;
    }

    private static byte[] Bytes(Raster raster)
    {
        byte[] all = new byte[raster.Width * raster.Height * raster.Layout.BytesPerPixel];
        switch (raster)
        {
            case Raster<Gray8> r:
                r.CopyPixelsTo(all);
                break;
            case Raster<Gray16> r:
                r.CopyPixelsTo(all);
                break;
            case Raster<GrayF32> r:
                r.CopyPixelsTo(all);
                break;
            case Raster<Rgb8> r:
                r.CopyPixelsTo(all);
                break;
            case Raster<Rgb16> r:
                r.CopyPixelsTo(all);
                break;
            default:
                throw new InvalidOperationException(raster.GetType().Name);
        }

        return all;
    }

    private static int[] Gray8Values(Raster raster) => [.. Bytes(Assert.IsType<Raster<Gray8>>(raster)).Select(b => (int)b)];

    private static int[] Gray16Values(Raster raster) => [.. MemoryMarshal.Cast<byte, ushort>(Bytes(Assert.IsType<Raster<Gray16>>(raster))).ToArray().Select(v => (int)v)];

    private static float[] FloatValues(Raster raster) => MemoryMarshal.Cast<byte, float>(Bytes(Assert.IsType<Raster<GrayF32>>(raster))).ToArray();
}

/// <summary>Palette descriptors for the tests.</summary>
internal static class DicomImageBuilderExtensions
{
    public static DicomImageBuilder UInt16Lut(this DicomImageBuilder image, ushort element, ushort[] descriptor)
    {
        return image.Add(0x0028, element, b => b.UInt16(0x0028, element, "US", descriptor));
    }
}
