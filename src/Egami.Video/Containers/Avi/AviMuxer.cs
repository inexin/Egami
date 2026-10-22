using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Avi;

/// <summary>
/// Writes AVI files (Microsoft AVI RIFF reference) with OpenDML indexes (OpenDML AVI 1.02): a RIFF 'AVI ' list holding the
/// headers, the first movi list and an idx1 index, then RIFF 'AVIX' lists once the file passes 1 GB. Every movi list ends
/// with one ix## standard index per stream, listed in each stream's indx super index, so players find every frame in files
/// of any size. Sizes and counts are patched when the file is finished, so the output must be seekable.
/// </summary>
internal sealed class AviMuxer : IMuxer
{
    private const int SuperIndexEntries = 256;
    private const int SuperIndexSize = 24 + (SuperIndexEntries * 16);
    private const int DmlhSize = 248;
    private const uint AvifHasIndex = 0x10;
    private const uint AvifIsInterleaved = 0x100;
    private const uint AvifTrustCkType = 0x800;
    private const uint AviifKeyframe = 0x10;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Stream _output;
    private readonly List<StreamWriter> _streams = [];
    private readonly Dictionary<int, StreamWriter> _byInputNumber = [];
    private readonly List<(uint Id, uint Flags, uint Offset, uint Size)> _idx1 = [];
    private long _avihOffset;
    private long _dmlhOffset;
    private long _riffStart;
    private long _moviStart;
    private bool _firstRiff = true;
    private bool _finished;
    private readonly Action<MediaWarning>? _warnings;
    private readonly MediaMetadata _metadata;

    public AviMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, bool leaveOpen, MediaContext? context = null, MediaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new NotSupportedException("AVI needs a seekable, writable output to patch its sizes and index.");
        }

        if (tracks.Count is 0 or > 100)
        {
            throw new NotSupportedException("AVI holds 1 to 100 streams.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _warnings = context?.Warnings;
        _output = new OutputStream(stream);
        List<TrackInfo> outputs = [];
        foreach (TrackInfo track in tracks)
        {
            if (_byInputNumber.ContainsKey(track.Number))
            {
                throw new ArgumentException($"Track number {track.Number} appears twice.", nameof(tracks));
            }

            StreamWriter writer = new(_streams.Count, track);
            _streams.Add(writer);
            _byInputNumber[track.Number] = writer;
            outputs.Add(writer.Output);
        }

        Tracks = outputs;
        _metadata = metadata ?? MediaMetadata.Empty;
        if (_metadata.Chapters.Count > 0 || _metadata.Attachments.Count > 0 || tracks.Any(t => t.Tags.Count > 0))
        {
            _warnings?.Invoke(new MediaWarning("metadata-dropped", "AVI holds no chapters, attachments or track tags; they are left out."));
        }

        WriteHeaders();
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    /// <summary>Gets or sets the size at which a new RIFF list starts. 1 GB, as OpenDML recommends; tests lower it.</summary>
    internal long SegmentLimit { get; set; } = 1L << 30;

    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished)
        {
            throw new InvalidOperationException("The muxer was finished.");
        }

        if (!_byInputNumber.TryGetValue(packet.TrackNumber, out StreamWriter? stream))
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        ReadOnlySpan<byte> data = packet.Data.Span;
        stream.Prepare(packet);
        if (stream.IsVideo)
        {
            // Constant frame rate: empty "drop" chunks fill gaps in the timestamps, as AVI players expect.
            long frame = stream.FrameIndex(packet.Timestamp);
            long error = Math.Abs(new MediaTime(frame, stream.TimeBase).Rescale(Rational.Nanosecond).Ticks - packet.Timestamp.Rescale(Rational.Nanosecond).Ticks);
            if (!stream.WarnedVfr && (error > 1_000_000 || frame < stream.Chunks))
            {
                stream.WarnedVfr = true;
                _warnings?.Invoke(new MediaWarning("avi-vfr",
                    $"Track {packet.TrackNumber} has timestamps off AVI's fixed {stream.TimeBase.Reciprocal()} fps grid; they are rounded to it.", packet.TrackNumber));
            }

            while (stream.Chunks < frame)
            {
                WriteChunk(stream, [], key: false);
            }
        }

        WriteChunk(stream, data, packet.IsKeyframe);
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        CloseSegment();
        PatchHeaders();
        _output.Flush();
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _output.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private void WriteChunk(StreamWriter stream, ReadOnlySpan<byte> data, bool key)
    {
        long needed = 8 + data.Length + (data.Length & 1);
        if (_output.Position - _riffStart + needed + IndexReserve() > SegmentLimit && stream.SegmentEntries.Count + _streams.Sum(s => s.SegmentEntries.Count) > 0)
        {
            CloseSegment();
            OpenSegment();
        }

        long chunkStart = _output.Position;
        WriteUInt32(stream.ChunkId);
        WriteUInt32((uint)data.Length);
        _output.Write(data);
        if ((data.Length & 1) != 0)
        {
            _output.WriteByte(0);
        }

        stream.SegmentEntries.Add((chunkStart + 8, (uint)data.Length, key));
        stream.Record(data.Length);
        if (_firstRiff)
        {
            _idx1.Add((stream.ChunkId, key ? AviifKeyframe : 0, (uint)(chunkStart - (_moviStart - 4)), (uint)data.Length));
        }
    }

    private long IndexReserve() => _streams.Sum(s => 32 + (8L * (s.SegmentEntries.Count + 1))) + (_firstRiff ? 16L * (_idx1.Count + 1) + 8 : 0);

    private void WriteHeaders()
    {
        _riffStart = 0;
        WriteFourCC(FourCC.Riff);
        WriteUInt32(0);
        WriteFourCC(FourCC.Avi);

        long hdrl = BeginList(FourCC.Hdrl);
        WriteFourCC(FourCC.Avih);
        WriteUInt32(56);
        _avihOffset = _output.Position;
        _output.Write(new byte[56]);

        foreach (StreamWriter stream in _streams)
        {
            long strl = BeginList(FourCC.Strl);
            WriteFourCC(FourCC.Strh);
            WriteUInt32(56);
            stream.StrhOffset = _output.Position;
            _output.Write(new byte[56]);

            WriteFourCC(FourCC.Strf);
            WriteUInt32((uint)stream.Format.Length);
            _output.Write(stream.Format);
            if ((stream.Format.Length & 1) != 0)
            {
                _output.WriteByte(0);
            }

            if (stream.Output.Name is { Length: > 0 } name)
            {
                byte[] text = Encoding.UTF8.GetBytes(name + "\0");
                WriteFourCC(FourCC.Strn);
                WriteUInt32((uint)text.Length);
                _output.Write(text);
                if ((text.Length & 1) != 0)
                {
                    _output.WriteByte(0);
                }
            }

            WriteFourCC(FourCC.Indx);
            WriteUInt32(SuperIndexSize);
            stream.IndxOffset = _output.Position;
            _output.Write(new byte[SuperIndexSize]);
            EndList(strl);
        }

        long odml = BeginList(FourCC.Odml);
        WriteFourCC(FourCC.Dmlh);
        WriteUInt32(DmlhSize);
        _dmlhOffset = _output.Position;
        _output.Write(new byte[DmlhSize]);
        EndList(odml);
        EndList(hdrl);

        if (_metadata.Tags.Count > 0)
        {
            long info = BeginList(FourCC.Info);
            foreach ((string name, string value) in _metadata.Tags)
            {
                string? chunk = Array.Find(FourCC.InfoTags, t => t.Tag == name).Chunk
                    ?? (name.Length == 4 && name[0] == 'I' && name.All(char.IsAsciiLetterUpper) ? name : null);
                if (chunk is null || value.Length == 0)
                {
                    continue;
                }

                byte[] text = Encoding.UTF8.GetBytes(value + " ");
                WriteFourCC(FourCC.FromString(chunk));
                WriteUInt32((uint)text.Length);
                _output.Write(text);
                if ((text.Length & 1) != 0)
                {
                    _output.WriteByte(0);
                }
            }

            EndList(info);
        }

        BeginMovi();
    }

    private void BeginMovi()
    {
        WriteFourCC(FourCC.List);
        WriteUInt32(0);
        WriteFourCC(FourCC.Movi);
        _moviStart = _output.Position;
    }

    private void OpenSegment()
    {
        _riffStart = _output.Position;
        WriteFourCC(FourCC.Riff);
        WriteUInt32(0);
        WriteFourCC(FourCC.Avix);
        BeginMovi();
    }

    private void CloseSegment()
    {
        // One ix## standard index per stream at the end of the movi list.
        Span<byte> head = stackalloc byte[24];
        Span<byte> entry = stackalloc byte[8];
        foreach (StreamWriter stream in _streams)
        {
            if (stream.SegmentEntries.Count == 0)
            {
                continue;
            }

            if (stream.SuperEntries.Count == SuperIndexEntries)
            {
                throw new NotSupportedException($"AVI files are limited to {SuperIndexEntries} RIFF lists ({SuperIndexEntries} GB) here.");
            }

            long baseOffset = _moviStart - 4;
            long ixStart = _output.Position;
            int entries = stream.SegmentEntries.Count;
            WriteUInt32(FourCC.FromString($"ix{stream.Number / 10}{stream.Number % 10}"));
            WriteUInt32((uint)(24 + (entries * 8)));
            head.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(head, 2);
            head[2] = 0;
            head[3] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)entries);
            BinaryPrimitives.WriteUInt32LittleEndian(head[8..], stream.ChunkId);
            BinaryPrimitives.WriteInt64LittleEndian(head[12..], baseOffset);
            _output.Write(head);
            long duration = 0;
            foreach ((long dataOffset, uint size, bool key) in stream.SegmentEntries)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry, checked((uint)(dataOffset - baseOffset)));
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], key ? size : size | 0x80000000);
                _output.Write(entry);
                duration += stream.TicksFor(size);
            }

            stream.SuperEntries.Add((ixStart, 32 + (entries * 8), (uint)duration));
            stream.SegmentEntries.Clear();
        }

        EndList(_moviStart - 12);
        if (_firstRiff)
        {
            WriteFourCC(FourCC.Idx1);
            WriteUInt32((uint)(_idx1.Count * 16));
            Span<byte> e = stackalloc byte[16];
            foreach ((uint id, uint flags, uint offset, uint size) in _idx1)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(e, id);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], flags);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], offset);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], size);
                _output.Write(e);
            }

            foreach (StreamWriter stream in _streams)
            {
                stream.FirstRiffChunks = stream.Chunks;
            }

            _idx1.Clear();
            _firstRiff = false;
        }

        EndList(_riffStart);
    }

    private void PatchHeaders()
    {
        long end = _output.Position;
        StreamWriter? video = _streams.FirstOrDefault(s => s.IsVideo);

        Span<byte> avih = stackalloc byte[56];
        Rational frameTime = video?.TimeBase ?? new Rational(1, 25);
        BinaryPrimitives.WriteUInt32LittleEndian(avih, (uint)Math.Clamp(frameTime.Numerator * 1_000_000 / frameTime.Denominator, 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(avih[12..], AvifHasIndex | AvifIsInterleaved | AvifTrustCkType);
        BinaryPrimitives.WriteUInt32LittleEndian(avih[16..], (uint)(video?.FirstRiffChunks ?? 0));
        BinaryPrimitives.WriteUInt32LittleEndian(avih[24..], (uint)_streams.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(avih[28..], (uint)Math.Min(uint.MaxValue, _streams.Max(s => s.MaxChunk) + 8));
        if (video?.Output is VideoTrackInfo v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(avih[32..], v.Width);
            BinaryPrimitives.WriteInt32LittleEndian(avih[36..], v.Height);
        }

        Patch(_avihOffset, avih);

        foreach (StreamWriter stream in _streams)
        {
            Patch(stream.StrhOffset, stream.Strh());
            byte[] indx = new byte[SuperIndexSize];
            BinaryPrimitives.WriteUInt16LittleEndian(indx, 4);
            indx[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(indx.AsSpan(4), (uint)stream.SuperEntries.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(indx.AsSpan(8), stream.ChunkId);
            for (int i = 0; i < stream.SuperEntries.Count; i++)
            {
                (long offset, int size, uint duration) = stream.SuperEntries[i];
                Span<byte> e = indx.AsSpan(24 + (i * 16), 16);
                BinaryPrimitives.WriteInt64LittleEndian(e, offset);
                BinaryPrimitives.WriteInt32LittleEndian(e[8..], size);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], duration);
            }

            Patch(stream.IndxOffset, indx);
        }

        Span<byte> dmlh = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dmlh, (uint)(video?.Chunks ?? 0));
        Patch(_dmlhOffset, dmlh);
        _output.Position = end;
    }

    private void Patch(long offset, ReadOnlySpan<byte> bytes)
    {
        _output.Position = offset;
        _output.Write(bytes);
    }

    private long BeginList(uint type)
    {
        long start = _output.Position;
        WriteFourCC(FourCC.List);
        WriteUInt32(0);
        WriteFourCC(type);
        return start;
    }

    private void EndList(long start)
    {
        long end = _output.Position;
        _output.Position = start + 4;
        WriteUInt32(checked((uint)(end - start - 8)));
        _output.Position = end;
    }

    private void WriteFourCC(uint code) => WriteUInt32(code);

    private void WriteUInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        _output.Write(b);
    }

    /// <summary>One output stream: its headers, timing and index entries.</summary>
    private sealed class StreamWriter
    {
        private readonly TrackInfo _input;
        private bool _timingFixed;

        public StreamWriter(int number, TrackInfo input)
        {
            Number = number;
            _input = input;
            switch (input)
            {
                case VideoTrackInfo video:
                    IsVideo = true;
                    Type = FourCC.Vids;
                    Format = AviCodecs.WriteBitmapInfo(video, out uint handler);
                    Handler = handler;
                    ChunkId = FourCC.ChunkId(number, "dc");
                    TimeBase = video.FrameRate is { Numerator: > 0 } rate ? rate.Reciprocal() : CoarseOrNull(video.TimeBase) ?? new Rational(1, 25);
                    _timingFixed = video.FrameRate is not null || CoarseOrNull(video.TimeBase) is not null;
                    Output = video with { Number = number, TimeBase = TimeBase, FrameRate = TimeBase.Reciprocal() };
                    break;
                case AudioTrackInfo audio:
                    Type = FourCC.Auds;
                    Format = AviCodecs.WriteWaveFormat(audio);
                    ChunkId = FourCC.ChunkId(number, "wb");
                    BlockAlign = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(Format.AsSpan(12)));
                    IsPcm = audio.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmFloat;
                    SampleSize = IsPcm ? (uint)BlockAlign : 0;
                    TimeBase = IsPcm ? new Rational(1, Math.Max(1, audio.SampleRate)) : new Rational(DefaultFrameSamples(audio), Math.Max(1, audio.SampleRate));
                    _timingFixed = IsPcm;
                    Output = audio with { Number = number, TimeBase = TimeBase };
                    break;
                default:
                    string[] parts = input.CodecId.Split('/');
                    if (parts.Length != 3 || parts[0] != "X_AVI" || parts[1].Length != 4 || parts[2].Length != 4)
                    {
                        throw new NotSupportedException($"AVI cannot hold codec '{input.CodecId}'.");
                    }

                    Type = FourCC.FromString(parts[1]);
                    Handler = FourCC.FromString(parts[2]);
                    Format = input.CodecPrivate.ToArray();
                    ChunkId = FourCC.ChunkId(number, "tx");
                    TimeBase = input.TimeBase;
                    Output = input with { Number = number };
                    break;
            }
        }

        public int Number { get; }

        public bool IsVideo { get; }

        public bool IsPcm { get; }

        public uint Type { get; }

        public uint Handler { get; }

        public byte[] Format { get; }

        public uint ChunkId { get; }

        public int BlockAlign { get; }

        public uint SampleSize { get; }

        public Rational TimeBase { get; private set; }

        public TrackInfo Output { get; }

        public long StrhOffset { get; set; }

        public long IndxOffset { get; set; }

        public long Chunks { get; private set; }

        public long FirstRiffChunks { get; set; }

        public long Ticks { get; private set; }

        public int MaxChunk { get; private set; }

        public bool WarnedVfr { get; set; }

        public List<(long DataOffset, uint Size, bool Key)> SegmentEntries { get; } = [];

        public List<(long Offset, int Size, uint Duration)> SuperEntries { get; } = [];

        public long TicksFor(long bytes) => SampleSize > 0 ? bytes / SampleSize : 1;

        /// <summary>Fixes the time base from the first packet when the track did not give one.</summary>
        public void Prepare(EncodedPacket packet)
        {
            if (_timingFixed)
            {
                return;
            }

            _timingFixed = true;
            if (packet.Duration is { Ticks: > 0 } duration)
            {
                if (IsVideo)
                {
                    MediaTime ns = duration.Rescale(Rational.Nanosecond);
                    TimeBase = new Rational(ns.Ticks, 1_000_000_000);
                }
                else if (Output is AudioTrackInfo audio && audio.SampleRate > 0)
                {
                    long samples = duration.Rescale(new Rational(1, audio.SampleRate)).Ticks;
                    if (samples > 0)
                    {
                        TimeBase = new Rational(samples, audio.SampleRate);
                    }
                }
            }
        }

        public long FrameIndex(MediaTime timestamp) => Math.Max(0, timestamp.Rescale(TimeBase).Ticks);

        public void Record(int size)
        {
            Chunks++;
            Ticks += TicksFor(size);
            MaxChunk = Math.Max(MaxChunk, size);
        }

        public byte[] Strh()
        {
            byte[] strh = new byte[56];
            Span<byte> s = strh;
            BinaryPrimitives.WriteUInt32LittleEndian(s, Type);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], Handler);
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)Math.Min(TimeBase.Numerator, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)Math.Min(TimeBase.Denominator, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(s[32..], (uint)Math.Min(Ticks, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)MaxChunk);
            BinaryPrimitives.WriteUInt32LittleEndian(s[40..], uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(s[44..], SampleSize);
            if (Output is VideoTrackInfo video)
            {
                BinaryPrimitives.WriteInt16LittleEndian(s[52..], (short)Math.Min(video.Width, short.MaxValue));
                BinaryPrimitives.WriteInt16LittleEndian(s[54..], (short)Math.Min(video.Height, short.MaxValue));
            }

            return strh;
        }

        private static Rational? CoarseOrNull(Rational timeBase) =>
            timeBase.Numerator > 0 && timeBase >= new Rational(1, 1000) ? timeBase : null;

        private static int DefaultFrameSamples(AudioTrackInfo audio) => audio.CodecId switch
        {
            CodecIds.Mp3 or CodecIds.Mp2 => audio.SampleRate < 32000 ? 576 : 1152,
            CodecIds.Ac3 => 1536,
            CodecIds.Aac => 1024,
            _ => 1024,
        };
    }
}
