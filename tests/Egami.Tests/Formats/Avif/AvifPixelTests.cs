using System.Security.Cryptography;
using System.Text.Json;

using Egami.Colors;
using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Phase 2.9.2 task 12's gate: Raster.Load of every corpus file equals avifdec's RGB decode (reference/avif/*.png, made without
/// libyuv) sample for sample, once turned by its orientation as avifdec turns it; and YuvRaster.Load gives the planes dav1d
/// decodes (the manifest's yuv_md5).
/// </summary>
public class AvifPixelTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> RgbFiles() => [.. Manifest.Value.EnumerateObject().Where(p => p.Value.TryGetProperty("reference", out _)).Select(p => p.Name)];

    public static TheoryData<string> YuvFiles() => [.. Manifest.Value.EnumerateObject().Where(p => p.Value.TryGetProperty("yuv_md5", out _)).Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(RgbFiles))]
    public void LoadEqualsAvifdec(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        using (Raster raster = Raster.Load(data))
        {
            RasterInfo info = Raster.Probe(data);
            Assert.Equal(info.Layout, raster.Layout);
            raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
            using Raster<Rgba16> actual = raster.As<Rgba16>();
            using Raster<Rgba16> expected = Raster.Load<Rgba16>(Path.Combine(References, Path.GetDirectoryName(file)!, entry.GetProperty("reference").GetString()!));
            Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
            if (AvifdecCopiesLuma(raster, data))
            {
                // avifdec writes an 8-bit gray image without alpha or transforms as the luma plane itself, skipping the range
                // expansion that every other image gets; Egami expands it.
                return;
            }

            for (int y = 0; y < actual.Height; y++)
            {
                ReadOnlySpan<Rgba16> a = actual.Row(y), e = expected.Row(y);
                for (int x = 0; x < actual.Width; x++)
                {
                    Assert.True(a[x].Equals(e[x]), $"{file} at ({x}, {y}): Egami {Show(a[x])}, avifdec {Show(e[x])}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(YuvFiles))]
    public void YuvLoadEqualsDav1d(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        using (YuvRaster planes = YuvRaster.Load(Path.Combine(Corpus, file)))
        {
            Assert.Equal(entry.GetProperty("yuv_md5").GetString(), Md5(planes));
        }
    }

    [Fact]
    public void MetadataComesWithThePixels()
    {
        using Raster paris = Raster.Load(Path.Combine(Corpus, "libavif", "paris_icc_exif_xmp.avif"));
        Assert.False(paris.Metadata.IccProfile.IsEmpty);
        Assert.NotNull(paris.Metadata.Exif);
        Assert.False(paris.Metadata.Xmp.IsEmpty);
        Assert.Null(paris.Metadata.CodePoints);   // the ICC profile says what the pixels mean (design question 8)
        Assert.IsType<Egami.Formats.Avif.AvifDetails>(paris.Metadata.FormatDetails);

        using Raster pq = Raster.Load(Path.Combine(Corpus, "duck-pq.avif"));
        Assert.Equal(new ColorCodePoints(9, 16, 0, true), pq.Metadata.CodePoints);
        Assert.True(pq.Metadata.GetColorProfile().IsEquivalentTo(ColorProfile.Rec2100Pq));
        Assert.NotNull(pq.Metadata.Hdr?.MaxContentLightLevel);
    }

    [Fact]
    public void IrotAndImirAreTheOrientation()
    {
        string path = Path.Combine(Corpus, "duck-irot2-imir0.avif");
        RasterInfo info = Raster.Probe(path);
        using Raster raster = Raster.Load(path);
        Assert.NotEqual(ImageOrientation.Normal, info.Orientation);
        Assert.Equal(info.Orientation, raster.Metadata.Exif!.Orientation);
        Assert.Equal((info.Width, info.Height), (raster.Width, raster.Height));
    }

    [Fact]
    public void RegionIsACropOfTheImage()
    {
        string path = Path.Combine(Corpus, "duck-clap.avif");
        using Raster<Rgba8> full = Raster.Load<Rgba8>(path);
        Rect region = new(3, 5, full.Width / 2, full.Height / 3);
        using Raster<Rgba8> part = Raster.Load<Rgba8>(path, options: new ReaderOptions { Region = region });
        Assert.Equal((region.Width, region.Height), (part.Width, part.Height));
        for (int y = 0; y < part.Height; y++)
        {
            Assert.True(part.Row(y).SequenceEqual(full.Row(region.Y + y).Slice(region.X, region.Width)), $"row {y}");
        }
    }

    [Theory]
    [InlineData("duck-grain.avif")]
    [InlineData("duck-grain-test10-10bit.avif")]
    public void FilmGrainCanBeLeftOut(string file)
    {
        JsonElement item = Manifest.Value.GetProperty(file).GetProperty("items")[0];
        string path = Path.Combine(Corpus, file);
        using YuvRaster clean = YuvRaster.Load(path, options: new Egami.Formats.Avif.AvifReaderOptions { FilmGrain = false });
        Assert.Equal(item.GetProperty("dav1d_no_grain_md5").GetString(), Md5(clean));
        Egami.Formats.Avif.AvifFilmGrain grain = ((Egami.Formats.Avif.AvifDetails)clean.Metadata.FormatDetails!).FilmGrain!;
        Assert.NotEmpty(grain.ScalingPointsY);

        using Raster grained = Raster.Load(path);
        using Raster plain = Raster.Load(path, options: new Egami.Formats.Avif.AvifReaderOptions { FilmGrain = false });
        Assert.NotNull(((Egami.Formats.Avif.AvifDetails)grained.Metadata.FormatDetails!).FilmGrain);
        Assert.False(grained.As<Rgba16>().Row(10).SequenceEqual(plain.As<Rgba16>().Row(10)));
    }

    [Fact]
    public void RegionDecodesOnlyTheGridCellsItTouches()
    {
        // duck-grid-2x2's last cell (item 5, 7757 bytes at 19634) is broken: a full load fails, a region in the first cell does not.
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "duck-grid-2x2.avif"));
        using Raster<Rgba8> full = Raster.Load<Rgba8>(data);
        data.AsSpan(19634, 64).Fill(0xFF);
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(data));
        Rect region = new(10, 20, 200, 150);
        using Raster<Rgba8> part = Raster.Load<Rgba8>(data, options: new ReaderOptions { Region = region });
        for (int y = 0; y < part.Height; y++)
        {
            Assert.True(part.Row(y).SequenceEqual(full.Row(region.Y + y).Slice(region.X, region.Width)), $"row {y}");
        }
    }

    /// <summary>Gets whether avifdec's PNG of this file is the raw luma (see apps/shared/avifpng.c's copyYPlane), which only differs for limited range.</summary>
    private static bool AvifdecCopiesLuma(Raster raster, byte[] data)
    {
        Egami.Formats.Avif.AvifDetails details = (Egami.Formats.Avif.AvifDetails)raster.Metadata.FormatDetails!;
        RasterInfo info = Raster.Probe(data);
        return details.Chroma == ChromaFormat.Yuv400 && details.BitDepth == 8 && !details.HasAlpha && !details.CodePoints.FullRange
            && info.Orientation == ImageOrientation.Normal && info.Width == ReadYuvWidth(data);

        static int ReadYuvWidth(byte[] data)
        {
            using YuvRaster planes = YuvRaster.Load(data);
            return planes.Width;
        }
    }

    /// <summary>The MD5 of the planes as a y4m writes them: Y, U, V, 8-bit samples as bytes, else 16-bit little endian.</summary>
    private static string Md5(YuvRaster planes)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        switch (planes)
        {
            case YuvRaster<byte> bytes:
                foreach (YuvPlane<byte>? plane in new[] { bytes.Y, bytes.U, bytes.V })
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        md5.AppendData(plane.Row(y));
                    }
                }

                break;
            case YuvRaster<ushort> words:
                foreach (YuvPlane<ushort>? plane in new[] { words.Y, words.U, words.V })
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        md5.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(plane.Row(y)));
                    }
                }

                break;
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    private static string Show(Rgba16 p) => $"({p.R}, {p.G}, {p.B}, {p.A})";
}
