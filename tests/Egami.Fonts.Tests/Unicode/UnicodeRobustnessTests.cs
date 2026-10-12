using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Unicode;

/// <summary>Random text, including lone surrogates, controls and deep embeddings, never throws and always partitions.</summary>
public sealed class UnicodeRobustnessTests
{
    private static readonly int[] s_interesting =
    [
        0x0009, 0x000A, 0x000D, 0x0020, 0x0028, 0x0029, 0x0031, 0x005B, 0x005D, 0x0061, 0x0301, 0x05D0, 0x0627, 0x0660, 0x0915, 0x094D,
        0x0964, 0x0E01, 0x1100, 0x1161, 0x11A8, 0x200C, 0x200D, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069,
        0x2029, 0x3001, 0x3042, 0x4E00, 0xAC00, 0xD800, 0xDC00, 0xFE0F, 0x1F1E6, 0x1F1E7, 0x1F468, 0x1F3FB, 0x1F600, 0xE0001, 0x10FFFF,
    ];

    [Fact]
    public void RandomTextPartitionsAndResolves()
    {
        Random random = new(2801);
        List<ScriptRun> runs = [];
        for (int round = 0; round < 3000; round++)
        {
            System.Text.StringBuilder builder = new();
            int length = random.Next(0, 200);
            for (int i = 0; i < length; i++)
            {
                int cp = random.Next(4) == 0 ? random.Next(0x110000) : s_interesting[random.Next(s_interesting.Length)];
                if (cp is >= 0xD800 and <= 0xDFFF)
                {
                    builder.Append((char)cp);
                }
                else
                {
                    builder.Append(char.ConvertFromUtf32(cp));
                }
            }

            string text = builder.ToString();
            int end = 0;
            foreach (TextSegment g in TextSegmenter.Graphemes(text))
            {
                Assert.Equal(end, g.Start);
                Assert.True(g.Length > 0);
                Assert.True(TextSegmenter.IsGraphemeBoundary(text, g.Start));
                end = g.End;
            }

            Assert.Equal(text.Length, end);
            end = 0;
            foreach (TextSegment w in TextSegmenter.Words(text))
            {
                Assert.Equal(end, w.Start);
                Assert.True(w.Length > 0);
                end = w.End;
            }

            Assert.Equal(text.Length, end);

            runs.Clear();
            ScriptItemizer.Itemize(text, runs);
            Assert.Equal(text.Length, runs.Sum(r => r.Length));

            foreach (TextDirection direction in new[] { TextDirection.Auto, TextDirection.LeftToRight, TextDirection.RightToLeft })
            {
                using BidiResult bidi = Bidi.Resolve(text, direction);
                Assert.All(bidi.Levels.ToArray(), level => Assert.InRange(level, 0, BidiResolver.MaxDepth + 1));
                Assert.Equal(text.Length, bidi.Runs.ToArray().Sum(r => r.Length));
                Assert.Equal(text.Length, bidi.Reorder(0, text.Length).Sum(r => r.Length));
            }
        }
    }

    [Fact]
    public void DeepEmbeddingsStopAtTheMaximumDepth()
    {
        string text = string.Concat(Enumerable.Repeat("\u202B\u202A", 200)) + "a" + string.Concat(Enumerable.Repeat("\u2067", 200)) + "b";
        using BidiResult bidi = Bidi.Resolve(text);
        Assert.True(bidi.Levels.ToArray().Max() <= BidiResolver.MaxDepth + 1);
    }
}
