using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;

using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video hash</c> and <c>egami-video compare</c> (Video CLI design Q3, Build Plan task 16): checksums of decoded frames
/// or packets in the layout of <c>ffmpeg -f framemd5</c>, and PSNR and SSIM between clips as <c>ffmpeg -lavfi psnr</c> and
/// <c>ssim</c> measure them.
/// </summary>
internal static class CheckCommands
{
    private static readonly string[] HashAlgorithms = ["md5", "sha256"];

    private static readonly string[] HashSampleFormats = ["u8", "s16", "s32", "f32", "f64"];

    public static Command Hash(VideoSession session)
    {
        MediaOptions options = new();
        Option<bool> packets = new("--packets") { Description = "Hash every packet as stored instead of every decoded frame, as ffmpeg -c copy -f framemd5 does." };
        Option<string> algorithm = new("--algorithm") { Description = "The hash: md5 (the default, as framemd5) or sha256.", HelpName = "md5|sha256", DefaultValueFactory = _ => "md5" };
        Option<bool> total = new("--total") { Description = "End with a line per track that hashes all its frames or packets together." };
        Option<string?> sampleFormat = new("--sample-format") { Description = "Convert decoded audio to u8, s16, s32, f32 or f64 before hashing; s16 matches ffmpeg's framemd5 default.", HelpName = "format" };
        Option<string?> format = new("--format", "-f") { Description = "How to write: text (the default, framemd5's lines), json or csv.", HelpName = "text|json|csv" };
        Option<bool> json = new("--json") { Description = "Write JSON; the same as --format json." };
        Option<bool> csv = new("--csv") { Description = "Write CSV; the same as --format csv." };
        Option<string?> output = new("--output", "-o") { Description = "Write the hashes to this file instead of standard output.", HelpName = "file" };
        Command command = new("hash", "Hash every decoded frame or packet of each track, in the layout of ffmpeg -f framemd5, to check that decoding matches.");
        options.AddTo(command, write: false);
        command.Options.Add(packets);
        command.Options.Add(algorithm);
        command.Options.Add(total);
        command.Options.Add(sampleFormat);
        command.Options.Add(format);
        command.Options.Add(json);
        command.Options.Add(csv);
        command.Options.Add(output);
        command.SetAction(result =>
        {
            string style = Style(result.GetValue(json), result.GetValue(csv), result.GetValue(format));
            string hash = result.GetValue(algorithm)!.ToLowerInvariant();
            if (!HashAlgorithms.Contains(hash))
            {
                throw new UsageException($"--algorithm '{hash}' is not md5 or sha256.");
            }

            SampleFormat? convert = null;
            if (result.GetValue(sampleFormat) is { } name)
            {
                if (!HashSampleFormats.Contains(name.ToLowerInvariant()))
                {
                    throw new UsageException($"--sample-format '{name}' is not u8, s16, s32, f32 or f64.");
                }

                if (result.GetValue(packets))
                {
                    throw new UsageException("--sample-format converts decoded audio; leave it out with --packets, which hashes packets as stored.");
                }

                convert = AudioCommand.ParseSampleFormat(name).Format;
            }

            HashRequest request = new(result.GetValue(packets), hash, result.GetValue(total), convert, options.Tracks(result));
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            HashData?[] found = new HashData?[inputs.Count];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                found[input.Index] = GatherHashes(session, input, request, inputs.Count);
                return [];
            });

            if (result.GetValue(output) is { } file && file != "-")
            {
                StringWriter text = new() { NewLine = "\n" };
                WriteHashes(text, style, [.. found.OfType<HashData>()], request, inputs.Count > 1);
                MediaIO.Write(session, session.ResolvePath(file), stream => stream.Write(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
            }
            else
            {
                WriteHashes(session.Console.Text, style, [.. found.OfType<HashData>()], request, inputs.Count > 1);
            }

            return code;
        });
        return command;
    }

    public static Command Compare(VideoSession session)
    {
        MediaOptions options = new();
        options.Inputs.Description = "The clips to measure against the reference: files, globs (*, ?, **), folders with --recursive, or - for standard input.";
        Argument<string> reference = new("reference") { Description = "The original clip, or - for standard input." };
        Option<string> metric = new("--metric", "-m") { Description = "What to measure: psnr, ssim, or both separated by a comma (the default).", HelpName = "psnr,ssim", DefaultValueFactory = _ => "psnr,ssim" };
        Option<bool> perFrame = new("--per-frame") { Description = "Also list the scores of every frame." };
        Option<bool> scale = new("--scale") { Description = "Resize frames to the reference's size (bicubic) when the sizes differ." };
        Option<double?> minPsnr = new("--min-psnr") { Description = "Exit with 1 when a clip's average PSNR is below this many decibels.", HelpName = "dB" };
        Option<double?> minSsim = new("--min-ssim") { Description = "Exit with 1 when a clip's average SSIM is below this, from 0 to 1.", HelpName = "value" };
        Option<string?> format = new("--format", "-f") { Description = "How to write: text (the default), json or csv.", HelpName = "text|json|csv" };
        Option<bool> json = new("--json") { Description = "Write JSON; the same as --format json." };
        Option<bool> csv = new("--csv") { Description = "Write CSV; the same as --format csv." };
        Command command = new("compare", "Measure PSNR and SSIM per plane between a reference clip and others, frame by frame in presentation order, as ffmpeg's psnr and ssim filters do.");
        command.Arguments.Add(reference);
        options.AddTo(command, write: false);
        command.Options.Add(metric);
        command.Options.Add(perFrame);
        command.Options.Add(scale);
        command.Options.Add(minPsnr);
        command.Options.Add(minSsim);
        command.Options.Add(format);
        command.Options.Add(json);
        command.Options.Add(csv);
        command.SetAction(result =>
        {
            string style = Style(result.GetValue(json), result.GetValue(csv), result.GetValue(format));
            HashSet<string> metrics = [.. result.GetValue(metric)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => m.ToLowerInvariant())];
            if (metrics.Count == 0 || metrics.Any(m => m is not ("psnr" or "ssim")))
            {
                throw new UsageException($"--metric '{result.GetValue(metric)}' is not psnr, ssim or psnr,ssim.");
            }

            double? psnrLimit = result.GetValue(minPsnr);
            double? ssimLimit = result.GetValue(minSsim);
            if (ssimLimit is < 0 or > 1)
            {
                throw new UsageException("--min-ssim must be between 0 and 1.");
            }

            if ((psnrLimit is not null && !metrics.Contains("psnr")) || (ssimLimit is not null && !metrics.Contains("ssim")))
            {
                throw new UsageException("--min-psnr needs psnr and --min-ssim needs ssim in --metric.");
            }

            List<InputFile> references = Globber.Expand(session, [result.GetValue(reference)!], recursive: false);
            if (references.Count != 1)
            {
                throw new UsageException($"The reference '{result.GetValue(reference)}' names {references.Count} files; give one.");
            }

            List<InputFile> inputs = options.ExpandInputs(session, result);
            if (references[0].IsStdin && inputs.Any(i => i.IsStdin))
            {
                throw new UsageException("Standard input can be the reference or a clip, not both.");
            }

            CompareRequest request = new(references[0], metrics.Contains("psnr"), metrics.Contains("ssim"), result.GetValue(perFrame), result.GetValue(scale), options.Tracks(result));
            byte[]? referenceBytes = references[0].IsStdin ? ReadAll(session) : null;
            BatchOptions batch = options.Batch(session, result);
            CompareData?[] found = new CompareData?[inputs.Count];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                found[input.Index] = Measure(session, input, request, referenceBytes, inputs.Count);
                return [];
            });

            List<CompareData> done = [.. found.OfType<CompareData>()];
            WriteComparison(session.Console.Text, style, done, request);
            bool below = false;
            foreach (CompareData data in done)
            {
                if (psnrLimit is { } p && data.Psnr is { } psnr && psnr.Average < p)
                {
                    session.Error($"{data.File.Display}: PSNR {Number(psnr.Average, 2)} dB is below --min-psnr {Number(p, 2)}.");
                    below = true;
                }

                if (ssimLimit is { } s && data.Ssim is { } ssim && ssim.Average < s)
                {
                    session.Error($"{data.File.Display}: SSIM {Number(ssim.Average, 4)} is below --min-ssim {Number(s, 4)}.");
                    below = true;
                }
            }

            return code != ExitCodes.Success ? code : below ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    /// <summary>The output style from --json, --csv and --format: text, json or csv.</summary>
    private static string Style(bool json, bool csv, string? format)
    {
        string style = (json, csv, format) switch
        {
            (true, true, _) => throw new UsageException("Give --json or --csv, not both."),
            (true, _, { } f) when !f.Equals("json", StringComparison.OrdinalIgnoreCase) => throw new UsageException($"--json and --format {f} disagree."),
            (_, true, { } f) when !f.Equals("csv", StringComparison.OrdinalIgnoreCase) => throw new UsageException($"--csv and --format {f} disagree."),
            (true, _, _) => "json",
            (_, true, _) => "csv",
            (_, _, { } f) => f.ToLowerInvariant(),
            _ => "text",
        };
        return style is "text" or "json" or "csv" ? style : throw new UsageException($"--format '{style}' is not text, json or csv.");
    }

    private static byte[] ReadAll(VideoSession session)
    {
        using Stream stream = session.Console.In();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Opens the video track <paramref name="tracks"/> picks (the first by default) for decoding.</summary>
    private static VideoReader OpenVideo(VideoSession session, string display, Func<Stream> open, TrackSelection tracks)
    {
        int? number = null;
        using (IDemuxer probe = MediaFile.OpenDemuxer(open(), session.Context))
        {
            TrackInfo track = tracks.SelectOne(probe.Tracks, TrackKind.Video, display);
            number = session.Context.FindCodec((VideoTrackInfo)track) is not null
                ? track.Number
                : throw new UsageException($"{display}: egami-video cannot decode {CodecNames.Name(track.CodecId)} video.");
        }

        return VideoReader.Open(open(), new VideoReaderOptions { TrackNumber = number }, session.Context, leaveOpen: false);
    }

    // ---- hash ----

    /// <summary>What <c>hash</c> hashes.</summary>
    internal sealed record HashRequest(bool Packets, string Algorithm, bool Total, SampleFormat? SampleFormat, TrackSelection Tracks);

    /// <summary>One hashed track: its stream index (its position in the file) and what the header lines say about it.</summary>
    internal sealed class HashStream(int index, TrackInfo track)
    {
        public int Index => index;

        public TrackInfo Track => track;

        public Rational TimeBase { get; set; } = track.TimeBase;

        public string Codec { get; set; } = CodecNames.FfprobeName(track) ?? track.CodecId;

        public string? Dimensions { get; set; }

        public int? SampleRate { get; set; }

        public string? Layout { get; set; }

        public string? Total { get; set; }

        public long Count { get; set; }
    }

    /// <summary>One frame or packet line.</summary>
    internal sealed record HashRow(int Stream, long Dts, long Pts, long Duration, long Size, string Hash, double Seconds);

    /// <summary>What <c>hash</c> found in one input.</summary>
    internal sealed record HashData(InputFile File, List<HashStream> Streams, List<HashRow> Rows);

    /// <summary>Hashes one input's selected tracks.</summary>
    internal static HashData GatherHashes(VideoSession session, InputFile input, HashRequest request, int count)
    {
        // Frames are decoded track by track, so standard input is kept in memory to read it again.
        byte[]? stdin = input.IsStdin && !request.Packets ? ReadAll(session) : null;
        Stream Open() => stdin is not null ? new MemoryStream(stdin, writable: false) : MediaIO.OpenStream(session, input);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Open(), session.Context);
        List<TrackInfo> all = [.. demuxer.Tracks];
        IReadOnlyList<TrackInfo> selected = request.Tracks.Select(all, input.Display);
        HashData data = new(input, [], []);
        JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), demuxer.Info.Duration, selected, session.Cancellation);
        if (request.Packets)
        {
            HashPackets(demuxer, all, selected, request, data, progress);
            progress.Finish();
            return data;
        }

        foreach (TrackInfo track in selected)
        {
            if (track is VideoTrackInfo video && session.Context.FindCodec(video) is not null)
            {
                HashStream stream = new(all.IndexOf(track), track) { Codec = "rawvideo" };
                data.Streams.Add(stream);
                using VideoReader reader = VideoReader.Open(Open(), new VideoReaderOptions { TrackNumber = video.Number }, session.Context);
                using IncrementalHash frameHash = CheckHashing.Create(request.Algorithm);
                using IncrementalHash total = CheckHashing.Create(request.Algorithm);
                while (reader.ReadFrame() is { } frame)
                {
                    using (frame)
                    {
                        progress.Frame(frame.Timestamp);
                        stream.Dimensions ??= $"{frame.Width}x{frame.Height}";
                        long size = CheckHashing.AppendPlanes(frame.Raster, frameHash, total);
                        long pts = frame.Timestamp.Rescale(stream.TimeBase).Ticks;
                        long duration = frame.Duration?.Rescale(stream.TimeBase).Ticks ?? 0;
                        data.Rows.Add(new HashRow(stream.Index, pts, pts, duration, size, Convert.ToHexStringLower(frameHash.GetHashAndReset()), frame.Timestamp.Seconds));
                        stream.Count++;
                    }
                }

                stream.Total = Convert.ToHexStringLower(total.GetHashAndReset());
            }
            else if (track is AudioTrackInfo audio && session.Context.FindAudioCodec(audio) is not null)
            {
                HashStream stream = new(all.IndexOf(track), track);
                data.Streams.Add(stream);
                AudioReaderOptions options = new() { TrackNumber = audio.Number, SampleFormat = request.SampleFormat };
                using AudioReader reader = AudioReader.Open(Open(), options, session.Context);
                using IncrementalHash frameHash = CheckHashing.Create(request.Algorithm);
                using IncrementalHash total = CheckHashing.Create(request.Algorithm);
                stream.TimeBase = new Rational(1, reader.SampleRate);
                stream.SampleRate = reader.SampleRate;
                stream.Layout = reader.Layout.ToString();
                while (reader.TryRead(out AudioFrame? frame))
                {
                    using (frame)
                    {
                        progress.Frame(frame.Timestamp);
                        if (stream.Count == 0)
                        {
                            stream.Codec = CheckHashing.PcmName(frame);
                        }

                        long size = CheckHashing.AppendSamples(frame, frameHash, total);
                        long pts = frame.Timestamp.Rescale(stream.TimeBase).Ticks;
                        data.Rows.Add(new HashRow(stream.Index, pts, pts, frame.SampleCount, size, Convert.ToHexStringLower(frameHash.GetHashAndReset()), frame.Timestamp.Seconds));
                        stream.Count++;
                    }
                }

                stream.Total = Convert.ToHexStringLower(total.GetHashAndReset());
            }
            else if (track.Kind is TrackKind.Video or TrackKind.Audio)
            {
                session.Warn($"{input.Display}: track {track.Number} ({CodecNames.Name(track.CodecId)}) has no decoder, so it is left out; --packets hashes its packets.");
            }
            else if (request.Tracks.IsExplicit)
            {
                session.Warn($"{input.Display}: track {track.Number} is a {Kinds.Name(track.Kind)} track, which has no frames; --packets hashes its packets.");
            }
        }

        // Interleave the tracks by time, as a muxer would, keeping each track's own order.
        List<HashRow> sorted = [.. data.Rows.Select((row, i) => (row, i)).OrderBy(p => p.row.Seconds).ThenBy(p => p.row.Stream).ThenBy(p => p.i).Select(p => p.row)];
        data.Rows.Clear();
        data.Rows.AddRange(sorted);
        progress.Finish();
        return data;
    }

    private static void HashPackets(IDemuxer demuxer, List<TrackInfo> all, IReadOnlyList<TrackInfo> selected, HashRequest request, HashData data, JobProgress progress)
    {
        Dictionary<int, (HashStream Stream, IncrementalHash Total)> streams = [];
        try
        {
            foreach (TrackInfo track in selected)
            {
                HashStream stream = new(all.IndexOf(track), track);
                data.Streams.Add(stream);
                streams[track.Number] = (stream, CheckHashing.Create(request.Algorithm));
            }

            using IncrementalHash packetHash = CheckHashing.Create(request.Algorithm);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (!streams.TryGetValue(packet.TrackNumber, out var entry))
                    {
                        continue;
                    }

                    progress.Packet(packet.TrackNumber, packet.Timestamp);
                    Rational timeBase = entry.Stream.TimeBase;
                    ReadOnlySpan<byte> bytes = packet.Data.Span;
                    packetHash.AppendData(bytes);
                    entry.Total.AppendData(bytes);
                    entry.Stream.Count++;
                    long pts = packet.Timestamp.Rescale(timeBase).Ticks;
                    data.Rows.Add(new HashRow(
                        entry.Stream.Index,
                        packet.DecodeTimestamp?.Rescale(timeBase).Ticks ?? pts,
                        pts,
                        packet.Duration?.Rescale(timeBase).Ticks ?? 0,
                        bytes.Length,
                        Convert.ToHexStringLower(packetHash.GetHashAndReset()),
                        packet.Timestamp.Seconds));
                }
            }

            foreach ((HashStream stream, IncrementalHash total) in streams.Values)
            {
                stream.Total = Convert.ToHexStringLower(total.GetHashAndReset());
            }
        }
        finally
        {
            foreach ((_, IncrementalHash total) in streams.Values)
            {
                total.Dispose();
            }
        }
    }

    /// <summary>Writes the hashes in the style asked for.</summary>
    internal static void WriteHashes(TextWriter output, string style, IReadOnlyList<HashData> files, HashRequest request, bool several)
    {
        switch (style)
        {
            case "json":
                ReportObject root = new();
                root.Add("hash", request.Algorithm);
                root.Add("mode", request.Packets ? "packets" : "frames");
                List<ReportObject> list = root.List("files");
                foreach (HashData file in files)
                {
                    ReportObject entry = new();
                    entry.Add("path", file.File.Display);
                    List<ReportObject> streams = entry.List("streams");
                    foreach (HashStream stream in file.Streams)
                    {
                        streams.Add(new ReportObject()
                            .Add("index", stream.Index)
                            .Add("track", stream.Track.Number)
                            .Add("kind", Kinds.Name(stream.Track.Kind))
                            .Add("codec", stream.Codec)
                            .Add("timeBase", InfoCommand.Ratio(stream.TimeBase))
                            .AddIf("dimensions", stream.Dimensions)
                            .AddIf("sampleRate", stream.SampleRate)
                            .AddIf("layout", stream.Layout)
                            .Add("count", stream.Count)
                            .Add("total", stream.Total));
                    }

                    List<ReportObject> frames = entry.List(request.Packets ? "packets" : "frames");
                    foreach (HashRow row in file.Rows)
                    {
                        frames.Add(new ReportObject().Add("stream", row.Stream).Add("dts", row.Dts).Add("pts", row.Pts).Add("duration", row.Duration).Add("size", row.Size).Add("hash", row.Hash));
                    }

                    list.Add(entry);
                }

                ReportWriter.Json(output, root);
                break;
            case "csv":
                List<ReportObject> rows = [];
                foreach (HashData file in files)
                {
                    rows.AddRange(file.Rows.Select(r => new ReportObject().Add("path", file.File.Display).Add("stream", r.Stream).Add("dts", r.Dts).Add("pts", r.Pts).Add("duration", r.Duration).Add("size", r.Size).Add("hash", r.Hash)));
                }

                ReportWriter.Csv(output, rows);
                break;
            default:
                foreach (HashData file in files)
                {
                    WriteHashText(output, file, request, several);
                }

                break;
        }
    }

    /// <summary>Writes framemd5's lines: header comments, then <c>stream, dts, pts, duration, size, hash</c> per frame.</summary>
    private static void WriteHashText(TextWriter output, HashData file, HashRequest request, bool several)
    {
        output.Write("#format: frame checksums\n#version: 2\n");
        output.Write($"#hash: {(request.Algorithm == "md5" ? "MD5" : "SHA256")}\n");
        if (several)
        {
            output.Write($"#input: {file.File.Display}\n");
        }

        foreach (HashStream stream in file.Streams)
        {
            int i = stream.Index;
            output.Write($"#tb {i}: {InfoCommand.Ratio(stream.TimeBase)}\n");
            output.Write($"#media_type {i}: {Kinds.Name(stream.Track.Kind)}\n");
            output.Write($"#codec_id {i}: {stream.Codec}\n");
            if (stream.Track is VideoTrackInfo video)
            {
                output.Write($"#dimensions {i}: {stream.Dimensions ?? $"{video.Width}x{video.Height}"}\n");
            }

            if (stream.Track is AudioTrackInfo audio)
            {
                output.Write($"#sample_rate {i}: {stream.SampleRate ?? audio.SampleRate}\n");
                output.Write($"#channel_layout_name {i}: {stream.Layout ?? ChannelLayout.ForChannelCount(audio.Channels).ToString()}\n");
            }
        }

        output.Write("#stream#, dts,        pts, duration,     size, hash\n");
        foreach (HashRow row in file.Rows)
        {
            output.Write(string.Create(CultureInfo.InvariantCulture, $"{row.Stream},{row.Dts,11},{row.Pts,11},{row.Duration,9},{row.Size,9}, {row.Hash}\n"));
        }

        if (request.Total)
        {
            foreach (HashStream stream in file.Streams)
            {
                output.Write(string.Create(CultureInfo.InvariantCulture, $"#total {stream.Index}: {stream.Count} {(request.Packets ? "packets" : "frames")}, {stream.Total}\n"));
            }
        }
    }

    // ---- compare ----

    /// <summary>What <c>compare</c> measures.</summary>
    internal sealed record CompareRequest(InputFile Reference, bool Psnr, bool Ssim, bool PerFrame, bool Scale, TrackSelection Tracks);

    /// <summary>One frame pair's scores.</summary>
    internal sealed record FrameScores(int Index, MediaTime Timestamp, PlaneScores? Psnr, PlaneScores? Ssim);

    /// <summary>One clip's scores against the reference.</summary>
    internal sealed record CompareData(InputFile File, long Frames, long ReferenceFrames, long ClipFrames, PlaneScores? Psnr, PlaneScores? Ssim, double PsnrMin, double PsnrMax, List<FrameScores> PerFrame);

    /// <summary>Measures one clip against the reference, pairing frames in presentation order.</summary>
    internal static CompareData Measure(VideoSession session, InputFile input, CompareRequest request, byte[]? referenceBytes, int count)
    {
        byte[]? clipBytes = input.IsStdin ? ReadAll(session) : null;
        using VideoReader reference = OpenVideo(session, request.Reference.Display, () => referenceBytes is not null ? new MemoryStream(referenceBytes, writable: false) : MediaIO.OpenStream(session, request.Reference), request.Tracks);
        using VideoReader clip = OpenVideo(session, input.Display, () => clipBytes is not null ? new MemoryStream(clipBytes, writable: false) : MediaIO.OpenStream(session, input), request.Tracks);
        VideoQuality quality = new();
        List<FrameScores> perFrame = [];
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), reference.Info.Duration, [reference.Track], session.Cancellation);
        long pairs = 0;
        while (true)
        {
            YuvVideoFrame? original = reference.ReadFrame();
            YuvVideoFrame? measured = original is null ? null : clip.ReadFrame();
            if (original is null || measured is null)
            {
                original?.Dispose();
                long referenceFrames = pairs + (original is null ? 0 : 1) + Drain(reference, session);
                long clipFrames = pairs + Drain(clip, session);
                if (referenceFrames != clipFrames)
                {
                    session.Warn($"{input.Display}: the reference has {referenceFrames} frames and this clip {clipFrames}; the first {pairs} were compared.");
                }

                progress.Finish();
                return pairs == 0
                    ? throw new InvalidDataException("no frames to compare.")
                    : new CompareData(input, pairs, referenceFrames, clipFrames, request.Psnr ? quality.Psnr : null, request.Ssim ? quality.Ssim : null, min, max, perFrame);
            }

            using (original)
            using (measured)
            {
                progress.Frame(original.Timestamp);
                YuvRaster target = original.Raster;
                YuvRaster other = measured.Raster;
                using YuvRaster? resized = other.Width != target.Width || other.Height != target.Height
                    ? request.Scale
                        ? other.Resize(target.Width, target.Height)
                        : throw new UsageException($"{input.Display}: frame {pairs} is {other.Width}x{other.Height} and the reference's is {target.Width}x{target.Height}; give --scale to resize it.")
                    : null;
                other = resized ?? other;
                if (other.ChromaFormat != target.ChromaFormat || other.BitDepth != target.BitDepth || other.HasAlpha != target.HasAlpha)
                {
                    throw new UsageException($"{input.Display}: frame {pairs} is {Describe(other)} and the reference's is {Describe(target)}; both must have the same chroma format, bit depth and alpha.");
                }

                (PlaneScores psnr, PlaneScores ssim) = quality.Add(target, other);
                min = Math.Min(min, psnr.Average);
                max = Math.Max(max, psnr.Average);
                if (request.PerFrame)
                {
                    perFrame.Add(new FrameScores((int)pairs, original.Timestamp, request.Psnr ? psnr : null, request.Ssim ? ssim : null));
                }
            }

            pairs++;
        }
    }

    /// <summary>Reads the frames left, to count them.</summary>
    private static long Drain(VideoReader reader, VideoSession session)
    {
        long count = 0;
        while (reader.ReadFrame() is { } frame)
        {
            frame.Dispose();
            session.Cancellation.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private static string Describe(YuvRaster raster)
    {
        string chroma = raster.ChromaFormat switch
        {
            ChromaFormat.Yuv420 => "4:2:0",
            ChromaFormat.Yuv422 => "4:2:2",
            ChromaFormat.Yuv444 => "4:4:4",
            ChromaFormat.Yuv400 => "gray",
            ChromaFormat.Yuv411 => "4:1:1",
            _ => "4:1:0",
        };
        return $"{chroma} {raster.BitDepth}-bit{(raster.HasAlpha ? " with alpha" : string.Empty)}";
    }

    /// <summary>Writes the scores in the style asked for.</summary>
    internal static void WriteComparison(TextWriter output, string style, IReadOnlyList<CompareData> files, CompareRequest request)
    {
        switch (style)
        {
            case "json":
                ReportObject root = new();
                root.Add("reference", request.Reference.Display);
                List<ReportObject> list = root.List("files");
                foreach (CompareData file in files)
                {
                    ReportObject entry = new ReportObject()
                        .Add("path", file.File.Display)
                        .Add("frames", file.Frames)
                        .Add("referenceFrames", file.ReferenceFrames)
                        .Add("clipFrames", file.ClipFrames);
                    if (file.Psnr is { } psnr)
                    {
                        entry.Add("psnr", Scores(psnr).Add("min", file.PsnrMin).Add("max", file.PsnrMax));
                    }

                    if (file.Ssim is { } ssim)
                    {
                        entry.Add("ssim", Scores(ssim));
                    }

                    if (request.PerFrame)
                    {
                        List<ReportObject> frames = entry.List("perFrame");
                        foreach (FrameScores frame in file.PerFrame)
                        {
                            frames.Add(new ReportObject()
                                .Add("frame", frame.Index)
                                .Add("time", frame.Timestamp.Seconds)
                                .AddIf("psnr", frame.Psnr is { } p ? Scores(p) : null)
                                .AddIf("ssim", frame.Ssim is { } s ? Scores(s) : null));
                        }
                    }

                    list.Add(entry);
                }

                ReportWriter.Json(output, root);
                break;
            case "csv":
                List<ReportObject> rows = [];
                foreach (CompareData file in files)
                {
                    IEnumerable<(string Frame, double? Time, PlaneScores? Psnr, PlaneScores? Ssim)> lines = request.PerFrame
                        ? file.PerFrame.Select(f => (f.Index.ToString(CultureInfo.InvariantCulture), (double?)f.Timestamp.Seconds, f.Psnr, f.Ssim))
                        : [];
                    foreach ((string frame, double? time, PlaneScores? psnr, PlaneScores? ssim) in lines.Append(("all", null, file.Psnr, file.Ssim)))
                    {
                        ReportObject row = new ReportObject().Add("path", file.File.Display).Add("frame", frame).Add("time", time);
                        AddColumns(row, "psnr", psnr, request.Psnr);
                        AddColumns(row, "ssim", ssim, request.Ssim);
                        rows.Add(row);
                    }
                }

                ReportWriter.Csv(output, rows);
                break;
            default:
                foreach (CompareData file in files)
                {
                    output.Write($"{file.File.Display}: {file.Frames} frames\n");
                    foreach (FrameScores frame in file.PerFrame)
                    {
                        output.Write(string.Create(CultureInfo.InvariantCulture, $"  frame {frame.Index} ({frame.Timestamp.Seconds:0.000}s):"));
                        output.Write(frame.Psnr is { } p ? $" PSNR {Planes(p, 2)}" : string.Empty);
                        output.Write(frame.Ssim is { } s ? $" SSIM {Planes(s, 6)}" : string.Empty);
                        output.Write('\n');
                    }

                    if (file.Psnr is { } psnr)
                    {
                        output.Write($"PSNR {Planes(psnr, 2)} min:{Number(file.PsnrMin, 2)} max:{Number(file.PsnrMax, 2)}\n");
                    }

                    if (file.Ssim is { } ssim)
                    {
                        output.Write($"SSIM {Planes(ssim, 6)}\n");
                    }
                }

                break;
        }
    }

    private static void AddColumns(ReportObject row, string name, PlaneScores? scores, bool asked)
    {
        if (asked)
        {
            row.Add($"{name}Y", scores?.Y).Add($"{name}U", scores?.U).Add($"{name}V", scores?.V).Add($"{name}A", scores?.A).Add($"{name}Average", scores?.Average);
        }
    }

    /// <summary>A score per plane for JSON; infinite PSNR (identical planes) is written as null.</summary>
    private static ReportObject Scores(PlaneScores scores)
    {
        ReportObject report = new ReportObject().Add("y", scores.Y).Add("u", scores.U).Add("v", scores.V);
        return report.AddIf("a", scores.A).Add("average", scores.Average);
    }

    private static string Planes(PlaneScores scores, int decimals)
    {
        string text = $"y:{Number(scores.Y, decimals)}";
        if (!double.IsNaN(scores.U))
        {
            text += $" u:{Number(scores.U, decimals)} v:{Number(scores.V, decimals)}";
        }

        if (scores.A is { } alpha)
        {
            text += $" a:{Number(alpha, decimals)}";
        }

        return text + $" average:{Number(scores.Average, decimals)}";
    }

    private static string Number(double value, int decimals) =>
        double.IsPositiveInfinity(value) ? "inf" : double.IsNaN(value) ? "nan" : value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
