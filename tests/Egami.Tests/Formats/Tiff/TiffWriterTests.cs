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
            foreach (TiffCompression compression in (TiffCompression[])[TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.PackBits])
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
