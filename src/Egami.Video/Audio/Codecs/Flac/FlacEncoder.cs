using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>
/// Encodes FLAC at levels 0 to 8 with the reference encoder's meaning for each (block size, LPC order, partition order,
/// stereo decorrelation and apodization windows): fixed block size, one frame per packet, and STREAMINFO with the MD5 of the
/// input in the track's codec private data once flushed.
/// </summary>
internal sealed class FlacEncoder : IAudioEncoder
{
    /// <summary>The settings of a level: block size, LPC order, stereo search, partition order, subdivide_tukey count.</summary>
    private static readonly (int BlockSize, int LpcOrder, int Stereo, int PartitionOrder, int Subdivide)[] s_levels =
    [
        (1152, 0, 0, 3, 1),
        (1152, 0, 1, 3, 1),
        (1152, 0, 2, 3, 1),
        (4096, 6, 0, 4, 1),
        (4096, 8, 1, 4, 1),
        (4096, 8, 2, 5, 1),
        (4096, 8, 2, 6, 2),
        (4096, 12, 2, 6, 2),
        (4096, 12, 2, 6, 3),
    ];

    private readonly int _bits;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _stereoSearch;
    private readonly AudioChunker _chunker;
    private readonly FlacSubframeEncoder _subframes;
    private readonly FlacBitWriter _writer = new();
    private readonly FlacSubframe[] _plans;
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly int[][] _samples;
    private int[] _mid = [];
    private int[] _side = [];
    private long _frameNumber;
    private long _totalSamples;
    private int _minFrame = int.MaxValue;
    private int _maxFrame;

    public FlacEncoder(AudioEncoderOptions options, MediaContext context)
    {
        int level = options.Level ?? 5;
        if (level is < 0 or > 8)
        {
            throw new NotSupportedException($"FLAC levels run from 0 to 8, not {level}.");
        }

        _bits = options.BitsPerSample > 0 ? options.BitsPerSample : 16;
        if (_bits is < 4 or > 32)
        {
            throw new NotSupportedException($"FLAC stores 4 to 32 bits per sample, not {_bits}.");
        }

        _channels = options.Layout.ChannelCount;
        if (_channels is < 1 or > 8)
        {
            throw new NotSupportedException($"FLAC stores 1 to 8 channels, not {_channels}.");
        }

        _sampleRate = options.SampleRate;
        if (_sampleRate is < 1 or > 1048575)
        {
            throw new NotSupportedException($"FLAC stores sample rates up to 1048575 Hz, not {_sampleRate}.");
        }

        (int blockSize, int lpcOrder, int stereo, int partitionOrder, int subdivide) = s_levels[level];
        FrameSize = options.FrameSize > 0 ? options.FrameSize : blockSize;
        if (FrameSize is < 16 or > 65535)
        {
            throw new NotSupportedException($"FLAC blocks hold 16 to 65535 samples, not {FrameSize}.");
        }

        _stereoSearch = _channels == 2 && _bits < 32 ? stereo : 0;
        _subframes = new FlacSubframeEncoder(lpcOrder, partitionOrder, new FlacApodization(subdivide));
        _plans = [.. Enumerable.Range(0, Math.Max(_channels, 4)).Select(_ => new FlacSubframe())];
        _samples = [.. Enumerable.Range(0, _channels).Select(_ => new int[FrameSize])];
        _chunker = new AudioChunker(SampleFormat.S32Planar, _bits, options.Layout, _sampleRate, FrameSize, context);
        Layout = options.Layout;
        Track = MakeTrack(new byte[16]);
    }

    public AudioTrackInfo Track { get; private set; }

    public int FrameSize { get; }

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
        Track = MakeTrack(_md5.GetHashAndReset());
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet) => _ready.TryDequeue(out packet);

    public void Dispose()
    {
        _chunker.Dispose();
        _md5.Dispose();
        while (_ready.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }

    private AudioTrackInfo MakeTrack(byte[] md5)
    {
        FlacStreamInfo info = new(
            FrameSize,
            FrameSize,
            _minFrame == int.MaxValue ? 0 : _minFrame,
            _maxFrame,
            _sampleRate,
            _channels,
            _bits,
            _totalSamples,
            md5);
        List<FlacBlock> blocks = [new(FlacBlock.StreamInfo, info.Write())];
        uint mask = (uint)Layout.Speakers;
        if (Layout != ChannelLayout.ForChannelCount(_channels) && mask != 0)
        {
            // RFC 9639 section 8.6.2: a layout other than the default goes in WAVEFORMATEXTENSIBLE_CHANNEL_MASK.
            Dictionary<string, string> tag = new() { ["WAVEFORMATEXTENSIBLE_CHANNEL_MASK"] = "0x" + mask.ToString("X", System.Globalization.CultureInfo.InvariantCulture) };
            blocks.Add(new FlacBlock(FlacBlock.VorbisComment, FlacMetadata.WriteComments(FlacCodec.Vendor, tag)));
        }

        return new AudioTrackInfo
        {
            Number = 1,
            Kind = TrackKind.Audio,
            CodecId = CodecIds.Flac,
            CodecPrivate = FlacMetadata.WriteBlocks(blocks),
            TimeBase = new Rational(1, _sampleRate),
            Duration = _totalSamples > 0 ? new MediaTime(_totalSamples, new Rational(1, _sampleRate)) : null,
            SampleRate = _sampleRate,
            Channels = _channels,
            BitsPerSample = _bits,
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
                    frame.AsSpan<int>(c).CopyTo(_samples[c]);
                }

                Hash(n);
                EncodedPacket packet = EncodeFrame(n, frame.Timestamp);
                _ready.Enqueue(packet);
                _totalSamples += n;
            }
        }
    }

    private EncodedPacket EncodeFrame(int n, MediaTime timestamp)
    {
        FlacChannels assignment = FlacChannels.Independent;
        if (_stereoSearch > 0)
        {
            assignment = PlanStereo(n);
        }
        else
        {
            for (int c = 0; c < _channels; c++)
            {
                _subframes.Plan(_samples[c].AsSpan(0, n), _bits, _plans[c]);
            }
        }

        FlacBitWriter w = _writer;
        w.Reset();
        WriteHeader(w, n, assignment);
        for (int c = 0; c < _channels; c++)
        {
            FlacSubframeEncoder.Write(w, _plans[c], n);
        }

        w.AlignToByte();
        ushort crc = FlacCrc.Crc16(w.Written);
        w.Write(crc, 16);
        w.AlignToByte();
        ReadOnlySpan<byte> bytes = w.Written;
        EncodedPacket packet = EncodedPacket.Rent(1, bytes.Length, timestamp);
        bytes.CopyTo(packet.GetWritableData().Span);
        packet.Duration = new MediaTime(n, new Rational(1, _sampleRate));
        packet.IsKeyframe = true;
        _minFrame = Math.Min(_minFrame, bytes.Length);
        _maxFrame = Math.Max(_maxFrame, bytes.Length);
        _frameNumber++;
        return packet;
    }

    /// <summary>Chooses independent, left/side, side/right or mid/side coding; plans land in _plans[0] and [1].</summary>
    private FlacChannels PlanStereo(int n)
    {
        if (_mid.Length < n)
        {
            _mid = new int[n];
            _side = new int[n];
        }

        Span<int> left = _samples[0].AsSpan(0, n), right = _samples[1].AsSpan(0, n);
        Span<int> mid = _mid.AsSpan(0, n), side = _side.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            long l = left[i], r = right[i];
            side[i] = (int)(l - r);
            mid[i] = (int)((l + r) >> 1);
        }

        // Plans: 0 left, 1 right, 2 mid, 3 side.
        if (_stereoSearch == 1)
        {
            // Adaptive: decide from the second-order fixed residual of each channel, then plan only the pair chosen.
            long el = FixedEnergy(left), er = FixedEnergy(right), em = FixedEnergy(mid), es = FixedEnergy(side);
            (FlacChannels choice, long _) = Cheapest(el, er, em, es);
            switch (choice)
            {
                case FlacChannels.Independent:
                    _subframes.Plan(left, _bits, _plans[0]);
                    _subframes.Plan(right, _bits, _plans[1]);
                    break;
                case FlacChannels.LeftSide:
                    _subframes.Plan(left, _bits, _plans[0]);
                    _subframes.Plan(side, _bits + 1, _plans[1]);
                    break;
                case FlacChannels.SideRight:
                    _subframes.Plan(side, _bits + 1, _plans[0]);
                    _subframes.Plan(right, _bits, _plans[1]);
                    break;
                default:
                    _subframes.Plan(mid, _bits, _plans[0]);
                    _subframes.Plan(side, _bits + 1, _plans[1]);
                    break;
            }

            return choice;
        }

        _subframes.Plan(left, _bits, _plans[0]);
        _subframes.Plan(right, _bits, _plans[1]);
        _subframes.Plan(mid, _bits, _plans[2]);
        _subframes.Plan(side, _bits + 1, _plans[3]);
        (FlacChannels best, long _) = Cheapest(_plans[0].Cost, _plans[1].Cost, _plans[2].Cost, _plans[3].Cost);
        switch (best)
        {
            case FlacChannels.LeftSide:
                Swap(1, 3);
                break;
            case FlacChannels.SideRight:
                Swap(0, 3);
                break;
            case FlacChannels.MidSide:
                Swap(0, 2);
                Swap(1, 3);
                break;
        }

        return best;
    }

    private static (FlacChannels Choice, long Cost) Cheapest(long left, long right, long mid, long side)
    {
        (FlacChannels, long) best = (FlacChannels.Independent, left + right);
        if (left + side < best.Item2)
        {
            best = (FlacChannels.LeftSide, left + side);
        }

        if (side + right < best.Item2)
        {
            best = (FlacChannels.SideRight, side + right);
        }

        if (mid + side < best.Item2)
        {
            best = (FlacChannels.MidSide, mid + side);
        }

        return best;
    }

    private static long FixedEnergy(ReadOnlySpan<int> s)
    {
        long sum = 0;
        for (int i = 2; i < s.Length; i++)
        {
            sum += Math.Abs(s[i] - (2L * s[i - 1]) + s[i - 2]);
        }

        return sum;
    }

    private void Swap(int a, int b) => (_plans[a], _plans[b]) = (_plans[b], _plans[a]);

    private void WriteHeader(FlacBitWriter w, int n, FlacChannels assignment)
    {
        w.Write(0xFFF8 >> 2, 14);
        w.Write(0, 1);
        w.Write(0, 1); // fixed block size: the coded number is the frame number
        int blockCode = n switch
        {
            192 => 1,
            576 => 2,
            1152 => 3,
            2304 => 4,
            4608 => 5,
            256 => 8,
            512 => 9,
            1024 => 10,
            2048 => 11,
            4096 => 12,
            8192 => 13,
            16384 => 14,
            32768 => 15,
            <= 256 => 6,
            _ => 7,
        };
        int rateCode = _sampleRate switch
        {
            88200 => 1,
            176400 => 2,
            192000 => 3,
            8000 => 4,
            16000 => 5,
            22050 => 6,
            24000 => 7,
            32000 => 8,
            44100 => 9,
            48000 => 10,
            96000 => 11,
            _ when _sampleRate % 1000 == 0 && _sampleRate <= 255000 => 12,
            <= 65535 => 13,
            _ when _sampleRate % 10 == 0 && _sampleRate <= 655350 => 14,
            _ => 0,
        };
        int sizeCode = _bits switch { 8 => 1, 12 => 2, 16 => 4, 20 => 5, 24 => 6, 32 => 7, _ => 0 };
        int channelCode = assignment switch
        {
            FlacChannels.LeftSide => 8,
            FlacChannels.SideRight => 9,
            FlacChannels.MidSide => 10,
            _ => _channels - 1,
        };
        w.Write((uint)blockCode, 4);
        w.Write((uint)rateCode, 4);
        w.Write((uint)channelCode, 4);
        w.Write((uint)sizeCode, 3);
        w.Write(0, 1);
        WriteCodedNumber(w, _frameNumber);
        if (blockCode == 6)
        {
            w.Write((uint)(n - 1), 8);
        }
        else if (blockCode == 7)
        {
            w.Write((uint)(n - 1), 16);
        }

        switch (rateCode)
        {
            case 12:
                w.Write((uint)(_sampleRate / 1000), 8);
                break;
            case 13:
                w.Write((uint)_sampleRate, 16);
                break;
            case 14:
                w.Write((uint)(_sampleRate / 10), 16);
                break;
        }

        w.AlignToByte();
        w.Write(FlacCrc.Crc8(w.Written), 8);
    }

    private static void WriteCodedNumber(FlacBitWriter w, long value)
    {
        if (value < 0x80)
        {
            w.Write((uint)value, 8);
            return;
        }

        int extra = value switch
        {
            < 0x800 => 1,
            < 0x10000 => 2,
            < 0x200000 => 3,
            < 0x4000000 => 4,
            < 0x80000000 => 5,
            _ => 6,
        };
        uint lead = extra == 6 ? 0xFE : (uint)((0xFF00 >> (extra + 1)) & 0xFF) | (uint)(value >> (6 * extra));
        w.Write(lead, 8);
        for (int i = extra - 1; i >= 0; i--)
        {
            w.Write(0x80 | (uint)((value >> (6 * i)) & 0x3F), 8);
        }
    }

    /// <summary>Adds the block to the MD5 of the input: interleaved little-endian samples of the stream's byte width.</summary>
    private void Hash(int n)
    {
        int width = (_bits + 7) / 8;
        int length = n * _channels * width;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int p = 0;
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < _channels; c++)
                {
                    int v = _samples[c][i];
                    for (int k = 0; k < width; k++)
                    {
                        buffer[p++] = (byte)(v >> (8 * k));
                    }
                }
            }

            _md5.AppendData(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
