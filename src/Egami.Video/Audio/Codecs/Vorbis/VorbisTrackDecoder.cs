using System.Diagnostics.CodeAnalysis;

using Egami.Video.Containers.Ogg;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>
/// The Vorbis codec adapter: the send and receive loop over <see cref="VorbisDecoder"/>, with the headers from the track's
/// Xiph-laced codec private data, channels remapped to the canonical order, timestamps kept on a running sample clock (so
/// Matroska's rounded milliseconds do not shift samples), and padding trimmed to the packet's stated duration. A lost packet
/// leaves silence of its length, after which the decoder starts its overlap again.
/// </summary>
internal sealed class VorbisTrackDecoder : IAudioDecoder
{
    private readonly AudioDecoderOptions _options;
    private readonly MediaContext _context;
    private readonly VorbisDecoder _decoder;
    private readonly ChannelLayout _layout;
    private readonly int[] _order;
    private readonly Rational _sampleTime;
    private readonly long _tolerance;
    private readonly Queue<AudioFrame> _ready = new();
    private DiagnosticReporter? _diagnostics;
    private long? _next;
    private int _packet = -1;

    public VorbisTrackDecoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _options = options;
        _context = context;
        List<byte[]> headers = OggCodecs.XiphUnlace(track.CodecPrivate.Span) is { Count: 3 } h
            ? h
            : throw new UnsupportedFormatException($"Vorbis track {track.Number} has no three Xiph-laced header packets in its codec private data.");
        _decoder = new VorbisDecoder(headers);
        (_layout, _order) = VorbisChannelOrder.Layout(_decoder.Channels);
        _sampleTime = new Rational(1, _decoder.SampleRate);

        // Containers with millisecond timestamps round each packet's time: a millisecond either way is the same sample.
        _tolerance = Math.Max(1, _decoder.SampleRate / 1000);
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        long ticks = packet.Timestamp.Rescale(_sampleTime).Ticks;
        if (_next is { } next && Math.Abs(ticks - next) <= _tolerance)
        {
            ticks = next;
        }

        int count;
        try
        {
            count = _decoder.Decode(packet.Data.Span);
        }
        catch (CorruptDataException e)
        {
            Report("Vorbis.CorruptPacket", $"Packet {_packet} cannot be decoded ({e.Message}).");
            switch (_options.Errors)
            {
                case DecodeErrorMode.Throw:
                    throw;
                case DecodeErrorMode.Conceal:
                    Silence(ticks, packet.Duration);
                    break;
            }

            return;
        }

        if (count == 0)
        {
            return;
        }

        int keep = count;
        if (!_options.KeepPadding && packet.Duration is { } duration)
        {
            // The container says the packet ends early: the rest is padding (the last granule position of an Ogg stream).
            long said = duration.Rescale(_sampleTime).Ticks;
            if (said >= 0 && said < count)
            {
                keep = (int)said;
            }
        }

        _next = ticks + count;
        if (keep == 0)
        {
            return;
        }

        AudioFrame frame = new(SampleFormat.F32Planar, _layout, _decoder.SampleRate, keep, new MediaTime(ticks, _sampleTime), _context);
        for (int c = 0; c < _decoder.Channels; c++)
        {
            _decoder.GetSamples(c)[..keep].CopyTo(frame.GetSpan<float>(_order[c]));
        }

        _ready.Enqueue(frame);
    }

    public void SendLoss(MediaTime duration)
    {
        // Vorbis has no concealment: the gap is silence, and the next packet starts a new overlap.
        _decoder.Reset();
        if (_next is { } next)
        {
            Silence(next, duration);
        }
    }

    public void Flush()
    {
    }

    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        _decoder.Reset();
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

    private void Silence(long ticks, MediaTime? duration)
    {
        long count = duration is { } d ? d.Rescale(_sampleTime).Ticks : _decoder.BlockSize0 / 2;
        if (count <= 0)
        {
            return;
        }

        AudioFrame frame = new(SampleFormat.F32Planar, _layout, _decoder.SampleRate, (int)Math.Min(count, 1 << 20), new MediaTime(ticks, _sampleTime), _context);
        _next = ticks + frame.SampleCount;
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
}
