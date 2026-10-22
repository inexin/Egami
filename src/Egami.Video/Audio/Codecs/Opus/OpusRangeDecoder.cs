using System.Numerics;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// Opus's range decoder (RFC 6716 section 4.1), shared by SILK and CELT; translated from libopus 1.5.2 celt/entdec.c and
/// entcode.c. Raw bits (<see cref="DecodeBits"/>) are read from the end of the frame, symbols from the start.
/// </summary>
internal sealed class OpusRangeDecoder
{
    private const int SymBits = 8;
    private const int CodeBits = 32;
    private const uint SymMax = (1u << SymBits) - 1;
    private const uint CodeTop = 1u << (CodeBits - 1);
    private const uint CodeBot = CodeTop >> SymBits;
    private const int CodeExtra = ((CodeBits - 2) % SymBits) + 1;
    private const int WindowSize = 32;
    private const int UintBits = 8;

    /// <summary>The resolution of <see cref="TellFrac"/>: eighth bits.</summary>
    public const int BitRes = 3;

    // A static array: a span of uint initialized in the method would allocate a field handle on every call.
    private static readonly uint[] s_correction = [35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535];

    private byte[] _buffer = [];
    private int _start;
    private uint _storage;
    private uint _endOffset;
    private uint _endWindow;
    private int _endBits;
    private int _nbitsTotal;
    private uint _offset;
    private uint _rng;
    private uint _val;
    private uint _ext;
    private int _rem;

    /// <summary>Gets whether a value read past what the stream allows.</summary>
    public bool Error { get; private set; }

    /// <summary>Gets the range at the end, which libopus reports as the final range of a frame.</summary>
    public uint Range => _rng;

    /// <summary>Gets the number of bytes the decoder reads from.</summary>
    public uint Storage => _storage;

    /// <summary>Starts decoding <paramref name="length"/> bytes of <paramref name="buffer"/> from <paramref name="start"/>.</summary>
    public void Init(byte[] buffer, int start, int length)
    {
        _buffer = buffer;
        _start = start;
        _storage = (uint)length;
        _endOffset = 0;
        _endWindow = 0;
        _endBits = 0;
        _nbitsTotal = CodeBits + 1 - ((CodeBits - CodeExtra) / SymBits * SymBits);
        _offset = 0;
        _rng = 1u << CodeExtra;
        _rem = ReadByte();
        _val = _rng - 1 - (uint)(_rem >> (SymBits - CodeExtra));
        Error = false;
        Normalize();
    }

    /// <summary>Shrinks the bytes the decoder may read, as Opus does when a CELT frame follows SILK in hybrid mode.</summary>
    public void SetStorage(uint storage) => _storage = storage;

    /// <summary>Counts <paramref name="bits"/> more bits as used (nbits_total += bits), as CELT does for a silent frame.</summary>
    public void AddUsedBits(int bits) => _nbitsTotal += bits;

    /// <summary>Gets the bits used so far, rounded up.</summary>
    public int Tell() => _nbitsTotal - Ilog(_rng);

    /// <summary>Gets the bits used so far in eighth bits.</summary>
    public uint TellFrac()
    {
        uint[] correction = s_correction;
        uint nbits = (uint)_nbitsTotal << BitRes;
        int l = Ilog(_rng);
        uint r = _rng >> (l - 16);
        uint b = (r >> 12) - 8;
        b += r > correction[(int)b] ? 1u : 0u;
        l = (l << 3) + (int)b;
        return nbits - (uint)l;
    }

    public uint Decode(uint ft)
    {
        _ext = _rng / ft;
        uint s = _val / _ext;
        return ft - Math.Min(s + 1, ft);
    }

    public uint DecodeBin(int bits)
    {
        _ext = _rng >> bits;
        uint s = _val / _ext;
        return (1u << bits) - Math.Min(s + 1, 1u << bits);
    }

    public void Update(uint fl, uint fh, uint ft)
    {
        uint s = _ext * (ft - fh);
        _val -= s;
        _rng = fl > 0 ? _ext * (fh - fl) : _rng - s;
        Normalize();
    }

    /// <summary>Decodes a bit whose probability of being one is 1/2^<paramref name="logp"/>.</summary>
    public bool DecodeBitLogp(int logp)
    {
        uint r = _rng;
        uint d = _val;
        uint s = r >> logp;
        bool ret = d < s;
        if (!ret)
        {
            _val = d - s;
        }

        _rng = ret ? s : r - s;
        Normalize();
        return ret;
    }

    /// <summary>Decodes a symbol with an inverse cumulative table of 8-bit entries over 2^<paramref name="ftb"/>.</summary>
    public int DecodeIcdf(ReadOnlySpan<byte> icdf, int ftb)
    {
        uint s = _rng;
        uint d = _val;
        uint r = s >> ftb;
        int ret = -1;
        uint t;
        do
        {
            t = s;
            s = r * icdf[++ret];
        }
        while (d < s);
        _val = d - s;
        _rng = t - s;
        Normalize();
        return ret;
    }

    /// <summary>Decodes a symbol with an inverse cumulative table of 16-bit entries.</summary>
    public int DecodeIcdf16(ReadOnlySpan<ushort> icdf, int ftb)
    {
        uint s = _rng;
        uint d = _val;
        uint r = s >> ftb;
        int ret = -1;
        uint t;
        do
        {
            t = s;
            s = r * icdf[++ret];
        }
        while (d < s);
        _val = d - s;
        _rng = t - s;
        Normalize();
        return ret;
    }

    /// <summary>Decodes a uniformly distributed integer in [0, <paramref name="ft"/>).</summary>
    public uint DecodeUint(uint ft)
    {
        ft--;
        int ftb = Ilog(ft);
        if (ftb > UintBits)
        {
            ftb -= UintBits;
            uint f = (ft >> ftb) + 1;
            uint s = Decode(f);
            Update(s, s + 1, f);
            uint t = (s << ftb) | DecodeBits(ftb);
            if (t <= ft)
            {
                return t;
            }

            Error = true;
            return ft;
        }

        ft++;
        uint v = Decode(ft);
        Update(v, v + 1, ft);
        return v;
    }

    /// <summary>Reads raw bits from the end of the frame.</summary>
    public uint DecodeBits(int bits)
    {
        uint window = _endWindow;
        int available = _endBits;
        if (available < bits)
        {
            do
            {
                window |= (uint)ReadByteFromEnd() << available;
                available += SymBits;
            }
            while (available <= WindowSize - SymBits);
        }

        uint ret = bits == 32 ? window : window & ((1u << bits) - 1);
        window = bits == 32 ? 0 : window >> bits;
        available -= bits;
        _endWindow = window;
        _endBits = available;
        _nbitsTotal += bits;
        return ret;
    }

    /// <summary>EC_ILOG: the number of bits needed to write <paramref name="value"/>, 0 for 0.</summary>
    public static int Ilog(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    private int ReadByte() => _offset < _storage ? _buffer[_start + (int)_offset++] : 0;

    private int ReadByteFromEnd() => _endOffset < _storage ? _buffer[_start + (int)(_storage - ++_endOffset)] : 0;

    private void Normalize()
    {
        while (_rng <= CodeBot)
        {
            _nbitsTotal += SymBits;
            _rng <<= SymBits;
            int sym = _rem;
            _rem = ReadByte();
            sym = ((sym << SymBits) | _rem) >> (SymBits - CodeExtra);
            _val = ((_val << SymBits) + (SymMax & (uint)~sym)) & (CodeTop - 1);
        }
    }
}
