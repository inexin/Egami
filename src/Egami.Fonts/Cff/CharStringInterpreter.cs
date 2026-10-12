using Egami.Fonts.Parsing;

namespace Egami.Fonts.Cff;

/// <summary>Which subroutines a set of charstrings calls, by font dict for the local ones.</summary>
internal sealed class SubrUsage
{
    public SubrUsage(int fontDicts)
    {
        Local = new HashSet<int>[fontDicts];
        for (int i = 0; i < fontDicts; i++)
        {
            Local[i] = [];
        }
    }

    public HashSet<int> Global { get; } = [];

    public HashSet<int>[] Local { get; }

    /// <summary>Gets the glyphs seac accents pull in.</summary>
    public HashSet<int> SeacGlyphs { get; } = [];
}

/// <summary>
/// A Type 2 charstring interpreter (Adobe Technical Note #5177): every path, hint, subroutine, arithmetic and storage operator.
/// Hints are skipped, flex becomes two cubics and seac draws the standard-encoded base and accent glyphs. The operand stack,
/// subroutine depth and operator count are capped so a hostile charstring stops with <see cref="CorruptDataException"/>.
/// </summary>
internal sealed class CharStringInterpreter
{
    public const int MaxStack = 48;
    public const int MaxSubrDepth = 10;
    public const int MaxOperators = 200_000;

    [ThreadStatic]
    private static CharStringInterpreter? t_shared;

    private CffFont _font;
    private readonly double[] _stack = new double[MaxStack];
    private readonly double[] _transient = new double[32];
    private OutlineRecorder? _sink;
    private SubrUsage? _usage;
    private int _sp;
    private int _stems;
    private int _operators;
    private bool _haveWidth;
    private double _width;
    private double _x;
    private double _y;
    private double _offsetX;
    private double _offsetY;
    private bool _ended;
    private int _seacDepth;
    private bool _widthOnly;
    private int _fontDict;
    private readonly Random _random = new(0);

    public CharStringInterpreter(CffFont font)
    {
        _font = font;
    }

    /// <summary>Takes the calling thread's interpreter, pointed at <paramref name="font"/>. Not re-entrant.</summary>
    public static CharStringInterpreter For(CffFont font)
    {
        CharStringInterpreter interpreter = t_shared ??= new CharStringInterpreter(font);
        interpreter._font = font;
        return interpreter;
    }

    /// <summary>Draws a glyph into <paramref name="sink"/> and returns its advance width from the charstring.</summary>
    public double Draw(int glyph, OutlineRecorder? sink, SubrUsage? usage = null)
    {
        _sink = sink;
        _usage = usage;
        _widthOnly = false;
        _seacDepth = 0;
        _offsetX = _offsetY = 0;
        Run(glyph);
        sink?.Finish();
        return _width;
    }

    /// <summary>Returns a glyph's advance width without drawing it.</summary>
    public double GetWidth(int glyph)
    {
        _sink = null;
        _usage = null;
        _widthOnly = true;
        _seacDepth = 0;
        _offsetX = _offsetY = 0;
        Run(glyph);
        return _width;
    }

    private void Run(int glyph)
    {
        if ((uint)glyph >= (uint)_font.GlyphCount)
        {
            throw new ArgumentOutOfRangeException(nameof(glyph));
        }

        _fontDict = _font.FontDictOf(glyph);
        PrivateData priv = _font.Privates[_fontDict];
        _sp = 0;
        _stems = 0;
        _operators = 0;
        _haveWidth = false;
        _width = priv.DefaultWidthX;
        _x = _y = 0;
        _ended = false;
        Execute(_font.CharStrings[glyph], 0);
    }

    private void Execute(ReadOnlySpan<byte> code, int depth)
    {
        if (depth > MaxSubrDepth)
        {
            throw Error("Charstring subroutines nest deeper than 10 levels.");
        }

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

                Push(value);
                continue;
            }

            if (++_operators > MaxOperators)
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
                    TakeWidth(_sp % 2 == 1);
                    _stems += _sp / 2;
                    _sp = 0;
                    break;
                case 19: // hintmask
                case 20: // cntrmask
                    TakeWidth(_sp % 2 == 1);
                    _stems += _sp / 2;
                    _sp = 0;
                    i += (_stems + 7) / 8;
                    if (i > code.Length)
                    {
                        throw Error("A hintmask runs past the end of its charstring.");
                    }

                    break;
                case 21: // rmoveto
                    TakeWidth(_sp > 2);
                    Check(2);
                    MoveTo(_x + _stack[_sp - 2], _y + _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 22: // hmoveto
                    TakeWidth(_sp > 1);
                    Check(1);
                    MoveTo(_x + _stack[_sp - 1], _y);
                    _sp = 0;
                    break;
                case 4: // vmoveto
                    TakeWidth(_sp > 1);
                    Check(1);
                    MoveTo(_x, _y + _stack[_sp - 1]);
                    _sp = 0;
                    break;
                case 5: // rlineto
                    for (int k = 0; k + 1 < _sp; k += 2)
                    {
                        LineTo(_x + _stack[k], _y + _stack[k + 1]);
                    }

                    _sp = 0;
                    break;
                case 6: // hlineto
                case 7: // vlineto
                    {
                        bool horizontal = op == 6;
                        for (int k = 0; k < _sp; k++)
                        {
                            if (horizontal)
                            {
                                LineTo(_x + _stack[k], _y);
                            }
                            else
                            {
                                LineTo(_x, _y + _stack[k]);
                            }

                            horizontal = !horizontal;
                        }

                        _sp = 0;
                        break;
                    }

                case 8: // rrcurveto
                    for (int k = 0; k + 5 < _sp; k += 6)
                    {
                        RelativeCurve(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    }

                    _sp = 0;
                    break;
                case 24: // rcurveline
                    {
                        int k = 0;
                        for (; _sp - k >= 8; k += 6)
                        {
                            RelativeCurve(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                        }

                        if (_sp - k >= 2)
                        {
                            LineTo(_x + _stack[k], _y + _stack[k + 1]);
                        }

                        _sp = 0;
                        break;
                    }

                case 25: // rlinecurve
                    {
                        int k = 0;
                        for (; _sp - k >= 8; k += 2)
                        {
                            LineTo(_x + _stack[k], _y + _stack[k + 1]);
                        }

                        if (_sp - k >= 6)
                        {
                            RelativeCurve(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                        }

                        _sp = 0;
                        break;
                    }

                case 26: // vvcurveto
                    {
                        int k = 0;
                        double dx1 = 0;
                        if (_sp % 2 == 1)
                        {
                            dx1 = _stack[0];
                            k = 1;
                        }

                        for (; k + 3 < _sp; k += 4)
                        {
                            RelativeCurve(dx1, _stack[k], _stack[k + 1], _stack[k + 2], 0, _stack[k + 3]);
                            dx1 = 0;
                        }

                        _sp = 0;
                        break;
                    }

                case 27: // hhcurveto
                    {
                        int k = 0;
                        double dy1 = 0;
                        if (_sp % 2 == 1)
                        {
                            dy1 = _stack[0];
                            k = 1;
                        }

                        for (; k + 3 < _sp; k += 4)
                        {
                            RelativeCurve(_stack[k], dy1, _stack[k + 1], _stack[k + 2], _stack[k + 3], 0);
                            dy1 = 0;
                        }

                        _sp = 0;
                        break;
                    }

                case 30: // vhcurveto
                case 31: // hvcurveto
                    {
                        bool horizontal = op == 31;
                        for (int k = 0; k + 3 < _sp; k += 4)
                        {
                            bool last = _sp - k == 5;
                            double extra = last ? _stack[k + 4] : 0;
                            if (horizontal)
                            {
                                RelativeCurve(_stack[k], 0, _stack[k + 1], _stack[k + 2], extra, _stack[k + 3]);
                            }
                            else
                            {
                                RelativeCurve(0, _stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], extra);
                            }

                            horizontal = !horizontal;
                        }

                        _sp = 0;
                        break;
                    }

                case 10: // callsubr
                case 29: // callgsubr
                    {
                        Check(1);
                        bool global = op == 29;
                        CffIndex? subrs = global ? _font.GlobalSubrs : _font.Privates[_fontDict].LocalSubrs;
                        int count = subrs?.Count ?? 0;
                        int index = (int)_stack[--_sp] + CffFont.Bias(count);
                        if (subrs is null || (uint)index >= (uint)count)
                        {
                            throw Error($"A charstring calls {(global ? "global" : "local")} subroutine {index} of {count}.");
                        }

                        if (_usage is not null)
                        {
                            (global ? _usage.Global : _usage.Local[_fontDict]).Add(index);
                        }

                        Execute(subrs.Value[index], depth + 1);
                        break;
                    }

                case 11: // return
                    return;
                case 14: // endchar
                    TakeWidth(_sp is 1 or 5);
                    if (_sp >= 4)
                    {
                        Seac(_stack[_sp - 4], _stack[_sp - 3], (int)_stack[_sp - 2], (int)_stack[_sp - 1]);
                    }

                    _sink?.Finish();
                    _ended = true;
                    _sp = 0;
                    return;
                case 1200: // dotsection, deprecated
                    _sp = 0;
                    break;
                case 1203: // and
                    Check(2);
                    Binary((a, b) => (a != 0 && b != 0) ? 1 : 0);
                    break;
                case 1204: // or
                    Check(2);
                    Binary((a, b) => (a != 0 || b != 0) ? 1 : 0);
                    break;
                case 1205: // not
                    Check(1);
                    _stack[_sp - 1] = _stack[_sp - 1] == 0 ? 1 : 0;
                    break;
                case 1209: // abs
                    Check(1);
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]);
                    break;
                case 1210: // add
                    Check(2);
                    Binary((a, b) => a + b);
                    break;
                case 1211: // sub
                    Check(2);
                    Binary((a, b) => a - b);
                    break;
                case 1212: // div
                    Check(2);
                    Binary((a, b) => b == 0 ? 0 : a / b);
                    break;
                case 1214: // neg
                    Check(1);
                    _stack[_sp - 1] = -_stack[_sp - 1];
                    break;
                case 1215: // eq
                    Check(2);
                    Binary((a, b) => a == b ? 1 : 0);
                    break;
                case 1218: // drop
                    Check(1);
                    _sp--;
                    break;
                case 1220: // put
                    {
                        Check(2);
                        int slot = (int)_stack[--_sp];
                        double value = _stack[--_sp];
                        if ((uint)slot < (uint)_transient.Length)
                        {
                            _transient[slot] = value;
                        }

                        break;
                    }

                case 1221: // get
                    {
                        Check(1);
                        int slot = (int)_stack[_sp - 1];
                        _stack[_sp - 1] = (uint)slot < (uint)_transient.Length ? _transient[slot] : 0;
                        break;
                    }

                case 1222: // ifelse
                    {
                        Check(4);
                        double v2 = _stack[--_sp], v1 = _stack[--_sp], s2 = _stack[--_sp], s1 = _stack[--_sp];
                        Push(v1 <= v2 ? s1 : s2);
                        break;
                    }

                case 1223: // random
                    Push((_random.NextDouble() * 0.999) + 0.001);
                    break;
                case 1224: // mul
                    Check(2);
                    Binary((a, b) => a * b);
                    break;
                case 1226: // sqrt
                    Check(1);
                    _stack[_sp - 1] = Math.Sqrt(Math.Max(0, _stack[_sp - 1]));
                    break;
                case 1227: // dup
                    Check(1);
                    Push(_stack[_sp - 1]);
                    break;
                case 1228: // exch
                    Check(2);
                    (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]);
                    break;
                case 1229: // index
                    {
                        Check(1);
                        int n = (int)_stack[_sp - 1];
                        int from = _sp - 2 - Math.Max(0, n);
                        if (from < 0)
                        {
                            throw Error("A charstring index reaches below the stack.");
                        }

                        _stack[_sp - 1] = _stack[from];
                        break;
                    }

                case 1230: // roll
                    {
                        Check(2);
                        int j = (int)_stack[--_sp];
                        int n = (int)_stack[--_sp];
                        if (n < 0 || n > _sp)
                        {
                            throw Error("A charstring roll reaches below the stack.");
                        }

                        if (n > 0)
                        {
                            Span<double> items = _stack.AsSpan(_sp - n, n);
                            int shift = ((j % n) + n) % n;
                            items.Reverse();
                            items[..shift].Reverse();
                            items[shift..].Reverse();
                        }

                        break;
                    }

                case 1234: // hflex
                    Check(7);
                    Flex(
                        _stack[0], 0, _stack[1], _stack[2], _stack[3], 0,
                        _stack[4], 0, _stack[5], -_stack[2], _stack[6], 0);
                    _sp = 0;
                    break;
                case 1235: // flex
                    Check(13);
                    Flex(
                        _stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5],
                        _stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                    _sp = 0;
                    break;
                case 1236: // hflex1
                    {
                        Check(9);
                        double dy1 = _stack[1], dy2 = _stack[3], dy5 = _stack[7];
                        Flex(
                            _stack[0], dy1, _stack[2], dy2, _stack[4], 0,
                            _stack[5], 0, _stack[6], dy5, _stack[8], -(dy1 + dy2 + dy5));
                        _sp = 0;
                        break;
                    }

                case 1237: // flex1
                    {
                        Check(11);
                        double dx = 0, dy = 0;
                        for (int k = 0; k < 10; k += 2)
                        {
                            dx += _stack[k];
                            dy += _stack[k + 1];
                        }

                        double lastX, lastY;
                        if (Math.Abs(dx) > Math.Abs(dy))
                        {
                            lastX = _stack[10];
                            lastY = -dy;
                        }
                        else
                        {
                            lastX = -dx;
                            lastY = _stack[10];
                        }

                        Flex(
                            _stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5],
                            _stack[6], _stack[7], _stack[8], _stack[9], lastX, lastY);
                        _sp = 0;
                        break;
                    }

                default:
                    // Reserved operators clear the stack, as the reference interpreters do.
                    _sp = 0;
                    break;
            }

            if (_widthOnly && _haveWidth)
            {
                _ended = true;
                return;
            }
        }
    }

    private void TakeWidth(bool hasWidth)
    {
        if (_haveWidth)
        {
            return;
        }

        _haveWidth = true;
        PrivateData priv = _font.Privates[_fontDict];
        if (hasWidth && _sp > 0)
        {
            _width = priv.NominalWidthX + _stack[0];
            Array.Copy(_stack, 1, _stack, 0, _sp - 1);
            _sp--;
        }
        else
        {
            _width = priv.DefaultWidthX;
        }
    }

    private void Seac(double adx, double ady, int baseCode, int accentCode)
    {
        if (_font.IsCidKeyed || _seacDepth > 0)
        {
            throw Error("A seac appears in a CID-keyed font or inside another seac.");
        }

        int baseGlyph = _font.GlyphOfSid(CffStandard.StandardEncodingSid(baseCode));
        int accentGlyph = _font.GlyphOfSid(CffStandard.StandardEncodingSid(accentCode));
        if (baseGlyph < 0 || accentGlyph < 0)
        {
            throw Error("A seac names a glyph the font does not have.");
        }

        _usage?.SeacGlyphs.Add(baseGlyph);
        _usage?.SeacGlyphs.Add(accentGlyph);
        if (_sink is null && _usage is null)
        {
            return;
        }

        // Draw both components with a fresh state, keeping this glyph's width.
        double width = _width;
        bool haveWidth = _haveWidth;
        int dict = _fontDict;
        _seacDepth++;
        _sink?.Finish();
        DrawComponent(baseGlyph, 0, 0);
        _sink?.Finish();
        DrawComponent(accentGlyph, adx, ady);
        _seacDepth--;
        _width = width;
        _haveWidth = haveWidth;
        _fontDict = dict;
    }

    private void DrawComponent(int glyph, double dx, double dy)
    {
        _sp = 0;
        _stems = 0;
        _haveWidth = false;
        _x = _y = 0;
        _offsetX = dx;
        _offsetY = dy;
        _ended = false;
        _fontDict = _font.FontDictOf(glyph);
        Execute(_font.CharStrings[glyph], 0);
        _offsetX = _offsetY = 0;
        _ended = true;
    }

    private void MoveTo(double x, double y)
    {
        _x = x;
        _y = y;
        _sink?.MoveTo((float)(x + _offsetX), (float)(y + _offsetY));
    }

    private void LineTo(double x, double y)
    {
        _x = x;
        _y = y;
        _sink?.LineTo((float)(x + _offsetX), (float)(y + _offsetY));
    }

    private void RelativeCurve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        double x1 = _x + dx1, y1 = _y + dy1;
        double x2 = x1 + dx2, y2 = y1 + dy2;
        _x = x2 + dx3;
        _y = y2 + dy3;
        _sink?.CubicTo(
            (float)(x1 + _offsetX),
            (float)(y1 + _offsetY),
            (float)(x2 + _offsetX),
            (float)(y2 + _offsetY),
            (float)(_x + _offsetX),
            (float)(_y + _offsetY));
    }

    private void Flex(double a, double b, double c, double d, double e, double f, double g, double h, double i, double j, double k, double l)
    {
        RelativeCurve(a, b, c, d, e, f);
        RelativeCurve(g, h, i, j, k, l);
    }

    private void Binary(Func<double, double, double> f)
    {
        double b = _stack[--_sp];
        _stack[_sp - 1] = f(_stack[_sp - 1], b);
    }

    private void Push(double value)
    {
        if (_sp >= MaxStack)
        {
            throw Error("A charstring overflows the operand stack.");
        }

        _stack[_sp++] = value;
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
}
