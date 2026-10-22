using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Pcm;

/// <summary>Packs frames into PCM packets of a fixed number of samples.</summary>
internal sealed class PcmEncoder : IAudioEncoder
{
    private readonly bool _float;
    private readonly bool _bigEndian;
    private readonly int _bits;
    private readonly AudioChunker _chunker;
    private readonly Queue<EncodedPacket> _ready = new();

    public PcmEncoder(string codecId, AudioEncoderOptions options, MediaContext context)
    {
        _float = codecId == CodecIds.PcmFloat;
        _bigEndian = codecId == CodecIds.PcmBigEndian;
        _bits = options.BitsPerSample > 0 ? options.BitsPerSample : _float ? 32 : 16;
        if (_float ? _bits is not (32 or 64) : _bits is not (8 or 16 or 24 or 32))
        {
            throw new NotSupportedException($"PCM cannot store {_bits}-bit {(_float ? "float" : "integer")} samples.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.SampleRate, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Layout.ChannelCount, 1, nameof(options));
        FrameSize = options.FrameSize > 0 ? options.FrameSize : 4096;
        SampleFormat format = PcmSamples.FormatFor(_float, _bits);
        _chunker = new AudioChunker(format, _float ? 0 : _bits, options.Layout, options.SampleRate, FrameSize, context);
        Track = new AudioTrackInfo
        {
            Number = 1,
            Kind = TrackKind.Audio,
            CodecId = codecId,
            TimeBase = new Rational(1, options.SampleRate),
            SampleRate = options.SampleRate,
            Channels = options.Layout.ChannelCount,
            BitsPerSample = _bits,
            Layout = options.Layout,
        };
    }

    public AudioTrackInfo Track { get; private set; }

    public int FrameSize { get; }

    public void Send(AudioFrame frame)
    {
        _chunker.Push(frame);
        Drain();
    }

    public void Flush()
    {
        _chunker.Flush();
        Drain();
        Track = Track with { Duration = new MediaTime(_chunker.SamplesIn, _chunker.TimeBase) };
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

    private void Drain()
    {
        int blockAlign = (_bits / 8) * Track.Channels;
        while (_chunker.TryTake(out AudioFrame? frame))
        {
            using (frame)
            {
                EncodedPacket packet = EncodedPacket.Rent(Track.Number, frame.SampleCount * blockAlign, frame.Timestamp);
                PcmSamples.Pack(frame, _float, _bigEndian, _bits, packet.GetWritableData().Span);
                packet.Duration = frame.Duration;
                packet.IsKeyframe = true;
                _ready.Enqueue(packet);
            }
        }
    }
}
