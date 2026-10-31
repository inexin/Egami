using Egami.Formats.Tiff;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>Every pixel type round trips exactly through the TIFF writer and reader, in every lossless compression and layout.</summary>
public class TiffWriterTests
{
    public static TheoryData<string, TiffCompression, int> Cases()
    {
        TheoryData<string, TiffCompression, int> data = [];
        foreach (string type in Types.Keys)
        {
            foreach (TiffCompression compression in (TiffCompression[])[TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.Zstd, TiffCompression.PackBits])
            {
                foreach (int tile in (int[])[0, 16])
                {
                    data.Add(type, compression, tile);
                }
            }
        }

        return data;
    }

    /// <summary>Each pixel type, and the type it should load back as.</summary>
    private static readonly Dictionary<string, (Func<Raster> Make, PixelLayoutInfo Loaded)> Types = new()
    {
        ["Gray8"] = (() => Pattern<Gray8>(), Gray8.Layout),
        ["Gray16"] = (() => Pattern<Gray16>(), Gray16.Layout),
        ["GrayAlpha8"] = (() => Pattern<GrayAlpha8>(), GrayAlpha8.Layout),
        ["GrayAlpha16"] = (() => Pattern<GrayAlpha16>(), GrayAlpha16.Layout),
        ["GrayF32"] = (() => Pattern<GrayF32>(), GrayF32.Layout),
        ["Rgb8"] = (() => Pattern<Rgb8>(), Rgb8.Layout),
        ["Rgba8"] = (() => Pattern<Rgba8>(), Rgba8.Layout),
        ["Bgra8"] = (() => Pattern<Bgra8>(), Rgba8.Layout),
        ["Rgb16"] = (() => Pattern<Rgb16>(), Rgb16.Layout),
        ["Rgba16"] = (() => Pattern<Rgba16>(), Rgba16.Layout),
        ["RgbF32"] = (() => Pattern<RgbF32>(), RgbF32.Layout),
        ["RgbaF32"] = (() => Pattern<RgbaF32>(), RgbaF32.Layout),
        ["RgbaF16"] = (() => Pattern<RgbaF16>(), RgbaF16.Layout),
        ["Cmyk8"] = (() => Pattern<Cmyk8>(), Cmyk8.Layout),
        ["Cmyk16"] = (() => Pattern<Cmyk16>(), Cmyk16.Layout),
    };

    /// <summary>A 37 × 29 raster whose every channel byte differs, so no layout or byte order mistake can hide.</summary>
    internal static Raster<TPixel> Pattern<TPixel>(int width = 37, int height = 29)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height);
        Random random = new(width * height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y));
            if (TPixel.Layout.ChannelType is PixelChannelType.Float32)
            {
                Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(row);
                for (int i = 0; i < floats.Length; i++)
                {
                    floats[i] = (float)(random.NextDouble() * 1.5) - 0.25f;
                }
            }
            else if (TPixel.Layout.ChannelType is PixelChannelType.Float16)
            {
                Span<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(row);
                for (int i = 0; i < halves.Length; i++)
                {
                    halves[i] = (Half)random.NextDouble();
                }
            }
            else
            {
                random.NextBytes(row);
                if (y % 3 == 0)
                {
                    // Some flat runs, so PackBits and LZW have something to find.
                    row[..(row.Length / 3)].Fill(row[0]);
                }
            }
        }

        return raster;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryTypeRoundTripsExactly(string type, TiffCompression compression, int tile)
    {
        (Func<Raster> make, PixelLayoutInfo loaded) = Types[type];
        using Raster source = make();
        using MemoryStream file = new();
        source.Save(file, TiffFormat.Info, new TiffWriterOptions { Compression = compression, TileSize = tile });
        using Raster back = Raster.Load(file.ToArray());
        Assert.Equal(loaded, back.Layout);
        Assert.Equal((source.Width, source.Height), (back.Width, back.Height));
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal(compression, details.Compression);
        Assert.Equal(tile == 0 ? null : tile, details.TileWidth);
        byte[] expected = source is Raster<Bgra8> bgra ? TiffReference.Bytes(bgra.As<Rgba8>()) : TiffReference.Bytes(source);
        Assert.Equal(expected, TiffReference.Bytes(back));
    }

    [Theory]
    [InlineData(TiffLayout.Big)]
    [InlineData(TiffLayout.Classic)]
    public void BigTiffRoundTrips(TiffLayout layout)
    {
        using Raster<Rgb16> source = Pattern<Rgb16>();
        using MemoryStream file = new();
        source.Save(file, TiffFormat.Info, new TiffWriterOptions { Layout = layout, TileSize = 16 });
        byte[] bytes = file.ToArray();
        Assert.Equal(layout == TiffLayout.Big ? 43 : 42, bytes[2]);
        using Raster<Rgb16> back = Raster.Load<Rgb16>(bytes);
        Assert.Equal(TiffReference.Bytes(source), TiffReference.Bytes(back));
    }

    [Fact]
    public void DefaultsAreDeflateWithThePredictorInStrips()
    {
        using Raster<Rgb8> rgb = Pattern<Rgb8>();
        using Raster<RgbF32> floats = Pattern<RgbF32>();
        static TiffDetails Saved(Raster raster)
        {
            using MemoryStream file = new();
            raster.Save(file, TiffFormat.Info);
            using Raster back = Raster.Load(file.ToArray());
            return (TiffDetails)back.Metadata.FormatDetails!;
        }

        TiffDetails a = Saved(rgb);
        Assert.Equal((TiffCompression.Deflate, TiffPredictor.Horizontal, TiffByteOrder.LittleEndian, TiffLayout.Classic), (a.Compression, a.Predictor, a.ByteOrder, a.Layout));
        Assert.NotNull(a.RowsPerStrip);
        Assert.Equal(TiffPredictor.FloatingPoint, Saved(floats).Predictor);
    }

    [Fact]
    public void ASourcesSettingsAreKept()
    {
        byte[] data = TiffCorpus.Main.Read("rgb16-zip-pred-be.tif");
        using Raster loaded = Raster.Load(data);
        using MemoryStream file = new();
        loaded.Save(file, TiffFormat.Info);
        using Raster back = Raster.Load(file.ToArray());
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal((TiffByteOrder.BigEndian, TiffCompression.Deflate, TiffPredictor.Horizontal), (details.ByteOrder, details.Compression, details.Predictor));
        Assert.Equal(TiffReference.Bytes(loaded), TiffReference.Bytes(back));

        using Raster tiled = Raster.Load(TiffCorpus.Main.Read("rgb8-tiles32x16-lzw.tif"));
        using MemoryStream again = new();
        tiled.Save(again, TiffFormat.Info);
        using Raster tiledBack = Raster.Load(again.ToArray());
        Assert.Equal(TiffCompression.Lzw, ((TiffDetails)tiledBack.Metadata.FormatDetails!).Compression);
    }

    [Fact]
    public void PremultipliedAndAssociatedAlphaAreWrittenAssociated()
    {
        using Raster<Rgba8> straight = Pattern<Rgba8>();
        using MemoryStream file = new();
        straight.Save(file, TiffFormat.Info, new TiffWriterOptions { AssociatedAlpha = true });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file.ToArray());
        Assert.Equal([TiffExtraSample.Associated], ((TiffDetails)back.Metadata.FormatDetails!).ExtraSamples);

        // Straight to premultiplied and back loses precision only where alpha is small.
        using Raster<PremulRgba8> premul = straight.As<PremulRgba8>();
        using MemoryStream file2 = new();
        premul.Save(file2, TiffFormat.Info);
        using Raster<Rgba8> back2 = Raster.Load<Rgba8>(file2.ToArray());
        Assert.Equal(TiffReference.Bytes(back), TiffReference.Bytes(back2));
    }

    [Fact]
    public void ResolutionIccExifXmpAndIptcRoundTrip()
    {
        byte[] data = TiffCorpus.Main.Read("meta-exif.tif");
        using Raster loaded = Raster.Load(data);
        loaded.Metadata.Resolution = new Resolution(300, 150, ResolutionUnit.PerCentimeter);
        loaded.Metadata.IccProfile = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "icc", "compact", "DisplayP3-v4.icc"));
        using MemoryStream file = new();
        loaded.Save(file, TiffFormat.Info);
        using Raster back = Raster.Load(file.ToArray());
        Assert.Equal(new Resolution(300, 150, ResolutionUnit.PerCentimeter), back.Metadata.Resolution);
        Assert.Equal(loaded.Metadata.IccProfile.ToArray(), back.Metadata.IccProfile.ToArray());
        Assert.Equal(loaded.Metadata.Exif!.CameraMake, back.Metadata.Exif!.CameraMake);
        Assert.Equal(loaded.Metadata.Exif.Location, back.Metadata.Exif.Location);
        Assert.Equal(loaded.Metadata.Exif.DateTaken, back.Metadata.Exif.DateTaken);
        Assert.Equal(["Egami"], back.Metadata.XmpData!.Creators);
        Assert.Equal(["duck", "bird"], back.Metadata.Iptc!.Keywords);
    }

    [Fact]
    public void SaveAllWritesPages()
    {
        using Raster<Rgb8> a = Pattern<Rgb8>(20, 10);
        using Raster<Gray16> b = Pattern<Gray16>(7, 33);
        using MemoryStream file = new();
        Raster.SaveAll([a, b], file, TiffFormat.Info);
        using RasterList pages = Raster.LoadAll(file.ToArray());
        Assert.Equal(2, pages.Count);
        Assert.Equal(TiffReference.Bytes(a), TiffReference.Bytes(pages[0]));
        Assert.Equal(TiffReference.Bytes(b), TiffReference.Bytes(pages[1]));
    }

    [Fact]
    public void TheWriterIsDeterministicAndWritesToStreamsThatCannotSeek()
    {
        using Raster<Rgba16> source = Pattern<Rgba16>();
        using MemoryStream first = new();
        source.Save(first, TiffFormat.Info);
        using MemoryStream second = new();
        source.Save(new ForwardOnly(second), TiffFormat.Info);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

/// <summary>The writer's extras: CCITT, JPEG, WebP, lossless reduction, and keeping a lossy source's compression.</summary>
public class TiffWriterExtrasTests
{
    private static Raster<Gray8> Bilevel()
    {
        Raster<Gray8> raster = Raster.Create<Gray8>(203, 71);
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Gray8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Gray8((byte)((((x / (1 + (y % 7))) + (y / 5)) % 3 == 0 || x * x % 97 < 9) ? 0 : 255));
            }
        }

        return raster;
    }

    private static byte[] Save(Raster raster, TiffWriterOptions options)
    {
        using MemoryStream file = new();
        raster.Save(file, TiffFormat.Info, options);
        return file.ToArray();
    }

    [Theory]
    [InlineData(TiffCompression.CcittGroup3, 0)]
    [InlineData(TiffCompression.CcittGroup4, 0)]
    [InlineData(TiffCompression.CcittGroup4, 16)]
    public void CcittRoundTripsBilevelImages(TiffCompression compression, int tile)
    {
        using Raster<Gray8> source = Bilevel();
        byte[] file = Save(source, new TiffWriterOptions { Compression = compression, TileSize = tile });
        using Raster<Gray8> back = Raster.Load<Gray8>(file);
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal((compression, 1, TiffPhotometric.WhiteIsZero), (details.Compression, details.BitsPerSample, details.Photometric));
        Assert.Equal(TiffReference.Bytes(source), TiffReference.Bytes(back));
    }

    [Fact]
    public void CcittRefusesImagesThatAreNotBilevel()
    {
        using Raster<Gray8> gray = TiffWriterTests.Pattern<Gray8>();
        Assert.Throws<ArgumentException>(() => Save(gray, new TiffWriterOptions { Compression = TiffCompression.CcittGroup4 }));
    }

    [Fact]
    public void ReduceFindsTheSmallestExactForm()
    {
        using Raster<Gray8> bilevel = Bilevel();
        using Raster<Rgba8> bilevelRgba = bilevel.As<Rgba8>();
        using Raster<Rgb8> grayRgb = TiffWriterTests.Pattern<Gray8>().As<Rgb8>();
        using Raster<Rgba16> wide = TiffWriterTests.Pattern<Rgba8>().As<Rgba16>();
        using Raster<Rgb8> fewColors = Raster.Create<Rgb8>(40, 30);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                fewColors.Row(y)[x] = new Rgb8((byte)(x % 5 * 60), (byte)(y % 3 * 100), 7);
            }
        }

        (Raster Source, TiffPhotometric Photometric, int Bits)[] cases =
        [
            (bilevelRgba, TiffPhotometric.WhiteIsZero, 1), (grayRgb, TiffPhotometric.BlackIsZero, 8), (wide, TiffPhotometric.Rgb, 8), (fewColors, TiffPhotometric.Palette, 4),
        ];
        foreach ((Raster source, TiffPhotometric photometric, int bits) in cases)
        {
            byte[] file = Save(source, new TiffWriterOptions { Reduce = true });
            using Raster<Rgba16> back = Raster.Load<Rgba16>(file);
            using Raster<Rgba16> expected = source.As<Rgba16>();
            TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
            Assert.Equal((photometric, bits), (details.Photometric, details.BitsPerSample));
            Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(back));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void JpegWritesYCbCrWithSharedTables(int tile)
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(TiffCorpus.Main.Read("rgb8.tif"));
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 90, TileSize = tile });
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal((TiffCompression.Jpeg, TiffPhotometric.YCbCr), (details.Compression, details.Photometric));
        Assert.True(Psnr(source, back) > 30, $"PSNR {Psnr(source, back):F1} dB");
    }

    [Fact]
    public void JpegRefusesAlpha()
    {
        using Raster<Rgba8> rgba = TiffWriterTests.Pattern<Rgba8>();
        Assert.Throws<ArgumentException>(() => Save(rgba, new TiffWriterOptions { Compression = TiffCompression.Jpeg }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(32, false)]
    [InlineData(0, true)]
    public void JpegWritesCmykAsSeparatedInks(int tile, bool sixteen)
    {
        using Raster<Rgb8> rgb = Raster.Load<Rgb8>(TiffCorpus.Main.Read("rgb8.tif"));
        using Raster<Cmyk8> source = rgb.As<Cmyk8>();
        using Raster input = sixteen ? source.As<Cmyk16>() : source.Copy();
        byte[] file = Save(input, new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 95, TileSize = tile });
        using Raster<Cmyk8> back = Raster.Load<Cmyk8>(file);
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal((TiffCompression.Jpeg, TiffPhotometric.Separated, 8), (details.Compression, details.Photometric, details.BitsPerSample));
        byte[] a = TiffReference.Bytes(source), b = TiffReference.Bytes(back);
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += (a[i] - b[i]) * (a[i] - b[i]);
        }

        double psnr = 10 * Math.Log10(255.0 * 255 / (sum / a.Length));
        Assert.True(psnr > 35, $"PSNR {psnr:F1} dB");
    }

    [Fact]
    public void LosslessWebpRoundTripsExactly()
    {
        using Raster<Rgba8> source = TiffWriterTests.Pattern<Rgba8>();
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Webp, Quality = 100 });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
        Assert.Equal(TiffCompression.Webp, ((TiffDetails)back.Metadata.FormatDetails!).Compression);
        Assert.Equal(TiffReference.Bytes(source), TiffReference.Bytes(back));
    }

    [Fact]
    public void ALosslessWebpSourceStaysLossless()
    {
        using Raster loaded = Raster.Load(TiffCorpus.Main.Read("webp-lossless-alpha.tif"));
        using MemoryStream file = new();
        loaded.Save(file, TiffFormat.Info);
        using Raster back = Raster.Load(file.ToArray());
        Assert.Equal(TiffCompression.Webp, ((TiffDetails)back.Metadata.FormatDetails!).Compression);
        Assert.Equal(TiffReference.Bytes(loaded), TiffReference.Bytes(back));
    }

    [Fact]
    public void LossyWebpWritesCloseToTheSource()
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(TiffCorpus.Main.Read("rgb8.tif"));
        byte[] file = Save(source, new TiffWriterOptions { Compression = TiffCompression.Webp, Quality = 90 });
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        Assert.True(Psnr(source, back) > 28, $"PSNR {Psnr(source, back):F1} dB");
    }

    [Fact]
    public void ALossySourceKeepsItsCompressionOnlyWhileUnchanged()
    {
        byte[] data = TiffCorpus.Main.Read("rgb8-jpeg.tif");
        using Raster loaded = Raster.Load(data);
        using MemoryStream again = new();
        loaded.Save(again, TiffFormat.Info);
        using (Raster back = Raster.Load(again.ToArray()))
        {
            Assert.Equal(TiffCompression.Jpeg, ((TiffDetails)back.Metadata.FormatDetails!).Compression);
        }

        using Raster changed = loaded.Derive(ops => ops.Fit(32, 24), TestContext.Current.CancellationToken);
        using MemoryStream resized = new();
        changed.Save(resized, TiffFormat.Info);
        using Raster back2 = Raster.Load(resized.ToArray());
        Assert.Equal(TiffCompression.Deflate, ((TiffDetails)back2.Metadata.FormatDetails!).Compression);
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double psnr = Raster.Compare(a, b).Overall.Psnr;
        return double.IsPositiveInfinity(psnr) ? 99 : psnr;
    }

    [Fact]
    public void ReduceAndCcittCannotStream()
    {
        using MemoryStream output = new();
        Assert.Throws<NotSupportedException>(() => RasterEncoder.Create<Gray8>(output, TiffFormat.Info, 8, 8, new TiffWriterOptions { Reduce = true }));
        Assert.Throws<NotSupportedException>(() => RasterEncoder.Create<Gray8>(output, TiffFormat.Info, 8, 8, new TiffWriterOptions { Compression = TiffCompression.CcittGroup4 }));
    }

    [Theory]
    [InlineData(TiffCompression.Deflate, 0)]
    [InlineData(TiffCompression.Lzw, 16)]
    [InlineData(TiffCompression.Zstd, 0)]
    [InlineData(TiffCompression.Zstd, 16)]
    [InlineData(TiffCompression.PackBits, 0)]
    [InlineData(TiffCompression.CcittGroup4, 0)]
    public void ParallelCompressionWritesTheSameFile(TiffCompression compression, int tile)
    {
        using Raster<Gray8> bilevel = Bilevel();
        using Raster<Rgb16> photo = TiffWriterTests.Pattern<Rgb16>(300, 200);
        Raster source = compression == TiffCompression.CcittGroup4 ? bilevel : photo;
        byte[] one = Save(source, new TiffWriterOptions { Compression = compression, TileSize = tile });
        byte[] many = Save(source, new TiffWriterOptions { Compression = compression, TileSize = tile, ParallelCompression = true });
        Assert.Equal(one, many);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(19)]
    public void ZstdKeepsItsCompressionAndPredictorThroughAResave(int level)
    {
        using Raster<Rgb16> photo = TiffWriterTests.Pattern<Rgb16>(120, 90);
        byte[] file = Save(photo, new TiffWriterOptions { Compression = TiffCompression.Zstd, ZstdLevel = level });
        using Raster<Rgb16> back = Raster.Load<Rgb16>(file);
        TiffDetails details = (TiffDetails)back.Metadata.FormatDetails!;
        Assert.Equal((TiffCompression.Zstd, TiffPredictor.Horizontal), (details.Compression, details.Predictor));
        Assert.Equal(TiffReference.Bytes(photo), TiffReference.Bytes(back));

        // Saved again with no options, the source's compression stays.
        using Raster<Rgb16> again = Raster.Load<Rgb16>(Save(back, TiffWriterOptions.Default));
        Assert.Equal(TiffCompression.Zstd, ((TiffDetails)again.Metadata.FormatDetails!).Compression);
    }

    [Fact]
    public void ZstdFloatsUseTheFloatingPointPredictor()
    {
        using Raster<RgbF32> floats = TiffWriterTests.Pattern<RgbF32>(64, 40);
        using Raster<RgbF32> back = Raster.Load<RgbF32>(Save(floats, new TiffWriterOptions { Compression = TiffCompression.Zstd }));
        Assert.Equal(TiffPredictor.FloatingPoint, ((TiffDetails)back.Metadata.FormatDetails!).Predictor);
        Assert.Equal(TiffReference.Bytes(floats), TiffReference.Bytes(back));
    }

    [Fact]
    public void ZstdLevelIsChecked()
    {
        Assert.Equal(9, new TiffWriterOptions().ZstdLevel);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TiffWriterOptions { ZstdLevel = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TiffWriterOptions { ZstdLevel = 23 });
    }

    [Fact]
    public void ADamagedZstdStripKeepsWhatDecodedLeniently()
    {
        // Smooth enough that every strip is a compressed block, whose damage the decoder sees.
        using Raster<Gray8> gray = Raster.Create<Gray8>(300, 400);
        for (int y = 0; y < gray.Height; y++)
        {
            Span<Gray8> row = gray.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Gray8((byte)((x * y / 50) + (x % 7)));
            }
        }

        byte[] file = Save(gray, new TiffWriterOptions { Compression = TiffCompression.Zstd, Predictor = TiffPredictor.None });

        // Overwrite bytes inside the strips (which follow the 8-byte header; the IFD comes last): strict reading fails, lenient
        // reading keeps the rest.
        byte[] cut = (byte[])file.Clone();
        cut.AsSpan(file.Length / 3, 64).Fill(0xA5);
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(cut, EgamiContext.Default, new Egami.Formats.ReaderOptions { Recovery = Egami.Formats.RecoveryMode.Strict }));
        using Raster lenient = Raster.Load(cut, EgamiContext.Default, new Egami.Formats.ReaderOptions { Recovery = Egami.Formats.RecoveryMode.Lenient });
        Assert.Equal((300, 400), (lenient.Width, lenient.Height));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void AStreamedZstdSaveWritesWhatSaveWrites(int tile)
    {
        using Raster<Rgb8> photo = TiffWriterTests.Pattern<Rgb8>(150, 130);
        TiffWriterOptions options = new() { Compression = TiffCompression.Zstd, ZstdLevel = 3, TileSize = tile };
        byte[] saved = Save(photo, options);
        using MemoryStream memory = new();
        using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(memory, TiffFormat.Info, photo.Width, photo.Height, options))
        {
            using Raster<Rgb8> band = Raster.Create<Rgb8>(photo.Width, 7);
            for (int y = 0; y < photo.Height; y += 7)
            {
                int rows = Math.Min(7, photo.Height - y);
                for (int r = 0; r < rows; r++)
                {
                    photo.Row(y + r).CopyTo(band.Row(r));
                }

                encoder.WriteRows(band, rows);
            }

            encoder.Complete();
        }

        Assert.Equal(saved, memory.ToArray());
    }
}
