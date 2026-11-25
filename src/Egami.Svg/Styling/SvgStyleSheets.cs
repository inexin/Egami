using System.Collections.Concurrent;

using Egami.Css;
using Egami.Svg.Animation;
using Egami.Svg.Reading;

namespace Egami.Svg.Styling;

/// <summary>
/// A document's style sheets, ready to cascade (question 5): SVG's user agent sheet, then every <c>style</c> element of type
/// text/css in document order (with its media query) as author sheets, with their @keyframes and @font-face rules. Animation
/// frames share one per document version (<see cref="SvgDocument"/> keeps it), so each frame does not parse the sheets again,
/// together with what does not change from frame to frame: the cascade of each element no animation touches, and each CSS
/// animation's keyframes. Everything here is read-only once built, or a cache that any thread may fill.
/// </summary>
internal sealed class SvgStyleSheets
{
    private const string UserAgentSheet = """
        @namespace url(http://www.w3.org/2000/svg);
        svg:not(:root), image, marker, pattern, symbol { overflow: hidden; }
        """;

    // A bound on the caches filled from computed values, which a value made per frame (through var()) would grow forever.
    private const int MaxCached = 4096;

    private static readonly CssPropertyRegistry s_registry = SvgProperties.CreateRegistry();
    private static readonly CssStyleSheet s_userAgent = CssStyleSheet.Parse(UserAgentSheet);
    private static readonly Dictionary<string, CssKeyframesRule> s_noKeyframes = [];

    private readonly List<(string Message, SvgElement Element)> _notes = [];
    private Dictionary<string, CssKeyframesRule>? _keyframes;
    private ConcurrentDictionary<SvgElement, CssCascadedStyle>? _cascades;
    private ConcurrentDictionary<SvgCssAnimationKey, SvgCssAnimationSet>? _animations;

    /// <summary>Parses a document's style sheets for a media context, noting their errors in <paramref name="report"/>.</summary>
    public SvgStyleSheets(SvgDocument document, SvgReport report, CssMediaContext media)
    {
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
                Errors = e =>
                {
                    string message = $"A style sheet has an error at its line {e.Line}, column {e.Column}: {e.Message}";
                    _notes.Add((message, element));
                    report.Note("Svg.BadStyleSheet", message, element);
                },
            });
            sheets.Add((sheet, CssOrigin.Author));
            CollectFontFaces(sheet.Rules, media, element);
            CollectKeyframes(sheet.Rules, media);
        }

        Cascade = new CssCascade(sheets, new CssCascadeOptions { Media = media, Registry = s_registry });
    }

    /// <summary>Gets the registry of SVG's properties.</summary>
    public static CssPropertyRegistry Registry => s_registry;

    /// <summary>Gets the cascade of the sheets.</summary>
    public CssCascade Cascade { get; }

    /// <summary>Gets the @keyframes rules of the sheets by name: the last of each name, those in matching @media rules too.</summary>
    public IReadOnlyDictionary<string, CssKeyframesRule> Keyframes => _keyframes ?? s_noKeyframes;

    /// <summary>Gets the @font-face rules of the sheets (those inside matching @media rules too), with their style element.</summary>
    public List<(CssFontFaceRule Rule, SvgElement Element)> FontFaces { get; } = [];

    /// <summary>Notes the sheets' errors in another report, as parsing them did in the first.</summary>
    public void Report(SvgReport report)
    {
        foreach ((string message, SvgElement element) in _notes)
        {
            report.Note("Svg.BadStyleSheet", message, element);
        }
    }

    /// <summary>
    /// Gets the cascades of the elements no animation touches at the frame being styled (no SMIL value of theirs and no
    /// animated class anywhere), which are the same in every frame: those of the static document.
    /// </summary>
    public ConcurrentDictionary<SvgElement, CssCascadedStyle> Cascades =>
        _cascades ?? Interlocked.CompareExchange(ref _cascades, new(IdentityComparer.Instance), null) ?? _cascades;

    /// <summary>
    /// The CSS animations a computed style sets, read once for each set of <c>animation-*</c> values (which the cascade
    /// shares between frames), with room for each animation's keyframes as the animator prepares them; null when there are none.
    /// </summary>
    public SvgCssAnimationSet? Animations(CssComputedStyle style)
    {
        if (Keyframes.Count == 0)
        {
            return null;
        }

        SvgCssAnimationKey key = SvgCssAnimationKey.Of(style);
        ConcurrentDictionary<SvgCssAnimationKey, SvgCssAnimationSet> animations =
            _animations ?? Interlocked.CompareExchange(ref _animations, new(), null) ?? _animations;
        if (animations.TryGetValue(key, out SvgCssAnimationSet? set))
        {
            return set.Animations is null ? null : set;
        }

        set = new SvgCssAnimationSet(SvgCssAnimation.Read(style, Keyframes));
        if (animations.Count < MaxCached)
        {
            animations.TryAdd(key, set);
        }

        return set.Animations is null ? null : set;
    }

    private void CollectKeyframes(IReadOnlyList<CssRule> rules, CssMediaContext media)
    {
        foreach (CssRule rule in rules)
        {
            switch (rule)
            {
                case CssKeyframesRule keyframes:
                    (_keyframes ??= new(StringComparer.Ordinal))[keyframes.Name] = keyframes;
                    break;
                case CssMediaRule m when m.Media.Matches(media):
                    CollectKeyframes(m.Rules, media);
                    break;
                case CssLayerRule { Rules: { } layerRules }:
                    CollectKeyframes(layerRules, media);
                    break;
            }
        }
    }

    private void CollectFontFaces(IReadOnlyList<CssRule> rules, CssMediaContext media, SvgElement element)
    {
        foreach (CssRule rule in rules)
        {
            if (rule is CssFontFaceRule face)
            {
                FontFaces.Add((face, element));
            }
            else if (rule is CssMediaRule m && m.Media.Matches(media))
            {
                CollectFontFaces(m.Rules, media, element);
            }
        }
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

/// <summary>
/// The <c>animation-*</c> values of a computed style, compared by reference: the cascade hands the same value lists to every
/// frame that cascades the same declarations, so equal keys read the same animations.
/// </summary>
internal readonly struct SvgCssAnimationKey : IEquatable<SvgCssAnimationKey>
{
    private static readonly string[] s_names =
    [
        "animation-name", "animation-duration", "animation-timing-function", "animation-delay", "animation-iteration-count",
        "animation-direction", "animation-fill-mode", "animation-play-state", "animation-composition",
    ];

    private readonly IReadOnlyList<CssComponentValue>?[] _values;

    private SvgCssAnimationKey(IReadOnlyList<CssComponentValue>?[] values) => _values = values;

    public static SvgCssAnimationKey Of(CssComputedStyle style)
    {
        IReadOnlyList<CssComponentValue>?[] values = new IReadOnlyList<CssComponentValue>?[s_names.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = style.TryGetValue(s_names[i], out IReadOnlyList<CssComponentValue>? value) ? value : null;
        }

        return new SvgCssAnimationKey(values);
    }

    public bool Equals(SvgCssAnimationKey other)
    {
        for (int i = 0; i < _values.Length; i++)
        {
            if (!ReferenceEquals(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is SvgCssAnimationKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (IReadOnlyList<CssComponentValue>? value in _values)
        {
            hash.Add(value is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value));
        }

        return hash.ToHashCode();
    }
}

/// <summary>The CSS animations one set of <c>animation-*</c> values reads, each with its keyframes once the animator has prepared them.</summary>
internal sealed class SvgCssAnimationSet(List<SvgCssAnimation>? animations)
{
    /// <summary>Gets the animations, in <c>animation-name</c> order; null when there are none.</summary>
    public List<SvgCssAnimation>? Animations { get; } = animations;

    /// <summary>Gets each animation's prepared keyframes, filled in as the animator prepares them (by any thread; each is the same).</summary>
    public object?[] Prepared { get; } = new object?[animations?.Count ?? 0];
}
