using Egami.Fonts.Cff;
using Egami.Fonts.Parsing;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// Rewrites the Type 2 charstrings of a subset operator by operator (Adobe Technical Note #5177; CFF2 in OpenType 1.9.1): CFF2
/// blends resolved at the instance's scalars, vsindex dropped, the hints and masks kept (or all dropped), the path written again
/// with the shortest operators, and the subroutines kept when every call of one writes the same charstring, renumbered by use,
/// otherwise inlined into their callers.
/// </summary>
/// <remarks>
/// <para>
/// Each charstring the subset writes is a list of tokens. An operator whose operands were all pushed by its own charstring is
/// written again from its values: rounded (a CFF2 instance) where each point lies relative to the start of the charstring, as
/// fontTools' otRound rounds (half up), with an exact zero kept zero so straight lines stay straight, and with the shortest
/// operators. An operator that takes operands pushed by another charstring, as subroutinized fonts do across a call, is written
/// as it was, each operand rounded where it was pushed, so the subroutine keeps working for every caller.
/// </para>
/// <para>
/// Charstrings that use what the writer does not model (CFF's arithmetic and storage operators, seac where CFF does not allow it,
/// a reserved operator) are drawn by the caller's fallback.
/// </para>
/// </remarks>
internal sealed class CharStringWriter
{
    // CFF's operand stack, which every written operator must fit.
    private const int MaxOperands = 48;

    // Passes over the glyphs before every subroutine is inlined; each pass only adds subroutines to inline.
    private const int MaxPasses = 24;

    private readonly CffFont _font;
    private readonly float[]? _scalars;
    private readonly bool _round;
    private readonly bool _dropHints;
    private readonly bool _allowSeac;

    private readonly HashSet<CharStringId> _inline = [];
    private readonly Dictionary<CharStringId, Frame> _subrs = [];
    private readonly List<Frame> _frames = [];
    private readonly Stack<Frame> _pool = [];
    private readonly List<CharStringId> _marked = [];
    private readonly List<CharStringId> _seen = [];
    private readonly List<(CharStringId Id, Frame Frame)> _called = [];
    private readonly double[] _stack = new double[CharStringInterpreter.MaxStackCff2];
    private readonly double[] _rounded = new double[CharStringInterpreter.MaxStackCff2];
    private readonly List<double> _trueDeltas = [];
    private readonly List<double> _roundedDeltas = [];
    private readonly (int Frame, int Token)[] _slot = new (int, int)[CharStringInterpreter.MaxStackCff2];
    private bool _changed;
    private bool _inlineAll;
    private int _sp;
    private int _stems;
    private int _vsindex;
    private int _operators;
    private int _fontDict;
    private bool _haveWidth;
    private double? _width;
    private bool _ended;
    private bool _abort;

    public CharStringWriter(CffFont font, float[]? scalars, bool round, bool dropHints, bool allowSeac)
    {
        _font = font;
        _scalars = scalars;
        _round = round;
        _dropHints = dropHints;
        _allowSeac = allowSeac;
    }

    /// <summary>Gets the charstring of each glyph of the subset, in its order.</summary>
    public byte[][] Glyphs { get; private set; } = [];

    /// <summary>Gets the global subroutines kept, renumbered.</summary>
    public List<byte[]> GlobalSubrs { get; } = [];

    /// <summary>Gets the local subroutines kept for each of the source's font dicts, renumbered.</summary>
    public Dictionary<int, List<byte[]>> LocalSubrs { get; } = [];

    private enum Kind : byte
    {
        // An operand on the stack not taken yet, one written as a number, one left out.
        Pending,
        Number,
        Skip,

        // Operators written from their values.
        Move,
        Line,
        Curve,
        Flex,
        Stem,
        Mask,
        End,

        // A subroutine call, and an operator written as it was, its operands numbers before it.
        Call,
        Raw,
    }

    /// <summary>
    /// Writes the charstrings of the glyphs <paramref name="oldIds"/> lists (a glyph <paramref name="map"/> does not keep at its
    /// position gets an empty charstring). <paramref name="width"/> gives each glyph's width operand for CFF2 (null for the
    /// default width); for CFF the charstring's own width is kept. <paramref name="fallback"/> draws a glyph the writer cannot
    /// rewrite, given its width operand.
    /// </summary>
    public void Write(ushort[] oldIds, Dictionary<ushort, ushort> map, Func<int, double?>? width, Func<int, double?, byte[]> fallback)
    {
        var roots = new Frame?[oldIds.Length];
        var widths = new double?[oldIds.Length];

        // What each glyph's last run called, and how many subroutines were marked to inline before it: a glyph runs again in a
        // later pass only when one it called was marked since.
        var seen = new CharStringId[]?[oldIds.Length];
        var called = new (CharStringId, Frame)[]?[oldIds.Length];
        var markedAt = new int[oldIds.Length];
        bool inlineAllBefore = false;
        for (int pass = 0; ; pass++)
        {
            _subrs.Clear();
            _changed = false;
            bool rerunAll = _inlineAll != inlineAllBefore;
            inlineAllBefore = _inlineAll;
            for (int g = 0; g < oldIds.Length; g++)
            {
                ushort old = oldIds[g];
                if (!map.TryGetValue(old, out ushort now) || now != g)
                {
                    continue;
                }

                if (!rerunAll && seen[g] is CharStringId[] ids && !MarkedSince(ids, markedAt[g]))
                {
                    _called.Clear();
                    _called.AddRange(called[g]!);
                    Commit();
                    continue;
                }

                // A glyph runs again while it adds subroutines to inline, so nested ones are inlined in one pass.
                for (int tries = 0; ; tries++)
                {
                    int inlined = _inline.Count;
                    markedAt[g] = _marked.Count;
                    roots[g] = Run(old);
                    if (roots[g] is null)
                    {
                        widths[g] = width?.Invoke(old);
                        _called.Clear();
                    }
                    else
                    {
                        widths[g] = width is null ? _width : width(old);
                    }

                    if (_inline.Count == inlined || tries >= MaxPasses)
                    {
                        break;
                    }
                }

                seen[g] = [.. _seen];
                called[g] = [.. _called];
                Commit();
            }

            if (!_changed)
            {
                // Stable: inline the subroutines that cost more kept than inlined, and run again if any.
                Dictionary<CharStringId, int> counts = StaticCounts(roots);
                foreach ((CharStringId id, Frame frame) in _subrs)
                {
                    int calls = counts.GetValueOrDefault(id);
                    if (calls == 0)
                    {
                        continue;
                    }

                    int body = EncodedSize(frame);
                    if (calls == 1 || body * (calls - 1) <= (2 * calls) + 3)
                    {
                        Mark(id);
                    }
                }

                if (!_changed)
                {
                    Finish(oldIds, map, roots, widths, counts, fallback);
                    return;
                }
            }

            if (pass >= MaxPasses)
            {
                _inlineAll = true;
            }
        }
    }

    // Whether a subroutine of ids was marked to inline after the first `from` marks.
    private bool MarkedSince(CharStringId[] ids, int from)
    {
        for (int m = from; m < _marked.Count; m++)
        {
            if (Array.IndexOf(ids, _marked[m]) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private void Finish(ushort[] oldIds, Dictionary<ushort, ushort> map, Frame?[] roots, double?[] widths, Dictionary<CharStringId, int> counts, Func<int, double?, byte[]> fallback)
    {
        // Numbers: the most called first, so they take the shortest operands.
        var globalIds = new List<CharStringId>();
        var localIds = new Dictionary<int, List<CharStringId>>();
        foreach (CharStringId id in _subrs.Keys)
        {
            if (counts.GetValueOrDefault(id) == 0)
            {
                continue;
            }

            if (id.Kind == CharStringKind.Global)
            {
                globalIds.Add(id);
            }
            else
            {
                if (!localIds.TryGetValue(id.FontDict, out List<CharStringId>? list))
                {
                    localIds[id.FontDict] = list = [];
                }

                list.Add(id);
            }
        }

        var numbers = new Dictionary<CharStringId, int>();
        int globalCount = Number(globalIds, counts, numbers);
        var localCounts = new Dictionary<int, int>();
        foreach ((int fd, List<CharStringId> ids) in localIds)
        {
            localCounts[fd] = Number(ids, counts, numbers);
        }

        int Operand(CharStringId id) => numbers[id] - CffFont.Bias(id.Kind == CharStringKind.Global ? globalCount : localCounts[id.FontDict]);

        Glyphs = new byte[oldIds.Length][];
        for (int g = 0; g < oldIds.Length; g++)
        {
            ushort old = oldIds[g];
            if (!map.TryGetValue(old, out ushort now) || now != g)
            {
                Glyphs[g] = [14];
                continue;
            }

            if (roots[g] is not Frame root)
            {
                Glyphs[g] = fallback(old, widths[g]);
                continue;
            }

            var w = new ByteWriter(64);
            if (widths[g] is double operand)
            {
                CffEncoding.CharStringNumber(w, operand);
            }

            Encode(root, w, Operand, glyph: true, widths[g] is not null);
            if (!root.Ends)
            {
                w.U8(14);
            }

            Glyphs[g] = w.ToArray();
        }

        GlobalSubrs.AddRange(globalIds.Select(_ => Array.Empty<byte>()));
        foreach ((int fd, List<CharStringId> ids) in localIds)
        {
            LocalSubrs[fd] = [.. ids.Select(_ => Array.Empty<byte>())];
        }

        foreach ((CharStringId id, int number) in numbers)
        {
            Frame frame = _subrs[id];
            var w = new ByteWriter(32);
            Encode(frame, w, Operand, glyph: false, width: false);
            if (frame.LastKind() != Kind.End)
            {
                w.U8(11);
            }

            (id.Kind == CharStringKind.Global ? GlobalSubrs : LocalSubrs[id.FontDict])[number] = w.ToArray();
        }
    }

    private static int Number(List<CharStringId> ids, Dictionary<CharStringId, int> counts, Dictionary<CharStringId, int> numbers)
    {
        ids.Sort((a, b) => counts[b] != counts[a] ? counts[b].CompareTo(counts[a]) : a.Index.CompareTo(b.Index));
        for (int i = 0; i < ids.Count; i++)
        {
            numbers[ids[i]] = i;
        }

        return ids.Count;
    }

    // How many calls of each kept subroutine the written charstrings hold.
    private Dictionary<CharStringId, int> StaticCounts(Frame?[] roots)
    {
        var counts = new Dictionary<CharStringId, int>();
        void Count(Frame frame)
        {
            foreach (Token token in frame.Tokens)
            {
                if (token.Kind == Kind.Call)
                {
                    counts[token.Callee] = counts.GetValueOrDefault(token.Callee) + 1;
                }
            }
        }

        foreach (Frame? root in roots)
        {
            if (root is not null)
            {
                Count(root);
            }
        }

        foreach (Frame frame in _subrs.Values)
        {
            Count(frame);
        }

        return counts;
    }

    private static int EncodedSize(Frame frame)
    {
        var w = new ByteWriter(32);
        Encode(frame, w, _ => 0, glyph: false, width: false);
        return w.Length + 1;
    }

    // The glyph's root frame, or null when the writer does not model it and the caller draws it instead.
    private Frame? Run(int glyph)
    {
        if ((uint)glyph >= (uint)_font.GlyphCount)
        {
            throw Error($"Glyph {glyph} is past the CFF data's {_font.GlyphCount} charstrings.");
        }

        _fontDict = _font.FontDictOf(glyph);
        _sp = 0;
        _stems = 0;
        _operators = 0;
        _haveWidth = false;
        _width = null;
        _ended = false;
        _abort = false;
        _vsindex = _font.Privates[_fontDict].VsIndex;

        // The frames of the run before were copied out: their lists are used again.
        foreach (Frame old in _frames)
        {
            _pool.Push(old);
        }

        _frames.Clear();
        _called.Clear();
        _seen.Clear();
        Frame root = NewFrame(new CharStringId(CharStringKind.Glyph, _fontDict, glyph), -1);
        Execute(_font.CharStrings[glyph], 0, root);
        if (_abort)
        {
            return null;
        }

        // Each frame copied out with what it writes only: operands nothing took and those left out go.
        var compact = new Frame[_frames.Count];
        for (int f = 0; f < compact.Length; f++)
        {
            compact[f] = _frames[f].Compact();
        }

        for (int c = 0; c < _called.Count; c++)
        {
            _called[c] = (_called[c].Id, compact[_called[c].Frame.Serial]);
        }

        root = compact[0];

        return root;
    }

    // Each kept subroutine's charstring must be the one its other calls wrote.
    private void Commit()
    {
        foreach ((CharStringId id, Frame child) in _called)
        {
            if (child.LastKind() is null)
            {
                Mark(id);
            }
            else if (_subrs.TryGetValue(id, out Frame? before))
            {
                if (!before.SameAs(child))
                {
                    Mark(id);
                }
            }
            else
            {
                _subrs[id] = child;
            }
        }

        _called.Clear();
    }

    private Frame NewFrame(CharStringId id, int parent)
    {
        Frame frame = _pool.Count > 0 ? _pool.Pop() : new Frame();
        frame.Reset(_frames.Count, parent, id);
        _frames.Add(frame);
        return frame;
    }

    private void Execute(ReadOnlySpan<byte> code, int depth, Frame frame)
    {
        if (depth > CharStringInterpreter.MaxSubrDepth)
        {
            throw Error("Charstring subroutines nest deeper than 10 levels.");
        }

        bool cff2 = _font.IsCff2;
        int i = 0;
        while (i < code.Length && !_ended)
        {
            int b0 = code[i];
            if (b0 >= 32 || b0 == 28)
            {
                double value;
                if (b0 == 28)
                {
                    Need(code, i, 3);
                    value = (short)((code[i + 1] << 8) | code[i + 2]);
                    i += 3;
                }
                else if (b0 <= 246)
                {
                    value = b0 - 139;
                    i++;
                }
                else if (b0 <= 250)
                {
                    Need(code, i, 2);
                    value = ((b0 - 247) * 256) + code[i + 1] + 108;
                    i += 2;
                }
                else if (b0 <= 254)
                {
                    Need(code, i, 2);
                    value = (-(b0 - 251) * 256) - code[i + 1] - 108;
                    i += 2;
                }
                else
                {
                    Need(code, i, 5);
                    value = ((code[i + 1] << 24) | (code[i + 2] << 16) | (code[i + 3] << 8) | code[i + 4]) / 65536.0;
                    i += 5;
                }

                Push(value, frame);
                continue;
            }

            if (++_operators > CharStringInterpreter.MaxOperators)
            {
                throw Error("A charstring runs more operators than allowed.");
            }

            i++;
            int op = b0;
            if (b0 == 12)
            {
                Need(code, i - 1, 2);
                op = 1200 + code[i++];
            }

            switch (op)
            {
                case 1: // hstem
                case 3: // vstem
                case 18: // hstemhm
                case 23: // vstemhm
                    {
                        TakeWidth(_sp % 2 == 1);
                        _stems += _sp / 2;
                        if (_dropHints)
                        {
                            Drop(0);
                        }
                        else if (Take(frame, 0))
                        {
                            if (_sp > 0)
                            {
                                Stems(frame, Kind.Stem, op, _sp, ReadOnlySpan<byte>.Empty);
                            }
                        }
                        else
                        {
                            Raw(frame, op, ReadOnlySpan<byte>.Empty);
                        }

                        _sp = 0;
                        break;
                    }

                case 19: // hintmask
                case 20: // cntrmask
                    {
                        TakeWidth(_sp % 2 == 1);
                        _stems += _sp / 2;
                        int length = (_stems + 7) / 8;
                        if (i + length > code.Length)
                        {
                            throw Error("A hintmask runs past the end of its charstring.");
                        }

                        ReadOnlySpan<byte> mask = code.Slice(i, length);
                        if (_dropHints)
                        {
                            Drop(0);
                        }
                        else if (Take(frame, 0))
                        {
                            Stems(frame, Kind.Mask, op, _sp - (_sp % 2), mask);
                        }
                        else
                        {
                            Raw(frame, op, mask);
                        }

                        i += length;
                        _sp = 0;
                        break;
                    }

                case 21: // rmoveto
                    TakeWidth(_sp > 2);
                    Check(2);
                    PathOp(frame, op);
                    break;
                case 22: // hmoveto
                case 4: // vmoveto
                    TakeWidth(_sp > 1);
                    Check(1);
                    PathOp(frame, op);
                    break;
                case 5: // rlineto
                case 6: // hlineto
                case 7: // vlineto
                case 8: // rrcurveto
                case 24: // rcurveline
                case 25: // rlinecurve
                case 26: // vvcurveto
                case 27: // hhcurveto
                case 30: // vhcurveto
                case 31: // hvcurveto
                    PathOp(frame, op);
                    break;
                case 1234: // hflex
                    Check(7);
                    PathOp(frame, op);
                    break;
                case 1235: // flex
                    Check(13);
                    PathOp(frame, op);
                    break;
                case 1236: // hflex1
                    Check(9);
                    PathOp(frame, op);
                    break;
                case 1237: // flex1
                    Check(11);
                    PathOp(frame, op);
                    break;
                case 10: // callsubr
                case 29: // callgsubr
                    Call(op == 29, depth, frame);
                    break;
                case 11 when !cff2: // return
                    return;
                case 15 when cff2: // vsindex
                    Check(1);
                    _vsindex = (int)_stack[_sp - 1];
                    Drop(_sp - 1);
                    _sp--;
                    break;
                case 16 when cff2: // blend
                    Blend(frame);
                    break;
                case 14 when !cff2: // endchar
                    TakeWidth(_sp is 1 or 5);
                    if (_sp >= 4)
                    {
                        if (!_allowSeac)
                        {
                            Abort();
                            return;
                        }

                        Drop(0, _sp - 4);
                        if (Take(frame, _sp - 4))
                        {
                            int start = frame.Values.Count;
                            for (int k = _sp - 4; k < _sp; k++)
                            {
                                frame.Values.Add(_stack[k]);
                            }

                            frame.Tokens.Add(new Token(Kind.End, 14, start, 4));
                        }
                        else
                        {
                            Raw(frame, 14, ReadOnlySpan<byte>.Empty, _sp - 4);
                        }
                    }
                    else
                    {
                        Drop(0);
                        frame.Tokens.Add(new Token(Kind.End, 14, frame.Values.Count, 0));
                    }

                    MarkEnded(frame);
                    _sp = 0;
                    return;
                case 1200 when !cff2: // dotsection, deprecated
                    Drop(0);
                    _sp = 0;
                    break;
                default:
                    // Arithmetic, storage and reserved operators: the caller draws the glyph instead.
                    Abort();
                    return;
            }
        }
    }

    // Stops the glyph: the caller draws it instead. (A glyph whose run sets only _abort goes on, to mark all it must inline.)
    private void Abort()
    {
        _abort = true;
        _ended = true;
    }

    private void MarkEnded(Frame frame)
    {
        _ended = true;
        for (int f = frame.Serial; f >= 0; f = _frames[f].Parent)
        {
            _frames[f].Ends = true;
        }
    }

    private void Call(bool global, int depth, Frame frame)
    {
        Check(1);
        CffIndex? subrs = global ? _font.GlobalSubrs : _font.Privates[_fontDict].LocalSubrs;
        int count = subrs?.Count ?? 0;
        int index = (int)_stack[_sp - 1] + CffFont.Bias(count);
        if (subrs is null || (uint)index >= (uint)count)
        {
            throw Error($"A charstring calls {(global ? "global" : "local")} subroutine {index} of {count}.");
        }

        var id = new CharStringId(global ? CharStringKind.Global : CharStringKind.Local, global ? 0 : _fontDict, index);
        _seen.Add(id);
        if (_inlineAll || _inline.Contains(id))
        {
            Drop(_sp - 1);
            _sp--;
            Execute(subrs.Value[index], depth + 1, frame);
            return;
        }

        if (!Take(frame, _sp - 1))
        {
            // A subroutine number pushed elsewhere: inlined from the next run, and the glyph drawn if this is the last; this run
            // goes on with it inlined, to find the others to inline.
            Mark(id);
            _abort = true;
            Drop(_sp - 1);
            _sp--;
            Execute(subrs.Value[index], depth + 1, frame);
            return;
        }

        _sp--;

        Frame child = NewFrame(id, frame.Serial);
        child.BaseTrueX = frame.BaseTrueX + frame.TrueX;
        child.BaseTrueY = frame.BaseTrueY + frame.TrueY;
        child.BaseRoundX = frame.BaseRoundX + frame.RoundX;
        child.BaseRoundY = frame.BaseRoundY + frame.RoundY;
        frame.Tokens.Add(new Token(Kind.Call, global ? 29 : 10, 0, 0, Callee: id));
        Execute(subrs.Value[index], depth + 1, child);
        frame.TrueX += child.TrueX;
        frame.TrueY += child.TrueY;
        frame.RoundX += child.RoundX;
        frame.RoundY += child.RoundY;
        _called.Add((id, child));
    }

    // blend: n default values, n * k deltas and n; the n values become the defaults plus each region's delta times its scalar.
    private void Blend(Frame frame)
    {
        Check(1);
        int n = (int)_stack[_sp - 1];
        Variations.ItemVariationStore? store = _font.VariationStore;
        int k = store?.RegionCountOf(_vsindex) ?? 0;
        long need = ((long)n * (k + 1)) + 1;
        if (n < 0 || need > _sp)
        {
            throw Error("A CFF2 blend has too few operands.");
        }

        int start = _sp - (int)need;
        if (!Take(frame, start))
        {
            // Values blended across a call: inlined from the next run, and the glyph drawn if this is the last; this run goes on, to
            // find the others to inline.
            for (int e = start; e < _sp; e++)
            {
                Cross(_slot[e].Frame, frame.Serial);
            }

            _abort = true;
            Drop(start);
        }

        if (_scalars is not null && k > 0)
        {
            ReadOnlySpan<ushort> regions = store!.RegionIndicesOf(_vsindex);
            for (int i = 0; i < n; i++)
            {
                double value = _stack[start + i];
                int deltas = start + n + (i * k);
                for (int j = 0; j < k; j++)
                {
                    float scalar = (uint)regions[j] < (uint)_scalars.Length ? _scalars[regions[j]] : 0;
                    if (scalar != 0)
                    {
                        value += _stack[deltas + j] * scalar;
                    }
                }

                _stack[start + i] = value;
            }
        }

        _sp = start;
        for (int i = 0; i < n; i++)
        {
            Push(_stack[start + i], frame);
        }
    }

    private void TakeWidth(bool hasWidth)
    {
        if (_haveWidth)
        {
            return;
        }

        _haveWidth = true;
        if (!_font.IsCff2 && hasWidth && _sp > 0)
        {
            _width = _stack[0];
            Drop(0, 1);
            Array.Copy(_stack, 1, _stack, 0, _sp - 1);
            Array.Copy(_slot, 1, _slot, 0, _sp - 1);
            _sp--;
        }
    }

    private void Push(double value, Frame frame)
    {
        if (_sp >= (_font.IsCff2 ? CharStringInterpreter.MaxStackCff2 : CharStringInterpreter.MaxStack))
        {
            throw Error("A charstring overflows the operand stack.");
        }

        _slot[_sp] = (frame.Serial, frame.Tokens.Count);
        frame.Tokens.Add(new Token(Kind.Pending, 0, frame.Values.Count, 1));
        frame.Values.Add(value);
        _stack[_sp++] = value;
    }

    // Takes the operands from `from` up for an operator of frame: true when they all are the last of its own pushes, which are then
    // dropped from its tokens for the operator to write; otherwise each becomes a number token where it was pushed.
    private bool Take(Frame frame, int from)
    {
        bool local = true;
        int first = frame.Tokens.Count;
        for (int e = from; e < _sp; e++)
        {
            if (_slot[e].Frame != frame.Serial)
            {
                local = false;
                break;
            }

            first = Math.Min(first, _slot[e].Token);
        }

        if (local)
        {
            // Only these operands, or tokens left out, may follow the first of them.
            int owned = 0;
            for (int t = first; t < frame.Tokens.Count; t++)
            {
                if (frame.Tokens[t].Kind == Kind.Pending)
                {
                    owned++;
                }
                else if (frame.Tokens[t].Kind != Kind.Skip)
                {
                    local = false;
                    break;
                }
            }

            local &= owned == _sp - from;
        }

        if (local)
        {
            frame.Tokens.RemoveRange(first, frame.Tokens.Count - first);
            return true;
        }

        for (int e = from; e < _sp; e++)
        {
            (int f, int t) = _slot[e];
            List<Token> tokens = _frames[f].Tokens;
            tokens[t] = tokens[t] with { Kind = Kind.Number };
            if (_round)
            {
                _frames[f].Values[tokens[t].Start] = Math.Floor(_stack[e] + 0.5);
            }
        }

        return false;
    }

    // Leaves out the operands from `from` (count of them, or to the top) wherever they were pushed.
    private void Drop(int from, int count = -1)
    {
        int to = count < 0 ? _sp : from + count;
        for (int e = from; e < to; e++)
        {
            (int f, int t) = _slot[e];
            List<Token> tokens = _frames[f].Tokens;
            tokens[t] = tokens[t] with { Kind = Kind.Skip };
        }
    }

    // An operator written as it was, after operands written where they were pushed.
    private void Raw(Frame frame, int op, ReadOnlySpan<byte> mask, int from = 0)
    {
        if (_sp - from > MaxOperands - 1)
        {
            // Too many operands for CFF with a width: inlined from the next run, and the glyph drawn if this is the last.
            for (int e = from; e < _sp; e++)
            {
                Cross(_slot[e].Frame, frame.Serial);
            }

            _abort = true;
        }

        int start = frame.Masks.Count;
        foreach (byte b in mask)
        {
            frame.Masks.Add(b);
        }

        frame.Tokens.Add(new Token(Kind.Raw, op, 0, 0, start, mask.Length));
    }

    private void PathOp(Frame frame, int op)
    {
        if (Take(frame, 0))
        {
            Decompose(op, _stack, frame, Mode.Write);
        }
        else
        {
            Raw(frame, op, ReadOnlySpan<byte>.Empty);
            if (_round)
            {
                // The pen moves by the operands as they were and as they are written, each rounded.
                for (int e = 0; e < _sp; e++)
                {
                    _rounded[e] = Math.Floor(_stack[e] + 0.5);
                }

                _trueDeltas.Clear();
                _roundedDeltas.Clear();
                Decompose(op, _stack, frame, Mode.True);
                Decompose(op, _rounded, frame, Mode.Rounded);
                bool far = false;
                for (int k = 0; k < _trueDeltas.Count; k++)
                {
                    if (k % 2 == 0)
                    {
                        frame.TrueX += _trueDeltas[k];
                        frame.RoundX += _roundedDeltas[k];
                    }
                    else
                    {
                        frame.TrueY += _trueDeltas[k];
                        frame.RoundY += _roundedDeltas[k];
                    }

                    far |= Far(frame);
                }

                if (far && !_abort)
                {
                    // (A run that already failed goes on only to find what to inline; how far its points are waits for the next.)
                    // A point more than half a unit off: the subroutines that pushed the operands are inlined from the next pass.
                    for (int e = 0; e < _sp; e++)
                    {
                        Cross(_slot[e].Frame, frame.Serial);
                    }

                    Mark(frame.Id);
                }
            }
        }

        _sp = 0;
    }

    private enum Mode : byte
    {
        Write,
        True,
        Rounded,
    }

    // The segments of a path operator, written as values or only moving the pen.
    private void Decompose(int op, double[] s, Frame f, Mode mode)
    {
        int sp = _sp;
        switch (op)
        {
            case 21:
                Segment(f, mode, Kind.Move, s[sp - 2], s[sp - 1]);
                break;
            case 22:
                Segment(f, mode, Kind.Move, s[sp - 1], 0);
                break;
            case 4:
                Segment(f, mode, Kind.Move, 0, s[sp - 1]);
                break;
            case 5:
                for (int k = 0; k + 1 < sp; k += 2)
                {
                    Segment(f, mode, Kind.Line, s[k], s[k + 1]);
                }

                break;
            case 6:
            case 7:
                {
                    bool horizontal = op == 6;
                    for (int k = 0; k < sp; k++)
                    {
                        Segment(f, mode, Kind.Line, horizontal ? s[k] : 0, horizontal ? 0 : s[k]);
                        horizontal = !horizontal;
                    }

                    break;
                }

            case 8:
                for (int k = 0; k + 5 < sp; k += 6)
                {
                    Segment(f, mode, Kind.Curve, s[k], s[k + 1], s[k + 2], s[k + 3], s[k + 4], s[k + 5]);
                }

                break;
            case 24:
                {
                    int k = 0;
                    for (; sp - k >= 8; k += 6)
                    {
                        Segment(f, mode, Kind.Curve, s[k], s[k + 1], s[k + 2], s[k + 3], s[k + 4], s[k + 5]);
                    }

                    if (sp - k >= 2)
                    {
                        Segment(f, mode, Kind.Line, s[k], s[k + 1]);
                    }

                    break;
                }

            case 25:
                {
                    int k = 0;
                    for (; sp - k >= 8; k += 2)
                    {
                        Segment(f, mode, Kind.Line, s[k], s[k + 1]);
                    }

                    if (sp - k >= 6)
                    {
                        Segment(f, mode, Kind.Curve, s[k], s[k + 1], s[k + 2], s[k + 3], s[k + 4], s[k + 5]);
                    }

                    break;
                }

            case 26:
            case 27:
                {
                    // vvcurveto: dx1? {dya dxb dyb dyc}+; hhcurveto: dy1? {dxa dxb dyb dxc}+.
                    int k = 0;
                    double first = 0;
                    if (sp % 2 == 1)
                    {
                        first = s[0];
                        k = 1;
                    }

                    for (; k + 3 < sp; k += 4)
                    {
                        if (op == 26)
                        {
                            Segment(f, mode, Kind.Curve, first, s[k], s[k + 1], s[k + 2], 0, s[k + 3]);
                        }
                        else
                        {
                            Segment(f, mode, Kind.Curve, s[k], first, s[k + 1], s[k + 2], s[k + 3], 0);
                        }

                        first = 0;
                    }

                    break;
                }

            case 30:
            case 31:
                {
                    bool horizontal = op == 31;
                    for (int k = 0; k + 3 < sp; k += 4)
                    {
                        double extra = sp - k == 5 ? s[k + 4] : 0;
                        if (horizontal)
                        {
                            Segment(f, mode, Kind.Curve, s[k], 0, s[k + 1], s[k + 2], extra, s[k + 3]);
                        }
                        else
                        {
                            Segment(f, mode, Kind.Curve, 0, s[k], s[k + 1], s[k + 2], s[k + 3], extra);
                        }

                        horizontal = !horizontal;
                    }

                    break;
                }

            case 1234: // hflex
                Flex(f, mode, [s[0], 0, s[1], s[2], s[3], 0, s[4], 0, s[5], -s[2], s[6], 0], 50);
                break;
            case 1235: // flex
                Flex(f, mode, s.AsSpan(0, 12), s[12]);
                break;
            case 1236: // hflex1
                {
                    double dy1 = s[1], dy2 = s[3], dy5 = s[7];
                    Flex(f, mode, [s[0], dy1, s[2], dy2, s[4], 0, s[5], 0, s[6], dy5, s[8], -(dy1 + dy2 + dy5)], 50);
                    break;
                }

            case 1237: // flex1
                {
                    double dx = 0, dy = 0;
                    for (int k = 0; k < 10; k += 2)
                    {
                        dx += s[k];
                        dy += s[k + 1];
                    }

                    Span<double> d = stackalloc double[12];
                    s.AsSpan(0, 10).CopyTo(d);
                    (d[10], d[11]) = Math.Abs(dx) > Math.Abs(dy) ? (s[10], -dy) : (-dx, s[10]);
                    Flex(f, mode, d, 50);
                    break;
                }
        }
    }

    private void Segment(Frame f, Mode mode, Kind kind, double a, double b, double c = 0, double d = 0, double e = 0, double g = 0)
    {
        if (mode != Mode.Write)
        {
            List<double> deltas = mode == Mode.True ? _trueDeltas : _roundedDeltas;
            deltas.Add(a);
            deltas.Add(b);
            if (kind == Kind.Curve)
            {
                deltas.Add(c);
                deltas.Add(d);
                deltas.Add(e);
                deltas.Add(g);
            }

            return;
        }

        int start = f.Values.Count;
        f.Values.Add(X(f, a));
        f.Values.Add(Y(f, b));
        if (kind == Kind.Curve)
        {
            f.Values.Add(X(f, c));
            f.Values.Add(Y(f, d));
            f.Values.Add(X(f, e));
            f.Values.Add(Y(f, g));
        }

        f.Tokens.Add(new Token(kind, kind == Kind.Curve ? 8 : kind == Kind.Line ? 5 : 21, start, kind == Kind.Curve ? 6 : 2));
    }

    private void Flex(Frame f, Mode mode, ReadOnlySpan<double> d, double depth)
    {
        if (mode != Mode.Write)
        {
            foreach (double delta in d)
            {
                (mode == Mode.True ? _trueDeltas : _roundedDeltas).Add(delta);
            }

            return;
        }

        int start = f.Values.Count;
        for (int k = 0; k < 12; k += 2)
        {
            f.Values.Add(X(f, d[k]));
            f.Values.Add(Y(f, d[k + 1]));
        }

        f.Values.Add(depth);
        f.Tokens.Add(new Token(Kind.Flex, 1235, start, 13));
    }

    private double X(Frame f, double d)
    {
        if (!_round || d == 0)
        {
            return d;
        }

        f.TrueX += d;
        double r = Math.Floor(f.TrueX + 0.5) - f.RoundX;
        f.RoundX += r;
        if (!_abort && Far(f))
        {
            Mark(f.Id);
        }

        return r;
    }

    private double Y(Frame f, double d)
    {
        if (!_round || d == 0)
        {
            return d;
        }

        f.TrueY += d;
        double r = Math.Floor(f.TrueY + 0.5) - f.RoundY;
        f.RoundY += r;
        if (!_abort && Far(f))
        {
            Mark(f.Id);
        }

        return r;
    }

    // Whether the pen, as written, is more than half a unit from where it is in the font.
    private static bool Far(Frame f) =>
        Strict && (Math.Abs(f.BaseRoundX + f.RoundX - f.BaseTrueX - f.TrueX) > 0.5 + 1e-9 || Math.Abs(f.BaseRoundY + f.RoundY - f.BaseTrueY - f.TrueY) > 0.5 + 1e-9);

    private const bool Strict = true;

    // Stem edges, each pair an edge and a width relative to the one before, rounded where the edges lie; and a mask's bytes.
    private void Stems(Frame f, Kind kind, int op, int count, ReadOnlySpan<byte> mask)
    {
        int start = f.Values.Count;
        double edge = 0, rounded = 0;
        for (int k = 0; k < count; k++)
        {
            double v = _stack[k];
            if (_round)
            {
                edge += v;
                double r = Math.Floor(edge + 0.5) - rounded;
                rounded += r;
                v = r;
            }

            f.Values.Add(v);
        }

        int maskStart = f.Masks.Count;
        foreach (byte b in mask)
        {
            f.Masks.Add(b);
        }

        f.Tokens.Add(new Token(kind, op, start, count, maskStart, mask.Length));
    }

    // Marks the subroutines between an operator and an operand pushed by another charstring, to be inlined.
    private void Cross(int owner, int current)
    {
        bool OnChain(int f)
        {
            for (int c = current; c >= 0; c = _frames[c].Parent)
            {
                if (c == f)
                {
                    return true;
                }
            }

            return false;
        }

        int e = owner;
        while (e >= 0 && !OnChain(e))
        {
            Mark(_frames[e].Id);
            e = _frames[e].Parent;
        }

        for (int c = current; c >= 0 && c != e; c = _frames[c].Parent)
        {
            Mark(_frames[c].Id);
        }
    }

    private void Mark(CharStringId id)
    {
        if (id.Kind != CharStringKind.Glyph && _inline.Add(id))
        {
            _changed = true;
            _marked.Add(id);
        }
    }

    private void Check(int count)
    {
        if (_sp < count)
        {
            throw Error("A charstring operator has too few operands.");
        }
    }

    private static void Need(ReadOnlySpan<byte> code, int at, int count)
    {
        if (at + count > code.Length)
        {
            throw Error("A charstring number runs past its end.");
        }
    }

    private static CorruptDataException Error(string problem) => FontErrors.Corrupt(-1, problem);

    // Writes a charstring's tokens, operators with their shortest forms; the first may carry the glyph's width before it.
    private static void Encode(Frame frame, ByteWriter w, Func<CharStringId, int> subr, bool glyph, bool width)
    {
        List<Token> tokens = frame.Written();
        List<double> v = frame.Values;
        int reserved = width ? 1 : 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            switch (token.Kind)
            {
                case Kind.Number:
                    CffEncoding.CharStringNumber(w, v[token.Start]);
                    break;
                case Kind.Move:
                    {
                        // Moves with nothing between them are one move; a glyph's last move draws nothing.
                        double dx = v[token.Start], dy = v[token.Start + 1];
                        while (i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.Move)
                        {
                            i++;
                            dx += v[tokens[i].Start];
                            dy += v[tokens[i].Start + 1];
                        }

                        if (glyph && reserved == 0 && (i + 1 == tokens.Count || tokens[i + 1].Kind == Kind.End))
                        {
                            break;
                        }

                        if (dy == 0)
                        {
                            CffEncoding.CharStringNumber(w, dx);
                            w.U8(22);
                        }
                        else if (dx == 0)
                        {
                            CffEncoding.CharStringNumber(w, dy);
                            w.U8(4);
                        }
                        else
                        {
                            CffEncoding.CharStringNumber(w, dx);
                            CffEncoding.CharStringNumber(w, dy);
                            w.U8(21);
                        }

                        break;
                    }

                case Kind.Line:
                case Kind.Curve:
                    {
                        int end = i;
                        while (end < tokens.Count && tokens[end].Kind is Kind.Line or Kind.Curve)
                        {
                            end++;
                        }

                        Path(tokens, v, i, end, w);
                        i = end - 1;
                        break;
                    }

                case Kind.Flex:
                    for (int k = 0; k < 13; k++)
                    {
                        CffEncoding.CharStringNumber(w, v[token.Start + k]);
                    }

                    w.U8(12);
                    w.U8(35);
                    break;
                case Kind.Stem:
                    WriteStems(w, v, token.Start, token.Count, MaxOperands - reserved, token.Code);
                    break;
                case Kind.Mask:
                    {
                        // Implicit vstems past what one operator holds go first as vstemhm.
                        int room = MaxOperands - reserved;
                        int count = token.Count, start = token.Start;
                        if (count > room)
                        {
                            int lead = count - (room - (room % 2));
                            WriteStems(w, v, start, lead, room, 23);
                            double edge = 0;
                            for (int k = 0; k < lead; k++)
                            {
                                edge += v[start + k];
                            }

                            for (int k = lead; k < count; k++)
                            {
                                CffEncoding.CharStringNumber(w, k == lead ? edge + v[start + k] : v[start + k]);
                            }
                        }
                        else
                        {
                            for (int k = 0; k < count; k++)
                            {
                                CffEncoding.CharStringNumber(w, v[start + k]);
                            }
                        }

                        w.U8(token.Code);
                        WriteMask(w, frame, token);
                        break;
                    }

                case Kind.End:
                    for (int k = 0; k < token.Count; k++)
                    {
                        CffEncoding.CharStringNumber(w, v[token.Start + k]);
                    }

                    w.U8(14);
                    break;
                case Kind.Call:
                    CffEncoding.CharStringInteger(w, subr(token.Callee));
                    w.U8(token.Code);
                    break;
                case Kind.Raw:
                    if (token.Code >= 1200)
                    {
                        w.U8(12);
                        w.U8(token.Code - 1200);
                    }
                    else
                    {
                        w.U8(token.Code);
                    }

                    WriteMask(w, frame, token);
                    break;
            }

            if (token.Kind != Kind.Number)
            {
                reserved = 0;
            }
        }
    }

    private static void WriteMask(ByteWriter w, Frame frame, Token token)
    {
        for (int k = 0; k < token.MaskLength; k++)
        {
            w.U8(frame.Masks[token.MaskStart + k]);
        }
    }

    // Stem operators of at most room operands; a later operator's first edge is absolute again.
    private static void WriteStems(ByteWriter w, List<double> v, int start, int count, int room, int code)
    {
        int chunk = room - (room % 2);
        double edge = 0;
        for (int at = 0; at < count; at += chunk)
        {
            int n = Math.Min(chunk, count - at);
            for (int k = 0; k < n; k++)
            {
                double value = v[start + at + k];
                CffEncoding.CharStringNumber(w, k == 0 ? edge + value : value);
                edge += value;
            }

            w.U8(code);
            chunk = MaxOperands;
        }
    }

    private enum Form : byte
    {
        RLine,
        HLine,
        VLine,
        RRCurve,
        RCurveLine,
        RLineCurve,
        HHCurve,
        VVCurve,
        HVCurve,
        VHCurve,
    }

    // The shortest operators for a run of lines and curves: each choice of operator and length, by dynamic programming.
    private static void Path(List<Token> tokens, List<double> v, int from, int to, ByteWriter w)
    {
        int n = to - from;
        Span<int> cost = n <= 256 ? stackalloc int[n + 1] : new int[n + 1];
        var choice = new (Form Form, int Count)[n];
        cost[n] = 0;

        // Each segment's values, their sizes and whether it is a curve, read once.
        double[] values = new double[n * 6];
        int[] sizes = new int[n * 6];
        int[] sums = new int[n];
        bool[] curves = new bool[n];
        for (int s = 0; s < n; s++)
        {
            Token token = tokens[from + s];
            curves[s] = token.Kind == Kind.Curve;
            for (int k = 0; k < token.Count; k++)
            {
                double value = v[token.Start + k];
                values[(s * 6) + k] = value;
                sizes[(s * 6) + k] = Size(value);
                sums[s] += sizes[(s * 6) + k];
            }
        }

        bool IsCurve(int s) => curves[s];
        double D(int s, int k) => values[(s * 6) + k];
        int Sz(int s, int k) => sizes[(s * 6) + k];
        int Size2(int s) => sums[s];
        int Size6(int s) => sums[s];
        for (int i = n - 1; i >= 0; i--)
        {
            int best = int.MaxValue;
            (Form, int) pick = default;
            void Offer(int c, Form form, int count)
            {
                if (c < best)
                {
                    best = c;
                    pick = (form, count);
                }
            }

            // rlineto and rlinecurve
            int sum = 0;
            for (int j = i; j < n && !IsCurve(j) && 2 * (j - i + 1) <= MaxOperands; j++)
            {
                sum += Size2(j);
                Offer(1 + sum + cost[j + 1], Form.RLine, j - i + 1);
                if (j + 1 < n && IsCurve(j + 1) && (2 * (j - i + 1)) + 6 <= MaxOperands)
                {
                    Offer(1 + sum + Size6(j + 1) + cost[j + 2], Form.RLineCurve, j - i + 1);
                }
            }

            // hlineto and vlineto
            for (int first = 0; first < 2; first++)
            {
                sum = 0;
                bool horizontal = first == 0;
                for (int j = i; j < n && !IsCurve(j) && j - i + 1 <= MaxOperands; j++)
                {
                    if (horizontal ? D(j, 1) != 0 : D(j, 0) != 0)
                    {
                        break;
                    }

                    sum += Size(horizontal ? D(j, 0) : D(j, 1));
                    Offer(1 + sum + cost[j + 1], first == 0 ? Form.HLine : Form.VLine, j - i + 1);
                    horizontal = !horizontal;
                }
            }

            // rrcurveto and rcurveline
            sum = 0;
            for (int j = i; j < n && IsCurve(j) && 6 * (j - i + 1) <= MaxOperands; j++)
            {
                sum += Size6(j);
                Offer(1 + sum + cost[j + 1], Form.RRCurve, j - i + 1);
                if (j + 1 < n && !IsCurve(j + 1) && (6 * (j - i + 1)) + 2 <= MaxOperands)
                {
                    Offer(1 + sum + Size2(j + 1) + cost[j + 2], Form.RCurveLine, j - i + 1);
                }
            }

            // hhcurveto (dy3 0; dy1 0 after the first) and vvcurveto (dx3 0; dx1 0 after the first)
            for (int axis = 0; axis < 2; axis++)
            {
                int cross1 = axis == 0 ? 1 : 0, cross3 = axis == 0 ? 5 : 4, along1 = axis == 0 ? 0 : 1, along3 = axis == 0 ? 4 : 5;
                sum = 0;
                int operands = 0;
                for (int j = i; j < n && IsCurve(j); j++)
                {
                    if (D(j, cross3) != 0 || (j > i && D(j, cross1) != 0))
                    {
                        break;
                    }

                    int lead = j == i && D(j, cross1) != 0 ? 1 : 0;
                    operands += 4 + lead;
                    if (operands > MaxOperands)
                    {
                        break;
                    }

                    sum += (lead == 1 ? Sz(j, cross1) : 0) + Sz(j, along1) + Sz(j, 2) + Sz(j, 3) + Sz(j, along3);
                    Offer(1 + sum + cost[j + 1], axis == 0 ? Form.HHCurve : Form.VVCurve, j - i + 1);
                }
            }

            // hvcurveto and vhcurveto: tangents alternate; the last curve may end off the axis.
            for (int first = 0; first < 2; first++)
            {
                sum = 0;
                int operands = 0;
                bool horizontal = first == 0;
                for (int j = i; j < n && IsCurve(j); j++)
                {
                    // Horizontal start: dy1 0, operands dx1 dx2 dy2 dy3, dx3 0 unless last. Vertical: dx1 0, dy1 dx2 dy2 dx3, dy3.
                    if (horizontal ? D(j, 1) != 0 : D(j, 0) != 0)
                    {
                        break;
                    }

                    int body = horizontal
                        ? Sz(j, 0) + Sz(j, 2) + Sz(j, 3) + Sz(j, 5)
                        : Sz(j, 1) + Sz(j, 2) + Sz(j, 3) + Sz(j, 4);
                    double offAxis = horizontal ? D(j, 4) : D(j, 5);
                    operands += 4;
                    if (operands + (offAxis != 0 ? 1 : 0) > MaxOperands)
                    {
                        break;
                    }

                    Offer(1 + sum + body + (offAxis != 0 ? Size(offAxis) : 0) + cost[j + 1], first == 0 ? Form.HVCurve : Form.VHCurve, j - i + 1);
                    if (offAxis != 0)
                    {
                        break;
                    }

                    sum += body;
                    horizontal = !horizontal;
                }
            }

            cost[i] = best;
            choice[i] = pick;
        }

        for (int i = 0; i < n;)
        {
            (Form form, int count) = choice[i];
            int s = from + i;
            double E(int segment, int k) => v[tokens[s + segment].Start + k];
            void Number(double value) => CffEncoding.CharStringNumber(w, value);
            void Six(int segment)
            {
                for (int k = 0; k < 6; k++)
                {
                    Number(E(segment, k));
                }
            }

            switch (form)
            {
                case Form.RLine:
                case Form.RLineCurve:
                    for (int k = 0; k < count; k++)
                    {
                        Number(E(k, 0));
                        Number(E(k, 1));
                    }

                    if (form == Form.RLineCurve)
                    {
                        Six(count);
                    }

                    w.U8(form == Form.RLine ? 5 : 25);
                    i += count + (form == Form.RLine ? 0 : 1);
                    break;
                case Form.HLine:
                case Form.VLine:
                    {
                        bool horizontal = form == Form.HLine;
                        for (int k = 0; k < count; k++)
                        {
                            Number(horizontal ? E(k, 0) : E(k, 1));
                            horizontal = !horizontal;
                        }

                        w.U8(form == Form.HLine ? 6 : 7);
                        i += count;
                        break;
                    }

                case Form.RRCurve:
                case Form.RCurveLine:
                    for (int k = 0; k < count; k++)
                    {
                        Six(k);
                    }

                    if (form == Form.RCurveLine)
                    {
                        Number(E(count, 0));
                        Number(E(count, 1));
                    }

                    w.U8(form == Form.RRCurve ? 8 : 24);
                    i += count + (form == Form.RRCurve ? 0 : 1);
                    break;
                case Form.HHCurve:
                    if (E(0, 1) != 0)
                    {
                        Number(E(0, 1));
                    }

                    for (int k = 0; k < count; k++)
                    {
                        Number(E(k, 0));
                        Number(E(k, 2));
                        Number(E(k, 3));
                        Number(E(k, 4));
                    }

                    w.U8(27);
                    i += count;
                    break;
                case Form.VVCurve:
                    if (E(0, 0) != 0)
                    {
                        Number(E(0, 0));
                    }

                    for (int k = 0; k < count; k++)
                    {
                        Number(E(k, 1));
                        Number(E(k, 2));
                        Number(E(k, 3));
                        Number(E(k, 5));
                    }

                    w.U8(26);
                    i += count;
                    break;
                case Form.HVCurve:
                case Form.VHCurve:
                    {
                        bool horizontal = form == Form.HVCurve;
                        for (int k = 0; k < count; k++)
                        {
                            if (horizontal)
                            {
                                Number(E(k, 0));
                                Number(E(k, 2));
                                Number(E(k, 3));
                                Number(E(k, 5));
                            }
                            else
                            {
                                Number(E(k, 1));
                                Number(E(k, 2));
                                Number(E(k, 3));
                                Number(E(k, 4));
                            }

                            horizontal = !horizontal;
                        }

                        // The last curve's off-axis end: dx3 after a horizontal start, dy3 after a vertical one.
                        bool lastHorizontal = (form == Form.HVCurve) == (count % 2 == 1);
                        double offAxis = lastHorizontal ? E(count - 1, 4) : E(count - 1, 5);
                        if (offAxis != 0)
                        {
                            Number(offAxis);
                        }

                        w.U8(form == Form.HVCurve ? 31 : 30);
                        i += count;
                        break;
                    }
            }
        }
    }

    // The bytes CffEncoding.CharStringNumber writes for a value.
    private static int Size(double value)
    {
        double rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 1e-6 && rounded is >= short.MinValue and <= short.MaxValue)
        {
            return rounded is >= -107 and <= 107 ? 1 : rounded is >= -1131 and <= 1131 ? 2 : 3;
        }

        return 5;
    }

    private readonly record struct Token(Kind Kind, int Code, int Start, int Count, int MaskStart = 0, int MaskLength = 0, CharStringId Callee = default);

    // What one charstring writes, and where its pen ended relative to where it started, before and after rounding.
    private sealed class Frame
    {
        public int Serial;
        public int Parent;
        public CharStringId Id;
        public double TrueX;
        public double TrueY;
        public double RoundX;
        public double RoundY;
        public double BaseTrueX;
        public double BaseTrueY;
        public double BaseRoundX;
        public double BaseRoundY;
        public bool Ends;

        public Frame()
        {
            Tokens = [];
            Values = [];
            Masks = [];
        }

        private Frame(int tokens, int values, int masks)
        {
            Tokens = new List<Token>(tokens);
            Values = new List<double>(values);
            Masks = new List<byte>(masks);
        }

        public List<Token> Tokens { get; }

        public List<double> Values { get; }

        public List<byte> Masks { get; }

        // A copy holding only the tokens that write something, with their values and masks.
        public Frame Compact()
        {
            int tokens = 0, values = 0, masks = 0;
            foreach (Token t in Tokens)
            {
                if (t.Kind is not (Kind.Pending or Kind.Skip))
                {
                    tokens++;
                    values += t.Kind == Kind.Number ? 1 : t.Count;
                    masks += t.MaskLength;
                }
            }

            var copy = new Frame(tokens, values, masks)
            {
                Serial = Serial,
                Parent = Parent,
                Id = Id,
                TrueX = TrueX,
                TrueY = TrueY,
                RoundX = RoundX,
                RoundY = RoundY,
                BaseTrueX = BaseTrueX,
                BaseTrueY = BaseTrueY,
                BaseRoundX = BaseRoundX,
                BaseRoundY = BaseRoundY,
                Ends = Ends,
            };
            foreach (Token t in Tokens)
            {
                if (t.Kind is Kind.Pending or Kind.Skip)
                {
                    continue;
                }

                int count = t.Kind == Kind.Number ? 1 : t.Count;
                int start = copy.Values.Count, maskStart = copy.Masks.Count;
                for (int k = 0; k < count; k++)
                {
                    copy.Values.Add(Values[t.Start + k]);
                }

                for (int k = 0; k < t.MaskLength; k++)
                {
                    copy.Masks.Add(Masks[t.MaskStart + k]);
                }

                copy.Tokens.Add(t with { Start = start, MaskStart = t.MaskLength > 0 ? maskStart : t.MaskStart });
            }

            return copy;
        }

        public void Reset(int serial, int parent, CharStringId id)
        {
            Serial = serial;
            Parent = parent;
            Id = id;
            TrueX = TrueY = RoundX = RoundY = 0;
            BaseTrueX = BaseTrueY = BaseRoundX = BaseRoundY = 0;
            Ends = false;
            Tokens.Clear();
            Values.Clear();
            Masks.Clear();
        }

        // The tokens that write something.
        public List<Token> Written() => [.. Tokens.Where(t => t.Kind is not (Kind.Pending or Kind.Skip))];

        public Kind? LastKind()
        {
            for (int t = Tokens.Count - 1; t >= 0; t--)
            {
                if (Tokens[t].Kind is not (Kind.Pending or Kind.Skip))
                {
                    return Tokens[t].Kind;
                }
            }

            return null;
        }

        public bool SameAs(Frame other)
        {
            if (Ends != other.Ends)
            {
                return false;
            }

            List<Token> a = Tokens, b = other.Tokens;
            int i = 0, j = 0;
            while (true)
            {
                while (i < a.Count && a[i].Kind is Kind.Pending or Kind.Skip)
                {
                    i++;
                }

                while (j < b.Count && b[j].Kind is Kind.Pending or Kind.Skip)
                {
                    j++;
                }

                if (i == a.Count || j == b.Count)
                {
                    return i == a.Count && j == b.Count;
                }

                Token x = a[i++], y = b[j++];
                if (x.Kind != y.Kind || x.Code != y.Code || x.Count != y.Count || x.Callee != y.Callee || x.MaskLength != y.MaskLength)
                {
                    return false;
                }

                int values = x.Kind == Kind.Number ? 1 : x.Count;
                for (int k = 0; k < values; k++)
                {
                    if (Values[x.Start + k] != other.Values[y.Start + k])
                    {
                        return false;
                    }
                }

                for (int k = 0; k < x.MaskLength; k++)
                {
                    if (Masks[x.MaskStart + k] != other.Masks[y.MaskStart + k])
                    {
                        return false;
                    }
                }
            }
        }
    }

}
