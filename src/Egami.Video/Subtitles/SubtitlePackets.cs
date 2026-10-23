using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>
/// Subtitle tracks in containers: cues to packets and back for SRT, WebVTT and ASS in Matroska and WebM, and mov_text and WebVTT
/// in MP4. Text formats go through the file writers and readers, so a track holds what the same file would.
/// </summary>
internal static class SubtitlePackets
{
    private const string AssEvents = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
    private const string SsaEvents = "Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    /// <summary>Gets whether a codec is a text subtitle codec these methods read and write.</summary>
    public static bool IsText(string codecId) => codecId is CodecIds.SubRip or CodecIds.WebVtt or CodecIds.Ass or CodecIds.Ssa
        or CodecIds.MovText or CodecIds.Mp4WebVtt || codecId.StartsWith("D_WEBVTT/", StringComparison.Ordinal);

    /// <summary>The subtitle format whose text a codec carries.</summary>
    public static SubtitleFormat FormatOf(string codecId) => codecId switch
    {
        CodecIds.SubRip or CodecIds.MovText => SubtitleFormat.Srt,
        CodecIds.Ass => SubtitleFormat.Ass,
        CodecIds.Ssa => SubtitleFormat.Ssa,
        _ => SubtitleFormat.WebVtt,
    };

    // ---- Reading ----

    public static SubtitleDocument Read(TrackInfo track, IEnumerable<EncodedPacket> packets, SubtitleReaderOptions? options, MediaContext? context)
    {
        if (!IsText(track.CodecId))
        {
            throw new NotSupportedException($"Track {track.Number} has codec '{track.CodecId}', which is not a text subtitle codec Egami.Video reads.");
        }

        List<(MediaTime Start, MediaTime? End, byte[] Data, PacketAddition[] Additions)> list = [];
        foreach (EncodedPacket packet in packets)
        {
            if (packet.TrackNumber == track.Number)
            {
                list.Add((packet.Timestamp, packet.Duration is { } d ? packet.Timestamp + d : null, packet.Data.ToArray(), [.. packet.Additions]));
            }
        }

        // A cue without a duration lasts until the next one starts.
        (MediaTime Start, MediaTime End, byte[] Data, PacketAddition[] Additions)[] cues = new (MediaTime, MediaTime, byte[], PacketAddition[])[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            MediaTime end = list[i].End ?? (i + 1 < list.Count ? list[i + 1].Start : list[i].Start);
            cues[i] = (list[i].Start, end, list[i].Data, list[i].Additions);
        }

        SubtitleFormat format = FormatOf(track.CodecId);
        using SubtitleLog log = new(context, $"{SubtitleDocument.Name(format)} track", options?.Strict ?? false);
        return track.CodecId switch
        {
            CodecIds.SubRip => ReadSubRip(cues),
            CodecIds.Ass or CodecIds.Ssa => ReadAss(track, cues, format, log),
            CodecIds.MovText => ReadMovText(cues, log),
            CodecIds.Mp4WebVtt => ReadMp4WebVtt(track, cues, log),
            _ => ReadWebVtt(track, cues, log),
        };
    }

    private static string Text(byte[] data) => Encoding.UTF8.GetString(data).TrimEnd('\0');

    private static SubtitleDocument ReadSubRip((MediaTime Start, MediaTime End, byte[] Data, PacketAddition[] Additions)[] packets)
    {
        SubtitleDocument document = new(SubtitleFormat.Srt);
        foreach ((MediaTime start, MediaTime end, byte[] data, _) in packets)
        {
            string text = Text(data).ReplaceLineEndings("\n").TrimEnd('\n');
            List<SubtitleSpan> spans = SrtFormat.ParseText(text, out SubtitleAlignment? alignment);
            document.Cues.Add(new SubtitleCue(start, end, spans) { Alignment = alignment, Id = (document.Cues.Count + 1).ToString(CultureInfo.InvariantCulture) });
        }

        return document;
    }

    private static SubtitleDocument ReadAss(TrackInfo track, (MediaTime Start, MediaTime End, byte[] Data, PacketAddition[] Additions)[] packets, SubtitleFormat format, SubtitleLog log)
    {
        // The header with an [Events] section in the block field order, then the events in read order with their times.
        string header = Encoding.UTF8.GetString(track.CodecPrivate.Span).TrimEnd('\0');
        string newLine = SubtitleText.NewLineOf(header) ?? "\r\n";
        int events = header.IndexOf("[Events]", StringComparison.OrdinalIgnoreCase);
        StringBuilder text = new(events >= 0 ? header[..events] : header.TrimEnd('\r', '\n') + newLine + newLine);
        text.Append("[Events]").Append(newLine).Append(format == SubtitleFormat.Ssa ? SsaEvents : AssEvents).Append(newLine);
        List<(long Order, MediaTime Start, MediaTime End, string Fields)> lines = [];
        foreach ((MediaTime start, MediaTime end, byte[] data, _) in packets)
        {
            string block = Text(data).ReplaceLineEndings("\\N");
            int comma = block.IndexOf(',', StringComparison.Ordinal);
            long order = comma > 0 && long.TryParse(block.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out long o) ? o : lines.Count;
            string rest = comma >= 0 ? block[(comma + 1)..] : block;
            int layerEnd = rest.IndexOf(',', StringComparison.Ordinal);
            lines.Add((order, start, end, layerEnd < 0 ? rest : rest[..layerEnd] + "," + AssFormat.FormatTime(start) + "," + AssFormat.FormatTime(end) + rest[layerEnd..]));
        }

        lines.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach ((_, _, _, string fields) in lines)
        {
            text.Append("Dialogue: ").Append(fields).Append(newLine);
        }

        SubtitleDocument document = SubtitleDocument.Parse(text.ToString(), format, false, log);
        if (document.Cues.Count == lines.Count)
        {
            // Matroska keeps milliseconds; ASS text keeps centiseconds.
            for (int i = 0; i < lines.Count; i++)
            {
                document.Cues[i].Start = lines[i].Start;
                document.Cues[i].End = lines[i].End;
            }
        }

        return document;
    }

    private static SubtitleDocument ReadWebVtt(TrackInfo track, (MediaTime Start, MediaTime End, byte[] Data, PacketAddition[] Additions)[] packets, SubtitleLog log)
    {
        bool webM = track.CodecId != CodecIds.WebVtt;
        string header = Encoding.UTF8.GetString(track.CodecPrivate.Span).TrimEnd('\0');
        List<(string? Id, string Settings, string Payload, MediaTime Start, MediaTime End)> cues = [];
        foreach ((MediaTime start, MediaTime end, byte[] data, PacketAddition[] additions) in packets)
        {
            string text = Text(data).ReplaceLineEndings("\n");
            string? id = null, settings = "";
            if (webM)
            {
                // WebM: the identifier line, the settings line, then the text.
                string[] parts = text.Split('\n', 3);
                (id, settings, text) = (parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
            }
            else if (additions.FirstOrDefault() is { } addition)
            {
                // Matroska: a block addition with the settings line, then the identifier line.
                string[] parts = Encoding.UTF8.GetString(addition.Data.Span).ReplaceLineEndings("\n").Split('\n', 3);
                (settings, id) = (parts[0], parts.Length > 1 ? parts[1] : null);
            }

            // Matroska's karaoke timestamps count from the cue's start.
            cues.Add((id, settings, webM ? text : ShiftTimestamps(text, start), start, end));
        }

        return WebVttFromCues(header, cues, log);
    }

    /// <summary>Moves the karaoke timestamps in WebVTT cue text (<c>&lt;00:01.500&gt;</c>) by a time, never before zero.</summary>
    private static string ShiftTimestamps(string payload, MediaTime by)
    {
        if (!payload.Contains('<', StringComparison.Ordinal) || by == MediaTime.Zero)
        {
            return payload;
        }

        StringBuilder output = new(payload.Length);
        int at = 0;
        while (at < payload.Length)
        {
            int open = payload.IndexOf('<', at);
            int close = open < 0 ? -1 : payload.IndexOf('>', open + 1);
            if (close < 0)
            {
                break;
            }

            output.Append(payload, at, open - at);
            string inner = payload[(open + 1)..close];
            if (inner.Length > 0 && char.IsAsciiDigit(inner[0]) && WebVttFormat.TryTimestamp(inner, out MediaTime time))
            {
                output.Append('<').Append(WebVttFormat.FormatTime(Max0(time + by))).Append('>');
            }
            else
            {
                output.Append(payload, open, close - open + 1);
            }

            at = close + 1;
        }

        return output.Append(payload, at, payload.Length - at).ToString();
    }

    /// <summary>Builds a WebVTT file from a header and cue parts and reads it, keeping the exact times.</summary>
    private static SubtitleDocument WebVttFromCues(string header, List<(string? Id, string Settings, string Payload, MediaTime Start, MediaTime End)> cues, SubtitleLog log)
    {
        StringBuilder text = new();
        string head = header.ReplaceLineEndings("\n").TrimEnd('\n');
        text.Append(head.StartsWith("WEBVTT", StringComparison.Ordinal) ? head : "WEBVTT").Append("\n\n");
        foreach ((string? id, string settings, string payload, MediaTime start, MediaTime end) in cues)
        {
            if (id is { Length: > 0 } && !id.Contains("-->", StringComparison.Ordinal))
            {
                text.Append(id).Append('\n');
            }

            text.Append(WebVttFormat.FormatTime(start)).Append(" --> ").Append(WebVttFormat.FormatTime(end));
            if (settings.Trim() is { Length: > 0 } s)
            {
                text.Append(' ').Append(s);
            }

            text.Append('\n');
            string body = string.Join('\n', payload.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0)).Replace("-->", "--&gt;");
            if (body.Length > 0)
            {
                text.Append(body).Append('\n');
            }

            text.Append('\n');
        }

        SubtitleDocument document = SubtitleDocument.Parse(text.ToString(), SubtitleFormat.WebVtt, false, log);
        if (document.Cues.Count == cues.Count)
        {
            for (int i = 0; i < cues.Count; i++)
            {
                document.Cues[i].Start = cues[i].Start;
                document.Cues[i].End = cues[i].End;
            }
        }

        return document;
    }

    private static SubtitleDocument ReadMovText((MediaTime Start, MediaTime End, byte[] Data, PacketAddition[] Additions)[] packets, SubtitleLog log)
    {
        SubtitleDocument document = new(SubtitleFormat.Srt);
        SubtitleCue? last = null;
        byte[]? lastData = null;
        int index = 0;
        foreach ((MediaTime start, MediaTime end, byte[] data, _) in packets)
        {
            index++;
            MovTextSample sample;
            try
            {
                sample = MovTextSample.Parse(data);
            }
            catch (CorruptDataException e)
            {
                log.Repaired(index, $"sample {index} is broken ({e.Message}) and was skipped.");
                continue;
            }

            if (sample.Text.Length == 0)
            {
                last = null;
                continue;
            }

            // A cue split over samples (by another starting during it) comes back whole.
            if (last is not null && lastData is not null && last.End == start && data.AsSpan().SequenceEqual(lastData))
            {
                last.End = end;
                continue;
            }

            last = new SubtitleCue(start, end, MovTextSpans(sample)) { Id = (document.Cues.Count + 1).ToString(CultureInfo.InvariantCulture) };
            lastData = data;
            document.Cues.Add(last);
        }

        return document;
    }

    private static List<SubtitleSpan> MovTextSpans(MovTextSample sample)
    {
        // Style runs count Unicode characters; split the text at their edges.
        int[] runeStarts = [.. RuneOffsets(sample.Text)];
        int Offset(int character) => character <= 0 ? 0 : character >= runeStarts.Length ? sample.Text.Length : runeStarts[character];
        List<SubtitleSpan> spans = [];
        int at = 0;
        foreach (MovTextStyle style in sample.Styles.OrderBy(s => s.Start))
        {
            int start = Math.Max(Offset(style.Start), at), end = Math.Max(Offset(style.End), start);
            if (start > at)
            {
                SubtitleWriting.Append(spans, new SubtitleSpan(sample.Text[at..start]));
            }

            if (end > start)
            {
                SubtitleWriting.Append(spans, new SubtitleSpan(sample.Text[start..end])
                {
                    Bold = style.Face.HasFlag(MovTextFace.Bold),
                    Italic = style.Face.HasFlag(MovTextFace.Italic),
                    Underline = style.Face.HasFlag(MovTextFace.Underline),
                    Color = style.Color == new Rgba8(255, 255, 255, 255) ? null : style.Color,
                    FontSize = style.FontSize is 0 or DefaultMovTextSize ? null : style.FontSize,
                });
            }

            at = end;
        }

        if (at < sample.Text.Length)
        {
            SubtitleWriting.Append(spans, new SubtitleSpan(sample.Text[at..]));
        }

        return spans;
    }

    private static IEnumerable<int> RuneOffsets(string text)
    {
        int at = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            yield return at;
            at += rune.Utf16SequenceLength;
        }

        yield return at;
    }

    private static SubtitleDocument ReadMp4WebVtt(TrackInfo track, (MediaTime Start, MediaTime End, byte[] Data, PacketAddition[] Additions)[] packets, SubtitleLog log)
    {
        string header = "WEBVTT";
        foreach ((string type, ReadOnlyMemory<byte> body) in Boxes(track.CodecPrivate.Length > 8 ? track.CodecPrivate[8..] : default))
        {
            if (type == "vttC")
            {
                header = Encoding.UTF8.GetString(body.Span);
            }
        }

        // Each sample lists the cues shown over its time; a cue in several samples in a row is one cue.
        List<(string? Id, string Settings, string Payload, MediaTime Start, MediaTime End)> cues = [];
        List<int> open = [];
        foreach ((MediaTime start, MediaTime end, byte[] data, _) in packets)
        {
            List<int> now = [];
            foreach ((string type, ReadOnlyMemory<byte> body) in Boxes(data))
            {
                if (type != "vttc")
                {
                    continue;
                }

                string? id = null;
                string settings = "", payload = "";
                foreach ((string part, ReadOnlyMemory<byte> value) in Boxes(body))
                {
                    string s = Encoding.UTF8.GetString(value.Span);
                    switch (part)
                    {
                        case "iden": id = s; break;
                        case "sttg": settings = s; break;
                        case "payl": payload = s; break;
                    }
                }

                int same = open.FindIndex(i => cues[i].End == start && cues[i].Id == id && cues[i].Settings == settings && cues[i].Payload == payload);
                if (same >= 0)
                {
                    int i = open[same];
                    cues[i] = cues[i] with { End = end };
                    now.Add(i);
                    open.RemoveAt(same);
                }
                else
                {
                    now.Add(cues.Count);
                    cues.Add((id, settings, payload, start, end));
                }
            }

            open = now;
        }

        return WebVttFromCues(header, cues, log);
    }

    /// <summary>Lists the boxes in MP4 data, stopping at one that does not fit.</summary>
    private static List<(string Type, ReadOnlyMemory<byte> Body)> Boxes(ReadOnlyMemory<byte> data)
    {
        List<(string, ReadOnlyMemory<byte>)> boxes = [];
        int at = 0;
        while (data.Length - at >= 8)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Span[at..]);
            if (size < 8 || size > data.Length - at)
            {
                break;
            }

            boxes.Add((Encoding.ASCII.GetString(data.Span.Slice(at + 4, 4)), data.Slice(at + 8, (int)size - 8)));
            at += (int)size;
        }

        return boxes;
    }

    // ---- Writing ----

    private const int DefaultMovTextSize = 18;

    public static (TrackInfo Track, List<EncodedPacket> Packets) Write(SubtitleDocument document, int trackNumber, string codecId, MediaContext? context)
    {
        if (!IsText(codecId) || codecId == CodecIds.Ssa)
        {
            throw new NotSupportedException($"Egami.Video writes subtitle tracks as {CodecIds.SubRip}, {CodecIds.WebVtt}, {CodecIds.WebMWebVtt}, {CodecIds.Ass}, {CodecIds.MovText} or {CodecIds.Mp4WebVtt}, not '{codecId}'.");
        }

        Rational timeBase = SubtitleText.Milliseconds;
        string name = codecId switch
        {
            CodecIds.MovText => "mov_text track",
            CodecIds.Mp4WebVtt => "MP4 WebVTT track",
            _ => SubtitleDocument.Name(FormatOf(codecId)) + " track",
        };
        using SubtitleLog log = new(context, name);
        List<EncodedPacket> packets;
        ReadOnlyMemory<byte> codecPrivate = default;
        switch (codecId)
        {
            case CodecIds.SubRip:
                packets = WriteSubRip(document, trackNumber, log);
                break;
            case CodecIds.Ass:
                (packets, codecPrivate) = WriteAss(document, trackNumber, log);
                break;
            case CodecIds.MovText:
                packets = WriteMovText(document, trackNumber, log);
                break;
            default:
                (packets, codecPrivate) = WriteWebVtt(document, trackNumber, codecId, log);
                break;
        }

        MediaTime end = packets.Count == 0 ? MediaTime.Zero : packets.Max(p => p.Timestamp + (p.Duration ?? MediaTime.Zero));
        TrackInfo track = new()
        {
            Number = trackNumber,
            Kind = TrackKind.Subtitle,
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            TimeBase = timeBase,
            Duration = end,
        };
        return (track, packets);
    }

    private static EncodedPacket Packet(int track, string text, MediaTime start, MediaTime end, IReadOnlyList<PacketAddition>? additions = null)
    {
        return Packet(track, Encoding.UTF8.GetBytes(text), start, end, additions);
    }

    private static EncodedPacket Packet(int track, byte[] data, MediaTime start, MediaTime end, IReadOnlyList<PacketAddition>? additions = null)
    {
        start = start.Rescale(SubtitleText.Milliseconds);
        end = end.Rescale(SubtitleText.Milliseconds);
        return new EncodedPacket(track, data, start)
        {
            Duration = end > start ? end - start : MediaTime.Zero,
            IsKeyframe = true,
            Additions = additions ?? [],
        };
    }

    /// <summary>The cues to write, in time order, without comments and empty or negative ones.</summary>
    private static List<(SubtitleCue Cue, int Index)> Playable(SubtitleDocument document, SubtitleLog log)
    {
        List<(SubtitleCue, int)> cues = [];
        for (int i = 0; i < document.Cues.Count; i++)
        {
            SubtitleCue cue = document.Cues[i];
            if (cue.IsComment)
            {
                log.Dropped("ASS comment lines", i + 1);
                continue;
            }

            SubtitleWriting.CheckTimes(cue, i, log);
            cues.Add((cue, i));
        }

        return [.. cues.OrderBy(c => c.Item1.Start)];
    }

    private static List<EncodedPacket> WriteSubRip(SubtitleDocument document, int track, SubtitleLog log)
    {
        List<EncodedPacket> packets = [];
        foreach ((SubtitleCue cue, int index) in Playable(document, log))
        {
            string text = SrtFormat.WriteText(document, cue, index, log).ReplaceLineEndings("\n");
            packets.Add(Packet(track, text, Max0(cue.Start), Max0(cue.End)));
        }

        return packets;
    }

    private static MediaTime Max0(MediaTime time) => time < MediaTime.Zero ? MediaTime.Zero : time;

    private static (List<EncodedPacket>, ReadOnlyMemory<byte>) WriteAss(SubtitleDocument document, int track, SubtitleLog log)
    {
        // Write the script, read it back, and cut it into the header and one block per event.
        string script = document.Write(SubtitleFormat.Ass, null, log);
        using SubtitleLog quiet = new(null, "ASS");
        SubtitleDocument written = SubtitleDocument.Parse(script, SubtitleFormat.Ass, false, quiet);
        string header = written.Source!.Header;
        string newLine = SubtitleText.NewLineOf(header) ?? "\r\n";
        int events = header.IndexOf("[Events]", StringComparison.OrdinalIgnoreCase);
        header = (events >= 0 ? header[..events] : header.TrimEnd('\r', '\n') + newLine + newLine) + "[Events]" + newLine + AssEvents + newLine;

        bool aligned = written.Cues.Count == document.Cues.Count;
        List<(MediaTime Start, MediaTime End, string Block)> blocks = [];
        for (int i = 0; i < written.Cues.Count; i++)
        {
            SubtitleCue cue = written.Cues[i];
            SubtitleCue source = aligned ? document.Cues[i] : cue;
            if (cue.IsComment)
            {
                log.Dropped("ASS comment lines", i + 1);
                continue;
            }

            string block = string.Join(',', blocks.Count.ToString(CultureInfo.InvariantCulture), cue.Layer.ToString(CultureInfo.InvariantCulture), cue.Style ?? "Default", cue.Actor ?? "",
                Margin(cue.MarginLeft), Margin(cue.MarginRight), Margin(cue.MarginVertical), cue.Effect ?? "", cue.RawText ?? "");
            blocks.Add((Max0(source.Start), Max0(source.End), block));
        }

        List<EncodedPacket> packets = [.. blocks.OrderBy(b => b.Start).Select(b => Packet(track, b.Block, b.Start, b.End))];
        return (packets, Encoding.UTF8.GetBytes(header));
    }

    private static string Margin(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes the cues as WebVTT, reads them back, and gives each cue's identifier, settings and text.</summary>
    private static (string Header, List<(string? Id, string Settings, string Payload, MediaTime Start, MediaTime End)> Cues) WebVttParts(SubtitleDocument document, SubtitleLog log)
    {
        SubtitleDocument sorted = document.Clone();
        sorted.Cues.Clear();
        List<(SubtitleCue Cue, int Index)> playable = Playable(document, log);
        sorted.Cues.AddRange(playable.Select(p => p.Cue));
        string text = sorted.Write(SubtitleFormat.WebVtt, new SubtitleWriterOptions { NewLine = "\n" }, log);
        using SubtitleLog quiet = new(null, "WebVTT");
        SubtitleDocument written = SubtitleDocument.Parse(text, SubtitleFormat.WebVtt, false, quiet);
        bool aligned = written.Cues.Count == playable.Count;
        List<(string?, string, string, MediaTime, MediaTime)> cues = [];
        for (int i = 0; i < written.Cues.Count; i++)
        {
            SubtitleCue cue = written.Cues[i];
            SubtitleCue source = aligned ? playable[i].Cue : cue;
            cues.Add((cue.Id, SettingsOf(cue.Source?.RawBlock ?? ""), cue.RawText ?? "", Max0(source.Start), Max0(source.End)));
        }

        return (written.Source!.Header.TrimEnd('\r', '\n'), cues);
    }

    /// <summary>Gets the settings after the end time on a cue's timing line.</summary>
    private static string SettingsOf(string block)
    {
        foreach (string line in block.ReplaceLineEndings("\n").Split('\n'))
        {
            int arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            string rest = line[(arrow + 3)..].TrimStart(' ', '\t');
            int space = rest.IndexOfAny([' ', '\t']);
            return space < 0 ? "" : rest[space..].Trim(' ', '\t');
        }

        return "";
    }

    private static (List<EncodedPacket>, ReadOnlyMemory<byte>) WriteWebVtt(SubtitleDocument document, int track, string codecId, SubtitleLog log)
    {
        (string header, List<(string? Id, string Settings, string Payload, MediaTime Start, MediaTime End)> cues) = WebVttParts(document, log);
        if (codecId == CodecIds.Mp4WebVtt)
        {
            return (WriteMp4WebVtt(cues, track), Mp4WebVttEntry(header));
        }

        List<EncodedPacket> packets = [];
        foreach ((string? id, string settings, string payload, MediaTime start, MediaTime end) in cues)
        {
            if (codecId == CodecIds.WebVtt)
            {
                PacketAddition[] additions = settings.Length > 0 || id is { Length: > 0 } ? [new PacketAddition(1, Encoding.UTF8.GetBytes(settings + "\n" + id + "\n"))] : [];
                packets.Add(Packet(track, ShiftTimestamps(payload, MediaTime.Zero - start), start, end, additions));
            }
            else
            {
                packets.Add(Packet(track, (id ?? "") + "\n" + settings + "\n" + payload, start, end));
            }
        }

        // WebM's WebVTT keeps the header in CodecPrivate only when it holds more than the signature line.
        bool keepHeader = codecId == CodecIds.WebVtt || header.Contains('\n', StringComparison.Ordinal);
        return (packets, keepHeader ? Encoding.UTF8.GetBytes(header) : default);
    }

    private static byte[] Mp4WebVttEntry(string header)
    {
        // The sample entry after its box header: 6 reserved bytes, data reference 1, then the vttC configuration box.
        byte[] config = Box("vttC", Encoding.UTF8.GetBytes(header));
        byte[] entry = new byte[8 + config.Length];
        entry[7] = 1;
        config.CopyTo(entry, 8);
        return entry;
    }

    private static byte[] Box(string type, ReadOnlySpan<byte> body)
    {
        byte[] box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        body.CopyTo(box.AsSpan(8));
        return box;
    }

    /// <summary>Cuts a timeline into spans where the same cues show; MP4 samples cannot overlap, so each holds every cue it shows.</summary>
    private static List<(long Start, long End, List<int> Cues)> Segments(List<(long Start, long End)> cues)
    {
        long[] edges = [.. cues.SelectMany(c => new[] { c.Start, c.End }).Append(0).Distinct().Order()];
        List<(long, long, List<int>)> segments = [];
        for (int e = 0; e + 1 < edges.Length; e++)
        {
            (long from, long to) = (edges[e], edges[e + 1]);
            List<int> showing = [.. Enumerable.Range(0, cues.Count).Where(i => cues[i].Start <= from && cues[i].End >= to)];
            if (segments.Count > 0 && segments[^1].Item3.Count == 0 && showing.Count == 0)
            {
                segments[^1] = (segments[^1].Item1, to, showing);
                continue;
            }

            segments.Add((from, to, showing));
        }

        return segments;
    }

    private static long Ms(MediaTime time) => Math.Max(0, time.Rescale(SubtitleText.Milliseconds).Ticks);

    private static List<EncodedPacket> WriteMp4WebVtt(List<(string? Id, string Settings, string Payload, MediaTime Start, MediaTime End)> cues, int track)
    {
        List<(long, long)> times = [.. cues.Select(c => (Ms(c.Start), Ms(c.End)))];
        List<EncodedPacket> packets = [];
        foreach ((long start, long end, List<int> showing) in Segments(times))
        {
            using MemoryStream sample = new();
            if (showing.Count == 0)
            {
                sample.Write(Box("vtte", []));
            }

            foreach (int i in showing)
            {
                using MemoryStream cue = new();
                if (cues[i].Id is { Length: > 0 } id)
                {
                    cue.Write(Box("iden", Encoding.UTF8.GetBytes(id)));
                }

                if (cues[i].Settings.Length > 0)
                {
                    cue.Write(Box("sttg", Encoding.UTF8.GetBytes(cues[i].Settings)));
                }

                cue.Write(Box("payl", Encoding.UTF8.GetBytes(cues[i].Payload)));
                sample.Write(Box("vttc", cue.ToArray()));
            }

            packets.Add(Packet(track, sample.ToArray(), new MediaTime(start, SubtitleText.Milliseconds), new MediaTime(end, SubtitleText.Milliseconds)));
        }

        return packets;
    }

    private static List<EncodedPacket> WriteMovText(SubtitleDocument document, int track, SubtitleLog log)
    {
        List<(SubtitleCue Cue, int Index)> playable = Playable(document, log);
        List<(long, long)> times = [.. playable.Select(c => (Ms(c.Cue.Start), Ms(c.Cue.End)))];
        List<EncodedPacket> packets = [];
        foreach ((long start, long end, List<int> showing) in Segments(times))
        {
            StringBuilder text = new();
            List<MovTextStyle> styles = [];
            foreach (int i in showing)
            {
                (SubtitleCue cue, int index) = playable[i];
                CheckMovText(document, cue, index, log);
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                foreach (SubtitleSpan span in cue.Spans.Where(s => !s.IsDrawing))
                {
                    int from = RuneCount(text);
                    text.Append(span.Text.ReplaceLineEndings("\n"));
                    MovTextFace face = (span.Bold ? MovTextFace.Bold : 0) | (span.Italic ? MovTextFace.Italic : 0) | (span.Underline ? MovTextFace.Underline : 0);
                    if (face != MovTextFace.None || span.Color is not null || span.FontSize is not null)
                    {
                        int size = span.FontSize is { } s ? (int)Math.Clamp(Math.Round(s), 1, 255) : DefaultMovTextSize;
                        styles.Add(new MovTextStyle(from, RuneCount(text), face, size, span.Color ?? new Rgba8(255, 255, 255, 255)));
                    }
                }
            }

            byte[] data = new MovTextSample(text.ToString()) { Styles = styles }.ToBytes();
            packets.Add(Packet(track, data, new MediaTime(start, SubtitleText.Milliseconds), new MediaTime(end, SubtitleText.Milliseconds)));
        }

        return packets;
    }

    private static int RuneCount(StringBuilder text)
    {
        int count = 0;
        foreach (ReadOnlyMemory<char> chunk in text.GetChunks())
        {
            foreach (Rune _ in chunk.Span.EnumerateRunes())
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Reports what mov_text cannot hold: SRT's losses, plus placement and strikeout.</summary>
    private static void CheckMovText(SubtitleDocument document, SubtitleCue cue, int index, SubtitleLog log)
    {
        SubtitleWriting.CheckCue(document, cue, index, log, SubtitleFormat.Srt);
        if (SubtitleWriting.EffectiveAlignment(cue) is { } a && a != SubtitleAlignment.BottomCenter)
        {
            log.Dropped("cue alignments", index + 1);
        }

        if (cue.Spans.Any(s => s.Strikeout))
        {
            log.Dropped("strikeout", index + 1);
        }

        if (cue.Spans.Any(s => s.Font is not null))
        {
            log.Dropped("font names", index + 1);
        }
    }
}
