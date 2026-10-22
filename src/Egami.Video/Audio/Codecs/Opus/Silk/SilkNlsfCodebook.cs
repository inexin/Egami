// Translated from libopus 1.5.2 silk/structs.h (silk_NLSF_CB_struct), tables_NLSF_CB_NB_MB.c and tables_NLSF_CB_WB.c.
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>One of SILK's two NLSF codebooks: narrow/medium band (order 10) or wide band (order 16).</summary>
internal sealed class SilkNlsfCodebook
{
    private readonly bool _wideband;

    private SilkNlsfCodebook(bool wideband, int quantStepSizeQ16, int invQuantStepSizeQ6)
    {
        _wideband = wideband;
        NVectors = 32;
        Order = wideband ? 16 : 10;
        QuantStepSizeQ16 = quantStepSizeQ16;
        InvQuantStepSizeQ6 = invQuantStepSizeQ6;
    }

    /// <summary>silk_NLSF_CB_NB_MB: SILK_FIX_CONST( 0.18, 16 ) and SILK_FIX_CONST( 1.0 / 0.18, 6 ).</summary>
    public static SilkNlsfCodebook NarrowMedium { get; } = new(false, 11796, 356);

    /// <summary>silk_NLSF_CB_WB: SILK_FIX_CONST( 0.15, 16 ) and SILK_FIX_CONST( 1.0 / 0.15, 6 ).</summary>
    public static SilkNlsfCodebook Wide { get; } = new(true, 9830, 427);

    public int NVectors { get; }

    public int Order { get; }

    public int QuantStepSizeQ16 { get; }

    public int InvQuantStepSizeQ6 { get; }

    public ReadOnlySpan<byte> Cb1NlsfQ8 => _wideband ? SilkTables.NlsfCb1WbQ8 : SilkTables.NlsfCb1NbMbQ8;

    public ReadOnlySpan<short> Cb1WghtQ9 => _wideband ? SilkTables.NlsfCb1WbWghtQ9 : SilkTables.NlsfCb1NbMbWghtQ9;

    public ReadOnlySpan<byte> Cb1Icdf => _wideband ? SilkTables.NlsfCb1WbIcdf : SilkTables.NlsfCb1NbMbIcdf;

    public ReadOnlySpan<byte> PredQ8 => _wideband ? SilkTables.NlsfPredWbQ8 : SilkTables.NlsfPredNbMbQ8;

    public ReadOnlySpan<byte> EcSel => _wideband ? SilkTables.NlsfCb2WbSelect : SilkTables.NlsfCb2NbMbSelect;

    public ReadOnlySpan<byte> EcIcdf => _wideband ? SilkTables.NlsfCb2WbIcdf : SilkTables.NlsfCb2NbMbIcdf;

    public ReadOnlySpan<short> DeltaMinQ15 => _wideband ? SilkTables.NlsfDeltaMinWbQ15 : SilkTables.NlsfDeltaMinNbMbQ15;
}
