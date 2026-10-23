// Translated from libopus 1.5.2 silk/define.h, tuning_parameters.h, structs.h and float/structs_FLP.h (the encoder's parts).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>The constants of SILK's encoder (define.h and tuning_parameters.h).</summary>
internal static class SilkEncoderConstants
{
    public const int EncoderNumChannels = 2;
    public const int MinTargetRateBps = 5000;
    public const int MaxTargetRateBps = 80000;
    public const int LbrrNbMinRateBps = 12000;
    public const int LbrrMbMinRateBps = 14000;
    public const int LbrrWbMinRateBps = 16000;
    public const int NbSpeechFramesBeforeDtx = 10;
    public const int MaxConsecutiveDtx = 20;
    public const float DtxActivityThreshold = 0.1f;
    public const int VadNoDecision = -1;
    public const int VadNoActivity = 0;
    public const int VadActivity = 1;
    public const int MaxApiFsKhz = 48;
    public const int StereoQuantTabSize = 16;
    public const int StereoQuantSubSteps = 5;
    public const int StereoInterpLenMs = 8;
    public const double StereoRatioSmoothCoef = 0.01;
    public const int LaPitchMs = 2;
    public const int LaPitchMax = LaPitchMs * SilkChannelDecoder.MaxFsKhz;
    public const int MaxFindPitchLpcOrder = 16;
    public const int FindPitchLpcWinMs = 20 + (LaPitchMs << 1);
    public const int FindPitchLpcWinMs2Sf = 10 + (LaPitchMs << 1);
    public const int FindPitchLpcWinMax = FindPitchLpcWinMs * SilkChannelDecoder.MaxFsKhz;
    public const int LaShapeMs = 5;
    public const int LaShapeMax = LaShapeMs * SilkChannelDecoder.MaxFsKhz;
    public const int ShapeLpcWinMax = 15 * SilkChannelDecoder.MaxFsKhz;
    public const int MinQGainDb = 2;
    public const int MaxQGainDb = 88;
    public const int NLevelsQGain = 64;
    public const int MaxDeltaGainQuant = 36;
    public const int MinDeltaGainQuant = -4;
    public const int OffsetVlQ10 = 32;
    public const int OffsetVhQ10 = 100;
    public const int OffsetUvlQ10 = 100;
    public const int OffsetUvhQ10 = 240;
    public const int QuantLevelAdjustQ10 = 80;
    public const float MaxPredictionPowerGain = 1e4f;
    public const float MaxPredictionPowerGainAfterReset = 1e2f;
    public const int NbLtpCbks = 3;
    public const int MaxShapeLpcOrder = 24;
    public const int HarmShapeFirTaps = 3;
    public const int MaxDelDecStates = 4;
    public const int LtpBufLength = 512;
    public const int LtpMask = LtpBufLength - 1;
    public const int DecisionDelay = 40;
    public const int NsqLpcBufLength = SilkChannelDecoder.MaxLpcOrder;
    public const int VadNBands = 4;
    public const int VadInternalSubframesLog2 = 2;
    public const int VadInternalSubframes = 1 << VadInternalSubframesLog2;
    public const int VadNoiseLevelSmoothCoefQ16 = 1024;
    public const int VadNoiseLevelsBias = 50;
    public const int VadNegativeOffsetQ5 = 128;
    public const int VadSnrFactorQ16 = 45000;
    public const int VadSnrSmoothCoefQ18 = 4096;
    public const int NlsfWQ = 2;
    public const int NlsfVqMaxVectors = 32;
    public const int NlsfQuantMaxAmplitude = 4;
    public const int NlsfQuantMaxAmplitudeExt = 10;
    public const double NlsfQuantLevelAdj = 0.1;
    public const int NlsfQuantDelDecStatesLog2 = 2;
    public const int NlsfQuantDelDecStates = 1 << NlsfQuantDelDecStatesLog2;
    public const int TransitionTimeMs = 5120;
    public const int TransitionNb = 3;
    public const int TransitionNa = 2;
    public const int TransitionIntNum = 5;
    public const int TransitionFrames = TransitionTimeMs / 20;
    public const int TransitionIntSteps = TransitionFrames / (TransitionIntNum - 1);

    public const int BitreservoirDecayTimeMs = 500;
    public const float FindPitchWhiteNoiseFraction = 1e-3f;
    public const float FindPitchBandwidthExpansion = 0.99f;
    public const float FindLpcCondFac = 1e-5f;
    public const float MaxSumLogGainDb = 250.0f;
    public const float LtpCorrInvMax = 0.03f;
    public const float VariableHpSmthCoef1 = 0.1f;
    public const float VariableHpSmthCoef2 = 0.015f;
    public const float VariableHpMaxDeltaFreq = 0.4f;
    public const int VariableHpMinCutoffHz = 60;
    public const int VariableHpMaxCutoffHz = 100;
    public const float SpeechActivityDtxThres = 0.05f;
    public const float LbrrSpeechActivityThres = 0.3f;
    public const float BgSnrDecrDb = 2.0f;
    public const float HarmSnrIncrDb = 2.0f;
    public const float SparseSnrIncrDb = 2.0f;
    public const float EnergyVariationThresholdQntOffset = 0.6f;
    public const float WarpingMultiplier = 0.015f;
    public const float ShapeWhiteNoiseFraction = 3e-5f;
    public const float BandwidthExpansion = 0.94f;
    public const float HarmonicShaping = 0.3f;
    public const float HighRateOrLowQualityHarmonicShaping = 0.2f;
    public const float HpNoiseCoef = 0.25f;
    public const float HarmHpNoiseCoef = 0.35f;
    public const float InputTilt = 0.05f;
    public const float HighRateInputTilt = 0.1f;
    public const float LowFreqShaping = 4.0f;
    public const float LowQualityLowFreqShapingDecr = 0.5f;
    public const float SubfrSmthCoef = 0.4f;
    public const float LambdaOffset = 1.2f;
    public const float LambdaSpeechAct = -0.2f;
    public const float LambdaDelayedDecisions = -0.05f;
    public const float LambdaInputQuality = -0.1f;
    public const float LambdaCodingQuality = -0.2f;
    public const float LambdaQuantOffset = 0.8f;
    public const int ReduceBitrate10MsBps = 2200;
    public const int MaxBandwidthSwitchDelayMs = 5000;
}

/// <summary>silk_nsq_state: the noise shaping quantizer's state.</summary>
internal sealed class SilkNsqState
{
    public readonly short[] Xq = new short[2 * SilkChannelDecoder.MaxFrameLength];
    public readonly int[] SLtpShpQ14 = new int[2 * SilkChannelDecoder.MaxFrameLength];
    public readonly int[] SLpcQ14 = new int[SilkChannelDecoder.MaxSubFrameLength + SilkEncoderConstants.NsqLpcBufLength];
    public readonly int[] SAr2Q14 = new int[SilkEncoderConstants.MaxShapeLpcOrder];
    public int SLfArShpQ14;
    public int SDiffShpQ14;
    public int LagPrev;
    public int SLtpBufIdx;
    public int SLtpShpBufIdx;
    public int RandSeed;
    public int PrevGainQ16;
    public int RewhiteFlag;

    public void Clear()
    {
        Array.Clear(Xq);
        Array.Clear(SLtpShpQ14);
        Array.Clear(SLpcQ14);
        Array.Clear(SAr2Q14);
        SLfArShpQ14 = 0;
        SDiffShpQ14 = 0;
        LagPrev = 0;
        SLtpBufIdx = 0;
        SLtpShpBufIdx = 0;
        RandSeed = 0;
        PrevGainQ16 = 0;
        RewhiteFlag = 0;
    }

    public void CopyFrom(SilkNsqState other)
    {
        other.Xq.CopyTo(Xq, 0);
        other.SLtpShpQ14.CopyTo(SLtpShpQ14, 0);
        other.SLpcQ14.CopyTo(SLpcQ14, 0);
        other.SAr2Q14.CopyTo(SAr2Q14, 0);
        SLfArShpQ14 = other.SLfArShpQ14;
        SDiffShpQ14 = other.SDiffShpQ14;
        LagPrev = other.LagPrev;
        SLtpBufIdx = other.SLtpBufIdx;
        SLtpShpBufIdx = other.SLtpShpBufIdx;
        RandSeed = other.RandSeed;
        PrevGainQ16 = other.PrevGainQ16;
        RewhiteFlag = other.RewhiteFlag;
    }
}

/// <summary>silk_VAD_state.</summary>
internal sealed class SilkVadState
{
    public readonly int[] AnaState = new int[2];
    public readonly int[] AnaState1 = new int[2];
    public readonly int[] AnaState2 = new int[2];
    public readonly int[] XnrgSubfr = new int[SilkEncoderConstants.VadNBands];
    public readonly int[] NrgRatioSmthQ8 = new int[SilkEncoderConstants.VadNBands];
    public short HpState;
    public readonly int[] Nl = new int[SilkEncoderConstants.VadNBands];
    public readonly int[] InvNl = new int[SilkEncoderConstants.VadNBands];
    public readonly int[] NoiseLevelBias = new int[SilkEncoderConstants.VadNBands];
    public int Counter;
}

/// <summary>silk_LP_state: the variable cut-off low-pass filter used when switching bandwidth.</summary>
internal sealed class SilkLpState
{
    public readonly int[] InLpState = new int[2];
    public int TransitionFrameNo;
    public int Mode;
    public int SavedFsKhz;
}

/// <summary>stereo_enc_state.</summary>
internal sealed class SilkStereoEncState
{
    public readonly short[] PredPrevQ13 = new short[2];
    public readonly short[] SMid = new short[2];
    public readonly short[] SSide = new short[2];
    public readonly int[] MidSideAmpQ0 = new int[4];
    public short SmthWidthQ14;
    public short WidthPrevQ14;
    public short SilentSideLen;

    /// <summary>predIx[ MAX_FRAMES_PER_PACKET ][ 2 ][ 3 ], flattened.</summary>
    public readonly sbyte[] PredIx = new sbyte[SilkChannelDecoder.MaxFramesPerPacket * 2 * 3];
    public readonly sbyte[] MidOnlyFlags = new sbyte[SilkChannelDecoder.MaxFramesPerPacket];
}

/// <summary>silk_encoder_control_FLP: the parameters of one frame being encoded.</summary>
internal sealed class SilkEncoderControl
{
    public readonly float[] Gains = new float[SilkChannelDecoder.MaxNbSubfr];

    /// <summary>PredCoef[ 2 ][ MAX_LPC_ORDER ], flattened.</summary>
    public readonly float[] PredCoef = new float[2 * SilkChannelDecoder.MaxLpcOrder];
    public readonly float[] LtpCoef = new float[SilkChannelDecoder.LtpOrder * SilkChannelDecoder.MaxNbSubfr];
    public float LtpScale;
    public readonly int[] PitchL = new int[SilkChannelDecoder.MaxNbSubfr];
    public readonly float[] Ar = new float[SilkChannelDecoder.MaxNbSubfr * SilkEncoderConstants.MaxShapeLpcOrder];
    public readonly float[] LfMaShp = new float[SilkChannelDecoder.MaxNbSubfr];
    public readonly float[] LfArShp = new float[SilkChannelDecoder.MaxNbSubfr];
    public readonly float[] Tilt = new float[SilkChannelDecoder.MaxNbSubfr];
    public readonly float[] HarmShapeGain = new float[SilkChannelDecoder.MaxNbSubfr];
    public float Lambda;
    public float InputQuality;
    public float CodingQuality;
    public float PredGain;
    public float LtpPredCodGain;
    public readonly float[] ResNrg = new float[SilkChannelDecoder.MaxNbSubfr];
    public readonly int[] GainsUnqQ16 = new int[SilkChannelDecoder.MaxNbSubfr];
    public sbyte LastGainIndexPrev;

    public Span<float> PredCoefHalf(int half) => PredCoef.AsSpan(half * SilkChannelDecoder.MaxLpcOrder, SilkChannelDecoder.MaxLpcOrder);

    public void Clear()
    {
        Array.Clear(Gains);
        Array.Clear(PredCoef);
        Array.Clear(LtpCoef);
        LtpScale = 0;
        Array.Clear(PitchL);
        Array.Clear(Ar);
        Array.Clear(LfMaShp);
        Array.Clear(LfArShp);
        Array.Clear(Tilt);
        Array.Clear(HarmShapeGain);
        Lambda = 0;
        InputQuality = 0;
        CodingQuality = 0;
        PredGain = 0;
        LtpPredCodGain = 0;
        Array.Clear(ResNrg);
        Array.Clear(GainsUnqQ16);
        LastGainIndexPrev = 0;
    }
}
