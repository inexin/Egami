using Egami.Css;
using Egami.Svg.Reading;
using Egami.Svg.Styling;

namespace Egami.Svg.Animation;

/// <summary>
/// The base values animations build on: a property's computed value from the cascade (so a to animation starts where the
/// document shows it), with the animated values of the frame being computed, so an element inherits what its ancestors are
/// animated to; any other attribute's value as written. The cascade is run only when an additive or to animation asks for a
/// property, and only along the ancestors of the elements asked about. One instance serves one frame.
/// </summary>
internal sealed class SvgBaseValues(SvgDocument document)
{
    private readonly Dictionary<SvgElement, CssComputedStyle> _computed = new(IdentityComparer.Instance);
    private SvgStyler? _styler;

    /// <summary>The base value of an attribute or property, or null when it has none.</summary>
    public string? Get(SvgAnimationFrame frame, SvgElement element, string name)
    {
        if (SvgProperties.Presentation.Contains(name))
        {
            return Computed(frame, element).GetValueText(name);
        }

        return name == "href"
            ? element.Attributes["href"] ?? element.Attributes.Get("href", SvgNamespaces.XLink)
            : element.Attributes[name];
    }

    private CssComputedStyle Computed(SvgAnimationFrame frame, SvgElement element)
    {
        if (_computed.TryGetValue(element, out CssComputedStyle? style))
        {
            return style;
        }

        // Ancestors first, without recursion, so a deep tree cannot overflow the stack.
        List<SvgElement> chain = [];
        for (SvgElement? e = element; e is not null && !_computed.ContainsKey(e); e = e.Parent)
        {
            chain.Add(e);
        }

        _styler ??= new SvgStyler(document, new SvgReport(strict: false), new CssMediaContext { Width = document.Size.X, Height = document.Size.Y }, frame);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            SvgElement e = chain[i];
            CssComputedStyle? parent = e.Parent is SvgElement p ? _computed[p] : null;
            _computed[e] = _styler.Computed(e, parent);
        }

        return _computed[element];
    }
}
