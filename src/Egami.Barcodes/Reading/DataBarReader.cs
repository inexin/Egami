using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Barcodes.Linear;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads GS1 DataBar (ISO/IEC 24724). A DataBar Omnidirectional symbol is two pairs, each an outside character, a finder and
/// an inside character: the left pair reads forwards, the right pair backwards, so a scan line finds each pair in the direction
/// its finder reads in, and the pairs combine, from one line or (for the stacked forms) from two, when their checksum holds.
/// Character widths come from the runs, corrected for ink spread measured on the finder, rounded to the module counts each
/// character group allows.
/// </summary>
internal static class DataBarReader
{
    /// <summary>One half of an Omnidirectional symbol, as read along a line.</summary>
    internal readonly record struct OmniPair(bool Right, int Finder, int Value, int Checksum, float Start, float End, float Module);

    /// <summary>Finds the pairs on a line whose runs start light; each is read in the line's direction.</summary>
    public static void OmniPairs(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, List<OmniPair> pairs)
    {
        Span<float> finder = stackalloc float[5];
        Span<float> outside = stackalloc float[8], inside = stackalloc float[8];
        int insideValue = 0;
        for (int i = 9; i + 13 <= runs.Length; i++)
        {
            // Widths from the offsets, which are where the runs start.
            float total = offsets[i + 5] - offsets[i];
            if (total <= 0)
            {
                continue;
            }

            // Finder: the last two elements are a module each.
            float unit = total / 15;
            if (runs[i + 3] + runs[i + 4] > 3 * unit)
            {
                continue;
            }

            // The characters around it are 16 and 15 modules, near the finder's module.
            float outsideWidth = offsets[i] - offsets[i - 8], insideWidth = offsets[i + 13] - offsets[i + 5];
            if (MathF.Abs((outsideWidth / 16) - unit) > 0.25f * unit || MathF.Abs((insideWidth / 15) - unit) > 0.25f * unit)
            {
                continue;
            }

            for (int k = 0; k < 5; k++)
            {
                finder[k] = runs[i + k] / unit;
            }

            bool right = i % 2 == 1;
            int value = FinderValue(finder, DataBarTables.OmniFinders, 9, right ? 0 : 1, out float spread);
            if (value < 0)
            {
                continue;
            }

            // Guards: the left pair's guard bar before it; the right pair's guard space and bar (read backwards).
            float module = (outsideWidth + total + insideWidth) / 46;
            if (!Near(runs[i - 9], module) || (right && (i < 10 || !Near(runs[i - 10], module))))
            {
                continue;
            }

            for (int k = 0; k < 8; k++)
            {
                // Element k of each character in the symbol's own order; the inside character reads backwards.
                int o = i - 8 + k, n = i + 12 - k;
                outside[k] = Corrected(runs[o], outsideWidth / 16, o, spread);
                inside[k] = Corrected(runs[n], insideWidth / 15, n, spread);
            }

            int[]? outsideWidths = OmniCharacter(outside, true, out int outsideValue);
            int[]? insideWidths = outsideWidths is null ? null : OmniCharacter(inside, false, out insideValue);
            if (outsideWidths is null || insideWidths is null)
            {
                continue;
            }

            int row = right ? 2 : 0, checksum = 0;
            for (int k = 0; k < 8; k++)
            {
                checksum += (DataBarTables.OmniChecksumWeights[(row * 8) + k] * outsideWidths[k]) + (DataBarTables.OmniChecksumWeights[((row + 1) * 8) + k] * insideWidths[k]);
            }

            pairs.Add(new OmniPair(right, value, (1597 * outsideValue) + insideValue, checksum, offsets[i - 9], offsets[i + 13], module));
            i += 12;
        }
    }

    /// <summary>The symbol two pairs make, when their checksum holds and they hold a GTIN.</summary>
    public static DecodedData? Combine(in OmniPair left, in OmniPair right)
    {
        int checksum = (left.Checksum + right.Checksum) % 79;
        if (checksum >= 8)
        {
            checksum++;
        }

        if (checksum >= 72)
        {
            checksum++;
        }

        if (checksum != (9 * left.Finder) + right.Finder)
        {
            return null;
        }

        long value = ((long)left.Value * 4537077) + right.Value;
        return value > DataBarEncoder.MaxOmniValue ? null : Gtin(BarcodeFormat.DataBar, value);
    }

    /// <summary>The result for a GTIN's first 13 digits: the AI and GTIN-14 with its check digit.</summary>
    internal static DecodedData Gtin(BarcodeFormat format, long value)
    {
        string digits = value.ToString("D13", System.Globalization.CultureInfo.InvariantCulture);
        string gtin = digits + (char)('0' + Gs1Data.Gs1Checks.CheckDigit(digits));
        Gs1ElementString element = Gs1ElementString.Create("01", gtin);
        return new DecodedData(format, element.ToString(), Encoding.ASCII.GetBytes("01" + gtin)) { Gs1 = [element] };
    }

    /// <summary>The value of an Omnidirectional character from its 8 widths in modules, and its integer widths.</summary>
    internal static int[]? OmniCharacter(ReadOnlySpan<float> elements, bool outside, out int value)
    {
        value = -1;
        int n = outside ? 16 : 15, first = outside ? 0 : 5, last = outside ? 4 : 8;
        float odd = elements[0] + elements[2] + elements[4] + elements[6];
        Span<int> order = stackalloc int[5];
        int count = 0;
        for (int g = first; g <= last; g++)
        {
            order[count++] = g;
        }

        order = order[..count];
        SortByDistance(order, odd, DataBarTables.OmniModules);
        int[] widths = new int[8];
        Span<float> half = stackalloc float[4];
        Span<int> rounded = stackalloc int[4];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int group = order[attempt];
            int oddModules = DataBarTables.OmniModules[group], widest = DataBarTables.OmniWidest[group];
            if (!Half(elements, 0, oddModules, half, rounded, widths) || !Half(elements, 1, n - oddModules, half, rounded, widths))
            {
                continue;
            }

            int oddValue = DataBarWidths.Value(Pick(widths, 0, rounded), widest, !outside);
            int evenValue = DataBarWidths.Value(Pick(widths, 1, rounded), 9 - widest, outside);
            if (oddValue < 0 || evenValue < 0)
            {
                continue;
            }

            int end = group == 4 ? 2841 : group == 8 ? 1597 : DataBarTables.OmniGroupSum[group + 1];
            int t = DataBarTables.OmniCombinations[group], other = (end - DataBarTables.OmniGroupSum[group]) / t;
            if (outside ? oddValue >= other || evenValue >= t : oddValue >= t || evenValue >= other)
            {
                continue;
            }

            value = DataBarTables.OmniGroupSum[group] + (outside ? (oddValue * t) + evenValue : (evenValue * t) + oddValue);
            return widths;
        }

        return null;
    }

    /// <summary>
    /// Rounds the odd (<paramref name="parity"/> 0) or even elements of a character to whole modules that sum to
    /// <paramref name="modules"/>: each to its nearest, then the ones rounded furthest moved by a module until the sum is right.
    /// </summary>
    internal static bool Half(ReadOnlySpan<float> elements, int parity, int modules, Span<float> half, Span<int> rounded, Span<int> widths)
    {
        int count = elements.Length / 2, sum = 0;
        for (int k = 0; k < count; k++)
        {
            half[k] = elements[(2 * k) + parity];
            rounded[k] = Math.Max(1, (int)MathF.Round(half[k]));
            sum += rounded[k];
        }

        if (Math.Abs(sum - modules) > 2)
        {
            return false;
        }

        while (sum != modules)
        {
            int best = -1;
            float bestError = float.MinValue;
            for (int k = 0; k < count; k++)
            {
                float error = sum < modules ? half[k] - rounded[k] : rounded[k] - half[k];
                if ((sum < modules || rounded[k] > 1) && error > bestError)
                {
                    (best, bestError) = (k, error);
                }
            }

            if (best < 0)
            {
                return false;
            }

            rounded[best] += sum < modules ? 1 : -1;
            sum += sum < modules ? 1 : -1;
        }

        for (int k = 0; k < count; k++)
        {
            widths[(2 * k) + parity] = rounded[k];
        }

        return true;
    }

    /// <summary>The odd or even elements of a character's widths.</summary>
    internal static ReadOnlySpan<int> Pick(ReadOnlySpan<int> widths, int parity, Span<int> into)
    {
        int count = widths.Length / 2;
        for (int k = 0; k < count; k++)
        {
            into[k] = widths[(2 * k) + parity];
        }

        return into[..count];
    }

    /// <summary>Orders groups by how near their odd module count is to the measured one.</summary>
    internal static void SortByDistance(Span<int> groups, float measured, ReadOnlySpan<byte> oddModules)
    {
        for (int a = 1; a < groups.Length; a++)
        {
            for (int b = a; b > 0 && MathF.Abs(oddModules[groups[b]] - measured) < MathF.Abs(oddModules[groups[b - 1]] - measured); b--)
            {
                (groups[b], groups[b - 1]) = (groups[b - 1], groups[b]);
            }
        }
    }

    /// <summary>
    /// The finder a run of 5 widths in modules matches, or -1; and the ink spread its bars show (in modules, positive when bars
    /// are wide). <paramref name="darkFirst"/> is 0 when the first element is dark, 1 when light. Only the patterns from
    /// <paramref name="first"/> in steps of <paramref name="step"/> are tried.
    /// </summary>
    internal static int FinderValue(ReadOnlySpan<float> finder, ReadOnlySpan<byte> patterns, int count, int darkFirst, out float spread, int first = 0, int step = 1)
    {
        spread = 0;
        int best = -1;
        float bestError = 0.5f;
        for (int p = first; p < count; p += step)
        {
            ReadOnlySpan<byte> pattern = patterns.Slice(p * 5, 5);

            // Bar-to-bar and space-to-space widths do not change with ink spread.
            float error = 0;
            for (int k = 0; k < 4; k++)
            {
                error = MathF.Max(error, MathF.Abs(finder[k] + finder[k + 1] - pattern[k] - pattern[k + 1]));
            }

            if (error < bestError)
            {
                (best, bestError) = (p, error);
            }
        }

        if (best < 0)
        {
            return -1;
        }

        float dark = 0, light = 0;
        for (int k = 0; k < 5; k++)
        {
            float excess = finder[k] - patterns[(best * 5) + k];
            if ((k + darkFirst) % 2 == 0)
            {
                dark += excess;
            }
            else
            {
                light += excess;
            }
        }

        spread = Math.Clamp((dark - light) / 5, -0.5f, 0.5f);
        return best;
    }

    /// <summary>A run in modules, less the ink spread on a bar or plus it on a space (runs at odd indices are dark).</summary>
    internal static float Corrected(float run, float module, int index, float spread) => (run / module) + (index % 2 == 1 ? -spread : spread);

    private static bool Near(float run, float module) => run >= 0.4f * module && run <= 2.2f * module;
}

/// <summary>
/// The DataBar pairs and Expanded segments a scan collects, in image coordinates, and the symbols they make: pairs combine
/// when their checksum holds and they lie next to each other (side by side on one row, or one above the other in a stacked
/// symbol); segments join in a finder sequence, from one line or from the rows of a stacked symbol, when the check character
/// holds.
/// </summary>
internal sealed class DataBarCollector
{
    /// <summary>The formats the collector reads.</summary>
    public const BarcodeFormat Formats = BarcodeFormat.DataBar | BarcodeFormat.DataBarExpanded;

    private const int MaxSegments = 64;

    private readonly List<(DataBarReader.OmniPair Pair, Vector2 From, Vector2 To)> _left = [], _right = [];
    private readonly List<DataBarReader.OmniPair> _pairs = [];
    private readonly List<(DataBarExpandedReader.Block[] Blocks, float Start, float End)> _lineSegments = [];
    private readonly List<(DataBarExpandedReader.Block[] Blocks, Vector2 From, Vector2 To)> _segments = [];

    // The symbols each stored segment is part of, and the segments of the assembly being searched.
    private readonly List<List<(DecodedData Data, Vector2 From, Vector2 To)>> _symbols = [];
    private readonly List<(int Segment, List<int> Segments)> _path = [];

    // The places and sums (with whether the required segment is in) from which a search found nothing, by generation.
    private readonly int[] _dead = new int[11 * 211 * 2];
    private int _generation;
    private readonly List<DataBarExpandedReader.Block> _assembly = [];

    // Where each finder is in the stored segments, as the block and the segments whose blocks from there on are the same,
    // since they assemble alike: the first such segment and block, and the segments.
    private readonly List<(int Segment, int Block, List<int> Segments)>[] _finders = [.. Enumerable.Range(0, 12).Select(_ => new List<(int, int, List<int>)>())];

    /// <summary>Reads the pairs and segments on a line and returns each symbol they complete, with the ends of the symbol.</summary>
    public void Add(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, Vector2 origin, Vector2 direction, BarcodeFormat formats, List<(DecodedData Data, Vector2 From, Vector2 To)> found)
    {
        if ((formats & BarcodeFormat.DataBar) != 0)
        {
            AddPairs(runs, offsets, origin, direction, found);
        }

        if ((formats & BarcodeFormat.DataBarExpanded) != 0)
        {
            AddSegments(runs, offsets, origin, direction, found);
        }
    }

    private void AddPairs(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, Vector2 origin, Vector2 direction, List<(DecodedData Data, Vector2 From, Vector2 To)> found)
    {
        _pairs.Clear();
        DataBarReader.OmniPairs(runs, offsets, _pairs);
        foreach (DataBarReader.OmniPair pair in _pairs)
        {
            Vector2 from = origin + (direction * pair.Start), to = origin + (direction * pair.End);
            List<(DataBarReader.OmniPair Pair, Vector2 From, Vector2 To)> mine = pair.Right ? _right : _left, others = pair.Right ? _left : _right;
            foreach ((DataBarReader.OmniPair other, Vector2 otherFrom, Vector2 otherTo) in others)
            {
                if (!Adjacent(from, to, otherFrom, otherTo))
                {
                    continue;
                }

                DecodedData? data = pair.Right ? DataBarReader.Combine(other, pair) : DataBarReader.Combine(pair, other);
                if (data is not null)
                {
                    found.Add(pair.Right ? (data, otherFrom, from) : (data, from, otherFrom));
                }
            }

            if (mine.Count < 256 && !mine.Exists(m => m.Pair.Finder == pair.Finder && m.Pair.Value == pair.Value && m.Pair.Checksum == pair.Checksum && Vector2.Distance(m.To, to) < Vector2.Distance(from, to) / 4))
            {
                mine.Add((pair, from, to));
            }
        }
    }

    /// <summary>
    /// Adds the line's Expanded segments (a segment read before counts again) and returns the symbols each one makes: a
    /// one-row symbol alone, a stacked one with the segments of its other rows. A new segment is assembled with the stored
    /// ones, and each symbol found is kept with every segment it uses, so that a segment read again returns its symbols
    /// without another search.
    /// </summary>
    private void AddSegments(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, Vector2 origin, Vector2 direction, List<(DecodedData Data, Vector2 From, Vector2 To)> found)
    {
        _lineSegments.Clear();
        DataBarExpandedReader.Segments(runs, offsets, _lineSegments);
        foreach ((DataBarExpandedReader.Block[] blocks, float start, float end) in _lineSegments)
        {
            int index = _segments.FindIndex(s => Same(s.Blocks, blocks));
            if (index < 0)
            {
                if (_segments.Count >= MaxSegments)
                {
                    continue;
                }

                index = _segments.Count;
                _segments.Add((blocks, origin + (direction * start), origin + (direction * end)));
                _symbols.Add([]);
                for (int o = 0; o < blocks.Length; o++)
                {
                    List<(int Segment, int Block, List<int> Segments)> places = _finders[blocks[o].Finder];
                    int place = places.FindIndex(p => Same(_segments[p.Segment].Blocks.AsSpan(p.Block), blocks.AsSpan(o)));
                    if (place < 0)
                    {
                        places.Add((index, o, [index]));
                    }
                    else
                    {
                        places[place].Segments.Add(index);
                    }
                }

                Assemble(index);
            }

            found.AddRange(_symbols[index]);
        }
    }

    /// <summary>
    /// Assembles segment <paramref name="required"/> with the stored segments into each finder sequence, and keeps the symbols
    /// found with the segments they use.
    /// </summary>
    private void Assemble(int required)
    {
        int budget = 512;
        for (int length = 2; length <= 11; length++)
        {
            for (int odd = 0; odd < 2; odd++)
            {
                int symbolCharacters = (2 * length) - odd;
                if (symbolCharacters >= 4 && DataBarExpandedReader.Fits(_segments[required].Blocks, DataBarTables.ExpandedFinderSequences.Slice((length - 2) * 11, length)))
                {
                    _generation++;
                    _assembly.Clear();
                    _path.Clear();
                    Extend(required, false, length, symbolCharacters, 0, ref budget);
                }
            }
        }
    }

    /// <summary>
    /// Extends the blocks assembled so far towards a sequence of <paramref name="length"/> finders and
    /// <paramref name="symbolCharacters"/> characters with each stored segment (from any of its blocks) that continues it, and
    /// keeps the symbols that use segment <paramref name="required"/>. The check character is a sum mod 211 of the other
    /// characters' widths, so the search carries the sum so far: from a place and sum where no symbol was found, none is.
    /// </summary>
    private bool Extend(int required, bool used, int length, int symbolCharacters, int sum, ref int budget)
    {
        int count = _assembly.Count;
        if (count == length)
        {
            if (sum != 0 || !used || DataBarExpandedReader.Decode(CollectionsMarshal.AsSpan(_assembly)) is not DecodedData data)
            {
                return false;
            }

            (DecodedData, Vector2, Vector2) symbol = (data, _segments[_path[0].Segment].From, _segments[_path[^1].Segment].To);
            foreach ((_, List<int> segments) in _path)
            {
                foreach (int segment in segments)
                {
                    if (!_symbols[segment].Contains(symbol))
                    {
                        _symbols[segment].Add(symbol);
                    }
                }
            }

            return true;
        }

        int state = (((count * 211) + sum) * 2) + (used ? 1 : 0);
        if (_dead[state] == _generation || --budget < 0)
        {
            return false;
        }

        bool found = false;
        ReadOnlySpan<byte> finders = DataBarTables.ExpandedFinderSequences.Slice((length - 2) * 11, length);
        foreach ((int s, int o, List<int> segments) in _finders[finders[count] - 1])
        {
            DataBarExpandedReader.Block[] blocks = _segments[s].Blocks;
            int end = count + blocks.Length - o;
            bool uses = used || segments.Contains(required);
            if (end > length || (!uses && end == length)
                || DataBarExpandedReader.Sum(blocks.AsSpan(o), count, finders, symbolCharacters, sum) is not int next)
            {
                continue;
            }

            _assembly.AddRange(blocks.AsSpan(o));
            _path.Add((s, segments));
            found |= Extend(required, uses, length, symbolCharacters, next, ref budget);
            _path.RemoveAt(_path.Count - 1);
            _assembly.RemoveRange(count, _assembly.Count - count);
        }

        if (!found)
        {
            _dead[state] = _generation;
        }

        return found;
    }

    private static bool Same(ReadOnlySpan<DataBarExpandedReader.Block> a, ReadOnlySpan<DataBarExpandedReader.Block> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int k = 0; k < a.Length; k++)
        {
            if (!a[k].SameAs(b[k]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether two pairs read from their outer ends towards each other lie next to each other: lengths alike, directions
    /// opposite, and placed as on one row or as the two rows of a stacked form.
    /// </summary>
    private static bool Adjacent(Vector2 from, Vector2 to, Vector2 otherFrom, Vector2 otherTo)
    {
        Vector2 a = to - from, b = otherTo - otherFrom;
        float la = a.Length(), lb = b.Length();
        if (la <= 0 || lb <= 0 || la > 1.4f * lb || lb > 1.4f * la || Vector2.Dot(a, b) > -0.8f * la * lb)
        {
            return false;
        }

        // On one row the inner ends meet; stacked, both rows run left to right, so the right pair (read backwards, from its
        // right end) ends below the left pair's start.
        Vector2 along = a / la, gap = otherTo - to;
        float shift = Vector2.Dot(gap, along), across = MathF.Abs((gap.X * along.Y) - (gap.Y * along.X));
        return (MathF.Abs(shift) < 0.12f * la && across < 0.2f * la) || (MathF.Abs(shift + la) < 0.15f * la && across < 0.9f * la);
    }
}
