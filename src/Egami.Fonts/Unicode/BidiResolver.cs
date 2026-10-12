namespace Egami.Fonts.Unicode;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9) over one paragraph of code points: paragraph level (P2, P3), explicit levels
/// (X1 to X10), weak and neutral types (W1 to W7, N0 to N2), implicit levels (I1, I2) and the line rules L1 and L2.
/// </summary>
/// <remarks>Characters removed by X9 (embeddings, overrides, PDF and BN) stay in the arrays with the level of the character
/// before them; the rules skip them.</remarks>
internal sealed class BidiResolver
{
    /// <summary>The deepest explicit embedding level (BD2).</summary>
    public const int MaxDepth = 125;

    private const int MaxBracketStack = 63;

    private BidiClass[] _initial = [];
    private BidiClass[] _types = [];
    private int[] _matchingPdi = [];
    private int[] _sequence = [];
    private int _length;
    private byte[] _levels = [];
    private byte[] _explicit = [];
    private int _paragraphLevel;
    private ReadOnlyMemory<int> _codePoints;

    /// <summary>Gets the resolved paragraph embedding level.</summary>
    public int ParagraphLevel => _paragraphLevel;

    /// <summary>Gets the original class of every character.</summary>
    public ReadOnlySpan<BidiClass> InitialClasses => _initial.AsSpan(0, _length);

    /// <summary>Gets whether X9 removes a class.</summary>
    public static bool IsRemovedByX9(BidiClass c) => c is BidiClass.LeftToRightEmbedding or BidiClass.RightToLeftEmbedding
        or BidiClass.LeftToRightOverride or BidiClass.RightToLeftOverride or BidiClass.PopDirectionalFormat or BidiClass.BoundaryNeutral;

    /// <summary>
    /// Resolves one paragraph. <paramref name="codePoints"/> gives the characters for paired brackets (N0) and may be empty, as for
    /// BidiTest.txt's class sequences; <paramref name="levels"/> receives each character's level before the line rules.
    /// </summary>
    public void Resolve(ReadOnlySpan<BidiClass> classes, ReadOnlyMemory<int> codePoints, TextDirection direction, Span<byte> levels)
    {
        _length = classes.Length;
        _codePoints = codePoints;
        Grow(ref _initial, _length);
        Grow(ref _types, _length);
        Grow(ref _matchingPdi, _length);
        Grow(ref _sequence, _length);
        Grow(ref _levels, _length);
        Grow(ref _explicit, _length);
        classes.CopyTo(_initial);
        classes.CopyTo(_types);

        FindMatchingPdis();
        _paragraphLevel = direction switch
        {
            TextDirection.RightToLeft => 1,
            TextDirection.Auto => FirstStrongLevel(0, _length, 0),
            _ => 0,
        };

        ResolveExplicit();
        _levels.AsSpan(0, _length).CopyTo(_explicit);
        ResolveSequences();
        _levels.AsSpan(0, _length).CopyTo(levels);
    }

    /// <summary>Applies rule L1 to one line of resolved levels: separators and trailing whitespace go to the paragraph level.</summary>
    public static void ApplyLineRule(ReadOnlySpan<BidiClass> initial, Span<byte> levels, int paragraphLevel)
    {
        bool trailing = true;
        for (int i = initial.Length - 1; i >= 0; i--)
        {
            BidiClass c = initial[i];
            if (c is BidiClass.SegmentSeparator or BidiClass.ParagraphSeparator)
            {
                levels[i] = (byte)paragraphLevel;
                trailing = true;
            }
            else if (c is BidiClass.WhiteSpace or BidiClass.FirstStrongIsolate or BidiClass.LeftToRightIsolate
                or BidiClass.RightToLeftIsolate or BidiClass.PopDirectionalIsolate || IsRemovedByX9(c))
            {
                if (trailing)
                {
                    levels[i] = (byte)paragraphLevel;
                }
            }
            else
            {
                trailing = false;
            }
        }
    }

    private static void Grow<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[Math.Max(length, array.Length * 2)];
        }
    }

    private static bool IsIsolateInitiator(BidiClass c) => c is BidiClass.LeftToRightIsolate or BidiClass.RightToLeftIsolate or BidiClass.FirstStrongIsolate;

    /// <summary>BD9: the PDI that matches each isolate initiator, or -1.</summary>
    private void FindMatchingPdis()
    {
        Span<int> matching = _matchingPdi.AsSpan(0, _length);
        matching.Fill(-1);
        Span<int> stack = _length <= 256 ? stackalloc int[_length] : new int[_length];
        int depth = 0;
        for (int i = 0; i < _length; i++)
        {
            BidiClass c = _initial[i];
            if (IsIsolateInitiator(c))
            {
                stack[depth++] = i;
            }
            else if (c == BidiClass.PopDirectionalIsolate && depth > 0)
            {
                matching[stack[--depth]] = i;
            }
        }
    }

    /// <summary>P2 and P3 between two indexes, skipping isolates; <paramref name="fallback"/> when nothing is strong.</summary>
    private int FirstStrongLevel(int start, int end, int fallback)
    {
        for (int i = start; i < end; i++)
        {
            BidiClass c = _initial[i];
            if (c == BidiClass.LeftToRight)
            {
                return 0;
            }

            if (c is BidiClass.RightToLeft or BidiClass.ArabicLetter)
            {
                return 1;
            }

            if (IsIsolateInitiator(c))
            {
                int pdi = _matchingPdi[i];
                if (pdi < 0)
                {
                    break;
                }

                i = pdi;
            }
        }

        return fallback;
    }

    /// <summary>X1 to X8.</summary>
    private void ResolveExplicit()
    {
        // The directional status stack: level, override (0 neutral, 1 L, 2 R) and isolate status per entry.
        Span<byte> stackLevel = stackalloc byte[MaxDepth + 2];
        Span<byte> stackOverride = stackalloc byte[MaxDepth + 2];
        Span<bool> stackIsolate = stackalloc bool[MaxDepth + 2];
        int top = 0;
        stackLevel[0] = (byte)_paragraphLevel;
        stackOverride[0] = 0;
        stackIsolate[0] = false;
        int overflowIsolates = 0;
        int overflowEmbeddings = 0;
        int validIsolates = 0;

        for (int i = 0; i < _length; i++)
        {
            BidiClass c = _initial[i];
            switch (c)
            {
                case BidiClass.RightToLeftEmbedding:
                case BidiClass.LeftToRightEmbedding:
                case BidiClass.RightToLeftOverride:
                case BidiClass.LeftToRightOverride:
                    {
                        bool rtl = c is BidiClass.RightToLeftEmbedding or BidiClass.RightToLeftOverride;
                        int level = rtl ? (stackLevel[top] + 1) | 1 : (stackLevel[top] + 2) & ~1;
                        _levels[i] = stackLevel[top];
                        if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            top++;
                            stackLevel[top] = (byte)level;
                            stackOverride[top] = c switch
                            {
                                BidiClass.RightToLeftOverride => 2,
                                BidiClass.LeftToRightOverride => 1,
                                _ => 0,
                            };
                            stackIsolate[top] = false;
                        }
                        else if (overflowIsolates == 0)
                        {
                            overflowEmbeddings++;
                        }

                        break;
                    }

                case BidiClass.RightToLeftIsolate:
                case BidiClass.LeftToRightIsolate:
                case BidiClass.FirstStrongIsolate:
                    {
                        _levels[i] = stackLevel[top];
                        ApplyOverride(i, stackOverride[top]);
                        bool rtl = c == BidiClass.RightToLeftIsolate;
                        if (c == BidiClass.FirstStrongIsolate)
                        {
                            int end = _matchingPdi[i] < 0 ? _length : _matchingPdi[i];
                            rtl = FirstStrongLevel(i + 1, end, 0) == 1;
                        }

                        int level = rtl ? (stackLevel[top] + 1) | 1 : (stackLevel[top] + 2) & ~1;
                        if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            validIsolates++;
                            top++;
                            stackLevel[top] = (byte)level;
                            stackOverride[top] = 0;
                            stackIsolate[top] = true;
                        }
                        else
                        {
                            overflowIsolates++;
                        }

                        break;
                    }

                case BidiClass.PopDirectionalIsolate:
                    if (overflowIsolates > 0)
                    {
                        overflowIsolates--;
                    }
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stackIsolate[top])
                        {
                            top--;
                        }

                        top--;
                        validIsolates--;
                    }

                    _levels[i] = stackLevel[top];
                    ApplyOverride(i, stackOverride[top]);
                    break;

                case BidiClass.PopDirectionalFormat:
                    _levels[i] = stackLevel[top];
                    if (overflowIsolates > 0)
                    {
                    }
                    else if (overflowEmbeddings > 0)
                    {
                        overflowEmbeddings--;
                    }
                    else if (!stackIsolate[top] && top > 0)
                    {
                        top--;
                    }

                    break;

                case BidiClass.ParagraphSeparator:
                    _levels[i] = (byte)_paragraphLevel;
                    break;

                case BidiClass.BoundaryNeutral:
                    _levels[i] = stackLevel[top];
                    break;

                default:
                    _levels[i] = stackLevel[top];
                    ApplyOverride(i, stackOverride[top]);
                    break;
            }
        }
    }

    private void ApplyOverride(int i, byte status)
    {
        if (status != 0)
        {
            _types[i] = status == 1 ? BidiClass.LeftToRight : BidiClass.RightToLeft;
        }
    }

    /// <summary>X10, then W1 to I2 on every isolating run sequence.</summary>
    private void ResolveSequences()
    {
        // Level runs over the characters X9 keeps: their first and last kept index, in order.
        List<(int Start, int End)> runs = [];
        int runStart = -1;
        int last = -1;
        for (int i = 0; i < _length; i++)
        {
            if (IsRemovedByX9(_initial[i]))
            {
                continue;
            }

            if (runStart < 0)
            {
                runStart = i;
            }
            else if (_levels[i] != _levels[last])
            {
                runs.Add((runStart, last));
                runStart = i;
            }

            last = i;
        }

        if (runStart >= 0)
        {
            runs.Add((runStart, last));
        }

        // Index of the level run each kept character starts, for following isolate initiators to their PDI's run.
        Dictionary<int, int> runAt = [];
        for (int r = 0; r < runs.Count; r++)
        {
            runAt[runs[r].Start] = r;
        }

        bool[] used = new bool[runs.Count];
        for (int r = 0; r < runs.Count; r++)
        {
            if (used[r])
            {
                continue;
            }

            // A run starting with a PDI that matches an initiator belongs to that initiator's sequence.
            int count = 0;
            int current = r;
            while (true)
            {
                used[current] = true;
                (int start, int end) = runs[current];
                for (int i = start; i <= end; i++)
                {
                    if (!IsRemovedByX9(_initial[i]))
                    {
                        _sequence[count++] = i;
                    }
                }

                int pdi = IsIsolateInitiator(_initial[end]) ? _matchingPdi[end] : -1;
                if (pdi < 0 || !runAt.TryGetValue(pdi, out int next))
                {
                    break;
                }

                current = next;
            }

            ResolveSequence(_sequence.AsSpan(0, count));
        }
    }

    private void ResolveSequence(Span<int> seq)
    {
        int first = seq[0];
        int lastIndex = seq[^1];
        int level = _levels[first];

        int before = first - 1;
        while (before >= 0 && IsRemovedByX9(_initial[before]))
        {
            before--;
        }

        int sosLevel = Math.Max(level, before >= 0 ? _explicit[before] : _paragraphLevel);
        int eosLevel;
        if (IsIsolateInitiator(_initial[lastIndex]))
        {
            eosLevel = Math.Max(level, _paragraphLevel);
        }
        else
        {
            int after = lastIndex + 1;
            while (after < _length && IsRemovedByX9(_initial[after]))
            {
                after++;
            }

            eosLevel = Math.Max(level, after < _length ? _explicit[after] : _paragraphLevel);
        }

        BidiClass sos = (sosLevel & 1) != 0 ? BidiClass.RightToLeft : BidiClass.LeftToRight;
        BidiClass eos = (eosLevel & 1) != 0 ? BidiClass.RightToLeft : BidiClass.LeftToRight;
        BidiClass[] t = _types;

        // W1: NSM takes the previous type, or ON after an isolate initiator or PDI.
        for (int k = 0; k < seq.Length; k++)
        {
            if (t[seq[k]] == BidiClass.NonspacingMark)
            {
                if (k == 0)
                {
                    t[seq[k]] = sos;
                }
                else
                {
                    BidiClass prev = t[seq[k - 1]];
                    t[seq[k]] = IsIsolateInitiator(prev) || prev == BidiClass.PopDirectionalIsolate ? BidiClass.OtherNeutral : prev;
                }
            }
        }

        // W2 and W3.
        BidiClass strong = sos;
        for (int k = 0; k < seq.Length; k++)
        {
            BidiClass c = t[seq[k]];
            if (c is BidiClass.LeftToRight or BidiClass.RightToLeft or BidiClass.ArabicLetter)
            {
                strong = c;
            }
            else if (c == BidiClass.EuropeanNumber && strong == BidiClass.ArabicLetter)
            {
                t[seq[k]] = BidiClass.ArabicNumber;
            }
        }

        for (int k = 0; k < seq.Length; k++)
        {
            if (t[seq[k]] == BidiClass.ArabicLetter)
            {
                t[seq[k]] = BidiClass.RightToLeft;
            }
        }

        // W4: a single separator between two numbers of the same type.
        for (int k = 1; k + 1 < seq.Length; k++)
        {
            BidiClass c = t[seq[k]];
            BidiClass prev = t[seq[k - 1]];
            BidiClass next = t[seq[k + 1]];
            if (c == BidiClass.EuropeanSeparator && prev == BidiClass.EuropeanNumber && next == BidiClass.EuropeanNumber)
            {
                t[seq[k]] = BidiClass.EuropeanNumber;
            }
            else if (c == BidiClass.CommonSeparator && prev == next && prev is BidiClass.EuropeanNumber or BidiClass.ArabicNumber)
            {
                t[seq[k]] = prev;
            }
        }

        // W5: terminators next to European numbers.
        for (int k = 0; k < seq.Length; k++)
        {
            if (t[seq[k]] != BidiClass.EuropeanTerminator)
            {
                continue;
            }

            int end = k;
            while (end < seq.Length && t[seq[end]] == BidiClass.EuropeanTerminator)
            {
                end++;
            }

            bool adjacent = (k > 0 && t[seq[k - 1]] == BidiClass.EuropeanNumber) || (end < seq.Length && t[seq[end]] == BidiClass.EuropeanNumber);
            if (adjacent)
            {
                for (int j = k; j < end; j++)
                {
                    t[seq[j]] = BidiClass.EuropeanNumber;
                }
            }

            k = end - 1;
        }

        // W6 and W7.
        for (int k = 0; k < seq.Length; k++)
        {
            if (t[seq[k]] is BidiClass.EuropeanSeparator or BidiClass.EuropeanTerminator or BidiClass.CommonSeparator)
            {
                t[seq[k]] = BidiClass.OtherNeutral;
            }
        }

        strong = sos;
        for (int k = 0; k < seq.Length; k++)
        {
            BidiClass c = t[seq[k]];
            if (c is BidiClass.LeftToRight or BidiClass.RightToLeft)
            {
                strong = c;
            }
            else if (c == BidiClass.EuropeanNumber && strong == BidiClass.LeftToRight)
            {
                t[seq[k]] = BidiClass.LeftToRight;
            }
        }

        BidiClass embedding = (level & 1) != 0 ? BidiClass.RightToLeft : BidiClass.LeftToRight;
        if (!_codePoints.IsEmpty)
        {
            ResolvePairedBrackets(seq, sos, embedding);
        }

        // N1 and N2: runs of neutrals and isolate formatting characters.
        for (int k = 0; k < seq.Length; k++)
        {
            if (!IsNeutralOrIsolate(t[seq[k]]))
            {
                continue;
            }

            int end = k;
            while (end < seq.Length && IsNeutralOrIsolate(t[seq[end]]))
            {
                end++;
            }

            BidiClass leading = k == 0 ? sos : StrongForNeutrals(t[seq[k - 1]]);
            BidiClass trailing = end == seq.Length ? eos : StrongForNeutrals(t[seq[end]]);
            BidiClass resolved = leading == trailing ? leading : embedding;
            for (int j = k; j < end; j++)
            {
                t[seq[j]] = resolved;
            }

            k = end - 1;
        }

        // I1 and I2.
        for (int k = 0; k < seq.Length; k++)
        {
            int i = seq[k];
            BidiClass c = t[i];
            if ((level & 1) == 0)
            {
                if (c == BidiClass.RightToLeft)
                {
                    _levels[i] = (byte)(level + 1);
                }
                else if (c is BidiClass.ArabicNumber or BidiClass.EuropeanNumber)
                {
                    _levels[i] = (byte)(level + 2);
                }
            }
            else if (c is BidiClass.LeftToRight or BidiClass.EuropeanNumber or BidiClass.ArabicNumber)
            {
                _levels[i] = (byte)(level + 1);
            }
        }

        // Characters X9 removed take the level of the character before them, for callers that keep them.
        for (int k = 0; k < seq.Length; k++)
        {
            int i = seq[k] + 1;
            while (i < _length && IsRemovedByX9(_initial[i]))
            {
                _levels[i] = _levels[i - 1];
                i++;
            }
        }

        if (first > 0 && before < 0)
        {
            for (int i = 0; i < first; i++)
            {
                _levels[i] = (byte)_paragraphLevel;
            }
        }
    }

    private static bool IsNeutralOrIsolate(BidiClass c) => c is BidiClass.ParagraphSeparator or BidiClass.SegmentSeparator or BidiClass.WhiteSpace
        or BidiClass.OtherNeutral or BidiClass.LeftToRightIsolate or BidiClass.RightToLeftIsolate or BidiClass.FirstStrongIsolate
        or BidiClass.PopDirectionalIsolate;

    private static BidiClass StrongForNeutrals(BidiClass c) => c is BidiClass.EuropeanNumber or BidiClass.ArabicNumber ? BidiClass.RightToLeft : c;

    /// <summary>BD16 and N0.</summary>
    private void ResolvePairedBrackets(Span<int> seq, BidiClass sos, BidiClass embedding)
    {
        ReadOnlySpan<int> cps = _codePoints.Span;
        BidiClass[] t = _types;
        List<(int Open, int Close)> pairs = [];
        Span<int> stackBracket = stackalloc int[MaxBracketStack];
        Span<int> stackPosition = stackalloc int[MaxBracketStack];
        int depth = 0;
        for (int k = 0; k < seq.Length; k++)
        {
            int i = seq[k];
            if (t[i] != BidiClass.OtherNeutral)
            {
                continue;
            }

            int cp = cps[i];
            int row = UnicodeData.FindPair(UnicodeTables.Brackets, 3, cp);
            if (row < 0)
            {
                continue;
            }

            if (UnicodeTables.Brackets[row + 2] == 1)
            {
                if (depth == MaxBracketStack)
                {
                    break;
                }

                stackBracket[depth] = Canonical(UnicodeTables.Brackets[row + 1]);
                stackPosition[depth] = k;
                depth++;
            }
            else
            {
                int closing = Canonical(cp);
                for (int d = depth - 1; d >= 0; d--)
                {
                    if (stackBracket[d] == closing)
                    {
                        pairs.Add((stackPosition[d], k));
                        depth = d;
                        break;
                    }
                }
            }
        }

        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
        foreach ((int open, int close) in pairs)
        {
            BidiClass inside = BidiClass.OtherNeutral;
            for (int k = open + 1; k < close; k++)
            {
                BidiClass c = StrongForNeutrals(t[seq[k]]);
                if (c == embedding)
                {
                    inside = embedding;
                    break;
                }

                if (c is BidiClass.LeftToRight or BidiClass.RightToLeft)
                {
                    inside = c;
                }
            }

            if (inside == BidiClass.OtherNeutral)
            {
                continue;
            }

            BidiClass resolved = embedding;
            if (inside != embedding)
            {
                BidiClass context = sos;
                for (int k = open - 1; k >= 0; k--)
                {
                    BidiClass c = StrongForNeutrals(t[seq[k]]);
                    if (c is BidiClass.LeftToRight or BidiClass.RightToLeft)
                    {
                        context = c;
                        break;
                    }
                }

                resolved = context == inside ? inside : embedding;
            }

            SetBracket(seq, open, resolved);
            SetBracket(seq, close, resolved);
        }
    }

    private void SetBracket(Span<int> seq, int k, BidiClass resolved)
    {
        _types[seq[k]] = resolved;
        for (int j = k + 1; j < seq.Length && _initial[seq[j]] == BidiClass.NonspacingMark; j++)
        {
            _types[seq[j]] = resolved;
        }
    }

    /// <summary>Brackets compare under canonical equivalence: U+2329 and U+232A equal U+3008 and U+3009.</summary>
    private static int Canonical(int codePoint)
    {
        int row = UnicodeData.FindPair(UnicodeTables.Decompositions, 3, codePoint);
        return row >= 0 && UnicodeTables.Decompositions[row + 2] == 0 ? UnicodeTables.Decompositions[row + 1] : codePoint;
    }
}
