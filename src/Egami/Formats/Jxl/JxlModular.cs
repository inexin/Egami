using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jxl;

/// <summary>A Modular channel: 32-bit samples, row by row, with its shifts against the image (-1 for a palette's meta channel).</summary>
internal sealed class JxlChannel
{
    internal JxlChannel(int width, int height, int hshift, int vshift)
    {
        Width = width;
        Height = height;
        HShift = hshift;
        VShift = vshift;
        Data = new int[(long)width * height];
    }

    internal int Width { get; }

    internal int Height { get; }

    internal int HShift { get; }

    internal int VShift { get; }

    internal int[] Data { get; set; }

    internal Span<int> Row(int y) => Data.AsSpan(y * Width, Width);

    internal bool SameShape(JxlChannel other) => Width == other.Width && Height == other.Height && HShift == other.HShift && VShift == other.VShift;
}

/// <summary>The self-correcting (weighted) predictor's parameters (H.5.1).</summary>
internal sealed record JxlWpParams(int P1 = 16, int P2 = 10, int P3a = 7, int P3b = 7, int P3c = 7, int P3d = 0, int P3e = 0, int W0 = 13, int W1 = 12, int W2 = 12, int W3 = 12)
{
    internal static JxlWpParams Read(JxlBitReader r)
    {
        if (r.ReadBool())
        {
            return new JxlWpParams();
        }

        return new JxlWpParams(
            (int)r.ReadBits(5), (int)r.ReadBits(5), (int)r.ReadBits(5), (int)r.ReadBits(5), (int)r.ReadBits(5), (int)r.ReadBits(5), (int)r.ReadBits(5),
            (int)r.ReadBits(4), (int)r.ReadBits(4), (int)r.ReadBits(4), (int)r.ReadBits(4));
    }
}

/// <summary>A meta-adaptive context tree (H.4.2) with the distributions of its leaves.</summary>
internal sealed class JxlTree
{
    // Flat nodes in breadth-first order: a decision has a property (0 or more), a split value and two children; a leaf has
    // property -1 and its context's cluster, predictor, offset and multiplier.
    private readonly int[] _property;
    private readonly int[] _value;
    private readonly int[] _left;
    private readonly int[] _right;
    private readonly int[] _predictor;
    private readonly int[] _offset;
    private readonly int[] _multiplier;
    private readonly int[] _cluster;
    private JxlLeaf[]? _leaves;
    private JxlChannelPlan?[]? _plans;

    private JxlTree(int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] offset, int[] multiplier, int[] cluster, JxlEntropyDecoder decoder)
    {
        _property = property;
        _value = value;
        _left = left;
        _right = right;
        _predictor = predictor;
        _offset = offset;
        _multiplier = multiplier;
        _cluster = cluster;
        Decoder = decoder;
        for (int i = 0; i < property.Length; i++)
        {
            MaxProperty = Math.Max(MaxProperty, property[i]);
            UsesWp |= property[i] == 15 || (property[i] < 0 && predictor[i] == 6);
        }
    }

    /// <summary>Gets the distributions of the leaves; each stream decodes with its own copy of the state.</summary>
    internal JxlEntropyDecoder Decoder { get; }

    /// <summary>Gets the highest property the tree tests, -1 for a single leaf.</summary>
    internal int MaxProperty { get; } = -1;

    /// <summary>Gets whether the self-correcting predictor's state is needed.</summary>
    internal bool UsesWp { get; }

    internal int NodeCount => _property.Length;

    /// <summary>Reads a tree and its leaf distributions, at most <paramref name="nodeLimit"/> nodes.</summary>
    internal static JxlTree Read(JxlBitReader r, long nodeLimit)
    {
        JxlEntropyDecoder treeDecoder = JxlEntropyDecoder.Read(r, 6);
        long single = treeDecoder.SingleToken(treeDecoder.Clusters[1]);
        if (single > 0)
        {
            throw r.Corrupt("The MA tree's distributions make an infinite tree.");
        }

        List<int> property = [];
        List<int> value = [];
        List<int> left = [];
        List<int> right = [];
        List<int> predictor = [];
        List<int> offset = [];
        List<int> multiplier = [];
        List<int> context = [];
        treeDecoder.Begin(r);
        int toDecode = 1;
        int leaves = 0;
        while (toDecode > 0)
        {
            if (property.Count > nodeLimit)
            {
                throw r.Corrupt($"The MA tree has more than {nodeLimit} nodes.");
            }

            toDecode--;
            uint p = treeDecoder.ReadUint(r, 1);
            if (p == 0)
            {
                uint pred = treeDecoder.ReadUint(r, 2);
                if (pred > 13)
                {
                    throw r.Corrupt($"Predictor {pred} is unknown.");
                }

                int off = JxlHeaders.UnpackSigned(treeDecoder.ReadUint(r, 3));
                uint mulLog = treeDecoder.ReadUint(r, 4);
                if (mulLog > 30)
                {
                    throw r.Corrupt("An MA tree leaf's multiplier is too large.");
                }

                uint mulBits = treeDecoder.ReadUint(r, 5);
                if (mulBits > (1u << (int)(31 - mulLog)) - 2)
                {
                    throw r.Corrupt("An MA tree leaf's multiplier is too large.");
                }

                property.Add(-1);
                value.Add(0);
                left.Add(0);
                right.Add(0);
                predictor.Add((int)pred);
                offset.Add(off);
                multiplier.Add((int)((mulBits + 1) << (int)mulLog));
                context.Add(leaves++);
                continue;
            }

            if (p - 1 > 255)
            {
                throw r.Corrupt($"MA tree property {p - 1} is out of range.");
            }

            int split = JxlHeaders.UnpackSigned(treeDecoder.ReadUint(r, 0));
            property.Add((int)(p - 1));
            value.Add(split);
            left.Add(property.Count + toDecode);
            right.Add(property.Count + toDecode + 1);
            predictor.Add(0);
            offset.Add(0);
            multiplier.Add(0);
            context.Add(-1);
            toDecode += 2;
        }

        treeDecoder.Finish(r);
        JxlEntropyDecoder decoder = JxlEntropyDecoder.Read(r, leaves);
        int[] cluster = new int[context.Count];
        for (int i = 0; i < cluster.Length; i++)
        {
            cluster[i] = context[i] >= 0 ? decoder.Clusters[context[i]] : -1;
        }

        JxlTree tree = new([.. property], [.. value], [.. left], [.. right], [.. predictor], [.. offset], [.. multiplier], cluster, decoder);
        if (tree.Depth() > 2048)
        {
            throw r.Corrupt("The MA tree is deeper than 2048.");
        }

        return tree;
    }

    /// <summary>Finds the leaf for the properties; properties 0 and 1 are the channel and stream indices.</summary>
    internal int Leaf(ReadOnlySpan<int> properties)
    {
        int node = 0;
        while (true)
        {
            int p = _property[node];
            if (p < 0)
            {
                return node;
            }

            node = properties[p] > _value[node] ? _left[node] : _right[node];
        }
    }

    /// <summary>Resolves the decisions on properties 0 and 1 (channel and stream) for one channel, giving the subtree root.</summary>
    internal int Root(int channel, int stream)
    {
        int node = 0;
        while (_property[node] is 0 or 1)
        {
            int v = _property[node] == 0 ? channel : stream;
            node = v > _value[node] ? _left[node] : _right[node];
        }

        return node;
    }

    internal int LeafFrom(int node, ReadOnlySpan<int> properties)
    {
        while (true)
        {
            int p = _property[node];
            if (p < 0)
            {
                return node;
            }

            node = properties[p] > _value[node] ? _left[node] : _right[node];
        }
    }

    internal bool IsLeaf(int node) => _property[node] < 0;

    /// <summary>Gets each node's property (−1 for a leaf), for decoders that walk the tree themselves.</summary>
    internal int[] Properties => _property;

    /// <summary>Gets each decision node's split value.</summary>
    internal int[] Values => _value;

    /// <summary>Gets each decision node's child taken when the property is above the value.</summary>
    internal int[] Lefts => _left;

    /// <summary>Gets each decision node's other child.</summary>
    internal int[] Rights => _right;

    /// <summary>Gets each node's leaf data in one entry (zero for a decision), for decoders that walk the tree themselves.</summary>
    internal JxlLeaf[] Leaves => _leaves ??= BuildLeaves();

    /// <summary>
    /// Gets how to decode a channel whose subtree starts at <paramref name="root"/>: worked out once per root and shared by
    /// every group and thread (two threads may both build it; the results are the same).
    /// </summary>
    internal JxlChannelPlan Plan(int root)
    {
        JxlChannelPlan?[] plans = _plans ??= new JxlChannelPlan?[_property.Length];
        return plans[root] ??= JxlChannelPlan.Build(this, root);
    }

    internal int Predictor(int leaf) => _predictor[leaf];

    internal int Offset(int leaf) => _offset[leaf];

    internal int Multiplier(int leaf) => _multiplier[leaf];

    internal int Cluster(int leaf) => _cluster[leaf];

    private JxlLeaf[] BuildLeaves()
    {
        JxlLeaf[] leaves = new JxlLeaf[_property.Length];
        for (int i = 0; i < leaves.Length; i++)
        {
            if (_property[i] < 0)
            {
                leaves[i] = new JxlLeaf(_predictor[i], _cluster[i], _multiplier[i], _offset[i]);
            }
        }

        return leaves;
    }

    private int Depth()
    {
        int[] depth = new int[_property.Length];
        int max = 0;
        for (int i = 0; i < _property.Length; i++)
        {
            max = Math.Max(max, depth[i]);
            if (_property[i] >= 0)
            {
                depth[_left[i]] = depth[i] + 1;
                depth[_right[i]] = depth[i] + 1;
            }
        }

        return max;
    }
}

/// <summary>An MA tree leaf: its predictor, its context's cluster, and the multiplier and offset applied to each residual.</summary>
internal readonly record struct JxlLeaf(int Predictor, int Cluster, int Multiplier, int Offset);

/// <summary>How a channel's subtree is decoded: the shape of its tree after the channel and stream decisions (H.4.2).</summary>
internal enum JxlChannelPlanKind
{
    /// <summary>One leaf for every sample, so no tree walk.</summary>
    Single,

    /// <summary>Every decision tests one property, so a table indexed by its clamped value gives the leaf.</summary>
    Lookup,

    /// <summary>Any other tree, walked per sample.</summary>
    Walk,
}

/// <summary>
/// A step of a flattened tree walk: a decision and both its children's decisions, so one step picks one of four nodes two
/// levels down without a branch on the first result (libjxl's flat decision nodes). A child that is a leaf tests
/// property 0 against <see cref="int.MaxValue"/>, which always fails, and both its slots hold the leaf.
/// </summary>
internal readonly record struct JxlFlatNode(int Property, int Value, int LeftProperty, int LeftValue, int RightProperty, int RightValue, int Child, int Leaf);

/// <summary>
/// What a channel's subtree needs, worked out once per root: libjxl's decoder specializes the same shapes (a single leaf,
/// and a tree on one property through a lookup table), and the weighted predictor runs only when a node or leaf uses it.
/// </summary>
internal sealed class JxlChannelPlan
{
    // A lookup table covers at most this many property values.
    private const int MaxLookup = 4096;

    internal JxlChannelPlanKind Kind { get; private init; }

    /// <summary>Gets the leaf of a <see cref="JxlChannelPlanKind.Single"/> plan.</summary>
    internal JxlLeaf Leaf { get; private init; }

    /// <summary>Gets the property a <see cref="JxlChannelPlanKind.Lookup"/> plan tests.</summary>
    internal int Property { get; private init; }

    /// <summary>Gets the lowest property value the table tells apart; lower values take the same leaf.</summary>
    internal int Min { get; private init; }

    /// <summary>Gets the highest property value the table tells apart; higher values take the same leaf.</summary>
    internal int Max { get; private init; }

    /// <summary>Gets the leaf node for each property value from <see cref="Min"/> to <see cref="Max"/>.</summary>
    internal int[]? Lookup { get; private init; }

    /// <summary>Gets a <see cref="JxlChannelPlanKind.Walk"/> plan's flattened subtree, its first step at index 0.</summary>
    internal JxlFlatNode[]? Flat { get; private init; }

    /// <summary>Gets whether the subtree tests the max-error property or has a weighted-predictor leaf.</summary>
    internal bool UsesWp { get; private init; }

    /// <summary>Gets whether the subtree tests a property of a previous channel (16 and up).</summary>
    internal bool UsesPrevious { get; private init; }

    /// <summary>Gets the predictor every leaf of the subtree uses, or -1 when they differ.</summary>
    internal int Predictor { get; private init; }

    internal static JxlChannelPlan Build(JxlTree tree, int root)
    {
        int[] property = tree.Properties;
        int[] value = tree.Values;
        if (property[root] < 0)
        {
            JxlLeaf only = tree.Leaves[root];
            return new JxlChannelPlan { Kind = JxlChannelPlanKind.Single, Leaf = only, UsesWp = only.Predictor == 6, Predictor = only.Predictor };
        }

        bool usesWp = false;
        bool usesPrevious = false;
        int predictor = -2;
        int tested = property[root];
        bool oneProperty = tested < 16;
        long min = long.MaxValue;
        long max = long.MinValue;
        Stack<int> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            int node = pending.Pop();
            int p = property[node];
            if (p < 0)
            {
                usesWp |= tree.Predictor(node) == 6;
                predictor = predictor == -2 || predictor == tree.Predictor(node) ? tree.Predictor(node) : -1;
                continue;
            }

            usesWp |= p == 15;
            usesPrevious |= p >= 16;
            oneProperty &= p == tested;
            min = Math.Min(min, value[node]);
            max = Math.Max(max, value[node]);
            pending.Push(tree.Lefts[node]);
            pending.Push(tree.Rights[node]);
        }

        // A value above every split goes the way max + 1 does, and one at or below every split the way min does.
        if (!oneProperty || max == int.MaxValue || max + 1 - min + 1 > MaxLookup)
        {
            return new JxlChannelPlan { Kind = JxlChannelPlanKind.Walk, Flat = Flatten(tree, root), UsesWp = usesWp, UsesPrevious = usesPrevious, Predictor = predictor };
        }

        int[] lookup = new int[(int)(max + 1 - min + 1)];
        for (int i = 0; i < lookup.Length; i++)
        {
            long v = min + i;
            int node = root;
            while (property[node] >= 0)
            {
                node = v > value[node] ? tree.Lefts[node] : tree.Rights[node];
            }

            lookup[i] = node;
        }

        return new JxlChannelPlan { Kind = JxlChannelPlanKind.Lookup, Property = tested, Min = (int)min, Max = (int)(max + 1), Lookup = lookup, UsesWp = usesWp, Predictor = predictor };
    }

    /// <summary>Flattens the subtree at <paramref name="root"/>, a decision, into two-level steps.</summary>
    private static JxlFlatNode[] Flatten(JxlTree tree, int root)
    {
        int[] property = tree.Properties;
        int[] value = tree.Values;
        int[] left = tree.Lefts;
        int[] right = tree.Rights;
        List<JxlFlatNode> flat = [default];
        Queue<(int Node, int Slot)> pending = new();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            (int node, int slot) = pending.Dequeue();
            if (property[node] < 0)
            {
                flat[slot] = new JxlFlatNode(0, 0, 0, 0, 0, 0, 0, node);
                continue;
            }

            int child = flat.Count;
            for (int i = 0; i < 4; i++)
            {
                flat.Add(default);
            }

            (int leftProperty, int leftValue) = Grandchildren(left[node], child);
            (int rightProperty, int rightValue) = Grandchildren(right[node], child + 2);
            flat[slot] = new JxlFlatNode(property[node], value[node], leftProperty, leftValue, rightProperty, rightValue, child, -1);
        }

        return [.. flat];

        // Queues the two nodes below a child at slots at and at + 1, and gives the child's test.
        (int Property, int Value) Grandchildren(int node, int at)
        {
            if (property[node] < 0)
            {
                pending.Enqueue((node, at));
                pending.Enqueue((node, at + 1));
                return (0, int.MaxValue);
            }

            pending.Enqueue((left[node], at));
            pending.Enqueue((right[node], at + 1));
            return (property[node], value[node]);
        }
    }
}

/// <summary>
/// The self-correcting predictor's running state over one channel (H.5.2), ported from jxl-oxide so the error bookkeeping at
/// row starts and ends matches libjxl exactly.
/// </summary>
internal sealed class JxlWeightedPredictor
{

    private readonly int _width;
    private readonly long _p1, _p2, _p3a, _p3b, _p3c, _p3d, _p3e;
    private readonly uint _w0, _w1, _w2, _w3;
    // The row above's errors, five per sample (the true error, then the four sub-predictors' errors), so a neighbour's are
    // one load apart.
    private readonly uint[] _errRow;
    private int[]? _rows;
    private int _x;
    private int _y;
    private int _errW;
    private int _errNw;
    private int _errN;
    private int _errNe;
    private uint _subNwWw0, _subNwWw1, _subNwWw2, _subNwWw3;
    private uint _subNW0, _subNW1, _subNW2, _subNW3;
    private uint _subNe0, _subNe1, _subNe2, _subNe3;

    // The last prediction, kept for Record.
    private long _prediction;
    private long _s0, _s1, _s2, _s3;

    internal JxlWeightedPredictor(int width, JxlWpParams wp)
    {
        _width = width;
        (_p1, _p2, _p3a, _p3b, _p3c, _p3d, _p3e) = (wp.P1, wp.P2, wp.P3a, wp.P3b, wp.P3c, wp.P3d, wp.P3e);
        (_w0, _w1, _w2, _w3) = ((uint)wp.W0, (uint)wp.W1, (uint)wp.W2, (uint)wp.W3);
        _errRow = new uint[width * 5];
    }

    /// <summary>Gets the max-error property (15) of the last <see cref="Predict"/>.</summary>
    internal int MaxError { get; private set; }

    /// <summary>Predicts at the current position, times 8; also sets <see cref="MaxError"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long Predict(int n, int nw, int ne, int w, int nn)
    {
        long errW = _errW;
        long errNw = _errNw;
        long errN = _errN;
        long errNe = _errNe;
        long n3 = (long)n << 3;
        long nw3 = (long)nw << 3;
        long ne3 = (long)ne << 3;
        long w3 = (long)w << 3;
        long nn3 = (long)nn << 3;
        _s0 = w3 + ne3 - n3;
        _s1 = n3 - (((errW + errN + errNe) * _p1) >> 5);
        _s2 = w3 - (((errW + errN + errNw) * _p2) >> 5);
        _s3 = n3 - (((errNw * _p3a) + (errN * _p3b) + (errNe * _p3c) + ((nn3 - n3) * _p3d) + ((nw3 - w3) * _p3e)) >> 5);
        uint w0 = Weight(unchecked(_subNwWw0 + _subNW0 + _subNe0), _w0);
        uint w1 = Weight(unchecked(_subNwWw1 + _subNW1 + _subNe1), _w1);
        uint w2 = Weight(unchecked(_subNwWw2 + _subNW2 + _subNe2), _w2);
        uint w3w = Weight(unchecked(_subNwWw3 + _subNW3 + _subNe3), _w3);
        uint sum = w0 + w1 + w2 + w3w;
        int logWeight = System.Numerics.BitOperations.Log2(sum >> 4);
        w0 >>= logWeight;
        w1 >>= logWeight;
        w2 >>= logWeight;
        w3w >>= logWeight;
        sum = w0 + w1 + w2 + w3w;
        long s = ((long)sum >> 1) - 1 + (_s0 * w0) + (_s1 * w1) + (_s2 * w2) + (_s3 * w3w);
        // Each weight is at least 4, so sum >> 4 is at least 1, and after the shift the sum is below 32.
        long prediction = (s * Divide(sum)) >> 24;

        // Clamped to the neighbours when the errors agree in sign; this and the largest error below avoid branches, which the
        // data would mispredict.
        long clamped = Min(Max(prediction, Min(n3, Min(w3, ne3))), Max(n3, Max(w3, ne3)));
        long agree = (((errN ^ errW) | (errN ^ errNw)) - 1) >> 63;
        prediction ^= (prediction ^ clamped) & agree;
        MaxError = (int)LargestError(errW, errN, errNw, errNe);
        _prediction = prediction;
        return prediction;
    }

    /// <summary>Records the decoded sample at the current position and moves to the next.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Record(int sample)
    {
        long s8 = (long)sample << 3;
        int trueErr = (int)(_prediction - s8);
        uint e0 = (uint)((Abs(_s0 - s8) + 3) >> 3);
        uint e1 = (uint)((Abs(_s1 - s8) + 3) >> 3);
        uint e2 = (uint)((Abs(_s2 - s8) + 3) >> 3);
        uint e3 = (uint)((Abs(_s3 - s8) + 3) >> 3);
        int x = _x;
        Span<uint> at = _errRow.AsSpan(5 * x, 5);
        at[0] = (uint)trueErr;
        at[1] = e0;
        at[2] = e1;
        at[3] = e2;
        at[4] = e3;
        _x++;
        if (_x >= _width)
        {
            _y++;
            _x = 0;
            _errW = 0;
            ReadOnlySpan<uint> first = _errRow.AsSpan(0, 5);
            _errN = (int)first[0];
            _errNw = _errN;
            (_subNW0, _subNW1, _subNW2, _subNW3) = (first[1], first[2], first[3], first[4]);
            (_subNwWw0, _subNwWw1, _subNwWw2, _subNwWw3) = (_subNW0, _subNW1, _subNW2, _subNW3);
            if (_width <= 1)
            {
                _errNe = _errN;
                (_subNe0, _subNe1, _subNe2, _subNe3) = (_subNW0, _subNW1, _subNW2, _subNW3);
            }
            else
            {
                ReadOnlySpan<uint> second = _errRow.AsSpan(5, 5);
                _errNe = (int)second[0];
                (_subNe0, _subNe1, _subNe2, _subNe3) = (second[1], second[2], second[3], second[4]);
            }

            return;
        }

        _errW = trueErr;
        _errNw = _errN;
        _errN = _errNe;
        (_subNwWw0, _subNwWw1, _subNwWw2, _subNwWw3) = (_subNW0, _subNW1, _subNW2, _subNW3);
        _subNW0 = unchecked(_subNe0 + e0);
        _subNW1 = unchecked(_subNe1 + e1);
        _subNW2 = unchecked(_subNe2 + e2);
        _subNW3 = unchecked(_subNe3 + e3);
        if (_x + 1 >= _width)
        {
            _errNe = _errN;
            (_subNe0, _subNe1, _subNe2, _subNe3) = (_subNW0, _subNW1, _subNW2, _subNW3);
        }
        else if (_y != 0)
        {
            ReadOnlySpan<uint> next = _errRow.AsSpan(5 * (_x + 1), 5);
            _errNe = (int)next[0];
            (_subNe0, _subNe1, _subNe2, _subNe3) = (next[1], next[2], next[3], next[4]);
        }
    }

    private static ulong Abs(long v) => v < 0 ? (ulong)-v : (ulong)v;

    /// <summary>Gets the error of largest magnitude, the first of equals, without branches.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LargestError(long errW, long errN, long errNw, long errNe)
    {
        long largest = errW;
        long magnitude = AbsL(errW);
        Larger(ref largest, ref magnitude, errN);
        Larger(ref largest, ref magnitude, errNw);
        Larger(ref largest, ref magnitude, errNe);
        return largest;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Larger(ref long largest, ref long magnitude, long e)
    {
        long a = AbsL(e);
        long more = (magnitude - a) >> 63;
        largest ^= (largest ^ e) & more;
        magnitude ^= (magnitude ^ a) & more;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AbsL(long v)
    {
        long m = v >> 63;
        return (v ^ m) - m;
    }

    // Branch-free minimum and maximum, for values far from the ends of the range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Min(long a, long b)
    {
        long d = a - b;
        return b + (d & (d >> 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Max(long a, long b)
    {
        long d = a - b;
        return a - (d & (d >> 63));
    }

    /// <summary>
    /// Predicts every sample of row <paramref name="y"/> of a channel whose samples are all known, the encoder's case, and
    /// records each: the same as <see cref="Predict"/> and <see cref="Record"/> per sample, with the state in locals. The
    /// predictor must be at the start of that row.
    /// </summary>
    internal void Row(int[] data, int y, Span<long> prediction, Span<int> maxError)
    {
        int width = _width;
        uint[] errRow = _errRow;
        long p1 = _p1, p2 = _p2, p3a = _p3a, p3b = _p3b, p3c = _p3c, p3d = _p3d, p3e = _p3e;
        uint mw0 = _w0, mw1 = _w1, mw2 = _w2, mw3 = _w3;
        int errW = _errW, errNw = _errNw, errN = _errN, errNe = _errNe;
        uint a0 = _subNwWw0, a1 = _subNwWw1, a2 = _subNwWw2, a3 = _subNwWw3;
        uint b0 = _subNW0, b1 = _subNW1, b2 = _subNW2, b3 = _subNW3;
        uint c0 = _subNe0, c1 = _subNe1, c2 = _subNe2, c3 = _subNe3;
        ref uint errors = ref MemoryMarshal.GetArrayDataReference(errRow);

        // The neighbours as rows with the edge rules filled in: the current row with its west of the first sample, the row
        // above with its north-west and north-east past the ends, and the row two above (the row above on row 1).
        int[] rows = _rows ??= new int[(3 * width) + 6];
        int row = y * width;
        Span<int> cur = rows.AsSpan(1, width + 1);
        Span<int> up = rows.AsSpan(width + 3, width + 1);
        Span<int> up2 = rows.AsSpan((2 * width) + 5, width);
        data.AsSpan(row, width).CopyTo(cur[..width]);
        if (y == 0)
        {
            rows[0] = 0;
        }
        else
        {
            data.AsSpan(row - width, width).CopyTo(up);
            rows[width + 2] = data[row - width];
            rows[(2 * width) + 3] = data[row - 1];
            rows[0] = data[row - width];
            data.AsSpan(y > 1 ? row - (2 * width) : row - width, width).CopyTo(up2);
        }

        ref int pc = ref rows[1];
        ref int pu = ref rows[width + 3];
        ref int pu2 = ref rows[(2 * width) + 5];
        prediction = prediction[..width];
        maxError = maxError[..width];
        for (int x = 0; x < width; x++)
        {
            int w = Unsafe.Add(ref pc, x - 1);
            int n, nw, ne, nn;
            if (y > 0)
            {
                n = Unsafe.Add(ref pu, x);
                nw = Unsafe.Add(ref pu, x - 1);
                ne = Unsafe.Add(ref pu, x + 1);
                nn = Unsafe.Add(ref pu2, x);
            }
            else
            {
                n = nw = ne = nn = w;
            }

            unchecked
            {
                long n3 = (long)n << 3;
                long nw3 = (long)nw << 3;
                long ne3 = (long)ne << 3;
                long w3 = (long)w << 3;
                long nn3 = (long)nn << 3;
                long s0 = w3 + ne3 - n3;
                long s1 = n3 - ((((long)errW + errN + errNe) * p1) >> 5);
                long s2 = w3 - ((((long)errW + errN + errNw) * p2) >> 5);
                long s3 = n3 - ((((long)errNw * p3a) + ((long)errN * p3b) + ((long)errNe * p3c) + ((nn3 - n3) * p3d) + ((nw3 - w3) * p3e)) >> 5);
                uint w0 = Weight(a0 + b0 + c0, mw0);
                uint w1 = Weight(a1 + b1 + c1, mw1);
                uint w2 = Weight(a2 + b2 + c2, mw2);
                uint w3w = Weight(a3 + b3 + c3, mw3);
                uint sum = w0 + w1 + w2 + w3w;
                int logWeight = System.Numerics.BitOperations.Log2(sum >> 4);
                w0 >>= logWeight;
                w1 >>= logWeight;
                w2 >>= logWeight;
                w3w >>= logWeight;
                sum = w0 + w1 + w2 + w3w;
                long sw = ((long)sum >> 1) - 1 + (s0 * w0) + (s1 * w1) + (s2 * w2) + (s3 * w3w);
                long predicted = (sw * Divide(sum)) >> 24;

                long clamped = Min(Max(predicted, Min(n3, Min(w3, ne3))), Max(n3, Max(w3, ne3)));
                long agree = ((long)((errN ^ errW) | (errN ^ errNw)) - 1) >> 63;
                predicted ^= (predicted ^ clamped) & agree;

                long me = LargestError(errW, errN, errNw, errNe);

                prediction[x] = predicted;
                maxError[x] = (int)me;

                // Record.
                long s8 = (long)Unsafe.Add(ref pc, x) << 3;
                int trueErr = (int)(predicted - s8);
                uint e0 = (uint)((AbsL(s0 - s8) + 3) >> 3);
                uint e1 = (uint)((AbsL(s1 - s8) + 3) >> 3);
                uint e2 = (uint)((AbsL(s2 - s8) + 3) >> 3);
                uint e3 = (uint)((AbsL(s3 - s8) + 3) >> 3);
                ref uint here = ref Unsafe.Add(ref errors, 5 * x);
                here = (uint)trueErr;
                Unsafe.Add(ref here, 1) = e0;
                Unsafe.Add(ref here, 2) = e1;
                Unsafe.Add(ref here, 3) = e2;
                Unsafe.Add(ref here, 4) = e3;
                if (x + 1 >= width)
                {
                    break;
                }

                errW = trueErr;
                errNw = errN;
                errN = errNe;
                (a0, a1, a2, a3) = (b0, b1, b2, b3);
                b0 = c0 + e0;
                b1 = c1 + e1;
                b2 = c2 + e2;
                b3 = c3 + e3;
                if (x + 2 >= width)
                {
                    errNe = errN;
                    (c0, c1, c2, c3) = (b0, b1, b2, b3);
                }
                else if (y != 0)
                {
                    ref uint next = ref Unsafe.Add(ref errors, 5 * (x + 2));
                    errNe = (int)next;
                    (c0, c1, c2, c3) = (Unsafe.Add(ref next, 1), Unsafe.Add(ref next, 2), Unsafe.Add(ref next, 3), Unsafe.Add(ref next, 4));
                }
            }
        }

        // The row end, as Record does it.
        _y = y + 1;
        _x = 0;
        _errW = 0;
        _errN = (int)errRow[0];
        _errNw = _errN;
        (_subNW0, _subNW1, _subNW2, _subNW3) = (errRow[1], errRow[2], errRow[3], errRow[4]);
        (_subNwWw0, _subNwWw1, _subNwWw2, _subNwWw3) = (_subNW0, _subNW1, _subNW2, _subNW3);
        if (width <= 1)
        {
            _errNe = _errN;
            (_subNe0, _subNe1, _subNe2, _subNe3) = (_subNW0, _subNW1, _subNW2, _subNW3);
        }
        else
        {
            _errNe = (int)errRow[5];
            (_subNe0, _subNe1, _subNe2, _subNe3) = (errRow[6], errRow[7], errRow[8], errRow[9]);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Weight(uint errorSum, uint maxWeight)
    {
        uint shifted = (uint)(((ulong)errorSum + 1) >> 5);
        int shift = System.Numerics.BitOperations.Log2(shifted | 1);
        // errorSum + 1 is below 2^(shift + 6), so the index is at most 64.
        return 4 + ((maxWeight * Divide((errorSum >> shift) + 1)) >> shift);
    }

    /// <summary>Gets 2^24 / <paramref name="i"/> (0 for 0) from the table; callers keep <paramref name="i"/> at most 64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Divide(uint i) => Unsafe.Add(ref MemoryMarshal.GetReference(DivLookup), (nint)i);

    // 2^24 / i for i from 0 (as 0) to 64, as constant data so no class initialization check guards it.
    private static ReadOnlySpan<uint> DivLookup =>
    [
        0, 16777216, 8388608, 5592405, 4194304, 3355443, 2796202, 2396745,
        2097152, 1864135, 1677721, 1525201, 1398101, 1290555, 1198372, 1118481,
        1048576, 986895, 932067, 883011, 838860, 798915, 762600, 729444,
        699050, 671088, 645277, 621378, 599186, 578524, 559240, 541200,
        524288, 508400, 493447, 479349, 466033, 453438, 441505, 430185,
        419430, 409200, 399457, 390167, 381300, 372827, 364722, 356962,
        349525, 342392, 335544, 328965, 322638, 316551, 310689, 305040,
        299593, 294337, 289262, 284359, 279620, 275036, 270600, 266305,
        262144,
    ];
}
