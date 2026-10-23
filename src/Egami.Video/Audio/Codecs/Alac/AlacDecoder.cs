using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Alac;

/// <summary>
/// Decodes ALAC packets, translated from Apple's ALACDecoder.cpp and matrix_dec.c (Apache-2.0): channel elements (mono,
/// LFE and pairs) in the stream's order, each predicted, entropy coded and for pairs matrixed, with the low bytes of 24 and
/// 32-bit samples stored raw. Frames come out planar (<see cref="SampleFormat.S16Planar"/> for 16 bits, else
/// <see cref="SampleFormat.S32Planar"/> with <see cref="AudioFrame.ValidBits"/> the stream's depth) in Egami's channel order.
/// </summary>
internal sealed class AlacDecoder : IAudioDecoder
{
    /// <summary>The longest frame accepted; Apple's encoder writes 4096 samples.</summary>
    private const int MaxFrameLength = 1 << 20;

    /// <summary>Zero bytes after each packet, for the entropy decoder's 32-bit reads.</summary>
    private const int Padding = 16;

    private const int IdSce = 0;
    private const int IdCpe = 1;
    private const int IdCce = 2;
    private const int IdLfe = 3;
    private const int IdDse = 4;
    private const int IdPce = 5;
    private const int IdFil = 6;
    private const int IdEnd = 7;

    private readonly AudioTrackInfo _track;
    private readonly MediaContext _context;
    private readonly AudioDecoderOptions _options;
    private readonly AlacSpecificConfig _config;
    private readonly ChannelLayout _layout;
    private readonly int[] _order;
    private readonly int[][] _samples;
    private readonly int[] _mixU;
    private readonly int[] _mixV;
    private readonly int[] _predictor;
    private readonly ushort[] _shift;
    private readonly short[] _coefsU = new short[32];
    private readonly short[] _coefsV = new short[32];
    private readonly Queue<AudioFrame> _ready = new();
    private byte[] _buffer = [];
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _next;
    private int _packet = -1;

    public AlacDecoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _config = AlacSpecificConfig.Read(track.CodecPrivate.Span)
            ?? throw new UnsupportedFormatException($"ALAC track {track.Number} has no ALACSpecificConfig.");
        if (_config.CompatibleVersion != 0)
        {
            throw new UnsupportedFormatException($"ALAC track {track.Number} needs decoder version {_config.CompatibleVersion}.");
        }

        if (_config.BitDepth is not (16 or 20 or 24 or 32) || _config.Channels is < 1 or > 8 || _config.FrameLength is < 1 or > MaxFrameLength)
        {
            throw new UnsupportedFormatException($"ALAC track {track.Number} has {_config.BitDepth} bits, {_config.Channels} channels and {_config.FrameLength} samples per frame.");
        }

        _track = track;
        _options = options;
        _context = context;
        (_layout, _order) = Layout(_config.Channels);
        int length = Math.Max((int)_config.FrameLength, 32) + 1;
        _samples = [.. Enumerable.Range(0, _config.Channels).Select(_ => new int[length])];
        _mixU = new int[length];
        _mixV = new int[length];
        _predictor = new int[length];
        _shift = new ushort[2 * length];
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        if (_buffer.Length < data.Length + Padding)
        {
            _buffer = new byte[data.Length + Padding];
        }

        data.CopyTo(_buffer);
        _buffer.AsSpan(data.Length, Padding).Clear();
        int count;
        try
        {
            count = Decode(data.Length);
        }
        catch (CorruptDataException e)
        {
            Report("Alac.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}).");
            switch (_options.Errors)
            {
                case DecodeErrorMode.Throw:
                    throw;
                case DecodeErrorMode.Conceal:
                    long length = packet.Duration is { } d ? d.Rescale(new Rational(1, SampleRate)).Ticks : _config.FrameLength;
                    Silence(length, packet.Timestamp);
                    break;
            }

            return;
        }

        if (!_options.KeepPadding && packet.Duration is { } duration)
        {
            // The container says the packet ends early: the rest is padding (CAF's remainder frames, Matroska DiscardPadding).
            long said = duration.Rescale(new Rational(1, SampleRate)).Ticks;
            if (said >= 0 && said < count)
            {
                count = (int)said;
            }
        }

        Emit(count, packet.Timestamp);
    }

    public void SendLoss(MediaTime duration) =>
        Silence(duration.Rescale(new Rational(1, SampleRate)).Ticks, _next ?? MediaTime.Zero);

    public void Flush()
    {
    }

    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        _next = null;
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    public void Dispose()
    {
        Reset();
        _diagnostics?.Dispose();
    }

    /// <summary>
    /// The layout of ALAC's channel elements for each channel count (Apple's ALACChannelLayoutTags: C; L R; C L R; C L R Cs;
    /// C L R Ls Rs; plus LFE; C L R Ls Rs Cs LFE; C Lc Rc L R Ls Rs LFE), and where each stream channel goes in Egami's order.
    /// </summary>
    internal static (ChannelLayout Layout, int[] Order) Layout(int channels)
    {
        Speakers[] positions = channels switch
        {
            1 => [Speakers.FrontCenter],
            2 => [Speakers.FrontLeft, Speakers.FrontRight],
            3 => [Speakers.FrontCenter, Speakers.FrontLeft, Speakers.FrontRight],
            4 => [Speakers.FrontCenter, Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackCenter],
            5 => [Speakers.FrontCenter, Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight],
            6 => [Speakers.FrontCenter, Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight, Speakers.LowFrequency],
            7 => [Speakers.FrontCenter, Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight, Speakers.BackCenter, Speakers.LowFrequency],
            _ => [Speakers.FrontCenter, Speakers.FrontLeftOfCenter, Speakers.FrontRightOfCenter, Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight, Speakers.LowFrequency],
        };
        Speakers all = positions.Aggregate(Speakers.None, (a, s) => a | s);
        int[] order = [.. positions.Select(s => System.Numerics.BitOperations.PopCount((uint)all & ((uint)s - 1)))];
        return (new ChannelLayout(all), order);
    }

    private int SampleRate => _track.SampleRate > 0 ? _track.SampleRate : Math.Max(1, (int)Math.Min(_config.SampleRate, int.MaxValue));

    /// <summary>Decodes the packet in the buffer into <see cref="_samples"/>; returns its sample count.</summary>
    private int Decode(int length)
    {
        Reader bits = new(_buffer, length);
        int channels = _config.Channels;
        int count = (int)_config.FrameLength;
        int channel = 0;
        while (channel < channels)
        {
            if (bits.Position >= bits.End)
            {
                throw Corrupt(bits, "The packet ends before its END element.");
            }

            int tag = bits.Read(3);
            switch (tag)
            {
                case IdSce or IdLfe:
                    count = DecodeMono(ref bits, count, _samples[channel]);
                    channel++;
                    break;
                case IdCpe:
                    if (channel + 2 > channels)
                    {
                        goto Done;
                    }

                    count = DecodePair(ref bits, count, _samples[channel], _samples[channel + 1]);
                    channel += 2;
                    break;
                case IdCce or IdPce:
                    throw Corrupt(bits, $"Element {tag} is not used by ALAC.");
                case IdDse:
                    bits.Read(4);
                    bool align = bits.Read(1) != 0;
                    int size = bits.Read(8);
                    if (size == 255)
                    {
                        size += bits.Read(8);
                    }

                    if (align)
                    {
                        bits.Align();
                    }

                    bits.Skip(size * 8);
                    break;
                case IdFil:
                    int fill = bits.Read(4);
                    if (fill == 15)
                    {
                        fill += bits.Read(8) - 1;
                    }

                    bits.Skip(fill * 8);
                    break;
                default:
                    bits.Align();
                    goto Done;
            }

            if (bits.Position > bits.End)
            {
                throw Corrupt(bits, "An element runs past the end of the packet.");
            }
        }

    Done:
        // Channels the packet has no element for are silent.
        for (; channel < channels; channel++)
        {
            _samples[channel].AsSpan(0, count).Clear();
        }

        return count;
    }

    /// <summary>Reads the element header shared by both kinds: returns the shift bytes and escape flag, and the frame's sample count.</summary>
    private int ReadHeader(ref Reader bits, ref int count, out bool escape)
    {
        bits.Read(4);
        if (bits.Read(12) != 0)
        {
            throw Corrupt(bits, "An element header's unused bits are set.");
        }

        int header = bits.Read(4);
        int bytesShifted = (header >> 1) & 3;
        if (bytesShifted == 3)
        {
            throw Corrupt(bits, "An element shifts off 3 bytes.");
        }

        escape = (header & 1) != 0;
        if ((header >> 3) != 0)
        {
            uint partial = ((uint)bits.Read(16) << 16) | (uint)bits.Read(16);
            if (partial > _config.FrameLength)
            {
                throw Corrupt(bits, $"A partial frame of {partial} samples is longer than the stream's {_config.FrameLength}.");
            }

            count = (int)partial;
        }

        return bytesShifted;
    }

    private int DecodeMono(ref Reader bits, int count, int[] output)
    {
        int bytesShifted = ReadHeader(ref bits, ref count, out bool escape);
        int chanBits = _config.BitDepth - (bytesShifted * 8);
        Reader shiftBits = default;
        if (!escape)
        {
            bits.Read(16); // mixBits and mixRes: unused for one channel
            (int mode, int denShift, int pbFactor, int num) = ReadPredictor(ref bits, _coefsU);
            if (bytesShifted != 0)
            {
                shiftBits = bits;
                bits.Skip(bytesShifted * 8 * count);
            }

            Residuals(ref bits, count, chanBits, pbFactor);
            Unpredict(_mixU, count, _coefsU, mode, num, chanBits, denShift);
        }
        else
        {
            ReadRaw(ref bits, count, chanBits, _mixU, null);
            bytesShifted = 0;
        }

        int shift = bytesShifted * 8;
        if (bytesShifted != 0)
        {
            for (int i = 0; i < count; i++)
            {
                _shift[i] = (ushort)shiftBits.Read(shift);
            }
        }

        // The sample's low bits, sign-extended from the stream's depth (the output conversions of ALACDecoder::Decode).
        int extend = 32 - _config.BitDepth;
        for (int i = 0; i < count; i++)
        {
            int v = bytesShifted != 0 ? (_mixU[i] << shift) | _shift[i] : _mixU[i];
            output[i] = (v << extend) >> extend;
        }

        return count;
    }

    private int DecodePair(ref Reader bits, int count, int[] left, int[] right)
    {
        int bytesShifted = ReadHeader(ref bits, ref count, out bool escape);
        int chanBits = _config.BitDepth - (bytesShifted * 8) + 1;
        int mixBits = 0, mixRes = 0;
        Reader shiftBits = default;
        if (!escape)
        {
            mixBits = bits.Read(8);
            mixRes = (sbyte)bits.Read(8);
            (int modeU, int denShiftU, int pbFactorU, int numU) = ReadPredictor(ref bits, _coefsU);
            (int modeV, int denShiftV, int pbFactorV, int numV) = ReadPredictor(ref bits, _coefsV);
            if (chanBits > 32)
            {
                throw Corrupt(bits, "A 32-bit pair is coded without shifting off its low bytes.");
            }

            if (bytesShifted != 0)
            {
                shiftBits = bits;
                bits.Skip(bytesShifted * 8 * 2 * count);
            }

            Residuals(ref bits, count, chanBits, pbFactorU);
            Unpredict(_mixU, count, _coefsU, modeU, numU, chanBits, denShiftU);
            Residuals(ref bits, count, chanBits, pbFactorV);
            Unpredict(_mixV, count, _coefsV, modeV, numV, chanBits, denShiftV);
        }
        else
        {
            ReadRaw(ref bits, count, _config.BitDepth, _mixU, _mixV);
            bytesShifted = 0;
        }

        int shift = bytesShifted * 8;
        if (bytesShifted != 0)
        {
            for (int i = 0; i < 2 * count; i += 2)
            {
                _shift[i] = (ushort)shiftBits.Read(shift);
                _shift[i + 1] = (ushort)shiftBits.Read(shift);
            }
        }

        // Unmix (matrix_dec.c): L = u + v - [mixRes * v / 2^mixBits], R = L - v; mixRes 0 means the channels were kept apart.
        int extend = 32 - _config.BitDepth;
        for (int i = 0; i < count; i++)
        {
            int l = _mixU[i], r = _mixV[i];
            if (mixRes != 0)
            {
                l = l + r - ((mixRes * r) >> mixBits);
                r = l - r;
            }

            if (bytesShifted != 0)
            {
                l = (l << shift) | _shift[2 * i];
                r = (r << shift) | _shift[(2 * i) + 1];
            }

            left[i] = (l << extend) >> extend;
            right[i] = (r << extend) >> extend;
        }

        return count;
    }

    /// <summary>Reads a channel's predictor: mode, coefficient scale, mean rate factor, and the coefficients themselves.</summary>
    private static (int Mode, int DenShift, int PbFactor, int Num) ReadPredictor(ref Reader bits, short[] coefs)
    {
        int header = bits.Read(8);
        int filter = bits.Read(8);
        int num = filter & 0x1f;
        for (int i = 0; i < num; i++)
        {
            coefs[i] = (short)bits.Read(16);
        }

        return (header >> 4, header & 0xf, filter >> 5, num);
    }

    /// <summary>Entropy-decodes one channel's residuals into the predictor buffer.</summary>
    private void Residuals(ref Reader bits, int count, int chanBits, int pbFactor)
    {
        int position = bits.Position;
        uint pb = (uint)(_config.Pb * pbFactor) / 4;
        AlacAdaptiveGolomb.Decompress(_buffer, ref position, bits.End, _predictor, count, chanBits, (uint)_config.Mb, pb, (uint)_config.Kb);
        bits.Position = position;
        if (position > bits.End)
        {
            throw Corrupt(bits, "The residuals run past the end of the packet.");
        }
    }

    /// <summary>Runs the predictor backwards from the predictor buffer into <paramref name="output"/>; mode 1 first undoes a first difference.</summary>
    private void Unpredict(int[] output, int count, short[] coefs, int mode, int num, int chanBits, int denShift)
    {
        if (mode != 0)
        {
            AlacPredictor.Unpredict(_predictor, _predictor, count, null, 31, chanBits, 0);
        }

        AlacPredictor.Unpredict(_predictor, output, count, coefs, num, chanBits, denShift);
    }

    /// <summary>Reads an escaped (uncompressed) element: interleaved samples of <paramref name="chanBits"/> bits.</summary>
    private static void ReadRaw(ref Reader bits, int count, int chanBits, int[] u, int[]? v)
    {
        if (chanBits < 1)
        {
            throw Corrupt(bits, "An escaped element has no bits per sample.");
        }

        if ((long)count * chanBits * (v is null ? 1 : 2) > bits.End - bits.Position)
        {
            throw Corrupt(bits, "The uncompressed samples run past the end of the packet.");
        }

        int shift = 32 - chanBits;
        for (int i = 0; i < count; i++)
        {
            u[i] = ReadSample(ref bits, chanBits, shift);
            if (v is not null)
            {
                v[i] = ReadSample(ref bits, chanBits, shift);
            }
        }
    }

    private static int ReadSample(ref Reader bits, int chanBits, int shift)
    {
        if (chanBits <= 16)
        {
            return (bits.Read(chanBits) << shift) >> shift;
        }

        int high = (bits.Read(16) << 16) >> shift;
        return high | bits.Read(chanBits - 16);
    }

    private static CorruptDataException Corrupt(Reader bits, string message) => new("ALAC", bits.Position >> 3, message);

    private void Emit(int count, MediaTime time)
    {
        if (count <= 0)
        {
            return;
        }

        int bits = _config.BitDepth;
        SampleFormat format = bits <= 16 ? SampleFormat.S16Planar : SampleFormat.S32Planar;
        AudioFrame frame = new(format, _layout, SampleRate, count, time, _context) { ValidBits = bits };
        for (int c = 0; c < _config.Channels; c++)
        {
            ReadOnlySpan<int> source = _samples[c].AsSpan(0, count);
            int target = _order[c];
            if (format == SampleFormat.S16Planar)
            {
                Span<short> d = frame.GetSpan<short>(target);
                for (int i = 0; i < count; i++)
                {
                    d[i] = (short)source[i];
                }
            }
            else
            {
                source.CopyTo(frame.GetSpan<int>(target));
            }
        }

        _next = time + frame.Duration;
        _ready.Enqueue(frame);
    }

    private void Silence(long count, MediaTime time)
    {
        if (count <= 0)
        {
            return;
        }

        int bits = _config.BitDepth;
        AudioFrame frame = new(bits <= 16 ? SampleFormat.S16Planar : SampleFormat.S32Planar, _layout, SampleRate, (int)Math.Min(count, MaxFrameLength), time, _context) { ValidBits = bits };
        _next = time + frame.Duration;
        _ready.Enqueue(frame);
    }

    private void Report(string id, string message)
    {
        if (_context.Egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _context.Egami.StartDiagnostics();
        _diagnostics.Report(DiagnosticLevel.Warning, id, message, frame: _packet);
    }

    /// <summary>
    /// The element reader (Apple's BitBuffer read side): MSB-first reads of up to 16 bits from a zero-padded buffer, failing
    /// rather than reading past the packet.
    /// </summary>
    private struct Reader(byte[] data, int length)
    {
        private readonly byte[] _data = data;

        public int Position { get; set; }

        public readonly int End { get; } = length * 8;

        public int Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            if (Position + count > End)
            {
                throw Corrupt(this, "A field runs past the end of the packet.");
            }

            int i = Position >> 3;
            int word = (_data[i] << 16) | (_data[i + 1] << 8) | _data[i + 2];
            word = (word << (Position & 7)) & 0xFFFFFF;
            Position += count;
            return word >> (24 - count);
        }

        public void Skip(int count) => Position = (int)Math.Min((long)Position + count, int.MaxValue / 2);

        public void Align() => Position = (Position + 7) & ~7;
    }
}
