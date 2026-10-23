namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// Opus's range encoder (RFC 6716 section 5.1), the mirror of <see cref="OpusRangeDecoder"/>; translated from libopus 1.5.2
/// celt/entenc.c, entcode.c and laplace.c. Symbols are written from the start of the buffer, raw bits
/// (<see cref="EncodeBits"/>) from its end.
/// </summary>
internal sealed class OpusRangeEncoder
{
    private const int SymBits = 8;
    private const int CodeBits = 32;
    private const uint SymMax = (1u << SymBits) - 1;
    private const int CodeShift = CodeBits - SymBits - 1;
    private const uint CodeTop = 1u << (CodeBits - 1);
    private const uint CodeBot = CodeTop >> SymBits;
    private const int WindowSize = 32;
    private const int UintBits = 8;
    private const int LaplaceLogMinP = 0;
    private const int LaplaceMinP = 1 << LaplaceLogMinP;
    private const int LaplaceNMin = 16;

    private static readonly uint[] s_correction = [35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535];

    private byte[] _buffer = [];
    private int _start;
    private EncoderState _s;

    /// <summary>The whole state of the encoder except its buffer: libopus copies the ec_enc struct to try an encode and roll back.</summary>
    public struct EncoderState
    {
        internal uint Storage;
        internal uint EndOffset;
        internal uint EndWindow;
        internal int EndBits;
        internal int NbitsTotal;
        internal uint Offset;
        internal uint Rng;
        internal uint Val;
        internal uint Ext;
        internal int Rem;
        internal int Error;
    }

    /// <summary>Gets or sets the state, to roll back to an earlier point (the bytes written since are not restored).</summary>
    public EncoderState State
    {
        get => _s;
        set => _s = value;
    }

    /// <summary>Gets whether the encoder ran out of room.</summary>
    public bool Error => _s.Error != 0;

    /// <summary>Gets the range at the end, which libopus reports as the final range of a frame.</summary>
    public uint Range => _s.Rng;

    /// <summary>Gets the size of the buffer the encoder writes.</summary>
    public uint Storage => _s.Storage;

    /// <summary>Gets the bytes the range coder wrote from the start so far (ec_range_bytes).</summary>
    public int RangeBytes => (int)_s.Offset;

    /// <summary>Gets the buffer the encoder writes into.</summary>
    public byte[] Buffer => _buffer;

    /// <summary>Gets where the encoded bytes start in <see cref="Buffer"/>.</summary>
    public int Start => _start;

    /// <summary>ec_enc_init: starts encoding into <paramref name="size"/> bytes of <paramref name="buffer"/> from <paramref name="start"/>.</summary>
    public void Init(byte[] buffer, int start, int size)
    {
        _buffer = buffer;
        _start = start;
        _s = default;
        // This is the offset from which ec_tell() will subtract partial bits.
        _s.NbitsTotal = CodeBits + 1;
        _s.Rng = CodeTop;
        _s.Rem = -1;
        _s.Storage = (uint)size;
    }

    /// <summary>Points the encoder at another buffer holding the same bytes, as libopus does when it moves a frame.</summary>
    public void Rebase(byte[] buffer, int start)
    {
        _buffer = buffer;
        _start = start;
    }

    /// <summary>Counts <paramref name="bits"/> more bits as used, as CELT does when it pretends a silent frame filled the packet.</summary>
    public void AddUsedBits(int bits) => _s.NbitsTotal += bits;

    /// <summary>Gets the bits used so far, rounded up.</summary>
    public int Tell() => _s.NbitsTotal - OpusRangeDecoder.Ilog(_s.Rng);

    /// <summary>Gets the bits used so far in eighth bits.</summary>
    public uint TellFrac()
    {
        uint[] correction = s_correction;
        uint nbits = (uint)_s.NbitsTotal << OpusRangeDecoder.BitRes;
        int l = OpusRangeDecoder.Ilog(_s.Rng);
        uint r = _s.Rng >> (l - 16);
        uint b = (r >> 12) - 8;
        b += r > correction[(int)b] ? 1u : 0u;
        l = (l << 3) + (int)b;
        return nbits - (uint)l;
    }

    /// <summary>ec_encode: a symbol occupying [fl, fh) out of ft.</summary>
    public void Encode(uint fl, uint fh, uint ft)
    {
        uint r = _s.Rng / ft;
        if (fl > 0)
        {
            _s.Val += _s.Rng - (r * (ft - fl));
            _s.Rng = r * (fh - fl);
        }
        else
        {
            _s.Rng -= r * (ft - fh);
        }

        Normalize();
    }

    /// <summary>ec_encode_bin: <see cref="Encode"/> with ft = 2^<paramref name="bits"/>.</summary>
    public void EncodeBin(uint fl, uint fh, int bits)
    {
        uint r = _s.Rng >> bits;
        if (fl > 0)
        {
            _s.Val += _s.Rng - (r * ((1u << bits) - fl));
            _s.Rng = r * (fh - fl);
        }
        else
        {
            _s.Rng -= r * ((1u << bits) - fh);
        }

        Normalize();
    }

    /// <summary>Encodes a bit whose probability of being one is 1/2^<paramref name="logp"/>.</summary>
    public void EncodeBitLogp(bool val, int logp)
    {
        uint r = _s.Rng;
        uint l = _s.Val;
        uint s = r >> logp;
        r -= s;
        if (val)
        {
            _s.Val = l + r;
        }

        _s.Rng = val ? s : r;
        Normalize();
    }

    /// <summary>Encodes symbol <paramref name="s"/> with an inverse cumulative table of 8-bit entries over 2^<paramref name="ftb"/>.</summary>
    public void EncodeIcdf(int s, ReadOnlySpan<byte> icdf, int ftb)
    {
        uint r = _s.Rng >> ftb;
        if (s > 0)
        {
            _s.Val += _s.Rng - (r * icdf[s - 1]);
            _s.Rng = r * (uint)(icdf[s - 1] - icdf[s]);
        }
        else
        {
            _s.Rng -= r * icdf[s];
        }

        Normalize();
    }

    /// <summary>Encodes symbol <paramref name="s"/> with an inverse cumulative table of 16-bit entries.</summary>
    public void EncodeIcdf16(int s, ReadOnlySpan<ushort> icdf, int ftb)
    {
        uint r = _s.Rng >> ftb;
        if (s > 0)
        {
            _s.Val += _s.Rng - (r * icdf[s - 1]);
            _s.Rng = r * (uint)(icdf[s - 1] - icdf[s]);
        }
        else
        {
            _s.Rng -= r * icdf[s];
        }

        Normalize();
    }

    /// <summary>Encodes a uniformly distributed integer <paramref name="fl"/> in [0, <paramref name="ft"/>).</summary>
    public void EncodeUint(uint fl, uint ft)
    {
        ft--;
        int ftb = OpusRangeDecoder.Ilog(ft);
        if (ftb > UintBits)
        {
            ftb -= UintBits;
            uint ft1 = (ft >> ftb) + 1;
            uint fl1 = fl >> ftb;
            Encode(fl1, fl1 + 1, ft1);
            EncodeBits(fl & ((1u << ftb) - 1), ftb);
        }
        else
        {
            Encode(fl, fl + 1, ft + 1);
        }
    }

    /// <summary>Writes raw bits at the end of the frame.</summary>
    public void EncodeBits(uint fl, int bits)
    {
        uint window = _s.EndWindow;
        int used = _s.EndBits;
        if (used + bits > WindowSize)
        {
            do
            {
                _s.Error |= WriteByteAtEnd(window & SymMax);
                window >>= SymBits;
                used -= SymBits;
            }
            while (used >= SymBits);
        }

        window |= fl << used;
        used += bits;
        _s.EndWindow = window;
        _s.EndBits = used;
        _s.NbitsTotal += bits;
    }

    /// <summary>ec_enc_patch_initial_bits: overwrites the first <paramref name="nbits"/> bits written.</summary>
    public void PatchInitialBits(uint val, int nbits)
    {
        int shift = SymBits - nbits;
        uint mask = ((1u << nbits) - 1) << shift;
        if (_s.Offset > 0)
        {
            // The first byte has been finalized.
            _buffer[_start] = (byte)((_buffer[_start] & ~mask) | (val << shift));
        }
        else if (_s.Rem >= 0)
        {
            // The first byte is still awaiting carry propagation.
            _s.Rem = (int)(((uint)_s.Rem & ~mask) | (val << shift));
        }
        else if (_s.Rng <= CodeTop >> nbits)
        {
            // The renormalization loop has never been run.
            _s.Val = (_s.Val & ~(mask << CodeShift)) | (val << (CodeShift + shift));
        }
        else
        {
            // The encoder hasn't even encoded nbits of data yet.
            _s.Error = -1;
        }
    }

    /// <summary>ec_enc_shrink: moves the raw bits at the end so the frame ends at <paramref name="size"/> bytes.</summary>
    public void Shrink(uint size)
    {
        int endOffs = (int)_s.EndOffset;
        Array.Copy(_buffer, _start + (int)_s.Storage - endOffs, _buffer, _start + (int)size - endOffs, endOffs);
        _s.Storage = size;
    }

    /// <summary>ec_enc_done: writes the fewest bits that decode the symbols so far, then the raw bits, and clears the gap between.</summary>
    public void Done()
    {
        // We output the minimum number of bits that ensures that the symbols encoded thus far will be decoded correctly regardless
        // of the bits that follow.
        int l = CodeBits - OpusRangeDecoder.Ilog(_s.Rng);
        uint msk = (CodeTop - 1) >> l;
        uint end = (_s.Val + msk) & ~msk;
        if ((end | msk) >= _s.Val + _s.Rng)
        {
            l++;
            msk >>= 1;
            end = (_s.Val + msk) & ~msk;
        }

        while (l > 0)
        {
            CarryOut((int)(end >> CodeShift));
            end = (end << SymBits) & (CodeTop - 1);
            l -= SymBits;
        }

        // If we have a buffered byte flush it into the output buffer.
        if (_s.Rem >= 0 || _s.Ext > 0)
        {
            CarryOut(0);
        }

        // If we have buffered extra bits, flush them as well.
        uint window = _s.EndWindow;
        int used = _s.EndBits;
        while (used >= SymBits)
        {
            _s.Error |= WriteByteAtEnd(window & SymMax);
            window >>= SymBits;
            used -= SymBits;
        }

        // Clear any excess space and add any remaining extra bits to the last byte.
        if (_s.Error == 0)
        {
            _buffer.AsSpan(_start + (int)_s.Offset, (int)(_s.Storage - _s.Offset - _s.EndOffset)).Clear();
            if (used > 0)
            {
                // If there's no range coder data at all, give up.
                if (_s.EndOffset >= _s.Storage)
                {
                    _s.Error = -1;
                }
                else
                {
                    l = -l;
                    // If we've busted, don't add too many extra bits to the last byte; it would corrupt the range coder data, and
                    // that's more important.
                    if (_s.Offset + _s.EndOffset >= _s.Storage && l < used)
                    {
                        window &= (1u << l) - 1;
                        _s.Error = -1;
                    }

                    _buffer[_start + (int)(_s.Storage - _s.EndOffset - 1)] |= (byte)window;
                }
            }
        }
    }

    /// <summary>ec_laplace_encode: encodes <paramref name="value"/>, which may be clamped, with probability of zero fs/32768.</summary>
    public void LaplaceEncode(ref int value, uint fs, int decay)
    {
        uint fl = 0;
        int val = value;
        if (val != 0)
        {
            int s = -(val < 0 ? 1 : 0);
            val = (val + s) ^ s;
            fl = fs;
            fs = LaplaceGetFreq1(fs, decay);
            // Search the decaying part of the PDF.
            int i;
            for (i = 1; fs > 0 && i < val; i++)
            {
                fs *= 2;
                fl += fs + (2 * LaplaceMinP);
                fs = (uint)((int)fs * decay) >> 15;
            }

            // Everything beyond that has probability LAPLACE_MINP.
            if (fs == 0)
            {
                int ndiMax = (int)(32768 - fl + LaplaceMinP - 1) >> LaplaceLogMinP;
                ndiMax = (ndiMax - s) >> 1;
                int di = Math.Min(val - i, ndiMax - 1);
                fl += (uint)(((2 * di) + 1 + s) * LaplaceMinP);
                fs = Math.Min(LaplaceMinP, 32768 - fl);
                value = (i + di + s) ^ s;
            }
            else
            {
                fs += LaplaceMinP;
                fl += fs & (uint)~s;
            }
        }

        EncodeBin(fl, fl + fs, 15);
    }

    private static uint LaplaceGetFreq1(uint fs0, int decay)
    {
        uint ft = 32768 - (LaplaceMinP * (2 * LaplaceNMin)) - fs0;
        return (ft * (uint)(16384 - decay)) >> 15;
    }

    private int WriteByte(uint value)
    {
        if (_s.Offset + _s.EndOffset >= _s.Storage)
        {
            return -1;
        }

        _buffer[_start + (int)_s.Offset++] = (byte)value;
        return 0;
    }

    private int WriteByteAtEnd(uint value)
    {
        if (_s.Offset + _s.EndOffset >= _s.Storage)
        {
            return -1;
        }

        _buffer[_start + (int)(_s.Storage - ++_s.EndOffset)] = (byte)value;
        return 0;
    }

    // Outputs a symbol, with a carry bit. If there is a potential to propagate a carry over several symbols, they are buffered
    // until it can be determined whether or not an actual carry will occur.
    private void CarryOut(int c)
    {
        if (c != SymMax)
        {
            // No further carry propagation possible, flush buffer.
            int carry = c >> SymBits;
            // Don't output a byte on the first write.
            if (_s.Rem >= 0)
            {
                _s.Error |= WriteByte((uint)(_s.Rem + carry));
            }

            if (_s.Ext > 0)
            {
                uint sym = (SymMax + (uint)carry) & SymMax;
                do
                {
                    _s.Error |= WriteByte(sym);
                }
                while (--_s.Ext > 0);
            }

            _s.Rem = c & (int)SymMax;
        }
        else
        {
            _s.Ext++;
        }
    }

    private void Normalize()
    {
        // If the range is too small, output some bits and rescale it.
        while (_s.Rng <= CodeBot)
        {
            CarryOut((int)(_s.Val >> CodeShift));
            // Move the next-to-high-order symbol into the high-order position.
            _s.Val = (_s.Val << SymBits) & (CodeTop - 1);
            _s.Rng <<= SymBits;
            _s.NbitsTotal += SymBits;
        }
    }
}
