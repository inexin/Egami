using Egami.Video.Audio.Codecs.Aac;
using Egami.Video.Containers.Mp3;

namespace Egami.Video.Containers.Adts;

/// <summary>
/// Reads raw AAC in ADTS frames (.aac). Each packet is one frame's raw data block, without the header and CRC, as MP4 and
/// Matroska store AAC; the track's AudioSpecificConfig is built from the first header. ID3v2 tags at the start are read, and
/// bytes between frames are skipped up to the next header that is followed by another. The duration is estimated from the
/// mean size of the first frames; a seek builds a frame index in one pass over the headers.
/// </summary>
internal sealed class AdtsDemuxer : IDemuxer
{
    /// <summary>How far to look for the first frame.</summary>
    private const int MaxLeadingBytes = 1 << 16;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly AdtsHeader _first;
    private readonly long _start;
    private readonly byte[] _window = new byte[1 << 16];
    private long _windowStart;
    private int _windowEnd;
    private int _at;
    private List<(long Offset, long Sample)>? _index;
    private long _sample;
    private bool _warnedBlocks;

    public AdtsDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, AdtsFormat.Instance.Name);
        _context = context;
        try
        {
            Dictionary<string, string> tags = [];
            List<ChapterInfo> chapters = [];
            List<AttachmentInfo> attachments = [];
            ReadId3(tags, chapters, attachments);

            if (!Sync(MaxLeadingBytes, null, out _first))
            {
                throw _input.Corrupt(Position, "No ADTS frame was found.");
            }

            if (_first.ChannelConfiguration == 0)
            {
                throw new UnsupportedFormatException("ADTS streams whose channels come from a program config element (channel configuration 0) are not supported.");
            }

            _start = Position;
            AacConfig.TryRead(_first.ToAudioSpecificConfig(), out AacConfig config);
            Rational timeBase = new(1, _first.SampleRate);
            MediaTime? duration = _input.CanSeek ? EstimateDuration(timeBase) : null;
            _track = new AudioTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Audio,
                CodecId = CodecIds.Aac,
                CodecPrivate = _first.ToAudioSpecificConfig(),
                TimeBase = timeBase,
                Duration = duration,
                SampleRate = _first.SampleRate,
                Channels = config.Channels,
                Layout = AacConfig.LayoutOf(_first.ChannelConfiguration),
            };
            Tracks = [_track];
            Info = new MediaInfo
            {
                FormatName = AdtsFormat.Instance.Name,
                Duration = duration,
                Tracks = Tracks,
                Tags = tags,
                Chapters = chapters,
                Attachments = attachments,
            };
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
        while (Sync(long.MaxValue, _first, out AdtsHeader header))
        {
            int available = Ensure(header.FrameBytes);
            int length = Math.Min(available, header.FrameBytes) - header.HeaderBytes;
            if (header.HasCrc && header.Blocks > 1)
            {
                // Each raw data block carries its own CRC word after it: without parsing the blocks they cannot be told apart.
                if (!_warnedBlocks)
                {
                    _warnedBlocks = true;
                    _context.Warnings?.Invoke(new MediaWarning("adts-blocks", "ADTS frames with several raw data blocks and CRCs are skipped."));
                }

                _at += Math.Max(available, 1);
                _sample += header.Samples;
                continue;
            }

            if (length <= 0 || length > _context.Limits.MaxPacketBytes)
            {
                return null;
            }

            // A cut-off last frame is passed as it is, as a decoder can conceal it. Several blocks without CRCs stay in one packet.
            EncodedPacket packet = EncodedPacket.Rent(_track.Number, length, new MediaTime(_sample, _track.TimeBase));
            _window.AsSpan(_at + header.HeaderBytes, length).CopyTo(packet.GetWritableData().Span);
            _at += header.HeaderBytes + length;
            packet.Duration = new MediaTime(header.Samples, _track.TimeBase);
            packet.IsKeyframe = true;
            _sample += header.Samples;
            return packet;
        }

        return null;
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

    /// <summary>Reads the ID3v2 tags at the start of the file, within the header budget; larger ones are skipped.</summary>
    private void ReadId3(Dictionary<string, string> tags, List<ChapterInfo> chapters, List<AttachmentInfo> attachments)
    {
        long budget = _context.Limits.MaxHeaderBytes;
        while (Ensure(Id3Tag.HeaderSize) == Id3Tag.HeaderSize && Id3Tag.Size(_window.AsSpan(_at, Id3Tag.HeaderSize)) is > 0 and int size)
        {
            if (size <= budget)
            {
                budget -= size;
                byte[] tag = new byte[size];
                int have = Math.Min(size, _windowEnd - _at);
                _window.AsSpan(_at, have).CopyTo(tag);
                int read = have;
                if (have < size)
                {
                    MoveTo(_windowStart + _windowEnd);
                    while (read < size && _input.Read(tag.AsSpan(read)) is > 0 and int got)
                    {
                        read += got;
                    }

                    MoveTo(_windowStart + read - have);
                }
                else
                {
                    _at += size;
                }

                if (read < size)
                {
                    return;
                }

                Id3Tag.Read(tag, tags, chapters, attachments);
            }
            else if (_windowEnd - _at >= size)
            {
                _at += size;
            }
            else if (_input.CanSeek)
            {
                MoveTo(Position + size);
            }
            else
            {
                long skip = size - (_windowEnd - _at);
                MoveTo(_windowStart + _windowEnd);
                if (!_input.Skip(skip))
                {
                    return;
                }

                _windowStart += skip;
            }
        }
    }

    /// <summary>Estimates the duration from the mean size of the frames in the first window of the file.</summary>
    private MediaTime EstimateDuration(Rational timeBase)
    {
        long bytes = 0, samples = 0;
        int at = _at;
        while (at + AdtsHeader.Size <= _windowEnd && AdtsHeader.TryRead(_window.AsSpan(at, _windowEnd - at), out AdtsHeader header)
               && _first.Matches(header))
        {
            bytes += header.FrameBytes;
            samples += header.Samples;
            at += header.FrameBytes;
        }

        long total = _input.Length - _start;
        return bytes == 0 ? new MediaTime(_first.Samples, timeBase) : new MediaTime((long)Math.Round((double)total * samples / bytes / 1024) * 1024, timeBase);
    }

    /// <summary>
    /// Moves to the next header with a valid frame, at most <paramref name="limit"/> bytes on. Once the stream is known
    /// (<paramref name="stream"/> is its first header), headers must match it.
    /// </summary>
    private bool Sync(long limit, AdtsHeader? stream, out AdtsHeader header)
    {
        long skipped = 0;
        while (skipped <= limit)
        {
            if (Ensure(AdtsHeader.Size) < AdtsHeader.Size)
            {
                header = default;
                return false;
            }

            if (AdtsHeader.TryRead(_window.AsSpan(_at, AdtsHeader.Size), out header) && (stream is not { } s || s.Matches(header))
                && FollowedByFrame(header, skipped > 0 || stream is null))
            {
                return true;
            }

            // Not a frame: try from the next byte that could start a sync word.
            int next = _window.AsSpan(_at + 1, AdtsHeader.Size - 1).IndexOf((byte)0xFF);
            int step = next < 0 ? AdtsHeader.Size : next + 1;
            _at += step;
            skipped += step;
        }

        header = default;
        return false;
    }

    /// <summary>
    /// Tells whether a header found after skipped bytes (or the first one) is a frame's: the next frame must start where it
    /// ends with a matching header, unless the file ends first. Frames that follow frames are trusted, so that a damaged one
    /// does not lose the rest.
    /// </summary>
    private bool FollowedByFrame(AdtsHeader header, bool resyncing)
    {
        if (!resyncing)
        {
            return true;
        }

        int needed = header.FrameBytes + AdtsHeader.Size;
        int available = Ensure(needed);
        return available < needed
            || (AdtsHeader.TryRead(_window.AsSpan(_at + header.FrameBytes, AdtsHeader.Size), out AdtsHeader next) && header.Matches(next));
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
        if (_input.CanSeek)
        {
            _input.Seek(position);
        }

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
        while (Sync(long.MaxValue, _first, out AdtsHeader header))
        {
            if (index.Count >= _context.Limits.MaxIndexEntries)
            {
                throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, index.Count + 1L);
            }

            index.Add((Position, _sample));
            _sample += header.Samples;
            int available = Ensure(header.FrameBytes);
            _at += Math.Max(available, 1);
        }

        MoveTo(position);
        _sample = sample;
        return index;
    }
}
