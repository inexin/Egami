namespace Egami.Formats.Av1;

/// <summary>
/// Where the tile syntax goes (<see cref="Av1TileCoder"/>): the symbols of <see cref="Av1SymbolEncoder"/>, against the tile's
/// CDFs. A struct, so the syntax is compiled once for each sink with no calls through the interface.
/// </summary>
internal interface IAv1SymbolSink
{
    /// <summary>A symbol against the adaptive CDF of <paramref name="n"/> symbols at <paramref name="at"/> in <paramref name="cdf"/>.</summary>
    void Symbol(ushort[] cdf, int at, int n, int value);

    /// <summary>A symbol against a CDF that does not adapt.</summary>
    void SymbolFixed(ReadOnlySpan<ushort> cdf, int n, int value);

    /// <summary>An equiprobable bit.</summary>
    void Bit(int value);

    /// <summary>An unsigned number in <paramref name="bits"/> equiprobable bits.</summary>
    void Literal(int value, int bits);

    /// <summary>A number below <paramref name="n"/> in as few equiprobable bits as needed.</summary>
    void Uniform(int n, int value);

    /// <summary>An Exp-Golomb number.</summary>
    void Golomb(uint value);

    /// <summary>A finite sub-exponential number below <paramref name="n"/>, recentered around <paramref name="reference"/>.</summary>
    void RefSubexpFin(int n, int k, int reference, int value);
}

/// <summary>Writes the symbols with a range encoder, adapting the CDFs.</summary>
internal readonly struct Av1WriteSink(Av1SymbolEncoder encoder) : IAv1SymbolSink
{
    /// <summary>Gets the encoder.</summary>
    internal Av1SymbolEncoder Encoder { get; } = encoder;

    public void Symbol(ushort[] cdf, int at, int n, int value) => Encoder.Symbol(cdf, n, at, value);

    public void SymbolFixed(ReadOnlySpan<ushort> cdf, int n, int value) => Encoder.SymbolFixed(cdf, n, value);

    public void Bit(int value) => Encoder.Bit(value);

    public void Literal(int value, int bits) => Encoder.Literal(value, bits);

    public void Uniform(int n, int value) => Encoder.Uniform(n, value);

    public void Golomb(uint value) => Encoder.Golomb(value);

    public void RefSubexpFin(int n, int k, int reference, int value) => Encoder.RefSubexpFin(n, k, reference, value);
}

/// <summary>
/// Counts what the symbols would cost, in 1/512 bits as libaom's rate tables count them, from the CDFs as they stand; when
/// <see cref="Adapt"/> is set, also adapts each CDF after its symbol, as writing it would.
/// </summary>
internal struct Av1CostSink(bool adapt) : IAv1SymbolSink
{
    /// <summary>The cost of one equiprobable bit.</summary>
    internal const int BitCost = 512;

    /// <summary>Whether the CDFs adapt.</summary>
    internal readonly bool Adapt = adapt;

    /// <summary>The cost so far, in 1/512 bits.</summary>
    internal long Cost;

    public void Symbol(ushort[] cdf, int at, int n, int value)
    {
        int high = value > 0 ? cdf[at + value - 1] : 32768;
        Cost += Av1Rate.ProbabilityCost(high - cdf[at + value]);
        if (Adapt)
        {
            Av1SymbolDecoder.Adapt(ref cdf[at], value, n);
        }
    }

    public void SymbolFixed(ReadOnlySpan<ushort> cdf, int n, int value)
    {
        int high = value > 0 ? cdf[value - 1] : 32768;
        Cost += Av1Rate.ProbabilityCost(high - cdf[value]);
    }

    public void Bit(int value) => Cost += BitCost;

    public void Literal(int value, int bits) => Cost += bits * BitCost;

    public void Uniform(int n, int value)
    {
        int l = 32 - int.LeadingZeroCount(n);
        int m = (1 << l) - n;
        Cost += (l == 0 ? 0 : value < m ? l - 1 : l) * BitCost;
    }

    public void Golomb(uint value)
    {
        int length = 64 - (int)ulong.LeadingZeroCount((ulong)value + 1);
        Cost += ((2 * length) - 1) * BitCost;
    }

    public void RefSubexpFin(int n, int k, int reference, int value)
    {
        int v = (reference << 1) <= n ? Recenter(reference, value) : Recenter(n - 1 - reference, n - 1 - value);
        int i = 0, mk = 0, bits = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (n <= mk + (3 * a))
            {
                int count = n - mk, l = 32 - int.LeadingZeroCount(count), m = (1 << l) - count;
                bits += l == 0 ? 0 : v - mk < m ? l - 1 : l;
                break;
            }

            bits++;
            if (v < mk + a)
            {
                bits += b;
                break;
            }

            i++;
            mk += a;
        }

        Cost += bits * BitCost;

        static int Recenter(int r, int v) => v > (r << 1) ? v : v >= r ? (v - r) << 1 : ((r - v) << 1) - 1;
    }
}

/// <summary>Symbol costs from probabilities, and the rate-distortion cost libaom weighs choices with.</summary>
internal static class Av1Rate
{
    private static readonly ushort[] Costs = BuildCosts();

    /// <summary>Gets the cost of a symbol of probability <paramref name="p15"/> / 32768, in 1/512 bits (av1_cost_symbol).</summary>
    internal static int ProbabilityCost(int p15) => Costs[Math.Clamp(p15, 4, 32768)];

    /// <summary>
    /// Gets libaom's rate-distortion multiplier for a key frame (av1_compute_rd_mult_based_on_qindex, rd.c): the square of the DC
    /// quantizer times 3.3 plus a little per quantizer step, with tune IQ's weight toward larger transforms, scaled to the bit
    /// depth's distortion.
    /// </summary>
    internal static long RdMultiplier(int qindex, int bitDepth)
    {
        int q = bitDepth == 8 ? Av1Tables.DcQLookup8[qindex] : bitDepth == 10 ? Av1Tables.DcQLookup10[qindex] : Av1Tables.DcQLookup12[qindex];
        long rdmult = (long)q * q;
        rdmult = (long)(rdmult * (3.3 + (0.0015 * q)));
        int weight = Math.Clamp((255 - qindex) * 3 / 4, 0, 72) + 128;
        rdmult = (long)(rdmult * weight / 128.0);
        rdmult = bitDepth == 10 ? (rdmult + 8) >> 4 : bitDepth == 12 ? (rdmult + 128) >> 8 : rdmult;
        return Math.Max(rdmult, 1);
    }

    /// <summary>
    /// Gets a frame's rate-distortion multiplier: a still picture's (<see cref="RdMultiplier(int, int)"/>), or a video frame's,
    /// the square of the DC quantizer step times a little over three, a little more for key frames and less for inter frames
    /// above the base layer, scaled up for each pyramid layer so the frames fewer others predict from spend fewer bits (libaom's
    /// and SVT-AV1's layer factors are the idea).
    /// </summary>
    internal static long RdMultiplier(int qindex, int bitDepth, Av1EncoderSettings settings)
    {
        if (!settings.Video)
        {
            return RdMultiplier(qindex, bitDepth);
        }

        int q = bitDepth == 8 ? Av1Tables.DcQLookup8[qindex] : bitDepth == 10 ? Av1Tables.DcQLookup10[qindex] : Av1Tables.DcQLookup12[qindex];
        double factor = (settings.Intra ? 3.3 : settings.Layer == 0 ? 3.25 : 3.2) + (0.0015 * (q >> (bitDepth - 8)));
        factor *= LayerFactor(settings.Layer, settings.Reference);
        long rdmult = (long)((double)q * q * factor);
        rdmult = bitDepth == 10 ? (rdmult + 8) >> 4 : bitDepth == 12 ? (rdmult + 128) >> 8 : rdmult;
        return Math.Max(rdmult, 1);
    }

    /// <summary>Gets a video frame's multiplier scale for its pyramid layer: 1 at the base, rising toward the leaves.</summary>
    internal static double LayerFactor(int layer, bool reference) => layer <= 0 ? 1 : Math.Min(1 + (0.1 * layer), 1.4) * (reference ? 1 : 1.1);

    /// <summary>Gets RDCOST (rd.h): the rate in 1/512 bits times the multiplier, plus the distortion (a sum of squared errors) times 128.</summary>
    internal static long RdCost(long rdmult, long rate, long distortion) => (((rate * rdmult) + 256) >> 9) + (distortion << 7);

    private static ushort[] BuildCosts()
    {
        ushort[] costs = new ushort[32769];
        for (int p = 1; p <= 32768; p++)
        {
            costs[p] = (ushort)Math.Round(-Math.Log2(p / 32768.0) * 512);
        }

        costs[0] = costs[1];
        return costs;
    }
}
