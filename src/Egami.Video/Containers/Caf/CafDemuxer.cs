using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Caf;

/// <summary>
/// Reads Apple's Core Audio Format. On a seekable stream every chunk is read on open, skipping over the samples, so tags and a
/// packet table after the data chunk are found; on a non-seekable one reading stops at the data chunk, so codecs that need a
/// packet table must have it before. Constant-size packets (PCM, G.711) are read in runs of whole frames; variable-size ones
/// one packet at a time, as the packet table cuts them.
/// </summary>
internal sealed class CafDemuxer : IDemuxer
{
    /// <summary>Sample frames per packet for constant-size codecs: about 0.1 s at 44.1 kHz.</summary>
    private const int PacketFrames = 4096;

    /// <summary>Opus's seek pre-roll in 48 kHz samples (80 ms), as Matroska's SeekPreRoll gives it.</summary>
    private const int OpusPreRoll = 3840;

    /// <summary>The most frames a packet may decode to: about 22 s at 48 kHz, far past any codec's (ALAC's 4096, Opus's 5760).</summary>
    private const long MaxPacketFrames = 1 << 20;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly long _dataStart;
    private readonly long _dataEnd;
    private readonly int _swap;

    // Constant-size packets.
    private readonly int _bytesPerPacket;
    private readonly int _framesPerPacket;

    // Variable-size packets, from the packet table: each one's start in the data, and its first frame.
    private readonly long[]? _offsets;
    private readonly long[]? _frames;
    private readonly long _priming;
    private readonly long _validEnd;
    private int _next;

    public CafDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, CafFormat.Instance.Name);
        _context = context;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (!_input.TryReadExactly(header) || !CafFormat.Instance.IsMatch(header))
            {
                throw _input.Corrupt(0, "The file is not CAF.");
            }

            CafDescription? desc = null;
            byte[] cookie = [];
            byte[]? packetTable = null;
            byte[]? layout = null;
            Dictionary<string, string> tags = [];
            Dictionary<uint, string> strings = [];
            List<(uint Id, double Frame)> markers = [];
            List<(uint Id, double Start, double End)> regions = [];
            long dataStart = -1, dataEnd = -1;
            Span<byte> chunkHeader = stackalloc byte[12];
            while (_input.TryReadExactly(chunkHeader))
            {
                string type = Encoding.Latin1.GetString(chunkHeader[..4]);
                long size = BinaryPrimitives.ReadInt64BigEndian(chunkHeader[4..]);
                if (type == "data")
                {
                    // The edit count comes first; the samples follow it.
                    if (!_input.Skip(4))
                    {
                        break;
                    }

                    dataStart = _input.Position;
                    long fileEnd = _input.CanSeek ? _input.Length : long.MaxValue;
                    dataEnd = size < 4 ? fileEnd : Math.Min(fileEnd, dataStart - 4 + size);
                    if (size < 4 || !_input.CanSeek || dataEnd >= _input.Length)
                    {
                        break;
                    }

                    // The packet table, tags and markers may follow the samples: read them, then come back.
                    _input.Seek(dataEnd);
                    continue;
                }

                if (size < 0)
                {
                    break;
                }

                if (size > _context.Limits.MaxHeaderBytes || size > _input.Remaining)
                {
                    if (!_input.Skip(size))
                    {
                        break;
                    }

                    continue;
                }

                byte[] body = new byte[size];
                if (!_input.TryReadExactly(body))
                {
                    break;
                }

                switch (type)
                {
                    case "desc":
                        desc ??= CafDescription.Read(body);
                        break;
                    case "kuki":
                        cookie = body;
                        break;
                    case "pakt":
                        packetTable = body;
                        break;
                    case "chan":
                        layout = body;
                        break;
                    case "info":
                        CafChunks.ReadInfo(body, tags);
                        break;
                    case "strg":
                        strings = CafChunks.ReadStrings(body);
                        break;
                    case "mark":
                        markers = CafChunks.ReadMarkers(body);
                        break;
                    case "regn":
                        regions = CafChunks.ReadRegions(body);
                        break;
                }
            }

            if (desc is null)
            {
                throw _input.Corrupt("The file has no desc chunk.");
            }

            if (dataStart < 0)
            {
                throw _input.Corrupt("The file has no data chunk.");
            }

            if (desc.Channels is < 1 or > 255 || !(desc.SampleRate >= 1 && desc.SampleRate <= int.MaxValue))
            {
                throw _input.Corrupt($"desc says {desc.Channels} channels at {desc.SampleRate} Hz.");
            }

            if (desc.FramesPerPacket > MaxPacketFrames)
            {
                throw _input.Corrupt($"desc says {desc.FramesPerPacket} frames per packet.");
            }

            if (packetTable is { Length: < CafChunks.PacketTableHeader })
            {
                packetTable = null;
            }

            int rate = (int)Math.Round(desc.SampleRate);
            bool variable = desc.BytesPerPacket == 0 || desc.FramesPerPacket == 0;
            long validFrames = -1;
            if (packetTable is not null && packetTable.Length >= CafChunks.PacketTableHeader)
            {
                validFrames = BinaryPrimitives.ReadInt64BigEndian(packetTable.AsSpan(8));
                _priming = Math.Max(0, BinaryPrimitives.ReadInt32BigEndian(packetTable.AsSpan(16)));
            }

            CafCodec codec = CafCodecs.Read(desc, cookie, _priming);
            _dataStart = dataStart;
            _dataEnd = dataEnd;
            _swap = codec.Swap;
            long totalFrames;
            if (variable)
            {
                if (packetTable is null)
                {
                    throw _input.CanSeek
                        ? _input.Corrupt("The packets vary in size but the file has no packet table.")
                        : new UnsupportedFormatException("The packet table comes after the samples; reading this CAF file needs a seekable stream.");
                }

                (_offsets, _frames) = ReadPacketTable(packetTable, desc, dataEnd == long.MaxValue ? long.MaxValue : dataEnd - dataStart);
                totalFrames = _frames[^1];
            }
            else
            {
                if (desc.BytesPerPacket > _context.Limits.MaxPacketBytes)
                {
                    throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, desc.BytesPerPacket);
                }

                _bytesPerPacket = (int)desc.BytesPerPacket;
                _framesPerPacket = (int)Math.Min(desc.FramesPerPacket, int.MaxValue / 2);
                long packets = dataEnd == long.MaxValue ? -1 : (dataEnd - dataStart) / _bytesPerPacket;
                totalFrames = packets < 0 ? -1 : packets * _framesPerPacket;
                if (packets >= 0)
                {
                    _dataEnd = dataStart + (packets * _bytesPerPacket);
                }
            }

            // The valid frames end before the last packet does when the codec padded it (pakt's remainder frames).
            long remainder = packetTable is not null && packetTable.Length >= CafChunks.PacketTableHeader ? Math.Max(0, BinaryPrimitives.ReadInt32BigEndian(packetTable.AsSpan(20))) : 0;
            _validEnd = validFrames > 0 && totalFrames >= 0 ? Math.Min(totalFrames, _priming + validFrames)
                : totalFrames >= 0 ? Math.Max(_priming, totalFrames - remainder) : long.MaxValue;
            Rational timeBase = new(1, rate);
            MediaTime? duration = _validEnd != long.MaxValue ? new MediaTime(_validEnd - _priming, timeBase) : null;
            bool opus = codec.CodecId == CodecIds.Opus;
            _track = new AudioTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Audio,
                CodecId = codec.CodecId,
                CodecPrivate = codec.CodecPrivate,
                TimeBase = timeBase,
                Duration = duration,
                SampleRate = rate,
                Channels = (int)desc.Channels,
                BitsPerSample = codec.Bits > 0 ? codec.Bits : codec.CodecId == CodecIds.Alac && codec.CodecPrivate.Length > 5 ? codec.CodecPrivate.Span[5] : 0,
                Layout = layout is null ? null : CoreAudioLayout.Read(layout, (int)desc.Channels),
                CodecDelay = _priming > 0 ? new MediaTime(_priming, timeBase) : null,
                SeekPreRoll = opus ? new MediaTime(OpusPreRoll, new Rational(1, 48000)) : null,
            };
            Tracks = [_track];
            Info = new MediaInfo { FormatName = CafFormat.Instance.Name, Duration = duration, Tracks = Tracks, Tags = tags, Chapters = Chapters(markers, regions, strings, timeBase, duration) };
            if (_input.Position != dataStart)
            {
                _input.Seek(dataStart);
            }
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

    public EncodedPacket? ReadPacket() => _offsets is null ? ReadConstant() : ReadVariable();

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

        long frame = Math.Max(0, time.Rescale(_track.TimeBase).Ticks) + _priming;
        if (_offsets is null || _frames is null)
        {
            long packet = frame / _framesPerPacket;
            long last = (_dataEnd - _dataStart) / _bytesPerPacket;
            _input.Seek(Math.Min(_dataStart + (Math.Min(packet, last) * _bytesPerPacket), _input.Length));
            return;
        }

        // The last packet that starts at or before the target.
        int index = Array.BinarySearch(_frames, 0, _offsets.Length - 1, frame);
        index = index >= 0 ? index : Math.Max(0, ~index - 1);
        while (index > 0 && _frames[index - 1] == _frames[index] && _frames[index] >= frame)
        {
            index--;
        }

        _next = Math.Min(index, _offsets.Length - 1);
        _input.Seek(Math.Min(_dataStart + _offsets[_next], _input.Length));
    }

    public void Dispose() => _input.Dispose();

    /// <summary>Reads the packet table: each packet's offset in the data and first frame, with one more entry for the end.</summary>
    private (long[] Offsets, long[] Frames) ReadPacketTable(byte[] table, CafDescription desc, long dataBytes)
    {
        long count = BinaryPrimitives.ReadInt64BigEndian(table);
        int entries = (desc.BytesPerPacket == 0 ? 1 : 0) + (desc.FramesPerPacket == 0 ? 1 : 0);
        if (count > _context.Limits.MaxIndexEntries)
        {
            throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, count);
        }

        if (count < 0 || count * entries > table.Length - CafChunks.PacketTableHeader)
        {
            throw _input.Corrupt($"The packet table says {count} packets but holds {table.Length - CafChunks.PacketTableHeader} bytes.");
        }

        long[] offsets = new long[count + 1];
        long[] frames = new long[count + 1];
        int at = CafChunks.PacketTableHeader;
        for (int i = 0; i < count; i++)
        {
            long size = desc.BytesPerPacket, length = desc.FramesPerPacket;
            if ((desc.BytesPerPacket == 0 && !CafChunks.TryReadVarint(table, ref at, out size))
                || (desc.FramesPerPacket == 0 && !CafChunks.TryReadVarint(table, ref at, out length)))
            {
                throw _input.Corrupt($"The packet table ends at packet {i} of {count}.");
            }

            if (length > MaxPacketFrames)
            {
                throw _input.Corrupt($"The packet table says packet {i} holds {length} frames.");
            }

            offsets[i + 1] = offsets[i] + size;
            frames[i + 1] = frames[i] + length;
            if (offsets[i + 1] > dataBytes || frames[i + 1] < frames[i])
            {
                // A cut-off file: keep the packets that are there.
                return (offsets[..(i + 1)], frames[..(i + 1)]);
            }
        }

        return (offsets, frames);
    }

    private EncodedPacket? ReadVariable()
    {
        long[] offsets = _offsets!;
        long[] frames = _frames!;
        if (_next >= offsets.Length - 1)
        {
            return null;
        }

        int index = _next;
        long size = offsets[index + 1] - offsets[index];
        if (size > _context.Limits.MaxPacketBytes)
        {
            throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, size);
        }

        long position = _dataStart + offsets[index];
        if (_input.Position != position)
        {
            _input.Seek(position);
        }

        EncodedPacket packet = EncodedPacket.Rent(_track.Number, (int)size, new MediaTime(frames[index] - _priming, _track.TimeBase));
        if (_input.Read(packet.GetWritableData().Span) < size)
        {
            packet.Dispose();
            _next = offsets.Length;
            return null;
        }

        _next++;
        long length = frames[index + 1] - frames[index];
        packet.Duration = new MediaTime(Math.Clamp(_validEnd - frames[index], 0, length), _track.TimeBase);
        packet.IsKeyframe = true;
        return packet;
    }

    private EncodedPacket? ReadConstant()
    {
        long position = _input.Position;
        long left = _dataEnd - position;
        if (left < _bytesPerPacket)
        {
            return null;
        }

        long packets = Math.Max(1, PacketFrames / _framesPerPacket);
        long wanted = Math.Min(packets * _bytesPerPacket, left / _bytesPerPacket * _bytesPerPacket);
        int length = (int)Math.Min(wanted, Math.Max(_bytesPerPacket, _context.Limits.MaxPacketBytes / _bytesPerPacket * _bytesPerPacket));

        long frame = (position - _dataStart) / _bytesPerPacket * _framesPerPacket;
        EncodedPacket packet = EncodedPacket.Rent(_track.Number, length, new MediaTime(frame - _priming, _track.TimeBase));
        int read = _input.Read(packet.GetWritableData().Span);
        if (read < length)
        {
            // A cut-off or streamed file: keep the whole packets that are there.
            int whole = read / _bytesPerPacket * _bytesPerPacket;
            EncodedPacket full = packet;
            if (whole == 0)
            {
                full.Dispose();
                return null;
            }

            packet = EncodedPacket.Rent(_track.Number, whole, full.Timestamp);
            full.Data.Span[..whole].CopyTo(packet.GetWritableData().Span);
            full.Dispose();
            length = whole;
        }

        if (_swap > 1)
        {
            Span<byte> data = packet.GetWritableData().Span[..length];
            for (int i = 0; i < data.Length; i += _swap)
            {
                data.Slice(i, _swap).Reverse();
            }
        }

        long frames = (long)length / _bytesPerPacket * _framesPerPacket;
        packet.Duration = new MediaTime(Math.Clamp(_validEnd - frame, 0, frames), _track.TimeBase);
        packet.IsKeyframe = true;
        return packet;
    }

    /// <summary>Chapters from regions (start to end) when the file has them, else from markers (each to the next).</summary>
    private static List<ChapterInfo> Chapters(List<(uint Id, double Frame)> markers, List<(uint Id, double Start, double End)> regions, Dictionary<uint, string> strings, Rational timeBase, MediaTime? duration)
    {
        List<ChapterInfo> chapters = [];
        if (regions.Count > 0)
        {
            foreach ((uint id, double start, double end) in regions.OrderBy(r => r.Start))
            {
                chapters.Add(new ChapterInfo(new MediaTime((long)start, timeBase), end > start ? new MediaTime((long)end, timeBase) : null, strings.GetValueOrDefault(id)));
            }

            return chapters;
        }

        markers.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        for (int i = 0; i < markers.Count; i++)
        {
            MediaTime start = new((long)markers[i].Frame, timeBase);
            MediaTime? end = i + 1 < markers.Count ? new MediaTime((long)markers[i + 1].Frame, timeBase) : duration;
            chapters.Add(new ChapterInfo(start, end, strings.GetValueOrDefault(markers[i].Id)));
        }

        return chapters;
    }
}
