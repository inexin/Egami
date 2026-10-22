// Translated from libopus 1.5.2 celt/modes.c, celt/static_modes_float.h (mode48000_960_120), celt/rate.h, celt/celt.c and celt/celt.h.
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>The static 48 kHz, 960-sample CELT mode of libopus and the lookups on its pulse cache.</summary>
internal static partial class CeltTables
{
    /// <summary>The MDCT overlap.</summary>
    public const int Overlap = 120;

    /// <summary>The number of energy bands.</summary>
    public const int NbEBands = 21;

    /// <summary>The number of bands below the Nyquist frequency.</summary>
    public const int EffEBands = 21;

    /// <summary>The largest LM (log2 of the number of short MDCTs).</summary>
    public const int MaxLM = 3;

    /// <summary>The size of a short MDCT.</summary>
    public const int ShortMdctSize = 120;

    /// <summary>The number of allocation vectors.</summary>
    public const int NbAllocVectors = 11;

    /// <summary>The first de-emphasis coefficient.</summary>
    public const float Preemph0 = 0.85000610f;

    /// <summary>The size of the largest MDCT (two frames).</summary>
    public const int MdctSize = 1920;

    /// <summary>BITRES: allocations are in eighth bits.</summary>
    public const int BitRes = 3;

    /// <summary>MAX_PERIOD.</summary>
    public const int MaxPeriod = 1024;

    /// <summary>COMBFILTER_MINPERIOD.</summary>
    public const int CombFilterMinPeriod = 15;

    /// <summary>MAX_FINE_BITS.</summary>
    public const int MaxFineBits = 8;

    /// <summary>FINE_OFFSET.</summary>
    public const int FineOffset = 21;

    /// <summary>QTHETA_OFFSET.</summary>
    public const int QThetaOffset = 4;

    /// <summary>QTHETA_OFFSET_TWOPHASE.</summary>
    public const int QThetaOffsetTwoPhase = 16;

    /// <summary>The spread decisions of bands.h.</summary>
    public const int SpreadNone = 0, SpreadLight = 1, SpreadNormal = 2, SpreadAggressive = 3;

    /// <summary>The band edges (eband5ms) in units of the short MDCT bin.</summary>
    public static ReadOnlySpan<short> EBands => EBand5ms;

    /// <summary>trim_icdf of celt.h.</summary>
    public static ReadOnlySpan<byte> TrimIcdf => [126, 124, 119, 109, 87, 41, 19, 9, 4, 2, 0];

    /// <summary>spread_icdf of celt.h.</summary>
    public static ReadOnlySpan<byte> SpreadIcdf => [25, 23, 2, 0];

    /// <summary>tapset_icdf of celt.h.</summary>
    public static ReadOnlySpan<byte> TapsetIcdf => [2, 1, 0];

    /// <summary>tf_select_table[4][8] of celt.c, indexed by 4 isTransient + 2 tf_select + per-band flag.</summary>
    public static ReadOnlySpan<sbyte> TfSelectTable =>
    [
        0, -1, 0, -1, 0, -1, 0, -1,
        0, -1, 0, -2, 1, 0, 1, -1,
        0, -2, 0, -3, 2, 0, 1, -1,
        0, -2, 0, -3, 3, 0, 1, -1,
    ];

    /// <summary>get_pulses.</summary>
    public static int GetPulses(int i) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

    /// <summary>The pulse cache row of <paramref name="band"/> for <paramref name="lm"/> (which may be -1).</summary>
    public static ReadOnlySpan<byte> Cache(int band, int lm) => CacheBits50[CacheIndex50[((lm + 1) * NbEBands) + band]..];

    /// <summary>bits2pulses.</summary>
    public static int Bits2Pulses(int band, int lm, int bits)
    {
        ReadOnlySpan<byte> cache = Cache(band, lm);
        int lo = 0;
        int hi = cache[0];
        bits--;
        for (int i = 0; i < 6; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cache[mid] >= bits)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return bits - (lo == 0 ? -1 : cache[lo]) <= cache[hi] - bits ? lo : hi;
    }

    /// <summary>pulses2bits.</summary>
    public static int Pulses2Bits(int band, int lm, int pulses) => pulses == 0 ? 0 : Cache(band, lm)[pulses] + 1;

    /// <summary>init_caps.</summary>
    public static void InitCaps(Span<int> cap, int lm, int c)
    {
        ReadOnlySpan<short> eBands = EBands;
        ReadOnlySpan<byte> caps = CacheCaps50;
        for (int i = 0; i < NbEBands; i++)
        {
            int n = (eBands[i + 1] - eBands[i]) << lm;
            cap[i] = (caps[(NbEBands * ((2 * lm) + c - 1)) + i] + 64) * c * n >> 2;
        }
    }
}
