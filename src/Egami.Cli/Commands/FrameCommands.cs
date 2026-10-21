using System.CommandLine;
using System.Globalization;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Formats;
using Egami.Pixels;

namespace Egami.Cli.Commands;

/// <summary><c>egami frames split</c> and <c>egami frames join</c> (stage C): animations to stills and back.</summary>
internal static class FrameCommands
{
    public static Command Create(CliSession session)
    {
        Command frames = new("frames", "Split an animation (GIF, APNG, WebP) into still images, or join stills into one.");
        frames.Subcommands.Add(Split(session));
        frames.Subcommands.Add(Join(session));
        return frames;
    }

    private static Command Split(CliSession session)
    {
        Argument<string> input = new("input") { Description = "The animation, or - for standard input." };
        Option<string> output = new("--output", "-o")
        {
            Description = "A template for each frame's file: {frame} or {index} is the frame number from 0, padded as in {frame:000}; {name} is the input's name.",
            HelpName = "template",
            Required = true,
        };
        Option<string?> format = new("--format", "-f") { Description = "The output format, when the template's extension does not say.", HelpName = "format" };
        Option<string[]> set = new("--set") { Description = "Writer settings as fmt.key=value.", HelpName = "fmt.key=value" };
        Option<string?> delays = new("--delays") { Description = "Also write each frame's delay in milliseconds, one per line, to this file, so 'frames join --delays' can restore the timing.", HelpName = "path" };
        Command command = new("split", "Write every frame of an animation as its own image, each a full picture as the animation shows it.");
        command.Arguments.Add(input);
        command.Options.Add(output);
        command.Options.Add(format);
        command.Options.Add(set);
        command.Options.Add(delays);
        command.SetAction(result =>
        {
            OutputTemplate template = OutputTemplate.Parse(result.GetValue(output)!);
            if (!template.NamesFrames)
            {
                throw new UsageException($"The output '{template.Text}' needs {{frame}} so each frame gets its own file, such as frames/{{frame:000}}.png.");
            }

            InputFile file = Globber.Expand(session, [result.GetValue(input)!], recursive: false)[0];
            InputSource source = new(file);
            using Raster animation = source.Load(session);
            Raster frames = animation is Raster<Rgba8> or Raster<Rgba16> ? animation : animation.As<Rgba16>();
            try
            {
                SaveSpec save = new(null, result.GetValue(format), ImageOptions.SetValues(result.GetValue(set)), new Dictionary<string, string>());
                FormatInfo outputFormat = save.Format is { } name
                    ? Runner.FindFormat(session, name) ?? throw new UsageException($"'{name}' is not a format egami knows; 'egami formats' lists them.")
                    : Runner.FindFormat(session, Path.GetExtension(template.Text)) ?? throw new UsageException($"The output '{template.Text}' has no extension egami writes; give --format.");
                if (outputFormat.Writer is null)
                {
                    throw new UsageException($"egami can read {outputFormat.Name} but not write it.");
                }

                WriterOptions options = Runner.Options(session, save, outputFormat);
                List<string> lines = [];
                for (int i = 0; i < frames.FrameCount; i++)
                {
                    using Raster frame = CopyFrame(frames, i);
                    string path = session.ResolvePath(template.Resolve(file with { Index = i }, outputFormat.Extensions[0], i));
                    Output.Write(session, path, stream => frame.Save(stream, outputFormat, options));
                    lines.Add(Delay(frames, i).TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
                }

                if (result.GetValue(delays) is { } delayFile)
                {
                    File.WriteAllLines(session.ResolvePath(delayFile), lines);
                }

                session.Info($"{file.Display}: {frames.FrameCount} frames");
            }
            finally
            {
                if (!ReferenceEquals(frames, animation))
                {
                    frames.Dispose();
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Join(CliSession session)
    {
        Argument<string[]> inputs = new("inputs") { Description = "The frames in order: files or globs (sorted by name).", Arity = ArgumentArity.OneOrMore };
        Option<string> output = new("--output", "-o") { Description = "The animation to write: .gif, .png (APNG) or .webp.", HelpName = "path", Required = true };
        Option<string?> format = new("--format", "-f") { Description = "The output format, when the extension does not say.", HelpName = "format" };
        Option<string?> delay = new("--delay") { Description = "How long each frame shows: 40ms, 0.04s or 40 (milliseconds). 100ms by default.", HelpName = "time" };
        Option<string?> delays = new("--delays") { Description = "A file of delays in milliseconds, one per frame, as 'frames split --delays' writes.", HelpName = "path" };
        Option<double?> fps = new("--fps") { Description = "Frames per second, instead of --delay.", HelpName = "rate" };
        Option<int> loop = new("--loop") { Description = "How many times the animation plays; 0 (the default) loops forever.", HelpName = "count" };
        Option<string[]> set = new("--set") { Description = "Writer settings as fmt.key=value.", HelpName = "fmt.key=value" };
        Option<int?> quality = new("--quality") { Description = "Lossy quality for WebP.", HelpName = "0-100" };
        Option<bool?> lossless = new("--lossless") { Description = "Lossless WebP." };
        Command command = new("join", "Join still images, all the same size, into an animation.");
        command.Arguments.Add(inputs);
        command.Options.Add(output);
        command.Options.Add(format);
        command.Options.Add(delay);
        command.Options.Add(delays);
        command.Options.Add(fps);
        command.Options.Add(loop);
        command.Options.Add(set);
        command.Options.Add(quality);
        command.Options.Add(lossless);
        command.SetAction(result =>
        {
            List<InputFile> files = Globber.Expand(session, result.GetValue(inputs) ?? [], recursive: false);
            TimeSpan each = result.GetValue(fps) is { } rate
                ? (rate > 0 && rate <= 1000 ? TimeSpan.FromMilliseconds(Math.Round(1000 / rate)) : throw new UsageException("--fps must be above 0 and at most 1000."))
                : ParseDelay(result.GetValue(delay) ?? "100ms");
            List<TimeSpan>? perFrame = result.GetValue(delays) is { } delayFile ? ReadDelays(session.ResolvePath(delayFile), files.Count) : null;
            List<Raster> stills = [];
            try
            {
                foreach (InputFile file in files)
                {
                    stills.Add(new InputSource(file).Load(session, new ReaderOptions { Frames = ..1 }));
                }

                Raster first = stills[0];
                if (stills.FirstOrDefault(s => s.Width != first.Width || s.Height != first.Height) is { } odd)
                {
                    int at = stills.IndexOf(odd);
                    throw new UsageException($"Every frame must be {first.Width}x{first.Height}; {files[at].Display} is {odd.Width}x{odd.Height}. Resize them first, for example with 'egami resize --size {first.Width}x{first.Height}!'.");
                }

                bool deep = stills.Any(s => s.Layout.BitsPerChannel > 8);
                using Raster animation = deep ? Assemble<Rgba16>(stills, each, perFrame) : Assemble<Rgba8>(stills, each, perFrame);
                animation.Metadata.LoopCount = result.GetValue(loop);
                Dictionary<string, string> common = [];
                if (result.GetValue(quality) is { } q)
                {
                    common["quality"] = q.ToString(CultureInfo.InvariantCulture);
                }

                if (result.GetValue(lossless) is { } l)
                {
                    common["lossless"] = l ? "true" : "false";
                }

                SaveSpec save = new(result.GetValue(output), result.GetValue(format), ImageOptions.SetValues(result.GetValue(set)), common);
                (_, string path, FormatInfo outputFormat) = Runner.Resolve(session, new InputSource(files[0]), save, null, Raster.Probe(files[0].Path ?? throw new UsageException("join takes files, not standard input.")).Format);
                WriterOptions options = Runner.Options(session, save, outputFormat);
                Output.Write(session, path, stream => animation.Save(stream, outputFormat, options));
                session.Info($"joined {stills.Count} frames into {path}");
                return ExitCodes.Success;
            }
            finally
            {
                foreach (Raster still in stills)
                {
                    still.Dispose();
                }
            }
        });
        return command;
    }

    /// <summary>Parses a delay: <c>40ms</c>, <c>0.04s</c> or <c>40</c> (milliseconds).</summary>
    public static TimeSpan ParseDelay(string text)
    {
        string value = text.Trim().ToLowerInvariant();
        double scale = 1;
        if (value.EndsWith("ms", StringComparison.Ordinal))
        {
            value = value[..^2];
        }
        else if (value.EndsWith('s'))
        {
            value = value[..^1];
            scale = 1000;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0 || number * scale > 655_350)
        {
            throw new UsageException($"'{text}' is not a delay; write 40ms, 0.04s or 40.");
        }

        return TimeSpan.FromMilliseconds(Math.Round(number * scale));
    }

    private static List<TimeSpan> ReadDelays(string path, int count)
    {
        List<TimeSpan> delays = [.. File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l => ParseDelay(l.Trim()))];
        if (delays.Count != count)
        {
            throw new UsageException($"{Path.GetFileName(path)} has {delays.Count} delays for {count} frames.");
        }

        return delays;
    }

    private static Raster<TPixel> Assemble<TPixel>(List<Raster> stills, TimeSpan each, List<TimeSpan>? perFrame)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> animation = stills[0] as Raster<TPixel> is { } same ? same.CopyFrame(0) : stills[0].As<TPixel>();
        animation.Frames[0].Metadata.Delay = perFrame?[0] ?? each;
        for (int i = 1; i < stills.Count; i++)
        {
            using Raster<TPixel>? converted = stills[i] is Raster<TPixel> ? null : stills[i].As<TPixel>();
            Raster<TPixel> source = converted ?? (Raster<TPixel>)stills[i];
            animation.Frames.Add(source.Frames[0]).Metadata.Delay = perFrame?[i] ?? each;
        }

        return animation;
    }

    private static Raster CopyFrame(Raster raster, int index)
    {
        return raster switch
        {
            Raster<Rgba8> r => r.CopyFrame(index),
            Raster<Rgba16> r => r.CopyFrame(index),
            _ => throw new InvalidOperationException("Frames are copied from RGBA rasters."),
        };
    }

    private static TimeSpan Delay(Raster raster, int index)
    {
        return raster switch
        {
            Raster<Rgba8> r => r.Frames[index].Metadata.Delay,
            Raster<Rgba16> r => r.Frames[index].Metadata.Delay,
            _ => TimeSpan.Zero,
        };
    }
}
