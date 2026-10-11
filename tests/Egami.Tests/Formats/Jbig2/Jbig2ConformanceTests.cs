using Egami.Formats;
using Egami.Formats.Jbig2;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jbig2;

/// <summary>
/// The T.88 conformance streams (ITU-T T.88 Annex K, from the Recommendation's electronic attachment), kept out of the repo
/// under .cache/standards/t88conf; each page must equal the BMP the ITU gives for it. Only F01_200_TT9, F01_200_TT10 and the
/// second and third pages of codeStreamTest1_TT1 follow T.88: the others' text regions refer to no symbol dictionary (7.3.1),
/// their symbol dictionaries' export flags run past their symbols (6.5.10), and TT1's first page differs from the Annex H
/// datastream it copies in one byte of its symbol ID table. jbig2dec 0.20 and PDFium reject them the same way, so Egami does
/// too: strict loading throws and lenient loading skips the broken regions with a warning.
/// </summary>
public class Jbig2ConformanceTests
{
    internal static string? Folder()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "standards", "t88conf", "JBIG2_ConformanceData-A20180829");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TheoryData<string> Streams()
    {
        string? folder = Folder();
        return folder is null ? ["(none)"] : [.. Directory.GetFiles(folder, "*.jb2").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];
    }

    /// <summary>The streams, and pages of them, that break T.88 (see the class remarks).</summary>
    private static readonly HashSet<(string, int)> Malformed =
    [
        ("codeStreamTest1_TT1.jb2", 0), ("codeStreamTest1_TT2.jb2", 0), ("codeStreamTest1_TT3.jb2", 0), ("codeStreamTest1_TT4.jb2", 0),
        ("codeStreamTest1_TT5.jb2", 0), ("codeStreamTest1_TT7.jb2", 0), ("codeStreamTest2_TT6.jb2", 0), ("codeStreamTest3_TT8.jb2", 0),
    ];

    [Theory]
    [MemberData(nameof(Streams))]
    public void PagesEqualTheItuBitmaps(string name)
    {
        string? folder = Folder();
        if (folder is null)
        {
            return;
        }

        if (Malformed.Contains((name, 0)))
        {
            byte[] broken = File.ReadAllBytes(Path.Combine(folder, name));
            Assert.Throws<CorruptDataException>(() => Raster.LoadAll(broken, options: new ReaderOptions { Recovery = RecoveryMode.Strict }).Dispose());
            using RasterList lenient = Raster.LoadAll(broken);
            Assert.NotEmpty(lenient[0].Metadata.Warnings);
            if (name != "codeStreamTest1_TT1.jb2")
            {
                return;
            }
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, name));
        using RasterList pages = Raster.LoadAll(data);
        string stem = Path.GetFileNameWithoutExtension(name);
        string[] expected = Directory.GetFiles(folder, stem + "_TT*.bmp").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Length, pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            if (Malformed.Contains((name, i)))
            {
                continue;
            }

            Assert.Empty(pages[i].Metadata.Warnings);
            using Raster<Gray8> want = Raster.Load<Gray8>(expected[i]);
            Raster got = pages[i];
            Assert.Equal((want.Width, want.Height), (got.Width, got.Height));
            Assert.Equal(Gray8.Layout, got.Layout);
            Raster<Gray8> gray = (Raster<Gray8>)got;
            long differ = 0;
            for (int y = 0; y < want.Height; y++)
            {
                Span<Gray8> a = want.Row(y);
                Span<Gray8> b = gray.Row(y);
                for (int x = 0; x < want.Width; x++)
                {
                    if ((a[x].L < 128) != (b[x].L < 128))
                    {
                        differ++;
                    }
                }
            }

            Assert.True(differ == 0, $"{name} page {i}: {differ} pixels differ.");
        }
    }
}
