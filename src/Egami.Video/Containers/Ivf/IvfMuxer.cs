using System.Buffers.Binary;

namespace Egami.Video.Containers.Ivf;

/// <summary>
/// Writes IVF from one VP8, VP9 or AV1 track: the 32-byte file header, then each packet behind its 12-byte frame header, with
/// the timestamp in the track's time base. The frame count is patched at the end when the stream can seek.
/// </summary>
internal sealed class IvfMuxer : IMuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private readonly Rational _timeBase;
    private readonly long _start;
    private uint _frames;
    private bool _finished;

    public IvfMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, bool leaveOpen)
    {
        if (tracks is not [VideoTrackInfo track] || IvfCodecs.FourCC(track.CodecId) is not { } fourCC)
        {
            throw new NotSupportedException("IVF holds one VP8, VP9 or AV1 video track.");
        }

        if (track.Width is < 0 or > ushort.MaxValue || track.Height is < 0 or > ushort.MaxValue)
        {
            throw new NotSupportedException($"IVF holds sizes up to {ushort.MaxValue} pixels; the track is {track.Width}×{track.Height}.");
        }

        if (track.TimeBase.Numerator is <= 0 or > uint.MaxValue || track.TimeBase.Denominator > uint.MaxValue)
        {
            throw new NotSupportedException($"IVF cannot store the time base {track.TimeBase}.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _inputNumber = track.Number;
        _timeBase = track.TimeBase;
        _start = stream.CanSeek ? stream.Position : 0;
        Tracks = [track with { Number = 1 }];

        Span<byte> header = stackalloc byte[IvfDemuxer.FileHeaderSize];
        "DKIF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], IvfDemuxer.FileHeaderSize);
        System.Text.Encoding.ASCII.GetBytes(fourCC, header[8..12]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)track.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)track.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)track.TimeBase.Denominator);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)track.TimeBase.Numerator);
        _stream.Write(header);
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished)
        {
            throw new InvalidOperationException("The muxer was finished.");
        }

        if (packet.TrackNumber != _inputNumber)
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        Span<byte> header = stackalloc byte[IvfDemuxer.FrameHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)packet.Data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[4..], packet.Timestamp.Rescale(_timeBase).Ticks);
        _stream.Write(header);
        _stream.Write(packet.Data.Span);
        _frames++;
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_stream.CanSeek)
        {
            long end = _stream.Position;
            Span<byte> count = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(count, _frames);
            _stream.Position = _start + 24;
            _stream.Write(count);
            _stream.Position = end;
        }

        _stream.Flush();
    }

    public void Dispose()
    {
        Finish();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
