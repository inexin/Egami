namespace Egami.Formats.Jxl;

/// <summary>
/// The entropy decoder of ISO/IEC 18181-1 Annex C: distributions in ANS (with alias tables) or prefix codes, context
/// clustering with optional move-to-front, hybrid unsigned integers, and the LZ77 copy mode with its special distances.
/// Translated from jxl-oxide's jxl-coding crate and checked against libjxl (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlEntropyDecoder
{
    private static readonly sbyte[] SpecialDistances =
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1,
        2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3, 3, 1, -3, 1, 2, 3, -2, 3,
        3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3,
        2, 4, -2, 4, 4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0,
        1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2, 4, 4, -4, 4,
        3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1,
        2, 6, -2, 6, 6, 2, -6, 2, 4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6,
        6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7,
        7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5, 8, 0, 4, 7, -4, 7, 7, 4,
        -7, 4, 8, 1, 8, 2, 6, 6, -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5,
        8, 4, 6, 7, -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    private readonly byte[] _clusters;
    private readonly JxlUintConfig[] _configs;
    private readonly JxlAnsHistogram[]? _ans;
    private readonly JxlPrefixHistogram[]? _prefix;

    // LZ77.
    private readonly bool _lz77;
    private readonly uint _minSymbol;
    private readonly uint _minLength;
    private readonly JxlUintConfig _lengthConfig;
    private uint[]? _window;
    private uint _numToCopy;
    private uint _copyPos;
    private uint _numDecoded;

    private uint _state;
    private bool _initial = true;

    private JxlEntropyDecoder(byte[] clusters, JxlUintConfig[] configs, JxlAnsHistogram[]? ans, JxlPrefixHistogram[]? prefix, bool lz77, uint minSymbol, uint minLength, JxlUintConfig lengthConfig)
    {
        _clusters = clusters;
        _configs = configs;
        _ans = ans;
        _prefix = prefix;
        _lz77 = lz77;
        _minSymbol = minSymbol;
        _minLength = minLength;
        _lengthConfig = lengthConfig;
    }

    /// <summary>Gets a decoder with the same distributions and a fresh state, for another stream.</summary>
    internal JxlEntropyDecoder Clone() => new(_clusters, _configs, _ans, _prefix, _lz77, _minSymbol, _minLength, _lengthConfig);

    /// <summary>Gets the cluster of each context.</summary>
    internal ReadOnlySpan<byte> Clusters => _clusters;

    /// <summary>Gets whether LZ77 copies may occur.</summary>
    internal bool UsesLz77 => _lz77;

    /// <summary>Reads the distributions for <paramref name="contexts"/> contexts (C.2).</summary>
    internal static JxlEntropyDecoder Read(JxlBitReader r, int contexts) => Read(r, contexts, allowLz77: true);

    private static JxlEntropyDecoder Read(JxlBitReader r, int contexts, bool allowLz77)
    {
        bool lz77 = r.ReadBool();
        uint minSymbol = 0;
        uint minLength = 0;
        JxlUintConfig lengthConfig = default;
        if (lz77)
        {
            if (!allowLz77)
            {
                throw r.Corrupt("LZ77 is not allowed in a small context map.");
            }

            minSymbol = r.ReadU32(new JxlU32(224, 0, 512, 0, 4096, 0, 8, 15));
            minLength = r.ReadU32(new JxlU32(3, 0, 4, 0, 5, 2, 9, 8));
            lengthConfig = JxlUintConfig.Read(r, 8);
            contexts++;
        }

        byte[] clusters = ReadClusters(r, contexts, out int count);
        bool prefixCodes = r.ReadBool();
        int logAlphabet = prefixCodes ? 15 : 5 + (int)r.ReadBits(2);
        JxlUintConfig[] configs = new JxlUintConfig[count];
        for (int i = 0; i < count; i++)
        {
            configs[i] = JxlUintConfig.Read(r, logAlphabet);
        }

        if (prefixCodes)
        {
            uint[] sizes = new uint[count];
            for (int i = 0; i < count; i++)
            {
                if (r.ReadBool())
                {
                    int n = (int)r.ReadBits(4);
                    sizes[i] = 1 + (1u << n) + r.ReadBits(n);
                    if (sizes[i] > 1 << 15)
                    {
                        throw r.Corrupt($"A prefix code has an alphabet of {sizes[i]} symbols; at most 32,768 are allowed.");
                    }
                }
                else
                {
                    sizes[i] = 1;
                }
            }

            JxlPrefixHistogram[] prefix = new JxlPrefixHistogram[count];
            for (int i = 0; i < count; i++)
            {
                prefix[i] = JxlPrefixHistogram.Read(r, sizes[i]);
            }

            return new JxlEntropyDecoder(clusters, configs, null, prefix, lz77, minSymbol, minLength, lengthConfig);
        }

        JxlAnsHistogram[] ans = new JxlAnsHistogram[count];
        for (int i = 0; i < count; i++)
        {
            ans[i] = JxlAnsHistogram.Read(r, logAlphabet);
        }

        return new JxlEntropyDecoder(clusters, configs, ans, null, lz77, minSymbol, minLength, lengthConfig);
    }

    /// <summary>Reads the initial ANS state; call before the first symbol of a stream.</summary>
    internal void Begin(JxlBitReader r)
    {
        if (_ans is not null)
        {
            _state = r.ReadBits(32);
        }

        _initial = false;
    }

    /// <summary>Checks the ANS final state at the end of a stream (C.3.2).</summary>
    internal void Finish(JxlBitReader r)
    {
        if (_ans is not null && _state != 0x130000)
        {
            throw r.Corrupt("An ANS stream does not end in its final state.");
        }
    }

    /// <summary>Gets the symbol a cluster always decodes to without reading bits, or -1 when it can vary.</summary>
    internal long SingleToken(int cluster)
    {
        if (_lz77)
        {
            return -1;
        }

        int symbol = _ans is not null ? _ans[cluster].SingleSymbol : _prefix![cluster].SingleSymbol;
        return symbol >= 0 && (uint)symbol < _configs[cluster].Split ? symbol : -1;
    }

    /// <summary>Decodes one integer in context <paramref name="context"/>.</summary>
    internal uint ReadUint(JxlBitReader r, int context) => ReadUintClustered(r, _clusters[context], 0);

    /// <summary>Decodes one integer with the cluster already looked up; <paramref name="distanceMultiplier"/> enables LZ77's special distances.</summary>
    internal uint ReadUintClustered(JxlBitReader r, int cluster, uint distanceMultiplier)
    {
        if (!_lz77)
        {
            return ReadUint(r, _configs[cluster], ReadSymbol(r, cluster));
        }

        uint value;
        if (_numToCopy > 0)
        {
            value = _window![_copyPos++ & 0xFFFFF];
            _numToCopy--;
        }
        else
        {
            uint token = ReadSymbol(r, cluster);
            if (token >= _minSymbol)
            {
                if (_numDecoded == 0)
                {
                    throw r.Corrupt("An LZ77 copy comes before any value was decoded.");
                }

                int distanceCluster = _clusters[^1];
                ulong length = (ulong)ReadUint(r, _lengthConfig, token - _minSymbol) + _minLength;
                if (length > uint.MaxValue)
                {
                    throw r.Corrupt("An LZ77 copy is too long.");
                }

                _numToCopy = (uint)length;
                uint distance = ReadUint(r, _configs[distanceCluster], ReadSymbol(r, distanceCluster));
                if (distanceMultiplier != 0)
                {
                    if (distance < 120)
                    {
                        int d = SpecialDistances[2 * distance] + ((int)distanceMultiplier * SpecialDistances[(2 * distance) + 1]);
                        distance = (uint)Math.Max(d - 1, 0);
                    }
                    else
                    {
                        distance -= 120;
                    }
                }

                distance = Math.Min(Math.Min(distance, (1u << 20) - 1) + 1, _numDecoded);
                _copyPos = _numDecoded - distance;
                value = _window![_copyPos++ & 0xFFFFF];
                _numToCopy--;
            }
            else
            {
                value = ReadUint(r, _configs[cluster], token);
            }
        }

        int at = (int)(_numDecoded & 0xFFFFF);
        if (_window is null || at >= _window.Length)
        {
            Array.Resize(ref _window, Math.Min(Math.Max(4096, (_window?.Length ?? 0) * 2), 1 << 20));
        }

        _window[at] = value;
        _numDecoded++;
        return value;
    }

    /// <summary>Reads a permutation (C.6.3): a Lehmer code of <paramref name="size"/> entries after <paramref name="skip"/> fixed ones.</summary>
    internal int[] ReadPermutation(JxlBitReader r, int size, int skip)
    {
        uint end = ReadUint(r, PermutationContext((uint)size));
        if (end > size - skip)
        {
            throw r.Corrupt($"A permutation of {size} entries says {end} are coded.");
        }

        int[] lehmer = new int[end];
        uint previous = 0;
        for (int i = 0; i < end; i++)
        {
            uint value = ReadUint(r, PermutationContext(previous));
            if (value >= size - skip - i)
            {
                throw r.Corrupt("A permutation's Lehmer code is out of range.");
            }

            lehmer[i] = (int)value;
            previous = value;
        }

        List<int> remaining = new(size - skip);
        for (int i = skip; i < size; i++)
        {
            remaining.Add(i);
        }

        int[] permutation = new int[size];
        for (int i = 0; i < skip; i++)
        {
            permutation[i] = i;
        }

        for (int i = 0; i < lehmer.Length; i++)
        {
            permutation[skip + i] = remaining[lehmer[i]];
            remaining.RemoveAt(lehmer[i]);
        }

        remaining.CopyTo(permutation, skip + lehmer.Length);
        return permutation;
    }

    internal static int CeilLog2Plus1(uint x) => x >= 0x80000000 ? 32 : 32 - System.Numerics.BitOperations.LeadingZeroCount(x);

    private static int PermutationContext(uint x) => Math.Min(CeilLog2Plus1(x), 7);

    private uint ReadSymbol(JxlBitReader r, int cluster)
    {
        if (_prefix is not null)
        {
            return _prefix[cluster].ReadSymbol(r);
        }

        if (_initial)
        {
            _state = r.ReadBits(32);
            _initial = false;
        }

        return _ans![cluster].ReadSymbol(r, ref _state);
    }

    private static uint ReadUint(JxlBitReader r, in JxlUintConfig c, uint token)
    {
        if (token < c.Split)
        {
            return token;
        }

        int inToken = c.MsbInToken + c.LsbInToken;
        int n = (c.SplitExponent - inToken + (int)((token - c.Split) >> inToken)) & 31;
        ulong rest = r.ReadBits(n);
        ulong low = token & ((1u << c.LsbInToken) - 1);
        ulong high = ((token >> c.LsbInToken) & ((1u << c.MsbInToken) - 1)) | (1u << c.MsbInToken);
        return (uint)((((high << n) | rest) << c.LsbInToken) | low);
    }

    /// <summary>Reads a context map of <paramref name="contexts"/> entries on its own, as the HF block context does.</summary>
    internal static byte[] ReadContextMap(JxlBitReader r, int contexts, out int count) => ReadClusters(r, contexts, out count);

    /// <summary>Reads the context map (C.2.2): which cluster each context uses.</summary>
    private static byte[] ReadClusters(JxlBitReader r, int contexts, out int count)
    {
        byte[] clusters = new byte[contexts];
        if (contexts == 1)
        {
            count = 1;
            return clusters;
        }

        if (r.ReadBool())
        {
            int bits = (int)r.ReadBits(2);
            for (int i = 0; i < contexts; i++)
            {
                clusters[i] = (byte)r.ReadBits(bits);
            }
        }
        else
        {
            bool mtf = r.ReadBool();
            JxlEntropyDecoder map = Read(r, 1, allowLz77: contexts > 2);
            map.Begin(r);
            for (int i = 0; i < contexts; i++)
            {
                uint value = map.ReadUint(r, 0);
                clusters[i] = value < 256 ? (byte)value : throw r.Corrupt($"A context maps to cluster {value}; at most 255 are allowed.");
            }

            map.Finish(r);
            if (mtf)
            {
                Span<byte> order = stackalloc byte[256];
                for (int i = 0; i < 256; i++)
                {
                    order[i] = (byte)i;
                }

                for (int i = 0; i < contexts; i++)
                {
                    int index = clusters[i];
                    byte value = order[index];
                    clusters[i] = value;
                    order[..index].CopyTo(order[1..]);
                    order[0] = value;
                }
            }
        }

        int max = 0;
        Span<bool> used = stackalloc bool[256];
        foreach (byte c in clusters)
        {
            used[c] = true;
            max = Math.Max(max, c);
        }

        for (int i = 0; i <= max; i++)
        {
            if (!used[i])
            {
                throw r.Corrupt($"The context map skips cluster {i}.");
            }
        }

        count = max + 1;
        return clusters;
    }
}

/// <summary>A hybrid unsigned integer configuration (C.2.6).</summary>
internal readonly record struct JxlUintConfig(int SplitExponent, uint Split, int MsbInToken, int LsbInToken)
{
    internal static JxlUintConfig Read(JxlBitReader r, int logAlphabet)
    {
        int split = (int)r.ReadBits(JxlEntropyDecoder.CeilLog2Plus1((uint)logAlphabet));
        int msb = 0;
        int lsb = 0;
        if (split != logAlphabet)
        {
            msb = (int)r.ReadBits(JxlEntropyDecoder.CeilLog2Plus1((uint)split));
            if (msb > split)
            {
                throw r.Corrupt("A hybrid integer configuration keeps more bits in the token than its split.");
            }

            lsb = (int)r.ReadBits(JxlEntropyDecoder.CeilLog2Plus1((uint)(split - msb)));
        }

        if (msb + lsb > split || split > 31)
        {
            throw r.Corrupt("A hybrid integer configuration is out of range.");
        }

        return new JxlUintConfig(split, 1u << split, msb, lsb);
    }
}

/// <summary>An ANS distribution with its alias table (C.2.4, C.2.5).</summary>
internal sealed class JxlAnsHistogram
{
    private readonly ushort[] _dist;
    private readonly byte[] _aliasSymbol;
    private readonly ushort[] _cutoff;
    private readonly ushort[] _offset;
    private readonly int _logBucket;

    private JxlAnsHistogram(ushort[] dist, byte[] aliasSymbol, ushort[] cutoff, ushort[] offset, int logBucket, int single)
    {
        _dist = dist;
        _aliasSymbol = aliasSymbol;
        _cutoff = cutoff;
        _offset = offset;
        _logBucket = logBucket;
        SingleSymbol = single;
    }

    internal int SingleSymbol { get; }

    internal static JxlAnsHistogram Read(JxlBitReader r, int logAlphabet)
    {
        int tableSize = 1 << logAlphabet;
        ushort[] dist = new ushort[tableSize];
        int alphabet;
        if (r.ReadBool())
        {
            if (r.ReadBool())
            {
                int v0 = ReadU8(r);
                int v1 = ReadU8(r);
                if (v0 == v1)
                {
                    throw r.Corrupt("An ANS distribution names the same symbol twice.");
                }

                alphabet = Math.Max(v0, v1) + 1;
                Check(r, alphabet, tableSize);
                ushort p = (ushort)r.ReadBits(12);
                dist[v0] = p;
                dist[v1] = (ushort)((1 << 12) - p);
            }
            else
            {
                int v = ReadU8(r);
                alphabet = v + 1;
                Check(r, alphabet, tableSize);
                dist[v] = 1 << 12;
            }
        }
        else if (r.ReadBool())
        {
            alphabet = ReadU8(r) + 1;
            Check(r, alphabet, tableSize);
            int baseCount = (1 << 12) / alphabet;
            int leftover = (1 << 12) % alphabet;
            for (int i = 0; i < alphabet; i++)
            {
                dist[i] = (ushort)(baseCount + (i < leftover ? 1 : 0));
            }
        }
        else
        {
            int len = 0;
            while (len < 3 && r.ReadBool())
            {
                len++;
            }

            int shift = (int)r.ReadBits(len) + (1 << len) - 1;
            if (shift > 13)
            {
                throw r.Corrupt("An ANS distribution's shift is above 13.");
            }

            alphabet = ReadU8(r) + 3;
            Check(r, alphabet, tableSize);
            Span<int> logCounts = stackalloc int[tableSize];
            Span<bool> repeated = stackalloc bool[tableSize];
            int omitLog = -1;
            int omitPos = -1;
            for (int i = 0; i < alphabet;)
            {
                logCounts[i] = ReadPrefix(r);
                if (logCounts[i] == 13)
                {
                    int count = ReadU8(r) + 4;
                    if (i + count > alphabet)
                    {
                        throw r.Corrupt("An ANS distribution's repeat runs past its alphabet.");
                    }

                    // The run copies the previous symbol's count (the entry before it), filled in below.
                    for (int j = i; j < i + count; j++)
                    {
                        repeated[j] = true;
                    }

                    i += count;
                    continue;
                }

                if (logCounts[i] > omitLog)
                {
                    omitLog = logCounts[i];
                    omitPos = i;
                }

                i++;
            }

            if (omitPos < 0 || (omitPos + 1 < alphabet && repeated[omitPos + 1]))
            {
                throw r.Corrupt("An ANS distribution has no symbol to omit, or repeats the omitted one.");
            }

            int total = 0;
            int previous = 0;
            for (int i = 0; i < alphabet; i++)
            {
                if (repeated[i])
                {
                    dist[i] = (ushort)previous;
                    total += previous;
                    if (total > 1 << 12)
                    {
                        throw r.Corrupt("An ANS distribution's counts exceed 4096.");
                    }

                    continue;
                }

                int code = logCounts[i];
                if (code == 0 || i == omitPos)
                {
                    previous = 0;
                    dist[i] = 0;
                    continue;
                }

                int value = 1;
                if (code > 1)
                {
                    int zeros = code - 1;
                    int bits = Math.Clamp(shift - ((12 - zeros) >> 1), 0, zeros);
                    value = (1 << zeros) + ((int)r.ReadBits(bits) << (zeros - bits));
                }

                dist[i] = (ushort)value;
                previous = value;
                total += value;
                if (total > 1 << 12)
                {
                    throw r.Corrupt("An ANS distribution's counts exceed 4096.");
                }
            }

            dist[omitPos] = (ushort)((1 << 12) - total);
        }

        return Build(dist, alphabet, logAlphabet);
    }

    internal uint ReadSymbol(JxlBitReader r, ref uint state)
    {
        uint index = state & 0xFFF;
        int i = (int)(index >> _logBucket);
        uint pos = index & ((1u << _logBucket) - 1);
        uint symbol;
        uint offset;
        if (pos >= _cutoff[i])
        {
            symbol = _aliasSymbol[i];
            offset = _offset[i] + pos;
        }
        else
        {
            symbol = (uint)i;
            offset = pos;
        }

        uint next = ((state >> 12) * _dist[symbol]) + offset;
        if (next < 1 << 16)
        {
            next = (next << 16) | r.PeekBits(16);
            r.Consume(16);
        }

        state = next;
        return symbol;
    }

    private static JxlAnsHistogram Build(ushort[] dist, int alphabet, int logAlphabet)
    {
        int tableSize = dist.Length;
        int logBucket = 12 - logAlphabet;
        int bucket = 1 << logBucket;
        byte[] alias = new byte[tableSize];
        ushort[] cutoff = new ushort[tableSize];
        ushort[] offset = new ushort[tableSize];
        for (int i = 0; i < tableSize; i++)
        {
            if (dist[i] == 1 << 12)
            {
                // A single symbol: every state maps to it.
                for (int j = 0; j < tableSize; j++)
                {
                    alias[j] = (byte)i;
                    cutoff[j] = 0;
                    offset[j] = (ushort)(bucket * j);
                }

                return new JxlAnsHistogram(dist, alias, cutoff, offset, logBucket, i);
            }
        }

        // Vose's alias method (C.2.5): each bucket of 2^(12 - log alphabet) states holds its own symbol up to the cutoff
        // and one other symbol above it.
        int[] cut = new int[tableSize];
        int[] sym = new int[tableSize];
        int[] off = new int[tableSize];
        Stack<int> underfull = new();
        Stack<int> overfull = new();
        for (int i = 0; i < tableSize; i++)
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
            int by = bucket - cut[u];
            cut[o] -= by;
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

        for (int i = 0; i < tableSize; i++)
        {
            if (cut[i] == bucket)
            {
                alias[i] = (byte)i;
                cutoff[i] = (ushort)bucket;
                offset[i] = 0;
            }
            else
            {
                alias[i] = (byte)sym[i];
                cutoff[i] = (ushort)cut[i];
                offset[i] = (ushort)(off[i] - cut[i]);
            }
        }

        return new JxlAnsHistogram(dist, alias, cutoff, offset, logBucket, -1);
    }

    private static void Check(JxlBitReader r, int alphabet, int tableSize)
    {
        if (alphabet > tableSize)
        {
            throw r.Corrupt($"An ANS distribution has {alphabet} symbols in a table of {tableSize}.");
        }
    }

    private static int ReadU8(JxlBitReader r)
    {
        if (!r.ReadBool())
        {
            return 0;
        }

        int n = (int)r.ReadBits(3);
        return (1 << n) + (int)r.ReadBits(n);
    }

    /// <summary>Reads a log count with the fixed prefix code of C.2.4.</summary>
    private static int ReadPrefix(JxlBitReader r)
    {
        switch (r.ReadBits(3))
        {
            case 0: return 10;
            case 1:
                foreach (int v in (ReadOnlySpan<int>)[4, 0, 11, 13])
                {
                    if (r.ReadBool())
                    {
                        return v;
                    }
                }

                return 12;
            case 2: return 7;
            case 3: return r.ReadBool() ? 1 : 3;
            case 4: return 6;
            case 5: return 8;
            case 6: return 9;
            default: return r.ReadBool() ? 2 : 5;
        }
    }
}

/// <summary>A prefix code (Brotli's, RFC 7932 3.4 and 3.5) as a two-level lookup table.</summary>
internal sealed class JxlPrefixHistogram
{
    private const int MaxBits = 15;
    private const int RootBits = 10;

    // Entry: symbol in the low 16 bits, code length in bits 16 to 23; a root entry with bit 31 set points to a sub-table
    // (offset in the low 16 bits, its index bits in 16 to 23).
    private readonly uint[] _table;
    private readonly int _rootBits;

    private JxlPrefixHistogram(uint[] table, int rootBits, int single)
    {
        _table = table;
        _rootBits = rootBits;
        SingleSymbol = single;
    }

    internal int SingleSymbol { get; }

    internal static JxlPrefixHistogram Read(JxlBitReader r, uint alphabet)
    {
        if (alphabet == 1)
        {
            return Single(0);
        }

        uint hskip = r.ReadBits(2);
        return hskip == 1 ? ReadSimple(r, alphabet) : ReadComplex(r, alphabet, (int)hskip);
    }

    internal uint ReadSymbol(JxlBitReader r)
    {
        if (_rootBits == 0)
        {
            return _table[0] & 0xFFFF;
        }

        uint peek = r.PeekBits(MaxBits);
        uint entry = _table[peek & ((1u << _rootBits) - 1)];
        if ((entry & 0x80000000) != 0)
        {
            int subBits = (int)((entry >> 16) & 0xFF);
            entry = _table[(entry & 0xFFFF) + ((peek >> _rootBits) & ((1u << subBits) - 1))];
        }

        r.Consume((int)((entry >> 16) & 0xFF));
        return entry & 0xFFFF;
    }

    private static JxlPrefixHistogram Single(int symbol) => new([(uint)symbol], 0, symbol);

    private static JxlPrefixHistogram ReadSimple(JxlBitReader r, uint alphabet)
    {
        int bits = JxlEntropyDecoder.CeilLog2Plus1(alphabet - 1);
        int count = (int)r.ReadBits(2) + 1;
        Span<int> symbols = stackalloc int[4];
        for (int i = 0; i < count; i++)
        {
            symbols[i] = (int)r.ReadBits(bits);
            if (symbols[i] >= alphabet)
            {
                throw r.Corrupt("A simple prefix code names a symbol outside its alphabet.");
            }
        }

        if (count == 1)
        {
            return Single(symbols[0]);
        }

        byte[] lengths = new byte[alphabet];
        ReadOnlySpan<byte> shape = count switch
        {
            2 => [1, 1],
            3 => [1, 2, 2],
            _ => r.ReadBool() ? [1, 2, 3, 3] : [2, 2, 2, 2],
        };
        for (int i = 0; i < count; i++)
        {
            if (lengths[symbols[i]] != 0)
            {
                throw r.Corrupt("A simple prefix code names a symbol twice.");
            }

            lengths[symbols[i]] = shape[i];
        }

        // Lengths go by the order read; the canonical code then orders symbols of one length by value.
        return Build(r, lengths);
    }

    private static JxlPrefixHistogram ReadComplex(JxlBitReader r, uint alphabet, int hskip)
    {
        ReadOnlySpan<int> order = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        byte[] codeLengthLengths = new byte[18];
        int space = 0;
        int nonzero = 0;
        int nonzeroSymbol = 0;
        for (int k = hskip; k < 18; k++)
        {
            // The code-length code lengths use a fixed variable-length code (RFC 7932 3.5).
            uint peek = r.PeekBits(4);
            int len;
            switch (peek & 3)
            {
                case 0:
                    len = 0;
                    r.Consume(2);
                    break;
                case 1:
                    len = 4;
                    r.Consume(2);
                    break;
                case 2:
                    len = 3;
                    r.Consume(2);
                    break;
                default:
                    if ((peek & 4) == 0)
                    {
                        len = 2;
                        r.Consume(3);
                    }
                    else if ((peek & 8) == 0)
                    {
                        len = 1;
                        r.Consume(4);
                    }
                    else
                    {
                        len = 5;
                        r.Consume(4);
                    }

                    break;
            }

            codeLengthLengths[order[k]] = (byte)len;
            if (len != 0)
            {
                nonzero++;
                nonzeroSymbol = order[k];
                space += 32 >> len;
                if (space == 32)
                {
                    break;
                }

                if (space > 32)
                {
                    throw r.Corrupt("A prefix code's code-length code is over-subscribed.");
                }
            }
        }

        if (nonzero != 1 && space != 32)
        {
            throw r.Corrupt("A prefix code's code-length code is incomplete.");
        }

        JxlPrefixHistogram lengthCode = nonzero == 1 ? Single(nonzeroSymbol) : Build(r, codeLengthLengths);
        byte[] lengths = new byte[alphabet];
        int total = 0;
        int previousSymbol = 8;
        int lastNonzero = 8;
        int lastRepeat = 0;
        int repeat = 0;
        int repeatLength = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (repeat > 0)
            {
                lengths[i] = (byte)repeatLength;
                repeat--;
            }
            else
            {
                int symbol = (int)lengthCode.ReadSymbol(r);
                if (symbol is > 0 and < 16)
                {
                    lengths[i] = (byte)symbol;
                    lastNonzero = symbol;
                }
                else if (symbol >= 16)
                {
                    // 16 repeats the last nonzero length 3 to 6 times, 17 repeats a zero 3 to 10 times; a run right after
                    // a run of the same kind extends it (RFC 7932 3.5).
                    bool zeros = symbol == 17;
                    repeat = (int)r.ReadBits(zeros ? 3 : 2) + 3;
                    if (previousSymbol == symbol)
                    {
                        repeat += (lastRepeat * (zeros ? 7 : 3)) - (zeros ? 16 : 8);
                        lastRepeat += repeat;
                    }
                    else
                    {
                        lastRepeat = repeat;
                    }

                    repeatLength = zeros ? 0 : lastNonzero;
                    lengths[i] = (byte)repeatLength;
                    repeat--;
                }

                previousSymbol = symbol;
            }

            if (lengths[i] != 0)
            {
                total += 1 << (MaxBits - lengths[i]);
                if (total > 1 << MaxBits)
                {
                    throw r.Corrupt("A prefix code's lengths over-fill the code space.");
                }

                if (total == 1 << MaxBits && repeat == 0)
                {
                    break;
                }
            }
        }

        if (total != 1 << MaxBits || repeat > 0)
        {
            throw r.Corrupt("A prefix code's lengths do not fill the code space.");
        }

        return Build(r, lengths);
    }

    /// <summary>Builds the lookup tables of a canonical code from its lengths; the codes are read LSB first, so they are bit-reversed.</summary>
    private static JxlPrefixHistogram Build(JxlBitReader r, byte[] lengths)
    {
        Span<int> counts = stackalloc int[MaxBits + 1];
        int maxLength = 0;
        int used = 0;
        int last = 0;
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] != 0)
            {
                counts[lengths[s]]++;
                maxLength = Math.Max(maxLength, lengths[s]);
                used++;
                last = s;
            }
        }

        if (used == 1)
        {
            return Single(last);
        }

        Span<int> next = stackalloc int[MaxBits + 2];
        int code = 0;
        long space = 0;
        for (int len = 1; len <= MaxBits; len++)
        {
            code = (code + counts[len - 1]) << 1;
            next[len] = code;
            space += (long)counts[len] << (MaxBits - len);
        }

        if (space != 1 << MaxBits)
        {
            throw r.Corrupt("A prefix code is not complete.");
        }

        int rootBits = Math.Min(maxLength, RootBits);
        int[] codes = new int[lengths.Length];
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] != 0)
            {
                codes[s] = Reverse(next[lengths[s]]++, lengths[s]);
            }
        }

        // Sub-table size per root prefix: the longest code through it.
        int[] subBits = new int[1 << rootBits];
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] > rootBits)
            {
                int prefix = codes[s] & ((1 << rootBits) - 1);
                subBits[prefix] = Math.Max(subBits[prefix], lengths[s] - rootBits);
            }
        }

        int size = 1 << rootBits;
        int[] subOffset = new int[1 << rootBits];
        for (int p = 0; p < subBits.Length; p++)
        {
            if (subBits[p] > 0)
            {
                subOffset[p] = size;
                size += 1 << subBits[p];
            }
        }

        uint[] table = new uint[size];
        for (int p = 0; p < subBits.Length; p++)
        {
            if (subBits[p] > 0)
            {
                table[p] = 0x80000000u | ((uint)subBits[p] << 16) | (uint)subOffset[p];
            }
        }

        for (int s = 0; s < lengths.Length; s++)
        {
            int len = lengths[s];
            if (len == 0)
            {
                continue;
            }

            if (len <= rootBits)
            {
                uint entry = ((uint)len << 16) | (uint)s;
                for (int k = codes[s]; k < 1 << rootBits; k += 1 << len)
                {
                    table[k] = entry;
                }
            }
            else
            {
                int prefix = codes[s] & ((1 << rootBits) - 1);
                int rest = codes[s] >> rootBits;
                int bits = subBits[prefix];
                uint entry = ((uint)len << 16) | (uint)s;
                for (int k = rest; k < 1 << bits; k += 1 << (len - rootBits))
                {
                    table[subOffset[prefix] + k] = entry;
                }
            }
        }

        return new JxlPrefixHistogram(table, rootBits, -1);
    }

    private static int Reverse(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return reversed;
    }
}
