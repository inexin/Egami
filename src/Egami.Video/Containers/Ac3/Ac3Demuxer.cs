using Egami.Video.Audio.Codecs.Ac3;

namespace Egami.Video.Containers.Ac3;

/// <summary>
/// Reads raw AC-3 and E-AC-3: sync frames back to back. Each packet is one AC-3 frame, or one E-AC-3 independent frame with
/// the dependent and further independent substreams that follow it (as MP4 and Matroska store them). Bytes between frames are
/// skipped up to the next sync word whose header is valid. The duration comes from the first frame's size; a seek builds a
/// frame index in one pass over the headers.
/// </summary>
internal sealed class Ac3Demuxer : IDemuxer
{
    /// <summary>How far to look for the first frame.</summary>
    private const int MaxLeadingBytes = 1 << 16;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly long _start;
    private readonly byte[] _window = new byte[1 << 16];
    private long _windowStart;
    private int _windowEnd;
    private int _at;
    private List<(long Offset, long Sample)>? _index;
    private long _sample;

    public Ac3Demuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, Ac3Format.Instance.Name);
        _context = context;
        try
        {
            if (!Sync(MaxLeadingBytes, out Ac3Header first))
            {
                throw _input.Corrupt(0, "No AC-3 sync frame was found.");
            }

            _start = Position;
            Rational timeBase = new(1, first.SampleRate);
            MediaTime? duration = null;
            if (_input.CanSeek && first.FrameBytes > 0)
            {
                // AC-3 is constant bit rate; E-AC-3 frames almost always keep their size too.
                double frames = (double)(_input.Length - _start) / first.FrameBytes;
                if (!first.IsEac3 && first.Fscod == 1)
                {
                    // At 44.1 kHz frames alternate in size by a word to keep the bit rate: use the mean.
                    frames = (_input.Length - _start) * 8.0 * first.SampleRate / (first.BitRate * 1000.0 * first.Samples);
                }

                duration = new MediaTime((long)Math.Round(frames) * first.Samples, timeBase);
            }

            _track = new AudioTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Audio,
                CodecId = first.IsEac3 ? CodecIds.EAc3 : CodecIds.Ac3,
                TimeBase = timeBase,
                Duration = duration,
                SampleRate = first.SampleRate,
                Channels = first.Channels,
                Layout = first.Layout,
            };
            Tracks = [_track];
            Info = new MediaInfo { FormatName = Ac3Format.Instance.Name, Duration = duration, Tracks = Tracks };
        }
        catch
        {
            _input.Dispose();
            throw;
        }
    }

    public MediaInfo Info { get; }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public bool CanSeek => _input.CanSeek;

    public EncodedPacket? ReadPacket()
    {
        if (!Sync(long.MaxValue, out Ac3Header header))
        {
            return null;
        }

        int length = 0;
        int samples = header.Samples;
        while (true)
        {
            int available = Ensure(length + header.FrameBytes);
            if (available < length + header.FrameBytes)
            {
                // A cut-off last frame: pass what is there, as a decoder can conceal it.
                length = available;
                break;
            }

            length += header.FrameBytes;

            // E-AC-3's dependent substreams and further independent substreams belong to the same packet.
            if (!header.IsEac3 || Ensure(length + Ac3Header.Size) < length + Ac3Header.Size
                || !Ac3Header.TryRead(_window.AsSpan(_at + length, Ac3Header.Size), out Ac3Header following) || !following.IsEac3
                || (following.StreamType != Ac3Header.Dependent && following.SubstreamId == 0))
            {
                break;
            }

            header = following;
        }

        if (length == 0 || length > _context.Limits.MaxPacketBytes)
        {
            return null;
        }

        EncodedPacket packet = EncodedPacket.Rent(_track.Number, length, new MediaTime(_sample, _track.TimeBase));
        _window.AsSpan(_at, length).CopyTo(packet.GetWritableData().Span);
        _at += length;
        packet.Duration = new MediaTime(samples, _track.TimeBase);
        packet.IsKeyframe = true;
        _sample += samples;
        return packet;
    }

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        if (trackNumber is { } n && n != _track.Number)
        {
            throw new ArgumentException($"Track {n} is not in the file.", nameof(trackNumber));
        }

        _index ??= BuildIndex();
        long target = Math.Max(0, time.Rescale(_track.TimeBase).Ticks);
        int i = _index.FindLastIndex(e => e.Sample <= target);
        (long offset, long sample) = i >= 0 ? _index[i] : (_start, 0);
        MoveTo(offset);
        _sample = sample;
    }

    public void Dispose() => _input.Dispose();

    /// <summary>Gets the file position of the next unread byte.</summary>
    private long Position => _windowStart + _at;

    /// <summary>Moves to the next sync word with a valid header, at most <paramref name="limit"/> bytes on.</summary>
    private bool Sync(long limit, out Ac3Header header)
    {
        long skipped = 0;
        while (skipped <= limit)
        {
            if (Ensure(Ac3Header.Size) < Ac3Header.Size)
            {
                header = default;
                return false;
            }

            if (Ac3Header.TryRead(_window.AsSpan(_at, Ac3Header.Size), out header) && FollowedByFrame(header, skipped > 0))
            {
                return true;
            }

            // Not a frame: try from the next byte that could start a sync word.
            int next = _window.AsSpan(_at + 1, Ac3Header.Size - 1).IndexOf((byte)0x0B);
            int step = next < 0 ? Ac3Header.Size : next + 1;
            _at += step;
            skipped += step;
        }

        header = default;
        return false;
    }

    /// <summary>
    /// Tells whether a header found after skipped bytes (or the first one) is a frame's: the next frame must start where it
    /// ends, unless the file ends first. Frames that follow frames are trusted, so that a damaged one does not lose the rest.
    /// </summary>
    private bool FollowedByFrame(Ac3Header header, bool resyncing)
    {
        if (!resyncing && _windowStart + _at > _start)
        {
            return true;
        }

        int needed = header.FrameBytes + Ac3Header.Size;
        int available = Ensure(needed);
        return available < needed || Ac3Header.TryRead(_window.AsSpan(_at + header.FrameBytes, Ac3Header.Size), out _);
    }

    /// <summary>Makes at least <paramref name="count"/> bytes readable from the window's position, as far as the file goes; returns how many are.</summary>
    private int Ensure(int count)
    {
        count = Math.Min(count, _window.Length);
        if (_windowEnd - _at >= count)
        {
            return count;
        }

        if (_at > 0)
        {
            _window.AsSpan(_at, _windowEnd - _at).CopyTo(_window);
            _windowEnd -= _at;
            _windowStart += _at;
            _at = 0;
        }

        while (_windowEnd < count)
        {
            int read = _input.Read(_window.AsSpan(_windowEnd));
            if (read <= 0)
            {
                break;
            }

            _windowEnd += read;
        }

        return Math.Min(count, _windowEnd);
    }

    private void MoveTo(long position)
    {
        _input.Seek(position);
        _windowStart = position;
        _at = _windowEnd = 0;
    }

    private List<(long Offset, long Sample)> BuildIndex()
    {
        long position = Position;
        long sample = _sample;
        List<(long, long)> index = [];
        MoveTo(_start);
        _sample = 0;
        while (Sync(long.MaxValue, out _))
        {
            long at = Position;
            long before = _sample;
            using EncodedPacket? packet = ReadPacket();
            if (packet is null)
            {
                break;
            }

            if (index.Count >= _context.Limits.MaxIndexEntries)
            {
                throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, index.Count + 1L);
            }

            index.Add((at, before));
        }

        MoveTo(position);
        _sample = sample;
        return index;
    }
}
