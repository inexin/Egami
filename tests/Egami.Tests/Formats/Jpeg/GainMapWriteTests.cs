using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Making gain maps (Phase 2.9.1 task 13, HDR Design question 10): an HDR raster saved with JpegWriterOptions.GainMap comes back
/// as HDR, and GainMap.Create follows its options.
/// </summary>
public class GainMapWriteTests(ITestOutputHelper output)
{
    private static string Hdr(string name) => Path.Combine(AppContext.BaseDirectory, "images", name);

    [Theory]
    [InlineData("radiance/desk-rle.hdr")]
    [InlineData("exr/half-zip.exr")]
    public void AnHdrRasterSavedAsUltraHdrComesBack(string name)
    {
        using Raster<RgbaF32> hdr = Raster.Load<RgbaF32>(Hdr(name));
        using MemoryStream stream = new();
        // Quality 100 and 4:4:4, so what is measured is the gain map; at quality 95 and 4:2:0 the JPEG steps of this small,
        // high-contrast picture dominate near black.
        hdr.Save(stream, JpegFormat.Info, new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Subsample444, GainMap = new GainMapOptions { Quality = 100 } });
        byte[] file = stream.ToArray();

        using Raster sdr = Raster.Load(file);
        GainMap map = Assert.IsType<GainMap>(sdr.Metadata.GainMap);
        Assert.Equal((hdr.Width, hdr.Height, 3), (map.Width, map.Height, map.Channels));
        Assert.Equal(PixelChannelType.Unorm8, sdr.Layout.ChannelType);

        // At the map's full capacity the HDR raster comes back, within the 8-bit steps of the base image and the map.
        using Raster<RgbaF32> back = Raster.Load<RgbaF32>(file, options: new ReaderOptions { Hdr = new HdrDecode() });
        double sum = 0;
        long count = 0;
        for (int y = 0; y < hdr.Height; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
            {
                RgbaF32 want = hdr.Row(y)[x], got = back.Row(y)[x];
                foreach ((float w, float g) in new[] { (want.R, got.R), (want.G, got.G), (want.B, got.B) })
                {
                    sum += Math.Abs(w - g) / Math.Max(Math.Max(w, g), 0.05);
                    count++;
                }
            }
        }

        double mean = sum / count;
        output.WriteLine($"{name}: mean relative error {mean:P2}, map max boost {Math.Pow(2, map.Info.GainMapMax.Red):0.##}");
        Assert.True(mean < 0.03, $"mean relative error {mean:P2}");
        Assert.Equal(map.Info.GainMapMax.Red, map.Info.HdrCapacityMax, 1e-6);
    }

    [Fact]
    public void OptionsSetTheMapsShape()
    {
        using Raster<RgbaF32> hdr = Raster.Load<RgbaF32>(Hdr("radiance/desk-rle.hdr"));
        GainMap gray = GainMap.Create(hdr, options: new GainMapOptions { Scale = 4, MultiChannel = false, Gamma = 2, Headroom = 8 });
        Assert.Equal((hdr.Width / 4, hdr.Height / 4, 1), (gray.Width, gray.Height, gray.Channels));
        Assert.Equal(2, gray.Info.Gamma.Red);
        Assert.Equal(3, gray.Info.HdrCapacityMax, 1e-9);
        Assert.Equal(1.0 / 64, gray.Info.OffsetSdr.Green);

        GainMap capped = GainMap.Create(hdr, options: new GainMapOptions { MaxContentBoost = 2 });
        Assert.Equal(1, capped.Info.GainMapMax.Red, 1e-9);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GainMapOptions { Scale = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GainMapOptions { Quality = 101 });
    }

    [Fact]
    public void AGivenSdrRenditionIsTheBase()
    {
        using Raster<RgbaF32> hdr = Raster.Load<RgbaF32>(Hdr("radiance/desk-rle.hdr"));
        using Raster<Rgb8> sdr = hdr.Derive(ops => ops.ToneMap(ToneMapping.Reinhard), TestContext.Current.CancellationToken).As<Rgb8>();
        sdr.Metadata.GainMap = GainMap.Create(hdr, sdr);
        using MemoryStream stream = new();
        sdr.Save(stream, JpegFormat.Info, new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Subsample444 });
        using Raster<Rgb8> back = Raster.Load<Rgb8>(stream.ToArray());
        int worst = 0;
        for (int y = 0; y < sdr.Height; y++)
        {
            for (int x = 0; x < sdr.Width; x++)
            {
                worst = Math.Max(worst, Math.Abs(sdr.Row(y)[x].G - back.Row(y)[x].G));
            }
        }

        Assert.True(worst <= 3, $"base differs by {worst}");
        Assert.NotNull(back.Metadata.GainMap);
    }

    [Fact]
    public void AStreamedEncodeCannotMakeAMap()
    {
        using MemoryStream stream = new();
        Assert.Throws<NotSupportedException>(() =>
        {
            using RasterEncoder<RgbaF32> encoder = RasterEncoder.Create<RgbaF32>(stream, JpegFormat.Info, 8, 8, new JpegWriterOptions { GainMap = new GainMapOptions() });
            using Raster<RgbaF32> band = Raster.Create<RgbaF32>(8, 8);
            encoder.WriteRows(band);
            encoder.Complete();
        });
    }

    /// <summary>
    /// Writes the files tools/check-gainmap-interop.py has libultrahdr decode: Ultra HDR JPEGs Egami made from HDR rasters, the
    /// corpus saved again, and saved after a crop and turn; each with Egami's own decode at a headroom of 4 (half-float RGBA, after
    /// an int32 width and height) and its primaries.
    /// </summary>
    [Fact]
    public void WritesInteropFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        string folder = Path.Combine(directory!, "gainmap");
        Directory.CreateDirectory(folder);
        JpegWriterOptions options = new() { Quality = 100, Subsampling = ChromaSubsampling.Subsample444 };
        foreach (string name in new[] { "radiance/desk-rle.hdr", "exr/half-zip.exr" })
        {
            using Raster<RgbaF32> hdr = Raster.Load<RgbaF32>(Hdr(name));
            string stem = "made-" + Path.GetFileNameWithoutExtension(name);
            hdr.Save(Path.Combine(folder, stem + ".jpg"), new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Subsample444, GainMap = new GainMapOptions { Quality = 100 } });
            hdr.Save(Path.Combine(folder, stem + "-quarter.jpg"), new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Subsample444, GainMap = new GainMapOptions { Quality = 100, Scale = 4, MultiChannel = false } });
        }

        foreach (string path in Directory.GetFiles(Hdr("gainmap"), "*.jpg"))
        {
            using Raster sdr = Raster.Load(path);
            sdr.Save(Path.Combine(folder, "same-" + Path.GetFileName(path)), options);
            using Raster moved = sdr.Derive(o => o.Crop(37, 21, 150, 101).Rotate(90).FlipHorizontal(), TestContext.Current.CancellationToken);
            moved.Save(Path.Combine(folder, "moved-" + Path.GetFileName(path)), options);
        }

        foreach (string path in Directory.GetFiles(folder, "*.jpg"))
        {
            using Raster<RgbaF16> hdr = Raster.Load<RgbaF16>(path, options: new ReaderOptions { Hdr = new HdrDecode { Headroom = 4 } });
            using (FileStream raw = File.Create(path + ".egami.raw"))
            {
                raw.Write(BitConverter.GetBytes(hdr.Width));
                raw.Write(BitConverter.GetBytes(hdr.Height));
                for (int y = 0; y < hdr.Height; y++)
                {
                    raw.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(hdr.Row(y)));
                }
            }

            ColorPrimaries p = hdr.Metadata.Primaries ?? ColorPrimaries.Rec709;
            File.WriteAllText(path + ".primaries", string.Join(' ', new[] { p.Red, p.Green, p.Blue, p.White }.SelectMany(c => new[] { c.X, c.Y }).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        }
    }
}
