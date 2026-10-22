using System.Buffers.Binary;

using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Containers.Ivf;

/// <summary>
/// Reads IVF: the 32-byte file header ("DKIF", version, header size, FourCC, width, height, time base as rate and scale, frame
/// count, unused) and frames, each a 12-byte header (size, 64-bit timestamp) and the frame's bytes. IVF has no keyframe flags or
/// index, so keyframes come from the frames' own bits and seeking scans the frame headers once.
/// </summary>
internal sealed class IvfDemuxer : IDemuxer
{
    public const int FileHeaderSize = 32;
    public const int FrameHeaderSize = 12;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly VideoTrackInfo _track;
    private readonly long _firstFrame;
    private readonly List<IndexEntry> _index = [];
    private bool _indexComplete;

    public IvfDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, IvfFormat.Instance.Name);
        _context = context;
        try
        {
            Span<byte> header = stackalloc byte[FileHeaderSize];
            _input.ReadExactly(header);
            if (!header.StartsWith("DKIF"u8))
            {
                throw _input.Corrupt(0, "The file does not start with DKIF.");
            }

            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            if (headerSize < FileHeaderSize)
            {
                throw _input.Corrupt(6, $"The header size is {headerSize}, less than {FileHeaderSize}.");
            }

            // A longer header carries bytes no writer defines; they are skipped.
            if (!_input.Skip(headerSize - FileHeaderSize))
            {
                throw _input.Corrupt("The data ends inside the file header.");
            }

            string fourCC = System.Text.Encoding.ASCII.GetString(header[8..12]);
            string codecId = IvfCodecs.CodecId(fourCC) ?? SniffVp8(fourCC)
                ?? throw new UnsupportedFormatException($"IVF FourCC '{IvfCodecs.Printable(fourCC)}' is not one Egami.Video knows (VP80, VP90, AV01).");
            int width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            uint rate = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            uint scale = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            if (rate == 0 || scale == 0)
            {
                throw _input.Corrupt(16, $"The time base {scale}/{rate} has a zero.");
            }

            SafetyLimits limits = context.Egami.Limits;
            if (width > limits.MaxWidth || height > limits.MaxHeight || (long)width * height > limits.MaxPixels)
            {
                throw new LimitExceededException("MaxPixels", limits.MaxPixels, (long)width * height);
            }

            _firstFrame = _input.Position;
            (int vp9Depth, ChromaFormat? vp9Chroma) = codecId == CodecIds.Vp9 ? SniffVp9() : (0, null);
            _track = new VideoTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Video,
                CodecId = codecId,
                TimeBase = new Rational(scale, rate),
                Width = width,
                Height = height,
                ChromaFormat = codecId == CodecIds.Vp9 ? vp9Chroma : ChromaFormat.Yuv420,
                BitDepth = codecId == CodecIds.Vp8 ? 8 : vp9Depth,
            };
            if (codecId == CodecIds.Av1)
            {
                _track = SniffAv1(_track);
            }
            Tracks = [_track];
            Info = new MediaInfo { FormatName = IvfFormat.Instance.Name, Tracks = Tracks };
            FrameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            Unused = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
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

    /// <summary>Gets the frame count the header gives (0 from writers that could not patch it).</summary>
    public uint FrameCount { get; }

    /// <summary>Gets the header's last 4 bytes, which no writer defines; kept so a rewrite gives the same bytes.</summary>
    public uint Unused { get; }

    public EncodedPacket? ReadPacket()
    {
        long position = _input.Position;
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        int read = _input.Read(header);
        if (read < FrameHeaderSize)
        {
            // A cut-off last frame header is the end of a truncated file, not an error.
            return null;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        long pts = BinaryPrimitives.ReadInt64LittleEndian(header[4..]);
        if (size > (uint)_context.Limits.MaxPacketBytes)
        {
            throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, size);
        }

        if (size > _input.Remaining)
        {
            // The last frame was cut off.
            _input.Skip(_input.Remaining);
            return null;
        }

        EncodedPacket packet = EncodedPacket.Rent(_track.Number, (int)size, new MediaTime(pts, _track.TimeBase));
        if (_input.Read(packet.GetWritableData().Span) < size)
        {
            packet.Dispose();
            return null;
        }

        packet.IsKeyframe = FrameKind.IsKeyframe(_track.CodecId, packet.Data.Span) ?? position == _firstFrame;
        if (!_indexComplete && (_index.Count == 0 || _index[^1].Position < position))
        {
            _index.Add(new IndexEntry(position, pts, packet.IsKeyframe));
        }

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
        long target = _firstFrame;
        bool found = false;
        foreach (IndexEntry entry in _index)
        {
            if (!entry.Keyframe)
            {
                continue;
            }

            if (!found || new MediaTime(entry.Pts, _track.TimeBase) <= time)
            {
                target = entry.Position;
                found = true;
            }
        }

        _input.Seek(target);
    }

    public void Dispose() => _input.Dispose();

    /// <summary>
    /// Returns VP8 when a file with an unknown FourCC starts with a VP8 keyframe (its start code 9D 01 2A), as some of libvpx's own
    /// VP8 test vectors do with "I420", which libvpx's tools decode as VP8; reports it as an "ivf-fourcc" warning.
    /// </summary>
    /// <summary>Reads the bit depth and subsampling of a VP9 stream from its first frame, when that is a keyframe.</summary>
    private (int BitDepth, ChromaFormat? Chroma) SniffVp9()
    {
        if (!_input.CanSeek)
        {
            return (0, null);
        }

        long start = _input.Position;
        // The uncompressed header ends within a few dozen bytes; 512 hold it with any segmentation and loop filter data.
        byte[] first = new byte[FrameHeaderSize + 512];
        int got = _input.Read(first);
        if (got >= FrameHeaderSize)
        {
            got = (int)Math.Min(got, FrameHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(first));
        }

        _input.Seek(start);
        if (got <= FrameHeaderSize)
        {
            return (0, null);
        }

        try
        {
            Codecs.Vp9.Vp9FrameHeader h = Codecs.Vp9.Vp9FrameHeader.Read(first.AsSpan(FrameHeaderSize, got - FrameHeaderSize), _ => (0, 0));
            if (!h.KeyFrame)
            {
                return (0, null);
            }

            ChromaFormat? chroma = (h.SubsamplingX, h.SubsamplingY) switch
            {
                (1, 1) => ChromaFormat.Yuv420,
                (1, 0) => ChromaFormat.Yuv422,
                (0, 0) => ChromaFormat.Yuv444,
                _ => null,
            };
            return (h.BitDepth, chroma);
        }
        catch (CorruptDataException)
        {
            return (0, null);
        }
    }

    /// <summary>
    /// Fills an AV1 track's bit depth, chroma format and av1C (<see cref="TrackInfo.CodecPrivate"/>, as MP4 and Matroska store
    /// it) from the sequence header in the first frame, when there is one.
    /// </summary>
    private VideoTrackInfo SniffAv1(VideoTrackInfo track)
    {
        if (!_input.CanSeek)
        {
            return track;
        }

        long start = _input.Position;
        byte[] first = new byte[FrameHeaderSize + 4096];
        int got = _input.Read(first);
        _input.Seek(start);
        if (got <= FrameHeaderSize)
        {
            return track;
        }

        got = (int)Math.Min(got, FrameHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(first));
        return Av1Packets.FillTrack(track, first.AsSpan(FrameHeaderSize, got - FrameHeaderSize));
    }

    private string? SniffVp8(string fourCC)
    {
        if (!_input.CanSeek)
        {
            return null;
        }

        long start = _input.Position;
        Span<byte> first = stackalloc byte[FrameHeaderSize + 6];
        bool vp8 = _input.TryReadExactly(first) && (first[FrameHeaderSize] & 1) == 0 && first[(FrameHeaderSize + 3)..].SequenceEqual((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A]);
        _input.Seek(start);
        if (vp8)
        {
            _context.Warnings?.Invoke(new MediaWarning("ivf-fourcc", $"The IVF header names FourCC '{IvfCodecs.Printable(fourCC)}', but the first frame is a VP8 keyframe; read as VP8.", 1));
        }

        return vp8 ? CodecIds.Vp8 : null;
    }

    private void BuildIndex()
    {
        if (_indexComplete)
        {
            return;
        }

        long resume = _input.Position;
        long position = _index.Count == 0 ? _firstFrame : _index[^1].Position;
        bool skipFirst = _index.Count > 0;
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        Span<byte> start = new byte[FrameKind.BytesNeeded(_track.CodecId)];
        while (true)
        {
            _input.Seek(position);
            if (_input.Read(header) < FrameHeaderSize)
            {
                break;
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (size > _input.Remaining)
            {
                break;
            }

            if (!skipFirst)
            {
                if (_index.Count >= _context.Limits.MaxIndexEntries)
                {
                    throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, _index.Count + 1);
                }

                // Only the first bytes of each frame are read: enough to tell a keyframe.
                int got = _input.Read(start[..(int)Math.Min(size, start.Length)]);
                bool key = FrameKind.IsKeyframe(_track.CodecId, start[..got]) ?? position == _firstFrame;
                _index.Add(new IndexEntry(position, BinaryPrimitives.ReadInt64LittleEndian(header[4..]), key));
            }

            skipFirst = false;
            position += FrameHeaderSize + size;
        }

        _indexComplete = true;
        _input.Seek(resume);
    }

    private readonly record struct IndexEntry(long Position, long Pts, bool Keyframe);
}
