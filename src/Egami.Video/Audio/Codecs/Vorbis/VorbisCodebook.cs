// Follows the Vorbis I specification (Xiph.Org), section 3 "Probability Model and Codebooks": codebook header decode (3.2.1),
// Huffman codeword assignment with the single-entry errata, VQ lookup tables of types 1 and 2 (3.2.1, 9.2.2, 9.2.3) and
// scalar and VQ context decode (3.3).
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>A Vorbis codebook: its Huffman tree and, when it has one, its expanded VQ lookup table.</summary>
internal sealed class VorbisCodebook
{
    /// <summary>The largest number of entries one codebook may declare.</summary>
    public const int MaxEntries = 1 << 20;

    /// <summary>The largest expanded VQ table (entries times dimensions) one codebook may have.</summary>
    public const int MaxTableValues = 1 << 22;

    private const int FastBits = 10;

    // Fast table, indexed by the next tableBits bits in stream order: (entry << 6) | length for a codeword at most tableBits
    // long, -(node + 2) for a prefix of a longer one (walk the tree from that node), -1 for no codeword.
    private readonly int[] _fast;
    private readonly int _tableBits;

    // Tree: children of node k at 2k (bit 0) and 2k + 1 (bit 1); a child >= 0 is a node, ~entry a leaf, int.MinValue none.
    private readonly int[] _tree;

    private VorbisCodebook(int dimensions, int entries, int[] fast, int tableBits, int[] tree, float[]? values)
    {
        Dimensions = dimensions;
        Entries = entries;
        _fast = fast;
        _tableBits = tableBits;
        _tree = tree;
        Values = values;
    }

    public int Dimensions { get; }

    public int Entries { get; }

    /// <summary>Gets the expanded VQ table, <see cref="Dimensions"/> values per entry, or null for lookup type 0.</summary>
    public float[]? Values { get; }

    public bool HasLookup => Values is not null;

    /// <summary>Reads one codebook from the setup header (3.2.1); <paramref name="budget"/> limits the total table sizes.</summary>
    public static VorbisCodebook Read(ref VorbisBitReader r, ref long budget)
    {
        if (r.ReadBits(24) != 0x564342)
        {
            throw Vorbis.Corrupt("A codebook has no sync pattern.");
        }

        int dimensions = (int)r.ReadBits(16);
        int entries = (int)r.ReadBits(24);
        if (entries > MaxEntries)
        {
            throw Vorbis.Unsupported($"a codebook with {entries} entries is larger than this decoder accepts.");
        }

        budget -= entries;
        if (budget < 0)
        {
            throw Vorbis.Unsupported("the codebooks are larger than this decoder accepts.");
        }

        byte[] lengths = new byte[entries];
        bool ordered = r.ReadBit();
        if (!ordered)
        {
            bool sparse = r.ReadBit();
            for (int i = 0; i < entries; i++)
            {
                if (!sparse || r.ReadBit())
                {
                    lengths[i] = (byte)(r.ReadBits(5) + 1);
                }
            }
        }
        else
        {
            int length = (int)r.ReadBits(5) + 1;
            int current = 0;
            while (current < entries)
            {
                if (length > 32)
                {
                    throw Vorbis.Corrupt("A codebook has a codeword longer than 32 bits.");
                }

                int number = (int)r.ReadBits(Vorbis.ILog(entries - current));
                if (number > entries - current)
                {
                    throw Vorbis.Corrupt("A codebook's ordered lengths overrun its entries.");
                }

                lengths.AsSpan(current, number).Fill((byte)length);
                current += number;
                length++;
            }
        }

        (int[] tree, int[] fast, int tableBits) = BuildTree(lengths);

        int lookupType = (int)r.ReadBits(4);
        float[]? values = null;
        if (lookupType is 1 or 2)
        {
            float minimum = Vorbis.Float32Unpack(r.ReadBits(32));
            float delta = Vorbis.Float32Unpack(r.ReadBits(32));
            int valueBits = (int)r.ReadBits(4) + 1;
            bool sequence = r.ReadBit();
            if (dimensions == 0)
            {
                throw Vorbis.Corrupt("A codebook with a lookup table has no dimensions.");
            }

            long tableValues = (long)entries * dimensions;
            if (tableValues > MaxTableValues || (budget -= tableValues) < 0)
            {
                throw Vorbis.Unsupported("a codebook's lookup table is larger than this decoder accepts.");
            }

            long lookupValues = lookupType == 1 ? Vorbis.Lookup1Values(entries, dimensions) : tableValues;
            if (lookupValues * valueBits > r.BitsLeft)
            {
                throw Vorbis.Corrupt("A codebook's lookup table runs past the end of the header.");
            }

            int[] multiplicands = new int[lookupValues];
            for (int i = 0; i < multiplicands.Length; i++)
            {
                multiplicands[i] = (int)r.ReadBits(valueBits);
            }

            values = new float[tableValues];
            for (int e = 0; e < entries; e++)
            {
                float last = 0;
                int divisor = 1;
                for (int i = 0; i < dimensions; i++)
                {
                    int offset = lookupType == 1 ? (int)((uint)(e / divisor) % (uint)lookupValues) : (e * dimensions) + i;
                    float value = (multiplicands[offset] * delta) + minimum + last;
                    values[(e * dimensions) + i] = value;
                    if (sequence)
                    {
                        last = value;
                    }

                    if (lookupType == 1)
                    {
                        divisor = (int)Math.Min((long)divisor * lookupValues, int.MaxValue);
                    }
                }
            }
        }
        else if (lookupType != 0)
        {
            throw Vorbis.Corrupt($"A codebook has the reserved lookup type {lookupType}.");
        }

        return new VorbisCodebook(dimensions, entries, fast, tableBits, tree, values);
    }

    /// <summary>
    /// Assigns each used entry, in order, the lowest free codeword of its length (3.2.1) and builds the decode tree and table.
    /// An over- or underspecified tree is an error, except a single used entry of length 1, which reads one bit of either value.
    /// </summary>
    private static (int[] Tree, int[] Fast, int TableBits) BuildTree(byte[] lengths)
    {
        int used = 0;
        int maxLength = 0;
        int single = -1;
        foreach (byte length in lengths)
        {
            if (length > 0)
            {
                used++;
                maxLength = Math.Max(maxLength, length);
            }
        }

        // A tree of 'used' leaves has used - 1 internal nodes.
        int[] tree = new int[Math.Max(2, 2 * (used - 1))];
        tree.AsSpan().Fill(int.MinValue);
        if (used == 0)
        {
            return (tree, [-1], 0);
        }

        if (used == 1)
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] > 0)
                {
                    single = i;
                }
            }

            if (lengths[single] != 1)
            {
                throw Vorbis.Corrupt("A single-entry codebook's codeword is not one bit long.");
            }

            tree[0] = ~single;
            tree[1] = ~single;
            return (tree, [(single << 6) | 1, (single << 6) | 1], 1);
        }

        // free[d] is the free node at depth d on the path of the last assigned codeword (-1 none), as a tree node index whose
        // parent slot is freeSlot[d]. The deepest free node is the lowest-valued codeword available.
        Span<int> freeSlot = stackalloc int[33];
        freeSlot.Fill(-1);
        int nodes = 1;
        bool rootFree = true;
        for (int entry = 0; entry < lengths.Length; entry++)
        {
            int length = lengths[entry];
            if (length == 0)
            {
                continue;
            }

            int depth = length;
            while (depth > 0 && freeSlot[depth] < 0)
            {
                depth--;
            }

            int slot;
            if (depth == 0)
            {
                if (!rootFree)
                {
                    throw Vorbis.Corrupt("A codebook's Huffman tree is overspecified.");
                }

                // Only the very first codeword starts at the root, which is node 0: descend its left edge.
                rootFree = false;
                slot = -1;
            }
            else
            {
                slot = freeSlot[depth];
                freeSlot[depth] = -1;
            }

            // Walk down from the free node to the codeword's depth, always left, leaving the right siblings free.
            int node;
            if (depth == length)
            {
                tree[slot] = ~entry;
                continue;
            }

            if (slot < 0)
            {
                node = 0;
            }
            else
            {
                if (nodes >= tree.Length / 2)
                {
                    throw Vorbis.Corrupt("A codebook's Huffman tree is overspecified.");
                }

                node = nodes++;
                tree[slot] = node;
            }

            for (int d = depth + 1; d <= length; d++)
            {
                freeSlot[d] = (2 * node) + 1;
                if (d == length)
                {
                    tree[2 * node] = ~entry;
                }
                else
                {
                    if (nodes >= tree.Length / 2)
                    {
                        throw Vorbis.Corrupt("A codebook's Huffman tree is overspecified.");
                    }

                    int child = nodes++;
                    tree[2 * node] = child;
                    node = child;
                }
            }
        }

        for (int d = 1; d <= 32; d++)
        {
            if (freeSlot[d] >= 0)
            {
                throw Vorbis.Corrupt("A codebook's Huffman tree is underspecified.");
            }
        }

        int tableBits = Math.Min(FastBits, maxLength);
        int[] fast = new int[1 << tableBits];
        for (int prefix = 0; prefix < fast.Length; prefix++)
        {
            int node = 0;
            int value = -1;
            for (int bit = 0; bit < tableBits; bit++)
            {
                int child = tree[(2 * node) + ((prefix >> bit) & 1)];
                if (child == int.MinValue)
                {
                    break;
                }

                if (child < 0)
                {
                    value = (~child << 6) | (bit + 1);
                    break;
                }

                node = child;
                if (bit == tableBits - 1)
                {
                    value = -(node + 2);
                }
            }

            fast[prefix] = value;
        }

        return (tree, fast, tableBits);
    }

    /// <summary>Reads one entry number (scalar context); -1 at the end of the packet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeScalar(ref VorbisBitReader r)
    {
        uint peek = r.Peek32();
        int e = _fast[peek & ((1u << _tableBits) - 1)];
        if (e >= 0)
        {
            return r.Skip(e & 63) ? e >> 6 : -1;
        }

        return DecodeSlow(ref r, peek, e);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int DecodeSlow(ref VorbisBitReader r, uint peek, int e)
    {
        if (e == -1)
        {
            r.EndOfPacket();
            return -1;
        }

        int node = -(e + 2);
        for (int bit = _tableBits; bit < 32; bit++)
        {
            int child = _tree[(2 * node) + (int)((peek >> bit) & 1)];
            if (child < 0)
            {
                if (child == int.MinValue)
                {
                    break;
                }

                return r.Skip(bit + 1) ? ~child : -1;
            }

            node = child;
        }

        r.EndOfPacket();
        return -1;
    }
}
