using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Alac;

/// <summary>
/// Encodes ALAC, translated from Apple's ALACEncoder.cpp and matrix_enc.c (Apache-2.0) so that packets come out byte for
/// byte as Apple's encoder makes them: level 1 (the default) searches the stereo matrix and the predictor order, level 0 is
/// Apple's fast mode, which keeps the defaults for pairs (Apple's encoder has no fast mode for single channels or for more
/// than two). The encoder keeps its predictor state and buffers from frame to frame, as Apple's does, since what is left in
/// them feeds its searches.
/// </summary>
internal sealed class AlacEncoder : IAudioEncoder
{
    private const int MaxCoefs = 16;
    private const int MaxSearches = 16;
    private const int DefaultMixBits = 2;
    private const int DefaultMixRes = 0;
    private const int MaxRes = 4;
    private const int DefaultNumUV = 8;
    private const int MinUV = 4;
    private const int MaxUV = 8;
    private const int PbFactor = 4;

    private const int IdSce = 0;
    private const int IdCpe = 1;
    private const int IdEnd = 7;

    /// <summary>The elements of each channel count, one per stream channel (a pair counts on its first channel): Apple's sChannelMaps.</summary>
    private static readonly int[][] s_elements =
    [
        [IdSce],
        [IdCpe, -1],
        [IdSce, IdCpe, -1],
        [IdSce, IdCpe, -1, IdSce],
        [IdSce, IdCpe, -1, IdCpe, -1],
        [IdSce, IdCpe, -1, IdCpe, -1, IdSce],
        [IdSce, IdCpe, -1, IdCpe, -1, IdSce, IdSce],
        [IdSce, IdCpe, -1, IdCpe, -1, IdCpe, -1, IdSce],
    ];

    private readonly int _bitDepth;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly bool _fast;
    private readonly int[] _order;
    private readonly AudioChunker _chunker;
    private readonly AlacBitWriter _writer;
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly int[][] _input;
    private readonly int[] _mixU;
    private readonly int[] _mixV;
    private readonly int[] _predictorU;
    private readonly int[] _predictorV;
    private readonly ushort[] _shiftUV;
    private readonly short[][][] _coefsU;
    private readonly short[][][] _coefsV;
    private readonly int[] _lastMixRes = new int[8];
    private long _totalBytes;
    private long _totalSamples;
    private int _maxFrameBytes;

    public AlacEncoder(AudioEncoderOptions options, MediaContext context)
    {
        int level = options.Level ?? 1;
        if (level is < 0 or > 1)
        {
            throw new NotSupportedException($"ALAC levels are 0 (fast) and 1 (normal), not {level}.");
        }

        _bitDepth = options.BitsPerSample > 0 ? options.BitsPerSample : 16;
        if (_bitDepth is not (16 or 20 or 24 or 32))
        {
            throw new NotSupportedException($"ALAC stores 16, 20, 24 or 32 bits per sample, not {_bitDepth}.");
        }

        _channels = options.Layout.ChannelCount;
        if (_channels is < 1 or > 8)
        {
            throw new NotSupportedException($"ALAC stores 1 to 8 channels, not {_channels}.");
        }

        _sampleRate = options.SampleRate;
        if (_sampleRate < 1)
        {
            throw new NotSupportedException($"The sample rate {_sampleRate} is not valid.");
        }

        FrameSize = options.FrameSize > 0 ? options.FrameSize : 4096;
        if (FrameSize is < 16 or > 65536)
        {
            throw new NotSupportedException($"ALAC frames hold 16 to 65536 samples here, not {FrameSize}.");
        }

        _fast = level == 0;
        (Layout, int[] streamToEgami) = AlacDecoder.Layout(_channels);
        _order = streamToEgami;
        _chunker = new AudioChunker(SampleFormat.S32Planar, _bitDepth, Layout, _sampleRate, FrameSize, context);
        _writer = new AlacBitWriter((FrameSize * _channels * ((10 + 32) / 8)) + 1);
        int length = FrameSize + 32;
        _input = [.. Enumerable.Range(0, _channels).Select(_ => new int[length])];
        _mixU = new int[length];
        _mixV = new int[length];
        _predictorU = new int[length];
        _predictorV = new int[length];
        _shiftUV = new ushort[2 * length];
        _coefsU = NewCoefs();
        _coefsV = NewCoefs();
        Track = MakeTrack();
    }

    public AudioTrackInfo Track { get; private set; }

    public int FrameSize { get; }

    /// <summary>Gets the channel layout: ALAC's fixed one for the channel count, which the input is mixed to.</summary>
    private ChannelLayout Layout { get; }

    public void Send(AudioFrame frame)
    {
        _chunker.Push(frame);
        Drain();
    }

    public void Flush()
    {
        _chunker.Flush();
        Drain();
        Track = MakeTrack();
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet) => _ready.TryDequeue(out packet);

    public void Dispose()
    {
        _chunker.Dispose();
        while (_ready.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }

    private short[][][] NewCoefs()
    {
        short[][][] coefs = new short[_channels][][];
        for (int c = 0; c < _channels; c++)
        {
            coefs[c] = new short[MaxSearches][];
            for (int s = 0; s < MaxSearches; s++)
            {
                coefs[c][s] = new short[MaxCoefs];
                AlacPredictor.InitCoefficients(coefs[c][s]);
            }
        }

        return coefs;
    }

    private AudioTrackInfo MakeTrack()
    {
        uint avgBitRate = _totalSamples > 0 ? (uint)Math.Min(uint.MaxValue, Math.Round(_totalBytes * 8.0 * _sampleRate / _totalSamples)) : 0;
        AlacSpecificConfig config = new(
            (uint)FrameSize,
            0,
            _bitDepth,
            AlacAdaptiveGolomb.Pb0,
            AlacAdaptiveGolomb.Mb0,
            AlacAdaptiveGolomb.Kb0,
            _channels,
            AlacAdaptiveGolomb.MaxRunDefault,
            (uint)_maxFrameBytes,
            avgBitRate,
            (uint)_sampleRate);
        return new AudioTrackInfo
        {
            Number = 1,
            Kind = TrackKind.Audio,
            CodecId = CodecIds.Alac,
            CodecPrivate = config.Write(),
            TimeBase = new Rational(1, _sampleRate),
            Duration = _totalSamples > 0 ? new MediaTime(_totalSamples, new Rational(1, _sampleRate)) : null,
            SampleRate = _sampleRate,
            Channels = _channels,
            BitsPerSample = _bitDepth,
            Layout = Layout,
        };
    }

    private void Drain()
    {
        while (_chunker.TryTake(out AudioFrame? frame))
        {
            using (frame)
            {
                int n = frame.SampleCount;
                for (int c = 0; c < _channels; c++)
                {
                    frame.AsSpan<int>(_order[c]).CopyTo(_input[c]);
                }

                ReadOnlySpan<byte> bytes = Encode(n);
                EncodedPacket packet = EncodedPacket.Rent(1, bytes.Length, frame.Timestamp);
                bytes.CopyTo(packet.GetWritableData().Span);
                packet.Duration = new MediaTime(n, new Rational(1, _sampleRate));
                packet.IsKeyframe = true;
                _ready.Enqueue(packet);
                _totalSamples += n;
                _totalBytes += bytes.Length;
                _maxFrameBytes = Math.Max(_maxFrameBytes, bytes.Length);
            }
        }
    }

    /// <summary>Encodes one frame of <paramref name="n"/> samples from <see cref="_input"/> (ALACEncoder::Encode).</summary>
    private ReadOnlySpan<byte> Encode(int n)
    {
        AlacBitWriter w = _writer;
        w.Reset();
        if (_channels == 2)
        {
            w.Write(IdCpe, 3);
            w.Write(0, 4);
            if (_fast)
            {
                EncodeStereoFast(w, 0, n);
            }
            else
            {
                EncodeStereo(w, 0, n);
            }
        }
        else if (_channels == 1)
        {
            w.Write(IdSce, 3);
            w.Write(0, 4);
            EncodeMono(w, 0, n);
        }
        else
        {
            int[] elements = s_elements[_channels - 1];
            uint stereoTag = 0, monoTag = 0;
            for (int c = 0; c < _channels;)
            {
                int tag = elements[c];
                w.Write((uint)tag, 3);
                if (tag == IdSce)
                {
                    w.Write(monoTag++, 4);
                    EncodeMono(w, c, n);
                    c++;
                }
                else
                {
                    w.Write(stereoTag++, 4);
                    EncodeStereo(w, c, n);
                    c += 2;
                }
            }
        }

        w.Write(IdEnd, 3);
        w.AlignWithZeros();
        return w.Written;
    }

    private int BytesShifted => _bitDepth == 32 ? 2 : _bitDepth >= 24 ? 1 : 0;

    /// <summary>
    /// Splits a pair into the mix buffers (mix16 to mix32): the low bytes go to the shift buffer, then the generalized
    /// middle-side matrix u = [(r L + (2^m - r) R) / 2^m], v = L - R, or the channels as they are when r is 0.
    /// </summary>
    private void Mix(int channel, int count, int mixBits, int mixRes, int bytesShifted)
    {
        int[] left = _input[channel], right = _input[channel + 1];
        int shift = bytesShifted * 8;
        int mask = (1 << shift) - 1;
        int m2 = (1 << mixBits) - mixRes;
        for (int j = 0, k = 0; j < count; j++, k += 2)
        {
            int l = left[j], r = right[j];
            if (bytesShifted != 0)
            {
                _shiftUV[k] = (ushort)(l & mask);
                _shiftUV[k + 1] = (ushort)(r & mask);
                l >>= shift;
                r >>= shift;
            }

            if (mixRes != 0)
            {
                _mixU[j] = ((mixRes * l) + (m2 * r)) >> mixBits;
                _mixV[j] = l - r;
            }
            else
            {
                _mixU[j] = l;
                _mixV[j] = r;
            }
        }
    }

    private void EncodeStereo(AlacBitWriter w, int channel, int n)
    {
        short[][] coefsU = _coefsU[channel], coefsV = _coefsV[channel];
        int bytesShifted = BytesShifted;
        int chanBits = _bitDepth - (bytesShifted * 8) + 1;
        bool partial = n != FrameSize;

        // Find the best matrix on an eighth of the frame.
        int dilate = 8;
        long minBits1 = 1L << 31;
        int bestRes = _lastMixRes[channel];
        for (int mixRes = 0; mixRes <= MaxRes; mixRes++)
        {
            Mix(channel, n / dilate, DefaultMixBits, mixRes, bytesShifted);
            AlacPredictor.Predict(_mixU, _predictorU, n / dilate, coefsU[DefaultNumUV - 1], DefaultNumUV, chanBits, AlacPredictor.DenShiftDefault);
            AlacPredictor.Predict(_mixV, _predictorV, n / dilate, coefsV[DefaultNumUV - 1], DefaultNumUV, chanBits, AlacPredictor.DenShiftDefault);
            int bits1 = AlacAdaptiveGolomb.Count(_predictorU, n / dilate, chanBits);
            int bits2 = AlacAdaptiveGolomb.Count(_predictorV, n / dilate, chanBits);
            if (bits1 + bits2 < minBits1)
            {
                minBits1 = bits1 + bits2;
                bestRes = mixRes;
            }
        }

        _lastMixRes[channel] = bestRes;
        int res = bestRes;
        Mix(channel, n, DefaultMixBits, res, bytesShifted);

        // Then the predictor order, letting the coefficients converge on a thirty-second of the frame first.
        int numU = MinUV, numV = MinUV;
        minBits1 = 1L << 31;
        long minBits2 = 1L << 31;
        for (int numUV = MinUV; numUV <= MaxUV; numUV += 4)
        {
            dilate = 32;
            for (int converge = 0; converge < 8; converge++)
            {
                AlacPredictor.Predict(_mixU, _predictorU, n / dilate, coefsU[numUV - 1], numUV, chanBits, AlacPredictor.DenShiftDefault);
                AlacPredictor.Predict(_mixV, _predictorV, n / dilate, coefsV[numUV - 1], numUV, chanBits, AlacPredictor.DenShiftDefault);
            }

            dilate = 8;
            long bits1 = AlacAdaptiveGolomb.Count(_predictorU, n / dilate, chanBits);
            if ((bits1 * dilate) + (16 * numUV) < minBits1)
            {
                minBits1 = (bits1 * dilate) + (16 * numUV);
                numU = numUV;
            }

            long bits2 = AlacAdaptiveGolomb.Count(_predictorV, n / dilate, chanBits);
            if ((bits2 * dilate) + (16 * numUV) < minBits2)
            {
                minBits2 = (bits2 * dilate) + (16 * numUV);
                numV = numUV;
            }
        }

        long minBits = minBits1 + minBits2 + (8 * 8) + (partial ? 32 : 0);
        if (bytesShifted != 0)
        {
            minBits += (long)n * (bytesShifted * 8) * 2;
        }

        long escapeBits = ((long)n * _bitDepth * 2) + (partial ? 32 : 0) + (2 * 8);
        bool escape = minBits >= escapeBits;
        if (!escape)
        {
            int start = w.Position;
            WritePairHeader(w, n, partial, bytesShifted, res, coefsU[numU - 1], numU, coefsV[numV - 1], numV);
            AlacPredictor.Predict(_mixU, _predictorU, n, coefsU[numU - 1], numU, chanBits, AlacPredictor.DenShiftDefault);
            AlacAdaptiveGolomb.Compress(_predictorU, w, n, chanBits);
            AlacPredictor.Predict(_mixV, _predictorV, n, coefsV[numV - 1], numV, chanBits, AlacPredictor.DenShiftDefault);
            AlacAdaptiveGolomb.Compress(_predictorV, w, n, chanBits);

            // Compressed after all bigger than raw: write it raw.
            if (w.Position - start >= escapeBits)
            {
                w.Position = start;
                escape = true;
            }
        }

        if (escape)
        {
            EncodeStereoEscape(w, channel, n, partial);
        }
    }

    private void EncodeStereoFast(AlacBitWriter w, int channel, int n)
    {
        short[][] coefsU = _coefsU[channel], coefsV = _coefsV[channel];
        int bytesShifted = BytesShifted;
        int chanBits = _bitDepth - (bytesShifted * 8) + 1;
        bool partial = n != FrameSize;
        const int numU = DefaultNumUV, numV = DefaultNumUV;
        Mix(channel, n, DefaultMixBits, DefaultMixRes, bytesShifted);

        // Written as if compression wins; rewound if it does not.
        int start = w.Position;
        WritePairHeader(w, n, partial, bytesShifted, DefaultMixRes, coefsU[numU - 1], numU, coefsV[numV - 1], numV);
        AlacPredictor.Predict(_mixU, _predictorU, n, coefsU[numU - 1], numU, chanBits, AlacPredictor.DenShiftDefault);
        int before = w.Position;
        AlacAdaptiveGolomb.Compress(_predictorU, w, n, chanBits);
        long bits1 = w.Position - before;
        AlacPredictor.Predict(_mixV, _predictorV, n, coefsV[numV - 1], numV, chanBits, AlacPredictor.DenShiftDefault);
        before = w.Position;
        AlacAdaptiveGolomb.Compress(_predictorV, w, n, chanBits);
        long bits2 = w.Position - before;

        long minBits = bits1 + (numU * 16) + bits2 + (numV * 16) + (8 * 8) + (partial ? 32 : 0);
        if (bytesShifted != 0)
        {
            minBits += (long)n * (bytesShifted * 8) * 2;
        }

        long escapeBits = ((long)n * _bitDepth * 2) + (partial ? 32 : 0) + (2 * 8);
        bool escape = minBits >= escapeBits || w.Position - start >= escapeBits;
        if (escape)
        {
            w.Position = start;
            EncodeStereoEscape(w, channel, n, partial);
        }
    }

    private void WritePairHeader(AlacBitWriter w, int n, bool partial, int bytesShifted, int mixRes, short[] coefsU, int numU, short[] coefsV, int numV)
    {
        w.Write(0, 12);
        w.Write((uint)(((partial ? 1 : 0) << 3) | (bytesShifted << 1)), 4);
        if (partial)
        {
            w.Write((uint)n, 32);
        }

        w.Write(DefaultMixBits, 8);
        w.Write((uint)mixRes, 8);
        w.Write(AlacPredictor.DenShiftDefault, 8);
        w.Write((uint)((PbFactor << 5) | numU), 8);
        for (int i = 0; i < numU; i++)
        {
            w.Write((uint)coefsU[i], 16);
        }

        w.Write(AlacPredictor.DenShiftDefault, 8);
        w.Write((uint)((PbFactor << 5) | numV), 8);
        for (int i = 0; i < numV; i++)
        {
            w.Write((uint)coefsV[i], 16);
        }

        if (bytesShifted != 0)
        {
            int bitShift = bytesShifted * 8;
            for (int i = 0; i < n * 2; i += 2)
            {
                w.Write(((uint)_shiftUV[i] << bitShift) | _shiftUV[i + 1], bitShift * 2);
            }
        }
    }

    private void EncodeStereoEscape(AlacBitWriter w, int channel, int n, bool partial)
    {
        w.Write(0, 12);
        w.Write((uint)(((partial ? 1 : 0) << 3) | 1), 4);
        if (partial)
        {
            w.Write((uint)n, 32);
        }

        int[] left = _input[channel], right = _input[channel + 1];
        if (_bitDepth is 20 or 24)
        {
            // Apple runs these depths through the mix buffers on the way out; what it leaves there feeds later searches.
            Mix(channel, n, 0, 0, 0);
        }

        for (int i = 0; i < n; i++)
        {
            w.Write((uint)left[i], _bitDepth);
            w.Write((uint)right[i], _bitDepth);
        }
    }

    private void EncodeMono(AlacBitWriter w, int channel, int n)
    {
        short[][] coefsU = _coefsU[channel];
        int bytesShifted = BytesShifted;
        int shift = bytesShifted * 8;
        int mask = (1 << shift) - 1;
        int chanBits = _bitDepth - shift;
        bool partial = n != FrameSize;
        int[] input = _input[channel];
        for (int i = 0; i < n; i++)
        {
            int v = input[i];
            if (bytesShifted != 0)
            {
                _shiftUV[i] = (ushort)(v & mask);
                v >>= shift;
            }

            _mixU[i] = v;
        }

        long minBits = 1L << 31;
        int bestU = 4;
        for (int numU = 4; numU <= 8; numU += 4)
        {
            int dilate = 32;
            for (int converge = 0; converge < 7; converge++)
            {
                AlacPredictor.Predict(_mixU, _predictorU, n / dilate, coefsU[numU - 1], numU, chanBits, AlacPredictor.DenShiftDefault);
            }

            dilate = 8;
            AlacPredictor.Predict(_mixU, _predictorU, n / dilate, coefsU[numU - 1], numU, chanBits, AlacPredictor.DenShiftDefault);
            long bits = ((long)dilate * AlacAdaptiveGolomb.Count(_predictorU, n / dilate, chanBits)) + (16 * numU);
            if (bits < minBits)
            {
                bestU = numU;
                minBits = bits;
            }
        }

        minBits += (4 * 8) + (partial ? 32 : 0);
        if (bytesShifted != 0)
        {
            minBits += (long)n * shift;
        }

        long escapeBits = ((long)n * _bitDepth) + (partial ? 32 : 0) + (2 * 8);
        bool escape = minBits >= escapeBits;
        if (!escape)
        {
            int start = w.Position;
            w.Write(0, 12);
            w.Write((uint)(((partial ? 1 : 0) << 3) | (bytesShifted << 1)), 4);
            if (partial)
            {
                w.Write((uint)n, 32);
            }

            w.Write(0, 16);
            w.Write(AlacPredictor.DenShiftDefault, 8);
            w.Write((uint)((PbFactor << 5) | bestU), 8);
            for (int i = 0; i < bestU; i++)
            {
                w.Write((uint)coefsU[bestU - 1][i], 16);
            }

            if (bytesShifted != 0)
            {
                for (int i = 0; i < n; i++)
                {
                    w.Write(_shiftUV[i], shift);
                }
            }

            AlacPredictor.Predict(_mixU, _predictorU, n, coefsU[bestU - 1], bestU, chanBits, AlacPredictor.DenShiftDefault);
            AlacAdaptiveGolomb.Compress(_predictorU, w, n, chanBits);
            if (w.Position - start >= escapeBits)
            {
                w.Position = start;
                escape = true;
            }
        }

        if (escape)
        {
            w.Write(0, 12);
            w.Write((uint)(((partial ? 1 : 0) << 3) | 1), 4);
            if (partial)
            {
                w.Write((uint)n, 32);
            }

            if (_bitDepth is 20 or 24)
            {
                // As for pairs, Apple copies these depths through the mix buffer.
                input.AsSpan(0, n).CopyTo(_mixU);
            }

            for (int i = 0; i < n; i++)
            {
                w.Write((uint)input[i], _bitDepth);
            }
        }
    }
}
