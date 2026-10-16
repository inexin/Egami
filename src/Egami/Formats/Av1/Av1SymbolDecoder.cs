using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Av1;

/// <summary>
/// The multi-symbol arithmetic decoder of the AV1 specification (section 8.2), in the form of libaom's od_ec_dec (BSD 2-clause,
/// see THIRD-PARTY-NOTICES): a 64-bit window of the tile's bytes, symbols read against CDFs held as 32768 minus the cumulative
/// probability, each CDF adapting after its symbol unless the frame disables CDF updates. Past the tile's end it reads zeros.
/// </summary>
internal sealed class Av1SymbolDecoder
{
    private const int WindowSize = 64;
    private const int ProbShift = 6;
    private const int MinProb = 4;
    private const int LotsOfBits = 0x4000;

    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _end;
    private readonly bool _adapt;
    private int _position;
    private ulong _dif;
    private uint _rng;
    private int _cnt;
    private int _tellOffs;

    /// <summary>Starts decoding a tile's data.</summary>
    /// <param name="data">The buffer holding the tile.</param>
    /// <param name="start">Where the tile's data starts.</param>
    /// <param name="length">The tile's size.</param>
    /// <param name="disableCdfUpdate">Whether the frame's disable_cdf_update flag is set.</param>
    internal Av1SymbolDecoder(byte[] data, int start, int length, bool disableCdfUpdate)
    {
        _data = data;
        _start = _position = start;
        _end = start + length;
        _adapt = !disableCdfUpdate;
        _dif = (1UL << (WindowSize - 1)) - 1;
        _rng = 0x8000;
        _cnt = -15;
        // Tell counts as libaom's 32-bit window does: 1 before the first symbol.
        _tellOffs = 10 - (32 - 8);
        Refill();
    }

    /// <summary>Gets how many bits the symbols so far took (od_ec_dec_tell).</summary>
    internal int Tell => ((_position - _start) * 8) - _cnt + _tellOffs;

    /// <summary>Gets whether decoding has gone past the tile's end (aom_reader_has_overflowed).</summary>
    internal bool Overflowed => (Tell + 7) >> 3 > _end - _start;

    /// <summary>
    /// Checks the tile's trailing bits after its last symbol: a one bit right after the bits used, then zeros to the end.
    /// </summary>
    internal bool TrailingBitsValid()
    {
        if (Overflowed)
        {
            return false;
        }

        int bits = Tell;
        int at = _start + ((bits + 7) >> 3);
        int pattern = 128 >> ((bits - 1) & 7);
        if ((_data[at - 1] & ((2 * pattern) - 1)) != pattern)
        {
            return false;
        }

        for (; at < _end; at++)
        {
            if (_data[at] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a symbol against an adaptive CDF of <paramref name="n"/> symbols starting at <paramref name="at"/>.</summary>
    internal int Symbol(ushort[] cdf, int n, int at = 0)
    {
        // The CDF and its counter lie inside the array, checked once, so the reads and the adaptation go without checks.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)(at + n), (uint)cdf.Length, nameof(at));

        ref ushort p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cdf), at);
        uint r = _rng, r8 = r >> 8;
        uint c = (uint)(_dif >> (WindowSize - 16));
        uint u, v = r;
        int symbol = -1;
        int last = n - 1;
        do
        {
            u = v;
            symbol++;
            v = ((r8 * (uint)(Unsafe.Add(ref p, symbol) >> ProbShift)) >> (7 - ProbShift)) + (uint)(MinProb * (last - symbol));
        }
        while (c < v);

        _dif -= (ulong)v << (WindowSize - 16);
        Normalize(u - v);
        if (_adapt)
        {
            Adapt(ref p, symbol, n);
        }

        return symbol;
    }

    /// <summary>Reads a symbol against a CDF that does not adapt.</summary>
    internal int SymbolFixed(ReadOnlySpan<ushort> cdf, int n) => Decode(cdf, 0, n);

    /// <summary>Reads an equiprobable bit.</summary>
    internal int Bit()
    {
        uint r = _rng;
        uint v = ((r >> 8) * (16384u >> ProbShift) >> (7 - ProbShift)) + MinProb;
        ulong vw = (ulong)v << (WindowSize - 16);
        int ret = 1;
        uint rNew = v;
        if (_dif >= vw)
        {
            rNew = r - v;
            _dif -= vw;
            ret = 0;
        }

        Normalize(rNew);
        return ret;
    }

    /// <summary>Reads an unsigned number of <paramref name="bits"/> bits, most significant first.</summary>
    internal int Literal(int bits)
    {
        int value = 0;
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            value |= Bit() << bit;
        }

        return value;
    }

    /// <summary>Reads a number below <paramref name="n"/> in as few bits as needed (av1_read_uniform, ns(n)).</summary>
    internal int Uniform(int n)
    {
        int l = 32 - int.LeadingZeroCount(n);
        int m = (1 << l) - n;
        int v = Literal(l - 1);
        return v < m ? v : (v << 1) - m + Literal(1);
    }

    /// <summary>
    /// Reads an Exp-Golomb number (read_golomb) as dav1d does: at most 32 leading zeros, the value wrapping at 32 bits (libaom
    /// rejects codes over 20 bits).
    /// </summary>
    internal uint Golomb()
    {
        int length = 0;
        while (Bit() == 0 && length < 32)
        {
            length++;
        }

        uint x = 1;
        while (length-- > 0)
        {
            x = (x << 1) + (uint)Bit();
        }

        return x - 1;
    }

    /// <summary>Reads a finite sub-exponential number below <paramref name="n"/>, recentered around <paramref name="reference"/>.</summary>
    internal int RefSubexpFin(int n, int k, int reference)
    {
        int v = SubexpFin(n, k);
        return (reference << 1) <= n ? InverseRecenter(reference, v) : n - 1 - InverseRecenter(n - 1 - reference, v);

        static int InverseRecenter(int r, int v) => v > (r << 1) ? v : (v & 1) == 0 ? (v >> 1) + r : r - ((v + 1) >> 1);
    }

    private int SubexpFin(int n, int k)
    {
        int i = 0, mk = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (n <= mk + (3 * a))
            {
                int count = n - mk;
                if (count <= 1)
                {
                    return mk;
                }

                int l = 32 - int.LeadingZeroCount(count);
                int m = (1 << l) - count;
                int v = Literal(l - 1);
                return (v < m ? v : (v << 1) - m + Bit()) + mk;
            }

            if (Bit() == 0)
            {
                return Literal(b) + mk;
            }

            i++;
            mk += a;
        }
    }

    private int Decode(ReadOnlySpan<ushort> cdf, int at, int n)
    {
        uint r = _rng;
        uint c = (uint)(_dif >> (WindowSize - 16));
        uint u, v = r;
        int ret = -1;
        int last = n - 1;
        do
        {
            u = v;
            ret++;
            v = ((r >> 8) * (uint)(cdf[at + ret] >> ProbShift) >> (7 - ProbShift)) + (uint)(MinProb * (last - ret));
        }
        while (c < v);

        _dif -= (ulong)v << (WindowSize - 16);
        Normalize(u - v);
        return ret;
    }

    private void Normalize(uint rng)
    {
        int d = 15 - (31 - (int)uint.LeadingZeroCount(rng));
        _cnt -= d;
        _dif = ((_dif + 1) << d) - 1;
        _rng = rng << d;
        if (_cnt < 0)
        {
            Refill();
        }
    }

    private void Refill()
    {
        int s = WindowSize - 9 - (_cnt + 15);
        for (; s >= 0 && _position < _end; s -= 8, _position++)
        {
            _dif ^= (ulong)_data[_position] << s;
            _cnt += 8;
        }

        if (_position >= _end)
        {
            _tellOffs += LotsOfBits - _cnt;
            _cnt = LotsOfBits;
        }
    }

    /// <summary>
    /// Adapts a CDF towards the symbol just read (update_cdf), the counter being the entry after the last symbol: the entries
    /// before the symbol move towards 32768, the others towards 0.
    /// </summary>
    private static void Adapt(ref ushort cdf, int symbol, int n)
    {
        int count = Unsafe.Add(ref cdf, n);
        int rate = 3 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0) + (n > 3 ? 2 : 1);
        int i = 0;
        for (int end = Math.Min(symbol, n - 1); i < end; i++)
        {
            int value = Unsafe.Add(ref cdf, i);
            Unsafe.Add(ref cdf, i) = (ushort)(value + ((32768 - value) >> rate));
        }

        for (; i < n - 1; i++)
        {
            int value = Unsafe.Add(ref cdf, i);
            Unsafe.Add(ref cdf, i) = (ushort)(value - (value >> rate));
        }

        Unsafe.Add(ref cdf, n) = (ushort)(count + (count < 32 ? 1 : 0));
    }
}
