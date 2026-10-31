using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// The encoding side of FSE (RFC 8878 section 4.1): normalizing counts, writing the table description that
/// <see cref="ZstdFse.ReadCounts"/> reads, and the encoding table whose states mirror <see cref="ZstdFse.Build"/>'s.
/// </summary>
internal sealed class ZstdFseEncoder
{
    // Per symbol: how many bits a state gives out (in the high half, offset so one shift finds it) and where its states start.
    private readonly int[] _deltaBits = new int[64];
    private readonly int[] _deltaState = new int[64];
    private readonly ushort[] _states = new ushort[512];

    /// <summary>Gets the accuracy log of the table built last.</summary>
    public int Log { get; private set; }

    /// <summary>Picks an accuracy log for <paramref name="total"/> symbols drawn from 0 to <paramref name="maxSymbol"/> (FSE's usual choice).</summary>
    public static int OptimalLog(int maxLog, int total, int maxSymbol)
    {
        int log = maxLog;
        int fromSource = BitOperations.Log2((uint)(total - 1)) - 2;
        int fromSymbols = Math.Min(BitOperations.Log2((uint)total) + 1, BitOperations.Log2((uint)Math.Max(maxSymbol, 1)) + 2);
        if (fromSource < log)
        {
            log = fromSource;
        }

        if (fromSymbols > log)
        {
            log = fromSymbols;
        }

        return Math.Clamp(log, 5, maxLog);
    }

    /// <summary>
    /// Normalizes <paramref name="counts"/> (summing to <paramref name="total"/>) to <paramref name="log"/> bits: every symbol that
    /// occurs gets at least one state, and the rest go out in proportion, rounding where the cost to the coded size is least.
    /// </summary>
    public static void Normalize(ReadOnlySpan<int> counts, int maxSymbol, int total, int log, Span<short> normalized)
    {
        int size = 1 << log;
        int given = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (counts[s] == 0)
            {
                normalized[s] = 0;
                continue;
            }

            int share = (int)(((long)counts[s] * size) / total);
            normalized[s] = (short)Math.Max(1, share);
            given += normalized[s];
        }

        // Hand out what is left (or take back what is over) one state at a time where it changes the cost least: a state more
        // for the symbol whose count per state is highest, a state less for the one whose count per state is lowest.
        while (given < size)
        {
            int best = -1;
            double bestGain = -1;
            for (int s = 0; s <= maxSymbol; s++)
            {
                if (counts[s] == 0)
                {
                    continue;
                }

                double gain = counts[s] * Math.Log2((normalized[s] + 1.0) / normalized[s]);
                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = s;
                }
            }

            normalized[best]++;
            given++;
        }

        while (given > size)
        {
            int best = -1;
            double bestLoss = double.MaxValue;
            for (int s = 0; s <= maxSymbol; s++)
            {
                if (normalized[s] <= 1)
                {
                    continue;
                }

                double loss = counts[s] * Math.Log2(normalized[s] / (normalized[s] - 1.0));
                if (loss < bestLoss)
                {
                    bestLoss = loss;
                    best = s;
                }
            }

            normalized[best]--;
            given--;
        }
    }

    /// <summary>The cost in bits of coding <paramref name="counts"/> with a table of these normalized counts, or infinity when one is missing.</summary>
    public static double Cost(ReadOnlySpan<int> counts, int maxSymbol, ReadOnlySpan<short> normalized, int log)
    {
        double bits = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (counts[s] == 0)
            {
                continue;
            }

            if (s >= normalized.Length || normalized[s] == 0)
            {
                return double.PositiveInfinity;
            }

            int share = normalized[s] == -1 ? 1 : normalized[s];
            bits += counts[s] * (log - Math.Log2(share));
        }

        return bits;
    }

    /// <summary>Writes the table description of <paramref name="normalized"/>; returns its length, or -1 when it does not fit.</summary>
    public static int WriteCounts(Span<byte> target, ReadOnlySpan<short> normalized, int maxSymbol, int log)
    {
        int size = 1 << log;
        ulong stream = (ulong)(log - 5);
        int bits = 4;
        int at = 0;
        int remaining = size + 1;
        int threshold = size;
        int width = log + 1;
        int symbol = 0;
        bool previousZero = false;
        while (symbol <= maxSymbol && remaining > 1)
        {
            if (previousZero)
            {
                int start = symbol;
                while (symbol <= maxSymbol && normalized[symbol] == 0)
                {
                    symbol++;
                }

                int zeros = symbol - start;
                while (zeros >= 3)
                {
                    stream |= 3UL << bits;
                    bits += 2;
                    zeros -= 3;
                    Spill(target, ref stream, ref bits, ref at);
                }

                stream |= (ulong)zeros << bits;
                bits += 2;
                Spill(target, ref stream, ref bits, ref at);
            }

            int count = normalized[symbol++];
            int max = (2 * threshold) - 1 - remaining;
            remaining -= Math.Abs(count);
            count++;
            if (count >= threshold)
            {
                count += max;
            }

            stream |= (ulong)count << bits;
            bits += width - (count < max ? 1 : 0);
            previousZero = count == 1;
            while (remaining < threshold)
            {
                width--;
                threshold >>= 1;
            }

            Spill(target, ref stream, ref bits, ref at);
            if (at < 0)
            {
                return -1;
            }
        }

        while (bits > 0)
        {
            if (at >= target.Length)
            {
                return -1;
            }

            target[at++] = (byte)stream;
            stream >>= 8;
            bits -= 8;
        }

        return at;
    }

    private static void Spill(Span<byte> target, ref ulong stream, ref int bits, ref int at)
    {
        while (bits >= 8)
        {
            if (at < 0 || at >= target.Length)
            {
                at = -1;
                return;
            }

            target[at++] = (byte)stream;
            stream >>= 8;
            bits -= 8;
        }
    }

    /// <summary>Builds the encoding table of <paramref name="normalized"/> (symbols 0 to <paramref name="maxSymbol"/>).</summary>
    public void Build(ReadOnlySpan<short> normalized, int maxSymbol, int log)
    {
        Log = log;
        int size = 1 << log;
        int high = size - 1;
        Span<byte> spread = stackalloc byte[512];
        Span<int> cumulative = stackalloc int[65];
        cumulative[0] = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (normalized[s] == -1)
            {
                cumulative[s + 1] = cumulative[s] + 1;
                spread[high--] = (byte)s;
            }
            else
            {
                cumulative[s + 1] = cumulative[s] + normalized[s];
            }
        }

        int step = (size >> 1) + (size >> 3) + 3;
        int mask = size - 1;
        int position = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            for (int i = 0; i < normalized[s]; i++)
            {
                spread[position] = (byte)s;
                do
                {
                    position = (position + step) & mask;
                }
                while (position > high);
            }
        }

        for (int u = 0; u < size; u++)
        {
            int s = spread[u];
            _states[cumulative[s]++] = (ushort)(size + u);
        }

        int total = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            int n = normalized[s];
            if (n == 0)
            {
                _deltaBits[s] = ((log + 1) << 16) - size;
            }
            else if (n is -1 or 1)
            {
                _deltaBits[s] = (log << 16) - size;
                _deltaState[s] = total - 1;
                total++;
            }
            else
            {
                int bitsOut = log - BitOperations.Log2((uint)(n - 1));
                _deltaBits[s] = (bitsOut << 16) - (n << bitsOut);
                _deltaState[s] = total - n;
                total += n;
            }
        }
    }

    /// <summary>The state to start with when the last symbol coded (the first decoded) is <paramref name="symbol"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Start(int symbol)
    {
        int bitsOut = (_deltaBits[symbol] + (1 << 15)) >> 16;
        int value = (bitsOut << 16) - _deltaBits[symbol];
        return _states[(value >> bitsOut) + _deltaState[symbol]];
    }

    /// <summary>Codes <paramref name="symbol"/> from <paramref name="state"/>, writing the bits the decoder reads for its next state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(ref ZstdBitWriter writer, ref int state, int symbol)
    {
        int bitsOut = (state + Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_deltaBits), symbol)) >> 16;
        writer.Add((ulong)state, bitsOut);
        state = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_states),
            (state >> bitsOut) + Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_deltaState), symbol));
    }

    /// <summary>Writes the final state, which the decoder reads first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Finish(ref ZstdBitWriter writer, int state) => writer.Add((ulong)state, Log);
}
