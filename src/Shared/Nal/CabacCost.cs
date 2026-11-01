namespace Egami.Nal;

/// <summary>
/// The state transitions of a CABAC context (H.264 Table 9-45, H.265 Table 9-53) for both symbols, and the cost of coding a bin
/// in each state, in 1/32768 of a bit, for an encoder's rate estimates. The probability of the less probable symbol in state
/// pStateIdx is 0.5 alpha^pStateIdx with alpha = (0.01875 / 0.5)^(1 / 63) (H.264 9.3.1.1); a bin costs -log2 of its probability.
/// </summary>
internal static class CabacCost
{
    /// <summary>The fractional bits of a cost.</summary>
    internal const int Shift = 15;

    /// <summary>The cost of one bit (a bypass bin).</summary>
    internal const int Bit = 1 << Shift;

    /// <summary>The next state after the more probable symbol, by state (pStateIdx &lt;&lt; 1) | valMps.</summary>
    internal static readonly byte[] NextMps = new byte[128];

    /// <summary>The next state after the less probable symbol, by state.</summary>
    internal static readonly byte[] NextLps = new byte[128];

    /// <summary>The cost of a bin by state ^ bin: even entries are the more probable symbol's, odd ones the less probable one's.</summary>
    internal static readonly int[] Costs = new int[128];

    static CabacCost()
    {
        double alpha = Math.Pow(0.01875 / 0.5, 1.0 / 63);
        for (int s = 0; s < 128; s++)
        {
            int p = s >> 1, mps = s & 1;
            NextMps[s] = (byte)((Math.Min(p + 1, 62) << 1) | mps);
            NextLps[s] = (byte)((CabacEngine.TransitionLps[p] << 1) | (p == 0 ? mps ^ 1 : mps));
            double lps = 0.5 * Math.Pow(alpha, Math.Min(p, 62));
            Costs[(p << 1) | 0] = (int)Math.Round(-Math.Log2(1 - lps) * Bit);
            Costs[(p << 1) | 1] = (int)Math.Round(-Math.Log2(lps) * Bit);
        }
    }

    /// <summary>Gets the cost of coding <paramref name="bin"/> in <paramref name="state"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Of(byte state, int bin) => Costs[state ^ bin];

    /// <summary>Gets the cost of coding <paramref name="bin"/> and moves the state on, as the encoder would.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Update(ref byte state, int bin)
    {
        int cost = Costs[state ^ bin];
        state = bin == (state & 1) ? NextMps[state] : NextLps[state];
        return cost;
    }
}
