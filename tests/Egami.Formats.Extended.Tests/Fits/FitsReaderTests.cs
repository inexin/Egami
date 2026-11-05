using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Fits;
using Egami.Pixels;

using static Egami.Formats.Extended.Tests.Fits.FitsFiles;

namespace Egami.Formats.Extended.Tests.Fits;

/// <summary>FITS reading: every BITPIX, scaling and blanks, stretches, HDUs and cubes, header cards, tile compression, damage.</summary>
public sealed class FitsReaderTests
{
    private static readonly FitsReaderOptions Raw = new() { Stretch = FitsStretch.None };

    public static TheoryData<int, double, double> Bitpixes() => new()
    {
        { 8, 1, 0 },
        { 16, 1, 0 },
        { 16, 1, 32768 },
        { 32, 0.5, -3 },
        { 64, 2, 1e9 },
        { -32, 1, 0 },
        { -64, 1e-3, 7 },
    };

    [Theory]
    [MemberData(nameof(Bitpixes))]
    public void ReadsEveryBitpixAsPhysicalValues(int bitpix, double scale, double zero)
    {
        double[] stored = [.. Enumerable.Range(0, 5 * 3).Select(i => bitpix == 8 ? i * 17 : bitpix < 0 ? (i * 1.25) - 4 : (i * 1001) - 7000)];
        byte[] file = Image(bitpix, [5, 3], stored, Card("BSCALE", scale), Card("BZERO", zero));
        FitsFile fits = FitsFile.Load(new MemoryStream(file));
        FitsHdu hdu = Assert.Single(fits.Hdus);
        Assert.Equal((bitpix, 5, 3, 1L), (hdu.Bitpix, hdu.Width, hdu.Height, hdu.Planes));
        double[] expected = [.. stored.Select(s => (bitpix == -32 ? (float)s : s) * scale + zero)];
        Assert.Equal(expected, fits.ReadValues(hdu));

        // Load without a stretch gives the same values as float, the file's first row at the bottom.
        using Raster<GrayF32> raster = Raster.Load<GrayF32>(file, TestImages.Context, Raw);
        Assert.Equal("FITS", raster.Metadata.FormatName);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal((float)expected[((2 - y) * 5) + x], raster.Row(y)[x].L);
            }
        }

        using Raster<GrayF32> plane = fits.ReadPlane(hdu);
        TestImages.AssertEqual(raster, plane);
    }

    [Fact]
    public void ReadsBlankAndNaNAsMissing()
    {
        FitsFile ints = FitsFile.Load(new MemoryStream(Image(16, [3, 1], [5, -1, 7], Card("BLANK", -1))));
        double[] values = ints.ReadValues(ints.Hdus[0]);
        Assert.Equal(5, values[0]);
        Assert.True(double.IsNaN(values[1]));

        byte[] floats = Image(-32, [3, 1], [1, double.NaN, 3]);
        using Raster<GrayF32> stretched = Raster.Load<GrayF32>(floats, TestImages.Context);
        Assert.Equal([0f, 0f, 1f], [.. MemoryMarshal.Cast<GrayF32, float>(stretched.Row(0))]);
        using Raster<GrayF32> raw = Raster.Load<GrayF32>(floats, TestImages.Context, Raw);
        Assert.True(float.IsNaN(raw.Row(0)[1].L));
    }

    [Fact]
    public void StretchesIntoZeroToOne()
    {
        double[] stored = [.. Enumerable.Range(0, 100).Select(i => (double)i)];
        byte[] file = Image(16, [10, 10], stored);

        // Min-max by default: 0 to 99.
        using Raster<GrayF32> minMax = Raster.Load<GrayF32>(file, TestImages.Context);
        Assert.Equal(0, minMax.Row(9)[0].L);
        Assert.Equal(1, minMax.Row(0)[9].L);
        FitsDetails details = Assert.IsType<FitsDetails>(minMax.Metadata.FormatDetails);
        Assert.Equal((FitsStretch.MinMax, 0d, 99d), (details.Stretch, details.Low, details.High));

        // DATAMIN and DATAMAX win when present; values outside clip.
        using Raster<GrayF32> limited = Raster.Load<GrayF32>(Image(16, [10, 10], stored, Card("DATAMIN", 10), Card("DATAMAX", 20)), TestImages.Context);
        Assert.Equal(0, limited.Row(9)[5].L);
        Assert.Equal(0.5f, limited.Row(8)[5].L, 5);
        Assert.Equal(1, limited.Row(0)[0].L);

        // Asinh lifts the faint end and keeps the ends.
        using Raster<GrayF32> asinh = Raster.Load<GrayF32>(file, TestImages.Context, new FitsReaderOptions { Stretch = FitsStretch.Asinh });
        Assert.True(asinh.Row(9)[9].L > 9f / 99);
        Assert.Equal(1, asinh.Row(0)[9].L, 5);

        // ZScale keeps a smooth ramp whole, and clips a few bright outliers.
        (double low, double high) = FitsData.ZScale([.. stored.Select(v => (float)v)]);
        Assert.InRange(low, 0, 1);
        Assert.InRange(high, 98, 99);
        float[] stars = [.. Enumerable.Range(0, 1000).Select(i => i % 97 == 0 ? 60000f : 100 + (i % 10))];
        (double faint, double bright) = FitsData.ZScale(stars);
        Assert.InRange(faint, 99, 101);
        Assert.InRange(bright, 108, 200);
    }

    [Fact]
    public void StretchesEveryValueTheSameWayInVectorsAndAlone()
    {
        // Lengths around the vector width, with NaN and the infinities in both the vector part and the tail.
        for (int length = 1; length < 40; length++)
        {
            float[] values = [.. Enumerable.Range(0, length).Select(i => (i * 7.3f) - 50)];
            values[length / 2] = float.NaN;
            if (length > 3)
            {
                values[1] = float.PositiveInfinity;
                values[^1] = float.NegativeInfinity;
            }

            float[] finite = [.. values.Where(float.IsFinite)];
            (double low, double high) = FitsData.Limits(FitsStretch.MinMax, values, new FitsHeader([]));
            Assert.Equal(finite.Length == 0 ? (0d, 1d) : (finite.Min(), finite.Max()), (low, high));

            float[] linear = new float[length];
            float[] asinh = new float[length];
            FitsData.Stretch(FitsStretch.MinMax, -40, 100, values, linear);
            FitsData.Stretch(FitsStretch.Asinh, -40, 100, values, asinh);
            for (int i = 0; i < length; i++)
            {
                float v = (values[i] + 40) / 140;
                float expected = float.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
                Assert.Equal(expected, linear[i], 1e-6f);
                double curve = Math.Asinh(expected / FitsData.AsinhSoftening) / Math.Asinh(1 / FitsData.AsinhSoftening);
                Assert.Equal(curve, asinh[i], 1e-6);
            }
        }
    }

    [Fact]
    public void ReadsEveryImageHduAndPlane()
    {
        // An empty primary HDU, a binary table that is not an image, a 3-plane cube, then a 2D image.
        byte[] table = [.. Header(Card("XTENSION", "BINTABLE"), Card("BITPIX", 8), Card("NAXIS", 2), Card("NAXIS1", 4), Card("NAXIS2", 2), Card("PCOUNT", 0), Card("GCOUNT", 1), Card("TFIELDS", 1), Card("TFORM1", "1J")), .. Pad(new byte[8])];
        byte[] file =
        [
            .. Empty(), .. table,
            .. Extension(8, [2, 2, 3], [1, 2, 3, 4, 10, 20, 30, 40, 100, 110, 120, 130], Card("EXTNAME", "CUBE")),
            .. Extension(-32, [3, 1], [0.5, 1.5, 2.5]),
        ];
        RasterInfo info = Raster.Probe(file, TestImages.Context);
        Assert.Equal((4, 2, 2), (info.ImageCount, info.Width, info.Height));
        using RasterList all = Raster.LoadAll(file, TestImages.Context, Raw);
        Assert.Equal(4, all.Count);
        Assert.Equal([(2, 0), (2, 1), (2, 2), (3, 0)], all.Select(r => (((FitsDetails)r.Metadata.FormatDetails!).Hdu, ((FitsDetails)r.Metadata.FormatDetails!).Plane)));
        Assert.Equal(20, ((Raster<GrayF32>)all[1]).Row(1)[1].L);
        using Raster<GrayF32> last = Raster.Load<GrayF32>(file, TestImages.Context, new FitsReaderOptions { Image = 3, Stretch = FitsStretch.None });
        Assert.Equal(2.5f, last.Row(0)[2].L);
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(file, TestImages.Context, new FitsReaderOptions { Image = 4 }));

        // A 3-plane cube reads as RGB when asked, the planes stretched alike.
        using Raster<RgbF32> rgb = Raster.Load<RgbF32>(file, TestImages.Context, new FitsReaderOptions { Rgb = true, Stretch = FitsStretch.None });
        Assert.Equal(new RgbF32(3, 30, 120), rgb.Row(0)[0]);
        Assert.Equal(PixelChannelOrder.Rgb, Raster.Probe(file, TestImages.Context, new FitsReaderOptions { Rgb = true }).Layout.ChannelOrder);

        FitsFile fits = FitsFile.Load(new MemoryStream(file));
        Assert.Equal([FitsHduKind.Primary, FitsHduKind.BinaryTable, FitsHduKind.Image, FitsHduKind.Image], fits.Hdus.Select(h => h.Kind));
        Assert.Equal(2, fits.FirstImage!.Index);
        Assert.Equal("CUBE", fits.Hdus[2].Header.GetString("EXTNAME"));
        Assert.Equal(12, fits.ReadValues(fits.Hdus[2]).Length);
        using Raster<GrayF32> plane = fits.ReadPlane(fits.Hdus[2], 2);
        Assert.Equal(110, plane.Row(1)[1].L);
        Assert.Throws<ArgumentException>(() => fits.ReadValues(fits.Hdus[1]));
    }

    [Fact]
    public void ParsesHeaderCards()
    {
        string[] cards =
        [
            "OBJECT  = 'M31 ''Andromeda'''  / the galaxy",
            "EXPTIME =               1.5D2 / seconds",
            "GAIN    =                 -12",
            "SIMPLEX =                    F",
            "LONG    = 'first part &'",
            "CONTINUE  'and the rest'      / more",
            "HIERARCH ESO DET CHIP NAME = 'CCD-44' / chip",
            "COMMENT   Hello, FITS",
            "HISTORY   made by a test",
            "CPLX    = (1.0, 2.0)",
            "EMPTY   =",
        ];
        FitsFile fits = FitsFile.Load(new MemoryStream(Image(8, [1, 1], [0], cards)));
        FitsHeader header = fits.Hdus[0].Header;
        Assert.Equal("M31 'Andromeda'", header.GetString("OBJECT"));
        Assert.Equal("the galaxy", header.Find("object")!.Comment);
        Assert.Equal(150, header.GetReal("EXPTIME"));
        Assert.Equal(-12, header.GetInteger("GAIN"));
        Assert.Equal(-12, header.GetReal("GAIN"));
        Assert.False(header.GetLogical("SIMPLEX"));
        Assert.Equal("first part and the rest", header.GetString("LONG"));
        Assert.Equal("CCD-44", header.GetString("ESO DET CHIP NAME"));
        Assert.Contains(header, c => c.Keyword == "COMMENT" && c.Comment == "Hello, FITS");
        Assert.Contains(header, c => c.Keyword == "HISTORY" && c.Comment == "made by a test");
        Assert.Equal("(1.0, 2.0)", header.GetString("CPLX"));
        Assert.True(header.Contains("EMPTY"));
        Assert.Null(header.Find("EMPTY")!.Value);
        Assert.DoesNotContain(header, c => c.Keyword == "CONTINUE");
    }

    [Fact]
    public void ChecksTheGeneratorTheDitherUses()
    {
        // The convention gives the seed after 10,000 draws as a check of the generator.
        Assert.Equal(1043618065, FitsTiles.LastSeed, 0);
    }

    [Fact]
    public void HoldsTheDitherTableAtSinglePrecision()
    {
        // The convention's table is of floats; dithered values match astropy (cfitsio) to the last bit only with those.
        double[] random = FitsTiles.MakeRandom();
        Assert.All(random, r => Assert.Equal((double)(float)r, r));
        Assert.Equal((double)(float)(16807 / 2147483647d), random[0]);
        Assert.NotEqual(16807 / 2147483647d, random[0]);
    }

    public static TheoryData<int, int> RiceDepths() => new() { { 8, 1 }, { 16, 2 }, { 32, 4 }, { 16, 4 } };

    [Theory]
    [MemberData(nameof(RiceDepths))]
    public void ReadsRiceTiles(int zbitpix, int bytePix)
    {
        // 13 × 7 in tiles of 5 × 3: the last column and row of tiles are cut short.
        long[] axes = [13, 7], tile = [5, 3];
        long[] values = new long[13 * 7];
        Random random = new(zbitpix);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = zbitpix == 8 ? random.Next(256) : i % 11 == 0 ? random.Next(-30000, 30000) : (i * 7) - 300 + random.Next(4);
        }

        byte[] file = [.. Empty(), .. Compressed("RICE_1", zbitpix, axes, tile, Tiles(values, axes, tile, t => new Tile(FitsFiles.Rice(t, bytePix, 8))), 'B', Card("ZNAME1", "BLOCKSIZE"), Card("ZVAL1", 8), Card("ZNAME2", "BYTEPIX"), Card("ZVAL2", bytePix))];
        AssertDecodes(file, values.Select(v => (double)v), "RICE_1", zbitpix);
    }

    [Fact]
    public void ReadsGzipTiles()
    {
        long[] axes = [9, 4], tile = [9, 1];
        long[] values = [.. Enumerable.Range(0, 36).Select(i => (long)((i * 997) - 18000))];
        foreach (string type in (string[])["GZIP_1", "GZIP_2"])
        {
            byte[] file = [.. Empty(), .. Compressed(type, 16, axes, tile, Tiles(values, axes, tile, t =>
            {
                byte[] raw = Samples(16, t.Select(v => (double)v));
                return new Tile(Gzip(type == "GZIP_2" ? Shuffle(raw, 2) : raw));
            }), 'B', Card("BZERO", 32768), Card("BSCALE", 1))];
            AssertDecodes(file, values.Select(v => v + 32768d), type, 16);
        }
    }

    [Theory]
    [InlineData("GZIP_1", -32)]
    [InlineData("GZIP_2", -32)]
    [InlineData("GZIP_1", -64)]
    [InlineData("GZIP_2", -64)]
    public void ReadsGzipTilesOfFloatsThatWereNotQuantized(string type, int zbitpix)
    {
        // With no ZSCALE the tiles hold the IEEE values themselves (astropy's quantize_level=0 writes these).
        long[] axes = [5, 3], tile = [5, 1];
        double[] values = [.. Enumerable.Range(0, 15).Select(i => i == 7 ? double.NaN : (i * 1.25) - 3.5e5)];
        List<Tile> tiles = [];
        for (int row = 0; row < 3; row++)
        {
            byte[] raw = Samples(zbitpix, values.Skip(row * 5).Take(5));
            tiles.Add(new Tile(Gzip(type == "GZIP_2" ? Shuffle(raw, zbitpix / -8) : raw)));
        }

        byte[] file = [.. Empty(), .. Compressed(type, zbitpix, axes, tile, tiles)];
        AssertDecodes(file, values, type, zbitpix);
    }

    [Theory]
    [InlineData(8, 'B')]
    [InlineData(16, 'B')]
    [InlineData(32, 'B')]
    [InlineData(-32, 'B')]
    [InlineData(-64, 'B')]
    [InlineData(16, 'I')]
    [InlineData(-32, 'E')]
    [InlineData(-64, 'D')]
    public void ReadsUncompressedTiles(int zbitpix, char element)
    {
        // cfitsio (and so astropy) writes NOCOMPRESS tiles to a byte column, big endian at the image's width; a typed column
        // holds them as its own type.
        long[] axes = [4, 3], tile = [4, 1];
        double[] values = [.. Enumerable.Range(0, 12).Select(i => zbitpix < 0 && i == 5 ? double.NaN : zbitpix == 8 ? i * 20 : (i * 1000.5) - 4000)];
        if (zbitpix > 0)
        {
            values = [.. values.Select(v => Math.Floor(v))];
        }

        List<Tile> tiles = [.. Enumerable.Range(0, 3).Select(row => new Tile(Samples(zbitpix, values.Skip(row * 4).Take(4))))];
        byte[] file = [.. Empty(), .. Compressed("NOCOMPRESS", zbitpix, axes, tile, tiles, element)];
        AssertDecodes(file, values, "NOCOMPRESS", zbitpix);
    }

    [Fact]
    public void ReadsUncompressedQuantizedTiles()
    {
        int[] quantized = [-3, 0, 7, 12, 100, -50];
        List<Tile> tiles = [new Tile(Samples(32, quantized.Select(q => (double)q)), 0.5, 10)];
        byte[] file = [.. Empty(), .. Compressed("NOCOMPRESS", -32, [6, 1], [6, 1], tiles, 'B', Card("ZQUANTIZ", "NO_DITHER"))];
        AssertDecodes(file, quantized.Select(q => (q * 0.5) + 10), "NOCOMPRESS", -32);
    }

    [Fact]
    public void ReadsPlioTiles()
    {
        // Hand-assembled line list: zeros 2, set value 5, run of 3, zero 1, +2 and store (7), -4 and store (3).
        ushort[] words = [0, 0, 11, (0 << 12) | 2, (1 << 12) | 5, 0, (4 << 12) | 3, (0 << 12) | 1, (6 << 12) | 2, (7 << 12) | 4, (0 << 12) | 0];
        byte[] list = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(list.AsSpan(i * 2), words[i]);
        }

        byte[] file = [.. Empty(), .. Compressed("PLIO_1", 32, [8, 1], [8, 1], [new Tile(list)], 'I')];
        AssertDecodes(file, [0, 0, 5, 5, 5, 0, 7, 3], "PLIO_1", 32);
    }

    [Fact]
    public void UndoesQuantizationAndDither()
    {
        // Two tiles of quantized floats, gzipped 32-bit integers with a scale and zero each; one null pixel; one tile kept as floats.
        long[] axes = [6, 3], tile = [6, 1];
        int[][] quantized = [[10, -5, 0, 7, -2147483647, 3], [1, 2, 3, 4, 5, 6]];
        double[] scale = [0.25, 2], zero = [100, -1];
        List<Tile> tiles = [];
        for (int t = 0; t < 2; t++)
        {
            tiles.Add(new Tile(Gzip(Samples(32, quantized[t].Select(v => (double)v))), scale[t], zero[t], -2147483647));
        }

        double[] kept = [1.5, -2.25, 3, 4, 5, 6.5];
        tiles.Add(new Tile([], 1, 0, -2147483647, Gzip(Samples(-32, kept))));
        foreach ((string dither, long seed) in ((string, long)[])[("NO_DITHER", 1), ("SUBTRACTIVE_DITHER_1", 1), ("SUBTRACTIVE_DITHER_1", 42)])
        {
            byte[] file = [.. Empty(), .. Compressed("GZIP_1", -32, axes, tile, tiles, 'B', Card("ZQUANTIZ", dither), Card("ZDITHER0", seed))];
            double[] random = FitsTiles.MakeRandom();
            List<double> expected = [];
            for (int t = 0; t < 2; t++)
            {
                int iseed = (int)((t + seed - 1) % 10000);
                int next = (int)(random[iseed] * 500);
                foreach (int q in quantized[t])
                {
                    double r = random[next++];
                    expected.Add(q == -2147483647 ? double.NaN : dither == "NO_DITHER" ? (q * scale[t]) + zero[t] : ((q - r + 0.5) * scale[t]) + zero[t]);
                }
            }

            expected.AddRange(kept);
            FitsFile fits = FitsFile.Load(new MemoryStream(file));
            Assert.Equal(expected, fits.ReadValues(fits.Hdus[1]));
        }
    }

    [Fact]
    public void ReportsDamagedTilesAndRefusesUnknownCompression()
    {
        long[] values = [.. Enumerable.Range(0, 20).Select(i => (long)i)];
        List<Tile> tiles = Tiles(values, [10, 2], [10, 1], t => new Tile(Gzip(Samples(16, t.Select(v => (double)v)))));
        tiles[1] = new Tile([1, 2, 3, 4]);
        byte[] file = [.. Empty(), .. Compressed("GZIP_1", 16, [10, 2], [10, 1], tiles)];
        using Raster<GrayF32> raster = Raster.Load<GrayF32>(file, TestImages.Context, Raw);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Fits.TilesDamaged");
        Assert.True(float.IsNaN(raster.Row(0)[0].L));
        Assert.Equal(9, raster.Row(1)[9].L);
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(file, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        byte[] hcompress = [.. Empty(), .. Compressed("HCOMPRESS_1", 16, [10, 2], [10, 2], [new Tile([1, 2])])];
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(hcompress, TestImages.Context));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(Empty(), TestImages.Format("FITS"), TestImages.Context));
    }

    [Fact]
    public void KeepsTheRowsOfAShortFile()
    {
        double[] stored = [.. Enumerable.Range(0, 40 * 30).Select(i => (double)(i % 200))];
        byte[] file = Image(8, [40, 30], stored);
        byte[] cut = file[..(2880 + (40 * 10) + 5)];
        using Raster<GrayF32> raster = Raster.Load<GrayF32>(cut, TestImages.Context, Raw);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Fits.RowsMissing");
        Assert.Equal(stored[(9 * 40) + 3], raster.Row(29 - 9)[3].L);
        Assert.Equal(0, raster.Row(0)[3].L);
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(cut, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void ReadsFromAForwardOnlyStream()
    {
        byte[] file = [.. Empty(), .. Extension(16, [4, 2], [1, 2, 3, 4, 5, 6, 7, 8]), .. Extension(16, [2, 2], [9, 9, 9, 9])];
        using Raster<GrayF32> raster = Raster.Load<GrayF32>(new ForwardOnlyStream(file), TestImages.Context, Raw);
        Assert.Equal(5, raster.Row(0)[0].L);
        Assert.True(FitsFormat.Info.Matches(file));
        Assert.False(FitsFormat.Info.Matches("SIMPLE  =                    F"u8));
    }

    /// <summary>Cuts an image into tiles in the convention's order and codes each.</summary>
    private static List<Tile> Tiles(long[] values, long[] axes, long[] tile, Func<long[], Tile> code)
    {
        List<Tile> tiles = [];
        for (long ty = 0; ty < axes[1]; ty += tile[1])
        {
            for (long tx = 0; tx < axes[0]; tx += tile[0])
            {
                List<long> t = [];
                for (long y = ty; y < Math.Min(ty + tile[1], axes[1]); y++)
                {
                    for (long x = tx; x < Math.Min(tx + tile[0], axes[0]); x++)
                    {
                        t.Add(values[(y * axes[0]) + x]);
                    }
                }

                tiles.Add(code([.. t]));
            }
        }

        return tiles;
    }

    private static void AssertDecodes(byte[] file, IEnumerable<double> expected, string compression, int bitpix)
    {
        FitsFile fits = FitsFile.Load(new MemoryStream(file));
        FitsHdu hdu = fits.Hdus[1];
        Assert.Equal((FitsHduKind.CompressedImage, compression, bitpix), (hdu.Kind, hdu.Compression, hdu.Bitpix));
        Assert.Equal(expected, fits.ReadValues(hdu));
        Assert.Equal(hdu.Width, Raster.Probe(file, TestImages.Context).Width);
        using Raster raster = Raster.Load(file, TestImages.Context);
        Assert.Equal(compression, ((FitsDetails)raster.Metadata.FormatDetails!).Compression);
        Assert.Empty(raster.Metadata.Warnings);
    }
}
