using System.Runtime.CompilerServices;

using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// Where the encoder's syntax elements go, bin by bin: to the CABAC encoder for the final bitstream, or, for the search, only
/// counted (in 1/32768 bit, <see cref="CabacCost"/>) with the contexts moved on as the encoder would move them.
/// </summary>
internal sealed class HevcBins
{
    private CabacEncoder? _encoder;

    /// <summary>Gets the context variables, indexed as <see cref="HevcContexts"/>.</summary>
    internal byte[] Contexts { get; } = new byte[HevcContexts.Count];

    /// <summary>Gets or sets the cost counted so far, when counting.</summary>
    internal long Cost { get; set; }

    /// <summary>Gets whether bins go to an encoder.</summary>
    internal bool Encoding => _encoder is not null;

    /// <summary>Sends bins to <paramref name="encoder"/>, or counts them when it is null.</summary>
    internal void Target(CabacEncoder? encoder) => _encoder = encoder;

    /// <summary>Codes a context-coded bin.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Encode(int context, int bin)
    {
        if (_encoder is null)
        {
            Cost += CabacCost.Update(ref Contexts[context], bin);
        }
        else
        {
            _encoder.Encode(ref Contexts[context], bin);
        }
    }

    /// <summary>Codes a bypass bin.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Bypass(int bin)
    {
        if (_encoder is null)
        {
            Cost += CabacCost.Bit;
        }
        else
        {
            _encoder.Bypass(bin);
        }
    }

    /// <summary>Codes the low <paramref name="count"/> bits of <paramref name="bins"/> as bypass bins, most significant first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Bypass(uint bins, int count)
    {
        if (_encoder is null)
        {
            Cost += (long)CabacCost.Bit * count;
        }
        else if (count > 0)
        {
            _encoder.Bypass(bins, count);
        }
    }

    /// <summary>Codes a terminating bin; a 0 costs next to nothing.</summary>
    internal void Terminate(int bin) => _encoder?.Terminate(bin);
}
