using System.Collections.Concurrent;

using Egami.Css;
using Egami.Svg.Animation;
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
    private readonly SvgStyleSheets _sheets;
    private readonly bool _shared;
    private readonly CssCascade _cascade;
    private readonly Dictionary<SvgElement, CssCascadedStyle> _cascaded = new(IdentityComparer.Instance);
    private readonly SvgReport _report;
    private readonly Dictionary<(string Name, string Value), CssDeclaration> _hints = [];
    private readonly Dictionary<string, IReadOnlyList<CssDeclaration>> _inline = new(StringComparer.Ordinal);
    private readonly SvgAnimationFrame? _frame;
    private readonly SvgCssAnimator? _animator;
    private Dictionary<SvgElement, Dictionary<string, string>>? _animated;

    /// <summary>
    /// Builds the cascade of a document. With a frame, SMIL's values at that moment take part; when <paramref name="cssAnimations"/>
    /// is set too, so do the document's CSS animations at the frame's time. <paramref name="sheets"/>, when given, are the
    /// document's parsed sheets that frames share (their errors are noted again here); else the sheets are parsed for this styler.
    /// </summary>
    public SvgStyler(SvgDocument document, SvgReport report, CssMediaContext media, SvgAnimationFrame? frame = null, bool cssAnimations = false, SvgStyleSheets? sheets = null)
    {
        _report = report;
        _frame = frame;
        if (sheets is null)
        {
            _sheets = new SvgStyleSheets(document, report, media);
        }
        else
        {
            _sheets = sheets;
            _shared = true;
            sheets.Report(report);
        }

        _cascade = _sheets.Cascade;
        if (cssAnimations && frame is not null && _sheets.Keyframes.Count > 0)
        {
            _animator = new SvgCssAnimator(_sheets, frame.Time, media.Width, media.Height);
        }
    }

    /// <summary>Gets the @keyframes rules of the document's style sheets by name: the last of each name, those in matching @media rules too.</summary>
    public IReadOnlyDictionary<string, CssKeyframesRule> Keyframes => _sheets.Keyframes;

    /// <summary>Gets a value indicating whether CSS animations are run.</summary>
    public bool Animates => _animator is not null;

    /// <summary>Gets the values CSS animations gave each element styled so far, by property; null when none did.</summary>
    public IReadOnlyDictionary<SvgElement, Dictionary<string, string>>? AnimatedValues => _animated;

    /// <summary>The value a CSS animation gives a property of an element at this moment, or null when none does.</summary>
    public string? AnimatedValue(SvgElement element, string name) =>
        _animated is not null && _animated.TryGetValue(element, out Dictionary<string, string>? values) && values.TryGetValue(name, out string? value) ? value : null;

    /// <summary>Gets the @font-face rules of the document's style sheets (those inside matching @media rules too), with their style element.</summary>
    public List<(CssFontFaceRule Rule, SvgElement Element)> FontFaces => _sheets.FontFaces;

    /// <summary>Gets the registry of SVG's properties.</summary>
    public static CssPropertyRegistry Registry => SvgStyleSheets.Registry;

    /// <summary>The element's cascaded style, computed once.</summary>
    public CssCascadedStyle Cascaded(SvgElement element)
    {
        if (!_cascaded.TryGetValue(element, out CssCascadedStyle? style))
        {
            // An element no animation touches at this moment cascades as in every other frame, so frames share its cascade.
            if (_shared && _frame is { AnimatesClass: false } && _frame.For(element) is null)
            {
                ConcurrentDictionary<SvgElement, CssCascadedStyle> shared = _sheets.Cascades;
                if (!shared.TryGetValue(element, out style))
                {
                    style = _cascade.Cascade(element, Hints(element), Inline(element));
                    shared.TryAdd(element, style);
                }
            }
            else
            {
                // When an animation sets a class, selectors match the classes of this moment.
                ICssElement matched = _frame?.AnimatesClass == true ? _frame.CssElement(element) : element;
                style = _cascade.Cascade(matched, Hints(element), Inline(element));
            }

            _cascaded[element] = style;
        }

        return style;
    }

    /// <summary>Drops an element's cached cascade, so it is cascaded again (after an animation frame changes its values).</summary>
    public void Forget(SvgElement element) => _cascaded.Remove(element);

    /// <summary>The element's computed CSS style where its parent's is <paramref name="parent"/>, with its CSS animations when they run.</summary>
    public CssComputedStyle Computed(SvgElement element, CssComputedStyle? parent)
    {
        CssCascadedStyle cascaded = Cascaded(element);
        CssComputedStyle computed = _cascade.Compute(cascaded, parent);
        return _animator is null ? computed : Animated(_animator, element, cascaded, computed, parent);
    }

    // The computed style with the element's running CSS animations applied. Kept out of Computed so its closure is only
    // allocated where animations run.
    private CssComputedStyle Animated(SvgCssAnimator animator, SvgElement element, CssCascadedStyle cascaded, CssComputedStyle computed, CssComputedStyle? parent)
    {
        if (animator.Animate(cascaded, computed, parent, name => AttributeValue(element, name)) is not Dictionary<string, string> values)
        {
            return computed;
        }

        List<CssDeclaration> declarations = new(values.Count);
        foreach ((string name, string value) in values)
        {
            declarations.Add(CssDeclaration.Create(name, value));
        }

        (_animated ??= new Dictionary<SvgElement, Dictionary<string, string>>(IdentityComparer.Instance))[element] = values;
        return _cascade.Compute(cascaded.WithAnimationValues(declarations), parent);
    }

    // What an attribute (animated by SMIL at this moment) gives a property CSS does not set, as CSS text: the transform as a
    // matrix, path data as path(), geometry as written, on the elements it belongs to.
    private string? AttributeValue(SvgElement element, string name)
    {
        string? text = _frame is not null && _frame.TryGet(element, name, out string? animated) ? animated : element.Attributes[name];
        if (text is null)
        {
            return null;
        }

        if (name == "transform")
        {
            return Values.SvgTransforms.TryParse(text, out System.Numerics.Matrix3x2 m) && !m.IsIdentity
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"matrix({m.M11}, {m.M12}, {m.M21}, {m.M22}, {m.M31}, {m.M32})")
                : null;
        }

        if (!SvgProperties.Geometry.TryGetValue(name, out string[]? elements) || !elements.Contains(element.Name))
        {
            return null;
        }

        return name == "d" ? "path(\"" + text.Replace("\"", "", StringComparison.Ordinal) + "\")" : text;
    }

    private List<CssDeclaration>? Hints(SvgElement element)
    {
        // SMIL animating a presentation attribute replaces its presentation hint, so CSS rules still win over it.
        IReadOnlyDictionary<string, string>? animated = _frame?.For(element);
        List<CssDeclaration>? hints = null;
        foreach (SvgAttribute attribute in element.Attributes)
        {
            if (attribute.Namespace is not null)
            {
                continue;
            }

            if (SvgProperties.Presentation.Contains(attribute.Name))
            {
                string value = animated is not null && animated.TryGetValue(attribute.Name, out string? now) ? now : attribute.Value;
                Hint(element, attribute.Name, value, ref hints);
            }
        }

        if (animated is not null)
        {
            foreach ((string name, string value) in animated)
            {
                if (SvgProperties.Presentation.Contains(name) && !element.Attributes.Contains(name))
                {
                    Hint(element, name, value, ref hints);
                }
            }
        }

        return hints;
    }

    private void Hint(SvgElement element, string name, string value, ref List<CssDeclaration>? hints)
    {
        // Presentation attributes take CSS values, but not !important or more than the one value.
        if (value.Contains('!', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal))
        {
            _report.Warn("Svg.BadAttribute", $"The {element.Name}'s {name} \"{Resolving.SvgGeometry.Clip(value)}\" is not a value; it is ignored.", element);
            return;
        }

        // Files repeat the same few values thousands of times; each is parsed once per document.
        if (!_hints.TryGetValue((name, value), out CssDeclaration? hint))
        {
            hint = CssDeclaration.Create(name, value);
            _hints[(name, value)] = hint;
        }

        (hints ??= []).Add(hint);
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
}
