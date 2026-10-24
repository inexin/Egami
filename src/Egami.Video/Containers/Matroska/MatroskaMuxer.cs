using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs.Vp9;

namespace Egami.Video.Containers.Matroska;

/// <summary>
/// Writes Matroska or WebM (RFC 9559): EBML header, a Segment with a SeekHead, Info and Tracks, Clusters of SimpleBlocks (BlockGroups for packets with additions, such as WebM alpha) and
/// Cues. A new Cluster starts on a keyframe once about a second or 5 MB has passed. On a seekable output the Segment size,
/// the Duration and the SeekHead are written at the end into space reserved up front; a non-seekable output gets an
/// unknown-size Segment with no Cues or Duration.
/// </summary>
internal sealed class MatroskaMuxer : IMuxer
{
    private const long TimestampScale = 1_000_000;
    private const int SeekHeadReserve = 160;
    private const long ClusterMilliseconds = 1000;
    private const long ClusterBytes = 5L << 20;

    private static readonly Rational Milliseconds = new(1, 1000);

    /// <summary>Uncompressed layouts written as V_UNCOMPRESSED with a ColourSpace; others use the Video for Windows mode, which more readers accept.</summary>
    private static readonly HashSet<string> NativeUncompressed = ["I420", "Y42B", "Y800", "NV12", "YUY2", "UYVY"];
    private static readonly HashSet<string> WebMCodecs = [CodecIds.Vp8, CodecIds.Vp9, CodecIds.Av1, CodecIds.Opus, CodecIds.Vorbis, CodecIds.WebVtt, CodecIds.WebMWebVtt, "D_WEBVTT/CAPTIONS", "D_WEBVTT/DESCRIPTIONS", "D_WEBVTT/METADATA"];

    private readonly Stream _stream;
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly bool _seekable;
    private readonly List<TrackWriter> _tracks = [];
    private readonly Dictionary<int, TrackWriter> _byInputNumber = [];
    private readonly List<(long Time, int Track, long Cluster, long Relative)> _cues = [];
    private readonly MemoryStream _cluster = new();
    private readonly TrackWriter? _cueTrack;
    private readonly string _docType;
    private readonly List<EncodedPacket> _held = [];
    private bool _headersWritten;
    private long _segmentSizeOffset;
    private long _segmentDataStart;
    private long _seekHeadOffset;
    private long _durationOffset;
    private long _infoPosition;
    private long _tracksPosition;
    private long _tagsPosition = -1;
    private long _chaptersPosition = -1;
    private long _attachmentsPosition = -1;
    private readonly MediaMetadata _metadata = MediaMetadata.Empty;
    private long _clusterTime = long.MinValue;
    private bool _clusterHasCue;
    private long _endTime;
    private bool _finished;

    public MatroskaMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, string docType, bool leaveOpen, MediaMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!stream.CanWrite)
        {
            throw new NotSupportedException("The output is not writable.");
        }

        if (tracks.Count is 0 or > 126)
        {
            throw new NotSupportedException("This writer holds 1 to 126 tracks.");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _seekable = stream.CanSeek;
        _output = new OutputStream(stream);
        List<TrackInfo> outputs = [];
        foreach (TrackInfo track in tracks)
        {
            if (_byInputNumber.ContainsKey(track.Number))
            {
                throw new ArgumentException($"Track number {track.Number} appears twice.", nameof(tracks));
            }

            if (track is VideoTrackInfo { CodecId: CodecIds.Uncompressed, FourCC: null })
            {
                throw new NotSupportedException("Matroska names uncompressed layouts by FourCC; this one (planar YUV beyond I420, Y42B, 444P and Y800) has none. Write YUV4MPEG2 instead.");
            }

            if (track.CodecId.StartsWith("X_", StringComparison.Ordinal))
            {
                throw new NotSupportedException($"Matroska cannot hold codec '{track.CodecId}'.");
            }

            if (docType == "webm" && !WebMCodecs.Contains(track.CodecId))
            {
                throw new NotSupportedException($"WebM cannot hold codec '{track.CodecId}'; write Matroska instead.");
            }

            TrackWriter writer = new(_tracks.Count + 1, track);
            _tracks.Add(writer);
            _byInputNumber[track.Number] = writer;
            outputs.Add(writer.Output);
        }

        Tracks = outputs;
        _cueTrack = _tracks.FirstOrDefault(t => t.Input.Kind == TrackKind.Video) ?? _tracks[0];
        _metadata = metadata ?? MediaMetadata.Empty;
        _docType = docType;

        // An AV1 track needs its av1C in CodecPrivate; a track from IVF or raw OBUs has none, so the headers wait for its first
        // packet's sequence header, and packets of other tracks before it are held.
        if (!_tracks.Any(NeedsAv1Config))
        {
            WriteHeaders();
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

        if (!_byInputNumber.TryGetValue(packet.TrackNumber, out TrackWriter? track))
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        if (!_headersWritten)
        {
            if (NeedsAv1Config(track))
            {
                track.Input = track.Input with { CodecPrivate = Av1ConfigOf(packet.Data.Span) };
                track.Av1ConfigLooked = true;
            }

            _held.Add(packet.Clone());
            if (_tracks.Any(NeedsAv1Config))
            {
                return;
            }

            WriteHeaders();
            foreach (EncodedPacket held in _held)
            {
                using (held)
                {
                    WritePacket(held);
                }
            }

            _held.Clear();
            return;
        }

        ReadOnlySpan<byte> data = packet.Data.Span;
        if (track.Input.CodecId == CodecIds.Vp9)
        {
            // WebM players expect one block per shown frame, so VP9 hidden frames that arrive as packets of their own (some IVF
            // and raw sources) are held and joined with the next shown frame into a superframe.
            if (Vp9Superframe.IsLoneHiddenFrame(data) && track.HiddenVp9Frames.Count < 7)
            {
                if (track.HiddenVp9Frames.Count == 0)
                {
                    track.HiddenVp9Time = packet.Timestamp;
                    track.HiddenVp9Keyframe = packet.IsKeyframe;
                }

                track.HiddenVp9Frames.Add(data.ToArray());
                return;
            }

            if (track.HiddenVp9Frames.Count > 0)
            {
                List<ReadOnlyMemory<byte>> frames = [.. track.HiddenVp9Frames, .. Vp9Superframe.Split(packet.Data)];
                track.HiddenVp9Frames.Clear();
                if (frames.Count <= 8)
                {
                    data = Vp9Superframe.Merge(frames);
                }
            }
        }

        WriteBlock(packet, track, data);
    }

    private void WriteBlock(EncodedPacket packet, TrackWriter track, ReadOnlySpan<byte> data)
    {
        long time = packet.Timestamp.Rescale(Milliseconds).Ticks + track.DelayMilliseconds;
        bool startsCluster = _clusterTime == long.MinValue
            || time - _clusterTime > short.MaxValue
            || time - _clusterTime < short.MinValue
            || (track == _cueTrack && packet.IsKeyframe && (time - _clusterTime >= ClusterMilliseconds || _cluster.Length >= ClusterBytes))
            || _cluster.Length + data.Length > int.MaxValue - 1024;
        if (startsCluster)
        {
            FlushCluster();
            _clusterTime = Math.Max(0, time);
            EbmlWriter.Unsigned(_cluster, MatroskaIds.Timestamp, (ulong)_clusterTime);
        }

        if (track == _cueTrack && packet.IsKeyframe && !_clusterHasCue && _seekable)
        {
            // The block's position is patched in when the cluster is written.
            _cues.Add((time, track.Number, -1, _cluster.Length));
            _clusterHasCue = true;
        }

        // SimpleBlock: track number VINT, 16-bit time relative to the Cluster, flags, data. A packet with additions needs a
        // BlockGroup, where a ReferenceBlock (pointing at the track's previous block) marks a frame that is not a keyframe.
        long relative = time - _clusterTime;
        long discardPadding = DiscardPadding(track, packet, data);

        // A subtitle shows for its BlockDuration, which only a BlockGroup has.
        long? blockDuration = track.Input.Kind == TrackKind.Subtitle && packet.Duration is { } shown ? Math.Max(0, shown.Rescale(Milliseconds).Ticks) : null;
        if (packet.Additions.Count == 0 && discardPadding == 0 && blockDuration is null)
        {
            EbmlWriter.Header(_cluster, MatroskaIds.SimpleBlock, 4 + data.Length);
            WriteBlockHeader(_cluster, track.Number, relative, packet.IsKeyframe ? (byte)0x80 : (byte)0);
            _cluster.Write(data);
        }
        else
        {
            using MemoryStream group = new();
            EbmlWriter.Header(group, MatroskaIds.Block, 4 + data.Length);
            WriteBlockHeader(group, track.Number, relative, 0);
            group.Write(data);
            if (!packet.IsKeyframe)
            {
                long reference = track.LastTime is { } last && last != time ? last - time : -1;
                Span<byte> value = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(value, reference);
                int skip = 0;
                while (skip < 7 && value[skip] == (reference < 0 ? 0xFF : 0) && (value[skip + 1] & 0x80) == (reference < 0 ? 0x80 : 0))
                {
                    skip++;
                }

                EbmlWriter.Element(group, MatroskaIds.ReferenceBlock, value[skip..]);
            }

            if (blockDuration is { } duration)
            {
                EbmlWriter.Unsigned(group, MatroskaIds.BlockDuration, (ulong)duration);
            }

            if (discardPadding > 0)
            {
                // A signed element: written unsigned, a padding whose top bit is set would read back negative.
                EbmlWriter.Signed(group, MatroskaIds.DiscardPadding, discardPadding);
            }

            if (packet.Additions.Count > 0)
            {
                EbmlWriter.Master(group, MatroskaIds.BlockAdditions, a =>
                {
                    foreach (PacketAddition addition in packet.Additions)
                    {
                        EbmlWriter.Master(a, MatroskaIds.BlockMore, m =>
                        {
                            EbmlWriter.Unsigned(m, MatroskaIds.BlockAddId, (ulong)Math.Max(1, addition.Id));
                            EbmlWriter.Element(m, MatroskaIds.BlockAdditional, addition.Data.Span);
                        });
                    }
                });
            }

            EbmlWriter.Header(_cluster, MatroskaIds.BlockGroup, group.Length);
            group.Position = 0;
            group.CopyTo(_cluster);
        }

        track.LastTime = time;

        long end = time + (packet.Duration is { } d ? d.Rescale(Milliseconds).Ticks : track.DefaultMilliseconds);
        _endTime = Math.Max(_endTime, end - track.DelayMilliseconds);
    }

    /// <summary>The nanoseconds an Opus or Vorbis packet decodes past its stated duration, written as DiscardPadding; 0 for others.</summary>
    private static long DiscardPadding(TrackWriter track, EncodedPacket packet, ReadOnlySpan<byte> data)
    {
        int samples, rate;
        if (track.Input.CodecId == CodecIds.Opus && !data.IsEmpty)
        {
            (samples, rate) = (Audio.Codecs.Opus.OpusPacket.SampleCount(data, 48000), 48000);
        }
        else if (track.Input is AudioTrackInfo { CodecId: CodecIds.Vorbis } audio && !data.IsEmpty)
        {
            // Every packet goes through the clock, so each one's length follows from the one before.
            track.Vorbis ??= Audio.Codecs.Vorbis.VorbisPacketClock.Create(audio.CodecPrivate.Span);
            (samples, rate) = (track.Vorbis?.Next(data) ?? 0, audio.SampleRate);
        }
        else
        {
            return 0;
        }

        if (packet.Duration is not { } duration)
        {
            return 0;
        }

        long padding = new MediaTime(samples, new Rational(1, rate)).Rescale(Rational.Nanosecond).Ticks - duration.Rescale(Rational.Nanosecond).Ticks;
        return samples > 0 && padding > 0 ? padding : 0;
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        if (!_headersWritten)
        {
            // An AV1 track that never sent a packet is written without av1C.
            foreach (TrackWriter track in _tracks)
            {
                track.Av1ConfigLooked = true;
            }

            WriteHeaders();
            foreach (EncodedPacket held in _held)
            {
                using (held)
                {
                    WritePacket(held);
                }
            }

            _held.Clear();
        }

        foreach (TrackWriter track in _tracks)
        {
            // Hidden VP9 frames with no shown frame after them are written as they are.
            if (track.HiddenVp9Frames.Count > 0)
            {
                List<ReadOnlyMemory<byte>> frames = [.. track.HiddenVp9Frames];
                track.HiddenVp9Frames.Clear();
                using EncodedPacket rest = new(track.Input.Number, frames.Count == 1 ? frames[0] : Vp9Superframe.Merge(frames), track.HiddenVp9Time)
                {
                    IsKeyframe = track.HiddenVp9Keyframe,
                };
                WriteBlock(rest, track, rest.Data.Span);
            }
        }

        _finished = true;
        FlushCluster();
        if (!_seekable)
        {
            _output.Flush();
            return;
        }

        long cuesPosition = -1;
        if (_cues.Count > 0)
        {
            cuesPosition = _output.Position;
            EbmlWriter.Master(_output, MatroskaIds.Cues, cues =>
            {
                foreach ((long time, int track, long cluster, long relative) in _cues)
                {
                    EbmlWriter.Master(cues, MatroskaIds.CuePoint, point =>
                    {
                        EbmlWriter.Unsigned(point, MatroskaIds.CueTime, (ulong)Math.Max(0, time));
                        EbmlWriter.Master(point, MatroskaIds.CueTrackPositions, positions =>
                        {
                            EbmlWriter.Unsigned(positions, MatroskaIds.CueTrack, (ulong)track);
                            EbmlWriter.Unsigned(positions, MatroskaIds.CueClusterPosition, (ulong)cluster);
                            EbmlWriter.Unsigned(positions, MatroskaIds.CueRelativePosition, (ulong)relative);
                        });
                    });
                }
            });
        }

        long end = _output.Position;

        // Segment size, Duration and SeekHead into the space reserved for them.
        _output.Position = _segmentSizeOffset;
        EbmlWriter.Size(_output, end - _segmentDataStart, 8);
        _output.Position = _durationOffset;
        EbmlWriter.Float(_output, MatroskaIds.Duration, Math.Max(0, _endTime));
        _output.Position = _seekHeadOffset;
        using MemoryStream seekHead = new();
        EbmlWriter.Master(seekHead, MatroskaIds.SeekHead, s =>
        {
            WriteSeek(s, MatroskaIds.Info, _infoPosition);
            WriteSeek(s, MatroskaIds.Tracks, _tracksPosition);
            foreach ((uint id, long position) in (ReadOnlySpan<(uint, long)>)[(MatroskaIds.Tags, _tagsPosition), (MatroskaIds.Chapters, _chaptersPosition), (MatroskaIds.Attachments, _attachmentsPosition)])
            {
                if (position >= 0)
                {
                    WriteSeek(s, id, position);
                }
            }
            if (cuesPosition >= 0)
            {
                WriteSeek(s, MatroskaIds.Cues, cuesPosition);
            }
        });
        seekHead.Position = 0;
        seekHead.CopyTo(_output);
        EbmlWriter.Void(_output, SeekHeadReserve - (int)seekHead.Length);
        _output.Position = end;
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
            _cluster.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private void WriteSeek(Stream s, uint id, long position) =>
        EbmlWriter.Master(s, MatroskaIds.Seek, seek =>
        {
            Span<byte> idBytes = stackalloc byte[4];
            EbmlWriter.Element(seek, MatroskaIds.SeekId, idBytes[..Ebml.WriteId(idBytes, id)]);
            EbmlWriter.Unsigned(seek, MatroskaIds.SeekPosition, (ulong)(position - _segmentDataStart));
        });

    private void FlushCluster()
    {
        if (_cluster.Length == 0)
        {
            return;
        }

        long clusterStart = _output.Position;
        EbmlWriter.Header(_output, MatroskaIds.Cluster, _cluster.Length, 8);
        _cluster.Position = 0;
        _cluster.CopyTo(_output);
        _cluster.SetLength(0);
        for (int i = _cues.Count - 1; i >= 0 && _cues[i].Cluster < 0; i--)
        {
            _cues[i] = _cues[i] with { Cluster = clusterStart - _segmentDataStart };
        }

        _clusterHasCue = false;
    }

    private static bool NeedsAv1Config(TrackWriter track) =>
        track.Input.CodecId == CodecIds.Av1 && track.Input.CodecPrivate.IsEmpty && !track.Av1ConfigLooked;

    /// <summary>The av1C of a packet's first sequence header, or nothing when it has none or it cannot be read.</summary>
    private static ReadOnlyMemory<byte> Av1ConfigOf(ReadOnlySpan<byte> unit)
    {
        try
        {
            return Codecs.Av1Packets.ExtractConfig(unit) ?? ReadOnlyMemory<byte>.Empty;
        }
        catch (CorruptDataException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    private void WriteHeaders()
    {
        _headersWritten = true;
        string docType = _docType;
        EbmlWriter.Master(_output, MatroskaIds.Ebml, e =>
        {
            EbmlWriter.Unsigned(e, MatroskaIds.EbmlVersion, 1);
            EbmlWriter.Unsigned(e, MatroskaIds.EbmlReadVersion, 1);
            EbmlWriter.Unsigned(e, MatroskaIds.EbmlMaxIdLength, 4);
            EbmlWriter.Unsigned(e, MatroskaIds.EbmlMaxSizeLength, 8);
            EbmlWriter.Text(e, MatroskaIds.DocType, docType);
            EbmlWriter.Unsigned(e, MatroskaIds.DocTypeVersion, 4);
            EbmlWriter.Unsigned(e, MatroskaIds.DocTypeReadVersion, 2);
        });

        EbmlWriter.Id(_output, MatroskaIds.Segment);
        _segmentSizeOffset = _output.Position;
        EbmlWriter.Size(_output, Ebml.UnknownSize, 8);
        _segmentDataStart = _output.Position;

        _seekHeadOffset = _output.Position;
        EbmlWriter.Void(_output, SeekHeadReserve);

        _infoPosition = _output.Position;
        using (MemoryStream info = new())
        {
            EbmlWriter.Unsigned(info, MatroskaIds.TimestampScale, TimestampScale);
            EbmlWriter.Text(info, MatroskaIds.MuxingApp, "Egami.Video");
            EbmlWriter.Text(info, MatroskaIds.WritingApp, "Egami.Video");
            if (_metadata.Tags.TryGetValue("TITLE", out string? title) && title.Length > 0)
            {
                EbmlWriter.Text(info, MatroskaIds.Title, title);
            }
            long durationAt = info.Length;
            if (_seekable)
            {
                EbmlWriter.Float(info, MatroskaIds.Duration, 0);
            }

            EbmlWriter.Header(_output, MatroskaIds.Info, info.Length);
            _durationOffset = _output.Position + durationAt;
            info.Position = 0;
            info.CopyTo(_output);
        }

        _tracksPosition = _output.Position;
        EbmlWriter.Master(_output, MatroskaIds.Tracks, t =>
        {
            foreach (TrackWriter track in _tracks)
            {
                EbmlWriter.Master(t, MatroskaIds.TrackEntry, track.WriteEntry);
            }
        });
        WriteMetadata();
    }

    /// <summary>Writes Tags, Chapters and Attachments after Tracks. The Info title and the muxing app names are not repeated as tags.</summary>
    private void WriteMetadata()
    {
        List<(string Name, string Value)> global = [.. _metadata.Tags.Where(t => t.Key is not ("TITLE" or "MUXINGAPP" or "WRITINGAPP")).Select(t => (t.Key, t.Value))];
        if (global.Count > 0 || _tracks.Any(t => t.Input.Tags.Count > 0))
        {
            _tagsPosition = _output.Position;
            EbmlWriter.Master(_output, MatroskaIds.Tags, tags =>
            {
                if (global.Count > 0)
                {
                    EbmlWriter.Master(tags, MatroskaIds.Tag, tag =>
                    {
                        EbmlWriter.Master(tag, MatroskaIds.Targets, _ => { });
                        WriteSimpleTags(tag, global);
                    });
                }

                foreach (TrackWriter track in _tracks.Where(t => t.Input.Tags.Count > 0))
                {
                    EbmlWriter.Master(tags, MatroskaIds.Tag, tag =>
                    {
                        EbmlWriter.Master(tag, MatroskaIds.Targets, targets => EbmlWriter.Unsigned(targets, MatroskaIds.TagTrackUid, (ulong)track.Number));
                        WriteSimpleTags(tag, [.. track.Input.Tags.Select(t => (t.Key, t.Value))]);
                    });
                }
            });
        }

        if (_metadata.Chapters.Count > 0)
        {
            _chaptersPosition = _output.Position;
            EbmlWriter.Master(_output, MatroskaIds.Chapters, chapters => EbmlWriter.Master(chapters, MatroskaIds.EditionEntry, edition =>
            {
                ulong uid = 1;
                foreach (ChapterInfo chapter in _metadata.Chapters)
                {
                    EbmlWriter.Master(edition, MatroskaIds.ChapterAtom, atom =>
                    {
                        EbmlWriter.Unsigned(atom, MatroskaIds.ChapterUid, uid++);
                        EbmlWriter.Unsigned(atom, MatroskaIds.ChapterTimeStart, (ulong)Math.Max(0, chapter.Start.Rescale(Rational.Nanosecond).Ticks));
                        if (chapter.End is { } end)
                        {
                            EbmlWriter.Unsigned(atom, MatroskaIds.ChapterTimeEnd, (ulong)Math.Max(0, end.Rescale(Rational.Nanosecond).Ticks));
                        }

                        if (chapter.Title is { } title)
                        {
                            EbmlWriter.Master(atom, MatroskaIds.ChapterDisplay, display =>
                            {
                                EbmlWriter.Text(display, MatroskaIds.ChapString, title);
                                EbmlWriter.Text(display, MatroskaIds.ChapLanguage, chapter.Language is { Length: 3 } language ? language : "und");
                            });
                        }
                    });
                }
            }));
        }

        if (_metadata.Attachments.Count > 0)
        {
            _attachmentsPosition = _output.Position;
            EbmlWriter.Master(_output, MatroskaIds.Attachments, attachments =>
            {
                ulong uid = 1;
                foreach (AttachmentInfo file in _metadata.Attachments)
                {
                    EbmlWriter.Master(attachments, MatroskaIds.AttachedFile, attached =>
                    {
                        if (file.Description is { Length: > 0 } description)
                        {
                            EbmlWriter.Text(attached, MatroskaIds.FileDescription, description);
                        }

                        EbmlWriter.Text(attached, MatroskaIds.FileName, file.Name);
                        EbmlWriter.Text(attached, MatroskaIds.FileMediaType, file.MediaType);
                        EbmlWriter.Element(attached, MatroskaIds.FileData, file.Data.Span);
                        EbmlWriter.Unsigned(attached, MatroskaIds.FileUid, uid++);
                    });
                }
            });
        }
    }

    private static void WriteSimpleTags(Stream tag, List<(string Name, string Value)> values)
    {
        foreach ((string name, string value) in values)
        {
            EbmlWriter.Master(tag, MatroskaIds.SimpleTag, simple =>
            {
                EbmlWriter.Text(simple, MatroskaIds.TagName, name);
                EbmlWriter.Text(simple, MatroskaIds.TagString, value);
            });
        }
    }

    /// <summary>One output track: its number and TrackEntry.</summary>
    private static void WriteBlockHeader(Stream output, int trackNumber, long relative, byte flags)
    {
        output.WriteByte((byte)(0x80 | trackNumber));
        output.WriteByte((byte)(relative >> 8));
        output.WriteByte((byte)relative);
        output.WriteByte(flags);
    }

    private sealed class TrackWriter
    {
        public TrackWriter(int number, TrackInfo input)
        {
            Number = number;
            Input = input;
            Output = input with { Number = number, TimeBase = Milliseconds };
            DelayMilliseconds = input.CodecDelay is { } delay ? delay.Rescale(Milliseconds).Ticks : 0;
            DefaultMilliseconds = input is VideoTrackInfo { FrameRate: { Numerator: > 0 } rate } ? new MediaTime(1, rate.Reciprocal()).Rescale(Milliseconds).Ticks : 0;
        }

        public int Number { get; }

        /// <summary>Gets or sets the track as given; an AV1 track's CodecPrivate is filled from its first packet when it had none.</summary>
        public TrackInfo Input { get; set; }

        /// <summary>Gets or sets whether an AV1 track without av1C had its first packet looked at for a sequence header.</summary>
        public bool Av1ConfigLooked { get; set; }

        /// <summary>Gets or sets a Vorbis track's packet clock, made at its first packet.</summary>
        public Audio.Codecs.Vorbis.VorbisPacketClock? Vorbis { get; set; }

        public TrackInfo Output { get; }

        public long DelayMilliseconds { get; }

        public long DefaultMilliseconds { get; }

        /// <summary>Gets or sets the time of the track's last block, for a BlockGroup's ReferenceBlock.</summary>
        public long? LastTime { get; set; }

        /// <summary>Gets the VP9 hidden frames held for the next shown frame's superframe.</summary>
        public List<ReadOnlyMemory<byte>> HiddenVp9Frames { get; } = [];

        /// <summary>Gets or sets the first held hidden frame's time, which a flush at the end uses.</summary>
        public MediaTime HiddenVp9Time { get; set; }

        /// <summary>Gets or sets whether the first held hidden frame's packet was flagged a keyframe.</summary>
        public bool HiddenVp9Keyframe { get; set; }

        public void WriteEntry(Stream e)
        {
            TrackInfo t = Input;
            EbmlWriter.Unsigned(e, MatroskaIds.TrackNumber, (ulong)Number);
            EbmlWriter.Unsigned(e, MatroskaIds.TrackUid, (ulong)Number);
            EbmlWriter.Unsigned(e, MatroskaIds.TrackType, t.Kind switch
            {
                TrackKind.Video => 1,
                TrackKind.Audio => 2,
                TrackKind.Subtitle => 17,
                _ => 33,
            });
            EbmlWriter.Unsigned(e, MatroskaIds.FlagLacing, 0);
            if (t.CodecId == CodecIds.WebVtt)
            {
                // WebVTT cue settings and identifiers come in block additions with BlockAddID 1.
                EbmlWriter.Unsigned(e, MatroskaIds.MaxBlockAdditionId, 1);
            }

            if (!t.IsDefault)
            {
                EbmlWriter.Unsigned(e, MatroskaIds.FlagDefault, 0);
            }

            if (t.Name is { Length: > 0 } name)
            {
                EbmlWriter.Text(e, MatroskaIds.Name, name);
            }

            // Without a Language element Matroska means English, so an unknown language is written as "und".
            string language = t.Language is { Length: > 0 } given ? given : "und";
            EbmlWriter.Text(e, language.Length == 3 && language.All(char.IsAsciiLetterLower) ? MatroskaIds.Language : MatroskaIds.LanguageBcp47, language);

            if (t is VideoTrackInfo { CodecId: CodecIds.Uncompressed, FourCC: { } layout } raw && !NativeUncompressed.Contains(layout))
            {
                EbmlWriter.Text(e, MatroskaIds.CodecId, CodecIds.VfwFourCC);
                EbmlWriter.Element(e, MatroskaIds.CodecPrivate, Avi.AviCodecs.WriteBitmapInfo(raw, out _));
            }
            else
            {
                EbmlWriter.Text(e, MatroskaIds.CodecId, t.CodecId);
                if (!t.CodecPrivate.IsEmpty)
                {
                    EbmlWriter.Element(e, MatroskaIds.CodecPrivate, t.CodecPrivate.Span);
                }
            }

            if (t.CodecDelay is { Ticks: > 0 } delay)
            {
                EbmlWriter.Unsigned(e, MatroskaIds.CodecDelay, (ulong)delay.Rescale(Rational.Nanosecond).Ticks);
            }

            if (t is AudioTrackInfo { SeekPreRoll: { Ticks: > 0 } preRoll })
            {
                EbmlWriter.Unsigned(e, MatroskaIds.SeekPreRoll, (ulong)preRoll.Rescale(Rational.Nanosecond).Ticks);
            }
            else if (t.CodecId == CodecIds.Opus)
            {
                // RFC 7845 asks for 80 ms of pre-roll before a seek target in Opus.
                EbmlWriter.Unsigned(e, MatroskaIds.SeekPreRoll, 80_000_000);
            }

            switch (t)
            {
                case VideoTrackInfo video:
                    if (video.FrameRate is { Numerator: > 0 } rate)
                    {
                        EbmlWriter.Unsigned(e, MatroskaIds.DefaultDuration, (ulong)new MediaTime(1, rate.Reciprocal()).Rescale(Rational.Nanosecond).Ticks);
                    }

                    EbmlWriter.Master(e, MatroskaIds.Video, v => WriteVideo(v, video));
                    break;
                case AudioTrackInfo audio:
                    EbmlWriter.Master(e, MatroskaIds.Audio, a =>
                    {
                        EbmlWriter.Float(a, MatroskaIds.SamplingFrequency, audio.SampleRate);
                        EbmlWriter.Unsigned(a, MatroskaIds.Channels, (ulong)Math.Max(1, audio.Channels));
                        if (audio.BitsPerSample > 0)
                        {
                            EbmlWriter.Unsigned(a, MatroskaIds.BitDepth, (ulong)audio.BitsPerSample);
                        }
                    });
                    break;
            }
        }

        private static void WriteColour(Stream c, VideoTrackInfo video)
        {
            if (video.Color is { } color)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.MatrixCoefficients, color.MatrixCoefficients);
            }

            if (video.BitDepth > 0)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.BitsPerChannel, (ulong)video.BitDepth);
            }

            if (video.ChromaFormat is { } chroma && chroma != ChromaFormat.Yuv400)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.ChromaSubsamplingHorz, chroma == ChromaFormat.Yuv444 ? 0UL : 1UL);
                EbmlWriter.Unsigned(c, MatroskaIds.ChromaSubsamplingVert, chroma == ChromaFormat.Yuv420 ? 1UL : 0UL);
            }

            if (video.ChromaSiting is { } siting)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.ChromaSitingHorz, siting == ChromaSiting.Center ? 2UL : 1UL);
                EbmlWriter.Unsigned(c, MatroskaIds.ChromaSitingVert, siting == ChromaSiting.TopLeft ? 1UL : 2UL);
            }

            if (video.Color is { } codePoints)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.Range, codePoints.FullRange ? 2UL : 1UL);
                EbmlWriter.Unsigned(c, MatroskaIds.TransferCharacteristics, codePoints.TransferCharacteristics);
                EbmlWriter.Unsigned(c, MatroskaIds.Primaries, codePoints.Primaries);
            }

            if (video.Hdr is not { } hdr)
            {
                return;
            }

            if (hdr.MaxContentLightLevel is { } cll)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.MaxCll, (ulong)Math.Clamp(Math.Round(cll), 0, 65535));
            }

            if (hdr.MaxFrameAverageLightLevel is { } fall)
            {
                EbmlWriter.Unsigned(c, MatroskaIds.MaxFall, (ulong)Math.Clamp(Math.Round(fall), 0, 65535));
            }

            if (hdr.MasteringPrimaries is not null || hdr.MasteringMaxLuminance is not null || hdr.MasteringMinLuminance is not null)
            {
                EbmlWriter.Master(c, MatroskaIds.MasteringMetadata, m =>
                {
                    if (hdr.MasteringPrimaries is { } p)
                    {
                        uint id = MatroskaIds.PrimaryRChromaticityX;
                        foreach (Chromaticity xy in (Chromaticity[])[p.Red, p.Green, p.Blue, p.White])
                        {
                            EbmlWriter.Float(m, id++, xy.X);
                            EbmlWriter.Float(m, id++, xy.Y);
                        }
                    }

                    if (hdr.MasteringMaxLuminance is { } max)
                    {
                        EbmlWriter.Float(m, MatroskaIds.LuminanceMax, max);
                    }

                    if (hdr.MasteringMinLuminance is { } min)
                    {
                        EbmlWriter.Float(m, MatroskaIds.LuminanceMin, min);
                    }
                });
            }
        }

        private static void WriteVideo(Stream v, VideoTrackInfo video)
        {
            EbmlWriter.Unsigned(v, MatroskaIds.PixelWidth, (ulong)video.Width);
            if (video.FourCC is { Length: 4 } fourCC && video.CodecId == CodecIds.Uncompressed && NativeUncompressed.Contains(fourCC))
            {
                EbmlWriter.Element(v, MatroskaIds.ColourSpace, [(byte)fourCC[0], (byte)fourCC[1], (byte)fourCC[2], (byte)fourCC[3]]);
            }

            EbmlWriter.Unsigned(v, MatroskaIds.PixelHeight, (ulong)video.Height);
            if (video.PixelAspectRatio != new Rational(1, 1) && video.PixelAspectRatio.Numerator > 0)
            {
                long displayWidth = (long)Math.Round(video.Width * video.PixelAspectRatio.ToDouble());
                EbmlWriter.Unsigned(v, MatroskaIds.DisplayWidth, (ulong)Math.Max(1, displayWidth));
                EbmlWriter.Unsigned(v, MatroskaIds.DisplayHeight, (ulong)video.Height);
            }

            if (video.FieldOrder != FieldOrder.Progressive)
            {
                EbmlWriter.Unsigned(v, MatroskaIds.FlagInterlaced, 1);
                EbmlWriter.Unsigned(v, MatroskaIds.FieldOrder, video.FieldOrder == FieldOrder.TopFieldFirst ? 1UL : 6UL);
            }

            if (video.HasAlpha)
            {
                EbmlWriter.Unsigned(v, MatroskaIds.AlphaMode, 1);
            }

            if (video.Color is not null || video.BitDepth > 0 || video.ChromaFormat is not null || video.ChromaSiting is not null || video.Hdr is not null)
            {
                EbmlWriter.Master(v, MatroskaIds.Colour, c => WriteColour(c, video));
            }
        }
    }
}
