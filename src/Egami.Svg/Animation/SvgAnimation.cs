using Egami.Svg.Reading;

namespace Egami.Svg.Animation;

/// <summary>The SMIL animation elements SVG 2 has.</summary>
internal enum SvgAnimationKind : byte
{
    Animate,
    Set,
    AnimateTransform,
    AnimateMotion,
    Discard,
}

/// <summary>What a SMIL element does when it may begin again (SMIL 3.0 Timing, the restart attribute).</summary>
internal enum SvgRestart : byte
{
    Always,
    WhenNotActive,
    Never,
}

/// <summary>One active interval of an animation, in seconds of document time.</summary>
/// <param name="Begin">When it begins.</param>
/// <param name="End">When it ends: begin plus the active duration, which <c>min</c> may extend; infinite when it never ends.</param>
/// <param name="PlayEnd">When the animation function stops: the end, or within a <c>min</c> extension when its repetitions end; the fill applies after.</param>
internal readonly record struct SvgInterval(double Begin, double End, double PlayEnd);

/// <summary>
/// A SMIL animation element as the timeline sees it: its target, its timing attributes read once, the intervals the timeline
/// gives it and, for animate, set and animateTransform, the function that gives its value at a simple time (for animateMotion,
/// its motion).
/// </summary>
internal sealed class SvgAnimation
{
    private SvgAnimation(SvgElement element, SvgAnimationKind kind, int order, SvgElement target)
    {
        Element = element;
        Kind = kind;
        Order = order;
        Target = target;
    }

    /// <summary>Gets the animation element.</summary>
    public SvgElement Element { get; }

    /// <summary>Gets which element it is.</summary>
    public SvgAnimationKind Kind { get; }

    /// <summary>Gets its place in document order, which breaks ties between animations that begin at the same time.</summary>
    public int Order { get; }

    /// <summary>Gets the element it animates: the one href names, else its parent.</summary>
    public SvgElement Target { get; }

    /// <summary>Gets the attribute it animates (xlink:href as href), or <see langword="null"/> for animateMotion and discard.</summary>
    public string? AttributeName { get; private set; }

    /// <summary>Gets the begin list; <c>0</c> when the attribute is missing.</summary>
    public List<SvgTimingValue> Begin { get; private set; } = [];

    /// <summary>Gets the end list, or <see langword="null"/> when the element has no end attribute.</summary>
    public List<SvgTimingValue>? End { get; private set; }

    /// <summary>Gets the simple duration in seconds: NaN when unspecified, infinite when indefinite.</summary>
    public double Dur { get; private set; } = double.NaN;

    /// <summary>Gets the repeat count: NaN when unspecified, infinite when indefinite.</summary>
    public double RepeatCount { get; private set; } = double.NaN;

    /// <summary>Gets the repeat duration in seconds: NaN when unspecified, infinite when indefinite.</summary>
    public double RepeatDur { get; private set; } = double.NaN;

    /// <summary>Gets the minimum active duration in seconds; 0 by default.</summary>
    public double Min { get; private set; }

    /// <summary>Gets the maximum active duration in seconds; infinite by default.</summary>
    public double Max { get; private set; } = double.PositiveInfinity;

    /// <summary>Gets when the element may begin again.</summary>
    public SvgRestart Restart { get; private set; }

    /// <summary>Gets a value indicating whether the last value stays after the active duration (<c>fill="freeze"</c>).</summary>
    public bool Freeze { get; private set; }

    /// <summary>Gets the value function of an animate or set, or <see langword="null"/> when it has none (or is in error).</summary>
    public SvgAnimationFunction? Function { get; internal set; }

    /// <summary>Gets the motion of an animateMotion, or <see langword="null"/> when it is something else (or in error).</summary>
    public SvgMotionFunction? Motion { get; internal set; }

    /// <summary>Gets or sets a value indicating whether the timeline's horizon (or interval limit) cut off further intervals.</summary>
    public bool ReachesHorizon { get; set; }

    /// <summary>Gets or sets the intervals the timeline gives it, in time order.</summary>
    public List<SvgInterval> Intervals { get; set; } = [];

    /// <summary>Gets the simple duration the value function runs over: indefinite when unspecified.</summary>
    public double SimpleDuration => double.IsNaN(Dur) ? double.PositiveInfinity : Dur;

    /// <summary>Gets a value indicating whether the element waits for a user event, an access key or a wall-clock time to begin.</summary>
    public bool WaitsForEvent => Begin.Exists(v => v.Kind == SvgTimingKind.Event);

    /// <summary>The element kinds SMIL animation elements have, by local name.</summary>
    public static bool TryGetKind(string name, out SvgAnimationKind kind)
    {
        (bool found, kind) = name switch
        {
            "animate" => (true, SvgAnimationKind.Animate),
            "set" => (true, SvgAnimationKind.Set),
            "animateTransform" => (true, SvgAnimationKind.AnimateTransform),
            "animateMotion" => (true, SvgAnimationKind.AnimateMotion),
            "discard" => (true, SvgAnimationKind.Discard),
            _ => (false, SvgAnimationKind.Animate),
        };
        return found;
    }

    /// <summary>Reads an animation element's target and timing; null (reported) when it has no target or attribute to animate.</summary>
    public static SvgAnimation? Read(SvgElement element, SvgAnimationKind kind, int order, SvgDocument document, SvgReport report)
    {
        SvgElement? target;
        string? href = element.Attributes["href"] ?? element.Attributes.Get("href", SvgNamespaces.XLink);
        if (href is not null)
        {
            string? id = Styling.SvgStyleValues.LocalId(href);
            target = id is null ? null : document.FindById(id);
            if (target is null)
            {
                report.Warn("Svg.BadAnimation", $"The {element.Name}'s href \"{Resolving.SvgGeometry.Clip(href)}\" names no element in this document; it animates nothing.", element);
                return null;
            }
        }
        else
        {
            target = element.Parent;
            if (target is null)
            {
                return null;
            }
        }

        SvgAnimation animation = new(element, kind, order, target);
        if (kind is SvgAnimationKind.Animate or SvgAnimationKind.Set or SvgAnimationKind.AnimateTransform)
        {
            string? name = element.Attributes["attributeName"]?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                report.Warn("Svg.BadAnimation", $"The {element.Name} has no attributeName; it animates nothing.", element);
                return null;
            }

            animation.AttributeName = name is "xlink:href" ? "href" : name;
        }

        animation.ReadTiming(report);
        return animation;
    }

    private void ReadTiming(SvgReport report)
    {
        SvgElement e = Element;
        if (e.Attributes["begin"] is string begin)
        {
            Begin = SvgTiming.ParseList(begin, out int errors);
            if (errors > 0)
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s begin \"{Resolving.SvgGeometry.Clip(begin)}\" has {errors} value(s) that cannot be read; they are ignored.", e);
            }
        }
        else
        {
            Begin = [new SvgTimingValue(SvgTimingKind.Offset, 0)];
        }

        if (Kind == SvgAnimationKind.Discard)
        {
            // discard has only begin: it removes its target at that time.
            return;
        }

        if (e.Attributes["end"] is string end)
        {
            End = SvgTiming.ParseList(end, out int errors);
            if (errors > 0)
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s end \"{Resolving.SvgGeometry.Clip(end)}\" has {errors} value(s) that cannot be read; they are ignored.", e);
            }
        }

        if (e.Attributes["dur"] is string dur)
        {
            string d = dur.Trim();
            if (d is "indefinite" or "media")
            {
                Dur = double.PositiveInfinity;
            }
            else if (SvgTiming.TryParseClock(d, out double seconds) && seconds > 0)
            {
                Dur = seconds;
            }
            else
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s dur \"{Resolving.SvgGeometry.Clip(dur)}\" is not a positive clock value; it is ignored.", e);
            }
        }

        if (e.Attributes["repeatCount"] is string count)
        {
            string c = count.Trim();
            if (c == "indefinite")
            {
                RepeatCount = double.PositiveInfinity;
            }
            else if (Values.SvgNumbers.TryParse(c, out double n) && n > 0 && double.IsFinite(n))
            {
                RepeatCount = n;
            }
            else
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s repeatCount \"{Resolving.SvgGeometry.Clip(count)}\" is not a positive number; it is ignored.", e);
            }
        }

        if (e.Attributes["repeatDur"] is string repeatDur)
        {
            string r = repeatDur.Trim();
            if (r == "indefinite")
            {
                RepeatDur = double.PositiveInfinity;
            }
            else if (SvgTiming.TryParseClock(r, out double seconds) && seconds > 0)
            {
                RepeatDur = seconds;
            }
            else
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s repeatDur \"{Resolving.SvgGeometry.Clip(repeatDur)}\" is not a positive clock value; it is ignored.", e);
            }
        }

        double min = 0, max = double.PositiveInfinity;
        if (e.Attributes["min"] is string minText && minText.Trim() != "media")
        {
            if (!SvgTiming.TryParseClock(minText, out min))
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s min \"{Resolving.SvgGeometry.Clip(minText)}\" is not a clock value; it is ignored.", e);
                min = 0;
            }
        }

        if (e.Attributes["max"] is string maxText && maxText.Trim() is not ("media" or "indefinite"))
        {
            if (!SvgTiming.TryParseClock(maxText, out max) || max <= 0)
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s max \"{Resolving.SvgGeometry.Clip(maxText)}\" is not a positive clock value; it is ignored.", e);
                max = double.PositiveInfinity;
            }
        }

        // SMIL: when min is greater than max, both are ignored.
        if (min <= max)
        {
            (Min, Max) = (min, max);
        }

        Restart = e.Attributes["restart"]?.Trim() switch
        {
            "whenNotActive" => SvgRestart.WhenNotActive,
            "never" => SvgRestart.Never,
            _ => SvgRestart.Always,
        };
        Freeze = e.Attributes["fill"]?.Trim() == "freeze";
    }

    /// <summary>
    /// The active duration of an interval that begins at <paramref name="begin"/> and whose end list gave
    /// <paramref name="end"/> (infinite for none or unresolved), as SMIL 3.0 computes it: the intermediate active duration from
    /// dur, repeatCount and repeatDur; cut by the end; then bounded by min and max. Also returns how long the animation function plays: the active duration, less any
    /// part of a min extension after its repetitions end.
    /// </summary>
    public (double Active, double Played) ActiveDuration(double begin, double end)
    {
        double d = Dur;
        double iad;
        if (d == 0)
        {
            iad = 0;
        }
        else if (double.IsNaN(RepeatCount) && double.IsNaN(RepeatDur))
        {
            iad = double.IsNaN(d) ? double.PositiveInfinity : d;
        }
        else
        {
            double simple = double.IsNaN(d) ? double.PositiveInfinity : d;
            double p1 = double.IsNaN(RepeatCount) ? double.PositiveInfinity : simple * RepeatCount;
            double p2 = double.IsNaN(RepeatDur) ? double.PositiveInfinity : RepeatDur;
            iad = Math.Min(p1, p2);
        }

        double pad;
        if (End is null)
        {
            pad = iad;
        }
        else if (double.IsNaN(d) && double.IsNaN(RepeatCount) && double.IsNaN(RepeatDur))
        {
            pad = end - begin;
        }
        else
        {
            pad = Math.Min(iad, end - begin);
        }

        pad = Math.Max(0, pad);
        double active = Math.Min(Math.Max(pad, Min), Max);

        // Within a min extension the animation plays on, as the SVG 1.1 suite's min test (animate-elem-65) has it, until
        // its own repetitions end; the fill applies after that.
        double played = Math.Min(Math.Max(iad, pad), active);
        return (active, played);
    }
}
