using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// The AC-3 codec adapter: gathers packet bytes, cuts them into sync frames (a packet may hold several frames or part of one),
/// checks both CRCs, decodes each frame into a planar float frame of 1536 samples and puts its channels in the output layout, the
/// stream's own or <see cref="Ac3DecoderOptions.Downmix"/>. Damaged frames and CRC mismatches are handled as
/// <see cref="AudioDecoderOptions.Errors"/> says; Conceal puts silence of the frame's length in its place.
/// </summary>
internal sealed class Ac3Decoder : IAudioDecoder
{
    /// <summary>Junk longer than this between frames is dropped while searching, keeping the tail that may start a header.</summary>
    private const int MaxSearch = 64 * 1024;

    private readonly AudioDecoderOptions _options;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly Ac3FrameDecoder _decoder = new();
    private readonly Queue<AudioFrame> _ready = new();
    private readonly ChannelLayout _layout;
    private readonly float[][] _samples = new float[Ac3FrameDecoder.MaxChannels][];
    private readonly int[] _route;
    private byte[] _pending = new byte[8192];
    private int _pendingLength;
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _next;
    private MediaTime? _packetEnd;
    private Rational _packetTick = new(1, 1000000);
    private int _packet = -1;
    private (int Acmod, bool Lfe, float Clev, float Slev) _mixKey = (-1, false, 0, 0);
    private float[,]? _matrix;
    private bool _routed;

    public Ac3Decoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _track = track;
        _options = options;
        _context = context;
        Ac3DecoderOptions ac3 = options.Ac3 ?? Ac3DecoderOptions.Default;
        if (!float.IsFinite(ac3.DynamicRange) || ac3.DynamicRange < 0)
        {
            throw new ArgumentException("Ac3DecoderOptions.DynamicRange must be 0 or more.", nameof(options));
        }

        _decoder.DynamicRange = ac3.DynamicRange;
        if (ac3.Downmix is { } downmix)
        {
            if (!downmix.IsPositioned || downmix.ChannelCount == 0)
            {
                throw new ArgumentException("Ac3DecoderOptions.Downmix must be a layout of positioned speakers.", nameof(options));
            }

            _layout = downmix;
        }
        else
        {
            int channels = Math.Clamp(track.Channels, 1, Ac3FrameDecoder.MaxChannels);
            _layout = track.Layout is { } layout && layout.IsPositioned && layout.ChannelCount == channels ? layout : ChannelLayout.ForChannelCount(channels);
        }

        _route = new int[_layout.ChannelCount];
        for (int c = 0; c < _samples.Length; c++)
        {
            _samples[c] = new float[Ac3FrameDecoder.FrameSamples];
        }
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        MediaTime start = Snap(packet.Timestamp);
        if (_pendingLength == 0)
        {
            _next = start;
        }

        _packetEnd = !_options.KeepPadding && packet.Duration is { } duration ? start + duration : null;
        _packetTick = packet.Timestamp.TimeBase;
        Append(packet.Data.Span);
        Decode(atEnd: false);
    }

    public void SendLoss(MediaTime duration)
    {
        _decoder.Reset();
        _pendingLength = 0;
        int rate = Math.Max(_track.SampleRate, 1);
        long samples = duration.Rescale(new Rational(1, rate)).Ticks;
        MediaTime time = _next ?? MediaTime.Zero;
        while (samples > 0)
        {
            int n = (int)Math.Min(samples, Ac3FrameDecoder.FrameSamples);
            AudioFrame frame = new(SampleFormat.F32Planar, _layout, rate, n, time, _context);
            _ready.Enqueue(frame);
            time += frame.Duration;
            samples -= n;
        }

        _next = time;
    }

    public void Flush()
    {
        Decode(atEnd: true);
        _pendingLength = 0;
    }

    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        _decoder.Reset();
        _pendingLength = 0;
        _next = null;
        _packetEnd = null;
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

    /// <summary>Puts a container's rounded timestamp (Matroska's milliseconds) on the sample after the last frame when it is within a millisecond of it.</summary>
    private MediaTime Snap(MediaTime timestamp)
    {
        if (_next is { } next)
        {
            MediaTime tolerance = new(1, new Rational(1, 1000));
            if (timestamp >= next - tolerance && timestamp <= next + tolerance)
            {
                return next;
            }
        }

        return timestamp;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_pendingLength + data.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLength + data.Length));
        }

        data.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += data.Length;
    }

    private void Decode(bool atEnd)
    {
        int pos = 0;
        while (_pendingLength - pos >= Ac3Header.Size)
        {
            ReadOnlySpan<byte> span = _pending.AsSpan(pos, _pendingLength - pos);
            if (!Ac3Header.TryRead(span, out Ac3Header header))
            {
                int found = FindSync(span);
                if (found < 0)
                {
                    int drop = atEnd ? span.Length : Math.Max(0, span.Length - 1);
                    if (atEnd || span.Length > MaxSearch)
                    {
                        Report(DiagnosticLevel.Warning, "Ac3.Junk", $"Skipped {drop} bytes that hold no AC-3 frames.");
                        pos += drop;
                    }

                    break;
                }

                Report(DiagnosticLevel.Info, "Ac3.Resync", $"Skipped {found} bytes before the next frame.");
                pos += found;
                continue;
            }

            if (header.FrameBytes > span.Length)
            {
                if (atEnd)
                {
                    Report(DiagnosticLevel.Warning, "Ac3.TruncatedFrame", $"The stream ends {span.Length} bytes into a {header.FrameBytes}-byte frame.");
                    pos = _pendingLength;
                }

                break;
            }

            if (header.IsEac3)
            {
                Report(DiagnosticLevel.Warning, "Ac3.Eac3Frame", $"Packet {_packet}: skipped an E-AC-3 frame in an AC-3 track.");
            }
            else
            {
                DecodeFrame(span[..header.FrameBytes], header);
            }

            pos += header.FrameBytes;
        }

        if (pos > 0)
        {
            _pending.AsSpan(pos, _pendingLength - pos).CopyTo(_pending);
            _pendingLength -= pos;
        }
    }

    /// <summary>Finds the next byte that starts a valid header, after the first.</summary>
    private static int FindSync(ReadOnlySpan<byte> span)
    {
        for (int i = 1; i + 1 < span.Length; i++)
        {
            if (span[i] == 0x0B && span[i + 1] == 0x77 && (i + Ac3Header.Size > span.Length || Ac3Header.TryRead(span[i..], out _)))
            {
                return i;
            }
        }

        return -1;
    }

    private void DecodeFrame(ReadOnlySpan<byte> data, Ac3Header header)
    {
        int rate = header.SampleRate;
        int samples = Ac3FrameDecoder.FrameSamples;
        MediaTime time = _next ?? MediaTime.Zero;
        Rational sampleTime = new(1, rate);
        if (time.TimeBase != sampleTime)
        {
            time = time.Rescale(sampleTime);
        }

        AudioFrame frame = new(SampleFormat.F32Planar, _layout, rate, samples, time, _context);
        _next = time + frame.Duration;
        string? error = null;
        string id = "Ac3.CorruptFrame";
        if (_options.VerifyChecksums)
        {
            Ac3Crc.Check(data, out bool crc1, out bool crc2);
            if (!crc1 || !crc2)
            {
                id = "Ac3.BadCrc";
                error = $"Packet {_packet}: an AC-3 frame's {(crc1 ? "crc2" : "crc1")} does not match.";
            }
        }

        if (error is null && !_decoder.Decode(data, _samples))
        {
            error = $"Packet {_packet}: an AC-3 frame cannot be decoded.";
        }

        if (error is not null)
        {
            _decoder.Reset();
            Report(DiagnosticLevel.Warning, id, error);
            switch (_options.Errors)
            {
                case DecodeErrorMode.Throw:
                    frame.Dispose();
                    throw new CorruptDataException("AC-3", -1, error);
                case DecodeErrorMode.Conceal:
                    for (int c = 0; c < frame.ChannelCount; c++)
                    {
                        frame.GetSpan<float>(c).Clear();
                    }

                    break;
                default:
                    frame.Dispose();
                    return;
            }
        }
        else
        {
            Output(frame);
        }

        // Timestamps coarser than a sample (Matroska's milliseconds) may round the end up to one tick early.
        bool coarse = _packetTick.Numerator * rate > _packetTick.Denominator;
        if (_packetEnd is { } end && _next > (coarse ? end + new MediaTime(1, _packetTick) : end))
        {
            long keep = Math.Max(0, end.Rescale(sampleTime).Ticks - time.Ticks);
            if (keep < samples)
            {
                frame.Resize((int)keep);
            }
        }

        if (frame.SampleCount > 0)
        {
            _ready.Enqueue(frame);
        }
        else
        {
            frame.Dispose();
        }
    }

    /// <summary>Copies the decoded channels into the frame, routed when each lands on one output, otherwise mixed.</summary>
    private void Output(AudioFrame frame)
    {
        int acmod = _decoder.Acmod;
        bool lfe = _decoder.HasLfe;
        (int, bool, float, float) key = (acmod, lfe, _decoder.CenterMixLevel, _decoder.SurroundMixLevel);
        if (key != _mixKey)
        {
            _mixKey = key;
            _matrix = Ac3Downmix.Matrix(acmod, lfe, _decoder.CenterMixLevel, _decoder.SurroundMixLevel, _layout);
            _routed = IsRouting(_matrix, _route);
        }

        float[,] m = _matrix!;
        int inputs = m.GetLength(1);
        for (int o = 0; o < frame.ChannelCount; o++)
        {
            Span<float> dst = frame.GetSpan<float>(o);
            if (_routed)
            {
                if (_route[o] >= 0)
                {
                    _samples[_route[o]].CopyTo(dst);
                }
                else
                {
                    dst.Clear();
                }

                continue;
            }

            dst.Clear();
            for (int i = 0; i < inputs; i++)
            {
                float g = m[o, i];
                if (g == 0)
                {
                    continue;
                }

                ReadOnlySpan<float> src = _samples[i];
                for (int n = 0; n < dst.Length; n++)
                {
                    dst[n] += g * src[n];
                }
            }
        }
    }

    /// <summary>Returns whether every output takes one input at unity gain (or none), and which.</summary>
    private static bool IsRouting(float[,] m, int[] route)
    {
        for (int o = 0; o < m.GetLength(0); o++)
        {
            route[o] = -1;
            for (int i = 0; i < m.GetLength(1); i++)
            {
                if (m[o, i] == 0)
                {
                    continue;
                }

                if (m[o, i] != 1 || route[o] >= 0)
                {
                    return false;
                }

                route[o] = i;
            }
        }

        return true;
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
