using Egami.Svg.Reading;

namespace Egami.Svg.Animation;

/// <summary>
/// The animated values at one moment: for each animated element and attribute, the result of its animation sandwich (SMIL
/// Animation, "The animation sandwich model"). The styler and resolver read these in place of the attribute values, so the
/// element tree is never edited.
/// </summary>
internal sealed class SvgAnimationFrame
{
    private readonly Dictionary<SvgElement, Dictionary<string, string>> _values = new(IdentityComparer.Instance);

    private SvgAnimationFrame()
    {
    }

    /// <summary>Gets a value indicating whether nothing is animated at this moment.</summary>
    public bool IsEmpty => _values.Count == 0;

    /// <summary>Gets the animated value of an attribute, if it has one at this moment.</summary>
    public bool TryGet(SvgElement element, string name, out string? value)
    {
        if (_values.TryGetValue(element, out Dictionary<string, string>? values) && values.TryGetValue(name, out string? v))
        {
            value = v;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Gets an element's animated attributes, or null when it has none.</summary>
    public IReadOnlyDictionary<string, string>? For(SvgElement element) => _values.GetValueOrDefault(element);

    /// <summary>
    /// Computes the values at <paramref name="time"/> seconds. Each attribute's active and frozen animations are applied in
    /// priority order, the one that began first at the bottom (document order breaks ties); each sees the result below it, or
    /// the base value <paramref name="baseValue"/> gives, as its underlying value.
    /// </summary>
    public static SvgAnimationFrame Compute(SvgTimeline timeline, double time, Func<SvgElement, string, string?> baseValue)
    {
        SvgAnimationFrame frame = new();
        Dictionary<SvgElement, Dictionary<string, List<Layer>>> sandwiches = new(IdentityComparer.Instance);
        foreach (SvgAnimation a in timeline.Animations)
        {
            if (a.Function is null || a.AttributeName is null || !TryState(a, time, out Layer layer))
            {
                continue;
            }

            if (!sandwiches.TryGetValue(a.Target, out Dictionary<string, List<Layer>>? byName))
            {
                byName = new Dictionary<string, List<Layer>>(StringComparer.Ordinal);
                sandwiches[a.Target] = byName;
            }

            if (!byName.TryGetValue(a.AttributeName, out List<Layer>? layers))
            {
                layers = [];
                byName[a.AttributeName] = layers;
            }

            layers.Add(layer);
        }

        foreach ((SvgElement element, Dictionary<string, List<Layer>> byName) in sandwiches)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach ((string name, List<Layer> layers) in byName)
            {
                layers.Sort(static (x, y) => x.Begin != y.Begin ? x.Begin.CompareTo(y.Begin) : x.Animation.Order.CompareTo(y.Animation.Order));
                string? value = null;
                bool haveBase = false;
                string? baseText = null;
                foreach (Layer layer in layers)
                {
                    SvgAnimationFunction f = layer.Animation.Function!;
                    string? underlying = value;
                    if (underlying is null && f.NeedsUnderlying)
                    {
                        if (!haveBase)
                        {
                            baseText = baseValue(element, name);
                            haveBase = true;
                        }

                        underlying = baseText;
                    }

                    value = f.Evaluate(layer.Progress, layer.Iteration, underlying);
                }

                if (value is not null)
                {
                    values[name] = value;
                }
            }

            frame._values[element] = values;
        }

        return frame;
    }

    /// <summary>
    /// Where an animation is at a time: in an interval (its simple time as a fraction and its repetition), frozen after one,
    /// or not in effect.
    /// </summary>
    internal static bool TryState(SvgAnimation a, double time, out Layer layer)
    {
        layer = default;
        List<SvgInterval> intervals = a.Intervals;

        // The last interval that has begun by now.
        int lo = 0, hi = intervals.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (intervals[mid].Begin <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return false;
        }

        SvgInterval interval = intervals[found];
        double d = a.SimpleDuration;
        if (time < interval.PlayEnd)
        {
            double active = time - interval.Begin;
            if (!double.IsFinite(d))
            {
                layer = new Layer(a, interval.Begin, 0, 0);
                return true;
            }

            double iteration = Math.Floor(active / d);
            layer = new Layer(a, interval.Begin, (active - (iteration * d)) / d, (long)Math.Min(iteration, long.MaxValue));
            return true;
        }

        // After it plays (and through a min extension): fill="freeze" holds the value it ended on.
        if (!a.Freeze)
        {
            return false;
        }

        double played = interval.PlayEnd - interval.Begin;
        if (!double.IsFinite(d) || played <= 0)
        {
            layer = new Layer(a, interval.Begin, 0, 0);
            return true;
        }

        double repeats = played / d;
        double whole = Math.Round(repeats);
        if (whole >= 1 && Math.Abs(repeats - whole) < 1e-9)
        {
            // Ended on a repeat boundary: the frozen value is the end of the simple duration, in the last repetition.
            layer = new Layer(a, interval.Begin, 1, (long)Math.Min(whole - 1, long.MaxValue));
            return true;
        }

        double floor = Math.Floor(repeats);
        layer = new Layer(a, interval.Begin, repeats - floor, (long)Math.Min(floor, long.MaxValue));
        return true;
    }

    /// <summary>One animation's part in a sandwich: its priority (the begin of its interval) and where it is.</summary>
    internal readonly record struct Layer(SvgAnimation Animation, double Begin, double Progress, long Iteration);
}
