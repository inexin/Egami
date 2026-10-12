using System.Text.Json;

using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>Shapes one corpus line as hb-shape did (no language, cluster level 0, no beginning-of-text flag) and compares.</summary>
internal static class CorpusLine
{
    public static string? Compare(Typeface face, string text, JsonElement expectedGlyphs)
    {
        int[] codePoints = [.. text.EnumerateRunes().Select(r => r.Value)];
        int[] clusters = [.. Enumerable.Range(0, codePoints.Length)];
        LayoutBuffer buffer = new();
        RunShaper.Shape(face, codePoints, clusters, new RunRequest(), buffer);
        ShapingReferences.Glyph[] expected = ShapingReferences.Glyphs(expectedGlyphs);
        List<string> actual = [];
        for (int i = 0; i < buffer.Len; i++)
        {
            GlyphInfo info = buffer.Info[i];
            GlyphPos pos = buffer.Pos[i];
            actual.Add($"{info.Codepoint}={info.Cluster}@{pos.XOffset},{pos.YOffset}+{pos.XAdvance},{pos.YAdvance}#{info.Mask & 1}");
        }

        List<string> wanted = [.. expected.Select(g => $"{g.Id}={g.Cluster}@{g.XOffset},{g.YOffset}+{g.XAdvance},{g.YAdvance}#{g.Flags & 1}")];
        if (actual.SequenceEqual(wanted))
        {
            return null;
        }

        int first = 0;
        while (first < Math.Min(actual.Count, wanted.Count) && actual[first] == wanted[first])
        {
            first++;
        }

        return $"{text[..Math.Min(40, text.Length)]}...: glyph {first}: got {string.Join('|', actual.Skip(first).Take(6))} expected {string.Join('|', wanted.Skip(first).Take(6))}";
    }
}
