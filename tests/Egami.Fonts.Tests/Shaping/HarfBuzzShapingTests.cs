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
    /// HarfBuzz's in-house test files that wait for a later 2.8.1 task (script shapers 9 to 11, vertical runs 13, variable fonts 14),
    /// or test what Egami leaves out (a dfont collection, an sbix-only font, other shapers, synthetic emboldening, extents output).
    /// </summary>
    private static readonly Dictionary<string, string> s_waiting = new()
    {
        ["arabic-fallback-shaping.tests"] = "task 9",
        ["arabic-feature-order.tests"] = "task 9",
        ["arabic-like-joining.tests"] = "task 9",
        ["arabic-mark-order.tests"] = "task 9",
        ["arabic-normalization.tests"] = "task 9",
        ["arabic-phags-pa.tests"] = "task 9",
        ["arabic-stch.tests"] = "task 9",
        ["cursive-positioning.tests"] = "task 9",
        ["default-ignorables.tests"] = "task 9",
        ["item-context.tests"] = "task 9",
        ["mark-filtering-sets.tests"] = "task 9",
        ["nested-mark-filtering-sets.tests"] = "task 9",
        ["mongolian-variation-selector.tests"] = "task 9",
        ["reverse-sub.tests"] = "task 9",
        ["unsafe-to-concat.tests"] = "task 9",
        ["cluster.tests"] = "task 10",
        ["ligature-id.tests"] = "task 10",
        ["per-script-kern-fallback.tests"] = "task 10",
        ["indic-consonant-with-stacker.tests"] = "task 10",
        ["indic-decompose.tests"] = "task 10",
        ["indic-init.tests"] = "task 10",
        ["indic-joiner-candrabindu.tests"] = "task 10",
        ["indic-joiners.tests"] = "task 10",
        ["indic-malayalam-dot-reph.tests"] = "task 10",
        ["indic-misc.tests"] = "task 10",
        ["indic-old-spec.tests"] = "task 10",
        ["indic-pref-blocking.tests"] = "task 10",
        ["indic-script-extensions.tests"] = "task 10",
        ["indic-special-cases.tests"] = "task 10",
        ["indic-syllable.tests"] = "task 10",
        ["indic-vowel-letter-spoofing.tests"] = "task 10",
        ["khmer-mark-order.tests"] = "task 10",
        ["khmer-misc.tests"] = "task 10",
        ["mark-attachment.tests"] = "task 10",
        ["myanmar-misc.tests"] = "task 10",
        ["myanmar-syllable.tests"] = "task 10",
        ["myanmar-zawgyi.tests"] = "task 10",
        ["context-matching.tests"] = "task 11",
        ["glyph-props-no-gdef.tests"] = "task 11",
        ["hangul-calt.tests"] = "task 11",
        ["hangul-jamo.tests"] = "task 11",
        ["sara-am.tests"] = "task 11",
        ["sinhala.tests"] = "task 11",
        ["tibetan-contractions-1.tests"] = "task 11",
        ["tibetan-contractions-2.tests"] = "task 11",
        ["tibetan-vowels.tests"] = "task 11",
        ["use-indic3.tests"] = "task 11",
        ["use-javanese.tests"] = "task 11",
        ["use-marchen.tests"] = "task 11",
        ["use-syllable.tests"] = "task 11",
        ["use-vowel-letter-spoofing.tests"] = "task 11",
        ["use.tests"] = "task 11",
        ["spaces.tests"] = "task 13",
        ["vertical.tests"] = "task 13",
        ["variations.tests"] = "task 14",
        ["variations-rvrn.tests"] = "task 14",
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

    /// <summary>Corpus runs whose scripts the engine shapes completely so far; the others wait for their script shapers.</summary>
    public static TheoryData<string> DefaultShaperCorpus() => ["NotoSans-Regular.english", "SourceSans3-Regular.english", "Roboto-Regular.english", "NotoSans-Regular.greek", "NotoSans-Regular.russian"];

    [Theory]
    [MemberData(nameof(DefaultShaperCorpus))]
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

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(5)));
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
