using Egami.Css;
using Egami.Svg.Styling;

namespace Egami.Svg.Animation;

/// <summary>How a CSS animation plays its iterations (<c>animation-direction</c>).</summary>
internal enum SvgCssDirection : byte
{
    Normal,
    Reverse,
    Alternate,
    AlternateReverse,
}

/// <summary>How a keyframe's value combines with the value below it (<c>animation-composition</c>, Web Animations 1).</summary>
internal enum SvgCssComposite : byte
{
    Replace,
    Add,
    Accumulate,
}

/// <summary>
/// One CSS animation of an element, as its computed <c>animation-*</c> properties describe it (CSS Animations 1 and 2), with the
/// <c>@keyframes</c> rule its name finds. Every animation starts at time 0, when the document loads, and its timing follows
/// Web Animations 1 with a playback rate of 1, no end delay and no iteration start.
/// </summary>
internal sealed record class SvgCssAnimation(
    CssKeyframesRule Keyframes,
    double Duration,
    double Delay,
    double Iterations,
    SvgCssDirection Direction,
    bool FillBackwards,
    bool FillForwards,
    bool Paused,
    CssEasingFunction Easing,
    SvgCssComposite Composite)
{
    /// <summary>Gets the length of the active interval: the duration times the iteration count.</summary>
    public double ActiveDuration => Duration == 0 || Iterations == 0 ? 0 : Duration * Iterations;

    /// <summary>Gets when the animation's active interval ends, in document seconds (infinity when it repeats forever).</summary>
    public double End => Delay + ActiveDuration;

    /// <summary>
    /// Reads the animations an element's computed style sets: one per name in <c>animation-name</c> (the other lists repeat to
    /// match it), in order, leaving out <c>none</c> and names no <c>@keyframes</c> rule has. Null when there are none.
    /// </summary>
    public static List<SvgCssAnimation>? Read(CssComputedStyle style, IReadOnlyDictionary<string, CssKeyframesRule> keyframes)
    {
        if (keyframes.Count == 0 || !style.TryGetValue("animation-name", out IReadOnlyList<CssComponentValue>? nameValue) || SvgStyleValues.IsKeyword(nameValue, "none"))
        {
            return null;
        }

        List<List<CssComponentValue>>? names = SvgAnimationProperties.Split(nameValue);
        if (names is null)
        {
            return null;
        }

        List<List<CssComponentValue>> durations = List(style, "animation-duration", "0s");
        List<List<CssComponentValue>> easings = List(style, "animation-timing-function", "ease");
        List<List<CssComponentValue>> delays = List(style, "animation-delay", "0s");
        List<List<CssComponentValue>> counts = List(style, "animation-iteration-count", "1");
        List<List<CssComponentValue>> directions = List(style, "animation-direction", "normal");
        List<List<CssComponentValue>> fills = List(style, "animation-fill-mode", "none");
        List<List<CssComponentValue>> states = List(style, "animation-play-state", "running");
        List<List<CssComponentValue>> composites = List(style, "animation-composition", "replace");

        List<SvgCssAnimation>? animations = null;
        for (int i = 0; i < names.Count; i++)
        {
            if (SvgStyleValues.Single(names[i]) is not CssTokenValue { Token: { Kind: CssTokenKind.Ident or CssTokenKind.String } name }
                || (name.Kind == CssTokenKind.Ident && name.Value.Equals("none", StringComparison.OrdinalIgnoreCase))
                || !keyframes.TryGetValue(name.Value, out CssKeyframesRule? rule))
            {
                continue;
            }

            SvgAnimationProperties.TryTime(Item(durations, i), out double duration);
            SvgAnimationProperties.TryTime(Item(delays, i), out double delay);
            if (!SvgAnimationProperties.TryIterations(Item(counts, i), out double iterations))
            {
                iterations = 1;
            }

            if (!CssEasingFunction.TryParse(Item(easings, i), out CssEasingFunction? easing))
            {
                easing = CssEasingFunction.Ease;
            }

            string direction = SvgStyleValues.Keyword(Item(directions, i)) ?? "normal";
            string fill = SvgStyleValues.Keyword(Item(fills, i)) ?? "none";
            animations ??= [];
            animations.Add(new SvgCssAnimation(
                rule,
                Math.Max(0, duration),
                delay,
                iterations,
                direction switch
                {
                    "reverse" => SvgCssDirection.Reverse,
                    "alternate" => SvgCssDirection.Alternate,
                    "alternate-reverse" => SvgCssDirection.AlternateReverse,
                    _ => SvgCssDirection.Normal,
                },
                fill is "backwards" or "both",
                fill is "forwards" or "both",
                SvgStyleValues.IsKeyword(Item(states, i), "paused"),
                easing,
                ParseComposite(SvgStyleValues.Keyword(Item(composites, i)))));
        }

        return animations;
    }

    /// <summary>Reads an <c>animation-composition</c> keyword; anything else is replace.</summary>
    public static SvgCssComposite ParseComposite(string? keyword) => keyword switch
    {
        "add" => SvgCssComposite.Add,
        "accumulate" => SvgCssComposite.Accumulate,
        _ => SvgCssComposite.Replace,
    };

    /// <summary>
    /// The iteration progress at a document time (Web Animations 1, "Core animation model"): where the animation is within its
    /// current iteration, after its direction; false when it has no effect then (before or after its active interval without a
    /// fill). A paused animation stays at its start, as it never began to play.
    /// </summary>
    public bool TryProgress(double time, out double progress, out double iteration)
    {
        progress = 0;
        iteration = 0;
        double local = Paused ? 0 : time;
        double active = ActiveDuration;
        double end = Delay + active;
        double activeTime;
        bool after = false;
        if (local < Math.Max(Math.Min(Delay, end), 0))
        {
            if (!FillBackwards)
            {
                return false;
            }

            activeTime = 0;
        }
        else if (local < Math.Max(Math.Min(Delay + active, end), 0))
        {
            activeTime = local - Delay;
        }
        else
        {
            if (!FillForwards)
            {
                return false;
            }

            after = true;
            activeTime = Math.Max(Math.Min(local - Delay, active), 0);
        }

        double overall = Duration == 0 ? (after ? Iterations : 0) : activeTime / Duration;
        double simple = double.IsInfinity(overall) ? 0 : overall % 1;
        if (simple == 0 && after && activeTime == active && Iterations != 0)
        {
            simple = 1;
        }

        double current = after && double.IsPositiveInfinity(Iterations)
            ? double.PositiveInfinity
            : simple == 1 ? Math.Floor(overall) - 1 : Math.Floor(overall);
        iteration = current;
        bool forwards = Direction switch
        {
            SvgCssDirection.Normal => true,
            SvgCssDirection.Reverse => false,
            _ => (double.IsInfinity(current) || current % 2 == 0) == (Direction == SvgCssDirection.Alternate),
        };
        progress = forwards ? simple : 1 - simple;
        return true;
    }

    private static List<List<CssComponentValue>> List(CssComputedStyle style, string name, string initial)
    {
        if (style.TryGetValue(name, out IReadOnlyList<CssComponentValue>? value) && SvgAnimationProperties.Split(value) is { } items)
        {
            return items;
        }

        return [[.. CssComponentValue.ParseList(initial)]];
    }

    private static List<CssComponentValue> Item(List<List<CssComponentValue>> list, int index) => list[index % list.Count];
}
