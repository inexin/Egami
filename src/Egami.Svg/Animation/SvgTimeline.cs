using Egami.Svg.Reading;

namespace Egami.Svg.Animation;

/// <summary>
/// The document's SMIL timeline: every animation element's intervals up to a horizon, computed with SMIL 3.0's interval rules
/// (begin and end instance lists, restart, min, max, repeatCount, repeatDur) and with syncbase and repeat values resolved from
/// the intervals of the elements they name. Dependencies are resolved in order, sources first; a loop (a begins with b, b
/// begins with a) is iterated until the intervals stop changing, and a loop that can never move time forward is cut and
/// reported. Events, access keys, wall-clock times and indefinite never resolve, as in a browser nobody touches.
/// </summary>
internal sealed class SvgTimeline
{
    /// <summary>The fewest seconds a timeline covers, so a loop's period can be seen.</summary>
    public const double MinHorizon = 20;

    /// <summary>The most seconds a timeline built for <see cref="SvgDocument.Animation"/> covers.</summary>
    public const double MaxInfoHorizon = 600;

    /// <summary>The most intervals one animation gets.</summary>
    internal const int MaxIntervals = 100_000;

    /// <summary>The most interval computations one build may do, so a hostile file cannot stall it.</summary>
    internal const long WorkBudget = 4_000_000;

    private readonly Dictionary<string, SvgAnimation> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<SvgAnimation, List<SvgAnimation>> _cut = [];
    private long _work;

    private SvgTimeline(List<SvgAnimation> animations, double horizon)
    {
        Animations = animations;
        Horizon = horizon;
    }

    /// <summary>Gets the animation elements with a target, in document order.</summary>
    public List<SvgAnimation> Animations { get; }

    /// <summary>Gets the document's CSS animations, with the elements they animate, as the static style sets them.</summary>
    public List<(SvgElement Element, SvgCssAnimation Animation)> CssAnimations { get; private init; } = [];

    /// <summary>Gets the time in seconds up to which intervals are computed; intervals that begin later are left out.</summary>
    public double Horizon { get; }

    /// <summary>Gets a value indicating whether the work budget ran out, so intervals near the horizon may be missing.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Builds the timeline of a document up to a horizon; null horizon picks one long enough for its timing values.</summary>
    public static SvgTimeline Build(SvgDocument document, SvgReport report, double? horizon = null)
    {
        List<SvgAnimation> animations = [];
        int order = 0;
        foreach (SvgElement element in document.Root.Descendants())
        {
            if (!SvgAnimation.TryGetKind(element.Name, out SvgAnimationKind kind) || !element.IsSvgOrBare(document))
            {
                continue;
            }

            if (SvgAnimation.Read(element, kind, order++, document, report) is SvgAnimation animation)
            {
                if (animation.AttributeName == "style")
                {
                    // SVG 2: the style attribute is not animatable; its properties are, by their own names.
                    report.Warn("Svg.BadAnimation", $"The {element.Name} animates the style attribute, which cannot be animated; animate the properties by name instead. It has no effect.", element);
                }
                else if (kind is SvgAnimationKind.Animate or SvgAnimationKind.Set or SvgAnimationKind.AnimateTransform)
                {
                    animation.Function = SvgAnimationFunction.Create(animation, report);
                }
                else if (kind == SvgAnimationKind.AnimateMotion)
                {
                    animation.Motion = SvgMotionFunction.Create(animation, document, report);
                }

                animations.Add(animation);
            }
        }

        SvgTimeline timeline = new(animations, horizon ?? DefaultHorizon(animations)) { CssAnimations = ScanCss(document) };
        timeline.Resolve(report);
        return timeline;
    }

    /// <summary>
    /// The CSS animations of a document: for each element, those its static computed style names (CSS Animations 1). Each
    /// starts when the document loads. Only documents with an @keyframes rule are cascaded for this.
    /// </summary>
    private static List<(SvgElement Element, SvgCssAnimation Animation)> ScanCss(SvgDocument document)
    {
        List<(SvgElement, SvgCssAnimation)> found = [];
        bool any = false;
        foreach (SvgElement element in document.Root.Descendants().Prepend(document.Root))
        {
            if (element.Name == "style" && element.Text.Contains("keyframes", StringComparison.OrdinalIgnoreCase))
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return found;
        }

        Styling.SvgStyler styler = new(document, new SvgReport(strict: false), new Css.CssMediaContext { Width = document.Size.X, Height = document.Size.Y });
        if (styler.Keyframes.Count == 0)
        {
            return found;
        }

        // Ancestors first, so each element inherits its parent's computed style.
        Dictionary<SvgElement, Css.CssComputedStyle> computed = new(IdentityComparer.Instance);
        foreach (SvgElement element in document.Root.Descendants().Prepend(document.Root))
        {
            Css.CssComputedStyle? parent = element.Parent is SvgElement p && computed.TryGetValue(p, out Css.CssComputedStyle? ps) ? ps : null;
            Css.CssComputedStyle style = styler.Computed(element, parent);
            computed[element] = style;
            if (SvgCssAnimation.Read(style, styler.Keyframes) is List<SvgCssAnimation> list)
            {
                foreach (SvgCssAnimation animation in list)
                {
                    found.Add((element, animation));
                }
            }
        }

        return found;
    }

    /// <summary>A horizon past every finite time the timing values add up to, between 20 and 600 seconds.</summary>
    private static double DefaultHorizon(List<SvgAnimation> animations)
    {
        double sum = 1;
        foreach (SvgAnimation a in animations)
        {
            foreach (SvgTimingValue v in a.Begin)
            {
                sum += Math.Abs(v.Offset);
            }

            foreach (SvgTimingValue v in a.End ?? [])
            {
                sum += Math.Abs(v.Offset);
            }

            double d = double.IsFinite(a.Dur) ? a.Dur : 0;
            sum += d * (double.IsFinite(a.RepeatCount) ? Math.Max(1, a.RepeatCount) : 1);
            sum += double.IsFinite(a.RepeatDur) ? a.RepeatDur : 0;
            sum += a.Min;
            if (sum > MaxInfoHorizon)
            {
                return MaxInfoHorizon;
            }
        }

        return Math.Clamp(sum, MinHorizon, MaxInfoHorizon);
    }

    private void Resolve(SvgReport report)
    {
        foreach (SvgAnimation a in Animations)
        {
            if (a.Element.Id is string id)
            {
                _byId.TryAdd(id, a);
            }
        }

        List<SvgAnimation> order = DependencyOrder(report);

        // Iterate until no interval changes: one pass settles everything without loops, each further pass carries a loop one
        // interval further.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (SvgAnimation a in order)
            {
                List<SvgInterval> intervals = Intervals(a, out bool cut);
                a.ReachesHorizon = cut;
                if (!intervals.SequenceEqual(a.Intervals))
                {
                    a.Intervals = intervals;
                    changed = true;
                }

                if (_work > WorkBudget)
                {
                    Truncated = true;
                    report.Warn("Svg.AnimationLimit", $"The animations' timing needs more work than {WorkBudget:N0} interval steps to reach {Horizon:0.###} s; the timeline is cut short there.", a.Element);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Orders the animations so that each comes after the ones its begin and end values name (Tarjan's strongly connected
    /// components, sources first; document order inside a loop). In a loop whose every link is a begin syncbase or repeat value
    /// with no positive offset, time never moves forward, so those links are cut and reported.
    /// </summary>
    private List<SvgAnimation> DependencyOrder(SvgReport report)
    {
        int index = 0;
        Dictionary<SvgAnimation, (int Index, int Low)> marks = [];
        Stack<SvgAnimation> stack = new();
        HashSet<SvgAnimation> onStack = [];
        List<List<SvgAnimation>> components = [];

        IEnumerable<SvgAnimation> Sources(SvgAnimation a)
        {
            foreach (SvgTimingValue v in a.End is null ? a.Begin : a.Begin.Concat(a.End))
            {
                if (v.Id is string id && _byId.TryGetValue(id, out SvgAnimation? source))
                {
                    yield return source;
                }
            }
        }

        // An explicit stack, so a long chain of animations cannot overflow the call stack.
        foreach (SvgAnimation start in Animations)
        {
            if (marks.ContainsKey(start))
            {
                continue;
            }

            Stack<(SvgAnimation Node, IEnumerator<SvgAnimation> Next)> work = new();
            marks[start] = (index, index);
            index++;
            stack.Push(start);
            onStack.Add(start);
            work.Push((start, Sources(start).GetEnumerator()));
            while (work.Count > 0)
            {
                (SvgAnimation node, IEnumerator<SvgAnimation> next) = work.Peek();
                if (next.MoveNext())
                {
                    SvgAnimation w = next.Current;
                    if (!marks.TryGetValue(w, out (int Index, int Low) wMark))
                    {
                        marks[w] = (index, index);
                        index++;
                        stack.Push(w);
                        onStack.Add(w);
                        work.Push((w, Sources(w).GetEnumerator()));
                    }
                    else if (onStack.Contains(w))
                    {
                        marks[node] = (marks[node].Index, Math.Min(marks[node].Low, wMark.Index));
                    }

                    continue;
                }

                work.Pop();
                if (work.Count > 0)
                {
                    SvgAnimation parent = work.Peek().Node;
                    marks[parent] = (marks[parent].Index, Math.Min(marks[parent].Low, marks[node].Low));
                }

                if (marks[node].Low == marks[node].Index)
                {
                    List<SvgAnimation> component = [];
                    SvgAnimation w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        component.Add(w);
                    }
                    while (w != node);

                    component.Sort((x, y) => x.Order.CompareTo(y.Order));
                    components.Add(component);
                }
            }
        }

        // Tarjan emits a component after every component it depends on, so the list is already sources first.
        List<SvgAnimation> order = [];
        foreach (List<SvgAnimation> component in components)
        {
            HashSet<SvgAnimation> members = [.. component];
            bool loops = component.Count > 1 || Sources(component[0]).Contains(component[0]);
            if (loops && !MovesForward(component, members))
            {
                // Only the links back to an element at or after this one in document order are cut, so the loop becomes a
                // chain in document order and the earlier elements still drive the later ones.
                foreach (SvgAnimation a in component)
                {
                    _cut[a] = [.. component.Where(m => m.Order >= a.Order)];
                }

                report.Warn("Svg.AnimationCycle", $"The begin values of {component.Count} animation(s) depend on each other without moving time forward; those dependencies are ignored.", component[0].Element);
            }

            order.AddRange(component);
        }

        return order;
    }

    /// <summary>Whether a loop of animations has a link that can move time forward: an end value, or a positive offset.</summary>
    private bool MovesForward(List<SvgAnimation> component, HashSet<SvgAnimation> members)
    {
        foreach (SvgAnimation a in component)
        {
            foreach (SvgTimingValue v in a.Begin)
            {
                if (v.Id is string id && _byId.TryGetValue(id, out SvgAnimation? source) && members.Contains(source) &&
                    (v.Kind == SvgTimingKind.SyncEnd || v.Offset > 0))
                {
                    return true;
                }
            }

            foreach (SvgTimingValue v in a.End ?? [])
            {
                if (v.Id is string id && _byId.TryGetValue(id, out SvgAnimation? source) && members.Contains(source))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The instance times a begin or end list gives now, sorted, and whether any of its values may never resolve.</summary>
    private List<double> Instances(SvgAnimation a, List<SvgTimingValue> list, out bool unresolved)
    {
        List<double> times = [];
        unresolved = false;
        foreach (SvgTimingValue v in list)
        {
            switch (v.Kind)
            {
                case SvgTimingKind.Offset:
                    times.Add(v.Offset);
                    break;
                case SvgTimingKind.SyncBegin:
                case SvgTimingKind.SyncEnd:
                case SvgTimingKind.Repeat:
                    unresolved = true;
                    if (!_byId.TryGetValue(v.Id!, out SvgAnimation? source) || (_cut.TryGetValue(a, out List<SvgAnimation>? cut) && cut.Contains(source)))
                    {
                        break;
                    }

                    foreach (SvgInterval interval in source.Intervals)
                    {
                        double t = v.Kind switch
                        {
                            SvgTimingKind.SyncBegin => interval.Begin,
                            SvgTimingKind.SyncEnd => interval.End,
                            _ => RepeatTime(source, interval, v.Iteration),
                        };
                        t += v.Offset;

                        // Times far before the document began cannot matter; dropping them stops a loop that runs backwards.
                        if (double.IsFinite(t) && t >= -Horizon)
                        {
                            times.Add(t);
                        }
                    }

                    break;
                default:
                    unresolved = true;
                    break;
            }
        }

        times.Sort();
        for (int i = times.Count - 1; i > 0; i--)
        {
            if (times[i] == times[i - 1])
            {
                times.RemoveAt(i);
            }
        }

        return times;
    }

    /// <summary>When the n-th repetition of an interval begins, or NaN when it never does.</summary>
    private static double RepeatTime(SvgAnimation source, SvgInterval interval, long iteration)
    {
        double d = source.SimpleDuration;
        if (iteration < 1 || !double.IsFinite(d) || d <= 0)
        {
            return double.NaN;
        }

        double t = interval.Begin + (iteration * d);
        return t < interval.PlayEnd ? t : double.NaN;
    }

    /// <summary>
    /// An animation's intervals from its current begin and end instance lists, as SMIL 3.0's getFirstInterval and
    /// getNextInterval find them, up to the horizon.
    /// </summary>
    private List<SvgInterval> Intervals(SvgAnimation a, out bool reachesHorizon)
    {
        // Every call counts toward the work budget, discard included, so Resolve's passes always end.
        _work++;
        reachesHorizon = false;
        List<SvgInterval> intervals = [];
        List<double> begins = Instances(a, a.Begin, out _);
        if (a.Kind == SvgAnimationKind.Discard)
        {
            // discard is a moment: its interval is the first begin time, and it lasts forever.
            if (begins.Count > 0 && begins[0] < Horizon)
            {
                intervals.Add(new SvgInterval(begins[0], double.PositiveInfinity, double.PositiveInfinity));
            }

            return intervals;
        }

        List<double>? ends = null;
        bool endUnresolved = false;
        if (a.End is not null)
        {
            ends = Instances(a, a.End, out endUnresolved);
        }

        double beginAfter = double.NegativeInfinity;
        double previousBegin = double.NegativeInfinity;
        int b = 0;
        while (intervals.Count < MaxIntervals)
        {
            _work++;

            // The first begin at or after beginAfter, later than the last interval's begin (so a zero-length interval cannot
            // repeat at one moment).
            while (b < begins.Count && (begins[b] < beginAfter || begins[b] <= previousBegin))
            {
                b++;
            }

            if (b >= begins.Count || begins[b] >= Horizon)
            {
                reachesHorizon = b < begins.Count;
                break;
            }

            double begin = begins[b];
            double endTime = double.PositiveInfinity;
            if (ends is not null)
            {
                int e = ends.BinarySearch(begin);
                e = e < 0 ? ~e : e;

                // An end equal to the begin ends only the first interval; later ones need an end after their begin.
                if (intervals.Count > 0)
                {
                    while (e < ends.Count && ends[e] <= begin)
                    {
                        e++;
                    }
                }

                if (e < ends.Count)
                {
                    endTime = ends[e];
                }
                else if (!endUnresolved)
                {
                    break;
                }
            }

            (double active, double played) = a.ActiveDuration(begin, endTime);
            double end = begin + active;
            double playEnd = begin + played;

            // restart="always": a begin time inside the interval ends it there and starts the next.
            if (a.Restart == SvgRestart.Always)
            {
                int next = b + 1;
                if (next < begins.Count && begins[next] < end)
                {
                    end = begins[next];
                    playEnd = Math.Min(playEnd, end);
                }
            }

            // SMIL: an interval that ends before the document begins is skipped (getFirstInterval's tempEnd > 0 test).
            if (end > 0 || (end == 0 && begin == 0))
            {
                intervals.Add(new SvgInterval(begin, end, playEnd));
                if (a.Restart == SvgRestart.Never)
                {
                    break;
                }
            }
            else if (a.Restart == SvgRestart.Never)
            {
                break;
            }

            beginAfter = end;
            previousBegin = begin;
            if (double.IsPositiveInfinity(end))
            {
                break;
            }
        }

        // The interval limit cuts the rest off as the horizon would.
        reachesHorizon |= intervals.Count >= MaxIntervals;
        return intervals;
    }

    /// <summary>Gets the animation an element id names, or null.</summary>
    public SvgAnimation? FindById(string id) => _byId.GetValueOrDefault(id);
}
