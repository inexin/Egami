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
    private readonly Dictionary<string, List<(Typeface Face, FontStyle Style)>> _fontFaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly FontCollection _faceCollection = new();
    private FontFallback? _fallback;

    public SvgFonts(SvgFontOptions options, SvgReport report)
    {
        _options = options;
        _report = report;
    }

    /// <summary>Gets per-character fallback over the same fonts, in the same order.</summary>
    public FontFallback Fallback => _fallback ??= new FontFallback(
        [.. _options.Collection?.Families ?? [], .. _faceCollection.Families],
        _options.UseSystemFonts ? new SystemFontFallback(FontCollection.System) : null);

    /// <summary>
    /// Loads the document's @font-face fonts (question 13): each src in turn, a url() (a data URI, or a file the load options'
    /// resolver allows) or a local() face, under the rule's family name and with its weight, style and stretch.
    /// </summary>
    public void AddFontFaces(IEnumerable<(CssFontFaceRule Rule, SvgElement Element)> rules, Func<string, SvgElement, byte[]?> load)
    {
        foreach ((CssFontFaceRule rule, SvgElement element) in rules)
        {
            Dictionary<string, IReadOnlyList<CssComponentValue>> d = new(StringComparer.OrdinalIgnoreCase);
            foreach (CssDeclaration declaration in rule.Declarations)
            {
                d[declaration.Name] = declaration.Value;
            }

            if (!d.TryGetValue("font-family", out IReadOnlyList<CssComponentValue>? familyValue) || !CssFontFamilyList.TryParse(familyValue, out CssFontFamilyList? names) || names.Families.Count != 1)
            {
                _report.Warn("Svg.FontNotLoaded", "An @font-face rule has no single font-family; it is ignored.", element);
                continue;
            }

            int weight = d.TryGetValue("font-weight", out IReadOnlyList<CssComponentValue>? w) && CssFontWeight.TryParse(w, out CssFontWeight fw) && fw.Kind == CssFontWeightKind.Absolute ? (int)fw.Value : 400;
            string? styleWord = d.TryGetValue("font-style", out IReadOnlyList<CssComponentValue>? st) ? Styling.SvgStyleValues.Keyword(st)?.ToLowerInvariant() : null;
            FontSlant slant = styleWord switch { "italic" => FontSlant.Italic, "oblique" => FontSlant.Oblique, _ => FontSlant.Upright };
            Typeface? face = d.TryGetValue("src", out IReadOnlyList<CssComponentValue>? src) ? Source(src, element, load) : null;
            if (face is null)
            {
                _report.Warn("Svg.FontNotLoaded", $"No source of the @font-face rule for \"{names.Families[0].Name}\" could be loaded; the family falls back.", element);
                continue;
            }

            if (!_fontFaces.TryGetValue(names.Families[0].Name, out List<(Typeface, FontStyle)>? list))
            {
                _fontFaces[names.Families[0].Name] = list = [];
            }

            list.Add((face, new FontStyle(weight, 5, slant)));
            _faceCollection.Add(face);
        }

        _faces.Clear();
        _fallback = null;
    }

    private Typeface? Source(IReadOnlyList<CssComponentValue> src, SvgElement element, Func<string, SvgElement, byte[]?> load)
    {
        foreach (CssComponentValue value in src)
        {
            if (CssUrl.TryParse(value, out CssUrl url))
            {
                try
                {
                    if (load(url.Url, element) is byte[] data)
                    {
                        return Typeface.FromMemory(data);
                    }
                }
                catch (EgamiException)
                {
                }
            }
            else if (value is CssFunction { Name: var name } local && name.Equals("local", StringComparison.OrdinalIgnoreCase) &&
                local.Arguments.FirstOrDefault(a => a is CssTokenValue { Token.Kind: CssTokenKind.String or CssTokenKind.Ident }) is CssTokenValue { Token: var t } &&
                Named(t.Value, default) is Typeface found)
            {
                return found;
            }
        }

        return null;
    }

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

    /// <summary>The family a face belongs to, for paragraph layout (which takes families): the collection that holds it.</summary>
    public FontFamily? FamilyOf(Typeface face)
    {
        foreach (FontCollection? collection in (ReadOnlySpan<FontCollection?>)[_options.Collection, _faceCollection, _options.UseSystemFonts ? FontCollection.System : null])
        {
            if (collection is not null && collection.TryGetFamily(face.FamilyName, out FontFamily? family))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>A family by name: the caller's collection, then the @font-face fonts (closest style), then the installed fonts.</summary>
    private Typeface? Named(string name, FontStyle style)
    {
        if (_options.Collection?.Match(name, style)?.Typeface is Typeface own)
        {
            return own;
        }

        if (_fontFaces.TryGetValue(name, out List<(Typeface Face, FontStyle Style)>? faces))
        {
            return faces.OrderBy(f => f.Style.Slant == style.Slant ? 0 : 1).ThenBy(f => Math.Abs(f.Style.Weight - style.Weight)).First().Face;
        }

        return _options.UseSystemFonts ? FontCollection.System.Match(name, style)?.Typeface : null;
    }

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
