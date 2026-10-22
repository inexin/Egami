using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;

namespace Egami.Video.Containers.Obu;

/// <summary>
/// Reads a raw AV1 stream: low-overhead OBUs (AV1 specification section 5.2), a temporal unit starting at each temporal
/// delimiter, as aomdec and dav1d read .obu files; or the length-delimited format of Annex B (temporal_unit_size, frame_unit_size
/// and obu_length, each leb128), whose OBUs are given out with their size field, as low-overhead units. Neither format has
/// timestamps, so temporal unit n is at n frames of the caller's frame rate. Keyframes come from the units' own frame headers;
/// seeking reads the file once to find them.
/// </summary>
internal sealed class Av1ObuDemuxer : IDemuxer
{
    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly bool _annexB;
    private readonly VideoTrackInfo _track;
    private readonly long _first;
    private readonly List<(long Position, bool Key)> _index = [];
    private bool _indexComplete;
    private long _next;

    public Av1ObuDemuxer(Stream stream, MediaContext context, bool leaveOpen, Rational frameRate, bool annexB, string formatName)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), frameRate, "The frame rate must be positive.");
        }

        _input = new MediaInput(stream, leaveOpen, formatName);
        _context = context;
        _annexB = annexB;
        try
        {
            _first = _input.Position;
            VideoTrackInfo track = new()
            {
                Number = 1,
                Kind = TrackKind.Video,
                CodecId = CodecIds.Av1,
                TimeBase = frameRate.Reciprocal(),
                FrameRate = frameRate,
                Width = 0,
                Height = 0,
            };

            // The first unit gives the size, bit depth, chroma format and av1C. On a stream that cannot seek it is read again
            // as the first packet from the replay buffer the detection left.
            if (_input.CanSeek && ReadUnit() is { } unit)
            {
                _input.Seek(_first);
                track = Av1Packets.FillTrack(track, unit);
                if (Av1SequenceInfo.Find(unit) is { } sequence)
                {
                    track = track with { Width = sequence.MaxFrameWidth, Height = sequence.MaxFrameHeight };
                }
            }

            _track = track;
            Tracks = [_track];
            Info = new MediaInfo { FormatName = formatName, Tracks = Tracks };
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
        long position = _input.Position;
        if (ReadUnit() is not { } unit)
        {
            return null;
        }

        EncodedPacket packet = new(_track.Number, unit, new MediaTime(_next, _track.TimeBase))
        {
            Duration = new MediaTime(1, _track.TimeBase),
            IsKeyframe = FrameKind.IsKeyframe(CodecIds.Av1, unit) ?? _next == 0,
        };
        if (!_indexComplete && _index.Count == _next)
        {
            _index.Add((position, packet.IsKeyframe));
        }

        _next++;
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

        BuildIndex();
        int target = 0;
        for (int i = 0; i < _index.Count; i++)
        {
            if (_index[i].Key && new MediaTime(i, _track.TimeBase) <= time)
            {
                target = i;
            }
        }

        _next = target;
        _input.Seek(_index.Count == 0 ? _first : _index[target].Position);
    }

    public void Dispose() => _input.Dispose();

    private void BuildIndex()
    {
        if (_indexComplete)
        {
            return;
        }

        long resume = _input.Position;
        long next = _next;
        _next = _index.Count;
        _input.Seek(_index.Count == 0 ? _first : _index[^1].Position);
        if (_index.Count > 0)
        {
            ReadUnit();
        }

        while (true)
        {
            if (_index.Count >= _context.Limits.MaxIndexEntries)
            {
                throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, _index.Count + 1);
            }

            long position = _input.Position;
            if (ReadUnit() is not { } unit)
            {
                break;
            }

            _index.Add((position, FrameKind.IsKeyframe(CodecIds.Av1, unit) ?? _index.Count == 0));
        }

        _indexComplete = true;
        _next = next;
        _input.Seek(resume);
    }

    /// <summary>Reads the next temporal unit as low-overhead OBUs, or returns null at the end of the data.</summary>
    private byte[]? ReadUnit() => _annexB ? ReadAnnexBUnit() : ReadLowOverheadUnit();

    private byte[]? ReadLowOverheadUnit()
    {
        if (_input.PeekByte() < 0)
        {
            return null;
        }

        using MemoryStream unit = new();
        bool first = true;
        Span<byte> header = stackalloc byte[2];
        while (_input.PeekByte() is int b and >= 0)
        {
            long start = _input.Position;
            if (!first && ((b >> 3) & 15) == Av1Obus.TemporalDelimiter)
            {
                break;
            }

            first = false;
            int headerLength = (b & 4) != 0 ? 2 : 1;
            if (!_input.TryReadExactly(header[..headerLength]))
            {
                throw _input.Corrupt(start, "The data ends inside an OBU header.");
            }

            if ((header[0] & 0x80) != 0)
            {
                throw _input.Corrupt(start, "An AV1 OBU header has its forbidden bit set.");
            }

            if ((header[0] & 2) == 0)
            {
                throw _input.Corrupt(start, "An OBU has no size field, which a low-overhead OBU stream needs on every OBU.");
            }

            unit.Write(header[..headerLength]);
            ulong size = ReadLeb128(unit);
            if (unit.Length + (long)size > _context.Limits.MaxPacketBytes)
            {
                throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, unit.Length + (long)size);
            }

            if (!Copy(unit, (long)size))
            {
                // A cut-off last OBU ends the stream, as a cut-off last IVF frame does.
                return null;
            }
        }

        return unit.ToArray();
    }

    private byte[]? ReadAnnexBUnit()
    {
        if (_input.PeekByte() < 0)
        {
            return null;
        }

        long start = _input.Position;
        ulong size = ReadLeb128(null);
        if (size > (ulong)_context.Limits.MaxPacketBytes)
        {
            throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, (long)size);
        }

        if ((long)size > _input.Remaining)
        {
            // The last temporal unit was cut off.
            _input.Skip(_input.Remaining);
            return null;
        }

        byte[] data = new byte[(int)size];
        if (!_input.TryReadExactly(data))
        {
            return null;
        }

        using MemoryStream unit = new();
        int at = 0;
        while (at < data.Length)
        {
            (ulong frameSize, int length) = Leb128(data, at, start);
            at += length;
            if (frameSize > (ulong)(data.Length - at))
            {
                throw _input.Corrupt(start, "An Annex B frame unit runs past its temporal unit.");
            }

            int frameEnd = at + (int)frameSize;
            while (at < frameEnd)
            {
                (ulong obuLength, int n) = Leb128(data, at, start);
                at += n;
                if (obuLength == 0 || at > frameEnd || obuLength > (ulong)(frameEnd - at))
                {
                    throw _input.Corrupt(start, "An Annex B OBU runs past its frame unit.");
                }

                ReadOnlySpan<byte> obuBytes = data.AsSpan(at, (int)obuLength);
                Av1Obu obu;
                try
                {
                    obu = Av1Obus.Read(obuBytes, 0);
                }
                catch (CorruptDataException e)
                {
                    throw _input.Corrupt(start, e.Message);
                }

                if (obu.End != obuBytes.Length)
                {
                    throw _input.Corrupt(start, "An Annex B OBU's obu_size disagrees with its obu_length.");
                }

                Av1Obus.WriteWithSize(unit, obuBytes, obu);
                at += (int)obuLength;
            }
        }

        return unit.ToArray();
    }

    /// <summary>Reads a leb128 value from the input, copying its bytes to <paramref name="copy"/> when given.</summary>
    private ulong ReadLeb128(MemoryStream? copy)
    {
        long start = _input.Position;
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            int b = _input.PeekByte();
            if (b < 0)
            {
                throw _input.Corrupt(start, "The data ends inside a leb128 size.");
            }

            _input.ReadByte();
            copy?.WriteByte((byte)b);
            value |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
            {
                return value <= uint.MaxValue ? value : throw _input.Corrupt(start, "A leb128 size is over 32 bits.");
            }
        }

        throw _input.Corrupt(start, "A leb128 size runs over 8 bytes.");
    }

    private (ulong Value, int Length) Leb128(byte[] data, int at, long unitStart)
    {
        (ulong value, int length) = Av1Obus.ReadLeb128(data.AsSpan(at));
        return length < 0 ? throw _input.Corrupt(unitStart, "A leb128 size in an Annex B temporal unit is cut off or over 32 bits.") : (value, length);
    }

    private bool Copy(MemoryStream to, long count)
    {
        byte[] buffer = new byte[(int)Math.Min(count, 1 << 16)];
        while (count > 0)
        {
            int n = _input.Read(buffer.AsSpan(0, (int)Math.Min(count, buffer.Length)));
            if (n == 0)
            {
                return false;
            }

            to.Write(buffer, 0, n);
            count -= n;
        }

        return true;
    }
}
