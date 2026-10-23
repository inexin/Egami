using System.Globalization;
using System.Text.Json;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>Runs HarfBuzz's shaping test cases (tests/reference/shaping/harfbuzz) through Egami's engine and compares with hb-shape.</summary>
internal static class HarfBuzzCases
{
    public sealed record Case(string File, int Line, string Font, string[] Options, string Unicodes, JsonElement Glyphs);

    public sealed record Result(Case Case, bool Passed, string? Skipped, string? Difference);

    private static readonly Dictionary<(string, int), Typeface> s_faces = [];

    public static IEnumerable<Case> Load(string suite)
    {
        JsonElement root = ShapingReferences.Load(Path.Combine("harfbuzz", suite + ".json.gz"));
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            yield return new Case(
                c.GetProperty("file").GetString()!,
                c.GetProperty("line").GetInt32(),
                c.GetProperty("font").GetString()!,
                [.. c.GetProperty("options").EnumerateArray().Select(o => o.GetString()!)],
                c.GetProperty("unicodes").GetString()!,
                c.GetProperty("glyphs"));
        }
    }

    public static Result Run(Case c)
    {
        RunRequest request = new();
        bool noPositions = false, noClusters = false, showFlags = true;
        int faceIndex = 0;
        List<FontVariation> variations = [];
        for (int o = 0; o < c.Options.Length; o++)
        {
            string option = c.Options[o];
            string name = option;
            string value = "";
            int eq = option.IndexOf('=');
            if (eq >= 0)
            {
                name = option[..eq];
                value = option[(eq + 1)..].Trim('"');
            }
            else if (option == "--variations" && o + 1 < c.Options.Length)
            {
                // hb-shape also takes the value as the next argument.
                value = c.Options[++o].Trim('"');
            }

            switch (name)
            {
                case "--features":
                    try
                    {
                        request.Features = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => ParseFeature(f.Trim()))];
                    }
                    catch (FormatException)
                    {
                        return new Result(c, false, "feature syntax " + value, null);
                    }

                    break;
                case "--direction":
                    request.Direction = char.ToLowerInvariant(value[0]) switch
                    {
                        'l' => LayoutDirection.LeftToRight,
                        'r' => LayoutDirection.RightToLeft,
                        't' => LayoutDirection.TopToBottom,
                        'b' => LayoutDirection.BottomToTop,
                        _ => LayoutDirection.Invalid,
                    };
                    break;
                case "--script":
                    if (Script.TryFromCode(value, out Script script))
                    {
                        request.Script = script;
                    }
                    else
                    {
                        return new Result(c, false, "script " + value, null);
                    }

                    break;
                case "--language":
                    request.LanguageTags = LanguageTags.FromBcp47(value);
                    break;
                case "--cluster-level":
                    request.ClusterLevel = (LayoutClusterLevel)int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--no-positions":
                    noPositions = true;
                    break;
                case "--no-clusters":
                    noClusters = true;
                    break;
                case "--ned":
                    noPositions = true;
                    noClusters = true;
                    break;
                case "--bot":
                    request.BeginningOfText = true;
                    break;
                case "--eot":
                    request.EndOfText = true;
                    break;
                case "--preserve-default-ignorables":
                    request.PreserveDefaultIgnorables = true;
                    break;
                case "--remove-default-ignorables":
                    request.RemoveDefaultIgnorables = true;
                    break;
                case "--do-not-insert-dotted-circle":
                    request.DoNotInsertDottedCircle = true;
                    break;
                case "--unsafe-to-concat":
                    request.ProduceUnsafeToConcat = true;
                    break;
                case "--safe-to-insert-tatweel":
                    request.ProduceSafeToInsertTatweel = true;
                    break;
                case "--invisible-glyph":
                    request.Invisible = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--not-found-glyph":
                    request.NotFound = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--not-found-variation-selector-glyph":
                    request.NotFoundVariationSelector = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--variations":
                    foreach (string setting in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = setting.Trim().Split('=');
                        variations.Add(new FontVariation(parts[0].PadRight(4), float.Parse(parts[1], CultureInfo.InvariantCulture)));
                    }

                    break;
                case "--font-size":
                    request.Scale = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--face-index":
                    faceIndex = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--show-flags":
                    showFlags = true;
                    break;
                case "--unicodes-before":
                    // Context runs nearest first, at most five characters, as hb_buffer_add_codepoints keeps it.
                    request.PreContext = [.. ParseUnicodes(value).Reverse().Take(5)];
                    break;
                case "--unicodes-after":
                    request.PostContext = [.. ParseUnicodes(value).Take(5)];
                    break;
                case "--shapers" or "--shaper" when value != "ot":
                    return new Result(c, false, "shaper " + value, null);
                case "--no-glyph-names":
                case "--font-funcs":
                case "--shapers":
                case "--shaper":
                case "--single-par":
                case "--show-text":
                case "--show-unicode":
                case "--verify":
                    break;
                default:
                    return new Result(c, false, "option " + option, null);
            }
        }

        Typeface face;
        try
        {
            if (!s_faces.TryGetValue((c.Font, faceIndex), out face!))
            {
                face = Typeface.FromFile(Path.Combine(TestFonts.Root, c.Font), faceIndex);
                s_faces[(c.Font, faceIndex)] = face;
            }
        }
        catch (Exception e) when (e is CorruptDataException or NotSupportedException or ArgumentException or UnsupportedFormatException)
        {
            return new Result(c, false, "font " + e.Message, null);
        }

        if (variations.Count > 0)
        {
            face = face.WithVariations([.. variations]);
        }

        int[] codePoints = ParseUnicodes(c.Unicodes);
        int[] clusters = [.. Enumerable.Range(0, codePoints.Length)];
        LayoutBuffer buffer = new();
        RunShaper.Shape(face, codePoints, clusters, request, buffer);

        ShapingReferences.Glyph[] expected = ShapingReferences.Glyphs(c.Glyphs);
        List<string> actual = [];
        List<string> wanted = [];
        for (int i = 0; i < buffer.Len; i++)
        {
            GlyphInfo info = buffer.Info[i];
            GlyphPos pos = buffer.Pos[i];
            actual.Add(Format((int)info.Codepoint, info.Cluster, (int)(info.Mask & (1u | (request.ProduceUnsafeToConcat ? 2u : 0u) | (request.ProduceSafeToInsertTatweel ? 4u : 0u))), pos.XAdvance, pos.YAdvance, pos.XOffset, pos.YOffset, noPositions, noClusters, showFlags));
        }

        foreach (ShapingReferences.Glyph g in expected)
        {
            wanted.Add(Format(g.Id, g.Cluster, g.Flags, g.XAdvance, g.YAdvance, g.XOffset, g.YOffset, noPositions, noClusters, showFlags));
        }

        if (actual.SequenceEqual(wanted))
        {
            return new Result(c, true, null, null);
        }

        return new Result(c, false, null, $"got [{string.Join('|', actual)}] expected [{string.Join('|', wanted)}]");
    }

    private static int[] ParseUnicodes(string text) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(u => int.Parse(u.Trim().TrimStart('U', 'u', '+'), NumberStyles.HexNumber, CultureInfo.InvariantCulture))];

    private static string Format(int g, int cl, int fl, int ax, int ay, int dx, int dy, bool noPositions, bool noClusters, bool showFlags)
    {
        string s = g.ToString(CultureInfo.InvariantCulture);
        if (!noClusters)
        {
            s += "=" + cl.ToString(CultureInfo.InvariantCulture);
        }

        if (!noPositions)
        {
            s += $"@{dx},{dy}+{ax},{ay}";
        }

        if (showFlags && fl != 0)
        {
            s += "#" + fl.ToString(CultureInfo.InvariantCulture);
        }

        return s;
    }

    /// <summary>HarfBuzz's feature syntax: tag, +tag, -tag, tag=value, tag[start:end]=value.</summary>
    public static FeatureSetting ParseFeature(string text) => FontFeatureSyntax.Parse(text);
}
