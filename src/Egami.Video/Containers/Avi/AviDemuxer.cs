using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Avi;

/// <summary>
/// Reads AVI files (Microsoft AVI RIFF reference) including the OpenDML extensions for files over 1 GB: the hdrl header list,
/// packets from the movi list and any AVIX lists, and the idx1 or indx/ix## indexes for keyframe flags and seeking.
/// </summary>
/// <remarks>
/// Packets are read by walking chunks in file order without tracking list nesting: movi, rec and AVIX lists are entered,
/// other lists and index chunks are skipped. That lets the reader resume anywhere a chunk header starts, which is what
/// seeking needs, and keeps reading through files whose list sizes are wrong.
/// </remarks>
internal sealed class AviDemuxer : IDemuxer
{
    private const int AviifList = 0x01;
    private const int AviifKeyframe = 0x10;
    private const int ResyncLimit = 1 << 20;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly List<AviStream> _streams = [];
    private readonly Dictionary<string, string> _tags = [];
    private long _moviListStart = -1;
    private long _moviDataStart;
    private long _moviEnd;
    private long _idx1Offset = -1;
    private int _idx1Size;
    private long _totalFramesOpenDml;
    private bool _indexLoaded;
    private bool _hasIndex;

    public AviDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, AviFormat.Instance.Name);
        _context = context;
        try
        {
            ReadHeaders();
            Tracks = [.. _streams.Select(s => s.Track)];
            Info = new MediaInfo
            {
                FormatName = AviFormat.Instance.Name,
                Tracks = Tracks,
                Duration = Tracks.Where(t => t.Duration is not null).Select(t => t.Duration!.Value).DefaultIfEmpty().Max() is { Ticks: > 0 } d ? d : null,
                Tags = _tags,
            };
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
        Span<byte> header = stackalloc byte[8];
        while (true)
        {
            long chunkStart = _input.Position;
            if (_input.Remaining < 8 || !_input.TryReadExactly(header))
            {
                return null;
            }

            uint id = FourCC.Read(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (id is FourCC.Riff or FourCC.List)
            {
                if (_input.Remaining < 4)
                {
                    return null;
                }

                uint type = _input.ReadUInt32LittleEndian();
                if (type is FourCC.Avix or FourCC.Movi or FourCC.Rec)
                {
                    continue;
                }

                if (!_input.Skip(Padded(size) - 4))
                {
                    return null;
                }

                continue;
            }

            int number = FourCC.StreamNumber(id);
            if (number >= 0 && number < _streams.Count)
            {
                AviStream stream = _streams[number];
                if (size > _context.Limits.MaxPacketBytes)
                {
                    throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, size);
                }

                // A truncated last chunk gives what is there.
                int length = (int)Math.Min(size, _input.Remaining);
                int chunkNumber = stream.ChunkCount;
                long ticks = stream.Ticks;
                stream.Advance(length);
                if (length == 0)
                {
                    continue;
                }

                EncodedPacket packet = EncodedPacket.Rent(number, length, new MediaTime(ticks, stream.TimeBase));
                try
                {
                    int read = _input.Read(packet.GetWritableData().Span);
                    if (read < length)
                    {
                        throw _input.Corrupt("The data ends inside a chunk.");
                    }

                    _input.Skip(Padded(size) - size);
                }
                catch
                {
                    packet.Dispose();
                    throw;
                }

                packet.Duration = new MediaTime(stream.TicksFor(length), stream.TimeBase);
                packet.IsKeyframe = stream.Index is { } index && chunkNumber < index.Count && index.Offset(chunkNumber) == chunkStart
                    ? index.IsKey(chunkNumber)
                    : !_hasIndex || stream.Track.Kind != TrackKind.Video;
                return packet;
            }

            if (FourCC.IsPrintable(id))
            {
                // idx1, ix##, JUNK, another stream's chunk past the declared count, or anything else: skip it.
                if (!_input.Skip(Padded(size)))
                {
                    return null;
                }

                continue;
            }

            if (!Resync(chunkStart))
            {
                return null;
            }
        }
    }

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        EnsureIndex();
        AviStream target = trackNumber is { } n
            ? _streams.FirstOrDefault(s => s.Number == n) ?? throw new ArgumentException($"There is no track {n}.", nameof(trackNumber))
            : _streams.FirstOrDefault(s => s.Track.Kind == TrackKind.Video) ?? _streams.FirstOrDefault()
                ?? throw new InvalidOperationException("The file has no tracks.");
        AviIndex index = target.Index!;

        // The tick at or before the target time, rounding down so a frame that starts after it is never chosen.
        MediaTime inBase = time.Rescale(target.TimeBase);
        long ticks = new MediaTime(inBase.Ticks, target.TimeBase) > time ? inBase.Ticks - 1 : inBase.Ticks;
        int entry = index.KeyframeAtOrBefore(ticks);
        long position = entry < 0 ? _moviDataStart : index.Offset(entry);
        foreach (AviStream stream in _streams)
        {
            int k = stream.Index!.FirstAtOrAfter(position);
            stream.Reposition(k);
        }

        _input.Seek(position);
    }

    public void Dispose() => _input.Dispose();

    private static long Padded(long size) => size + (size & 1);

    private void ReadHeaders()
    {
        Span<byte> header = stackalloc byte[12];
        _input.ReadExactly(header);
        if (FourCC.Read(header) != FourCC.Riff || FourCC.Read(header[8..]) != FourCC.Avi)
        {
            throw _input.Corrupt(0, "The file does not start with RIFF AVI.");
        }

        long riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        long riffEnd = riffSize < 4 || 8 + riffSize > _input.Length ? _input.Length : 8 + riffSize;
        bool sawHeaders = false;
        while (_input.Position + 8 <= riffEnd)
        {
            long chunkStart = _input.Position;
            if (!_input.TryReadExactly(header[..8]))
            {
                break;
            }

            uint id = FourCC.Read(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (id == FourCC.List)
            {
                uint type = _input.ReadUInt32LittleEndian();
                if (type == FourCC.Movi)
                {
                    _moviListStart = chunkStart + 8;
                    _moviDataStart = chunkStart + 12;
                    _moviEnd = size < 4 || _moviListStart + size > _input.Length ? _input.Length : _moviListStart + size;
                    break;
                }

                if (type == FourCC.Hdrl)
                {
                    ReadHeaderList(ReadBody(size - 4));
                    sawHeaders = true;
                }
                else if (type == FourCC.Info)
                {
                    ReadInfo(ReadBody(size - 4));
                }
                else if (!_input.Skip(Padded(size) - 4))
                {
                    break;
                }
            }
            else if (!_input.Skip(Padded(size)))
            {
                break;
            }
        }

        if (!sawHeaders || _streams.Count == 0)
        {
            throw _input.Corrupt("The file has no hdrl header list or no streams.");
        }

        if (_moviListStart < 0)
        {
            throw _input.Corrupt("The file has no movi list.");
        }

        if (_input.CanSeek)
        {
            FindTrailingChunks(riffEnd);
            LoadIndex();
            _input.Seek(_moviDataStart);
        }

        foreach (AviStream stream in _streams)
        {
            stream.BuildTrack(_totalFramesOpenDml);
        }
    }

    private byte[] ReadBody(long size)
    {
        if (size < 0 || size > _context.Limits.MaxHeaderBytes)
        {
            throw new LimitExceededException("MaxHeaderBytes", _context.Limits.MaxHeaderBytes, size);
        }

        byte[] body = new byte[size];
        _input.ReadExactly(body);
        _input.Skip(Padded(size) - size);
        return body;
    }

    private void ReadHeaderList(ReadOnlySpan<byte> list)
    {
        foreach ((uint id, uint type, ReadOnlyMemory<byte> body) in Chunks(list.ToArray()))
        {
            if (id == FourCC.List && type == FourCC.Strl)
            {
                if (_streams.Count >= Math.Min(100, _context.Limits.MaxTracks))
                {
                    throw new LimitExceededException("MaxTracks", Math.Min(100, _context.Limits.MaxTracks), _streams.Count + 1);
                }

                ReadStreamList(body.Span);
            }
            else if (id == FourCC.List && type == FourCC.Odml)
            {
                foreach ((uint sub, _, ReadOnlyMemory<byte> dmlh) in Chunks(body.ToArray()))
                {
                    if (sub == FourCC.Dmlh && dmlh.Length >= 4)
                    {
                        _totalFramesOpenDml = BinaryPrimitives.ReadUInt32LittleEndian(dmlh.Span);
                    }
                }
            }
        }
    }

    private void ReadStreamList(ReadOnlySpan<byte> list)
    {
        AviStream stream = new(_streams.Count);
        foreach ((uint id, _, ReadOnlyMemory<byte> body) in Chunks(list.ToArray()))
        {
            ReadOnlySpan<byte> b = body.Span;
            if (id == FourCC.Strh && b.Length >= 36)
            {
                stream.Type = FourCC.Read(b);
                stream.Handler = FourCC.Read(b[4..]);
                stream.Scale = BinaryPrimitives.ReadUInt32LittleEndian(b[20..]);
                stream.Rate = BinaryPrimitives.ReadUInt32LittleEndian(b[24..]);
                stream.Start = BinaryPrimitives.ReadUInt32LittleEndian(b[28..]);
                stream.Length = BinaryPrimitives.ReadUInt32LittleEndian(b[32..]);
                stream.SampleSize = b.Length >= 48 ? BinaryPrimitives.ReadUInt32LittleEndian(b[44..]) : 0;
            }
            else if (id == FourCC.Strf)
            {
                stream.Format = b.ToArray();
            }
            else if (id == FourCC.Strn)
            {
                int end = b.IndexOf((byte)0);
                stream.Name = Encoding.Latin1.GetString(end < 0 ? b : b[..end]);
            }
            else if (id == FourCC.Indx)
            {
                stream.SuperIndex = b.ToArray();
            }
        }

        _streams.Add(stream);
    }

    private void ReadInfo(ReadOnlySpan<byte> list)
    {
        foreach ((uint id, _, ReadOnlyMemory<byte> body) in Chunks(list.ToArray()))
        {
            ReadOnlySpan<byte> b = body.Span;
            int end = b.IndexOf((byte)0);
            string value = Encoding.UTF8.GetString(end < 0 ? b : b[..end]);
            if (value.Length > 0)
            {
                string chunk = FourCC.ToString(id);
                _tags[Array.Find(FourCC.InfoTags, t => t.Chunk == chunk).Tag ?? chunk] = value;
            }
        }
    }

    /// <summary>Lists the chunks of a list body: id, list type (for LIST chunks) and body.</summary>
    private static IEnumerable<(uint Id, uint Type, ReadOnlyMemory<byte> Body)> Chunks(byte[] data)
    {
        int position = 0;
        while (position + 8 <= data.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            int bodyStart = position + 8;
            int bodySize = (int)Math.Min(size, data.Length - bodyStart);
            if (id == FourCC.List && bodySize >= 4)
            {
                yield return (id, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(bodyStart)), data.AsMemory(bodyStart + 4, bodySize - 4));
            }
            else
            {
                yield return (id, 0, data.AsMemory(bodyStart, bodySize));
            }

            long next = bodyStart + Padded(size);
            if (next > data.Length)
            {
                yield break;
            }

            position = (int)next;
        }
    }

    private void FindTrailingChunks(long riffEnd)
    {
        // After the first movi list come idx1 and, sometimes, an INFO list.
        long position = _moviEnd + (_moviEnd & 1);
        Span<byte> header = stackalloc byte[12];
        while (position + 8 <= riffEnd)
        {
            _input.Seek(position);
            if (!_input.TryReadExactly(header[..8]))
            {
                break;
            }

            uint id = FourCC.Read(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (id == FourCC.Idx1)
            {
                _idx1Offset = position + 8;
                _idx1Size = (int)Math.Min(size, Math.Min(riffEnd, _input.Length) - _idx1Offset);
            }
            else if (id == FourCC.List && _input.TryReadExactly(header[8..12]) && FourCC.Read(header[8..]) == FourCC.Info && size <= _context.Limits.MaxHeaderBytes)
            {
                ReadInfo(ReadBody(size - 4));
            }
            else if (!FourCC.IsPrintable(id))
            {
                break;
            }

            position += 8 + Padded(size);
        }
    }

    private void LoadIndex()
    {
        if (_streams.All(s => s.SuperIndex is not null))
        {
            try
            {
                foreach (AviStream stream in _streams)
                {
                    stream.Index = ReadOpenDmlIndex(stream);
                }

                _indexLoaded = _hasIndex = true;
                return;
            }
            catch (CorruptDataException)
            {
                // Fall back to idx1 or a scan.
            }
        }

        if (_idx1Offset >= 0 && ReadIdx1())
        {
            _indexLoaded = _hasIndex = true;
            return;
        }

        foreach (AviStream stream in _streams)
        {
            stream.Index = null;
        }
    }

    private void EnsureIndex()
    {
        if (_indexLoaded)
        {
            return;
        }

        // No usable index: walk every chunk header once, skipping the payloads.
        AviIndex[] indexes = [.. _streams.Select(_ => new AviIndex())];
        long[] ticks = [.. _streams.Select(s => (long)s.Start)];
        long resume = _input.Position;
        _input.Seek(_moviDataStart);
        Span<byte> header = stackalloc byte[8];
        long entries = 0;
        while (_input.Remaining >= 8 && _input.TryReadExactly(header))
        {
            long chunkStart = _input.Position - 8;
            uint id = FourCC.Read(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (id is FourCC.Riff or FourCC.List)
            {
                if (_input.Remaining < 4)
                {
                    break;
                }

                uint type = _input.ReadUInt32LittleEndian();
                if (type is not (FourCC.Avix or FourCC.Movi or FourCC.Rec) && !_input.Skip(Padded(size) - 4))
                {
                    break;
                }

                continue;
            }

            int number = FourCC.StreamNumber(id);
            if (number >= 0 && number < _streams.Count)
            {
                if (++entries > _context.Limits.MaxIndexEntries)
                {
                    throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, entries);
                }

                int length = (int)Math.Min(Math.Min(size, int.MaxValue), _input.Remaining);
                indexes[number].Add(chunkStart, length, ticks[number], true);
                ticks[number] += _streams[number].TicksFor(length);
            }
            else if (!FourCC.IsPrintable(id))
            {
                if (!Resync(chunkStart))
                {
                    break;
                }

                continue;
            }

            if (!_input.Skip(Padded(size)))
            {
                break;
            }
        }

        for (int i = 0; i < _streams.Count; i++)
        {
            _streams[i].Index = indexes[i];
        }

        _indexLoaded = true;
        _input.Seek(resume);
    }

    private bool ReadIdx1()
    {
        if (_idx1Size < 16)
        {
            return false;
        }

        long count = _idx1Size / 16;
        if (count > _context.Limits.MaxIndexEntries)
        {
            throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, count);
        }

        byte[] entries = new byte[count * 16];
        _input.Seek(_idx1Offset);
        if (!_input.TryReadExactly(entries))
        {
            return false;
        }

        // Offsets count from the 'movi' FourCC in most files and from the file start in some; check the first entry.
        long? baseOffset = null;
        for (int i = 0; i < count && baseOffset is null; i++)
        {
            ReadOnlySpan<byte> e = entries.AsSpan(i * 16, 16);
            uint id = FourCC.Read(e);
            if (FourCC.StreamNumber(id) < 0 || (BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) & AviifList) != 0)
            {
                continue;
            }

            long offset = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            foreach (long candidate in (ReadOnlySpan<long>)[_moviListStart, 0, _moviDataStart])
            {
                if (ChunkIdAt(candidate + offset) == id)
                {
                    baseOffset = candidate;
                    break;
                }
            }

            if (baseOffset is null)
            {
                return false;
            }
        }

        if (baseOffset is null)
        {
            return false;
        }

        AviIndex[] indexes = [.. _streams.Select(_ => new AviIndex())];
        long[] ticks = [.. _streams.Select(s => (long)s.Start)];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = entries.AsSpan(i * 16, 16);
            int number = FourCC.StreamNumber(FourCC.Read(e));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            if (number < 0 || number >= _streams.Count || (flags & AviifList) != 0)
            {
                continue;
            }

            long offset = baseOffset.Value + BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), int.MaxValue);
            if (offset > _input.Length - 8)
            {
                return false;
            }

            indexes[number].Add(offset, size, ticks[number], (flags & AviifKeyframe) != 0);
            ticks[number] += _streams[number].TicksFor(size);
        }

        if (indexes.Any(x => !x.IsSorted()))
        {
            return false;
        }

        for (int i = 0; i < _streams.Count; i++)
        {
            _streams[i].Index = indexes[i];
        }

        return true;
    }

    private AviIndex ReadOpenDmlIndex(AviStream stream)
    {
        ReadOnlySpan<byte> super = stream.SuperIndex;
        if (super.Length < 24)
        {
            throw _input.Corrupt("The indx chunk is too short.");
        }

        int longsPerEntry = BinaryPrimitives.ReadUInt16LittleEndian(super);
        byte indexType = super[3];
        long entriesInUse = BinaryPrimitives.ReadUInt32LittleEndian(super[4..]);
        AviIndex index = new();
        long ticks = stream.Start;
        if (indexType == 0x01)
        {
            // A standard index stored directly in the strl.
            ReadStandardIndexEntries(super, stream, index, ref ticks);
            return index;
        }

        if (indexType != 0x00 || longsPerEntry != 4 || 24 + (entriesInUse * 16) > super.Length)
        {
            throw _input.Corrupt("The indx super index is malformed.");
        }

        for (int i = 0; i < entriesInUse; i++)
        {
            ReadOnlySpan<byte> e = super.Slice(24 + (i * 16), 16);
            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(e);
            int size = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
            if (offset < 0 || offset + 8 > _input.Length || size < 32 || size > _context.Limits.MaxHeaderBytes)
            {
                throw _input.Corrupt("An indx entry points outside the file.");
            }

            _input.Seek(offset);
            byte[] chunk = new byte[Math.Min(size, _input.Length - offset)];
            _input.ReadExactly(chunk);
            if (FourCC.IndexStreamNumber(FourCC.Read(chunk)) < 0)
            {
                throw _input.Corrupt(offset, "An indx entry does not point at an ix## chunk.");
            }

            ReadStandardIndexEntries(chunk.AsSpan(8), stream, index, ref ticks);
        }

        return index;
    }

    private void ReadStandardIndexEntries(ReadOnlySpan<byte> ix, AviStream stream, AviIndex index, ref long ticks)
    {
        // AVISTDINDEX: wLongsPerEntry, bIndexSubType, bIndexType, nEntriesInUse, dwChunkId, qwBaseOffset, dwReserved, entries.
        if (ix.Length < 24)
        {
            throw _input.Corrupt("An ix## chunk is too short.");
        }

        int longsPerEntry = BinaryPrimitives.ReadUInt16LittleEndian(ix);
        long entries = BinaryPrimitives.ReadUInt32LittleEndian(ix[4..]);
        long baseOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(ix[12..]);
        int entrySize = longsPerEntry * 4;
        if (entrySize < 8 || 24 + (entries * entrySize) > ix.Length)
        {
            throw _input.Corrupt("An ix## chunk is malformed.");
        }

        if (index.Count + entries > _context.Limits.MaxIndexEntries)
        {
            throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, index.Count + entries);
        }

        for (int i = 0; i < entries; i++)
        {
            ReadOnlySpan<byte> e = ix.Slice(24 + (i * entrySize), entrySize);
            long offset = baseOffset + BinaryPrimitives.ReadUInt32LittleEndian(e) - 8;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            int length = (int)(size & 0x7FFFFFFF);
            if (offset < 0 || offset > _input.Length - 8 || baseOffset < 0)
            {
                throw _input.Corrupt("An ix## entry points outside the file.");
            }

            index.Add(offset, length, ticks, (size & 0x80000000) == 0);
            ticks += stream.TicksFor(length);
        }
    }

    private uint ChunkIdAt(long offset)
    {
        if (offset < 0 || offset + 4 > _input.Length)
        {
            return 0;
        }

        _input.Seek(offset);
        return _input.ReadUInt32LittleEndian();
    }

    /// <summary>Scans forward from a damaged chunk header for the next stream chunk. Seekable streams only.</summary>
    private bool Resync(long badChunkStart)
    {
        if (!_input.CanSeek)
        {
            return false;
        }

        _input.Seek(badChunkStart + 1);
        uint window = 0;
        int seen = 0;
        for (int scanned = 0; scanned < ResyncLimit; scanned++)
        {
            int b = _input.PeekByte();
            if (b < 0)
            {
                return false;
            }

            _input.ReadByte();
            window = (window >> 8) | ((uint)b << 24);
            if (++seen >= 4)
            {
                int number = FourCC.StreamNumber(window);
                if ((number >= 0 && number < _streams.Count) || window is FourCC.List or FourCC.Riff)
                {
                    _input.Seek(_input.Position - 4);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One AVI stream: its headers, index and read position.</summary>
    private sealed class AviStream(int number)
    {
        public int Number { get; } = number;

        public uint Type { get; set; }

        public uint Handler { get; set; }

        public uint Scale { get; set; }

        public uint Rate { get; set; }

        public uint Start { get; set; }

        public uint Length { get; set; }

        public uint SampleSize { get; set; }

        public byte[] Format { get; set; } = [];

        public byte[]? SuperIndex { get; set; }

        public string? Name { get; set; }

        public AviIndex? Index { get; set; }

        public TrackInfo Track { get; private set; } = null!;

        public Rational TimeBase { get; private set; } = new(1, 1);

        public int ChunkCount { get; private set; }

        public long Ticks { get; private set; }

        /// <summary>Stream ticks a chunk of this many bytes lasts: one per chunk, or bytes over the sample size for CBR audio.</summary>
        public long TicksFor(long bytes) => Type == FourCC.Auds && SampleSize > 0 ? bytes / SampleSize : 1;

        public void Advance(long bytes)
        {
            ChunkCount++;
            Ticks += TicksFor(bytes);
        }

        public void Reposition(int chunk)
        {
            ChunkCount = chunk;
            Ticks = chunk < Index!.Count ? Index.Ticks(chunk) : Index.Count == 0 ? Start : Index.Ticks(Index.Count - 1) + TicksFor(Index.Size(Index.Count - 1));
        }

        public void BuildTrack(long totalFramesOpenDml)
        {
            Ticks = Start;
            long length = Length;
            if (Index is { Count: > 0 } index && Type != FourCC.Auds)
            {
                length = Math.Max(length, index.Count);
            }

            if (Type == FourCC.Vids)
            {
                (string codecId, byte[] codecPrivate, int width, int height, uint compression) = AviCodecs.ReadBitmapInfo(Format, Handler);
                Codecs.Raw.RawLayout? raw = codecId == CodecIds.Uncompressed ? Codecs.Raw.RawLayout.FromFourCC(AviCodecs.UncompressedFourCC(compression)!) : null;
                (int BitDepth, Pixels.ChromaFormat? ChromaFormat)? ffv1 = codecId == CodecIds.Ffv1 ? Codecs.Ffv1Codec.Layout(codecPrivate) : null;
                TimeBase = Scale > 0 && Rate > 0 ? new Rational(Scale, Rate) : new Rational(1, 25);
                if (totalFramesOpenDml > length)
                {
                    length = totalFramesOpenDml;
                }

                Track = new VideoTrackInfo
                {
                    Number = Number,
                    Kind = TrackKind.Video,
                    CodecId = codecId,
                    CodecPrivate = codecPrivate,
                    TimeBase = TimeBase,
                    Width = width,
                    Height = height,
                    FrameRate = TimeBase.Reciprocal(),
                    Duration = new MediaTime(Start + length, TimeBase),
                    Name = Name,
                    FourCC = raw?.FourCC,
                    ChromaFormat = raw?.Chroma ?? ffv1?.ChromaFormat,
                    BitDepth = raw?.BitDepth ?? ffv1?.BitDepth ?? 0,
                };
            }
            else if (Type == FourCC.Auds)
            {
                (string codecId, byte[] codecPrivate, int sampleRate, int channels, int bits) = AviCodecs.ReadWaveFormat(Format);
                TimeBase = Scale > 0 && Rate > 0 ? new Rational(Scale, Rate) : new Rational(1, Math.Max(1, sampleRate));
                if (Index is { Count: > 0 } audio)
                {
                    length = Math.Max(length, audio.Ticks(audio.Count - 1) + TicksFor(audio.Size(audio.Count - 1)) - Start);
                }

                Track = new AudioTrackInfo
                {
                    Number = Number,
                    Kind = TrackKind.Audio,
                    CodecId = codecId,
                    CodecPrivate = codecPrivate,
                    TimeBase = TimeBase,
                    SampleRate = sampleRate,
                    Channels = channels,
                    BitsPerSample = bits,
                    Duration = new MediaTime(Start + length, TimeBase),
                    Name = Name,
                };
            }
            else
            {
                TimeBase = Scale > 0 && Rate > 0 ? new Rational(Scale, Rate) : new Rational(1, 1000);
                Track = new TrackInfo
                {
                    Number = Number,
                    Kind = Type == FourCC.Txts ? TrackKind.Subtitle : TrackKind.Other,
                    CodecId = "X_AVI/" + FourCC.ToString(Type) + "/" + FourCC.ToString(Handler),
                    CodecPrivate = Format,
                    TimeBase = TimeBase,
                    Name = Name,
                };
            }
        }
    }
}
