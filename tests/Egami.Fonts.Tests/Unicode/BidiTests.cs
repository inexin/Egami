using System.Globalization;
using System.Text;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Unicode;

/// <summary>Unicode's bidi conformance files pass in full: BidiTest.txt and BidiCharacterTest.txt.</summary>
public sealed class BidiTests
{
    private static readonly Dictionary<string, BidiClass> s_classes = new()
    {
        ["L"] = BidiClass.LeftToRight,
        ["R"] = BidiClass.RightToLeft,
        ["AL"] = BidiClass.ArabicLetter,
        ["EN"] = BidiClass.EuropeanNumber,
        ["ES"] = BidiClass.EuropeanSeparator,
        ["ET"] = BidiClass.EuropeanTerminator,
        ["AN"] = BidiClass.ArabicNumber,
        ["CS"] = BidiClass.CommonSeparator,
        ["NSM"] = BidiClass.NonspacingMark,
        ["BN"] = BidiClass.BoundaryNeutral,
        ["B"] = BidiClass.ParagraphSeparator,
        ["S"] = BidiClass.SegmentSeparator,
        ["WS"] = BidiClass.WhiteSpace,
        ["ON"] = BidiClass.OtherNeutral,
        ["LRE"] = BidiClass.LeftToRightEmbedding,
        ["LRO"] = BidiClass.LeftToRightOverride,
        ["RLE"] = BidiClass.RightToLeftEmbedding,
        ["RLO"] = BidiClass.RightToLeftOverride,
        ["PDF"] = BidiClass.PopDirectionalFormat,
        ["LRI"] = BidiClass.LeftToRightIsolate,
        ["RLI"] = BidiClass.RightToLeftIsolate,
        ["FSI"] = BidiClass.FirstStrongIsolate,
        ["PDI"] = BidiClass.PopDirectionalIsolate,
    };

    [Fact]
    public void BidiTestPasses()
    {
        BidiResolver resolver = new();
        string[] expectedLevels = [];
        string expectedOrder = "";
        int cases = 0;
        List<string> failures = [];
        foreach (string raw in UnicodeFiles.Lines("BidiTest.txt.gz"))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                expectedLevels = line[8..].Split([' ', '	'], StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                expectedOrder = string.Join(' ', line[9..].Split([' ', '	'], StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            if (line.StartsWith('@'))
            {
                continue;
            }

            string[] parts = line.Split(';');
            BidiClass[] classes = [.. parts[0].Split([' ', '	'], StringSplitOptions.RemoveEmptyEntries).Select(c => s_classes[c])];
            int bits = int.Parse(parts[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            foreach ((int bit, TextDirection direction) in new[] { (1, TextDirection.Auto), (2, TextDirection.LeftToRight), (4, TextDirection.RightToLeft) })
            {
                if ((bits & bit) == 0)
                {
                    continue;
                }

                cases++;
                byte[] levels = new byte[classes.Length];
                resolver.Resolve(classes, ReadOnlyMemory<int>.Empty, direction, levels);
                BidiResolver.ApplyLineRule(classes, levels, resolver.ParagraphLevel);
                string actualLevels = string.Join(' ', classes.Select((c, i) => BidiResolver.IsRemovedByX9(c) ? "x" : levels[i].ToString(CultureInfo.InvariantCulture)));
                string actualOrder = string.Join(' ', VisualOrder(classes, levels));
                if (actualLevels != string.Join(' ', expectedLevels) || actualOrder != expectedOrder)
                {
                    if (failures.Count < 20)
                    {
                        failures.Add($"{parts[0].Trim()} ({direction}): levels {actualLevels} expected {string.Join(' ', expectedLevels)}; order {actualOrder} expected {expectedOrder}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.True(cases > 400_000, $"Only {cases} cases ran.");
    }

    [Fact]
    public void BidiCharacterTestPasses()
    {
        int cases = 0;
        List<string> failures = [];
        foreach (string raw in UnicodeFiles.Lines("BidiCharacterTest.txt.gz"))
        {
            if (raw.Length == 0 || raw.StartsWith('#'))
            {
                continue;
            }

            string[] f = raw.Split(';');
            int[] cps = [.. f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
            TextDirection direction = f[1] switch
            {
                "0" => TextDirection.LeftToRight,
                "1" => TextDirection.RightToLeft,
                _ => TextDirection.Auto,
            };
            int paragraphLevel = int.Parse(f[2], CultureInfo.InvariantCulture);
            string expectedLevels = f[3].Trim();
            string expectedOrder = f[4].Trim();

            StringBuilder text = new();
            List<int> unitOf = [];
            foreach (int cp in cps)
            {
                unitOf.Add(text.Length);
                text.Append(char.ConvertFromUtf32(cp));
            }

            cases++;
            using BidiResult result = Bidi.Resolve(text.ToString(), direction);
            string actualLevels = string.Join(' ', cps.Select((cp, i) => BidiResolver.IsRemovedByX9(UnicodeData.GetBidiClass(cp)) ? "x" : result.Levels[unitOf[i]].ToString(CultureInfo.InvariantCulture)));
            List<int> order = [];
            foreach (BidiRun run in result.Reorder(0, result.Length))
            {
                for (int k = 0; k < run.Length; k++)
                {
                    int unit = run.IsRightToLeft ? run.Start + run.Length - 1 - k : run.Start + k;
                    int index = unitOf.IndexOf(unit);
                    if (index >= 0 && !BidiResolver.IsRemovedByX9(UnicodeData.GetBidiClass(cps[index])))
                    {
                        order.Add(index);
                    }
                }
            }

            string actualOrder = string.Join(' ', order);
            if (result.ParagraphLevel != paragraphLevel || actualLevels != expectedLevels || actualOrder != expectedOrder)
            {
                if (failures.Count < 20)
                {
                    failures.Add($"{f[0]} ({direction}): paragraph {result.ParagraphLevel} expected {paragraphLevel}; levels {actualLevels} expected {expectedLevels}; order {actualOrder} expected {expectedOrder}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.True(cases > 90_000, $"Only {cases} cases ran.");
    }

    [Fact]
    public void ResolvesMixedTextAndReordersALine()
    {
        using BidiResult result = Bidi.Resolve("abc \u05D0\u05D1\u05D2 def");
        Assert.Equal(0, result.ParagraphLevel);
        Assert.Equal([new BidiRun(0, 4, 0), new BidiRun(4, 3, 1), new BidiRun(7, 4, 0)], result.Runs.ToArray());
        Assert.Equal([new BidiRun(0, 4, 0), new BidiRun(4, 3, 1), new BidiRun(7, 4, 0)], result.Reorder(0, result.Length));

        using BidiResult rtl = Bidi.Resolve("\u05D0\u05D1 abc \u05D2");
        Assert.Equal(1, rtl.ParagraphLevel);
        Assert.Equal([new BidiRun(6, 2, 1), new BidiRun(3, 3, 2), new BidiRun(0, 3, 1)], rtl.Reorder(0, rtl.Length));
    }

    [Fact]
    public void SurrogatePairsShareALevelAndParagraphsResolveOnTheirOwn()
    {
        // U+10900 PHOENICIAN LETTER ALF is R; the second paragraph is left to right.
        using BidiResult result = Bidi.Resolve("\uD802\uDD00x\u2029abc");
        Assert.Equal(1, result.ParagraphLevel);
        Assert.Equal(1, result.Levels[0]);
        Assert.Equal(1, result.Levels[1]);
        Assert.Equal(2, result.Levels[2]);
        Assert.Equal(1, result.Levels[3]);
        Assert.Equal(0, result.Levels[4]);
    }

    [Fact]
    public void EmptyTextAndDisposal()
    {
        BidiResult empty = Bidi.Resolve("", TextDirection.RightToLeft);
        Assert.Equal(0, empty.Length);
        Assert.Equal(1, empty.ParagraphLevel);
        Assert.Empty(empty.Reorder(0, 0));
        empty.Dispose();
        empty.Dispose();
        Assert.Throws<ObjectDisposedException>(() => empty.Levels.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Bidi.Resolve("a", (TextDirection)9));

        using BidiResult result = Bidi.Resolve("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Reorder(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Reorder(-1, 1));
    }

    /// <summary>Rule L2 over characters, leaving out the ones X9 removes.</summary>
    private static List<int> VisualOrder(BidiClass[] classes, byte[] levels)
    {
        List<int> order = [];
        for (int i = 0; i < classes.Length; i++)
        {
            if (!BidiResolver.IsRemovedByX9(classes[i]))
            {
                order.Add(i);
            }
        }

        int highest = order.Count == 0 ? 0 : order.Max(i => levels[i]);
        int lowestOdd = order.Where(i => (levels[i] & 1) != 0).Select(i => (int)levels[i]).DefaultIfEmpty(int.MaxValue).Min();
        for (int level = highest; level >= lowestOdd && level > 0; level--)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (levels[order[i]] < level)
                {
                    continue;
                }

                int end = i;
                while (end < order.Count && levels[order[end]] >= level)
                {
                    end++;
                }

                order.Reverse(i, end - i);
                i = end;
            }
        }

        return order;
    }
}
