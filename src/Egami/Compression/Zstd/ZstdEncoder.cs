using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// A Zstandard encoder (RFC 8878) for TIFF's ZSTD compression: one single-segment frame a call, with its content size and no
/// checksum, as libtiff writes. Levels 1 to 22 follow libzstd's parameter ladder (window, hash and chain sizes, search depth);
/// levels 1 and 2 use its fast match finder, 3 and 4 the double fast one, 5 to 12 its greedy, lazy and lazy2 parsers on a hash
/// chain or its row match finder, and 13 to 22, where libzstd uses binary trees and optimal parsing, lazy2 on a binary tree with
/// libzstd's bigger tables and deeper searches.
/// </summary>
/// <remarks>
/// Each block picks what is smallest: Huffman literals with a new or the last tree, or raw or RLE literals; for each sequence
/// table the predefined distribution, the last table, an RLE symbol or a new FSE table; and a raw or RLE block when coding does
/// not pay. Each thread keeps one instance for its tables and buffers, so a warm call allocates nothing.
/// </remarks>
internal sealed partial class ZstdEncoder
{
    /// <summary>The lowest level.</summary>
    public const int MinLevel = 1;

    /// <summary>The highest level.</summary>
    public const int MaxLevel = 22;

    private const int MaxBlock = ZstdDecoder.MaxBlockSize;
    private const int MaxSequences = (MaxBlock / 3) + 1;

    // libzstd's level parameters for large inputs: window log, chain log, hash log, search log, minimum match, target length,
    // and the strategy (0 fast, 1 double fast, 2 greedy, 3 lazy, 4 lazy2; libzstd's binary-tree strategies map to lazy2).
    private static readonly int[,] Levels =
    {
        { 19, 13, 14, 1, 7, 0, 0 }, // 1
        { 20, 15, 16, 1, 6, 0, 0 }, // 2
        { 21, 16, 17, 1, 5, 0, 1 }, // 3
        { 21, 18, 18, 1, 5, 0, 1 }, // 4
        { 21, 18, 19, 3, 5, 2, 2 }, // 5
        { 21, 18, 19, 3, 5, 4, 3 }, // 6
        { 21, 19, 20, 4, 5, 8, 3 }, // 7
        { 21, 19, 20, 4, 5, 16, 4 }, // 8
        { 22, 20, 21, 4, 5, 16, 4 }, // 9
        { 22, 21, 22, 5, 5, 16, 4 }, // 10
        { 22, 21, 22, 6, 5, 16, 4 }, // 11
        { 22, 22, 23, 6, 5, 32, 4 }, // 12
        { 22, 22, 22, 4, 5, 32, 4 }, // 13
        { 22, 22, 23, 5, 5, 32, 4 }, // 14
        { 22, 23, 23, 6, 5, 32, 4 }, // 15
        { 22, 22, 22, 5, 5, 48, 4 }, // 16
        { 23, 23, 22, 5, 4, 64, 4 }, // 17
        { 23, 23, 22, 6, 4, 64, 4 }, // 18
        { 23, 24, 22, 7, 4, 256, 4 }, // 19
        { 25, 25, 23, 7, 4, 256, 4 }, // 20
        { 26, 26, 24, 7, 4, 512, 4 }, // 21
        { 27, 27, 25, 9, 4, 999, 4 }, // 22
    };

    // Literal and match length codes for small values; larger ones follow from their highest bit.
    private static readonly byte[] LiteralLengthCode = CodeTable(ZstdDecoder.LiteralLengthBase, 64);
    private static readonly byte[] MatchLengthCode = CodeTable(ZstdDecoder.MatchLengthBase, 128 + 3);

    [ThreadStatic]
    private static ZstdEncoder? cached;

    private readonly byte[] _literals = new byte[MaxBlock + 64];
    private readonly int[] _literalLengths = new int[MaxSequences];
    private readonly int[] _matchLengths = new int[MaxSequences];
    private readonly int[] _distances = new int[MaxSequences];
    private readonly byte[] _llCodes = new byte[MaxSequences];
    private readonly byte[] _mlCodes = new byte[MaxSequences];
    private readonly byte[] _ofCodes = new byte[MaxSequences];
    private readonly uint[] _offsetValues = new uint[MaxSequences];
    private readonly uint[] _llExtras = new uint[MaxSequences];
    private readonly uint[] _mlExtras = new uint[MaxSequences];
    private readonly byte[] _block = new byte[MaxBlock + 1024];
    private readonly ZstdHuffmanEncoder _huffman = new();
    private readonly ZstdHuffmanEncoder _lastHuffman = new();
    private readonly ZstdFseEncoder _llEncoder = new();
    private readonly ZstdFseEncoder _ofEncoder = new();
    private readonly ZstdFseEncoder _mlEncoder = new();
    private readonly SequenceTable _llTable = new(ZstdDecoder.LiteralLengthDefault, 6, 35, 9);
    private readonly SequenceTable _ofTable = new(ZstdDecoder.OffsetDefault, 5, 31, 8);
    private readonly SequenceTable _mlTable = new(ZstdDecoder.MatchLengthDefault, 6, 52, 9);
    private readonly int[] _counts = new int[256];
    private readonly int[] _histogram = new int[1024];
    private int _sequences;
    private int _literalCount;

    // The repeat offsets as the decoder has them after the blocks written so far.
    private uint _rep1;
    private uint _rep2;
    private uint _rep3;

    // The repeat offsets after the block being written, which become the decoder's if it goes out compressed.
    private uint _pendingRep1;
    private uint _pendingRep2;
    private uint _pendingRep3;

    /// <summary>Gets the most bytes <see cref="Encode"/> can write for <paramref name="length"/> bytes of input.</summary>
    public static int MaxEncodedLength(int length) => length + (3 * ((length / MaxBlock) + 1)) + 18;

    /// <summary>
    /// Compresses <paramref name="input"/> into one frame at <paramref name="output"/>, which needs room for
    /// <see cref="MaxEncodedLength"/> bytes; returns the bytes written.
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> input, Span<byte> output, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxLevel);
        if (output.Length < MaxEncodedLength(input.Length))
        {
            throw new ArgumentException("The output has less room than MaxEncodedLength gives.", nameof(output));
        }

        ZstdEncoder encoder = cached ?? new ZstdEncoder();
        cached = null;
        try
        {
            return encoder.Run(input, output, level);
        }
        finally
        {
            cached = encoder;
        }
    }

    private static byte[] CodeTable(uint[] baseValues, int size)
    {
        byte[] table = new byte[size];
        int code = 0;
        for (int v = 0; v < size; v++)
        {
            while (code + 1 < baseValues.Length && baseValues[code + 1] <= v)
            {
                code++;
            }

            table[v] = (byte)code;
        }

        return table;
    }

    private int Run(ReadOnlySpan<byte> input, Span<byte> output, int level)
    {
        Parameters parameters = Parameters.For(level, input.Length);
        int at = WriteHeader(input.Length, parameters, output);
        Prepare(parameters);
        _hashLimit = input.Length - 8;
        _huffman.Clear();
        _lastHuffman.Clear();
        _llTable.Reset();
        _ofTable.Reset();
        _mlTable.Reset();
        _rep1 = 1;
        _rep2 = 4;
        _rep3 = 8;
        int start = 0;
        do
        {
            int end = Math.Min(input.Length, start + MaxBlock);
            bool last = end == input.Length;
            at += Block(input, start, end, last, output[at..], parameters);
            start = end;
        }
        while (start < input.Length);

        return at;
    }

    private static int WriteHeader(int length, Parameters parameters, Span<byte> output)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output, 0xFD2FB528);

        // A single segment (the window is the content) up to libzstd's default window limit of 128 MB, else a window descriptor.
        bool single = length <= 1 << 27;
        int sizeFlag = length < 256 && single ? 0 : length < 65536 + 256 ? 1 : 2;
        int at = 5;
        output[4] = (byte)((sizeFlag << 6) | (single ? 0x20 : 0));
        if (!single)
        {
            output[at++] = (byte)((parameters.WindowLog - 10) << 3);
        }

        if (sizeFlag == 0)
        {
            output[at++] = (byte)length;
        }
        else if (sizeFlag == 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output[at..], (ushort)(length - 256));
            at += 2;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output[at..], (uint)length);
            at += 4;
        }

        return at;
    }

    /// <summary>Writes one block (header included); returns its size.</summary>
    private int Block(ReadOnlySpan<byte> input, int start, int end, bool last, Span<byte> output, Parameters parameters)
    {
        ReadOnlySpan<byte> data = input[start..end];
        int size = data.Length;
        if (size > 1 && !data[1..].ContainsAnyExcept(data[0]))
        {
            WriteBlockHeader(output, last, 1, size);
            output[3] = data[0];
            return 4;
        }

        _sequences = 0;
        _literalCount = 0;
        if (size >= 16)
        {
            FindMatches(input, start, end, parameters);
        }
        else
        {
            AddLiterals(input, start, size);
        }

        int compressed = size >= 16 ? Compress(_block, size, parameters) : -1;
        if (compressed < 0 || compressed >= size)
        {
            // Nothing the block's tables or offsets did reaches the decoder.
            _huffman.CopyFrom(_lastHuffman);
            _llTable.Revert();
            _ofTable.Revert();
            _mlTable.Revert();
            WriteBlockHeader(output, last, 0, size);
            data.CopyTo(output[3..]);
            return 3 + size;
        }

        _lastHuffman.CopyFrom(_huffman);
        _rep1 = _pendingRep1;
        _rep2 = _pendingRep2;
        _rep3 = _pendingRep3;
        _llTable.Confirm();
        _ofTable.Confirm();
        _mlTable.Confirm();
        WriteBlockHeader(output, last, 2, compressed);
        _block.AsSpan(0, compressed).CopyTo(output[3..]);
        return 3 + compressed;
    }

    private static void WriteBlockHeader(Span<byte> output, bool last, int type, int size)
    {
        int header = (last ? 1 : 0) | (type << 1) | (size << 3);
        output[0] = (byte)header;
        output[1] = (byte)(header >> 8);
        output[2] = (byte)(header >> 16);
    }

    /// <summary>Codes the block's literals and sequences into <paramref name="target"/>; returns the size, or -1 when it does not fit.</summary>
    private int Compress(Span<byte> target, int blockSize, Parameters parameters)
    {
        int limit = Math.Min(target.Length, blockSize);
        int at = Literals(target[..limit], parameters);
        if (at < 0)
        {
            return -1;
        }

        int sequences = Sequences(target[at..limit]);
        return sequences < 0 ? -1 : at + sequences;
    }

    /// <summary>Writes the literals section; returns its size, or -1.</summary>
    private int Literals(Span<byte> target, Parameters parameters)
    {
        int count = _literalCount;
        ReadOnlySpan<byte> literals = _literals.AsSpan(0, count);
        int rawHeader = count < 32 ? 1 : count < 4096 ? 2 : 3;
        Span<int> counts = _counts;
        Histogram(literals, counts);
        int maxSymbol = 0;

        int present = 0;
        int most = 0;
        for (int s = 0; s < 256; s++)
        {
            if (counts[s] != 0)
            {
                maxSymbol = s;
                present++;
                most = Math.Max(most, counts[s]);
            }
        }

        if (count > 0 && present == 1 && count > 2)
        {
            if (target.Length < rawHeader + 1)
            {
                return -1;
            }

            WriteRawHeader(target, 1, count, rawHeader);
            target[rawHeader] = literals[0];
            return rawHeader + 1;
        }

        long lastCost = _lastHuffman.Cost(counts, maxSymbol);
        int minimum = lastCost >= 0 ? 6 : 63;
        if (count <= minimum || present < 2)
        {
            return Raw(target, literals, rawHeader);
        }

        // A new tree, or the last one when that costs less, coded as one stream under 256 literals and as four above.
        bool four = count >= 256;
        int header = !four ? 3 : count < 1024 ? 3 : count < 16384 ? 4 : 5;
        if (target.Length < header + 64)
        {
            return Raw(target, literals, rawHeader);
        }

        Span<byte> body = target[header..];
        int tree = -1;
        bool reuse = false;
        if (_huffman.Build(counts, maxSymbol))
        {
            tree = _huffman.WriteTree(body);
        }

        long newCost = tree < 0 ? long.MaxValue : _huffman.Cost(counts, maxSymbol) + (8L * tree);
        if (lastCost >= 0 && lastCost + 64 <= newCost)
        {
            _huffman.CopyFrom(_lastHuffman);
            reuse = true;
            tree = 0;
        }
        else if (tree < 0)
        {
            _huffman.CopyFrom(_lastHuffman);
            return Raw(target, literals, rawHeader);
        }

        int streams = four ? _huffman.EncodeFour(literals, body[tree..]) : _huffman.EncodeOne(literals, body[tree..]);
        int compressed = tree + streams;
        int minGain = (count >> (parameters.Strategy >= 4 ? 7 : 6)) + 2;
        int maxSize = header switch
        {
            3 => 1023,
            4 => 16383,
            _ => 262143,
        };
        if (streams < 0 || compressed > maxSize || header + compressed >= rawHeader + count - minGain)
        {
            _huffman.CopyFrom(_lastHuffman);
            return Raw(target, literals, rawHeader);
        }

        int format = !four ? 0 : header - 2;
        int bits = header switch
        {
            3 => 10,
            4 => 14,
            _ => 18,
        };
        ulong value = (reuse ? 3UL : 2UL) | ((ulong)(uint)format << 2) | ((ulong)(uint)count << 4) | ((ulong)(uint)compressed << (4 + bits));
        for (int i = 0; i < header; i++)
        {
            target[i] = (byte)(value >> (8 * i));
        }

        return header + compressed;
    }

    /// <summary>Counts the bytes into four tables taken in turn, so a run of one byte does not wait on its own count.</summary>
    private void Histogram(ReadOnlySpan<byte> data, Span<int> counts)
    {
        Span<int> four = _histogram;
        four.Clear();
        ref int t = ref MemoryMarshal.GetReference(four);
        ref byte d = ref MemoryMarshal.GetReference(data);
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            Unsafe.Add(ref t, Unsafe.Add(ref d, i))++;
            Unsafe.Add(ref t, 256 + Unsafe.Add(ref d, i + 1))++;
            Unsafe.Add(ref t, 512 + Unsafe.Add(ref d, i + 2))++;
            Unsafe.Add(ref t, 768 + Unsafe.Add(ref d, i + 3))++;
        }

        for (; i < data.Length; i++)
        {
            Unsafe.Add(ref t, Unsafe.Add(ref d, i))++;
        }

        for (int s = 0; s < 256; s++)
        {
            counts[s] = four[s] + four[256 + s] + four[512 + s] + four[768 + s];
        }
    }

    private static int Raw(Span<byte> target, ReadOnlySpan<byte> literals, int header)
    {
        if (target.Length < header + literals.Length)
        {
            return -1;
        }

        WriteRawHeader(target, 0, literals.Length, header);
        literals.CopyTo(target[header..]);
        return header + literals.Length;
    }

    private static void WriteRawHeader(Span<byte> target, int type, int count, int header)
    {
        switch (header)
        {
            case 1:
                target[0] = (byte)(type | (count << 3));
                break;
            case 2:
                target[0] = (byte)(type | (1 << 2) | ((count & 15) << 4));
                target[1] = (byte)(count >> 4);
                break;
            default:
                target[0] = (byte)(type | (3 << 2) | ((count & 15) << 4));
                target[1] = (byte)(count >> 4);
                target[2] = (byte)(count >> 12);
                break;
        }
    }

    /// <summary>Writes the sequences section; returns its size, or -1.</summary>
    private int Sequences(Span<byte> target)
    {
        int n = _sequences;
        _pendingRep1 = _rep1;
        _pendingRep2 = _rep2;
        _pendingRep3 = _rep3;
        if (target.Length < 4)
        {
            return -1;
        }

        int at;
        if (n < 128)
        {
            target[0] = (byte)n;
            at = 1;
        }
        else if (n < 0x7F00)
        {
            target[0] = (byte)((n >> 8) + 128);
            target[1] = (byte)n;
            at = 2;
        }
        else
        {
            target[0] = 255;
            target[1] = (byte)(n - 0x7F00);
            target[2] = (byte)((n - 0x7F00) >> 8);
            at = 3;
        }

        if (n == 0)
        {
            return at;
        }

        // Offsets as the decoder will read them: repeat codes where they apply, from the offsets confirmed so far.
        uint rep1 = _rep1;
        uint rep2 = _rep2;
        uint rep3 = _rep3;
        for (int i = 0; i < n; i++)
        {
            uint distance = (uint)_distances[i];
            uint value;
            if (_literalLengths[i] != 0)
            {
                value = distance == rep1 ? 1u : distance == rep2 ? 2u : distance == rep3 ? 3u : distance + 3;
            }
            else
            {
                value = distance == rep2 ? 1u : distance == rep3 ? 2u : distance == rep1 - 1 ? 3u : distance + 3;
            }

            if (value > 3)
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = distance;
            }
            else
            {
                uint index = value - (_literalLengths[i] == 0 ? 0u : 1u);
                if (index != 0)
                {
                    if (index != 1)
                    {
                        rep3 = rep2;
                    }

                    rep2 = rep1;
                    rep1 = distance;
                }
            }

            _offsetValues[i] = value;
            _ofCodes[i] = (byte)BitOperations.Log2(value);
            int ll = _literalLengths[i];
            int llCode = ll < 64 ? LiteralLengthCode[ll] : BitOperations.Log2((uint)ll) + 19;
            _llCodes[i] = (byte)llCode;
            _llExtras[i] = (uint)(ll - (int)ZstdDecoder.LiteralLengthBase[llCode]) | ((uint)ZstdDecoder.LiteralLengthBits[llCode] << 16);
            int ml = _matchLengths[i];
            int mlCode = ml - 3 < 128 ? MatchLengthCode[ml] : BitOperations.Log2((uint)(ml - 3)) + 36;
            _mlCodes[i] = (byte)mlCode;
            _mlExtras[i] = (uint)(ml - (int)ZstdDecoder.MatchLengthBase[mlCode]) | ((uint)ZstdDecoder.MatchLengthBits[mlCode] << 16);
        }

        _pendingRep1 = rep1;
        _pendingRep2 = rep2;
        _pendingRep3 = rep3;

        int modesAt = at++;
        int llMode = _llTable.Choose(_llCodes.AsSpan(0, n), _counts, target[at..], out int used);
        if (llMode < 0)
        {
            return -1;
        }

        at += used;
        int ofMode = _ofTable.Choose(_ofCodes.AsSpan(0, n), _counts, target[at..], out used);
        if (ofMode < 0)
        {
            return -1;
        }

        at += used;
        int mlMode = _mlTable.Choose(_mlCodes.AsSpan(0, n), _counts, target[at..], out used);
        if (mlMode < 0)
        {
            return -1;
        }

        at += used;
        target[modesAt] = (byte)((llMode << 6) | (ofMode << 4) | (mlMode << 2));
        _llTable.BuildEncoder(_llEncoder);
        _ofTable.BuildEncoder(_ofEncoder);
        _mlTable.BuildEncoder(_mlEncoder);

        ZstdBitWriter writer = new(target[at..]);
        int last = n - 1;
        int llState = _llEncoder.Start(_llCodes[last]);
        int mlState = _mlEncoder.Start(_mlCodes[last]);
        int ofState = _ofEncoder.Start(_ofCodes[last]);
        WriteExtras(ref writer, last);
        for (int i = n - 2; i >= 0; i--)
        {
            _ofEncoder.Encode(ref writer, ref ofState, Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ofCodes), i));
            _mlEncoder.Encode(ref writer, ref mlState, Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_mlCodes), i));
            _llEncoder.Encode(ref writer, ref llState, Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_llCodes), i));
            writer.Flush();
            WriteExtras(ref writer, i);
            if (writer.Overflowed)
            {
                return -1;
            }
        }

        _mlEncoder.Finish(ref writer, mlState);
        _ofEncoder.Finish(ref writer, ofState);
        _llEncoder.Finish(ref writer, llState);
        int stream = writer.Close();
        return stream < 0 ? -1 : at + stream;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteExtras(ref ZstdBitWriter writer, int i)
    {
        // Each extra packs its value in the low 16 bits and its bit count above.
        uint ll = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_llExtras), i);
        uint ml = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_mlExtras), i);
        int ofCode = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ofCodes), i);
        writer.AddClean(ll & 0xFFFF, (int)(ll >> 16));
        writer.AddClean(ml & 0xFFFF, (int)(ml >> 16));
        writer.Flush();
        writer.AddClean(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_offsetValues), i) - (1u << ofCode), ofCode);
        writer.Flush();
    }

    /// <summary>The level's parameters, cut down for small inputs as libzstd does.</summary>
    private readonly record struct Parameters(int WindowLog, int ChainLog, int HashLog, int SearchLog, int MinMatch, int TargetLength, int Strategy,
        bool Rows, bool Tree)
    {
        /// <summary>Gets the log of a row's size for the row match finder: 16 entries, or 32 for deeper searches.</summary>
        public int RowLog => Math.Clamp(SearchLog, 4, 5);

        public static Parameters For(int level, int length)
        {
            int i = level - 1;
            int windowLog = Levels[i, 0];
            int chainLog = Levels[i, 1];
            int hashLog = Levels[i, 2];
            int sourceLog = Math.Max(6, BitOperations.Log2((uint)Math.Max(length - 1, 1)) + 1);
            windowLog = Math.Max(10, Math.Min(windowLog, sourceLog));
            hashLog = Math.Min(hashLog, windowLog + 1);
            chainLog = Math.Min(chainLog, windowLog);
            int strategy = Levels[i, 6];

            // libzstd's greedy, lazy and lazy2 levels use its row match finder; the deeper levels keep a hash chain.
            bool rows = strategy >= 2 && level <= 12;
            if (rows)
            {
                hashLog = Math.Max(hashLog, Math.Clamp(Levels[i, 3], 4, 5) + 1);
            }

            // From level 13, where libzstd switches to binary trees, a binary tree of half the chain's size replaces the chain.
            bool tree = level >= 13;
            if (tree)
            {
                // The tree covers the whole window wherever that is 8 MB or less.
                chainLog = windowLog <= 23 ? windowLog + 1 : Levels[i, 1];
            }

            return new Parameters(windowLog, chainLog, hashLog, Levels[i, 3], Math.Clamp(Levels[i, 4], 4, 7), Levels[i, 5], strategy, rows, tree);
        }
    }

    /// <summary>
    /// One kind of sequence code (literal lengths, offsets or match lengths): the table the decoder holds, for the repeat mode,
    /// and the one this block picks, kept apart until the block is written compressed.
    /// </summary>
    private sealed class SequenceTable
    {
        private readonly short[] _default;
        private readonly int _defaultLog;
        private readonly int _maxSymbol;
        private readonly int _maxLog;
        private readonly short[] _normalized = new short[64];
        private readonly short[] _confirmed = new short[64];
        private readonly short[] _candidate = new short[64];
        private readonly byte[] _scratch = new byte[128];
        private int _log;
        private int _confirmedLog;
        private int _symbols;
        private int _confirmedSymbols;
        private bool _valid;
        private bool _confirmedValid;

        public SequenceTable(short[] defaults, int defaultLog, int maxSymbol, int maxLog)
        {
            _default = defaults;
            _defaultLog = defaultLog;
            _maxSymbol = maxSymbol;
            _maxLog = maxLog;
        }

        public void Reset()
        {
            _valid = false;
            _confirmedValid = false;
        }

        public void Confirm()
        {
            _normalized.AsSpan().CopyTo(_confirmed);
            _confirmedLog = _log;
            _confirmedSymbols = _symbols;
            _confirmedValid = _valid;
        }

        public void Revert()
        {
            _confirmed.AsSpan().CopyTo(_normalized);
            _log = _confirmedLog;
            _symbols = _confirmedSymbols;
            _valid = _confirmedValid;
        }

        /// <summary>Picks the cheapest mode for <paramref name="codes"/> and writes its description; returns the mode, or -1.</summary>
        public int Choose(ReadOnlySpan<byte> codes, int[] counts, Span<byte> target, out int used)
        {
            used = 0;
            Span<int> histogram = counts.AsSpan(0, 64);
            histogram.Clear();
            int maxSymbol = 0;
            foreach (byte c in codes)
            {
                histogram[c]++;
                maxSymbol = Math.Max(maxSymbol, c);
            }

            int n = codes.Length;
            double predefined = ZstdFseEncoder.Cost(histogram, maxSymbol, _default, _defaultLog);
            double repeat = _confirmedValid ? ZstdFseEncoder.Cost(histogram, maxSymbol, _confirmed.AsSpan(0, _confirmedSymbols), _confirmedLog)
                : double.PositiveInfinity;
            double rle = histogram[maxSymbol] == n ? 8 : double.PositiveInfinity;
            double fse = double.PositiveInfinity;
            int fseLog = 0;
            int header = 0;
            if (n >= 2 && rle == double.PositiveInfinity)
            {
                fseLog = ZstdFseEncoder.OptimalLog(_maxLog, n, maxSymbol);
                ZstdFseEncoder.Normalize(histogram, maxSymbol, n, fseLog, _candidate);
                header = ZstdFseEncoder.WriteCounts(_scratch, _candidate, maxSymbol, fseLog);
                if (header > 0)
                {
                    fse = (8 * header) + ZstdFseEncoder.Cost(histogram, maxSymbol, _candidate, fseLog);
                }
            }

            double best = Math.Min(Math.Min(predefined, repeat), Math.Min(rle, fse));
            if (double.IsPositiveInfinity(best))
            {
                return -1;
            }

            if (best == repeat)
            {
                _confirmed.AsSpan().CopyTo(_normalized);
                _log = _confirmedLog;
                _symbols = _confirmedSymbols;
                _valid = true;
                return 3;
            }

            if (best == rle)
            {
                if (target.IsEmpty)
                {
                    return -1;
                }

                target[0] = (byte)maxSymbol;
                used = 1;
                _normalized.AsSpan().Clear();
                _normalized[maxSymbol] = 1;
                _log = 0;
                _symbols = maxSymbol + 1;
                _valid = true;
                return 1;
            }

            if (best == predefined)
            {
                _default.CopyTo(_normalized, 0);
                _log = _defaultLog;
                _symbols = _default.Length;
                _valid = true;
                return 0;
            }

            if (target.Length < header)
            {
                return -1;
            }

            _scratch.AsSpan(0, header).CopyTo(target);
            used = header;
            _candidate.AsSpan(0, maxSymbol + 1).CopyTo(_normalized);
            _log = fseLog;
            _symbols = maxSymbol + 1;
            _valid = true;
            return 2;
        }

        public void BuildEncoder(ZstdFseEncoder encoder) => encoder.Build(_normalized, _symbols - 1, _log);
    }
}
