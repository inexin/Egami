using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Pcm;

/// <summary>Unpacks PCM packets into interleaved frames of the stored sample type.</summary>
internal sealed class PcmDecoder : IAudioDecoder
{
    private readonly AudioTrackInfo _track;
    private readonly MediaContext _context;
    private readonly bool _float;
    private readonly bool _bigEndian;
    private readonly int _bits;
    private readonly int _blockAlign;
    private readonly ChannelLayout _layout;
    private readonly Queue<AudioFrame> _ready = new();
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _next;
    private int _packet = -1;

    public PcmDecoder(AudioTrackInfo track, MediaContext context)
    {
        _track = track;
        _context = context;
        _float = track.CodecId == CodecIds.PcmFloat;
        _bigEndian = track.CodecId == CodecIds.PcmBigEndian;
        _bits = track.BitsPerSample > 0 ? track.BitsPerSample : _float ? 32 : 16;
        if (track.Channels is < 1 or > ChannelLayout.MaxChannels || track.SampleRate < 1)
        {
            throw new UnsupportedFormatException($"PCM track {track.Number} has {track.Channels} channels at {track.SampleRate} Hz.");
        }

        if (_float ? _bits is not (32 or 64) : _bits is < 1 or > 64)
        {
            throw new UnsupportedFormatException($"PCM track {track.Number} has {_bits}-bit {(_float ? "float" : "integer")} samples.");
        }

        _blockAlign = ((_bits + 7) / 8) * track.Channels;
        _layout = track.EffectiveLayout;
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        int count = data.Length / _blockAlign;
        if (data.Length % _blockAlign != 0)
        {
            Report(DiagnosticLevel.Warning, "Audio.PartialSample", $"Packet {_packet} ends {data.Length % _blockAlign} bytes into a sample; those bytes were dropped.");
        }

        SampleFormat format = PcmSamples.FormatFor(_float, _bits);
        AudioFrame frame = new(format, _layout, _track.SampleRate, count, packet.Timestamp, _context)
        {
            ValidBits = format.IsFloat() ? 0 : Math.Min(((_bits + 7) / 8) * 8, format.BitsPerSample()),
        };
        PcmSamples.Unpack(data, _float, _bigEndian, _bits, frame);
        _next = frame.Timestamp + frame.Duration;
        _ready.Enqueue(frame);
    }

    public void SendLoss(MediaTime duration)
    {
        // PCM has nothing to conceal from: the gap is silence.
        long count = duration.Rescale(new Rational(1, _track.SampleRate)).Ticks;
        if (count > 0 && _next is { } next)
        {
            AudioFrame frame = new(PcmSamples.FormatFor(_float, _bits), _layout, _track.SampleRate, (int)Math.Min(count, int.MaxValue / _blockAlign), next, _context);
            _next = next + frame.Duration;
            _ready.Enqueue(frame);
        }
    }

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

    private void Report(DiagnosticLevel level, string id, string message)
    {
        if (_context.Egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _context.Egami.StartDiagnostics();
        _diagnostics.Report(level, id, message, frame: _packet);
    }
}
