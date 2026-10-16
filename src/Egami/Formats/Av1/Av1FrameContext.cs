namespace Egami.Formats.Av1;

/// <summary>
/// A frame's adaptive CDFs (libaom's FRAME_CONTEXT, the specification's saved CDF arrays), held in one flat array so a tile's
/// copy is a single array copy. Each table's CDFs sit at a fixed stride from the table's offset; a CDF of N symbols holds N - 1
/// inverse cumulative probabilities, then 0, then its adaptation counter. The coefficient tables hold the set the frame's base_q_idx
/// selects.
/// </summary>
internal sealed class Av1FrameContext
{
    private static readonly List<(int Offset, int Count)> AllCdfs = [];
    private static readonly List<Action<ushort[], int>> Fillers = [];
    private static int s_size;

    internal static readonly Table KfYMode = Add(Av1DefaultCdfs.KfYMode);
    internal static readonly Table AngleDelta = Add(Av1DefaultCdfs.AngleDelta);
    internal static readonly Table IfYMode = Add(Av1DefaultCdfs.IfYMode);
    internal static readonly Table UvMode = Add(Av1DefaultCdfs.UvMode);
    internal static readonly Table Partition = Add(Av1DefaultCdfs.Partition);
    internal static readonly Table IntraExtTx = Add(Av1DefaultCdfs.IntraExtTx);
    internal static readonly Table InterExtTx = Add(Av1DefaultCdfs.InterExtTx);
    internal static readonly Table CflSign = Add(Av1DefaultCdfs.CflSign);
    internal static readonly Table CflAlpha = Add(Av1DefaultCdfs.CflAlpha);
    internal static readonly Table SwitchableInterp = Add(Av1DefaultCdfs.SwitchableInterp);
    internal static readonly Table Newmv = Add(Av1DefaultCdfs.Newmv);
    internal static readonly Table Zeromv = Add(Av1DefaultCdfs.Zeromv);
    internal static readonly Table Refmv = Add(Av1DefaultCdfs.Refmv);
    internal static readonly Table Drl = Add(Av1DefaultCdfs.Drl);
    internal static readonly Table InterCompoundMode = Add(Av1DefaultCdfs.InterCompoundMode);
    internal static readonly Table Interintra = Add(Av1DefaultCdfs.Interintra);
    internal static readonly Table InterintraMode = Add(Av1DefaultCdfs.InterintraMode);
    internal static readonly Table WedgeInterintra = Add(Av1DefaultCdfs.WedgeInterintra);
    internal static readonly Table CompoundType = Add(Av1DefaultCdfs.CompoundType);
    internal static readonly Table WedgeIdx = Add(Av1DefaultCdfs.WedgeIdx);
    internal static readonly Table MotionMode = Add(Av1DefaultCdfs.MotionMode);
    internal static readonly Table Obmc = Add(Av1DefaultCdfs.Obmc);
    internal static readonly Table IntraInter = Add(Av1DefaultCdfs.IntraInter);
    internal static readonly Table CompInter = Add(Av1DefaultCdfs.CompInter);
    internal static readonly Table CompRefType = Add(Av1DefaultCdfs.CompRefType);
    internal static readonly Table UniCompRef = Add(Av1DefaultCdfs.UniCompRef);
    internal static readonly Table SingleRef = Add(Av1DefaultCdfs.SingleRef);
    internal static readonly Table CompRef = Add(Av1DefaultCdfs.CompRef);
    internal static readonly Table CompBwdref = Add(Av1DefaultCdfs.CompBwdref);
    internal static readonly Table PaletteYSize = Add(Av1DefaultCdfs.PaletteYSize);
    internal static readonly Table PaletteUvSize = Add(Av1DefaultCdfs.PaletteUvSize);
    internal static readonly Table PaletteYMode = Add(Av1DefaultCdfs.PaletteYMode);
    internal static readonly Table PaletteUvMode = Add(Av1DefaultCdfs.PaletteUvMode);
    internal static readonly Table PaletteYColorIndex = Add(Av1DefaultCdfs.PaletteYColorIndex);
    internal static readonly Table PaletteUvColorIndex = Add(Av1DefaultCdfs.PaletteUvColorIndex);
    internal static readonly Table TxfmPartition = Add(Av1DefaultCdfs.TxfmPartition);
    internal static readonly Table SkipTxfm = Add(Av1DefaultCdfs.SkipTxfm);
    internal static readonly Table SkipMode = Add(Av1DefaultCdfs.SkipMode);
    internal static readonly Table CompoundIdx = Add(Av1DefaultCdfs.CompoundIdx);
    internal static readonly Table CompGroupIdx = Add(Av1DefaultCdfs.CompGroupIdx);
    internal static readonly Table Intrabc = Add(Av1DefaultCdfs.Intrabc);
    internal static readonly Table FilterIntraMode = Add(Av1DefaultCdfs.FilterIntraMode);
    internal static readonly Table FilterIntra = Add(Av1DefaultCdfs.FilterIntra);
    internal static readonly Table SwitchableRestore = Add(Av1DefaultCdfs.SwitchableRestore);
    internal static readonly Table WienerRestore = Add(Av1DefaultCdfs.WienerRestore);
    internal static readonly Table SgrprojRestore = Add(Av1DefaultCdfs.SgrprojRestore);
    internal static readonly Table DeltaQ = Add(Av1DefaultCdfs.DeltaQ);
    internal static readonly Table DeltaLfMulti = Add(Av1DefaultCdfs.DeltaLfMulti);
    internal static readonly Table DeltaLf = Add(Av1DefaultCdfs.DeltaLf);
    internal static readonly Table SegmentPred = Add(Av1DefaultCdfs.SegmentPred);
    internal static readonly Table SpatialPredSegTree = Add(Av1DefaultCdfs.SpatialPredSegTree);
    internal static readonly Table TxSize = Add(Av1DefaultCdfs.TxSize);

    /// <summary>[plane type][context].</summary>
    internal static readonly Table DcSign = AddPerQ(Av1DefaultCdfs.DcSign);

    /// <summary>[square transform size][context].</summary>
    internal static readonly Table TxbSkip = AddPerQ(Av1DefaultCdfs.TxbSkip);

    /// <summary>[square transform size][plane type][context].</summary>
    internal static readonly Table EobExtra = AddPerQ(Av1DefaultCdfs.EobExtra);

    /// <summary>[plane type][1D or 2D], for 16 coefficients and up to 1024.</summary>
    internal static readonly Table[] EobMulti =
    [
        AddPerQ(Av1DefaultCdfs.EobMulti16), AddPerQ(Av1DefaultCdfs.EobMulti32), AddPerQ(Av1DefaultCdfs.EobMulti64),
        AddPerQ(Av1DefaultCdfs.EobMulti128), AddPerQ(Av1DefaultCdfs.EobMulti256), AddPerQ(Av1DefaultCdfs.EobMulti512),
        AddPerQ(Av1DefaultCdfs.EobMulti1024),
    ];

    /// <summary>[square transform size][plane type][context].</summary>
    internal static readonly Table CoeffBr = AddPerQ(Av1DefaultCdfs.CoeffLpsMulti);

    /// <summary>[square transform size][plane type][context].</summary>
    internal static readonly Table CoeffBase = AddPerQ(Av1DefaultCdfs.CoeffBaseMulti);

    /// <summary>[square transform size][plane type][context].</summary>
    internal static readonly Table CoeffBaseEob = AddPerQ(Av1DefaultCdfs.CoeffBaseEobMulti);

    /// <summary>The motion vector CDFs of intra block copy (ndvc).</summary>
    internal static readonly MvTables Dv = AddMv();

    /// <summary>The motion vector CDFs of inter prediction (nmvc).</summary>
    internal static readonly MvTables Mv = AddMv();

    private static readonly int Size = s_size;
    private static readonly ushort[]?[] Defaults = new ushort[4][];

    private Av1FrameContext(ushort[] cdf) => Cdf = cdf;

    /// <summary>Gets the CDFs.</summary>
    internal ushort[] Cdf { get; }

    /// <summary>Creates the default CDFs for a frame of a base_q_idx (setup_past_independence).</summary>
    internal static Av1FrameContext CreateDefault(int baseQIdx)
    {
        int q = baseQIdx <= 20 ? 0 : baseQIdx <= 60 ? 1 : baseQIdx <= 120 ? 2 : 3;
        ushort[]? defaults = Volatile.Read(ref Defaults[q]);
        if (defaults is null)
        {
            defaults = new ushort[Size];
            foreach (Action<ushort[], int> fill in Fillers)
            {
                fill(defaults, q);
            }

            Volatile.Write(ref Defaults[q], defaults);
        }

        return new Av1FrameContext((ushort[])defaults.Clone());
    }

    /// <summary>Copies the CDFs, as a tile starts from the frame's.</summary>
    internal Av1FrameContext Clone() => new((ushort[])Cdf.Clone());

    /// <summary>Clears every adaptation counter, as the CDFs a frame saves are (av1_reset_cdf_symbol_counters).</summary>
    internal void ResetCounters()
    {
        foreach ((int offset, int count) in AllCdfs)
        {
            Cdf[offset + count] = 0;
        }
    }

    private static Table Add(ushort[][] table) => Add(table, table.Length, 0);

    private static Table AddPerQ(ushort[][] table) => Add(table, table.Length / 4, table.Length / 4);

    private static Table Add(ushort[][] table, int count, int qStride)
    {
        int stride = 0;
        foreach (ushort[] cdf in table)
        {
            stride = Math.Max(stride, cdf.Length);
        }

        int offset = s_size;
        for (int i = 0; i < count; i++)
        {
            AllCdfs.Add((offset + (i * stride), table[i].Length - 2));
        }

        Fillers.Add((buffer, q) =>
        {
            for (int i = 0; i < count; i++)
            {
                table[(q * qStride) + i].CopyTo(buffer, offset + (i * stride));
            }
        });
        s_size += count * stride;
        return new Table(offset, stride);
    }

    private static MvTables AddMv()
    {
        Table joints = Add([Icdf(4096, 11264, 19328)]);
        Table classes = Add([Icdf(28672, 30976, 31858, 32320, 32551, 32656, 32740, 32757, 32762, 32767), Icdf(28672, 30976, 31858, 32320, 32551, 32656, 32740, 32757, 32762, 32767)]);
        Table class0Fp = Add([Icdf(16384, 24576, 26624), Icdf(12288, 21248, 24128), Icdf(16384, 24576, 26624), Icdf(12288, 21248, 24128)]);
        Table fp = Add([Icdf(8192, 17408, 21248), Icdf(8192, 17408, 21248)]);
        Table sign = Add([Icdf(128 * 128), Icdf(128 * 128)]);
        Table class0Hp = Add([Icdf(160 * 128), Icdf(160 * 128)]);
        Table hp = Add([Icdf(128 * 128), Icdf(128 * 128)]);
        Table class0 = Add([Icdf(216 * 128), Icdf(216 * 128)]);
        int[] bitProbs = [136, 140, 148, 160, 176, 192, 224, 234, 234, 240];
        ushort[][] bits = new ushort[20][];
        for (int i = 0; i < 20; i++)
        {
            bits[i] = Icdf(128 * bitProbs[i % 10]);
        }

        return new MvTables(joints, classes, class0Fp, fp, sign, class0Hp, hp, class0, Add(bits));

        static ushort[] Icdf(params int[] cumulative)
        {
            ushort[] cdf = new ushort[cumulative.Length + 2];
            for (int i = 0; i < cumulative.Length; i++)
            {
                cdf[i] = (ushort)(32768 - cumulative[i]);
            }

            return cdf;
        }
    }

    /// <summary>Where a table's CDFs sit: CDF i is at <see cref="Offset"/> + i * <see cref="Stride"/>.</summary>
    internal readonly record struct Table(int Offset, int Stride)
    {
        /// <summary>Gets the offset of CDF <paramref name="index"/>.</summary>
        internal int this[int index] => Offset + (index * Stride);
    }

    /// <summary>The CDFs of a motion vector context (nmv_context); the per-component tables are indexed by component first.</summary>
    internal readonly record struct MvTables(Table Joints, Table Classes, Table Class0Fp, Table Fp, Table Sign, Table Class0Hp, Table Hp, Table Class0, Table Bits);
}
