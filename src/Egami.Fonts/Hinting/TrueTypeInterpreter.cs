namespace Egami.Fonts.Hinting;

/// <summary>What an interpreter answers GETINFO and MPS with, and how far it follows ClearType backward compatibility.</summary>
/// <param name="Version">The rasterizer version, GETINFO's bits 0 to 7.</param>
/// <param name="InfoFlags">The GETINFO result bits 8 and up this target sets; a program sees those its selector asks for.</param>
/// <param name="Compatibility">The ClearType backward-compatibility mode.</param>
/// <param name="AdvanceBias">
/// How far, in pixels, the advance phantom point's scaled position is moved before it rounds to a whole pixel; 0 rounds it as
/// the TrueType specification does.
/// </param>
internal sealed record InterpreterTarget(int Version, int InfoFlags, CompatibilityMode Compatibility, float AdvanceBias = 0)
{
    /// <summary>
    /// GDI+ ClearTypeGridFit as the 2.7.5 probes measured it: version 42, bits 10, 11 and 13 (GETINFO 0x2C2A). Its advances round
    /// up from about 0.458 of a pixel, not 0.5 (measured in 2.7.6 from glyph origins in drawn lines), an advance bias of 0.042.
    /// </summary>
    public static InterpreterTarget GdiPlus { get; } = new(42, 0x2C00, CompatibilityMode.VirtualGrid, 0.042f);

    /// <summary>GDI ClearType as measured: version 42, bits 10, 11, 13, 14 and 18 (GETINFO 0x46C2A).</summary>
    public static InterpreterTarget Gdi { get; } = new(42, 0x46C00, CompatibilityMode.VirtualGrid);

    /// <summary>A FreeType-v40-like target: version 40, ClearType (13) and subpixel positioned (17).</summary>
    public static InterpreterTarget FreeType { get; } = new(40, 0x22000, CompatibilityMode.FrozenX);

    /// <summary>Bi-level hinting as the instructions were written for: version 35, no ClearType bits, no compatibility mode.</summary>
    public static InterpreterTarget Monochrome { get; } = new(35, 0, CompatibilityMode.None);
}

/// <summary>How the interpreter softens hints written for black-and-white screens when rendering for ClearType.</summary>
internal enum CompatibilityMode
{
    /// <summary>Instructions run as written.</summary>
    None,

    /// <summary>Stage A, FreeType's minimal set: no moves in x, nothing after both IUPs, SHPIX and DELTAP limited.</summary>
    FrozenX,

    /// <summary>
    /// Stage B, Microsoft's ClearType backward compatibility as GDI and GDI+ show it: in the ClearType direction (x) rounding is to
    /// a 1/16-pixel virtual grid, the control value cut-in is a sixteenth and the minimum distance half as large, MIRP always
    /// applies the cut-in, and DELTAP acts only on points already touched in y. The control value program rounds to whole pixels.
    /// </summary>
    VirtualGrid,
}

/// <summary>Why a program stopped.</summary>
internal sealed class HintingException(string message) : Exception(message);

/// <summary>
/// A TrueType bytecode interpreter for one face, written from the OpenType specification's instruction set and instructing-fonts
/// chapters, Apple's TrueType Reference Manual and the 2.7.5 FreeType study (never FreeType's code). Per face it holds the font
/// program's functions; per size it runs the control value program once and keeps the scaled control values, storage, twilight
/// zone and graphics state; per glyph it runs the glyph's program on a zone, with control value and storage writes kept private
/// to that glyph so results never depend on drawing order.
/// </summary>
internal sealed partial class TrueTypeInterpreter
{
    private const int Fpgm = 1, Prep = 2, GlyphRange = 3;

    private readonly HintingData _data;
    private readonly InterpreterTarget _target;
    private readonly Def[] _functions;
    private readonly Dictionary<int, Def> _instructionDefs = [];
    private readonly int[] _stack;

    private bool _fpgmRan;
    private bool _fpgmFailed;
    private int[] _cvt = [];
    private int[] _storage = [];
    private readonly Zone _twilight;
    private GraphicsState _prepState = new();

    // The run in progress.
    private GraphicsState _gs = new();
    private Zone _glyph;
    private int _sp;
    private long _budget;
    private int _range;
    private bool _iupX;
    private bool _iupY;
    private bool _composite;
    private long _glyphScale;

    public TrueTypeInterpreter(HintingData data, InterpreterTarget target)
    {
        _data = data;
        _target = target;
        _functions = new Def[Math.Max(data.MaxFunctionDefs, 0)];
        int declared = Math.Max(data.MaxStackElements, 0);
        _stack = new int[declared + Math.Max(declared / 2, 128) + 32];
        _twilight = new Zone(Math.Min(data.MaxTwilightPoints, 0x8000) + 4, twilight: true);
        _glyph = new Zone(0, twilight: false);
    }

    /// <summary>Gets the ppem of the current size.</summary>
    public int Ppem { get; private set; }

    /// <summary>Gets the scale of the current size: font units to 26.6 pixels, in 16.16.</summary>
    public long Scale { get; private set; }

    /// <summary>Gets a value indicating whether the current size is hinted: the font program and control value program ran.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Gets the target this interpreter answers for.</summary>
    public InterpreterTarget Target => _target;

    /// <summary>Gets the control values of the current size in 26.6, after the control value program.</summary>
    internal int[] Cvt => _cvt;

    /// <summary>Gets the storage area of the current size.</summary>
    internal int[] Storage => _storage;

    /// <summary>Gets the graphics state the control value program left.</summary>
    internal GraphicsState PrepState => _prepState;

    /// <summary>Gets the twilight zone.</summary>
    internal Zone Twilight => _twilight;

    /// <summary>
    /// Sets up a size: runs the font program the first time, scales the control values and runs the control value program.
    /// Returns false when either program fails, after which the size is not hinted.
    /// </summary>
    public bool SetSize(int ppem, long scale)
    {
        Ppem = ppem;
        Scale = scale;
        IsReady = false;
        if (!_fpgmRan)
        {
            _fpgmRan = true;
            _storage = new int[Math.Max(_data.MaxStorage, 0)];
            _cvt = [];
            _gs = new GraphicsState();
            _fpgmFailed = !Run(Fpgm, _data.Fpgm, Empty, 100_000 + (100L * _data.Fpgm.Length));
        }

        if (_fpgmFailed)
        {
            return false;
        }

        _cvt = new int[_data.Cvt.Length];
        for (int i = 0; i < _cvt.Length; i++)
        {
            _cvt[i] = (int)MulFix(_data.Cvt[i], scale);
        }

        Array.Clear(_storage);
        _twilight.Reset();
        _gs = new GraphicsState();
        _glyphScale = scale;
        if (!Run(Prep, _data.Prep, Empty, 300_000 + (22L * _cvt.Length) + (100L * _data.Prep.Length)))
        {
            return false;
        }

        _prepState = _gs.Clone();
        IsReady = true;
        return true;
    }

    /// <summary>
    /// Runs a glyph's program on <paramref name="glyph"/>. Its control value and storage writes are dropped afterwards. With
    /// <paramref name="composite"/>, the zone's unscaled positions are already 26.6 pixels (a composite's assembled points) and the
    /// program measures originals with a scale of 1. Returns false when the program stopped with an error; the points keep the
    /// moves made until then.
    /// </summary>
    public bool RunGlyph(Zone glyph, byte[] program, bool composite = false)
    {
        if (!IsReady || program.Length == 0 || (_prepState.InstructControl & 1) != 0)
        {
            return IsReady;
        }

        int[] cvt = _cvt, storage = _storage;
        _cvt = (int[])cvt.Clone();
        _storage = (int[])storage.Clone();
        try
        {
            _gs = (_prepState.InstructControl & 2) != 0 ? new GraphicsState() : _prepState.ForGlyph();
            _composite = composite;
            _glyphScale = composite ? 0x10000 : Scale;
            long budget = Math.Max(100_000L, 1000L * (glyph.Count + cvt.Length)) + (100L * program.Length);
            return Run(GlyphRange, program, glyph, budget);
        }
        finally
        {
            _cvt = cvt;
            _storage = storage;
            _composite = false;
            _glyphScale = Scale;
        }
    }

    /// <summary>Runs a program on a glyph zone with the current state as it is, for tests of single instructions.</summary>
    internal bool RunForTest(byte[] program, Zone glyph, GraphicsState? state = null)
    {
        _gs = state ?? _gs;
        _glyphScale = Scale;
        return Run(GlyphRange, program, glyph, 1_000_000);
    }

    /// <summary>Gets the state left by the last run, for tests.</summary>
    internal GraphicsState LastState => _gs;

    /// <summary>Gets the stack left by the last run, bottom first, for tests.</summary>
    internal int[] LastStack => _stack[.._sp];

    private bool Run(int range, byte[] code, Zone glyph, long budget)
    {
        _glyph = glyph;
        _sp = 0;
        _budget = budget;
        _range = range;
        _iupX = _iupY = false;
        try
        {
            Execute(code);
            return true;
        }
        catch (HintingException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (ArithmeticException)
        {
            return false;
        }
    }

    // The glyph zone while the font and control value programs run: no points.
    private static readonly Zone Empty = new(0, twilight: false);

    private static HintingException Error(string message) => new(message);

    // A function or instruction definition: the code it lives in and where its body starts.
    private readonly record struct Def(byte[] Code, int Start, bool Defined);

    private readonly record struct Frame(byte[] Code, int ReturnIp, Def Def, int Remaining);
}
