using System.Numerics;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Jxl;

/// <summary>One value to entropy code, in a context; an LZ77 length when <see cref="IsLz77Length"/> is set.</summary>
internal readonly record struct JxlToken(int Context, uint Value, bool IsLz77Length = false);

/// <summary>Something bits can be written to: a <see cref="JxlBitWriter"/>, or a counter that only adds up their number.</summary>
internal interface IJxlBitSink
{
    void Write(ulong value, int bits);
}

/// <summary>Counts bits without keeping them, to price a choice before writing it.</summary>
internal sealed class JxlBitCounter : IJxlBitSink
{
    internal long Bits { get; private set; }

    public void Write(ulong value, int bits) => Bits += bits;
}

/// <summary>How hard the entropy coder works (libjxl's HistogramParams).</summary>
internal sealed record JxlHistogramParams
{
    internal enum ClusteringKind
    {
        Fast,
        Best,
    }

    internal enum UintKind
    {
        None,
        Fast,
        ContextMap,
        Best,
    }

    internal enum AnsKind
    {
        Fast,
        Approximate,
        Precise,
    }

    internal ClusteringKind Clustering { get; init; } = ClusteringKind.Best;

    internal UintKind UintMethod { get; init; } = UintKind.Best;

    /// <summary>Gets whether runs of a repeated value may become LZ77 copies at distance 1 (libjxl's kRLE).</summary>
    internal bool Rle { get; init; }

    internal AnsKind AnsStrategy { get; init; } = AnsKind.Precise;

    /// <summary>Gets each stream's widest channel, which makes LZ77 distance 1 the special "one to the left" distance.</summary>
    internal IReadOnlyList<int> ImageWidths { get; init; } = [];

    internal int MaxHistograms { get; init; } = int.MaxValue;

    /// <summary>Gets libjxl's settings for Modular data at <paramref name="effort"/> (ForModular, lossless).</summary>
    internal static JxlHistogramParams ForModular(int effort) => new()
    {
        Clustering = effort >= 8 ? ClusteringKind.Best : ClusteringKind.Fast,
        AnsStrategy = effort <= 1 ? AnsKind.Fast : effort >= 8 ? AnsKind.Precise : AnsKind.Approximate,
        Rle = effort >= 5,
        UintMethod = effort >= 5 ? UintKind.Fast : UintKind.None,
    };

    /// <summary>Gets the hybrid integer configuration tokens are counted with before the per-cluster choice.</summary>
    internal JxlUintConfig UintConfig => UintMethod == UintKind.ContextMap ? JxlEntropyCode.Config(2, 0, 1) : JxlEntropyCode.Config(4, 2, 0);
}

/// <summary>A histogram of token counts (libjxl's Histogram).</summary>
internal sealed class JxlHistogram
{
    internal int[] Counts = new int[8];

    internal long Total;

    /// <summary>A cached entropy or cost; not kept up to date by <see cref="Add"/>.</summary>
    internal float Entropy;

    internal void Add(uint symbol)
    {
        if (symbol >= Counts.Length)
        {
            Array.Resize(ref Counts, (int)((symbol + 8) & ~7u));
        }

        Counts[symbol]++;
        Total++;
    }

    internal void AddHistogram(JxlHistogram other)
    {
        if (other.Counts.Length > Counts.Length)
        {
            Array.Resize(ref Counts, other.Counts.Length);
        }

        for (int i = 0; i < other.Counts.Length; i++)
        {
            Counts[i] += other.Counts[i];
        }

        Total += other.Total;
    }

    internal JxlHistogram Clone() => new() { Counts = (int[])Counts.Clone(), Total = Total, Entropy = Entropy };

    internal int AlphabetSize()
    {
        for (int i = Counts.Length - 1; i >= 0; i--)
        {
            if (Counts[i] > 0)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Gets the Shannon entropy of the counts in bits, and keeps it in <see cref="Entropy"/>.</summary>
    internal float ShannonEntropy()
    {
        Entropy = 0;
        if (Total == 0)
        {
            return 0;
        }

        Entropy = EntropyOf(Counts, [], Total);
        return Entropy;
    }

    /// <summary>
    /// Gets the entropy of the counts <paramref name="a"/> plus <paramref name="b"/> (lengths multiples of 8) with libjxl's fast
    /// logarithm, as its HistogramEntropy and HistogramDistance work them out.
    /// </summary>
    internal static float EntropyOf(ReadOnlySpan<int> a, ReadOnlySpan<int> b, long total)
    {
        float inv = 1f / total;
        int n = Math.Max(a.Length, b.Length);
        if (JxlFastMath.Vectorized && (a.Length & 7) == 0 && (b.Length & 7) == 0)
        {
            ref int ra = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(a);
            ref int rb = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(b);
            Vector256<float> vInv = Vector256.Create(inv);
            Vector256<int> vTotal = Vector256.Create((int)total);
            Vector256<float> acc = Vector256<float>.Zero;
            for (int i = 0; i < n; i += 8)
            {
                Vector256<int> c = i < a.Length ? Vector256.LoadUnsafe(ref ra, (nuint)i) : default;
                if (i < b.Length)
                {
                    c += Vector256.LoadUnsafe(ref rb, (nuint)i);
                }

                Vector256<float> cf = Vector256.ConvertToSingle(c);
                Vector256<float> e = cf * JxlFastMath.Log2(vInv * cf);
                acc -= Vector256.AndNot(e, Vector256.Equals(c, vTotal).AsSingle());
            }

            return Vector256.Sum(acc);
        }

        float sum = 0;
        for (int i = 0; i < n; i++)
        {
            int c = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
            if (c != 0 && c != total)
            {
                sum -= c * JxlFastMath.Log2(inv * c);
            }
        }

        return sum;
    }

    /// <summary>Gets the bits the counts and their ANS description would take, with the fast choice of precision.</summary>
    internal float AnsPopulationCost() => AlphabetSize() > 256 ? float.MaxValue : JxlAnsEncodingHistogram.ComputeBest(this, JxlHistogramParams.AnsKind.Fast).Cost;
}

/// <summary>
/// A histogram normalized to 4096 for ANS and its description (C.2.4): flat, one or two symbols, or bit widths with a
/// precision shift, run lengths for repeated counts and the largest count left out. Translated from libjxl's
/// ANSEncodingHistogram (enc_ans.cc).
/// </summary>
internal sealed class JxlAnsEncodingHistogram
{
    private const int LogTab = 12;
    private const int TabSize = 1 << LogTab;
    private const int MaxAlphabet = 256;

    private static readonly uint[] Lg2 = BuildLg2();
    private static readonly (int Count, int StepLog, int DeltaLg2)[][] Allowed;
    private static readonly ushort[][] AllowedIndex;

    private readonly int[] _symbols = new int[2];
    private int[] _counts = [];
    private int _method;
    private int _omit;
    private int _alphabet;
    private int _symbolCount;

    static JxlAnsEncodingHistogram()
    {
        Allowed = new (int, int, int)[LogTab][];
        AllowedIndex = new ushort[LogTab][];
        for (int shift = 0; shift < LogTab; shift++)
        {
            (int Count, int StepLog, int DeltaLg2)[] ac = new (int, int, int)[TabSize];
            ushort[] ai = new ushort[TabSize];
            int last = -1;
            int slot = 0;
            for (int i = TabSize - 1; i >= 0; i--)
            {
                int curr = i & ~((1 << SmallestIncrementLog((uint)i, shift)) - 1);
                if (curr == last)
                {
                    continue;
                }

                last = curr;
                ac[slot].Count = curr;
                ai[curr] = (ushort)slot;
                if (curr == 0)
                {
                    ac[slot].DeltaLg2 = int.MaxValue;
                    ac[slot].StepLog = 0;
                }
                else if (slot > 0)
                {
                    int prev = ac[slot - 1].Count;
                    ac[slot].DeltaLg2 = (int)Math.Round(Math.ScaleB(Math.Log2((double)prev / curr) / LogTab, 31));
                    ac[slot].StepLog = BitOperations.Log2((uint)(prev - curr));
                }

                slot++;
            }

            Allowed[shift] = ac;
            AllowedIndex[shift] = ai;
        }
    }

    private JxlAnsEncodingHistogram()
    {
    }

    /// <summary>Gets the normalized counts, 4096 in total.</summary>
    internal int[] Counts => _counts;

    /// <summary>Gets the estimated bits of the description and the data coded with it.</summary>
    internal float Cost { get; private set; }

    /// <summary>Picks the description, trying precision shifts as <paramref name="strategy"/> says, that costs least.</summary>
    internal static JxlAnsEncodingHistogram ComputeBest(JxlHistogram histo, JxlHistogramParams.AnsKind strategy)
    {
        JxlAnsEncodingHistogram result = new() { _alphabet = histo.AlphabetSize() };
        if (result._alphabet > MaxAlphabet)
        {
            throw new InvalidOperationException("An ANS histogram has more than 256 symbols.");
        }

        if (result._alphabet == 0)
        {
            result._method = 1;
            result._counts = new int[histo.Counts.Length];
            result.Cost = 3;
            return result;
        }

        result._method = 0;
        result._symbolCount = result._alphabet;
        result._counts = Flat(result._alphabet, histo.Counts.Length);
        JxlBitCounter flatSize = new();
        result.Encode(flatSize);
        result.Cost = flatSize.Bits + EstimateDataBitsFlat(histo);

        int symbols = 0;
        for (int n = 0; n < result._alphabet; n++)
        {
            if (histo.Counts[n] > 0)
            {
                if (symbols < 2)
                {
                    result._symbols[symbols] = n;
                }

                symbols++;
            }
        }

        result._symbolCount = symbols;
        if (symbols == 1)
        {
            result._method = 1;
            result._counts = (int[])histo.Counts.Clone();
            result._counts[result._symbols[0]] = TabSize;
            JxlBitCounter size = new();
            result.Encode(size);
            result.Cost = size.Bits;
            return result;
        }

        JxlAnsEncodingHistogram best = result;
        void TryShift(int shift)
        {
            JxlAnsEncodingHistogram n = new() { _alphabet = result._alphabet, _symbolCount = symbols, _counts = new int[histo.Counts.Length] };
            n._symbols[0] = result._symbols[0];
            n._symbols[1] = result._symbols[1];
            n._method = Math.Min(shift, LogTab - 1) + 1;
            if (!n.Rebalance(histo))
            {
                throw new InvalidOperationException("A histogram could not be rebalanced.");
            }

            JxlBitCounter size = new();
            n.Encode(size);
            n.Cost = size.Bits + n.EstimateDataBits(histo);
            if (n.Cost < best.Cost)
            {
                best = n;
            }
        }

        switch (strategy)
        {
            case JxlHistogramParams.AnsKind.Precise:
                for (int shift = 0; shift < LogTab; shift++)
                {
                    TryShift(shift);
                }

                break;
            case JxlHistogramParams.AnsKind.Approximate:
                for (int shift = 0; shift <= LogTab; shift += 2)
                {
                    TryShift(shift);
                }

                break;
            default:
                TryShift(0);
                TryShift(LogTab / 2);
                TryShift(LogTab);
                break;
        }

        return best;
    }

    internal static void WriteU8(IJxlBitSink w, int n)
    {
        if (n == 0)
        {
            w.Write(0, 1);
            return;
        }

        w.Write(1, 1);
        int bits = BitOperations.Log2((uint)n);
        w.Write((ulong)bits, 3);
        w.Write((ulong)(n - (1 << bits)), bits);
    }

    /// <summary>Writes the description (C.2.4).</summary>
    internal void Encode(IJxlBitSink w)
    {
        if (_method == 0)
        {
            w.Write(0, 1);
            w.Write(1, 1);
            WriteU8(w, _alphabet - 1);
            return;
        }

        if (_symbolCount <= 2)
        {
            w.Write(1, 1);
            if (_symbolCount == 0)
            {
                w.Write(0, 1);
                WriteU8(w, 0);
            }
            else
            {
                w.Write((ulong)(_symbolCount - 1), 1);
                for (int i = 0; i < _symbolCount; i++)
                {
                    WriteU8(w, _symbols[i]);
                }
            }

            if (_symbolCount == 2)
            {
                w.Write((ulong)_counts[_symbols[0]], LogTab);
            }

            return;
        }

        w.Write(0, 1);
        w.Write(0, 1);

        // An Elias gamma-like code for the shift, without the terminating zero at the longest length.
        int upperBoundLog = BitOperations.Log2(LogTab + 1);
        int log = BitOperations.Log2((uint)_method);
        w.Write((1UL << log) - 1, log);
        if (log != upperBoundLog)
        {
            w.Write(0, 1);
        }

        w.Write((ulong)(((1 << log) - 1) & _method), log);
        WriteU8(w, _alphabet - 3);

        // Runs of equal counts, recorded at each run's first symbol; the omitted symbol and the one after it never join one.
        Span<byte> same = stackalloc byte[MaxAlphabet];
        same.Clear();
        int last = 0;
        for (int i = 1; i <= _alphabet; i++)
        {
            if (i == _alphabet || i == _omit || i == _omit + 1 || _counts[i] != _counts[last])
            {
                same[last] = (byte)(i - last);
                last = i;
            }
        }

        // The omitted symbol gets the shortest code at or above every other width; it must be the first of the widest.
        Span<byte> width = stackalloc byte[MaxAlphabet];
        width.Clear();
        int omitWidth = 10;
        for (int i = 0; i < _alphabet; i++)
        {
            if (i != _omit && _counts[i] > 0)
            {
                width[i] = (byte)(BitOperations.Log2((uint)_counts[i]) + 1);
                omitWidth = Math.Max(omitWidth, width[i] + (i < _omit ? 1 : 0));
            }
        }

        width[_omit] = (byte)omitWidth;
        ReadOnlySpan<byte> lengths = [5, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 6, 7, 7];
        ReadOnlySpan<byte> codes = [17, 11, 15, 3, 9, 7, 4, 2, 5, 6, 0, 33, 1, 65];
        const int MinReps = 5;
        const int Rep = LogTab + 1;
        for (int i = 0; i < _alphabet; i++)
        {
            w.Write(codes[width[i]], lengths[width[i]]);
            if (same[i] >= MinReps)
            {
                w.Write(codes[Rep], lengths[Rep]);
                WriteU8(w, same[i] - MinReps);
                i += same[i] - 1;
            }
        }

        int shift = _method - 1;
        if (shift != 0)
        {
            for (int i = 0; i < _alphabet; i++)
            {
                if (width[i] > 1 && i != _omit)
                {
                    int bitcount = Precision(width[i] - 1, shift);
                    int drop = width[i] - 1 - bitcount;
                    w.Write((ulong)((_counts[i] >> drop) - (1 << bitcount)), bitcount);
                }

                if (same[i] >= MinReps)
                {
                    i += same[i] - 1;
                }
            }
        }
    }

    private static int Precision(int logcount, int shift)
    {
        int r = Math.Min(logcount, shift - ((LogTab - logcount) >> 1));
        return r < 0 ? 0 : r;
    }

    private static int SmallestIncrementLog(uint count, int shift)
    {
        if (count == 0)
        {
            return 0;
        }

        int bits = BitOperations.Log2(count);
        return bits - Precision(bits, shift);
    }

    private static int[] Flat(int length, int size)
    {
        int[] result = new int[Math.Max(size, length)];
        int count = TabSize / length;
        int rem = TabSize % length;
        for (int i = 0; i < length; i++)
        {
            result[i] = count + (i < rem ? 1 : 0);
        }

        return result;
    }

    private static uint[] BuildLg2()
    {
        uint[] lg2 = new uint[TabSize + 1];
        for (int i = 1; i <= TabSize; i++)
        {
            lg2[i] = (uint)Math.Round(Math.ScaleB(Math.Log2(i) / LogTab, 31));
        }

        return lg2;
    }

    private static float EstimateDataBitsFlat(JxlHistogram histo)
    {
        int len = histo.AlphabetSize();
        long flatBits = (long)Lg2[len] * LogTab;
        return MathF.ScaleB(histo.Total * flatBits, -31);
    }

    private float EstimateDataBits(JxlHistogram histo)
    {
        long sum = 0;
        for (int i = 0; i < _alphabet; i++)
        {
            sum += histo.Counts[i] * (long)Lg2[_counts[i]];
        }

        return (histo.Total - MathF.ScaleB(sum, -31)) * LogTab;
    }

    /// <summary>
    /// Scales the counts to 4096 within the precision the shift allows, then moves single steps between bins while that
    /// raises the coded entropy, the largest bin absorbing what is left (libjxl's RebalanceHistogram).
    /// </summary>
    private bool Rebalance(JxlHistogram histo)
    {
        int shift = _method - 1;
        long[] balanceInc = new long[LogTab - 1];
        long[] balanceDec = new long[LogTab - 1];
        (int Count, int StepLog, int DeltaLg2)[] ac = Allowed[shift];
        ushort[] ai = AllowedIndex[shift];
        List<(int Freq, int CountIndex, int Bin)> bins = new(256);
        double norm = (double)TabSize / histo.Total;
        int remainder = 0;
        long maxFreq = 0;
        int rest = TabSize;
        for (int n = 0; n < _alphabet; n++)
        {
            int freq = histo.Counts[n];
            if (freq > maxFreq)
            {
                remainder = n;
                maxFreq = freq;
            }

            double target = freq * norm;
            int count = Math.Max((int)Math.Round(target, MidpointRounding.AwayFromZero), freq > 0 ? 1 : 0);
            count = Math.Min(count, TabSize - 1);
            int stepLog = SmallestIncrementLog((uint)count, shift);
            count &= ~((1 << stepLog) - 1);
            _counts[n] = count;
            rest -= count;
            if (target > 1.0)
            {
                bins.Add((freq, ai[count], n));
            }
        }

        bins.RemoveAt(bins.FindIndex(b => b.Bin == remainder));
        rest += _counts[remainder];

        if (bins.Count > 0)
        {
            int maxLog = ac[1].StepLog;
            long DeltaInc((int Freq, int CountIndex, int Bin) a) => (a.Freq * (long)ac[a.CountIndex].DeltaLg2) - balanceInc[ac[a.CountIndex].StepLog];
            long DeltaDec((int Freq, int CountIndex, int Bin) a) => (a.Freq * (long)ac[a.CountIndex + 1].DeltaLg2) - balanceDec[ac[a.CountIndex + 1].StepLog];
            while (true)
            {
                for (int log = 0; log <= maxLog; log++)
                {
                    int delta = 1 << log;
                    if (rest >= TabSize)
                    {
                        balanceInc[log] = 0;
                        balanceDec[log] = 0;
                    }
                    else if (rest > 1)
                    {
                        balanceInc[log] = rest > delta ? maxFreq * (long)(Lg2[rest] - Lg2[rest - delta]) : long.MaxValue;
                        balanceDec[log] = rest + delta < TabSize ? maxFreq * (long)(Lg2[rest + delta] - Lg2[rest]) : 0;
                    }
                    else
                    {
                        balanceInc[log] = long.MaxValue;
                        balanceDec[log] = long.MaxValue;
                    }
                }

                // The first bin whose step gains most per unit of change (std::max_element keeps the first of equals).
                int bestInc = 0;
                long bestIncKey = DeltaInc(bins[0]) >> ac[bins[0].CountIndex].StepLog;
                for (int i = 1; i < bins.Count; i++)
                {
                    long key = DeltaInc(bins[i]) >> ac[bins[i].CountIndex].StepLog;
                    if (bestIncKey < key)
                    {
                        (bestInc, bestIncKey) = (i, key);
                    }
                }

                if (DeltaInc(bins[bestInc]) > 0)
                {
                    (int f, int ci, int b) = bins[bestInc];
                    rest -= 1 << ac[ci].StepLog;
                    bins[bestInc] = (f, ci - 1, b);
                }
                else
                {
                    int bestDec = 0;
                    long bestDecKey = DeltaDec(bins[0]) >> ac[bins[0].CountIndex + 1].StepLog;
                    for (int i = 1; i < bins.Count; i++)
                    {
                        long key = DeltaDec(bins[i]) >> ac[bins[i].CountIndex + 1].StepLog;
                        if (key < bestDecKey)
                        {
                            (bestDec, bestDecKey) = (i, key);
                        }
                    }

                    if (DeltaDec(bins[bestDec]) >= 0)
                    {
                        break;
                    }

                    (int f, int ci, int b) = bins[bestDec];
                    rest += 1 << ac[ci + 1].StepLog;
                    bins[bestDec] = (f, ci + 1, b);
                }
            }

            foreach ((_, int ci, int b) in bins)
            {
                _counts[b] = ac[ci].Count;
            }

            // The balancing bin needs room to grow its bit width; else the first bin of 12 bits takes its place.
            for (int n = 0; n < remainder; n++)
            {
                if (_counts[n] >= 2048)
                {
                    _counts[remainder] = _counts[n];
                    remainder = n;
                    break;
                }
            }
        }

        _counts[remainder] = rest;
        _omit = remainder;
        return _counts[remainder] > 0;
    }
}

/// <summary>
/// The JPEG XL entropy coder's encoding side (Annex C), the inverse of <see cref="JxlEntropyDecoder"/>: LZ77 runs, clustered
/// contexts, a hybrid integer configuration per cluster and ANS distributions, and token streams coded with rANS in reverse.
/// Translated from libjxl 0.12.0's enc_ans, enc_cluster, enc_context_map and enc_lz77 (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlEntropyCode
{
    private const int LogTab = 12;
    private const int TabSize = 1 << LogTab;
    private const int ClustersLimit = 128;
    private const uint Lz77MinSymbol = 224;
    private const uint Lz77MinLength = 3;

    private static readonly JxlUintConfig Lz77LengthConfig = Config(0, 0, 0);

    private byte[] _contextMap = [];
    private JxlUintConfig[] _configs = [];
    private ushort[][] _freq = [];
    private ushort[][] _reverse = [];
    private ushort[][] _start = [];
    private bool _lz77;

    /// <summary>Makes a hybrid integer configuration from its split exponent and the bits kept in the token.</summary>
    internal static JxlUintConfig Config(int split, int msb, int lsb) => new(split, 1u << split, msb, lsb);

    /// <summary>
    /// Builds a code for <paramref name="contexts"/> contexts from the streams it will code and writes its description to
    /// <paramref name="w"/>; with RLE allowed, <paramref name="streams"/> may come back with runs replaced by LZ77 copies, which
    /// is what <see cref="WriteTokens"/> must then write.
    /// </summary>
    internal static JxlEntropyCode Write(IJxlBitSink w, JxlHistogramParams p, int contexts, List<List<JxlToken>> streams) => Build(w, p, contexts, streams, out _);

    /// <summary>As <see cref="Write"/>, also giving the estimated bits of the description and data.</summary>
    internal static JxlEntropyCode Build(IJxlBitSink w, JxlHistogramParams p, int contexts, List<List<JxlToken>> streams, out long cost)
    {
        JxlEntropyCode code = new();
        List<List<JxlToken>>? lz = p.Rle ? ApplyRle(p, contexts, streams) : null;
        code._lz77 = lz is not null;

        JxlBitCounter header = new();
        WriteLz77Header(w, code._lz77);
        WriteLz77Header(header, code._lz77);
        cost = header.Bits;
        if (code._lz77)
        {
            contexts++;
            streams.Clear();
            streams.AddRange(lz!);
        }

        JxlHistogram[] builder = new JxlHistogram[contexts];
        for (int i = 0; i < contexts; i++)
        {
            builder[i] = new JxlHistogram();
        }

        JxlUintConfig config = p.UintConfig;
        foreach (List<JxlToken> stream in streams)
        {
            foreach (JxlToken t in stream)
            {
                (uint tok, _, _) = Split(t.IsLz77Length ? Lz77LengthConfig : config, t.Value);
                builder[t.Context].Add(t.IsLz77Length ? tok + Lz77MinSymbol : tok);
            }
        }

        cost += code.BuildCodes(w, p, streams, builder);
        return code;
    }

    /// <summary>Splits a value into its token, the number of raw bits and the raw bits (C.2.6, inverted).</summary>
    internal static (uint Token, int NBits, uint Bits) Split(in JxlUintConfig c, uint value)
    {
        if (value < c.Split)
        {
            return (value, 0, 0);
        }

        int n = BitOperations.Log2(value);
        uint m = value - (1u << n);
        int ml = c.MsbInToken + c.LsbInToken;
        uint token = c.Split + ((uint)(n - c.SplitExponent) << ml) + ((m >> (n - c.MsbInToken)) << c.LsbInToken) + (m & ((1u << c.LsbInToken) - 1));
        int nbits = n - ml;
        uint bits = (m >> c.LsbInToken) & (uint)((1UL << nbits) - 1);
        return (token, nbits, bits);
    }

    /// <summary>Writes one stream: the final ANS state, then the renormalization bits and raw bits in decoding order.</summary>
    internal void WriteTokens(IJxlBitSink w, List<JxlToken> tokens)
    {
        // Coded last to first; a token's raw bits follow its symbol when decoding, so they are pushed first here.
        List<(uint Bits, int Count)> chunks = new(tokens.Count * 2);
        uint state = 0x130000;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            JxlToken t = tokens[i];
            int histo = _contextMap[t.Context];
            (uint tok, int nbits, uint bits) = Split(t.IsLz77Length ? Lz77LengthConfig : _configs[histo], t.Value);
            if (t.IsLz77Length)
            {
                tok += Lz77MinSymbol;
            }

            if (nbits > 0)
            {
                chunks.Add((bits, nbits));
            }

            uint freq = _freq[histo][tok];
            if ((state >> (32 - LogTab)) >= freq)
            {
                chunks.Add((state & 0xFFFF, 16));
                state >>= 16;
            }

            state = ((state / freq) << LogTab) + _reverse[histo][_start[histo][tok] + (state % freq)];
        }

        w.Write(state, 32);
        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            w.Write(chunks[i].Bits, chunks[i].Count);
        }
    }

    private static void WriteLz77Header(IJxlBitSink w, bool enabled)
    {
        w.Write(enabled ? 1UL : 0, 1);
        if (!enabled)
        {
            return;
        }

        // min_symbol 224 and min_length 3 are each the first of their U32 distributions; the length configuration is 0,0,0.
        w.Write(0, 2);
        w.Write(0, 2);
        WriteConfig(w, Lz77LengthConfig, 8);
    }

    private static void WriteConfig(IJxlBitSink w, JxlUintConfig c, int logAlphabet)
    {
        w.Write((ulong)c.SplitExponent, CeilLog2(logAlphabet + 1));
        if (c.SplitExponent == logAlphabet)
        {
            return;
        }

        w.Write((ulong)c.MsbInToken, CeilLog2(c.SplitExponent + 1));
        w.Write((ulong)c.LsbInToken, CeilLog2(c.SplitExponent - c.MsbInToken + 1));
    }

    private static int CeilLog2(int x) => x <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(x - 1));

    /// <summary>Writes the context map (C.2.2): a few bits each, or entropy coded with or without move-to-front, whichever is smallest.</summary>
    private static void WriteContextMap(IJxlBitSink w, byte[] map, int histograms)
    {
        if (histograms == 1)
        {
            w.Write(1, 1);
            w.Write(0, 2);
            return;
        }

        byte[] mtf = MoveToFront(map);
        JxlHistogramParams p = new() { UintMethod = JxlHistogramParams.UintKind.ContextMap };
        long plainCost = CodedCost(p, map);
        long mtfCost = CodedCost(p, mtf);
        bool useMtf = mtfCost < plainCost;
        int entryBits = CeilLog2(histograms);
        long simpleCost = (long)entryBits * map.Length;
        if (entryBits < 4 && simpleCost < plainCost && simpleCost < mtfCost)
        {
            w.Write(1, 1);
            w.Write((ulong)entryBits, 2);
            foreach (byte e in map)
            {
                w.Write(e, entryBits);
            }

            return;
        }

        w.Write(0, 1);
        w.Write(useMtf ? 1UL : 0, 1);
        List<List<JxlToken>> tokens = [[.. (useMtf ? mtf : map).Select(v => new JxlToken(0, v))]];
        JxlEntropyCode code = Write(w, p, 1, tokens);
        code.WriteTokens(w, tokens[0]);
    }

    private static long CodedCost(JxlHistogramParams p, byte[] values)
    {
        List<List<JxlToken>> tokens = [[.. values.Select(v => new JxlToken(0, v))]];
        Build(new JxlBitCounter(), p, 1, tokens, out long cost);
        return cost;
    }

    private static byte[] MoveToFront(byte[] v)
    {
        if (v.Length == 0)
        {
            return v;
        }

        List<byte> order = [.. Enumerable.Range(0, v.Max() + 1).Select(i => (byte)i)];
        byte[] result = new byte[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            int index = order.IndexOf(v[i]);
            result[i] = (byte)index;
            order.RemoveAt(index);
            order.Insert(0, v[i]);
        }

        return result;
    }

    /// <summary>
    /// Replaces runs of a repeated value by LZ77 copies at distance 1 where that saves bits (libjxl's ApplyLZ77_RLE), or
    /// gives <see langword="null"/> when the saving is too small to switch LZ77 on.
    /// </summary>
    private static List<List<JxlToken>>? ApplyRle(JxlHistogramParams p, int contexts, List<List<JxlToken>> streams)
    {
        JxlHistogram[] builder = new JxlHistogram[contexts];
        for (int i = 0; i < contexts; i++)
        {
            builder[i] = new JxlHistogram();
        }

        JxlUintConfig config = Config(4, 2, 0);
        foreach (List<JxlToken> stream in streams)
        {
            foreach (JxlToken t in stream)
            {
                builder[t.Context].Add(Split(config, t.Value).Token);
            }
        }

        float[][] bits = new float[contexts][];
        for (int i = 0; i < contexts; i++)
        {
            float inv = 1f / (builder[i].Total + 1e-8f);
            bits[i] = new float[builder[i].Counts.Length];
            for (int j = 0; j < bits[i].Length; j++)
            {
                int cnt = builder[i].Counts[j];
                bits[i][j] = cnt != 0 && cnt != builder[i].Total ? -MathF.Log2(cnt * inv) : cnt == 0 ? LogTab : 0;
            }
        }

        List<List<JxlToken>> output = new(streams.Count);
        float decrease = 0;
        long total = 0;
        for (int s = 0; s < streams.Count; s++)
        {
            int multiplier = p.ImageWidths.Count > s ? p.ImageWidths[s] : 0;
            List<JxlToken> input = streams[s];
            List<JxlToken> o = new(input.Count);
            total += input.Count;
            float[] cumulative = new float[input.Count + 1];
            for (int i = 0; i < input.Count; i++)
            {
                (uint tok, int nbits, _) = Split(config, input[i].Value);
                cumulative[i + 1] = bits[input[i].Context][tok] + nbits + cumulative[i];
            }

            for (int i = 0; i < input.Count; i++)
            {
                int copy = 0;
                if (i > 0)
                {
                    while (i + copy < input.Count && input[i + copy].Value == input[i - 1].Value)
                    {
                        copy++;
                    }
                }

                if (copy == 0)
                {
                    o.Add(input[i]);
                    continue;
                }

                float cost = cumulative[i + copy] - cumulative[i];
                uint length = unchecked((uint)copy - Lz77MinLength);
                float lzCost = copy >= Lz77MinLength ? CeilLog2((int)length + 1) + 1 : 0;
                if (copy < Lz77MinLength || cost <= lzCost)
                {
                    for (int j = 0; j < copy; j++)
                    {
                        o.Add(input[i + j]);
                    }

                    i += copy - 1;
                    continue;
                }

                o.Add(new JxlToken(input[i].Context, length, true));
                i += copy - 1;
                decrease += cost - lzCost;

                // Distance 1: the special distance (1, 0) when the stream has a width, else the plain distance code 0.
                o.Add(new JxlToken(contexts, multiplier != 0 ? 1u : 0u));
            }

            output.Add(o);
        }

        return decrease > (total * 0.2) + 16 ? output : null;
    }

    /// <summary>
    /// Clusters the contexts' histograms (libjxl's FastClusterHistograms, then for the best setting the pairwise merges that
    /// lower the ANS cost), numbered in order of first use.
    /// </summary>
    private static List<JxlHistogram> ClusterHistograms(JxlHistogramParams p, JxlHistogram[] input, int maxHistograms, out int[] symbols)
    {
        maxHistograms = Math.Min(Math.Min(maxHistograms, p.MaxHistograms), input.Length);
        List<JxlHistogram> output = new(maxHistograms);
        symbols = new int[input.Length];
        Array.Fill(symbols, maxHistograms);
        float[] dists = new float[input.Length];
        Array.Fill(dists, float.MaxValue);
        int largest = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i].Total == 0)
            {
                symbols[i] = 0;
                dists[i] = 0;
                continue;
            }

            input[i].ShannonEntropy();
            if (input[i].Total > input[largest].Total)
            {
                largest = i;
            }
        }

        const float MinDistanceForDistinct = 48;
        while (output.Count < maxHistograms)
        {
            symbols[largest] = output.Count;
            output.Add(input[largest].Clone());
            dists[largest] = 0;
            largest = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (dists[i] == 0)
                {
                    continue;
                }

                dists[i] = Math.Min(Distance(input[i], output[^1]), dists[i]);
                if (dists[i] > dists[largest])
                {
                    largest = i;
                }
            }

            if (dists[largest] < MinDistanceForDistinct)
            {
                break;
            }
        }

        for (int i = 0; i < input.Length; i++)
        {
            if (symbols[i] != maxHistograms)
            {
                continue;
            }

            int best = 0;
            float bestDist = float.MaxValue;
            for (int j = 0; j < output.Count; j++)
            {
                float d = Distance(input[i], output[j]);
                if (d < bestDist)
                {
                    (best, bestDist) = (j, d);
                }
            }

            output[best].AddHistogram(input[i]);
            output[best].ShannonEntropy();
            symbols[i] = best;
        }

        if (p.Clustering == JxlHistogramParams.ClusteringKind.Best)
        {
            MergePairs(output, symbols);
        }

        // Canonical numbering: clusters in order of first use.
        Dictionary<int, int> index = [];
        List<JxlHistogram> ordered = [];
        foreach (int s in symbols)
        {
            if (!index.ContainsKey(s))
            {
                index[s] = ordered.Count;
                ordered.Add(output[s]);
            }
        }

        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = index[symbols[i]];
        }

        return ordered;
    }

    private static void MergePairs(List<JxlHistogram> output, int[] symbols)
    {
        foreach (JxlHistogram h in output)
        {
            h.Entropy = h.AnsPopulationCost();
        }

        uint next = 2;
        uint[] version = [.. Enumerable.Repeat(1u, output.Count)];
        int[] renumber = [.. Enumerable.Range(0, output.Count)];
        PriorityQueue<(int First, int Second, uint Version), (float, int, int, uint)> pairs = new();
        void Push(int a, int b)
        {
            JxlHistogram merged = output[a].Clone();
            merged.AddHistogram(output[b]);
            float cost = merged.AnsPopulationCost() - output[a].Entropy - output[b].Entropy;
            if (cost < 0)
            {
                uint v = Math.Max(version[a], version[b]);
                pairs.Enqueue((a, b, v), (cost, a, b, v));
            }
        }

        for (int i = 0; i < output.Count; i++)
        {
            for (int j = i + 1; j < output.Count; j++)
            {
                Push(i, j);
            }
        }

        while (pairs.TryDequeue(out (int First, int Second, uint Version) top, out _))
        {
            (int first, int second, uint ver) = top;
            if (ver != Math.Max(version[first], version[second]) || version[first] == 0 || version[second] == 0)
            {
                continue;
            }

            output[first].AddHistogram(output[second]);
            output[first].Entropy = output[first].AnsPopulationCost();
            for (int i = 0; i < renumber.Length; i++)
            {
                if (renumber[i] == second)
                {
                    renumber[i] = first;
                }
            }

            version[second] = 0;
            version[first] = next++;
            for (int j = 0; j < output.Count; j++)
            {
                if (j != first && version[j] != 0)
                {
                    Push(Math.Min(first, j), Math.Max(first, j));
                }
            }
        }

        int[] reverse = new int[output.Count];
        List<JxlHistogram> alive = [];
        for (int i = 0; i < output.Count; i++)
        {
            if (version[i] != 0)
            {
                reverse[i] = alive.Count;
                alive.Add(output[i]);
            }
        }

        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = reverse[renumber[symbols[i]]];
        }

        output.Clear();
        output.AddRange(alive);
    }

    /// <summary>Gets how many bits merging two histograms costs over coding each with its own (libjxl's HistogramDistance).</summary>
    private static float Distance(JxlHistogram a, JxlHistogram b)
    {
        if (a.Total == 0 || b.Total == 0)
        {
            return 0;
        }

        return JxlHistogram.EntropyOf(a.Counts, b.Counts, a.Total + b.Total) - a.Entropy - b.Entropy;
    }

    /// <summary>
    /// For each symbol and each of its slots, the ANS state index that decodes to it (the decoder's alias table, inverted):
    /// symbol s's slots start at the sum of the frequencies below it.
    /// </summary>
    private static (ushort[] Reverse, ushort[] Start) ReverseMap(ushort[] dist, int logAlphabet)
    {
        int logBucket = LogTab - logAlphabet;
        int bucket = 1 << logBucket;
        int size = 1 << logAlphabet;
        ushort[] start = new ushort[size + 1];
        for (int i = 0; i < size; i++)
        {
            start[i + 1] = (ushort)Math.Min(TabSize, start[i] + dist[i]);
        }

        ushort[] reverse = new ushort[TabSize];
        int single = Array.IndexOf(dist, (ushort)TabSize);
        if (single >= 0)
        {
            for (int s = 0; s < TabSize; s++)
            {
                reverse[s] = (ushort)s;
            }

            return (reverse, start);
        }

        int alphabet = 0;
        for (int i = 0; i < size; i++)
        {
            if (dist[i] != 0)
            {
                alphabet = i + 1;
            }
        }

        // The same Vose alias table JxlAnsHistogram builds.
        int[] cut = new int[size];
        int[] sym = new int[size];
        int[] off = new int[size];
        Stack<int> underfull = new();
        Stack<int> overfull = new();
        for (int i = 0; i < size; i++)
        {
            cut[i] = dist[i];
            sym[i] = i < alphabet ? i : 0;
            if (cut[i] < bucket)
            {
                underfull.Push(i);
            }
            else if (cut[i] > bucket)
            {
                overfull.Push(i);
            }
        }

        while (overfull.Count > 0 && underfull.Count > 0)
        {
            int o = overfull.Pop();
            int u = underfull.Pop();
            cut[o] -= bucket - cut[u];
            sym[u] = o;
            off[u] = cut[o];
            if (cut[o] < bucket)
            {
                underfull.Push(o);
            }
            else if (cut[o] > bucket)
            {
                overfull.Push(o);
            }
        }

        for (int index = 0; index < TabSize; index++)
        {
            int i = index >> logBucket;
            int pos = index & (bucket - 1);
            bool own = cut[i] == bucket || pos < cut[i];
            int symbol = own ? i : sym[i];
            int offset = own ? pos : off[i] - cut[i] + pos;
            reverse[start[symbol] + offset] = (ushort)index;
        }

        return (reverse, start);
    }

    /// <summary>Clusters, writes the context map, chooses each cluster's configuration and writes the distributions.</summary>
    private long BuildCodes(IJxlBitSink w, JxlHistogramParams p, List<List<JxlToken>> streams, JxlHistogram[] builder)
    {
        long cost = 0;
        List<JxlHistogram> clustered;
        if (builder.Length > 1)
        {
            clustered = ClusterHistograms(p, builder, ClustersLimit, out int[] symbols);
            _contextMap = [.. symbols.Select(s => (byte)s)];
            JxlBitCounter mapCost = new();
            WriteContextMap(mapCost, _contextMap, clustered.Count);
            cost += mapCost.Bits;
            WriteContextMap(w, _contextMap, clustered.Count);
        }
        else
        {
            clustered = [builder[0]];
            _contextMap = [0];
        }

        int logAlpha = ChooseUintConfigs(p, streams, clustered);
        cost += 3;
        w.Write(0, 1); // ANS, not prefix codes
        w.Write((ulong)(logAlpha - 5), 2);
        JxlBitCounter configCost = new();
        foreach (JxlUintConfig c in _configs)
        {
            WriteConfig(w, c, logAlpha);
            WriteConfig(configCost, c, logAlpha);
        }

        cost += configCost.Bits;
        _freq = new ushort[clustered.Count][];
        _reverse = new ushort[clustered.Count][];
        _start = new ushort[clustered.Count][];
        for (int c = 0; c < clustered.Count; c++)
        {
            JxlAnsEncodingHistogram normalized = JxlAnsEncodingHistogram.ComputeBest(clustered[c], p.AnsStrategy);
            normalized.Encode(w);
            cost += (long)MathF.Ceiling(normalized.Cost);
            ushort[] freq = new ushort[1 << logAlpha];
            int alphabet = clustered[c].AlphabetSize();
            if (alphabet == 0)
            {
                // An empty histogram is written as the single symbol 0.
                freq[0] = TabSize;
            }

            for (int s = 0; s < alphabet; s++)
            {
                freq[s] = (ushort)normalized.Counts[s];
            }

            _freq[c] = freq;
            (_reverse[c], _start[c]) = ReverseMap(freq, logAlpha);
        }

        return cost;
    }

    /// <summary>
    /// Picks a hybrid integer configuration for each cluster (libjxl's ChooseUintConfigs) and the smallest alphabet that
    /// holds every token; returns the log alphabet size.
    /// </summary>
    private int ChooseUintConfigs(JxlHistogramParams p, List<List<JxlToken>> streams, List<JxlHistogram> clustered)
    {
        int count = clustered.Count;
        _configs = [.. Enumerable.Repeat(p.UintConfig, count)];
        if (p.UintMethod is JxlHistogramParams.UintKind.Fast or JxlHistogramParams.UintKind.Best)
        {
            JxlUintConfig[] candidates = p.UintMethod == JxlHistogramParams.UintKind.Best
                ? [Config(4, 2, 0), Config(4, 1, 0), Config(4, 2, 1), Config(4, 2, 2), Config(4, 1, 2), Config(5, 2, 0), Config(5, 1, 0), Config(5, 2, 1), Config(5, 2, 2), Config(5, 1, 2),
                    Config(3, 2, 0), Config(3, 1, 0), Config(3, 2, 1), Config(3, 1, 2), Config(4, 1, 3), Config(5, 1, 4), Config(5, 2, 3), Config(6, 1, 5), Config(6, 2, 4), Config(6, 0, 0),
                    Config(0, 0, 0), Config(2, 0, 1), Config(7, 0, 0), Config(8, 0, 0), Config(9, 0, 0), Config(10, 0, 0), Config(11, 0, 0), Config(12, 0, 0)]
                : [Config(4, 2, 0), Config(4, 1, 2), Config(0, 0, 0), Config(2, 0, 1)];

            List<uint>[] values = new List<uint>[count];
            for (int h = 0; h < count; h++)
            {
                values[h] = [];
            }

            foreach (List<JxlToken> stream in streams)
            {
                foreach (JxlToken t in stream)
                {
                    if (!t.IsLz77Length)
                    {
                        values[_contextMap[t.Context]].Add(t.Value);
                    }
                }
            }

            for (int h = 0; h < count; h++)
            {
                uint max = values[h].Count == 0 ? 0 : values[h].Max();
                float best = float.MaxValue;
                foreach (JxlUintConfig cfg in candidates)
                {
                    (uint maxTok, _, _) = Split(cfg, max);
                    maxTok |= (1u << cfg.LsbInToken) - 1;
                    if (maxTok >= 256 || (_lz77 && maxTok >= Lz77MinSymbol))
                    {
                        continue;
                    }

                    JxlHistogram histo = new();
                    long extra = 0;
                    foreach (uint v in values[h])
                    {
                        (uint tok, int nbits, _) = Split(cfg, v);
                        histo.Add(tok);
                        extra += nbits;
                    }

                    float cost = histo.AnsPopulationCost() + extra + CeilLog2(cfg.SplitExponent + 1) + CeilLog2(cfg.SplitExponent - cfg.MsbInToken + 1);
                    if (cost < best)
                    {
                        _configs[h] = cfg;
                        best = cost;
                        clustered[h] = histo;
                    }
                }
            }

            // The LZ77 lengths keep their place above the literal tokens.
            foreach (List<JxlToken> stream in streams)
            {
                foreach (JxlToken t in stream)
                {
                    if (t.IsLz77Length)
                    {
                        clustered[_contextMap[t.Context]].Add(Split(Lz77LengthConfig, t.Value).Token + Lz77MinSymbol);
                    }
                }
            }
        }

        int maxSymbol = 0;
        foreach (JxlHistogram h in clustered)
        {
            maxSymbol = Math.Max(maxSymbol, h.AlphabetSize() - 1);
        }

        int log = 5;
        while (maxSymbol >= 1 << log)
        {
            log++;
        }

        return log;
    }
}
