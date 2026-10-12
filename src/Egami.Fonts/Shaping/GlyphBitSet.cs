using System.Numerics;

namespace Egami.Fonts.Shaping;

/// <summary>
/// A set of glyph ids below a fixed bound, as the bit sets HarfBuzz's closure works with: ids at or past the bound are not
/// added. <see cref="Next"/> walks it as hb_set_t's next does, with <see cref="Invalid"/> before the first id and after the last.
/// </summary>
internal sealed class GlyphBitSet
{
    public const uint Invalid = uint.MaxValue;

    private readonly ulong[] _words;

    public GlyphBitSet(int bound)
    {
        Bound = bound;
        _words = new ulong[(bound + 63) >> 6];
    }

    /// <summary>Gets the bound: ids from 0 up to, not including, it fit.</summary>
    public int Bound { get; }

    public int Count { get; private set; }

    public bool Contains(uint glyph) => glyph < (uint)Bound && (_words[glyph >> 6] & (1UL << (int)(glyph & 63))) != 0;

    /// <summary>Adds a glyph; one at or past the bound is ignored.</summary>
    public void Add(uint glyph)
    {
        if (glyph >= (uint)Bound)
        {
            return;
        }

        ref ulong word = ref _words[glyph >> 6];
        ulong bit = 1UL << (int)(glyph & 63);
        if ((word & bit) == 0)
        {
            word |= bit;
            Count++;
        }
    }

    public void Clear()
    {
        Array.Clear(_words);
        Count = 0;
    }

    public void CopyFrom(GlyphBitSet other)
    {
        other._words.CopyTo(_words, 0);
        Count = other.Count;
    }

    public void UnionWith(GlyphBitSet other)
    {
        int count = 0;
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i] |= other._words[i];
            count += BitOperations.PopCount(_words[i]);
        }

        Count = count;
    }

    public bool IsSubsetOf(GlyphBitSet other)
    {
        for (int i = 0; i < _words.Length; i++)
        {
            if ((_words[i] & ~other._words[i]) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Moves <paramref name="glyph"/> to the next id in the set (the first after <see cref="Invalid"/>); false at the end.</summary>
    public bool Next(ref uint glyph)
    {
        long start = glyph == Invalid ? 0 : (long)glyph + 1;
        if (start < Bound)
        {
            int index = (int)(start >> 6);
            ulong word = _words[index] & (ulong.MaxValue << (int)(start & 63));
            while (true)
            {
                if (word != 0)
                {
                    glyph = (uint)((index << 6) + BitOperations.TrailingZeroCount(word));
                    return true;
                }

                if (++index >= _words.Length)
                {
                    break;
                }

                word = _words[index];
            }
        }

        glyph = Invalid;
        return false;
    }

    /// <summary>Gets the smallest id, or <see cref="Invalid"/> when empty.</summary>
    public uint Min
    {
        get
        {
            uint g = Invalid;
            Next(ref g);
            return g;
        }
    }

    /// <summary>Gets the largest id, or <see cref="Invalid"/> when empty.</summary>
    public uint Max
    {
        get
        {
            for (int i = _words.Length - 1; i >= 0; i--)
            {
                if (_words[i] != 0)
                {
                    return (uint)((i << 6) + 63 - BitOperations.LeadingZeroCount(_words[i]));
                }
            }

            return Invalid;
        }
    }

    /// <summary>Whether any id from <paramref name="first"/> to <paramref name="last"/> is in the set.</summary>
    public bool IntersectsRange(uint first, uint last)
    {
        uint g = first - 1;
        return Next(ref g) && g <= last;
    }

    /// <summary>Adds the ids of <paramref name="source"/> from <paramref name="first"/> to <paramref name="last"/>.</summary>
    public void AddRange(GlyphBitSet source, uint first, uint last)
    {
        for (uint g = first - 1; source.Next(ref g) && g <= last;)
        {
            Add(g);
        }
    }

    /// <summary>The ids, ascending.</summary>
    public IEnumerable<uint> Glyphs()
    {
        for (uint g = Invalid; Next(ref g);)
        {
            yield return g;
        }
    }
}
