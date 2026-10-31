using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Lzma;

/// <summary>
/// The match finders of the LZMA encoder, as XZ Utils names them: hash chains over a 4-byte hash (HC4, for presets 0 to 3, with
/// a 3-byte hash for HC3 at preset 0) and binary trees (BT4, presets 4 to 9), each with 2- and 3-byte hashes for short matches.
/// <see cref="Find"/> lists the matches at a position, each longer than the last and as near as any of its length, and inserts
/// the position; <see cref="Skip"/> only inserts.
/// </summary>
internal sealed class LzmaMatchFinder
{
    /// <summary>The longest match LZMA codes.</summary>
    public const int MaxLength = 273;

    private const int Hash2Size = 1 << 10;
    private const int Hash3Size = 1 << 16;

    private int[] _hash2 = [];
    private int[] _hash3 = [];
    private int[] _hash4 = [];
    private int[] _son = [];
    private int _hash4Bits;
    private int _cycle;
    private int _depth;
    private int _nice;
    private bool _tree;
    private bool _three;

    /// <summary>Gets the distances (less one) of the matches <see cref="Find"/> found, beside <see cref="Lengths"/>.</summary>
    public int[] Distances { get; } = new int[MaxLength + 1];

    /// <summary>Gets the lengths of the matches <see cref="Find"/> found, increasing.</summary>
    public int[] Lengths { get; } = new int[MaxLength + 1];

    /// <summary>Sets the finder up for an input of <paramref name="length"/> bytes and a dictionary of <paramref name="dictionary"/>.</summary>
    public void Reset(int length, int dictionary, bool tree, bool three, int nice, int depth)
    {
        _tree = tree;
        _three = three;
        _nice = nice;
        _depth = depth;
        _cycle = Math.Max(1, Math.Min(dictionary, length)) + 1;
        _hash4Bits = Math.Clamp(BitOperations.Log2((uint)Math.Max(_cycle, 2)) - 1, 16, 24);
        Fill(ref _hash2, Hash2Size);
        Fill(ref _hash3, Hash3Size);
        Fill(ref _hash4, three ? 0 : 1 << _hash4Bits);
        int sonSize = tree ? 2 * _cycle : _cycle;
        if (_son.Length < sonSize)
        {
            _son = new int[sonSize];
        }
    }

    private static void Fill(ref int[] table, int size)
    {
        if (table.Length < size)
        {
            table = new int[size];
        }

        table.AsSpan(0, size).Fill(-1);
    }

    /// <summary>
    /// Lists the matches at <paramref name="pos"/> into <see cref="Lengths"/> and <see cref="Distances"/> and inserts the position;
    /// returns how many. Matches stop at <paramref name="end"/> and at <see cref="MaxLength"/>.
    /// </summary>
    public int Find(ReadOnlySpan<byte> data, int pos, int end) => Insert(data, pos, end, true);

    /// <summary>Inserts <paramref name="pos"/> without listing matches.</summary>
    public void Skip(ReadOnlySpan<byte> data, int pos, int end) => Insert(data, pos, end, false);

    private int Insert(ReadOnlySpan<byte> data, int pos, int end, bool find)
    {
        int available = Math.Min(end - pos, MaxLength);
        int low = Math.Max(0, pos - _cycle + 1);
        if (available < 4)
        {
            // Too near the end to hash; nothing is inserted, as no later position can match these bytes for long.
            return 0;
        }

        ref byte b = ref MemoryMarshal.GetReference(data);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        int h2 = (int)(((value & 0xFFFF) * 0x9E3779B1u) >> 22);
        int h3 = (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> 16);
        int count = 0;
        int best = 1;
        int c2 = _hash2[h2];
        int c3 = _hash3[h3];
        _hash2[h2] = pos;
        _hash3[h3] = pos;
        int head;
        if (_three)
        {
            head = c3;
        }
        else
        {
            int h4 = (int)((value * 0x9E3779B1u) >> (32 - _hash4Bits));
            head = _hash4[h4];
            _hash4[h4] = pos;
        }

        if (find)
        {
            // Two- and three-byte matches from their own hashes, when nearer than what the longer search will find.
            if (c2 >= low && Unsafe.Add(ref b, c2) == Unsafe.Add(ref b, pos) && Unsafe.Add(ref b, c2 + 1) == Unsafe.Add(ref b, pos + 1))
            {
                best = 2;
                Lengths[count] = 2;
                Distances[count++] = pos - c2 - 1;
            }

            if (c3 >= low && c3 != c2 && Count(ref b, pos, c3, available) >= 3)
            {
                best = 3;
                if (count > 0 && Lengths[count - 1] == 2 && pos - c3 - 1 <= Distances[count - 1])
                {
                    count--;
                }

                Lengths[count] = 3;
                Distances[count++] = pos - c3 - 1;
            }

            if (count > 0)
            {
                // A short match may already run long.
                int length = Count(ref b, pos, pos - Distances[count - 1] - 1, available);
                if (length > best)
                {
                    best = length;
                    Lengths[count - 1] = length;
                }

                if (best >= _nice || best >= available)
                {
                    if (_tree)
                    {
                        SkipTree(ref b, pos, head, available, low);
                    }
                    else
                    {
                        _son[pos % _cycle] = head;
                    }

                    return count;
                }
            }
        }

        return _tree ? Tree(ref b, pos, head, available, low, find, count, best) : Chain(ref b, pos, head, available, low, find, count, best);
    }

    private int Chain(ref byte b, int pos, int match, int available, int low, bool find, int count, int best)
    {
        _son[pos % _cycle] = match;
        if (!find)
        {
            return 0;
        }

        for (int depth = _depth; depth > 0 && match >= low; depth--)
        {
            if (Unsafe.Add(ref b, match + best) == Unsafe.Add(ref b, pos + best))
            {
                int length = Count(ref b, pos, match, available);
                if (length > best)
                {
                    best = length;
                    Lengths[count] = length;
                    Distances[count++] = pos - match - 1;
                    if (length >= _nice || length >= available)
                    {
                        break;
                    }
                }
            }

            match = _son[match % _cycle];
        }

        return count;
    }

    private int Tree(ref byte b, int pos, int match, int available, int low, bool find, int count, int best)
    {
        int cycle = _cycle;
        ref int son = ref MemoryMarshal.GetArrayDataReference(_son);
        ref int smaller = ref Unsafe.Add(ref son, 2 * (pos % cycle));
        ref int larger = ref Unsafe.Add(ref son, (2 * (pos % cycle)) + 1);
        int smallerLength = 0;
        int largerLength = 0;
        for (int depth = _depth; ; depth--)
        {
            if (match < low || depth == 0)
            {
                smaller = -1;
                larger = -1;
                return count;
            }

            ref int pair = ref Unsafe.Add(ref son, 2 * (match % cycle));
            int length = Math.Min(smallerLength, largerLength);
            if (Unsafe.Add(ref b, match + length) == Unsafe.Add(ref b, pos + length))
            {
                length += Count(ref b, pos + length, match + length, available - length);
                if (find && length > best)
                {
                    best = length;
                    Lengths[count] = length;
                    Distances[count++] = pos - match - 1;
                }

                if (length >= available || length >= _nice)
                {
                    smaller = pair;
                    larger = Unsafe.Add(ref pair, 1);
                    return count;
                }
            }

            if (Unsafe.Add(ref b, match + length) < Unsafe.Add(ref b, pos + length))
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
    }

    private void SkipTree(ref byte b, int pos, int head, int available, int low) => Tree(ref b, pos, head, available, low, false, 0, 0);

    /// <summary>How many bytes from <paramref name="a"/> equal those from <paramref name="m"/>, up to <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Count(ref byte b, int a, int m, int limit)
    {
        int n = 0;
        while (n + 8 <= limit)
        {
            ulong x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, a + n)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, m + n));
            if (x != 0)
            {
                return n + (BitConverter.IsLittleEndian ? BitOperations.TrailingZeroCount(x) : BitOperations.LeadingZeroCount(x)) / 8;
            }

            n += 8;
        }

        while (n < limit && Unsafe.Add(ref b, a + n) == Unsafe.Add(ref b, m + n))
        {
            n++;
        }

        return n;
    }
}
