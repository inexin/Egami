using System.IO.Compression;
using System.Text.Json;

namespace Egami.Fonts.Tests.Layout;

/// <summary>
/// Headless Chromium's paragraph layouts under tests/reference/layout (tools/make-layout-references.py): per corpus case and
/// width, the line starts and line boxes, each grapheme's text rectangle, and, for interaction cases, carets, hit tests and
/// selection rectangles. Values are CSS pixels relative to the layout box's top-left corner.
/// </summary>
internal static class LayoutReferences
{
    public static readonly string[] Groups = ["corpus", "features"];

    public static string Folder => Path.Combine(TestFonts.Root, "tests", "reference", "layout");

    private static readonly Dictionary<string, Lazy<Group>> s_groups = Groups.ToDictionary(g => g, g => new Lazy<Group>(() => Read(g)));

    public static Group Load(string group) => s_groups[group].Value;

    public static IEnumerable<Case> AllCases() => Groups.SelectMany(g => Load(g).Cases);

    public static Case Find(string name) => AllCases().Single(c => c.Name == name);

    /// <summary>One group file: the Chromium version and its cases.</summary>
    public sealed record Group(string Chromium, IReadOnlyList<Case> Cases);

    /// <summary>
    /// A corpus case as tools/layoutref/corpus.py defines it: fonts in fallback order, size, text, style fields under Egami's
    /// member names, spans, placeholders, and the layout at each width.
    /// </summary>
    public sealed record Case(
        string Name,
        IReadOnlyList<string> Fonts,
        float Size,
        string Text,
        JsonElement Style,
        JsonElement Spans,
        JsonElement Placeholders,
        IReadOnlyList<Layout> Layouts);

    /// <summary>One width's layout; <see cref="Width"/> is null for no wrapping. <see cref="Ellipsis"/> is the left and right of a
    /// line clamp's ellipsis ink on the last kept line, when the recorder found it.</summary>
    public sealed record Layout(
        float? Width,
        float Height,
        float MinContent,
        float MaxContent,
        IReadOnlyList<Line> Lines,
        IReadOnlyList<Grapheme> Graphemes,
        IReadOnlyList<Caret>? Carets,
        IReadOnlyList<Hit>? Hits,
        IReadOnlyList<Selection>? Selections,
        float[]? Ellipsis,
        IReadOnlyList<IReadOnlyList<DecorationInk>?>? Decorations);

    /// <summary>One band of decoration ink on a line (one line of a decoration, two for double): its top and bottom and the x
    /// runs with ink, where gaps between runs are skip-ink.</summary>
    public sealed record DecorationInk(float Top, float Bottom, IReadOnlyList<float[]> Runs);

    /// <summary>
    /// A line: its first UTF-16 index and its line box along the block axis (over, baseline, under: y in horizontal layouts, x
    /// in vertical ones, where over is the right side), or null when the recorder could not place its markers.
    /// </summary>
    public readonly record struct Line(int Start, float[]? Box);

    /// <summary>A grapheme's text rectangle, or null when Chromium gives it none.</summary>
    public readonly record struct Grapheme(int Index, float[]? Rect);

    /// <summary>The collapsed range at a grapheme boundary: x, y and the caret's length along the block axis.</summary>
    public readonly record struct Caret(int Index, float X, float Y, float Length);

    /// <summary>caretPositionFromPoint at a point: the UTF-16 index, or null where Chromium names no text position.</summary>
    public readonly record struct Hit(float X, float Y, int? Index);

    /// <summary>Range.getClientRects for a logical range: x, y, width and height each.</summary>
    public readonly record struct Selection(int Start, int End, IReadOnlyList<float[]> Rects);

    private static Group Read(string group)
    {
        using FileStream file = File.OpenRead(Path.Combine(Folder, group + ".json.gz"));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using JsonDocument document = JsonDocument.Parse(gzip);
        JsonElement root = document.RootElement.Clone();
        return new Group(root.GetProperty("chromium").GetString()!, [.. root.GetProperty("cases").EnumerateArray().Select(ReadCase)]);
    }

    private static Case ReadCase(JsonElement element)
    {
        JsonElement c = element.GetProperty("case");
        return new Case(
            c.GetProperty("name").GetString()!,
            [.. c.GetProperty("fonts").EnumerateArray().Select(f => f.GetString()!)],
            c.GetProperty("size").GetSingle(),
            c.GetProperty("text").GetString()!,
            c.GetProperty("style"),
            c.GetProperty("spans"),
            c.GetProperty("placeholders"),
            [.. element.GetProperty("layouts").EnumerateArray().Select(ReadLayout)]);
    }

    private static Layout ReadLayout(JsonElement l) => new(
        l.GetProperty("width").ValueKind == JsonValueKind.Null ? null : l.GetProperty("width").GetSingle(),
        l.GetProperty("height").GetSingle(),
        l.GetProperty("minContent").GetSingle(),
        l.GetProperty("maxContent").GetSingle(),
        [.. l.GetProperty("lines").EnumerateArray().Select(x => new Line(x.GetProperty("start").GetInt32(), Floats(x.GetProperty("box"))))],
        [.. l.GetProperty("graphemes").EnumerateArray().Select(g => new Grapheme(g[0].GetInt32(), g[1].ValueKind == JsonValueKind.Null ? null : [.. g.EnumerateArray().Skip(1).Select(v => v.GetSingle())]))],
        l.TryGetProperty("carets", out JsonElement carets) ? [.. carets.EnumerateArray().Select(c => new Caret(c[0].GetInt32(), c[1].GetSingle(), c[2].GetSingle(), c[3].GetSingle()))] : null,
        l.TryGetProperty("hits", out JsonElement hits) ? [.. hits.EnumerateArray().Select(h => new Hit(h[0].GetSingle(), h[1].GetSingle(), h[2].ValueKind == JsonValueKind.Null ? null : h[2].GetInt32()))] : null,
        l.TryGetProperty("selections", out JsonElement selections)
            ? [.. selections.EnumerateArray().Select(s => new Selection(s[0].GetInt32(), s[1].GetInt32(), [.. s[2].EnumerateArray().Select(Floats).OfType<float[]>()]))]
            : null,
        l.TryGetProperty("ellipsis", out JsonElement ellipsis) ? Floats(ellipsis) : null,
        l.TryGetProperty("decorations", out JsonElement decorations)
            ? [.. decorations.EnumerateArray().Select(line => line.ValueKind == JsonValueKind.Null ? null : (IReadOnlyList<DecorationInk>)[.. line.EnumerateArray().Select(b =>
                new DecorationInk(b.GetProperty("top").GetSingle(), b.GetProperty("bottom").GetSingle(), [.. b.GetProperty("runs").EnumerateArray().Select(Floats).OfType<float[]>()]))])]
            : null);

    private static float[]? Floats(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : [.. e.EnumerateArray().Select(v => v.GetSingle())];
}
