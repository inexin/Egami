// Translated from libopus 1.5.2 silk/VAD.c and ana_filt_bank_1.c.
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's voice activity detector: speech activity, input tilt and per-band quality from four subband energies.</summary>
internal static class SilkVad
{
    private static ReadOnlySpan<int> TiltWeights => [30000, 6000, -12000, -12000];

    /// <summary>silk_VAD_Init.</summary>
    public static void Init(SilkVadState vad)
    {
        Array.Clear(vad.AnaState);
        Array.Clear(vad.AnaState1);
        Array.Clear(vad.AnaState2);
        Array.Clear(vad.XnrgSubfr);
        vad.HpState = 0;
        for (int b = 0; b < VadNBands; b++)
        {
            vad.NoiseLevelBias[b] = Math.Max(VadNoiseLevelsBias / (b + 1), 1);
        }

        for (int b = 0; b < VadNBands; b++)
        {
            vad.Nl[b] = 100 * vad.NoiseLevelBias[b];
            vad.InvNl[b] = int.MaxValue / vad.Nl[b];
        }

        vad.Counter = 15;
        for (int b = 0; b < VadNBands; b++)
        {
            vad.NrgRatioSmthQ8[b] = 100 * 256;
        }
    }

    /// <summary>silk_VAD_GetSA_Q8: returns the speech activity in Q8 and sets the input tilt and quality bands.</summary>
    public static int GetSpeechActivityQ8(SilkVadState vad, ReadOnlySpan<short> pIn, int frameLength, int fsKhz, out int inputTiltQ15, Span<int> inputQualityBandsQ15)
    {
        int decimatedFramelength1 = frameLength >> 1;
        int decimatedFramelength2 = frameLength >> 2;
        int decimatedFramelength = frameLength >> 3;
        Span<int> xOffset = stackalloc int[VadNBands];
        xOffset[0] = 0;
        xOffset[1] = decimatedFramelength + decimatedFramelength2;
        xOffset[2] = xOffset[1] + decimatedFramelength;
        xOffset[3] = xOffset[2] + decimatedFramelength2;
        Span<short> x = stackalloc short[xOffset[3] + decimatedFramelength1];
        AnaFiltBank1(pIn, vad.AnaState, x, x[xOffset[3]..], frameLength);
        AnaFiltBank1(x, vad.AnaState1, x, x[xOffset[2]..], decimatedFramelength1);
        AnaFiltBank1(x, vad.AnaState2, x, x[xOffset[1]..], decimatedFramelength2);

        // HP filter on the lowest band (differentiator).
        x[decimatedFramelength - 1] = (short)(x[decimatedFramelength - 1] >> 1);
        short hpStateTmp = x[decimatedFramelength - 1];
        for (int i = decimatedFramelength - 1; i > 0; i--)
        {
            x[i - 1] = (short)(x[i - 1] >> 1);
            x[i] -= x[i - 1];
        }

        x[0] -= vad.HpState;
        vad.HpState = hpStateTmp;

        Span<int> xnrg = stackalloc int[VadNBands];
        int sumSquared = 0;
        for (int b = 0; b < VadNBands; b++)
        {
            decimatedFramelength = frameLength >> Math.Min(VadNBands - b, VadNBands - 1);
            int decSubframeLength = decimatedFramelength >> VadInternalSubframesLog2;
            int decSubframeOffset = 0;
            xnrg[b] = vad.XnrgSubfr[b];
            for (int s = 0; s < VadInternalSubframes; s++)
            {
                sumSquared = 0;
                for (int i = 0; i < decSubframeLength; i++)
                {
                    int xTmp = x[xOffset[b] + i + decSubframeOffset] >> 3;
                    sumSquared = Smlabb(sumSquared, xTmp, xTmp);
                }

                xnrg[b] = s < VadInternalSubframes - 1 ? AddPosSat32(xnrg[b], sumSquared) : AddPosSat32(xnrg[b], sumSquared >> 1);
                decSubframeOffset += decSubframeLength;
            }

            vad.XnrgSubfr[b] = sumSquared;
        }

        GetNoiseLevels(xnrg, vad);

        // Signal-plus-noise to noise ratio estimation.
        Span<int> nrgToNoiseRatioQ8 = stackalloc int[VadNBands];
        sumSquared = 0;
        int inputTilt = 0;
        int speechNrg, snrQ7;
        for (int b = 0; b < VadNBands; b++)
        {
            speechNrg = xnrg[b] - vad.Nl[b];
            if (speechNrg > 0)
            {
                nrgToNoiseRatioQ8[b] = (xnrg[b] & unchecked((int)0xFF800000)) == 0
                    ? (xnrg[b] << 8) / (vad.Nl[b] + 1)
                    : xnrg[b] / ((vad.Nl[b] >> 8) + 1);
                snrQ7 = SilkSigProc.Lin2Log(nrgToNoiseRatioQ8[b]) - (8 * 128);
                sumSquared = Smlabb(sumSquared, snrQ7, snrQ7);
                if (speechNrg < (1 << 20))
                {
                    snrQ7 = Smulwb(SqrtApprox(speechNrg) << 6, snrQ7);
                }

                inputTilt = Smlawb(inputTilt, TiltWeights[b], snrQ7);
            }
            else
            {
                nrgToNoiseRatioQ8[b] = 256;
            }
        }

        sumSquared /= VadNBands;
        int pSnrDbQ7 = (short)(3 * SqrtApprox(sumSquared));
        int saQ15 = SigmQ15(Smulwb(VadSnrFactorQ16, pSnrDbQ7) - VadNegativeOffsetQ5);
        inputTiltQ15 = (SigmQ15(inputTilt) - 16384) << 1;

        // Scale the sigmoid output based on power levels.
        speechNrg = 0;
        for (int b = 0; b < VadNBands; b++)
        {
            speechNrg += (b + 1) * ((xnrg[b] - vad.Nl[b]) >> 4);
        }

        if (frameLength == 20 * fsKhz)
        {
            speechNrg >>= 1;
        }

        if (speechNrg <= 0)
        {
            saQ15 >>= 1;
        }
        else if (speechNrg < 16384)
        {
            speechNrg = SqrtApprox(speechNrg << 16);
            saQ15 = Smulwb(32768 + speechNrg, saQ15);
        }

        int speechActivityQ8 = Math.Min(saQ15 >> 7, byte.MaxValue);

        // Energy level and SNR estimation.
        int smoothCoefQ16 = Smulwb(VadSnrSmoothCoefQ18, Smulwb(saQ15, saQ15));
        if (frameLength == 10 * fsKhz)
        {
            smoothCoefQ16 >>= 1;
        }

        for (int b = 0; b < VadNBands; b++)
        {
            vad.NrgRatioSmthQ8[b] = Smlawb(vad.NrgRatioSmthQ8[b], nrgToNoiseRatioQ8[b] - vad.NrgRatioSmthQ8[b], smoothCoefQ16);
            snrQ7 = 3 * (SilkSigProc.Lin2Log(vad.NrgRatioSmthQ8[b]) - (8 * 128));
            inputQualityBandsQ15[b] = SigmQ15((snrQ7 - (16 * 128)) >> 4);
        }

        return speechActivityQ8;
    }

    private static void GetNoiseLevels(ReadOnlySpan<int> pX, SilkVadState vad)
    {
        int minCoef;
        if (vad.Counter < 1000)
        {
            minCoef = short.MaxValue / ((vad.Counter >> 4) + 1);
            vad.Counter++;
        }
        else
        {
            minCoef = 0;
        }

        for (int k = 0; k < VadNBands; k++)
        {
            int nl = vad.Nl[k];
            int nrg = AddPosSat32(pX[k], vad.NoiseLevelBias[k]);
            int invNrg = int.MaxValue / nrg;
            int coef;
            if (nrg > nl << 3)
            {
                coef = VadNoiseLevelSmoothCoefQ16 >> 3;
            }
            else if (nrg < nl)
            {
                coef = VadNoiseLevelSmoothCoefQ16;
            }
            else
            {
                coef = Smulwb(Smulww(invNrg, nl), VadNoiseLevelSmoothCoefQ16 << 1);
            }

            coef = Math.Max(coef, minCoef);
            vad.InvNl[k] = Smlawb(vad.InvNl[k], invNrg - vad.InvNl[k], coef);
            nl = int.MaxValue / vad.InvNl[k];
            nl = Math.Min(nl, 0x00FFFFFF);
            vad.Nl[k] = nl;
        }
    }

    /// <summary>silk_ana_filt_bank_1: splits a signal into two decimated bands with first-order allpass filters.</summary>
    private static void AnaFiltBank1(ReadOnlySpan<short> input, Span<int> s, Span<short> outL, Span<short> outH, int n)
    {
        const short AFb120 = 5394 << 1;
        const short AFb121 = -24290;
        int n2 = n >> 1;
        for (int k = 0; k < n2; k++)
        {
            int in32 = input[2 * k] << 10;
            int y = unchecked(in32 - s[0]);
            int xv = Smlawb(y, y, AFb121);
            int out1 = unchecked(s[0] + xv);
            s[0] = unchecked(in32 + xv);

            in32 = input[(2 * k) + 1] << 10;
            y = unchecked(in32 - s[1]);
            xv = Smulwb(y, AFb120);
            int out2 = unchecked(s[1] + xv);
            s[1] = unchecked(in32 + xv);

            outL[k] = (short)Sat16(RShiftRound(unchecked(out2 + out1), 11));
            outH[k] = (short)Sat16(RShiftRound(unchecked(out2 - out1), 11));
        }
    }
}
