using Egami.Video.Audio.Codecs.Flac;

namespace Egami.Video.Containers.Flac;

/// <summary>
/// Reads native FLAC: the metadata blocks, then frames found by sync code. A frame ends where the next valid header starts
/// (sync code, CRC-8, the same stream parameters and the next frame or sample number) and the bytes before it pass CRC-16.
/// </summary>
internal sealed class FlacDemuxer : IDemuxer
{
    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly FlacStreamInfo _info;
    private readonly long _firstFrame;
    private readonly List<(long Sample, long Offset)> _seekPoints;
    private readonly int _nominalBlock;
    private byte[] _buffer = new byte[1 << 16];
    private int _start;
    private int _end;
    private bool _eof;
    private long _bufferPosition;
    private List<(long Sample, long Position)>? _index;

    public FlacDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, FlacFormat.Instance.Name);
        _context = context;
        try
        {
            Span<byte> magic = stackalloc byte[4];
            if (!_input.TryReadExactly(magic) || !magic.SequenceEqual("fLaC"u8))
            {
                throw _input.Corrupt(0, "The file does not start with fLaC.");
            }

            FlacStreamInfo? info = null;
            Dictionary<string, string> tags = [];
            List<AttachmentInfo> attachments = [];
            List<(long, long)> seekPoints = [];
            byte[]? streamInfoBlock = null;
            long metadataBytes = 0;
            Span<byte> header = stackalloc byte[4];
            while (true)
            {
                if (!_input.TryReadExactly(header))
                {
                    throw _input.Corrupt("The file ends inside its metadata.");
                }

                bool last = (header[0] & 0x80) != 0;
                int type = header[0] & 0x7F;
                int length = (header[1] << 16) | (header[2] << 8) | header[3];
                if (type == 127)
                {
                    throw _input.Corrupt("A metadata block has the invalid type 127.");
                }

                // Blocks read into memory count against the limit; padding, application data and anything too large are skipped.
                bool keep = type is FlacBlock.StreamInfo or FlacBlock.VorbisComment or FlacBlock.Picture or FlacBlock.SeekTable
                    && metadataBytes + length <= context.Limits.MaxHeaderBytes;
                if (keep)
                {
                    metadataBytes += length;
                }

                if (!keep)
                {
                    if (!_input.Skip(length))
                    {
                        throw _input.Corrupt("The file ends inside its metadata.");
                    }
                }
                else
                {
                    byte[] body = new byte[length];
                    if (!_input.TryReadExactly(body))
                    {
                        throw _input.Corrupt("The file ends inside its metadata.");
                    }

                    switch (type)
                    {
                        case FlacBlock.StreamInfo:
                            info ??= FlacStreamInfo.Read(body);
                            streamInfoBlock ??= body;
                            break;
                        case FlacBlock.VorbisComment:
                            FlacMetadata.ReadComments(body, tags);
                            break;
                        case FlacBlock.Picture when FlacMetadata.ReadPicture(body, attachments.Count) is { } picture:
                            attachments.Add(picture);
                            break;
                        case FlacBlock.SeekTable:
                            seekPoints = FlacMetadata.ReadSeekTable(body);
                            break;
                    }
                }

                if (last)
                {
                    break;
                }
            }

            if (info is null || streamInfoBlock is null)
            {
                throw _input.Corrupt("The file has no STREAMINFO block.");
            }

            if (info.SampleRate < 1)
            {
                throw _input.Corrupt("STREAMINFO gives a sample rate of 0.");
            }

            _info = info;
            _nominalBlock = info.MinBlockSize == info.MaxBlockSize ? info.MaxBlockSize : 0;

            _firstFrame = _input.Position;
            _bufferPosition = _firstFrame;
            _seekPoints = seekPoints;
            Rational timeBase = new(1, info.SampleRate);
            MediaTime? duration = info.TotalSamples > 0 ? new MediaTime(info.TotalSamples, timeBase) : null;
            Audio.ChannelLayout? layout = FlacMetadata.LayoutFromTags(tags, info.Channels);
            tags.Remove("WAVEFORMATEXTENSIBLE_CHANNEL_MASK");
            _track = new AudioTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Audio,
                CodecId = CodecIds.Flac,
                CodecPrivate = FlacMetadata.WriteBlocks([new FlacBlock(FlacBlock.StreamInfo, streamInfoBlock)]),
                TimeBase = timeBase,
                Duration = duration,
                SampleRate = info.SampleRate,
                Channels = info.Channels,
                BitsPerSample = info.BitsPerSample,
                Layout = layout,
            };
            Tracks = [_track];
            Info = new MediaInfo { FormatName = FlacFormat.Instance.Name, Duration = duration, Tracks = Tracks, Tags = tags, Attachments = attachments };
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
        while (true)
        {
            Fill(16);
            if (_end - _start < 6)
            {
                return null;
            }

            FlacFrameHeader? header = FlacFrameHeader.Parse(_buffer.AsSpan(_start, _end - _start), _info);
            if (header is not { } h || !Matches(h))
            {
                // Not at a frame: resynchronize at the next sync code.
                _start++;
                SkipToSync();
                continue;
            }

            int length = FrameLength(h);
            long sample = h.FirstSample(_nominalBlock);
            EncodedPacket packet = EncodedPacket.Rent(_track.Number, length, new MediaTime(sample, _track.TimeBase));
            _buffer.AsSpan(_start, length).CopyTo(packet.GetWritableData().Span);
            packet.Duration = new MediaTime(h.BlockSize, _track.TimeBase);
            packet.IsKeyframe = true;
            _start += length;
            return packet;
        }
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

        long target = Math.Max(0, time.Rescale(_track.TimeBase).Ticks);
        long offset = 0;
        if (_seekPoints.Count > 0)
        {
            foreach ((long sample, long pointOffset) in _seekPoints)
            {
                if (sample <= target && pointOffset >= offset)
                {
                    offset = pointOffset;
                }
            }

            MoveTo(_firstFrame + offset);
            return;
        }

        BuildIndex();
        long position = _firstFrame;
        foreach ((long sample, long framePosition) in _index!)
        {
            if (sample > target)
            {
                break;
            }

            position = framePosition;
        }

        MoveTo(position);
    }

    public void Dispose() => _input.Dispose();

    private bool Matches(FlacFrameHeader h) =>
        h.Channels == _info.Channels && h.BitsPerSample == _info.BitsPerSample && h.BlockSize <= Math.Max(_info.MaxBlockSize, 16) + (_info.MaxBlockSize == 0 ? 65535 : 0);

    /// <summary>Finds where the frame at the buffer start ends: the next valid header after bytes that pass CRC-16, or the end of the file.</summary>
    private int FrameLength(FlacFrameHeader h)
    {
        int limit = Math.Min(_context.Limits.MaxPacketBytes, _info.MaxFrameSize > 0 ? _info.MaxFrameSize + 1024 : int.MaxValue);
        ushort[] table = FlacCrc.Crc16Table;
        ushort crc = 0;
        int j = 0;
        long expected = h.NextNumber(_nominalBlock);
        while (true)
        {
            Fill(j + 18);
            int available = _end - _start;
            if (j >= available)
            {
                return available;
            }

            ReadOnlySpan<byte> data = _buffer.AsSpan(_start, available);
            int stop = _eof ? available : available - 17;
            for (; j < stop; j++)
            {
                byte b = data[j];
                if (b == 0xFF && crc == 0 && j >= h.Length + 2 && j + 1 < available && (data[j + 1] & 0xFE) == 0xF8
                    && FlacFrameHeader.Parse(data[j..], _info) is { } next && Matches(next) && next.Number == expected && next.VariableBlockSize == h.VariableBlockSize)
                {
                    return j;
                }

                crc = (ushort)((crc << 8) ^ table[(crc >> 8) ^ b]);
                if (j > limit)
                {
                    throw _input.Corrupt(_bufferPosition + _start, "No frame end found within the largest frame size.");
                }
            }

            if (_eof && j >= available)
            {
                return available;
            }
        }
    }

    private void SkipToSync()
    {
        while (true)
        {
            int at = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)0xFF);
            if (at >= 0)
            {
                _start += at;
                return;
            }

            _start = _end;
            if (!Fill(16))
            {
                return;
            }
        }
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

    private void MoveTo(long position)
    {
        _input.Seek(position);
        _bufferPosition = position;
        _start = _end = 0;
        _eof = false;
    }

    private void BuildIndex()
    {
        if (_index is not null)
        {
            return;
        }

        long resume = _bufferPosition + _start;
        MoveTo(_firstFrame);
        List<(long, long)> index = [];
        while (true)
        {
            long position = _bufferPosition + _start;
            using EncodedPacket? packet = ReadPacket();
            if (packet is null)
            {
                break;
            }

            if (index.Count == 0 || position - index[^1].Item2 >= 1 << 16)
            {
                index.Add((packet.Timestamp.Ticks, position));
            }
        }

        _index = index;
        MoveTo(resume);
    }
}
