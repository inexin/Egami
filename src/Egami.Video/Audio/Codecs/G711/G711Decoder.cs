using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.G711;

/// <summary>Expands G.711 bytes into interleaved 16-bit frames through a 256-entry table per law.</summary>
internal sealed class G711Decoder : IAudioDecoder
{
    private readonly AudioTrackInfo _track;
    private readonly MediaContext _context;
    private readonly short[] _table;
    private readonly ChannelLayout _layout;
    private readonly Queue<AudioFrame> _ready = new();
    private MediaTime? _next;

    public G711Decoder(AudioTrackInfo track, MediaContext context)
    {
        if (track.Channels is < 1 or > ChannelLayout.MaxChannels || track.SampleRate < 1)
        {
            throw new UnsupportedFormatException($"G.711 track {track.Number} has {track.Channels} channels at {track.SampleRate} Hz.");
        }

        _track = track;
        _context = context;
        _table = track.CodecId == CodecIds.G711ALaw ? G711Tables.ALaw : G711Tables.MuLaw;
        _layout = track.EffectiveLayout;
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ReadOnlySpan<byte> data = packet.Data.Span;
        int count = data.Length / _track.Channels;
        AudioFrame frame = new(SampleFormat.S16, _layout, _track.SampleRate, count, packet.Timestamp, _context);
        Span<short> samples = frame.GetSpan<short>();
        short[] table = _table;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = table[data[i]];
        }

        _next = frame.Timestamp + frame.Duration;
        _ready.Enqueue(frame);
    }

    public void SendLoss(MediaTime duration)
    {
        // Nothing to conceal from: the gap is silence.
        long count = duration.Rescale(new Rational(1, _track.SampleRate)).Ticks;
        if (count > 0 && _next is { } next)
        {
            AudioFrame frame = new(SampleFormat.S16, _layout, _track.SampleRate, (int)Math.Min(count, int.MaxValue / 2 / _track.Channels), next, _context);
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

    public void Dispose() => Reset();
}
