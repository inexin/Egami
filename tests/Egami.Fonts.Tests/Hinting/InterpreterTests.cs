using Egami.Fonts.Hinting;

namespace Egami.Fonts.Tests.Hinting;

/// <summary>
/// The TrueType interpreter (2.7.5 tasks 12 to 15), one instruction or rule at a time, from hand-written programs. Sizes are 16
/// ppem with 1024 units per em, so one font unit is one 26.6 unit (1/64 pixel) and unscaled positions equal original ones.
/// </summary>
public class InterpreterTests
{
    private const long Scale = 0x10000;

    private static HintingData Data(short[]? cvt = null, byte[]? fpgm = null, byte[]? prep = null) => new()
    {
        MaxStackElements = 64,
        MaxStorage = 16,
        MaxFunctionDefs = 16,
        MaxTwilightPoints = 8,
        Cvt = cvt ?? [100, 64, 200, -70],
        Fpgm = fpgm ?? [],
        Prep = prep ?? [],
    };

    private static TrueTypeInterpreter Ready(HintingData? data = null, InterpreterTarget? target = null)
    {
        var t = new TrueTypeInterpreter(data ?? Data(), target ?? InterpreterTarget.Monochrome);
        Assert.True(t.SetSize(16, Scale));
        return t;
    }

    // A one-contour square (0,0)-(0,200)-(200,200)-(200,0) and a second contour at x 400-500, plus four phantom points.
    private static Zone Square()
    {
        int[] xs = [0, 0, 200, 200, 400, 400, 500, 500, 0, 600, 0, 0];
        int[] ys = [0, 200, 200, 0, 0, 100, 100, 0, 0, 0, 0, 0];
        var z = new Zone(xs.Length, twilight: false) { ContourEnds = [3, 7], OutlineCount = 8 };
        for (int i = 0; i < xs.Length; i++)
        {
            z.UnscaledX[i] = z.OrgX[i] = z.CurX[i] = xs[i];
            z.UnscaledY[i] = z.OrgY[i] = z.CurY[i] = ys[i];
            z.OnCurve[i] = true;
        }

        return z;
    }

    // NPUSHW of the values, then the opcodes.
    private static byte[] Code(int[] push, params byte[] ops)
    {
        var b = new List<byte>();
        if (push.Length > 0)
        {
            b.Add(0x41);
            b.Add((byte)push.Length);
            foreach (int v in push)
            {
                b.Add((byte)(v >> 8));
                b.Add((byte)v);
            }
        }

        b.AddRange(ops);
        return [.. b];
    }

    private static int[] Stack(byte[] code, TrueTypeInterpreter? t = null)
    {
        t ??= Ready();
        Assert.True(t.RunForTest(code, Square(), new GraphicsState()));
        return t.LastStack;
    }

    [Fact]
    public void PushesReadBytesAndSignedWords()
    {
        Assert.Equal([1, 2, 3], Stack([0xB2, 1, 2, 3]));
        Assert.Equal([-2, 300], Stack([0xB9, 0xFF, 0xFE, 0x01, 0x2C]));
        Assert.Equal([7, 8], Stack([0x40, 2, 7, 8]));
        Assert.Equal([-1], Stack([0x41, 1, 0xFF, 0xFF]));
    }

    [Fact]
    public void StackInstructions()
    {
        Assert.Equal([5, 5], Stack(Code([5], 0x20)));
        Assert.Equal([1], Stack(Code([1, 2], 0x21)));
        Assert.Equal([], Stack(Code([1, 2], 0x22)));
        Assert.Equal([2, 1], Stack(Code([1, 2], 0x23)));
        Assert.Equal([9, 9, 2], Stack(Code([9, 9], 0x24)));
        Assert.Equal([10, 20, 30, 10], Stack(Code([10, 20, 30, 3], 0x25)));
        Assert.Equal([20, 30, 10], Stack(Code([10, 20, 30, 3], 0x26)));
        Assert.Equal([2, 3, 1], Stack(Code([1, 2, 3], 0x8A)));
    }

    [Fact]
    public void Arithmetic()
    {
        Assert.Equal([150], Stack(Code([100, 50], 0x60)));
        Assert.Equal([50], Stack(Code([100, 50], 0x61)));
        Assert.Equal([128], Stack(Code([64, 32], 0x62))); // 1.0 / 0.5 = 2.0
        Assert.Equal([-32], Stack(Code([-64, 128], 0x62)));
        Assert.Equal([32], Stack(Code([64, 32], 0x63))); // 1.0 × 0.5
        Assert.Equal([5], Stack(Code([-5], 0x64)));
        Assert.Equal([-5], Stack(Code([5], 0x65)));
        Assert.Equal([64, -128], Stack(Code([100, -100], 0x23, 0x66, 0x23, 0x66))); // FLOOR of both
        Assert.Equal([128], Stack(Code([65], 0x67)));
        Assert.Equal([9], Stack(Code([3, 9], 0x8B)));
        Assert.Equal([3], Stack(Code([3, 9], 0x8C)));
        Assert.False(Ready().RunForTest(Code([1, 0], 0x62), Square(), new GraphicsState()));
    }

    [Fact]
    public void ComparisonsAndLogic()
    {
        Assert.Equal([1], Stack(Code([1, 2], 0x50)));
        Assert.Equal([1], Stack(Code([2, 2], 0x51)));
        Assert.Equal([0], Stack(Code([1, 2], 0x52)));
        Assert.Equal([0], Stack(Code([1, 2], 0x53)));
        Assert.Equal([0], Stack(Code([1, 2], 0x54)));
        Assert.Equal([1], Stack(Code([1, 2], 0x55)));
        Assert.Equal([0], Stack(Code([1, 0], 0x5A)));
        Assert.Equal([1], Stack(Code([1, 0], 0x5B)));
        Assert.Equal([1], Stack(Code([0], 0x5C)));
        Assert.Equal([1], Stack(Code([64], 0x56))); // ODD: 1.0 rounds to 1
        Assert.Equal([1], Stack(Code([130], 0x57))); // EVEN: 2.03 rounds to 2
    }

    [Fact]
    public void IfElseNestsAndSkipsPushData()
    {
        // 0 IF [PUSHB 0x58 (an IF byte as data); IF ... EIF] ELSE 7 EIF
        byte[] code = [0xB0, 0, 0x58, 0xB0, 0x58, 0xB0, 1, 0x58, 0x59, 0x1B, 0xB0, 7, 0x59];
        Assert.Equal([7], Stack(code));
        byte[] taken = [0xB0, 1, 0x58, 0xB0, 5, 0x1B, 0xB0, 7, 0x59];
        Assert.Equal([5], Stack(taken));
    }

    [Fact]
    public void Jumps()
    {
        // JMPR over a PUSHB: offset counts from the jump instruction.
        Assert.Equal([2], Stack([0xB0, 3, 0x1C, 0xB0, 1, 0xB0, 2]));
        Assert.Equal([2], Stack([0xB1, 3, 1, 0x78, 0xB0, 1, 0xB0, 2])); // JROT taken
        Assert.Equal([1, 2], Stack([0xB1, 3, 1, 0x79, 0xB0, 1, 0xB0, 2])); // JROF not taken
        Assert.False(Ready().RunForTest([0xB0, 0, 0x1C], Square(), new GraphicsState())); // jump to itself
        Assert.False(Ready().RunForTest([0xB8, 0xFF, 0xFF, 0x1C], Square(), new GraphicsState())); // a backward loop forever
    }

    [Fact]
    public void FunctionsCallsLoopCallsAndInstructionDefinitions()
    {
        // fpgm: FDEF 0 { PUSHB 1, ADD } ENDF; IDEF 0x91 { PUSHB 42 } ENDF
        byte[] fpgm = [0xB0, 0, 0x2C, 0xB0, 1, 0x60, 0x2D, 0xB0, 0x91, 0x89, 0xB0, 42, 0x2D];
        TrueTypeInterpreter t = Ready(Data(fpgm: fpgm));
        Assert.Equal([11], Stack(Code([10, 0], 0x2B), t));
        Assert.Equal([13], Stack(Code([10, 3, 0], 0x2A), t)); // LOOPCALL 3 times
        Assert.Equal([42], Stack([0x91], t));
        Assert.False(t.RunForTest(Code([5], 0x2B), Square(), new GraphicsState())); // undefined function
        Assert.False(t.RunForTest([0x2D], Square(), new GraphicsState())); // ENDF outside a function
    }

    [Fact]
    public void StorageAndControlValues()
    {
        TrueTypeInterpreter t = Ready();
        Assert.Equal([77], Stack(Code([3, 77], 0x42, 0xB0, 3, 0x43), t));
        Assert.Equal([0], Stack(Code([99], 0x43), t)); // out of range reads 0
        Assert.Equal([64], Stack(Code([1], 0x45), t)); // cvt[1] = 64 units = 1 px at this scale
        Assert.Equal([0], Stack(Code([50], 0x45), t)); // out of range reads 0
        Assert.Equal([-70], Stack(Code([3], 0x45), t));
        Assert.Equal([123], Stack(Code([0, 123], 0x44, 0xB0, 0, 0x45), t)); // WCVTP
        Assert.Equal([32], Stack(Code([0, 32], 0x70, 0xB0, 0, 0x45), t)); // WCVTF scales font units
    }

    [Fact]
    public void ControlValuesScaleWithTheSize()
    {
        var t = new TrueTypeInterpreter(Data(), InterpreterTarget.Monochrome);
        Assert.True(t.SetSize(8, Scale / 2));
        Assert.Equal([50, 32, 100, -35], t.Cvt);
    }

    [Fact]
    public void GlyphWritesToControlValuesAndStorageArePrivate()
    {
        TrueTypeInterpreter t = Ready();
        Assert.True(t.RunGlyph(Square(), Code([0, 999, 2, 5], 0x44, 0x42)));
        Assert.Equal(100, t.Cvt[0]);
        Assert.Equal(0, t.Storage[2]);
    }

    [Fact]
    public void GetInfoAnswersPerTarget()
    {
        Assert.Equal([0x2C2A], Stack(Code([0x3FFF], 0x88), Ready(target: InterpreterTarget.GdiPlus)));
        Assert.Equal([0x46C2A], Stack(Code([0x3FFF], 0x88), Ready(target: InterpreterTarget.Gdi)));
        Assert.Equal([42], Stack(Code([1], 0x88), Ready(target: InterpreterTarget.Gdi)));
        Assert.Equal([0x2000], Stack(Code([0x40], 0x88), Ready(target: InterpreterTarget.GdiPlus))); // ClearType only
        Assert.Equal([35], Stack(Code([0x3FFF], 0x88)));
        Assert.Equal([16, 12], Stack([0x4B, 0x4C]));
    }

    [Fact]
    public void InstructionControlCountsInTheControlValueProgramOnly()
    {
        // prep: INSTCTRL selector 1 value 1 stops glyph programs.
        TrueTypeInterpreter t = Ready(Data(prep: Code([1, 1], 0x8E)));
        Zone z = Square();
        Assert.True(t.RunGlyph(z, Code([3, 64], 0x00, 0x38)));
        Assert.Equal(0, z.CurY[3]);

        // In a glyph program, selector 1 is ignored.
        TrueTypeInterpreter plain = Ready();
        Assert.True(plain.RunForTest(Code([1, 1], 0x8E), Square(), new GraphicsState()));
        Assert.Equal(0, plain.LastState.InstructControl);
    }

    [Theory]
    [InlineData(0x18, 95, 64)]
    [InlineData(0x18, 96, 128)]
    [InlineData(0x18, -96, -128)]
    [InlineData(0x18, 10, 0)]
    [InlineData(0x19, 10, 32)] // half grid
    [InlineData(0x19, -70, -96)]
    [InlineData(0x3D, 50, 64)] // double grid
    [InlineData(0x3D, 40, 32)]
    [InlineData(0x7D, 127, 64)] // down
    [InlineData(0x7C, 65, 128)] // up
    [InlineData(0x7A, 77, 77)] // off
    public void RoundingModes(byte mode, int value, int rounded)
        => Assert.Equal([rounded], Stack(Code([value], mode, 0x68)));

    [Fact]
    public void SuperRounding()
    {
        // Period 1, phase 0, threshold 1/2: as RTG.
        Assert.Equal([64], Stack(Code([0x48, 95], 0x23, 0x76, 0x68)));
        // Period 1, phase 1/4, threshold 1/2 (0x58): 40 -> 16, 100 -> 80.
        Assert.Equal([16], Stack(Code([0x58, 40], 0x23, 0x76, 0x68)));
        Assert.Equal([80], Stack(Code([0x58, 100], 0x23, 0x76, 0x68)));
        // Threshold 0 means period less one: everything rounds up.
        Assert.Equal([64], Stack(Code([0x40, 1], 0x23, 0x76, 0x68)));
        // S45ROUND, period 1 (about 45): 50 -> 45.
        Assert.Equal([45], Stack(Code([0x48, 50], 0x23, 0x77, 0x68)));
    }

    [Fact]
    public void Vectors()
    {
        TrueTypeInterpreter t = Ready();
        // SPVTL pops p1 (top) then p2 and points from p1 to p2: from point 0 (0,0) to point 2 (200,200) is the diagonal up.
        Assert.Equal([11585, 11585], Stack(Code([2, 0], 0x06, 0x0C), t));
        // The other order points the other way.
        Assert.Equal([-11585, -11585], Stack(Code([0, 2], 0x06, 0x0C), t));
        // Perpendicular: rotated 90° counter-clockwise.
        Assert.Equal([-11585, 11585], Stack(Code([2, 0], 0x07, 0x0C), t));
        // SFVTL the same for the freedom vector, from point 0 to point 3 (200,0): the x axis.
        Assert.Equal([16384, 0], Stack(Code([3, 0], 0x08, 0x0D), t));
        // SFVFS normalises; GFV reads it back.
        Assert.Equal([0, 16384], Stack(Code([0, 5], 0x0B, 0x0D), t));
        // Coinciding points give the x axis.
        Assert.Equal([16384, 0], Stack(Code([0, 0], 0x06, 0x0C), t));
    }

    [Fact]
    public void VectorToLineReadsP1FromZp2AndP2FromZp1()
    {
        // p1 = twilight point 3 (at the origin after the reset) through zp2 = 0; p2 = glyph point 2 (200,200) through zp1 = 1.
        // Read with the zones swapped, the points would be glyph point 3 (200,0) and twilight point 2 (0,0): a different line.
        TrueTypeInterpreter t = Ready();
        Assert.Equal([11585, 11585], Stack(Code([2, 3, 0], 0x15, 0x06, 0x0C), t));

        // SDPVTL: the dual vector from original positions, the projection from current ones. Point 2 moved right by 200 after
        // loading: the projection follows the move, the dual vector keeps the original diagonal.
        Zone z = Square();
        z.CurX[2] = 400;
        var s = new GraphicsState();
        Assert.True(t.RunForTest(Code([2, 0], 0x86), z, s));
        Assert.Equal((14654, 7327), (t.LastState.ProjX, t.LastState.ProjY));
        Assert.Equal((11585, 11585), (t.LastState.DualX, t.LastState.DualY));
    }

    private static (TrueTypeInterpreter T, Zone Z) Move(byte[] code, InterpreterTarget? target = null, GraphicsState? state = null)
    {
        TrueTypeInterpreter t = Ready(target: target);
        Zone z = Square();
        Assert.True(t.RunForTest(code, z, state ?? new GraphicsState()));
        return (t, z);
    }

    [Fact]
    public void MdapRoundsAPointAndTouchesIt()
    {
        Zone start = Square();
        start.CurX[2] = start.OrgX[2] = start.UnscaledX[2] = 220;
        TrueTypeInterpreter t = Ready();
        Assert.True(t.RunForTest(Code([2], 0x01, 0x2F), start, new GraphicsState()));
        Assert.Equal(192, start.CurX[2]);
        Assert.Equal(Zone.TouchX, start.Touched[2]);
        Assert.Equal(2, t.LastState.Rp0);
    }

    [Fact]
    public void MiapUsesTheControlValueWithinTheCutIn()
    {
        // Point 1 at y 200; cvt[2] = 200 rounds to 192. cvt[0] = 100 is beyond the 68 cut-in, so the outline's 200 rounds instead.
        (_, Zone z) = Move(Code([1, 2], 0x00, 0x3F));
        Assert.Equal(192, z.CurY[1]);
        (_, z) = Move(Code([1, 0], 0x00, 0x3F));
        Assert.Equal(192, z.CurY[1]);
        (_, z) = Move(Code([1, 0], 0x00, 0x3E)); // no rounding: the control value as it is
        Assert.Equal(100, z.CurY[1]);
    }

    [Fact]
    public void MdrpKeepsTheOriginalDistanceRoundedAndAtLeastTheMinimum()
    {
        // rp0 = 0 (y 0), move point 1 (y 200) to its rounded distance: 192.
        (TrueTypeInterpreter t, Zone z) = Move(Code([1, 0], 0x00, 0x10, 0xC4));
        Assert.Equal(192, z.CurY[1]);
        Assert.Equal(1, t.LastState.Rp2);
        Assert.Equal(0, t.LastState.Rp1);

        // Point 5 is 100 above point 4; with a 2-pixel minimum distance MDRP[min] moves it to 128.
        (_, z) = Move(Code([5, 4, 128], 0x00, 0x1A, 0x10, 0xC8));
        Assert.Equal(128, z.CurY[5]);
    }

    [Fact]
    public void MirpUsesTheControlValueAndAutoFlip()
    {
        // From point 0, point 1 (200 above) by cvt[2] = 200, rounded: 192.
        (_, Zone z) = Move(Code([1, 2, 0], 0x00, 0x10, 0xE4));
        Assert.Equal(192, z.CurY[1]);

        // cvt[3] = -70 against an upward distance: auto-flip makes it +70, outside the cut-in of 200? |70 - 200| > 68: outline wins.
        (_, z) = Move(Code([1, 3, 0], 0x00, 0x10, 0xE4));
        Assert.Equal(192, z.CurY[1]);

        // Not rounded: the control value as it is, flipped to the outline's sign.
        (_, z) = Move(Code([1, 3, 0], 0x00, 0x10, 0xE0));
        Assert.Equal(70, z.CurY[1]);

        // Index -1 reads as 0; with the minimum distance it becomes 1 pixel.
        (_, z) = Move(Code([1, -1, 0], 0x00, 0x10, 0xE8));
        Assert.Equal(64, z.CurY[1]);
    }

    [Fact]
    public void MsirpSetsADistanceFromRp0()
    {
        (TrueTypeInterpreter t, Zone z) = Move(Code([1, 300, 0], 0x00, 0x10, 0x3B));
        Assert.Equal(300, z.CurY[1]);
        Assert.Equal(1, t.LastState.Rp0);
    }

    [Fact]
    public void ShiftsFollowTheReferencePoint()
    {
        // Move point 0 up by 30 with MSIRP-free SHPIX, make it rp2, then SHP point 3 by the same.
        (_, Zone z) = Move(Code([3, 0, 0, 30], 0x00, 0x38, 0x12, 0x32));
        Assert.Equal(30, z.CurY[3]);

        // SHC: every point of contour 1 but the reference (point 4) shifts with it.
        (_, z) = Move(Code([1, 4, 4, 20], 0x00, 0x38, 0x12, 0x34));
        Assert.Equal([20, 120, 120, 20], z.CurY[4..8]);
        Assert.Equal(0, z.CurY[0]);

        // SHZ shifts the whole zone but the reference, not the phantom points, and touches nothing.
        (_, z) = Move(Code([1, 0, 0, 10], 0x00, 0x38, 0x12, 0x36));
        Assert.Equal(10, z.CurY[7]);
        Assert.Equal(0, z.CurY[9]);
        Assert.Equal(0, z.Touched[7]);
    }

    [Fact]
    public void ShpixMovesAlongTheFreedomVector()
    {
        (_, Zone z) = Move(Code([2, 3, 64, 2], 0x01, 0x17, 0x38));
        Assert.Equal(264, z.CurX[2]);
        Assert.Equal(264, z.CurX[3]);
        Assert.Equal(Zone.TouchX, z.Touched[2]);
    }

    [Fact]
    public void IpKeepsRatiosAndAlignRpAligns()
    {
        // rp1 = point 0 (y 0, stays), rp2 = point 1 moved from 200 to 300; point 5 (y 100, half way) goes to 150.
        (_, Zone z) = Move(Code([5, 1, 0, 1, 100], 0x00, 0x38, 0x11, 0x12, 0x39));
        Assert.Equal(150, z.CurY[5]);

        // ALIGNRP: point 5 to rp0's (point 1) y.
        (_, z) = Move(Code([5, 1], 0x00, 0x10, 0x3C));
        Assert.Equal(200, z.CurY[5]);

        // ALIGNPTS: points 0 and 1 meet half way.
        (_, z) = Move(Code([0, 1], 0x00, 0x27));
        Assert.Equal(100, z.CurY[0]);
        Assert.Equal(100, z.CurY[1]);
    }

    [Fact]
    public void IsectFindsTheCrossing()
    {
        // Diagonals of the square: (0,0)-(200,200) and (0,200)-(200,0) cross at (100,100); put it in point 4.
        (_, Zone z) = Move(Code([4, 0, 2, 1, 3], 0x0F));
        Assert.Equal(100, z.CurX[4]);
        Assert.Equal(100, z.CurY[4]);
        Assert.Equal(Zone.TouchX | Zone.TouchY, z.Touched[4]);
    }

    [Fact]
    public void IupInterpolatesUntouchedPoints()
    {
        // Touch points 4 (y 0 -> 0) and 5 (y 100 -> 200) of contour 1: 6 (with 5's unscaled y) follows 5, 7 (with 4's) stays.
        (_, Zone z) = Move(Code([4, 5, 100], 0x00, 0x38, 0x2E, 0x30));
        Assert.Equal(200, z.CurY[6]);
        Assert.Equal(0, z.CurY[7]);

        // One touched point shifts its whole contour.
        (_, z) = Move(Code([0, 50], 0x00, 0x38, 0x30));
        Assert.Equal([50, 250, 250, 50], z.CurY[0..4]);
        Assert.Equal(0, z.CurY[4]);
    }

    [Fact]
    public void IupInterpolatesBetweenReferences()
    {
        // A contour with a point between two touched ones: (0,0), (50,0), (100,0); move point 2 to 200 and touch point 0.
        var z = new Zone(7, twilight: false) { ContourEnds = [2], OutlineCount = 3 };
        int[] xs = [0, 50, 100];
        for (int i = 0; i < 3; i++)
        {
            z.UnscaledX[i] = z.OrgX[i] = z.CurX[i] = xs[i];
        }

        TrueTypeInterpreter t = Ready();
        Assert.True(t.RunForTest(Code([0, 2, 100], 0x01, 0x38, 0x2E, 0x31), z, new GraphicsState()));
        Assert.Equal(200, z.CurX[2]);
        Assert.Equal(100, z.CurX[1]);
    }

    [Fact]
    public void UntouchAndFlips()
    {
        (_, Zone z) = Move(Code([0, 0, 10], 0x00, 0x38, 0x29));
        Assert.Equal(0, z.Touched[0]);
        (_, z) = Move(Code([2], 0x80));
        Assert.False(z.OnCurve[2]);
        (_, z) = Move(Code([1, 3], 0x82));
        Assert.Equal([true, false, false, false], z.OnCurve[0..4]);
    }

    [Fact]
    public void DeltasApplyAtTheirPpem()
    {
        // DELTAP1 at 16 ppem (base 9: nibble 7), +8 steps of 1/8: point 1 up a pixel. Point 99 is out of range and skipped.
        (_, Zone z) = Move(Code([0x7F, 1, 0x7F, 99, 2], 0x00, 0x5D));
        Assert.Equal(264, z.CurY[1]);
        (_, z) = Move(Code([0x6F, 1, 1], 0x00, 0x5D)); // 15 ppem: nothing
        Assert.Equal(200, z.CurY[1]);

        // DELTAC1 adds to cvt[0].
        TrueTypeInterpreter t = Ready();
        Assert.Equal([108], Stack(Code([0x78, 0, 1], 0x73, 0xB0, 0, 0x45), t)); // +1 step = 8
    }

    [Fact]
    public void MeasuringAndSetting()
    {
        // GC[0] current y of point 1 (200), GC[1] original.
        Assert.Equal([200, 200], Stack(Code([1, 1], 0x00, 0x46, 0x23, 0x47)));
        // MD pops p1 (top, zp1) then p2 (zp0) and measures p2 less p1, as GDI and GDI+ do: point 0 less point 1 is -200.
        Assert.Equal([-200], Stack(Code([0, 1], 0x00, 0x49)));
        Assert.Equal([200], Stack(Code([1, 0], 0x00, 0x49)));

        // SCFS sets point 2's y to 300.
        (_, Zone z) = Move(Code([2, 300], 0x00, 0x48));
        Assert.Equal(300, z.CurY[2]);
    }

    [Fact]
    public void MeasureDistanceTakesTheGridFittedOutlineWithFlagZero()
    {
        // As egami-compare's probe-measure.ttf showed on GDI and GDI+: MD[0] (0x49) measures the moved outline, MD[1] (0x4A) the
        // original. Point 1 is at y 200 originally and moved to 300.
        foreach ((byte op, int expected) in new[] { ((byte)0x49, -300), ((byte)0x4A, -200) })
        {
            Zone z = Square();
            z.CurY[1] = 300;
            TrueTypeInterpreter t = Ready();
            Assert.True(t.RunForTest(Code([0, 1], 0x00, op), z, new GraphicsState()));
            Assert.Equal([expected], t.LastStack);
        }
    }

    [Fact]
    public void MeasureDistanceReadsP1FromZp1AndP2FromZp0()
    {
        // zp1 = twilight (point 1 at the origin after the reset), zp0 = glyph: glyph point 2 (x 200) less twilight point 1 (x 0).
        TrueTypeInterpreter t = Ready();
        Assert.Equal([200], Stack(Code([2, 1, 0], 0x14, 0x01, 0x49), t));

        // Out-of-range points measure 0 rather than failing.
        Assert.Equal([0], Stack(Code([2, 500], 0x01, 0x49), t));
    }

    [Fact]
    public void ArithmeticWrapsAtTheEdgesOfInt32()
    {
        // int.MinValue built as 2^14 × 2^14 × 2^14 × 2^7 in 26.6 MULs (2^31 wraps to int.MinValue); ABS of it wraps, no exception.
        byte[] min = [0xB8, 0x40, 0x00, 0xB8, 0x40, 0x00, 0x63, 0xB8, 0x40, 0x00, 0x63, 0xB0, 128, 0x63];
        Assert.Equal([int.MinValue], Stack([.. min, 0x64]));
        Assert.Equal([int.MinValue], Stack([.. min, 0x65]));

        // A control value of int.MinValue: MIAP's cut-in test measures in 64 bits rather than overflowing.
        TrueTypeInterpreter t = Ready();
        Assert.True(t.RunForTest([0xB0, 0, .. min, 0x44, 0xB1, 1, 0, 0x00, 0x3F], Square(), new GraphicsState()));

        // And MIRP's single-width test with extreme settings.
        Assert.True(Ready().RunForTest(Code([1, 0, 0], 0x00, 0x10, 0xE4), Square(), new GraphicsState { SingleWidth = int.MinValue, SingleWidthCutIn = int.MaxValue }));
    }

    [Fact]
    public void DeltaCountsCannotOutrunTheStack()
    {
        // A count larger than the stack holds pairs for fails at once, rather than looping 2^31 times.
        TrueTypeInterpreter t = Ready();
        Assert.False(t.RunForTest(Code([1, 2, 32767], 0x00, 0x5D), Square(), new GraphicsState()));
        Assert.False(t.RunForTest(Code([1, 2, -1], 0x00, 0x73), Square(), new GraphicsState()));
        Assert.True(t.RunForTest([0x5D], Square(), new GraphicsState())); // an empty stack: 0 pairs, nothing to do
    }

    [Fact]
    public void TwilightPointsAreSetUpByMiap()
    {
        // MIAP on twilight point 3 places it at cvt[2] = 200 along y, original and current.
        TrueTypeInterpreter t = Ready();
        Assert.True(t.RunForTest(Code([3, 2, 0], 0x13, 0x00, 0x3E), Square(), new GraphicsState()));
        Assert.Equal(200, t.Twilight.CurY[3]);
        Assert.Equal(200, t.Twilight.OrgY[3]);
    }

    [Fact]
    public void CompatibilityModeAFreezesXAndAllAfterIup()
    {
        // GDI+ target (mode A): SHPIX in x does nothing; an MDAP x move does nothing but touches.
        (_, Zone z) = Move(Code([2, 64], 0x01, 0x38), InterpreterTarget.GdiPlus);
        Assert.Equal(200, z.CurX[2]);
        Zone start = Square();
        start.CurX[2] = start.OrgX[2] = start.UnscaledX[2] = 220;
        TrueTypeInterpreter t = Ready(target: InterpreterTarget.GdiPlus);
        Assert.True(t.RunForTest(Code([2], 0x01, 0x2F), start, new GraphicsState()));
        Assert.Equal(220, start.CurX[2]);
        Assert.Equal(Zone.TouchX, start.Touched[2]);

        // After IUP[y] and IUP[x], a y move is ignored.
        (_, z) = Move(Code([1, 0], 0x30, 0x31, 0x00, 0x10, 0xB0, 0, 0x10, 0xB0, 1, 0xC4), InterpreterTarget.GdiPlus);
        Assert.Equal(200, z.CurY[1]);

        // Before IUP, a y move works.
        (_, z) = Move(Code([1, 0], 0x00, 0x10, 0xC4), InterpreterTarget.GdiPlus);
        Assert.Equal(192, z.CurY[1]);
    }

    [Fact]
    public void BrokenProgramsStop()
    {
        TrueTypeInterpreter t = Ready();
        Assert.False(t.RunForTest([0x4F], Square(), new GraphicsState())); // DEBUG
        Assert.False(t.RunForTest([0x8F], Square(), new GraphicsState())); // unknown opcode
        Assert.False(t.RunForTest(Code([500], 0x2F), Square(), new GraphicsState())); // point out of range
        Assert.False(t.RunForTest(Code([3], 0x13), Square(), new GraphicsState())); // zone 3
        Assert.False(t.RunForTest([0xB0, 0, 0x58], Square(), new GraphicsState())); // IF without EIF
        Assert.False(t.RunForTest([.. Enumerable.Repeat((byte)0x24, 300)], Square(), new GraphicsState())); // stack overflow
        Assert.Equal([0], Stack([0x20, 0x21])); // popping an empty stack gives zeros
    }

    [Fact]
    public void AFailingFontProgramDisablesHinting()
    {
        var t = new TrueTypeInterpreter(Data(fpgm: [0x4F]), InterpreterTarget.Monochrome);
        Assert.False(t.SetSize(16, Scale));
        Assert.False(t.SetSize(17, Scale));
        var ok = new TrueTypeInterpreter(Data(prep: [0x4F]), InterpreterTarget.Monochrome);
        Assert.False(ok.SetSize(16, Scale));
        Assert.False(ok.IsReady);
    }
}
