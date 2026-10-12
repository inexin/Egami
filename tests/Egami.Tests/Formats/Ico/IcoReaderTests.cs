using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Ico;
using Egami.Pixels;

namespace Egami.Tests.Formats.Ico;

/// <summary>
/// The ICO and CUR corpus (tools/make-ico-references.py) against the pixels its hand-written files are known to hold, and every
/// entry against ImageMagick and Pillow; the entry Load picks; damaged files in both recovery modes.
/// </summary>
public class IcoReaderTests
{
    internal static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "ico");
    internal static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "ico");

    /// <summary>
    /// Entries a reference tool reads differently, with the reason. ImageMagick cannot read 16-bit entries at all, so they have no
    /// reference from it.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDifferences = new(StringComparer.Ordinal)
    {
        ["bmp16.ico#0:pillow"] = "Pillow widens 5-bit samples by a table that differs by 1 from Egami's BMP reader, which rounds v × 255 / 31.",
        ["bmp16-565.ico#0:pillow"] = "As bmp16.ico.",
        ["bmp32-zero-alpha.ico#0:magick"] = "ImageMagick keeps an alpha channel that is 0 everywhere; Windows, and Egami, use the AND mask then.",
        ["bmp32-zero-alpha.ico#0:pillow"] = "As ImageMagick: the whole entry is transparent.",
        ["bmp32-fields.ico#0:magick"] = "ImageMagick reads the three bit-field masks after the header as pixels.",
    };

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryEntryMatchesItsReferences(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, name));
        using RasterList entries = Raster.LoadAll<Rgba8>(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        int checkedAgainst = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            Raster<Rgba8> entry = (Raster<Rgba8>)entries[i];
            Assert.Equal(i, ((IcoDetails)entry.Metadata.FormatDetails!).Index);
            byte[] actual = MemoryMarshal.AsBytes(Pixels(entry)).ToArray();
            if (Reference(name, i, "truth") is byte[] truth)
            {
                Assert.True(truth.AsSpan().SequenceEqual(actual), $"{name} entry {i} differs from the pixels written.");
                checkedAgainst++;
            }

            foreach (string tool in new[] { "magick", "pillow" })
            {
                if (Reference(name, i, tool) is not byte[] reference || KnownDifferences.ContainsKey($"{name}#{i}:{tool}"))
                {
                    continue;
                }

                Assert.True(SamePixels(reference, actual, out string where), $"{name} entry {i} differs from {tool}: {where}");
                checkedAgainst++;
            }
        }

        if (name.EndsWith(".cur", StringComparison.Ordinal) && Reference(name, -1, "pillow") is byte[] pillow)
        {
            using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data);
            Assert.True(SamePixels(pillow, MemoryMarshal.AsBytes(Pixels(loaded)).ToArray(), out string where), $"{name} Load differs from Pillow's: {where}");
            checkedAgainst++;
        }

        Assert.True(checkedAgainst > 0, $"{name} has no reference.");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeListsTheDirectoryWithoutPixels(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, name));
        RasterInfo info = Raster.Probe(data);
        using RasterList entries = Raster.LoadAll(data);
        Assert.Equal(entries.Count, info.ImageCount);
        Assert.Equal(Rgba8.Layout, info.Layout);
        using Raster first = Raster.Load(data);
        Assert.Equal((first.Width, first.Height), (info.Width, info.Height));
        IcoDetails details = (IcoDetails)first.Metadata.FormatDetails!;
        Assert.Equal(entries.Select(e => (e.Width, e.Height)), details.Entries.Select(e => (e.Width, e.Height)));
        Assert.Equal(data[2] == 2 ? CurFormat.Info : IcoFormat.Info, info.Format);
    }

    [Fact]
    public void LoadPicksTheLargestThenTheDeepest()
    {
        using Raster set = Raster.Load(Path.Combine(Images, "set.ico"));
        Assert.Equal((256, 256), (set.Width, set.Height));
        Assert.Equal(IcoEntryEncoding.Png, ((IcoDetails)set.Metadata.FormatDetails!).Encoding);

        using Raster depths = Raster.Load(Path.Combine(Images, "set-depths.ico"));
        Assert.Equal(2, ((IcoDetails)depths.Metadata.FormatDetails!).Index);

        using Raster unsorted = Raster.Load(Path.Combine(Images, "set-unsorted.ico"));
        Assert.Equal(0, ((IcoDetails)unsorted.Metadata.FormatDetails!).Index);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(17, 24)]
    [InlineData(32, 32)]
    [InlineData(33, 48)]
    [InlineData(200, 256)]
    public void MinimumSizePicksTheSmallestEntryThatIsBigEnough(int minimum, int expected)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "set.ico"));
        using RasterList all = Raster.LoadAll<Rgba8>(data);
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, options: new ReaderOptions { MinimumSize = new Size(minimum, minimum) });
        Assert.Equal((expected, expected), (loaded.Width, loaded.Height));
        Raster<Rgba8> entry = (Raster<Rgba8>)all.First(e => e.Width == expected);
        Assert.True(Pixels(entry).SequenceEqual(Pixels(loaded)));
    }

    [Fact]
    public void MinimumSizeBeyondTheLargestReducesNothing()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "set.ico"));
        using Raster loaded = Raster.Load(data, options: new ReaderOptions { MinimumSize = new Size(1000, 1000) });
        Assert.Equal((256, 256), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void EntryPicksByDirectoryIndex()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "set-unsorted.ico"));
        using Raster second = Raster.Load(data, options: new IcoReaderOptions { Entry = 1 });
        Assert.Equal((16, 16), (second.Width, second.Height));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(data, options: new IcoReaderOptions { Entry = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(data, options: new IcoReaderOptions { Entry = -1 }));
    }

    [Fact]
    public void CursorsKeepTheirHotspots()
    {
        using RasterList cursors = Raster.LoadAll(Path.Combine(Images, "cursor-set.cur"));
        Assert.Equal(new Point(3, 4), ((IcoDetails)cursors[0].Metadata.FormatDetails!).Hotspot);
        Assert.Equal(new Point(4, 6), ((IcoDetails)cursors[1].Metadata.FormatDetails!).Hotspot);
        Assert.Equal(CurFormat.Info.Name, cursors[0].Metadata.FormatName);
        using Raster icon = Raster.Load(Path.Combine(Images, "set.ico"));
        Assert.Null(((IcoDetails)icon.Metadata.FormatDetails!).Hotspot);
    }

    [Fact]
    public void ADirectoryThatDisagreesWithTheEntryIsAWarning()
    {
        using Raster raster = Raster.Load(Path.Combine(Images, "dir-mismatch.ico"));
        Assert.Equal((16, 16), (raster.Width, raster.Height));
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Ico.DirectoryMismatch");
    }

    [Theory]
    [InlineData("no-entries.ico", false)]
    [InlineData("offset-in-directory.ico", false)]
    [InlineData("truncated.ico", true)]
    [InlineData("not-an-image.ico", true)]
    public void FilesWithNoReadableEntryFailInBothModes(string name, bool detected)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "bad", name));
        if (!detected)
        {
            // The signature check reads the count and the first offset, so these are not taken for icons at all.
            Assert.Throws<UnsupportedFormatException>(() => Raster.Load(data));
        }
        else
        {
            Assert.Throws<CorruptDataException>(() => Raster.Load(data));
        }

        Assert.Throws<CorruptDataException>(() => Raster.Load(data, IcoFormat.Info));
        Assert.Throws<CorruptDataException>(() => Raster.Load(data, IcoFormat.Info, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Theory]
    [InlineData("overlap.ico", "Ico.EntrySkipped")]
    [InlineData("one-bad-entry.ico", "Ico.EntrySkipped")]
    [InlineData("no-mask.ico", "Ico.MaskMissing")]
    public void DamagedEntriesAreSkippedLenientlyAndThrowStrictly(string name, string warning)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "bad", name));
        using (RasterList lenient = Raster.LoadAll(data))
        {
            Assert.Contains(lenient.SelectMany(r => r.Metadata.Warnings), w => w.Id == warning);
        }

        Assert.Throws<CorruptDataException>(() => Raster.LoadAll(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict }).Dispose());
    }

    [Fact]
    public void TgaDoesNotShadowAnIcon()
    {
        foreach (string file in Directory.GetFiles(Images))
        {
            Assert.NotEqual("TGA", Raster.Probe(file).Format.Name);
        }
    }

    internal static Span<Rgba8> Pixels(Raster<Rgba8> raster)
    {
        Rgba8[] all = new Rgba8[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(all.AsSpan(y * raster.Width));
        }

        return all;
    }

    /// <summary>Compares 8-bit RGBA: alpha everywhere, color only where alpha is not 0.</summary>
    internal static bool SamePixels(byte[] expected, byte[] actual, out string where)
    {
        where = "";
        if (expected.Length != actual.Length)
        {
            where = $"{expected.Length / 4} pixels expected, {actual.Length / 4} read";
            return false;
        }

        for (int i = 0; i < expected.Length; i += 4)
        {
            bool same = expected[i + 3] == actual[i + 3] && (expected[i + 3] == 0 || expected.AsSpan(i, 3).SequenceEqual(actual.AsSpan(i, 3)));
            if (!same)
            {
                where = $"pixel {i / 4}: expected {string.Join(",", expected[i..(i + 4)])}, read {string.Join(",", actual[i..(i + 4)])}";
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns a reference's 8-bit RGBA, or <see langword="null"/> when the tool gave none; entry -1 is Pillow's one image of a cursor.</summary>
    internal static byte[]? Reference(string name, int entry, string tool)
    {
        string path = Path.Combine(References, $"{name}.{(entry < 0 ? "load" : entry.ToString(System.Globalization.CultureInfo.InvariantCulture))}.{tool}.rgba.gz");
        if (!File.Exists(path))
        {
            return null;
        }

        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream memory = new();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    internal static JsonElement Manifest(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json")));
        return document.RootElement.GetProperty("files").GetProperty(name).Clone();
    }
}
