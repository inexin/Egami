namespace Egami.Fonts.Layout.Hyphenation;

/// <summary>
/// Liang's patterns as a packed trie (F. M. Liang, "Word Hy-phen-a-tion by Com-put-er", Stanford, 1983). States are numbered
/// breadth first, so edge e (in order of parent, then character) leads to state e + 1 and needs no stored target: a state keeps
/// where its edges start, and each edge its UTF-16 code unit. A state that ends a pattern names that pattern's values (one per
/// gap, from before its first letter) among the distinct value strings, which many patterns share.
/// </summary>
internal sealed class PatternTrie
{
    /// <summary>The most letters a pattern may have, so its values (one more) fit a byte length.</summary>
    public const int MaxPatternLength = 254;

    // Edges of state s: indexes EdgeStart[s] to EdgeStart[s + 1] - 1, sorted by EdgeChars; edge e leads to state e + 1.
    internal readonly int[] EdgeStart;
    internal readonly char[] EdgeChars;

    // Values of state s: none when ValueId[s] is 0, else ValuePool[ValueOffset[id - 1] .. ValueOffset[id]].
    internal readonly ushort[] ValueId;
    internal readonly int[] ValueOffset;
    internal readonly byte[] ValuePool;

    internal PatternTrie(int[] edgeStart, char[] edgeChars, ushort[] valueId, int[] valueOffset, byte[] valuePool)
    {
        EdgeStart = edgeStart;
        EdgeChars = edgeChars;
        ValueId = valueId;
        ValueOffset = valueOffset;
        ValuePool = valuePool;
    }

    public int StateCount => ValueId.Length;

    /// <summary>Gets the number of distinct value strings.</summary>
    public int ValueCount => ValueOffset.Length - 1;

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

                int id = ValueId[state];
                if (id != 0)
                {
                    int from = ValueOffset[id - 1];
                    ReadOnlySpan<byte> values = ValuePool.AsSpan(from, ValueOffset[id] - from);
                    Span<byte> target = levels.Slice(start, values.Length);
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
                return mid + 1;
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
    public static PatternTrie Validated(int[] edgeStart, char[] edgeChars, ushort[] valueId, int[] valueOffset, byte[] valuePool)
    {
        int states = valueId.Length;
        if (states == 0 || edgeStart.Length != states + 1 || edgeChars.Length != states - 1 || edgeStart[0] != 0
            || edgeStart[states] != edgeChars.Length || valueOffset.Length == 0 || valueOffset[0] != 0
            || valueOffset[^1] != valuePool.Length)
        {
            throw new InvalidDataException("The hyphenation trie's tables do not fit together.");
        }

        for (int v = 1; v < valueOffset.Length; v++)
        {
            int length = valueOffset[v] - valueOffset[v - 1];
            if (length <= 0 || length > MaxPatternLength + 1)
            {
                throw new InvalidDataException("The hyphenation trie's values are out of order.");
            }
        }

        // Edge e leads to state e + 1, which must come after its parent s (so no walk loops); depth counts the letters on the
        // way to a state, and a state's values may not reach past the end of its pattern.
        byte[] depth = new byte[states];
        for (int s = 0; s < states; s++)
        {
            int from = edgeStart[s];
            int to = edgeStart[s + 1];
            if (to < from || from < s)
            {
                throw new InvalidDataException("The hyphenation trie's edge table is out of order.");
            }

            for (int e = from; e < to; e++)
            {
                if (e > from && edgeChars[e] <= edgeChars[e - 1])
                {
                    throw new InvalidDataException("The hyphenation trie has a bad edge.");
                }

                depth[e + 1] = (byte)Math.Min(depth[s] + 1, byte.MaxValue);
            }

            int id = valueId[s];
            if (id >= valueOffset.Length || (id != 0 && valueOffset[id] - valueOffset[id - 1] > depth[s] + 1))
            {
                throw new InvalidDataException("A hyphenation pattern's values are out of range.");
            }
        }

        return new PatternTrie(edgeStart, edgeChars, valueId, valueOffset, valuePool);
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
        // Number the states breadth first: edge e then leads to state e + 1, which the trie and the loader rely on.
        List<Node> order = [_root];
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i].Children is { } children)
            {
                order.AddRange(children.Values);
            }
        }

        int[] edgeStart = new int[order.Count + 1];
        char[] edgeChars = new char[order.Count - 1];
        ushort[] valueId = new ushort[order.Count];
        List<int> valueOffset = [0];
        List<byte> pool = [];
        Dictionary<string, ushort> shared = new(StringComparer.Ordinal);
        int edges = 0;
        for (int s = 0; s < order.Count; s++)
        {
            Node node = order[s];
            edgeStart[s] = edges;
            if (node.Children is { } children)
            {
                foreach (char c in children.Keys)
                {
                    edgeChars[edges++] = c;
                }
            }

            if (node.Values is { Length: > 0 } values)
            {
                // Many patterns share their values; store each distinct string once.
                string key = Convert.ToHexString(values);
                if (!shared.TryGetValue(key, out ushort id))
                {
                    if (valueOffset.Count > ushort.MaxValue)
                    {
                        throw new InvalidDataException($"The patterns have more than {ushort.MaxValue} distinct value strings.");
                    }

                    pool.AddRange(values);
                    valueOffset.Add(pool.Count);
                    id = (ushort)(valueOffset.Count - 1);
                    shared.Add(key, id);
                }

                valueId[s] = id;
            }
        }

        edgeStart[order.Count] = edges;
        return new PatternTrie(edgeStart, edgeChars, valueId, [.. valueOffset], [.. pool]);
    }
}
