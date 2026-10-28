namespace Egami.Fonts.Hinting;

internal sealed partial class TrueTypeInterpreter
{
    private const int MaxCallDepth = 64;

    // The main loop: one opcode at a time until the code ends, with calls and loop calls on a frame stack.
    private void Execute(byte[] program)
    {
        byte[] code = program;
        int ip = 0;
        var frames = new Stack<Frame>();
        while (true)
        {
            if (ip >= code.Length)
            {
                if (frames.Count > 0)
                {
                    throw Error("A function runs past the end of its code.");
                }

                return;
            }

            if (--_budget < 0)
            {
                throw Error("The program ran too many instructions.");
            }

            byte op = code[ip];
            int next = ip + 1;
            switch (op)
            {
                case 0x00: // SVTCA[y]
                case 0x01: // SVTCA[x]
                    SetVectors(op == 0x01, projection: true, freedom: true);
                    break;
                case 0x02: // SPVTCA
                case 0x03:
                    SetVectors(op == 0x03, projection: true, freedom: false);
                    break;
                case 0x04: // SFVTCA
                case 0x05:
                    SetVectors(op == 0x05, projection: false, freedom: true);
                    break;
                case 0x06: // SPVTL
                case 0x07:
                    VectorToLine(op & 1, projection: true, dual: false);
                    break;
                case 0x08: // SFVTL
                case 0x09:
                    VectorToLine(op & 1, projection: false, dual: false);
                    break;
                case 0x0A: // SPVFS
                    {
                        int y = Pop(), x = Pop();
                        (int nx, int ny) = Normalize(x, y, keepZero: true);
                        _gs.ProjX = _gs.DualX = nx;
                        _gs.ProjY = _gs.DualY = ny;
                        break;
                    }

                case 0x0B: // SFVFS
                    {
                        int y = Pop(), x = Pop();
                        (_gs.FreeX, _gs.FreeY) = Normalize(x, y, keepZero: true);
                        break;
                    }

                case 0x0C: // GPV
                    Push(_gs.ProjX);
                    Push(_gs.ProjY);
                    break;
                case 0x0D: // GFV
                    Push(_gs.FreeX);
                    Push(_gs.FreeY);
                    break;
                case 0x0E: // SFVTPV
                    _gs.FreeX = _gs.ProjX;
                    _gs.FreeY = _gs.ProjY;
                    break;
                case 0x0F:
                    Isect();
                    break;
                case 0x10:
                    _gs.Rp0 = Pop();
                    break;
                case 0x11:
                    _gs.Rp1 = Pop();
                    break;
                case 0x12:
                    _gs.Rp2 = Pop();
                    break;
                case 0x13:
                    _gs.Zp0 = ZoneIndex(Pop());
                    break;
                case 0x14:
                    _gs.Zp1 = ZoneIndex(Pop());
                    break;
                case 0x15:
                    _gs.Zp2 = ZoneIndex(Pop());
                    break;
                case 0x16: // SZPS
                    _gs.Zp0 = _gs.Zp1 = _gs.Zp2 = ZoneIndex(Pop());
                    break;
                case 0x17: // SLOOP
                    {
                        int n = Pop();
                        _gs.Loop = n > 0 ? Math.Min(n, 0xFFFF) : throw Error("SLOOP with a count under 1.");
                        break;
                    }

                case 0x18:
                    _gs.Round = RoundMode.Grid;
                    break;
                case 0x19:
                    _gs.Round = RoundMode.HalfGrid;
                    break;
                case 0x1A: // SMD
                    _gs.MinDistance = Pop();
                    break;
                case 0x1B: // ELSE: the IF branch ran, skip to EIF
                    next = SkipBranch(code, ip, stopAtElse: false);
                    break;
                case 0x1C: // JMPR
                    next = Jump(code, ip, Pop());
                    break;
                case 0x1D: // SCVTCI
                    _gs.CvtCutIn = Pop();
                    break;
                case 0x1E: // SSWCI
                    _gs.SingleWidthCutIn = Pop();
                    break;
                case 0x1F: // SSW, in font units
                    _gs.SingleWidth = (int)MulFix(Pop(), Scale);
                    break;
                case 0x20: // DUP
                    {
                        int v = Pop();
                        Push(v);
                        Push(v);
                        break;
                    }

                case 0x21:
                    Pop();
                    break;
                case 0x22: // CLEAR
                    _sp = 0;
                    break;
                case 0x23: // SWAP
                    {
                        int b = Pop(), a = Pop();
                        Push(b);
                        Push(a);
                        break;
                    }

                case 0x24: // DEPTH
                    Push(_sp);
                    break;
                case 0x25: // CINDEX
                    {
                        int k = Pop();
                        Push(k >= 1 && k <= _sp ? _stack[_sp - k] : throw Error("CINDEX past the stack."));
                        break;
                    }

                case 0x26: // MINDEX
                    {
                        int k = Pop();
                        if (k < 1 || k > _sp)
                        {
                            throw Error("MINDEX past the stack.");
                        }

                        int v = _stack[_sp - k];
                        Array.Copy(_stack, _sp - k + 1, _stack, _sp - k, k - 1);
                        _stack[_sp - 1] = v;
                        break;
                    }

                case 0x27:
                    AlignPoints();
                    break;
                case 0x29: // UTP
                    {
                        int p = Pop();
                        Zone z = Z(_gs.Zp0);
                        CheckPoint(z, p);
                        if (_gs.FreeX != 0)
                        {
                            z.Touched[p] &= unchecked((byte)~Zone.TouchX);
                        }

                        if (_gs.FreeY != 0)
                        {
                            z.Touched[p] &= unchecked((byte)~Zone.TouchY);
                        }

                        break;
                    }

                case 0x2A: // LOOPCALL
                case 0x2B: // CALL
                    {
                        int f = Pop();
                        int count = op == 0x2A ? Pop() : 1;
                        if ((uint)f >= (uint)_functions.Length || !_functions[f].Defined)
                        {
                            throw Error($"Call of undefined function {f}.");
                        }

                        if (count <= 0)
                        {
                            break;
                        }

                        if (frames.Count >= MaxCallDepth)
                        {
                            throw Error("Calls nest too deep.");
                        }

                        Def def = _functions[f];
                        frames.Push(new Frame(code, next, def, count));
                        code = def.Code;
                        next = def.Start;
                        break;
                    }

                case 0x2C: // FDEF
                    {
                        int f = Pop();
                        if ((uint)f >= (uint)_functions.Length)
                        {
                            throw Error($"FDEF {f} beyond maxp.maxFunctionDefs.");
                        }

                        _functions[f] = new Def(code, next, true);
                        next = SkipDefinition(code, ip);
                        break;
                    }

                case 0x89: // IDEF
                    {
                        int opcode = Pop();
                        _instructionDefs[opcode & 0xFF] = new Def(code, next, true);
                        next = SkipDefinition(code, ip);
                        break;
                    }

                case 0x2D: // ENDF
                    {
                        if (frames.Count == 0)
                        {
                            throw Error("ENDF outside a function.");
                        }

                        Frame frame = frames.Pop();
                        if (frame.Remaining > 1)
                        {
                            frames.Push(frame with { Remaining = frame.Remaining - 1 });
                            next = frame.Def.Start;
                        }
                        else
                        {
                            code = frame.Code;
                            next = frame.ReturnIp;
                        }

                        break;
                    }

                case 0x2E: // MDAP
                case 0x2F:
                    Mdap(op == 0x2F);
                    break;
                case 0x30: // IUP[y]
                case 0x31: // IUP[x]
                    Iup(op == 0x31);
                    break;
                case 0x32: // SHP
                case 0x33:
                    Shp(op == 0x33);
                    break;
                case 0x34: // SHC
                case 0x35:
                    Shc(op == 0x35);
                    break;
                case 0x36: // SHZ
                case 0x37:
                    Shz(op == 0x37);
                    break;
                case 0x38:
                    Shpix();
                    break;
                case 0x39:
                    Ip();
                    break;
                case 0x3A: // MSIRP
                case 0x3B:
                    Msirp(op == 0x3B);
                    break;
                case 0x3C:
                    AlignRp();
                    break;
                case 0x3D:
                    _gs.Round = RoundMode.DoubleGrid;
                    break;
                case 0x3E: // MIAP
                case 0x3F:
                    Miap(op == 0x3F);
                    break;
                case 0x40: // NPUSHB
                    {
                        int n = code[ip + 1];
                        for (int i = 0; i < n; i++)
                        {
                            Push(code[ip + 2 + i]);
                        }

                        next = ip + 2 + n;
                        break;
                    }

                case 0x41: // NPUSHW
                    {
                        int n = code[ip + 1];
                        for (int i = 0; i < n; i++)
                        {
                            Push((short)((code[ip + 2 + (2 * i)] << 8) | code[ip + 3 + (2 * i)]));
                        }

                        next = ip + 2 + (2 * n);
                        break;
                    }

                case 0x42: // WS
                    {
                        int v = Pop(), i = Pop();
                        if ((uint)i < (uint)_storage.Length)
                        {
                            _storage[i] = v;
                        }

                        break;
                    }

                case 0x43: // RS
                    {
                        int i = Pop();
                        Push((uint)i < (uint)_storage.Length ? _storage[i] : 0);
                        break;
                    }

                case 0x44: // WCVTP
                    {
                        int v = Pop(), i = Pop();
                        if ((uint)i < (uint)_cvt.Length)
                        {
                            _cvt[i] = v;
                        }

                        break;
                    }

                case 0x70: // WCVTF, in font units
                    {
                        int v = Pop(), i = Pop();
                        if ((uint)i < (uint)_cvt.Length)
                        {
                            _cvt[i] = (int)MulFix(v, Scale);
                        }

                        break;
                    }

                case 0x45: // RCVT
                    Push(CvtValue(Pop()));
                    break;
                case 0x46: // GC
                case 0x47:
                    {
                        int p = Pop();
                        Zone z = Z(_gs.Zp2);
                        CheckPoint(z, p);
                        Push(op == 0x46 ? Project(z.CurX[p], z.CurY[p]) : DualProject(z.OrgX[p], z.OrgY[p]));
                        break;
                    }

                case 0x48: // SCFS
                    {
                        int k = Pop(), p = Pop();
                        Zone z = Z(_gs.Zp2);
                        CheckPoint(z, p);
                        MovePoint(z, p, k - Project(z.CurX[p], z.CurY[p]), true);
                        if (z.IsTwilight)
                        {
                            z.OrgX[p] = z.CurX[p];
                            z.OrgY[p] = z.CurY[p];
                        }

                        break;
                    }

                case 0x49: // MD[0]: original outline
                case 0x4A: // MD[1]: current outline
                    {
                        int l = Pop(), k = Pop();
                        Zone a = Z(_gs.Zp0), b = Z(_gs.Zp1);
                        CheckPoint(a, l);
                        CheckPoint(b, k);
                        Push(op == 0x4A ? Project(a.CurX[l] - b.CurX[k], a.CurY[l] - b.CurY[k]) : OriginalDistance(a, l, b, k));
                        break;
                    }

                case 0x4B: // MPPEM
                    Push(Ppem);
                    break;
                case 0x4C: // MPS
                    Push(PointSize);
                    break;
                case 0x4D:
                    _gs.AutoFlip = true;
                    break;
                case 0x4E:
                    _gs.AutoFlip = false;
                    break;
                case 0x4F: // DEBUG
                    throw Error("DEBUG.");
                case 0x50: // LT
                case 0x51:
                case 0x52:
                case 0x53:
                case 0x54:
                case 0x55:
                    {
                        int b = Pop(), a = Pop();
                        Push(op switch
                        {
                            0x50 => a < b,
                            0x51 => a <= b,
                            0x52 => a > b,
                            0x53 => a >= b,
                            0x54 => a == b,
                            _ => a != b,
                        } ? 1 : 0);
                        break;
                    }

                case 0x56: // ODD
                case 0x57: // EVEN
                    {
                        int v = Round(Pop()) & 127;
                        Push((op == 0x56 ? v == 64 : v == 0) ? 1 : 0);
                        break;
                    }

                case 0x58: // IF
                    if (Pop() == 0)
                    {
                        next = SkipBranch(code, ip, stopAtElse: true);
                    }

                    break;
                case 0x59: // EIF
                    break;
                case 0x5A: // AND
                    {
                        int b = Pop(), a = Pop();
                        Push(a != 0 && b != 0 ? 1 : 0);
                        break;
                    }

                case 0x5B: // OR
                    {
                        int b = Pop(), a = Pop();
                        Push(a != 0 || b != 0 ? 1 : 0);
                        break;
                    }

                case 0x5C: // NOT
                    Push(Pop() == 0 ? 1 : 0);
                    break;
                case 0x5D: // DELTAP1
                    DeltaP(0);
                    break;
                case 0x71:
                    DeltaP(16);
                    break;
                case 0x72:
                    DeltaP(32);
                    break;
                case 0x73: // DELTAC1
                    DeltaC(0);
                    break;
                case 0x74:
                    DeltaC(16);
                    break;
                case 0x75:
                    DeltaC(32);
                    break;
                case 0x5E: // SDB
                    _gs.DeltaBase = Pop();
                    break;
                case 0x5F: // SDS
                    {
                        int s = Pop();
                        _gs.DeltaShift = (uint)s <= 6 ? s : throw Error("SDS above 6.");
                        break;
                    }

                case 0x60: // ADD
                    {
                        int b = Pop(), a = Pop();
                        Push(unchecked(a + b));
                        break;
                    }

                case 0x61: // SUB
                    {
                        int b = Pop(), a = Pop();
                        Push(unchecked(a - b));
                        break;
                    }

                case 0x62: // DIV
                    {
                        int b = Pop(), a = Pop();
                        if (b == 0)
                        {
                            throw Error("DIV by zero.");
                        }

                        Push((int)Math.Clamp((long)a * 64 / b, int.MinValue, int.MaxValue));
                        break;
                    }

                case 0x63: // MUL
                    {
                        int b = Pop(), a = Pop();
                        Push((int)MulDiv(a, b, 64));
                        break;
                    }

                case 0x64:
                    Push(Math.Abs(Pop()));
                    break;
                case 0x65:
                    Push(unchecked(-Pop()));
                    break;
                case 0x66: // FLOOR
                    Push(Pop() & ~63);
                    break;
                case 0x67: // CEILING
                    Push(unchecked(Pop() + 63) & ~63);
                    break;
                case >= 0x68 and <= 0x6B: // ROUND (engine compensation 0)
                    Push(Round(Pop()));
                    break;
                case >= 0x6C and <= 0x6F: // NROUND
                    break;
                case 0x76: // SROUND
                    SetSuperRound(Pop(), 64);
                    _gs.Round = RoundMode.Super;
                    break;
                case 0x77: // S45ROUND
                    SetSuperRound(Pop(), 45);
                    _gs.Round = RoundMode.Super45;
                    break;
                case 0x78: // JROT
                case 0x79: // JROF
                    {
                        int e = Pop(), offset = Pop();
                        if ((e != 0) == (op == 0x78))
                        {
                            next = Jump(code, ip, offset);
                        }

                        break;
                    }

                case 0x7A:
                    _gs.Round = RoundMode.Off;
                    break;
                case 0x7C:
                    _gs.Round = RoundMode.UpToGrid;
                    break;
                case 0x7D:
                    _gs.Round = RoundMode.DownToGrid;
                    break;
                case 0x7E: // SANGW
                case 0x7F: // AA
                    Pop();
                    break;
                case 0x80:
                    FlipPoints();
                    break;
                case 0x81: // FLIPRGON
                case 0x82: // FLIPRGOFF
                    FlipRange(op == 0x81);
                    break;
                case 0x85: // SCANCTRL
                    _gs.ScanControl = Pop();
                    break;
                case 0x86: // SDPVTL
                case 0x87:
                    VectorToLine(op & 1, projection: true, dual: true);
                    break;
                case 0x88:
                    Push(GetInfo(Pop()));
                    break;
                case 0x8A: // ROLL: a b c -> b c a
                    {
                        int c = Pop(), b = Pop(), a = Pop();
                        Push(b);
                        Push(c);
                        Push(a);
                        break;
                    }

                case 0x8B:
                    {
                        int b = Pop(), a = Pop();
                        Push(Math.Max(a, b));
                        break;
                    }

                case 0x8C:
                    {
                        int b = Pop(), a = Pop();
                        Push(Math.Min(a, b));
                        break;
                    }

                case 0x8D: // SCANTYPE
                    _gs.ScanType = Pop();
                    break;
                case 0x8E: // INSTCTRL
                    InstructionControl();
                    break;
                case >= 0xB0 and <= 0xB7: // PUSHB
                    {
                        int n = op - 0xB0 + 1;
                        for (int i = 0; i < n; i++)
                        {
                            Push(code[ip + 1 + i]);
                        }

                        next = ip + 1 + n;
                        break;
                    }

                case >= 0xB8 and <= 0xBF: // PUSHW
                    {
                        int n = op - 0xB8 + 1;
                        for (int i = 0; i < n; i++)
                        {
                            Push((short)((code[ip + 1 + (2 * i)] << 8) | code[ip + 2 + (2 * i)]));
                        }

                        next = ip + 1 + (2 * n);
                        break;
                    }

                case >= 0xC0 and <= 0xDF:
                    Mdrp(op & 0x1F);
                    break;
                case >= 0xE0:
                    Mirp(op & 0x1F);
                    break;
                default:
                    {
                        // An instruction definition may give an unused opcode a meaning.
                        if (!_instructionDefs.TryGetValue(op, out Def def))
                        {
                            throw Error($"Unknown opcode 0x{op:X2}.");
                        }

                        if (frames.Count >= MaxCallDepth)
                        {
                            throw Error("Calls nest too deep.");
                        }

                        frames.Push(new Frame(code, next, def, 1));
                        code = def.Code;
                        next = def.Start;
                        break;
                    }
            }

            ip = next;
        }
    }

    private int Pop() => _sp > 0 ? _stack[--_sp] : 0;

    private void Push(int value)
    {
        if (_sp >= _stack.Length)
        {
            throw Error("Stack overflow.");
        }

        _stack[_sp++] = value;
    }

    private static int ZoneIndex(int zone) => zone is 0 or 1 ? zone : throw Error($"Zone {zone}.");

    private Zone Z(int zone) => zone == 0 ? _twilight : _glyph;

    private static void CheckPoint(Zone zone, int p)
    {
        if ((uint)p >= (uint)zone.Count)
        {
            throw Error($"Point {p} out of range.");
        }
    }

    private int CvtValue(int index) => (uint)index < (uint)_cvt.Length ? _cvt[index] : 0;

    // MPS answers the point size at 96 pixels per inch, as GDI and GDI+ do (12 at 16 ppem).
    private int PointSize => (Ppem * 72 + 48) / 96;

    // A relative jump from the jump instruction itself; a zero offset is an endless loop and an error.
    private static int Jump(byte[] code, int ip, int offset)
    {
        if (offset == 0)
        {
            throw Error("A jump to itself.");
        }

        int target = ip + offset;
        return target >= 0 && target <= code.Length ? target : throw Error("A jump out of the program.");
    }

    private static int Length(byte[] code, int ip)
    {
        byte op = code[ip];
        return op switch
        {
            0x40 => 2 + code[ip + 1],
            0x41 => 2 + (2 * code[ip + 1]),
            >= 0xB0 and <= 0xB7 => 1 + (op - 0xB0 + 1),
            >= 0xB8 and <= 0xBF => 1 + (2 * (op - 0xB8 + 1)),
            _ => 1,
        };
    }

    // From an IF (or an ELSE being run), the instruction after the matching ELSE (only when stopAtElse) or EIF.
    private static int SkipBranch(byte[] code, int ip, bool stopAtElse)
    {
        int depth = 1;
        ip += Length(code, ip);
        while (ip < code.Length)
        {
            byte op = code[ip];
            if (op == 0x58)
            {
                depth++;
            }
            else if (op == 0x59 && --depth == 0)
            {
                return ip + 1;
            }
            else if (op == 0x1B && depth == 1 && stopAtElse)
            {
                return ip + 1;
            }

            ip += Length(code, ip);
        }

        throw Error("IF without EIF.");
    }

    // From an FDEF or IDEF, the instruction after its ENDF.
    private static int SkipDefinition(byte[] code, int ip)
    {
        ip += Length(code, ip);
        while (ip < code.Length)
        {
            byte op = code[ip];
            if (op == 0x2D)
            {
                return ip + 1;
            }

            if (op is 0x2C or 0x89)
            {
                throw Error("A definition inside a definition.");
            }

            ip += Length(code, ip);
        }

        throw Error("FDEF without ENDF.");
    }

    private int GetInfo(int selector)
    {
        int result = 0;
        if ((selector & 1) != 0)
        {
            result |= _target.Version & 0xFF;
        }

        for (int bit = 1; bit < 24; bit++)
        {
            if ((selector & (1 << bit)) != 0)
            {
                result |= _target.InfoFlags & (1 << (bit + 7));
            }
        }

        return result;
    }

    private void InstructionControl()
    {
        int selector = Pop(), value = Pop();
        if (selector is < 1 or > 3)
        {
            return;
        }

        int flag = 1 << (selector - 1);
        if (value != 0 && value != flag)
        {
            return;
        }

        // Selectors 1 and 2 count only in the control value program; 3 (native ClearType) anywhere.
        if (selector < 3 && _range != Prep)
        {
            return;
        }

        _gs.InstructControl = (_gs.InstructControl & ~flag) | value;
    }
}
