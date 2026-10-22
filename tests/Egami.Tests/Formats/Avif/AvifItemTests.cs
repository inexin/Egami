using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Phase 2.9.2 task 16: items (design question 13), sequences (question 4) and layered images (question 15). AvifDetails.Items
/// gives every item with the role libavif's item model gives it; AvifImage.ReadItem decodes an image item on its own; a
/// sequence of key frames loads every frame, another its first; a layered image its base layer, until inter frames (2.9.5).
/// </summary>
public class AvifItemTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> StillFiles() => [.. Manifest.Value.EnumerateObject()
        .Where(p => p.Value.GetProperty("libavif").TryGetProperty("major_brand", out JsonElement b) && b.GetString() == "avif" && !p.Value.TryGetProperty("avifdec_error", out _))
        .Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(StillFiles))]
    public void ItemsHaveLibavifsRoles(string file)
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        AvifDetails details = Assert.IsType<AvifDetails>(raster.Metadata.FormatDetails);
        JsonElement libavif = Manifest.Value.GetProperty(file).GetProperty("libavif");
        uint primary = libavif.GetProperty("primary_item").GetUInt32();
        HashSet<uint> inFile = [.. Manifest.Value.GetProperty(file).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetUInt32())];
        foreach (JsonElement item in libavif.GetProperty("items").EnumerateArray())
        {
            uint id = item.GetProperty("id").GetUInt32();
            if (!inFile.Contains(id))
            {
                // libavif makes up an alpha grid item for files that give each cell its own alpha.
                continue;
            }

            AvifItem mine = Assert.Single(details.Items, i => i.Id == id);
            Assert.Equal(item.GetProperty("type").GetString(), mine.Type);
            if (id == primary)
            {
                Assert.Equal(AvifItemRole.Primary, mine.Role);
            }
            else if (item.GetProperty("thumbnail_for").GetUInt32() != 0)
            {
                Assert.Equal(AvifItemRole.Thumbnail, mine.Role);
            }
            else if (item.GetProperty("aux_for").GetUInt32() != 0)
            {
                Assert.Contains(mine.Role, (AvifItemRole[])[AvifItemRole.Alpha, AvifItemRole.Depth, AvifItemRole.Auxiliary]);
            }
            else if (item.GetProperty("dimg_for").GetUInt32() != 0)
            {
                Assert.Contains(mine.Role, (AvifItemRole[])[AvifItemRole.Derived, AvifItemRole.GainMap, AvifItemRole.Alpha]);
            }
        }
    }

    [Theory]
    [InlineData("duck-alpha.avif")]
    [InlineData("duck-alpha-lossless.avif")]
    public void ReadItemGivesTheAlphaImage(string file)
    {
        string path = Path.Combine(Corpus, file);
        using Raster raster = Raster.Load(path);
        AvifItem alpha = Assert.Single(((AvifDetails)raster.Metadata.FormatDetails!).Items, i => i.Role == AvifItemRole.Alpha);
        using Raster item = AvifImage.ReadItem(path, alpha.Id);
        Assert.Equal(Gray8.Layout, item.Layout);

        // The alpha image's samples are the alpha channel avifdec writes.
        using Raster<Gray8> gray = item.As<Gray8>();
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(Path.Combine(References, Manifest.Value.GetProperty(file).GetProperty("reference").GetString()!));
        for (int y = 0; y < gray.Height; y++)
        {
            for (int x = 0; x < gray.Width; x++)
            {
                Assert.Equal(expected.Row(y)[x].A, gray.Row(y)[x].L);
            }
        }
    }

    [Fact]
    public void ReadItemRefusesItemsThatAreNotImages()
    {
        string path = Path.Combine(Corpus, "libavif", "colors_hdr_srgb.avif");
        Assert.Throws<ArgumentException>(() => AvifImage.ReadItem(path, 2));
        Assert.Throws<ArgumentException>(() => AvifImage.ReadItem(path, 99));
        using Raster auxiliary = AvifImage.ReadItem(path, 4);
        Assert.True(auxiliary.Width > 0);
    }

    [Fact]
    public void KeyFrameSequenceLoadsEveryFrame()
    {
        string path = Path.Combine(Corpus, "pan-sequence-keyframes.avif");
        RasterInfo info = Raster.Probe(path);
        Assert.Equal(4, info.FrameCount);
        using Raster raster = Raster.Load(path);
        Assert.Equal(4, raster.FrameCount);
        Assert.True(((AvifDetails)raster.Metadata.FormatDetails!).IsSequence);
        Assert.Equal(TimeSpan.FromSeconds(1 / 30.0).TotalMilliseconds, raster.FrameMetadataAt(1).Delay.TotalMilliseconds, 0.01);
        Assert.DoesNotContain(raster.Metadata.Warnings, w => w.Id == "Avif.SequenceFirstFrame");

        // Each frame decodes on its own, so the frames range gives the same pixels.
        using Raster<Rgba8> all = raster.As<Rgba8>();
        using Raster<Rgba8> third = Raster.Load<Rgba8>(path, options: new ReaderOptions { Frames = 2..3 });
        Assert.Equal(1, third.FrameCount);
        for (int y = 0; y < third.Height; y++)
        {
            Assert.True(all.Frames[2].Row(y).SequenceEqual(third.Row(y)));
        }
    }

    [Theory]
    [InlineData("duck-layered-3.avif")]
    [InlineData("duck-progressive.avif")]
    public void LayeredImageLoadsItsBaseLayerWithAWarning(string file)
    {
        string path = Path.Combine(Corpus, file);
        using Raster raster = Raster.Load(path);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Avif.BaseLayerOnly");

        // Probe gives the size Load gives: a spatially layered base layer's own, smaller than ispe's.
        RasterInfo info = Raster.Probe(path);
        Assert.Equal((raster.Width, raster.Height), (info.Width, info.Height));
    }
}
