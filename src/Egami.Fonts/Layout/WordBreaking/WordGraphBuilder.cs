namespace Egami.Fonts.Layout.WordBreaking;

/// <summary>
/// Builds the minimal acyclic automaton of words given in ascending order, by the incremental algorithm of J. Daciuk,
/// S. Mihov, B. W. Watson and R. E. Watson, "Incremental Construction of Minimal Acyclic Finite-State Automata"
/// (Computational Linguistics 26(1), 2000), then lays it out in <see cref="WordGraph"/>'s form.
/// </summary>
internal sealed class WordGraphBuilder
{
    private readonly State _root = new();
    private readonly Dictionary<State, State> _register = new(StateComparer.Instance);
    private readonly List<State> _path = [];
    private byte[] _previous = [];
    private int _previousLength;
    private int _nextId = 1;

    /// <summary>Adds a word of symbols (1 to 255), greater than the one added before.</summary>
    public void Add(ReadOnlySpan<byte> word)
    {
        int common = 0;
        while (common < word.Length && common < _previousLength && word[common] == _previous[common])
        {
            common++;
        }

        if (_path.Count == 0)
        {
            _path.Add(_root);
        }

        Minimize(common);
        State state = _path[common];
        for (int i = common; i < word.Length; i++)
        {
            State child = new();
            state.Edges.Add((word[i], child));
            _path.Add(child);
            state = child;
        }

        state.Final = true;
        if (_previous.Length < word.Length)
        {
            Array.Resize(ref _previous, Math.Max(word.Length, _previous.Length * 2));
        }

        word.CopyTo(_previous);
        _previousLength = word.Length;
    }

    /// <summary>Minimises what is left and lays the graph out.</summary>
    public byte[] Finish()
    {
        if (_path.Count == 0)
        {
            return [];
        }

        Minimize(0);
        if (_root.Edges.Count == 0)
        {
            return [];
        }

        // Reverse postorder puts every node before the nodes its edges lead to, and a node's last child often right after it.
        List<State> order = [];
        HashSet<State> seen = new(ReferenceEqualityComparer.Instance);
        Stack<(State State, int Edge)> stack = new();
        stack.Push((_root, 0));
        seen.Add(_root);
        while (stack.Count > 0)
        {
            (State state, int edge) = stack.Pop();
            if (edge < state.Edges.Count)
            {
                stack.Push((state, edge + 1));
                State child = state.Edges[edge].Target;
                if (child.Edges.Count > 0 && seen.Add(child))
                {
                    stack.Push((child, 0));
                }
            }
            else
            {
                order.Add(state);
            }
        }

        // Write from the last node back, each node's edges last to first, so every target's distance from the end is known.
        List<byte> reversed = [];
        Dictionary<State, int> fromEnd = new(ReferenceEqualityComparer.Instance);
        Span<byte> varint = stackalloc byte[5];
        foreach (State state in order)
        {
            for (int e = state.Edges.Count - 1; e >= 0; e--)
            {
                (byte symbol, State target) = state.Edges[e];
                long t = target.Edges.Count == 0 ? 0 : reversed.Count - fromEnd[target] + 1;
                ulong v = ((ulong)t << 2) | (e == state.Edges.Count - 1 ? 2UL : 0UL) | (target.Final ? 1UL : 0UL);
                int n = 0;
                while (v >= 0x80)
                {
                    varint[n++] = (byte)(v | 0x80);
                    v >>= 7;
                }

                varint[n++] = (byte)v;
                for (int i = n - 1; i >= 0; i--)
                {
                    reversed.Add(varint[i]);
                }

                reversed.Add(symbol);
            }

            fromEnd[state] = reversed.Count;
        }

        byte[] graph = [.. reversed];
        Array.Reverse(graph);
        return graph;
    }

    /// <summary>Replaces each state of the previous word's path below <paramref name="depth"/> by an equal registered one,
    /// or registers it.</summary>
    private void Minimize(int depth)
    {
        for (int i = _path.Count - 1; i > depth; i--)
        {
            State child = _path[i];
            State parent = _path[i - 1];
            if (_register.TryGetValue(child, out State? same))
            {
                (byte symbol, _) = parent.Edges[^1];
                parent.Edges[^1] = (symbol, same);
            }
            else
            {
                child.Id = _nextId++;
                _register.Add(child, child);
            }

            _path.RemoveAt(i);
        }
    }

    private sealed class State
    {
        public List<(byte Symbol, State Target)> Edges { get; } = [];

        public bool Final { get; set; }

        public int Id { get; set; }
    }

    /// <summary>Equal states: both final or not, and the same edges to the same registered states.</summary>
    private sealed class StateComparer : IEqualityComparer<State>
    {
        public static readonly StateComparer Instance = new();

        public bool Equals(State? x, State? y)
        {
            if (x!.Final != y!.Final || x.Edges.Count != y.Edges.Count)
            {
                return false;
            }

            for (int i = 0; i < x.Edges.Count; i++)
            {
                if (x.Edges[i].Symbol != y.Edges[i].Symbol || x.Edges[i].Target.Id != y.Edges[i].Target.Id)
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(State state)
        {
            HashCode hash = default;
            hash.Add(state.Final);
            foreach ((byte symbol, State target) in state.Edges)
            {
                hash.Add(symbol);
                hash.Add(target.Id);
            }

            return hash.ToHashCode();
        }
    }
}
