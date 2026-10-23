// Translated from libopus 1.5.2 silk/SigProc_FIX.h, silk/macros.h and silk/Inlines.h (the OPUS_FAST_INT64 forms).
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's fixed-point macros. Arithmetic wraps on overflow like the C on two's complement machines.</summary>
internal static class SilkMath
{
    public const int RandMultiplier = 196314165;
    public const int RandIncrement = 907633515;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulwb(int a32, int b32) => unchecked((int)((a32 * (long)(short)b32) >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlawb(int a32, int b32, int c32) => unchecked((int)(a32 + ((b32 * (long)(short)c32) >> 16)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulwt(int a32, int b32) => unchecked((int)((a32 * (long)(b32 >> 16)) >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlawt(int a32, int b32, int c32) => unchecked((int)(a32 + ((b32 * (long)(c32 >> 16)) >> 16)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulbb(int a32, int b32) => unchecked((short)a32 * (short)b32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlabb(int a32, int b32, int c32) => unchecked(a32 + ((short)b32 * (short)c32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SmlabbOvflw(int a32, int b32, int c32) => unchecked(a32 + ((short)b32 * (short)c32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulbt(int a32, int b32) => unchecked((short)a32 * (b32 >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smultt(int a32, int b32) => unchecked((a32 >> 16) * (b32 >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulww(int a32, int b32) => unchecked((int)(((long)a32 * b32) >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlaww(int a32, int b32, int c32) => unchecked((int)(a32 + (((long)b32 * c32) >> 16)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Smull(int a32, int b32) => (long)a32 * b32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smmul(int a32, int b32) => unchecked((int)(((long)a32 * b32) >> 32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mul(int a32, int b32) => unchecked(a32 * b32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mla(int a32, int b32, int c32) => unchecked(a32 + (b32 * c32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddOvflw(int a, int b) => unchecked((int)((uint)a + (uint)b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SubOvflw(int a, int b) => unchecked((int)((uint)a - (uint)b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MlaOvflw(int a32, int b32, int c32) => unchecked((int)((uint)a32 + ((uint)b32 * (uint)c32)));

    /// <summary>silk_RAND: the linear congruential generator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Rand(int seed) => MlaOvflw(RandIncrement, seed, RandMultiplier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sat16(int a) => a > short.MaxValue ? short.MaxValue : (a < short.MinValue ? short.MinValue : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddSat16(int a, int b) => (short)Sat16(unchecked(a + b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddSat32(int a, int b)
    {
        int sum = unchecked(a + b);
        if ((sum & int.MinValue) == 0)
        {
            return (a & b & int.MinValue) != 0 ? int.MinValue : sum;
        }

        return ((a | b) & int.MinValue) == 0 ? int.MaxValue : sum;
    }

    /// <summary>silk_ADD_POS_SAT32: adds two non-negative values, saturating to int.MaxValue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddPosSat32(int a, int b)
    {
        int sum = unchecked(a + b);
        return (sum & int.MinValue) != 0 ? int.MaxValue : sum;
    }

    /// <summary>silk_sigm_Q15: an approximate sigmoid in Q15 of a Q5 input.</summary>
    public static int SigmQ15(int inQ5)
    {
        ReadOnlySpan<int> slopeQ10 = [237, 153, 73, 30, 12, 7];
        ReadOnlySpan<int> posQ15 = [16384, 23955, 28861, 31213, 32178, 32548];
        ReadOnlySpan<int> negQ15 = [16384, 8812, 3906, 1554, 589, 219];
        if (inQ5 < 0)
        {
            inQ5 = -inQ5;
            if (inQ5 >= 6 * 32)
            {
                return 0;
            }

            int ind = inQ5 >> 5;
            return negQ15[ind] - Smulbb(slopeQ10[ind], inQ5 & 0x1F);
        }
        else
        {
            if (inQ5 >= 6 * 32)
            {
                return 32767;
            }

            int ind = inQ5 >> 5;
            return posQ15[ind] + Smulbb(slopeQ10[ind], inQ5 & 0x1F);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SubSat32(int a, int b)
    {
        int diff = unchecked(a - b);
        if ((diff & int.MinValue) == 0)
        {
            return (a & (b ^ int.MinValue) & int.MinValue) != 0 ? int.MinValue : diff;
        }

        return ((a ^ int.MinValue) & b & int.MinValue) != 0 ? int.MaxValue : diff;
    }

    /// <summary>silk_LSHIFT / silk_LSHIFT32: shifts through unsigned, so it wraps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LShift(int a, int shift) => unchecked((int)((uint)a << shift));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Limit(int a, int limit1, int limit2) => limit1 > limit2
        ? (a > limit1 ? limit1 : (a < limit2 ? limit2 : a))
        : (a > limit2 ? limit2 : (a < limit1 ? limit1 : a));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LShiftSat32(int a, int shift) => LShift(Limit(a, int.MinValue >> shift, int.MaxValue >> shift), shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RShiftRound(int a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RShiftRound64(long a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddLShift32(int a, int b, int shift) => unchecked(a + LShift(b, shift));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Abs(int a) => a > 0 ? a : unchecked(-a);

    /// <summary>silk_CLZ32: leading zeros; 32 for zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clz32(int in32) => BitOperations.LeadingZeroCount(unchecked((uint)in32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clz16(short in16) => BitOperations.LeadingZeroCount(unchecked((uint)((in16 << 16) | 0x8000)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ror32(int a32, int rot)
    {
        uint x = unchecked((uint)a32);
        uint r = unchecked((uint)rot);
        uint m = unchecked((uint)-rot);
        if (rot == 0)
        {
            return a32;
        }

        if (rot < 0)
        {
            return unchecked((int)((x << (int)m) | (x >> (int)(32 - m))));
        }

        return unchecked((int)((x << (int)(32 - r)) | (x >> (int)r)));
    }

    /// <summary>silk_CLZ_FRAC: leading zeros and the 7 bits after the leading one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClzFrac(int value, out int lz, out int fracQ7)
    {
        int lzeros = Clz32(value);
        lz = lzeros;
        fracQ7 = Ror32(value, 24 - lzeros) & 0x7f;
    }

    /// <summary>silk_SQRT_APPROX.</summary>
    public static int SqrtApprox(int x)
    {
        if (x <= 0)
        {
            return 0;
        }

        ClzFrac(x, out int lz, out int fracQ7);
        int y = (lz & 1) != 0 ? 32768 : 46214;
        y >>= lz >> 1;
        return Smlawb(y, y, Smulbb(213, fracQ7));
    }

    /// <summary>silk_DIV32_varQ: about (a32 &lt;&lt; qres) / b32.</summary>
    public static int Div32VarQ(int a32, int b32, int qres)
    {
        unchecked
        {
            int aHeadrm = Clz32(Abs(a32)) - 1;
            int a32Nrm = LShift(a32, aHeadrm);
            int bHeadrm = Clz32(Abs(b32)) - 1;
            int b32Nrm = LShift(b32, bHeadrm);
            int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
            int result = Smulwb(a32Nrm, b32Inv);
            a32Nrm = SubOvflw(a32Nrm, LShift(Smmul(b32Nrm, result), 3));
            result = Smlawb(result, a32Nrm, b32Inv);
            int lshift = 29 + aHeadrm - bHeadrm - qres;
            if (lshift < 0)
            {
                return LShiftSat32(result, -lshift);
            }

            return lshift < 32 ? result >> lshift : 0;
        }
    }

    /// <summary>silk_INVERSE32_varQ: about (1 &lt;&lt; qres) / b32.</summary>
    public static int Inverse32VarQ(int b32, int qres)
    {
        unchecked
        {
            int bHeadrm = Clz32(Abs(b32)) - 1;
            int b32Nrm = LShift(b32, bHeadrm);
            int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
            int result = LShift(b32Inv, 16);
            int errQ32 = LShift((1 << 29) - Smulwb(b32Nrm, b32Inv), 3);
            result = Smlaww(result, errQ32, b32Inv);
            int lshift = 61 - bHeadrm - qres;
            if (lshift <= 0)
            {
                return LShiftSat32(result, -lshift);
            }

            return lshift < 32 ? result >> lshift : 0;
        }
    }
}
