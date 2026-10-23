// Translated from libopus 1.5.2 silk/enc_API.c, check_control_input.c and float/structs_FLP.h (silk_encoder).
using static Egami.Video.Audio.Codecs.Opus.SilkChannelDecoder;
using static Egami.Video.Audio.Codecs.Opus.SilkEncoderConstants;
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>silk_encoder: SILK's encoder for one Opus stream (one channel, or mid and side).</summary>
internal sealed class SilkEncoder
{
    private readonly SilkChannelEncoder[] _stateFxx = [new(), new()];
    private readonly SilkStereoEncState _sStereo = new();
    private readonly short[] _buf = new short[MaxApiFsKhz * 60];
    private readonly SilkLpState _saveLp = new();
    private int _nBitsUsedLbrr;
    private int _nBitsExceeded;
    private int _nChannelsApi;
    private int _nChannelsInternal;
    private int _nPrevChannelsInternal;
    private int _timeSinceSwitchAllowedMs;
    private int _allowBandwidthSwitch;
    private int _prevDecodeOnlyMiddle;

    /// <summary>The mid (0) or side (1) channel encoder.</summary>
    public SilkChannelEncoder Channel(int n) => _stateFxx[n];

    /// <summary>silk_InitEncoder: resets the encoder and reports its state into <paramref name="encStatus"/>.</summary>
    public void Init(SilkEncControl encStatus)
    {
        _stateFxx[0].Init();
        _stateFxx[1].Init();
        Array.Clear(_sStereo.PredPrevQ13);
        Array.Clear(_sStereo.SMid);
        Array.Clear(_sStereo.SSide);
        Array.Clear(_sStereo.MidSideAmpQ0);
        _sStereo.SmthWidthQ14 = 0;
        _sStereo.WidthPrevQ14 = 0;
        _sStereo.SilentSideLen = 0;
        Array.Clear(_sStereo.PredIx);
        Array.Clear(_sStereo.MidOnlyFlags);
        _nBitsUsedLbrr = 0;
        _nBitsExceeded = 0;
        _nPrevChannelsInternal = 0;
        _timeSinceSwitchAllowedMs = 0;
        _allowBandwidthSwitch = 0;
        _prevDecodeOnlyMiddle = 0;
        _nChannelsApi = 1;
        _nChannelsInternal = 1;
        Query(encStatus);
    }

    private void Query(SilkEncControl encStatus)
    {
        SilkChannelEncoder s = _stateFxx[0];
        encStatus.NChannelsApi = _nChannelsApi;
        encStatus.NChannelsInternal = _nChannelsInternal;
        encStatus.ApiSampleRate = s.ApiFsHz;
        encStatus.MaxInternalSampleRate = s.MaxInternalFsHz;
        encStatus.MinInternalSampleRate = s.MinInternalFsHz;
        encStatus.DesiredInternalSampleRate = s.DesiredInternalFsHz;
        encStatus.PayloadSizeMs = s.PacketSizeMs;
        encStatus.BitRate = s.TargetRateBps;
        encStatus.PacketLossPercentage = s.PacketLossPerc;
        encStatus.Complexity = s.Complexity;
        encStatus.UseInBandFec = s.UseInBandFec;
        encStatus.UseDtx = s.UseDtx;
        encStatus.UseCbr = s.UseCbr;
        encStatus.InternalSampleRate = Smulbb(s.FsKhz, 1000);
        encStatus.AllowBandwidthSwitch = s.AllowBandwidthSwitch;
        encStatus.InWbModeWithoutVariableLp = s.FsKhz == 16 && s.SLp.Mode == 0 ? 1 : 0;
    }

    /// <summary>
    /// silk_Encode: buffers, resamples and encodes <paramref name="nSamplesIn"/> samples per channel (10 to 60 ms, at most one
    /// packet). <paramref name="nBytesOut"/> is the maximum payload in and the payload size out. Returns 0 or a SILK error code.
    /// </summary>
    public int Encode(SilkEncControl encControl, ReadOnlySpan<short> samplesIn, int nSamplesIn, OpusRangeEncoder? enc, ref int nBytesOut,
        int prefillFlag, int activity)
    {
        int ret = 0;
        int tmpPayloadSizeMs = 0;
        int tmpComplexity = 0;
        Span<int> mStargetRatesBps = stackalloc int[2];
        SilkChannelEncoder s0 = _stateFxx[0];
        SilkChannelEncoder s1 = _stateFxx[1];

        if (encControl.ReducedDependency != 0)
        {
            s0.FirstFrameAfterReset = 1;
            s1.FirstFrameAfterReset = 1;
        }

        s0.NFramesEncoded = 0;
        s1.NFramesEncoded = 0;
        if ((ret = CheckControlInput(encControl)) != 0)
        {
            return ret;
        }

        encControl.SwitchReady = 0;
        if (encControl.NChannelsInternal > _nChannelsInternal)
        {
            s1.Init();
            Array.Clear(_sStereo.PredPrevQ13);
            Array.Clear(_sStereo.SSide);
            _sStereo.MidSideAmpQ0[0] = 0;
            _sStereo.MidSideAmpQ0[1] = 1;
            _sStereo.MidSideAmpQ0[2] = 0;
            _sStereo.MidSideAmpQ0[3] = 1;
            _sStereo.WidthPrevQ14 = 0;
            _sStereo.SmthWidthQ14 = 1 << 14;
            if (_nChannelsApi == 2)
            {
                s1.ResamplerState.CopyFrom(s0.ResamplerState);
                s0.InHpState.CopyTo(s1.InHpState, 0);
            }
        }

        bool transition = encControl.PayloadSizeMs != s0.PacketSizeMs || _nChannelsInternal != encControl.NChannelsInternal;
        _nChannelsApi = encControl.NChannelsApi;
        _nChannelsInternal = encControl.NChannelsInternal;
        int nBlocksOf10Ms = 100 * nSamplesIn / encControl.ApiSampleRate;
        int totBlocks = nBlocksOf10Ms > 1 ? nBlocksOf10Ms >> 1 : 1;
        int currBlock = 0;
        if (prefillFlag != 0)
        {
            if (nBlocksOf10Ms != 1)
            {
                return -101;
            }

            if (prefillFlag == 2)
            {
                _saveLp.InLpState[0] = s0.SLp.InLpState[0];
                _saveLp.InLpState[1] = s0.SLp.InLpState[1];
                _saveLp.TransitionFrameNo = s0.SLp.TransitionFrameNo;
                _saveLp.Mode = s0.SLp.Mode;
                _saveLp.SavedFsKhz = s0.FsKhz;
            }

            for (int n = 0; n < encControl.NChannelsInternal; n++)
            {
                _stateFxx[n].Init();
                if (prefillFlag == 2)
                {
                    _stateFxx[n].CopyLpStateFrom(_saveLp);
                }
            }

            tmpPayloadSizeMs = encControl.PayloadSizeMs;
            encControl.PayloadSizeMs = 10;
            tmpComplexity = encControl.Complexity;
            encControl.Complexity = 0;
            for (int n = 0; n < encControl.NChannelsInternal; n++)
            {
                _stateFxx[n].ControlledSinceLastPayload = 0;
                _stateFxx[n].PrefillFlag = 1;
            }
        }
        else
        {
            if (nBlocksOf10Ms * encControl.ApiSampleRate != 100 * nSamplesIn || nSamplesIn < 0)
            {
                return -101;
            }

            if (1000 * nSamplesIn > encControl.PayloadSizeMs * encControl.ApiSampleRate)
            {
                return -101;
            }
        }

        for (int n = 0; n < encControl.NChannelsInternal; n++)
        {
            int forceFsKhz = n == 1 ? s0.FsKhz : 0;
            if ((ret = _stateFxx[n].Control(encControl, _allowBandwidthSwitch, n, forceFsKhz)) != 0)
            {
                return ret;
            }

            if (_stateFxx[n].FirstFrameAfterReset != 0 || transition)
            {
                for (int i = 0; i < s0.NFramesPerPacket; i++)
                {
                    _stateFxx[n].LbrrFlags[i] = 0;
                }
            }

            _stateFxx[n].InDtx = _stateFxx[n].UseDtx;
        }

        int nSamplesToBufferMax = 10 * nBlocksOf10Ms * s0.FsKhz;
        Span<short> buf = _buf;
        int inPtr = 0;
        while (true)
        {
            int currNBitsUsedLbrr = 0;
            int nSamplesToBuffer = s0.FrameLength - s0.InputBufIx;
            nSamplesToBuffer = Math.Min(nSamplesToBuffer, nSamplesToBufferMax);
            int nSamplesFromInput = nSamplesToBuffer * s0.ApiFsHz / (s0.FsKhz * 1000);
            if (encControl.NChannelsApi == 2 && encControl.NChannelsInternal == 2)
            {
                int id = s0.NFramesEncoded;
                for (int n = 0; n < nSamplesFromInput; n++)
                {
                    buf[n] = samplesIn[inPtr + (2 * n)];
                }

                if (_nPrevChannelsInternal == 1 && id == 0)
                {
                    s1.ResamplerState.CopyFrom(s0.ResamplerState);
                }

                ret += s0.ResamplerState.Process(s0.InputBuf.AsSpan(s0.InputBufIx + 2), buf, nSamplesFromInput);
                s0.InputBufIx += nSamplesToBuffer;

                nSamplesToBuffer = s1.FrameLength - s1.InputBufIx;
                nSamplesToBuffer = Math.Min(nSamplesToBuffer, 10 * nBlocksOf10Ms * s1.FsKhz);
                for (int n = 0; n < nSamplesFromInput; n++)
                {
                    buf[n] = samplesIn[inPtr + (2 * n) + 1];
                }

                ret += s1.ResamplerState.Process(s1.InputBuf.AsSpan(s1.InputBufIx + 2), buf, nSamplesFromInput);
                s1.InputBufIx += nSamplesToBuffer;
            }
            else if (encControl.NChannelsApi == 2 && encControl.NChannelsInternal == 1)
            {
                for (int n = 0; n < nSamplesFromInput; n++)
                {
                    int sum = samplesIn[inPtr + (2 * n)] + samplesIn[inPtr + (2 * n) + 1];
                    buf[n] = (short)RShiftRound(sum, 1);
                }

                ret += s0.ResamplerState.Process(s0.InputBuf.AsSpan(s0.InputBufIx + 2), buf, nSamplesFromInput);
                if (_nPrevChannelsInternal == 2 && s0.NFramesEncoded == 0)
                {
                    ret += s1.ResamplerState.Process(s1.InputBuf.AsSpan(s1.InputBufIx + 2), buf, nSamplesFromInput);
                    for (int n = 0; n < s0.FrameLength; n++)
                    {
                        s0.InputBuf[s0.InputBufIx + n + 2] = (short)((s0.InputBuf[s0.InputBufIx + n + 2] + s1.InputBuf[s1.InputBufIx + n + 2]) >> 1);
                    }
                }

                s0.InputBufIx += nSamplesToBuffer;
            }
            else
            {
                samplesIn.Slice(inPtr, nSamplesFromInput).CopyTo(buf);
                ret += s0.ResamplerState.Process(s0.InputBuf.AsSpan(s0.InputBufIx + 2), buf, nSamplesFromInput);
                s0.InputBufIx += nSamplesToBuffer;
            }

            inPtr += nSamplesFromInput * encControl.NChannelsApi;
            nSamplesIn -= nSamplesFromInput;
            _allowBandwidthSwitch = 0;

            if (s0.InputBufIx >= s0.FrameLength)
            {
                if (s0.NFramesEncoded == 0 && prefillFlag == 0)
                {
                    // Room at the start of the payload for the VAD and LBRR flags, patched in at the end.
                    Span<byte> iCdf = [(byte)(256 - (256 >> ((s0.NFramesPerPacket + 1) * encControl.NChannelsInternal))), 0];
                    enc!.EncodeIcdf(0, iCdf, 8);
                    currNBitsUsedLbrr = enc!.Tell();

                    for (int n = 0; n < encControl.NChannelsInternal; n++)
                    {
                        SilkChannelEncoder sn = _stateFxx[n];
                        int lbrrSymbol = 0;
                        for (int i = 0; i < sn.NFramesPerPacket; i++)
                        {
                            lbrrSymbol |= LShift(sn.LbrrFlags[i], i);
                        }

                        sn.LbrrFlag = lbrrSymbol > 0 ? (sbyte)1 : (sbyte)0;
                        if (lbrrSymbol != 0 && sn.NFramesPerPacket > 1)
                        {
                            enc!.EncodeIcdf(lbrrSymbol - 1, sn.NFramesPerPacket == 2 ? SilkTables.LbrrFlags2Icdf : SilkTables.LbrrFlags3Icdf, 8);
                        }
                    }

                    for (int i = 0; i < s0.NFramesPerPacket; i++)
                    {
                        for (int n = 0; n < encControl.NChannelsInternal; n++)
                        {
                            SilkChannelEncoder sn = _stateFxx[n];
                            if (sn.LbrrFlags[i] != 0)
                            {
                                if (encControl.NChannelsInternal == 2 && n == 0)
                                {
                                    SilkEntropyEncoder.StereoEncodePred(enc!, _sStereo.PredIx.AsSpan(i * 6, 6));
                                    if (s1.LbrrFlags[i] == 0)
                                    {
                                        SilkEntropyEncoder.StereoEncodeMidOnly(enc!, _sStereo.MidOnlyFlags[i]);
                                    }
                                }

                                int condCoding = i > 0 && sn.LbrrFlags[i - 1] != 0 ? CodeConditionally : CodeIndependently;
                                sn.EncodeIndices(enc!, i, true, condCoding);
                                SilkEntropyEncoder.EncodePulses(enc!, sn.IndicesLbrr[i].SignalType, sn.IndicesLbrr[i].QuantOffsetType,
                                    sn.PulsesLbrr.AsSpan(i * MaxFrameLength, MaxFrameLength), sn.FrameLength);
                            }
                        }
                    }

                    for (int n = 0; n < encControl.NChannelsInternal; n++)
                    {
                        Array.Clear(_stateFxx[n].LbrrFlags);
                    }

                    currNBitsUsedLbrr = enc!.Tell() - currNBitsUsedLbrr;
                }

                s0.HpVariableCutoff();

                int nBits = encControl.BitRate * encControl.PayloadSizeMs / 1000;
                if (prefillFlag == 0)
                {
                    if (currNBitsUsedLbrr < 10)
                    {
                        _nBitsUsedLbrr = 0;
                    }
                    else if (_nBitsUsedLbrr < 10)
                    {
                        _nBitsUsedLbrr = currNBitsUsedLbrr;
                    }
                    else
                    {
                        _nBitsUsedLbrr = (_nBitsUsedLbrr + currNBitsUsedLbrr) / 2;
                    }

                    nBits -= _nBitsUsedLbrr;
                }

                nBits /= s0.NFramesPerPacket;
                int targetRateBps = encControl.PayloadSizeMs == 10 ? Smulbb(nBits, 100) : Smulbb(nBits, 50);
                targetRateBps -= _nBitsExceeded * 1000 / BitreservoirDecayTimeMs;
                if (prefillFlag == 0 && s0.NFramesEncoded > 0)
                {
                    int bitsBalance = enc!.Tell() - _nBitsUsedLbrr - (nBits * s0.NFramesEncoded);
                    targetRateBps -= bitsBalance * 1000 / BitreservoirDecayTimeMs;
                }

                targetRateBps = Limit(targetRateBps, encControl.BitRate, 5000);

                if (encControl.NChannelsInternal == 2)
                {
                    int frame = s0.NFramesEncoded;
                    SilkStereoEncoder.LrToMs(_sStereo, s0.InputBuf, s1.InputBuf, _sStereo.PredIx.AsSpan(frame * 6, 6), out _sStereo.MidOnlyFlags[frame],
                        mStargetRatesBps, targetRateBps, s0.SpeechActivityQ8, encControl.ToMono, s0.FsKhz, s0.FrameLength);
                    if (_sStereo.MidOnlyFlags[frame] == 0)
                    {
                        if (_prevDecodeOnlyMiddle == 1)
                        {
                            s1.ShapeLastGainIndex = 0;
                            s1.HarmShapeGainSmth = 0;
                            s1.TiltSmth = 0;
                            s1.SNsq.Clear();
                            Array.Clear(s1.PrevNlsfqQ15);
                            Array.Clear(s1.SLp.InLpState);
                            s1.PrevLag = 100;
                            s1.SNsq.LagPrev = 100;
                            s1.ShapeLastGainIndex = 10;
                            s1.PrevSignalType = TypeNoVoiceActivity;
                            s1.SNsq.PrevGainQ16 = 65536;
                            s1.FirstFrameAfterReset = 1;
                        }

                        s1.EncodeDoVad(activity);
                    }
                    else
                    {
                        s1.VadFlags[frame] = 0;
                    }

                    if (prefillFlag == 0)
                    {
                        SilkEntropyEncoder.StereoEncodePred(enc!, _sStereo.PredIx.AsSpan(frame * 6, 6));
                        if (s1.VadFlags[frame] == 0)
                        {
                            SilkEntropyEncoder.StereoEncodeMidOnly(enc!, _sStereo.MidOnlyFlags[frame]);
                        }
                    }
                }
                else
                {
                    _sStereo.SMid.CopyTo(s0.InputBuf, 0);
                    s0.InputBuf.AsSpan(s0.FrameLength, 2).CopyTo(_sStereo.SMid);
                }

                s0.EncodeDoVad(activity);

                for (int n = 0; n < encControl.NChannelsInternal; n++)
                {
                    SilkChannelEncoder sn = _stateFxx[n];
                    int maxBits = encControl.MaxBits;
                    if (totBlocks == 2 && currBlock == 0)
                    {
                        maxBits = maxBits * 3 / 5;
                    }
                    else if (totBlocks == 3)
                    {
                        if (currBlock == 0)
                        {
                            maxBits = maxBits * 2 / 5;
                        }
                        else if (currBlock == 1)
                        {
                            maxBits = maxBits * 3 / 4;
                        }
                    }

                    bool useCbr = encControl.UseCbr != 0 && currBlock == totBlocks - 1;
                    int channelRateBps;
                    if (encControl.NChannelsInternal == 1)
                    {
                        channelRateBps = targetRateBps;
                    }
                    else
                    {
                        channelRateBps = mStargetRatesBps[n];
                        if (n == 0 && mStargetRatesBps[1] > 0)
                        {
                            useCbr = false;
                            maxBits -= encControl.MaxBits / (totBlocks * 2);
                        }
                    }

                    if (channelRateBps > 0)
                    {
                        sn.ControlSnr(channelRateBps);
                        int condCoding;
                        if (s0.NFramesEncoded - n <= 0)
                        {
                            condCoding = CodeIndependently;
                        }
                        else if (n > 0 && _prevDecodeOnlyMiddle != 0)
                        {
                            condCoding = CodeIndependentlyNoLtpScaling;
                        }
                        else
                        {
                            condCoding = CodeConditionally;
                        }

                        nBytesOut = sn.EncodeFrame(enc!, condCoding, maxBits, useCbr);
                    }

                    sn.ControlledSinceLastPayload = 0;
                    sn.InputBufIx = 0;
                    sn.NFramesEncoded++;
                }

                _prevDecodeOnlyMiddle = _sStereo.MidOnlyFlags[s0.NFramesEncoded - 1];

                if (nBytesOut > 0 && s0.NFramesEncoded == s0.NFramesPerPacket)
                {
                    int flags = 0;
                    for (int n = 0; n < encControl.NChannelsInternal; n++)
                    {
                        SilkChannelEncoder sn = _stateFxx[n];
                        for (int i = 0; i < sn.NFramesPerPacket; i++)
                        {
                            flags = LShift(flags, 1);
                            flags |= (byte)sn.VadFlags[i];
                        }

                        flags = LShift(flags, 1);
                        flags |= (byte)sn.LbrrFlag;
                    }

                    if (prefillFlag == 0)
                    {
                        enc!.PatchInitialBits((uint)flags, (s0.NFramesPerPacket + 1) * encControl.NChannelsInternal);
                    }

                    if (s0.InDtx != 0 && (encControl.NChannelsInternal == 1 || s1.InDtx != 0))
                    {
                        nBytesOut = 0;
                    }

                    _nBitsExceeded += nBytesOut * 8;
                    _nBitsExceeded -= encControl.BitRate * encControl.PayloadSizeMs / 1000;
                    _nBitsExceeded = Limit(_nBitsExceeded, 0, 10000);

                    int speechActThrForSwitchQ8 = Smlawb(SilkEncoderQuant.FixConst(SpeechActivityDtxThres, 8),
                        SilkEncoderQuant.FixConst((1 - SpeechActivityDtxThres) / MaxBandwidthSwitchDelayMs, 16 + 8), _timeSinceSwitchAllowedMs);
                    if (s0.SpeechActivityQ8 < speechActThrForSwitchQ8)
                    {
                        _allowBandwidthSwitch = 1;
                        _timeSinceSwitchAllowedMs = 0;
                    }
                    else
                    {
                        _allowBandwidthSwitch = 0;
                        _timeSinceSwitchAllowedMs += encControl.PayloadSizeMs;
                    }
                }

                if (nSamplesIn == 0)
                {
                    break;
                }
            }
            else
            {
                break;
            }

            currBlock++;
        }

        _nPrevChannelsInternal = encControl.NChannelsInternal;
        encControl.AllowBandwidthSwitch = _allowBandwidthSwitch;
        encControl.InWbModeWithoutVariableLp = s0.FsKhz == 16 && s0.SLp.Mode == 0 ? 1 : 0;
        encControl.InternalSampleRate = Smulbb(s0.FsKhz, 1000);
        encControl.StereoWidthQ14 = encControl.ToMono != 0 ? 0 : _sStereo.SmthWidthQ14;
        if (prefillFlag != 0)
        {
            encControl.PayloadSizeMs = tmpPayloadSizeMs;
            encControl.Complexity = tmpComplexity;
            for (int n = 0; n < encControl.NChannelsInternal; n++)
            {
                _stateFxx[n].ControlledSinceLastPayload = 0;
                _stateFxx[n].PrefillFlag = 0;
            }
        }

        encControl.SignalType = s0.Indices.SignalType;
        encControl.Offset = SilkTables.QuantizationOffsetsQ10[((s0.Indices.SignalType >> 1) * 2) + s0.Indices.QuantOffsetType];
        return ret;
    }

    // check_control_input.
    private static int CheckControlInput(SilkEncControl c)
    {
        static bool IsInternal(int r) => r == 8000 || r == 12000 || r == 16000;
        int api = c.ApiSampleRate;
        if ((api != 8000 && api != 12000 && api != 16000 && api != 24000 && api != 32000 && api != 44100 && api != 48000) ||
            !IsInternal(c.DesiredInternalSampleRate) || !IsInternal(c.MaxInternalSampleRate) || !IsInternal(c.MinInternalSampleRate) ||
            c.MinInternalSampleRate > c.DesiredInternalSampleRate || c.MaxInternalSampleRate < c.DesiredInternalSampleRate ||
            c.MinInternalSampleRate > c.MaxInternalSampleRate)
        {
            return -102;
        }

        if (c.PayloadSizeMs != 10 && c.PayloadSizeMs != 20 && c.PayloadSizeMs != 40 && c.PayloadSizeMs != 60)
        {
            return -103;
        }

        if (c.PacketLossPercentage < 0 || c.PacketLossPercentage > 100)
        {
            return -105;
        }

        if (c.UseDtx < 0 || c.UseDtx > 1)
        {
            return -108;
        }

        if (c.UseCbr < 0 || c.UseCbr > 1)
        {
            return -109;
        }

        if (c.UseInBandFec < 0 || c.UseInBandFec > 1)
        {
            return -107;
        }

        if (c.NChannelsApi < 1 || c.NChannelsApi > EncoderNumChannels || c.NChannelsInternal < 1 || c.NChannelsInternal > EncoderNumChannels ||
            c.NChannelsInternal > c.NChannelsApi)
        {
            return -111;
        }

        if (c.Complexity < 0 || c.Complexity > 10)
        {
            return -106;
        }

        return 0;
    }
}
