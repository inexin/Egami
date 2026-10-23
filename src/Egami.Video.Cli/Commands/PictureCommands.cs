using System.CommandLine;
using System.Globalization;

using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Substitutes;
using Egami.Pixels;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// The commands that make pictures from a video (Build Plan tasks 13 to 15): <c>thumbnail</c> (a representative frame),
/// <c>scenes</c> (cuts between shots, and a frame of each), <c>sheet</c> (a contact sheet) and <c>animate</c> (GIF, WebP or APNG).
/// </summary>
internal static class PictureCommands
{
    /// <summary>The family contact sheets are labeled in: bundled, so a sheet looks the same on every machine.</summary>
    public const string SheetFamily = "Liberation Sans";

    /// <summary>The most frames <c>thumbnail</c> looks at to choose one.</summary>
    private const int ThumbnailSamples = 100;

    /// <summary>The most frames <c>animate</c> writes; more would hold too many pictures in memory.</summary>
    private const int MaxAnimationFrames = 3000;

    public static Command Thumbnail(VideoSession session)
    {
        PictureOptions options = new("thumbs/{name}.jpg");
        Option<string?> size = new("--size", "-s") { Description = "The thumbnail's size in egami's size syntax: 320x, x180, 320x180 (fits inside), 50%.", HelpName = "size" };
        Option<string?> at = new("--at") { Description = "Take the frame shown at this time (seconds, [HH:]MM:SS[.mmm] or #frame) instead of choosing one.", HelpName = "time" };
        Command command = new("thumbnail", "Write a representative frame of each video as an image: a typical frame of its opening, not a black or empty one, or the frame at --at.");
        options.AddTo(command);
        command.Options.Add(size);
        command.Options.Add(at);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Media.Batch(session, result);
            List<InputFile> inputs = options.Media.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs);
            SizeSpec? spec = result.GetValue(size) is { } sizeText ? PictureFrames.ParseSize(sizeText) : null;
            TimeValue? time = result.GetValue(at) is { } atText ? TimeValue.Parse(atText, "--at") : null;
            TrackSelection selection = options.Tracks(result);
            SaveSpec save = options.Save(result);
            string? format = result.GetValue(options.Format);
            return Batch.Run(session, inputs, batch, input =>
            {
                PictureTarget target = PictureTarget.Resolve(session, input, output, format, "jpg");
                using Raster<Rgba8> image = time is { } t
                    ? FrameAt(session, input, selection, t, inputs.Count)
                    : Representative(session, input, selection, inputs.Count);
                PictureFrames.Resize(image, spec);
                target.Write(session, image, save);
                return [target.Path];
            });
        });
        return command;
    }

    public static Command Scenes(VideoSession session)
    {
        PictureOptions options = new("shots/{name}-{scene:000}.jpg");
        Option<double> threshold = new("--threshold") { Description = "How sharp a change starts a scene, from 0 to 1; lower finds more cuts.", HelpName = "0-1", DefaultValueFactory = _ => 0.4 };
        Option<string> minGap = new("--min-gap") { Description = "The shortest scene: a cut closer than this to the last one is not taken.", HelpName = "time", DefaultValueFactory = _ => "1s" };
        Option<bool> json = new("--json") { Description = "Write the scenes as JSON." };
        Option<bool> csv = new("--csv") { Description = "Write the scenes as CSV, one row per scene." };
        options.Output.Description = "Also write the first frame of each scene: a folder, or a template with {scene} (from 1, padded as {scene:000}), {name}, {dir} and {index}.";
        Command command = new("scenes", "Find the cuts between shots: list where each scene starts (frame, time and how sharp the cut is), and optionally write a frame of each.");
        options.AddTo(command);
        command.Options.Add(threshold);
        command.Options.Add(minGap);
        command.Options.Add(json);
        command.Options.Add(csv);
        command.SetAction(result =>
        {
            if (result.GetValue(json) && result.GetValue(csv))
            {
                throw new UsageException("Give --json or --csv, not both.");
            }

            double limit = result.GetValue(threshold);
            if (limit is < 0 or > 1 || double.IsNaN(limit))
            {
                throw new UsageException("--threshold must be from 0 to 1.");
            }

            TimeValue gap = TimeValue.Parse(result.GetValue(minGap)!, "--min-gap");
            if (gap.IsFrame || gap.Seconds < 0)
            {
                throw new UsageException("--min-gap must be a length of time of 0 or more, such as 1s or 500ms.");
            }

            BatchOptions batch = options.Media.Batch(session, result);
            List<InputFile> inputs = options.Media.ExpandInputs(session, result);
            OutputTemplate? output = result.GetValue(options.Output) is null ? null : options.OutputTemplate(session, result, inputs, "scene");
            if (output is not null)
            {
                bool folder = output.Text.EndsWith('/') || output.Text.EndsWith('\\') || (!output.HasPlaceholders && Directory.Exists(session.ResolvePath(output.Text)));
                if (output.IsStdout || (!folder && !output.NamesFrames))
                {
                    throw new UsageException($"-o '{output.Text}' would write every scene to one file; give a folder or a template with {{scene}}, such as shots/{{name}}-{{scene:000}}.jpg.");
                }
            }

            TrackSelection selection = options.Tracks(result);
            SaveSpec save = options.Save(result);
            string? format = result.GetValue(options.Format);
            List<Scene>?[] found = new List<Scene>?[inputs.Count];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                List<string> written = [];
                found[input.Index] = FindScenes(session, input, selection, limit, gap.Seconds, inputs.Count, (scene, image) =>
                {
                    if (output is not null)
                    {
                        PictureTarget target = PictureTarget.Resolve(session, input, output, format, "jpg", scene.Number);
                        target.Write(session, image, save);
                        written.Add(target.Path);
                    }
                });
                return written;
            });
            WriteScenes(session.Console.Text, inputs, found, result.GetValue(json), result.GetValue(csv));
            return code;
        });
        return command;
    }

    public static Command Sheet(VideoSession session)
    {
        PictureOptions options = new("sheets/{name}.png");
        Option<string> grid = new("--grid") { Description = "Columns and rows of frames, such as 4x4 or 5x3.", HelpName = "CxR", DefaultValueFactory = _ => "4x4" };
        Option<int> width = new("--width") { Description = "The sheet's width in pixels; the tiles share it and keep the video's shape.", HelpName = "pixels", DefaultValueFactory = _ => 1280 };
        Option<bool> timestamps = new("--timestamps") { Description = "Write each frame's time under it." };
        Option<bool> title = new("--title") { Description = "Write the file's name, length, size and codec above the frames." };
        Command command = new("sheet", "Make a contact sheet: a grid of evenly spaced frames, with their times and a title if asked.");
        options.AddTo(command);
        command.Options.Add(grid);
        command.Options.Add(width);
        command.Options.Add(timestamps);
        command.Options.Add(title);
        command.SetAction(result =>
        {
            (int columns, int rows) = ParseGrid(result.GetValue(grid)!);
            int sheetWidth = result.GetValue(width);
            if (sheetWidth is < 64 or > 16384)
            {
                throw new UsageException("--width must be from 64 to 16384 pixels.");
            }

            BatchOptions batch = options.Media.Batch(session, result);
            List<InputFile> inputs = options.Media.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs);
            TrackSelection selection = options.Tracks(result);
            SaveSpec save = options.Save(result);
            string? format = result.GetValue(options.Format);
            SheetLayout layout = new(columns, rows, sheetWidth, result.GetValue(timestamps), result.GetValue(title));
            return Batch.Run(session, inputs, batch, input =>
            {
                PictureTarget target = PictureTarget.Resolve(session, input, output, format, "png");
                using Raster<Rgba8> sheet = MakeSheet(session, input, selection, layout, inputs.Count);
                target.Write(session, sheet, save);
                return [target.Path];
            });
        });
        return command;
    }

    public static Command Animate(VideoSession session)
    {
        PictureOptions options = new("clips/{name}.gif");
        Option<string> rate = new("--rate") { Description = "Frames per second of the animation; frames of the video are dropped or held to match.", HelpName = "fps", DefaultValueFactory = _ => "10" };
        Option<string?> size = new("--size", "-s") { Description = "The animation's size in egami's size syntax: 320x, x180, 320x180 (fits inside), 50%.", HelpName = "size" };
        Option<string?> from = new("--from") { Description = "Start at this time (seconds, [HH:]MM:SS[.mmm] or #frame); the start of the video by default.", HelpName = "time" };
        Option<string?> to = new("--to") { Description = "End at this time; the end of the video by default.", HelpName = "time" };
        Option<int> loop = new("--loop") { Description = "How many times the animation plays; 0 (the default) loops forever.", HelpName = "count" };
        Option<bool?> lossless = new("--lossless") { Description = "Write lossless WebP." };
        options.Output.Description = "The animation to write: .gif, .webp or .png (APNG); a folder, or a template with {name}, {dir} and {index}.";
        Command command = new("animate", "Make an animated GIF, WebP or PNG (APNG) from a stretch of video, at a frame rate and size of your choice.");
        options.AddTo(command);
        command.Options.Add(rate);
        command.Options.Add(size);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(loop);
        command.Options.Add(lossless);
        command.SetAction(result =>
        {
            double fps = PictureFrames.ParseRate(result.GetValue(rate)!, "--rate").ToDouble();
            SizeSpec? spec = result.GetValue(size) is { } sizeText ? PictureFrames.ParseSize(sizeText) : null;
            TimeValue? start = result.GetValue(from) is { } fromText ? TimeValue.Parse(fromText, "--from") : null;
            TimeValue? end = result.GetValue(to) is { } toText ? TimeValue.Parse(toText, "--to") : null;
            int loops = result.GetValue(loop);
            if (loops is < 0 or > 65535)
            {
                throw new UsageException("--loop must be from 0 (forever) to 65535.");
            }

            BatchOptions batch = options.Media.Batch(session, result);
            List<InputFile> inputs = options.Media.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs);
            TrackSelection selection = options.Tracks(result);
            Dictionary<string, string> more = [];
            if (result.GetValue(lossless) is { } l)
            {
                more["lossless"] = l ? "true" : "false";
            }

            SaveSpec save = options.Save(result, more);
            string? format = result.GetValue(options.Format);
            return Batch.Run(session, inputs, batch, input =>
            {
                PictureTarget target = PictureTarget.Resolve(session, input, output, format, "gif");
                if (target.Format.Name.ToUpperInvariant() is not ("GIF" or "WEBP" or "PNG"))
                {
                    throw new UsageException($"animate writes GIF, WebP or PNG (APNG), not {target.Format.Name}; give -o with .gif, .webp or .png.");
                }

                using Raster<Rgba8> animation = MakeAnimation(session, input, selection, fps, spec, start, end, inputs.Count);
                animation.Metadata.LoopCount = loops;
                target.Write(session, animation, save);
                session.Info($"{input.Display}: {animation.Frames.Count} frames");
                return [target.Path];
            });
        });
        return command;
    }

    /// <summary>Which sample is most typical: the closest to the mean color histogram, leaving out near-black and near-flat frames.</summary>
    internal static int ChooseRepresentative(IReadOnlyList<FrameSummary> samples)
    {
        List<int> candidates = [.. Enumerable.Range(0, samples.Count).Where(i => samples[i].Mean >= 0.08 && samples[i].Spread >= 0.04)];
        if (candidates.Count == 0)
        {
            candidates = [.. Enumerable.Range(0, samples.Count)];
        }

        double[] mean = new double[PictureFrames.HistogramBins];
        foreach (int i in candidates)
        {
            for (int b = 0; b < mean.Length; b++)
            {
                mean[b] += samples[i].Histogram[b] / candidates.Count;
            }
        }

        int best = candidates[0];
        double bestDistance = double.MaxValue;
        foreach (int i in candidates)
        {
            double distance = 0;
            for (int b = 0; b < mean.Length; b++)
            {
                double d = samples[i].Histogram[b] - mean[b];
                distance += d * d;
            }

            if (distance < bestDistance)
            {
                (best, bestDistance) = (i, distance);
            }
        }

        return best;
    }

    /// <summary>The frame shown at a time, or the frame with a number.</summary>
    private static Raster<Rgba8> FrameAt(VideoSession session, InputFile input, TrackSelection selection, TimeValue time, int count)
    {
        using FrameSource source = FrameSource.Open(session, input, selection);
        JobProgress progress = Progress(session, input, source, count);
        Raster<Rgba8>? image = null;
        if (time.Frame is { } number)
        {
            long index = 0;
            foreach (YuvVideoFrame frame in source.Frames())
            {
                using (frame)
                {
                    progress.Frame(frame.Timestamp);
                    if (index++ == number)
                    {
                        image = FrameSource.ToImage(session, frame);
                        break;
                    }
                }
            }

            progress.Finish();
            return image ?? throw new UsageException($"{input.Display}: --at {time.Text} is past the last frame; the video has {index} frames (#0 to #{index - 1}).");
        }

        if (PictureFrames.Duration(source) is { } duration && time.Seconds > duration)
        {
            throw new UsageException($"{input.Display}: --at {time.Text} is past the end; the video is {TimeValue.Format(duration)} long.");
        }

        PictureFrames.AtTimes(source, [time.Seconds], progress, (_, frame) => image = FrameSource.ToImage(session, frame));
        progress.Finish();
        return image ?? throw new InvalidDataException($"{input.Display}: the video has no frames.");
    }

    /// <summary>
    /// A representative frame, chosen as thumbnail makers classically do: up to 100 frames sampled evenly from the opening (the
    /// first tenth of the video, or its first 100 frames when that is longer), less the near-black and near-flat ones, and of
    /// those the frame whose color histogram is closest to their mean histogram.
    /// </summary>
    private static Raster<Rgba8> Representative(VideoSession session, InputFile input, TrackSelection selection, int count)
    {
        List<(long Index, FrameSummary Summary, Raster<Rgba8>? Image)> samples = [];
        try
        {
            using (FrameSource source = FrameSource.Open(session, input, selection))
            {
                JobProgress progress = Progress(session, input, source, count);
                double fps = source.Track.FrameRate is { } r && r.ToDouble() > 0 ? r.ToDouble() : 25;
                double? duration = PictureFrames.Duration(source);
                double window = duration is { } d ? Math.Max(d * 0.1, ThumbnailSamples / fps) : double.PositiveInfinity;
                double expected = Math.Min(duration ?? (ThumbnailSamples / fps), window) * fps;
                int stride = Math.Max(1, (int)Math.Ceiling(expected / ThumbnailSamples));
                long index = -1;
                double? first = null;
                foreach (YuvVideoFrame frame in source.Frames())
                {
                    using (frame)
                    {
                        progress.Frame(frame.Timestamp);
                        index++;
                        first ??= frame.Timestamp.Seconds;
                        if ((frame.Timestamp.Seconds - first >= window && samples.Count > 0) || samples.Count >= ThumbnailSamples)
                        {
                            break;
                        }

                        if (index % stride != 0)
                        {
                            continue;
                        }

                        Raster<Rgba8> image = FrameSource.ToImage(session, frame);
                        FrameSummary summary = PictureFrames.Summarize(image);

                        // Standard input cannot be read again, so its samples are kept; a file is decoded again to the chosen one.
                        if (input.IsStdin)
                        {
                            samples.Add((index, summary, image));
                        }
                        else
                        {
                            image.Dispose();
                            samples.Add((index, summary, null));
                        }
                    }
                }

                progress.Finish();
            }

            if (samples.Count == 0)
            {
                throw new InvalidDataException($"{input.Display}: the video has no frames.");
            }

            int chosen = ChooseRepresentative([.. samples.Select(s => s.Summary)]);
            long frameNumber = samples[chosen].Index;
            session.Info($"{input.Display}: chose frame #{frameNumber} of {samples.Count} looked at");
            if (samples[chosen].Image is { } kept)
            {
                samples[chosen] = (frameNumber, samples[chosen].Summary, null);
                return kept;
            }

            return FrameAt(session, input, selection, new TimeValue(0, frameNumber, $"#{frameNumber}"), count);
        }
        finally
        {
            foreach ((_, _, Raster<Rgba8>? image) in samples)
            {
                image?.Dispose();
            }
        }
    }

    /// <summary>Runs the scene detector over every frame; <paramref name="start"/> gets each scene's first frame.</summary>
    private static List<Scene> FindScenes(VideoSession session, InputFile input, TrackSelection selection, double threshold, double minGap, int count, Action<Scene, Raster<Rgba8>> start)
    {
        using FrameSource source = FrameSource.Open(session, input, selection);
        JobProgress progress = Progress(session, input, source, count);
        SceneDetector detector = new(threshold, minGap);
        foreach (YuvVideoFrame frame in source.Frames())
        {
            using (frame)
            {
                progress.Frame(frame.Timestamp);
                using Raster<Rgba8> image = FrameSource.ToImage(session, frame);
                if (detector.Add(PictureFrames.Summarize(image), frame.Timestamp.Seconds) is { } scene)
                {
                    start(scene, image);
                }
            }
        }

        progress.Finish();
        session.Info($"{input.Display}: {detector.Scenes.Count} scenes");
        return detector.Scenes;
    }

    private static void WriteScenes(TextWriter output, List<InputFile> inputs, List<Scene>?[] found, bool json, bool csv)
    {
        if (json)
        {
            ReportObject report = new();
            List<ReportObject> files = report.List("files");
            for (int i = 0; i < inputs.Count; i++)
            {
                if (found[i] is not { } scenes)
                {
                    continue;
                }

                ReportObject file = new ReportObject().Add("file", inputs[i].Display);
                List<ReportObject> list = file.List("scenes");
                foreach (Scene scene in scenes)
                {
                    list.Add(new ReportObject().Add("scene", scene.Number).Add("frame", scene.Frame).Add("time", TimeValue.Format(scene.Seconds))
                        .Add("seconds", Math.Round(scene.Seconds, 6)).Add("score", Math.Round(scene.Score, 4)));
                }

                files.Add(file);
            }

            ReportWriter.Json(output, report);
            return;
        }

        if (csv)
        {
            List<ReportObject> rows = [];
            for (int i = 0; i < inputs.Count; i++)
            {
                foreach (Scene scene in found[i] ?? [])
                {
                    rows.Add(new ReportObject().Add("file", inputs[i].Display).Add("scene", scene.Number).Add("frame", scene.Frame)
                        .Add("time", TimeValue.Format(scene.Seconds)).Add("seconds", Math.Round(scene.Seconds, 6)).Add("score", Math.Round(scene.Score, 4)));
                }
            }

            ReportWriter.Csv(output, rows);
            return;
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            if (found[i] is not { } scenes)
            {
                continue;
            }

            if (inputs.Count > 1)
            {
                output.WriteLine($"{inputs[i].Display}:");
            }

            output.WriteLine("scene   frame  time         score");
            foreach (Scene scene in scenes)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{scene.Number,5}  {scene.Frame,6}  {TimeValue.Format(scene.Seconds),-11}  {scene.Score:0.000}"));
            }
        }
    }

    private static (int Columns, int Rows) ParseGrid(string text)
    {
        string[] parts = text.Trim().ToLowerInvariant().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int columns)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int rows)
            && columns is >= 1 and <= 32 && rows is >= 1 and <= 32
                ? (columns, rows)
                : throw new UsageException($"--grid '{text}' is not a grid: give columns x rows, each from 1 to 32, such as 4x4 or 5x3.");
    }

    /// <summary>Lays out and draws a contact sheet of one input.</summary>
    private static Raster<Rgba8> MakeSheet(VideoSession session, InputFile input, TrackSelection selection, SheetLayout layout, int count)
    {
        using FrameSource source = FrameSource.Open(session, input, selection);
        JobProgress progress = Progress(session, input, source, count);
        double duration = PictureFrames.Duration(source) ?? LastFrameTime(session, input, source);
        int tiles = layout.Columns * layout.Rows;
        int gap = Math.Max(2, layout.Width / 160);
        int tileWidth = (layout.Width - (gap * (layout.Columns + 1))) / layout.Columns;
        if (tileWidth < 16)
        {
            throw new UsageException($"--width {layout.Width} leaves tiles under 16 pixels wide for {layout.Columns} columns; give a wider sheet or fewer columns.");
        }

        VideoTrackInfo track = source.Track;
        double aspect = track.Width * track.PixelAspectRatio.ToDouble() / Math.Max(1, track.Height);
        int tileHeight = Math.Max(1, (int)Math.Round(tileWidth / (aspect > 0 ? aspect : 1)));
        float labelSize = Math.Clamp(tileWidth / 14f, 10, 20);
        int labelHeight = layout.Timestamps ? (int)MathF.Ceiling(labelSize * 1.6f) : 0;
        float titleSize = Math.Clamp(layout.Width / 48f, 12, 32);
        float infoSize = Math.Max(10, titleSize * 0.7f);
        int titleHeight = layout.Title ? (int)MathF.Ceiling((titleSize * 1.4f) + (infoSize * 1.5f)) + gap : 0;
        int height = titleHeight + (layout.Rows * (tileHeight + labelHeight)) + (gap * (layout.Rows + 1));

        Raster<Rgba8> sheet = Raster.Create(layout.Width, height, new Rgba8(24, 24, 24, 255), session.Images.Context);
        try
        {
            string?[] labels = new string?[tiles];
            double[] targets = [.. Enumerable.Range(0, tiles).Select(i => duration * (i + 0.5) / tiles)];
            PictureFrames.AtTimes(source, targets, progress, (i, frame) =>
            {
                using Raster<Rgba8> image = FrameSource.ToImage(session, frame);
                image.Apply(ops => ops.Resize(tileWidth, tileHeight));
                (int x, int y) = TileCorner(i);
                sheet.Apply(ops => ops.DrawImage(image, new Point(x, y)));
                labels[i] = TimeValue.Format(frame.Timestamp);
            });
            progress.Finish();

            if (layout.Timestamps || layout.Title)
            {
                FontFamily family = SubstituteFonts.Collection.TryGetFamily(SheetFamily, out FontFamily bundled) ? bundled : SubstituteFonts.Collection.Families[0];
                using RasterCanvas<Rgba8> canvas = sheet.CreateCanvas();
                if (layout.Title)
                {
                    ParagraphStyle name = Style(family, titleSize, Rgba8.White, TextAlign.Left);
                    ParagraphStyle info = Style(family, infoSize, new Rgba8(190, 190, 190, 255), TextAlign.Left);
                    float lineWidth = layout.Width - (2 * gap);
                    canvas.DrawText(input.Path is { } path ? System.IO.Path.GetFileName(path) : input.Display, new RectF(gap, gap, lineWidth, titleSize * 1.4f), name, VerticalAlign.Center);
                    canvas.DrawText(Describe(source, duration), new RectF(gap, gap + (titleSize * 1.4f), lineWidth, infoSize * 1.5f), info, VerticalAlign.Center);
                }

                if (layout.Timestamps)
                {
                    ParagraphStyle style = Style(family, labelSize, new Rgba8(230, 230, 230, 255), TextAlign.Center);
                    for (int i = 0; i < tiles; i++)
                    {
                        if (labels[i] is { } label)
                        {
                            (int x, int y) = TileCorner(i);
                            canvas.DrawText(label, new RectF(x, y + tileHeight, tileWidth, labelHeight), style, VerticalAlign.Center);
                        }
                    }
                }
            }

            return sheet;
        }
        catch
        {
            sheet.Dispose();
            throw;
        }

        (int X, int Y) TileCorner(int i) =>
            (gap + ((i % layout.Columns) * (tileWidth + gap)), titleHeight + gap + ((i / layout.Columns) * (tileHeight + labelHeight + gap)));
    }

    /// <summary>The time of the last frame, for a file whose headers give no length.</summary>
    private static double LastFrameTime(VideoSession session, InputFile input, FrameSource source)
    {
        if (input.IsStdin)
        {
            throw new UsageException($"{input.Display}: the video's headers do not give its length, which a sheet needs; give a file.");
        }

        IReadOnlyList<MediaTime> times = MediaIO.FrameTimes(session, input, source.Track.Number);
        return times.Count > 0 ? times[^1].Seconds : 0;
    }

    private static ParagraphStyle Style(FontFamily family, float size, Rgba8 color, TextAlign align)
    {
        return new ParagraphStyle
        {
            DefaultStyle = new TextStyle { FontFamilies = [family], Size = size, Color = color },
            Align = align,
            MaxLines = 1,
            Ellipsis = "...",
        };
    }

    /// <summary>The sheet's second title line: length, picture size, codec and frame rate.</summary>
    private static string Describe(FrameSource source, double duration)
    {
        VideoTrackInfo track = source.Track;
        List<string> parts = [TimeValue.Format(duration), $"{track.Width}x{track.Height}", CodecNames.Name(track.CodecId)];
        if (track.FrameRate is { } rate && rate.ToDouble() > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{rate.ToDouble():0.###} fps"));
        }

        return string.Join("   ", parts);
    }

    /// <summary>
    /// The frames of an animation: the frame shown at each step of <paramref name="fps"/> from the start to the end, a video frame
    /// held over several steps written once with a longer delay. Delays are whole milliseconds that add up to the steps' times.
    /// </summary>
    private static Raster<Rgba8> MakeAnimation(VideoSession session, InputFile input, TrackSelection selection, double fps, SizeSpec? size, TimeValue? from, TimeValue? to, int count)
    {
        using FrameSource source = FrameSource.Open(session, input, selection);
        double start = Seconds(session, input, source, from, "--from") ?? 0;
        double end = Seconds(session, input, source, to, "--to") ?? double.PositiveInfinity;
        if (end <= start)
        {
            throw new UsageException($"{input.Display}: --to must come after --from.");
        }

        if (PictureFrames.Duration(source) is { } duration && start >= duration)
        {
            throw new UsageException($"{input.Display}: --from {from?.Text} is past the end; the video is {TimeValue.Format(duration)} long.");
        }

        double sourceStep = source.Track.FrameRate is { } r && r.ToDouble() > 0 ? 1 / r.ToDouble() : 1 / fps;
        JobProgress progress = Progress(session, input, source, count);
        if (start > 0 && source.Reader.CanSeek)
        {
            source.Reader.Seek(MediaTime.FromSeconds(start));
        }

        Raster<Rgba8>? animation = null;
        YuvVideoFrame? held = null;
        YuvVideoFrame? last = null;
        long step = 0;
        double Time(long k) => start + (k / fps);
        void Add(YuvVideoFrame frame)
        {
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Round((step + 1) * 1000 / fps) - Math.Round(step * 1000 / fps));
            step++;
            if (ReferenceEquals(frame, last) && animation is not null)
            {
                animation.Frames[^1].Metadata.Delay += delay;
                return;
            }

            if ((animation?.Frames.Count ?? 0) >= MaxAnimationFrames)
            {
                throw new UsageException($"{input.Display}: the animation would have more than {MaxAnimationFrames} frames; give --to, a later --from or a lower --rate.");
            }

            last = frame;
            Raster<Rgba8> image = PictureFrames.Resize(FrameSource.ToImage(session, frame), size);
            if (animation is null)
            {
                animation = image;
                animation.Frames[0].Metadata.Delay = delay;
                return;
            }

            using (image)
            {
                animation.Frames.Add(image.Frames[0]).Metadata.Delay = delay;
            }
        }

        try
        {
            foreach (YuvVideoFrame frame in source.Frames())
            {
                progress.Frame(frame.Timestamp);
                while (Time(step) < end && frame.Timestamp.Seconds > Time(step) + 1e-6)
                {
                    Add(held ?? frame);
                }

                if (Time(step) >= end)
                {
                    frame.Dispose();
                    break;
                }

                held?.Dispose();
                held = frame;
            }

            if (held is not null)
            {
                double stop = Math.Min(end, held.Timestamp.Seconds + (held.Duration is { Ticks: > 0 } d ? d.Seconds : sourceStep));
                while (Time(step) < stop - 1e-6)
                {
                    Add(held);
                }
            }

            progress.Finish();
            return animation ?? throw new UsageException($"{input.Display}: no frames between --from and --to.");
        }
        catch
        {
            animation?.Dispose();
            throw;
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>A time option in seconds; a frame number is found among the file's frame times.</summary>
    private static double? Seconds(VideoSession session, InputFile input, FrameSource source, TimeValue? value, string option)
    {
        if (value is not { } time)
        {
            return null;
        }

        if (time.IsFrame && input.IsStdin)
        {
            throw new UsageException($"{option} {time.Text} counts frames, which needs a file; give a time for standard input.");
        }

        return time.Resolve(() => MediaIO.FrameTimes(session, input, source.Track.Number), input.Display, option).Seconds;
    }

    private static JobProgress Progress(VideoSession session, InputFile input, FrameSource source, int count)
    {
        return new JobProgress(session.ProgressSink?.For(input, input.Index, count), source.Info.Duration, [source.Track], session.Cancellation);
    }

    /// <summary>The shape of a contact sheet.</summary>
    /// <param name="Columns">Frames per row.</param>
    /// <param name="Rows">Rows of frames.</param>
    /// <param name="Width">The sheet's width in pixels.</param>
    /// <param name="Timestamps">Whether each frame's time is written under it.</param>
    /// <param name="Title">Whether a title is written above the frames.</param>
    private sealed record SheetLayout(int Columns, int Rows, int Width, bool Timestamps, bool Title);
}
