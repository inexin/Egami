using System.CommandLine;
using System.Globalization;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video timestamps</c> (Build Plan task 7, Video CLI design Q5): changes packet timestamps without decoding. Reads
/// the input twice: once for the timestamps, which are worked out for the whole file, and once to copy the packets with their
/// new times (standard input is held in memory for the second read).
/// </summary>
internal static class TimestampsCommand
{
    /// <summary>One packet's times from the first read, in nanoseconds.</summary>
    private readonly record struct Entry(int Track, long Pts, long? Dts, long? Duration, bool Discardable);

    public static Command Create(VideoSession session)
    {
        MediaOptions options = new();
        Option<string?> shift = new("--shift") { Description = "Move timestamps later, or earlier with a minus sign: seconds or [HH:]MM:SS[.mmm], such as 1.5, -1.5 or --shift=-0:01.", HelpName = "time" };
        Option<string?> rate = new("--rate") { Description = "Re-time video packets to a constant frame rate in presentation order, such as 25 or 30000/1001; audio stays as it is.", HelpName = "fps" };
        Option<bool> fix = new("--fix") { Description = "Repair duplicated or out-of-order timestamps and missing durations: decode order, strictly increasing times per track, durations from the frame rate or the next packet." };
        Option<bool> zero = new("--start-at-zero") { Description = "Move every track by the same amount so the earliest packet starts at 0." };
        Command command = new("timestamps", "Change packet timestamps without decoding: shift tracks, re-time video to a constant frame rate, repair broken timestamps or start at 0. With --track, --shift, --rate and --fix change only those tracks; every track is written.");
        options.AddTo(command);
        command.Options.Add(shift);
        command.Options.Add(rate);
        command.Options.Add(fix);
        command.Options.Add(zero);
        command.SetAction(result =>
        {
            string? shiftText = result.GetValue(shift);
            string? rateText = result.GetValue(rate);
            if (shiftText is null && rateText is null && !result.GetValue(fix) && !result.GetValue(zero))
            {
                throw new UsageException("timestamps needs --shift, --rate, --fix or --start-at-zero, such as --shift 0.5 --track a.");
            }

            Settings settings = new(
                shiftText is null ? null : ParseShift(shiftText),
                rateText is null ? null : ParseRate(rateText),
                result.GetValue(fix),
                result.GetValue(zero));
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs, "out/{name}.mkv");
            TrackSelection kept = TrackSelection.Parse([], result.GetValue(options.NoAudio), result.GetValue(options.NoSubtitles));
            TrackSelection changed = TrackSelection.Parse(result.GetValue(options.Track), false, false);
            string? format = result.GetValue(options.Format);
            return Batch.Run(session, inputs, batch, input =>
            {
                MediaTarget target = MediaIO.Resolve(session, input, output, format, CutCommands.OutputExtension(session, input));
                Run(session, input, target, kept, changed, settings, inputs.Count);
                return [target.Path];
            });
        });
        return command;
    }

    /// <summary>What to change.</summary>
    private sealed record Settings(long? Shift, Rational? Rate, bool Fix, bool StartAtZero);

    /// <summary>Parses --shift: a time with an optional minus sign, in nanoseconds.</summary>
    internal static long ParseShift(string text)
    {
        string value = text.Trim();
        bool negative = value.StartsWith('-');
        TimeValue time = TimeValue.Parse(negative || value.StartsWith('+') ? value[1..] : value, "--shift");
        if (time.IsFrame)
        {
            throw new UsageException($"--shift '{text}' must be a time, such as 1.5, -1.5 or -0:01, not a frame number.");
        }

        long nanoseconds = CutTimes.Nanoseconds(MediaTime.FromSeconds(time.Seconds));
        return negative ? -nanoseconds : nanoseconds;
    }

    /// <summary>Parses --rate: a whole number, a fraction or a decimal number of frames per second.</summary>
    internal static Rational ParseRate(string text)
    {
        string value = text.Trim();
        Rational rate;
        if (value.Contains('/', StringComparison.Ordinal) || !value.Contains('.', StringComparison.Ordinal))
        {
            try
            {
                rate = Rational.Parse(value);
            }
            catch (FormatException)
            {
                throw WrongRate(text);
            }
            catch (OverflowException)
            {
                throw WrongRate(text);
            }
        }
        else
        {
            rate = double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double fps) && fps is > 0 and < 1e6
                ? new Rational((long)Math.Round(fps * 1000), 1000)
                : throw WrongRate(text);
        }

        return rate.Numerator > 0 && rate.Denominator > 0 && rate.ToDouble() < 1e6 ? rate : throw WrongRate(text);
    }

    private static UsageException WrongRate(string text) =>
        new($"--rate '{text}' is not a frame rate: give frames per second as a number or a fraction, such as 25, 29.97 or 30000/1001.");

    /// <summary>Whether a video codec may show frames in another order than it stores them (B-frames).</summary>
    private static bool Reorders(TrackInfo track) => track.Kind == TrackKind.Video
        && track.CodecId is not (CodecIds.MotionJpeg or CodecIds.Uncompressed or CodecIds.Vp8 or CodecIds.Vp9 or CodecIds.Av1);

    /// <summary>A video track's frame time from its frame rate, in nanoseconds.</summary>
    private static long? Period(TrackInfo track) =>
        track is VideoTrackInfo { FrameRate: { Numerator: > 0 } rate } ? (long)Math.Round(1e9 * rate.Denominator / rate.Numerator) : null;

    /// <summary>Frame <paramref name="index"/>'s time at a constant rate, in nanoseconds from the first frame.</summary>
    private static long FrameTime(long index, Rational rate)
    {
        Int128 scaled = (Int128)index * 1_000_000_000 * rate.Denominator * 2;
        Int128 twice = scaled / rate.Numerator;
        return (long)((twice + (twice >= 0 ? 1 : -1)) / 2);
    }

    private static void Run(VideoSession session, InputFile input, MediaTarget target, TrackSelection keptSelection, TrackSelection changedSelection, Settings settings, int count)
    {
        // Standard input is read twice, so it is kept in memory.
        byte[]? spooled = null;
        if (input.IsStdin)
        {
            using Stream stdin = MediaIO.OpenStream(session, input);
            using MemoryStream copy = new();
            stdin.CopyTo(copy);
            spooled = copy.ToArray();
        }

        IDemuxer Open() => spooled is null
            ? MediaIO.OpenDemuxer(session, input)
            : MediaFile.OpenDemuxer(new MemoryStream(spooled, writable: false), session.Context, leaveOpen: false);

        TrackInfo[] tracks;
        HashSet<int> changed;
        List<Entry> entries = [];
        using (IDemuxer demuxer = Open())
        {
            tracks = [.. keptSelection.Select(demuxer.Tracks, input.Display)];
            if (tracks.Length == 0)
            {
                throw new UsageException($"{input.Display}: no tracks left to write.");
            }

            changed = [.. changedSelection.Select(tracks, input.Display).Select(t => t.Number)];
            HashSet<int> kept = [.. tracks.Select(t => t.Number)];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    if (kept.Contains(packet.TrackNumber))
                    {
                        entries.Add(new Entry(
                            packet.TrackNumber,
                            CutTimes.Nanoseconds(packet.Timestamp),
                            packet.DecodeTimestamp is { } dts ? CutTimes.Nanoseconds(dts) : null,
                            packet.Duration is { } duration ? CutTimes.Nanoseconds(duration) : null,
                            packet.IsDiscardable));
                    }
                }
            }
        }

        Retimer plan = new(session, input.Display, tracks, changed, entries, settings);
        plan.Compute();

        IProgress<MediaProgress>? progress = session.ProgressSink?.For(input, input.Index, count);
        MediaIO.Write(session, target.Path, output =>
        {
            using IDemuxer demuxer = Open();
            CountingStream counted = new(output);
            MediaMetadata metadata = demuxer.Info.Metadata;
            if (plan.ChapterShift != 0)
            {
                MediaTime by = CutTimes.FromNanoseconds(-plan.ChapterShift);
                metadata = metadata with { Chapters = [.. metadata.Chapters.Select(c => c with { Start = c.Start - by, End = c.End is { } e ? e - by : null })] };
            }

            using IMuxer muxer = MediaIO.CreateMuxer(session, counted, target.Container, plan.OutputTracks, metadata);
            JobProgress job = new(progress, demuxer.Info.Duration, tracks, session.Cancellation, counted);
            HashSet<int> kept = [.. tracks.Select(t => t.Number)];
            Dictionary<int, EncodedPacket> waiting = [];
            int index = 0;
            int next = 0;
            try
            {
                while (demuxer.ReadPacket() is { } packet)
                {
                    if (!kept.Contains(packet.TrackNumber))
                    {
                        packet.Dispose();
                        continue;
                    }

                    if (index >= entries.Count)
                    {
                        packet.Dispose();
                        throw new InvalidDataException("the file gave more packets on the second read than on the first.");
                    }

                    plan.Apply(index, packet);
                    if (plan.Order is null)
                    {
                        Write(packet);
                    }
                    else
                    {
                        waiting[index] = packet;
                        while (next < plan.Order.Length && waiting.Remove(plan.Order[next], out EncodedPacket? ready))
                        {
                            next++;
                            Write(ready);
                        }
                    }

                    index++;
                }

                if (index != entries.Count || waiting.Count > 0)
                {
                    throw new InvalidDataException("the file gave fewer packets on the second read than on the first.");
                }
            }
            finally
            {
                foreach (EncodedPacket packet in waiting.Values)
                {
                    packet.Dispose();
                }
            }

            muxer.Finish();
            job.Finish();

            void Write(EncodedPacket packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                    job.Packet(packet.TrackNumber, packet.Timestamp);
                }
            }
        });
    }

    /// <summary>Works out every packet's new timestamps, in nanoseconds, from the first read.</summary>
    private sealed class Retimer(VideoSession session, string display, TrackInfo[] tracks, HashSet<int> changed, List<Entry> entries, Settings settings)
    {
        private readonly long[] _pts = [.. entries.Select(e => e.Pts)];
        private readonly long?[] _dts = [.. entries.Select(e => e.Dts)];
        private readonly long?[] _durations = [.. entries.Select(e => e.Duration)];
        private readonly Dictionary<int, List<int>> _order = tracks.ToDictionary(t => t.Number, _ => new List<int>());
        private readonly Dictionary<int, List<string>> _notes = tracks.ToDictionary(t => t.Number, _ => new List<string>());

        /// <summary>Gets the order to write packets in (indexes of the first read), or <see langword="null"/> for the file's order.</summary>
        public int[]? Order { get; private set; }

        /// <summary>Gets the tracks to give the muxer: re-timed video tracks take the new frame rate.</summary>
        public TrackInfo[] OutputTracks { get; private set; } = [];

        /// <summary>Gets how far chapters move, in nanoseconds: what every track moved by.</summary>
        public long ChapterShift { get; private set; }

        public void Compute()
        {
            OutputTracks = tracks;
            for (int i = 0; i < entries.Count; i++)
            {
                _order[entries[i].Track].Add(i);
            }

            if (settings.Fix)
            {
                foreach (TrackInfo track in tracks.Where(t => changed.Contains(t.Number)))
                {
                    Fix(track);
                }

                Order = Interleave();
            }

            if (settings.Rate is { } rate)
            {
                TrackInfo[] video = [.. tracks.Where(t => t.Kind == TrackKind.Video && changed.Contains(t.Number))];
                if (video.Length == 0)
                {
                    session.Warn($"{display}: --rate re-times video, and no video track is picked; nothing was re-timed.");
                }

                foreach (TrackInfo track in video)
                {
                    Retime(track, rate);
                }

                OutputTracks = [.. tracks.Select(t => t is VideoTrackInfo v && changed.Contains(t.Number) ? v with { FrameRate = rate } : t)];
            }

            if (settings.Shift is { } shift && shift != 0)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (changed.Contains(entries[i].Track))
                    {
                        Move(i, shift);
                    }
                }

                foreach (int number in changed)
                {
                    _notes[number].Add($"shifted by {TimeValue.Format(shift / 1e9)}");
                }

                if (changed.Count == tracks.Length)
                {
                    ChapterShift += shift;
                }

                if (shift < 0 && !settings.StartAtZero)
                {
                    foreach (int number in changed)
                    {
                        long first = Enumerable.Range(0, entries.Count).Where(i => entries[i].Track == number && !entries[i].Discardable).Select(i => _pts[i]).DefaultIfEmpty(0).Min();
                        if (first < 0)
                        {
                            session.Warn($"{display}: track {number} now starts at {TimeValue.Format(first / 1e9)}, before 0, which some containers and players drop; add --start-at-zero to move every track later.");
                        }
                    }
                }
            }

            if (settings.StartAtZero && entries.Count > 0)
            {
                bool any = entries.Any(e => !e.Discardable);
                long first = Enumerable.Range(0, entries.Count).Where(i => !any || !entries[i].Discardable).Min(i => _pts[i]);
                if (first != 0)
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        Move(i, -first);
                    }

                    ChapterShift -= first;
                    session.Info($"{display}: every track moved by {TimeValue.Format(-first / 1e9)} to start at 0");
                }
                else
                {
                    session.Info($"{display}: already starts at 0");
                }
            }

            foreach (TrackInfo track in tracks.Where(t => _notes[t.Number].Count > 0))
            {
                session.Info($"{display}: track {track.Number} ({Kinds.Name(track.Kind)}): {string.Join(", ", _notes[track.Number])}");
            }
        }

        /// <summary>Gives a packet of the second read its new times.</summary>
        public void Apply(int index, EncodedPacket packet)
        {
            Entry entry = entries[index];
            if (_pts[index] != entry.Pts)
            {
                packet.Timestamp = CutTimes.FromNanoseconds(_pts[index]);
            }

            if (_dts[index] != entry.Dts)
            {
                packet.DecodeTimestamp = _dts[index] is { } dts ? CutTimes.FromNanoseconds(dts) : null;
            }

            if (_durations[index] != entry.Duration)
            {
                packet.Duration = _durations[index] is { } duration ? CutTimes.FromNanoseconds(duration) : null;
            }
        }

        private void Move(int index, long by)
        {
            _pts[index] += by;
            if (_dts[index] is { } dts)
            {
                _dts[index] = dts + by;
            }
        }

        /// <summary>
        /// Puts a track in decode order and makes its times strictly increasing: decode timestamps when every packet has one,
        /// otherwise timestamps, except for codecs with B-frames, whose timestamps only have to differ. Then fills in missing
        /// durations from the frame rate or the next packet in presentation order.
        /// </summary>
        private void Fix(TrackInfo track)
        {
            List<int> list = _order[track.Number];
            bool decodeTimes = list.Count > 0 && list.All(i => _dts[i] is not null);
            if (decodeTimes)
            {
                List<int> sorted = [.. list.OrderBy(i => _dts[i]!.Value)];
                if (!sorted.SequenceEqual(list))
                {
                    _notes[track.Number].Add("put in decode order");
                }

                list = _order[track.Number] = sorted;
            }

            long? period = Period(track);
            bool reorders = Reorders(track);
            long? previous = null;
            long? lastDuration = null;
            HashSet<long> seen = [];
            int repaired = 0;
            foreach (int i in list)
            {
                long step = period ?? _durations[i] ?? lastDuration ?? 1_000_000;
                lastDuration = _durations[i] ?? lastDuration;
                bool moved = false;
                if (decodeTimes)
                {
                    long dts = _dts[i]!.Value;
                    if (previous is { } p && dts <= p)
                    {
                        dts = p + step;
                        _dts[i] = dts;
                        moved = true;
                    }

                    if (_pts[i] < dts)
                    {
                        _pts[i] = dts;
                        moved = true;
                    }

                    previous = dts;
                }
                else if (!reorders)
                {
                    if (previous is { } p && _pts[i] <= p)
                    {
                        _pts[i] = p + step;
                        moved = true;
                    }

                    previous = _pts[i];
                }
                else
                {
                    while (!seen.Add(_pts[i]))
                    {
                        _pts[i] += step;
                        moved = true;
                    }
                }

                repaired += moved ? 1 : 0;
            }

            int filled = 0;
            int[] shown = [.. list.OrderBy(i => _pts[i])];
            long? before = null;
            for (int k = 0; k < shown.Length; k++)
            {
                int i = shown[k];
                if (_durations[i] is null)
                {
                    long? gap = k + 1 < shown.Length && _pts[shown[k + 1]] > _pts[i] ? _pts[shown[k + 1]] - _pts[i] : null;
                    _durations[i] = period ?? gap ?? before;
                    filled += _durations[i] is null ? 0 : 1;
                }

                before = _durations[i] ?? before;
            }

            if (repaired > 0)
            {
                _notes[track.Number].Add($"{repaired} timestamp{(repaired == 1 ? string.Empty : "s")} made increasing");
            }

            if (filled > 0)
            {
                _notes[track.Number].Add($"{filled} duration{(filled == 1 ? string.Empty : "s")} filled in");
            }
        }

        /// <summary>
        /// The order to write packets in: each track in its (decode) order, the tracks merged by decode time. A track's merge key
        /// is the least decode time of it and the packets after it, so a track whose timestamps go back (B-frames without decode
        /// timestamps) keeps its order.
        /// </summary>
        private int[] Interleave()
        {
            Dictionary<int, long[]> keys = [];
            foreach ((int number, List<int> list) in _order)
            {
                long[] key = new long[list.Count];
                long least = long.MaxValue;
                for (int k = list.Count - 1; k >= 0; k--)
                {
                    least = Math.Min(least, _dts[list[k]] ?? _pts[list[k]]);
                    key[k] = least;
                }

                keys[number] = key;
            }

            int[] order = new int[entries.Count];
            Dictionary<int, int> heads = _order.Keys.ToDictionary(n => n, _ => 0);
            PriorityQueue<int, (long Key, int Index)> queue = new();
            foreach ((int number, List<int> list) in _order)
            {
                if (list.Count > 0)
                {
                    queue.Enqueue(number, (keys[number][0], list[0]));
                }
            }

            int n = 0;
            while (queue.TryDequeue(out int number, out _))
            {
                List<int> list = _order[number];
                int head = heads[number];
                order[n++] = list[head];
                heads[number] = ++head;
                if (head < list.Count)
                {
                    queue.Enqueue(number, (keys[number][head], list[head]));
                }
            }

            return order;
        }

        /// <summary>Gives a video track's packets a constant frame rate: the n-th frame shown starts n frame times after the first.</summary>
        private void Retime(TrackInfo track, Rational rate)
        {
            List<int> list = _order[track.Number];
            if (list.Count == 0)
            {
                return;
            }

            int[] shown = [.. list.OrderBy(i => _pts[i])];
            Dictionary<int, int> rank = [];
            for (int r = 0; r < shown.Length; r++)
            {
                rank[shown[r]] = r;
            }

            long start = _pts[shown[0]];
            for (int r = 0; r < shown.Length; r++)
            {
                _pts[shown[r]] = start + FrameTime(r, rate);
                _durations[shown[r]] = FrameTime(r + 1, rate) - FrameTime(r, rate);
            }

            if (list.Any(i => _dts[i] is not null))
            {
                // Decode times count frames in decode order, held back by the deepest reordering so none decodes after it shows.
                int delay = 0;
                for (int k = 0; k < list.Count; k++)
                {
                    delay = Math.Max(delay, k - rank[list[k]]);
                }

                for (int k = 0; k < list.Count; k++)
                {
                    _dts[list[k]] = start + FrameTime(k - delay, rate);
                }
            }

            _notes[track.Number].Add($"re-timed to {rate} frames per second");
        }
    }
}
