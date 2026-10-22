using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;

namespace Egami.Video.Containers.Obu;

/// <summary>
/// Writes a raw AV1 stream from one AV1 track: low-overhead OBUs, each temporal unit starting with a temporal delimiter (one is
/// added when a packet has none) and every OBU with its size field; or Annex B, each temporal unit as length-delimited frame units
/// of OBUs without their size fields (the frames split as <see cref="Av1Packets.SplitFrames"/> does). Timestamps are not stored.
/// </summary>
internal sealed class Av1ObuMuxer : IMuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _annexB;
    private readonly int _inputNumber;
    private bool _finished;

    public Av1ObuMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, bool leaveOpen, bool annexB)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks is not [VideoTrackInfo { CodecId: CodecIds.Av1 } track])
        {
            throw new NotSupportedException("A raw AV1 stream holds one AV1 video track.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _annexB = annexB;
        _inputNumber = track.Number;
        Tracks = [track with { Number = 1 }];
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

        ReadOnlySpan<byte> unit = packet.Data.Span;
        if (_annexB)
        {
            WriteAnnexB(unit);
            return;
        }

        List<Av1Obu> obus = Av1Obus.Split(unit);
        if (obus.Count == 0 || obus[0].Type != Av1Obus.TemporalDelimiter)
        {
            _stream.Write(Av1Obus.TemporalDelimiterObu);
        }

        foreach (Av1Obu obu in obus)
        {
            // OBUs that have their size field are copied as they are; one without (the last of a unit may lack it) gets it.
            if (obu.HasSize)
            {
                _stream.Write(unit[obu.Start..obu.End]);
            }
            else
            {
                Av1Obus.WriteWithSize(_stream, unit, obu);
            }
        }
    }

    public void Finish()
    {
        if (!_finished)
        {
            _finished = true;
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        Finish();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private void WriteAnnexB(ReadOnlySpan<byte> unit)
    {
        List<(int Start, int End, List<Av1Obu> Obus)> frames = Av1Packets.FrameRanges(unit);
        bool delimited = frames[0].Obus.Count > 0 && frames[0].Obus[0].Type == Av1Obus.TemporalDelimiter;
        long[] frameSizes = new long[frames.Count];
        long unitSize = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            long size = f == 0 && !delimited ? 2 : 0; // obu_length and the header of an added temporal delimiter
            foreach (Av1Obu obu in frames[f].Obus)
            {
                int length = Av1Obus.SizeWithoutSizeField(obu);
                size += Av1Obus.Leb128Length((ulong)length) + length;
            }

            frameSizes[f] = size;
            unitSize += Av1Obus.Leb128Length((ulong)size) + size;
        }

        Av1Obus.WriteLeb128(_stream, (ulong)unitSize);
        for (int f = 0; f < frames.Count; f++)
        {
            Av1Obus.WriteLeb128(_stream, (ulong)frameSizes[f]);
            if (f == 0 && !delimited)
            {
                _stream.WriteByte(1);
                _stream.WriteByte(Av1Obus.TemporalDelimiter << 3);
            }

            foreach (Av1Obu obu in frames[f].Obus)
            {
                Av1Obus.WriteLeb128(_stream, (ulong)Av1Obus.SizeWithoutSizeField(obu));
                Av1Obus.WriteWithoutSize(_stream, unit, obu);
            }
        }
    }
}
