using System.Text;

namespace Egami.Fonts.Layout;

/// <summary>A line end <see cref="LineFill.Optimal"/> chose: where the line ends, and whether at an automatic hyphenation
/// point.</summary>
internal readonly record struct OptimalBreak(int End, bool AutoHyphen);

/// <summary>A <see cref="LineFlow"/>'s plan for <see cref="LineFill.Optimal"/>: the lines from where it was made, and the widths
/// it was made for.</summary>
internal sealed class OptimalPlan
{
    public float? FollowingWidth;
    public List<int> Starts = [];
    public List<OptimalBreak> Ends = [];
    public float First;
    public float Following;
}

/// <summary>
/// <see cref="LineFill.Optimal"/>: the total-fit line breaking of D. E. Knuth and M. F. Plass, "Breaking Paragraphs into Lines",
/// Software: Practice and Experience 11 (1981). Each line between two breaks has a badness from how far its spaces stretch or
/// shrink, demerits from that badness and the break's penalty, and an active-node search finds the breaks whose total demerits
/// are least.
/// </summary>
internal sealed partial class LineBuilder
{
    /// <summary>Gets the flow's plan, made the first time it is needed so plain layouts stay small.</summary>
    internal OptimalPlan Plan => Breaks.Plan ??= new();

    /// <summary>Gets <see cref="LineFlow.FollowingWidth"/> without making a plan.</summary>
    internal float? FollowingWidth => _lazy?.Breaks?.Plan?.FollowingWidth;

    // Penalties at or below this force a break (TeX's eject penalty); badness at or above inf_bad is "infinitely bad".
    private const int ForcedPenalty = -10000;
    private const double InfiniteBadness = 10000;

    private enum Fitness : byte
    {
        VeryLoose = 0,
        Loose = 1,
        Decent = 2,
        Tight = 3,
    }

    /// <summary>A break the search may choose: the end of a line, its penalty, whether it ends with a hyphen, where the line's
    /// content ends (before trailing spaces, for a line starting at or before it) and the width of the hyphen it adds.</summary>
    private readonly record struct Candidate(int Index, int Penalty, bool Flagged, bool Auto, bool SoftHyphen, int ContentEnd = 0, float Hyphen = 0)
    {
        public bool Forced => Penalty <= ForcedPenalty;
    }

    /// <summary>A feasible break: the best way to reach it with its fitness and run of hyphenated lines.</summary>
    private struct Node
    {
        public int Position;
        public int Candidate;
        public Fitness Fitness;
        public byte Hyphens;
        public bool Flagged;
        public double Total;
        public int Previous;
    }

    /// <summary>
    /// The line ends <see cref="LineFill.Optimal"/> chooses from <paramref name="start"/> to the paragraph's end: the first line
    /// <paramref name="first"/> wide, the others <paramref name="following"/>. Each run of text between mandatory breaks is
    /// broken on its own.
    /// </summary>
    public List<OptimalBreak> OptimalEnds(int start, float first, float following)
    {
        OptimalLineOptions options = _paragraph.Style.OptimalLines ?? OptimalLineOptions.Default;
        List<OptimalBreak> ends = [];
        List<Candidate> candidates = [];
        int segmentStart = start;
        bool firstLine = true;
        while (segmentStart < _text.Length)
        {
            candidates.Clear();
            Candidates(segmentStart, options, candidates);
            BreakSegment(segmentStart, candidates, firstLine ? first : following, following, options, ends);
            segmentStart = candidates[^1].Index;
            firstLine = false;
        }

        return ends;
    }

    /// <summary>The break candidates from <paramref name="start"/> to the next mandatory break, for tests.</summary>
    internal List<OptimalBreak> CandidateEnds(int start)
    {
        List<Candidate> candidates = [];
        Candidates(start, _paragraph.Style.OptimalLines ?? OptimalLineOptions.Default, candidates);
        return candidates.ConvertAll(c => new OptimalBreak(c.Index, c.Auto));
    }

    /// <summary>
    /// The total demerits of the lines from <paramref name="start"/> to <paramref name="ends"/>, as the search counts them, or
    /// infinity when a line is overfull or worse than <paramref name="tolerance"/>; with <paramref name="overfullFree"/>, an
    /// overfull line costs nothing, as the search's last resort makes it. For tests: the ends must stay within one run between
    /// mandatory breaks.
    /// </summary>
    internal double TotalDemerits(int start, IReadOnlyList<OptimalBreak> ends, float first, float following, double tolerance, bool overfullFree = false)
    {
        OptimalLineOptions options = _paragraph.Style.OptimalLines ?? OptimalLineOptions.Default;
        List<Candidate> candidates = [];
        Candidates(start, options, candidates);
        Node from = new() { Position = start, Candidate = -1, Fitness = Fitness.Decent };
        double total = 0;
        foreach (OptimalBreak end in ends)
        {
            Candidate c = candidates.Find(x => x.Index == end.End && x.Auto == end.AutoHyphen);
            if (c.Index == 0)
            {
                return double.PositiveInfinity;
            }

            float width = from.Candidate < 0 ? first : following;
            double ratio = Ratio(from.Position, c, width, options, out bool ragged);
            if (ratio < -1)
            {
                if (!overfullFree)
                {
                    return double.PositiveInfinity;
                }

                from = new() { Position = c.Index, Candidate = 0, Fitness = Fitness.Decent, Flagged = c.Flagged };
                continue;
            }

            double badness = Badness(ratio, ragged);
            if (badness > tolerance)
            {
                return double.PositiveInfinity;
            }

            Fitness fitness = FitnessOf(ratio);
            total += Demerits(from, c, badness, fitness, options);
            from = new() { Position = c.Index, Candidate = 0, Fitness = fitness, Flagged = c.Flagged };
        }

        return total;
    }

    /// <summary>What the search knows of one line, for tests.</summary>
    internal readonly record struct OptimalLine(double Ratio, bool Ragged, int Penalty, bool Flagged, bool Forced)
    {
        public double Badness => LineBuilder.Badness(Ratio, Ragged);
    }

    /// <summary>
    /// For tests: every line of the run from <paramref name="start"/> to its next mandatory break, entry [i + 1, j] the line from
    /// candidate i (-1 for <paramref name="start"/>, whose line is <paramref name="first"/> wide) to candidate j, null when it
    /// is overfull or does not go forward.
    /// </summary>
    internal OptimalLine?[,] LineTable(int start, float first, float following)
    {
        OptimalLineOptions options = _paragraph.Style.OptimalLines ?? OptimalLineOptions.Default;
        List<Candidate> candidates = [];
        Candidates(start, options, candidates);
        OptimalLine?[,] table = new OptimalLine?[candidates.Count + 1, candidates.Count];
        for (int i = -1; i < candidates.Count; i++)
        {
            int from = i < 0 ? start : candidates[i].Index;
            for (int j = i + 1; j < candidates.Count; j++)
            {
                Candidate c = candidates[j];
                double ratio = Ratio(from, c, i < 0 ? first : following, options, out bool ragged);
                if (ratio >= -1 && c.Index > from)
                {
                    table[i + 1, j] = new OptimalLine(ratio, ragged, c.Penalty, c.Flagged, c.Forced);
                }
            }
        }

        return table;
    }

    /// <summary>For tests: the demerits of <paramref name="line"/> after <paramref name="previous"/> (null at the start).</summary>
    internal double LineDemerits(OptimalLine? previous, OptimalLine line)
    {
        OptimalLineOptions options = _paragraph.Style.OptimalLines ?? OptimalLineOptions.Default;
        Node from = previous is { } p
            ? new Node { Fitness = FitnessOf(p.Ratio), Flagged = p.Flagged }
            : new Node { Fitness = Fitness.Decent };
        Candidate c = new(1, line.Penalty, line.Flagged, false, false, 1);
        return Demerits(from, c, line.Badness, FitnessOf(line.Ratio), options);
    }

    /// <summary>Collects the candidates after <paramref name="start"/> up to and including the next mandatory break: the
    /// paragraph's break opportunities and, with <see cref="Hyphens.Auto"/>, the hyphenation points between them.</summary>
    private void Candidates(int start, OptimalLineOptions options, List<Candidate> candidates)
    {
        LineBreak[] breaks = _paragraph.Breaks;
        HyphenationOptions? auto = _paragraph.Style.Hyphens == Hyphens.Auto ? _paragraph.Style.Hyphenation : null;
        List<int>? points = auto is null ? null : [];
        int previous = start;
        for (int k = FirstBreakAfter(start); k < breaks.Length; k++)
        {
            LineBreak b = breaks[k];
            if (points is not null)
            {
                points.Clear();
                WordPoints(previous, b.Index, auto!, points);
                foreach (int point in points)
                {
                    if (point > start)
                    {
                        candidates.Add(new Candidate(point, options.HyphenPenalty, true, true, false, point, HyphenWidth(point)));
                    }
                }
            }

            int contentEnd = ContentEnd(0, b.Index);
            if (b.IsMandatory || b.Index >= _text.Length)
            {
                candidates.Add(new Candidate(b.Index, ForcedPenalty, false, false, false, contentEnd));
                return;
            }

            if (b.IsSoftHyphen)
            {
                candidates.Add(new Candidate(b.Index, options.HyphenPenalty, true, false, true, contentEnd, HyphenWidth(b.Index)));
            }
            else if (b.Index > 0 && _text[b.Index - 1] is '-' or (char)0x2010)
            {
                candidates.Add(new Candidate(b.Index, options.ExplicitHyphenPenalty, true, false, false, contentEnd));
            }
            else
            {
                candidates.Add(new Candidate(b.Index, 0, false, false, false, contentEnd));
            }

            previous = b.Index;
        }
    }

    /// <summary>
    /// Breaks one run of text between mandatory breaks, adding its line ends to <paramref name="ends"/>: a pass at
    /// <see cref="OptimalLineOptions.Tolerance"/>, then one at <see cref="OptimalLineOptions.SecondTolerance"/>, then one that
    /// takes any line that is not overfull and, where nothing fits, an overfull one.
    /// </summary>
    private void BreakSegment(int start, List<Candidate> candidates, float first, float following, OptimalLineOptions options, List<OptimalBreak> ends)
    {
        List<Node> nodes = [];
        List<int> active = [];
        int maxHyphens = _paragraph.Style.Hyphens == Hyphens.Auto && _paragraph.Style.Hyphenation is { } h ? Math.Min(h.MaxConsecutiveLines, 254) : 0;
        int classes = 4 * (maxHyphens + 1);
        double[] best = new double[classes];
        int[] bestFrom = new int[classes];
        double[] least = new double[maxHyphens + 1];
        ReadOnlySpan<double> tolerances = [options.Tolerance, options.SecondTolerance, double.PositiveInfinity];
        for (int pass = 0; pass < tolerances.Length; pass++)
        {
            double tolerance = tolerances[pass];
            bool final = pass == tolerances.Length - 1;
            nodes.Clear();
            active.Clear();
            nodes.Add(new Node { Position = start, Candidate = -1, Fitness = Fitness.Decent, Previous = -1 });
            active.Add(0);
            int last = -1;
            for (int c = 0; c < candidates.Count && active.Count > 0; c++)
            {
                Candidate candidate = candidates[c];
                Array.Fill(best, double.PositiveInfinity);
                Array.Fill(least, double.PositiveInfinity);
                for (int a = 0; a < active.Count; a++)
                {
                    Node from = nodes[active[a]];
                    float width = from.Candidate < 0 ? first : following;
                    double ratio = Ratio(from.Position, candidate, width, options, out bool ragged);
                    if (ratio < -1)
                    {
                        // Overfull, and only more so for later candidates. On the last pass, the last active node breaks here
                        // anyway when nothing else can: the piece that is too wide gets a line of its own.
                        if (final && active.Count == 1 && double.IsPositiveInfinity(best.Min()))
                        {
                            Offer(from, active[a], candidate, 0, Fitness.Decent, maxHyphens, best, bestFrom, least, artificial: true);
                        }

                        active.RemoveAt(a--);
                        continue;
                    }

                    double badness = Badness(ratio, ragged);
                    if (badness <= tolerance && (!candidate.Auto || maxHyphens == 0 || from.Hyphens < maxHyphens))
                    {
                        Fitness fitness = FitnessOf(ratio);
                        Offer(from, active[a], candidate, Demerits(from, candidate, badness, fitness, options), fitness, maxHyphens, best, bestFrom, least);
                    }

                    if (candidate.Forced)
                    {
                        active.RemoveAt(a--);
                    }
                }

                // New active nodes, one per fitness class and hyphen run; as in TeX, only those that might still win: within the
                // adjacency demerits of the best with the same run of hyphens (the next line's fitness is all that differs).
                for (int k = 0; k < classes; k++)
                {
                    if (best[k] <= least[k >> 2] + options.AdjacentDemerits && !double.IsPositiveInfinity(best[k]))
                    {
                        nodes.Add(new Node
                        {
                            Position = candidate.Index,
                            Candidate = c,
                            Fitness = (Fitness)(k & 3),
                            Hyphens = (byte)(k >> 2),
                            Flagged = candidate.Flagged,
                            Total = best[k],
                            Previous = bestFrom[k],
                        });
                        if (candidate.Forced)
                        {
                            if (last < 0 || best[k] < nodes[last].Total)
                            {
                                last = nodes.Count - 1;
                            }
                        }
                        else
                        {
                            active.Add(nodes.Count - 1);
                        }
                    }
                }
            }

            if (last >= 0)
            {
                // The tolerance passes trade a little cost for speed, as in TeX: a pass may settle for breaks that cost more
                // than greedy filling's, which may have a line past the tolerance. Then the greedy breaks win.
                List<int> greedy = [];
                if (Greedy(start, candidates, first, following, options, maxHyphens, greedy) < nodes[last].Total)
                {
                    foreach (int c in greedy)
                    {
                        ends.Add(new OptimalBreak(candidates[c].Index, candidates[c].Auto));
                    }

                    return;
                }

                int at = ends.Count;
                for (int n = last; nodes[n].Candidate >= 0; n = nodes[n].Previous)
                {
                    Candidate c = candidates[nodes[n].Candidate];
                    ends.Insert(at, new OptimalBreak(c.Index, c.Auto));
                }

                return;
            }
        }

        // Unreachable: the last pass always reaches the mandatory break. Fall back to one line per candidate run.
        ends.Add(new OptimalBreak(candidates[^1].Index, false));
    }

    /// <summary>
    /// The breaks greedy filling would choose in the run (each line to the furthest candidate that fits, or the first when none
    /// does), added to <paramref name="picks"/>, and their total demerits, an overfull line costing nothing.
    /// </summary>
    private double Greedy(int start, List<Candidate> candidates, float first, float following, OptimalLineOptions options, int maxHyphens, List<int> picks)
    {
        Node from = new() { Position = start, Candidate = -1, Fitness = Fitness.Decent };
        double total = 0;
        int c = 0;
        while (c < candidates.Count)
        {
            float width = from.Candidate < 0 ? first : following;
            int chosen = -1;
            double chosenRatio = 0;
            bool chosenRagged = false;
            for (int k = c; k < candidates.Count; k++)
            {
                if (candidates[k].Auto && maxHyphens > 0 && from.Hyphens >= maxHyphens)
                {
                    continue;
                }

                double ratio = Ratio(from.Position, candidates[k], width, options, out bool ragged);
                if (ratio < -1)
                {
                    break;
                }

                (chosen, chosenRatio, chosenRagged) = (k, ratio, ragged);
                if (candidates[k].Forced)
                {
                    break;
                }
            }

            Candidate candidate;
            Fitness fitness = Fitness.Decent;
            if (chosen < 0)
            {
                chosen = c;
                candidate = candidates[c];
            }
            else
            {
                candidate = candidates[chosen];
                fitness = FitnessOf(chosenRatio);
                total += Demerits(from, candidate, Badness(chosenRatio, chosenRagged), fitness, options);
            }

            picks.Add(chosen);
            int hyphens = candidate.Flagged && maxHyphens > 0 ? Math.Min(from.Hyphens + 1, maxHyphens) : 0;
            from = new Node { Position = candidate.Index, Candidate = chosen, Fitness = fitness, Hyphens = (byte)hyphens, Flagged = candidate.Flagged };
            c = chosen + 1;
        }

        return total;
    }

    private static void Offer(Node from, int fromIndex, Candidate candidate, double demerits, Fitness fitness, int maxHyphens, double[] best, int[] bestFrom, double[] least, bool artificial = false)
    {
        int hyphens = candidate.Flagged && maxHyphens > 0 ? Math.Min(from.Hyphens + 1, maxHyphens) : 0;
        int k = (hyphens << 2) | (int)fitness;
        double total = artificial ? from.Total : from.Total + demerits;
        if (total < best[k])
        {
            best[k] = total;
            bestFrom[k] = fromIndex;
            least[hyphens] = Math.Min(least[hyphens], total);
        }
    }

    /// <summary>
    /// How far the line from <paramref name="start"/> to <paramref name="candidate"/> stretches (positive) or shrinks (negative)
    /// to fill <paramref name="width"/>, as a share of what it can: below -1 is overfull. Justified lines stretch their word
    /// spaces by half a space each and, with <see cref="OptimalLineOptions.ShrinkSpaces"/>, shrink them by a third (one without
    /// spaces cannot stretch). Ragged lines are <paramref name="ragged"/>: the ratio is the space left at the end in units of
    /// 2 em. The line before a mandatory break stretches without limit.
    /// </summary>
    private double Ratio(int start, Candidate candidate, float width, OptimalLineOptions options, out bool ragged)
    {
        ragged = false;
        int end = Math.Max(start, candidate.ContentEnd);
        float natural = Measure(start, end) + candidate.Hyphen;
        bool justified = _paragraph.Style.Align == TextAlign.Justify && _paragraph.Style.Justify != TextJustify.None;
        float[]? stretch = justified ? Stretch() : null;
        double spaces = stretch is null ? 0 : stretch[end] - stretch[start];
        if (natural > width + Epsilon)
        {
            double shrink = justified && options.ShrinkSpaces ? spaces * (2.0 / 3) : 0;
            return shrink > 0 ? (width - natural) / shrink : double.NegativeInfinity;
        }

        if (candidate.Forced)
        {
            return 0;
        }

        // A justified line without word spaces cannot stretch: infinitely bad unless full, as an underfull box in TeX.
        ragged = !justified;
        double room = ragged ? 2 * _paragraph.Styles[0].Size : spaces;
        return room > 0 ? Math.Max(0, width - natural) / room : double.PositiveInfinity;
    }

    /// <summary>TeX's badness, 100 |r|^3, or for ragged lines 100 r^2 (the squared space left), capped at 10000 (infinitely
    /// bad).</summary>
    private static double Badness(double ratio, bool ragged)
    {
        double r = Math.Abs(ratio);
        if (r >= 100)
        {
            return InfiniteBadness;
        }

        return Math.Min(InfiniteBadness, ragged ? 100 * r * r : 100 * r * r * r);
    }

    private static Fitness FitnessOf(double ratio) => ratio switch
    {
        < -0.5 => Fitness.Tight,
        <= 0.5 => Fitness.Decent,
        <= 1 => Fitness.Loose,
        _ => Fitness.VeryLoose,
    };

    /// <summary>The demerits of a line: (line penalty + badness)^2 plus or minus the break's penalty squared, and the extra
    /// demerits for two hyphenated lines in a row, a hyphen on the last line but one, and fitness classes more than one apart.</summary>
    private static double Demerits(Node from, Candidate candidate, double badness, Fitness fitness, OptimalLineOptions options)
    {
        double d = options.LinePenalty + badness;
        d *= d;
        if (candidate.Penalty > 0)
        {
            d += (double)candidate.Penalty * candidate.Penalty;
        }
        else if (candidate.Penalty > ForcedPenalty)
        {
            d -= (double)candidate.Penalty * candidate.Penalty;
        }

        if (from.Flagged && candidate.Flagged)
        {
            d += options.DoubleHyphenDemerits;
        }
        else if (from.Flagged && candidate.Forced)
        {
            d += options.FinalHyphenDemerits;
        }

        if (Math.Abs((int)from.Fitness - (int)fitness) > 1)
        {
            d += options.AdjacentDemerits;
        }

        return d;
    }

    /// <summary>The running sum of the stretch of word separators (half their advance), made the first time it is needed.</summary>
    private float[] Stretch()
    {
        BreakState state = Breaks;
        if (state.Stretch is { } done)
        {
            return done;
        }

        float[] sums = new float[_text.Length + 1];
        float sum = 0;
        for (int i = 0; i < _text.Length; i++)
        {
            if (Rune.DecodeFromUtf16(_text.AsSpan(i), out Rune rune, out _) == System.Buffers.OperationStatus.Done
                && AdvanceTable.IsWordSeparator(rune.Value))
            {
                sum += _m.Advance[i] / 2;
            }

            sums[i + 1] = sum;
        }

        state.Stretch = sums;
        return sums;
    }
}
