using System.Text.Json;

using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Builds the Egami paragraph a Chromium reference case describes, field for field (tools/layoutref/corpus.py).</summary>
internal static class LayoutCases
{
    public static Paragraph Paragraph(LayoutReferences.Case c) => Egami.Fonts.Layout.Paragraph.Create(RichText(c), Style(c));

    public static RichText RichText(LayoutReferences.Case c)
    {
        List<StyleSpan> spans = [];
        foreach (JsonElement span in c.Spans.EnumerateArray())
        {
            spans.Add(new StyleSpan(span.GetProperty("start").GetInt32(), span.GetProperty("length").GetInt32(), TextStyle(span.GetProperty("style"))));
        }

        List<Placeholder> placeholders = [];
        foreach (JsonElement p in c.Placeholders.EnumerateArray())
        {
            placeholders.Add(new Placeholder(
                p.GetProperty("width").GetSingle(),
                p.GetProperty("height").GetSingle(),
                System.Enum.Parse<PlaceholderAlignment>(p.GetProperty("alignment").GetString()!),
                p.TryGetProperty("baselineOffset", out JsonElement o) ? o.GetSingle() : 0));
        }

        return new RichText(c.Text, spans, placeholders);
    }

    public static ParagraphStyle Style(LayoutReferences.Case c)
    {
        JsonElement s = c.Style;
        TextStyle defaults = new()
        {
            FontFamilies = [.. c.Fonts.Select(ParagraphTests.Family)],
            Size = c.Size,
            Language = String(s, "language"),
            LetterSpacing = Float(s, "letterSpacing"),
            WordSpacing = Float(s, "wordSpacing"),
            LineHeight = s.TryGetProperty("lineHeight", out JsonElement lh) ? LineHeight(lh) : null,
        };
        ParagraphStyle style = new()
        {
            DefaultStyle = defaults,
            Direction = EnumOf<TextDirection>(s, "direction", TextDirection.Auto),
            Align = EnumOf<TextAlign>(s, "align", TextAlign.Start),
            AlignLast = EnumOf<TextAlignLast>(s, "alignLast", TextAlignLast.Auto),
            Justify = EnumOf<TextJustify>(s, "justify", TextJustify.Auto),
            LineBreaking = LineBreakTests.Options(c),
            LineFill = EnumOf<LineFill>(s, "lineFill", LineFill.Greedy),
            WritingMode = EnumOf<WritingMode>(s, "writingMode", WritingMode.HorizontalTb),
            TextOrientation = EnumOf<TextOrientation>(s, "textOrientation", TextOrientation.Mixed),
            TabSize = Float(s, "tabSize") ?? 8,
            MaxLines = s.TryGetProperty("maxLines", out JsonElement ml) ? ml.GetInt32() : null,
        };
        if (s.TryGetProperty("hyphen", out JsonElement hyphen))
        {
            style = style with { Hyphen = hyphen.GetString() };
        }

        if (s.TryGetProperty("ellipsis", out JsonElement ellipsis))
        {
            style = style with { Ellipsis = ellipsis.GetString() };
        }

        if (s.TryGetProperty("strut", out JsonElement strut))
        {
            style = style with
            {
                Strut = new Strut
                {
                    FontFamilies = [.. strut.GetProperty("fonts").EnumerateArray().Select(f => ParagraphTests.Family(f.GetString()!))],
                    Size = strut.GetProperty("size").GetSingle(),
                    LineHeight = strut.TryGetProperty("lineHeight", out JsonElement slh) ? LineHeight(slh) : Egami.Fonts.Layout.LineHeight.Normal,
                },
            };
        }

        return style;
    }

    private static TextStyle TextStyle(JsonElement s) => new()
    {
        FontFamilies = s.TryGetProperty("fonts", out JsonElement fonts) ? [.. fonts.EnumerateArray().Select(f => ParagraphTests.Family(f.GetString()!))] : null,
        Size = Float(s, "size"),
        Language = String(s, "language"),
        LetterSpacing = Float(s, "letterSpacing"),
        WordSpacing = Float(s, "wordSpacing"),
        LineHeight = s.TryGetProperty("lineHeight", out JsonElement lh) ? LineHeight(lh) : null,
        BaselineShift = Float(s, "baselineShift"),
    };

    private static LineHeight LineHeight(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? Egami.Fonts.Layout.LineHeight.Normal
        : e.TryGetProperty("multiple", out JsonElement m) ? Egami.Fonts.Layout.LineHeight.Multiple(m.GetSingle())
        : Egami.Fonts.Layout.LineHeight.Absolute(e.GetProperty("absolute").GetSingle());

    private static float? Float(JsonElement s, string name) => s.TryGetProperty(name, out JsonElement v) ? v.GetSingle() : null;

    private static string? String(JsonElement s, string name) => s.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

    private static T EnumOf<T>(JsonElement s, string name, T fallback)
        where T : struct, System.Enum => s.TryGetProperty(name, out JsonElement v) ? System.Enum.Parse<T>(v.GetString()!) : fallback;
}
