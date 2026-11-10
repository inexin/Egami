using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// A prefix-code lookup built from a codebook's code words and lengths: the first <see cref="RootBits"/> bits index a root
/// table, and the few longer codes go through second-level tables indexed by the bits after them. Each entry packs the symbol
/// (its index in the codebook) and the code length, so a lookup is one or two array reads after a 32-bit peek.
/// </summary>
internal sealed class AacHuffman
{
    private const int RootBits = 10;

    // An entry is symbol << 8 | length; a link to a second-level table is (offset << 8 | bits) with the top bit set.
    private const int Link = 1 << 30;

    private readonly int[] _table;

    /// <summary>Builds the lookup for a codebook.</summary>
    /// <param name="codes">Each symbol's code word, right-aligned.</param>
    /// <param name="lengths">Each symbol's code length in bits, 1 to 32.</param>
    public AacHuffman(ReadOnlySpan<uint> codes, ReadOnlySpan<byte> lengths)
    {
        int maxLength = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            maxLength = Math.Max(maxLength, lengths[i]);
        }

        // Second-level tables, one per root slot that long codes share, each as wide as its longest code needs.
        int[] extra = new int[1 << RootBits];
        for (int i = 0; i < codes.Length; i++)
        {
            if (lengths[i] > RootBits)
            {
                int root = (int)(codes[i] >> (lengths[i] - RootBits));
                extra[root] = Math.Max(extra[root], lengths[i] - RootBits);
            }
        }

        int size = 1 << RootBits;
        int[] offsets = new int[1 << RootBits];
        for (int r = 0; r < extra.Length; r++)
        {
            if (extra[r] > 0)
            {
                offsets[r] = size;
                size += 1 << extra[r];
            }
        }

        _table = new int[size];
        for (int r = 0; r < extra.Length; r++)
        {
            if (extra[r] > 0)
            {
                _table[r] = Link | (offsets[r] << 8) | extra[r];
            }
        }

        for (int i = 0; i < codes.Length; i++)
        {
            int length = lengths[i];
            uint code = codes[i];
            int entry = (i << 8) | length;
            if (length <= RootBits)
            {
                int first = (int)(code << (RootBits - length));
                _table.AsSpan(first, 1 << (RootBits - length)).Fill(entry);
            }
            else
            {
                int root = (int)(code >> (length - RootBits));
                int bits = extra[root];
                int rest = (int)(code & ((1u << (length - RootBits)) - 1));
                int first = offsets[root] + (rest << (bits - (length - RootBits)));
                _table.AsSpan(first, 1 << (bits - (length - RootBits))).Fill(entry);
            }
        }

        MaxLength = maxLength;
    }

    /// <summary>Gets the longest code length.</summary>
    public int MaxLength { get; }

    /// <summary>Reads one code word and returns its symbol, or -1 for bits that start no code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(ref AacBitReader reader)
    {
        uint bits = reader.Peek32();
        int entry = _table[bits >> (32 - RootBits)];
        if ((entry & Link) != 0)
        {
            int width = entry & 0xFF;
            int offset = (entry & ~Link) >> 8;
            entry = _table[offset + (int)((bits << RootBits) >> (32 - width))];
            if (entry == 0)
            {
                return -1;
            }

            reader.Skip(entry & 0xFF);
            return entry >> 8;
        }

        if (entry == 0)
        {
            return -1;
        }

        reader.Skip(entry & 0xFF);
        return entry >> 8;
    }
}
