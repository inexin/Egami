using Egami.Formats.Jpeg;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>
/// Lossless JPEG (SOF3) as TIFF compression 7: written at quality 100 for 8 and 16-bit gray, RGB and CMYK in strips and tiles,
/// either byte order, and read back exactly; read at every depth from 2 to 16 bits, against the same samples stored
/// uncompressed.
/// </summary>
public class TiffLosslessJpegTests
{
    private static readonly byte[] Sof0 = [0xFF, 0xC0];
    private static readonly byte[] Sof3 = [0xFF, 0xC3];
    private static readonly byte[] Sos = [0xFF, 0xDA];

    public static TheoryData<string, int, TiffByteOrder> Cases()
    {
        TheoryData<string, int, TiffByteOrder> data = [];
        foreach (string type in (string[])["Gray8", "Gray16", "Rgb8", "Rgb16", "Cmyk8", "Cmyk16"])
        {
            data.Add(type, 0, TiffByteOrder.LittleEndian);
            data.Add(type, 16, TiffByteOrder.LittleEndian);
            data.Add(type, 0, TiffByteOrder.BigEndian);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Quality100WritesLosslessJpegThatRoundTripsExactly(string type, int tile, TiffByteOrder order)
    {
        using Raster source = Make(type);
        source.Metadata.FormatDetails = new TiffDetails { ByteOrder = order };
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100, TileSize = tile });
        using Raster back = Raster.Load(file);
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        bool wide = type.EndsWith("16", StringComparison.Ordinal);
        TiffPhotometric photometric = type[0] switch { 'G' => TiffPhotometric.BlackIsZero, 'R' => TiffPhotometric.Rgb, _ => TiffPhotometric.Separated };
        Assert.Equal((TiffCompression.Jpeg, photometric, wide ? 16 : 8), (details.Compression, details.Photometric, details.BitsPerSample));
        Assert.Equal(source.Layout, back.Layout);
        Assert.Equal(TiffReference.Bytes(source), TiffReference.Bytes(back));
    }

    [Fact]
    public void LosslessUnitsAreWholeSof3Streams()
    {
        using Raster<Rgb16> source = TiffWriterTests.Pattern<Rgb16>(40, 30);
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100, TileSize = 16 });
        using Raster<Rgb16> back = Raster.Load<Rgb16>(file);
        Assert.Equal(TiffReference.Bytes(source), TiffReference.Bytes(back));

        // No shared tables and no YCbCr: each tile carries its own Huffman tables, so a reader decodes it alone.
        Assert.Equal(-1, file.AsSpan().IndexOf("\xFF\xC0"u8.ToArray()));
        Assert.True(file.AsSpan().IndexOf(Sof3) > 0);
    }

    [Fact]
    public void ParallelCompressionWritesTheSameFile()
    {
        using Raster<Rgb16> photo = TiffWriterTests.Pattern<Rgb16>(300, 200);
        foreach (int tile in (int[])[0, 32])
        {
            byte[] one = Save(photo, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100, TileSize = tile });
            byte[] many = Save(photo, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100, TileSize = tile, ParallelCompression = true });
            Assert.Equal(one, many);
        }
    }

    [Fact]
    public void ALosslessSourceStaysLosslessThroughAnEditAndAResave()
    {
        using Raster<Gray16> source = TiffWriterTests.Pattern<Gray16>(64, 48);
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100 });
        using Raster<Gray16> loaded = Raster.Load<Gray16>(file);
        using Raster<Gray16> edited = loaded.Derive(ops => ops.Fit(32, 24), TestContext.Current.CancellationToken);
        using MemoryStream again = new();
        edited.Save(again, TiffFormat.Info);
        using Raster<Gray16> back = Raster.Load<Gray16>(again.ToArray());
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal((TiffCompression.Jpeg, 16), (details.Compression, details.BitsPerSample));
        Assert.Equal(TiffReference.Bytes(edited), TiffReference.Bytes(back));
    }

    [Fact]
    public void OtherQualitiesStayBaseline()
    {
        using Raster<Gray16> source = TiffWriterTests.Pattern<Gray16>(64, 48);
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 99 });
        using Raster back = Raster.Load(file);
        Assert.Equal(8, ((TiffDetails)back.Metadata.FormatDetails!).BitsPerSample);
        Assert.Equal(-1, file.AsSpan().IndexOf(Sof3));
    }

    [Fact]
    public void LosslessJpegRefusesAlpha()
    {
        using Raster<Rgba16> rgba = TiffWriterTests.Pattern<Rgba16>();
        Assert.Throws<ArgumentException>(() => Save(rgba, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100 }));
    }

    public static TheoryData<int, int, bool> Depths()
    {
        TheoryData<int, int, bool> data = [];
        for (int bits = 2; bits <= 16; bits++)
        {
            data.Add(bits, 1, false);
            data.Add(bits, 3, bits % 5 == 0);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void EveryDepthReadsAsItsUncompressedTwin(int bits, int samples, bool tiled)
    {
        const int Width = 64, Height = 37, Rows = 16;
        Random random = new((bits * 10) + samples);
        int stride = Width * samples;
        ushort[] values = new ushort[stride * Height];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)random.Next(1 << bits);
        }

        int unitWidth = tiled ? 32 : Width;
        List<byte[]> plain = [], jpeg = [];
        for (int y0 = 0; y0 < Height; y0 += Rows)
        {
            for (int x0 = 0; x0 < Width; x0 += unitWidth)
            {
                // Tiles are whole, padded past the bottom edge; the last strip holds only the rows left.
                int rows = tiled ? Rows : Math.Min(Rows, Height - y0);
                ushort[] unit = new ushort[unitWidth * samples * rows];
                for (int r = 0; r < rows; r++)
                {
                    int y = Math.Min(y0 + r, Height - 1);
                    values.AsSpan((y * stride) + (x0 * samples), unitWidth * samples).CopyTo(unit.AsSpan(r * unitWidth * samples));
                }

                plain.Add(Pack(unit, unitWidth * samples, bits));
                using MemoryStream coded = new();
                JpegLosslessWriter.Write(coded, unit, samples, unitWidth, bits, 1 + (bits % 7));
                jpeg.Add(coded.ToArray());
            }
        }

        int photometric = samples == 1 ? 1 : 2;
        using Raster expected = Raster.Load(TiffJxlTests.Build(Width, Height, tiled, Rows, bits, samples, photometric, false, 1, plain, tileWidth: unitWidth));
        using Raster actual = Raster.Load(TiffJxlTests.Build(Width, Height, tiled, Rows, bits, samples, photometric, false, 7, jpeg, tileWidth: unitWidth));
        Assert.Equal(expected.Layout, actual.Layout);
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(actual));
        Assert.Empty(actual.Metadata.Warnings);
    }

    [Fact]
    public void ATwoComponentCfaTileReadsAsItsMosaic()
    {
        // DNG's layout: a 32-wide one-sample tile coded as a 16-wide two-component frame.
        ushort[] mosaic = Egami.Tests.Formats.Jpeg.JpegLosslessSampleTests.Mosaic(32, 16, 12);
        using MemoryStream coded = new();
        JpegLosslessWriter.WriteCfa(coded, mosaic, 32, 12);
        using Raster expected = Raster.Load(TiffJxlTests.Build(32, 16, true, 16, 12, 1, 1, false, 1, [Pack(mosaic, 32, 12)], tileWidth: 32));
        using Raster actual = Raster.Load(TiffJxlTests.Build(32, 16, true, 16, 12, 1, 1, false, 7, [coded.ToArray()], tileWidth: 32));
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(actual));
    }

    [Fact]
    public void ADamagedUnitIsShortLenientlyAndCorruptStrictly()
    {
        using Raster<Gray16> source = TiffWriterTests.Pattern<Gray16>(32, 32);
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 100 });
        int sos = file.AsSpan().IndexOf(Sos);
        file.AsSpan(sos + 20, 40).Fill(0xFF);
        using Raster lenient = Raster.Load(file);
        Assert.Contains(lenient.Metadata.Warnings, w => w.Id == "Tiff.UnitsShort");
        Assert.ThrowsAny<CorruptDataException>(() => Raster.Load(file, EgamiContext.Default, new Egami.Formats.ReaderOptions { Recovery = Egami.Formats.RecoveryMode.Strict }));
    }

    [Fact]
    public void ABaselineUnitAtSixteenBitsIsRefused()
    {
        using Raster<Gray8> gray = TiffWriterTests.Pattern<Gray8>(16, 16);
        using MemoryStream baseline = new();
        gray.Save(baseline, JpegFormat.Info);
        byte[] file = TiffJxlTests.Build(16, 16, false, 16, 16, 1, 1, false, 7, [baseline.ToArray()]);
        Assert.Throws<NotSupportedException>(() => Raster.Load(file));
    }

    private static Raster Make(string type) => type switch
    {
        "Gray8" => TiffWriterTests.Pattern<Gray8>(),
        "Gray16" => TiffWriterTests.Pattern<Gray16>(),
        "Rgb8" => TiffWriterTests.Pattern<Rgb8>(),
        "Rgb16" => TiffWriterTests.Pattern<Rgb16>(),
        "Cmyk8" => TiffWriterTests.Pattern<Cmyk8>(),
        _ => TiffWriterTests.Pattern<Cmyk16>(),
    };

    private static byte[] Save(Raster raster, TiffWriterOptions options)
    {
        using MemoryStream file = new();
        raster.Save(file, TiffFormat.Info, options);
        return file.ToArray();
    }

    /// <summary>Packs rows of samples as TIFF stores them uncompressed (little-endian): from the high bit, each row padded to a byte.</summary>
    private static byte[] Pack(ushort[] samples, int rowSamples, int bits)
    {
        int rows = samples.Length / rowSamples;
        int rowBytes = ((rowSamples * bits) + 7) / 8;
        byte[] packed = new byte[rows * rowBytes];
        for (int r = 0; r < rows; r++)
        {
            if (bits == 16)
            {
                for (int i = 0; i < rowSamples; i++)
                {
                    ushort v = samples[(r * rowSamples) + i];
                    packed[(r * rowBytes) + (2 * i)] = (byte)v;
                    packed[(r * rowBytes) + (2 * i) + 1] = (byte)(v >> 8);
                }

                continue;
            }

            long bit = 0;
            for (int i = 0; i < rowSamples; i++)
            {
                int v = samples[(r * rowSamples) + i];
                for (int b = bits - 1; b >= 0; b--, bit++)
                {
                    if (((v >> b) & 1) != 0)
                    {
                        packed[(r * rowBytes) + (bit / 8)] |= (byte)(0x80 >> (int)(bit % 8));
                    }
                }
            }
        }

        return packed;
    }
}
