using Egami.Css;
using Egami.Svg.Styling;

namespace Egami.Svg.Animation;

/// <summary>
/// Runs an element's CSS animations at one moment (CSS Animations 1 and 2, Web Animations 1): reads its <c>animation-*</c>
/// properties, finds where each animation is, computes the keyframe values in the element's context (so <c>inherit</c>,
/// <c>var()</c> and <c>currentcolor</c> resolve as in its style), adds the implicit <c>from</c> and <c>to</c> keyframes from
/// the value below, and eases, composites and interpolates per property. The animations stack in <c>animation-name</c> order,
/// each on the result of those before it, the first on the element's computed value (which holds what SMIL animates, as a
/// presentation hint). The results go to the cascade's animation origin.
/// </summary>
internal sealed class SvgCssAnimator(CssCascade cascade, IReadOnlyDictionary<string, CssKeyframesRule> keyframes, double time, double viewportWidth, double viewportHeight)
{
    /// <summary>Gets the time, in seconds since the document loaded, the animations are run at.</summary>
    public double Time => time;

    /// <summary>
    /// The animated values of an element whose cascaded style is <paramref name="cascaded"/> and computed style, without
    /// animations, <paramref name="computed"/>; null when no animation affects it now. <paramref name="attributeValue"/> gives,
    /// as CSS text, the value an attribute gives a property CSS does not set: transform and the geometry properties (x, r, d...).
    /// </summary>
    public Dictionary<string, string>? Animate(CssCascadedStyle cascaded, CssComputedStyle computed, CssComputedStyle? parent, Func<string, string?> attributeValue)
    {
        if (SvgCssAnimation.Read(computed, keyframes) is not List<SvgCssAnimation> animations)
        {
            return null;
        }

        SvgCssValueContext context = new(FontSize(computed), 16, viewportWidth, viewportHeight, CurrentColor(computed));
        Dictionary<string, string>? values = null;
        foreach (SvgCssAnimation animation in animations)
        {
            if (!animation.TryProgress(time, out double progress, out _))
            {
                continue;
            }

            foreach ((string name, List<Frame> frames) in PropertyKeyframes(animation, cascaded, parent))
            {
                values ??= new Dictionary<string, string>(StringComparer.Ordinal);
                string underlying = values.TryGetValue(name, out string? below) ? below : Base(computed, name, attributeValue);
                values[name] = Evaluate(name, frames, progress, underlying, animation, context);
            }
        }

        return values;
    }

    /// <summary>One keyframe of one property: its offset, computed value, easing and composite operation.</summary>
    private readonly record struct Frame(double Offset, string? Value, CssEasingFunction Easing, SvgCssComposite Composite);

    /// <summary>
    /// The keyframes of each property an animation names (Web Animations 1, "property-specific keyframes"), sorted by offset,
    /// keyframes with the same offset, easing and composite merged (the later wins), and the implicit 0 and 1 keyframes added,
    /// whose value (null here) is the value below.
    /// </summary>
    private Dictionary<string, List<Frame>> PropertyKeyframes(SvgCssAnimation animation, CssCascadedStyle cascaded, CssComputedStyle? parent)
    {
        Dictionary<string, List<Frame>> byProperty = new(StringComparer.Ordinal);
        foreach (CssKeyframe keyframe in animation.Keyframes.Keyframes)
        {
            CssEasingFunction easing = animation.Easing;
            SvgCssComposite composite = animation.Composite;
            List<CssDeclaration> declarations = [];
            foreach (CssDeclaration declaration in keyframe.Declarations)
            {
                if (declaration.Name == "animation-timing-function")
                {
                    if (CssEasingFunction.TryParse(declaration.Value, out CssEasingFunction? own))
                    {
                        easing = own;
                    }

                    continue;
                }

                if (declaration.Name == "animation-composition")
                {
                    composite = SvgCssAnimation.ParseComposite(SvgStyleValues.Keyword(declaration.Value));
                    continue;
                }

                Expand(declaration, declarations);
            }

            if (declarations.Count == 0)
            {
                continue;
            }

            // The keyframe's values computed as the element would compute them.
            CssComputedStyle style = cascade.Compute(cascaded.WithAnimationValues(declarations), parent);
            foreach (CssDeclaration declaration in declarations)
            {
                string? value = style.GetValueText(declaration.Name);
                if (value is null)
                {
                    continue;
                }

                if (!byProperty.TryGetValue(declaration.Name, out List<Frame>? frames))
                {
                    frames = [];
                    byProperty[declaration.Name] = frames;
                }

                foreach (double offset in keyframe.Offsets)
                {
                    int same = frames.FindIndex(f => f.Offset == offset && f.Easing.ToString() == easing.ToString() && f.Composite == composite);
                    if (same >= 0)
                    {
                        frames[same] = frames[same] with { Value = value };
                    }
                    else
                    {
                        frames.Add(new Frame(offset, value, easing, composite));
                    }
                }
            }
        }

        foreach (List<Frame> frames in byProperty.Values)
        {
            // A stable sort: keyframes at one offset keep their order.
            List<Frame> sorted = [.. frames.OrderBy(f => f.Offset)];
            frames.Clear();
            frames.AddRange(sorted);
            if (frames[0].Offset != 0)
            {
                frames.Insert(0, new Frame(0, null, animation.Easing, SvgCssComposite.Replace));
            }

            if (frames[^1].Offset != 1)
            {
                frames.Add(new Frame(1, null, animation.Easing, SvgCssComposite.Replace));
            }
        }

        return byProperty;
    }

    // A declaration as the longhands it sets; animation properties, and properties SVG does not know, are not animated.
    private void Expand(CssDeclaration declaration, List<CssDeclaration> into)
    {
        if (declaration.IsCustomProperty)
        {
            into.Add(declaration);
            return;
        }

        if (declaration.Name.StartsWith("animation", StringComparison.Ordinal))
        {
            return;
        }

        if (cascade.Registry.TryGetShorthand(declaration.Name, out CssShorthandDefinition? shorthand))
        {
            if (shorthand.Expand(declaration.Value) is IReadOnlyList<CssDeclaration> longhands)
            {
                foreach (CssDeclaration longhand in longhands)
                {
                    Expand(longhand, into);
                }
            }

            return;
        }

        if (cascade.Registry.TryGetProperty(declaration.Name, out CssPropertyDefinition? definition) && definition.Validate?.Invoke(declaration.Value) != false)
        {
            into.Add(declaration);
        }
    }

    /// <summary>The value of a property at the animation's iteration progress (Web Animations 1, "The effect value of a keyframe effect").</summary>
    private static string Evaluate(string name, List<Frame> frames, double progress, string underlying, SvgCssAnimation animation, in SvgCssValueContext context)
    {
        Frame start;
        Frame end;
        if (progress < 0 && frames.Count(f => f.Offset == 0) > 1)
        {
            return Value(name, frames[0], underlying, context);
        }

        if (progress >= 1 && frames.Count(f => f.Offset == 1) > 1)
        {
            return Value(name, frames[^1], underlying, context);
        }

        int index = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Offset <= progress && frames[i].Offset < 1)
            {
                index = i;
            }
        }

        start = frames[index];
        end = frames[Math.Min(index + 1, frames.Count - 1)];
        string from = Value(name, start, underlying, context);
        string to = Value(name, end, underlying, context);
        double span = end.Offset - start.Offset;
        double distance = span == 0 ? 0 : (progress - start.Offset) / span;
        return SvgCssValues.Interpolate(name, from, to, start.Easing.Evaluate(distance), context);
    }

    private static string Value(string name, Frame frame, string underlying, in SvgCssValueContext context) =>
        frame.Value is null ? underlying : SvgCssValues.Composite(name, underlying, frame.Value, frame.Composite, context);

    /// <summary>The value below every animation: the computed value, or for transform and geometry CSS does not set, the attribute's.</summary>
    private string Base(CssComputedStyle computed, string name, Func<string, string?> attributeValue)
    {
        if ((name == "transform" || SvgProperties.Geometry.ContainsKey(name)) && !computed.IsSetOnElement(name) && attributeValue(name) is string attribute)
        {
            return attribute;
        }

        if (computed.GetValueText(name) is string value)
        {
            return value;
        }

        return cascade.Registry.TryGetProperty(name, out CssPropertyDefinition? definition) && definition.InitialValue is string initial ? initial : "";
    }

    /// <summary>The font size in px a style resolves to, for em lengths; 16 at the root.</summary>
    private static double FontSize(CssComputedStyle style)
    {
        List<CssComputedStyle> chain = [];
        for (CssComputedStyle? s = style; s is not null; s = s.Parent)
        {
            chain.Add(s);
        }

        float size = 16;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].IsSetOnElement("font-size") && chain[i].TryGetValue("font-size", out IReadOnlyList<CssComponentValue>? value)
                && SvgStyleParsing.TryFontSize(value, size, 16, 96, out float own))
            {
                size = own;
            }
        }

        return size;
    }

    private static CssColor CurrentColor(CssComputedStyle style)
    {
        for (CssComputedStyle? s = style; s is not null; s = s.Parent)
        {
            if (s.TryGetValue("color", out IReadOnlyList<CssComponentValue>? value) && CssColor.TryParse(value, out CssColor color) && !color.IsCurrentColor)
            {
                return color;
            }
        }

        return new CssColor(0, 0, 0);
    }
}
