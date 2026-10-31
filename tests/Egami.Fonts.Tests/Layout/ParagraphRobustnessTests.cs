using System.Text;

using Egami.Fuzz;

namespace Egami.Fonts.Tests.Layout;

/// <summary>
/// The paragraph robustness suite (2.8.2 task 14): the paragraph fuzz target (tests/Egami.Fuzz/ParagraphFuzzTarget.cs) run in
/// process on its seeds, on random mutations of them and on hostile text under every option header below. Layout never throws
/// on text, and every line, run, caret, hit, selection, decoration and outline it gives is in range and finite.
/// </summary>
public sealed class ParagraphRobustnessTests
{
    private static readonly Lazy<Typeface[]> s_faces = new(() =>
    [
        TestFonts.Load("NotoSans-Regular.ttf"),
        TestFonts.Load("shaping/NotoSansArabic-Regular.ttf"),
        TestFonts.Load("derived/NotoSansCJK-layout-subset.otf"),
    ]);

    /// <summary>Option headers: plain; justified narrow with ellipsis and two lines; right to left balanced; vertical upright
    /// keep-all; anywhere at width 0 with a height limit; centred, unlimited width, tabs, decimal stops and wide spacing.</summary>
    private static readonly byte[][] s_headers =
    [
        [0, 0, 0, 60, 0, 0, 0, 40],
        [5, 0x40, 2, 10, 4, 0x41, 0, 0x40],
        [0x81, 0x10, 0, 30, 0, 2, 0, 4],
        [0x10, 0x01, 0, 50, 0, 0x08, 0, 1],
        [0x20, 0x8a, 210, 0, 0xf0, 0x60, 0, 2],
        [4, 0x62, 0, 255, 40, 0x80, 0x0e, 0x7c],
    ];

    private static List<byte[]> Seeds()
    {
        string folder = Path.Combine(TestFonts.Root, "tests", "images", "fuzz", "paragraph-seeds");
        return [.. Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
    }

    [Fact]
    public void SeedsRun()
    {
        List<byte[]> seeds = Seeds();
        Assert.True(seeds.Count >= 10);
        foreach (byte[] seed in seeds)
        {
            ParagraphFuzzTarget.Run(seed, s_faces.Value);
        }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void RandomMutationsNeverThrow()
    {
        List<byte[]> seeds = Seeds();
        Random random = new(282);
        for (int iteration = 0; iteration < 400; iteration++)
        {
            byte[] input = Mutate(seeds[random.Next(seeds.Count)], random);
            try
            {
                ParagraphFuzzTarget.Run(input, s_faces.Value);
            }
            catch (Exception e)
            {
                Assert.Fail($"Iteration {iteration} threw {e.GetType().Name}: {e.Message}\nInput (base64): {Convert.ToBase64String(input)}\n{e.StackTrace}");
            }
        }
    }

    public static TheoryData<string, string> HostileTexts()
    {
        TheoryData<string, string> data = new()
        {
            { "empty", string.Empty },
            { "one space", " " },
            { "only breaks", "\n\n\r\n\r\u2028\u2029\u000B\u000C\u0085" },
            { "only spaces", new string(' ', 3000) },
            { "one long word", new string('m', 2000) },
            { "nested embeddings", string.Concat(Enumerable.Repeat("\u202B\u202A", 150)) + "abc \u05D0\u05D1" + new string('\u202C', 300) },
            { "nested isolates", string.Concat(Enumerable.Repeat("\u2067\u2066", 150)) + "abc \u0627\u0644" + new string('\u2069', 300) },
            { "unpaired isolates and overrides", "\u2069\u202C\u202E abc \u2066\u202D def \u2068" },
            { "lone surrogates", "a\uD800b\uDC00c\uDFFF\uD83D" },
            { "reversed pair", "\uDE00\uD83D x \uDE00\uD83D" },
            { "combining storm", "a" + new string('\u0301', 500) + " b" },
            { "joiner chain", string.Concat(Enumerable.Repeat("\U0001F468\u200D", 300)) },
            { "tabs", "\t\t1.5\t22.75\tend\t" },
            { "soft hyphens", string.Concat(Enumerable.Repeat("ab\u00AD", 300)) },
            { "placeholders", new string('\uFFFC', 50) + " x " + new string('\uFFFC', 50) },
            { "controls", new string([.. Enumerable.Range(0, 32).Select(i => (char)i)]) },
            { "noncharacters", "\uFFFE\uFFFF\uFDD0 \U0010FFFF" },
            { "mixed scripts", "Latin \u0627\u0644\u0639\u0631\u0628\u064A\u0629 \u65E5\u672C\u8A9E \uD55C\uAD6D\uC5B4 \u0E44\u0E17\u0E22 \u0939\u093F\u0928\u094D\u0926\u0940 123 (\u05D0[b]c)" },
            { "brackets", string.Concat(Enumerable.Repeat("(\u05D0[", 200)) + string.Concat(Enumerable.Repeat("])", 200)) },
            { "url", "https://example.com/a/very/long/path/without/spaces/" + new string('x', 500) + "?q=1&r=2#frag" },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(HostileTexts))]
    public void HostileTextUnderEveryHeader(string name, string text)
    {
        foreach (byte[] header in s_headers)
        {
            try
            {
                ParagraphFuzzTarget.Run(text, header, s_faces.Value);
            }
            catch (Exception e)
            {
                Assert.Fail($"{name} with header {Convert.ToHexString(header)} threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    [Fact]
    public void EveryHeaderByteRuns()
    {
        // Each header byte through all its values, on a short mixed text.
        const string Text = "Mix \u05D0\u05D1 1.5\tx\u00ADy \uFFFC \u65E5\u672C. End\n2nd";
        for (int position = 0; position < 8; position++)
        {
            byte[] header = [3, 0x10, 2, 40, 4, 7, 6, 9];
            for (int value = 0; value < 256; value++)
            {
                header[position] = (byte)value;
                ParagraphFuzzTarget.Run(Text, header, s_faces.Value);
            }
        }
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        List<byte> data = [.. seed];
        int edits = 1 + random.Next(8);
        byte[] interesting = Encoding.Unicode.GetBytes(" \n\t\u00AD\uFFFC\u200D\u202E\u2067\u2069\u05D0\u0627\u65E5\U0001F600-/.(");
        for (int i = 0; i < edits && data.Count > 0; i++)
        {
            int at = random.Next(data.Count);
            switch (random.Next(5))
            {
                case 0:
                    data[at] = (byte)random.Next(256);
                    break;
                case 1:
                    data.RemoveRange(at, Math.Min(data.Count - at, 1 + random.Next(16)));
                    break;
                case 2:
                    int pick = random.Next(interesting.Length / 2) * 2;
                    data.InsertRange(at & ~1, interesting.AsSpan(pick, 2).ToArray());
                    break;
                case 3:
                    int from = random.Next(data.Count);
                    data.InsertRange(at, data.GetRange(from, Math.Min(64, data.Count - from)));
                    break;
                default:
                    data[random.Next(Math.Min(8, data.Count))] = (byte)random.Next(256);
                    break;
            }
        }

        return [.. data];
    }
}
