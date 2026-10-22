using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>
/// The FLAC codec adapter: decodes each packet's frames into planar integer frames (<see cref="SampleFormat.S16Planar"/> up to
/// 16 bits, else <see cref="SampleFormat.S32Planar"/>, with <see cref="AudioFrame.ValidBits"/> the stream's depth), checks
/// the CRCs and, when a whole stream was decoded from its start, STREAMINFO's MD5.
/// </summary>
internal sealed class FlacDecoder : IAudioDecoder
{
    private readonly AudioTrackInfo _track;
    private readonly MediaContext _context;
    private readonly AudioDecoderOptions _options;
    private readonly FlacStreamInfo? _info;
    private readonly FlacFrameDecoder _frames = new();
    private readonly Queue<AudioFrame> _ready = new();
    private readonly ChannelLayout _layout;
    private readonly IncrementalHash? _md5;
    private byte[] _md5Buffer = [];
    private long _decodedSamples;
    private bool _md5Broken;
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _next;
    private int _packet = -1;

    public FlacDecoder(AudioTrackInfo track, AudioDecoderOptions options, MediaContext context)
    {
        _track = track;
        _context = context;
        _options = options;
        Dictionary<string, string> tags = [];
        foreach (FlacBlock block in FlacMetadata.ReadBlocks(track.CodecPrivate.Span))
        {
            if (block.Type == FlacBlock.StreamInfo)
            {
                _info ??= FlacStreamInfo.Read(block.Body);
            }
            else if (block.Type == FlacBlock.VorbisComment)
            {
                FlacMetadata.ReadComments(block.Body, tags);
            }
        }

        int channels = _info?.Channels ?? track.Channels;
        if (channels is < 1 or > 8)
        {
            throw new UnsupportedFormatException($"FLAC track {track.Number} has {channels} channels.");
        }

        _layout = track.Layout is { } layout && layout.ChannelCount == channels
            ? layout
            : FlacMetadata.LayoutFromTags(tags, channels) ?? ChannelLayout.ForChannelCount(channels);
        if (options.VerifyChecksums && _info is { TotalSamples: > 0 } && _info.Md5.AsSpan().ContainsAnyExcept((byte)0))
        {
            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        MediaTime time = packet.Timestamp;
        int offset = 0;
        while (data.Length - offset >= 6)
        {
            FlacFrameHeader header;
            bool crcOk;
            int length;
            try
            {
                header = _frames.Decode(data[offset..], _info, _options.VerifyChecksums, out crcOk, out length);
            }
            catch (CorruptDataException e)
            {
                _md5Broken = true;
                FlacFrameHeader? parsed = FlacFrameHeader.Parse(data[offset..], _info);
                Fail("Flac.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}).", e, parsed?.BlockSize ?? 0, time);
                return;
            }

            if (!crcOk)
            {
                _md5Broken = true;
                if (!Fail("Flac.BadCrc", $"Packet {_packet}'s frame CRC-16 does not match.", null, header.BlockSize, time, keep: true))
                {
                    return;
                }
            }

            if (header.SampleRate != _track.SampleRate && _track.SampleRate > 0)
            {
                Report(DiagnosticLevel.Warning, "Flac.RateChange", $"Packet {_packet} is coded at {header.SampleRate} Hz; the track says {_track.SampleRate} Hz.");
            }

            Emit(header, ExactTime(header, time));
            time += new MediaTime(header.BlockSize, new Rational(1, Math.Max(header.SampleRate, 1)));
            offset += length;
        }
    }

    public void SendLoss(MediaTime duration)
    {
        _md5Broken = true;
        Silence(duration.Rescale(new Rational(1, _track.SampleRate)).Ticks, _next ?? MediaTime.Zero);
    }

    public void Flush()
    {
        if (_md5 is null || _md5Broken || _info is null || _decodedSamples != _info.TotalSamples)
        {
            return;
        }

        byte[] hash = _md5.GetHashAndReset();
        _md5Broken = true;
        if (!hash.AsSpan().SequenceEqual(_info.Md5))
        {
            Report(DiagnosticLevel.Warning, "Flac.Md5Mismatch", "The decoded audio's MD5 does not match STREAMINFO's.");
            if (_options.Errors == DecodeErrorMode.Throw)
            {
                throw new CorruptDataException("FLAC", -1, "The decoded audio's MD5 does not match STREAMINFO's.");
            }
        }
    }

    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        // After a seek the MD5 can no longer cover the whole stream.
        _md5Broken = true;
        _next = null;
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    public void Dispose()
    {
        Reset();
        _md5?.Dispose();
        if (_md5Buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_md5Buffer);
            _md5Buffer = [];
        }

        _diagnostics?.Dispose();
    }

    /// <summary>
    /// The frame's time from its own sample number when the container's timestamp is a rounded version of it (Matroska's
    /// milliseconds), so trimming and seeking are sample exact; otherwise the container's.
    /// </summary>
    private MediaTime ExactTime(FlacFrameHeader header, MediaTime time)
    {
        Rational samples = new(1, Math.Max(header.SampleRate, 1));
        if (time.TimeBase == samples)
        {
            return time;
        }

        MediaTime own = new(header.FirstSample(_info is { } i && i.MinBlockSize == i.MaxBlockSize ? i.MaxBlockSize : 0), samples);
        MediaTime tick = new(1, time.TimeBase);
        return own >= time - tick && own <= time + tick ? own : time;
    }

    private void Emit(FlacFrameHeader header, MediaTime time)
    {
        int n = header.BlockSize;
        int bits = header.BitsPerSample;
        SampleFormat format = bits <= 16 ? SampleFormat.S16Planar : SampleFormat.S32Planar;
        AudioFrame frame = new(format, _layout, header.SampleRate, n, time, _context) { ValidBits = bits };
        int[][] channels = _frames.Channels;
        for (int c = 0; c < header.Channels; c++)
        {
            ReadOnlySpan<int> source = channels[c].AsSpan(0, n);
            if (format == SampleFormat.S16Planar)
            {
                Span<short> d = frame.GetSpan<short>(c);
                for (int i = 0; i < n; i++)
                {
                    d[i] = (short)source[i];
                }
            }
            else
            {
                source.CopyTo(frame.GetSpan<int>(c));
            }
        }

        if (_md5 is not null && !_md5Broken)
        {
            HashFrame(header, channels, n);
        }

        _decodedSamples += n;
        _next = time + frame.Duration;
        _ready.Enqueue(frame);
    }

    /// <summary>Adds a frame to the MD5: interleaved little-endian samples of the stream's byte width (RFC 9639 section 8.2).</summary>
    private void HashFrame(FlacFrameHeader header, int[][] channels, int n)
    {
        int width = (header.BitsPerSample + 7) / 8;
        int length = n * header.Channels * width;
        if (_md5Buffer.Length < length)
        {
            if (_md5Buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_md5Buffer);
            }

            _md5Buffer = ArrayPool<byte>.Shared.Rent(length);
        }

        Span<byte> b = _md5Buffer.AsSpan(0, length);
        int p = 0;
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < header.Channels; c++)
            {
                int v = channels[c][i];
                for (int k = 0; k < width; k++)
                {
                    b[p++] = (byte)(v >> (8 * k));
                }
            }
        }

        _md5!.AppendData(b);
    }

    /// <summary>Reports a broken frame and handles it as the options say. Returns whether to keep the decoded frame.</summary>
    private bool Fail(string id, string message, CorruptDataException? inner, int blockSize, MediaTime time, bool keep = false)
    {
        Report(DiagnosticLevel.Warning, id, message);
        switch (_options.Errors)
        {
            case DecodeErrorMode.Throw:
                throw inner ?? new CorruptDataException("FLAC", -1, message);
            case DecodeErrorMode.Conceal:
                if (keep)
                {
                    return true;
                }

                Silence(blockSize, time);
                return false;
            default:
                return false;
        }
    }

    private void Silence(long count, MediaTime time)
    {
        if (count <= 0)
        {
            return;
        }

        int bits = _info?.BitsPerSample ?? (_track.BitsPerSample > 0 ? _track.BitsPerSample : 16);
        AudioFrame frame = new(bits <= 16 ? SampleFormat.S16Planar : SampleFormat.S32Planar, _layout, Math.Max(_track.SampleRate, 1), (int)Math.Min(count, 1 << 20), time, _context) { ValidBits = bits };
        _next = time + frame.Duration;
        _ready.Enqueue(frame);
    }

    private void Report(DiagnosticLevel level, string id, string message)
    {
        if (_context.Egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _context.Egami.StartDiagnostics();
        _diagnostics.Report(level, id, message, frame: _packet);
    }
}
