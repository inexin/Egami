namespace Egami.Video.Containers.Ac3;

/// <summary>Writes raw AC-3 or E-AC-3: the packets' sync frames back to back, with nothing around them.</summary>
internal sealed class Ac3Muxer : IMuxer
{
    private readonly Stream _stream;
    private readonly OutputStream _out;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private bool _finished;

    public Ac3Muxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks is not [AudioTrackInfo track] || track.CodecId is not (CodecIds.Ac3 or CodecIds.EAc3))
        {
            throw new NotSupportedException("A raw AC-3 file holds one AC-3 or E-AC-3 track.");
        }

        if (metadata.Tags.Count > 0 || metadata.Chapters.Count > 0 || metadata.Attachments.Count > 0)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "A raw AC-3 file holds no tags, chapters or attachments; they are left out."));
        }

        _stream = stream;
        _out = new OutputStream(stream);
        _leaveOpen = leaveOpen;
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

        _out.Write(packet.Data.Span);
    }

    public void Finish()
    {
        if (!_finished)
        {
            _finished = true;
            _out.Flush();
        }
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _out.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
