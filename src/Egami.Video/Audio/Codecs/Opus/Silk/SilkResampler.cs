// Translated from libopus 1.5.2 silk/resampler.c, resampler_private_up2_HQ.c, resampler_private_IIR_FIR.c,
// resampler_private_down_FIR.c, resampler_private_AR2.c, resampler_rom.h and resampler_structs.h.
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's resampler between the internal rate (8, 12 or 16 kHz) and the API rate (8, 12, 16, 24 or 48 kHz).</summary>
internal sealed class SilkResampler
{
    private const int MaxFirOrder = 36;
    private const int MaxIirOrder = 6;
    private const int MaxBatchSizeMs = 10;
    private const int BatchSizeIn = 480;
    private const int DownOrderFir0 = 18;
    private const int DownOrderFir1 = 24;
    private const int DownOrderFir2 = 36;
    private const int OrderFir12 = 8;

    private const int UseCopy = 0;
    private const int UseUp2Hq = 1;
    private const int UseIirFir = 2;
    private const int UseDownFir = 3;

    private enum Coefficients
    {
        None,
        Ratio34,
        Ratio23,
        Ratio12,
        Ratio13,
        Ratio14,
        Ratio16,
    }

    // delay_matrix_dec: in (8, 12, 16) by out (8, 12, 16, 24, 48).
    private static ReadOnlySpan<sbyte> DelayMatrixDec => [4, 0, 2, 0, 0, 0, 9, 4, 7, 4, 0, 3, 12, 7, 7];

    // delay_matrix_enc: in (8, 12, 16, 24, 48) by out (8, 12, 16).
    private static ReadOnlySpan<sbyte> DelayMatrixEnc => [6, 0, 3, 0, 7, 3, 0, 1, 10, 0, 2, 6, 18, 10, 12];

    private static ReadOnlySpan<short> Up2Hq0 => [1746, 14986, 39083 - 65536];

    private static ReadOnlySpan<short> Up2Hq1 => [6854, 25769, 55542 - 65536];

    private readonly int[] _sIir = new int[MaxIirOrder];

    // The C union of sFIR.i32 and sFIR.i16; each resampler only uses one of them.
    private readonly int[] _sFir32 = new int[MaxFirOrder];
    private readonly short[] _sFir16 = new short[MaxFirOrder];
    private readonly short[] _delayBuf = new short[48];
    private int _resamplerFunction;
    private int _batchSize;
    private int _invRatioQ16;
    private int _firOrder;
    private int _firFracs;
    private int _fsInKhz;
    private int _fsOutKhz;
    private int _inputDelay;
    private Coefficients _coefs;

    /// <summary>The input delay in samples, for tests.</summary>
    public int InputDelay => _inputDelay;

    private ReadOnlySpan<short> Coefs => _coefs switch
    {
        Coefficients.Ratio34 => SilkTables.Resampler34Coefs,
        Coefficients.Ratio23 => SilkTables.Resampler23Coefs,
        Coefficients.Ratio12 => SilkTables.Resampler12Coefs,
        Coefficients.Ratio13 => SilkTables.Resampler13Coefs,
        Coefficients.Ratio14 => SilkTables.Resampler14Coefs,
        Coefficients.Ratio16 => SilkTables.Resampler16Coefs,
        _ => [],
    };

    /// <summary>Clears the state, as the memset of the whole silk_decoder_state does.</summary>
    public void Clear()
    {
        Array.Clear(_sIir);
        Array.Clear(_sFir32);
        Array.Clear(_sFir16);
        Array.Clear(_delayBuf);
        _resamplerFunction = 0;
        _batchSize = 0;
        _invRatioQ16 = 0;
        _firOrder = 0;
        _firFracs = 0;
        _fsInKhz = 0;
        _fsOutKhz = 0;
        _inputDelay = 0;
        _coefs = Coefficients.None;
    }

    /// <summary>Copies the whole state of another resampler (the memcpy in silk_Decode).</summary>
    public void CopyFrom(SilkResampler other)
    {
        other._sIir.CopyTo(_sIir, 0);
        other._sFir32.CopyTo(_sFir32, 0);
        other._sFir16.CopyTo(_sFir16, 0);
        other._delayBuf.CopyTo(_delayBuf, 0);
        _resamplerFunction = other._resamplerFunction;
        _batchSize = other._batchSize;
        _invRatioQ16 = other._invRatioQ16;
        _firOrder = other._firOrder;
        _firFracs = other._firFracs;
        _fsInKhz = other._fsInKhz;
        _fsOutKhz = other._fsOutKhz;
        _inputDelay = other._inputDelay;
        _coefs = other._coefs;
    }

    private static int RateId(int r) => ((((r >> 12) - (r > 16000 ? 1 : 0)) >> (r > 24000 ? 1 : 0)) - 1);

    /// <summary>silk_resampler_init: <paramref name="forEncoder"/> selects the encoder's direction and delays. Returns 0, or -1 for unsupported rates.</summary>
    public int Init(int fsHzIn, int fsHzOut, bool forEncoder = false)
    {
        Clear();
        if (forEncoder)
        {
            if ((fsHzIn != 8000 && fsHzIn != 12000 && fsHzIn != 16000 && fsHzIn != 24000 && fsHzIn != 48000) ||
                (fsHzOut != 8000 && fsHzOut != 12000 && fsHzOut != 16000))
            {
                return -1;
            }

            _inputDelay = DelayMatrixEnc[(RateId(fsHzIn) * 3) + RateId(fsHzOut)];
        }
        else
        {
            if ((fsHzIn != 8000 && fsHzIn != 12000 && fsHzIn != 16000) ||
                (fsHzOut != 8000 && fsHzOut != 12000 && fsHzOut != 16000 && fsHzOut != 24000 && fsHzOut != 48000))
            {
                return -1;
            }

            _inputDelay = DelayMatrixDec[(RateId(fsHzIn) * 5) + RateId(fsHzOut)];
        }

        _fsInKhz = fsHzIn / 1000;
        _fsOutKhz = fsHzOut / 1000;
        _batchSize = _fsInKhz * MaxBatchSizeMs;
        int up2x = 0;
        if (fsHzOut > fsHzIn)
        {
            if (fsHzOut == fsHzIn * 2)
            {
                _resamplerFunction = UseUp2Hq;
            }
            else
            {
                _resamplerFunction = UseIirFir;
                up2x = 1;
            }
        }
        else if (fsHzOut < fsHzIn)
        {
            _resamplerFunction = UseDownFir;
            if (fsHzOut * 4 == fsHzIn * 3)
            {
                _firFracs = 3;
                _firOrder = DownOrderFir0;
                _coefs = Coefficients.Ratio34;
            }
            else if (fsHzOut * 3 == fsHzIn * 2)
            {
                _firFracs = 2;
                _firOrder = DownOrderFir0;
                _coefs = Coefficients.Ratio23;
            }
            else if (fsHzOut * 2 == fsHzIn)
            {
                _firFracs = 1;
                _firOrder = DownOrderFir1;
                _coefs = Coefficients.Ratio12;
            }
            else if (fsHzOut * 3 == fsHzIn)
            {
                _firFracs = 1;
                _firOrder = DownOrderFir2;
                _coefs = Coefficients.Ratio13;
            }
            else if (fsHzOut * 4 == fsHzIn)
            {
                _firFracs = 1;
                _firOrder = DownOrderFir2;
                _coefs = Coefficients.Ratio14;
            }
            else if (fsHzOut * 6 == fsHzIn)
            {
                _firFracs = 1;
                _firOrder = DownOrderFir2;
                _coefs = Coefficients.Ratio16;
            }
            else
            {
                return -1;
            }
        }
        else
        {
            _resamplerFunction = UseCopy;
        }

        _invRatioQ16 = LShift(LShift(fsHzIn, 14 + up2x) / fsHzOut, 2);
        while (Smulww(_invRatioQ16, fsHzOut) < LShift(fsHzIn, up2x))
        {
            _invRatioQ16++;
        }

        return 0;
    }

    /// <summary>silk_resampler: resamples <paramref name="inLen"/> samples (at least 1 ms) into <paramref name="output"/>.</summary>
    public int Process(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        int nSamples = _fsInKhz - _inputDelay;
        input[..nSamples].CopyTo(_delayBuf.AsSpan(_inputDelay));
        switch (_resamplerFunction)
        {
            case UseUp2Hq:
                Up2Hq(_sIir, output, _delayBuf, _fsInKhz);
                Up2Hq(_sIir, output[_fsOutKhz..], input[nSamples..], inLen - _fsInKhz);
                break;
            case UseIirFir:
                IirFir(output, _delayBuf, _fsInKhz);
                IirFir(output[_fsOutKhz..], input[nSamples..], inLen - _fsInKhz);
                break;
            case UseDownFir:
                DownFir(output, _delayBuf, _fsInKhz);
                DownFir(output[_fsOutKhz..], input[nSamples..], inLen - _fsInKhz);
                break;
            default:
                _delayBuf.AsSpan(0, _fsInKhz).CopyTo(output);
                input.Slice(nSamples, inLen - _fsInKhz).CopyTo(output[_fsOutKhz..]);
                break;
        }

        input.Slice(inLen - _inputDelay, _inputDelay).CopyTo(_delayBuf);
        return 0;
    }

    /// <summary>silk_resampler_private_up2_HQ: 2x upsampling with allpass filters.</summary>
    public static void Up2Hq(Span<int> s, Span<short> output, ReadOnlySpan<short> input, int len)
    {
        ReadOnlySpan<short> hq0 = Up2Hq0;
        ReadOnlySpan<short> hq1 = Up2Hq1;
        unchecked
        {
            for (int k = 0; k < len; k++)
            {
                int in32 = LShift(input[k], 10);

                int y = in32 - s[0];
                int x = Smulwb(y, hq0[0]);
                int out32A = s[0] + x;
                s[0] = in32 + x;

                y = out32A - s[1];
                x = Smulwb(y, hq0[1]);
                int out32B = s[1] + x;
                s[1] = out32A + x;

                y = out32B - s[2];
                x = Smlawb(y, y, hq0[2]);
                out32A = s[2] + x;
                s[2] = out32B + x;

                output[2 * k] = (short)Sat16(RShiftRound(out32A, 10));

                y = in32 - s[3];
                x = Smulwb(y, hq1[0]);
                out32A = s[3] + x;
                s[3] = in32 + x;

                y = out32A - s[4];
                x = Smulwb(y, hq1[1]);
                out32B = s[4] + x;
                s[4] = out32A + x;

                y = out32B - s[5];
                x = Smlawb(y, y, hq1[2]);
                out32A = s[5] + x;
                s[5] = out32B + x;

                output[(2 * k) + 1] = (short)Sat16(RShiftRound(out32A, 10));
            }
        }
    }

    /// <summary>silk_resampler_private_IIR_FIR_INTERPOL; returns the number of samples written.</summary>
    private static int IirFirInterpol(Span<short> output, ReadOnlySpan<short> buf, int maxIndexQ16, int indexIncrementQ16)
    {
        ReadOnlySpan<short> fir = SilkTables.ResamplerFracFir12;
        int n = 0;
        for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += indexIncrementQ16)
        {
            int tableIndex = Smulwb(indexQ16 & 0xFFFF, 12);
            ReadOnlySpan<short> b = buf[(indexQ16 >> 16)..];
            int t0 = tableIndex * 4;
            int t1 = (11 - tableIndex) * 4;
            int resQ15 = Smulbb(b[0], fir[t0]);
            resQ15 = Smlabb(resQ15, b[1], fir[t0 + 1]);
            resQ15 = Smlabb(resQ15, b[2], fir[t0 + 2]);
            resQ15 = Smlabb(resQ15, b[3], fir[t0 + 3]);
            resQ15 = Smlabb(resQ15, b[4], fir[t1 + 3]);
            resQ15 = Smlabb(resQ15, b[5], fir[t1 + 2]);
            resQ15 = Smlabb(resQ15, b[6], fir[t1 + 1]);
            resQ15 = Smlabb(resQ15, b[7], fir[t1]);
            output[n++] = (short)Sat16(RShiftRound(resQ15, 15));
        }

        return n;
    }

    /// <summary>silk_resampler_private_IIR_FIR: 2x allpass upsampling followed by FIR interpolation.</summary>
    private void IirFir(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        Span<short> buf = stackalloc short[(2 * _batchSize) + OrderFir12];
        _sFir16.AsSpan(0, OrderFir12).CopyTo(buf);
        int indexIncrementQ16 = _invRatioQ16;
        int nSamplesIn;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, _batchSize);
            Up2Hq(_sIir, buf[OrderFir12..], input, nSamplesIn);
            int maxIndexQ16 = LShift(nSamplesIn, 16 + 1);
            int written = IirFirInterpol(output, buf, maxIndexQ16, indexIncrementQ16);
            output = output[written..];
            input = input[nSamplesIn..];
            inLen -= nSamplesIn;
            if (inLen > 0)
            {
                buf.Slice(nSamplesIn << 1, OrderFir12).CopyTo(buf);
            }
            else
            {
                break;
            }
        }

        buf.Slice(nSamplesIn << 1, OrderFir12).CopyTo(_sFir16);
    }

    /// <summary>silk_resampler_down2: downsamples by two with allpass filters; <paramref name="s"/> holds two state values.</summary>
    public static void Down2(Span<int> s, Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        const short Down20 = 9872;
        const short Down21 = 39809 - 65536;
        unchecked
        {
            int len2 = inLen >> 1;
            for (int k = 0; k < len2; k++)
            {
                int in32 = input[2 * k] << 10;
                int y = in32 - s[0];
                int x = Smlawb(y, y, Down21);
                int out32 = s[0] + x;
                s[0] = in32 + x;
                in32 = input[(2 * k) + 1] << 10;
                y = in32 - s[1];
                x = Smulwb(y, Down20);
                out32 += s[1];
                out32 += x;
                s[1] = in32 + x;
                output[k] = (short)Sat16(RShiftRound(out32, 11));
            }
        }
    }

    /// <summary>silk_resampler_down2_3: downsamples by 2/3 at low quality; <paramref name="s"/> holds six state values.</summary>
    public static void Down23(Span<int> s, Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        const int OrderFir = 4;
        ReadOnlySpan<short> coefs = SilkTables.Resampler23CoefsLq;
        Span<int> buf = stackalloc int[BatchSizeIn + OrderFir];
        s[..OrderFir].CopyTo(buf);
        int o = 0;
        int nSamplesIn;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, BatchSizeIn);
            Ar2(s[OrderFir..], buf[OrderFir..], input, coefs, nSamplesIn);
            int bufPtr = 0;
            int counter = nSamplesIn;
            while (counter > 2)
            {
                int resQ6 = Smulwb(buf[bufPtr], coefs[2]);
                resQ6 = Smlawb(resQ6, buf[bufPtr + 1], coefs[3]);
                resQ6 = Smlawb(resQ6, buf[bufPtr + 2], coefs[5]);
                resQ6 = Smlawb(resQ6, buf[bufPtr + 3], coefs[4]);
                output[o++] = (short)Sat16(RShiftRound(resQ6, 6));
                resQ6 = Smulwb(buf[bufPtr + 1], coefs[4]);
                resQ6 = Smlawb(resQ6, buf[bufPtr + 2], coefs[5]);
                resQ6 = Smlawb(resQ6, buf[bufPtr + 3], coefs[3]);
                resQ6 = Smlawb(resQ6, buf[bufPtr + 4], coefs[2]);
                output[o++] = (short)Sat16(RShiftRound(resQ6, 6));
                bufPtr += 3;
                counter -= 3;
            }

            input = input[nSamplesIn..];
            inLen -= nSamplesIn;
            if (inLen > 0)
            {
                buf.Slice(nSamplesIn, OrderFir).CopyTo(buf);
            }
            else
            {
                break;
            }
        }

        buf.Slice(nSamplesIn, OrderFir).CopyTo(s);
    }

    /// <summary>silk_resampler_private_AR2: second-order AR filter, output in Q8.</summary>
    private static void Ar2(Span<int> s, Span<int> outQ8, ReadOnlySpan<short> input, ReadOnlySpan<short> aQ14, int len)
    {
        unchecked
        {
            for (int k = 0; k < len; k++)
            {
                int out32 = AddLShift32(s[0], input[k], 8);
                outQ8[k] = out32;
                out32 = LShift(out32, 2);
                s[0] = Smlawb(s[1], out32, aQ14[0]);
                s[1] = Smulwb(out32, aQ14[1]);
            }
        }
    }

    /// <summary>silk_resampler_private_down_FIR_INTERPOL; returns the number of samples written.</summary>
    private static int DownFirInterpol(Span<short> output, ReadOnlySpan<int> buf, ReadOnlySpan<short> firCoefs, int firOrder, int firFracs, int maxIndexQ16, int indexIncrementQ16)
    {
        int n = 0;
        unchecked
        {
            switch (firOrder)
            {
                case DownOrderFir0:
                    for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += indexIncrementQ16)
                    {
                        ReadOnlySpan<int> b = buf[(indexQ16 >> 16)..];
                        int interpolInd = Smulwb(indexQ16 & 0xFFFF, firFracs);
                        ReadOnlySpan<short> ip = firCoefs[(DownOrderFir0 / 2 * interpolInd)..];
                        int resQ6 = Smulwb(b[0], ip[0]);
                        resQ6 = Smlawb(resQ6, b[1], ip[1]);
                        resQ6 = Smlawb(resQ6, b[2], ip[2]);
                        resQ6 = Smlawb(resQ6, b[3], ip[3]);
                        resQ6 = Smlawb(resQ6, b[4], ip[4]);
                        resQ6 = Smlawb(resQ6, b[5], ip[5]);
                        resQ6 = Smlawb(resQ6, b[6], ip[6]);
                        resQ6 = Smlawb(resQ6, b[7], ip[7]);
                        resQ6 = Smlawb(resQ6, b[8], ip[8]);
                        ip = firCoefs[(DownOrderFir0 / 2 * (firFracs - 1 - interpolInd))..];
                        resQ6 = Smlawb(resQ6, b[17], ip[0]);
                        resQ6 = Smlawb(resQ6, b[16], ip[1]);
                        resQ6 = Smlawb(resQ6, b[15], ip[2]);
                        resQ6 = Smlawb(resQ6, b[14], ip[3]);
                        resQ6 = Smlawb(resQ6, b[13], ip[4]);
                        resQ6 = Smlawb(resQ6, b[12], ip[5]);
                        resQ6 = Smlawb(resQ6, b[11], ip[6]);
                        resQ6 = Smlawb(resQ6, b[10], ip[7]);
                        resQ6 = Smlawb(resQ6, b[9], ip[8]);
                        output[n++] = (short)Sat16(RShiftRound(resQ6, 6));
                    }

                    break;
                case DownOrderFir1:
                    for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += indexIncrementQ16)
                    {
                        ReadOnlySpan<int> b = buf[(indexQ16 >> 16)..];
                        int resQ6 = Smulwb(b[0] + b[23], firCoefs[0]);
                        for (int j = 1; j < 12; j++)
                        {
                            resQ6 = Smlawb(resQ6, b[j] + b[23 - j], firCoefs[j]);
                        }

                        output[n++] = (short)Sat16(RShiftRound(resQ6, 6));
                    }

                    break;
                case DownOrderFir2:
                    for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += indexIncrementQ16)
                    {
                        ReadOnlySpan<int> b = buf[(indexQ16 >> 16)..];
                        int resQ6 = Smulwb(b[0] + b[35], firCoefs[0]);
                        for (int j = 1; j < 18; j++)
                        {
                            resQ6 = Smlawb(resQ6, b[j] + b[35 - j], firCoefs[j]);
                        }

                        output[n++] = (short)Sat16(RShiftRound(resQ6, 6));
                    }

                    break;
            }
        }

        return n;
    }

    /// <summary>silk_resampler_private_down_FIR: AR2 filter followed by FIR interpolation.</summary>
    private void DownFir(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        Span<int> buf = stackalloc int[_batchSize + _firOrder];
        _sFir32.AsSpan(0, _firOrder).CopyTo(buf);
        ReadOnlySpan<short> coefs = Coefs;
        ReadOnlySpan<short> firCoefs = coefs[2..];
        int indexIncrementQ16 = _invRatioQ16;
        int nSamplesIn;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, _batchSize);
            Ar2(_sIir, buf[_firOrder..], input, coefs, nSamplesIn);
            int maxIndexQ16 = LShift(nSamplesIn, 16);
            int written = DownFirInterpol(output, buf, firCoefs, _firOrder, _firFracs, maxIndexQ16, indexIncrementQ16);
            output = output[written..];
            input = input[nSamplesIn..];
            inLen -= nSamplesIn;
            if (inLen > 1)
            {
                buf.Slice(nSamplesIn, _firOrder).CopyTo(buf);
            }
            else
            {
                break;
            }
        }

        buf.Slice(nSamplesIn, _firOrder).CopyTo(_sFir32);
    }
}
