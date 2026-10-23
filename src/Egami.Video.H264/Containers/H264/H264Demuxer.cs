using Egami.Colors;
using Egami.Nal;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.Containers.H264;

/// <summary>
/// Reads a raw Annex B H.264 stream: finds the NAL units between start codes a piece of the stream at a time, groups them into
/// access units with <see cref="NalAccessUnitScanner"/> and gives each as an Annex B packet. Keyframes are the access units with an
/// IDR picture; seeking goes back to the last IDR at or before the time, scanning forward once when it has not been read yet.
/// </summary>
internal sealed class H264Demuxer : IDemuxer
{
    private const int ChunkSize = 1 << 16;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly MediaContext _context;
    private readonly VideoTrackInfo _track;
    private readonly NalAccessUnitScanner _scanner = new();
    private readonly Queue<(List<byte[]> Units, long Offset)> _ready = new();
    private readonly List<(long Frame, long Offset)> _keyframes = [];
    private readonly List<byte[]> _units = [];
    private byte[] _buffer = new byte[ChunkSize];
    private long _bufferOffset;
    private int _length;
    private int _nalStart = -1;
    private long _nalOffset;
    private int _searchFrom;
    private bool _endOfStream;
    private long _accessUnitOffset;
    private long _frame;
    private bool _disposed;

    public H264Demuxer(Stream stream, MediaContext context, bool leaveOpen, Rational? frameRate, string formatName)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _context = context;
        try
        {
            _bufferOffset = stream.CanSeek ? stream.Position : 0;
            H264Sps sps = FindSps() ?? throw new UnsupportedFormatException("The H.264 stream has no sequence parameter set.");
            Rational rate = frameRate ?? sps.Vui?.FrameRate ?? new Rational(25, 1);
            ColorCodePoints? color = sps.Vui is { } vui && (vui.ColourPrimaries != 2 || vui.TransferCharacteristics != 2 || vui.MatrixCoefficients != 2)
                ? new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange)
                : null;
            _track = new VideoTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Video,
                CodecId = CodecIds.Avc,
                TimeBase = rate.Reciprocal(),
                FrameRate = rate,
                Width = sps.CroppedWidth,
                Height = sps.CroppedHeight,
                BitDepth = Math.Max(sps.BitDepthLuma, sps.BitDepthChroma),
                ChromaFormat = sps.ChromaFormatIdc switch { 0 => ChromaFormat.Yuv400, 1 => ChromaFormat.Yuv420, _ => ChromaFormat.Yuv422 },
                ChromaSiting = sps.Vui is { } v ? H264VideoDecoder.SitingOf(v.ChromaSampleLocation) : null,
                Color = color,
                PixelAspectRatio = sps.Vui is { SarWidth: > 0 } s ? new Rational(s.SarWidth, s.SarHeight) : new Rational(1, 1),
            };
            Tracks = [_track];
            Info = new MediaInfo { FormatName = formatName, Tracks = Tracks };
        }
        catch
        {
            if (!leaveOpen)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    public MediaInfo Info { get; }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public bool CanSeek => _stream.CanSeek;

    public EncodedPacket? ReadPacket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NextAccessUnit(out List<byte[]>? units, out long offset))
        {
            return null;
        }

        int size = 0;
        bool idr = false;
        foreach (byte[] unit in units)
        {
            size += 4 + unit.Length;
            idr |= (unit[0] & 31) == 5;
        }

        if (size > _context.Limits.MaxPacketBytes)
        {
            throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, size);
        }

        EncodedPacket packet = EncodedPacket.Rent(_track.Number, size, new MediaTime(_frame, _track.TimeBase));
        Span<byte> data = packet.GetWritableData().Span;
        int at = 0;
        foreach (byte[] unit in units)
        {
            data[at + 3] = 1;
            data[at..(at + 3)].Clear();
            unit.CopyTo(data[(at + 4)..]);
            at += 4 + unit.Length;
        }

        packet.IsKeyframe = idr;
        packet.Duration = new MediaTime(1, _track.TimeBase);
        if (idr && (_keyframes.Count == 0 || _keyframes[^1].Frame < _frame))
        {
            _keyframes.Add((_frame, offset));
        }

        _frame++;
        return packet;
    }

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        long target = Math.Max(0, time.Rescale(_track.TimeBase).Ticks);
        while ((_keyframes.Count == 0 || _keyframes[^1].Frame < target) && ReadPacket() is { } skipped)
        {
            skipped.Dispose();
        }

        (long frame, long offset) = (0, -1);
        foreach ((long Frame, long Offset) entry in _keyframes)
        {
            if (entry.Frame <= target)
            {
                (frame, offset) = entry;
            }
        }

        if (offset < 0)
        {
            (frame, offset) = _keyframes.Count > 0 ? _keyframes[0] : (0, 0);
        }

        _stream.Position = offset;
        _bufferOffset = offset;
        _length = 0;
        _nalStart = -1;
        _searchFrom = 0;
        _endOfStream = false;
        _scanner.Reset();
        _units.Clear();
        _ready.Clear();
        _frame = frame;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Gets what a NAL unit means for the access unit boundary (7.4.1.2.3).</summary>
    internal static NalUnitRole RoleOf(byte[] unit) => (unit[0] & 31) switch
    {
        // first_mb_in_slice is ue(v): a first bit of 1 codes 0, the picture's first slice (slices come in order without ASO).
        1 or 2 or 5 => unit.Length > 1 && (unit[1] & 0x80) != 0 ? NalUnitRole.FirstSlice : NalUnitRole.Slice,
        6 or 7 or 8 or 9 or 13 or 15 or 16 or 17 or 18 => NalUnitRole.Prefix,
        _ => NalUnitRole.Continues,
    };

    private H264Sps? FindSps()
    {
        // The units read here stay queued, so the first packets still hold them.
        long start = _bufferOffset;
        while (NextNal(out byte[] unit, out long offset))
        {
            Add(unit, offset);
            if ((unit[0] & 31) == 7)
            {
                return H264Sps.Read(NalFraming.Unescape(unit), _context.Egami.Limits);
            }

            if (_bufferOffset + _length - start > _context.Limits.MaxHeaderBytes)
            {
                return null;
            }
        }

        Complete(_scanner.Flush());
        return null;
    }

    private bool NextAccessUnit(out List<byte[]> units, out long offset)
    {
        while (_ready.Count == 0)
        {
            if (!NextNal(out byte[] unit, out long at))
            {
                Complete(_scanner.Flush());
                if (_ready.Count == 0)
                {
                    units = [];
                    offset = 0;
                    return false;
                }

                break;
            }

            Add(unit, at);
        }

        (units, offset) = _ready.Dequeue();
        return true;
    }

    private void Add(byte[] unit, long offset)
    {
        if (_scanner.Pending == 0)
        {
            _accessUnitOffset = offset;
        }

        long current = _accessUnitOffset;
        if (_scanner.Add(unit, RoleOf(unit)) is { } completed)
        {
            _ready.Enqueue((ToArrays(completed), current));
            _accessUnitOffset = offset;
        }
    }

    private void Complete(List<ReadOnlyMemory<byte>>? units)
    {
        if (units is not null)
        {
            _ready.Enqueue((ToArrays(units), _accessUnitOffset));
        }
    }

    private static List<byte[]> ToArrays(List<ReadOnlyMemory<byte>> units)
    {
        // The units are arrays the reader made, so each is taken back as it is.
        List<byte[]> arrays = new(units.Count);
        foreach (ReadOnlyMemory<byte> unit in units)
        {
            arrays.Add(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(unit, out ArraySegment<byte> segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array
                : unit.ToArray());
        }

        return arrays;
    }

    /// <summary>Reads the next NAL unit: the bytes after a start code up to the next one, without trailing zero bytes.</summary>
    private bool NextNal(out byte[] unit, out long offset)
    {
        while (true)
        {
            while (_nalStart < 0)
            {
                int found = NalFraming.IndexOfStartCode(_buffer.AsSpan(0, _length), Math.Min(_searchFrom, _length));
                if (found >= 0)
                {
                    _nalStart = found + 3;
                    _nalOffset = _bufferOffset + found;
                    _searchFrom = _nalStart;
                }
                else
                {
                    _searchFrom = Math.Max(0, _length - 2);
                    if (!Fill())
                    {
                        unit = [];
                        offset = 0;
                        return false;
                    }
                }
            }

            int end, next;
            while (true)
            {
                int found = NalFraming.IndexOfStartCode(_buffer.AsSpan(0, _length), Math.Min(_searchFrom, _length));
                if (found >= 0)
                {
                    end = found;
                    next = found + 3;
                    break;
                }

                _searchFrom = Math.Max(_nalStart, _length - 2);
                if (!Fill())
                {
                    end = _length;
                    next = -1;
                    break;
                }
            }

            int start = _nalStart;
            while (end > start && _buffer[end - 1] == 0)
            {
                end--;
            }

            offset = _nalOffset;
            if (next >= 0)
            {
                _nalStart = next;
                _nalOffset = _bufferOffset + next - 3;
                _searchFrom = next;
            }
            else
            {
                _nalStart = -1;
                _searchFrom = _length;
            }

            if (end > start)
            {
                unit = _buffer.AsSpan(start, end - start).ToArray();
                return true;
            }

            if (next < 0)
            {
                unit = [];
                return false;
            }
        }
    }

    /// <summary>Reads more of the stream, keeping the NAL unit being read. Returns false at the end of the stream.</summary>
    private bool Fill()
    {
        if (_endOfStream)
        {
            return false;
        }

        int keep = _nalStart >= 0 ? _nalStart : _searchFrom;
        int kept = _length - keep;
        if (keep > 0)
        {
            Buffer.BlockCopy(_buffer, keep, _buffer, 0, kept);
            _bufferOffset += keep;
            _searchFrom -= keep;
            if (_nalStart >= 0)
            {
                _nalStart -= keep;
            }

            _length = kept;
        }

        if (_buffer.Length - _length < ChunkSize)
        {
            if (_length > _context.Limits.MaxPacketBytes)
            {
                throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, _length);
            }

            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + ChunkSize));
        }

        int read = _stream.Read(_buffer, _length, _buffer.Length - _length);
        if (read <= 0)
        {
            _endOfStream = true;
            return false;
        }

        _length += read;
        return true;
    }
}
