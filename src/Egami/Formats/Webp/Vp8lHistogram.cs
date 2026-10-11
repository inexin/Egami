using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Symbol counts for one VP8L prefix code group (green with lengths and cache slots, red, blue, alpha, distance), and estimates of
/// the bits a group of codes built from them would take: the symbols at their entropy plus the code lengths' own header.
/// </summary>
internal sealed class Vp8lHistogram
{
    internal Vp8lHistogram(int cacheBits)
    {
        CacheBits = cacheBits;
        Literal = new uint[256 + Vp8lEncoder.LengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0)];
    }

    internal int CacheBits { get; }

    /// <summary>Gets the green, length-prefix and cache-slot counts.</summary>
    internal uint[] Literal { get; }

    internal uint[] Red { get; } = new uint[256];

    internal uint[] Blue { get; } = new uint[256];

    internal uint[] Alpha { get; } = new uint[256];

    internal uint[] Distance { get; } = new uint[Vp8lEncoder.DistanceCodes];

    /// <summary>Gets or sets a cached <see cref="Bits"/> value, or NaN when it must be worked out again.</summary>
    internal double CachedBits { get; set; } = double.NaN;

    internal void Clear()
    {
        Array.Clear(Literal);
        Array.Clear(Red);
        Array.Clear(Blue);
        Array.Clear(Alpha);
        Array.Clear(Distance);
        CachedBits = double.NaN;
    }

    /// <summary>Counts one token.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(ulong token)
    {
        Vp8lEncoder.Token.Read(token, out int kind, out uint value, out int length);
        switch (kind)
        {
            case Vp8lEncoder.Token.Literal:
                Literal[(value >> 8) & 0xFF]++;
                Red[(value >> 16) & 0xFF]++;
                Blue[value & 0xFF]++;
                Alpha[value >> 24]++;
                break;
            case Vp8lEncoder.Token.Cached:
                Literal[256 + Vp8lEncoder.LengthCodes + (int)value]++;
                break;
            default:
                Literal[256 + Vp8lEncoder.PrefixOf(length, out _, out _)]++;
                Distance[Vp8lEncoder.PrefixOf((int)value, out _, out _)]++;
                break;
        }

        CachedBits = double.NaN;
    }

    /// <summary>Adds another histogram's counts to this one.</summary>
    internal void Merge(Vp8lHistogram other)
    {
        AddTo(other.Literal, Literal);
        AddTo(other.Red, Red);
        AddTo(other.Blue, Blue);
        AddTo(other.Alpha, Alpha);
        AddTo(other.Distance, Distance);
        CachedBits = double.NaN;
    }

    /// <summary>Copies another histogram's counts.</summary>
    internal void CopyFrom(Vp8lHistogram other)
    {
        other.Literal.CopyTo(Literal, 0);
        other.Red.CopyTo(Red, 0);
        other.Blue.CopyTo(Blue, 0);
        other.Alpha.CopyTo(Alpha, 0);
        other.Distance.CopyTo(Distance, 0);
        CachedBits = other.CachedBits;
    }

    /// <summary>Returns whether nothing was counted.</summary>
    internal bool IsEmpty()
    {
        foreach (uint c in Literal)
        {
            if (c != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the estimated bits of the five codes and the symbols coded with them.</summary>
    internal double Bits()
    {
        if (double.IsNaN(CachedBits))
        {
            CachedBits = Vp8lCost.Bits(Literal) + Vp8lCost.Bits(Red) + Vp8lCost.Bits(Blue) + Vp8lCost.Bits(Alpha) + Vp8lCost.Bits(Distance);
        }

        return CachedBits;
    }

    /// <summary>Returns the estimated bits of this histogram merged with <paramref name="other"/>, without merging.</summary>
    internal double MergedBits(Vp8lHistogram other)
    {
        return Vp8lCost.Bits(Literal, other.Literal) + Vp8lCost.Bits(Red, other.Red) + Vp8lCost.Bits(Blue, other.Blue)
            + Vp8lCost.Bits(Alpha, other.Alpha) + Vp8lCost.Bits(Distance, other.Distance);
    }

    private static void AddTo(ReadOnlySpan<uint> source, Span<uint> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            target[i] += source[i];
        }
    }
}

/// <summary>Bit-cost estimates for prefix codes: entropy for the symbols, a run-length model for the code lengths.</summary>
internal static class Vp8lCost
{
    private const int TableSize = 4096;

    private static readonly float[] Log2Table = BuildLog2();
    private static readonly float[] SLog2Table = BuildSLog2();

    /// <summary>Returns log2(v), 0 for 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Log2(uint v) => v < TableSize ? Log2Table[v] : MathF.Log2(v);

    /// <summary>Returns v × log2(v), 0 for 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float SLog2(uint v) => v < TableSize ? SLog2Table[v] : v * MathF.Log2(v);

    /// <summary>Returns the estimated bits of a code for <paramref name="counts"/> and the symbols it codes.</summary>
    internal static double Bits(ReadOnlySpan<uint> counts)
    {
        uint total = 0;
        uint largest = 0;
        double sum = 0;
        int used = 0;
        int last = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            uint c = counts[i];
            if (c != 0)
            {
                total += c;
                sum += SLog2(c);
                largest = Math.Max(largest, c);
                used++;
                last = i;
            }
        }

        return Estimate(total, largest, sum, used, last) + (used > 2 || last >= 256 ? Header(counts, total, used) : 0);
    }

    /// <summary>Returns the estimated bits of a code for the sum of two count arrays.</summary>
    internal static double Bits(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        uint total = 0;
        uint largest = 0;
        double sum = 0;
        int used = 0;
        int last = -1;
        for (int i = 0; i < a.Length; i++)
        {
            uint c = a[i] + b[i];
            if (c != 0)
            {
                total += c;
                sum += SLog2(c);
                largest = Math.Max(largest, c);
                used++;
                last = i;
            }
        }

        return Estimate(total, largest, sum, used, last) + (used > 2 || last >= 256 ? Header(a, b, total, used) : 0);
    }

    /// <summary>
    /// Returns the bits of the symbols: their entropy, raised toward what whole-bit code lengths can reach. A prefix code gives its
    /// commonest symbol at least one bit and every other at least two, so a skewed alphabet costs more than its entropy says.
    /// </summary>
    private static double Estimate(uint total, uint largest, double sum, int used, int last)
    {
        if (used <= 1)
        {
            return 12;
        }

        if (used == 2 && last < 256)
        {
            return 20 + total;
        }

        double entropy = SLog2(total) - sum;
        double mix = used == 2 ? 0.99 : used == 3 ? 0.95 : used == 4 ? 0.7 : 0.627;
        double floor = (2.0 * total) - largest;
        floor = (mix * floor) + ((1 - mix) * entropy);
        return Math.Max(entropy, floor);
    }

    /// <summary>
    /// Estimates the code lengths' header: each symbol's length from its probability, then runs of equal lengths and zeros as the
    /// code-length code would code them, at about 3 bits a code-length symbol plus the repeat codes' extra bits.
    /// </summary>
    private static double Header(ReadOnlySpan<uint> counts, uint total, int used)
    {
        float logTotal = Log2(total);
        double bits = 40;
        int previous = -1;
        int run = 0;
        for (int i = 0; i <= counts.Length; i++)
        {
            int length = i == counts.Length ? -2 : counts[i] == 0 ? 0 : Math.Clamp((int)(logTotal - Log2(counts[i]) + 0.5f), 1, 15);
            if (length == previous)
            {
                run++;
                continue;
            }

            bits += RunBits(previous, run);
            previous = length;
            run = 1;
        }

        return bits + (used * 0.25);
    }

    private static double Header(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, uint total, int used)
    {
        float logTotal = Log2(total);
        double bits = 40;
        int previous = -1;
        int run = 0;
        for (int i = 0; i <= a.Length; i++)
        {
            uint c = i == a.Length ? 0 : a[i] + b[i];
            int length = i == a.Length ? -2 : c == 0 ? 0 : Math.Clamp((int)(logTotal - Log2(c) + 0.5f), 1, 15);
            if (length == previous)
            {
                run++;
                continue;
            }

            bits += RunBits(previous, run);
            previous = length;
            run = 1;
        }

        return bits + (used * 0.25);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double RunBits(int length, int run)
    {
        if (run == 0 || length < 0)
        {
            return 0;
        }

        if (length == 0)
        {
            // Zeros: code 18 for 11 to 138, code 17 for 3 to 10, single zeros otherwise.
            double bits = 0;
            while (run >= 11)
            {
                bits += 10;
                run -= Math.Min(run, 138);
            }

            return bits + (run >= 3 ? 6 : run * 2.5);
        }

        // The length once, then code 16 for 3 to 6 repeats.
        double cost = 3.5;
        run--;
        while (run >= 3)
        {
            cost += 4.5;
            run -= Math.Min(run, 6);
        }

        return cost + (run * 3.5);
    }

    private static float[] BuildLog2()
    {
        float[] table = new float[TableSize];
        for (int i = 1; i < TableSize; i++)
        {
            table[i] = MathF.Log2(i);
        }

        return table;
    }

    private static float[] BuildSLog2()
    {
        float[] table = new float[TableSize];
        for (int i = 1; i < TableSize; i++)
        {
            table[i] = i * MathF.Log2(i);
        }

        return table;
    }
}
