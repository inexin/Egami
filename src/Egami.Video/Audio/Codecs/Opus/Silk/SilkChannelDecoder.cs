// Translated from libopus 1.5.2 silk/structs.h (silk_decoder_state), init_decoder.c, decoder_set_fs.c, decode_frame.c,
// decode_indices.c, decode_parameters.c, decode_pulses.c, shell_coder.c and code_signs.c (the decoder's parts).
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>silk_decoder_control: the parameters of one decoded frame.</summary>
internal sealed class SilkDecoderControl
{
    public readonly int[] PitchL = new int[SilkChannelDecoder.MaxNbSubfr];
    public readonly int[] GainsQ16 = new int[SilkChannelDecoder.MaxNbSubfr];

    /// <summary>PredCoef_Q12[ 2 ][ MAX_LPC_ORDER ], flattened.</summary>
    public readonly short[] PredCoefQ12 = new short[2 * SilkChannelDecoder.MaxLpcOrder];
    public readonly short[] LtpCoefQ14 = new short[SilkChannelDecoder.LtpOrder * SilkChannelDecoder.MaxNbSubfr];
    public int LtpScaleQ14;

    public Span<short> PredCoef(int half) => PredCoefQ12.AsSpan(half * SilkChannelDecoder.MaxLpcOrder, SilkChannelDecoder.MaxLpcOrder);

    public void Clear()
    {
        Array.Clear(PitchL);
        Array.Clear(GainsQ16);
        Array.Clear(PredCoefQ12);
        Array.Clear(LtpCoefQ14);
        LtpScaleQ14 = 0;
    }
}

/// <summary>SideInfoIndices: the quantization indices of one frame.</summary>
internal sealed class SilkSideInfoIndices
{
    public readonly sbyte[] GainsIndices = new sbyte[SilkChannelDecoder.MaxNbSubfr];
    public readonly sbyte[] LtpIndex = new sbyte[SilkChannelDecoder.MaxNbSubfr];
    public readonly sbyte[] NlsfIndices = new sbyte[SilkChannelDecoder.MaxLpcOrder + 1];
    public short LagIndex;
    public sbyte ContourIndex;
    public sbyte SignalType;
    public sbyte QuantOffsetType;
    public sbyte NlsfInterpCoefQ2;
    public sbyte PerIndex;
    public sbyte LtpScaleIndex;
    public sbyte Seed;

    public void Clear()
    {
        Array.Clear(GainsIndices);
        Array.Clear(LtpIndex);
        Array.Clear(NlsfIndices);
        LagIndex = 0;
        ContourIndex = 0;
        SignalType = 0;
        QuantOffsetType = 0;
        NlsfInterpCoefQ2 = 0;
        PerIndex = 0;
        LtpScaleIndex = 0;
        Seed = 0;
    }
}

/// <summary>silk_decoder_state: the SILK decoder of one channel (mid or side).</summary>
internal sealed partial class SilkChannelDecoder
{
    public const int MaxNbSubfr = 4;
    public const int MaxLpcOrder = 16;
    public const int MinLpcOrder = 10;
    public const int LtpOrder = 5;
    public const int MaxFsKhz = 16;
    public const int SubFrameLengthMs = 5;
    public const int MaxSubFrameLength = SubFrameLengthMs * MaxFsKhz;
    public const int MaxFrameLength = SubFrameLengthMs * MaxNbSubfr * MaxFsKhz;
    public const int LtpMemLengthMs = 20;
    public const int MaxFramesPerPacket = 3;
    public const int TypeNoVoiceActivity = 0;
    public const int TypeUnvoiced = 1;
    public const int TypeVoiced = 2;
    public const int CodeIndependently = 0;
    public const int CodeIndependentlyNoLtpScaling = 1;
    public const int CodeConditionally = 2;
    public const int FlagDecodeNormal = 0;
    public const int FlagPacketLost = 1;
    public const int FlagDecodeLbrr = 2;
    private const int ShellCodecFrameLength = 16;
    private const int Log2ShellCodecFrameLength = 4;
    private const int MaxNbShellBlocks = MaxFrameLength / ShellCodecFrameLength;
    private const int NRateLevels = 10;
    private const int SilkMaxPulses = 16;
    private const int NlsfQuantMaxAmplitude = 4;
    private const int BweAfterLossQ16 = 63570;

    public int PrevGainQ16;
    public readonly int[] ExcQ14 = new int[MaxFrameLength];
    public readonly int[] SLpcQ14Buf = new int[MaxLpcOrder];
    public readonly short[] OutBuf = new short[MaxFrameLength + (2 * MaxSubFrameLength)];
    public int LagPrev;
    public sbyte LastGainIndex;
    public int FsKhz;
    public int FsApiHz;
    public int NbSubfr;
    public int FrameLength;
    public int SubfrLength;
    public int LtpMemLength;
    public int LpcOrder;
    public readonly short[] PrevNlsfQ15 = new short[MaxLpcOrder];
    public int FirstFrameAfterReset;
    public int NFramesDecoded;
    public int NFramesPerPacket;
    public int EcPrevSignalType;
    public short EcPrevLagIndex;
    public readonly int[] VadFlags = new int[MaxFramesPerPacket];
    public int LbrrFlag;
    public readonly int[] LbrrFlags = new int[MaxFramesPerPacket];
    public readonly SilkResampler ResamplerState = new();
    public SilkNlsfCodebook? NlsfCodebook;
    public readonly SilkSideInfoIndices Indices = new();
    public int LossCnt;
    public int PrevSignalType;

    // pitch_lag_low_bits_iCDF and pitch_contour_iCDF, kept as which table they point at.
    private int _pitchLagLowBitsTable;
    private int _pitchContourTable;

    private readonly SilkDecoderControl _control = new();
    private readonly short[] _pulses = new short[MaxFrameLength];

    public SilkChannelDecoder()
    {
        Init();
    }

    private ReadOnlySpan<byte> PitchLagLowBitsIcdf => _pitchLagLowBitsTable switch
    {
        16 => SilkTables.Uniform8Icdf,
        12 => SilkTables.Uniform6Icdf,
        _ => SilkTables.Uniform4Icdf,
    };

    private ReadOnlySpan<byte> PitchContourIcdf => _pitchContourTable switch
    {
        0 => SilkTables.PitchContourNbIcdf,
        1 => SilkTables.PitchContour10MsNbIcdf,
        2 => SilkTables.PitchContourIcdf,
        _ => SilkTables.PitchContour10MsIcdf,
    };

    /// <summary>silk_reset_decoder: clears the state (the whole struct, with OSCE off) and resets CNG and PLC.</summary>
    public void Reset()
    {
        PrevGainQ16 = 0;
        Array.Clear(ExcQ14);
        Array.Clear(SLpcQ14Buf);
        Array.Clear(OutBuf);
        LagPrev = 0;
        LastGainIndex = 0;
        FsKhz = 0;
        FsApiHz = 0;
        NbSubfr = 0;
        FrameLength = 0;
        SubfrLength = 0;
        LtpMemLength = 0;
        LpcOrder = 0;
        Array.Clear(PrevNlsfQ15);
        FirstFrameAfterReset = 0;
        _pitchLagLowBitsTable = 0;
        _pitchContourTable = 0;
        NFramesDecoded = 0;
        NFramesPerPacket = 0;
        EcPrevSignalType = 0;
        EcPrevLagIndex = 0;
        Array.Clear(VadFlags);
        LbrrFlag = 0;
        Array.Clear(LbrrFlags);
        ResamplerState.Clear();
        NlsfCodebook = null;
        Indices.Clear();
        ClearCng();
        LossCnt = 0;
        PrevSignalType = 0;
        ClearPlc();

        FirstFrameAfterReset = 1;
        PrevGainQ16 = 65536;
        CngReset();
        PlcReset();
    }

    /// <summary>silk_init_decoder.</summary>
    public void Init() => Reset();

    /// <summary>silk_decoder_set_fs: sets the internal and API sampling rates. Returns 0 or the resampler's error.</summary>
    public int SetFs(int fsKhz, int fsApiHz)
    {
        int ret = 0;
        SubfrLength = Smulbb(SubFrameLengthMs, fsKhz);
        int frameLength = Smulbb(NbSubfr, SubfrLength);
        if (FsKhz != fsKhz || FsApiHz != fsApiHz)
        {
            ret += ResamplerState.Init(Smulbb(fsKhz, 1000), fsApiHz);
            FsApiHz = fsApiHz;
        }

        if (FsKhz != fsKhz || frameLength != FrameLength)
        {
            if (fsKhz == 8)
            {
                _pitchContourTable = NbSubfr == MaxNbSubfr ? 0 : 1;
            }
            else
            {
                _pitchContourTable = NbSubfr == MaxNbSubfr ? 2 : 3;
            }

            if (FsKhz != fsKhz)
            {
                LtpMemLength = Smulbb(LtpMemLengthMs, fsKhz);
                if (fsKhz == 8 || fsKhz == 12)
                {
                    LpcOrder = MinLpcOrder;
                    NlsfCodebook = SilkNlsfCodebook.NarrowMedium;
                }
                else
                {
                    LpcOrder = MaxLpcOrder;
                    NlsfCodebook = SilkNlsfCodebook.Wide;
                }

                _pitchLagLowBitsTable = fsKhz;
                FirstFrameAfterReset = 1;
                LagPrev = 100;
                LastGainIndex = 10;
                PrevSignalType = TypeNoVoiceActivity;
                Array.Clear(OutBuf);
                Array.Clear(SLpcQ14Buf);
            }

            FsKhz = fsKhz;
            FrameLength = frameLength;
        }

        return ret;
    }

    /// <summary>silk_decode_frame: decodes (or conceals) one frame into <paramref name="pOut"/>.</summary>
    public int DecodeFrame(OpusRangeDecoder rangeDecoder, Span<short> pOut, out int pN, int lostFlag, int condCoding)
    {
        SilkDecoderControl decCtrl = _control;
        decCtrl.Clear();
        int length = FrameLength;
        decCtrl.LtpScaleQ14 = 0;
        if (lostFlag == FlagDecodeNormal || (lostFlag == FlagDecodeLbrr && LbrrFlags[NFramesDecoded] == 1))
        {
            Span<short> pulses = _pulses;
            DecodeIndices(rangeDecoder, NFramesDecoded, lostFlag != 0, condCoding);
            DecodePulses(rangeDecoder, pulses, Indices.SignalType, Indices.QuantOffsetType, FrameLength);
            DecodeParameters(decCtrl, condCoding);
            DecodeCore(decCtrl, pOut, pulses);

            int mvLen = LtpMemLength - FrameLength;
            OutBuf.AsSpan(FrameLength, mvLen).CopyTo(OutBuf);
            pOut[..FrameLength].CopyTo(OutBuf.AsSpan(mvLen));

            Plc(decCtrl, pOut, false);
            LossCnt = 0;
            PrevSignalType = Indices.SignalType;
            FirstFrameAfterReset = 0;
        }
        else
        {
            Plc(decCtrl, pOut, true);
            int mvLen = LtpMemLength - FrameLength;
            OutBuf.AsSpan(FrameLength, mvLen).CopyTo(OutBuf);
            pOut[..FrameLength].CopyTo(OutBuf.AsSpan(mvLen));
        }

        Cng(decCtrl, pOut, length);
        PlcGlueFrames(pOut, length);
        LagPrev = decCtrl.PitchL[NbSubfr - 1];
        pN = length;
        return 0;
    }

    /// <summary>silk_decode_indices: decodes the side information of a frame.</summary>
    public void DecodeIndices(OpusRangeDecoder rangeDecoder, int frameIndex, bool decodeLbrr, int condCoding)
    {
        SilkSideInfoIndices indices = Indices;
        SilkNlsfCodebook codebook = NlsfCodebook!;
        int ix;
        if (decodeLbrr || VadFlags[frameIndex] != 0)
        {
            ix = rangeDecoder.DecodeIcdf(SilkTables.TypeOffsetVadIcdf, 8) + 2;
        }
        else
        {
            ix = rangeDecoder.DecodeIcdf(SilkTables.TypeOffsetNoVadIcdf, 8);
        }

        indices.SignalType = (sbyte)(ix >> 1);
        indices.QuantOffsetType = (sbyte)(ix & 1);

        if (condCoding == CodeConditionally)
        {
            indices.GainsIndices[0] = (sbyte)rangeDecoder.DecodeIcdf(SilkTables.DeltaGainIcdf, 8);
        }
        else
        {
            indices.GainsIndices[0] = (sbyte)LShift(rangeDecoder.DecodeIcdf(SilkTables.GainIcdf.Slice(indices.SignalType * 8, 8), 8), 3);
            indices.GainsIndices[0] += (sbyte)rangeDecoder.DecodeIcdf(SilkTables.Uniform8Icdf, 8);
        }

        for (int i = 1; i < NbSubfr; i++)
        {
            indices.GainsIndices[i] = (sbyte)rangeDecoder.DecodeIcdf(SilkTables.DeltaGainIcdf, 8);
        }

        indices.NlsfIndices[0] = (sbyte)rangeDecoder.DecodeIcdf(codebook.Cb1Icdf[((indices.SignalType >> 1) * codebook.NVectors)..], 8);
        Span<short> ecIx = stackalloc short[MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[MaxLpcOrder];
        SilkSigProc.NlsfUnpack(ecIx, predQ8, codebook, indices.NlsfIndices[0]);
        ReadOnlySpan<byte> ecIcdf = codebook.EcIcdf;
        for (int i = 0; i < codebook.Order; i++)
        {
            ix = rangeDecoder.DecodeIcdf(ecIcdf[ecIx[i]..], 8);
            if (ix == 0)
            {
                ix -= rangeDecoder.DecodeIcdf(SilkTables.NlsfExtIcdf, 8);
            }
            else if (ix == 2 * NlsfQuantMaxAmplitude)
            {
                ix += rangeDecoder.DecodeIcdf(SilkTables.NlsfExtIcdf, 8);
            }

            indices.NlsfIndices[i + 1] = (sbyte)(ix - NlsfQuantMaxAmplitude);
        }

        if (NbSubfr == MaxNbSubfr)
        {
            indices.NlsfInterpCoefQ2 = (sbyte)rangeDecoder.DecodeIcdf(SilkTables.NlsfInterpolationFactorIcdf, 8);
        }
        else
        {
            indices.NlsfInterpCoefQ2 = 4;
        }

        if (indices.SignalType == TypeVoiced)
        {
            bool decodeAbsoluteLagIndex = true;
            if (condCoding == CodeConditionally && EcPrevSignalType == TypeVoiced)
            {
                int deltaLagIndex = (short)rangeDecoder.DecodeIcdf(SilkTables.PitchDeltaIcdf, 8);
                if (deltaLagIndex > 0)
                {
                    deltaLagIndex -= 9;
                    indices.LagIndex = (short)(EcPrevLagIndex + deltaLagIndex);
                    decodeAbsoluteLagIndex = false;
                }
            }

            if (decodeAbsoluteLagIndex)
            {
                indices.LagIndex = (short)(rangeDecoder.DecodeIcdf(SilkTables.PitchLagIcdf, 8) * (FsKhz >> 1));
                indices.LagIndex += (short)rangeDecoder.DecodeIcdf(PitchLagLowBitsIcdf, 8);
            }

            EcPrevLagIndex = indices.LagIndex;
            indices.ContourIndex = (sbyte)rangeDecoder.DecodeIcdf(PitchContourIcdf, 8);
            indices.PerIndex = (sbyte)rangeDecoder.DecodeIcdf(SilkTables.LtpPerIndexIcdf, 8);
            ReadOnlySpan<byte> ltpGainIcdf = indices.PerIndex switch
            {
                0 => SilkTables.LtpGainIcdf0,
                1 => SilkTables.LtpGainIcdf1,
                _ => SilkTables.LtpGainIcdf2,
            };
            for (int k = 0; k < NbSubfr; k++)
            {
                indices.LtpIndex[k] = (sbyte)rangeDecoder.DecodeIcdf(ltpGainIcdf, 8);
            }

            if (condCoding == CodeIndependently)
            {
                indices.LtpScaleIndex = (sbyte)rangeDecoder.DecodeIcdf(SilkTables.LtpScaleIcdf, 8);
            }
            else
            {
                indices.LtpScaleIndex = 0;
            }
        }

        EcPrevSignalType = indices.SignalType;
        indices.Seed = (sbyte)rangeDecoder.DecodeIcdf(SilkTables.Uniform4Icdf, 8);
    }

    /// <summary>silk_decode_parameters: dequantizes gains, LPC and LTP parameters.</summary>
    private void DecodeParameters(SilkDecoderControl decCtrl, int condCoding)
    {
        SilkSideInfoIndices indices = Indices;
        Span<short> nlsfQ15 = stackalloc short[MaxLpcOrder];
        Span<short> nlsf0Q15 = stackalloc short[MaxLpcOrder];
        SilkSigProc.GainsDequant(decCtrl.GainsQ16, indices.GainsIndices, ref LastGainIndex, condCoding == CodeConditionally, NbSubfr);
        SilkSigProc.NlsfDecode(nlsfQ15, indices.NlsfIndices, NlsfCodebook!);
        SilkSigProc.Nlsf2A(decCtrl.PredCoef(1), nlsfQ15, LpcOrder);
        if (FirstFrameAfterReset == 1)
        {
            indices.NlsfInterpCoefQ2 = 4;
        }

        if (indices.NlsfInterpCoefQ2 < 4)
        {
            for (int i = 0; i < LpcOrder; i++)
            {
                nlsf0Q15[i] = (short)(PrevNlsfQ15[i] + ((indices.NlsfInterpCoefQ2 * (nlsfQ15[i] - PrevNlsfQ15[i])) >> 2));
            }

            SilkSigProc.Nlsf2A(decCtrl.PredCoef(0), nlsf0Q15, LpcOrder);
        }
        else
        {
            decCtrl.PredCoef(1)[..LpcOrder].CopyTo(decCtrl.PredCoef(0));
        }

        nlsfQ15[..LpcOrder].CopyTo(PrevNlsfQ15);
        if (LossCnt != 0)
        {
            SilkSigProc.BwExpander(decCtrl.PredCoef(0), LpcOrder, BweAfterLossQ16);
            SilkSigProc.BwExpander(decCtrl.PredCoef(1), LpcOrder, BweAfterLossQ16);
        }

        if (indices.SignalType == TypeVoiced)
        {
            SilkSigProc.DecodePitch(indices.LagIndex, indices.ContourIndex, decCtrl.PitchL, FsKhz, NbSubfr);
            ReadOnlySpan<sbyte> cbk = indices.PerIndex switch
            {
                0 => SilkTables.LtpGainVq0,
                1 => SilkTables.LtpGainVq1,
                _ => SilkTables.LtpGainVq2,
            };
            for (int k = 0; k < NbSubfr; k++)
            {
                int ix = indices.LtpIndex[k];
                for (int i = 0; i < LtpOrder; i++)
                {
                    decCtrl.LtpCoefQ14[(k * LtpOrder) + i] = (short)LShift(cbk[(ix * LtpOrder) + i], 7);
                }
            }

            decCtrl.LtpScaleQ14 = SilkTables.LtpScalesTableQ14[indices.LtpScaleIndex];
        }
        else
        {
            decCtrl.PitchL.AsSpan(0, NbSubfr).Clear();
            decCtrl.LtpCoefQ14.AsSpan(0, LtpOrder * NbSubfr).Clear();
            indices.PerIndex = 0;
            decCtrl.LtpScaleQ14 = 0;
        }
    }

    /// <summary>silk_decode_pulses: decodes the excitation pulses.</summary>
    public static void DecodePulses(OpusRangeDecoder rangeDecoder, Span<short> pulses, int signalType, int quantOffsetType, int frameLength)
    {
        Span<int> sumPulses = stackalloc int[MaxNbShellBlocks];
        Span<int> nLshifts = stackalloc int[MaxNbShellBlocks];
        int rateLevelIndex = rangeDecoder.DecodeIcdf(SilkTables.RateLevelsIcdf.Slice((signalType >> 1) * (NRateLevels - 1), NRateLevels - 1), 8);
        int iter = frameLength >> Log2ShellCodecFrameLength;
        if (iter * ShellCodecFrameLength < frameLength)
        {
            iter++;
        }

        const int Row = SilkMaxPulses + 2;
        ReadOnlySpan<byte> cdf = SilkTables.PulsesPerBlockIcdf.Slice(rateLevelIndex * Row, Row);
        for (int i = 0; i < iter; i++)
        {
            nLshifts[i] = 0;
            sumPulses[i] = rangeDecoder.DecodeIcdf(cdf, 8);
            while (sumPulses[i] == SilkMaxPulses + 1)
            {
                nLshifts[i]++;
                int offset = ((NRateLevels - 1) * Row) + (nLshifts[i] == 10 ? 1 : 0);
                sumPulses[i] = rangeDecoder.DecodeIcdf(SilkTables.PulsesPerBlockIcdf[offset..], 8);
            }
        }

        for (int i = 0; i < iter; i++)
        {
            Span<short> block = pulses.Slice(Smulbb(i, ShellCodecFrameLength), ShellCodecFrameLength);
            if (sumPulses[i] > 0)
            {
                ShellDecoder(block, rangeDecoder, sumPulses[i]);
            }
            else
            {
                block.Clear();
            }
        }

        for (int i = 0; i < iter; i++)
        {
            if (nLshifts[i] > 0)
            {
                int nLs = nLshifts[i];
                Span<short> block = pulses.Slice(Smulbb(i, ShellCodecFrameLength), ShellCodecFrameLength);
                for (int k = 0; k < ShellCodecFrameLength; k++)
                {
                    int absQ = block[k];
                    for (int j = 0; j < nLs; j++)
                    {
                        absQ = LShift(absQ, 1);
                        absQ += rangeDecoder.DecodeIcdf(SilkTables.LsbIcdf, 8);
                    }

                    block[k] = (short)absQ;
                }

                sumPulses[i] |= nLs << 5;
            }
        }

        DecodeSigns(rangeDecoder, pulses, frameLength, signalType, quantOffsetType, sumPulses);
    }

    /// <summary>decode_split of shell_coder.c.</summary>
    private static void DecodeSplit(out short child1, out short child2, OpusRangeDecoder rangeDecoder, int p, ReadOnlySpan<byte> shellTable)
    {
        if (p > 0)
        {
            child1 = (short)rangeDecoder.DecodeIcdf(shellTable[SilkTables.ShellCodeTableOffsets[p]..], 8);
            child2 = (short)(p - child1);
        }
        else
        {
            child1 = 0;
            child2 = 0;
        }
    }

    /// <summary>silk_shell_decoder: splits a pulse count over a block of 16 samples.</summary>
    private static void ShellDecoder(Span<short> pulses0, OpusRangeDecoder rangeDecoder, int pulses4)
    {
        Span<short> pulses3 = stackalloc short[2];
        Span<short> pulses2 = stackalloc short[4];
        Span<short> pulses1 = stackalloc short[8];
        ReadOnlySpan<byte> table0 = SilkTables.ShellCodeTable0;
        ReadOnlySpan<byte> table1 = SilkTables.ShellCodeTable1;
        ReadOnlySpan<byte> table2 = SilkTables.ShellCodeTable2;
        ReadOnlySpan<byte> table3 = SilkTables.ShellCodeTable3;

        DecodeSplit(out pulses3[0], out pulses3[1], rangeDecoder, pulses4, table3);
        DecodeSplit(out pulses2[0], out pulses2[1], rangeDecoder, pulses3[0], table2);
        DecodeSplit(out pulses1[0], out pulses1[1], rangeDecoder, pulses2[0], table1);
        DecodeSplit(out pulses0[0], out pulses0[1], rangeDecoder, pulses1[0], table0);
        DecodeSplit(out pulses0[2], out pulses0[3], rangeDecoder, pulses1[1], table0);
        DecodeSplit(out pulses1[2], out pulses1[3], rangeDecoder, pulses2[1], table1);
        DecodeSplit(out pulses0[4], out pulses0[5], rangeDecoder, pulses1[2], table0);
        DecodeSplit(out pulses0[6], out pulses0[7], rangeDecoder, pulses1[3], table0);
        DecodeSplit(out pulses2[2], out pulses2[3], rangeDecoder, pulses3[1], table2);
        DecodeSplit(out pulses1[4], out pulses1[5], rangeDecoder, pulses2[2], table1);
        DecodeSplit(out pulses0[8], out pulses0[9], rangeDecoder, pulses1[4], table0);
        DecodeSplit(out pulses0[10], out pulses0[11], rangeDecoder, pulses1[5], table0);
        DecodeSplit(out pulses1[6], out pulses1[7], rangeDecoder, pulses2[3], table1);
        DecodeSplit(out pulses0[12], out pulses0[13], rangeDecoder, pulses1[6], table0);
        DecodeSplit(out pulses0[14], out pulses0[15], rangeDecoder, pulses1[7], table0);
    }

    /// <summary>silk_decode_signs: attaches the signs to the pulses.</summary>
    private static void DecodeSigns(OpusRangeDecoder rangeDecoder, Span<short> pulses, int length, int signalType, int quantOffsetType, ReadOnlySpan<int> sumPulses)
    {
        Span<byte> icdf = stackalloc byte[2];
        icdf[1] = 0;
        int i = Smulbb(7, quantOffsetType + (signalType << 1));
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
                    if (pulses[q + j] > 0)
                    {
                        pulses[q + j] = (short)(pulses[q + j] * ((rangeDecoder.DecodeIcdf(icdf, 8) << 1) - 1));
                    }
                }
            }

            q += ShellCodecFrameLength;
        }
    }
}
