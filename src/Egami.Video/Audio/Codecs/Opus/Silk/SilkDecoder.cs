// Translated from libopus 1.5.2 silk/dec_API.c, stereo_decode_pred.c and stereo_MS_to_LR.c (DRED, OSCE and deep PLC off).
using static Egami.Video.Audio.Codecs.Opus.SilkMath;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>silk_DecControlStruct: what the Opus layer tells the SILK decoder about a call.</summary>
internal sealed class SilkDecodeControl
{
    /// <summary>nChannelsAPI: 1 or 2.</summary>
    public int ChannelsApi;

    /// <summary>nChannelsInternal: 1 or 2.</summary>
    public int ChannelsInternal;

    /// <summary>API_sampleRate: 8000, 12000, 16000, 24000 or 48000.</summary>
    public int ApiSampleRate;

    /// <summary>internalSampleRate: 8000, 12000 or 16000.</summary>
    public int InternalSampleRate;

    /// <summary>payloadSize_ms: 10, 20, 40 or 60 (0 means a lost packet; 10 ms is assumed).</summary>
    public int PayloadSizeMs;

    /// <summary>prevPitchLag (output): the pitch lag of the last frame at 48 kHz, or 0 if it was not voiced.</summary>
    public int PrevPitchLag;
}

/// <summary>The SILK decoder (silk_decoder): two channel decoders, the stereo state and the API-rate output.</summary>
internal sealed class SilkDecoder
{
    public const int NoError = 0;
    public const int DecInvalidSamplingFrequency = -200;
    public const int DecPayloadTooLarge = -201;
    public const int DecPayloadError = -202;
    public const int DecInvalidFrameSize = -203;

    private const int DecoderNumChannels = 2;
    private const int MaxApiFsKhz = 48;
    private const int StereoInterpLenMs = 8;

    private readonly SilkChannelDecoder[] _channelState = [new SilkChannelDecoder(), new SilkChannelDecoder()];

    // stereo_dec_state.
    private readonly short[] _predPrevQ13 = new short[2];
    private readonly short[] _sMid = new short[2];
    private readonly short[] _sSide = new short[2];
    private int _nChannelsApi;
    private int _nChannelsInternal;
    private int _prevDecodeOnlyMiddle;

    private readonly short[] _samplesOut1 = new short[2 * (SilkChannelDecoder.MaxFrameLength + 2)];
    private readonly short[] _samplesOut2 = new short[MaxApiFsKhz * 20];
    private readonly short[] _lbrrPulses = new short[SilkChannelDecoder.MaxFrameLength];

    /// <summary>silk_Get_Decoder_Size and silk_InitDecoder.</summary>
    public SilkDecoder()
    {
        Init();
    }

    /// <summary>The channel decoders (mid and side), for tests.</summary>
    internal SilkChannelDecoder this[int channel] => _channelState[channel];

    /// <summary>silk_InitDecoder.</summary>
    private void Init()
    {
        for (int n = 0; n < DecoderNumChannels; n++)
        {
            _channelState[n].Init();
        }

        ClearStereo();
        _prevDecodeOnlyMiddle = 0;
    }

    /// <summary>silk_ResetDecoder, as OPUS_RESET_STATE calls it. With OSCE off it clears as much as silk_InitDecoder.</summary>
    public void Reset()
    {
        for (int n = 0; n < DecoderNumChannels; n++)
        {
            _channelState[n].Reset();
        }

        ClearStereo();
        _prevDecodeOnlyMiddle = 0;
    }

    private void ClearStereo()
    {
        Array.Clear(_predPrevQ13);
        Array.Clear(_sMid);
        Array.Clear(_sSide);
    }

    /// <summary>
    /// silk_Decode: decodes one SILK frame (or conceals it) into <paramref name="samplesOut"/>, interleaved 16-bit samples at
    /// the API rate. <paramref name="lostFlag"/> is 0 for normal decoding, 1 for a lost packet and 2 for FEC (LBRR) data.
    /// Returns 0 or a negative SILK error code.
    /// </summary>
    public int Decode(SilkDecodeControl control, int lostFlag, bool newPacket, OpusRangeDecoder rangeDecoder, Span<short> samplesOut, out int samplesPerChannel)
    {
        int decodeOnlyMiddle = 0;
        int ret = NoError;
        int nSamplesOutDec = 0;
        Span<int> msPredQ13 = stackalloc int[2];
        SilkChannelDecoder[] channelState = _channelState;
        samplesPerChannel = 0;

        if (newPacket)
        {
            for (int n = 0; n < control.ChannelsInternal; n++)
            {
                channelState[n].NFramesDecoded = 0;
            }
        }

        if (control.ChannelsInternal > _nChannelsInternal)
        {
            channelState[1].Init();
        }

        bool stereoToMono = control.ChannelsInternal == 1 && _nChannelsInternal == 2 &&
            control.InternalSampleRate == 1000 * channelState[0].FsKhz;

        if (channelState[0].NFramesDecoded == 0)
        {
            for (int n = 0; n < control.ChannelsInternal; n++)
            {
                SilkChannelDecoder ch = channelState[n];
                switch (control.PayloadSizeMs)
                {
                    case 0:
                    case 10:
                        ch.NFramesPerPacket = 1;
                        ch.NbSubfr = 2;
                        break;
                    case 20:
                        ch.NFramesPerPacket = 1;
                        ch.NbSubfr = 4;
                        break;
                    case 40:
                        ch.NFramesPerPacket = 2;
                        ch.NbSubfr = 4;
                        break;
                    case 60:
                        ch.NFramesPerPacket = 3;
                        ch.NbSubfr = 4;
                        break;
                    default:
                        return DecInvalidFrameSize;
                }

                int fsKhzDec = (control.InternalSampleRate >> 10) + 1;
                if (fsKhzDec != 8 && fsKhzDec != 12 && fsKhzDec != 16)
                {
                    return DecInvalidSamplingFrequency;
                }

                ret += ch.SetFs(fsKhzDec, control.ApiSampleRate);
            }
        }

        if (control.ChannelsApi == 2 && control.ChannelsInternal == 2 && (_nChannelsApi == 1 || _nChannelsInternal == 1))
        {
            Array.Clear(_predPrevQ13);
            Array.Clear(_sSide);
            channelState[1].ResamplerState.CopyFrom(channelState[0].ResamplerState);
        }

        _nChannelsApi = control.ChannelsApi;
        _nChannelsInternal = control.ChannelsInternal;

        if (control.ApiSampleRate > MaxApiFsKhz * 1000 || control.ApiSampleRate < 8000)
        {
            return DecInvalidSamplingFrequency;
        }

        if (lostFlag != SilkChannelDecoder.FlagPacketLost && channelState[0].NFramesDecoded == 0)
        {
            for (int n = 0; n < control.ChannelsInternal; n++)
            {
                SilkChannelDecoder ch = channelState[n];
                for (int i = 0; i < ch.NFramesPerPacket; i++)
                {
                    ch.VadFlags[i] = rangeDecoder.DecodeBitLogp(1) ? 1 : 0;
                }

                ch.LbrrFlag = rangeDecoder.DecodeBitLogp(1) ? 1 : 0;
            }

            for (int n = 0; n < control.ChannelsInternal; n++)
            {
                SilkChannelDecoder ch = channelState[n];
                Array.Clear(ch.LbrrFlags);
                if (ch.LbrrFlag != 0)
                {
                    if (ch.NFramesPerPacket == 1)
                    {
                        ch.LbrrFlags[0] = 1;
                    }
                    else
                    {
                        ReadOnlySpan<byte> icdf = ch.NFramesPerPacket == 2 ? SilkTables.LbrrFlags2Icdf : SilkTables.LbrrFlags3Icdf;
                        int lbrrSymbol = rangeDecoder.DecodeIcdf(icdf, 8) + 1;
                        for (int i = 0; i < ch.NFramesPerPacket; i++)
                        {
                            ch.LbrrFlags[i] = (lbrrSymbol >> i) & 1;
                        }
                    }
                }
            }

            if (lostFlag == SilkChannelDecoder.FlagDecodeNormal)
            {
                // Regular decoding: skip all LBRR data.
                for (int i = 0; i < channelState[0].NFramesPerPacket; i++)
                {
                    for (int n = 0; n < control.ChannelsInternal; n++)
                    {
                        SilkChannelDecoder ch = channelState[n];
                        if (ch.LbrrFlags[i] != 0)
                        {
                            if (control.ChannelsInternal == 2 && n == 0)
                            {
                                StereoDecodePred(rangeDecoder, msPredQ13);
                                if (channelState[1].LbrrFlags[i] == 0)
                                {
                                    decodeOnlyMiddle = StereoDecodeMidOnly(rangeDecoder);
                                }
                            }

                            int condCoding = i > 0 && ch.LbrrFlags[i - 1] != 0
                                ? SilkChannelDecoder.CodeConditionally
                                : SilkChannelDecoder.CodeIndependently;
                            ch.DecodeIndices(rangeDecoder, i, true, condCoding);
                            SilkChannelDecoder.DecodePulses(rangeDecoder, _lbrrPulses, ch.Indices.SignalType, ch.Indices.QuantOffsetType, ch.FrameLength);
                        }
                    }
                }
            }
        }

        if (control.ChannelsInternal == 2)
        {
            int frame = channelState[0].NFramesDecoded;
            if (lostFlag == SilkChannelDecoder.FlagDecodeNormal ||
                (lostFlag == SilkChannelDecoder.FlagDecodeLbrr && channelState[0].LbrrFlags[frame] == 1))
            {
                StereoDecodePred(rangeDecoder, msPredQ13);
                if ((lostFlag == SilkChannelDecoder.FlagDecodeNormal && channelState[1].VadFlags[frame] == 0) ||
                    (lostFlag == SilkChannelDecoder.FlagDecodeLbrr && channelState[1].LbrrFlags[frame] == 0))
                {
                    decodeOnlyMiddle = StereoDecodeMidOnly(rangeDecoder);
                }
                else
                {
                    decodeOnlyMiddle = 0;
                }
            }
            else
            {
                for (int n = 0; n < 2; n++)
                {
                    msPredQ13[n] = _predPrevQ13[n];
                }
            }
        }

        if (control.ChannelsInternal == 2 && decodeOnlyMiddle == 0 && _prevDecodeOnlyMiddle == 1)
        {
            SilkChannelDecoder side = channelState[1];
            Array.Clear(side.OutBuf);
            Array.Clear(side.SLpcQ14Buf);
            side.LagPrev = 100;
            side.LastGainIndex = 10;
            side.PrevSignalType = SilkChannelDecoder.TypeNoVoiceActivity;
            side.FirstFrameAfterReset = 1;
        }

        // samplesOut1_tmp: two buffers of frame_length + 2 samples; the first two hold the previous frame's last samples.
        int stride = channelState[0].FrameLength + 2;
        Span<short> out1 = _samplesOut1;
        Span<short> out1Mid = out1[..stride];
        Span<short> out1Side = out1.Slice(stride, stride);

        bool hasSide;
        if (lostFlag == SilkChannelDecoder.FlagDecodeNormal)
        {
            hasSide = decodeOnlyMiddle == 0;
        }
        else
        {
            hasSide = _prevDecodeOnlyMiddle == 0 ||
                (control.ChannelsInternal == 2 && lostFlag == SilkChannelDecoder.FlagDecodeLbrr &&
                 channelState[1].LbrrFlags[channelState[1].NFramesDecoded] == 1);
        }

        for (int n = 0; n < control.ChannelsInternal; n++)
        {
            Span<short> target = n == 0 ? out1Mid : out1Side;
            if (n == 0 || hasSide)
            {
                int frameIndex = channelState[0].NFramesDecoded - n;
                int condCoding;
                if (frameIndex <= 0)
                {
                    condCoding = SilkChannelDecoder.CodeIndependently;
                }
                else if (lostFlag == SilkChannelDecoder.FlagDecodeLbrr)
                {
                    condCoding = channelState[n].LbrrFlags[frameIndex - 1] != 0
                        ? SilkChannelDecoder.CodeConditionally
                        : SilkChannelDecoder.CodeIndependently;
                }
                else if (n > 0 && _prevDecodeOnlyMiddle != 0)
                {
                    condCoding = SilkChannelDecoder.CodeIndependentlyNoLtpScaling;
                }
                else
                {
                    condCoding = SilkChannelDecoder.CodeConditionally;
                }

                ret += channelState[n].DecodeFrame(rangeDecoder, target[2..], out nSamplesOutDec, lostFlag, condCoding);
            }
            else
            {
                target.Slice(2, nSamplesOutDec).Clear();
            }

            channelState[n].NFramesDecoded++;
        }

        if (control.ChannelsApi == 2 && control.ChannelsInternal == 2)
        {
            StereoMsToLr(out1Mid, out1Side, msPredQ13, channelState[0].FsKhz, nSamplesOutDec);
        }
        else
        {
            _sMid.CopyTo(out1Mid);
            out1Mid.Slice(nSamplesOutDec, 2).CopyTo(_sMid);
        }

        int nSamplesOut = nSamplesOutDec * control.ApiSampleRate / Smulbb(channelState[0].FsKhz, 1000);
        samplesPerChannel = nSamplesOut;

        Span<short> resampleOut = control.ChannelsApi == 2 ? _samplesOut2.AsSpan(0, nSamplesOut) : samplesOut;
        int channels = Math.Min(control.ChannelsApi, control.ChannelsInternal);
        for (int n = 0; n < channels; n++)
        {
            Span<short> source = n == 0 ? out1Mid : out1Side;
            ret += channelState[n].ResamplerState.Process(resampleOut, source[1..], nSamplesOutDec);
            if (control.ChannelsApi == 2)
            {
                for (int i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[n + (2 * i)] = resampleOut[i];
                }
            }
        }

        if (control.ChannelsApi == 2 && control.ChannelsInternal == 1)
        {
            if (stereoToMono)
            {
                // Resample the right channel too, in case collapsing to mono started without it.
                ret += channelState[1].ResamplerState.Process(resampleOut, out1Mid[1..], nSamplesOutDec);
                for (int i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[1 + (2 * i)] = resampleOut[i];
                }
            }
            else
            {
                for (int i = 0; i < nSamplesOut; i++)
                {
                    samplesOut[1 + (2 * i)] = samplesOut[2 * i];
                }
            }
        }

        if (channelState[0].PrevSignalType == SilkChannelDecoder.TypeVoiced)
        {
            ReadOnlySpan<byte> multTab = [6, 4, 3];
            control.PrevPitchLag = channelState[0].LagPrev * multTab[(channelState[0].FsKhz - 8) >> 2];
        }
        else
        {
            control.PrevPitchLag = 0;
        }

        if (lostFlag == SilkChannelDecoder.FlagPacketLost)
        {
            // On packet loss, remove the gain clamping so the energy does not bounce back when it is going down.
            for (int i = 0; i < _nChannelsInternal; i++)
            {
                channelState[i].LastGainIndex = 10;
            }
        }
        else
        {
            _prevDecodeOnlyMiddle = decodeOnlyMiddle;
        }

        return ret;
    }

    /// <summary>silk_stereo_decode_pred: the mid/side predictors.</summary>
    public static void StereoDecodePred(OpusRangeDecoder rangeDecoder, Span<int> predQ13)
    {
        Span<int> ix = stackalloc int[6];
        int n = rangeDecoder.DecodeIcdf(SilkTables.StereoPredJointIcdf, 8);
        ix[2] = n / 5;
        ix[5] = n - (5 * ix[2]);
        for (n = 0; n < 2; n++)
        {
            ix[(n * 3) + 0] = rangeDecoder.DecodeIcdf(SilkTables.Uniform3Icdf, 8);
            ix[(n * 3) + 1] = rangeDecoder.DecodeIcdf(SilkTables.Uniform5Icdf, 8);
        }

        ReadOnlySpan<short> quant = SilkTables.StereoPredQuantQ13;
        for (n = 0; n < 2; n++)
        {
            ix[(n * 3) + 0] += 3 * ix[(n * 3) + 2];
            int lowQ13 = quant[ix[n * 3]];
            int stepQ13 = Smulwb(quant[ix[n * 3] + 1] - lowQ13, 6554);
            predQ13[n] = Smlabb(lowQ13, stepQ13, (2 * ix[(n * 3) + 1]) + 1);
        }

        predQ13[0] -= predQ13[1];
    }

    /// <summary>silk_stereo_decode_mid_only: the flag that only the mid channel is coded.</summary>
    public static int StereoDecodeMidOnly(OpusRangeDecoder rangeDecoder) => rangeDecoder.DecodeIcdf(SilkTables.StereoOnlyCodeMidIcdf, 8);

    /// <summary>silk_stereo_MS_to_LR: converts mid/side to left/right in place; both buffers have two leading samples.</summary>
    private void StereoMsToLr(Span<short> x1, Span<short> x2, ReadOnlySpan<int> predQ13, int fsKhz, int frameLength)
    {
        _sMid.CopyTo(x1);
        _sSide.CopyTo(x2);
        x1.Slice(frameLength, 2).CopyTo(_sMid);
        x2.Slice(frameLength, 2).CopyTo(_sSide);

        unchecked
        {
            int pred0Q13 = _predPrevQ13[0];
            int pred1Q13 = _predPrevQ13[1];
            int denomQ16 = (1 << 16) / (StereoInterpLenMs * fsKhz);
            int delta0Q13 = RShiftRound(Smulbb(predQ13[0] - _predPrevQ13[0], denomQ16), 16);
            int delta1Q13 = RShiftRound(Smulbb(predQ13[1] - _predPrevQ13[1], denomQ16), 16);
            int n;
            for (n = 0; n < StereoInterpLenMs * fsKhz; n++)
            {
                pred0Q13 += delta0Q13;
                pred1Q13 += delta1Q13;
                int sum = LShift(AddLShift32(x1[n] + x1[n + 2], x1[n + 1], 1), 9);
                sum = Smlawb(LShift(x2[n + 1], 8), sum, pred0Q13);
                sum = Smlawb(sum, LShift(x1[n + 1], 11), pred1Q13);
                x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
            }

            pred0Q13 = predQ13[0];
            pred1Q13 = predQ13[1];
            for (n = StereoInterpLenMs * fsKhz; n < frameLength; n++)
            {
                int sum = LShift(AddLShift32(x1[n] + x1[n + 2], x1[n + 1], 1), 9);
                sum = Smlawb(LShift(x2[n + 1], 8), sum, pred0Q13);
                sum = Smlawb(sum, LShift(x1[n + 1], 11), pred1Q13);
                x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
            }

            _predPrevQ13[0] = (short)predQ13[0];
            _predPrevQ13[1] = (short)predQ13[1];

            for (n = 0; n < frameLength; n++)
            {
                int sum = x1[n + 1] + x2[n + 1];
                int diff = x1[n + 1] - x2[n + 1];
                x1[n + 1] = (short)Sat16(sum);
                x2[n + 1] = (short)Sat16(diff);
            }
        }
    }
}
