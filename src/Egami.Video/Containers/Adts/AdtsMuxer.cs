using Egami.Video.Audio.Codecs.Aac;

namespace Egami.Video.Containers.Adts;

/// <summary>
/// Writes raw AAC in ADTS frames: each packet (one raw data block) gets a 7-byte MPEG-4 header without a CRC, built from the
/// track's AudioSpecificConfig. HE-AAC with explicit SBR signalling is written as its AAC LC core at the core rate, which
/// decoders detect SBR in on their own, as ADTS has no field for it.
/// </summary>
internal sealed class AdtsMuxer : IMuxer
{
    private readonly Stream _stream;
    private readonly OutputStream _out;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private readonly int _profile;
    private readonly int _rateIndex;
    private readonly int _channelConfiguration;
    private readonly byte[] _header = new byte[AdtsHeader.Size];
    private bool _finished;

    public AdtsMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks is not [AudioTrackInfo track] || track.CodecId != CodecIds.Aac)
        {
            throw new NotSupportedException("An ADTS file holds one AAC track.");
        }

        if (!AacConfig.TryRead(track.CodecPrivate.Span, out AacConfig config))
        {
            throw new NotSupportedException("The AAC track has no valid AudioSpecificConfig to build ADTS headers from.");
        }

        _rateIndex = AacConfig.RateIndex(config.CoreSampleRate);
        if (config.CoreObjectType is < 1 or > 4 || _rateIndex < 0 || config.ChannelConfiguration is < 1 or > 7)
        {
            throw new NotSupportedException(
                $"ADTS holds AAC object types 1 to 4 at the standard rates with channel configurations 1 to 7; this track has object type {config.CoreObjectType}, {config.CoreSampleRate} Hz and channel configuration {config.ChannelConfiguration}.");
        }

        if (metadata.Tags.Count > 0 || metadata.Chapters.Count > 0 || metadata.Attachments.Count > 0)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "An ADTS file holds no tags, chapters or attachments; they are left out."));
        }

        _profile = config.CoreObjectType - 1;
        _channelConfiguration = config.ChannelConfiguration;
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

        int frameBytes = AdtsHeader.Size + packet.Data.Length;
        if (frameBytes > AdtsHeader.MaxFrameBytes)
        {
            throw new ArgumentException($"An AAC packet of {packet.Data.Length} bytes is too long for an ADTS frame (at most {AdtsHeader.MaxFrameBytes - AdtsHeader.Size}).", nameof(packet));
        }

        AdtsHeader.Write(_header, _profile, _rateIndex, _channelConfiguration, frameBytes);
        _out.Write(_header);
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
