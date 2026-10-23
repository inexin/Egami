// Translated from libopus 1.5.2 celt/celt.h (AnalysisInfo, SILKInfo).
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>AnalysisInfo: what the encoder's tonality analysis tells the mode decisions and CELT about a frame.</summary>
internal struct OpusAnalysisInfo
{
    /// <summary>LEAK_BANDS.</summary>
    public const int LeakBands = 19;

    public bool Valid;
    public float Tonality;
    public float TonalitySlope;
    public float Noisiness;
    public float Activity;
    public float MusicProb;
    public float MusicProbMin;
    public float MusicProbMax;
    public int Bandwidth;
    public float ActivityProbability;
    public float MaxPitchRatio;

    /// <summary>The boost per band in Q6, to compensate for energy leaking between bands.</summary>
    public LeakBoostBuffer LeakBoost;

    /// <summary>unsigned char leak_boost[LEAK_BANDS].</summary>
    [InlineArray(LeakBands)]
    public struct LeakBoostBuffer
    {
        private byte _element0;
    }
}

/// <summary>SILKInfo: what the SILK layer tells CELT about a hybrid frame.</summary>
internal struct OpusSilkInfo
{
    public int SignalType;
    public int Offset;
}
