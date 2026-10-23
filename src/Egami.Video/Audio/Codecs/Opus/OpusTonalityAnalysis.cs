// Translated from libopus 1.5.2 src/analysis.c, src/analysis.h, src/mlp.c and fast_atan2f of celt/mathops.h (float build).
using static Egami.Video.Audio.Codecs.Opus.OpusAnalysisTables;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// TonalityAnalysisState: the Opus encoder's look-ahead analysis. Every 20 ms it measures tonality, bandwidth and loudness on a
/// 480-point FFT of the mono downmix at 24 kHz, and a small recurrent network turns the features into speech/music and voice
/// activity probabilities. The encoder uses them to pick the mode, the bandwidth and CELT's allocation.
/// </summary>
internal sealed class OpusTonalityAnalysis
{
    private const int NbFrames = 8;
    private const int NbTBands = 18;
    private const int AnalysisBufSize = 720;
    private const int AnalysisCountMax = 10000;
    public const int DetectSize = 100;
    private const int MaxNeurons = 32;
    private const int NbTonalSkipBands = 9;
    private const float TransitionPenalty = 10;
    private const float LeakageOffset = 2.5f;
    private const float LeakageSlope = 2f;
    private const float WeightsScale = 1f / 128;
    private const double MPi = 3.141592653;

    private static ReadOnlySpan<float> StdFeatureBias => [5.684947f, 3.475288f, 1.770634f, 1.599784f, 3.773215f, 2.163313f, 1.260756f, 1.116868f, 1.918795f];

    private readonly int _fs;
    private readonly float[] _angle = new float[240];
    private readonly float[] _dAngle = new float[240];
    private readonly float[] _d2Angle = new float[240];
    private readonly float[] _inmem = new float[AnalysisBufSize];
    private int _memFill;
    private readonly float[] _prevBandTonality = new float[NbTBands];
    private float _prevTonality;
    private int _prevBandwidth;
    private readonly float[] _e = new float[NbFrames * NbTBands];
    private readonly float[] _logE = new float[NbFrames * NbTBands];
    private readonly float[] _lowE = new float[NbTBands];
    private readonly float[] _highE = new float[NbTBands];
    private readonly float[] _meanE = new float[NbTBands + 1];
    private readonly float[] _mem = new float[32];
    private readonly float[] _cmean = new float[8];
    private readonly float[] _std = new float[9];
    private float _eTracker;
    private float _lowECount;
    private int _eCount;
    private int _count;
    private int _analysisOffset;
    private int _writePos;
    private int _readPos;
    private int _readSubframe;
    private float _hpEnerAccum;
    private bool _initialized;
    private readonly float[] _rnnState = new float[MaxNeurons];
    private readonly float[] _downmixState = new float[3];
    private readonly OpusAnalysisInfo[] _info = new OpusAnalysisInfo[DetectSize];

    // Scratch.
    private readonly float[] _fftIn = new float[960];
    private readonly float[] _fftOut = new float[960];
    private readonly float[] _tmp = new float[3 * 960];
    private readonly float[] _tmp3x = new float[3 * 960];

    /// <summary>tonality_analysis_init for the encoder's sample rate.</summary>
    public OpusTonalityAnalysis(int sampleRate)
    {
        _fs = sampleRate;
        Reset();
    }

    /// <summary>Gets whether the analysis has run since the last reset.</summary>
    public bool Initialized => _initialized;

    /// <summary>Gets or sets the read position (read_pos), which the encoder saves and restores around multi-frame packets.</summary>
    public int ReadPos
    {
        get => _readPos;
        set => _readPos = value;
    }

    /// <summary>Gets or sets read_subframe.</summary>
    public int ReadSubframe
    {
        get => _readSubframe;
        set => _readSubframe = value;
    }

    /// <summary>tonality_analysis_reset.</summary>
    public void Reset()
    {
        Array.Clear(_angle);
        Array.Clear(_dAngle);
        Array.Clear(_d2Angle);
        Array.Clear(_inmem);
        _memFill = 0;
        Array.Clear(_prevBandTonality);
        _prevTonality = 0;
        _prevBandwidth = 0;
        Array.Clear(_e);
        Array.Clear(_logE);
        Array.Clear(_lowE);
        Array.Clear(_highE);
        Array.Clear(_meanE);
        Array.Clear(_mem);
        Array.Clear(_cmean);
        Array.Clear(_std);
        _eTracker = 0;
        _lowECount = 0;
        _eCount = 0;
        _count = 0;
        _analysisOffset = 0;
        _writePos = 0;
        _readPos = 0;
        _readSubframe = 0;
        _hpEnerAccum = 0;
        _initialized = false;
        Array.Clear(_rnnState);
        Array.Clear(_downmixState);
        Array.Clear(_info);
    }

    /// <summary>
    /// run_analysis: analyses the new samples of <paramref name="pcm"/> (<paramref name="analysisFrameSize"/> samples per channel of
    /// <paramref name="channels"/> interleaved channels; <paramref name="c1"/> and <paramref name="c2"/> pick the downmix as in
    /// libopus) and reports the analysis of the frame of <paramref name="frameSize"/> samples about to be encoded.
    /// </summary>
    public void Run(ReadOnlySpan<float> pcm, int analysisFrameSize, int frameSize, int c1, int c2, int channels, int lsbDepth, out OpusAnalysisInfo info)
    {
        analysisFrameSize -= analysisFrameSize & 1;
        if (!pcm.IsEmpty)
        {
            analysisFrameSize = Math.Min((DetectSize - 5) * _fs / 50, analysisFrameSize);
            int pcmLen = analysisFrameSize - _analysisOffset;
            int offset = _analysisOffset;
            while (pcmLen > 0)
            {
                Analyse(pcm, Math.Min(_fs / 50, pcmLen), offset, c1, c2, channels, lsbDepth);
                offset += _fs / 50;
                pcmLen -= _fs / 50;
            }

            _analysisOffset = analysisFrameSize;
            _analysisOffset -= frameSize;
        }

        GetInfo(out info, frameSize);
    }

    /// <summary>tonality_get_info: the analysis of the next <paramref name="len"/> samples to encode.</summary>
    public void GetInfo(out OpusAnalysisInfo infoOut, int len)
    {
        int pos = _readPos;
        int currLookahead = _writePos - _readPos;
        if (currLookahead < 0)
        {
            currLookahead += DetectSize;
        }

        _readSubframe += len / (_fs / 400);
        while (_readSubframe >= 8)
        {
            _readSubframe -= 8;
            _readPos++;
        }

        if (_readPos >= DetectSize)
        {
            _readPos -= DetectSize;
        }

        // On long frames, look at the second analysis window rather than the first.
        if (len > _fs / 50 && pos != _writePos)
        {
            pos++;
            if (pos == DetectSize)
            {
                pos = 0;
            }
        }

        if (pos == _writePos)
        {
            pos--;
        }

        if (pos < 0)
        {
            pos = DetectSize - 1;
        }

        int pos0 = pos;
        infoOut = _info[pos];
        if (!infoOut.Valid)
        {
            return;
        }

        float tonalityMax = infoOut.Tonality;
        float tonalityAvg = infoOut.Tonality;
        int tonalityCount = 1;
        // Look at the neighbouring frames and pick largest bandwidth.
        int bandwidthSpan = 6;
        for (int i = 0; i < 3; i++)
        {
            pos++;
            if (pos == DetectSize)
            {
                pos = 0;
            }

            if (pos == _writePos)
            {
                break;
            }

            tonalityMax = Max(tonalityMax, _info[pos].Tonality);
            tonalityAvg += _info[pos].Tonality;
            tonalityCount++;
            infoOut.Bandwidth = Math.Max(infoOut.Bandwidth, _info[pos].Bandwidth);
            bandwidthSpan--;
        }

        pos = pos0;
        // Look back in time to see if any has a wider bandwidth than the current frame.
        for (int i = 0; i < bandwidthSpan; i++)
        {
            pos--;
            if (pos < 0)
            {
                pos = DetectSize - 1;
            }

            if (pos == _writePos)
            {
                break;
            }

            infoOut.Bandwidth = Math.Max(infoOut.Bandwidth, _info[pos].Bandwidth);
        }

        infoOut.Tonality = Max(tonalityAvg / tonalityCount, tonalityMax - .2f);

        int mpos = pos0;
        int vpos = pos0;
        // If we have enough look-ahead, compensate for the ~5-frame delay in the music prob and ~1 frame delay in the VAD prob.
        if (currLookahead > 15)
        {
            mpos += 5;
            if (mpos >= DetectSize)
            {
                mpos -= DetectSize;
            }

            vpos += 1;
            if (vpos >= DetectSize)
            {
                vpos -= DetectSize;
            }
        }

        float probMin = 1f;
        float probMax = 0f;
        float vadProb = _info[vpos].ActivityProbability;
        float probCount = Max(.1f, vadProb);
        float probAvg = Max(.1f, vadProb) * _info[mpos].MusicProb;
        while (true)
        {
            mpos++;
            if (mpos == DetectSize)
            {
                mpos = 0;
            }

            if (mpos == _writePos)
            {
                break;
            }

            vpos++;
            if (vpos == DetectSize)
            {
                vpos = 0;
            }

            if (vpos == _writePos)
            {
                break;
            }

            float posVad = _info[vpos].ActivityProbability;
            probMin = Min((probAvg - (TransitionPenalty * (vadProb - posVad))) / probCount, probMin);
            probMax = Max((probAvg + (TransitionPenalty * (vadProb - posVad))) / probCount, probMax);
            probCount += Max(.1f, posVad);
            probAvg += Max(.1f, posVad) * _info[mpos].MusicProb;
        }

        infoOut.MusicProb = probAvg / probCount;
        probMin = Min(probAvg / probCount, probMin);
        probMax = Max(probAvg / probCount, probMax);
        probMin = Max(probMin, 0f);
        probMax = Min(probMax, 1f);

        // If we don't have enough look-ahead, do our best to make a decent decision.
        if (currLookahead < 10)
        {
            float pmin = probMin;
            float pmax = probMax;
            pos = pos0;
            // Look for min/max in the past.
            for (int i = 0; i < Math.Min(_count - 1, 15); i++)
            {
                pos--;
                if (pos < 0)
                {
                    pos = DetectSize - 1;
                }

                pmin = Min(pmin, _info[pos].MusicProb);
                pmax = Max(pmax, _info[pos].MusicProb);
            }

            // Bias against switching on active audio.
            pmin = Max(0f, pmin - (.1f * vadProb));
            pmax = Min(1f, pmax + (.1f * vadProb));
            probMin += (1f - (.1f * currLookahead)) * (pmin - probMin);
            probMax += (1f - (.1f * currLookahead)) * (pmax - probMax);
        }

        infoOut.MusicProbMin = probMin;
        infoOut.MusicProbMax = probMax;
    }

    // tonality_analysis: one 20 ms step of the analysis.
    private void Analyse(ReadOnlySpan<float> x, int len, int offset, int c1, int c2, int channels, int lsbDepth)
    {
        const int N = 480;
        const int N2 = 240;
        float[] a = _angle;
        float[] dA = _dAngle;
        float[] d2A = _d2Angle;
        Span<float> tonality = stackalloc float[240];
        Span<float> noisiness = stackalloc float[240];
        Span<float> bandTonality = stackalloc float[NbTBands];
        Span<float> logE = stackalloc float[NbTBands];
        Span<float> bfcc = stackalloc float[8];
        Span<float> features = stackalloc float[25];
        const float Pi4 = (float)(MPi * MPi * MPi * MPi);
        float slope = 0;
        Span<float> frameProbs = stackalloc float[2];
        Span<bool> isMasked = stackalloc bool[NbTBands + 1];
        Span<float> tonality2 = stackalloc float[240];
        Span<float> midE = stackalloc float[8];
        float specVariability = 0;
        Span<float> bandLog2 = stackalloc float[NbTBands + 1];
        Span<float> leakageFrom = stackalloc float[NbTBands + 1];
        Span<float> leakageTo = stackalloc float[NbTBands + 1];
        Span<float> layerOut = stackalloc float[MaxNeurons];

        if (!_initialized)
        {
            _memFill = 240;
            _initialized = true;
        }

        float alpha = 1f / Math.Min(10, 1 + _count);
        float alphaE = 1f / Math.Min(25, 1 + _count);
        // Noise floor related decay for bandwidth detection: -2.2 dB/second.
        float alphaE2 = 1f / Math.Min(100, 1 + _count);
        if (_count <= 1)
        {
            alphaE2 = 1;
        }

        if (_fs == 48000)
        {
            // len and offset are now at 24 kHz.
            len /= 2;
            offset /= 2;
        }
        else if (_fs == 16000)
        {
            len = 3 * len / 2;
            offset = 3 * offset / 2;
        }

        _hpEnerAccum += DownmixAndResample(x, _inmem.AsSpan(_memFill), Math.Min(len, AnalysisBufSize - _memFill), offset, c1, c2, channels);
        if (_memFill + len < AnalysisBufSize)
        {
            _memFill += len;
            // Don't have enough to update the analysis.
            return;
        }

        float hpEner = _hpEnerAccum;
        ref OpusAnalysisInfo info = ref _info[_writePos++];
        if (_writePos >= DetectSize)
        {
            _writePos -= DetectSize;
        }

        bool isSilence = IsDigitalSilence(_inmem, AnalysisBufSize, lsbDepth);

        Span<float> fin = _fftIn;
        Span<float> fout = _fftOut;
        for (int i = 0; i < N2; i++)
        {
            float w = AnalysisWindow[i];
            fin[2 * i] = w * _inmem[i];
            fin[(2 * i) + 1] = w * _inmem[N2 + i];
            fin[2 * (N - i - 1)] = w * _inmem[N - i - 1];
            fin[(2 * (N - i - 1)) + 1] = w * _inmem[N + N2 - i - 1];
        }

        Array.Copy(_inmem, AnalysisBufSize - 240, _inmem, 0, 240);
        int remaining = len - (AnalysisBufSize - _memFill);
        _hpEnerAccum = DownmixAndResample(x, _inmem.AsSpan(240), remaining, offset + AnalysisBufSize - _memFill, c1, c2, channels);
        _memFill = 240 + remaining;
        if (isSilence)
        {
            // On silence, copy the previous analysis.
            int prevPos = _writePos - 2;
            if (prevPos < 0)
            {
                prevPos += DetectSize;
            }

            info = _info[prevPos];
            return;
        }

        // opus_fft on the 480-point state: scale, bit-reverse, transform.
        ReadOnlySpan<short> bitrev = CeltTables.FftBitrev480;
        const float Scale = 0.002083333f;
        for (int i = 0; i < N; i++)
        {
            fout[2 * bitrev[i]] = Scale * fin[2 * i];
            fout[(2 * bitrev[i]) + 1] = Scale * fin[(2 * i) + 1];
        }

        CeltMdct.Fft(0, fout);
        if (float.IsNaN(fout[0]))
        {
            info.Valid = false;
            return;
        }

        for (int i = 1; i < N2; i++)
        {
            float x1r = fout[2 * i] + fout[2 * (N - i)];
            float x1i = fout[(2 * i) + 1] - fout[(2 * (N - i)) + 1];
            float x2r = fout[(2 * i) + 1] + fout[(2 * (N - i)) + 1];
            float x2i = fout[2 * (N - i)] - fout[2 * i];

            float angle = (float)(.5f / MPi) * FastAtan2(x1i, x1r);
            float dAngle = angle - a[i];
            float d2Angle = dAngle - dA[i];

            float angle2 = (float)(.5f / MPi) * FastAtan2(x2i, x2r);
            float dAngle2 = angle2 - angle;
            float d2Angle2 = dAngle2 - dAngle;

            float mod1 = d2Angle - Float2Int(d2Angle);
            noisiness[i] = MathF.Abs(mod1);
            mod1 *= mod1;
            mod1 *= mod1;

            float mod2 = d2Angle2 - Float2Int(d2Angle2);
            noisiness[i] += MathF.Abs(mod2);
            mod2 *= mod2;
            mod2 *= mod2;

            float avgMod = .25f * (d2A[i] + mod1 + (2 * mod2));
            // This introduces an extra delay of 2 frames in the detection.
            tonality[i] = (1f / (1f + (40f * 16f * Pi4 * avgMod))) - .015f;
            // No delay on this detection, but it's less reliable.
            tonality2[i] = (1f / (1f + (40f * 16f * Pi4 * mod2))) - .015f;

            a[i] = angle2;
            dA[i] = dAngle2;
            d2A[i] = mod2;
        }

        for (int i = 2; i < N2 - 1; i++)
        {
            float tt = Min(tonality2[i], Max(tonality2[i - 1], tonality2[i + 1]));
            tonality[i] = .9f * Max(tonality[i], tt - .1f);
        }

        float frameTonality = 0;
        float maxFrameTonality = 0;
        info.Activity = 0;
        float frameNoisiness = 0;
        float frameStationarity = 0;
        if (_count == 0)
        {
            for (int b = 0; b < NbTBands; b++)
            {
                _lowE[b] = 1e10f;
                _highE[b] = -1e10f;
            }
        }

        float relativeE = 0;
        float frameLoudness = 0;
        // The energy of the very first band is special because of DC.
        {
            float x1r = 2 * fout[0];
            float x2r = 2 * fout[1];
            float e = (x1r * x1r) + (x2r * x2r);
            for (int i = 1; i < 4; i++)
            {
                e += BinEnergy(fout, i);
            }

            bandLog2[0] = .5f * 1.442695f * (float)Math.Log(e + 1e-10f);
        }

        ReadOnlySpan<short> tbands = TBands;
        for (int b = 0; b < NbTBands; b++)
        {
            float e = 0;
            float tE = 0;
            float nE = 0;
            for (int i = tbands[b]; i < tbands[b + 1]; i++)
            {
                float binE = BinEnergy(fout, i);
                e += binE;
                tE += binE * Max(0, tonality[i]);
                nE += binE * 2f * (.5f - noisiness[i]);
            }

            // Check for extreme band energies that could cause NaNs later.
            if (!(e < 1e9f) || float.IsNaN(e))
            {
                info.Valid = false;
                return;
            }

            _e[(_eCount * NbTBands) + b] = e;
            frameNoisiness += nE / (1e-15f + e);

            frameLoudness += (float)Math.Sqrt(e + 1e-10f);
            logE[b] = (float)Math.Log(e + 1e-10f);
            bandLog2[b + 1] = .5f * 1.442695f * (float)Math.Log(e + 1e-10f);
            _logE[(_eCount * NbTBands) + b] = logE[b];
            if (_count == 0)
            {
                _highE[b] = _lowE[b] = logE[b];
            }

            if (_highE[b] > _lowE[b] + 7.5)
            {
                if (_highE[b] - logE[b] > logE[b] - _lowE[b])
                {
                    _highE[b] -= .01f;
                }
                else
                {
                    _lowE[b] += .01f;
                }
            }

            if (logE[b] > _highE[b])
            {
                _highE[b] = logE[b];
                _lowE[b] = Max(_highE[b] - 15, _lowE[b]);
            }
            else if (logE[b] < _lowE[b])
            {
                _lowE[b] = logE[b];
                _highE[b] = Min(_lowE[b] + 15, _highE[b]);
            }

            relativeE += (logE[b] - _lowE[b]) / (1e-5f + (_highE[b] - _lowE[b]));

            float l1 = 0;
            float l2 = 0;
            for (int i = 0; i < NbFrames; i++)
            {
                l1 += (float)Math.Sqrt(_e[(i * NbTBands) + b]);
                l2 += _e[(i * NbTBands) + b];
            }

            float stationarity = Min(0.99f, l1 / (float)Math.Sqrt(1e-15 + (NbFrames * l2)));
            stationarity *= stationarity;
            stationarity *= stationarity;
            frameStationarity += stationarity;
            bandTonality[b] = Max(tE / (1e-15f + e), stationarity * _prevBandTonality[b]);
            frameTonality += bandTonality[b];
            if (b >= NbTBands - NbTonalSkipBands)
            {
                frameTonality -= bandTonality[b - NbTBands + NbTonalSkipBands];
            }

            maxFrameTonality = Max(maxFrameTonality, (1f + (.03f * (b - NbTBands))) * frameTonality);
            slope += bandTonality[b] * (b - 8);
            _prevBandTonality[b] = bandTonality[b];
        }

        leakageFrom[0] = bandLog2[0];
        leakageTo[0] = bandLog2[0] - LeakageOffset;
        for (int b = 1; b < NbTBands + 1; b++)
        {
            float leakSlope = LeakageSlope * (tbands[b] - tbands[b - 1]) / 4;
            leakageFrom[b] = Min(leakageFrom[b - 1] + leakSlope, bandLog2[b]);
            leakageTo[b] = Max(leakageTo[b - 1] - leakSlope, bandLog2[b] - LeakageOffset);
        }

        for (int b = NbTBands - 2; b >= 0; b--)
        {
            float leakSlope = LeakageSlope * (tbands[b + 1] - tbands[b]) / 4;
            leakageFrom[b] = Min(leakageFrom[b + 1] + leakSlope, leakageFrom[b]);
            leakageTo[b] = Max(leakageTo[b + 1] - leakSlope, leakageTo[b]);
        }

        for (int b = 0; b < NbTBands + 1; b++)
        {
            // leak_boost[] is made up of two terms. The first, based on leakage_to[], represents the boost needed to overcome the
            // amount of analysis leakage caused in a weaker band b by louder neighbouring bands. The second, based on
            // leakage_from[], applies to a loud band b for which the quantization noise causes synthesis leakage to the weaker
            // neighbouring bands.
            float boost = Max(0, leakageTo[b] - bandLog2[b]) + Max(0, bandLog2[b] - (leakageFrom[b] + LeakageOffset));
            info.LeakBoost[b] = (byte)Math.Min(255, (int)Math.Floor(.5 + (64f * boost)));
        }

        for (int b = NbTBands + 1; b < OpusAnalysisInfo.LeakBands; b++)
        {
            info.LeakBoost[b] = 0;
        }

        for (int i = 0; i < NbFrames; i++)
        {
            float mindist = 1e15f;
            for (int j = 0; j < NbFrames; j++)
            {
                float dist = 0;
                for (int k = 0; k < NbTBands; k++)
                {
                    float tmp = _logE[(i * NbTBands) + k] - _logE[(j * NbTBands) + k];
                    dist += tmp * tmp;
                }

                if (j != i)
                {
                    mindist = Min(mindist, dist);
                }
            }

            specVariability += mindist;
        }

        specVariability = (float)Math.Sqrt(specVariability / NbFrames / NbTBands);
        float bandwidthMask = 0;
        int bandwidth = 0;
        float maxE = 0;
        float noiseFloor = 5.7e-4f / (1 << Math.Max(0, lsbDepth - 8));
        noiseFloor *= noiseFloor;
        float belowMaxPitch = 0;
        float aboveMaxPitch = 0;
        int bb;
        for (bb = 0; bb < NbTBands; bb++)
        {
            float e = 0;
            int bandStart = tbands[bb];
            int bandEnd = tbands[bb + 1];
            for (int i = bandStart; i < bandEnd; i++)
            {
                e += BinEnergy(fout, i);
            }

            maxE = Max(maxE, e);
            if (bandStart < 64)
            {
                belowMaxPitch += e;
            }
            else
            {
                aboveMaxPitch += e;
            }

            _meanE[bb] = Max((1 - alphaE2) * _meanE[bb], e);
            float em = Max(e, _meanE[bb]);
            // Consider the band "active" only if all these conditions are met: 1) less than 90 dB below the peak band (maximal
            // masking possible considering both the ATH and the loudness-dependent slope of the spreading function); 2) above
            // the PCM quantization noise floor. We use b+1 because the first CELT band isn't included in tbands[].
            if (e * 1e9f > maxE && (em > 3 * noiseFloor * (bandEnd - bandStart) || e > noiseFloor * (bandEnd - bandStart)))
            {
                bandwidth = bb + 1;
            }

            // Check if the band is masked (see below).
            isMasked[bb] = e < (_prevBandwidth >= bb + 1 ? .01f : .05f) * bandwidthMask;
            // Use a simple follower with 13 dB/Bark slope for spreading function.
            bandwidthMask = Max(.05f * bandwidthMask, e);
        }

        // Special case for the last two bands, for which we don't have spectrum but only the energy above 12 kHz. The
        // difficulty here is that the high-pass we use for computing the energy is not very selective, so we need to be more
        // conservative in what we consider to be "active".
        if (_fs == 48000)
        {
            float e = hpEner * (1f / (60 * 60));
            float noiseRatio = _prevBandwidth == 20 ? 10f : 30f;
            aboveMaxPitch += e;
            _meanE[bb] = Max((1 - alphaE2) * _meanE[bb], e);
            float em = Max(e, _meanE[bb]);
            if (em > 3 * noiseRatio * noiseFloor * 160 || e > noiseRatio * noiseFloor * 160)
            {
                bandwidth = 20;
            }

            // Check if the band is masked (see below).
            isMasked[bb] = e < (_prevBandwidth == 20 ? .01f : .05f) * bandwidthMask;
        }

        info.MaxPitchRatio = aboveMaxPitch > belowMaxPitch ? belowMaxPitch / aboveMaxPitch : 1;

        // In some cases, resampling aliasing can create a small amount of energy in the first band being cut. So if the last band
        // is masked, we don't include it.
        if (bandwidth == 20 && isMasked[NbTBands])
        {
            bandwidth -= 2;
        }
        else if (bandwidth > 0 && bandwidth <= NbTBands && isMasked[bandwidth - 1])
        {
            bandwidth--;
        }

        if (_count <= 2)
        {
            bandwidth = 20;
        }

        frameLoudness = 20 * (float)Math.Log10(frameLoudness);
        _eTracker = Max(_eTracker - .003f, frameLoudness);
        _lowECount *= 1 - alphaE;
        if (frameLoudness < _eTracker - 30)
        {
            _lowECount += alphaE;
        }

        ReadOnlySpan<float> dct = DctTable;
        for (int i = 0; i < 8; i++)
        {
            float sum = 0;
            for (int b = 0; b < 16; b++)
            {
                sum += dct[(i * 16) + b] * logE[b];
            }

            bfcc[i] = sum;
        }

        for (int i = 0; i < 8; i++)
        {
            float sum = 0;
            for (int b = 0; b < 16; b++)
            {
                sum += dct[(i * 16) + b] * .5f * (_highE[b] + _lowE[b]);
            }

            midE[i] = sum;
        }

        frameStationarity /= NbTBands;
        relativeE /= NbTBands;
        if (_count < 10)
        {
            relativeE = .5f;
        }

        frameNoisiness /= NbTBands;
        info.Activity = frameNoisiness + ((1 - frameNoisiness) * relativeE);
        frameTonality = maxFrameTonality / (NbTBands - NbTonalSkipBands);
        frameTonality = Max(frameTonality, _prevTonality * .8f);
        _prevTonality = frameTonality;

        slope /= 8 * 8;
        info.TonalitySlope = slope;

        _eCount = (_eCount + 1) % NbFrames;
        _count = Math.Min(_count + 1, AnalysisCountMax);
        info.Tonality = frameTonality;

        float[] mem = _mem;
        for (int i = 0; i < 4; i++)
        {
            features[i] = (-0.12299f * (bfcc[i] + mem[i + 24])) + (0.49195f * (mem[i] + mem[i + 16])) + (0.69693f * mem[i + 8]) - (1.4349f * _cmean[i]);
        }

        for (int i = 0; i < 4; i++)
        {
            _cmean[i] = ((1 - alpha) * _cmean[i]) + (alpha * bfcc[i]);
        }

        for (int i = 0; i < 4; i++)
        {
            features[4 + i] = (0.63246f * (bfcc[i] - mem[i + 24])) + (0.31623f * (mem[i] - mem[i + 16]));
        }

        for (int i = 0; i < 3; i++)
        {
            features[8 + i] = (0.53452f * (bfcc[i] + mem[i + 24])) - (0.26726f * (mem[i] + mem[i + 16])) - (0.53452f * mem[i + 8]);
        }

        if (_count > 5)
        {
            for (int i = 0; i < 9; i++)
            {
                _std[i] = ((1 - alpha) * _std[i]) + (alpha * features[i] * features[i]);
            }
        }

        for (int i = 0; i < 4; i++)
        {
            features[i] = bfcc[i] - midE[i];
        }

        for (int i = 0; i < 8; i++)
        {
            mem[i + 24] = mem[i + 16];
            mem[i + 16] = mem[i + 8];
            mem[i + 8] = mem[i];
            mem[i] = bfcc[i];
        }

        ReadOnlySpan<float> stdBias = StdFeatureBias;
        for (int i = 0; i < 9; i++)
        {
            features[11 + i] = (float)Math.Sqrt(_std[i]) - stdBias[i];
        }

        features[18] = specVariability - 0.78f;
        features[20] = info.Tonality - 0.154723f;
        features[21] = info.Activity - 0.724643f;
        features[22] = frameStationarity - 0.743717f;
        features[23] = info.TonalitySlope + 0.069216f;
        features[24] = _lowECount - 0.067930f;

        ComputeDense(Layer0Bias, Layer0Weights, 25, 32, false, layerOut, features);
        ComputeGru(layerOut, _rnnState);
        ComputeDense(Layer2Bias, Layer2Weights, 24, 2, true, frameProbs, _rnnState);
        info.ActivityProbability = frameProbs[1];
        info.MusicProb = frameProbs[0];
        info.Bandwidth = bandwidth;
        _prevBandwidth = bandwidth;
        info.Noisiness = frameNoisiness;
        info.Valid = true;
    }

    private static float BinEnergy(ReadOnlySpan<float> fout, int i)
    {
        const int N = 480;
        return (fout[2 * i] * fout[2 * i]) + (fout[2 * (N - i)] * fout[2 * (N - i)])
            + (fout[(2 * i) + 1] * fout[(2 * i) + 1]) + (fout[(2 * (N - i)) + 1] * fout[(2 * (N - i)) + 1]);
    }

    // downmix_and_resample with downmix_float: the mono downmix at 24 kHz; returns the energy above 12 kHz at 48 kHz.
    private float DownmixAndResample(ReadOnlySpan<float> x, Span<float> y, int subframe, int offset, int c1, int c2, int channels)
    {
        if (subframe == 0)
        {
            return 0;
        }

        if (_fs == 48000)
        {
            subframe *= 2;
            offset *= 2;
        }
        else if (_fs == 16000)
        {
            subframe = subframe * 2 / 3;
            offset = offset * 2 / 3;
        }

        Span<float> tmp = _tmp.AsSpan(0, subframe);
        for (int j = 0; j < subframe; j++)
        {
            tmp[j] = x[((j + offset) * channels) + c1] * 32768f;
        }

        if (c2 > -1)
        {
            for (int j = 0; j < subframe; j++)
            {
                tmp[j] += x[((j + offset) * channels) + c2] * 32768f;
            }
        }
        else if (c2 == -2)
        {
            for (int c = 1; c < channels; c++)
            {
                for (int j = 0; j < subframe; j++)
                {
                    tmp[j] += x[((j + offset) * channels) + c] * 32768f;
                }
            }
        }

        float scale = 1f / 32768;
        if (c2 == -2)
        {
            scale /= channels;
        }
        else if (c2 > -1)
        {
            scale /= 2;
        }

        for (int j = 0; j < subframe; j++)
        {
            tmp[j] *= scale;
        }

        float ret = 0;
        if (_fs == 48000)
        {
            ret = ResamplerDown2Hp(_downmixState, y, tmp, subframe);
        }
        else if (_fs == 24000)
        {
            tmp.CopyTo(y);
        }
        else if (_fs == 16000)
        {
            Span<float> tmp3x = _tmp3x.AsSpan(0, 3 * subframe);
            for (int j = 0; j < subframe; j++)
            {
                tmp3x[3 * j] = tmp[j];
                tmp3x[(3 * j) + 1] = tmp[j];
                tmp3x[(3 * j) + 2] = tmp[j];
            }

            ResamplerDown2Hp(_downmixState, y, tmp3x, 3 * subframe);
        }

        return ret;
    }

    // silk_resampler_down2_hp: halves the rate with SILK's all-pass pair and returns the energy of the high half-band.
    private static float ResamplerDown2Hp(float[] s, Span<float> output, ReadOnlySpan<float> input, int inLen)
    {
        int len2 = inLen / 2;
        float hpEner = 0;
        for (int k = 0; k < len2; k++)
        {
            // All-pass section for even input sample.
            float in32 = input[2 * k];
            float y = in32 - s[0];
            float x = 0.6074371f * y;
            float out32 = s[0] + x;
            s[0] = in32 + x;
            float out32Hp = out32;
            // All-pass section for odd input sample, and add to output of previous section.
            in32 = input[(2 * k) + 1];
            y = in32 - s[1];
            x = 0.15063f * y;
            out32 += s[1];
            out32 += x;
            s[1] = in32 + x;

            y = -in32 - s[2];
            x = 0.15063f * y;
            out32Hp += s[2];
            out32Hp += x;
            s[2] = -in32 + x;

            hpEner += out32Hp * out32Hp;
            // Add, convert back to int16 and store to output.
            output[k] = .5f * out32;
        }

        return hpEner;
    }

    // is_digital_silence on the analysis buffer (one channel).
    private static bool IsDigitalSilence(ReadOnlySpan<float> pcm, int len, int lsbDepth)
    {
        float maxval = 0;
        float minval = 0;
        for (int i = 0; i < len; i++)
        {
            maxval = Max(maxval, pcm[i]);
            minval = Min(minval, pcm[i]);
        }

        return Max(maxval, -minval) <= (float)1 / (1 << lsbDepth);
    }

    // analysis_compute_dense.
    private static void ComputeDense(ReadOnlySpan<sbyte> bias, ReadOnlySpan<sbyte> weights, int nbInputs, int nbNeurons, bool sigmoid,
        Span<float> output, ReadOnlySpan<float> input)
    {
        for (int i = 0; i < nbNeurons; i++)
        {
            output[i] = bias[i];
        }

        GemmAccum(output, weights, nbNeurons, nbInputs, nbNeurons, input);
        for (int i = 0; i < nbNeurons; i++)
        {
            output[i] *= WeightsScale;
        }

        for (int i = 0; i < nbNeurons; i++)
        {
            output[i] = sigmoid ? SigmoidApprox(output[i]) : TansigApprox(output[i]);
        }
    }

    // analysis_compute_gru for layer1 (32 inputs, 24 neurons).
    private static void ComputeGru(ReadOnlySpan<float> input, Span<float> state)
    {
        const int M = 32;
        const int N = 24;
        const int Stride = 3 * N;
        ReadOnlySpan<sbyte> bias = Layer1Bias;
        ReadOnlySpan<sbyte> inputWeights = Layer1Weights;
        ReadOnlySpan<sbyte> recurrentWeights = Layer1RecurWeights;
        Span<float> tmp = stackalloc float[N];
        Span<float> z = stackalloc float[N];
        Span<float> r = stackalloc float[N];
        Span<float> h = stackalloc float[N];

        // Compute update gate.
        for (int i = 0; i < N; i++)
        {
            z[i] = bias[i];
        }

        GemmAccum(z, inputWeights, N, M, Stride, input);
        GemmAccum(z, recurrentWeights, N, N, Stride, state);
        for (int i = 0; i < N; i++)
        {
            z[i] = SigmoidApprox(WeightsScale * z[i]);
        }

        // Compute reset gate.
        for (int i = 0; i < N; i++)
        {
            r[i] = bias[N + i];
        }

        GemmAccum(r, inputWeights[N..], N, M, Stride, input);
        GemmAccum(r, recurrentWeights[N..], N, N, Stride, state);
        for (int i = 0; i < N; i++)
        {
            r[i] = SigmoidApprox(WeightsScale * r[i]);
        }

        // Compute output.
        for (int i = 0; i < N; i++)
        {
            h[i] = bias[(2 * N) + i];
        }

        for (int i = 0; i < N; i++)
        {
            tmp[i] = state[i] * r[i];
        }

        GemmAccum(h, inputWeights[(2 * N)..], N, M, Stride, input);
        GemmAccum(h, recurrentWeights[(2 * N)..], N, N, Stride, tmp);
        for (int i = 0; i < N; i++)
        {
            h[i] = (z[i] * state[i]) + ((1 - z[i]) * TansigApprox(WeightsScale * h[i]));
        }

        h.CopyTo(state);
    }

    private static void GemmAccum(Span<float> output, ReadOnlySpan<sbyte> weights, int rows, int cols, int colStride, ReadOnlySpan<float> x)
    {
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                output[i] += weights[(j * colStride) + i] * x[j];
            }
        }
    }

    private static float TansigApprox(float x)
    {
        const float N0 = 952.52801514f;
        const float N1 = 96.39235687f;
        const float N2 = 0.60863042f;
        const float D0 = 952.72399902f;
        const float D1 = 413.36801147f;
        const float D2 = 11.88600922f;
        float x2 = x * x;
        float num = (((N2 * x2) + N1) * x2) + N0;
        float den = (((D2 * x2) + D1) * x2) + D0;
        num = num * x / den;
        return Max(-1f, Min(1f, num));
    }

    private static float SigmoidApprox(float x) => .5f + (.5f * TansigApprox(.5f * x));

    // fast_atan2f of celt/mathops.h.
    private static float FastAtan2(float y, float x)
    {
        const float CA = 0.43157974f;
        const float CB = 0.67848403f;
        const float CC = 0.08595542f;
        const float CE = 3.141592653f / 2;
        float x2 = x * x;
        float y2 = y * y;
        // For very small values, we don't care about the answer, so we can just return 0.
        if (x2 + y2 < 1e-18f)
        {
            return 0;
        }

        if (x2 < y2)
        {
            float den = (y2 + (CB * x2)) * (y2 + (CC * x2));
            return (-x * y * (y2 + (CA * x2)) / den) + (y < 0 ? -CE : CE);
        }
        else
        {
            float den = (x2 + (CB * y2)) * (x2 + (CC * y2));
            return (x * y * (x2 + (CA * y2)) / den) + (y < 0 ? -CE : CE) - (x * y < 0 ? -CE : CE);
        }
    }

    // float2int: lrintf, round half to even.
    private static float Float2Int(float x) => (int)MathF.Round(x);

    private static float Max(float a, float b) => a > b ? a : b;

    private static float Min(float a, float b) => a < b ? a : b;
}
