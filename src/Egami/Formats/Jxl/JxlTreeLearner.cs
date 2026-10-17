using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Jxl;

/// <summary>A node of an MA tree being built: a decision on a property (split on strictly greater) or a leaf.</summary>
internal struct JxlTreeNode
{
    internal int Property;
    internal int SplitValue;

    /// <summary>The child taken when the property is above the split value.</summary>
    internal int Left;

    internal int Right;
    internal int Predictor;

    /// <summary>A leaf's residual multiplier: 0 or 1 for none, else what the coded residuals are multiplied by.</summary>
    internal int Multiplier;

    internal static JxlTreeNode Leaf(int predictor) => new() { Property = -1, Predictor = predictor };

    internal static JxlTreeNode Split(int property, int value, int left) => new() { Property = property, SplitValue = value, Left = left, Right = left + 1 };
}

/// <summary>
/// Learns an MA tree from samples of the image (libjxl's TreeSamples and FindBestSplit, enc_ma.cc): each sample keeps the
/// residual token of every candidate predictor and the quantized properties, equal samples are kept once with a count, and a
/// node splits where the two halves cost fewer bits than the whole, the cheaper predictor taken on each side. Translated from
/// libjxl 0.12.0 (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlTreeLearner
{
    /// <summary>The property range quantization works in; values beyond it are clamped.</summary>
    private const int PropertyRange = 511;

    private const int StaticProperties = 2;

    private readonly int[] _predictors;
    private readonly int[] _properties;
    private readonly int _staticCount;
    private readonly byte[][] _tokens;
    private readonly byte[][] _nbits;
    private readonly int[][] _static;
    private readonly byte[][] _props;
    private ushort[] _counts;
    private int _n;
    private int[] _table = [];
    private int[][] _compact = [];
    private ushort[][] _staticMapping = [];
    private byte[][] _mapping = [];

    /// <summary>Creates a learner for the candidate predictors and the properties a split may test (static ones, 0 and 1, first).</summary>
    internal JxlTreeLearner(int[] predictors, int[] properties, int capacity)
    {
        _predictors = predictors;
        _properties = properties;
        _staticCount = properties.Count(p => p < StaticProperties);
        capacity = Math.Max(capacity, 64);
        _tokens = [.. predictors.Select(_ => new byte[capacity])];
        _nbits = [.. predictors.Select(_ => new byte[capacity])];
        _static = [.. Enumerable.Range(0, _staticCount).Select(_ => new int[capacity])];
        _props = [.. Enumerable.Range(0, properties.Length - _staticCount).Select(_ => new byte[capacity])];
        _counts = new ushort[capacity];
        Rehash(BitOperations.RoundUpToPowerOf2((uint)capacity * 2));
    }

    /// <summary>Gets the number of samples added, duplicates included.</summary>
    internal long SampleCount { get; private set; }

    internal static uint PackSigned(int v) => unchecked((uint)((v << 1) ^ (v >> 31)));

    /// <summary>
    /// Works out each property's split thresholds from the image (libjxl's PreQuantizeProperties): channel and stream by pixel
    /// counts, coordinates evenly, pixel values and differences by their sampled distribution, the WP error by a fixed list.
    /// </summary>
    internal void Quantize(List<long> groupPixels, List<long> channelPixels, List<int> pixelSamples, List<int> diffSamples, int maxValues)
    {
        _compact = new int[_properties.Length][];
        _staticMapping = new ushort[_staticCount][];
        _mapping = new byte[_properties.Length - _staticCount][];
        int[]? pixel = null;
        int[]? absPixel = null;
        int[]? diff = null;
        for (int i = 0; i < _properties.Length; i++)
        {
            int p = _properties[i];
            _compact[i] = p switch
            {
                0 => QuantizeHistogram(channelPixels, maxValues),
                1 => QuantizeHistogram(groupPixels, maxValues),
                2 or 3 => [.. Enumerable.Range(0, maxValues - 1).Select(k => ((k + 1) * 256 / maxValues) - 1)],
                6 or 7 or 8 => pixel ??= QuantizeSamples(pixelSamples, maxValues),
                4 or 5 => absPixel ??= QuantizeSamples([.. pixelSamples.Select(Math.Abs)], maxValues),
                15 => WpThresholds(maxValues),
                _ => diff ??= QuantizeSamples(diffSamples, maxValues),
            };

            if (i < _staticCount)
            {
                _staticMapping[i] = QuantMap<ushort>(_compact[i]);
            }
            else
            {
                _mapping[i - _staticCount] = QuantMap<byte>(_compact[i]);
            }
        }
    }

    /// <summary>
    /// Adds a sample: its value, its properties (all 16, indexed by property) and the predictions, indexed by predictor, of at
    /// least the learner's predictors.
    /// </summary>
    internal void Add(long pixel, ReadOnlySpan<int> properties, ReadOnlySpan<long> predictions)
    {
        if (_n == _counts.Length)
        {
            Grow();
        }

        int a = _n;
        for (int i = 0; i < _predictors.Length; i++)
        {
            uint v = PackSigned(unchecked((int)(pixel - predictions[_predictors[i]])));
            (byte tok, byte nbits) = ResidualToken(v);
            _tokens[i][a] = tok;
            _nbits[i][a] = nbits;
        }

        for (int i = 0; i < _staticCount; i++)
        {
            _static[i][a] = _staticMapping[i][Math.Clamp(properties[_properties[i]], -PropertyRange, PropertyRange) + PropertyRange];
        }

        for (int i = _staticCount; i < _properties.Length; i++)
        {
            _props[i - _staticCount][a] = _mapping[i - _staticCount][Math.Clamp(properties[_properties[i]], -PropertyRange, PropertyRange) + PropertyRange];
        }

        SampleCount++;
        int mask = _table.Length - 1;
        int slot = Hash(a) & mask;
        while (true)
        {
            int e = _table[slot];
            if (e == -1)
            {
                break;
            }

            if (e >= 0 && Same(a, e))
            {
                // A duplicate: counted on the sample kept, which leaves the table once its count saturates.
                if (++_counts[e] == ushort.MaxValue)
                {
                    _table[slot] = -2;
                }

                return;
            }

            slot = (slot + 1) & mask;
        }

        _table[slot] = a;
        _counts[a] = 1;
        _n++;
        if (_n * 2 > _table.Length)
        {
            Rehash(_table.Length * 2);
        }
    }

    /// <summary>
    /// Learns the tree: a leaf with the first predictor, split greedily while a split saves more than
    /// <paramref name="threshold"/> bits (libjxl's ComputeBestTree and FindBestSplit).
    /// </summary>
    internal List<JxlTreeNode> Learn(float threshold, float fastDecodeMultiplier, int parallelism)
    {
        List<JxlTreeNode> tree = [JxlTreeNode.Leaf(_predictors[0])];
        _table = [];
        if (_n == 0)
        {
            return tree;
        }

        FindBestSplit(tree, threshold, fastDecodeMultiplier, parallelism);
        return tree;
    }

    /// <summary>The residual's token and raw bit count in the 4, 1, 2 hybrid integer configuration the learner prices with.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (byte Token, byte NBits) ResidualToken(uint v)
    {
        if (v < 16)
        {
            return ((byte)v, 0);
        }

        int n = BitOperations.Log2(v);
        uint m = v - (1u << n);
        return ((byte)(16 + ((n - 4) << 3) + ((m >> (n - 1)) << 2) + (m & 3)), (byte)(n - 3));
    }

    private static int[] WpThresholds(int maxValues) => maxValues < 32
        ? [-127, -63, -31, -15, -7, -3, -1, 0, 1, 3, 7, 15, 31, 63, 127]
        : maxValues < 64
            ? [-255, -191, -127, -95, -63, -47, -31, -23, -15, -11, -7, -5, -3, -1, 0, 1, 3, 5, 7, 11, 15, 23, 31, 47, 63, 95, 127, 191, 255]
            : [-255, -223, -191, -159, -127, -111, -95, -79, -63, -55, -47, -39, -31, -27, -23, -19, -15, -13, -11, -9, -7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5,
                6, 7, 9, 11, 13, 15, 19, 23, 27, 31, 39, 47, 55, 63, 79, 95, 111, 127, 159, 191, 223, 255];

    /// <summary>Picks thresholds at even quantiles of a histogram; the last bucket holds the rest and is not a threshold.</summary>
    private static int[] QuantizeHistogram(List<long> histogram, int chunks)
    {
        if (histogram.Count == 0 || chunks == 0)
        {
            return [];
        }

        long sum = histogram.Sum();
        if (sum == 0)
        {
            return [];
        }

        List<int> thresholds = [];
        long cumsum = 0;
        long threshold = 1;
        for (int i = 0; i < histogram.Count; i++)
        {
            cumsum += histogram[i];
            if (cumsum * chunks >= threshold * sum)
            {
                thresholds.Add(i);
                while (cumsum * chunks >= threshold * sum)
                {
                    threshold++;
                }
            }
        }

        thresholds.RemoveAt(thresholds.Count - 1);
        return [.. thresholds];
    }

    private static int[] QuantizeSamples(List<int> samples, int chunks)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        const int Range = 512;
        int min = Math.Clamp(samples.Min(), -Range, Range);
        List<long> counts = [.. new long[(2 * Range) + 1]];
        foreach (int s in samples)
        {
            counts[Math.Clamp(s, -Range, Range) - min]++;
        }

        int[] thresholds = QuantizeHistogram(counts, chunks);
        for (int i = 0; i < thresholds.Length; i++)
        {
            thresholds[i] += min;
        }

        return thresholds;
    }

    /// <summary>Maps each property value in ±511 to the number of thresholds it is above (the tree splits on value &gt; threshold).</summary>
    private static T[] QuantMap<T>(int[] from)
        where T : INumber<T>
    {
        const int Pegs = (PropertyRange * 2) + 1;
        T[] to = new T[Pegs];
        int mapped = 0;
        for (int i = 0; i < Pegs; i++)
        {
            while (mapped < from.Length && i - PropertyRange > from[mapped])
            {
                mapped++;
            }

            to[i] = T.CreateTruncating(mapped);
        }

        return to;
    }

    /// <summary>
    /// Gets the entropy of the counts, every used symbol at least 1/4096 likely and a symbol that is all of them free (libjxl's
    /// EstimateBits, with its fast logarithm); the length is a multiple of 8.
    /// </summary>
    private static float EstimateBits(ReadOnlySpan<int> counts)
    {
        int total = 0;
        foreach (int c in counts)
        {
            total += c;
        }

        return EstimateBits(counts, total);
    }

    /// <summary>As <see cref="EstimateBits(ReadOnlySpan{int})"/>, with the counts' total known.</summary>
    private static float EstimateBits(ReadOnlySpan<int> counts, int total)
    {
        if (total == 0)
        {
            return 0;
        }

        const float MinProb = 1f / 4096;
        float inv = 1f / total;
        if (JxlFastMath.Vectorized)
        {
            ref int first = ref MemoryMarshal.GetReference(counts);
            Vector256<float> vInv = Vector256.Create(inv);
            Vector256<float> vMin = Vector256.Create(MinProb);
            Vector256<int> vTotal = Vector256.Create(total);
            Vector256<float> acc = Vector256<float>.Zero;
            for (int i = 0; i < counts.Length; i += 8)
            {
                Vector256<int> ci = Vector256.LoadUnsafe(ref first, (nuint)i);
                Vector256<float> cf = Vector256.ConvertToSingle(ci);
                Vector256<float> log = JxlFastMath.Log2(Vector256.Max(cf * vInv, vMin));
                log = Vector256.AndNot(log, Vector256.Equals(ci, vTotal).AsSingle());
                acc -= cf * log;
            }

            return Vector256.Sum(acc);
        }

        float bits = 0;
        foreach (int c in counts)
        {
            if (c != total)
            {
                bits -= c * JxlFastMath.Log2(MathF.Max(c * inv, MinProb));
            }
        }

        return bits;
    }

    private static int Padded(int x) => (x + 7) & ~7;

    /// <summary>Moves one property value's counts from the above side to the below side; the lengths are multiples of 8.</summary>
    private static void Move(ReadOnlySpan<int> inc, Span<int> above, Span<int> below)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            ref int ri = ref MemoryMarshal.GetReference(inc);
            ref int ra = ref MemoryMarshal.GetReference(above);
            ref int rb = ref MemoryMarshal.GetReference(below);
            for (int s = 0; s < inc.Length; s += 8)
            {
                Vector256<int> v = Vector256.LoadUnsafe(ref ri, (nuint)s);
                (Vector256.LoadUnsafe(ref ra, (nuint)s) - v).StoreUnsafe(ref ra, (nuint)s);
                (Vector256.LoadUnsafe(ref rb, (nuint)s) + v).StoreUnsafe(ref rb, (nuint)s);
            }

            return;
        }

        for (int s = 0; s < inc.Length; s++)
        {
            above[s] -= inc[s];
            below[s] += inc[s];
        }
    }

    private void Grow()
    {
        int size = _counts.Length * 2;
        for (int i = 0; i < _tokens.Length; i++)
        {
            Array.Resize(ref _tokens[i], size);
            Array.Resize(ref _nbits[i], size);
        }

        for (int i = 0; i < _static.Length; i++)
        {
            Array.Resize(ref _static[i], size);
        }

        for (int i = 0; i < _props.Length; i++)
        {
            Array.Resize(ref _props[i], size);
        }

        Array.Resize(ref _counts, size);
    }

    private void Rehash(long size)
    {
        _table = new int[size];
        Array.Fill(_table, -1);
        int mask = _table.Length - 1;
        for (int a = 0; a < _n; a++)
        {
            if (_counts[a] == ushort.MaxValue)
            {
                continue;
            }

            int slot = Hash(a) & mask;
            while (_table[slot] != -1)
            {
                slot = (slot + 1) & mask;
            }

            _table[slot] = a;
        }
    }

    private int Hash(int a)
    {
        ulong h = 0x1e35a7bd;
        for (int i = 0; i < _tokens.Length; i++)
        {
            h = (h * 0x1e35a7bd) + _tokens[i][a];
            h = (h * 0x1e35a7bd) + _nbits[i][a];
        }

        foreach (int[] s in _static)
        {
            h = (h * 0x1e35a7bd) + (uint)s[a];
        }

        foreach (byte[] p in _props)
        {
            h = (h * 0x1e35a7bd) + p[a];
        }

        return (int)(h >> 16);
    }

    private bool Same(int a, int b)
    {
        for (int i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i][a] != _tokens[i][b] || _nbits[i][a] != _nbits[i][b])
            {
                return false;
            }
        }

        foreach (int[] s in _static)
        {
            if (s[a] != s[b])
            {
                return false;
            }
        }

        foreach (byte[] p in _props)
        {
            if (p[a] != p[b])
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Property(int index, int sample) => index < _staticCount ? _static[index][sample] : _props[index - _staticCount][sample];

    private void Swap(int a, int b)
    {
        if (a == b)
        {
            return;
        }

        for (int i = 0; i < _tokens.Length; i++)
        {
            (_tokens[i][a], _tokens[i][b]) = (_tokens[i][b], _tokens[i][a]);
            (_nbits[i][a], _nbits[i][b]) = (_nbits[i][b], _nbits[i][a]);
        }

        foreach (int[] s in _static)
        {
            (s[a], s[b]) = (s[b], s[a]);
        }

        foreach (byte[] p in _props)
        {
            (p[a], p[b]) = (p[b], p[a]);
        }

        (_counts[a], _counts[b]) = (_counts[b], _counts[a]);
    }

    /// <summary>Moves the samples of [begin, end) whose property is at most <paramref name="value"/> before those above it.</summary>
    private void SplitSamples(int begin, int pos, int end, int prop, int value)
    {
        int b = begin;
        int e = pos;
        do
        {
            while (b < pos && Property(prop, b) <= value)
            {
                b++;
            }

            while (e < end && Property(prop, e) > value)
            {
                e++;
            }

            if (b < pos && e < end)
            {
                Swap(b, e);
            }

            b++;
            e++;
        }
        while (b < pos && e < end);
    }

    private void FindBestSplit(List<JxlTreeNode> tree, float threshold, float fastDecodeMultiplier, int parallelism)
    {
        Stack<(int Pos, int Begin, int End)> nodes = new();
        nodes.Push((0, 0, _n));
        int predictors = _predictors.Length;
        int properties = _properties.Length;
        float changePenalty = 800f / (100f + threshold);
        int maxValues = _compact.Max(c => c.Length) + 1;
        Candidates[] found = new Candidates[properties];
        Scratch[] scratch = [.. Enumerable.Range(0, properties).Select(_ => new Scratch(maxValues, predictors))];
        while (nodes.Count > 0)
        {
            (int pos, int begin, int end) = nodes.Pop();
            if (begin == end)
            {
                continue;
            }

            int maxSymbols = 0;
            for (int pr = 0; pr < predictors; pr++)
            {
                byte[] toks = _tokens[pr];
                for (int i = begin; i < end; i++)
                {
                    maxSymbols = Math.Max(maxSymbols, toks[i] + 1);
                }
            }

            maxSymbols = Padded(maxSymbols);
            int[] counts = new int[maxSymbols * predictors];
            long[] totalExtra = new long[predictors];
            int nodeTotal = 0;
            for (int i = begin; i < end; i++)
            {
                nodeTotal += _counts[i];
            }

            for (int pr = 0; pr < predictors; pr++)
            {
                byte[] toks = _tokens[pr];
                byte[] nb = _nbits[pr];
                long extra = 0;
                int o = pr * maxSymbols;
                for (int i = begin; i < end; i++)
                {
                    int c = _counts[i];
                    counts[o + toks[i]] += c;
                    extra += nb[i] * c;
                }

                totalExtra[pr] = extra;
            }

            int nodePredictor = Array.IndexOf(_predictors, tree[pos].Predictor);
            float baseBits = EstimateBits(counts.AsSpan(nodePredictor * maxSymbols, maxSymbols), nodeTotal) + totalExtra[nodePredictor];
            Array.Fill(found, Candidates.None);
            if (baseBits > threshold)
            {
                NodeScan scan = new(begin, end, maxSymbols, maxValues, nodeTotal, counts, totalExtra, tree[pos].Predictor, changePenalty);

                // Each property is priced on its own; large nodes price them in parallel, and the results combine in order.
                if (parallelism > 1 && end - begin >= 4096)
                {
                    Parallel.For(0, properties, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, prop => found[prop] = ScanProperty(prop, scan, scratch[prop]));
                }
                else
                {
                    for (int prop = 0; prop < properties; prop++)
                    {
                        found[prop] = ScanProperty(prop, scan, scratch[prop]);
                    }

                }
            }

            Split staticConstant = Split.None;
            Split staticSplit = Split.None;
            Split nonStatic = Split.None;
            Split noWp = Split.None;
            foreach (Candidates c in found)
            {
                staticConstant = Better(staticConstant, c.StaticConstant);
                staticSplit = Better(staticSplit, c.Static);
                nonStatic = Better(nonStatic, c.NonStatic);
                noWp = Better(noWp, c.NoWp);
            }

            Split chosen = nonStatic;
            if (noWp.Cost + threshold < baseBits && noWp.Cost <= fastDecodeMultiplier * chosen.Cost)
            {
                chosen = noWp;
            }

            if (staticSplit.Cost + threshold < baseBits && staticSplit.Cost <= fastDecodeMultiplier * chosen.Cost)
            {
                chosen = staticSplit;
            }

            if (staticConstant.Cost + threshold < baseBits)
            {
                chosen = staticConstant;
            }

            if (chosen.Cost + threshold >= baseBits)
            {
                continue;
            }

            int left = tree.Count;
            JxlTreeNode node = tree[pos];
            node.Property = _properties[chosen.Prop];
            node.SplitValue = _compact[chosen.Prop][chosen.Value];
            node.Left = left;
            node.Right = left + 1;
            tree[pos] = node;

            // The left child takes the samples above the split, coded with the right side's predictor in libjxl's naming.
            tree.Add(JxlTreeNode.Leaf(chosen.RightPredictor));
            tree.Add(JxlTreeNode.Leaf(chosen.LeftPredictor));
            SplitSamples(begin, chosen.Pos, end, chosen.Prop, chosen.Value);
            nodes.Push((left + 1, begin, chosen.Pos));
            nodes.Push((left, chosen.Pos, end));
        }
    }

    /// <summary>
    /// Prices every split of a node on one property: for each predictor, the entropy of the samples at or below each used value
    /// and of those above it, moving one value's counts across at a time (libjxl's inner loop of FindBestSplit).
    /// </summary>
    private Candidates ScanProperty(int prop, NodeScan n, Scratch scratch)
    {
        int predictors = _predictors.Length;
        int size = _compact[prop].Length + 1;
        int maxSymbols = n.MaxSymbols;
        int[] used = scratch.Used;
        int[] valueTotal = scratch.ValueTotal;
        int[] countIncrease = scratch.CountIncrease;
        int[] countsAbove = scratch.CountsAbove;
        int[] countsBelow = scratch.CountsBelow;
        long[] extraIncrease = scratch.ExtraIncrease;
        (float Cost, float Extra, int Predictor)[] costsL = scratch.CostsL;
        (float Cost, float Extra, int Predictor)[] costsR = scratch.CostsR;
        {
            Array.Clear(used, 0, size);
            Array.Clear(valueTotal, 0, size);
            int first = size;
            int last = 0;
            if (prop < _staticCount)
            {
                Count(_static[prop], n.Begin, n.End, used, valueTotal, ref first, ref last);
            }
            else
            {
                Count(_props[prop - _staticCount], n.Begin, n.End, used, valueTotal, ref first, ref last);
            }

            if (last <= first)
            {
                return Candidates.None;
            }

            // Only the values that occur need clearing.
            for (int pr = 0; pr < predictors; pr++)
            {
                Array.Clear(countIncrease, ((pr * size) + first) * maxSymbols, (last - first + 1) * maxSymbols);
                Array.Clear(extraIncrease, (pr * size) + first, last - first + 1);
            }

            if (prop < _staticCount)
            {
                Gather(_static[prop], n.Begin, n.End, countIncrease, extraIncrease, size, maxSymbols);
            }
            else
            {
                Gather(_props[prop - _staticCount], n.Begin, n.End, countIncrease, extraIncrease, size, maxSymbols);
            }

            Array.Fill(costsL, (float.MaxValue, 0f, 0), 0, last - first);
            Array.Fill(costsR, (float.MaxValue, 0f, 0), 0, last - first);
            for (int pr = 0; pr < predictors; pr++)
            {
                Array.Copy(n.Counts, pr * maxSymbols, countsAbove, 0, maxSymbols);
                Array.Clear(countsBelow, 0, maxSymbols);
                long extraBelow = 0;
                int totalBelow = 0;
                int predictor = _predictors[pr];
                float penalty = 0;

                // Never discourage moving away from the weighted predictor; between equals, prefer Zero and avoid Weighted.
                if (predictor != n.NodePredictor && n.NodePredictor != 6)
                {
                    penalty = n.ChangePenalty;
                }

                if (predictor == 6)
                {
                    penalty += 1e-8f;
                }

                if (predictor == 0)
                {
                    penalty -= 1e-8f;
                }

                // Up to the last used value, so neither side is empty.
                int baseIndex = pr * size;
                for (int i = first; i < last; i++)
                {
                    if (used[i] == 0)
                    {
                        continue;
                    }

                    extraBelow += extraIncrease[baseIndex + i];
                    totalBelow += valueTotal[i];
                    Move(countIncrease.AsSpan((baseIndex + i) * maxSymbols, maxSymbols), countsAbove.AsSpan(0, maxSymbols), countsBelow.AsSpan(0, maxSymbols));
                    float rcost = EstimateBits(countsAbove.AsSpan(0, maxSymbols), n.Total - totalBelow) + n.TotalExtra[pr] - extraBelow;
                    float lcost = EstimateBits(countsBelow.AsSpan(0, maxSymbols), totalBelow) + extraBelow;
                    int k = i - first;
                    if (rcost + penalty < costsR[k].Cost + costsR[k].Extra)
                    {
                        costsR[k] = (rcost, penalty, predictor);
                    }

                    if (lcost + penalty < costsL[k].Cost + costsL[k].Extra)
                    {
                        costsL[k] = (lcost, penalty, predictor);
                    }
                }
            }

            Candidates result = Candidates.None;
            int split = n.Begin;
            for (int i = first; i < last; i++)
            {
                if (used[i] == 0)
                {
                    continue;
                }

                split += used[i];
                int k = i - first;
                float rcost = costsR[k].Cost;
                float lcost = costsL[k].Cost;
                bool usesWp = _properties[prop] == 15 || costsL[k].Predictor == 6 || costsR[k].Predictor == 6;
                bool zeroEntropySide = rcost == 0 || lcost == 0;
                Split candidate = new(prop, i, split, lcost, rcost, costsL[k].Predictor, costsR[k].Predictor);
                if (_properties[prop] < StaticProperties)
                {
                    if (zeroEntropySide)
                    {
                        result.StaticConstant = Better(result.StaticConstant, candidate);
                    }
                    else
                    {
                        result.Static = Better(result.Static, candidate);
                    }
                }
                else if (usesWp)
                {
                    result.NonStatic = Better(result.NonStatic, candidate);
                }
                else
                {
                    result.NoWp = Better(result.NoWp, candidate);
                }
            }

            return result;
        }
    }

    /// <summary>One pass over a node's samples for one property: which values occur and their counts.</summary>
    private void Count<T>(T[] values, int begin, int end, int[] used, int[] valueTotal, ref int first, ref int last)
        where T : IBinaryInteger<T>
    {
        ushort[] counts = _counts;
        int lo = first;
        int hi = last;
        for (int i = begin; i < end; i++)
        {
            int p = int.CreateTruncating(values[i]);
            used[p]++;
            valueTotal[p] += counts[i];
            lo = Math.Min(lo, p);
            hi = Math.Max(hi, p);
        }

        first = lo;
        last = hi;
    }

    /// <summary>A second pass: each predictor's token counts and raw bits per property value.</summary>
    private void Gather<T>(T[] values, int begin, int end, int[] countIncrease, long[] extraIncrease, int size, int maxSymbols)
        where T : IBinaryInteger<T>
    {
        int predictors = _predictors.Length;
        ushort[] counts = _counts;
        if (predictors == 2)
        {
            byte[] t0 = _tokens[0];
            byte[] t1 = _tokens[1];
            byte[] n0 = _nbits[0];
            byte[] n1 = _nbits[1];
            int second = size * maxSymbols;
            for (int i = begin; i < end; i++)
            {
                int p = int.CreateTruncating(values[i]);
                int c = counts[i];
                countIncrease[(p * maxSymbols) + t0[i]] += c;
                countIncrease[second + (p * maxSymbols) + t1[i]] += c;
                extraIncrease[p] += n0[i] * c;
                extraIncrease[size + p] += n1[i] * c;
            }

            return;
        }

        for (int i = begin; i < end; i++)
        {
            int p = int.CreateTruncating(values[i]);
            int c = counts[i];
            for (int pr = 0; pr < predictors; pr++)
            {
                countIncrease[(((pr * size) + p) * maxSymbols) + _tokens[pr][i]] += c;
                extraIncrease[(pr * size) + p] += _nbits[pr][i] * c;
            }
        }
    }

    private static Split Better(Split best, Split candidate) => candidate.Cost < best.Cost ? candidate : best;

    private readonly record struct Split(int Prop, int Value, int Pos, float LeftCost, float RightCost, int LeftPredictor, int RightPredictor)
    {
        internal static Split None => new(0, 0, 0, float.MaxValue, float.MaxValue, 0, 0);

        internal float Cost => LeftCost + RightCost;
    }

    /// <summary>The best split of each kind one property gives.</summary>
    private struct Candidates
    {
        internal Split StaticConstant;
        internal Split Static;
        internal Split NonStatic;
        internal Split NoWp;

        internal static Candidates None => new() { StaticConstant = Split.None, Static = Split.None, NonStatic = Split.None, NoWp = Split.None };
    }

    /// <summary>One property scan's working arrays, sized for any node (tokens are below 256).</summary>
    private sealed class Scratch(int values, int predictors)
    {
        internal int[] Used { get; } = new int[values];

        internal int[] ValueTotal { get; } = new int[values];

        internal int[] CountIncrease { get; } = new int[predictors * values * 256];

        internal int[] CountsAbove { get; } = new int[256];

        internal int[] CountsBelow { get; } = new int[256];

        internal long[] ExtraIncrease { get; } = new long[predictors * values];

        internal (float Cost, float Extra, int Predictor)[] CostsL { get; } = new (float, float, int)[values];

        internal (float Cost, float Extra, int Predictor)[] CostsR { get; } = new (float, float, int)[values];
    }

    /// <summary>What every property's scan of one node shares.</summary>
    private sealed record NodeScan(int Begin, int End, int MaxSymbols, int MaxValues, int Total, int[] Counts, long[] TotalExtra, int NodePredictor, float ChangePenalty);
}
