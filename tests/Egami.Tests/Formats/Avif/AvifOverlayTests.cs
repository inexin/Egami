using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Heif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Phase 2.9.2 task 13's overlay (iovl) checks. No reference tool decodes iovl (libavif does not), so the files are built here
/// from a corpus image: the overlay must equal its canvas fill with the image, as Load gives it alone, drawn at each offset.
/// </summary>
public class AvifOverlayTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");

    [Fact]
    public void OverlayDrawsLayersOverTheFill()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Corpus, "odd-31x17-420.avif"));
        using Raster<Rgb8> layer = Raster.Load<Rgb8>(source);
        (int X, int Y)[] offsets = [(-10, 5), (20, 30), (60, 70)];
        byte[] file = BuildOverlay(source, 70, 50, [0xFFFF, 0x8000, 0, 0xFFFF], offsets, large: false);

        using Raster raster = Raster.Load(file);
        Assert.Equal(Rgb8.Layout, raster.Layout);
        Assert.Equal((70, 50), (raster.Width, raster.Height));
        using Raster<Rgb8> actual = raster.As<Rgb8>();
        Rgb8[] expected = new Rgb8[70 * 50];
        Array.Fill(expected, new Rgb8(255, 128, 0));
        foreach ((int ox, int oy) in offsets)
        {
            for (int y = 0; y < layer.Height; y++)
            {
                for (int x = 0; x < layer.Width; x++)
                {
                    if (ox + x is >= 0 and < 70 && oy + y is >= 0 and < 50)
                    {
                        expected[((oy + y) * 70) + ox + x] = layer.Row(y)[x];
                    }
                }
            }
        }

        for (int y = 0; y < 50; y++)
        {
            for (int x = 0; x < 70; x++)
            {
                Assert.True(expected[(y * 70) + x].Equals(actual.Row(y)[x]), $"({x}, {y})");
            }
        }
    }

    public static TheoryData<string> LibheifFiles() => [.. LibheifManifest().EnumerateObject().Select(p => p.Name)];

    /// <summary>
    /// The gate against libheif (heif-dec, run as a black box): the canvas size and fill agree exactly, and Egami's overlay is
    /// Egami's decode of the layer image placed at each offset, pixel for pixel. libheif composes in YUV and converts the canvas
    /// its own way, so its 8-bit layers are within 2 steps of its decode of the layer alone; its 10-bit overlays come out in other
    /// colors than its layer decode (heif-dec 1.23.4), so there only the fill is compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(LibheifFiles))]
    public void OverlayComposesAsLibheif(string file)
    {
        JsonElement entry = LibheifManifest().GetProperty(file);
        string references = Path.Combine(AppContext.BaseDirectory, "reference", "avif", "overlay");
        using Raster<Rgba16> egami = Raster.Load<Rgba16>(Path.Combine(Corpus, "overlay", file));
        using Raster<Rgba16> egamiLayer = Raster.Load<Rgba16>(Path.Combine(Corpus, entry.GetProperty("source").GetString()!));
        using Raster<Rgba16> libheif = Raster.Load<Rgba16>(Path.Combine(references, entry.GetProperty("reference").GetString()!));
        using Raster<Rgba16> libheifLayer = Raster.Load<Rgba16>(Path.Combine(references, entry.GetProperty("layer").GetString()!));
        Assert.Equal((libheif.Width, libheif.Height), (egami.Width, egami.Height));
        (int X, int Y)[] offsets = [.. entry.GetProperty("offsets").EnumerateArray().Select(o => (o[0].GetInt32(), o[1].GetInt32()))];
        int[] fill = [.. entry.GetProperty("fill").EnumerateArray().Select(v => v.GetInt32())];

        // heif-dec writes 10 and 12 bits shifted left in its 16-bit PNG; Egami scales them to 16 bits. Compare at the coded depth.
        int depth = ((Egami.Formats.Avif.AvifDetails)Raster.Load(Path.Combine(Corpus, "overlay", file)).Metadata.FormatDetails!).BitDepth;
        int max = (1 << depth) - 1;
        int FromEgami(int v) => (int)(((v * (long)max) + 32767) / 65535);
        int FromLibheif(int v) => depth == 8 ? v / 257 : v >> (16 - depth);

        for (int y = 0; y < egami.Height; y++)
        {
            for (int x = 0; x < egami.Width; x++)
            {
                int layer = Array.FindLastIndex(offsets, o => x >= o.X && x < o.X + egamiLayer.Width && y >= o.Y && y < o.Y + egamiLayer.Height);
                Rgba16 e = egami.Row(y)[x], l = libheif.Row(y)[x];
                if (layer < 0)
                {
                    int[] expected = [.. fill.Take(3).Select(v => (int)(((v * (long)max) + 32767) / 65535))];
                    Assert.True(Same(expected, [FromEgami(e.R), FromEgami(e.G), FromEgami(e.B)]), $"{file} Egami fill at ({x}, {y}): {e}");
                    Assert.True(Same(expected, [FromLibheif(l.R), FromLibheif(l.G), FromLibheif(l.B)]), $"{file} libheif fill at ({x}, {y}): {l}");
                    continue;
                }

                (int ox, int oy) = offsets[layer];
                Assert.True(e.Equals(egamiLayer.Row(y - oy)[x - ox]), $"{file} Egami at ({x}, {y}): {e}, layer {egamiLayer.Row(y - oy)[x - ox]}");
                Rgba16 alone = libheifLayer.Row(y - oy)[x - ox];
                int diff = Math.Max(Math.Abs(l.R - alone.R), Math.Max(Math.Abs(l.G - alone.G), Math.Abs(l.B - alone.B)));
                Assert.True(depth > 8 || diff <= 2 * 257, $"{file} libheif at ({x}, {y}): {l}, layer {alone}");
            }
        }

        static bool Same(int[] a, int[] b) => a.AsSpan().SequenceEqual(b);
    }

    private static JsonElement LibheifManifest() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "overlay", "overlay.json"))).RootElement.GetProperty("files").Clone();

    [Fact]
    public void TranslucentFillGivesAlpha()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Corpus, "odd-31x17-420.avif"));
        byte[] file = BuildOverlay(source, 40, 20, [0, 0, 0xFFFF, 0], [(5, 0)], large: true);
        using Raster raster = Raster.Load(file);
        Assert.Equal(Rgba8.Layout, raster.Layout);
        using Raster<Rgba8> actual = raster.As<Rgba8>();
        Assert.Equal(new Rgba8(0, 0, 255, 0), actual.Row(0)[0]);
        Assert.Equal(255, actual.Row(0)[5].A);
        Assert.Equal(0, actual.Row(19)[39].A);
        Assert.Throws<NotSupportedException>(() => YuvRaster.Load(file));
    }

    /// <summary>
    /// Builds an AVIF whose primary item is an overlay of the source file's (single) av01 item, once for each offset, with the
    /// item's properties copied: ftyp, meta (hdlr, pitm, iinf, iref, iprp, iloc), then mdat.
    /// </summary>
    internal static byte[] BuildOverlay(byte[] source, int width, int height, ushort[] fill, (int X, int Y)[] offsets, bool large)
    {
        HeifFile heif = HeifFile.Parse(source, "AVIF");
        HeifItem item = heif.PrimaryItem!;
        byte[] payload = heif.ItemData(item).ToArray();
        List<byte[]> itemProperties = [.. heif.PropertiesOf(item).Select(p => heif.Bytes.Span[p.Property.Box.Start..p.Property.Box.End].ToArray())];

        using MemoryStream overlay = new();
        overlay.WriteByte(0);
        overlay.WriteByte(large ? (byte)1 : (byte)0);
        foreach (ushort value in fill)
        {
            Write16(overlay, value);
        }

        WriteField(overlay, width, large);
        WriteField(overlay, height, large);
        foreach ((int x, int y) in offsets)
        {
            WriteField(overlay, x, large);
            WriteField(overlay, y, large);
        }

        int layers = offsets.Length;
        ushort overlayId = (ushort)(layers + 1);
        List<(ushort Id, string Type, byte[] Data)> items = [];
        for (int i = 0; i < layers; i++)
        {
            items.Add(((ushort)(i + 1), "av01", payload));
        }

        items.Add((overlayId, "iovl", overlay.ToArray()));

        // Properties: the item's own (shared by every layer), then the overlay's ispe.
        List<byte[]> properties = [.. itemProperties, Box("ispe", [0, 0, 0, 0, .. U32(width), .. U32(height)])];
        byte[] ipco = Box("ipco", [.. properties.SelectMany(p => p)]);
        List<byte> ipma = [0, 0, 0, 0, .. U32(items.Count)];
        foreach ((ushort id, string type, _) in items)
        {
            ipma.AddRange(U16(id));
            if (type == "av01")
            {
                ipma.Add((byte)itemProperties.Count);
                for (int p = 0; p < itemProperties.Count; p++)
                {
                    bool essential = Encoding.ASCII.GetString(itemProperties[p], 4, 4) == "av1C";
                    ipma.Add((byte)((essential ? 0x80 : 0) | (p + 1)));
                }
            }
            else
            {
                ipma.Add(1);
                ipma.Add((byte)properties.Count);
            }
        }

        byte[] iprp = Box("iprp", [.. ipco, .. Box("ipma", [.. ipma])]);
        byte[] hdlr = Box("hdlr", [0, 0, 0, 0, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("pict"), .. new byte[12], 0]);
        byte[] pitm = Box("pitm", [0, 0, 0, 0, .. U16(overlayId)]);
        List<byte> infe = [];
        foreach ((ushort id, string type, _) in items)
        {
            // The layers are hidden (flags 1), as a derived image's inputs should be.
            infe.AddRange(Box("infe", [2, 0, 0, type == "av01" ? (byte)1 : (byte)0, .. U16(id), 0, 0, .. Encoding.ASCII.GetBytes(type), 0]));
        }

        byte[] iinf = Box("iinf", [0, 0, 0, 0, .. U16(items.Count), .. infe]);
        byte[] iref = Box("iref", [0, 0, 0, 0, .. Box("dimg", [.. U16(overlayId), .. U16(layers), .. Enumerable.Range(1, layers).SelectMany(i => U16(i))])]);
        byte[] ftyp = Box("ftyp", [.. Encoding.ASCII.GetBytes("avif"), 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("avifmif1miaf")]);

        // iloc with 4-byte offsets and lengths; its size does not depend on the offsets, so build it twice.
        byte[] Iloc(int mdatData)
        {
            List<byte> iloc = [0, 0, 0, 0, 0x44, 0x00, .. U16(items.Count)];
            int at = mdatData;
            foreach ((ushort id, _, byte[] data) in items)
            {
                iloc.AddRange([.. U16(id), 0, 0, 0, 1, .. U32(at), .. U32(data.Length)]);
                at += data.Length;
            }

            return Box("iloc", [.. iloc]);
        }

        byte[] Meta(int mdatData) => Box("meta", [0, 0, 0, 0, .. hdlr, .. pitm, .. iinf, .. iref, .. iprp, .. Iloc(mdatData)]);
        int start = ftyp.Length + Meta(0).Length + 8;
        byte[] mdat = Box("mdat", [.. items.SelectMany(i => i.Data)]);
        return [.. ftyp, .. Meta(start), .. mdat];
    }

    private static byte[] Box(string type, byte[] content) => [.. U32(content.Length + 8), .. Encoding.ASCII.GetBytes(type), .. content];

    private static byte[] U32(int value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return b;
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static void Write16(Stream s, int value) => s.Write(U16(value));

    private static void WriteField(Stream s, int value, bool large) => s.Write(large ? U32(value) : U16(value));
}
