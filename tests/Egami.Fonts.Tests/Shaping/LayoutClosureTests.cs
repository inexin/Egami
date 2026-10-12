using System.Globalization;
using System.Text.Json;

using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>
/// <see cref="OpenTypeLayout.GetClosure"/> gives exactly the GSUB closure hb-subset computes, for the references under
/// tests/reference/shaping/closure (tools/make-closure-references.py).
/// </summary>
public sealed class LayoutClosureTests
{
    /// <summary>HarfBuzz test fonts with GSUB that Typeface does not load: they have no outlines. CBDT-only fonts load since 2.7.2.</summary>
    private static readonly HashSet<string> s_unloadable =
    [
        "tests/fonts/harfbuzz/in-house/fonts/755160ddba002332349fda3eb999e629d63dccf6.ttf",
        "tests/fonts/harfbuzz/in-house/fonts/932ad5132c2761297c74e9976fe25b08e5ffa10b.ttf",
        "tests/fonts/harfbuzz/in-house/fonts/a59fd13f1525a91cbe529c882e93d9d1fbb80463.ttf",
    ];

    [Fact]
    public void ClosuresEqualHbSubsets()
    {
        JsonElement root = ShapingReferences.Load(Path.Combine("closure", "closure.json.gz"));
        Assert.Equal("14.5.1", root.GetProperty("harfbuzz").GetString());
        Dictionary<string, Typeface?> faces = [];
        List<string> failed = [];
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            string font = c.GetProperty("font").GetString()!;
            if (!faces.TryGetValue(font, out Typeface? face))
            {
                faces[font] = face = Load(font);
            }

            if (face is null)
            {
                continue;
            }

            cases++;
            string features = c.GetProperty("features").GetString()!;
            IEnumerable<FontTag>? tags = features switch
            {
                "default" => null,
                "*" => face.Layout.Features,
                "-" => [],
                _ => features.Split(',').Select(t => new FontTag(t)),
            };
            IReadOnlySet<ushort> closure = face.Layout.GetClosure(Ranges(c.GetProperty("input").GetString()!), tags);
            List<ushort> expected = Ranges(c.GetProperty("closure").GetString()!);
            if (!closure.SequenceEqual(expected))
            {
                failed.Add($"{font} [{features}]: {Describe(expected.Except(closure))} missing, {Describe(closure.Except(expected))} extra");
            }
        }

        Assert.True(cases > 1000, $"Only {cases} cases.");
        Assert.True(failed.Count == 0, $"{failed.Count} of {cases} differ:{Environment.NewLine}{string.Join(Environment.NewLine, failed.Take(30))}");
        Assert.Equal(s_unloadable, [.. faces.Where(f => f.Value is null).Select(f => f.Key)]);
    }

    [Fact]
    public void DefaultsAreHbSubsetsAndNoFeaturesKeepTheInput()
    {
        Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, "tests", "fonts", "NotoSans-Regular.ttf"));
        ushort[] office = [0, .. "office".Select(c => face.GetGlyph(c))];
        Assert.Equal(office.Distinct().Order(), face.Layout.GetClosure(office, []).Order());
        Assert.Equal(face.Layout.GetClosure(office).Order(), face.Layout.GetClosure(office, OpenTypeLayout.DefaultClosureFeatures).Order());
        Assert.Contains(new FontTag("liga"), OpenTypeLayout.DefaultClosureFeatures);
        Assert.DoesNotContain(new FontTag("smcp"), OpenTypeLayout.DefaultClosureFeatures);
        Assert.Contains(new FontTag("smcp"), face.Layout.Features);

        // Ids past the face's glyphs are left out.
        Assert.DoesNotContain((ushort)65535, face.Layout.GetClosure([65535]));
    }

    private static Typeface? Load(string font)
    {
        try
        {
            return Typeface.FromFile(Path.Combine(TestFonts.Root, font));
        }
        catch (Exception e) when (e is CorruptDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<ushort> Ranges(string text)
    {
        List<ushort> values = [];
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] bounds = part.Split('-');
            int first = int.Parse(bounds[0], CultureInfo.InvariantCulture);
            int last = bounds.Length > 1 ? int.Parse(bounds[1], CultureInfo.InvariantCulture) : first;
            for (int g = first; g <= last; g++)
            {
                values.Add((ushort)g);
            }
        }

        return values;
    }

    private static string Describe(IEnumerable<ushort> glyphs)
    {
        List<ushort> list = [.. glyphs];
        return list.Count == 0 ? "none" : $"[{string.Join(",", list.Take(20))}{(list.Count > 20 ? ",..." : "")}] ({list.Count})";
    }
}
