using Egami.Formats.Heif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Heif;

/// <summary>The HEIF box writer: files it writes parse back to the same item model, and AVIF files rebuilt from another file's items decode the same.</summary>
public class HeifWriterTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");

    [Fact]
    public void WritesTheItemModel()
    {
        HeifWriter writer = new("avif", "avif", "mif1", "miaf", "MA1B");
        uint color = writer.AddItem("av01", new byte[] { 1, 2, 3, 4 }, "Color");
        uint alpha = writer.AddItem("av01", new byte[] { 5, 6 }, "Alpha", hidden: true);
        uint xmp = writer.AddItem("mime", new byte[] { 7, 8, 9 }, "XMP", contentType: "application/rdf+xml");
        uint grid = writer.AddItem("grid", new byte[] { 0, 0, 0, 0, 0, 64, 0, 48 }, inIdat: true);
        writer.PrimaryItemId = color;
        int ispe = writer.AddProperty(HeifWriter.SpatialExtents(64, 48));
        writer.Associate(color, ispe);
        writer.Associate(alpha, ispe);
        writer.AddProperty(color, HeifWriter.PixelInformation(10, 3));
        writer.AddProperty(color, HeifWriter.ColorNclx(9, 16, 9, false));
        writer.AddProperty(alpha, HeifWriter.AuxiliaryType("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"), essential: true);
        writer.AddProperty(color, HeifWriter.Rotation(3), essential: true);
        writer.Reference("auxl", alpha, color);
        writer.Reference("cdsc", xmp, color);
        byte[] file = writer.ToArray();

        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.Empty(heif.Problems);
        Assert.Equal(IsoTypes.Of("avif"), heif.MajorBrand);
        Assert.Equal([IsoTypes.Of("avif"), IsoTypes.Of("mif1"), IsoTypes.Of("miaf"), IsoTypes.Of("MA1B")], heif.CompatibleBrands);
        Assert.Equal(IsoTypes.Pict, heif.Handler);
        Assert.Equal(color, heif.PrimaryItemId);
        Assert.Equal([color, alpha, xmp, grid], heif.Items.Select(i => i.Id));
        Assert.Equal("Color", heif.Item(color)!.Name);
        Assert.True(heif.Item(alpha)!.Hidden);
        Assert.Equal("application/rdf+xml", heif.Item(xmp)!.ContentType);
        Assert.Equal([1, 2, 3, 4], heif.ItemData(heif.Item(color)!).ToArray());
        Assert.Equal([5, 6], heif.ItemData(heif.Item(alpha)!).ToArray());
        Assert.Equal([7, 8, 9], heif.ItemData(heif.Item(xmp)!).ToArray());
        Assert.Equal(1, heif.Item(grid)!.ConstructionMethod);
        Assert.Equal([0, 0, 0, 0, 0, 64, 0, 48], heif.ItemData(heif.Item(grid)!).ToArray());
        Assert.Equal([color], heif.Item(alpha)!.ReferencesOf(IsoTypes.Of("auxl")));
        Assert.Equal([color], heif.Item(xmp)!.ReferencesOf(IsoTypes.Of("cdsc")));
        Assert.Equal(
            [(IsoTypes.Of("ispe"), false), (IsoTypes.Of("pixi"), false), (IsoTypes.Of("colr"), false), (IsoTypes.Of("irot"), true)],
            heif.PropertiesOf(heif.Item(color)!).Select(p => (p.Property.Type, p.Essential)));

        // The shared ispe is written once.
        Assert.Equal(5, heif.Properties.Count);
        Assert.Equal(heif.PropertiesOf(heif.Item(color)!).First().Property, heif.PropertiesOf(heif.Item(alpha)!).First().Property);

        // The meta box comes before the mdat.
        Assert.Equal([IsoTypes.Ftyp, IsoTypes.Meta, IsoTypes.Of("mdat")], heif.Boxes.Select(b => b.Type.Value));
    }

    [Fact]
    public void WritesWideFieldsForManyProperties()
    {
        HeifWriter writer = new("mif1", "mif1");
        uint item = writer.AddItem("av01", new byte[] { 1 });
        writer.PrimaryItemId = item;
        for (int i = 0; i < 200; i++)
        {
            writer.AddProperty(item, HeifWriter.SpatialExtents(i + 1, 1), essential: i == 150);
        }

        HeifFile heif = HeifFile.Parse(writer.ToArray(), "AVIF");
        (HeifProperty property, bool essential)[] properties = [.. heif.PropertiesOf(heif.Item(item)!)];
        Assert.Equal(200, properties.Length);
        Assert.True(properties[150].essential);
        Assert.False(properties[149].essential);
        Assert.Equal(200, heif.Properties.Count);
    }

    [Theory]
    [InlineData("duck-420-q60.avif")]
    [InlineData("duck-10bit-444.avif")]
    [InlineData("duck-alpha-10bit.avif")]
    [InlineData("duck-12bit-400.avif")]
    public void RebuiltFilesDecodeTheSame(string name)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Corpus, name));
        HeifFile source = HeifFile.Parse(original, "AVIF");
        HeifWriter writer = new("avif", "avif", "mif1", "miaf");
        Dictionary<uint, uint> ids = [];
        foreach (HeifItem item in source.Items)
        {
            ids[item.Id] = writer.AddItem(IsoTypes.ToString(item.Type), source.ItemData(item).ToArray(), item.Name, item.Hidden, item.ContentType);
        }

        writer.PrimaryItemId = ids[source.PrimaryItemId];
        foreach (HeifItem item in source.Items)
        {
            foreach ((HeifProperty property, bool essential) in source.PropertiesOf(item))
            {
                byte[] box = original[property.Box.StartIndex..property.Box.EndIndex];
                writer.AddProperty(ids[item.Id], box, essential);
            }

            foreach (HeifReference reference in item.References)
            {
                writer.Reference(IsoTypes.ToString(reference.Type), ids[item.Id], [.. reference.To.Select(t => ids[t])]);
            }
        }

        byte[] rebuilt = writer.ToArray();
        using Raster<Rgba16> a = Raster.Load<Rgba16>(original);
        using Raster<Rgba16> b = Raster.Load<Rgba16>(rebuilt);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (int y = 0; y < a.Height; y++)
        {
            Assert.True(a.Row(y).SequenceEqual(b.Row(y)), $"Row {y} differs.");
        }
    }
}
