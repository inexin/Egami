using System.Buffers.Binary;

namespace Egami.Fonts.Tests;

/// <summary>
/// Robustness properties: truncated files, every table cut short or scrambled, and random glyph ids never throw anything but the
/// documented exceptions, leniently or strictly.
/// </summary>
public class RobustnessTests
{
    private static readonly string[] s_seeds =
    [
        "images/fuzz/font-seeds/noto.ttf", "images/fuzz/font-seeds/kern.ttf", "images/fuzz/font-seeds/source.otf",
        "images/fuzz/font-seeds/cjk.otf", "images/fuzz/font-seeds/NotoSans-symbol.ttf",
    ];

    public static TheoryData<string> Seeds() => [.. s_seeds];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TruncatedFilesFailOnlyWithDocumentedExceptions(string seed)
    {
        byte[] font = Bytes(seed);
        for (int length = 0; length < font.Length; length += Math.Max(1, font.Length / 97))
        {
            Exercise(font.AsSpan(0, length).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryTableCutShortFailsOnlyWithDocumentedExceptions(string seed)
    {
        byte[] font = Bytes(seed);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int t = 0; t < tables; t++)
        {
            int record = 12 + (t * 16);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12));
            foreach (uint cut in new uint[] { 0, 1, 2, 3, 7, length / 4, length / 2, length - 1 })
            {
                byte[] copy = (byte[])font.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(record + 12), Math.Min(cut, length));
                Exercise(copy);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ScrambledTablesFailOnlyWithDocumentedExceptions(string seed)
    {
        byte[] font = Bytes(seed);
        var random = new Random(seed.Length * 31);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int t = 0; t < tables; t++)
        {
            int record = 12 + (t * 16);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12));
            for (int round = 0; round < 6; round++)
            {
                byte[] copy = (byte[])font.Clone();
                for (int k = 0; k < 8 && length > 0; k++)
                {
                    copy[offset + random.Next(Math.Min(length, 64 + (round * 200)))] = (byte)random.Next(256);
                }

                Exercise(copy);
            }
        }
    }

    [Fact]
    public void RandomGlyphIdsAreSafe()
    {
        var random = new Random(7);
        foreach (string seed in s_seeds)
        {
            Typeface face = Typeface.FromMemory(Bytes(seed));
            var sink = new TextSink();
            for (int i = 0; i < 2000; i++)
            {
                ushort glyph = (ushort)random.Next(65536);
                bool inside = glyph < face.GlyphCount;
                Assert.Equal(inside, face.GetGlyphOutline(glyph, sink));
                _ = face.GetGlyphExtents(glyph);
                _ = face.GetKerning(glyph, (ushort)random.Next(65536));
                Assert.Equal(inside ? face.GetAdvance(glyph) : 0, face.GetAdvance(glyph));
            }

            GlyphRun run = GlyphRun.Create(new Font(face, 12), [65535, 0, 3], [default, default, default]);
            Assert.Equal(3, run.Count);
            _ = run.Bounds;
        }
    }

    /// <summary>The inputs AFL++ found crashes with in the first 10-minute runs, each now loading or failing as documented.</summary>
    [Fact]
    public void FuzzFindingsStayFixed()
    {
        string[] files = Directory.GetFiles(Path.Combine(TestFonts.Root, "tests", "images", "fuzz", "font-regressions"));
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(file);
            Exercise(data);
            try
            {
                Typeface face = Typeface.FromCff(data);
                _ = face.GetAdvance(1);
                face.GetGlyphOutline(1, new TextSink());
            }
            catch (EgamiException)
            {
            }
        }
    }

    private static byte[] Bytes(string seed) => File.ReadAllBytes(Path.Combine(TestFonts.Root, "tests", seed));

    private static void Exercise(byte[] data)
    {
        foreach (bool strict in new[] { false, true })
        {
            try
            {
                Typeface face = Typeface.FromMemory(data, 0, new FontLoadOptions { Strict = strict });
                _ = face.FamilyName + face.FullName + face.PostScriptName;
                _ = face.Extents;
                _ = face.Style;
                _ = face.Embedding;
                var sink = new TextSink();
                for (int g = 0; g < Math.Min(face.GlyphCount, 40); g++)
                {
                    face.GetGlyphOutline((ushort)g, sink);
                    _ = face.GetGlyphExtents((ushort)g);
                    _ = face.GetAdvance((ushort)g);
                    _ = face.GetKerning((ushort)g, 1);
                }

                _ = new Font(face, 12).MeasureText("Hello AV Å");
                _ = FontSubsetter.Subset(face, [1, 2, 3], new SubsetOptions { Format = SubsetFormat.Auto }).ToTypeface().GlyphCount;
            }
            catch (EgamiException)
            {
            }
            catch (ArgumentOutOfRangeException e) when (e.ParamName == "glyphs")
            {
                // A face with fewer than four glyphs.
            }
        }
    }
}
