using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Ccitt;
using Egami.Formats.Pnm;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Ccitt;

/// <summary>
/// Phase 2.3.6: raw CCITT streams with PDF's parameters, through the filter call, Load and the 2.2 row reader. Every
/// <c>shapes-*</c> stream decodes to <c>shapes.pbm</c>; the gates against PDFium and pdf.js are in egami-compare.
/// </summary>
public class CcittTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "ccitt");

    public static TheoryData<string> Shapes() => [.. Directory.GetFiles(Folder, "shapes-*.ccitt").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void FilterDecodesToThePicture(string name)
    {
        (byte[] data, CcittFaxParameters p) = Read(name);
        byte[] packed = CcittFax.Decode(data, p, out int rows);
        (int width, int height, byte[] truth) = Picture();
        Assert.Equal(height, rows);
        int rowBytes = (width + 7) / 8;
        Assert.Equal(rowBytes * height, packed.Length);
        for (int i = 0; i < packed.Length; i++)
        {
            // The PBM has 1 for black and 0 padding; the filter gives 0 for black unless BlackIs1, with 0 padding either way.
            int padding = (i % rowBytes) == rowBytes - 1 ? (0xFF >> (width & 7)) & 0xFF : 0;
            if ((width & 7) == 0)
            {
                padding = 0;
            }

            byte expected = p.BlackIs1 ? truth[i] : (byte)(~truth[i] & ~padding);
            Assert.True(expected == packed[i], $"{name}: byte {i} (row {i / rowBytes}) is {packed[i]:X2}, not {expected:X2}.");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void LoadAndRowsGiveThePicture(string name)
    {
        (byte[] data, CcittFaxParameters p) = Read(name);
        CcittReaderOptions options = new() { Parameters = p };
        using Raster<Gray8> expected = Raster.Load<Gray8>(Path.Combine(Folder, "shapes.pbm"));
        using Raster<Gray8> loaded = Raster.Load<Gray8>(data, CcittFormat.Info, options: options);
        Assert.Equal(Bytes(expected), Bytes(loaded));

        RasterInfo info = Raster.Probe(data, CcittFormat.Info, options: options);
        Assert.Equal((expected.Width, expected.Height), (info.Width, info.Height));
        using RasterDecoder decoder = RasterDecoder.Open(data, CcittFormat.Info, options: options);
        Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
        using Raster rows = decoder.ReadAll();
        Assert.Equal(Bytes(expected), Bytes(rows));
    }

    [Fact]
    public void ProbeReadsNoDataWhenRowsIsGiven()
    {
        CcittReaderOptions options = new() { Parameters = new CcittFaxParameters { K = -1, Columns = 640, Rows = 480 } };
        RasterInfo info = Raster.Probe(new byte[] { 0xFF, 0xFF }, CcittFormat.Info, options: options);
        Assert.Equal((640, 480), (info.Width, info.Height));
    }

    [Fact]
    public void DetectionNeverPicksCcitt()
    {
        (byte[] data, _) = Read("shapes-k-1-a0-e0-b1-i0-r0");
        Assert.Contains(CcittFormat.Info, EgamiContext.Default.Formats);
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(data));
    }

    [Fact]
    public void DamagedDataKeepsTheRowsBeforeAndWarns()
    {
        (byte[] data, CcittFaxParameters p) = Read("damaged-k-1-e0-d0");
        List<EgamiDiagnostic> events = [];
        EgamiContext context = new() { Diagnostics = events.Add };
        byte[] packed = CcittFax.Decode(data, p, out int rows, context);
        Assert.Equal(p.Rows, rows);
        Assert.Contains(events, e => e.Id == "Ccitt.Damaged");
        (int width, _, byte[] truth) = Picture();
        int rowBytes = (width + 7) / 8;
        int good = 0;
        while (Enumerable.Range(0, rowBytes - 1).All(i => (byte)~packed[(good * rowBytes) + i] == truth[(good * rowBytes) + i]))
        {
            good++;
        }

        Assert.InRange(good, 10, rows - 1);

        // The rows after the damage are white: all 1 bits but the padding.
        byte[] last = packed[((rows - 1) * rowBytes)..];
        Assert.All(last[..^1], b => Assert.Equal(0xFF, b));

        using Raster<Gray8> loaded = Raster.Load<Gray8>(data, CcittFormat.Info, context, new CcittReaderOptions { Parameters = p });
        Assert.Contains(loaded.Metadata.Warnings, w => w.Id == "Ccitt.Damaged");
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, CcittFormat.Info, options: new CcittReaderOptions { Parameters = p, Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void DamagedRowsBeforeErrorSkipsToTheNextEol()
    {
        (byte[] data, CcittFaxParameters p) = Read("damaged-k0-e1-d2");
        List<EgamiDiagnostic> events = [];
        CcittFax.Decode(data, p, out int rows, new EgamiContext { Diagnostics = events.Add });
        Assert.Equal(p.Rows, rows);
        Assert.Contains(events, e => e.Id == "Ccitt.RowsSkipped");
    }

    [Fact]
    public void NoRowThrows()
    {
        Assert.Throws<CorruptDataException>(() => CcittFax.Decode([0x02, 0xFF, 0xFF], new CcittFaxParameters { K = -1, Columns = 64 }, out _));
        Assert.Throws<CorruptDataException>(() => CcittFax.Decode([], new CcittFaxParameters(), out _));
    }

    [Fact]
    public void BadParametersThrow()
    {
        Assert.Throws<ArgumentNullException>(() => CcittFax.Decode([0], null!, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittFax.Decode([0], new CcittFaxParameters { Columns = 0 }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittFax.Decode([0], new CcittFaxParameters { Rows = -1 }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittFax.Decode([0], new CcittFaxParameters { DamagedRowsBeforeError = -1 }, out _));
    }

    [Fact]
    public void LimitsApply()
    {
        EgamiContext small = new() { Limits = new SafetyLimits { MaxWidth = 100 } };
        Assert.Throws<LimitExceededException>(() => CcittFax.Decode([0], new CcittFaxParameters { Columns = 101, Rows = 1 }, out _, small));

        // Rows 0: a stream of all-white 1-D rows (a white run of 8 is 10011) grows until MaxPixels stops it.
        byte[] rows = [.. Enumerable.Repeat(new byte[] { 0x9C, 0xE7, 0x39, 0xCE, 0x73 }, 4000).SelectMany(b => b)];
        EgamiContext tight = new() { Limits = new SafetyLimits { MaxPixels = 8 * 1000 } };
        Assert.Throws<LimitExceededException>(() => CcittFax.Decode(rows, new CcittFaxParameters { Columns = 8 }, out _, tight));
        byte[] all = CcittFax.Decode(rows, new CcittFaxParameters { Columns = 8 }, out int count);
        Assert.Equal(8 * 4000, count);
        Assert.All(all, b => Assert.Equal(0xFF, b));
    }

    private static (byte[] Data, CcittFaxParameters Parameters) Read(string name)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")));
        JsonElement p = json.RootElement.GetProperty("parameters");
        CcittFaxParameters parameters = new()
        {
            K = p.GetProperty("K").GetInt32(),
            EndOfLine = p.GetProperty("EndOfLine").GetBoolean(),
            EncodedByteAlign = p.GetProperty("EncodedByteAlign").GetBoolean(),
            Columns = p.GetProperty("Columns").GetInt32(),
            Rows = p.GetProperty("Rows").GetInt32(),
            EndOfBlock = p.GetProperty("EndOfBlock").GetBoolean(),
            BlackIs1 = p.GetProperty("BlackIs1").GetBoolean(),
            DamagedRowsBeforeError = p.GetProperty("DamagedRowsBeforeError").GetInt32(),
        };
        return (File.ReadAllBytes(Path.Combine(Folder, name + ".ccitt")), parameters);
    }

    /// <summary>shapes.pbm's packed rows, 1 for black.</summary>
    private static (int Width, int Height, byte[] Bits) Picture()
    {
        byte[] pbm = File.ReadAllBytes(Path.Combine(Folder, "shapes.pbm"));
        string[] header = System.Text.Encoding.ASCII.GetString(pbm, 0, 16).Split(' ');
        int width = int.Parse(header[1], System.Globalization.CultureInfo.InvariantCulture);
        int height = int.Parse(header[2], System.Globalization.CultureInfo.InvariantCulture);
        int start = header[0].Length + header[1].Length + header[2].Length + 3;
        return (width, height, pbm[start..]);
    }

    private static byte[] Bytes(Raster raster) => Tiff.TiffReference.Bytes(raster);
}
