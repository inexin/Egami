using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// Decodes AC-3 sync frames (ATSC A/52:2018 sections 5 to 7) into 1536 float samples per channel: the bit stream information,
/// then per audio block the exponents, the parametric bit allocation, the mantissas, decoupling, rematrixing, dynamic range
/// gain and the 512 or 256-point IMDCT with overlap-add. Channels come out in the stream's order (Table 5.8), the LFE last.
/// </summary>
internal sealed class Ac3FrameDecoder
{
    /// <summary>The samples a frame decodes to per channel.</summary>
    public const int FrameSamples = 1536;

    /// <summary>The most channels a frame holds: five full-bandwidth channels and the LFE.</summary>
    public const int MaxChannels = 6;

    // Arrays below are indexed by channel: 0 to 4 for the full-bandwidth channels, Lfe for the LFE and Cpl for the coupling channel.
    private const int Lfe = 5;
    private const int Cpl = 6;

    private static readonly float[] CenterLevels = [0.70710678f, 0.59460356f, 0.5f, 0.59460356f];
    private static readonly float[] SurroundLevels = [0.70710678f, 0.5f, 0f, 0.5f];

    private readonly Ac3Imdct _imdct = new();
    private readonly byte[][] _exp = Jagged<byte>(7, 256 + 64);
    private readonly byte[][] _bap = Jagged<byte>(7, 256);
    private readonly float[][] _coeffs = Jagged<float>(7, 256);
    private readonly float[][] _delay = Jagged<float>(MaxChannels, 256);
    private readonly int[] _endMant = new int[7];
    private readonly bool[] _blockSwitch = new bool[5];
    private readonly bool[] _dither = new bool[5];
    private readonly bool[] _inCoupling = new bool[5];
    private readonly bool[] _bandStructure = new bool[18];
    private readonly float[][] _cplCoord = Jagged<float>(5, 18);
    private readonly bool[] _phaseFlags = new bool[18];
    private readonly bool[] _remat = new bool[4];
    private readonly int[] _fineSnr = new int[7];
    private readonly int[] _fastGain = new int[7];
    private readonly int[] _deltaMode = new int[7];
    private readonly int[] _deltaSegments = new int[7];
    private readonly byte[][] _deltaOffset = Jagged<byte>(7, 8);
    private readonly byte[][] _deltaLength = Jagged<byte>(7, 8);
    private readonly byte[][] _deltaBa = Jagged<byte>(7, 8);
    private readonly bool[] _reallocate = new bool[7];
    private readonly int[] _expScratch = new int[256 * 3];
    private int _acmod;
    private int _fullChannels;
    private bool _lfe;
    private int _fscod;
    private bool _cplInUse;
    private int _cplBegin;
    private int _cplEnd;
    private int _cplStart;
    private int _cplStop;
    private int _cplBands;
    private bool _phaseInUse;
    private int _rematBands;
    private int _slowDecay;
    private int _fastDecay;
    private int _slowGain;
    private int _dbKnee;
    private int _floor;
    private int _coarseSnr;
    private int _cplFastLeak;
    private int _cplSlowLeak;
    private float _gain1;
    private float _gain2;
    private uint _random = 0x12345679;

    /// <summary>Gets or sets the fraction of the stream's dynamic range control to apply: 1 in full, 0 none.</summary>
    public float DynamicRange { get; set; } = 1;

    /// <summary>Gets the last frame's audio coding mode.</summary>
    public int Acmod => _acmod;

    /// <summary>Gets whether the last frame had an LFE channel.</summary>
    public bool HasLfe => _lfe;

    /// <summary>Gets the last frame's channels, the LFE included.</summary>
    public int Channels => _fullChannels + (_lfe ? 1 : 0);

    /// <summary>Gets the last frame's center mix level (clev, Table 5.9).</summary>
    public float CenterMixLevel { get; private set; } = CenterLevels[0];

    /// <summary>Gets the last frame's surround mix level (slev, Table 5.10).</summary>
    public float SurroundMixLevel { get; private set; } = SurroundLevels[0];

    /// <summary>
    /// Gets or sets a list that collects the bit offset of every dithflag in the frames decoded, for tests that rewrite streams;
    /// <see langword="null"/>, the default, collects nothing.
    /// </summary>
    internal List<long>? DitherFlagBits { get; set; }

    /// <summary>Gets or sets a list that collects named bit offsets in the frames decoded, for tests that rewrite streams.</summary>
    internal List<(string Name, long Bit)>? Marks { get; set; }

    /// <summary>Clears the overlap-add history, as after a seek or a lost frame.</summary>
    public void Reset()
    {
        foreach (float[] d in _delay)
        {
            Array.Clear(d);
        }
    }

    /// <summary>Decodes a frame into <paramref name="output"/>, one span of <see cref="FrameSamples"/> per channel.</summary>
    /// <param name="frame">The whole frame, sync word first.</param>
    /// <param name="output">The channel buffers, in the stream's channel order with the LFE last.</param>
    /// <returns>Whether the frame decoded; on false the history is cleared and the output is unspecified.</returns>
    public bool Decode(ReadOnlySpan<byte> frame, float[][] output)
    {
        Ac3BitReader r = new(frame);
        if (!ReadBsi(ref r))
        {
            Reset();
            return false;
        }

        for (int blk = 0; blk < 6; blk++)
        {
            if (!DecodeBlock(ref r, blk) || r.Overrun)
            {
                Reset();
                return false;
            }

            Synthesize(output, blk * 256);
        }

        Marks?.Add(("audio-end", r.Position));

        return true;
    }

    private bool ReadBsi(ref Ac3BitReader r)
    {
        r.Skip(32); // syncword, crc1
        _fscod = r.Read(2);
        int frmsizecod = r.Read(6);
        int bsid = r.Read(5);
        if (_fscod == 3 || frmsizecod > 37 || bsid > 10)
        {
            return false;
        }

        r.Skip(3); // bsmod
        _acmod = r.Read(3);
        _fullChannels = _acmod == 0 ? 2 : _acmod switch { 1 => 1, 2 => 2, 3 or 4 => 3, 5 or 6 => 4, _ => 5 };
        CenterMixLevel = (_acmod & 1) != 0 && _acmod != 1 ? CenterLevels[r.Read(2)] : CenterLevels[0];
        SurroundMixLevel = (_acmod & 4) != 0 ? SurroundLevels[r.Read(2)] : SurroundLevels[0];
        if (_acmod == 2)
        {
            Marks?.Add(("dsurmod", r.Position));
            r.Skip(2); // dsurmod
        }

        _lfe = r.Read(1) != 0;
        SkipProgramInfo(ref r);
        Marks?.Add(("program2", r.Position));
        if (_acmod == 0)
        {
            SkipProgramInfo(ref r);
        }

        r.Skip(2); // copyrightb, origbs
        if (r.Read(1) != 0)
        {
            r.Skip(14); // timecod1, or Annex D's xbsi1
        }

        if (r.Read(1) != 0)
        {
            r.Skip(14); // timecod2, or xbsi2
        }

        if (r.Read(1) != 0)
        {
            r.Skip((r.Read(6) + 1) * 8); // addbsi
        }

        return !r.Overrun;
    }

    /// <summary>Skips dialnorm, compr, langcod and the audio production information of one program.</summary>
    private static void SkipProgramInfo(ref Ac3BitReader r)
    {
        r.Skip(5);
        if (r.Read(1) != 0)
        {
            r.Skip(8);
        }

        if (r.Read(1) != 0)
        {
            r.Skip(8);
        }

        if (r.Read(1) != 0)
        {
            r.Skip(7);
        }
    }

    private bool DecodeBlock(ref Ac3BitReader r, int blk)
    {
        int nfchans = _fullChannels;
        for (int ch = 0; ch < nfchans; ch++)
        {
            _blockSwitch[ch] = r.Read(1) != 0;
        }

        for (int ch = 0; ch < nfchans; ch++)
        {
            DitherFlagBits?.Add(r.Position);
            _dither[ch] = r.Read(1) != 0;
        }

        if (r.Read(1) != 0)
        {
            _gain1 = DynamicGain(r.Read(8));
        }
        else if (blk == 0)
        {
            _gain1 = 1;
        }

        if (_acmod == 0)
        {
            if (r.Read(1) != 0)
            {
                _gain2 = DynamicGain(r.Read(8));
            }
            else if (blk == 0)
            {
                _gain2 = 1;
            }
        }

        Marks?.Add(("dynrng2", r.Position));
        if (!ReadCoupling(ref r, blk))
        {
            return false;
        }

        Marks?.Add(("remat", r.Position));
        if (_acmod == 2 && !ReadRematrix(ref r, blk))
        {
            return false;
        }

        Marks?.Add(("remat-end", r.Position));

        if (!ReadExponents(ref r, blk) || !ReadBitAllocation(ref r, blk))
        {
            return false;
        }

        if (r.Read(1) != 0)
        {
            r.Skip(r.Read(9) * 8); // skipfld
        }

        if (r.Overrun)
        {
            return false;
        }

        Allocate();
        return ReadMantissas(ref r);
    }

    private float DynamicGain(int code) => DynamicGain(code, DynamicRange);

    /// <summary>
    /// The gain of a dynrng word (A/52 section 7.7.1.2), X0 X1 X2 . Y3..Y7: (X + 1) * 6.02 dB from the signed top three bits,
    /// times 0.1Y (one half to 63/64), raised to <paramref name="scale"/> so the decibels scale.
    /// </summary>
    internal static float DynamicGain(int code, float scale)
    {
        int x = (code << 24) >> 29;
        float gain = MathF.ScaleB((32 + (code & 31)) / 64f, x + 1);
        return scale == 1 ? gain : scale == 0 ? 1 : MathF.Pow(gain, scale);
    }

    private bool ReadCoupling(ref Ac3BitReader r, int blk)
    {
        int nfchans = _fullChannels;
        bool newCoupling = false;
        if (r.Read(1) != 0)
        {
            bool wasInUse = _cplInUse;
            _cplInUse = r.Read(1) != 0;
            if (_cplInUse)
            {
                if (_acmod < 2)
                {
                    return false;
                }

                int coupled = 0;
                for (int ch = 0; ch < nfchans; ch++)
                {
                    _inCoupling[ch] = r.Read(1) != 0;
                    coupled += _inCoupling[ch] ? 1 : 0;
                }

                _phaseInUse = _acmod == 2 && r.Read(1) != 0;
                _cplBegin = r.Read(4);
                _cplEnd = r.Read(4) + 3;
                int subbands = _cplEnd - _cplBegin;
                if (coupled < 2 || subbands < 1)
                {
                    return false;
                }

                _cplBands = 1;
                _bandStructure[0] = false;
                for (int sb = 1; sb < subbands; sb++)
                {
                    _bandStructure[sb] = r.Read(1) != 0;
                    _cplBands += _bandStructure[sb] ? 0 : 1;
                }

                _cplStart = (_cplBegin * 12) + 37;
                _cplStop = (_cplEnd * 12) + 37;
                newCoupling = true;
                if (!wasInUse)
                {
                    // Nothing about coupling carries over from the last time it was on (A/52 section 5.5).
                    Array.Clear(_phaseFlags);
                }
            }
            else
            {
                Array.Clear(_inCoupling);
                _phaseInUse = false;
            }
        }
        else if (blk == 0)
        {
            return false;
        }

        if (!_cplInUse)
        {
            return true;
        }

        bool anyCoordinates = false;
        for (int ch = 0; ch < nfchans; ch++)
        {
            if (!_inCoupling[ch])
            {
                continue;
            }

            if (r.Read(1) == 0)
            {
                if (newCoupling && blk == 0)
                {
                    return false;
                }

                continue;
            }

            anyCoordinates = true;
            int master = 3 * r.Read(2);
            float[] coords = _cplCoord[ch];
            int band = -1;
            float value = 0;
            for (int sb = 0; sb < _cplEnd - _cplBegin; sb++)
            {
                if (!_bandStructure[sb])
                {
                    band++;
                    int exponent = r.Read(4);
                    int mantissa = r.Read(4);
                    float m = exponent == 15 ? mantissa / 16f : (mantissa + 16) / 32f;

                    // The 8 of the decoupling equation (A/52 section 7.4.3) is folded in here.
                    value = m * Ac3Tables.PowerOfTwo[exponent + master] * 8;
                }

                coords[sb] = value;
            }
        }

        if (_acmod == 2 && _phaseInUse && anyCoordinates)
        {
            for (int band = 0; band < _cplBands; band++)
            {
                _phaseFlags[band] = r.Read(1) != 0;
            }
        }

        return true;
    }

    private bool ReadRematrix(ref Ac3BitReader r, int blk)
    {
        if (r.Read(1) == 0)
        {
            return blk != 0;
        }

        _rematBands = !_cplInUse || _cplBegin > 2 ? 4 : _cplBegin > 0 ? 3 : 2;
        for (int band = 0; band < _rematBands; band++)
        {
            _remat[band] = r.Read(1) != 0;
        }

        return true;
    }

    private bool ReadExponents(ref Ac3BitReader r, int blk)
    {
        int nfchans = _fullChannels;
        int cplStrategy = _cplInUse ? r.Read(2) : 0;
        Span<int> strategy = stackalloc int[5];
        for (int ch = 0; ch < nfchans; ch++)
        {
            strategy[ch] = r.Read(2);
        }

        int lfeStrategy = _lfe ? r.Read(1) : 0;
        if (blk == 0 && (lfeStrategy == 0 && _lfe || (_cplInUse && cplStrategy == 0)))
        {
            return false;
        }

        for (int ch = 0; ch < nfchans; ch++)
        {
            if (strategy[ch] == 0)
            {
                if (blk == 0)
                {
                    return false;
                }

                continue;
            }

            if (_inCoupling[ch])
            {
                _endMant[ch] = _cplStart;
            }
            else
            {
                int bandwidth = r.Read(6);
                if (bandwidth > 60)
                {
                    return false;
                }

                _endMant[ch] = ((bandwidth + 12) * 3) + 37;
            }
        }

        Array.Clear(_reallocate);
        if (cplStrategy != 0)
        {
            int size = 3 << (cplStrategy - 1);
            int groups = (_cplStop - _cplStart) / size;
            int absolute = r.Read(4) << 1;
            if (!ReadExponentGroups(ref r, _exp[Cpl].AsSpan(_cplStart - 1), absolute, groups, cplStrategy))
            {
                return false;
            }

            _endMant[Cpl] = _cplStop;
            _reallocate[Cpl] = true;
        }

        for (int ch = 0; ch < nfchans; ch++)
        {
            if (strategy[ch] == 0)
            {
                continue;
            }

            int end = _endMant[ch];
            int groups = strategy[ch] switch { 1 => (end - 1) / 3, 2 => (end + 2) / 6, _ => (end + 8) / 12 };
            byte[] exp = _exp[ch];
            int absolute = r.Read(4);
            exp[0] = (byte)absolute;
            if (!ReadExponentGroups(ref r, exp, absolute, groups, strategy[ch]))
            {
                return false;
            }

            r.Skip(2); // gainrng
            _reallocate[ch] = true;
        }

        if (lfeStrategy != 0)
        {
            byte[] exp = _exp[Lfe];
            int absolute = r.Read(4);
            exp[0] = (byte)absolute;
            if (!ReadExponentGroups(ref r, exp, absolute, 2, 1))
            {
                return false;
            }

            _endMant[Lfe] = 7;
            _reallocate[Lfe] = true;
        }

        return true;
    }

    /// <summary>
    /// Decodes grouped differential exponents (A/52 section 7.1.3) into <paramref name="exp"/>[1..]: three mapped values per
    /// 7-bit group, each a step of -2 to +2 from the last exponent, repeated 1, 2 or 4 times for D15, D25 or D45.
    /// </summary>
    private static bool ReadExponentGroups(ref Ac3BitReader r, Span<byte> exp, int absolute, int groups, int strategy)
    {
        int size = 1 << (strategy - 1);
        int previous = absolute;
        int at = 1;
        for (int g = 0; g < groups; g++)
        {
            int code = r.Read(7);
            if (code > 124)
            {
                return false;
            }

            int m1 = code / 25;
            int rest = code - (m1 * 25);
            int m2 = rest / 5;
            int m3 = rest - (m2 * 5);
            for (int i = 0; i < 3; i++)
            {
                previous += (i == 0 ? m1 : i == 1 ? m2 : m3) - 2;
                if ((uint)previous > 24)
                {
                    return false;
                }

                for (int j = 0; j < size; j++)
                {
                    exp[at++] = (byte)previous;
                }
            }
        }

        return true;
    }

    private bool ReadBitAllocation(ref Ac3BitReader r, int blk)
    {
        int nfchans = _fullChannels;
        bool all = false;
        if (r.Read(1) != 0)
        {
            _slowDecay = Ac3Tables.SlowDecay[r.Read(2)];
            _fastDecay = Ac3Tables.FastDecay[r.Read(2)];
            _slowGain = Ac3Tables.SlowGain[r.Read(2)];
            _dbKnee = Ac3Tables.DbPerBit[r.Read(2)];
            _floor = Ac3Tables.Floor[r.Read(3)];
            all = true;
        }
        else if (blk == 0)
        {
            return false;
        }

        if (r.Read(1) != 0)
        {
            _coarseSnr = r.Read(6);
            if (_cplInUse)
            {
                _fineSnr[Cpl] = r.Read(4);
                _fastGain[Cpl] = Ac3Tables.FastGain[r.Read(3)];
            }

            for (int ch = 0; ch < nfchans; ch++)
            {
                _fineSnr[ch] = r.Read(4);
                _fastGain[ch] = Ac3Tables.FastGain[r.Read(3)];
            }

            if (_lfe)
            {
                _fineSnr[Lfe] = r.Read(4);
                _fastGain[Lfe] = Ac3Tables.FastGain[r.Read(3)];
            }

            all = true;
        }
        else if (blk == 0)
        {
            return false;
        }

        if (_cplInUse)
        {
            if (r.Read(1) != 0)
            {
                _cplFastLeak = (r.Read(3) << 8) + 768;
                _cplSlowLeak = (r.Read(3) << 8) + 768;
                _reallocate[Cpl] = true;
            }
            else if (blk == 0)
            {
                return false;
            }
        }

        if (blk == 0)
        {
            // No delta bit allocation unless the stream sends it (A/52 section 5.4.3.47).
            Array.Fill(_deltaMode, 2);
        }

        if (r.Read(1) != 0)
        {
            if (_cplInUse)
            {
                _deltaMode[Cpl] = r.Read(2);
            }

            for (int ch = 0; ch < nfchans; ch++)
            {
                _deltaMode[ch] = r.Read(2);
            }

            if (_cplInUse && !ReadDelta(ref r, Cpl))
            {
                return false;
            }

            for (int ch = 0; ch < nfchans; ch++)
            {
                if (!ReadDelta(ref r, ch))
                {
                    return false;
                }
            }

            all = true;
        }

        if (all)
        {
            Array.Fill(_reallocate, true);
        }

        return true;
    }

    private bool ReadDelta(ref Ac3BitReader r, int ch)
    {
        if (_deltaMode[ch] == 3)
        {
            return false;
        }

        if (_deltaMode[ch] != 1)
        {
            return true;
        }

        int segments = r.Read(3) + 1;
        _deltaSegments[ch] = segments;
        for (int seg = 0; seg < segments; seg++)
        {
            _deltaOffset[ch][seg] = (byte)r.Read(5);
            _deltaLength[ch][seg] = (byte)r.Read(4);
            _deltaBa[ch][seg] = (byte)r.Read(3);
        }

        return true;
    }

    private void Allocate()
    {
        bool silent = _coarseSnr == 0;
        for (int ch = 0; ch < 7 && silent; ch++)
        {
            silent = _fineSnr[ch] == 0 || !Active(ch);
        }

        for (int ch = 0; ch < 7; ch++)
        {
            if (!_reallocate[ch] || !Active(ch))
            {
                continue;
            }

            int start = ch == Cpl ? _cplStart : 0;
            int end = _endMant[ch];
            if (silent)
            {
                // Every SNR offset zero: no bits for any mantissa (A/52 section 7.2.2.1.1).
                _bap[ch].AsSpan(start, end - start).Clear();
                continue;
            }

            int snrOffset = (((_coarseSnr - 15) << 4) + _fineSnr[ch]) << 2;
            bool cpl = ch == Cpl;
            BitAllocation(ch, start, end, _fastGain[ch], snrOffset, cpl ? _cplFastLeak : 0, cpl ? _cplSlowLeak : 0);
        }
    }

    private bool Active(int ch) => ch == Cpl ? _cplInUse : ch == Lfe ? _lfe : ch < _fullChannels;

    /// <summary>The parametric bit allocation of A/52 section 7.2.2 for one exponent set, filling bap[start..end].</summary>
    private void BitAllocation(int ch, int start, int end, int fgain, int snrOffset, int fastLeak, int slowLeak)
    {
        ReadOnlySpan<byte> exp = _exp[ch];
        Span<byte> bap = _bap[ch];
        ReadOnlySpan<byte> bandOf = Ac3Tables.BandOfBin;
        ReadOnlySpan<byte> bandStart = Ac3Tables.BandStart;
        ReadOnlySpan<byte> logAdd = Ac3Tables.LogAdd;
        Span<int> bandPsd = stackalloc int[51];
        Span<int> excite = stackalloc int[50];

        // PSD integration over the bands, by log-addition.
        int j = start;
        int k = bandOf[start];
        int lastBin;
        do
        {
            lastBin = Math.Min(bandStart[k + 1], end);
            int sum = 3072 - (exp[j] << 7);
            for (j++; j < lastBin; j++)
            {
                int psd = 3072 - (exp[j] << 7);
                int c = sum - psd;
                int address = Math.Min(Math.Abs(c) >> 1, 255);
                sum = (c >= 0 ? sum : psd) + logAdd[address];
            }

            bandPsd[k] = sum;
            k++;
        }
        while (end > lastBin);

        int bandBegin = bandOf[start];
        int bandEnd = bandOf[end - 1] + 1;
        int sdecay = _slowDecay;
        int fdecay = _fastDecay;
        int sgain = _slowGain;
        int begin;
        if (bandBegin == 0)
        {
            bool lfe = bandEnd == 7;
            int lowComp = LowComp(0, bandPsd[0], bandPsd[1], 0);
            excite[0] = bandPsd[0] - fgain - lowComp;
            lowComp = LowComp(lowComp, bandPsd[1], bandPsd[2], 1);
            excite[1] = bandPsd[1] - fgain - lowComp;
            begin = 7;
            for (int bin = 2; bin < 7; bin++)
            {
                bool last = lfe && bin == 6;
                if (!last)
                {
                    lowComp = LowComp(lowComp, bandPsd[bin], bandPsd[bin + 1], bin);
                }

                fastLeak = bandPsd[bin] - fgain;
                slowLeak = bandPsd[bin] - sgain;
                excite[bin] = fastLeak - lowComp;
                if (!last && bandPsd[bin] <= bandPsd[bin + 1])
                {
                    begin = bin + 1;
                    break;
                }
            }

            int stop = Math.Min(bandEnd, 22);
            for (int bin = begin; bin < stop; bin++)
            {
                if (!(lfe && bin == 6))
                {
                    lowComp = LowComp(lowComp, bandPsd[bin], bandPsd[bin + 1], bin);
                }

                fastLeak = Math.Max(fastLeak - fdecay, bandPsd[bin] - fgain);
                slowLeak = Math.Max(slowLeak - sdecay, bandPsd[bin] - sgain);
                excite[bin] = Math.Max(fastLeak - lowComp, slowLeak);
            }

            begin = 22;
        }
        else
        {
            begin = bandBegin;
        }

        for (int bin = begin; bin < bandEnd; bin++)
        {
            fastLeak = Math.Max(fastLeak - fdecay, bandPsd[bin] - fgain);
            slowLeak = Math.Max(slowLeak - sdecay, bandPsd[bin] - sgain);
            excite[bin] = Math.Max(fastLeak, slowLeak);
        }

        // The masking curve: excitation lifted below the knee, never under the hearing threshold.
        ReadOnlySpan<short> hth = Ac3Tables.HearingThreshold.Slice(_fscod * 50, 50);
        int knee = _dbKnee;
        Span<int> mask = excite;
        for (int bin = bandBegin; bin < bandEnd; bin++)
        {
            int e = excite[bin];
            if (bandPsd[bin] < knee)
            {
                e += (knee - bandPsd[bin]) >> 2;
            }

            mask[bin] = Math.Max(e, hth[bin]);
        }

        int mode = _deltaMode[ch];
        if (mode is 0 or 1)
        {
            int band = 0;
            for (int seg = 0; seg < _deltaSegments[ch]; seg++)
            {
                band += _deltaOffset[ch][seg];
                int ba = _deltaBa[ch][seg];
                int delta = (ba >= 4 ? ba - 3 : ba - 4) << 7;
                for (int n = 0; n < _deltaLength[ch][seg] && band < 50; n++)
                {
                    mask[band++] += delta;
                }
            }
        }

        int floor = _floor;
        ReadOnlySpan<byte> bapTable = Ac3Tables.Bap;
        int i = start;
        j = bandOf[start];
        do
        {
            lastBin = Math.Min(bandStart[j + 1], end);
            int m = mask[j] - snrOffset - floor;
            m = (m < 0 ? 0 : m) & 0x1fe0;
            m += floor;
            for (; i < lastBin; i++)
            {
                int address = ((3072 - (exp[i] << 7)) - m) >> 5;
                bap[i] = bapTable[Math.Clamp(address, 0, 63)];
            }

            j++;
        }
        while (end > lastBin);
    }

    private static int LowComp(int a, int b0, int b1, int bin)
    {
        if (bin < 7)
        {
            if (b0 + 256 == b1)
            {
                return 384;
            }

            return b0 > b1 ? Math.Max(0, a - 64) : a;
        }

        if (bin < 20)
        {
            if (b0 + 256 == b1)
            {
                return 320;
            }

            return b0 > b1 ? Math.Max(0, a - 64) : a;
        }

        return Math.Max(0, a - 128);
    }

    private bool ReadMantissas(ref Ac3BitReader r)
    {
        MantissaGroups groups = default;
        bool gotCoupling = false;
        for (int ch = 0; ch < _fullChannels; ch++)
        {
            float[] coeffs = _coeffs[ch];
            if (!ReadChannel(ref r, ref groups, coeffs, _exp[ch], _bap[ch], 0, _endMant[ch], _dither[ch]))
            {
                return false;
            }

            if (_cplInUse && _inCoupling[ch] && !gotCoupling)
            {
                // The coupling channel keeps its mantissas without dither: each channel adds its own when it is decoupled.
                if (!ReadChannel(ref r, ref groups, _coeffs[Cpl], _exp[Cpl], _bap[Cpl], _cplStart, _cplStop, false))
                {
                    return false;
                }

                gotCoupling = true;
            }
        }

        if (_lfe)
        {
            float[] lfe = _coeffs[Lfe];
            if (!ReadChannel(ref r, ref groups, lfe, _exp[Lfe], _bap[Lfe], 0, 7, false))
            {
                return false;
            }

            lfe.AsSpan(7).Clear();
        }

        for (int ch = 0; ch < _fullChannels; ch++)
        {
            float[] coeffs = _coeffs[ch];
            if (_cplInUse && _inCoupling[ch])
            {
                Decouple(ch);
                coeffs.AsSpan(_cplStop).Clear();
            }
            else
            {
                coeffs.AsSpan(_endMant[ch]).Clear();
            }
        }

        if (_acmod == 2)
        {
            Rematrix();
        }

        return true;
    }

    /// <summary>Unpacks the mantissas of bins start to end into coefficients, mantissa times 2^-exponent (A/52 section 7.3).</summary>
    private bool ReadChannel(ref Ac3BitReader reader, ref MantissaGroups groups, float[] coeffs, byte[] exp, byte[] bap, int start, int end, bool dither)
    {
        if ((uint)end > (uint)coeffs.Length || (uint)end > (uint)bap.Length || (uint)end > (uint)exp.Length)
        {
            return false;
        }

        // The reader, the group state and the dither generator live in locals here, so the loop keeps them in registers.
        ReadOnlySpan<byte> data = reader.Data;
        reader.GetState(out ulong cache, out int cached, out int next);
        int at3 = groups.At3, left3 = groups.Left3, at5 = groups.At5, left5 = groups.Left5, at11 = groups.At11, left11 = groups.Left11;
        uint random = _random;
        float[] pow = Ac3Tables.PowerOfTwo;
        float[] group3 = Ac3Tables.Group3;
        float[] group5 = Ac3Tables.Group5;
        float[] group11 = Ac3Tables.Group11;
        float[] level7 = Ac3Tables.Level7;
        float[] level15 = Ac3Tables.Level15;
        ReadOnlySpan<byte> sizes = Ac3Tables.MantissaBits;
        bool ok = true;
        for (int bin = start; bin < end; bin++)
        {
            int b = bap[bin];
            if (b == 0)
            {
                float d = 0;
                if (dither)
                {
                    random ^= random << 13;
                    random ^= random >> 17;
                    random ^= random << 5;
                    d = (int)random * (0.70710678f / 2147483648f);
                }

                coeffs[bin] = d * pow[exp[bin]];
                continue;
            }

            // No mantissa or group code is longer than 16 bits: top up to at least that many.
            if (cached < 16)
            {
                if (next + 8 <= data.Length)
                {
                    int bytes = (63 - cached) >> 3;
                    ulong word = BinaryPrimitives.ReadUInt64BigEndian(data[next..]);
                    cache = (cache << (8 * bytes)) | (word >> (64 - (8 * bytes)));
                    next += bytes;
                    cached += 8 * bytes;
                }
                else
                {
                    while (cached <= 55)
                    {
                        cache = (cache << 8) | (next < data.Length ? data[next] : 0u);
                        next++;
                        cached += 8;
                    }
                }
            }

            float value;
            switch (b)
            {
                case 1:
                    if (left3 == 0)
                    {
                        cached -= 5;
                        int code = (int)(cache >> cached) & 31;
                        if (code > 26)
                        {
                            ok = false;
                            goto done;
                        }

                        at3 = code * 3;
                        left3 = 3;
                    }

                    value = group3[at3++];
                    left3--;
                    break;
                case 2:
                    if (left5 == 0)
                    {
                        cached -= 7;
                        int code = (int)(cache >> cached) & 127;
                        if (code > 124)
                        {
                            ok = false;
                            goto done;
                        }

                        at5 = code * 3;
                        left5 = 3;
                    }

                    value = group5[at5++];
                    left5--;
                    break;
                case 3:
                    cached -= 3;
                    value = level7[(int)(cache >> cached) & 7];
                    break;
                case 4:
                    if (left11 == 0)
                    {
                        cached -= 7;
                        int code = (int)(cache >> cached) & 127;
                        if (code > 120)
                        {
                            ok = false;
                            goto done;
                        }

                        at11 = code * 2;
                        left11 = 2;
                    }

                    value = group11[at11++];
                    left11--;
                    break;
                case 5:
                    cached -= 4;
                    value = level15[(int)(cache >> cached) & 15];
                    break;
                default:
                    {
                        // Asymmetric: a two's complement fraction of 5 to 16 bits.
                        int bits = sizes[b & 15];
                        cached -= bits;
                        int m = (int)((long)(cache << (64 - bits - cached)) >> (64 - bits));
                        coeffs[bin] = m * pow[bits - 1 + exp[bin]];
                        continue;
                    }
            }

            coeffs[bin] = value * pow[exp[bin]];
        }

    done:
        reader.SetState(cache, cached, next);
        groups = new MantissaGroups { At3 = at3, Left3 = left3, At5 = at5, Left5 = left5, At11 = at11, Left11 = left11 };
        _random = random;
        return ok;
    }

    /// <summary>Rebuilds a coupled channel's bins from the coupling channel and its coordinates (A/52 section 7.4.3).</summary>
    private void Decouple(int ch)
    {
        float[] coeffs = _coeffs[ch];
        float[] cpl = _coeffs[Cpl];
        byte[] cplBap = _bap[Cpl];
        byte[] cplExp = _exp[Cpl];
        float[] coords = _cplCoord[ch];
        float[] pow = Ac3Tables.PowerOfTwo;
        bool dither = _dither[ch];
        bool phase = _acmod == 2 && _phaseInUse && ch == 1;
        int band = -1;
        for (int sb = 0; sb < _cplEnd - _cplBegin; sb++)
        {
            band += _bandStructure[sb] ? 0 : 1;
            float coord = coords[sb];
            if (phase && _phaseFlags[band])
            {
                coord = -coord;
            }

            int first = _cplStart + (sb * 12);
            for (int bin = first; bin < first + 12; bin++)
            {
                float value = cplBap[bin] == 0 ? dither ? NextDither() * pow[cplExp[bin]] : 0 : cpl[bin];
                coeffs[bin] = value * coord;
            }
        }
    }

    private void Rematrix()
    {
        ReadOnlySpan<byte> edges = [13, 25, 37, 61, 253];
        int limit = _cplInUse ? _cplStart : Math.Min(_endMant[0], _endMant[1]);
        float[] left = _coeffs[0];
        float[] right = _coeffs[1];
        for (int band = 0; band < _rematBands; band++)
        {
            if (!_remat[band])
            {
                continue;
            }

            int end = Math.Min(edges[band + 1], limit);
            for (int bin = edges[band]; bin < end; bin++)
            {
                float l = left[bin];
                float rr = right[bin];
                left[bin] = l + rr;
                right[bin] = l - rr;
            }
        }
    }

    private void Synthesize(float[][] output, int offset)
    {
        for (int ch = 0; ch < _fullChannels; ch++)
        {
            float gain = _acmod == 0 && ch == 1 ? _gain2 : _gain1;
            Transform(ch, ch, gain, _blockSwitch[ch], output[ch].AsSpan(offset, 256));
        }

        if (_lfe)
        {
            Transform(Lfe, _fullChannels, _gain1, false, output[_fullChannels].AsSpan(offset, 256));
        }
    }

    private void Transform(int ch, int delayIndex, float gain, bool shortBlocks, Span<float> output)
    {
        float[] coeffs = _coeffs[ch];
        if (gain != 1)
        {
            for (int i = 0; i < 256; i++)
            {
                coeffs[i] *= gain;
            }
        }

        _imdct.Transform(coeffs, shortBlocks, _delay[delayIndex], output);
    }

    /// <summary>A dither value uniform in plus or minus 0.707 (A/52 section 7.3.4), from a 32-bit xorshift generator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float NextDither()
    {
        uint x = _random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _random = x;
        return (int)x * (0.70710678f / 2147483648f);
    }

    private static T[][] Jagged<T>(int count, int length)
    {
        T[][] arrays = new T[count][];
        for (int i = 0; i < count; i++)
        {
            arrays[i] = new T[length];
        }

        return arrays;
    }

    /// <summary>The grouped mantissas of baps 1, 2 and 4 left over in the block: groups run on across channels.</summary>
    private struct MantissaGroups
    {
        public int At3;
        public int Left3;
        public int At5;
        public int Left5;
        public int At11;
        public int Left11;
    }
}
