using System.Buffers.Binary;
using System.Text;

using Egami.Formats;
using Egami.Formats.Eps;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Eps;

/// <summary>EPS previews: the container (EpsFile), detection, Probe and Load of each preview kind, and damaged files.</summary>
public class EpsTests
{
    private const string Header = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 80 24\n%%Creator: (Egami \\(tests\\))\n%%Title: Box\n%%CreationDate: November 9, 1990\n%%EndComments\n";

    private const string Body = "%%EndProlog\n4 4 moveto 72 0 rlineto 0 16 rlineto -72 0 rlineto closepath 8 setlinewidth stroke\n%%EOF\n";

    /// <summary>Builds a text EPS: the header comments, an optional EPSI preview and a body.</summary>
    internal static byte[] Text(string header = Header, string? preview = null, string body = Body)
    {
        return Encoding.Latin1.GetBytes(header + (preview ?? string.Empty) + body);
    }

    /// <summary>Builds an EPSI preview section from rows of bytes, two hex digits a byte, one line a row.</summary>
    internal static string Preview(int width, int height, int depth, IEnumerable<byte[]> rows)
    {
        StringBuilder text = new($"%%BeginPreview: {width} {height} {depth} {height}\n");
        foreach (byte[] row in rows)
        {
            text.Append('%').Append(Convert.ToHexString(row)).Append('\n');
        }

        return text.Append("%%EndPreview\n").ToString();
    }

    /// <summary>Builds a DOS binary EPS: the 30-byte header, then the PostScript, metafile and TIFF sections in that order.</summary>
    internal static byte[] Binary(byte[] postScript, byte[]? wmf = null, byte[]? tiff = null, ushort checksum = 0xFFFF)
    {
        wmf ??= [];
        tiff ??= [];
        byte[] file = new byte[30 + postScript.Length + wmf.Length + tiff.Length];
        Span<byte> header = file;
        header[0] = 0xC5;
        header[1] = 0xD0;
        header[2] = 0xD3;
        header[3] = 0xC6;
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)postScript.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], wmf.Length == 0 ? 0u : (uint)(30 + postScript.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)wmf.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], tiff.Length == 0 ? 0u : (uint)(30 + postScript.Length + wmf.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)tiff.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], checksum);
        postScript.CopyTo(file, 30);
        wmf.CopyTo(file, 30 + postScript.Length);
        tiff.CopyTo(file, 30 + postScript.Length + wmf.Length);
        return file;
    }

    private static byte[] Tiff(out Raster<Rgb8> source)
    {
        source = Raster.Create(7, 5, new Rgb8(0, 0, 0));
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                source.Row(y)[x] = new Rgb8((byte)(x * 30), (byte)(y * 50), 99);
            }
        }

        using MemoryStream stream = new();
        source.Save(stream, TiffFormat.Info);
        return stream.ToArray();
    }

    [Fact]
    public void TheContainerGivesItsParts()
    {
        byte[] tiff = Tiff(out Raster<Rgb8> source);
        source.Dispose();
        byte[] ps = Text(Header.Replace("%%EndComments", "%%HiResBoundingBox: 0.5 0 79.25 23.5\n%%EndComments", StringComparison.Ordinal));
        byte[] data = Binary(ps, tiff: tiff);
        EpsFile file = EpsFile.Load(data);
        Assert.True(file.PostScript.Span.SequenceEqual(ps));
        Assert.True(file.TiffPreview.Span.SequenceEqual(tiff));
        Assert.True(file.WmfPreview.IsEmpty);
        Assert.Equal(EpsPreviewKind.Tiff, file.Preview);
        Assert.True(file.IsEncapsulated);
        Assert.Equal(new EpsBoundingBox(0.5, 0, 79.25, 23.5), file.BoundingBox);
        Assert.Equal("Egami (tests)", file.Creator);
        Assert.Equal("Box", file.Title);
        Assert.Equal("November 9, 1990", file.CreationDate);
        Assert.Empty(file.Warnings);

        EpsFile streamed = EpsFile.Load(new MemoryStream(data));
        Assert.Equal(file.BoundingBox, streamed.BoundingBox);
        Assert.True(streamed.TiffPreview.Span.SequenceEqual(tiff));
    }

    [Fact]
    public void TheTiffPreviewLoads()
    {
        byte[] data = Binary(Text(), tiff: Tiff(out Raster<Rgb8> source));
        using (source)
        {
            RasterInfo info = Raster.Probe(data);
            Assert.Same(EpsFormat.Info, info.Format);
            Assert.Equal((7, 5), (info.Width, info.Height));

            using Raster<Rgb8> raster = Raster.Load<Rgb8>(data);
            for (int y = 0; y < 5; y++)
            {
                Assert.True(source.Row(y).SequenceEqual(raster.Row(y)));
            }

            Assert.Equal("EPS", raster.Metadata.FormatName);
            EpsDetails details = Assert.IsType<EpsDetails>(raster.Metadata.FormatDetails);
            Assert.Equal(EpsPreviewKind.Tiff, details.Preview);
            Assert.Equal(new EpsBoundingBox(0, 0, 80, 24), details.BoundingBox);
            Assert.Equal("Box", details.Title);
            Assert.Contains(raster.Metadata.Text, t => t.Key == "Title" && t.Value == "Box");
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.PreviewLoaded");
        }
    }

    [Fact]
    public void TheEpsiExampleOfTheSpecificationLoads()
    {
        // EPSF 3.0, example 5: an 80 × 24 1-bit preview, a black frame (1 bits) around a white middle (0 bits).
        byte[][] rows = [.. Enumerable.Range(0, 24).Select(y => y is >= 8 and < 16
            ? (byte[])[0xFF, .. new byte[8], 0xFF]
            : Enumerable.Repeat((byte)0xFF, 10).ToArray())];
        byte[] data = Text(preview: Preview(80, 24, 1, rows));
        RasterInfo info = Raster.Probe(data);
        Assert.Equal((80, 24), (info.Width, info.Height));

        using Raster raster = Raster.Load(data);
        Raster<Gray8> gray = Assert.IsType<Raster<Gray8>>(raster);
        Assert.Equal(0, gray.Row(0)[0].L);
        Assert.Equal(0, gray.Row(12)[3].L);
        Assert.Equal(255, gray.Row(12)[40].L);
        Assert.Equal(EpsPreviewKind.Epsi, Assert.IsType<EpsDetails>(raster.Metadata.FormatDetails).Preview);
        Assert.Equal(["Eps.PreviewLoaded"], raster.Metadata.Warnings.Select(w => w.Id));
    }

    [Theory]
    [InlineData(2, new byte[] { 0b00_01_10_11 }, new byte[] { 255, 170, 85, 0 })]
    [InlineData(4, new byte[] { 0x0F, 0x58 }, new byte[] { 255, 0, 170, 119 })]
    [InlineData(8, new byte[] { 0, 255, 51, 204 }, new byte[] { 255, 0, 204, 51 })]
    public void DeeperEpsiPreviewsAreGray(int depth, byte[] row, byte[] expected)
    {
        using Raster<Gray8> raster = Raster.Load<Gray8>(Text(preview: Preview(4, 1, depth, [row])));
        Assert.Equal(expected, raster.Row(0).ToArray().Select(p => p.L));
    }

    [Fact]
    public void RowsArePaddedToBytesAndLinesMaySplit()
    {
        // 3 pixels of 1 bit need a byte a row; the second row's digits are split over two lines with stray characters.
        string preview = "%%BeginPreview: 3 2 1 3\n%A0\n%4\n% 0\n%%EndPreview\n";
        using Raster<Gray8> raster = Raster.Load<Gray8>(Text(preview: preview));
        Assert.Equal(new byte[] { 0, 255, 0 }, raster.Row(0).ToArray().Select(p => p.L));
        Assert.Equal(new byte[] { 255, 0, 255 }, raster.Row(1).ToArray().Select(p => p.L));
    }

    [Fact]
    public void PostScriptOnlyProbesAtItsBoundingBoxAndFailsToLoad()
    {
        byte[] data = Text(Header.Replace("0 0 80 24", "10 20 110.5 70", StringComparison.Ordinal));
        RasterInfo info = Raster.Probe(data);
        Assert.Same(EpsFormat.Info, info.Format);
        Assert.Equal((101, 50), (info.Width, info.Height));

        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load(data));
        Assert.Contains("PostScript is not run", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainPostScriptIsDetectedAndRefused()
    {
        byte[] data = Encoding.ASCII.GetBytes("%!PS\n/Times-Roman findfont 12 scalefont setfont 72 72 moveto (Hi) show showpage\n");
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(data));
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load(data));
        Assert.Contains("not EPS with a preview", e.Message, StringComparison.Ordinal);
        Assert.False(EpsFile.Load(data).IsEncapsulated);
    }

    [Fact]
    public void TheBoundingBoxMayComeAtTheEnd()
    {
        byte[] data = Text(Header.Replace("0 0 80 24", "(atend)", StringComparison.Ordinal), body: Body + "%%Trailer\n%%BoundingBox: 0 0 30 40\n");
        Assert.Equal(new EpsBoundingBox(0, 0, 30, 40), EpsFile.Load(data).BoundingBox);
    }

    [Fact]
    public void PhotoshopSectionsAreRead()
    {
        byte[] profile = [.. Enumerable.Range(0, 40).Select(i => (byte)i)];
        string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'/><?xpacket end='w'?>";
        string header = Header + "%%BeginICCProfile: (Photoshop Profile) -1 Hex\n%" + Convert.ToHexString(profile, 0, 20) + "\n%" + Convert.ToHexString(profile, 20, 20) + "\n%%EndICCProfile\n"
            + "%begin_xml_code\n" + xmp + "\n%end_xml_code\n%ImageData: 7 5 8 3 0 7 2 \"beginimage\"\n";
        byte[] data = Binary(Text(header, body: string.Empty), tiff: Tiff(out Raster<Rgb8> source));
        source.Dispose();
        EpsFile file = EpsFile.Load(data);
        Assert.Equal(profile, file.IccProfile.ToArray());
        Assert.Equal(xmp, Encoding.UTF8.GetString(file.Xmp.Span));

        using Raster raster = Raster.Load(data);
        Assert.Equal(profile, raster.Metadata.IccProfile.ToArray());
        Assert.Equal(xmp, Encoding.UTF8.GetString(raster.Metadata.Xmp.Span));
    }

    [Fact]
    public void AMetafilePreviewNeedsAMetafileFormat()
    {
        byte[] wmf = [0x01, 0x00, 0x09, 0x00, 0x00, 0x03, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] data = Binary(Text(), wmf: wmf);
        Assert.Equal(EpsPreviewKind.Wmf, EpsFile.Load(data).Preview);
        RasterInfo info = Raster.Probe(data);
        Assert.Equal((107, 32), (info.Width, info.Height));
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load(data));
        Assert.Contains("Egami.Metafiles", e.Message, StringComparison.Ordinal);

        byte[] withEpsi = Binary(Text(preview: Preview(8, 1, 1, [[0x0F]])), wmf: wmf);
        using Raster raster = Raster.Load(withEpsi);
        Assert.Equal(EpsPreviewKind.Epsi, Assert.IsType<EpsDetails>(raster.Metadata.FormatDetails).Preview);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.MetafileSkipped");
    }

    [Fact]
    public void SectionsPastTheEndAreWarnedAbout()
    {
        byte[] tiff = Tiff(out Raster<Rgb8> source);
        source.Dispose();
        byte[] data = Binary(Text(preview: Preview(8, 1, 1, [[0xF0]])), tiff: tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)data.Length + 10);
        EpsFile file = EpsFile.Load(data);
        Assert.Equal(EpsPreviewKind.Epsi, file.Preview);
        Assert.Contains(file.Warnings, w => w.Id == "Eps.SectionPastEnd");

        using Raster raster = Raster.Load(data);
        Assert.Equal((8, 1), (raster.Width, raster.Height));
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.SectionPastEnd");
        Assert.ThrowsAny<CorruptDataException>(() => Raster.Load(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));

        byte[] cut = Binary(Text(), tiff: tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(24), (uint)tiff.Length + 1000);
        Assert.Contains(EpsFile.Load(cut).Warnings, w => w.Id == "Eps.SectionPastEnd");
    }

    [Fact]
    public void TheChecksumIsChecked()
    {
        byte[] data = Binary(Text(preview: Preview(8, 1, 1, [[0xF0]])), checksum: 0x1234);
        Assert.Contains(EpsFile.Load(data).Warnings, w => w.Id == "Eps.ChecksumMismatch");

        ushort words = 0;
        for (int i = 0; i < 28; i += 2)
        {
            words ^= BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), words);
        Assert.Empty(EpsFile.Load(data).Warnings);
    }

    [Fact]
    public void OverlappingSectionsAreWarnedAbout()
    {
        byte[] data = Binary(Text(preview: Preview(8, 1, 1, [[0xF0]])), tiff: Tiff(out Raster<Rgb8> source));
        source.Dispose();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)(data.Length - 30));
        Assert.Contains(EpsFile.Load(data).Warnings, w => w.Id == "Eps.SectionsOverlap");
    }

    [Fact]
    public void DamagedFilesNeverCrash()
    {
        Assert.Throws<CorruptDataException>(() => EpsFile.Load(new byte[] { 0xC5, 0xD0, 0xD3, 0xC6, 1, 2 }));
        Assert.Throws<CorruptDataException>(() => EpsFile.Load("GIF89a"u8.ToArray()));
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(new byte[] { 0xC5, 0xD0, 0xD3, 0xC6, 1, 2 }));

        // A preview too large for the limits, a truncated one and one with a bad header.
        byte[] huge = Text(preview: "%%BeginPreview: 100000 100000 8 1\n%00\n%%EndPreview\n");
        Assert.Throws<LimitExceededException>(() => Raster.Load(huge));
        using (Raster<Gray8> raster = Raster.Load<Gray8>(Text(preview: "%%BeginPreview: 8 2 1 1\n%00\n%%EndPreview\n")))
        {
            Assert.Equal(255, raster.Row(1)[0].L);
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Eps.PreviewTruncated");
        }

        Assert.Throws<CorruptDataException>(() => Raster.Load(Text(preview: "%%BeginPreview: 8 2 1 1\n%00\n%%EndPreview\n"), options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        EpsFile bad = EpsFile.Load(Text(preview: "%%BeginPreview: 8 x 3 1\n%00\n%%EndPreview\n"));
        Assert.Equal(EpsPreviewKind.None, bad.Preview);
        Assert.Contains(bad.Warnings, w => w.Id == "Eps.PreviewInvalid");
        Assert.Contains(EpsFile.Load(Text(Header.Replace("0 0 80 24", "0 0 zero 24", StringComparison.Ordinal))).Warnings, w => w.Id == "Eps.BoundingBoxInvalid");
    }

    [Fact]
    public void OtherFormatsAreStillDetected()
    {
        Assert.False(EpsFormat.Info.Matches("\x89PNG\r\n\x1a\n"u8));
        Assert.False(EpsFormat.Info.Matches("%PDF-1.7"u8));
        Assert.True(EpsFormat.Info.Matches("%!PS-Adobe-3.0 EPSF-3.0"u8));
        Assert.True(EpsFormat.Info.Matches([0xC5, 0xD0, 0xD3, 0xC6]));
        Assert.Same(EpsFormat.Info, EgamiContext.Default.FindFormat("ept"));
    }
}
