using Egami.Video.Audio.Codecs.Mp3;

namespace Egami.Video.Containers.Mp3;

/// <summary>
/// Reads MPEG audio files: ID3v2 tags at the start, ID3v1, APEv2, Lyrics3 and appended ID3v2 tags at the end (when the stream
/// can seek), then one packet per frame. The first frame is found by three matching headers; a Xing, Info or VBRI frame is read
/// and left out, and its LAME tag's delay and padding make the packet times and durations gapless. Seeks are exact: one pass
/// over the frame headers builds an index, and the seek starts early enough to fill the bit reservoir and the filter banks.
/// </summary>
internal sealed class Mp3Demuxer : IDemuxer
{
    /// <summary>How far the first frame is searched for before the file is taken as not MPEG audio.</summary>
    private const int MaxLeadingJunk = 1 << 20;

    private readonly MediaInput _input;
    private readonly AudioTrackInfo _track;
    private readonly bool _fastSeek;
    private readonly long _dataEnd;
    private readonly long _firstAudio;
    private readonly long _infoStart;
    private readonly byte[] _firstHeader = new byte[MpegAudioHeader.Size];
    private readonly int _samplesPerFrame;
    private readonly long _skip;
    private readonly long? _frames;
    private readonly long? _endSample;
    private readonly XingHeader? _xing;
    private readonly int _bitrate;
    private byte[] _buffer = new byte[1 << 16];
    private int _start;
    private int _end;
    private bool _eof;
    private long _bufferPosition;
    private int _freeFormatBytes;
    private bool _resync = true;
    private long _frameIndex;
    private List<long>? _index;

    public Mp3Demuxer(Stream stream, MediaContext context, bool fastSeek, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, Mp3Format.Instance.Name);
        _fastSeek = fastSeek;
        try
        {
            Dictionary<string, string> tags = [];
            Dictionary<string, string> endTags = [];
            List<ChapterInfo> chapters = [];
            List<AttachmentInfo> attachments = [];
            long budget = context.Limits.MaxHeaderBytes;
            _dataEnd = _input.CanSeek ? ReadEndTags(endTags, attachments, ref budget) : long.MaxValue;

            // ID3v2 tags at the start; some files carry more than one.
            while (Fill(Id3Tag.HeaderSize) && Id3Tag.Size(Available()) is > 0 and int size)
            {
                if (size <= budget && Fill(size))
                {
                    budget -= size;
                    Id3Tag.Read(Available()[..size], tags, chapters, attachments);
                }

                Consume(size);
            }

            foreach ((string key, string value) in endTags)
            {
                tags.TryAdd(key, value);
            }

            int total = FindFirstFrame();
            _infoStart = Position;
            Available()[..MpegAudioHeader.Size].CopyTo(_firstHeader);
            MpegAudioHeader header = new(_firstHeader);
            if (Fill(total) && XingHeader.Read(Available()[..total]) is { } xing)
            {
                _xing = xing;
                Consume(total);
            }

            _firstAudio = Position;
            _resync = false;
            _samplesPerFrame = header.SamplesPerFrame;
            _bitrate = header.BitrateKbps;
            int rate = header.SampleRate;
            Rational timeBase = new(1, rate);

            // The LAME tag's delay is the encoder's; the decoder adds its own 529 samples. Padding past those 529 is cut at the end.
            _frames = _xing?.Frames;
            if (_xing is { EncoderDelay: { } delay, EncoderPadding: { } padding })
            {
                _skip = delay + XingHeader.DecoderDelay;
                if (_frames is { } f)
                {
                    _endSample = (f * _samplesPerFrame) - Math.Max(0, padding - XingHeader.DecoderDelay);
                }
            }

            long? samples = _endSample is { } end ? end - _skip
                : _frames is { } count ? count * _samplesPerFrame
                : _input.CanSeek && _bitrate > 0 ? (long)((_dataEnd - _firstAudio) * 8.0 * rate / (_bitrate * 1000.0) + 0.5)
                : null;
            MediaTime? duration = samples is { } s and >= 0 ? new MediaTime(s, timeBase) : null;
            _track = new AudioTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Audio,
                CodecId = header.Layer switch { 1 => CodecIds.Mp1, 2 => CodecIds.Mp2, _ => CodecIds.Mp3 },
                TimeBase = timeBase,
                Duration = duration,
                SampleRate = rate,
                Channels = header.Channels,
                CodecDelay = _skip > 0 ? new MediaTime(_skip, timeBase) : null,
                Tags = _xing?.Encoder is { Length: > 0 } encoder ? new Dictionary<string, string> { ["ENCODER"] = encoder } : new Dictionary<string, string>(),
            };
            Tracks = [_track];
            Info = new MediaInfo { FormatName = Mp3Format.Instance.Name, Duration = duration, Tracks = Tracks, Tags = tags, Chapters = chapters, Attachments = attachments };
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

    private long Position => _bufferPosition + _start;

    public EncodedPacket? ReadPacket()
    {
        int total = NextFrame();
        if (total <= 0)
        {
            return null;
        }

        int length = (int)Math.Min(total, Math.Min(_end - _start, _dataEnd - Position));
        long start = (_frameIndex * _samplesPerFrame) - _skip;
        long duration = _samplesPerFrame;
        if (_endSample is { } end && _frameIndex < _frames)
        {
            // The last frames hold the encoder's padding: their durations stop at the source's end.
            duration = Math.Clamp(end - _skip - start, 0, _samplesPerFrame);
        }

        EncodedPacket packet = EncodedPacket.Rent(_track.Number, length, new MediaTime(start, _track.TimeBase));
        _buffer.AsSpan(_start, length).CopyTo(packet.GetWritableData().Span);
        packet.Duration = new MediaTime(duration, _track.TimeBase);
        packet.IsKeyframe = true;
        Consume(length);
        _frameIndex++;
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

        // In decoder output samples, which run _skip ahead of the source's.
        long target = Math.Max(0, time.Rescale(_track.TimeBase).Ticks + _skip);
        if (_fastSeek)
        {
            FastSeek(target);
            return;
        }

        BuildIndex();
        List<long> index = _index!;
        if (index.Count == 0)
        {
            MoveTo(_firstAudio, 0);
            return;
        }

        int frame = (int)Math.Min(target / _samplesPerFrame, index.Count - 1);

        // The frames before it whose output must be right: the IMDCT overlap and the synthesis filter's 512 samples of history.
        int first = Math.Max(0, frame - 1 - ((512 + _samplesPerFrame - 1) / _samplesPerFrame));
        MpegAudioHeader header = new(_firstHeader);
        if (header.Layer == 3 && first > 0)
        {
            // Then the frames that hold that frame's bit reservoir (main_data_begin bytes of main data before it), and one more.
            int reservoir = MainDataBegin(index[first]);
            int side = MpegAudioHeader.Size + (header.HasCrc ? 2 : 0) + header.SideInfoBytes;
            long have = 0;
            while (first > 0 && have < reservoir)
            {
                first--;
                have += index[first + 1] - index[first] - side;
            }

            first = Math.Max(0, first - 1);
        }

        MoveTo(index[first], first);
    }

    public void Dispose() => _input.Dispose();

    /// <summary>Reads the tags at the end of the file and returns where the frames end.</summary>
    private long ReadEndTags(Dictionary<string, string> tags, List<AttachmentInfo> attachments, ref long budget)
    {
        long end = _input.Length;
        Span<byte> footer = stackalloc byte[Id3Tag.V1Size];
        while (true)
        {
            if (end >= Id3Tag.V1Size && ReadAt(end - Id3Tag.V1Size, footer) && footer.StartsWith("TAG"u8))
            {
                Id3Tag.ReadV1(footer, tags);
                end -= Id3Tag.V1Size;
                continue;
            }

            if (end >= Id3Tag.ApeFooterSize && ReadAt(end - Id3Tag.ApeFooterSize, footer[..Id3Tag.ApeFooterSize])
                && Id3Tag.ApeSize(footer[..Id3Tag.ApeFooterSize], out _) is > 0 and int apeSize && apeSize <= end)
            {
                if (apeSize <= budget)
                {
                    budget -= apeSize;
                    byte[] ape = new byte[apeSize];
                    if (ReadAt(end - apeSize, ape))
                    {
                        Id3Tag.ReadApe(ape, tags, attachments);
                    }
                }

                end -= apeSize;
                continue;
            }

            // Lyrics3 v2: the tag's size in six digits, then "LYRICS200".
            if (end >= 15 && ReadAt(end - 15, footer[..15]) && footer[6..15].SequenceEqual("LYRICS200"u8)
                && int.TryParse(System.Text.Encoding.ASCII.GetString(footer[..6]), out int lyrics) && lyrics + 15 <= end)
            {
                end -= lyrics + 15;
                continue;
            }

            if (end >= Id3Tag.HeaderSize && ReadAt(end - Id3Tag.HeaderSize, footer[..Id3Tag.HeaderSize])
                && Id3Tag.FooterTagSize(footer[..Id3Tag.HeaderSize]) is > 0 and int id3Size && id3Size <= end)
            {
                if (id3Size <= budget)
                {
                    budget -= id3Size;
                    byte[] id3 = new byte[id3Size];
                    if (ReadAt(end - id3Size, id3))
                    {
                        Id3Tag.Read(id3, tags);
                    }
                }

                end -= id3Size;
                continue;
            }

            break;
        }

        _input.Seek(0);
        return end;
    }

    private bool ReadAt(long position, Span<byte> destination)
    {
        _input.Seek(position);
        return _input.TryReadExactly(destination);
    }

    /// <summary>Finds the first frame that three matching headers lock onto and returns its length; the buffer then starts there.</summary>
    private int FindFirstFrame()
    {
        long searched = 0;
        while (true)
        {
            Fill(1 << 15);
            ReadOnlySpan<byte> data = Available();
            bool atEnd = AtEnd(data.Length);
            int found = MpegAudioSync.Find(data, ref _freeFormatBytes, out int total, atEnd);
            if (found >= 0)
            {
                Consume(found);
                return total;
            }

            int drop = Math.Max(0, data.Length - MpegAudioHeader.MaxFreeFormatBytes - (3 * MpegAudioHeader.Size));
            searched += drop;
            if (atEnd || searched > MaxLeadingJunk)
            {
                throw _input.Corrupt(Position, "No MPEG audio frames found.");
            }

            Consume(drop);
            if (drop == 0)
            {
                // The buffer holds a partial search window: make it larger.
                Fill(_end - _start + (1 << 15));
            }
        }
    }

    /// <summary>
    /// Puts the buffer start at the next frame of the stream and returns its whole length (which may run past the end of a
    /// truncated file), or 0 at the end. Junk and frames of another stream are skipped by a fresh three-header lock.
    /// </summary>
    private int NextFrame()
    {
        while (true)
        {
            if (Position >= _dataEnd || !Fill(MpegAudioHeader.Size))
            {
                return 0;
            }

            ReadOnlySpan<byte> data = Available();
            if (data.Length < MpegAudioHeader.Size)
            {
                return 0;
            }

            if (!_resync && MpegAudioHeader.Matches(_firstHeader, data))
            {
                int total = new MpegAudioHeader(data).TotalBytes(_freeFormatBytes);
                if (total > 0)
                {
                    Fill(total);
                    return total;
                }
            }

            if (data.StartsWith("ID3"u8) && Fill(Id3Tag.HeaderSize) && Id3Tag.Size(Available()) is > 0 and int tag)
            {
                // A tag between frames, as when files are joined.
                Consume(tag);
                continue;
            }

            Fill(1 << 15);
            data = Available();
            bool atEnd = AtEnd(data.Length);
            int found = MpegAudioSync.Find(data, ref _freeFormatBytes, out int length, atEnd);
            if (found < 0)
            {
                if (atEnd)
                {
                    Consume(data.Length);
                    return 0;
                }

                Consume(Math.Max(1, data.Length - MpegAudioHeader.MaxFreeFormatBytes - (3 * MpegAudioHeader.Size)));
                continue;
            }

            Consume(found);
            if (!MpegAudioHeader.Matches(_firstHeader, Available()))
            {
                // A locked run of another stream's frames (or false syncs that happen to chain): not ours.
                Consume(1);
                continue;
            }

            _resync = false;
            Fill(length);
            return length;
        }
    }

    /// <summary>Reads main_data_begin, the first side information field, of the frame at <paramref name="position"/>.</summary>
    private int MainDataBegin(long position)
    {
        Span<byte> head = stackalloc byte[MpegAudioHeader.Size + 4];
        if (!ReadAt(position, head))
        {
            return 0;
        }

        MpegAudioHeader header = new(head);
        int p = header.HasCrc ? 6 : 4;
        int bits = (head[p] << 8) | head[p + 1];
        return header.IsMpeg1 ? bits >> 7 : bits >> 8;
    }

    private void FastSeek(long target)
    {
        long frame = target / _samplesPerFrame;
        long position;
        if (_xing is { Toc: { } toc, Bytes: { } bytes, Frames: { } frames } && frames > 0)
        {
            // Xing's table: entry i is the byte position at i percent of the time.
            double percent = Math.Clamp(100.0 * frame / frames, 0, 100);
            int i = Math.Min((int)percent, 99);
            double a = toc[i];
            double b = i < 99 ? toc[i + 1] : 256;
            position = _infoStart + (long)((a + ((b - a) * (percent - i))) / 256 * bytes);
        }
        else if (_xing is { Points: { } points })
        {
            (long Frame, long Offset) best = points[0];
            foreach ((long Frame, long Offset) point in points)
            {
                if (point.Frame <= frame)
                {
                    best = point;
                }
            }

            frame = best.Frame;
            position = _infoStart + best.Offset;
        }
        else
        {
            // Constant bit rate: frames are all about as long.
            MpegAudioHeader header = new(_firstHeader);
            double frameBytes = header.TotalBytes(_freeFormatBytes) > 0 ? header.SamplesPerFrame * header.BitrateKbps * 125.0 / header.SampleRate : _freeFormatBytes;
            position = _firstAudio + (long)(frame * frameBytes);
        }

        position = Math.Clamp(position, _firstAudio, Math.Min(_dataEnd, _input.Length));
        MoveTo(position, frame);
        _resync = position != _firstAudio;
    }

    private void BuildIndex()
    {
        if (_index is not null)
        {
            return;
        }

        long resume = Position;
        long resumeFrame = _frameIndex;
        bool resync = _resync;
        MoveTo(_firstAudio, 0);
        List<long> index = [];
        while (true)
        {
            int total = NextFrame();
            if (total <= 0)
            {
                break;
            }

            index.Add(Position);
            Consume(Math.Min(total, _end - _start));
        }

        _index = index;
        MoveTo(resume, resumeFrame);
        _resync = resync;
    }

    private ReadOnlySpan<byte> Available() => _buffer.AsSpan(_start, (int)Math.Min(_end - _start, Math.Max(0, _dataEnd - Position)));

    /// <summary>Whether <paramref name="available"/> bytes from the buffer start run to the end of the frames.</summary>
    private bool AtEnd(int available) => (_eof && available == _end - _start) || Position + available >= _dataEnd;

    private void Consume(long count)
    {
        if (count <= _end - _start)
        {
            _start += (int)count;
            return;
        }

        long target = Position + count;
        if (_input.CanSeek)
        {
            MoveTo(Math.Min(target, _input.Length), _frameIndex);
            return;
        }

        count -= _end - _start;
        _start = _end;
        if (!_input.Skip(count))
        {
            _eof = true;
        }

        _bufferPosition = target - _start;
    }

    /// <summary>Makes at least <paramref name="count"/> bytes from the buffer start available; false when the file ends first.</summary>
    private bool Fill(int count)
    {
        if (_end - _start >= count)
        {
            return true;
        }

        if (_start > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
            _bufferPosition += _start;
            _end -= _start;
            _start = 0;
        }

        if (count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(count, _buffer.Length * 2));
        }

        while (_end < count && !_eof)
        {
            int read = _input.Read(_buffer.AsSpan(_end, _buffer.Length - _end));
            if (read == 0)
            {
                _eof = true;
            }

            _end += read;
        }

        return _end - _start >= count;
    }

    private void MoveTo(long position, long frame)
    {
        _input.Seek(position);
        _bufferPosition = position;
        _start = _end = 0;
        _eof = false;
        _frameIndex = frame;
    }
}
