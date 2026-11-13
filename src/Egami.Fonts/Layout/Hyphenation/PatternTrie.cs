namespace Egami.Fonts.Layout.Hyphenation;

/// <summary>
/// Liang's patterns as a packed trie (F. M. Liang, "Word Hy-phen-a-tion by Com-put-er", Stanford, 1983): every state keeps its
/// edges as a sorted run of UTF-16 code units with their target states, and a state that ends a pattern points at that
/// pattern's values (one per gap, from before its first letter to after its last) in a shared pool.
/// </summary>
internal sealed class PatternTrie
{
    /// <summary>The most letters a pattern may have, so its values (one more) fit a byte length.</summary>
    public const int MaxPatternLength = 254;

    // Edges of state s: indexes EdgeStart[s] to EdgeStart[s + 1] - 1, sorted by EdgeChars.
    internal readonly int[] EdgeStart;
    internal readonly char[] EdgeChars;
    internal readonly int[] EdgeTargets;

    // Values of state s: ValuePool[ValueStart[s] .. + ValueLength[s]], or none when ValueLength[s] is 0.
    internal readonly int[] ValueStart;
    internal readonly byte[] ValueLength;
    internal readonly byte[] ValuePool;

    internal PatternTrie(int[] edgeStart, char[] edgeChars, int[] edgeTargets, int[] valueStart, byte[] valueLength, byte[] valuePool)
    {
        EdgeStart = edgeStart;
        EdgeChars = edgeChars;
        EdgeTargets = edgeTargets;
        ValueStart = valueStart;
        ValueLength = valueLength;
        ValuePool = valuePool;
    }

    public int StateCount => ValueStart.Length;

    /// <summary>
    /// Raises <paramref name="levels"/> (one per gap of <paramref name="dotted"/>, which is the lower-cased word between two
    /// dots) to every matching pattern's values: the lookup Liang's thesis describes, walking the trie from each position.
    /// </summary>
    public void Apply(ReadOnlySpan<char> dotted, Span<byte> levels)
    {
        for (int start = 0; start < dotted.Length; start++)
        {
            int state = 0;
            for (int i = start; i < dotted.Length; i++)
            {
                state = Next(state, dotted[i]);
                if (state < 0)
                {
                    break;
                }

                int length = ValueLength[state];
                if (length != 0)
                {
                    ReadOnlySpan<byte> values = ValuePool.AsSpan(ValueStart[state], length);
                    Span<byte> target = levels.Slice(start, length);
                    for (int k = 0; k < values.Length; k++)
                    {
                        if (values[k] > target[k])
                        {
                            target[k] = values[k];
                        }
                    }
                }
            }
        }
    }

    private int Next(int state, char c)
    {
        int lo = EdgeStart[state];
        int hi = EdgeStart[state + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            char m = EdgeChars[mid];
            if (m == c)
            {
                return EdgeTargets[mid];
            }

            if (m < c)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    /// <summary>Checks the arrays of a loaded trie, so a lookup never reads outside them or loops.</summary>
    public static PatternTrie Validated(int[] edgeStart, char[] edgeChars, int[] edgeTargets, int[] valueStart, byte[] valueLength, byte[] valuePool)
    {
        int states = valueStart.Length;
        if (states == 0 || edgeStart.Length != states + 1 || valueLength.Length != states || edgeChars.Length != edgeTargets.Length
            || edgeStart[0] != 0 || edgeStart[states] != edgeChars.Length)
        {
            throw new InvalidDataException("The hyphenation trie's tables do not fit together.");
        }

        // The fewest letters on a path to each state: a state's values may not reach past the end of its pattern.
        int[] depth = new int[states];
        Array.Fill(depth, int.MaxValue);
        depth[0] = 0;
        for (int s = 0; s < states; s++)
        {
            int from = edgeStart[s];
            int to = edgeStart[s + 1];
            if (to < from || to > edgeChars.Length)
            {
                throw new InvalidDataException("The hyphenation trie's edge table is out of order.");
            }

            for (int e = from; e < to; e++)
            {
                // Targets point forward, as the builder numbers states breadth first, so no walk can loop.
                if (edgeTargets[e] <= s || edgeTargets[e] >= states || (e > from && edgeChars[e] <= edgeChars[e - 1]))
                {
                    throw new InvalidDataException("The hyphenation trie has a bad edge.");
                }

                if (depth[s] != int.MaxValue)
                {
                    depth[edgeTargets[e]] = Math.Min(depth[edgeTargets[e]], depth[s] + 1);
                }
            }

            if (valueLength[s] > depth[s] + 1L)
            {
                throw new InvalidDataException("A hyphenation pattern has more values than gaps.");
            }

            if (valueLength[s] != 0 && ((uint)valueStart[s] > (uint)valuePool.Length || valuePool.Length - valueStart[s] < valueLength[s]))
            {
                throw new InvalidDataException("The hyphenation trie's values are out of range.");
            }
        }

        return new PatternTrie(edgeStart, edgeChars, edgeTargets, valueStart, valueLength, valuePool);
    }
}

/// <summary>Collects patterns and packs them into a <see cref="PatternTrie"/>.</summary>
internal sealed class PatternTrieBuilder
{
    private sealed class Node
    {
        public SortedDictionary<char, Node>? Children;
        public byte[]? Values;
    }

    private readonly Node _root = new();

    public int Count { get; private set; }

    /// <summary>
    /// Adds one pattern in Liang's notation, such as <c>.hy3ph</c> or <c>hen5at</c>: letters (and <c>.</c> for a word edge)
    /// with digits between them. Letters are lower-cased; a later pattern with the same letters replaces an earlier one.
    /// </summary>
    public void Add(ReadOnlySpan<char> pattern)
    {
        Span<char> letters = stackalloc char[Math.Min(pattern.Length, PatternTrie.MaxPatternLength + 1)];
        Span<byte> values = stackalloc byte[letters.Length + 1];
        values.Clear();
        int count = 0;
        foreach (char c in pattern)
        {
            if (c is >= '0' and <= '9')
            {
                values[count] = (byte)(c - '0');
                continue;
            }

            if (count == PatternTrie.MaxPatternLength)
            {
                throw new InvalidDataException($"A hyphenation pattern is longer than {PatternTrie.MaxPatternLength} characters.");
            }

            letters[count++] = char.ToLowerInvariant(c);
        }

        if (count == 0)
        {
            return;
        }

        // A digit between the halves of a surrogate pair would split a character; ignore it.
        for (int i = 1; i < count; i++)
        {
            if (char.IsLowSurrogate(letters[i]) && char.IsHighSurrogate(letters[i - 1]))
            {
                values[i] = 0;
            }
        }

        Node node = _root;
        for (int i = 0; i < count; i++)
        {
            node.Children ??= [];
            if (!node.Children.TryGetValue(letters[i], out Node? next))
            {
                next = new Node();
                node.Children.Add(letters[i], next);
            }

            node = next;
        }

        if (node.Values is null)
        {
            Count++;
        }

        // Only the span from the first to the last nonzero value matters, but the values are applied from the pattern's
        // start, so keep the leading zeros and drop the trailing ones.
        int last = count;
        while (last >= 0 && values[last] == 0)
        {
            last--;
        }

        node.Values = last < 0 ? [] : values[..(last + 1)].ToArray();
    }

    public PatternTrie Build()
    {
        // Number the states breadth first, so every edge points to a later state (the loader relies on it).
        List<Node> order = [_root];
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i].Children is { } children)
            {
                order.AddRange(children.Values);
            }
        }

        Dictionary<Node, int> index = new(order.Count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < order.Count; i++)
        {
            index[order[i]] = i;
        }

        int[] edgeStart = new int[order.Count + 1];
        List<char> edgeChars = [];
        List<int> edgeTargets = [];
        int[] valueStart = new int[order.Count];
        byte[] valueLength = new byte[order.Count];
        List<byte> pool = [];
        Dictionary<string, int> shared = new(StringComparer.Ordinal);
        for (int s = 0; s < order.Count; s++)
        {
            Node node = order[s];
            edgeStart[s] = edgeChars.Count;
            if (node.Children is { } children)
            {
                foreach ((char c, Node child) in children)
                {
                    edgeChars.Add(c);
                    edgeTargets.Add(index[child]);
                }
            }

            if (node.Values is { Length: > 0 } values)
            {
                // Many patterns share their values; store each distinct run once.
                string key = Convert.ToHexString(values);
                if (!shared.TryGetValue(key, out int at))
                {
                    at = pool.Count;
                    pool.AddRange(values);
                    shared.Add(key, at);
                }

                valueStart[s] = at;
                valueLength[s] = (byte)values.Length;
            }
        }

        edgeStart[order.Count] = edgeChars.Count;
        return new PatternTrie(edgeStart, [.. edgeChars], [.. edgeTargets], valueStart, valueLength, [.. pool]);
    }
}
