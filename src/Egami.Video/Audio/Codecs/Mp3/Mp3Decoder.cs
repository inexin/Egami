using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Codecs.Mp3;

/// <summary>
/// The MPEG audio codec adapter: gathers packet bytes, cuts them into frames (a packet may hold several frames or part of one,
/// as AVI chunks do), decodes each into a planar float frame of the track's layout and timestamps them sample by sample from
/// the first packet's time. Damaged frames and CRC mismatches are handled as <see cref="AudioDecoderOptions.Errors"/> says;
/// Conceal puts silence of the frame's length in its place, so timing stays right.
/// </summary>
internal sealed class Mp3Decoder : IAudioDecoder
{
    /// <summary>Junk longer than this between frames is dropped while searching, keeping the tail that may start a header.</summary>
    private const int MaxSearch = 64 * 1024;

    private readonly AudioDecoderOptions _options;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly Mp3FrameDecoder _decoder = new();
    private readonly Queue<AudioFrame> _ready = new();
    private readonly ChannelLayout _layout;
    private readonly float[] _scratch = new float[Mp3FrameDecoder.MaxSamples];
    private byte[] _pending = new byte[8192];
    private int _pendingLength;
    private int _freeFormatBytes;
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _next;
    private MediaTime? _packetEnd;
    private bool _synced;
    private int _packet = -1;

    public Mp3Decoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _track = track;
        _options = options;
        _context = context;
        int channels = track.Channels is 1 or 2 ? track.Channels : 2;
        _layout = track.Layout is { } layout && layout.ChannelCount == channels ? layout : ChannelLayout.ForChannelCount(channels);
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        if (_pendingLength == 0)
        {
            _next = Snap(packet.Timestamp);
        }

        _packetEnd = !_options.KeepPadding && packet.Duration is { } duration ? packet.Timestamp + duration : null;
        Append(data);
        Decode(atEnd: false);
    }

    public void SendLoss(MediaTime duration)
    {
        // The frames after a lost one may take main data from it, so they decode as silence until the reservoir refills.
        _decoder.Reset();
        _pendingLength = 0;
        _synced = false;
        int rate = Math.Max(_track.SampleRate, 1);
        long samples = duration.Rescale(new Rational(1, rate)).Ticks;
        MediaTime time = _next ?? MediaTime.Zero;
        while (samples > 0)
        {
            int n = (int)Math.Min(samples, Mp3FrameDecoder.MaxSamples);
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
        _synced = false;
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
        while (_pendingLength - pos >= MpegAudioHeader.Size)
        {
            ReadOnlySpan<byte> span = _pending.AsSpan(pos, _pendingLength - pos);
            int total = 0;
            if (_synced && MpegAudioHeader.IsValid(span))
            {
                MpegAudioHeader header = new(span);
                total = header.TotalBytes(_freeFormatBytes);
                if (total == 0)
                {
                    // Free format with no length yet: the frame runs to the next matching header or, at the end, to the end.
                    int next = NextHeader(span);
                    total = next > 0 ? next : atEnd ? span.Length : 0;
                    if (next > 0)
                    {
                        _freeFormatBytes = next - header.PaddingBytes;
                    }
                }
            }

            if (total == 0 || (total > span.Length && atEnd))
            {
                if (_synced && total > span.Length && atEnd)
                {
                    Report(DiagnosticLevel.Warning, "Mp3.TruncatedFrame", $"The stream ends {span.Length} bytes into a {total}-byte frame.");
                    break;
                }

                int found = MpegAudioSync.Find(span, ref _freeFormatBytes, out total, atEnd);
                if (found < 0)
                {
                    if (span.Length > MaxSearch || atEnd)
                    {
                        // Nothing that locks: drop all but the last bytes, which may begin a header.
                        int drop = atEnd ? span.Length : span.Length - MpegAudioHeader.MaxFreeFormatBytes;
                        Report(DiagnosticLevel.Warning, "Mp3.Junk", $"Skipped {drop} bytes that hold no MPEG audio frames.");
                        pos += drop;
                    }

                    break;
                }

                if (found > 0)
                {
                    Report(DiagnosticLevel.Info, "Mp3.Resync", $"Skipped {found} bytes before the next frame.");
                    pos += found;
                    span = span[found..];
                }

                _synced = true;
            }

            if (total > span.Length)
            {
                break;
            }

            DecodeFrame(span[..total]);
            pos += total;
        }

        if (pos > 0)
        {
            _pending.AsSpan(pos, _pendingLength - pos).CopyTo(_pending);
            _pendingLength -= pos;
        }
    }

    private static int NextHeader(ReadOnlySpan<byte> span)
    {
        for (int k = MpegAudioHeader.Size; k + MpegAudioHeader.Size <= span.Length && k <= MpegAudioHeader.MaxFreeFormatBytes + 4; k++)
        {
            if (MpegAudioHeader.Matches(span, span[k..]))
            {
                return k;
            }
        }

        return 0;
    }

    private void DecodeFrame(ReadOnlySpan<byte> data)
    {
        MpegAudioHeader header = new(data);
        int samples = header.SamplesPerFrame;
        int rate = header.SampleRate;
        MediaTime time = _next ?? MediaTime.Zero;
        Rational sampleTime = new(1, rate);
        if (time.TimeBase != sampleTime)
        {
            time = time.Rescale(sampleTime);
        }

        AudioFrame frame = new(SampleFormat.F32Planar, _layout, rate, samples, time, _context);
        Span<float> left = frame.GetSpan<float>(0);
        Span<float> right = header.Channels == 2 ? _layout.ChannelCount == 2 ? frame.GetSpan<float>(1) : _scratch : left;
        Mp3FrameResult result = _decoder.Decode(data, left, right);
        _next = time + frame.Duration;

        bool crcOk = result != Mp3FrameResult.Decoded || !header.HasCrc || !_options.VerifyChecksums || MpegAudioCrc.Check(data, _decoder.ProtectedBits);
        if (result == Mp3FrameResult.Corrupt || !crcOk)
        {
            string message = result == Mp3FrameResult.Corrupt
                ? $"Packet {_packet}: an MPEG audio frame cannot be decoded."
                : $"Packet {_packet}: an MPEG audio frame's CRC-16 does not match.";
            Report(DiagnosticLevel.Warning, result == Mp3FrameResult.Corrupt ? "Mp3.CorruptFrame" : "Mp3.BadCrc", message);
            switch (_options.Errors)
            {
                case DecodeErrorMode.Throw:
                    frame.Dispose();
                    throw new CorruptDataException("MP3", -1, message);
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
        else if (header.Channels != _layout.ChannelCount)
        {
            if (header.Channels == 1)
            {
                // A mono frame in a stereo track: both channels carry it.
                left.CopyTo(frame.GetSpan<float>(1));
            }
            else
            {
                // A stereo frame in a mono track: the mix of both.
                for (int i = 0; i < samples; i++)
                {
                    left[i] = (left[i] + right[i]) * 0.5f;
                }
            }
        }

        if (_packetEnd is { } end && _next > end)
        {
            // The container says the stream ends inside this frame: the rest is the encoder's padding.
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
