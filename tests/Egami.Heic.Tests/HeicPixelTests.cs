using System.Globalization;
using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.4 task 9's gate: Raster.Load of every corpus HEIC with Egami.Heic equals heif-dec's RGB decode
/// (reference/heic/*.png) sample for sample, once turned by its orientation as heif-dec turns it.
/// </summary>
public partial class HeicPixelTests(ITestOutputHelper output)
{
    private static readonly EgamiContext Context = new EgamiContext().WithHeic();

    /// <summary>
    /// The corpus files with a heif-dec reference whose image is HEVC-coded (JPEG-coded and uncompressed HEIF come with task 13),
    /// but two. hm-slices.heic: libde265, inside heif-dec, filters across its slice boundaries differently from HM, the bit-exact
    /// reference (stage A's finding), so heif-dec's pixels there are not HEVC's. duck-clap-odd.heic: its clean aperture has
    /// half-sample offsets, and heif-dec's output there is no crop of the decoded image (even its luma differs), so Egami crops at
    /// whole samples, as libavif does.
    /// </summary>
    public static TheoryData<string> RgbFiles() => [.. Directory.GetFiles(HevcTestFiles.References, "*.png")
        .Select(Path.GetFileNameWithoutExtension).Where(n => File.Exists(Path.Combine(HevcTestFiles.Corpus, n + ".heic")))
        .Select(n => n + ".heic").Where(n => n is not ("hm-slices.heic" or "duck-clap-odd.heic") && Egami.Formats.Heic.HeicSupport.Matches(File.ReadAllBytes(Path.Combine(HevcTestFiles.Corpus, n)).AsSpan(0, 64)))
        .Order(StringComparer.Ordinal)!];

    [Theory]
    [MemberData(nameof(RgbFiles))]
    public void LoadEqualsHeifDec(string file)
    {
        string path = Path.Combine(HevcTestFiles.Corpus, file);
        using Raster raster = Raster.Load(path, Context);
        RasterInfo info = Raster.Probe(path, Context);
        Assert.Equal(info.Layout, raster.Layout);
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        using Raster<Rgba16> actual = raster.As<Rgba16>();
        using Raster<Rgba16> expected = Raster.Load<Rgba16>(Path.Combine(HevcTestFiles.References, Path.ChangeExtension(file, ".png")));
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));

        // heif-dec writes 10 and 12 bits shifted up into 16-bit PNGs; Egami rescales them to the full 16 bits. Both are compared at
        // the image's own depth.
        int depth = Depth(file);
        int max = (1 << depth) - 1;
        Rgba16 AtDepth(Rgba16 p, bool shifted) => depth == 8 ? p
            : shifted ? new Rgba16((ushort)(p.R >> (16 - depth)), (ushort)(p.G >> (16 - depth)), (ushort)(p.B >> (16 - depth)), (ushort)(p.A >> (16 - depth)))
            : new Rgba16(Down(p.R), Down(p.G), Down(p.B), Down(p.A));
        ushort Down(ushort v) => (ushort)(((v * max) + 32767) / 65535);
        int differ = 0, worst = 0;
        string first = "";
        for (int y = 0; y < actual.Height; y++)
        {
            ReadOnlySpan<Rgba16> actualRow = actual.Row(y), expectedRow = expected.Row(y);
            for (int x = 0; x < actual.Width; x++)
            {
                Rgba16 a = AtDepth(actualRow[x], false), e = AtDepth(expectedRow[x], true);
                if (!a.Equals(e))
                {
                    int d = Math.Max(Math.Max(Math.Abs(a.R - e.R), Math.Abs(a.G - e.G)), Math.Max(Math.Abs(a.B - e.B), Math.Abs(a.A - e.A)));
                    if (differ++ == 0)
                    {
                        first = $"({x}, {y}): Egami {Show(a)}, heif-dec {Show(e)}";
                    }

                    worst = Math.Max(worst, d);
                }
            }
        }

        output.WriteLine($"{file}: {raster.Layout.BitsPerChannel}-bit, {differ} of {actual.Width * actual.Height} differ, worst {worst}; first {first}");
        Assert.True(differ == 0, $"{file}: {differ} pixels differ, worst {worst}; first {first}");
    }

    /// <summary>Gets the bit depth heif-info gives the primary image.</summary>
    private static int Depth(string file)
    {
        string info = HevcTestFiles.Manifest.Value.GetProperty(file).GetProperty("heif_info").GetString()!;
        Match m = BitDepth().Match(info);
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"bit depth: (\d+)")]
    private static partial Regex BitDepth();

    private static string Show(Rgba16 p) => $"({p.R}, {p.G}, {p.B}, {p.A})";
}
