using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Extended.Pcx;
using Egami.Formats.Extended.Sgi;
using Egami.Formats.Extended.SunRaster;
using Egami.Formats.Extended.Xbm;
using Egami.Formats.Extended.Xpm;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests;

/// <summary>Files built by hand from each format's specification, for the layouts Egami's writers do not produce.</summary>
public class FormatFileTests
{
    private static readonly Rgba8 Black = new(0, 0, 0, 255);
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void ReadsFarbfeld()
    {
        byte[] file = [.. "farbfeld"u8, 0, 0, 0, 2, 0, 0, 0, 1, 0xFF, 0xFF, 0, 0, 0x12, 0x34, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        using Raster raster = Raster.Load(file, TestImages.Context);
        Raster<Rgba16> pixels = Assert.IsType<Raster<Rgba16>>(raster);
        Assert.Equal(new Rgba16(65535, 0, 0x1234, 0x8000), pixels.Row(0)[0]);
        Assert.Equal(default, pixels.Row(0)[1]);
    }

    [Fact]
    public void ReadsWbmpWithExtensionHeaders()
    {
        // 10 × 2, white is 1; the header byte says a type 3 extension follows: one pair of a 1-byte name and 2-byte value.
        byte[] plain = [0, 0, 10, 2, 0b1010_0000, 0b1100_0000, 0xFF, 0x00];
        byte[] extended = [0, 0xE0, 0x12, (byte)'n', 1, 2, 10, 2, 0b1010_0000, 0b1100_0000, 0xFF, 0x00];
        foreach (byte[] file in (byte[][])[plain, extended])
        {
            using Raster<Gray8> raster = Raster.Load<Gray8>(file, Wbmp.WbmpFormat.Info, TestImages.Context);
            Assert.Equal((10, 2), (raster.Width, raster.Height));
            Assert.Equal([255, 0, 255, 0, 0, 0, 0, 0, 255, 255], raster.Row(0).ToArray().Select(p => (int)p.L));
            Assert.Equal([255, 255, 255, 255, 255, 255, 255, 255, 0, 0], raster.Row(1).ToArray().Select(p => (int)p.L));
        }
    }

    [Fact]
    public void ReadsPcxEgaPlanes()
    {
        // 4 × 1, four 1-bit planes: pixel x takes bit p of its index from plane p. Indices 1, 2, 4, 15.
        byte[] file = PcxHeader(4, 1, bits: 1, planes: 4, lineBytes: 2, palette: Enumerable.Range(0, 16).Select(i => new Rgb8((byte)(i * 16), (byte)i, 0)).ToArray());
        file = [.. file, 0b1001_0000, 0, 0b0101_0000, 0, 0b0011_0000, 0, 0b0001_0000, 0];
        using Raster<Rgb8> raster = Raster.Load<Rgb8>(file, TestImages.Context);
        Assert.Equal([new Rgb8(16, 1, 0), new Rgb8(32, 2, 0), new Rgb8(64, 4, 0), new Rgb8(240, 15, 0)], raster.Row(0).ToArray());
        Assert.Equal(4, ((PcxDetails)raster.Metadata.FormatDetails!).Planes);
    }

    [Fact]
    public void ReadsPcxRunsAcrossScanlines()
    {
        // 3 × 2, 8-bit gray palette: a run of six 7s covers both scanlines (each 4 bytes with padding), then literals.
        byte[] file = PcxHeader(3, 2, bits: 8, planes: 1, lineBytes: 4, palette: []);
        file = [.. file, 0xC6, 7, 1, 0xC1, 0xC0, .. PcxPalette(i => new Rgb8((byte)i, (byte)i, (byte)i))];
        using Raster<Gray8> raster = Raster.Load<Gray8>(file, TestImages.Context);
        Assert.Equal([7, 7, 7], raster.Row(0).ToArray().Select(p => (int)p.L));
        Assert.Equal([7, 7, 1], raster.Row(1).ToArray().Select(p => (int)p.L));
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Fact]
    public void ReadsPcxPaletteFromTheEnd()
    {
        byte[] file = PcxHeader(2, 1, bits: 8, planes: 1, lineBytes: 2, palette: []);
        byte[] body = [.. file, 0, 0xC1, 200];
        using Raster<Rgb8> colored = Raster.Load<Rgb8>((byte[])[.. body, .. PcxPalette(i => new Rgb8((byte)i, 0, (byte)(255 - i)))], TestImages.Context);
        Assert.Equal([new Rgb8(0, 0, 255), new Rgb8(200, 0, 55)], colored.Row(0).ToArray());

        // Without it the indices are gray levels, and the warning says so.
        using Raster<Rgb8> gray = Raster.Load<Rgb8>(body, TestImages.Context);
        Assert.Equal([new Rgb8(0, 0, 0), new Rgb8(200, 200, 200)], gray.Row(0).ToArray());
        Assert.Equal("Pcx.PaletteMissing", Assert.Single(gray.Metadata.Warnings).Id);
        Assert.Throws<CorruptDataException>(() => Raster.Load(body, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void ReadsPcxMonochromeAndDpi()
    {
        byte[] file = PcxHeader(9, 1, bits: 1, planes: 1, lineBytes: 2, palette: []);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), 150);
        file = [.. file, 0b1000_0001, 0b1000_0000];
        using Raster raster = Raster.Load(file, TestImages.Context);
        Raster<Gray8> gray = Assert.IsType<Raster<Gray8>>(raster);
        Assert.Equal([255, 0, 0, 0, 0, 0, 0, 255, 255], gray.Row(0).ToArray().Select(p => (int)p.L));
        Assert.Equal(new Metadata.Resolution(300, 150, Metadata.ResolutionUnit.PerInch), raster.Metadata.Resolution);
    }

    [Fact]
    public void ReadsSunDepths()
    {
        // 1-bit without a colormap: set bits are black. Rows pad to 16 bits.
        using (Raster<Gray8> one = Raster.Load<Gray8>(Sun(3, 2, 1, 1, [], [0b1010_0000, 0, 0b0100_0000, 0]), TestImages.Context))
        {
            Assert.Equal([0, 255, 0], one.Row(0).ToArray().Select(p => (int)p.L));
            Assert.Equal([255, 0, 255], one.Row(1).ToArray().Select(p => (int)p.L));
        }

        // 8-bit with a colormap of planes: reds, then greens, then blues.
        using (Raster<Rgb8> mapped = Raster.Load<Rgb8>(Sun(2, 1, 8, 1, [10, 20, 30, 40, 50, 60], [1, 0]), TestImages.Context))
        {
            Assert.Equal([new Rgb8(20, 40, 60), new Rgb8(10, 30, 50)], mapped.Row(0).ToArray());
        }

        // 24-bit is blue first in type 1 and red first in type 3; 32-bit has a pad byte before each pixel.
        using (Raster<Rgb8> bgr = Raster.Load<Rgb8>(Sun(1, 1, 24, 1, [], [1, 2, 3, 0]), TestImages.Context))
        {
            Assert.Equal(new Rgb8(3, 2, 1), bgr.Row(0)[0]);
        }

        using (Raster<Rgb8> rgb = Raster.Load<Rgb8>(Sun(1, 1, 24, 3, [], [1, 2, 3, 0]), TestImages.Context))
        {
            Assert.Equal(new Rgb8(1, 2, 3), rgb.Row(0)[0]);
        }

        using Raster<Rgb8> xbgr = Raster.Load<Rgb8>(Sun(2, 1, 32, 1, [], [9, 1, 2, 3, 9, 4, 5, 6]), TestImages.Context);
        Assert.Equal([new Rgb8(3, 2, 1), new Rgb8(6, 5, 4)], xbgr.Row(0).ToArray());
    }

    [Fact]
    public void ReadsSunRuns()
    {
        // 0x80 0 is one 0x80; 0x80 n v is n + 1 copies of v, and a run may cross rows.
        byte[] file = Sun(4, 2, 8, 2, [], [0x80, 0, 0x80, 4, 7, 0x80, 1, 9]);
        using Raster<Gray8> raster = Raster.Load<Gray8>(file, TestImages.Context);
        Assert.Equal([0x80, 7, 7, 7], raster.Row(0).ToArray().Select(p => (int)p.L));
        Assert.Equal([7, 7, 9, 9], raster.Row(1).ToArray().Select(p => (int)p.L));
    }

    [Fact]
    public void ReadsSgiPlanesBottomUp()
    {
        // 2 × 2, two channels of 16-bit samples, uncoded: channel 0's bottom row, its top row, then channel 1's.
        byte[] file = SgiHeader(rle: false, bytes: 2, dimension: 3, 2, 2, 2);
        file = [.. file, 0, 1, 0, 2, 0, 3, 0, 4, 0xFF, 0xFF, 0x80, 0, 0, 0, 0, 5];
        using Raster raster = Raster.Load(file, TestImages.Context);
        Raster<GrayAlpha16> pixels = Assert.IsType<Raster<GrayAlpha16>>(raster);
        Assert.Equal([new GrayAlpha16(3, 0), new GrayAlpha16(4, 5)], pixels.Row(0).ToArray());
        Assert.Equal([new GrayAlpha16(1, 0xFFFF), new GrayAlpha16(2, 0x8000)], pixels.Row(1).ToArray());
    }

    [Fact]
    public void ReadsSgiRunsInAnyOrder()
    {
        // 3 × 1 RGB, 8-bit, coded; the rows are stored blue, green, red, as the offset table allows.
        byte[] header = SgiHeader(rle: true, bytes: 1, dimension: 3, 3, 1, 3);
        int data = 512 + 24;
        byte[] red = [0x83, 1, 2, 3, 0];
        byte[] green = [3, 9, 0];
        byte[] blue = [0x81, 4, 2, 5, 0];
        byte[] tables = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(tables, (uint)(data + blue.Length + green.Length));
        BinaryPrimitives.WriteUInt32BigEndian(tables.AsSpan(4), (uint)(data + blue.Length));
        BinaryPrimitives.WriteUInt32BigEndian(tables.AsSpan(8), (uint)data);
        using Raster<Rgb8> raster = Raster.Load<Rgb8>((byte[])[.. header, .. tables, .. blue, .. green, .. red], TestImages.Context);
        Assert.Equal([new Rgb8(1, 9, 4), new Rgb8(2, 9, 5), new Rgb8(3, 9, 5)], raster.Row(0).ToArray());
        Assert.Equal(("", true, 3), (((SgiDetails)raster.Metadata.FormatDetails!).Name, ((SgiDetails)raster.Metadata.FormatDetails!).Rle, ((SgiDetails)raster.Metadata.FormatDetails!).Channels));
    }

    [Fact]
    public void KeepsTheSgiName()
    {
        using Raster<Gray8> bitmap = TestImages.Bitmap();
        bitmap.Metadata.FormatDetails = new SgiDetails { Name = "checkers" };
        using Raster back = Raster.Load(TestImages.Save(bitmap, "SGI"), TestImages.Context);
        Assert.Equal("checkers", ((SgiDetails)back.Metadata.FormatDetails!).Name);
    }

    [Fact]
    public void ReadsXbmOfBytesAndWords()
    {
        string x11 = """
            #define arrow_width 10
            #define arrow_height 2
            #define arrow_x_hot 3
            #define arrow_y_hot 1
            static unsigned char arrow_bits[] = {
               0x01, 0x02, /* row 1 */ 0xff, 0x03};
            """;
        using Raster<Gray8> bytes = Raster.Load<Gray8>(Encoding.ASCII.GetBytes(x11), TestImages.Context);
        Assert.Equal([0, 255, 255, 255, 255, 255, 255, 255, 255, 0], bytes.Row(0).ToArray().Select(p => (int)p.L));
        Assert.All(bytes.Row(1).ToArray(), p => Assert.Equal(0, p.L));
        XbmDetails details = (XbmDetails)bytes.Metadata.FormatDetails!;
        Assert.Equal(("arrow", new Point(3, 1)), (details.Name, details.Hotspot));

        string x10 = "#define w_width 3\n#define w_height 1\nstatic short w_bits[] = { 0x0005 };\n";
        using Raster<Gray8> words = Raster.Load<Gray8>(Encoding.ASCII.GetBytes(x10), TestImages.Context);
        Assert.Equal([0, 255, 0], words.Row(0).ToArray().Select(p => (int)p.L));

        // The writer keeps the name and hotspot.
        string written = Encoding.ASCII.GetString(TestImages.Save(bytes, "XBM"));
        Assert.StartsWith("#define arrow_width 10\n#define arrow_height 2\n#define arrow_x_hot 3\n#define arrow_y_hot 1\nstatic unsigned char arrow_bits[] = {\n   0x01, 0x02, 0xff, 0x03 };\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsXpmColorKeys()
    {
        string xpm = """
            /* XPM */
            static const char * const icon[] = {
            /* width height colors chars */
            "4 2 6 2 1 0",
            "   c None",
            ".. c #FFF",
            "Xx g gray50 c light blue",
            "o. m black",
            "## c #000080008000",
            "@@ s background c red",
            "  ..XxXx",
            "o.##@@  "
            };
            """;
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(Encoding.ASCII.GetBytes(xpm), TestImages.Context);
        Assert.Equal([default, new Rgba8(0xF0, 0xF0, 0xF0, 255), new Rgba8(0xAD, 0xD8, 0xE6, 255), new Rgba8(0xAD, 0xD8, 0xE6, 255)], raster.Row(0).ToArray());
        Assert.Equal([Black, new Rgba8(0, 0x80, 0x80, 255), new Rgba8(255, 0, 0, 255), default], raster.Row(1).ToArray());
        XpmDetails details = (XpmDetails)raster.Metadata.FormatDetails!;
        Assert.Equal(("icon", new Point(1, 0), 2), (details.Name, details.Hotspot, details.CharactersPerPixel));
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Fact]
    public void ReadsXpmGrayLevelsAndWarnsOfUnknownColors()
    {
        string xpm = "/* XPM */\nstatic char *g[] = {\n\"3 1 3 1\",\n\"a c gray50\",\n\"b c grey100\",\n\"c c no-such-color\",\n\"abz\"\n};\n";
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(Encoding.ASCII.GetBytes(xpm), TestImages.Context);
        Assert.Equal([new Rgba8(127, 127, 127, 255), White, default], raster.Row(0).ToArray());
        Assert.Equal(["Xpm.ColorUnknown", "Xpm.PixelUnknown"], raster.Metadata.Warnings.Select(w => w.Id));
        Assert.Throws<CorruptDataException>(() => Raster.Load(Encoding.ASCII.GetBytes(xpm), TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void WritesXpmWithMoreCharactersForManyColors()
    {
        using Raster<Rgb8> many = Raster.Create<Rgb8>(30, 10);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                many.Row(y)[x] = new Rgb8((byte)x, (byte)y, 0);
            }
        }

        byte[] file = TestImages.Save(many, "XPM");
        Assert.Contains("\"30 10 300 2\"", Encoding.ASCII.GetString(file), StringComparison.Ordinal);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file, TestImages.Context);
        TestImages.AssertEqual(many, back);
    }

    [Fact]
    public void ReadsXpmEndingInABackslash()
    {
        // In the values string, which is read from a span that ends exactly at the end of the file.
        byte[] values = [.. "/* XPM */\nstatic char *b[] = {\n\"1 1 1"u8, (byte)'\\'];
        Assert.Throws<CorruptDataException>(() => Raster.Probe(values, TestImages.Context));
        Assert.Throws<CorruptDataException>(() => Raster.Load(values, TestImages.Context));

        // In a row, where it ends the file early.
        byte[] row = [.. "/* XPM */\nstatic char *b[] = {\n\"1 1 1 1\",\n\". c #000000\",\n\""u8, (byte)'\\'];
        using Raster raster = Raster.Load(row, TestImages.Context);
        Assert.Equal((1, 1), (raster.Width, raster.Height));
    }

    [Fact]
    public void WritesNarrowXbmRows()
    {
        // A one-byte row whose value takes a line break writes nine bytes; over many rows the write buffer ends at every offset.
        // Names of different lengths shift the header, so some line break lands where the buffer ends.
        using Raster<Gray8> narrow = TestImages.Bitmap(8, 30000);
        for (int length = 1; length <= 12; length++)
        {
            narrow.Metadata.FormatDetails = new XbmDetails { Name = new string('n', length) };
            using Raster<Gray8> back = Raster.Load<Gray8>(TestImages.Save(narrow, "XBM"), TestImages.Context);
            TestImages.AssertEqual(narrow, back);
        }
    }

    private static byte[] PcxHeader(int width, int height, int bits, int planes, int lineBytes, Rgb8[] palette)
    {
        byte[] header = new byte[128];
        header[0] = 0x0A;
        header[1] = 5;
        header[2] = 1;
        header[3] = (byte)bits;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(width - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)(height - 1));
        for (int i = 0; i < palette.Length; i++)
        {
            header[16 + (i * 3)] = palette[i].R;
            header[17 + (i * 3)] = palette[i].G;
            header[18 + (i * 3)] = palette[i].B;
        }

        header[65] = (byte)planes;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), (ushort)lineBytes);
        header[68] = 1;
        return header;
    }

    private static byte[] PcxPalette(Func<int, Rgb8> color)
    {
        byte[] palette = new byte[769];
        palette[0] = 0x0C;
        for (int i = 0; i < 256; i++)
        {
            Rgb8 c = color(i);
            palette[1 + (i * 3)] = c.R;
            palette[2 + (i * 3)] = c.G;
            palette[3 + (i * 3)] = c.B;
        }

        return palette;
    }

    private static byte[] Sun(int width, int height, int depth, int type, byte[] map, byte[] data)
    {
        byte[] header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x59A66A95);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)depth);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), map.Length > 0 ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), (uint)map.Length);
        return [.. header, .. map, .. data];
    }

    private static byte[] SgiHeader(bool rle, int bytes, int dimension, int width, int height, int channels)
    {
        byte[] header = new byte[512];
        BinaryPrimitives.WriteUInt16BigEndian(header, 474);
        header[2] = (byte)(rle ? 1 : 0);
        header[3] = (byte)bytes;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)dimension);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)channels);
        return header;
    }
}
