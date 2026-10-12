using Egami.Css;
using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Svg.Reading;

namespace Egami.Svg.Text;

/// <summary>
/// Finds the faces a document's text asks for, in the order <see cref="SvgFontOptions"/> gives: the caller's collection, then
/// the installed fonts, each family of font-family in turn, then the serif family, as resvg's font selector does. Each lookup is
/// made once per document and a family nothing has is reported once.
/// </summary>
internal sealed class SvgFonts
{
    private readonly SvgFontOptions _options;
    private readonly SvgReport _report;
    private readonly Dictionary<(string Families, FontStyle Style), Typeface?> _faces = [];
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private FontFallback? _fallback;

    public SvgFonts(SvgFontOptions options, SvgReport report)
    {
        _options = options;
        _report = report;
    }

    /// <summary>Gets per-character fallback over the same fonts, in the same order.</summary>
    public FontFallback Fallback => _fallback ??= new FontFallback(
        _options.Collection?.Families ?? [],
        _options.UseSystemFonts ? new SystemFontFallback(FontCollection.System) : null);

    /// <summary>The face for a family list and style, or null when no font at all is available.</summary>
    public Typeface? Match(CssFontFamilyList? families, FontStyle style, SvgElement element)
    {
        string key = families?.ToString() ?? "";
        if (_faces.TryGetValue((key, style), out Typeface? cached))
        {
            return cached;
        }

        Typeface? face = null;
        IReadOnlyList<CssFontFamily> list = families?.Families ?? [];
        if (list.Count == 0)
        {
            list = [new CssFontFamily(_options.DefaultFamily, false)];
        }

        foreach (CssFontFamily family in list)
        {
            face = Find(family, style);
            if (face is not null)
            {
                break;
            }

            if (!family.IsGeneric && _missing.Add(family.Name))
            {
                _report.Warn("Svg.FontNotFound", $"No font has the family \"{family.Name}\"; the next family or the serif family is used.", element);
            }
        }

        face ??= Find(new CssFontFamily("serif", true), style);
        if (face is null && _missing.Add("\0none"))
        {
            _report.Warn("Svg.FontNotFound", "No font is available, so text is not drawn; give SvgFontOptions a collection or allow system fonts.", element);
        }

        _faces[(key, style)] = face;
        return face;
    }

    private Typeface? Find(CssFontFamily family, FontStyle style)
    {
        if (family.IsGeneric && Generic(family.Name) is GenericFamily generic)
        {
            if (_options.GenericFamilies is { } map && map.TryGetValue(generic, out string? name))
            {
                return Named(name, style);
            }

            return _options.Collection?.Match(generic, style)?.Typeface
                ?? (_options.UseSystemFonts ? FontCollection.System.Match(generic, style)?.Typeface : null);
        }

        return Named(family.Name, style);
    }

    private Typeface? Named(string name, FontStyle style) =>
        _options.Collection?.Match(name, style)?.Typeface
        ?? (_options.UseSystemFonts ? FontCollection.System.Match(name, style)?.Typeface : null);

    private static GenericFamily? Generic(string name) => name.ToLowerInvariant() switch
    {
        "serif" => GenericFamily.Serif,
        "sans-serif" => GenericFamily.SansSerif,
        "monospace" => GenericFamily.Monospace,
        "cursive" => GenericFamily.Cursive,
        "fantasy" => GenericFamily.Fantasy,
        _ => null,
    };
}
