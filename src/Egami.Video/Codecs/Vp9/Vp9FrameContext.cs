namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// A VP9 probability context: every probability the decoder adapts, as one 2,039-byte array laid out as libvpx's FRAME_CONTEXT, so
/// saving and loading the four saved contexts is a copy and the bytes compare with libvpx's directly. The offsets below name its
/// parts; multi-dimensional parts are flattened in C order.
/// </summary>
internal sealed class Vp9FrameContext
{
    internal const int YMode = 0;                       // [4 size groups][9]
    internal const int UVMode = YMode + (4 * 9);        // [10 y modes][9]
    internal const int Partition = UVMode + (10 * 9);   // [16 contexts][3]
    internal const int Coef = Partition + (16 * 3);     // [4 tx][2 planes][2 refs][6 bands][6 contexts][3]
    internal const int SwitchableInterp = Coef + (4 * 432); // [4 contexts][2]
    internal const int InterMode = SwitchableInterp + (4 * 2); // [7 contexts][3]
    internal const int IntraInter = InterMode + (7 * 3);    // [4]
    internal const int CompInter = IntraInter + 4;         // [5]
    internal const int SingleRef = CompInter + 5;          // [5][2]
    internal const int CompRef = SingleRef + (5 * 2);      // [5]
    internal const int Tx32x32 = CompRef + 5;              // [2][3]
    internal const int Tx16x16 = Tx32x32 + (2 * 3);        // [2][2]
    internal const int Tx8x8 = Tx16x16 + (2 * 2);          // [2][1]
    internal const int Skip = Tx8x8 + 2;                   // [3]
    internal const int MvJoints = Skip + 3;                // [3]
    internal const int MvComp = MvJoints + 3;              // [2 components][33]
    internal const int Size = MvComp + (2 * MvCompSize);

    // Offsets inside one motion vector component.
    internal const int MvSign = 0;
    internal const int MvClasses = 1;    // [10]
    internal const int MvClass0 = 11;    // [1]
    internal const int MvBits = 12;      // [10]
    internal const int MvClass0Fp = 22;  // [2][3]
    internal const int MvFp = 28;        // [3]
    internal const int MvClass0Hp = 31;
    internal const int MvHp = 32;
    internal const int MvCompSize = 33;

    /// <summary>Gets the probabilities.</summary>
    public byte[] P { get; } = new byte[Size];

    /// <summary>Gets the offset of the coefficient probabilities of one transform size, plane type and reference type.</summary>
    internal static int CoefOffset(int txSize, int planeType, int reference) => Coef + (txSize * 432) + (planeType * 216) + (reference * 108);

    /// <summary>Sets every probability to its default (vp9_default_coef_probs, init_mode_probs and vp9_init_mv_probs).</summary>
    public void SetDefaults()
    {
        Span<byte> p = P;
        Vp9Tables.DefaultIfYProbs.CopyTo(p[YMode..]);
        Vp9Tables.DefaultIfUVProbs.CopyTo(p[UVMode..]);
        Vp9Tables.DefaultPartitionProbs.CopyTo(p[Partition..]);
        Vp9Tables.DefaultCoefProbs4x4.CopyTo(p[(Coef + (0 * 432))..]);
        Vp9Tables.DefaultCoefProbs8x8.CopyTo(p[(Coef + (1 * 432))..]);
        Vp9Tables.DefaultCoefProbs16x16.CopyTo(p[(Coef + (2 * 432))..]);
        Vp9Tables.DefaultCoefProbs32x32.CopyTo(p[(Coef + (3 * 432))..]);
        Vp9Tables.DefaultSwitchableInterpProb.CopyTo(p[SwitchableInterp..]);
        Vp9Tables.DefaultInterModeProbs.CopyTo(p[InterMode..]);
        Vp9Tables.DefaultIntraInterP.CopyTo(p[IntraInter..]);
        Vp9Tables.DefaultCompInterP.CopyTo(p[CompInter..]);
        Vp9Tables.DefaultSingleRefP.CopyTo(p[SingleRef..]);
        Vp9Tables.DefaultCompRefP.CopyTo(p[CompRef..]);
        Vp9Tables.DefaultTxProbs.CopyTo(p[Tx32x32..]);
        Vp9Tables.DefaultSkipProbs.CopyTo(p[Skip..]);
        Vp9Tables.DefaultNmvContext.CopyTo(p[MvJoints..]);
    }

    public void CopyFrom(Vp9FrameContext other) => other.P.CopyTo(P, 0);
}

/// <summary>
/// The symbol counts a frame gathers while it is parsed, for backward adaptation (libvpx's FRAME_COUNTS), each part a flat array.
/// </summary>
internal sealed class Vp9Counts
{
    public int[] YMode { get; } = new int[4 * 10];

    public int[] UVMode { get; } = new int[10 * 10];

    public int[] Partition { get; } = new int[16 * 4];

    /// <summary>Gets the coefficient token counts: [tx][plane][ref][band][ctx][4] (zero, one, two or more, end of block).</summary>
    public int[] Coef { get; } = new int[4 * 2 * 2 * 6 * 6 * 4];

    /// <summary>Gets how often each coefficient position was checked for the end of block: [tx][plane][ref][band][ctx].</summary>
    public int[] EobBranch { get; } = new int[4 * 2 * 2 * 6 * 6];

    public int[] SwitchableInterp { get; } = new int[4 * 3];

    public int[] InterMode { get; } = new int[7 * 4];

    public int[] IntraInter { get; } = new int[4 * 2];

    public int[] CompInter { get; } = new int[5 * 2];

    public int[] SingleRef { get; } = new int[5 * 2 * 2];

    public int[] CompRef { get; } = new int[5 * 2];

    public int[] Tx32x32 { get; } = new int[2 * 4];

    public int[] Tx16x16 { get; } = new int[2 * 3];

    public int[] Tx8x8 { get; } = new int[2 * 2];

    public int[] Skip { get; } = new int[3 * 2];

    public int[] MvJoints { get; } = new int[4];

    /// <summary>Gets the motion vector component counts, per component: sign[2], classes[11], class0[2], bits[10][2], class0_fp[2][4], fp[4], class0_hp[2], hp[2].</summary>
    public int[] MvComp { get; } = new int[2 * MvCompSize];

    internal const int MvSign = 0;
    internal const int MvClasses = 2;
    internal const int MvClass0 = 13;
    internal const int MvBits = 15;
    internal const int MvClass0Fp = 35;
    internal const int MvFp = 43;
    internal const int MvClass0Hp = 47;
    internal const int MvHp = 49;
    internal const int MvCompSize = 51;

    public void Clear()
    {
        Array.Clear(YMode);
        Array.Clear(UVMode);
        Array.Clear(Partition);
        Array.Clear(Coef);
        Array.Clear(EobBranch);
        Array.Clear(SwitchableInterp);
        Array.Clear(InterMode);
        Array.Clear(IntraInter);
        Array.Clear(CompInter);
        Array.Clear(SingleRef);
        Array.Clear(CompRef);
        Array.Clear(Tx32x32);
        Array.Clear(Tx16x16);
        Array.Clear(Tx8x8);
        Array.Clear(Skip);
        Array.Clear(MvJoints);
        Array.Clear(MvComp);
    }

    /// <summary>Adds another tile's counts to these.</summary>
    public void Add(Vp9Counts other)
    {
        AddTo(YMode, other.YMode);
        AddTo(UVMode, other.UVMode);
        AddTo(Partition, other.Partition);
        AddTo(Coef, other.Coef);
        AddTo(EobBranch, other.EobBranch);
        AddTo(SwitchableInterp, other.SwitchableInterp);
        AddTo(InterMode, other.InterMode);
        AddTo(IntraInter, other.IntraInter);
        AddTo(CompInter, other.CompInter);
        AddTo(SingleRef, other.SingleRef);
        AddTo(CompRef, other.CompRef);
        AddTo(Tx32x32, other.Tx32x32);
        AddTo(Tx16x16, other.Tx16x16);
        AddTo(Tx8x8, other.Tx8x8);
        AddTo(Skip, other.Skip);
        AddTo(MvJoints, other.MvJoints);
        AddTo(MvComp, other.MvComp);
    }

    private static void AddTo(int[] to, int[] from)
    {
        for (int i = 0; i < to.Length; i++)
        {
            to[i] += from[i];
        }
    }
}
