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

    internal int Predictor(int leaf) => _predictor[leaf];

    internal int Offset(int leaf) => _offset[leaf];

    internal int Multiplier(int leaf) => _multiplier[leaf];

    internal int Cluster(int leaf) => _cluster[leaf];

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

/// <summary>
/// The self-correcting predictor's running state over one channel (H.5.2), ported from jxl-oxide so the error bookkeeping at
/// row starts and ends matches libjxl exactly.
/// </summary>
internal sealed class JxlWeightedPredictor
{
    private static readonly uint[] DivLookup = BuildDivLookup();

    private readonly JxlWpParams _wp;
    private readonly int _width;
    private readonly int[] _trueErrRow;
    private readonly uint[] _subErrRow;
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
        _wp = wp;
        _width = width;
        _trueErrRow = new int[width];
        _subErrRow = new uint[width * 4];
    }

    /// <summary>Gets the max-error property (15) of the last <see cref="Predict"/>.</summary>
    internal int MaxError { get; private set; }

    /// <summary>Predicts at the current position, times 8; also sets <see cref="MaxError"/>.</summary>
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
        _s1 = n3 - (((errW + errN + errNe) * _wp.P1) >> 5);
        _s2 = w3 - (((errW + errN + errNw) * _wp.P2) >> 5);
        _s3 = n3 - (((errNw * _wp.P3a) + (errN * _wp.P3b) + (errNe * _wp.P3c) + ((nn3 - n3) * _wp.P3d) + ((nw3 - w3) * _wp.P3e)) >> 5);
        uint w0 = Weight(unchecked(_subNwWw0 + _subNW0 + _subNe0), (uint)_wp.W0);
        uint w1 = Weight(unchecked(_subNwWw1 + _subNW1 + _subNe1), (uint)_wp.W1);
        uint w2 = Weight(unchecked(_subNwWw2 + _subNW2 + _subNe2), (uint)_wp.W2);
        uint w3w = Weight(unchecked(_subNwWw3 + _subNW3 + _subNe3), (uint)_wp.W3);
        uint sum = w0 + w1 + w2 + w3w;
        int logWeight = System.Numerics.BitOperations.Log2(sum >> 4);
        w0 >>= logWeight;
        w1 >>= logWeight;
        w2 >>= logWeight;
        w3w >>= logWeight;
        sum = w0 + w1 + w2 + w3w;
        long s = ((long)sum >> 1) - 1 + (_s0 * w0) + (_s1 * w1) + (_s2 * w2) + (_s3 * w3w);
        long prediction = (s * DivLookup[sum]) >> 24;
        if (((errN ^ errW) | (errN ^ errNw)) <= 0)
        {
            long min = Math.Min(n3, Math.Min(w3, ne3));
            long max = Math.Max(n3, Math.Max(w3, ne3));
            prediction = Math.Clamp(prediction, min, max);
        }

        long maxError = errW;
        if (Math.Abs(errN) > Math.Abs(maxError))
        {
            maxError = errN;
        }

        if (Math.Abs(errNw) > Math.Abs(maxError))
        {
            maxError = errNw;
        }

        if (Math.Abs(errNe) > Math.Abs(maxError))
        {
            maxError = errNe;
        }

        MaxError = (int)maxError;
        _prediction = prediction;
        return prediction;
    }

    /// <summary>Records the decoded sample at the current position and moves to the next.</summary>
    internal void Record(int sample)
    {
        long s8 = (long)sample << 3;
        int trueErr = (int)(_prediction - s8);
        uint e0 = (uint)((Abs(_s0 - s8) + 3) >> 3);
        uint e1 = (uint)((Abs(_s1 - s8) + 3) >> 3);
        uint e2 = (uint)((Abs(_s2 - s8) + 3) >> 3);
        uint e3 = (uint)((Abs(_s3 - s8) + 3) >> 3);
        int x = _x;
        _trueErrRow[x] = trueErr;
        _subErrRow[4 * x] = e0;
        _subErrRow[(4 * x) + 1] = e1;
        _subErrRow[(4 * x) + 2] = e2;
        _subErrRow[(4 * x) + 3] = e3;
        _x++;
        if (_x >= _width)
        {
            _y++;
            _x = 0;
            _errW = 0;
            _errN = _trueErrRow[0];
            _errNw = _errN;
            (_subNW0, _subNW1, _subNW2, _subNW3) = (_subErrRow[0], _subErrRow[1], _subErrRow[2], _subErrRow[3]);
            (_subNwWw0, _subNwWw1, _subNwWw2, _subNwWw3) = (_subNW0, _subNW1, _subNW2, _subNW3);
            if (_width <= 1)
            {
                _errNe = _errN;
                (_subNe0, _subNe1, _subNe2, _subNe3) = (_subNW0, _subNW1, _subNW2, _subNW3);
            }
            else
            {
                _errNe = _trueErrRow[1];
                (_subNe0, _subNe1, _subNe2, _subNe3) = (_subErrRow[4], _subErrRow[5], _subErrRow[6], _subErrRow[7]);
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
            int nx = _x + 1;
            _errNe = _trueErrRow[nx];
            (_subNe0, _subNe1, _subNe2, _subNe3) = (_subErrRow[4 * nx], _subErrRow[(4 * nx) + 1], _subErrRow[(4 * nx) + 2], _subErrRow[(4 * nx) + 3]);
        }
    }

    private static ulong Abs(long v) => v < 0 ? (ulong)-v : (ulong)v;

    private static uint Weight(uint errorSum, uint maxWeight)
    {
        uint shifted = (uint)(((ulong)errorSum + 1) >> 5);
        int shift = shifted == 0 ? 0 : System.Numerics.BitOperations.Log2(shifted);
        return 4 + ((maxWeight * DivLookup[(errorSum >> shift) + 1]) >> shift);
    }

    private static uint[] BuildDivLookup()
    {
        uint[] table = new uint[65];
        for (int i = 1; i <= 64; i++)
        {
            table[i] = (uint)((1 << 24) / i);
        }

        return table;
    }
}
