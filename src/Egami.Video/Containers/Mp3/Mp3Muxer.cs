using Egami.Video.Audio.Codecs.Mp3;

namespace Egami.Video.Containers.Mp3;

/// <summary>
/// Writes MPEG audio files: an ID3v2.4 tag, then the frames as they come. On a seekable stream a Layer III file starts with a
/// silent Xing frame that is filled in at the end with the frame and byte counts, a 100-entry seek table and, when the track's
/// codec delay is known, a LAME-layout tag with the encoder delay and padding, so readers trim it gaplessly.
/// </summary>
internal sealed class Mp3Muxer : IMuxer
{
    private readonly OutputStream _out;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private readonly AudioTrackInfo _track;
    private readonly Rational _sampleTime;
    private long _skip;
    private readonly List<long> _frameOffsets = [];
    private long _xingAt = -1;
    private byte[]? _xing;
    private int _samplesPerFrame;
    private int _bitrateIndex = -1;
    private bool _constant = true;
    private MediaTime? _end;
    private bool _finished;

    public Mp3Muxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks is not [AudioTrackInfo track] || track.CodecId is not (CodecIds.Mp3 or CodecIds.Mp2 or CodecIds.Mp1))
        {
            throw new NotSupportedException("MP3 files hold one MPEG audio track.");
        }

        _stream = stream;
        _out = new OutputStream(stream);
        _leaveOpen = leaveOpen;
        _inputNumber = track.Number;
        _track = track;
        _sampleTime = new Rational(1, Math.Max(track.SampleRate, 1));
        _skip = track.CodecDelay is { } delay ? delay.Rescale(_sampleTime).Ticks : 0;
        Tracks = [track with { Number = 1 }];

        List<AttachmentInfo> pictures = [.. metadata.Attachments.Where(a => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))];
        if (pictures.Count < metadata.Attachments.Count)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "ID3 holds tags, pictures and chapters but no other attachments; they are left out."));
        }

        if (Id3Tag.Write(metadata with { Attachments = pictures }, track.Duration) is { } tag)
        {
            _out.Write(tag);
        }
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

        ReadOnlySpan<byte> data = packet.Data.Span;
        if (_frameOffsets.Count == 0 && _skip == 0 && packet.Timestamp < MediaTime.Zero)
        {
            // An MP4 edit list gives the delay as the first packet's negative time instead of a codec delay.
            _skip = -packet.Timestamp.Rescale(_sampleTime).Ticks;
        }

        if (_xingAt < 0 && _xing is null && _out.CanSeek && MpegAudioHeader.IsValid(data))
        {
            WriteXingFrame(new MpegAudioHeader(data));
        }

        // A packet may hold several frames (or, from AVI, part of one): note where each whole frame starts for the seek table.
        long at = _out.Position;
        int p = 0;
        while (p + MpegAudioHeader.Size <= data.Length && MpegAudioHeader.IsValid(data[p..]))
        {
            MpegAudioHeader header = new(data[p..]);
            int total = header.TotalBytes(0);
            _samplesPerFrame = header.SamplesPerFrame;
            _frameOffsets.Add(at + p);
            _constant &= _bitrateIndex < 0 || header.BitrateIndex == _bitrateIndex;
            _bitrateIndex = header.BitrateIndex;
            if (total == 0)
            {
                break;
            }

            p += total;
        }

        _out.Write(data);
        MediaTime end = packet.Timestamp + (packet.Duration ?? new MediaTime(_samplesPerFrame, _sampleTime));
        _end = _end is { } e && e > end ? e : end;
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_xing is null || !_out.CanSeek)
        {
            _out.Flush();
            return;
        }

        long fileEnd = _out.Position;
        long frames = _frameOffsets.Count;
        long bytes = fileEnd - _xingAt;
        Span<byte> toc = stackalloc byte[100];
        for (int i = 0; i < 100; i++)
        {
            long offset = frames == 0 ? 0 : _frameOffsets[(int)Math.Min(frames - 1, i * frames / 100)] - _xingAt;
            toc[i] = (byte)Math.Clamp(offset * 256 / Math.Max(bytes, 1), 0, 255);
        }

        // The delay the decoder adds (529) is part of the track's codec delay; padding is what is left after the source's end.
        int? delay = null;
        int? padding = null;
        if (_skip >= XingHeader.DecoderDelay && _end is { } end)
        {
            long validEnd = end.Rescale(_sampleTime).Ticks + _skip;
            delay = (int)(_skip - XingHeader.DecoderDelay);
            padding = (int)Math.Clamp((frames * _samplesPerFrame) - validEnd + XingHeader.DecoderDelay, 0, 4095);
        }

        // The LAME tag names the encoder that made the frames when the track says (an MP3's LAME tag, Matroska's track ENCODER tag).
        string encoder = _track.Tags.TryGetValue("ENCODER", out string? name) && name.Length > 0 ? name : "Egami";
        XingHeader.Write(_xing, _constant, frames, bytes, toc, delay, padding, encoder);
        _out.Position = _xingAt;
        _out.Write(_xing);
        _out.Position = fileEnd;
        _out.Flush();
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

    /// <summary>Writes a silent frame like the first one, without CRC, at the lowest bit rate that holds the Xing fields and LAME tag.</summary>
    private void WriteXingFrame(MpegAudioHeader first)
    {
        if (first.Layer != 3 || first.IsFreeFormat)
        {
            return;
        }

        byte[] bytes = new byte[MpegAudioHeader.Size];
        first.Write(bytes);
        bytes[1] |= 1;
        MpegAudioHeader header = new(bytes);
        int need = MpegAudioHeader.Size + header.SideInfoBytes + XingHeader.WrittenBytes;
        for (int index = 1; index < 15; index++)
        {
            MpegAudioHeader candidate = header.WithBitrate(index, false);
            int total = candidate.TotalBytes(0);
            if (total >= need)
            {
                _xing = new byte[total];
                candidate.Write(_xing);
                _xingAt = _out.Position;
                _out.Write(_xing);
                return;
            }
        }
    }
}
