using System.Buffers.Binary;
using System.Text;

using Egami.Video.Containers.Mp3;

namespace Egami.Video.Containers.Aiff;

/// <summary>
/// Reads AIFF and AIFF-C. Chunks may come in any order: on a seekable stream every chunk is read on open, skipping over the
/// samples; on a non-seekable one reading stops at SSND, so COMM must come before it. Packets are runs of whole sample frames
/// from the SSND chunk; big-endian float is turned to little-endian on the way out, so the track is plain float PCM.
/// </summary>
internal sealed class AiffDemuxer : IDemuxer
{
    /// <summary>Samples per packet: about 0.1 s at 44.1 kHz.</summary>
    private const int PacketSamples = 4096;

    /// <summary>Bytes per packet for compressions passed through undecoded.</summary>
    private const int OpaquePacketBytes = 4096;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly AudioTrackInfo _track;
    private readonly int _blockAlign;
    private readonly int _swapBytes;
    private readonly long _dataStart;
    private readonly long _dataEnd;
    private readonly long _frames;
    private readonly bool _opaque;

    public AiffDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, AiffFormat.Instance.Name);
        _context = context;
        try
        {
            Span<byte> header = stackalloc byte[12];
            if (!_input.TryReadExactly(header) || !AiffFormat.Instance.IsMatch(header))
            {
                throw _input.Corrupt(0, "The file is not AIFF.");
            }

            bool aifc = header[8..12].SequenceEqual("AIFC"u8);
            long formEnd = 8 + (long)BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            AiffCommon? common = null;
            Dictionary<string, string> tags = [];
            List<ChapterInfo> id3Chapters = [];
            List<AttachmentInfo> attachments = [];
            List<(int Id, long Frame, string Name)> markers = [];
            byte[]? channelLayout = null;
            long dataStart = -1, dataEnd = -1;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> ssnd = stackalloc byte[8];
            while (_input.TryReadExactly(chunkHeader))
            {
                string id = Encoding.ASCII.GetString(chunkHeader[..4]);
                long size = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[4..]);
                if (id == "SSND")
                {
                    if (!_input.TryReadExactly(ssnd))
                    {
                        break;
                    }

                    long offset = BinaryPrimitives.ReadUInt32BigEndian(ssnd);
                    long bodyEnd = _input.Position + Math.Max(0, size - 8);
                    if (!_input.Skip(offset))
                    {
                        break;
                    }

                    dataStart = _input.Position;
                    bool cut = size < 8 || bodyEnd > (_input.CanSeek ? _input.Length : long.MaxValue);
                    dataEnd = cut ? (_input.CanSeek ? _input.Length : long.MaxValue) : Math.Max(dataStart, bodyEnd);
                    if (!_input.CanSeek || dataEnd >= _input.Length)
                    {
                        break;
                    }

                    // Markers and tags may follow the samples: read them, then come back.
                    _input.Skip(dataEnd - dataStart + ((dataEnd - dataStart + offset + 8) & 1));
                    continue;
                }

                if (size > _context.Limits.MaxHeaderBytes || size > _input.Remaining)
                {
                    if (!_input.Skip(size + (size & 1)))
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
                    case "COMM":
                        common ??= AiffChunks.ReadCommon(body, aifc);
                        break;
                    case "CHAN":
                        channelLayout = body;
                        break;
                    case "MARK":
                        markers = AiffChunks.ReadMarkers(body);
                        break;
                    case "NAME" or "AUTH" or "(c) " or "ANNO":
                        string text = AiffChunks.Text(body);
                        string tag = Array.Find(AiffChunks.TextChunks, t => t.Chunk == id).Tag;
                        if (text.Length > 0)
                        {
                            // Several ANNO chunks are one comment, a line each.
                            tags[tag] = tags.TryGetValue(tag, out string? before) && id == "ANNO" ? before + "\n" + text : text;
                        }

                        break;
                    case "ID3 " or "id3 ":
                        Dictionary<string, string> id3 = [];
                        Id3Tag.Read(body, id3, id3Chapters, attachments);
                        foreach ((string name, string value) in id3)
                        {
                            tags.TryAdd(name, value);
                        }

                        break;
                }

                if (!_input.Skip(size & 1) || (formEnd > 12 && _input.Position >= formEnd && dataStart >= 0))
                {
                    break;
                }
            }

            if (common is null || dataStart < 0)
            {
                throw _input.Corrupt(common is null ? "The file has no COMM chunk before its samples." : "The file has no SSND chunk.");
            }

            int rate = (int)Math.Round(Math.Clamp(common.SampleRate, 0, int.MaxValue));
            if (common.Channels < 1 || rate < 1 || common.Bits is < 1 or > 64)
            {
                throw _input.Corrupt($"COMM says {common.Channels} channels of {common.Bits} bits at {common.SampleRate} Hz.");
            }

            (string codecId, int bits, _swapBytes, _opaque) = Codec(common);
            _blockAlign = _opaque ? 1 : (bits + 7) / 8 * common.Channels;
            _dataStart = dataStart;
            _dataEnd = dataEnd;
            long stored = dataEnd == long.MaxValue ? -1 : (dataEnd - dataStart) / _blockAlign;
            // Apple's IMA4 counts 64-frame packets in COMM rather than sample frames.
            long commonFrames = common.Compression is "ima4" ? common.Frames * 64 : common.Frames;
            _frames = _opaque || stored < 0 ? commonFrames : Math.Min(commonFrames, stored);
            if (!_opaque && stored >= 0 && common.Frames < stored)
            {
                // Trailing bytes past the frames COMM counts (some writers round SSND up) are not samples.
                _dataEnd = dataStart + (common.Frames * _blockAlign);
            }

            Rational timeBase = new(1, rate);
            MediaTime? duration = _frames > 0 || stored == 0 ? new MediaTime(_frames, timeBase) : null;
            _track = new AudioTrackInfo
            {
                Number = 1,
                Kind = TrackKind.Audio,
                CodecId = codecId,
                TimeBase = timeBase,
                Duration = duration,
                SampleRate = rate,
                Channels = common.Channels,
                BitsPerSample = bits,
                Layout = channelLayout is null ? null : CoreAudioLayout.Read(channelLayout, common.Channels),
            };
            Tracks = [_track];
            List<ChapterInfo> chapters = id3Chapters;
            if (markers.Count > 0)
            {
                markers.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                chapters = [];
                for (int i = 0; i < markers.Count; i++)
                {
                    MediaTime start = new(markers[i].Frame, timeBase);
                    MediaTime? end = i + 1 < markers.Count ? new MediaTime(markers[i + 1].Frame, timeBase) : duration;
                    chapters.Add(new ChapterInfo(start, end, markers[i].Name.Length > 0 ? markers[i].Name : null));
                }
            }

            Info = new MediaInfo { FormatName = AiffFormat.Instance.Name, Duration = duration, Tracks = Tracks, Tags = tags, Chapters = chapters, Attachments = attachments };
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
        if (left < _blockAlign || left <= 0)
        {
            return null;
        }

        int length = (int)Math.Min(_opaque ? OpaquePacketBytes : (long)PacketSamples * _blockAlign, left / _blockAlign * _blockAlign);
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

        if (_swapBytes > 1)
        {
            Span<byte> data = packet.GetWritableData().Span[..length];
            for (int i = 0; i < data.Length; i += _swapBytes)
            {
                data.Slice(i, _swapBytes).Reverse();
            }
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

        long frame = Math.Max(0, time.Rescale(_track.TimeBase).Ticks);
        long bytes = _opaque
            ? _frames > 0 ? (long)((double)frame / _frames * (_dataEnd - _dataStart)) / OpaquePacketBytes * OpaquePacketBytes : 0
            : frame * _blockAlign;
        long position = Math.Min(_dataStart + bytes, _dataStart + ((_dataEnd - _dataStart) / _blockAlign * _blockAlign));
        _input.Seek(Math.Min(position, _input.Length));
    }

    public void Dispose() => _input.Dispose();

    /// <summary>Maps COMM's compression type to a codec id, stored bits, the byte run to reverse (big-endian float), and whether it is opaque.</summary>
    private static (string CodecId, int Bits, int Swap, bool Opaque) Codec(AiffCommon common)
    {
        int bits = (common.Bits + 7) / 8 * 8;
        return common.Compression switch
        {
            // AIFF's 8-bit samples are signed, as big-endian PCM's are.
            "NONE" or "twos" => (CodecIds.PcmBigEndian, bits, 0, false),
            "sowt" => (bits > 8 ? CodecIds.PcmLittleEndian : CodecIds.PcmBigEndian, bits, 0, false),
            "in24" => (CodecIds.PcmBigEndian, 24, 0, false),
            "in32" => (CodecIds.PcmBigEndian, 32, 0, false),
            "raw " => (CodecIds.PcmLittleEndian, 8, 0, false),
            "fl32" or "FL32" => (CodecIds.PcmFloat, 32, 4, false),
            "fl64" or "FL64" => (CodecIds.PcmFloat, 64, 8, false),
            "ulaw" or "ULAW" => (CodecIds.G711MuLaw, 8, 0, false),
            "alaw" or "ALAW" => (CodecIds.G711ALaw, 8, 0, false),
            _ => ("X_AIFC/" + common.Compression.TrimEnd(), 0, 0, true),
        };
    }

    private MediaTime Time(long position)
    {
        if (!_opaque)
        {
            return new MediaTime((position - _dataStart) / _blockAlign, _track.TimeBase);
        }

        long bytes = _dataEnd - _dataStart;
        return new MediaTime(bytes > 0 && _frames > 0 ? (long)((double)(position - _dataStart) / bytes * _frames) : 0, _track.TimeBase);
    }
}
