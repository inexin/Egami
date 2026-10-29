using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.4 task 9: HEIC through the HEIF image layer with Egami.Heic: YuvRaster.Load gives HM's planes, grid cells decode in
/// parallel with the same output at any thread count, and Region, orientation and metadata work as for AVIF.
/// </summary>
public partial class HeicLoadTests
{
    private static readonly EgamiContext Context = new EgamiContext().WithHeic();

    /// <summary>
    /// The corpus files whose primary image is one hvc1 item with HM's MD5 (no grid or overlay). pan-sequence.heic loads as its
    /// sequence since 2.9.10, whose first frame is not its still image; HeicExtrasTests checks its frames.
    /// </summary>
    public static TheoryData<string> SingleItemFiles()
    {
        TheoryData<string> files = [];
        foreach (JsonProperty file in HevcTestFiles.Manifest.Value.EnumerateObject().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (PrimaryItem(file.Value) is JsonElement item && item.TryGetProperty("hm_md5", out _) && file.Name != "pan-sequence.heic")
            {
                files.Add(file.Name);
            }
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(SingleItemFiles))]
    public void YuvLoadGivesHmsPlanes(string file)
    {
        using YuvRaster planes = YuvRaster.Load(Path.Combine(HevcTestFiles.Corpus, file), Context);
        Assert.Equal(PrimaryItem(HevcTestFiles.Manifest.Value.GetProperty(file))!.Value.GetProperty("hm_md5").GetString(), Md5(planes));
    }

    [Theory]
    [InlineData("grid-hm-3x2.heic")]
    [InlineData("duck-grid-alpha.heic")]
    [InlineData("grid-hm-512-10bit.heic")]
    public void GridCellsDecodeTheSameOnAnyNumberOfThreads(string file)
    {
        string path = Path.Combine(HevcTestFiles.Corpus, file);
        using Raster<Rgba16> one = Raster.Load<Rgba16>(path, Context with { MaxParallelism = 1 });
        using Raster<Rgba16> many = Raster.Load<Rgba16>(path, Context with { MaxParallelism = 8 });
        for (int y = 0; y < one.Height; y++)
        {
            Assert.True(one.Row(y).SequenceEqual(many.Row(y)), $"row {y}");
        }
    }

    [Theory]
    [InlineData("grid-hm-3x2.heic")]
    [InlineData("duck-clap.heic")]
    public void RegionIsACropOfTheImage(string file)
    {
        string path = Path.Combine(HevcTestFiles.Corpus, file);
        using Raster<Rgba8> full = Raster.Load<Rgba8>(path, Context);
        Rect region = new(3, 5, full.Width / 2, full.Height / 3);
        using Raster<Rgba8> part = Raster.Load<Rgba8>(path, Context, new ReaderOptions { Region = region });
        Assert.Equal((region.Width, region.Height), (part.Width, part.Height));
        for (int y = 0; y < part.Height; y++)
        {
            Assert.True(part.Row(y).SequenceEqual(full.Row(region.Y + y).Slice(region.X, region.Width)), $"row {y}");
        }
    }

    [Fact]
    public void IrotAndImirAreTheOrientation()
    {
        string path = Path.Combine(HevcTestFiles.Corpus, "duck-irot1.heic");
        RasterInfo info = Raster.Probe(path, Context);
        using Raster raster = Raster.Load(path, Context);
        Assert.NotEqual(ImageOrientation.Normal, info.Orientation);
        Assert.Equal(info.Orientation, raster.Metadata.Exif!.Orientation);
        Assert.Equal((info.Width, info.Height), (raster.Width, raster.Height));
    }

    [Fact]
    public void MetadataComesWithThePixels()
    {
        using Raster tagged = Raster.Load(Path.Combine(HevcTestFiles.Corpus, "duck-exif-xmp.heic"), Context);
        Assert.NotNull(tagged.Metadata.Exif);
        Assert.False(tagged.Metadata.Xmp.IsEmpty);
        Assert.NotNull(tagged.Metadata.CodePoints);

        using Raster profiled = Raster.Load(Path.Combine(HevcTestFiles.Corpus, "duck-icc-p3.heic"), Context);
        Assert.False(profiled.Metadata.IccProfile.IsEmpty);
        Assert.Null(profiled.Metadata.CodePoints);   // the ICC profile says what the pixels mean

        using Raster alpha = Raster.Load(Path.Combine(HevcTestFiles.Corpus, "duck-alpha-10bit.heic"), Context);
        Assert.Equal(Rgba16.Layout, alpha.Layout);
    }

    /// <summary>Gets the manifest's entry for a file's primary item, when it is an hvc1 item.</summary>
    private static JsonElement? PrimaryItem(JsonElement file)
    {
        if (!file.TryGetProperty("heif_info", out JsonElement info) || PrimaryId().Match(info.GetString()!) is not { Success: true } match
            || !file.TryGetProperty("items", out JsonElement items))
        {
            return null;
        }

        int id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt32() == id && item.GetProperty("type").GetString() == "hvc1")
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>The MD5 of the planes, one byte a sample up to 8 bits and two (little-endian) above, as HM writes them.</summary>
    private static string Md5(YuvRaster planes)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        if (planes is YuvRaster<byte> bytes)
        {
            foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[bytes.Y, bytes.U, bytes.V])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    md5.AppendData(plane.Row(y));
                }
            }
        }
        else
        {
            YuvRaster<ushort> words = (YuvRaster<ushort>)planes;
            foreach (YuvPlane<ushort>? plane in (YuvPlane<ushort>?[])[words.Y, words.U, words.V])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    md5.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(plane.Row(y)));
                }
            }
        }

#pragma warning disable CA1308 // MD5s are written in lower case
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
#pragma warning restore CA1308
    }

    [GeneratedRegex(@"\(id=(\d+)\), primary")]
    private static partial Regex PrimaryId();
}
