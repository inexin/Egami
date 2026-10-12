namespace Egami.Fonts.Tests.Shaping;

/// <summary>HarfBuzz's own shaping tests give hb-shape's glyphs, clusters, positions and flags through Egami's engine.</summary>
public sealed class HarfBuzzShapingTests
{
    [Fact]
    public void AotsCasesEqualHbShape()
    {
        List<HarfBuzzCases.Result> results = [.. HarfBuzzCases.Load("aots").Select(HarfBuzzCases.Run)];
        WriteReport("aots", results);
        List<HarfBuzzCases.Result> failed = [.. results.Where(r => !r.Passed)];
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed.Take(30).Select(Describe)));
    }

    /// <summary>
    /// HarfBuzz's in-house test files that test what Egami leaves out: fonts without outlines, the Zawgyi script, repeated axis tags,
    /// a dfont collection, an sbix-only font, other shapers, synthetic emboldening, extents output.
    /// </summary>
    private static readonly Dictionary<string, string> s_waiting = new()
    {
        ["indic-decompose.tests"] = "a font without outlines, which Typeface does not load",
        ["indic-misc.tests"] = "a font without outlines, which Typeface does not load",
        ["myanmar-zawgyi.tests"] = "the Qaag (Zawgyi) script, which Script does not have",
        ["context-matching.tests"] = "a font without outlines, which Typeface does not load",
        ["variations.tests"] = "an fvar with three axes of one tag, all of which hb-shape sets but Typeface.WithVariations sets the first",
        ["collections.tests"] = "a dfont collection",
        ["emoji.tests"] = "an sbix-only font",
        ["color-fonts.tests"] = "extents output",
        ["simple.tests"] = "the fallback shaper",
        ["synthetic.tests"] = "synthetic slant and emboldening",
    };

    [Fact]
    public void InHouseCasesEqualHbShape()
    {
        List<HarfBuzzCases.Result> results = [.. HarfBuzzCases.Load("in-house").Select(HarfBuzzCases.Run)];
        WriteReport("in-house", results);
        List<HarfBuzzCases.Result> failed = [.. results.Where(r => !r.Passed && !s_waiting.ContainsKey(r.Case.File))];
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed.Take(30).Select(Describe)));
        Assert.True(results.Count(r => r.Passed) > 4000);
    }

    /// <summary>The corpus runs: a font and a text in its script.</summary>
    public static TheoryData<string> Corpus() =>
    [
        "NotoSans-Regular.english", "SourceSans3-Regular.english", "Roboto-Regular.english", "NotoSans-Regular.greek", "NotoSans-Regular.russian",
        "NotoSansArabic-Regular.arabic", "NotoSansArabic-Regular.persian", "NotoSansArabic-Regular.urdu", "Amiri-Regular.arabic",
        "Amiri-Regular.persian", "Amiri-Regular.urdu", "ScheherazadeNew-Regular.arabic", "ScheherazadeNew-Regular.persian",
        "ScheherazadeNew-Regular.urdu", "NotoSansHebrew-Regular.hebrew", "NotoSansDevanagari-Regular.hindi", "Mukta-Regular.hindi",
        "NotoSansBengali-Regular.bengali", "NotoSansTamil-Regular.tamil", "NotoSansThai-Regular.thai", "NotoSansLao-Regular.lao",
        "NotoSansKhmer-Regular.khmer", "NotoSansMyanmar-Regular.burmese", "NotoSansSinhala-Regular.sinhala",
    ];

    /// <summary>Corpus runs whose script shapers come in a later 2.8.1 task.</summary>
    private static readonly Dictionary<string, string> s_corpusWaiting = new()
    {
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CorpusEqualsHbShape(string name)
    {
        System.Text.Json.JsonElement root = ShapingReferences.Load(Path.Combine("corpus", name + ".json.gz"));
        Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, root.GetProperty("font").GetString()!));
        List<string> failures = [];
        foreach (System.Text.Json.JsonElement line in root.GetProperty("lines").EnumerateArray())
        {
            string text = line.GetProperty("text").GetString()!;
            string? difference = CorpusLine.Compare(face, text, line.GetProperty("glyphs"));
            if (difference is not null)
            {
                failures.Add(difference);
            }
        }

        if (Environment.GetEnvironmentVariable("EGAMI_SHAPING_REPORT") is string folder)
        {
            File.WriteAllLines(Path.Combine(folder, "corpus." + name + ".txt"), [$"{failures.Count} failing lines", .. failures]);
        }

        if (!s_corpusWaiting.ContainsKey(name))
        {
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(5)));
        }
    }

    internal static string Describe(HarfBuzzCases.Result r) =>
        $"{r.Case.File}:{r.Case.Line} {string.Join(' ', r.Case.Options)} {r.Case.Unicodes}: {r.Skipped ?? r.Difference}";

    private static void WriteReport(string suite, List<HarfBuzzCases.Result> results)
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_SHAPING_REPORT");
        if (folder is null)
        {
            return;
        }

        List<string> lines = [];
        foreach (IGrouping<string, HarfBuzzCases.Result> file in results.GroupBy(r => r.Case.File))
        {
            lines.Add($"{file.Key}: {file.Count(r => r.Passed)}/{file.Count()}");
        }

        lines.Add("");
        lines.AddRange(results.Where(r => !r.Passed).Select(Describe));
        File.WriteAllLines(Path.Combine(folder, suite + ".txt"), lines);
    }
}
