namespace Egami.Fonts.Hinting;

/// <summary>How the interpreter rounds distances: the round-state instructions and SROUND/S45ROUND.</summary>
internal enum RoundMode
{
    /// <summary>RTG.</summary>
    Grid,

    /// <summary>RTHG.</summary>
    HalfGrid,

    /// <summary>RTDG.</summary>
    DoubleGrid,

    /// <summary>RDTG.</summary>
    DownToGrid,

    /// <summary>RUTG.</summary>
    UpToGrid,

    /// <summary>ROFF.</summary>
    Off,

    /// <summary>SROUND.</summary>
    Super,

    /// <summary>S45ROUND.</summary>
    Super45,
}

/// <summary>The TrueType graphics state, with the defaults of the OpenType instruction set chapter.</summary>
internal sealed class GraphicsState
{
    public int ProjX { get; set; } = 0x4000;

    public int ProjY { get; set; }

    public int FreeX { get; set; } = 0x4000;

    public int FreeY { get; set; }

    public int DualX { get; set; } = 0x4000;

    public int DualY { get; set; }

    public int Rp0 { get; set; }

    public int Rp1 { get; set; }

    public int Rp2 { get; set; }

    public int Zp0 { get; set; } = 1;

    public int Zp1 { get; set; } = 1;

    public int Zp2 { get; set; } = 1;

    public int Loop { get; set; } = 1;

    public RoundMode Round { get; set; }

    /// <summary>Gets or sets the super-rounding period in 26.6.</summary>
    public int Period { get; set; } = 64;

    /// <summary>Gets or sets the super-rounding phase in 26.6.</summary>
    public int Phase { get; set; }

    /// <summary>Gets or sets the super-rounding threshold in 26.6.</summary>
    public int Threshold { get; set; } = 32;

    public int MinDistance { get; set; } = 64;

    /// <summary>Gets or sets the control value cut-in: 17/16 pixel by default.</summary>
    public int CvtCutIn { get; set; } = 68;

    public int SingleWidthCutIn { get; set; }

    public int SingleWidth { get; set; }

    public int DeltaBase { get; set; } = 9;

    public int DeltaShift { get; set; } = 3;

    public bool AutoFlip { get; set; } = true;

    public int InstructControl { get; set; }

    public int ScanControl { get; set; }

    public int ScanType { get; set; }

    public GraphicsState Clone() => (GraphicsState)MemberwiseClone();

    /// <summary>
    /// The state a glyph program starts with: the defaults, plus what carries over from the control value program (minimum
    /// distance, cut-ins, single width, delta base and shift, auto-flip, instruction control and scan control).
    /// </summary>
    public GraphicsState ForGlyph()
    {
        return new GraphicsState
        {
            MinDistance = MinDistance,
            CvtCutIn = CvtCutIn,
            SingleWidthCutIn = SingleWidthCutIn,
            SingleWidth = SingleWidth,
            DeltaBase = DeltaBase,
            DeltaShift = DeltaShift,
            AutoFlip = AutoFlip,
            InstructControl = InstructControl,
            ScanControl = ScanControl,
            ScanType = ScanType,
        };
    }
}
