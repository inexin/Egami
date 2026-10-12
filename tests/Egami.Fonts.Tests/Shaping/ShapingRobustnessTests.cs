using System.Buffers.Binary;
using System.Text;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>Property tests: random text, random spans and damaged layout tables shape without throwing and keep their invariants.</summary>
public sealed class ShapingRobustnessTests
{
    private static readonly string[] s_fonts =
    [
        "NotoSans-Regular.ttf", "SourceSans3-Regular.otf", "shaping/Amiri-Regular.ttf", "shaping/NotoSansDevanagari-Regular.ttf",
        "shaping/NotoSansKhmer-Regular.ttf", "shaping/NotoSansMyanmar-Regular.ttf", "shaping/NotoSansThai-Regular.ttf",
    ];

    // Code point ranges random text draws from: Latin, combining marks, Arabic, Hebrew, Devanagari, Bengali, Thai, Khmer, Myanmar,
    // Hangul jamo and syllables, Tibetan, joiners and variation selectors, bidi controls, emoji, and lone surrogates.
    private static readonly (int Start, int End)[] s_ranges =
    [
        (0x20, 0x7E), (0x300, 0x36F), (0x600, 0x6FF), (0x590, 0x5FF), (0x900, 0x97F), (0x980, 0x9FF), (0xE00, 0xE7F), (0x1780, 0x17FF),
        (0x1000, 0x109F), (0x1100, 0x11FF), (0xAC00, 0xAC40), (0xF00, 0xFFF), (0x200B, 0x200F), (0xFE00, 0xFE0F), (0x202A, 0x202E),
        (0x2066, 0x2069), (0x1F300, 0x1F64F), (0x1F3FB, 0x1F3FF), (0xD800, 0xDFFF),
    ];

    private static string RandomText(Random random, int length)
    {
        StringBuilder text = new();
        while (text.Length < length)
        {
            (int start, int end) = s_ranges[random.Next(s_ranges.Length)];
            int cp = random.Next(start, end + 1);
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                text.Append((char)cp);
            }
            else
            {
                text.Append(char.ConvertFromUtf32(cp));
            }

            if (random.Next(20) == 0)
            {
                text.Append(random.Next(3) switch { 0 => '\n', 1 => ' ', _ => '\u2029' });
            }
        }

        return text.ToString();
    }

    [Fact]
    public void RandomTextShapesInEveryFont()
    {
        Random random = new(2801);
        Typeface[] faces = [.. s_fonts.Select(f => TestFonts.Load(f))];
        FontFallback fallback = new([.. faces.Select(Family)]);
        for (int round = 0; round < 300; round++)
        {
            string text = RandomText(random, random.Next(1, 60));
            Font font = new(faces[random.Next(faces.Length)], 12);
            ShapingOptions options = new()
            {
                Direction = (TextDirection)random.Next(4),
                Fallback = random.Next(2) == 0 ? fallback : FontFallback.None,
                ClusterLevel = (ClusterLevel)random.Next(3),
                Language = random.Next(3) == 0 ? "ur" : null,
                Spans = RandomSpans(random, text.Length, faces),
            };
            ShapedText shaped = TextShaper.Shape(text, font, options);
            foreach (ShapedLine line in shaped.Lines)
            {
                foreach (ShapedRun run in line.Runs)
                {
                    Assert.True(run.Glyphs.Count > 0);
                    foreach (int cluster in run.Glyphs.Clusters)
                    {
                        Assert.InRange(cluster, run.Start, run.Start + run.Length - 1);
                    }

                    Assert.All(run.Glyphs.Positions.ToArray(), p => Assert.True(float.IsFinite(p.XAdvance) && float.IsFinite(p.YOffset)));
                }
            }
        }
    }

    private static TextSpan[] RandomSpans(Random random, int length, Typeface[] faces)
    {
        List<TextSpan> spans = [];
        int at = 0;
        while (at < length && random.Next(3) != 0)
        {
            int start = random.Next(at, length);
            int spanLength = random.Next(0, length - start + 1);
            spans.Add(new TextSpan(start, spanLength)
            {
                Font = random.Next(2) == 0 ? new Font(faces[random.Next(faces.Length)], 9) : null,
                Features = random.Next(2) == 0 ? [FontFeature.Ligatures(false), FontFeature.Parse("kern[1:3]=0")] : null,
            });
            at = start + spanLength + 1;
        }

        return [.. spans];
    }

    [Theory]
    [InlineData("shaping/Amiri-Regular.ttf", "\u0628\u0650\u0633\u0652\u0645\u0650 \u0627\u0644\u0644\u0651\u064E\u0647\u0650")]
    [InlineData("shaping/NotoSansDevanagari-Regular.ttf", "\u0915\u094D\u0937\u093F \u0930\u094D\u0926\u094D\u0927")]
    [InlineData("NotoSans-Regular.ttf", "office AVA fi\u0301")]
    public void LayoutTablesCutShortShapeWithoutThrowing(string file, string text)
    {
        byte[] original = File.ReadAllBytes(TestFonts.FontPath(file));
        foreach (string tag in new[] { "GSUB", "GPOS", "GDEF" })
        {
            if (!TryFindTable(original, tag, out int entry, out int length))
            {
                continue;
            }

            // Every lookup, and every structure before it, cut short: the table ends at 1/16 steps of its length.
            for (int step = 0; step < 16; step++)
            {
                byte[] damaged = (byte[])original.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(entry + 12), (uint)(length * step / 16));
                Typeface face = Typeface.FromMemory(damaged);
                ShapedText shaped = TextShaper.Shape(text, new Font(face, 12));
                Assert.NotEmpty(shaped.Lines[0].Runs);
            }
        }
    }

    private static bool TryFindTable(byte[] font, string tag, out int entry, out int length)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                entry = record;
                length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12));
                return true;
            }
        }

        entry = 0;
        length = 0;
        return false;
    }

    private static FontFamily Family(Typeface face)
    {
        FontCollection collection = new();
        collection.Add(face);
        return collection.Families[0];
    }
}
