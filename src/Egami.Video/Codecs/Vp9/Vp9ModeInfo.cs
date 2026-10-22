using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// What one block of a VP9 frame codes (libvpx's MODE_INFO): its size, modes, references, motion vectors and transform size.
/// A motion vector is packed as libvpx's int_mv: row in the low 16 bits, column in the high 16, so equal vectors compare equal as
/// integers. Blocks below 8x8 keep a mode and vectors per 4x4 in <see cref="Bmi"/>.
/// </summary>
internal struct Vp9ModeInfo
{
    public byte SbType;
    public byte Mode;
    public byte UVMode;
    public byte TxSize;
    public byte Skip;
    public byte SegmentId;
    public byte SegIdPredicted;
    public byte InterpFilter;
    public sbyte Ref0;
    public sbyte Ref1;
    public int Mv0;
    public int Mv1;
    public Vp9SubBlocks Bmi;

    public readonly bool IsInter => Ref0 > Vp9Tables.IntraFrame;

    public readonly bool HasSecondRef => Ref1 > Vp9Tables.IntraFrame;

    public readonly int Mv(int which) => which == 0 ? Mv0 : Mv1;

    public readonly sbyte Ref(int which) => which == 0 ? Ref0 : Ref1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pack(int row, int col) => (ushort)row | (col << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Row(int mv) => (short)mv;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Col(int mv) => mv >> 16;
}

/// <summary>The four 4x4 sub-blocks of a block below 8x8: a mode and two motion vectors each.</summary>
internal struct Vp9SubBlocks
{
    public Vp9SubBlock B0;
    public Vp9SubBlock B1;
    public Vp9SubBlock B2;
    public Vp9SubBlock B3;

    [UnscopedRef]
    public ref Vp9SubBlock this[int i]
    {
        get
        {
            switch (i)
            {
                case 0: return ref B0;
                case 1: return ref B1;
                case 2: return ref B2;
                default: return ref B3;
            }
        }
    }
}

internal struct Vp9SubBlock
{
    public byte Mode;
    public int Mv0;
    public int Mv1;

    public readonly int Mv(int which) => which == 0 ? Mv0 : Mv1;

    public void SetMv(int which, int mv)
    {
        if (which == 0)
        {
            Mv0 = mv;
        }
        else
        {
            Mv1 = mv;
        }
    }
}

/// <summary>A block's references and vectors as later frames read them (libvpx's MV_REF), kept per 8x8 of the previous frame.</summary>
internal struct Vp9MvRef
{
    public sbyte Ref0;
    public sbyte Ref1;
    public int Mv0;
    public int Mv1;
}
