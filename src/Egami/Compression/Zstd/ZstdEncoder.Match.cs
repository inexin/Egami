using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Compression.Zstd;

/// <content>
/// The match finders: libzstd's fast (one hash table, skipping faster through data that does not match), double fast (a long
/// and a short hash table) and the greedy, lazy and lazy2 parsers on a hash chain, which look one or two bytes further for a
/// longer or nearer match before taking one. Each turns a block into sequences: literals, then a match at some distance.
/// </content>
internal sealed partial class ZstdEncoder
{
    // How fast the search skips ahead through bytes that find nothing: one more byte a step for every 256 without a match.
    private const int SearchStrength = 8;

    // Past this many positions without a search, only the first SkipKeep and the last SkipTail go into the tables.
    private const int SkipThreshold = 384;
    private const int SkipKeep = 96;
    private const int SkipTail = 32;

    private int[] _hashTable = [];
    private int[] _chainTable = [];
    private byte[] _rowTags = [];
    private byte[] _rowHeads = [];
    private readonly int[] _hashCache = new int[8];
    private int _hashed;
    private int _hashLimit;
    private int _nextToUpdate;

    private void Prepare(Parameters parameters)
    {
        int hashSize = 1 << parameters.HashLog;
        int chainSize = parameters.Strategy == 0 ? 0 : 1 << parameters.ChainLog;
        if (_hashTable.Length < hashSize)
        {
            _hashTable = new int[hashSize];
        }

        if (_chainTable.Length < chainSize)
        {
            _chainTable = new int[chainSize];
        }

        _hashTable.AsSpan(0, hashSize).Fill(-1);
        _chainTable.AsSpan(0, chainSize).Fill(-1);
        if (parameters.Rows)
        {
            int rows = hashSize >> parameters.RowLog;
            if (_rowTags.Length < hashSize)
            {
                _rowTags = new byte[hashSize];
            }

            if (_rowHeads.Length < rows)
            {
                _rowHeads = new byte[rows];
            }

            _rowHeads.AsSpan(0, rows).Clear();
        }

        _hashed = 0;

        _nextToUpdate = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddLiterals(ReadOnlySpan<byte> input, int from, int count)
    {
        // Sixteen bytes at a time where the input has them; the literals buffer has the slack.
        if (from + count + 16 <= input.Length)
        {
            ref byte source = ref Unsafe.Add(ref MemoryMarshal.GetReference(input), from);
            ref byte target = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_literals), _literalCount);
            for (int i = 0; i < count; i += 16)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i), Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<byte>>(ref Unsafe.Add(ref source, i)));
            }
        }
        else
        {
            input.Slice(from, count).CopyTo(_literals.AsSpan(_literalCount));
        }

        _literalCount += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store(ReadOnlySpan<byte> input, int from, int literals, int distance, int length)
    {
        AddLiterals(input, from, literals);
        int i = _sequences++;
        _literalLengths[i] = literals;
        _matchLengths[i] = length;
        _distances[i] = distance;
    }

    private void FindMatches(ReadOnlySpan<byte> input, int start, int end, Parameters parameters)
    {
        switch (parameters.Strategy)
        {
            case 0:
                Fast(input, start, end, parameters);
                break;
            case 1:
                DoubleFast(input, start, end, parameters);
                break;
            default:
                Lazy(input, start, end, parameters, parameters.Strategy - 2);
                break;
        }
    }

    private void Fast(ReadOnlySpan<byte> input, int start, int end, Parameters parameters)
    {
        ref byte b = ref MemoryMarshal.GetReference(input);
        int[] table = _hashTable;
        int log = parameters.HashLog;
        int length = parameters.MinMatch;
        int maxDistance = 1 << parameters.WindowLog;
        int anchor = start;
        int ip = start == 0 ? 1 : start;
        int limit = end - 8;
        int rep1 = (int)_rep1;
        int rep2 = (int)_rep2;
        while (ip < limit)
        {
            int h = Hash(ref b, ip, log, length);
            int candidate = table[h];
            table[h] = ip;
            int matchStart;
            int matchLength;
            int distance;
            int next = ip + 1;
            if (rep1 <= next && Read32(ref b, next - rep1) == Read32(ref b, next))
            {
                matchStart = next;
                distance = rep1;
                matchLength = Count(ref b, next + 4, next + 4 - rep1, end) + 4;
            }
            else if (candidate >= 0 && ip - candidate <= maxDistance && Read32(ref b, candidate) == Read32(ref b, ip))
            {
                matchStart = ip;
                distance = ip - candidate;
                matchLength = Count(ref b, ip + 4, candidate + 4, end) + 4;
                while (matchStart > anchor && candidate > 0 && Unsafe.Add(ref b, matchStart - 1) == Unsafe.Add(ref b, candidate - 1))
                {
                    matchStart--;
                    candidate--;
                    matchLength++;
                }

                rep2 = rep1;
                rep1 = distance;
            }
            else
            {
                ip += ((ip - anchor) >> SearchStrength) + 1;
                continue;
            }

            Store(input, anchor, matchStart - anchor, distance, matchLength);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip <= limit)
            {
                table[Hash(ref b, matchStart + 2, log, length)] = matchStart + 2;
                table[Hash(ref b, ip - 2, log, length)] = ip - 2;
                while (ip <= limit && rep2 <= ip && Read32(ref b, ip - rep2) == Read32(ref b, ip))
                {
                    int repeat = Count(ref b, ip + 4, ip + 4 - rep2, end) + 4;
                    (rep1, rep2) = (rep2, rep1);
                    table[Hash(ref b, ip, log, length)] = ip;
                    Store(input, ip, 0, rep1, repeat);
                    ip += repeat;
                    anchor = ip;
                }
            }
        }

        AddLiterals(input, anchor, end - anchor);
    }

    private void DoubleFast(ReadOnlySpan<byte> input, int start, int end, Parameters parameters)
    {
        ref byte b = ref MemoryMarshal.GetReference(input);
        int[] longTable = _hashTable;
        int[] shortTable = _chainTable;
        int longLog = parameters.HashLog;
        int shortLog = parameters.ChainLog;
        int length = parameters.MinMatch;
        int maxDistance = 1 << parameters.WindowLog;
        int anchor = start;
        int ip = start == 0 ? 1 : start;
        int limit = end - 8;
        int rep1 = (int)_rep1;
        int rep2 = (int)_rep2;
        while (ip < limit)
        {
            int hl = Hash(ref b, ip, longLog, 8);
            int hs = Hash(ref b, ip, shortLog, length);
            int longCandidate = longTable[hl];
            int shortCandidate = shortTable[hs];
            longTable[hl] = ip;
            shortTable[hs] = ip;
            int matchStart;
            int matchLength;
            int distance;
            int next = ip + 1;
            if (rep1 <= next && Read32(ref b, next - rep1) == Read32(ref b, next))
            {
                matchStart = next;
                distance = rep1;
                matchLength = Count(ref b, next + 4, next + 4 - rep1, end) + 4;
            }
            else
            {
                int candidate;
                if (longCandidate >= 0 && ip - longCandidate <= maxDistance && Read64(ref b, longCandidate) == Read64(ref b, ip))
                {
                    matchStart = ip;
                    candidate = longCandidate;
                    matchLength = Count(ref b, ip + 8, candidate + 8, end) + 8;
                }
                else if (shortCandidate >= 0 && ip - shortCandidate <= maxDistance && Read32(ref b, shortCandidate) == Read32(ref b, ip))
                {
                    // A short match: see whether a long one starts a byte later.
                    int h3 = Hash(ref b, next, longLog, 8);
                    int third = longTable[h3];
                    longTable[h3] = next;
                    if (third >= 0 && next - third <= maxDistance && Read64(ref b, third) == Read64(ref b, next))
                    {
                        matchStart = next;
                        candidate = third;
                        matchLength = Count(ref b, next + 8, third + 8, end) + 8;
                    }
                    else
                    {
                        matchStart = ip;
                        candidate = shortCandidate;
                        matchLength = Count(ref b, ip + 4, candidate + 4, end) + 4;
                    }
                }
                else
                {
                    ip += ((ip - anchor) >> SearchStrength) + 1;
                    continue;
                }

                distance = matchStart - candidate;
                while (matchStart > anchor && candidate > 0 && Unsafe.Add(ref b, matchStart - 1) == Unsafe.Add(ref b, candidate - 1))
                {
                    matchStart--;
                    candidate--;
                    matchLength++;
                }

                rep2 = rep1;
                rep1 = distance;
            }

            Store(input, anchor, matchStart - anchor, distance, matchLength);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip <= limit)
            {
                int at = matchStart + 2;
                longTable[Hash(ref b, at, longLog, 8)] = at;
                shortTable[Hash(ref b, at, shortLog, length)] = at;
                longTable[Hash(ref b, ip - 2, longLog, 8)] = ip - 2;
                shortTable[Hash(ref b, ip - 1, shortLog, length)] = ip - 1;
                while (ip <= limit && rep2 <= ip && Read32(ref b, ip - rep2) == Read32(ref b, ip))
                {
                    int repeat = Count(ref b, ip + 4, ip + 4 - rep2, end) + 4;
                    (rep1, rep2) = (rep2, rep1);
                    shortTable[Hash(ref b, ip, shortLog, length)] = ip;
                    longTable[Hash(ref b, ip, longLog, 8)] = ip;
                    Store(input, ip, 0, rep1, repeat);
                    ip += repeat;
                    anchor = ip;
                }
            }
        }

        AddLiterals(input, anchor, end - anchor);
    }

    /// <summary>The greedy (depth 0), lazy (1) and lazy2 (2) parsers.</summary>
    private void Lazy(ReadOnlySpan<byte> input, int start, int end, Parameters parameters, int depth)
    {
        ref byte b = ref MemoryMarshal.GetReference(input);
        int anchor = start;
        int ip = start == 0 ? 1 : start;
        int limit = end - 8;
        int rep1 = (int)_rep1;
        int rep2 = (int)_rep2;
        while (ip < limit)
        {
            int matchLength = 0;
            int distance = 0;
            bool repeat = false;
            int matchStart = ip;
            int next = ip + 1;
            if (rep1 <= next && Read32(ref b, next - rep1) == Read32(ref b, next))
            {
                matchLength = Count(ref b, next + 4, next + 4 - rep1, end) + 4;
                distance = rep1;
                repeat = true;
                matchStart = next;
            }

            if (!repeat || depth > 0)
            {
                int found = Search(ref b, ip, end, parameters, out int foundDistance);
                if (found > matchLength)
                {
                    matchLength = found;
                    distance = foundDistance;
                    repeat = false;
                    matchStart = ip;
                }
            }

            if (matchLength < 4)
            {
                ip += ((ip - anchor) >> SearchStrength) + 1;
                continue;
            }

            // Look a byte (or two) further for something better, weighing length against the cost of the offset.
            while (depth > 0 && ip < limit)
            {
                ip++;
                if (Better(ref b, ip, end, parameters, rep1, 3, 1, 4, 4, ref matchLength, ref distance, ref repeat, ref matchStart))
                {
                    continue;
                }

                if (depth == 2 && ip < limit)
                {
                    ip++;
                    if (Better(ref b, ip, end, parameters, rep1, 4, 1, 4, 7, ref matchLength, ref distance, ref repeat, ref matchStart))
                    {
                        continue;
                    }
                }

                break;
            }

            if (!repeat)
            {
                while (matchStart > anchor && matchStart - distance > 0
                    && Unsafe.Add(ref b, matchStart - 1) == Unsafe.Add(ref b, matchStart - 1 - distance))
                {
                    matchStart--;
                    matchLength++;
                }

                rep2 = rep1;
                rep1 = distance;
            }

            Store(input, anchor, matchStart - anchor, distance, matchLength);
            ip = matchStart + matchLength;
            anchor = ip;
            while (ip <= limit && rep2 <= ip && Read32(ref b, ip - rep2) == Read32(ref b, ip))
            {
                int length = Count(ref b, ip + 4, ip + 4 - rep2, end) + 4;
                (rep1, rep2) = (rep2, rep1);
                Store(input, ip, 0, rep1, length);
                ip += length;
                anchor = ip;
            }
        }

        AddLiterals(input, anchor, end - anchor);
    }

    /// <summary>
    /// Whether a repeat match or a searched match at <paramref name="ip"/> beats the current one, by libzstd's gain rule; takes it
    /// when it does. A found match counts 4 a byte less the offset's bits; the current one gets <paramref name="searchBonus"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Better(ref byte b, int ip, int end, Parameters parameters, int rep1, int repeatScale, int repeatBonus, int scale,
        int searchBonus, ref int matchLength, ref int distance, ref bool repeat, ref int matchStart)
    {
        int currentCost = BitOperations.Log2(repeat ? 1u : (uint)distance + 3);
        if (!repeat && rep1 <= ip && Read32(ref b, ip - rep1) == Read32(ref b, ip))
        {
            int length = Count(ref b, ip + 4, ip + 4 - rep1, end) + 4;
            if (length * repeatScale > (matchLength * repeatScale) - currentCost + repeatBonus)
            {
                matchLength = length;
                distance = rep1;
                repeat = true;
                matchStart = ip;
                currentCost = 0;
            }
        }

        int found = Search(ref b, ip, end, parameters, out int foundDistance);
        if (found >= 4 && (found * scale) - BitOperations.Log2((uint)foundDistance + 3) > (matchLength * scale) - currentCost + searchBonus)
        {
            matchLength = found;
            distance = foundDistance;
            repeat = false;
            matchStart = ip;
            return true;
        }

        return false;
    }

    /// <summary>Walks the hash chain from <paramref name="ip"/>; returns the longest match of four bytes or more, or 0.</summary>
    private int Search(ref byte b, int ip, int end, Parameters parameters, out int distance)
    {
        if (parameters.Rows)
        {
            return SearchRow(ref b, ip, end, parameters, out distance);
        }

        if (parameters.Tree)
        {
            return SearchTree(ref b, ip, end, parameters, out distance);
        }

        ref int hashTable = ref MemoryMarshal.GetArrayDataReference(_hashTable);
        ref int chain = ref MemoryMarshal.GetArrayDataReference(_chainTable);
        int log = parameters.HashLog;
        int length = parameters.MinMatch;
        int mask = (1 << parameters.ChainLog) - 1;
        int p = _nextToUpdate;
        if (ip - p > SkipThreshold)
        {
            for (int stop = p + SkipKeep; p < stop; p++)
            {
                ref int head = ref Unsafe.Add(ref hashTable, Hash(ref b, p, log, length));
                Unsafe.Add(ref chain, p & mask) = head;
                head = p;
            }

            p = ip - SkipTail;
        }

        for (; p < ip; p++)
        {
            ref int head = ref Unsafe.Add(ref hashTable, Hash(ref b, p, log, length));
            Unsafe.Add(ref chain, p & mask) = head;
            head = p;
        }

        _nextToUpdate = ip;
        int match = Unsafe.Add(ref hashTable, Hash(ref b, ip, log, length));
        int low = Math.Max(0, ip - (1 << parameters.WindowLog));
        int oldest = Math.Max(0, ip - mask - 1);
        int best = 3;
        int left = end - ip;
        distance = 0;
        for (int attempts = 1 << parameters.SearchLog; attempts > 0 && match >= low; attempts--)
        {
            // Only a candidate that matches at the current best length can beat it.
            if (Unsafe.Add(ref b, match + best) == Unsafe.Add(ref b, ip + best) && Read32(ref b, match) == Read32(ref b, ip))
            {
                int found = Count(ref b, ip + 4, match + 4, end) + 4;
                if (found > best)
                {
                    best = found;
                    distance = ip - match;
                    if (found >= left - 1)
                    {
                        break;
                    }
                }
            }

            if (match <= oldest)
            {
                break;
            }

            match = Unsafe.Add(ref chain, match & mask);
        }

        return best >= 4 ? best : 0;
    }

    /// <summary>
    /// A binary tree match finder: the positions with one hash sit in a tree ordered by the bytes that follow them, which each
    /// new position is inserted into from the root while the walk notes the longest match on the way, so a deep search costs a
    /// path through the tree rather than a list of every earlier position.
    /// </summary>
    private int SearchTree(ref byte b, int ip, int end, Parameters parameters, out int distance)
    {
        distance = 0;
        if (ip < _nextToUpdate)
        {
            return 0;
        }

        int p = _nextToUpdate;
        if (ip - p > SkipThreshold)
        {
            for (int stop = p + SkipKeep; p < stop; p++)
            {
                InsertTree(ref b, p, end, parameters, out _);
            }

            p = ip - SkipTail;
        }

        for (; p < ip; p++)
        {
            InsertTree(ref b, p, end, parameters, out _);
        }

        _nextToUpdate = ip + 1;
        return InsertTree(ref b, ip, end, parameters, out distance);
    }

    private int InsertTree(ref byte b, int cur, int end, Parameters parameters, out int distance)
    {
        ref int hashTable = ref MemoryMarshal.GetArrayDataReference(_hashTable);
        ref int tree = ref MemoryMarshal.GetArrayDataReference(_chainTable);
        int cycle = 1 << (parameters.ChainLog - 1);
        int mask = cycle - 1;
        ref int head = ref Unsafe.Add(ref hashTable, Hash(ref b, cur, parameters.HashLog, parameters.MinMatch));
        int match = head;
        head = cur;
        int low = Math.Max(0, Math.Max(cur - (1 << parameters.WindowLog), cur - cycle + 1));
        int limit = end - cur;

        // The two links still to fill: the smaller subtree's and the larger one's, and how far each side is known to match.
        ref int smaller = ref Unsafe.Add(ref tree, 2 * (cur & mask));
        ref int larger = ref Unsafe.Add(ref tree, (2 * (cur & mask)) + 1);
        int smallerLength = 0;
        int largerLength = 0;
        int best = 3;
        distance = 0;
        for (int attempts = 1 << parameters.SearchLog; ; attempts--)
        {
            if (match < low || attempts == 0)
            {
                smaller = -1;
                larger = -1;
                break;
            }

            ref int pair = ref Unsafe.Add(ref tree, 2 * (match & mask));
            int length = Math.Min(smallerLength, largerLength);
            length += Count(ref b, cur + length, match + length, end);
            if (length > best)
            {
                best = length;
                distance = cur - match;
            }

            if (length >= limit)
            {
                // The whole rest matches, so this position's place in the order is the match's: take over its children.
                smaller = pair;
                larger = Unsafe.Add(ref pair, 1);
                break;
            }

            if (Unsafe.Add(ref b, match + length) < Unsafe.Add(ref b, cur + length))
            {
                smaller = match;
                smallerLength = length;
                smaller = ref Unsafe.Add(ref pair, 1);
                match = smaller;
            }
            else
            {
                larger = match;
                largerLength = length;
                larger = ref pair;
                match = larger;
            }
        }

        return best >= 4 ? best : 0;
    }

    /// <summary>
    /// libzstd's row match finder for the greedy and lazy levels: the hash picks a row of 16 or 32 recent positions, each with an
    /// eight-bit tag from more of the hash, and one vector compare of the tags picks the candidates worth checking, newest first.
    /// </summary>
    private int SearchRow(ref byte b, int ip, int end, Parameters parameters, out int distance)
    {
        ref int positions = ref MemoryMarshal.GetArrayDataReference(_hashTable);
        ref byte tags = ref MemoryMarshal.GetArrayDataReference(_rowTags);
        ref byte heads = ref MemoryMarshal.GetArrayDataReference(_rowHeads);
        int rowLog = parameters.RowLog;
        int rowMask = (1 << rowLog) - 1;
        int log = parameters.HashLog - rowLog + 8;
        int length = parameters.MinMatch;
        int p = _nextToUpdate;
        if (ip - p > SkipThreshold)
        {
            // A long stretch without matches: insert its start and its end only, as libzstd does.
            for (int stop = p + SkipKeep; p < stop; p++)
            {
                InsertRow(p, CachedHash(ref b, p, log, length, rowLog), rowLog, rowMask, ref positions, ref tags, ref heads);
            }

            p = ip - SkipTail;
        }

        for (; p < ip; p++)
        {
            InsertRow(p, CachedHash(ref b, p, log, length, rowLog), rowLog, rowMask, ref positions, ref tags, ref heads);
        }

        _nextToUpdate = ip;
        int hash = CachedHash(ref b, ip, log, length, rowLog);
        int start = (hash >> 8) << rowLog;
        int first = Unsafe.Add(ref heads, hash >> 8);
        Vector128<byte> tag = Vector128.Create((byte)hash);
        uint mask = Vector128.Equals(Vector128.LoadUnsafe(ref tags, (nuint)start), tag).ExtractMostSignificantBits();
        if (rowLog == 5)
        {
            mask |= Vector128.Equals(Vector128.LoadUnsafe(ref tags, (nuint)(start + 16)), tag).ExtractMostSignificantBits() << 16;
            mask = BitOperations.RotateRight(mask, first);
        }
        else
        {
            mask = ((mask >> first) | (mask << (16 - first))) & 0xFFFF;
        }

        int low = Math.Max(0, ip - (1 << parameters.WindowLog));
        int best = 3;
        int left = end - ip;
        distance = 0;
        for (int attempts = Math.Min(1 << parameters.SearchLog, rowMask + 1); mask != 0 && attempts > 0; attempts--)
        {
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int match = Unsafe.Add(ref positions, start + ((first + k) & rowMask));
            if (match < low)
            {
                break;
            }

            if (Unsafe.Add(ref b, match + best) == Unsafe.Add(ref b, ip + best) && Read32(ref b, match) == Read32(ref b, ip))
            {
                int found = Count(ref b, ip + 4, match + 4, end) + 4;
                if (found > best)
                {
                    best = found;
                    distance = ip - match;
                    if (found >= left - 1)
                    {
                        break;
                    }
                }
            }
        }

        return best >= 4 ? best : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertRow(int p, int h, int rowLog, int rowMask, ref int positions, ref byte tags, ref byte heads)
    {
        int row = (h >> 8) << rowLog;
        ref byte head = ref Unsafe.Add(ref heads, h >> 8);
        head = (byte)((head - 1) & rowMask);
        Unsafe.Add(ref tags, row + head) = (byte)h;
        Unsafe.Add(ref positions, row + head) = p;
    }

    /// <summary>
    /// The row hash of <paramref name="p"/>, from hashes worked out eight positions ahead, each with its row fetched into the
    /// cache then, so the row is there by the time it is needed (libzstd's hash cache).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CachedHash(ref byte b, int p, int log, int length, int rowLog)
    {
        if (_hashed < p)
        {
            _hashed = p;
        }

        int ahead = Math.Min(p + 8, _hashLimit);
        while (_hashed < ahead)
        {
            int h = Hash(ref b, _hashed, log, length);
            _hashCache[_hashed & 7] = h;
            Prefetch(ref _rowTags[(h >> 8) << rowLog]);
            Prefetch(ref Unsafe.As<int, byte>(ref _hashTable[(h >> 8) << rowLog]));
            _hashed++;
        }

        return p < _hashed && p >= _hashed - 8 ? _hashCache[p & 7] : Hash(ref b, p, log, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Prefetch(ref byte at)
    {
        // A hint only: an address the collector has since moved costs nothing but the fetch.
        if (Sse.IsSupported)
        {
            Sse.Prefetch0(Unsafe.AsPointer(ref at));
        }
    }

    /// <summary>Hashes the <paramref name="length"/> bytes (4 to 8) at <paramref name="p"/> to <paramref name="log"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ref byte b, int p, int log, int length) =>
        (int)(((Read64(ref b, p) << (64 - (8 * length))) * 0xCF1BBCDCB7A56463UL) >> (64 - log));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Read32(ref byte b, int p)
    {
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, p));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Read64(ref byte b, int p)
    {
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, p));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }

    /// <summary>How many bytes from <paramref name="a"/> equal those from <paramref name="m"/> (which lies before it), up to <paramref name="end"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Count(ref byte b, int a, int m, int end)
    {
        int from = a;
        while (a + 8 <= end)
        {
            ulong x = Read64(ref b, a) ^ Read64(ref b, m);
            if (x != 0)
            {
                return a - from + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            a += 8;
            m += 8;
        }

        while (a < end && Unsafe.Add(ref b, a) == Unsafe.Add(ref b, m))
        {
            a++;
            m++;
        }

        return a - from;
    }
}
