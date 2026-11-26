using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>
/// Every HarfBuzz shaping test case shaped with the full face and with a <see cref="SubsetOptions.LayoutClosure"/> subset of
/// the characters it shapes: the glyphs (through the glyph map), clusters, positions and flags must be the same.
/// </summary>
public sealed class SubsetShapingCasesTests
{
    [Theory]
    [InlineData("aots")]
    [InlineData("in-house")]
    [Trait("Category", "Slow")]
    public void SubsetsShapeAsTheFullFace(string suite)
    {
        List<string> failures = [];
        Dictionary<string, int> skipped = [];
        int compared = 0;
        string? only = Environment.GetEnvironmentVariable("EGAMI_SUBSET_CASES");
        foreach (HarfBuzzCases.Case c in HarfBuzzCases.Load(suite))
        {
            if (only is not null && !only.Split(',').Any(f => c.File.StartsWith(f, StringComparison.Ordinal)))
            {
                continue;
            }

            string? why = Compare(c, retain: (c.Line & 1) != 0, out string? failure);
            if (why is not null)
            {
                skipped[why] = skipped.GetValueOrDefault(why) + 1;
                continue;
            }

            compared++;
            if (failure is not null)
            {
                failures.Add($"{c.File}:{c.Line} {string.Join(' ', c.Options)} {c.Unicodes}: {failure}");
            }
        }

        if (Environment.GetEnvironmentVariable("EGAMI_SHAPING_REPORT") is string folder)
        {
            File.WriteAllLines(
                Path.Combine(folder, "subset." + suite + ".txt"),
                [$"{compared} compared, {failures.Count} failed", .. skipped.Select(s => $"skipped {s.Value}: {s.Key}"), .. failures]);
        }

        Assert.True(only is not null || compared > (suite == "aots" ? 250 : 2000), $"Only {compared} cases compared.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {compared} differ:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(20))}");
    }

    /// <summary>
    /// The glyph flags compared: unsafe-to-break and safe-to-insert-tatweel. Unsafe-to-concat also marks where a lookup failed to
    /// match, and a subset drops the lookups and ligatures that can no longer match.
    /// </summary>
    private const uint Flags = 1 | 4;

    /// <summary>Shapes a case both ways; the reason when it is skipped, else null with the difference, if any, in <paramref name="failure"/>.</summary>
    private static string? Compare(HarfBuzzCases.Case c, bool retain, out string? failure)
    {
        failure = null;
        if (!HarfBuzzCases.TryPrepare(c, out Typeface face, out RunRequest request, out int[] codePoints, out _, out string? skipped))
        {
            return "case: " + skipped;
        }

        if (face.IsVariable || face.Instance is not null)
        {
            return "variable face (variation deltas are baked later)";
        }

        if (request.NotFound != 0 || request.Invisible != 0 || request.NotFoundVariationSelector != uint.MaxValue)
        {
            return "glyph id options";
        }

        if (face.TryGetTable(new FontTag("kern"), out _) && !(face.Layout.HasGpos && face.Layout.Features.Contains(new FontTag("kern"))))
        {
            return "legacy kern table (kept in a later step)";
        }

        if (LayoutBuffer.IsVertical(request.Direction) && face.TryGetTable(new FontTag("vmtx"), out _))
        {
            return "vertical metrics (kept in a later step)";
        }

        if (c.File.StartsWith("gsub1_1_modulo", StringComparison.Ordinal))
        {
            return "lookups that pass through glyph ids past the glyph count";
        }

        int[] all = [.. codePoints, .. request.PreContext, .. request.PostContext];
        if (all.Any(cp => cp is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF))
        {
            return "variation selectors (no cmap format 14 in subsets)";
        }

        var options = new SubsetOptions
        {
            LayoutClosure = true,
            RetainGlyphIds = retain,
            LayoutFeatures = [.. OpenTypeLayout.DefaultClosureFeatures, .. request.Features.Select(f => new FontTag(f.Tag))],
        };
        FontSubset subset;
        try
        {
            subset = FontSubsetter.Subset(face, FontSubsetter.GlyphsFor(face, all), options);
        }
        catch (ArgumentException e)
        {
            return "subset: " + e.Message;
        }
        catch (CorruptDataException e)
        {
            return "damaged: " + e.Message;
        }

        int[] clusters = [.. Enumerable.Range(0, codePoints.Length)];
        LayoutBuffer full = new();
        RunShaper.Shape(face, codePoints, clusters, request, full);
        for (int i = 0; i < full.Len; i++)
        {
            if (full.Info[i].Codepoint >= (uint)face.GlyphCount)
            {
                return "glyph ids past the glyph count";
            }
        }

        Typeface cut = subset.ToTypeface();
        foreach (int cp in all)
        {
            if (face.TryGetGlyph(cp, out ushort glyph) && glyph != 0 && subset.GlyphMap.ContainsKey(glyph) && !(cut.TryGetGlyph(cp, out ushort now) && now != 0))
            {
                return "the subset's cmap leaves out a kept character (symbol cmap)";
            }
        }

        foreach ((string tag, bool gpos) in (ReadOnlySpan<(string, bool)>)[("GSUB", false), ("GPOS", true), ("GDEF", false)])
        {
            if (cut.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> table) &&
                !(tag == "GDEF" ? LayoutSanitizer.SanitizeGdef(table.Span) : LayoutSanitizer.SanitizeLayout(table.Span, gpos)))
            {
                failure = $"the subset's {tag} fails the shaper's checks";
                return null;
            }
        }

        LayoutBuffer part = new();
        RunShaper.Shape(cut, codePoints, clusters, request, part);
        List<string> expected = [];
        List<string> actual = [];
        for (int i = 0; i < full.Len; i++)
        {
            GlyphInfo info = full.Info[i];
            GlyphPos pos = full.Pos[i];
            string id = subset.GlyphMap.TryGetValue((ushort)info.Codepoint, out ushort now) ? now.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"!{info.Codepoint}";
            expected.Add($"{id}={info.Cluster}@{pos.XOffset},{pos.YOffset}+{pos.XAdvance},{pos.YAdvance}#{info.Mask & Flags}");
        }

        for (int i = 0; i < part.Len; i++)
        {
            GlyphInfo info = part.Info[i];
            GlyphPos pos = part.Pos[i];
            actual.Add($"{info.Codepoint}={info.Cluster}@{pos.XOffset},{pos.YOffset}+{pos.XAdvance},{pos.YAdvance}#{info.Mask & Flags}");
        }

        if (!actual.SequenceEqual(expected))
        {
            failure = $"{(retain ? "retained" : "dense")}: got [{string.Join('|', actual)}] expected [{string.Join('|', expected)}]";
        }

        return null;
    }
}
