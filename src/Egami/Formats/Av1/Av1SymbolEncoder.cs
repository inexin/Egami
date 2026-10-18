using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Av1;

/// <summary>
/// The multi-symbol arithmetic encoder that <see cref="Av1SymbolDecoder"/> reads, in the form of libaom's od_ec_enc (BSD
/// 2-clause, see THIRD-PARTY-NOTICES): a 32-bit low end and 16-bit range, bytes kept in a 16-bit precarry buffer until the end
/// resolves their carries. Symbols are coded against the same inverse CDFs, adapted the same way, so a CDF holds the same values
/// on both sides after each symbol. <see cref="Finish"/> ends the tile with the AV1 trailing bits: a one, then zeros.
/// </summary>
internal sealed class Av1SymbolEncoder
{
    private const int ProbShift = 6;
    private const int MinProb = 4;

    private readonly bool _adapt;
    private ushort[] _precarry = new ushort[256];
    private int _offs;
    private uint _low;
    private uint _rng = 0x8000;
    private int _cnt = -9;

    /// <summary>Starts a tile's data.</summary>
    /// <param name="disableCdfUpdate">Whether the frame's disable_cdf_update flag is set, so CDFs do not adapt.</param>
    internal Av1SymbolEncoder(bool disableCdfUpdate = false)
    {
        _adapt = !disableCdfUpdate;
    }

    /// <summary>Gets how many bits the symbols so far take (od_ec_enc_tell), as <see cref="Av1SymbolDecoder.Tell"/> counts them.</summary>
    internal int Tell => (_offs * 8) + _cnt + 10;

    /// <summary>Codes a symbol against an adaptive CDF of <paramref name="n"/> symbols starting at <paramref name="at"/>, then adapts it.</summary>
    internal void Symbol(ushort[] cdf, int n, int at, int symbol)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)(at + n), (uint)cdf.Length, nameof(at));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)symbol, (uint)n, nameof(symbol));
        ref ushort p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cdf), at);
        Encode(symbol > 0 ? Unsafe.Add(ref p, symbol - 1) : 32768u, Unsafe.Add(ref p, symbol), symbol, n);
        if (_adapt)
        {
            Av1SymbolDecoder.Adapt(ref p, symbol, n);
        }
    }

    /// <summary>Codes a symbol against a CDF that does not adapt.</summary>
    internal void SymbolFixed(ReadOnlySpan<ushort> cdf, int n, int symbol) =>
        Encode(symbol > 0 ? cdf[symbol - 1] : 32768u, cdf[symbol], symbol, n);

    /// <summary>Codes an equiprobable bit.</summary>
    internal void Bit(int value)
    {
        uint l = _low, r = _rng;
        uint v = ((r >> 8) * (16384u >> ProbShift) >> (7 - ProbShift)) + MinProb;
        if (value != 0)
        {
            l += r - v;
        }

        Normalize(l, value != 0 ? v : r - v);
    }

    /// <summary>Codes an unsigned number in <paramref name="bits"/> bits, most significant first.</summary>
    internal void Literal(int value, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            Bit((value >> bit) & 1);
        }
    }

    /// <summary>Codes a number below <paramref name="n"/> in as few bits as needed (the inverse of <see cref="Av1SymbolDecoder.Uniform"/>).</summary>
    internal void Uniform(int n, int value)
    {
        int l = 32 - int.LeadingZeroCount(n);
        int m = (1 << l) - n;
        if (l == 0)
        {
            return;
        }

        if (value < m)
        {
            Literal(value, l - 1);
        }
        else
        {
            Literal((value + m) >> 1, l - 1);
            Bit((value + m) & 1);
        }
    }

    /// <summary>Codes an Exp-Golomb number (write_golomb).</summary>
    internal void Golomb(uint value)
    {
        ulong x = (ulong)value + 1;
        int length = 64 - (int)ulong.LeadingZeroCount(x);
        for (int i = 0; i < length - 1; i++)
        {
            Bit(0);
        }

        for (int i = length - 1; i >= 0; i--)
        {
            Bit((int)((x >> i) & 1));
        }
    }

    /// <summary>Codes a finite sub-exponential number below <paramref name="n"/>, recentered around <paramref name="reference"/>.</summary>
    internal void RefSubexpFin(int n, int k, int reference, int value)
    {
        SubexpFin(n, k, (reference << 1) <= n ? Recenter(reference, value) : Recenter(n - 1 - reference, n - 1 - value));

        static int Recenter(int r, int v) => v > (r << 1) ? v : v >= r ? (v - r) << 1 : ((r - v) << 1) - 1;
    }

    /// <summary>Ends the tile and returns its bytes: the fewest that decode every symbol, the last ending in the trailing bits.</summary>
    internal byte[] Finish()
    {
        uint l = _low;
        int c = _cnt;
        int s = 10;
        uint m = 0x3FFF;
        uint e = ((l + m) & ~m) | (m + 1);
        s += c;
        if (s > 0)
        {
            uint n = (1u << (c + 16)) - 1;
            do
            {
                Push((ushort)(e >> (c + 16)));
                e &= n;
                s -= 8;
                c -= 8;
                n >>= 8;
            }
            while (s > 0);
        }

        byte[] output = new byte[_offs];
        uint carry = 0;
        for (int i = _offs - 1; i >= 0; i--)
        {
            carry = _precarry[i] + carry;
            output[i] = (byte)carry;
            carry >>= 8;
        }

        return output;
    }

    private void Encode(uint fl, uint fh, int symbol, int n)
    {
        uint l = _low, r = _rng;
        int last = n - 1;
        if (fl < 32768)
        {
            uint u = ((r >> 8) * (fl >> ProbShift) >> (7 - ProbShift)) + (uint)(MinProb * (last - (symbol - 1)));
            uint v = ((r >> 8) * (fh >> ProbShift) >> (7 - ProbShift)) + (uint)(MinProb * (last - symbol));
            l += r - u;
            r = u - v;
        }
        else
        {
            r -= ((r >> 8) * (fh >> ProbShift) >> (7 - ProbShift)) + (uint)(MinProb * (last - symbol));
        }

        Normalize(l, r);
    }

    private void Normalize(uint low, uint rng)
    {
        int c = _cnt;
        int d = 15 - (31 - (int)uint.LeadingZeroCount(rng));
        int s = c + d;
        if (s >= 0)
        {
            c += 16;
            uint m = (1u << c) - 1;
            if (s >= 8)
            {
                Push((ushort)(low >> c));
                low &= m;
                c -= 8;
                m >>= 8;
            }

            Push((ushort)(low >> c));
            s = c + d - 24;
            low &= m;
        }

        _low = low << d;
        _rng = rng << d;
        _cnt = s;
    }

    private void Push(ushort value)
    {
        if (_offs == _precarry.Length)
        {
            Array.Resize(ref _precarry, _precarry.Length * 2);
        }

        _precarry[_offs++] = value;
    }

    private void SubexpFin(int n, int k, int value)
    {
        int i = 0, mk = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (n <= mk + (3 * a))
            {
                Uniform(n - mk, value - mk);
                return;
            }

            bool more = value >= mk + a;
            Bit(more ? 1 : 0);
            if (!more)
            {
                Literal(value - mk, b);
                return;
            }

            i++;
            mk += a;
        }
    }
}
