using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Jpeg2000;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg2000;

/// <summary>
/// High-Throughput JPEG 2000 (T.814): the corpus tools/make-htj2k-references.py builds with OpenJPH's ojph_compress, against
/// opj_decompress's samples (reversible files identical, irreversible ones within 1, at full resolution and with one level
/// dropped), and the HT parts of the codestream syntax.
/// </summary>
public class HtJ2kCorpusTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "htj2k");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "htj2k");

    public static TheoryData<string, int> Files()
    {
        TheoryData<string, int> data = [];
        foreach (string file in Directory.GetFiles(Images).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file), 0);
            if (!Path.GetFileName(file).StartsWith("rgb-levels0", StringComparison.Ordinal))
            {
                data.Add(Path.GetFileName(file), 1);
            }
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
        Assert.True(stream.HighThroughput);
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

            (int width, int height, int[] samples) = Jpeg2000CorpusTests.ReadPgx(reference);
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
        Jpeg2000Details details = Assert.IsType<Jpeg2000Details>(whole.Metadata.FormatDetails);
        Assert.True(details.HighThroughput);
        Assert.Equal(name.EndsWith(".jph", StringComparison.Ordinal) ? Jpeg2000Container.Jph : Jpeg2000Container.Codestream, details.Container);
    }

    [Fact]
    public void DetailsDescribeAnHtFile()
    {
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "rgb-comment.jph")));
        Jpeg2000Details details = Assert.IsType<Jpeg2000Details>(raster.Metadata.FormatDetails);
        Assert.Equal(Jpeg2000Container.Jph, details.Container);
        Assert.True(details.HighThroughput);
        Assert.NotEqual(0, details.Profile & 0x4000);
        Assert.True(details.Reversible);
        Assert.Contains("Egami HT test", details.Comments);
        Assert.Equal(Rgb8.Layout, raster.Layout);
        Assert.True(((Jpeg2000Details)details.Copy()).HighThroughput);

        using Raster legacy = Raster.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "jpeg2000", "rgb-comment.jp2")));
        Assert.False(((Jpeg2000Details)legacy.Metadata.FormatDetails!).HighThroughput);
    }

    [Fact]
    public void DepthsAndChromaLoadAsTheirNaturalTypes()
    {
        using Raster gray16 = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "gray16-lossless.jph")));
        Assert.Equal(Gray16.Layout, gray16.Layout);
        using Raster signed = Raster.Load(File.ReadAllBytes(Path.Combine(Images, "signed12.j2c")));
        Assert.Contains(signed.Metadata.Warnings, w => w.Id == "Jpeg2000.SignedSamples");
        using YuvRaster planes = YuvRaster.Load(File.ReadAllBytes(Path.Combine(Images, "yuv420.j2c")));
        Assert.Equal(ChromaFormat.Yuv420, planes.ChromaFormat);
    }

    [Fact]
    public void FormatKnowsTheHtNames()
    {
        Assert.Contains("jph", Jpeg2000Format.Info.Extensions);
        Assert.Contains("jhc", Jpeg2000Format.Info.Extensions);
        Assert.Contains("image/jph", Jpeg2000Format.Info.MimeTypes);
        Assert.Contains("image/jphc", Jpeg2000Format.Info.MimeTypes);
    }

    /// <summary>Each CxtVLC table of T.814 Annex C, per context, is a complete prefix code: every 7-bit lookahead decodes to one codeword.</summary>
    [Fact]
    public void CodeTablesAreCompletePrefixCodes()
    {
        for (int table = 0; table < 2; table++)
        {
            int[] hits = new int[8 * 128];
            int count = 0;
            foreach (uint e in J2kHtTables.Entries(table))
            {
                int context = (int)(e >> 23);
                int codeword = (int)(e >> 3) & 0x7F;
                int length = (int)(e & 7);
                Assert.InRange(length, 1, 7);
                Assert.Equal(0, codeword >> length);
                for (int rest = 0; rest < 1 << (7 - length); rest++)
                {
                    hits[(context << 7) | codeword | (rest << length)]++;
                }

                count++;
            }

            Assert.Equal(table == 0 ? 444 : 358, count);
            for (int context = 0; context < 8; context++)
            {
                ReadOnlySpan<int> slots = hits.AsSpan(context << 7, 128);
                if (slots.IndexOfAnyExcept(0) < 0)
                {
                    continue;
                }

                Assert.True(slots.IndexOfAnyExcept(1) < 0, $"Table {table}, context {context}: a lookahead decodes to no codeword or to two.");
            }
        }
    }

    [Fact]
    public void CapAndCpfAreRead()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Images, "rgb-lossless-j2c.j2c"));
        J2kCodestream plain = J2kCodestream.Parse(bytes, SafetyLimits.Default);
        Assert.True(plain.HtCapabilities >= 0);
        Assert.Equal(-1, plain.CorrespondingProfile);

        // A CPF segment after SIZ: Pcpf 3, so CPFnum 2.
        int siz = 2;
        int after = siz + 2 + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(siz + 2));
        byte[] withCpf = [.. bytes.AsSpan(0, after), 0xFF, 0x59, 0x00, 0x04, 0x00, 0x03, .. bytes.AsSpan(after)];
        J2kCodestream cpf = J2kCodestream.Parse(withCpf, SafetyLimits.Default);
        Assert.Equal(2, cpf.CorrespondingProfile);
        using (Raster expected = Raster.Load(bytes))
        using (Raster actual = Raster.Load(withCpf))
        {
            Assert.Equal(Tiff.TiffReference.Bytes(expected), Tiff.TiffReference.Bytes(actual));
        }

        // Part 2 capabilities are refused.
        int cap = bytes.AsSpan().IndexOf([(byte)0xFF, (byte)0x50]);
        Assert.True(cap > 0);
        byte[] part2 = (byte[])bytes.Clone();
        part2[cap + 4] |= 0x40;
        Assert.Throws<NotSupportedException>(() => J2kCodestream.Parse(part2, SafetyLimits.Default));
    }

    /// <summary>
    /// Fuzz findings (tests/images/fuzz/jpeg2000-regressions), each with an HT and a T.800 twin: a 3-component file with
    /// subsampled components, 50-row tiles and a damaged tail, read at a reduction and cropped, where a subsampled component's
    /// sample under a tile's first row belongs to the tile above. They load or throw an <see cref="EgamiException"/>.
    /// </summary>
    [Fact]
    public void FuzzRegressionsLoadOrRefuse()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "jpeg2000-regressions")))
        {
            byte[] data = File.ReadAllBytes(path);
            try
            {
                RasterInfo info = Raster.Probe(data);
                using Raster whole = Raster.Load(data);
                Rect region = new(info.Width / 4, info.Height / 4, Math.Max(1, info.Width / 2), Math.Max(1, info.Height / 2));
                using Raster<Rgba8> cropped = Raster.Load<Rgba8>(data, options: new ReaderOptions { Region = region, MinimumSize = new Size(Math.Max(1, info.Width / 5), 1) });
                Assert.True(cropped.Width > 0);
            }
            catch (EgamiException)
            {
            }
        }
    }

    [Fact]
    public void TruncatedHtStreamsDecodeWhatTheyHold()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Images, "rgb-lossless-j2c.j2c"));
        foreach (int length in new[] { bytes.Length * 3 / 4, bytes.Length / 2, bytes.Length / 5 })
        {
            using Raster raster = Raster.Load(bytes.AsMemory(0, length));
            Assert.Equal((96, 72), (raster.Width, raster.Height));
        }
    }
}
