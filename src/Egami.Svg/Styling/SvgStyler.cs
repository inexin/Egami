using Egami.Css;
using Egami.Svg.Reading;

namespace Egami.Svg.Styling;

/// <summary>
/// Runs Egami.Css's cascade over a document (question 5): SVG's user agent sheet, then every <c>style</c> element of type
/// text/css in document order (with its media query), as author sheets. Each element's presentation attributes are author
/// declarations of specificity zero below every sheet rule, and its <c>style</c> attribute is the inline style. Cascaded styles
/// are kept per element, so the copies <c>use</c> makes match selectors where the originals are and inherit where they are used,
/// as SVG 2 says.
/// </summary>
internal sealed class SvgStyler
{
    private const string UserAgentSheet = """
        @namespace url(http://www.w3.org/2000/svg);
        svg:not(:root), image, marker, pattern, symbol { overflow: hidden; }
        """;

    private static readonly CssPropertyRegistry s_registry = SvgProperties.CreateRegistry();
    private static readonly CssStyleSheet s_userAgent = CssStyleSheet.Parse(UserAgentSheet);

    private readonly CssCascade _cascade;
    private readonly Dictionary<SvgElement, CssCascadedStyle> _cascaded = new(IdentityComparer.Instance);
    private readonly SvgReport _report;
    private readonly Dictionary<(string Name, string Value), CssDeclaration> _hints = [];
    private readonly Dictionary<string, IReadOnlyList<CssDeclaration>> _inline = new(StringComparer.Ordinal);

    public SvgStyler(SvgDocument document, SvgReport report, CssMediaContext media)
    {
        _report = report;
        List<(CssStyleSheet, CssOrigin)> sheets = [(s_userAgent, CssOrigin.UserAgent)];
        foreach (SvgElement element in Descendants(document.Root))
        {
            if (!element.IsSvgOrBare(document) || element.Name != "style")
            {
                continue;
            }

            string type = element.Attributes["type"]?.Trim() ?? "";
            if (type.Length > 0 && !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (element.Attributes["media"] is string mediaText && !CssMediaQueryList.Parse(mediaText).Matches(media))
            {
                continue;
            }

            CssStyleSheet sheet = CssStyleSheet.Parse(element.Text, new CssParseOptions
            {
                Errors = e => report.Note("Svg.BadStyleSheet", $"A style sheet has an error at its line {e.Line}, column {e.Column}: {e.Message}", element),
            });
            sheets.Add((sheet, CssOrigin.Author));
        }

        _cascade = new CssCascade(sheets, new CssCascadeOptions { Media = media, Registry = s_registry });
    }

    /// <summary>Gets the registry of SVG's properties.</summary>
    public static CssPropertyRegistry Registry => s_registry;

    /// <summary>The element's cascaded style, computed once.</summary>
    public CssCascadedStyle Cascaded(SvgElement element)
    {
        if (!_cascaded.TryGetValue(element, out CssCascadedStyle? style))
        {
            style = _cascade.Cascade(element, Hints(element), Inline(element));
            _cascaded[element] = style;
        }

        return style;
    }

    /// <summary>The element's computed CSS style where its parent's is <paramref name="parent"/>.</summary>
    public CssComputedStyle Computed(SvgElement element, CssComputedStyle? parent) => _cascade.Compute(Cascaded(element), parent);

    private List<CssDeclaration>? Hints(SvgElement element)
    {
        List<CssDeclaration>? hints = null;
        foreach (SvgAttribute attribute in element.Attributes)
        {
            if (attribute.Namespace is not null)
            {
                continue;
            }

            if (SvgProperties.Presentation.Contains(attribute.Name))
            {
                // Presentation attributes take CSS values, but not !important or more than the one value.
                if (attribute.Value.Contains('!', StringComparison.Ordinal) || attribute.Value.Contains(';', StringComparison.Ordinal))
                {
                    _report.Warn("Svg.BadAttribute", $"The {element.Name}'s {attribute.Name} \"{Resolving.SvgGeometry.Clip(attribute.Value)}\" is not a value; it is ignored.", element);
                    continue;
                }

                // Files repeat the same few values thousands of times; each is parsed once per document.
                if (!_hints.TryGetValue((attribute.Name, attribute.Value), out CssDeclaration? hint))
                {
                    hint = CssDeclaration.Create(attribute.Name, attribute.Value);
                    _hints[(attribute.Name, attribute.Value)] = hint;
                }

                (hints ??= []).Add(hint);
            }
        }

        return hints;
    }

    private IReadOnlyList<CssDeclaration>? Inline(SvgElement element)
    {
        string? style = element.Attributes["style"];
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        if (_inline.TryGetValue(style, out IReadOnlyList<CssDeclaration>? cached))
        {
            return cached;
        }

        // A style with an error is parsed again for each element, so each gets its report; the others are shared.
        bool clean = true;
        IReadOnlyList<CssDeclaration> declarations = CssDeclaration.ParseList(style, new CssParseOptions
        {
            Errors = e =>
            {
                clean = false;
                _report.Note("Svg.BadStyleSheet", $"The style attribute has an error at column {e.Column}: {e.Message}", element);
            },
        });
        if (clean)
        {
            _inline[style] = declarations;
        }

        return declarations;
    }

    private static IEnumerable<SvgElement> Descendants(SvgElement root)
    {
        yield return root;
        foreach (SvgElement element in root.Descendants())
        {
            yield return element;
        }
    }
}
