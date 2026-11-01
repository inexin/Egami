using System.CommandLine;
using System.Globalization;

using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Operations;
using Egami.Pixels;
using Egami.Video.Cli.Host;
using Egami.Video.Codecs;
using Egami.Video.Editing;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// V9's commands (Build Plan tasks 11 and 17, Design Q13): <c>join</c>, <c>fade</c>, <c>overlay</c>, <c>pip</c>,
/// <c>stack</c>, <c>speed</c>, <c>reverse</c>, <c>loop</c>, <c>freeze</c>, <c>slideshow</c> and <c>edit</c>. Each builds a <see cref="VideoEdit"/> and renders it, so the CLI and the library always
/// match; <c>--save-edit</c> writes that edit as the JSON file <c>edit</c> runs.
/// </summary>
internal static class EditCommands
{
    public static Command Join(VideoSession session)
    {
        Common common = new();
        Argument<string[]> inputs = new("inputs") { Description = "The clips, in order.", Arity = ArgumentArity.OneOrMore };
        Option<string?> transition = new("--transition") { Description = "The transition between clips, as kind:duration, such as dissolve:1s or wipeleft:0.5; kinds: " + TransitionNames + ". A cut by default.", HelpName = "kind:time" };
        Command command = new("join", "Join clips of any size, frame rate and audio into one, fitted to one canvas, with an optional transition between them; everything is encoded again. For clips that already match, concat copies without decoding.");
        command.Arguments.Add(inputs);
        command.Options.Add(transition);
        common.AddTo(command);
        command.SetAction(result =>
        {
            string[] files = result.GetValue(inputs) ?? [];
            if (files.Length < 2)
            {
                throw new UsageException("join takes two or more clips, such as join a.mp4 b.mkv -o joined.webm.");
            }

            Transition? between = result.GetValue(transition) is { } t ? ParseTransition(t) : null;
            VideoEdit edit = new();
            foreach (string file in files)
            {
                edit.Track.Add(Clip.Open(session.ResolvePath(file)), edit.Track.Count == 0 ? null : between);
            }

            return common.Run(session, result, edit);
        });
        return command;
    }

    public static Command Fade(VideoSession session)
    {
        Common common = new();
        Argument<string> input = new("input") { Description = "The clip." };
        Option<string?> fadeIn = new("--in") { Description = "Fade in from the colour over this long at the start, such as 1s.", HelpName = "time" };
        Option<string?> fadeOut = new("--out") { Description = "Fade out to the colour over this long at the end.", HelpName = "time" };
        Option<string?> color = new("--color") { Description = "The colour, as a name or hex such as #000000. Default black.", HelpName = "color" };
        Command command = new("fade", "Fade a clip in from a colour and out to it, picture and sound together; like ffmpeg's fade and afade.");
        command.Arguments.Add(input);
        command.Options.Add(fadeIn);
        command.Options.Add(fadeOut);
        command.Options.Add(color);
        common.AddTo(command);
        command.SetAction(result =>
        {
            if (result.GetValue(fadeIn) is null && result.GetValue(fadeOut) is null)
            {
                throw new UsageException("fade needs --in, --out or both, such as --in 1s --out 2s.");
            }

            Rgba8 c = result.GetValue(color) is { } text ? ParseColor(text, "--color") : Rgba8.Black;
            Clip clip = Clip.Open(session.ResolvePath(result.GetValue(input)!));
            if (result.GetValue(fadeIn) is { } i)
            {
                clip = clip.FadeIn(Duration(i, "--in"), c);
            }

            if (result.GetValue(fadeOut) is { } o)
            {
                clip = clip.FadeOut(Duration(o, "--out"), c);
            }

            VideoEdit edit = new();
            edit.Track.Add(clip);
            return common.Run(session, result, edit);
        });
        return command;
    }

    public static Command Overlay(VideoSession session)
    {
        Common common = new();
        LayerOptions layer = new();
        Argument<string> main = new("main") { Description = "The clip underneath, or a still image's video." };
        Argument<string> over = new("over") { Description = "The clip drawn over it." };
        Option<string?> at = new("--at") { Description = "Where the top-left corner goes, as x,y in pixels, or right-N,top-N and the like from the edges (left, right, top, bottom, center). Centred by default.", HelpName = "x,y" };
        Option<double?> scale = new("--scale") { Description = "The overlay's size as a factor of its own. Default 1.", HelpName = "factor" };
        Option<string?> key = new("--key") { Description = "Chroma key the overlay: auto (the screen colour read from its first frame), green, blue or a hex colour such as #00b140.", HelpName = "color" };
        Option<float?> similarity = new("--similarity") { Description = "With --key: how close to the colour is keyed out, from 0 to 1. Default 0.05.", HelpName = "0-1" };
        Option<float?> blend = new("--blend") { Description = "With --key: the soft edge beyond --similarity, from 0 to 1. Default 0.35.", HelpName = "0-1" };
        Option<float?> despill = new("--despill") { Description = "With --key: how much of the screen's colour to take out of the edges, from 0 to 1. Default 0.5.", HelpName = "0-1" };
        Command command = new("overlay", "Draw one clip over another, such as a logo or a keyed presenter, with an opacity and a time window; like ffmpeg's overlay (with chromakey and despill for --key).");
        command.Arguments.Add(main);
        command.Arguments.Add(over);
        command.Options.Add(at);
        command.Options.Add(scale);
        command.Options.Add(key);
        command.Options.Add(similarity);
        command.Options.Add(blend);
        command.Options.Add(despill);
        layer.AddTo(command);
        common.AddTo(command);
        command.SetAction(result =>
        {
            Layer l = Layer.Of(Clip.Open(session.ResolvePath(result.GetValue(over)!)));
            double factor = result.GetValue(scale) ?? 1;
            if (!(factor > 0))
            {
                throw new UsageException("--scale must be above 0.");
            }

            l = result.GetValue(at) is { } place ? Place(l, place, factor, session, result.GetValue(over)!) : l.Place(Anchor.Center, 0, factor);
            if (result.GetValue(key) is { } k)
            {
                bool auto = k.Equals("auto", StringComparison.OrdinalIgnoreCase);
                Rgba8 color = auto ? ScreenColor(session.ResolvePath(result.GetValue(over)!), session) : KeyColor(k);

                // With the screen's own colour the soft edge ends short of gray, so grays and whites stay solid however pale the
                // screen is; given values are taken as they are.
                float s = Fraction(result.GetValue(similarity), 0.05f, "--similarity");
                float b = Fraction(result.GetValue(blend), auto ? Math.Clamp((0.75f * NeutralDistance(color)) - s, 0.05f, 0.35f) : 0.35f, "--blend");
                l = l.Key(new ChromaKey(color)
                {
                    Similarity = s,
                    Blend = b,
                    Despill = Fraction(result.GetValue(despill), 0.5f, "--despill"),
                });
            }
            else if (result.GetValue(similarity) is not null || result.GetValue(blend) is not null || result.GetValue(despill) is not null)
            {
                throw new UsageException("--similarity, --blend and --despill go with --key.");
            }

            VideoEdit edit = new();
            edit.Track.Add(Clip.Open(session.ResolvePath(result.GetValue(main)!)));
            edit.Layers.Add(layer.Apply(l, result));
            return common.Run(session, result, edit);
        });
        return command;
    }

    public static Command Pip(VideoSession session)
    {
        Common common = new();
        LayerOptions layer = new();
        Argument<string> main = new("main") { Description = "The main clip." };
        Argument<string> inset = new("inset") { Description = "The clip shown small in a corner." };
        Option<string?> corner = new("--corner") { Description = "The corner: top-left, top-right, bottom-left or bottom-right (the default).", HelpName = "corner" };
        Option<double?> scale = new("--scale") { Description = "The inset's width as a fraction of the canvas. Default 0.3.", HelpName = "fraction" };
        Option<int?> margin = new("--margin") { Description = "The space to the canvas edges in pixels. Default 16.", HelpName = "pixels" };
        Command command = new("pip", "Show a second clip small in a corner of the first (picture-in-picture), with a time window and fades.");
        command.Arguments.Add(main);
        command.Arguments.Add(inset);
        command.Options.Add(corner);
        command.Options.Add(scale);
        command.Options.Add(margin);
        layer.AddTo(command);
        common.AddTo(command);
        command.SetAction(result =>
        {
            Anchor anchor = (result.GetValue(corner) ?? "bottom-right").ToLowerInvariant() switch
            {
                "top-left" => Anchor.TopLeft,
                "top-right" => Anchor.TopRight,
                "bottom-left" => Anchor.BottomLeft,
                "bottom-right" => Anchor.BottomRight,
                string other => throw new UsageException($"--corner '{other}' is not top-left, top-right, bottom-left or bottom-right."),
            };
            double fraction = result.GetValue(scale) ?? 0.3;
            if (!(fraction > 0 && fraction <= 1))
            {
                throw new UsageException("--scale must be above 0 and at most 1.");
            }

            VideoEdit edit = new();
            edit.Track.Add(Clip.Open(session.ResolvePath(result.GetValue(main)!)));
            EditPlan plan = common.Canvas(session, result, edit) is { } canvas ? Preview(edit, canvas) : edit.Plan(session.Context);
            Clip insetClip = Clip.Open(session.ResolvePath(result.GetValue(inset)!));
            VideoTrackInfo track = MediaFile.Probe(session.ResolvePath(result.GetValue(inset)!), session.Context).VideoTrack
                ?? throw new UsageException($"{result.GetValue(inset)} has no video.");
            double factor = fraction * plan.Canvas.Width / track.Width;
            edit.Layers.Add(layer.Apply(Layer.Of(insetClip).Place(anchor, result.GetValue(margin) ?? 16, factor), result));
            return common.Run(session, result, edit);
        });
        return command;
    }

    public static Command StackCommand(VideoSession session)
    {
        Common common = new();
        Argument<string[]> inputs = new("inputs") { Description = "The clips, in order.", Arity = ArgumentArity.OneOrMore };
        Option<bool> vertical = new("--vertical") { Description = "One above the other, like vstack, instead of side by side." };
        Option<string?> grid = new("--grid") { Description = "A grid of columns by rows, such as 2x2, filled row by row.", HelpName = "CxR" };
        Option<int?> gap = new("--gap") { Description = "The gap between clips in pixels. Default 0.", HelpName = "pixels" };
        Option<bool> shortest = new("--shortest") { Description = "End with the shortest clip instead of the longest." };
        Option<bool> mixAudio = new("--mix-audio") { Description = "Mix every clip's audio instead of keeping the first clip's." };
        Command command = new("stack", "Show clips side by side, one above the other, or in a grid; like ffmpeg's hstack, vstack and xstack.");
        command.Arguments.Add(inputs);
        command.Options.Add(vertical);
        command.Options.Add(grid);
        command.Options.Add(gap);
        command.Options.Add(shortest);
        command.Options.Add(mixAudio);
        common.AddTo(command);
        command.SetAction(result =>
        {
            Clip[] clips = [.. (result.GetValue(inputs) ?? []).Select(f => Common.Deinterlaced(result, Clip.Open(session.ResolvePath(f))))];
            StackOptions options = new() { Gap = result.GetValue(gap) ?? 0, Shortest = result.GetValue(shortest), MixAudio = result.GetValue(mixAudio) };
            Clip stack;
            if (result.GetValue(grid) is { } g)
            {
                Size cells = EncodeCommand.ParseSize(g);
                stack = Stack.Grid(cells.Width, cells.Height, clips, options);
            }
            else if (clips.Length < 2)
            {
                throw new UsageException("stack takes two or more clips, or one or more with --grid.");
            }
            else
            {
                stack = result.GetValue(vertical) ? Stack.Vertical(clips, options) : Stack.Horizontal(clips, options);
            }

            VideoEdit edit = new();
            edit.Track.Add(stack);
            return common.Run(session, result, edit);
        });
        return command;
    }

    public static Command SpeedCommand(VideoSession session)
    {
        Common common = new();
        Argument<string> input = new("input") { Description = "The clip." };
        Argument<double> factor = new("factor") { Description = "How much faster, from 0.1 to 10: 2 is twice as fast, 0.5 half as fast." };
        Option<bool> pitch = new("--pitch") { Description = "Let the sound's pitch follow the speed, like a tape, instead of keeping it." };
        Command command = new("speed", "Play a clip faster or slower: the picture takes the nearest frames, and the sound keeps its pitch; like ffmpeg's setpts with atempo.");
        command.Arguments.Add(input);
        command.Arguments.Add(factor);
        command.Options.Add(pitch);
        common.AddTo(command);
        command.SetAction(result =>
        {
            double f = result.GetValue(factor);
            if (!(f >= 0.1 && f <= 10))
            {
                throw new UsageException($"The speed {f.ToString(CultureInfo.InvariantCulture)} is not from 0.1 to 10.");
            }

            return One(session, result, common, Clip.Open(session.ResolvePath(result.GetValue(input)!)).Speed(f, keepPitch: !result.GetValue(pitch)));
        });
        return command;
    }

    public static Command ReverseCommand(VideoSession session)
    {
        Common common = new();
        Argument<string> input = new("input") { Description = "The clip." };
        Command command = new("reverse", "Play a clip backwards, picture and sound, holding only one keyframe interval at a time; like ffmpeg's reverse and areverse, which hold the whole clip.");
        command.Arguments.Add(input);
        common.AddTo(command);
        command.SetAction(result => One(session, result, common, Clip.Open(session.ResolvePath(result.GetValue(input)!)).Reverse()));
        return command;
    }

    public static Command LoopCommand(VideoSession session)
    {
        Common common = new();
        Argument<string> input = new("input") { Description = "The clip." };
        Option<int> count = new("--count") { Description = "How many times it plays in all. Default 2.", DefaultValueFactory = _ => 2, HelpName = "times" };
        Command command = new("loop", "Play a clip several times in a row; like ffmpeg's loop and aloop.");
        command.Arguments.Add(input);
        command.Options.Add(count);
        common.AddTo(command);
        command.SetAction(result =>
        {
            int n = result.GetValue(count);
            if (n < 1)
            {
                throw new UsageException("--count must be 1 or more.");
            }

            return One(session, result, common, Clip.Open(session.ResolvePath(result.GetValue(input)!)).Loop(n));
        });
        return command;
    }

    public static Command FreezeCommand(VideoSession session)
    {
        Common common = new();
        Argument<string> input = new("input") { Description = "The clip." };
        Option<string?> at = new("--at") { Description = "The time of the frame to freeze, or end for the last one.", HelpName = "time" };
        Option<string?> length = new("--for") { Description = "How long it stays, such as 2s. Default 1s.", HelpName = "time" };
        Command command = new("freeze", "Hold one frame of a clip still for a while, with silence, then go on; --at end holds the last frame, like ffmpeg's tpad.");
        command.Arguments.Add(input);
        command.Options.Add(at);
        command.Options.Add(length);
        common.AddTo(command);
        command.SetAction(result =>
        {
            MediaTime duration = result.GetValue(length) is { } l ? Duration(l, "--for") : MediaTime.FromSeconds(1);
            Clip clip = Clip.Open(session.ResolvePath(result.GetValue(input)!));
            string where = result.GetValue(at) ?? throw new UsageException("freeze needs --at, such as --at 0:12 or --at end.");
            clip = where.Equals("end", StringComparison.OrdinalIgnoreCase)
                ? clip.Hold(duration)
                : clip.Freeze(MediaTime.FromSeconds(TimeValue.Parse(where, "--at").Seconds), duration);
            return One(session, result, common, clip);
        });
        return command;
    }

    public static Command SlideshowCommand(VideoSession session)
    {
        Common common = new();
        Argument<string[]> images = new("images") { Description = "The images, in order (files or globs, sorted by name).", Arity = ArgumentArity.OneOrMore };
        Option<string?> each = new("--each") { Description = "How long each image shows, its transitions included. Default 4s.", HelpName = "time" };
        Option<string?> transition = new("--transition") { Description = "The transition between images, as kind:duration; fade:0.5 by default, cut for none.", HelpName = "kind:time" };
        Option<bool> panZoom = new("--pan-zoom") { Description = "Slowly pan and zoom each image (Ken Burns)." };
        Option<int> seed = new("--seed") { Description = "The seed the pan and zoom directions come from. Default 1.", DefaultValueFactory = _ => 1, HelpName = "number" };
        Command command = new("slideshow", "Make a video of images shown one after another, with transitions and an optional pan and zoom; like ffmpeg's loop=1, zoompan and xfade, but with sub-pixel motion.");
        command.Arguments.Add(images);
        command.Options.Add(each);
        command.Options.Add(transition);
        command.Options.Add(panZoom);
        command.Options.Add(seed);
        common.AddTo(command);
        command.SetAction(result =>
        {
            List<InputFile> files = Globber.Expand(session.Images, result.GetValue(images) ?? [], recursive: false);
            Transition? between = result.GetValue(transition) switch
            {
                null => Transition.Fade(MediaTime.FromSeconds(0.5)),
                "cut" => null,
                string t => ParseTransition(t),
            };
            SlideshowOptions options = new()
            {
                Each = result.GetValue(each) is { } e ? Duration(e, "--each") : MediaTime.FromSeconds(4),
                Transition = between,
                PanZoom = result.GetValue(panZoom),
                Seed = result.GetValue(seed),
            };
            VideoEdit edit;
            try
            {
                edit = Slideshow.Create(files.Select(f => f.Path!), options);
            }
            catch (ArgumentException error)
            {
                throw new UsageException(error.Message);
            }

            // The canvas comes from --size and --rate when given; the slideshow's own is the first image's size at 30 fps.
            return common.Run(session, result, edit);
        });
        return command;
    }

    public static Command StabilizeCommand(VideoSession session)
    {
        Common common = new();
        Argument<string> input = new("input") { Description = "The shaky clip." };
        Option<int?> smoothing = new("--smoothing") { Description = "How many frames either way the camera path is smoothed over: more keeps only slow intended motion. Default 15.", HelpName = "frames" };
        Option<string?> edges = new("--edges") { Description = "What fills the edges a correction uncovers: zoom (the default, the least zoom that hides them), black or blur.", HelpName = "mode" };
        Option<string?> zoom = new("--zoom") { Description = "auto (the default) zooms to hide the edges, the same as --edges zoom; none keeps the size and fills them with black.", HelpName = "auto|none" };
        Option<string?> motion = new("--motion") { Description = "Use a camera motion saved by --save-motion instead of measuring it again.", HelpName = "file.json" };
        Option<string?> saveMotion = new("--save-motion") { Description = "Write the measured camera motion (pass 1) to a JSON file; with no -o, only that.", HelpName = "file.json" };
        Command command = new("stabilize", "Take the shake out of a clip in two passes: measure the camera's motion (corners tracked with Lucas-Kanade, a RANSAC fit), smooth its path with a Gaussian, and warp each frame onto it; like ffmpeg's vidstabdetect and vidstabtransform.");
        command.Arguments.Add(input);
        command.Options.Add(smoothing);
        command.Options.Add(edges);
        command.Options.Add(zoom);
        command.Options.Add(motion);
        command.Options.Add(saveMotion);
        common.AddTo(command);
        command.SetAction(result =>
        {
            string path = session.ResolvePath(result.GetValue(input)!);
            StabilizeEdges mode = (result.GetValue(edges), result.GetValue(zoom)) switch
            {
                (null, null or "auto") => StabilizeEdges.Zoom,
                (null, "none") => StabilizeEdges.Black,
                (null, string z) => throw new UsageException($"--zoom '{z}' is not auto or none."),
                (string e, null) => e.ToLowerInvariant() switch
                {
                    "zoom" => StabilizeEdges.Zoom,
                    "black" => StabilizeEdges.Black,
                    "blur" => StabilizeEdges.Blur,
                    _ => throw new UsageException($"--edges '{e}' is not zoom, black or blur."),
                },
                _ => throw new UsageException("Give --edges or --zoom, not both."),
            };
            int frames = result.GetValue(smoothing) ?? 15;
            if (frames < 0)
            {
                throw new UsageException("--smoothing must be 0 or more.");
            }

            CameraMotion camera;
            if (result.GetValue(motion) is { } saved)
            {
                try
                {
                    camera = CameraMotion.FromJson(File.ReadAllText(session.ResolvePath(saved)));
                }
                catch (FormatException error)
                {
                    throw new UsageException($"--motion {saved}: {error.Message}");
                }
            }
            else
            {
                InputFile display = new(path, result.GetValue(input)!, Path.GetDirectoryName(path) ?? ".", 0);
                camera = Stabilizer.Analyze(path, progress: session.ProgressSink?.For(display, 0, 1), cancellationToken: session.Cancellation);
                session.Info($"measured the camera's motion over {camera.Frames.Count} frames");
            }

            if (result.GetValue(saveMotion) is { } file)
            {
                string target = session.ResolvePath(file);
                File.WriteAllText(target, camera.ToJson());
                session.Written.Enqueue(target);
                session.Info($"wrote {file}");
                if (result.GetValue(common.OutputOption) is null && result.GetValue(common.SaveEditOption) is null)
                {
                    return ExitCodes.Success;
                }
            }

            return One(session, result, common, Clip.Open(path).Stabilize(camera, new StabilizeOptions { Smoothing = frames, Edges = mode }));
        });
        return command;
    }

    public static Command Edit(VideoSession session)
    {
        Common common = new();
        Argument<string> file = new("file") { Description = "The edit file: JSON as --save-edit writes it, with effects as egami run steps such as \"blur 8\"." };
        Command command = new("edit", "Render an edit file: clips on a track with transitions, layers over it and effects with time windows. Every other editing command writes one with --save-edit.");
        command.Arguments.Add(file);
        common.AddTo(command);
        command.SetAction(result =>
        {
            string path = session.ResolvePath(result.GetValue(file)!);
            VideoEdit edit;
            try
            {
                // Clip paths in the file are relative to the file's folder.
                edit = VideoEdit.FromJson(File.ReadAllText(path), Effect, Path.GetDirectoryName(path));
            }
            catch (FormatException error)
            {
                throw new UsageException($"{result.GetValue(file)}: {error.Message}");
            }

            return common.Run(session, result, edit);
        });
        return command;
    }

    private static int One(VideoSession session, ParseResult result, Common common, Clip clip)
    {
        VideoEdit edit = new();
        edit.Track.Add(clip);
        return common.Run(session, result, edit);
    }

    /// <summary>Turns an effect's name, egami run step text such as <c>blur 8 + sharpen</c>, into an effect that runs the steps.</summary>
    internal static EditEffect Effect(string text)
    {
        RunCommand.Parsed parsed = RunCommand.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (parsed.Inputs.Count > 0 || parsed.Steps.Count == 0)
        {
            throw new FormatException($"The effect \"{text}\" is not egami run steps, such as \"blur 8\".");
        }

        List<BoundStep> steps = [];
        foreach (RunCommand.ParsedStep step in parsed.Steps)
        {
            steps.Add(RunCommand.Bind(step) is StepItem item ? item.Step : throw new FormatException($"\"{step.Name}\" in the effect \"{text}\" is not an image step."));
        }

        if (RunSteps.ChangesSize(steps))
        {
            throw new FormatException($"The effect \"{text}\" changes the frame size; edit effects must keep it.");
        }

        return EditEffect.Of(new StepEffect(steps), text);
    }

    private static string TransitionNames => "fade, fadeblack, fadewhite, dissolve, wipeleft, wiperight, wipeup, wipedown, slideleft, slideright, slideup, slidedown, circleopen, circleclose, clock, zoom, pixelize, blur";

    internal static Transition ParseTransition(string text)
    {
        string[] parts = text.Split(':', 2);
        MediaTime duration = parts.Length == 2 ? Duration(parts[1], "--transition") : MediaTime.FromSeconds(1);
        return parts[0].ToLowerInvariant() switch
        {
            "fade" or "crossfade" => Transition.Fade(duration),
            "fadeblack" => Transition.FadeThrough(Rgba8.Black, duration),
            "fadewhite" => Transition.FadeThrough(Rgba8.White, duration),
            "dissolve" => Transition.Dissolve(duration),
            "wipeleft" => Transition.Wipe(TransitionDirection.Left, duration),
            "wiperight" => Transition.Wipe(TransitionDirection.Right, duration),
            "wipeup" => Transition.Wipe(TransitionDirection.Up, duration),
            "wipedown" => Transition.Wipe(TransitionDirection.Down, duration),
            "slideleft" => Transition.Slide(TransitionDirection.Left, duration),
            "slideright" => Transition.Slide(TransitionDirection.Right, duration),
            "slideup" => Transition.Slide(TransitionDirection.Up, duration),
            "slidedown" => Transition.Slide(TransitionDirection.Down, duration),
            "circleopen" => Transition.CircleOpen(duration),
            "circleclose" => Transition.CircleClose(duration),
            "clock" or "radial" => Transition.Clock(duration),
            "zoom" or "zoomin" => Transition.Zoom(duration),
            "pixelize" => Transition.Pixelize(duration),
            "blur" or "hblur" => Transition.Blur(duration),
            string other => throw new UsageException($"--transition '{other}' is not a transition; use one of {TransitionNames}."),
        };
    }

    private static MediaTime Duration(string text, string option)
    {
        TimeValue value = TimeValue.ParseDuration(text.TrimEnd('s'), option, allowFrames: false);
        return MediaTime.FromSeconds(value.Seconds);
    }

    internal static Rgba8 ParseColor(string text, string option) => Rgba8.TryFromHex(text, out Rgba8 hex)
        ? hex
        : text.ToLowerInvariant() switch
        {
            "black" => Rgba8.Black,
            "white" => Rgba8.White,
            "gray" or "grey" => Rgba8.Gray,
            "red" => Rgba8.Red,
            "green" => Rgba8.Green,
            "blue" => Rgba8.Blue,
            _ => throw new UsageException($"{option} '{text}' is not a colour: give black, white, gray, red, green, blue or a hex colour such as #202020."),
        };

    private static Rgba8 KeyColor(string text) => text.ToLowerInvariant() switch
    {
        "green" => Rgba8.FromHex("#00b140"),
        "blue" => Rgba8.FromHex("#0047bb"),
        _ => Rgba8.TryFromHex(text, out Rgba8 c) ? c : throw new UsageException($"--key '{text}' is not green, blue or a hex colour such as #00b140."),
    };

    /// <summary>
    /// The colour of a green or blue screen: the median, per channel, of the first frame's pixels whose green (or blue) stands
    /// well above both other channels, from whichever of the two covers more of the frame. A real screen is seldom the pure
    /// colour --key green names, and keying its actual colour keeps grays and whites solid.
    /// </summary>
    private static Rgba8 ScreenColor(string path, VideoSession session)
    {
        using VideoReader reader = VideoReader.Open(path, null, session.Context);
        using YuvVideoFrame frame = reader.ReadFrame() ?? throw new UsageException($"--key auto: '{path}' has no frame to read the screen colour from.");
        using Raster<Rgb8> rgb = frame.ToRaster<Rgb8>(session.Context.Egami);
        int[][] green = [new int[256], new int[256], new int[256]], blue = [new int[256], new int[256], new int[256]];
        int greens = 0, blues = 0;
        for (int y = 0; y < rgb.Height; y++)
        {
            foreach (Rgb8 p in rgb.Row(y))
            {
                if (p.G - Math.Max(p.R, p.B) > 40)
                {
                    greens++;
                    Count(green, p);
                }
                else if (p.B - Math.Max(p.R, p.G) > 40)
                {
                    blues++;
                    Count(blue, p);
                }
            }
        }

        int count = Math.Max(greens, blues);
        if (count < (long)rgb.Width * rgb.Height / 20)
        {
            throw new UsageException($"--key auto: the first frame of '{path}' shows no green or blue screen; give its colour, such as --key #00b140.");
        }

        int[][] histograms = greens >= blues ? green : blue;
        return new Rgba8(Median(histograms[0], count), Median(histograms[1], count), Median(histograms[2], count), 255);

        static void Count(int[][] h, Rgb8 p)
        {
            h[0][p.R]++;
            h[1][p.G]++;
            h[2][p.B]++;
        }

        static byte Median(int[] histogram, int count)
        {
            int seen = 0;
            for (int v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (2 * seen >= count)
                {
                    return (byte)v;
                }
            }

            return 255;
        }
    }

    /// <summary>
    /// How far a colour's chroma lies from gray, as a fraction of the largest chroma distance (green to magenta), the scale
    /// ChromaKey's similarity and blend use: Rec. 709 Cb and Cr.
    /// </summary>
    private static float NeutralDistance(Rgba8 color)
    {
        const float Kr = 0.2126f, Kb = 0.0722f, Kg = 1 - Kr - Kb;
        const float CbScale = 1 / (2 * (1 - Kb)), CrScale = 1 / (2 * (1 - Kr));
        float r = color.R / 255f, g = color.G / 255f, bl = color.B / 255f, y = (Kr * r) + (Kg * g) + (Kb * bl);
        float cb = (bl - y) * CbScale, cr = (r - y) * CrScale;
        float max = 2 * MathF.Sqrt((Kg * CbScale * Kg * CbScale) + (Kg * CrScale * Kg * CrScale));
        return MathF.Sqrt((cb * cb) + (cr * cr)) / max;
    }

    private static float Fraction(float? value, float fallback, string option) => value is null
        ? fallback
        : value is >= 0 and <= 1 ? value.Value : throw new UsageException($"{option} must be from 0 to 1.");

    /// <summary>Places a layer from --at: x,y in pixels, or edge-relative forms such as right-10,top-10.</summary>
    private static Layer Place(Layer layer, string text, double scale, VideoSession session, string over)
    {
        string[] parts = text.Split(',');
        if (parts.Length != 2)
        {
            throw new UsageException($"--at '{text}' is not x,y, such as 10,20 or right-10,top-10.");
        }

        (string xs, string ys) = (parts[0].Trim().ToLowerInvariant(), parts[1].Trim().ToLowerInvariant());
        if (int.TryParse(xs, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) && int.TryParse(ys, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            VideoTrackInfo track = MediaFile.Probe(session.ResolvePath(over), session.Context).VideoTrack ?? throw new UsageException($"{over} has no video.");
            return layer.Place(new Rect(x, y, Math.Max(1, (int)Math.Round(track.Width * scale)), Math.Max(1, (int)Math.Round(track.Height * scale))));
        }

        (string hEdge, int hMargin) = Edge(xs, "left", "right", "center");
        (string vEdge, int vMargin) = Edge(ys, "top", "bottom", "center");
        Anchor anchor = (hEdge, vEdge) switch
        {
            ("left", "top") => Anchor.TopLeft,
            ("right", "top") => Anchor.TopRight,
            ("left", "bottom") => Anchor.BottomLeft,
            ("right", "bottom") => Anchor.BottomRight,
            ("left", _) => Anchor.Left,
            ("right", _) => Anchor.Right,
            (_, "top") => Anchor.Top,
            (_, "bottom") => Anchor.Bottom,
            _ => Anchor.Center,
        };
        return layer.Place(anchor, Math.Max(hMargin, vMargin), scale);

        (string, int) Edge(string value, string start, string end, string middle)
        {
            foreach (string edge in new[] { start, end, middle })
            {
                if (value == edge)
                {
                    return (edge, 0);
                }

                if (value.StartsWith(edge + "-", StringComparison.Ordinal) && int.TryParse(value[(edge.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int margin))
                {
                    return (edge, margin);
                }
            }

            throw new UsageException($"--at '{text}': '{value}' is not a number or {start}, {end} or {middle} with an optional -margin.");
        }
    }

    private static EditPlan Preview(VideoEdit edit, EditCanvas canvas)
    {
        edit.Canvas = canvas;
        return edit.Plan();
    }

    /// <summary>Runs egami run steps on a frame in place.</summary>
    private sealed class StepEffect(List<BoundStep> steps) : IFrameEffect
    {
        public void Apply(Raster<Rgba8> frame, FrameEffectContext context) => RunSteps.Apply(frame, steps);
    }

    /// <summary>A layer's time window and fades, shared by overlay and pip.</summary>
    private sealed class LayerOptions
    {
        private readonly Option<string?> _from = new("--from") { Description = "When the layer appears, in the main clip's time: seconds or [HH:]MM:SS[.mmm]. Default the start.", HelpName = "time" };
        private readonly Option<string?> _to = new("--to") { Description = "When it disappears. Default when its clip ends.", HelpName = "time" };
        private readonly Option<float?> _opacity = new("--opacity") { Description = "Its opacity, from 0 to 1. Default 1.", HelpName = "0-1" };
        private readonly Option<string?> _fadeIn = new("--fade-in") { Description = "Fade it in over this long.", HelpName = "time" };
        private readonly Option<string?> _fadeOut = new("--fade-out") { Description = "Fade it out over this long.", HelpName = "time" };
        private readonly Option<bool> _hold = new("--hold") { Description = "Keep showing its last frame until --to or the end, when its clip is shorter." };
        private readonly Option<bool> _mute = new("--mute") { Description = "Leave out its audio." };

        public void AddTo(Command command)
        {
            foreach (Option option in new Option[] { _from, _to, _opacity, _fadeIn, _fadeOut, _hold, _mute })
            {
                command.Options.Add(option);
            }
        }

        public Layer Apply(Layer layer, ParseResult result)
        {
            MediaTime start = result.GetValue(_from) is { } f ? MediaTime.FromSeconds(TimeValue.Parse(f, "--from").Seconds) : MediaTime.Zero;
            MediaTime? end = result.GetValue(_to) is { } t ? MediaTime.FromSeconds(TimeValue.Parse(t, "--to").Seconds) : null;
            if (end is MediaTime e && e <= start)
            {
                throw new UsageException("--to must be after --from.");
            }

            layer = layer.Between(start, end).Opacity(Fraction(result.GetValue(_opacity), 1, "--opacity"));
            if (result.GetValue(_fadeIn) is { } fi)
            {
                layer = layer.FadeIn(Duration(fi, "--fade-in"));
            }

            if (result.GetValue(_fadeOut) is { } fo)
            {
                layer = layer.FadeOut(Duration(fo, "--fade-out"));
            }

            if (result.GetValue(_hold))
            {
                layer = layer.HoldLastFrame();
            }

            return result.GetValue(_mute) ? layer with { Clip = layer.Clip.Muted() } : layer;
        }
    }

    /// <summary>The canvas, encoder and output options every editing command takes.</summary>
    private sealed class Common
    {
        private readonly Option<string?> _output = new("--output", "-o") { Description = "The file to write; its extension names the container (webm, mkv, mp4, avi).", HelpName = "path" };
        private readonly Option<string?> _size = new("--size") { Description = "The canvas size, such as 1920x1080. Default the first clip's.", HelpName = "WxH" };
        private readonly Option<string?> _rate = new("--rate") { Description = "The frame rate, such as 30 or 30000/1001. Default the first clip's.", HelpName = "fps" };
        private readonly Option<string?> _fit = new("--fit") { Description = "How clips of another size fit the canvas: contain (the default, with bars), cover (cropped), stretch or none.", HelpName = "mode" };
        private readonly Option<string?> _background = new("--background") { Description = "The colour behind clips that do not cover the canvas. Default black.", HelpName = "color" };
        private readonly Option<string?> _codec = new("--codec", "-c") { Description = "The video codec: vp9 (the default for webm), vp8, av1, mjpeg (the default otherwise) or raw.", HelpName = "codec" };
        private readonly Option<int?> _quality = new("--quality") { Description = "Quality from 0 to 100.", HelpName = "0-100" };
        private readonly Option<bool> _noAudio = new("--no-audio", "-an") { Description = "Write no audio track." };
        private readonly Option<bool> _smart = new("--smart") { Description = "Copy the untouched parts of clips instead of encoding them again (smart render), where the source matches the canvas and the encoder's stream." };
        public Option<string?> OutputOption => _output;

        public Option<string?> SaveEditOption => _saveEdit;

        private readonly Option<string?> _saveEdit = new("--save-edit") { Description = "Also write the edit as a JSON file that 'egami-video edit' renders; with no -o, write only the file.", HelpName = "file.json" };

        public void AddTo(Command command)
        {
            foreach (Option option in new Option[] { _output, _size, _rate, _fit, _background, _codec, _quality, _noAudio, _smart, _saveEdit })
            {
                command.Options.Add(option);
            }

            Deinterlacing.AddTo(command);
        }

        /// <summary>The clip deinterlaced as --deinterlace asks, unless it is not from a file or has its own setting.</summary>
        public static Clip Deinterlaced(ParseResult result, Clip clip) =>
            Deinterlacing.Parse(result) is { } options && clip.Path is not null && clip.Deinterlacing is null ? clip.Deinterlace(options) : clip;

        public EditCanvas? Canvas(VideoSession session, ParseResult result, VideoEdit edit)
        {
            string? size = result.GetValue(_size), rate = result.GetValue(_rate), fit = result.GetValue(_fit), background = result.GetValue(_background);
            if (edit.Canvas is null && size is null && rate is null && fit is null && background is null)
            {
                return null;
            }

            EditCanvas canvas = edit.Canvas ?? edit.Plan(session.Context).Canvas;
            if (size is not null)
            {
                Size s = EncodeCommand.ParseSize(size);
                canvas = canvas with { Width = s.Width, Height = s.Height };
            }

            if (rate is not null)
            {
                canvas = canvas with { FrameRate = EncodeCommand.ParseRate(rate) };
            }

            if (fit is not null)
            {
                canvas = canvas with
                {
                    Fit = fit.ToLowerInvariant() switch
                    {
                        "contain" => ClipFit.Contain,
                        "cover" => ClipFit.Cover,
                        "stretch" => ClipFit.Stretch,
                        "none" => ClipFit.None,
                        _ => throw new UsageException($"--fit '{fit}' is not contain, cover, stretch or none."),
                    },
                };
            }

            if (background is not null)
            {
                canvas = canvas with { Background = ParseColor(background, "--background") };
            }

            return canvas;
        }

        public int Run(VideoSession session, ParseResult result, VideoEdit edit)
        {
            for (int i = 0; i < edit.Track.Count; i++)
            {
                edit.Track.Set(i, edit.Track[i] with { Clip = Deinterlaced(result, edit.Track[i].Clip) });
            }

            for (int i = 0; i < edit.Layers.Count; i++)
            {
                edit.Layers[i] = edit.Layers[i] with { Clip = Deinterlaced(result, edit.Layers[i].Clip) };
            }

            if (Canvas(session, result, edit) is { } canvas)
            {
                edit.Canvas = canvas;
            }

            string? output = result.GetValue(_output);
            if (result.GetValue(_saveEdit) is { } save)
            {
                string json;
                try
                {
                    json = edit.ToJson();
                }
                catch (NotSupportedException error)
                {
                    throw new UsageException(error.Message);
                }

                string path = session.ResolvePath(save);
                File.WriteAllText(path, json);
                session.Written.Enqueue(path);
                session.Info($"wrote {save}");
                if (output is null)
                {
                    return ExitCodes.Success;
                }
            }

            if (output is null)
            {
                throw new UsageException("No output: give -o with the file to write, such as -o out.webm, or --save-edit to write the edit file only.");
            }

            string target = session.ResolvePath(output);
            string extension = Path.GetExtension(target).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0)
            {
                throw new UsageException($"-o '{output}' has no extension to name the container, such as .webm or .mkv.");
            }

            ContainerCheck(session, extension);
            VideoCodec codec = EncodeCommand.FindCodec(session, result.GetValue(_codec) ?? (extension == "webm" ? "vp9" : null), MediaIO.FindContainer(session, extension));
            int? quality = result.GetValue(_quality) is { } q ? (q is >= 0 and <= 100 ? Math.Max(1, q) : throw new UsageException("--quality must be from 0 to 100.")) : null;
            EditRenderOptions options = new() { Writer = new VideoWriterOptions { Codec = codec, Quality = quality }, Audio = !result.GetValue(_noAudio), SmartRender = result.GetValue(_smart), Context = session.Context };
            EditPlan? plan = null;
            InputFile display = new(target, output, Path.GetDirectoryName(target) ?? ".", 0);
            try
            {
                MediaIO.Write(session, target, stream =>
                {
                    plan = edit.Render(stream, extension, options, session.ProgressSink?.For(display, 0, 1), session.Cancellation);
                });
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith("The edit cannot render", StringComparison.Ordinal))
            {
                throw new UsageException(error.Message);
            }

            foreach (string warning in plan!.Warnings)
            {
                session.Warn(warning);
            }

            session.Info($"{plan.FrameCount} frames at {plan.Canvas.FrameRate} fps, {plan.Canvas.Width}x{plan.Canvas.Height}, {TimeValue.Format(plan.Duration)}");
            return ExitCodes.Success;
        }

        private static void ContainerCheck(VideoSession session, string extension)
        {
            if (extension is "y4m" or "ivf" or "obu" or "wav" or "flac" or "opus" or "mp3")
            {
                throw new UsageException($"An edit writes video and audio; .{extension} cannot hold both. Write webm, mkv, mp4 or avi.");
            }

            _ = MediaIO.FindContainer(session, extension);
        }
    }
}
