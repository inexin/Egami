using System.Buffers.Binary;

using Egami.Video.Audio.Codecs.Flac;

namespace Egami.Video.Containers.Flac;

/// <summary>A muxer that takes a track's final setup once its encoder is flushed, such as FLAC's STREAMINFO with the MD5.</summary>
internal interface ITrackUpdatable
{
    void UpdateTrack(TrackInfo track);
}

/// <summary>
/// Writes native FLAC: "fLaC", STREAMINFO, Vorbis comments, pictures and padding, then the frames as they come. On a seekable
/// stream it rewrites STREAMINFO at the end and turns the padding into a seek table and the padding left over.
/// </summary>
internal sealed class FlacMuxer : IMuxer, ITrackUpdatable
{
    private const int PaddingBytes = 8192;
    private const int SeekPointSeconds = 10;

    private readonly OutputStream _out;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private readonly long _streamInfoAt;
    private readonly long _paddingAt;
    private readonly long _firstFrame;
    private readonly List<(long Sample, long Offset, int Samples)> _points = [];
    private FlacStreamInfo _info;
    private int _minFrame = int.MaxValue;
    private int _maxFrame;
    private long _samples;
    private bool _finished;

    public FlacMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks is not [AudioTrackInfo track] || track.CodecId != CodecIds.Flac)
        {
            throw new NotSupportedException("FLAC files hold one FLAC track.");
        }

        List<FlacBlock> source = FlacMetadata.ReadBlocks(track.CodecPrivate.Span);
        _info = source.FirstOrDefault(b => b.Type == FlacBlock.StreamInfo).Body is { } body && FlacStreamInfo.Read(body) is { } info
            ? info
            : throw new NotSupportedException("The FLAC track has no STREAMINFO in its codec private data.");
        _stream = stream;
        _out = new OutputStream(stream);
        _leaveOpen = leaveOpen;
        _inputNumber = track.Number;
        Tracks = [track with { Number = 1 }];

        Dictionary<string, string> tags = new(metadata.Tags);
        foreach (FlacBlock block in source.Where(b => b.Type == FlacBlock.VorbisComment))
        {
            Dictionary<string, string> own = [];
            FlacMetadata.ReadComments(block.Body, own);
            foreach ((string key, string value) in own)
            {
                tags.TryAdd(key, value);
            }
        }

        if (track.Layout is { } layout && layout != Audio.ChannelLayout.ForChannelCount(_info.Channels) && layout.Speakers != Audio.Speakers.None)
        {
            tags["WAVEFORMATEXTENSIBLE_CHANNEL_MASK"] = "0x" + ((uint)layout.Speakers).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        }

        List<AttachmentInfo> pictures = [.. metadata.Attachments.Where(a => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))];
        if (metadata.Chapters.Count > 0 || pictures.Count < metadata.Attachments.Count)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "FLAC holds tags and pictures but no chapters or other attachments; they are left out."));
        }

        _out.Write("fLaC"u8);
        _streamInfoAt = _out.Position;
        bool padding = stream.CanSeek;
        int count = 1 + (tags.Count > 0 ? 1 : 0) + pictures.Count + (padding ? 1 : 0);
        int written = 0;
        WriteBlock(FlacBlock.StreamInfo, _info.Write(), ++written == count);
        if (tags.Count > 0)
        {
            WriteBlock(FlacBlock.VorbisComment, FlacMetadata.WriteComments(Audio.Codecs.FlacCodec.Vendor, tags), ++written == count);
        }

        foreach (AttachmentInfo picture in pictures)
        {
            WriteBlock(FlacBlock.Picture, FlacMetadata.WritePicture(picture), ++written == count);
        }

        _paddingAt = _out.Position;
        if (padding)
        {
            WriteBlock(FlacBlock.Padding, new byte[PaddingBytes], true);
        }

        _firstFrame = _out.Position;
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public void UpdateTrack(TrackInfo track)
    {
        if (FlacMetadata.ReadBlocks(track.CodecPrivate.Span).FirstOrDefault(b => b.Type == FlacBlock.StreamInfo).Body is { } body
            && FlacStreamInfo.Read(body) is { } info)
        {
            _info = _info with { Md5 = info.Md5, TotalSamples = info.TotalSamples };
        }
    }

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
        FlacFrameHeader? header = FlacFrameHeader.Parse(data, _info);
        int blockSize = header?.BlockSize ?? (int)(packet.Duration?.Rescale(new Rational(1, _info.SampleRate)).Ticks ?? 0);
        long sample = _samples;
        if (_points.Count == 0 || sample - _points[^1].Sample >= (long)SeekPointSeconds * _info.SampleRate)
        {
            _points.Add((sample, _out.Position - _firstFrame, blockSize));
        }

        _out.Write(data);
        _minFrame = Math.Min(_minFrame, data.Length);
        _maxFrame = Math.Max(_maxFrame, data.Length);
        _samples += blockSize;
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (!_out.CanSeek)
        {
            _out.Flush();
            return;
        }

        long end = _out.Position;
        // The packets decide the length; an MD5 made for another length would not match, so it becomes "unknown".
        FlacStreamInfo final = _info with
        {
            MinFrameSize = _minFrame == int.MaxValue ? 0 : _minFrame,
            MaxFrameSize = _maxFrame,
            TotalSamples = _samples,
            Md5 = _info.TotalSamples == _samples ? _info.Md5 : new byte[16],
        };
        _out.Position = _streamInfoAt + 4;
        _out.Write(final.Write());

        // The padding becomes a seek table, spread out when there are more points than fit, and the padding left over.
        int capacity = (PaddingBytes - 4) / 18;
        List<(long, long, int)> points = _points.Count <= capacity
            ? _points
            : [.. Enumerable.Range(0, capacity).Select(i => _points[(int)((long)i * _points.Count / capacity)])];
        byte[] table = FlacMetadata.WriteSeekTable(points);
        _out.Position = _paddingAt;
        WriteBlock(FlacBlock.SeekTable, table, false);
        WriteBlock(FlacBlock.Padding, new byte[PaddingBytes - 4 - table.Length], true);
        _out.Position = end;
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

    private void WriteBlock(int type, ReadOnlySpan<byte> body, bool last)
    {
        Span<byte> header = stackalloc byte[4];
        header[0] = (byte)(type | (last ? 0x80 : 0));
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(header[0] << 24) | (uint)body.Length);
        _out.Write(header);
        _out.Write(body);
    }
}
