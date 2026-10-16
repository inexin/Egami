using System.Text.Json;

using Egami.Colors;
using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Phase 2.9.2 task 15: tmap gain maps read into 2.9.1's GainMap (design question 12). The SDR or HDR base loads with the map in
/// RasterMetadata.GainMap, and ReaderOptions.Hdr renders the other rendition within 2.9.1's tolerance of libavif's
/// (avifgainmaputil tonemap, in .cache/avif/reference/gainmap) at headrooms of 2, 4 and 8.
/// </summary>
public class AvifGainMapTests(ITestOutputHelper output)
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");

    public static TheoryData<string, int> Renditions()
    {
        TheoryData<string, int> data = [];
        if (Summary() is JsonElement files)
        {
            foreach (JsonProperty file in files.EnumerateObject())
            {
                foreach (int stops in (int[])[1, 2, 3])
                {
                    if (file.Value.TryGetProperty($"h{stops}", out _))
                    {
                        data.Add(file.Name, stops);
                    }
                }
            }
        }

        if (data.Count == 0)
        {
            data.Add("gainmap-sdr-base.avif", 0);
        }

        return data;
    }

    [Theory]
    [InlineData("gainmap-sdr-base.avif", false)]
    [InlineData("gainmap-hdr-base.avif", true)]
    [InlineData("libavif/seine_sdr_gainmap_srgb.avif", false)]
    [InlineData("libavif/color_grid_gainmap_different_grid.avif", true)]
    public void BaseLoadsWithItsGainMap(string file, bool hdrBase)
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        GainMap map = Assert.IsType<GainMap>(raster.Metadata.GainMap);
        Assert.Equal(hdrBase, map.Info.BaseRenditionIsHdr);
        Assert.Equal(GainMapFormat.Iso21496, map.Info.Format);
        Assert.DoesNotContain(raster.Metadata.Warnings, w => w.Id == "Avif.GainMapDropped");
    }

    [Theory]
    [InlineData("libavif/seine_hdr_gainmap_wrongaltr.avif")]
    [InlineData("libavif/seine_sdr_gainmap_notmapbrand.avif")]
    public void GainMapsLibavifIgnoresAreIgnored(string file)
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        Assert.Null(raster.Metadata.GainMap);
    }

    [Theory]
    [InlineData("libavif/unsupported_gainmap_version.avif")]
    [InlineData("libavif/unsupported_gainmap_minimum_version.avif")]
    public void UnsupportedGainMapsAreDroppedWithAWarning(string file)
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        Assert.Null(raster.Metadata.GainMap);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Avif.GainMapDropped");
    }

    [Fact]
    public void ReadMetadataFalseSkipsTheGainMap()
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, "gainmap-sdr-base.avif"), options: new ReaderOptions { ReadMetadata = false });
        Assert.Null(raster.Metadata.GainMap);
    }

    [Theory]
    [MemberData(nameof(Renditions))]
    public void HdrIsCloseToLibavif(string file, int stops)
    {
        if (Summary() is not JsonElement files || stops == 0)
        {
            throw LocalCorpus.Skip("avif");
        }

        string path = Path.Combine(Corpus, file);
        if (!Raster.Load(path).Metadata.IccProfile.IsEmpty)
        {
            // avifgainmaputil cannot apply an ICC profile, so its rendition of such a file is not what the file means.
            return;
        }

        double headroom = Math.Pow(2, stops);
        ReaderOptions options = new() { Hdr = new HdrDecode { Headroom = headroom } };
        Assert.Equal(RgbaF16.Layout, Raster.Probe(path, options: options).Layout);
        using Raster raster = Raster.Load(path, options: options);
        Raster<RgbaF16> hdr = Assert.IsType<Raster<RgbaF16>>(raster);
        string reference = Path.Combine(AvifReferenceTests.Cache()!, "reference", "gainmap", files.GetProperty(file).GetProperty($"h{stops}").GetString()!);
        using Raster<Rgba16> want = Raster.Load<Rgba16>(reference);
        Assert.Equal((want.Width, want.Height), (hdr.Width, hdr.Height));

        // avifgainmaputil writes PQ in its cICP primaries, with SDR white at 203 cd/m²; Egami gives linear light in the base's.
        byte[] png = File.ReadAllBytes(reference);
        int cicp = png.AsSpan().IndexOf("cICP"u8);
        ColorPrimaries target = new ColorCodePoints(png[cicp + 4], 16, 0, true).GetPrimaries() ?? ColorPrimaries.Rec709;
        ColorPrimaries source = hdr.Metadata.Primaries ?? hdr.Metadata.CodePoints?.GetPrimaries() ?? ColorPrimaries.Rec709;
        Matrix3d m = target.ToXyz().Inverse() * Matrix3d.Bradford(source.White.ToXyz(), target.White.ToXyz()) * source.ToXyz();
        double sum = 0, worst = 0;
        int count = 0, far = 0;
        for (int y = 0; y < hdr.Height; y++)
        {
            for (int x = 0; x < hdr.Width; x++)
            {
                RgbaF16 p = hdr.Row(y)[x];
                Rgba16 w = want.Row(y)[x];
                ReadOnlySpan<double> theirs = [Pq(w.R), Pq(w.G), Pq(w.B)];
                for (int c = 0; c < 3; c++)
                {
                    double mine = (m[c, 0] * (float)p.R) + (m[c, 1] * (float)p.G) + (m[c, 2] * (float)p.B);
                    double error = Math.Abs(mine - theirs[c]) / Math.Max(Math.Max(mine, theirs[c]), 0.05);
                    sum += error;
                    worst = Math.Max(worst, error);
                    far += error > 0.1 ? 1 : 0;
                    count++;
                }
            }
        }

        double mean = sum / count;
        output.WriteLine($"{file} at {stops} stops: mean relative error {mean:P2}, worst {worst:P1}, {far} of {count} samples beyond 10%");
        GainMap map = Raster.Load(path).Metadata.GainMap!;
        bool scaled = map.Width != hdr.Width || map.Height != hdr.Height;
        Assert.True(mean < (scaled ? 0.06 : 0.015), $"mean relative error {mean:P2}");
        Assert.True(far < count / (scaled ? 5 : 40), $"{far} samples beyond 10%");
    }

    /// <summary>PQ (SMPTE ST 2084) to linear light with SDR white, 203 cd/m², at 1.</summary>
    private static double Pq(ushort code)
    {
        const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;
        double e = Math.Pow(code / 65535.0, 1 / M2);
        return Math.Pow(Math.Max(e - C1, 0) / (C2 - (C3 * e)), 1 / M1) * 10000 / 203;
    }

    private static JsonElement? Summary()
    {
        if (AvifReferenceTests.Cache() is not string cache || !File.Exists(Path.Combine(cache, "reference", "gainmap", "summary.json")))
        {
            return null;
        }

        return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "gainmap", "summary.json"))).RootElement.GetProperty("files").Clone();
    }
}
