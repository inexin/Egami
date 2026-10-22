using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Wav;

/// <summary>
/// Reads RIFF WAVE, RF64, BW64 and Wave64. The chunks before "data" are read on open; on a seekable stream the chunks after it
/// (tags and cue points written at the end) are read too. Packets are runs of whole samples from the data chunk.
/// </summary>
internal sealed class WavDemuxer : IDemuxer
{
    /// <summary>Samples per packet for PCM: about 0.1 s at 44.1 kHz.</summary>
    private const int PacketSamples = 4096;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly int _blockAlign;
    private readonly long _dataStart;
    private readonly long _dataEnd;
    private readonly int _bytesPerSecond;
    private readonly bool _pcm;

    public WavDemuxer(Stream stream, MediaContext context, bool leaveOpen, WavFormat format)
    {
        _input = new MediaInput(stream, leaveOpen, format.Name);
        _context = context;
        try
        {
            bool wave64 = format.IsWave64;
            Span<byte> header = stackalloc byte[wave64 ? 40 : 12];
            if (!_input.TryReadExactly(header) || !format.IsMatch(header))
            {
                throw _input.Corrupt(0, $"The file is not {format.Name}.");
            }

            bool rf64 = !wave64 && !header.StartsWith("RIFF"u8);
            long riffEnd = wave64
                ? Math.Min(long.MaxValue, (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(header[16..]), long.MaxValue))
                : 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long ds64Data = -1;
            long ds64Samples = -1;
            WaveFormat? fmt = null;
            Dictionary<string, string> tags = [];
            List<(uint Id, long Sample)> cues = [];
            Dictionary<uint, string> labels = [];
            long factSamples = -1;
            long dataStart = -1, dataEnd = -1;
            while (true)
            {
                long chunkStart = _input.Position;
                (string? id, long size, bool streamed) = ReadChunkHeader(wave64);
                if (id is null)
                {
                    break;
                }

                if (id == "data")
                {
                    if (rf64 && ds64Data >= 0 && streamed)
                    {
                        size = ds64Data;
                        streamed = false;
                    }

                    dataStart = _input.Position;
                    dataEnd = streamed || size > _input.Remaining ? (_input.CanSeek ? _input.Length : long.MaxValue) : dataStart + size;
                    if (!_input.CanSeek || streamed || dataEnd >= _input.Length)
                    {
                        break;
                    }

                    // Tags and cue points may follow the data: read them, then come back.
                    _input.Skip(dataEnd - dataStart + (wave64 ? Pad8(dataEnd - chunkStart) : (dataEnd - dataStart) & 1));
                    continue;
                }

                if (size > _context.Limits.MaxHeaderBytes || size > _input.Remaining)
                {
                    if (!_input.Skip(size))
                    {
                        break;
                    }

                    continue;
                }

                byte[] body = new byte[size];
                if (!_input.TryReadExactly(body))
                {
                    break;
                }

                switch (id)
                {
                    case "ds64" when rf64 && body.Length >= 24:
                        riffEnd = 8 + (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(body), long.MaxValue - 8);
                        ds64Data = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(8)), long.MaxValue);
                        ds64Samples = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(16)), long.MaxValue);
                        break;
                    case "fmt ":
                        fmt ??= WavChunks.ReadFormat(body);
                        break;
                    case "fact" when body.Length >= 4:
                        factSamples = BinaryPrimitives.ReadUInt32LittleEndian(body);
                        break;
                    case "LIST" when body.Length >= 4:
                        string type = Encoding.ASCII.GetString(body, 0, 4);
                        if (type == "INFO")
                        {
                            WavChunks.ReadInfo(body.AsSpan(4), tags);
                        }
                        else if (type == "adtl")
                        {
                            WavChunks.ReadLabels(body.AsSpan(4), labels);
                        }

                        break;
                    case "cue ":
                        cues = WavChunks.ReadCue(body);
                        break;
                    case "id3 " or "ID3 ":
                        WavChunks.ReadId3(body, tags);
                        break;
                }

                if (!_input.Skip(wave64 ? Pad8(_input.Position - chunkStart) : size & 1))
                {
                    break;
                }

                if (!wave64 && riffEnd > 12 && _input.Position >= riffEnd && dataStart >= 0)
                {
                    break;
                }
            }

            if (fmt is null || dataStart < 0)
            {
                throw _input.Corrupt(fmt is null ? "The file has no fmt chunk before its data." : "The file has no data chunk.");
            }

            if (fmt.Channels < 1 || fmt.SampleRate < 1 || fmt.BlockAlign < 1)
            {
                throw _input.Corrupt($"The format says {fmt.Channels} channels at {fmt.SampleRate} Hz with {fmt.BlockAlign}-byte blocks.");
            }

            _blockAlign = fmt.BlockAlign;
            _bytesPerSecond = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(fmt.Raw.AsSpan(8)));
            _dataStart = dataStart;
            _dataEnd = dataEnd;
            AudioTrackInfo track = WavChunks.Track(fmt, null);
            _pcm = track.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmFloat;
            long samples = ds64Samples > 0 && !_pcm ? ds64Samples : factSamples > 0 && !_pcm ? factSamples : -1;
            MediaTime? duration = dataEnd == long.MaxValue
                ? null
                : _pcm
                    ? new MediaTime((dataEnd - dataStart) / _blockAlign, track.TimeBase)
                    : samples > 0
                        ? new MediaTime(samples, track.TimeBase)
                        : _bytesPerSecond > 0 ? new MediaTime(dataEnd - dataStart, new Rational(1, _bytesPerSecond)) : null;
            _track = track with { Duration = duration };
            Tracks = [_track];
            List<ChapterInfo> chapters = [];
            cues.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            for (int i = 0; i < cues.Count; i++)
            {
                MediaTime start = new(cues[i].Sample, _track.TimeBase);
                MediaTime? end = i + 1 < cues.Count ? new MediaTime(cues[i + 1].Sample, _track.TimeBase) : duration;
                chapters.Add(new ChapterInfo(start, end, labels.GetValueOrDefault(cues[i].Id)));
            }

            Info = new MediaInfo { FormatName = format.Name, Duration = duration, Tracks = Tracks, Tags = tags, Chapters = chapters };
            if (_input.Position != dataStart)
            {
                _input.Seek(dataStart);
            }
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
        long left = _dataEnd - position;
        if (left < _blockAlign)
        {
            return null;
        }

        int blocks = _pcm ? PacketSamples : Math.Max(1, 4096 / _blockAlign);
        int length = (int)Math.Min((long)blocks * _blockAlign, left / _blockAlign * _blockAlign);
        if (length > _context.Limits.MaxPacketBytes)
        {
            length = Math.Max(_blockAlign, _context.Limits.MaxPacketBytes / _blockAlign * _blockAlign);
        }

        MediaTime timestamp = Time(position);
        EncodedPacket packet = EncodedPacket.Rent(_track.Number, length, timestamp);
        int read = _input.Read(packet.GetWritableData().Span);
        if (read < length)
        {
            // A cut-off file: keep the whole samples that are there.
            int whole = read / _blockAlign * _blockAlign;
            EncodedPacket full = packet;
            if (whole == 0)
            {
                full.Dispose();
                return null;
            }

            packet = EncodedPacket.Rent(_track.Number, whole, timestamp);
            full.Data.Span[..whole].CopyTo(packet.GetWritableData().Span);
            full.Dispose();
            length = whole;
        }

        packet.IsKeyframe = true;
        packet.Duration = Time(position + length) - timestamp;
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

        long block = _pcm
            ? Math.Max(0, time.Rescale(_track.TimeBase).Ticks)
            : _bytesPerSecond > 0 ? (long)Math.Max(0, time.Seconds * _bytesPerSecond / _blockAlign) : 0;
        long position = Math.Min(_dataStart + (block * _blockAlign), _dataStart + ((_dataEnd - _dataStart) / _blockAlign * _blockAlign));
        _input.Seek(Math.Min(position, _input.Length));
    }

    public void Dispose() => _input.Dispose();

    private static long Pad8(long length) => (8 - (length & 7)) & 7;

    private MediaTime Time(long position)
    {
        long blocks = (position - _dataStart) / _blockAlign;
        return _pcm || _bytesPerSecond <= 0
            ? new MediaTime(blocks, _track.TimeBase)
            : new MediaTime(position - _dataStart, new Rational(1, _bytesPerSecond));
    }

    /// <summary>Reads a chunk header: its id, its body size, and whether the size is a streaming placeholder.</summary>
    private (string? Id, long Size, bool Streamed) ReadChunkHeader(bool wave64)
    {
        if (wave64)
        {
            Span<byte> h = stackalloc byte[24];
            if (!_input.TryReadExactly(h))
            {
                return (null, 0, false);
            }

            ulong total = BinaryPrimitives.ReadUInt64LittleEndian(h[16..]);
            string id = WavChunks.Wave64Name(h[..16]) ?? "????";
            return total < 24 ? (id, 0, true) : (id, (long)Math.Min(total - 24, long.MaxValue), false);
        }

        Span<byte> b = stackalloc byte[8];
        if (!_input.TryReadExactly(b))
        {
            return (null, 0, false);
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        string name = Encoding.ASCII.GetString(b[..4]);
        bool streamed = name == "data" && size is 0 or uint.MaxValue;
        return (name, size, streamed);
    }
}
