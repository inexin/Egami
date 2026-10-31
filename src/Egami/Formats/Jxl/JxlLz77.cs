namespace Egami.Formats.Jxl;

/// <summary>
/// LZ77 for the entropy coder at efforts 8 and up (libjxl 0.12.0's enc_lz77, translated; see THIRD-PARTY-NOTICES): a hash
/// chain over each stream's token values finds earlier runs to copy, the greedy parse with one step of lazy matching
/// (ApplyLZ77_LZ77) at effort 8, and at 9 and up the cheapest parse by dynamic programming (ApplyLZ77_Optimal), its costs
/// estimated from the greedy parse. Either gives <see langword="null"/> when LZ77 would not pay.
/// </summary>
internal static class JxlLz77
{
    private const int WindowSize = 1 << 20;
    private const int MinLength = 3;
    private const uint MinSymbol = 224;
    private const int LogTab = 12;

    private static readonly JxlUintConfig DefaultConfig = JxlEntropyCode.Config(4, 2, 0);
    private static readonly JxlUintConfig LengthConfig = JxlEntropyCode.Config(0, 0, 0);

    // libjxl's fitted bit costs of a length (hybrid uint 1, 0, 0) and a distance symbol (7, 0, 0) token.
    private static readonly float[] LenCostTable =
    [
        2.797667318563126f, 3.213177690381199f, 2.5706009246743737f, 2.408392498667534f, 2.829649191872326f, 3.3923087753324577f,
        4.029267451554331f, 4.415576699706408f, 4.509357574741465f, 9.21481543803004f, 10.020590190114898f, 11.858671627804766f,
        12.45853300490526f, 11.713105831990857f, 12.561996324849314f, 13.775477692278367f, 13.174027068768641f,
    ];

    private static readonly float[] DistCostTable =
    [
        6.368282626312716f, 5.680793277090298f, 8.347404197105247f, 7.641619201599141f, 6.914328374119438f, 7.959808291537444f,
        8.70023120759855f, 8.71378518934703f, 9.379132523982769f, 9.110472749092708f, 9.159029569270908f, 9.430936766731973f,
        7.278284055315169f, 7.8278514904267755f, 10.026641158289236f, 9.976049229827066f, 9.64351607048908f, 9.563403863480442f,
        10.171474111762747f, 10.45950155077234f, 9.994813912104219f, 10.322524683741156f, 8.465808729388186f, 8.756254166066853f,
        10.160930174662234f, 10.247329273413435f, 10.04090403724809f, 10.129398517544082f, 9.342311691539546f, 9.07608009102374f,
        10.104799540677513f, 10.378079384990906f, 10.165828974075072f, 10.337595322341553f, 7.940557464567944f, 10.575665823319431f,
        11.023344321751955f, 10.736144698831827f, 11.118277044595054f, 7.468468230648442f, 10.738305230932939f, 10.906980780216568f,
        10.163468216353817f, 10.17805759656433f, 11.167283670483565f, 11.147050200274544f, 10.517921919244333f, 10.651764778156886f,
        10.17074446448919f, 11.217636876224745f, 11.261630721139484f, 11.403140815247259f, 10.892472096873417f, 11.1859607804481f,
        8.017346947551262f, 7.895143720278828f, 11.036577113822025f, 11.170562110315794f, 10.326988722591086f, 10.40872184751056f,
        11.213498225466386f, 11.30580635516863f, 10.672272515665442f, 10.768069466228063f, 11.145257364153565f, 11.64668307145549f,
        10.593156194627339f, 11.207499484844943f, 10.767517766396908f, 10.826629811407042f, 10.737764794499988f, 10.6200448518045f,
        10.191315385198092f, 8.468384171390085f, 11.731295299170432f, 11.824619886654398f, 10.41518844301179f, 10.16310536548649f,
        10.539423685097576f, 10.495136599328031f, 10.469112847728267f, 11.72057686174922f, 10.910326337834674f, 11.378921834673758f,
        11.847759036098536f, 11.92071647623854f, 10.810628276345282f, 11.008601085273893f, 11.910326337834674f, 11.949212023423133f,
        11.298614839104337f, 11.611603659010392f, 10.472930394619985f, 11.835564720850282f, 11.523267392285337f, 12.01055816679611f,
        8.413029688994023f, 11.895784139536406f, 11.984679534970505f, 11.220654278717394f, 11.716311684833672f, 10.61036646226114f,
        10.89849965960364f, 10.203762898863669f, 10.997560826267238f, 11.484217379438984f, 11.792836176993665f, 12.24310468755171f,
        11.464858097919262f, 12.212747017409377f, 11.425595666074955f, 11.572048533398757f, 12.742093965163013f, 11.381874288645637f,
        12.191870445817015f, 11.683156920035426f, 11.152442115262197f, 11.90303691580457f, 11.653292787169159f, 11.938615382266098f,
        16.970641701570223f, 16.853602280380002f, 17.26240782594733f, 16.644655390108507f, 17.14310889757499f, 16.910935455445955f,
        17.505678976959697f, 17.213498225466388f, 2.4162310293553024f, 3.494587244462329f, 3.5258600986408344f, 3.4959806589517095f,
        3.098390886949687f, 3.343454654302911f, 3.588847442290287f, 4.14614790111827f, 5.152948641990529f, 7.433696808092598f,
        9.716311684833672f,
    ];

    /// <summary>The greedy parse with lazy matching (effort 8), or <see langword="null"/> when it saves too little.</summary>
    internal static List<List<JxlToken>>? Greedy(JxlHistogramParams p, int contexts, List<List<JxlToken>> streams)
    {
        SymbolCost sce = new(contexts, streams);
        float decrease = 0;
        long total = 0;
        List<List<JxlToken>> output = new(streams.Count);
        for (int s = 0; s < streams.Count; s++)
        {
            int multiplier = p.ImageWidths.Count > s ? p.ImageWidths[s] : 0;
            List<JxlToken> input = streams[s];
            total += input.Count;
            float[] cumulative = Cumulative(sce, input);
            List<JxlToken> o = new(input.Count);
            HashChain chain = new(input, Window(input.Count), MinLength, input.Count, multiplier);
            bool updated = false;
            for (int i = 0; i < input.Count; i++)
            {
                o.Add(input[i]);
                if (!updated)
                {
                    chain.Update(i);
                }

                updated = false;
                (int dist, int len) = chain.FindMatch(i);
                if (len < MinLength)
                {
                    continue;
                }

                if (len < 256 && i + 1 < input.Count)
                {
                    // Lazy matching: a longer match from the next symbol wins, after this one as a literal.
                    chain.Update(i + 1);
                    updated = true;
                    (int dist2, int len2) = chain.FindMatch(i + 1);
                    if (len2 > len)
                    {
                        i++;
                        updated = false;
                        (len, dist) = (len2, dist2);
                        o.Add(input[i]);
                    }
                }

                float cost = cumulative[i + len] - cumulative[i];
                float lzCost = LenCost(len - MinLength) + DistCost(dist) + sce.AddSymbolCost(o[^1].Context);
                if (lzCost <= cost)
                {
                    o[^1] = new JxlToken(o[^1].Context, (uint)(len - MinLength), true);
                    o.Add(new JxlToken(contexts, (uint)dist));
                    decrease += cost - lzCost;
                }
                else
                {
                    for (int j = 1; j < len; j++)
                    {
                        o.Add(input[i + j]);
                    }
                }

                if (updated)
                {
                    chain.Update(i + 2, len - 2);
                    updated = false;
                }
                else
                {
                    chain.Update(i + 1, len - 1);
                }

                i += len - 1;
            }

            output.Add(o);
        }

        return decrease > (total * 0.2) + 16 ? output : null;
    }

    /// <summary>The cheapest parse (efforts 9 and up), costed from the greedy one, or <see langword="null"/> when that saves too little.</summary>
    internal static List<List<JxlToken>>? Optimal(JxlHistogramParams p, int contexts, List<List<JxlToken>> streams)
    {
        List<List<JxlToken>>? estimate = Greedy(p, contexts, streams);
        if (estimate is null)
        {
            return null;
        }

        SymbolCost sce = new(contexts + 1, estimate);
        List<List<JxlToken>> output = new(streams.Count);
        List<int> distSymbols = [];
        for (int s = 0; s < streams.Count; s++)
        {
            int multiplier = p.ImageWidths.Count > s ? p.ImageWidths[s] : 0;
            List<JxlToken> input = streams[s];
            float[] cumulative = Cumulative(sce, input);
            HashChain chain = new(input, Window(input.Count), MinLength, input.Count, multiplier);
            int n = input.Count;
            int[] len = new int[n + 1];
            int[] dist = new int[n + 1];
            int[] ctx = new int[n + 1];
            float[] totalCost = new float[n + 1];
            Array.Fill(totalCost, float.MaxValue);
            totalCost[0] = 0;
            int rle = 0, skip = 0;
            for (int i = 0; i < n; i++)
            {
                chain.Update(i);
                float lit = totalCost[i] + cumulative[i + 1] - cumulative[i];
                if (totalCost[i + 1] > lit)
                {
                    (dist[i + 1], len[i + 1], ctx[i + 1], totalCost[i + 1]) = (0, 1, input[i].Context, lit);
                }

                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                distSymbols.Clear();
                chain.FindMatches(i, (l, d) =>
                {
                    while (distSymbols.Count <= l)
                    {
                        distSymbols.Add(d);
                    }

                    if (d < distSymbols[l])
                    {
                        distSymbols[l] = d;
                    }
                });
                if (distSymbols.Count <= MinLength)
                {
                    continue;
                }

                int best = distSymbols[^1];
                for (int j = distSymbols.Count - 1; j >= MinLength; j--)
                {
                    best = Math.Min(best, distSymbols[j]);
                    distSymbols[j] = best;
                }

                for (int j = MinLength; j < distSymbols.Count; j++)
                {
                    float cost = totalCost[i] + sce.LenCost(input[i].Context, j - MinLength) + sce.DistCost(contexts, distSymbols[j]);
                    if (totalCost[i + j] > cost)
                    {
                        (len[i + j], dist[i + j], ctx[i + j], totalCost[i + j]) = (j, distSymbols[j] + 1, input[i].Context, cost);
                    }
                }

                // In a run of one symbol only the first and last 8 positions are tried, so long runs stay linear.
                rle = (distSymbols[^1] == 0 && multiplier == 0) || (distSymbols[^1] == 1 && multiplier != 0) ? rle + 1 : 0;
                if (rle >= 8 && distSymbols.Count > 9)
                {
                    skip = distSymbols.Count - 10;
                    rle = 0;
                }
            }

            List<JxlToken> o = new(n);
            int pos = n;
            while (pos > 0)
            {
                bool copy = dist[pos] != 0;
                if (copy)
                {
                    o.Add(new JxlToken(contexts, (uint)(dist[pos] - 1)));
                }

                o.Add(copy ? new JxlToken(ctx[pos], (uint)(len[pos] - MinLength), true) : new JxlToken(ctx[pos], input[pos - 1].Value));
                pos -= len[pos];
            }

            o.Reverse();
            output.Add(o);
        }

        return output;
    }

    private static int Window(int count)
    {
        int w = 1;
        while (w < count && w < WindowSize)
        {
            w <<= 1;
        }

        return w;
    }

    private static float[] Cumulative(SymbolCost sce, List<JxlToken> input)
    {
        float[] cumulative = new float[input.Count + 1];
        for (int i = 0; i < input.Count; i++)
        {
            (uint tok, int nbits, _) = JxlEntropyCode.Split(DefaultConfig, input[i].Value);
            cumulative[i + 1] = sce.Bits(input[i].Context, tok) + nbits + cumulative[i];
        }

        return cumulative;
    }

    private static float LenCost(int len)
    {
        (uint tok, int nbits, _) = JxlEntropyCode.Split(JxlEntropyCode.Config(1, 0, 0), (uint)len);
        return LenCostTable[Math.Min((int)tok, LenCostTable.Length - 1)] + nbits;
    }

    private static float DistCost(int dist)
    {
        (uint tok, int nbits, _) = JxlEntropyCode.Split(JxlEntropyCode.Config(7, 0, 0), (uint)dist);
        return DistCostTable[Math.Min((int)tok, DistCostTable.Length - 1)] + nbits;
    }

    /// <summary>Estimates each symbol's cost per context from the streams' own counts (SymbolCostEstimator).</summary>
    private sealed class SymbolCost
    {
        private readonly float[][] _bits;
        private readonly float[] _addSymbol;

        internal SymbolCost(int contexts, List<List<JxlToken>> streams)
        {
            JxlHistogram[] builder = new JxlHistogram[contexts];
            for (int i = 0; i < contexts; i++)
            {
                builder[i] = new JxlHistogram();
            }

            foreach (List<JxlToken> stream in streams)
            {
                foreach (JxlToken t in stream)
                {
                    (uint tok, _, _) = JxlEntropyCode.Split(t.IsLz77Length ? LengthConfig : DefaultConfig, t.Value);
                    builder[t.Context].Add(t.IsLz77Length ? tok + MinSymbol : tok);
                }
            }

            int alphabet = builder.Max(b => b.Counts.Length);
            _bits = new float[contexts][];
            _addSymbol = new float[contexts];
            for (int i = 0; i < contexts; i++)
            {
                float inv = 1f / (builder[i].Total + 1e-8f);
                float total = 0;
                _bits[i] = new float[alphabet];
                for (int j = 0; j < alphabet; j++)
                {
                    int cnt = j < builder[i].Counts.Length ? builder[i].Counts[j] : 0;
                    float cost = cnt != 0 && cnt != builder[i].Total ? -MathF.Log2(cnt * inv) : cnt == 0 ? LogTab : 0;
                    _bits[i][j] = cost;
                    total += cost * cnt;
                }

                // A context of low entropy loses more by taking LZ77 symbols too.
                _addSymbol[i] = MathF.Max(0, 6 - (total * inv));
            }
        }

        internal float Bits(int ctx, uint sym) => sym < _bits[ctx].Length ? _bits[ctx][sym] : LogTab;

        internal float AddSymbolCost(int ctx) => _addSymbol[ctx];

        internal float LenCost(int ctx, int len)
        {
            (uint tok, int nbits, _) = JxlEntropyCode.Split(LengthConfig, (uint)len);
            return nbits + Bits(ctx, tok + MinSymbol);
        }

        internal float DistCost(int distanceContext, int dist)
        {
            (uint tok, int nbits, _) = JxlEntropyCode.Split(DefaultConfig, (uint)dist);
            return nbits + Bits(distanceContext, tok);
        }
    }

    /// <summary>libjxl's hash chain over a stream's values: three values hashed, with a separate chain for runs of zeros.</summary>
    private sealed class HashChain
    {
        private const int HashValues = 32768;
        private const int HashMask = HashValues - 1;
        private const int HashShift = 5;
        private const int MaxChainLength = 256;
        private const int NumSpecialDistances = 120;

        private readonly uint[] _data;
        private readonly int _size;
        private readonly int _windowMask;
        private readonly int _minLength;
        private readonly int _maxLength;
        private readonly int[] _head = new int[HashValues];
        private readonly int[] _chain;
        private readonly int[] _val;
        private readonly int[] _headz;
        private readonly int[] _chainz;
        private readonly int[] _zeros;
        private readonly Dictionary<int, int> _special = [];
        private readonly int _numSpecial;
        private int _numZeros;

        internal HashChain(List<JxlToken> tokens, int window, int minLength, int maxLength, int multiplier)
        {
            _size = tokens.Count;
            _data = new uint[_size];
            for (int i = 0; i < _size; i++)
            {
                _data[i] = tokens[i].Value;
            }

            _windowMask = window - 1;
            _minLength = minLength;
            _maxLength = maxLength;
            Array.Fill(_head, -1);
            _val = new int[window];
            Array.Fill(_val, -1);
            _chain = new int[window];
            _chainz = new int[window];
            for (int i = 0; i < window; i++)
            {
                _chain[i] = i;
                _chainz[i] = i;
            }

            _zeros = new int[window];
            _headz = new int[window + 1];
            Array.Fill(_headz, -1);
            if (multiplier != 0)
            {
                // Counted down, so that when distances coincide the smallest code wins.
                for (int i = NumSpecialDistances - 1; i >= 0; i--)
                {
                    _special[JxlEntropyDecoder.SpecialDistance(i, multiplier)] = i;
                }

                _numSpecial = NumSpecialDistances;
            }
        }

        private int Hash(int pos) => pos + 2 < _size
            ? (int)((_data[pos] ^ (_data[pos + 1] << HashShift) ^ (_data[pos + 2] << (HashShift * 2))) & HashMask)
            : 0;

        private int CountZeros(int pos, int previous)
        {
            int end = Math.Min(pos + _windowMask + 1, _size);
            if (previous > 0)
            {
                return previous >= _windowMask && _data[end - 1] == 0 && end == pos + _windowMask + 1 ? previous : previous - 1;
            }

            int n = 0;
            while (pos + n < end && _data[pos + n] == 0)
            {
                n++;
            }

            return n;
        }

        internal void Update(int pos)
        {
            int hash = Hash(pos);
            int wpos = pos & _windowMask;
            _val[wpos] = hash;
            if (_head[hash] != -1)
            {
                _chain[wpos] = _head[hash];
            }

            _head[hash] = wpos;
            if (pos > 0 && _data[pos] != _data[pos - 1])
            {
                _numZeros = 0;
            }

            _numZeros = CountZeros(pos, _numZeros);
            _zeros[wpos] = _numZeros;
            if (_headz[_numZeros] != -1)
            {
                _chainz[wpos] = _headz[_numZeros];
            }

            _headz[_numZeros] = wpos;
        }

        internal void Update(int pos, int len)
        {
            for (int i = 0; i < len; i++)
            {
                Update(pos + i);
            }
        }

        internal void FindMatches(int pos, Action<int, int> found)
        {
            int wpos = pos & _windowMask;
            int hash = Hash(pos);
            int hashpos = _chain[wpos];
            int prev = 0;
            int end = Math.Min(pos + _maxLength, _size);
            int chainLength = 0;
            int bestLen = 0;
            while (true)
            {
                int dist = hashpos <= wpos ? wpos - hashpos : wpos - hashpos + _windowMask + 1;
                if (dist < prev)
                {
                    break;
                }

                prev = dist;
                int len = 0;
                if (dist > 0)
                {
                    int i = pos, j = pos - dist;
                    if (_numZeros > 3)
                    {
                        int r = Math.Min(_numZeros - 1, _zeros[hashpos]);
                        if (i + r >= end)
                        {
                            r = end - i - 1;
                        }

                        i += r;
                        j += r;
                    }

                    while (i < end && _data[i] == _data[j])
                    {
                        i++;
                        j++;
                    }

                    len = i - pos;
                    if (len >= _minLength && len + 2 >= bestLen)
                    {
                        int symbol = _special.TryGetValue(dist, out int code) ? code : _numSpecial + dist - 1;
                        found(len, symbol);
                        bestLen = Math.Max(bestLen, len);
                    }
                }

                if (++chainLength >= MaxChainLength)
                {
                    break;
                }

                if (_numZeros >= 3 && len > _numZeros)
                {
                    if (hashpos == _chainz[hashpos])
                    {
                        break;
                    }

                    hashpos = _chainz[hashpos];
                    if (_zeros[hashpos] != _numZeros)
                    {
                        break;
                    }
                }
                else
                {
                    if (hashpos == _chain[hashpos])
                    {
                        break;
                    }

                    hashpos = _chain[hashpos];
                    if (_val[hashpos] != hash)
                    {
                        break;
                    }
                }
            }
        }

        internal (int Dist, int Len) FindMatch(int pos)
        {
            int bestDist = 0, bestLen = 1;
            FindMatches(pos, (len, dist) =>
            {
                if (len > bestLen || (len == bestLen && bestDist > dist))
                {
                    (bestLen, bestDist) = (len, dist);
                }
            });
            return (bestDist, bestLen);
        }
    }
}
