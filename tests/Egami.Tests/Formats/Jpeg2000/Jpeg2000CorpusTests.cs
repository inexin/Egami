using System.IO.Compression;

using Egami.Formats;
using Egami.Formats.Jpeg2000;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg2000;

/// <summary>
/// The corpus tools/make-jpeg2000-references.py builds with opj_compress, one file per coding option, against opj_decompress's
/// samples: reversible files identical, irreversible ones within 1, at full resolution and with one level dropped. The YCbCr
/// files are checked against the planes encoded, which opj_decompress does not write.
/// </summary>
public class Jpeg2000CorpusTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg2000");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg2000");

    public static TheoryData<string, int> Files()
    {
        TheoryData<string, int> data = [];
        foreach (string file in Directory.GetFiles(Images).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file), 0);
            data.Add(Path.GetFileName(file), 1);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ComponentsMatchOpenJpeg(string name, int reduce)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Images, name));
        Jp2File file = Jp2File.Parse(bytes);
        J2kCodestream stream = J2kCodestream.Parse(file.Codestream, SafetyLimits.Default);
        J2kImage image = new J2kDecoder(stream, reduce, 0, Environment.ProcessorCount, default).Decode();
        string stem = Path.GetFileNameWithoutExtension(name);
        bool ycc = File.Exists(Path.Combine(References, $"{stem}_src_0.pgx.gz"));
        if (ycc && (reduce != 0 || !stream.MainCod.Reversible))
        {
            // Only the planes encoded are known for YCbCr, and only a lossless full-size decode returns them exactly.
            return;
        }

        for (int c = 0; c < image.Planes.Length; c++)
        {
            string reference = Path.Combine(References, ycc ? $"{stem}_src_{c}.pgx.gz" : $"{stem}_r{reduce}_{c}.pgx.gz");
            if (!File.Exists(reference))
            {
                reference = Path.Combine(References, $"{stem}_r{reduce}.pgx.gz");
            }

            (int width, int height, int[] samples) = ReadPgx(reference);
            Assert.Equal((width, height), (image.Widths[c], image.Heights[c]));
            int peak = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs(samples[i] - image.Planes[c][i]));
            }

            Assert.True(peak <= (stream.MainCod.Reversible ? 0 : 1), $"{name} at reduction {reduce}, component {c}: off by {peak}.");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void LoadsStreamsAndCrops(string name, int reduce)
    {
        if (reduce != 0)
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(Path.Combine(Images, name));
        using Raster whole = Raster.Load(bytes);
        using (RasterDecoder decoder = RasterDecoder.Open(bytes))
        {
            using Raster rows = decoder.ReadAll();
            Assert.Equal(Tiff.TiffReference.Bytes(whole), Tiff.TiffReference.Bytes(rows));
        }

        Rect region = new(whole.Width / 4, whole.Height / 3, whole.Width / 2, whole.Height / 2);
        using Raster cropped = Raster.Load(bytes, options: new ReaderOptions { Region = region });
        using Raster expected = whole.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
        Assert.Equal(Tiff.TiffReference.Bytes(expected), Tiff.TiffReference.Bytes(cropped));
        Assert.IsType<Jpeg2000Details>(whole.Metadata.FormatDetails);
    }

    [Fact]
    public void DetailsDescribeTheCodestream()
    {
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "rgb-comment.jp2")));
        Jpeg2000Details details = Assert.IsType<Jpeg2000Details>(raster.Metadata.FormatDetails);
        Assert.Equal(Jpeg2000Container.Jp2, details.Container);
        Assert.True(details.Reversible);
        Assert.Equal(3, details.Components.Count);
        Assert.Equal(new Jpeg2000Component(8, false, 1, 1), details.Components[0]);
        Assert.Equal(Jpeg2000ColorSpace.Srgb, details.ColorSpace);
        Assert.Contains("Egami test comment", details.Comments);
        Assert.Equal(Rgb8.Layout, raster.Layout);

        using Raster layers = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "rgb-layers.jp2")));
        Assert.Equal(4, ((Jpeg2000Details)layers.Metadata.FormatDetails!).QualityLayers);
        Assert.Equal(Jpeg2000ProgressionOrder.Rlcp, ((Jpeg2000Details)layers.Metadata.FormatDetails!).ProgressionOrder);
    }

    [Fact]
    public void QualityLayersCanBeLimited()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Images, "rgb-layers.jp2"));
        using Raster<Rgb8> all = Raster.Load<Rgb8>(bytes);
        using Raster<Rgb8> one = Raster.Load<Rgb8>(bytes, options: new Jpeg2000ReaderOptions { MaxQualityLayers = 1 });
        using Raster<Rgb8> four = Raster.Load<Rgb8>(bytes, options: new Jpeg2000ReaderOptions { MaxQualityLayers = 4 });
        Assert.Equal(Tiff.TiffReference.Bytes(all), Tiff.TiffReference.Bytes(four));
        Assert.NotEqual(Tiff.TiffReference.Bytes(all), Tiff.TiffReference.Bytes(one));
    }

    [Fact]
    public void AlphaAndDepthsLoadAsTheirNaturalTypes()
    {
        using Raster rgba = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "rgba.jp2")));
        Assert.Equal(Rgba8.Layout, rgba.Layout);
        using Raster gray12 = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "gray12-lossless.j2k")));
        Assert.Equal(Gray16.Layout, gray12.Layout);
        using Raster signed = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "signed12.j2k")));
        Assert.Contains(signed.Metadata.Warnings, w => w.Id == "Jpeg2000.SignedSamples");
    }

    [Fact]
    public void SubsampledChromaLoadsAsPlanes()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Images, "yuv420.j2k"));
        using YuvRaster planes = YuvRaster.Load(bytes);
        Assert.Equal(ChromaFormat.Yuv420, planes.ChromaFormat);
    }

    /// <summary>Reads a gzipped PGX file.</summary>
    private static (int Width, int Height, int[] Samples) ReadPgx(string path)
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pgx");
        try
        {
            using (FileStream source = File.OpenRead(path))
            using (GZipStream gzip = new(source, CompressionMode.Decompress))
            using (FileStream target = File.Create(temp))
            {
                gzip.CopyTo(target);
            }

            return Jpeg2000ConformanceTests.ReadPgx(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
