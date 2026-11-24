using System.Text;

using Egami.Formats;
using Egami.Formats.Eps;
using Egami.Formats.Jpeg;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Eps;

/// <summary>Photoshop's own image in an EPS file, described by its %ImageData comment.</summary>
public class EpsPhotoshopTests
{
    private const string Header = "%!PS-Adobe-3.0 EPSF-3.0\n%%Creator: Adobe Photoshop Version 25.0\n%%BoundingBox: 0 0 4 3\n%%HiResBoundingBox: 0 0 4 3\n%%EndComments\n";

    /// <summary>Builds a Photoshop-style EPS: the comments, %ImageData, a little PostScript, the start line and the data.</summary>
    internal static byte[] Build(string imageData, byte[] data, string extra = "")
    {
        string head = Header + extra + "%ImageData: " + imageData + " \"beginimage\"\n%%BeginProlog\n/beginimage { image } def\n%%EndProlog\nbeginimage\n";
        return [.. Encoding.Latin1.GetBytes(head), .. data, .. "\n%%EOF\n"u8];
    }

    private static byte[] Hex(byte[] data)
    {
        string hex = Convert.ToHexString(data);
        StringBuilder lines = new();
        for (int i = 0; i < hex.Length; i += 10)
        {
            lines.Append(hex.AsSpan(i, Math.Min(10, hex.Length - i))).Append('\n');
        }

        return Encoding.ASCII.GetBytes(lines.ToString());
    }

    private static byte[] Samples(int count, int seed) => [.. Enumerable.Range(0, count).Select(i => (byte)((i * 37) + seed))];

    [Fact]
    public void GrayBinaryLoads()
    {
        byte[] samples = Samples(12, 5);
        byte[] eps = Build("4 3 8 1 0 4 1", samples);
        Assert.Equal(EpsPreviewKind.PhotoshopImage, EpsFile.Load(eps).Preview);

        RasterInfo info = Raster.Probe(eps);
        Assert.Equal((4, 3), (info.Width, info.Height));
        Assert.Equal(Gray8.Layout, info.Layout);

        using Raster raster = Raster.Load(eps);
        Raster<Gray8> gray = Assert.IsType<Raster<Gray8>>(raster);
        Assert.Equal(samples, Enumerable.Range(0, 3).SelectMany(y => gray.Row(y).ToArray().Select(p => p.L)));
        Assert.Equal(EpsPreviewKind.PhotoshopImage, Assert.IsType<EpsDetails>(raster.Metadata.FormatDetails).Preview);
        Assert.DoesNotContain(raster.Metadata.Warnings, w => w.Id == "Eps.PreviewLoaded");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public void RgbIsReadInterleavedOrByLines(int blockSize, bool hex)
    {
        // The same pixels stored pixel by pixel (block 1) or a row of red, then green, then blue (block = 4 bytes).
        byte[] rgb = Samples(4 * 3 * 3, 11);
        byte[] stored = blockSize == 1
            ? rgb
            : [.. Enumerable.Range(0, 3).SelectMany(y => Enumerable.Range(0, 3).SelectMany(c => Enumerable.Range(0, 4).Select(x => rgb[(((y * 4) + x) * 3) + c])))];
        byte[] eps = Build($"4 3 8 3 0 {blockSize} {(hex ? 2 : 1)}", hex ? Hex(stored) : stored);

        using Raster<Rgb8> raster = Raster.Load<Rgb8>(eps);
        Assert.Equal(rgb, Enumerable.Range(0, 3).SelectMany(y => raster.Row(y).ToArray().SelectMany(p => new[] { p.R, p.G, p.B })));
    }

    [Fact]
    public void CmykKeepsItsInks()
    {
        byte[] cmyk = Samples(4 * 3 * 4, 3);
        using Raster raster = Raster.Load(Build("4 3 8 4 0 1 1", cmyk));
        Raster<Cmyk8> ink = Assert.IsType<Raster<Cmyk8>>(raster);
        Assert.Equal(cmyk, Enumerable.Range(0, 3).SelectMany(y => ink.Row(y).ToArray().SelectMany(p => new[] { p.C, p.M, p.Y, p.K })));
    }

    [Fact]
    public void PaddingChannelsAreSkipped()
    {
        // RGB with one padding channel after the colours: four bytes a pixel, the fourth ignored.
        byte[] rgb = Samples(4 * 3 * 3, 1);
        byte[] stored = [.. Enumerable.Range(0, 12).SelectMany(i => new[] { rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2], (byte)0xEE })];
        using Raster<Rgb8> raster = Raster.Load<Rgb8>(Build("4 3 8 3 1 1 1", stored));
        Assert.Equal(rgb, Enumerable.Range(0, 3).SelectMany(y => raster.Row(y).ToArray().SelectMany(p => new[] { p.R, p.G, p.B })));
    }

    [Fact]
    public void BitmapsAreBlackAndWhite()
    {
        // 10 pixels a row need two bytes; 1 bits are white, as PostScript's image operator draws them.
        byte[] rows = [0b1010_0000, 0b0100_0000, 0b0000_0000, 0b1100_0000, 0xFF, 0xFF];
        using Raster<Gray8> raster = Raster.Load<Gray8>(Build("10 3 1 1 0 2 1", rows));
        Assert.Equal(new byte[] { 255, 0, 255, 0, 0, 0, 0, 0, 0, 255 }, raster.Row(0).ToArray().Select(p => p.L));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 255, 255 }, raster.Row(1).ToArray().Select(p => p.L));
        Assert.All(raster.Row(2).ToArray(), p => Assert.Equal(255, p.L));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JpegDataIsDecoded(bool hex)
    {
        using Raster<Rgb8> source = Raster.Create(16, 8, new Rgb8(30, 140, 220));
        using MemoryStream jpeg = new();
        source.Save(jpeg, JpegFormat.Info, new JpegWriterOptions { Quality = 95 });
        byte[] eps = Build("16 8 8 3 0 1 3", hex ? Hex(jpeg.ToArray()) : jpeg.ToArray());

        using Raster<Rgb8> raster = Raster.Load<Rgb8>(eps);
        using Raster<Rgb8> direct = Raster.Load<Rgb8>(jpeg.ToArray());
        Assert.Equal((16, 8), (raster.Width, raster.Height));
        Assert.True(direct.Row(4).SequenceEqual(raster.Row(4)));
        Assert.Equal("EPS", raster.Metadata.FormatName);
    }

    [Fact]
    public void ThePhotoshopImageWinsOverTheTiffPreview()
    {
        using Raster<Rgb8> preview = Raster.Create(2, 2, new Rgb8(1, 2, 3));
        using MemoryStream tiff = new();
        preview.Save(tiff, TiffFormat.Info);
        byte[] eps = EpsTests.Binary(Build("4 3 8 1 0 4 1", Samples(12, 0)), tiff: tiff.ToArray());
        using Raster raster = Raster.Load(eps);
        Assert.Equal((4, 3), (raster.Width, raster.Height));
    }

    [Fact]
    public void DamagedImagesFallBackToThePreview()
    {
        using Raster<Rgb8> preview = Raster.Create(2, 2, new Rgb8(1, 2, 3));
        using MemoryStream tiff = new();
        preview.Save(tiff, TiffFormat.Info);

        // A block size that fits neither layout: the TIFF preview loads instead, with a warning.
        byte[] bad = EpsTests.Binary(Build("4 3 8 3 0 7 1", Samples(36, 0)), tiff: tiff.ToArray());
        using (Raster raster = Raster.Load(bad))
        {
            Assert.Equal((2, 2), (raster.Width, raster.Height));
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.ImageDataDamaged");
        }

        Assert.ThrowsAny<CorruptDataException>(() => Raster.Load(bad, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // Lab is left for the preview.
        byte[] lab = EpsTests.Binary(Build("4 3 8 2 0 1 1", Samples(36, 0)), tiff: tiff.ToArray());
        Assert.Equal(EpsPreviewKind.Tiff, EpsFile.Load(lab).Preview);
        using (Raster raster = Raster.Load(lab))
        {
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.ImageDataSkipped");
        }

        // Data cut short: the missing samples are 0, with a warning.
        using (Raster<Gray8> raster = Raster.Load<Gray8>(Build("4 3 8 1 0 4 1", Samples(6, 9)).AsMemory(..^7)))
        {
            Assert.Equal(0, raster.Row(2)[3].L);
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.ImageDataTruncated");
        }

        // A comment that cannot be read, and one whose start line never comes.
        EpsFile unreadable = EpsFile.Load(Build("4 3 16 1 0 4 1", Samples(12, 0)));
        Assert.Contains(unreadable.Warnings, w => w.Id == "Eps.ImageDataInvalid");
        Assert.Equal(EpsPreviewKind.None, unreadable.Preview);
        byte[] noStart = Encoding.ASCII.GetBytes(Header + "%ImageData: 4 3 8 1 0 4 1 \"beginimage\"\nshowpage\n");
        Assert.Equal(EpsPreviewKind.None, EpsFile.Load(noStart).Preview);
    }

    [Fact]
    public void TheStartLineInsideBeginBinaryIsFound()
    {
        // As Photoshop 3 writes CMYK: %ImageData after the setup, a padding channel, a row of each plane in turn, in hex, and
        // the start line and data wrapped in %%BeginBinary.
        byte[] cmyk = Samples(3 * 2 * 4, 21);
        byte[] stored = [.. Enumerable.Range(0, 2).SelectMany(y => Enumerable.Range(0, 5).SelectMany(c => Enumerable.Range(0, 3).Select(x => c < 4 ? cmyk[(((y * 3) + x) * 4) + c] : (byte)0)))];
        byte[] hex = Hex(stored);
        string head = Header + "%%BeginProlog\n%%EndProlog\n%%BeginSetup\n%%EndSetup\n%ImageData: 3 2 8 4 1 3 2 \"beginimage\"\n/beginimage { } def\n"
            + $"%%BeginBinary: {hex.Length + 11}\nbeginimage\n";
        byte[] eps = [.. Encoding.ASCII.GetBytes(head), .. hex, .. "%%EndBinary\n%%EOF\n"u8];
        using Raster<Cmyk8> raster = Raster.Load<Cmyk8>(eps);
        Assert.Equal(cmyk, Enumerable.Range(0, 2).SelectMany(y => raster.Row(y).ToArray().SelectMany(p => new[] { p.C, p.M, p.Y, p.K })));
    }

    [Fact]
    public void APlacedPhotoshopFileIsNotTheImage()
    {
        // An Illustrator drawing with a Photoshop EPS placed in it: the placed file's image is part of the drawing, not the
        // drawing, so the outer file has no image of its own.
        byte[] placed = Build("4 3 8 1 0 4 1", Samples(12, 0));
        byte[] eps = [.. "%!PS-Adobe-3.0 EPSF-3.0\n%%Creator: Adobe Illustrator\n%%BoundingBox: 0 0 50 40\n%%EndComments\n%%BeginDocument: photo.eps\n"u8,
            .. placed, .. "%%EndDocument\nshowpage\n%%EOF\n"u8];
        EpsFile file = EpsFile.Load(eps);
        Assert.Equal(EpsPreviewKind.None, file.Preview);
        Assert.Equal(50, file.BoundingBox?.Width);
        Assert.Equal("Adobe Illustrator", file.Creator);
    }

    [Fact]
    public void TheProfileAndResourcesComeAfterTheImageDataComment()
    {
        byte[] profile = Samples(16, 77);
        string extra = string.Empty;
        string tail = "%%BeginICCProfile: (Photoshop Profile) -1 Hex\n%" + Convert.ToHexString(profile) + "\n%%EndICCProfile\n";
        string head = Header + extra + "%ImageData: 4 3 8 1 0 4 1 \"beginimage\"\n" + tail + "beginimage\n";
        byte[] eps = [.. Encoding.ASCII.GetBytes(head), .. Samples(12, 0)];
        using Raster raster = Raster.Load(eps);
        Assert.Equal(profile, raster.Metadata.IccProfile.ToArray());
    }
}
