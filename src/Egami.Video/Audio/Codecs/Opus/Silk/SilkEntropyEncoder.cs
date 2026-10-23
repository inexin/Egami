// Translated from libopus 1.5.2 silk/encode_pulses.c, shell_coder.c, code_signs.c and stereo_encode_pred.c (the encoder's parts).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>SILK's entropy coding of the excitation pulses and the stereo predictors.</summary>
internal static class SilkEntropyEncoder
{
    private const int ShellCodecFrameLength = 16;
    private const int Log2ShellCodecFrameLength = 4;
    private const int NRateLevels = 10;
    private const int SilkMaxPulses = 16;
    private const int PulsesRow = SilkMaxPulses + 2;

    /// <summary>silk_encode_pulses. <paramref name="pulses"/> must hold a whole number of 16-pulse blocks.</summary>
    public static void EncodePulses(OpusRangeEncoder enc, int signalType, int quantOffsetType, Span<sbyte> pulses, int frameLength)
    {
        Span<int> pulsesComb = stackalloc int[8];
        pulsesComb.Clear();
        int iter = frameLength >> Log2ShellCodecFrameLength;
        if (iter * ShellCodecFrameLength < frameLength)
        {
            iter++;
            pulses.Slice(frameLength, ShellCodecFrameLength).Clear();
        }

        Span<int> absPulses = stackalloc int[iter * ShellCodecFrameLength];
        for (int i = 0; i < iter * ShellCodecFrameLength; i++)
        {
            absPulses[i] = Math.Abs((int)pulses[i]);
        }

        Span<int> sumPulses = stackalloc int[iter];
        Span<int> nRshifts = stackalloc int[iter];
        ReadOnlySpan<byte> maxPulses = SilkTables.MaxPulsesTable;
        for (int i = 0; i < iter; i++)
        {
            Span<int> absPtr = absPulses.Slice(i * ShellCodecFrameLength, ShellCodecFrameLength);
            nRshifts[i] = 0;
            while (true)
            {
                int scaleDown = CombineAndCheck(pulsesComb, absPtr, maxPulses[0], 8);
                scaleDown += CombineAndCheck(pulsesComb, pulsesComb, maxPulses[1], 4);
                scaleDown += CombineAndCheck(pulsesComb, pulsesComb, maxPulses[2], 2);
                scaleDown += CombineAndCheck(sumPulses.Slice(i, 1), pulsesComb, maxPulses[3], 1);
                if (scaleDown != 0)
                {
                    nRshifts[i]++;
                    for (int k = 0; k < ShellCodecFrameLength; k++)
                    {
                        absPtr[k] >>= 1;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        // Find the rate level that needs the fewest bits for the pulses per block.
        int minSumBitsQ5 = int.MaxValue;
        int rateLevelIndex = 0;
        for (int k = 0; k < NRateLevels - 1; k++)
        {
            ReadOnlySpan<byte> nBits = SilkTables.PulsesPerBlockBitsQ5.Slice(k * PulsesRow, PulsesRow);
            int sumBitsQ5 = SilkTables.RateLevelsBitsQ5[((signalType >> 1) * (NRateLevels - 1)) + k];
            for (int i = 0; i < iter; i++)
            {
                sumBitsQ5 += nRshifts[i] > 0 ? nBits[SilkMaxPulses + 1] : nBits[sumPulses[i]];
            }

            if (sumBitsQ5 < minSumBitsQ5)
            {
                minSumBitsQ5 = sumBitsQ5;
                rateLevelIndex = k;
            }
        }

        enc.EncodeIcdf(rateLevelIndex, SilkTables.RateLevelsIcdf.Slice((signalType >> 1) * (NRateLevels - 1), NRateLevels - 1), 8);

        ReadOnlySpan<byte> cdf = SilkTables.PulsesPerBlockIcdf.Slice(rateLevelIndex * PulsesRow, PulsesRow);
        ReadOnlySpan<byte> lastCdf = SilkTables.PulsesPerBlockIcdf.Slice((NRateLevels - 1) * PulsesRow, PulsesRow);
        for (int i = 0; i < iter; i++)
        {
            if (nRshifts[i] == 0)
            {
                enc.EncodeIcdf(sumPulses[i], cdf, 8);
            }
            else
            {
                enc.EncodeIcdf(SilkMaxPulses + 1, cdf, 8);
                for (int k = 0; k < nRshifts[i] - 1; k++)
                {
                    enc.EncodeIcdf(SilkMaxPulses + 1, lastCdf, 8);
                }

                enc.EncodeIcdf(sumPulses[i], lastCdf, 8);
            }
        }

        for (int i = 0; i < iter; i++)
        {
            if (sumPulses[i] > 0)
            {
                ShellEncoder(enc, absPulses.Slice(i * ShellCodecFrameLength, ShellCodecFrameLength));
            }
        }

        // LSB encoding.
        for (int i = 0; i < iter; i++)
        {
            if (nRshifts[i] > 0)
            {
                int pulsesPtr = i * ShellCodecFrameLength;
                int nLs = nRshifts[i] - 1;
                for (int k = 0; k < ShellCodecFrameLength; k++)
                {
                    int absQ = (sbyte)Math.Abs((int)pulses[pulsesPtr + k]);
                    for (int j = nLs; j > 0; j--)
                    {
                        enc.EncodeIcdf((absQ >> j) & 1, SilkTables.LsbIcdf, 8);
                    }

                    enc.EncodeIcdf(absQ & 1, SilkTables.LsbIcdf, 8);
                }
            }
        }

        EncodeSigns(enc, pulses, frameLength, signalType, quantOffsetType, sumPulses);
    }

    private static int CombineAndCheck(Span<int> pulsesComb, ReadOnlySpan<int> pulsesIn, int maxPulses, int len)
    {
        for (int k = 0; k < len; k++)
        {
            int sum = pulsesIn[2 * k] + pulsesIn[(2 * k) + 1];
            if (sum > maxPulses)
            {
                return 1;
            }

            pulsesComb[k] = sum;
        }

        return 0;
    }

    /// <summary>silk_shell_encoder: one shell code frame of 16 pulse amplitudes.</summary>
    private static void ShellEncoder(OpusRangeEncoder enc, ReadOnlySpan<int> pulses0)
    {
        Span<int> pulses1 = stackalloc int[8];
        Span<int> pulses2 = stackalloc int[4];
        Span<int> pulses3 = stackalloc int[2];
        Span<int> pulses4 = stackalloc int[1];
        CombinePulses(pulses1, pulses0, 8);
        CombinePulses(pulses2, pulses1, 4);
        CombinePulses(pulses3, pulses2, 2);
        CombinePulses(pulses4, pulses3, 1);
        ReadOnlySpan<byte> t0 = SilkTables.ShellCodeTable0;
        ReadOnlySpan<byte> t1 = SilkTables.ShellCodeTable1;
        ReadOnlySpan<byte> t2 = SilkTables.ShellCodeTable2;
        ReadOnlySpan<byte> t3 = SilkTables.ShellCodeTable3;
        EncodeSplit(enc, pulses3[0], pulses4[0], t3);
        EncodeSplit(enc, pulses2[0], pulses3[0], t2);
        EncodeSplit(enc, pulses1[0], pulses2[0], t1);
        EncodeSplit(enc, pulses0[0], pulses1[0], t0);
        EncodeSplit(enc, pulses0[2], pulses1[1], t0);
        EncodeSplit(enc, pulses1[2], pulses2[1], t1);
        EncodeSplit(enc, pulses0[4], pulses1[2], t0);
        EncodeSplit(enc, pulses0[6], pulses1[3], t0);
        EncodeSplit(enc, pulses2[2], pulses3[1], t2);
        EncodeSplit(enc, pulses1[4], pulses2[2], t1);
        EncodeSplit(enc, pulses0[8], pulses1[4], t0);
        EncodeSplit(enc, pulses0[10], pulses1[5], t0);
        EncodeSplit(enc, pulses1[6], pulses2[3], t1);
        EncodeSplit(enc, pulses0[12], pulses1[6], t0);
        EncodeSplit(enc, pulses0[14], pulses1[7], t0);
    }

    private static void CombinePulses(Span<int> output, ReadOnlySpan<int> input, int len)
    {
        for (int k = 0; k < len; k++)
        {
            output[k] = input[2 * k] + input[(2 * k) + 1];
        }
    }

    private static void EncodeSplit(OpusRangeEncoder enc, int pChild1, int p, ReadOnlySpan<byte> shellTable)
    {
        if (p > 0)
        {
            enc.EncodeIcdf(pChild1, shellTable[SilkTables.ShellCodeTableOffsets[p]..], 8);
        }
    }

    /// <summary>silk_encode_signs.</summary>
    private static void EncodeSigns(OpusRangeEncoder enc, ReadOnlySpan<sbyte> pulses, int length, int signalType, int quantOffsetType, ReadOnlySpan<int> sumPulses)
    {
        Span<byte> icdf = stackalloc byte[2];
        icdf[1] = 0;
        int i = 7 * (quantOffsetType + (signalType << 1));
        ReadOnlySpan<byte> icdfPtr = SilkTables.SignIcdf[i..];
        length = (length + (ShellCodecFrameLength / 2)) >> Log2ShellCodecFrameLength;
        int q = 0;
        for (i = 0; i < length; i++)
        {
            int p = sumPulses[i];
            if (p > 0)
            {
                icdf[0] = icdfPtr[Math.Min(p & 0x1F, 6)];
                for (int j = 0; j < ShellCodecFrameLength; j++)
                {
                    if (pulses[q + j] != 0)
                    {
                        enc.EncodeIcdf((pulses[q + j] >> 15) + 1, icdf, 8);
                    }
                }
            }

            q += ShellCodecFrameLength;
        }
    }

    /// <summary>silk_stereo_encode_pred: <paramref name="ix"/> is ix[ 2 ][ 3 ], flattened.</summary>
    public static void StereoEncodePred(OpusRangeEncoder enc, ReadOnlySpan<sbyte> ix)
    {
        int n = (5 * ix[2]) + ix[5];
        enc.EncodeIcdf(n, SilkTables.StereoPredJointIcdf, 8);
        for (n = 0; n < 2; n++)
        {
            enc.EncodeIcdf(ix[(n * 3) + 0], SilkTables.Uniform3Icdf, 8);
            enc.EncodeIcdf(ix[(n * 3) + 1], SilkTables.Uniform5Icdf, 8);
        }
    }

    /// <summary>silk_stereo_encode_mid_only.</summary>
    public static void StereoEncodeMidOnly(OpusRangeEncoder enc, int midOnlyFlag) => enc.EncodeIcdf(midOnlyFlag, SilkTables.StereoOnlyCodeMidIcdf, 8);
}
