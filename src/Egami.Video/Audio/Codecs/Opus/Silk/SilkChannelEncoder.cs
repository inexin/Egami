// Translated from libopus 1.5.2 silk/structs.h, float/structs_FLP.h, control.h, init_encoder.c, control_codec.c, control_SNR.c,
// control_audio_bandwidth.c, HP_variable_cutoff.c, LP_variable_cutoff.c, biquad_alt.c and encode_indices.c (float build).
using static Egami.Video.Audio.Codecs.Opus.SilkChannelDecoder;
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>silk_EncControlStruct: the settings the Opus layer hands SILK for one call, and what SILK reports back.</summary>
internal sealed class SilkEncControl
{
    public int NChannelsApi;
    public int NChannelsInternal;
    public int ApiSampleRate;
    public int MaxInternalSampleRate;
    public int MinInternalSampleRate;
    public int DesiredInternalSampleRate;
    public int PayloadSizeMs;
    public int BitRate;
    public int PacketLossPercentage;
    public int Complexity;
    public int UseInBandFec;
    public int LbrrCoded;
    public int UseDtx;
    public int UseCbr;
    public int MaxBits;
    public int ToMono;
    public int OpusCanSwitch;
    public int ReducedDependency;
    public int InternalSampleRate;
    public int AllowBandwidthSwitch;
    public int InWbModeWithoutVariableLp;
    public int StereoWidthQ14;
    public int SwitchReady;
    public int SignalType;
    public int Offset;

    public void CopyFrom(SilkEncControl other)
    {
        NChannelsApi = other.NChannelsApi;
        NChannelsInternal = other.NChannelsInternal;
        ApiSampleRate = other.ApiSampleRate;
        MaxInternalSampleRate = other.MaxInternalSampleRate;
        MinInternalSampleRate = other.MinInternalSampleRate;
        DesiredInternalSampleRate = other.DesiredInternalSampleRate;
        PayloadSizeMs = other.PayloadSizeMs;
        BitRate = other.BitRate;
        PacketLossPercentage = other.PacketLossPercentage;
        Complexity = other.Complexity;
        UseInBandFec = other.UseInBandFec;
        LbrrCoded = other.LbrrCoded;
        UseDtx = other.UseDtx;
        UseCbr = other.UseCbr;
        MaxBits = other.MaxBits;
        ToMono = other.ToMono;
        OpusCanSwitch = other.OpusCanSwitch;
        ReducedDependency = other.ReducedDependency;
        InternalSampleRate = other.InternalSampleRate;
        AllowBandwidthSwitch = other.AllowBandwidthSwitch;
        InWbModeWithoutVariableLp = other.InWbModeWithoutVariableLp;
        StereoWidthQ14 = other.StereoWidthQ14;
        SwitchReady = other.SwitchReady;
        SignalType = other.SignalType;
        Offset = other.Offset;
    }
}

/// <summary>silk_encoder_state_FLP: the SILK encoder of one channel (mid or side).</summary>
internal sealed partial class SilkChannelEncoder
{
    public readonly int[] InHpState = new int[2];
    public int VariableHpSmth1Q15;
    public int VariableHpSmth2Q15;
    public readonly SilkLpState SLp = new();
    public readonly SilkVadState SVad = new();
    public readonly SilkNsqState SNsq = new();
    public readonly short[] PrevNlsfqQ15 = new short[MaxLpcOrder];
    public int SpeechActivityQ8;
    public int AllowBandwidthSwitch;
    public sbyte LbrrPrevLastGainIndex;
    public sbyte PrevSignalType;
    public int PrevLag;
    public int PitchLpcWinLength;
    public int MaxPitchLag;
    public int ApiFsHz;
    public int PrevApiFsHz;
    public int MaxInternalFsHz;
    public int MinInternalFsHz;
    public int DesiredInternalFsHz;
    public int FsKhz;
    public int NbSubfr;
    public int FrameLength;
    public int SubfrLength;
    public int LtpMemLength;
    public int LaPitch;
    public int LaShape;
    public int ShapeWinLength;
    public int TargetRateBps;
    public int PacketSizeMs;
    public int PacketLossPerc;
    public int FrameCounter;
    public int Complexity;
    public int NStatesDelayedDecision;
    public int UseInterpolatedNlsfs;
    public int ShapingLpcOrder;
    public int PredictLpcOrder;
    public int PitchEstimationComplexity;
    public int PitchEstimationLpcOrder;
    public int PitchEstimationThresholdQ16;
    public int SumLogGainQ7;
    public int NlsfMsvqSurvivors;
    public int FirstFrameAfterReset;
    public int ControlledSinceLastPayload;
    public int WarpingQ16;
    public int UseCbr;
    public int PrefillFlag;
    public SilkNlsfCodebook? NlsfCodebook;
    public readonly int[] InputQualityBandsQ15 = new int[VadNBands];
    public int InputTiltQ15;
    public int SnrDbQ7;
    public readonly sbyte[] VadFlags = new sbyte[MaxFramesPerPacket];
    public sbyte LbrrFlag;
    public readonly int[] LbrrFlags = new int[MaxFramesPerPacket];
    public readonly SilkSideInfoIndices Indices = new();
    public readonly sbyte[] Pulses = new sbyte[MaxFrameLength];
    public readonly short[] InputBuf = new short[MaxFrameLength + 2];
    public int InputBufIx;
    public int NFramesPerPacket;
    public int NFramesEncoded;
    public int NChannelsApi;
    public int NChannelsInternal;
    public int ChannelNb;
    public int FramesSinceOnset;
    public int EcPrevSignalType;
    public short EcPrevLagIndex;
    public readonly SilkResampler ResamplerState = new();
    public int UseDtx;
    public int InDtx;
    public int NoSpeechCounter;
    public int UseInBandFec;
    public int LbrrEnabled;
    public int LbrrGainIncreases;
    public readonly SilkSideInfoIndices[] IndicesLbrr = [new(), new(), new()];
    public readonly sbyte[] PulsesLbrr = new sbyte[MaxFramesPerPacket * MaxFrameLength];

    // silk_shape_state_FLP.
    public sbyte ShapeLastGainIndex;
    public float HarmShapeGainSmth;
    public float TiltSmth;

    // The FLP state: the buffer for pitch and noise shape analysis, and the pitch correlation.
    public readonly float[] XBuf = new float[(2 * MaxFrameLength) + LaShapeMax];
    public float LtpCorr;

    // pitch_lag_low_bits_iCDF (the internal rate it was picked for) and pitch_contour_iCDF, as in SilkChannelDecoder.
    private int _pitchLagLowBitsTable;
    private int _pitchContourTable;

    private readonly SilkResampler _tempResampler = new();

    public SilkChannelEncoder()
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

    /// <summary>silk_init_encoder: clears the whole state and sets up the high-pass smoother and the VAD.</summary>
    public void Init()
    {
        Clear();
        VariableHpSmth1Q15 = LShift(SilkSigProc.Lin2Log(VariableHpMinCutoffHz << 16) - (16 << 7), 8);
        VariableHpSmth2Q15 = VariableHpSmth1Q15;
        FirstFrameAfterReset = 1;
        SilkVad.Init(SVad);
    }

    // silk_memset( psEnc, 0, sizeof( silk_encoder_state_Fxx ) ).
    private void Clear()
    {
        Array.Clear(InHpState);
        VariableHpSmth1Q15 = 0;
        VariableHpSmth2Q15 = 0;
        Array.Clear(SLp.InLpState);
        SLp.TransitionFrameNo = 0;
        SLp.Mode = 0;
        SLp.SavedFsKhz = 0;
        Array.Clear(SVad.AnaState);
        Array.Clear(SVad.AnaState1);
        Array.Clear(SVad.AnaState2);
        Array.Clear(SVad.XnrgSubfr);
        Array.Clear(SVad.NrgRatioSmthQ8);
        SVad.HpState = 0;
        Array.Clear(SVad.Nl);
        Array.Clear(SVad.InvNl);
        Array.Clear(SVad.NoiseLevelBias);
        SVad.Counter = 0;
        SNsq.Clear();
        Array.Clear(PrevNlsfqQ15);
        SpeechActivityQ8 = 0;
        AllowBandwidthSwitch = 0;
        LbrrPrevLastGainIndex = 0;
        PrevSignalType = 0;
        PrevLag = 0;
        PitchLpcWinLength = 0;
        MaxPitchLag = 0;
        ApiFsHz = 0;
        PrevApiFsHz = 0;
        MaxInternalFsHz = 0;
        MinInternalFsHz = 0;
        DesiredInternalFsHz = 0;
        FsKhz = 0;
        NbSubfr = 0;
        FrameLength = 0;
        SubfrLength = 0;
        LtpMemLength = 0;
        LaPitch = 0;
        LaShape = 0;
        ShapeWinLength = 0;
        TargetRateBps = 0;
        PacketSizeMs = 0;
        PacketLossPerc = 0;
        FrameCounter = 0;
        Complexity = 0;
        NStatesDelayedDecision = 0;
        UseInterpolatedNlsfs = 0;
        ShapingLpcOrder = 0;
        PredictLpcOrder = 0;
        PitchEstimationComplexity = 0;
        PitchEstimationLpcOrder = 0;
        PitchEstimationThresholdQ16 = 0;
        SumLogGainQ7 = 0;
        NlsfMsvqSurvivors = 0;
        FirstFrameAfterReset = 0;
        ControlledSinceLastPayload = 0;
        WarpingQ16 = 0;
        UseCbr = 0;
        PrefillFlag = 0;
        NlsfCodebook = null;
        _pitchLagLowBitsTable = 0;
        _pitchContourTable = 0;
        Array.Clear(InputQualityBandsQ15);
        InputTiltQ15 = 0;
        SnrDbQ7 = 0;
        Array.Clear(VadFlags);
        LbrrFlag = 0;
        Array.Clear(LbrrFlags);
        Indices.Clear();
        Array.Clear(Pulses);
        Array.Clear(InputBuf);
        InputBufIx = 0;
        NFramesPerPacket = 0;
        NFramesEncoded = 0;
        NChannelsApi = 0;
        NChannelsInternal = 0;
        ChannelNb = 0;
        FramesSinceOnset = 0;
        EcPrevSignalType = 0;
        EcPrevLagIndex = 0;
        ResamplerState.Clear();
        UseDtx = 0;
        InDtx = 0;
        NoSpeechCounter = 0;
        UseInBandFec = 0;
        LbrrEnabled = 0;
        LbrrGainIncreases = 0;
        foreach (SilkSideInfoIndices indices in IndicesLbrr)
        {
            indices.Clear();
        }

        Array.Clear(PulsesLbrr);
        ShapeLastGainIndex = 0;
        HarmShapeGainSmth = 0;
        TiltSmth = 0;
        Array.Clear(XBuf);
        LtpCorr = 0;
    }

    /// <summary>Copies the variable low-pass filter state (the prefill path of silk_Encode keeps it across a reset).</summary>
    public void CopyLpStateFrom(SilkLpState lp)
    {
        lp.InLpState.CopyTo(SLp.InLpState, 0);
        SLp.TransitionFrameNo = lp.TransitionFrameNo;
        SLp.Mode = lp.Mode;
        SLp.SavedFsKhz = lp.SavedFsKhz;
    }

    /// <summary>silk_control_encoder: applies the control settings; returns 0 or a SILK error code.</summary>
    public int Control(SilkEncControl encControl, int allowBwSwitch, int channelNb, int forceFsKhz)
    {
        int ret = 0;
        UseDtx = encControl.UseDtx;
        UseCbr = encControl.UseCbr;
        ApiFsHz = encControl.ApiSampleRate;
        MaxInternalFsHz = encControl.MaxInternalSampleRate;
        MinInternalFsHz = encControl.MinInternalSampleRate;
        DesiredInternalFsHz = encControl.DesiredInternalSampleRate;
        UseInBandFec = encControl.UseInBandFec;
        NChannelsApi = encControl.NChannelsApi;
        NChannelsInternal = encControl.NChannelsInternal;
        AllowBandwidthSwitch = allowBwSwitch;
        ChannelNb = channelNb;

        if (ControlledSinceLastPayload != 0 && PrefillFlag == 0)
        {
            if (ApiFsHz != PrevApiFsHz && FsKhz > 0)
            {
                ret += SetupResamplers(FsKhz);
            }

            return ret;
        }

        int fsKhz = ControlAudioBandwidth(encControl);
        if (forceFsKhz != 0)
        {
            fsKhz = forceFsKhz;
        }

        ret += SetupResamplers(fsKhz);
        ret += SetupFs(fsKhz, encControl.PayloadSizeMs);
        SetupComplexity(encControl.Complexity);
        PacketLossPerc = encControl.PacketLossPercentage;
        SetupLbrr(encControl);
        ControlledSinceLastPayload = 1;
        return ret;
    }

    private int SetupResamplers(int fsKhz)
    {
        int ret = 0;
        if (FsKhz != fsKhz || PrevApiFsHz != ApiFsHz)
        {
            if (FsKhz == 0)
            {
                ret += ResamplerState.Init(ApiFsHz, fsKhz * 1000, true);
            }
            else
            {
                int bufLengthMs = LShift(NbSubfr * 5, 1) + LaShapeMs;
                int oldBufSamples = bufLengthMs * FsKhz;
                int newBufSamples = bufLengthMs * fsKhz;
                Span<short> xBufFix = stackalloc short[((2 * MaxFrameLength) / SilkChannelDecoder.MaxFsKhz + LaShapeMs) * SilkChannelDecoder.MaxFsKhz];
                SilkFloat.Float2ShortArray(xBufFix, XBuf, oldBufSamples);
                ret += _tempResampler.Init(Smulbb(FsKhz, 1000), ApiFsHz);
                int apiBufSamples = bufLengthMs * (ApiFsHz / 1000);
                Span<short> xBufApiFsHz = stackalloc short[((2 * MaxFrameLength) / SilkChannelDecoder.MaxFsKhz + LaShapeMs) * MaxApiFsKhz];
                ret += _tempResampler.Process(xBufApiFsHz, xBufFix, oldBufSamples);
                ret += ResamplerState.Init(ApiFsHz, Smulbb(fsKhz, 1000), true);
                ret += ResamplerState.Process(xBufFix, xBufApiFsHz, apiBufSamples);
                SilkFloat.Short2FloatArray(XBuf, xBufFix, newBufSamples);
            }
        }

        PrevApiFsHz = ApiFsHz;
        return ret;
    }

    private int SetupFs(int fsKhz, int packetSizeMs)
    {
        int ret = 0;
        if (packetSizeMs != PacketSizeMs)
        {
            if (packetSizeMs != 10 && packetSizeMs != 20 && packetSizeMs != 40 && packetSizeMs != 60)
            {
                ret = -103;
            }

            if (packetSizeMs <= 10)
            {
                NFramesPerPacket = 1;
                NbSubfr = packetSizeMs == 10 ? 2 : 1;
                FrameLength = Smulbb(packetSizeMs, fsKhz);
                PitchLpcWinLength = Smulbb(FindPitchLpcWinMs2Sf, fsKhz);

                // libopus picks the contour table from the rate before this call (fs_kHz has not been updated yet).
                _pitchContourTable = FsKhz == 8 ? 1 : 3;
            }
            else
            {
                NFramesPerPacket = packetSizeMs / (SubFrameLengthMs * MaxNbSubfr);
                NbSubfr = MaxNbSubfr;
                FrameLength = Smulbb(20, fsKhz);
                PitchLpcWinLength = Smulbb(FindPitchLpcWinMs, fsKhz);
                _pitchContourTable = FsKhz == 8 ? 0 : 2;
            }

            PacketSizeMs = packetSizeMs;
            TargetRateBps = 0;
        }

        if (FsKhz != fsKhz)
        {
            ShapeLastGainIndex = 0;
            HarmShapeGainSmth = 0;
            TiltSmth = 0;
            SNsq.Clear();
            Array.Clear(PrevNlsfqQ15);
            Array.Clear(SLp.InLpState);
            InputBufIx = 0;
            NFramesEncoded = 0;
            TargetRateBps = 0;
            PrevLag = 100;
            FirstFrameAfterReset = 1;
            ShapeLastGainIndex = 10;
            SNsq.LagPrev = 100;
            SNsq.PrevGainQ16 = 65536;
            PrevSignalType = TypeNoVoiceActivity;
            FsKhz = fsKhz;
            if (FsKhz == 8)
            {
                _pitchContourTable = NbSubfr == MaxNbSubfr ? 0 : 1;
            }
            else
            {
                _pitchContourTable = NbSubfr == MaxNbSubfr ? 2 : 3;
            }

            if (FsKhz == 8 || FsKhz == 12)
            {
                PredictLpcOrder = MinLpcOrder;
                NlsfCodebook = SilkNlsfCodebook.NarrowMedium;
            }
            else
            {
                PredictLpcOrder = MaxLpcOrder;
                NlsfCodebook = SilkNlsfCodebook.Wide;
            }

            SubfrLength = SubFrameLengthMs * fsKhz;
            FrameLength = Smulbb(SubfrLength, NbSubfr);
            LtpMemLength = Smulbb(LtpMemLengthMs, fsKhz);
            LaPitch = Smulbb(LaPitchMs, fsKhz);
            MaxPitchLag = Smulbb(18, fsKhz);
            PitchLpcWinLength = NbSubfr == MaxNbSubfr ? Smulbb(FindPitchLpcWinMs, fsKhz) : Smulbb(FindPitchLpcWinMs2Sf, fsKhz);
            _pitchLagLowBitsTable = FsKhz;
        }

        return ret;
    }

    private void SetupComplexity(int complexity)
    {
        if (complexity < 1)
        {
            SetComplexityParams(SilkPitchAnalysis.PeMinComplex, 0.8, 6, 12, 3, 1, 0, 2, false);
        }
        else if (complexity < 2)
        {
            SetComplexityParams(SilkPitchAnalysis.PeMidComplex, 0.76, 8, 14, 5, 1, 0, 3, false);
        }
        else if (complexity < 3)
        {
            SetComplexityParams(SilkPitchAnalysis.PeMinComplex, 0.8, 6, 12, 3, 2, 0, 2, false);
        }
        else if (complexity < 4)
        {
            SetComplexityParams(SilkPitchAnalysis.PeMidComplex, 0.76, 8, 14, 5, 2, 0, 4, false);
        }
        else if (complexity < 6)
        {
            SetComplexityParams(SilkPitchAnalysis.PeMidComplex, 0.74, 10, 16, 5, 2, 1, 6, true);
        }
        else if (complexity < 8)
        {
            SetComplexityParams(SilkPitchAnalysis.PeMidComplex, 0.72, 12, 20, 5, 3, 1, 8, true);
        }
        else
        {
            SetComplexityParams(SilkPitchAnalysis.PeMaxComplex, 0.7, 16, 24, 5, MaxDelDecStates, 1, 16, true);
        }

        PitchEstimationLpcOrder = Math.Min(PitchEstimationLpcOrder, PredictLpcOrder);
        ShapeWinLength = (SubFrameLengthMs * FsKhz) + (2 * LaShape);
        Complexity = complexity;
    }

    private void SetComplexityParams(int pitchComplexity, double pitchThreshold, int pitchLpcOrder, int shapingLpcOrder, int laShapeMs, int nStates,
        int useInterpolatedNlsfs, int survivors, bool warping)
    {
        PitchEstimationComplexity = pitchComplexity;
        PitchEstimationThresholdQ16 = SilkEncoderQuant.FixConst(pitchThreshold, 16);
        PitchEstimationLpcOrder = pitchLpcOrder;
        ShapingLpcOrder = shapingLpcOrder;
        LaShape = laShapeMs * FsKhz;
        NStatesDelayedDecision = nStates;
        UseInterpolatedNlsfs = useInterpolatedNlsfs;
        NlsfMsvqSurvivors = survivors;
        WarpingQ16 = warping ? FsKhz * SilkEncoderQuant.FixConst(WarpingMultiplier, 16) : 0;
    }

    private void SetupLbrr(SilkEncControl encControl)
    {
        int lbrrInPreviousPacket = LbrrEnabled;
        LbrrEnabled = encControl.LbrrCoded;
        if (LbrrEnabled != 0)
        {
            LbrrGainIncreases = lbrrInPreviousPacket == 0
                ? 7
                : Math.Max(7 - Smulwb(PacketLossPerc, SilkEncoderQuant.FixConst(0.2, 16)), 3);
        }
    }

    /// <summary>silk_control_audio_bandwidth: the internal rate (kHz) for the next packet, with the variable low-pass transition.</summary>
    private int ControlAudioBandwidth(SilkEncControl encControl)
    {
        int origKhz = FsKhz;
        if (origKhz == 0)
        {
            origKhz = SLp.SavedFsKhz;
        }

        int fsKhz = origKhz;
        int fsHz = Smulbb(fsKhz, 1000);
        if (fsHz == 0)
        {
            fsHz = Math.Min(DesiredInternalFsHz, ApiFsHz);
            fsKhz = fsHz / 1000;
        }
        else if (fsHz > ApiFsHz || fsHz > MaxInternalFsHz || fsHz < MinInternalFsHz)
        {
            fsHz = ApiFsHz;
            fsHz = Math.Min(fsHz, MaxInternalFsHz);
            fsHz = Math.Max(fsHz, MinInternalFsHz);
            fsKhz = fsHz / 1000;
        }
        else
        {
            if (SLp.TransitionFrameNo >= TransitionFrames)
            {
                SLp.Mode = 0;
            }

            if (AllowBandwidthSwitch != 0 || encControl.OpusCanSwitch != 0)
            {
                if (Smulbb(origKhz, 1000) > DesiredInternalFsHz)
                {
                    if (SLp.Mode == 0)
                    {
                        SLp.TransitionFrameNo = TransitionFrames;
                        Array.Clear(SLp.InLpState);
                    }

                    if (encControl.OpusCanSwitch != 0)
                    {
                        SLp.Mode = 0;
                        fsKhz = origKhz == 16 ? 12 : 8;
                    }
                    else
                    {
                        if (SLp.TransitionFrameNo <= 0)
                        {
                            encControl.SwitchReady = 1;
                            encControl.MaxBits -= encControl.MaxBits * 5 / (encControl.PayloadSizeMs + 5);
                        }
                        else
                        {
                            SLp.Mode = -2;
                        }
                    }
                }
                else if (Smulbb(origKhz, 1000) < DesiredInternalFsHz)
                {
                    if (encControl.OpusCanSwitch != 0)
                    {
                        fsKhz = origKhz == 8 ? 12 : 16;
                        SLp.TransitionFrameNo = 0;
                        Array.Clear(SLp.InLpState);
                        SLp.Mode = 1;
                    }
                    else
                    {
                        if (SLp.Mode == 0)
                        {
                            encControl.SwitchReady = 1;
                            encControl.MaxBits -= encControl.MaxBits * 5 / (encControl.PayloadSizeMs + 5);
                        }
                        else
                        {
                            SLp.Mode = 1;
                        }
                    }
                }
                else if (SLp.Mode < 0)
                {
                    SLp.Mode = 1;
                }
            }
        }

        return fsKhz;
    }

    /// <summary>silk_control_SNR: the target SNR for a bitrate.</summary>
    public void ControlSnr(int targetRateBps)
    {
        TargetRateBps = targetRateBps;
        if (NbSubfr == 2)
        {
            targetRateBps -= 2000 + (FsKhz / 16);
        }

        ReadOnlySpan<byte> snrTable = FsKhz == 8 ? SilkTables.TargetRateNb21 : FsKhz == 12 ? SilkTables.TargetRateMb21 : SilkTables.TargetRateWb21;
        int id = (targetRateBps + 200) / 400;
        id = Math.Min(id - 10, snrTable.Length - 1);
        SnrDbQ7 = id <= 0 ? 0 : snrTable[id] * 21;
    }

    /// <summary>silk_HP_variable_cutoff: adapts the high-pass smoother to the pitch of the previous frame (mid channel only).</summary>
    public void HpVariableCutoff()
    {
        if (PrevSignalType == TypeVoiced)
        {
            int pitchFreqHzQ16 = LShift(Mul(FsKhz, 1000), 16) / PrevLag;
            int pitchFreqLogQ7 = SilkSigProc.Lin2Log(pitchFreqHzQ16) - (16 << 7);
            int qualityQ15 = InputQualityBandsQ15[0];
            pitchFreqLogQ7 = Smlawb(pitchFreqLogQ7, Smulwb(LShift(-qualityQ15, 2), qualityQ15),
                pitchFreqLogQ7 - (SilkSigProc.Lin2Log(VariableHpMinCutoffHz << 16) - (16 << 7)));
            int deltaFreqQ7 = pitchFreqLogQ7 - (VariableHpSmth1Q15 >> 8);
            if (deltaFreqQ7 < 0)
            {
                deltaFreqQ7 = Mul(deltaFreqQ7, 3);
            }

            int maxDelta = SilkEncoderQuant.FixConst(VariableHpMaxDeltaFreq, 7);
            deltaFreqQ7 = Limit(deltaFreqQ7, -maxDelta, maxDelta);
            VariableHpSmth1Q15 = Smlawb(VariableHpSmth1Q15, Smulbb(SpeechActivityQ8, deltaFreqQ7), SilkEncoderQuant.FixConst(VariableHpSmthCoef1, 16));
            VariableHpSmth1Q15 = Limit(VariableHpSmth1Q15, LShift(SilkSigProc.Lin2Log(VariableHpMinCutoffHz), 8),
                LShift(SilkSigProc.Lin2Log(VariableHpMaxCutoffHz), 8));
        }
    }

    /// <summary>silk_LP_variable_cutoff: the low-pass filter that smooths a switch of internal bandwidth.</summary>
    private void LpVariableCutoff(Span<short> frame, int frameLength)
    {
        if (SLp.Mode != 0)
        {
            Span<int> bQ28 = stackalloc int[TransitionNb];
            Span<int> aQ28 = stackalloc int[TransitionNa];

            // TRANSITION_INT_STEPS is 64, so libopus takes the shift form of the division.
            int facQ16 = LShift(TransitionFrames - SLp.TransitionFrameNo, 16 - 6);
            int ind = facQ16 >> 16;
            facQ16 -= LShift(ind, 16);
            LpInterpolateFilterTaps(bQ28, aQ28, ind, facQ16);
            SLp.TransitionFrameNo = Limit(SLp.TransitionFrameNo + SLp.Mode, 0, TransitionFrames);
            BiquadAltStride1(frame, bQ28, aQ28, SLp.InLpState, frame, frameLength);
        }
    }

    private static void LpInterpolateFilterTaps(Span<int> bQ28, Span<int> aQ28, int ind, int facQ16)
    {
        ReadOnlySpan<int> tb = SilkTables.TransitionLpBQ28;
        ReadOnlySpan<int> ta = SilkTables.TransitionLpAQ28;
        if (ind < TransitionIntNum - 1)
        {
            if (facQ16 > 0)
            {
                if (facQ16 < 32768)
                {
                    for (int nb = 0; nb < TransitionNb; nb++)
                    {
                        bQ28[nb] = Smlawb(tb[(ind * TransitionNb) + nb], tb[((ind + 1) * TransitionNb) + nb] - tb[(ind * TransitionNb) + nb], facQ16);
                    }

                    for (int na = 0; na < TransitionNa; na++)
                    {
                        aQ28[na] = Smlawb(ta[(ind * TransitionNa) + na], ta[((ind + 1) * TransitionNa) + na] - ta[(ind * TransitionNa) + na], facQ16);
                    }
                }
                else
                {
                    for (int nb = 0; nb < TransitionNb; nb++)
                    {
                        bQ28[nb] = Smlawb(tb[((ind + 1) * TransitionNb) + nb], tb[((ind + 1) * TransitionNb) + nb] - tb[(ind * TransitionNb) + nb],
                            facQ16 - (1 << 16));
                    }

                    for (int na = 0; na < TransitionNa; na++)
                    {
                        aQ28[na] = Smlawb(ta[((ind + 1) * TransitionNa) + na], ta[((ind + 1) * TransitionNa) + na] - ta[(ind * TransitionNa) + na],
                            facQ16 - (1 << 16));
                    }
                }
            }
            else
            {
                tb.Slice(ind * TransitionNb, TransitionNb).CopyTo(bQ28);
                ta.Slice(ind * TransitionNa, TransitionNa).CopyTo(aQ28);
            }
        }
        else
        {
            tb.Slice((TransitionIntNum - 1) * TransitionNb, TransitionNb).CopyTo(bQ28);
            ta.Slice((TransitionIntNum - 1) * TransitionNa, TransitionNa).CopyTo(aQ28);
        }
    }

    /// <summary>silk_biquad_alt_stride1: a second-order ARMA filter with the A coefficients split in two halves for precision.</summary>
    private static void BiquadAltStride1(ReadOnlySpan<short> input, ReadOnlySpan<int> bQ28, ReadOnlySpan<int> aQ28, Span<int> s, Span<short> output, int len)
    {
        int a0LQ28 = -aQ28[0] & 0x00003FFF;
        int a0UQ28 = -aQ28[0] >> 14;
        int a1LQ28 = -aQ28[1] & 0x00003FFF;
        int a1UQ28 = -aQ28[1] >> 14;
        for (int k = 0; k < len; k++)
        {
            int inval = input[k];
            int out32Q14 = LShift(Smlawb(s[0], bQ28[0], inval), 2);
            s[0] = s[1] + RShiftRound(Smulwb(out32Q14, a0LQ28), 14);
            s[0] = Smlawb(s[0], out32Q14, a0UQ28);
            s[0] = Smlawb(s[0], bQ28[1], inval);
            s[1] = RShiftRound(Smulwb(out32Q14, a1LQ28), 14);
            s[1] = Smlawb(s[1], out32Q14, a1UQ28);
            s[1] = Smlawb(s[1], bQ28[2], inval);
            output[k] = (short)Sat16((out32Q14 + (1 << 14) - 1) >> 14);
        }
    }

    /// <summary>silk_encode_indices: writes the side information of the frame (or of LBRR frame <paramref name="frameIndex"/>).</summary>
    public void EncodeIndices(OpusRangeEncoder enc, int frameIndex, bool encodeLbrr, int condCoding)
    {
        SilkSideInfoIndices indices = encodeLbrr ? IndicesLbrr[frameIndex] : Indices;
        SilkNlsfCodebook cb = NlsfCodebook!;
        int typeOffset = (2 * indices.SignalType) + indices.QuantOffsetType;
        if (encodeLbrr || typeOffset >= 2)
        {
            enc.EncodeIcdf(typeOffset - 2, SilkTables.TypeOffsetVadIcdf, 8);
        }
        else
        {
            enc.EncodeIcdf(typeOffset, SilkTables.TypeOffsetNoVadIcdf, 8);
        }

        if (condCoding == CodeConditionally)
        {
            enc.EncodeIcdf(indices.GainsIndices[0], SilkTables.DeltaGainIcdf, 8);
        }
        else
        {
            enc.EncodeIcdf(indices.GainsIndices[0] >> 3, SilkTables.GainIcdf.Slice(indices.SignalType * 8, 8), 8);
            enc.EncodeIcdf(indices.GainsIndices[0] & 7, SilkTables.Uniform8Icdf, 8);
        }

        for (int i = 1; i < NbSubfr; i++)
        {
            enc.EncodeIcdf(indices.GainsIndices[i], SilkTables.DeltaGainIcdf, 8);
        }

        enc.EncodeIcdf(indices.NlsfIndices[0], cb.Cb1Icdf[((indices.SignalType >> 1) * cb.NVectors)..], 8);
        Span<short> ecIx = stackalloc short[MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[MaxLpcOrder];
        SilkSigProc.NlsfUnpack(ecIx, predQ8, cb, indices.NlsfIndices[0]);
        ReadOnlySpan<byte> ecIcdf = cb.EcIcdf;
        for (int i = 0; i < cb.Order; i++)
        {
            int ix = indices.NlsfIndices[i + 1];
            if (ix >= NlsfQuantMaxAmplitude)
            {
                enc.EncodeIcdf(2 * NlsfQuantMaxAmplitude, ecIcdf[ecIx[i]..], 8);
                enc.EncodeIcdf(ix - NlsfQuantMaxAmplitude, SilkTables.NlsfExtIcdf, 8);
            }
            else if (ix <= -NlsfQuantMaxAmplitude)
            {
                enc.EncodeIcdf(0, ecIcdf[ecIx[i]..], 8);
                enc.EncodeIcdf(-ix - NlsfQuantMaxAmplitude, SilkTables.NlsfExtIcdf, 8);
            }
            else
            {
                enc.EncodeIcdf(ix + NlsfQuantMaxAmplitude, ecIcdf[ecIx[i]..], 8);
            }
        }

        if (NbSubfr == MaxNbSubfr)
        {
            enc.EncodeIcdf(indices.NlsfInterpCoefQ2, SilkTables.NlsfInterpolationFactorIcdf, 8);
        }

        if (indices.SignalType == TypeVoiced)
        {
            bool encodeAbsoluteLagIndex = true;
            if (condCoding == CodeConditionally && EcPrevSignalType == TypeVoiced)
            {
                int deltaLagIndex = indices.LagIndex - EcPrevLagIndex;
                if (deltaLagIndex < -8 || deltaLagIndex > 11)
                {
                    deltaLagIndex = 0;
                }
                else
                {
                    deltaLagIndex += 9;
                    encodeAbsoluteLagIndex = false;
                }

                enc.EncodeIcdf(deltaLagIndex, SilkTables.PitchDeltaIcdf, 8);
            }

            if (encodeAbsoluteLagIndex)
            {
                int pitchHighBits = indices.LagIndex / (FsKhz >> 1);
                int pitchLowBits = indices.LagIndex - Smulbb(pitchHighBits, FsKhz >> 1);
                enc.EncodeIcdf(pitchHighBits, SilkTables.PitchLagIcdf, 8);
                enc.EncodeIcdf(pitchLowBits, PitchLagLowBitsIcdf, 8);
            }

            EcPrevLagIndex = indices.LagIndex;
            enc.EncodeIcdf(indices.ContourIndex, PitchContourIcdf, 8);
            enc.EncodeIcdf(indices.PerIndex, SilkTables.LtpPerIndexIcdf, 8);
            ReadOnlySpan<byte> ltpGainIcdf = indices.PerIndex switch
            {
                0 => SilkTables.LtpGainIcdf0,
                1 => SilkTables.LtpGainIcdf1,
                _ => SilkTables.LtpGainIcdf2,
            };
            for (int k = 0; k < NbSubfr; k++)
            {
                enc.EncodeIcdf(indices.LtpIndex[k], ltpGainIcdf, 8);
            }

            if (condCoding == CodeIndependently)
            {
                enc.EncodeIcdf(indices.LtpScaleIndex, SilkTables.LtpScaleIcdf, 8);
            }
        }

        EcPrevSignalType = indices.SignalType;
        enc.EncodeIcdf(indices.Seed, SilkTables.Uniform4Icdf, 8);
    }
}
