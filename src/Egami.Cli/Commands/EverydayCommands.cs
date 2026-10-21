using System.CommandLine;
using System.Globalization;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;

namespace Egami.Cli.Commands;

/// <summary>The everyday commands (stage A): convert, resize, thumbnail, crop, rotate, flip and auto-orient.</summary>
internal static class EverydayCommands
{
    public static Command Convert(CliSession session)
    {
        ImageOptions options = new();
        Command command = new("convert", "Convert images to another format, or write them again with other settings.");
        options.AddTo(command);
        command.SetAction(result => ImageCommand.Run(session, result, options, () => new Plan(
            [.. ImageCommand.Leading(result, options), new SaveItem(options.Save(result))],
            options.Read(result))));
        return command;
    }

    public static Command Resize(CliSession session)
    {
        ImageOptions options = new();
        ResizeOptions resize = new(required: true);
        Command command = new("resize", "Resize images: to a width, a height, a box, a percentage, filling or padding a box.");
        options.AddTo(command);
        resize.AddTo(command);
        command.SetAction(result => ImageCommand.Run(session, result, options, () => new Plan(
            [.. ImageCommand.Leading(result, options), new StepItem(resize.Step(result, null)), new SaveItem(options.Save(result))],
            options.Read(result))));
        return command;
    }

    public static Command Thumbnail(CliSession session)
    {
        ImageOptions options = new();
        ResizeOptions resize = new(required: false);
        Option<bool> keepMetadata = new("--keep-metadata") { Description = "Keep EXIF, XMP, IPTC and text, which thumbnails leave out by default." };
        Command command = new(
            "thumbnail",
            "Make thumbnails for the web: turned upright, shrunk (never enlarged) to fit --size (256 by default), converted to sRGB, metadata left out.");
        options.AddTo(command);
        resize.AddTo(command);
        command.Options.Add(keepMetadata);
        command.SetAction(result => ImageCommand.Run(session, result, options, () =>
        {
            SizeSpec spec = SizeSpec.Parse(result.GetValue(resize.Size) ?? "256");
            if (!spec.OnlyEnlarge && !spec.OnlyShrink)
            {
                spec = spec with { OnlyShrink = true };
            }

            Dictionary<string, string> common = result.GetValue(keepMetadata) ? [] : new() { ["strip-metadata"] = "true" };
            ReadSpec read = options.Read(result);
            return new Plan(
                [new StepItem(ImageCommand.Step("auto-orient")), new StepItem(resize.Step(result, spec.ToString())), new SaveItem(options.Save(result, common))],
                read with { ConvertTo = read.ConvertTo ?? ColorProfile.Srgb });
        }));
        return command;
    }

    public static Command Crop(CliSession session)
    {
        ImageOptions options = new();
        Option<string> rect = new("--rect") { Description = "The area to keep: x,y,width,height in pixels.", HelpName = "x,y,w,h", Required = true };
        Command command = new("crop", "Crop images to a rectangle.");
        options.AddTo(command);
        command.Options.Add(rect);
        command.SetAction(result => ImageCommand.Run(session, result, options, () => new Plan(
            [.. ImageCommand.Leading(result, options), new StepItem(ImageCommand.Step("crop", ("rect", result.GetValue(rect)))), new SaveItem(options.Save(result))],
            options.Read(result))));
        return command;
    }

    public static Command Rotate(CliSession session)
    {
        ImageOptions options = new();
        Option<string?> angle = new("--angle", "-a") { Description = "Degrees clockwise; the last input may give it instead, as in 'egami rotate in.jpg 90 -o out.jpg'.", HelpName = "degrees" };
        Option<string?> filter = new("--filter") { Description = "The resampling kernel for angles that are not a multiple of 90.", HelpName = "filter" };
        Option<string?> background = new("--background") { Description = "The color of the corners an angle uncovers (transparent by default).", HelpName = "color" };
        Option<bool> reencode = new("--reencode") { Description = "Decode and encode a JPEG even when a lossless rotation is possible." };
        Command command = new("rotate", "Rotate images clockwise. A JPEG turned by a multiple of 90 degrees into a JPEG is rotated losslessly.");
        options.AddTo(command);
        command.Options.Add(angle);
        command.Options.Add(filter);
        command.Options.Add(background);
        command.Options.Add(reencode);
        command.SetAction(result =>
        {
            string degrees = result.GetValue(angle) ?? TakeTrailingValue(result, options, IsNumber)
                ?? throw new UsageException("Give the angle: 'egami rotate in.jpg 90 -o out.jpg' or --angle 90.");
            double value = ValueParsers.ParseDouble(degrees);
            JpegTransformOperation? lossless = (((value % 360) + 360) % 360) switch
            {
                90 => JpegTransformOperation.Rotate90,
                180 => JpegTransformOperation.Rotate180,
                270 => JpegTransformOperation.Rotate270,
                _ => null,
            };
            return ImageCommand.Run(
                session,
                result,
                options,
                () => new Plan(
                    [.. ImageCommand.Leading(result, options), new StepItem(ImageCommand.Step("rotate", ("degrees", degrees), ("filter", result.GetValue(filter)), ("background", result.GetValue(background)))), new SaveItem(options.Save(result))],
                    options.Read(result)),
                LosslessJpeg(session, result, options, reencode, lossless));
        });
        return command;
    }

    public static Command Flip(CliSession session)
    {
        ImageOptions options = new();
        Option<string?> direction = new("--direction", "-d") { Description = "horizontal (mirror left to right), vertical (top to bottom) or both; the last input may give it instead.", HelpName = "direction" };
        Option<bool> reencode = new("--reencode") { Description = "Decode and encode a JPEG even when a lossless flip is possible." };
        Command command = new("flip", "Mirror images left to right (horizontal), top to bottom (vertical) or both. A JPEG flipped into a JPEG is flipped losslessly.");
        options.AddTo(command);
        command.Options.Add(direction);
        command.Options.Add(reencode);
        command.SetAction(result =>
        {
            // No default: ImageMagick's -flip is top to bottom and a photo app's flip is left to right, so egami asks (review R1).
            string which = result.GetValue(direction) ?? TakeTrailingValue(result, options, t => t is "horizontal" or "vertical" or "both")
                ?? throw new UsageException("Give the direction: egami flip in.jpg horizontal|vertical|both -o out.jpg, or --direction.");
            JpegTransformOperation? lossless = which switch
            {
                "horizontal" => JpegTransformOperation.FlipHorizontal,
                "vertical" => JpegTransformOperation.FlipVertical,
                "both" => JpegTransformOperation.Rotate180,
                _ => null,
            };
            return ImageCommand.Run(
                session,
                result,
                options,
                () => new Plan([.. ImageCommand.Leading(result, options), new StepItem(ImageCommand.Step("flip", ("direction", which))), new SaveItem(options.Save(result))], options.Read(result)),
                LosslessJpeg(session, result, options, reencode, lossless));
        });
        return command;
    }

    public static Command AutoOrient(CliSession session)
    {
        ImageOptions options = new();
        Option<bool> reencode = new("--reencode") { Description = "Decode and encode a JPEG even when a lossless turn is possible." };
        Command command = new("auto-orient", "Turn images upright from their EXIF orientation and reset it. A JPEG into a JPEG is turned losslessly.");
        command.Aliases.Add("autoorient");
        options.AddTo(command, read: false);
        command.Options.Add(options.To);
        command.Options.Add(reencode);
        command.SetAction(result => ImageCommand.Run(
            session,
            result,
            options,
            () => new Plan([new StepItem(ImageCommand.Step("auto-orient")), new SaveItem(options.Save(result))], options.Read(result)),
            LosslessJpeg(session, result, options, reencode, JpegTransformOperation.AutoOrient)));
        return command;
    }

    /// <summary>
    /// The lossless JPEG route (2.4.4): a JPEG input written as JPEG with no other step, setting or color conversion is
    /// transformed in its compressed form, so no generation is lost. A transform that would move a partial block falls back
    /// to decoding.
    /// </summary>
    private static Func<InputSource, OutputTemplate?, IReadOnlyList<string>?>? LosslessJpeg(
        CliSession session, ParseResult result, ImageOptions options, Option<bool> reencode, JpegTransformOperation? operation)
    {
        if (operation is null || result.GetValue(reencode) || result.GetValue(options.AutoOrient) || result.GetValue(options.To) is not null
            || options.Save(result).HasSettings)
        {
            return null;
        }

        return (input, output) =>
        {
            RasterInfo info = input.Probe(session);
            if (info.Format != JpegFormat.Info)
            {
                return null;
            }

            (_, string path, FormatInfo format) = Runner.Resolve(session, input, options.Save(result), output, info.Format);
            if (format != JpegFormat.Info)
            {
                return null;
            }

            try
            {
                JpegTransformOptions transform = new() { Operation = operation.Value, Edges = JpegTransformEdges.Strict };
                Output.Write(session, path, destination =>
                {
                    using Stream source = input.Open(session);
                    JpegTransformResult done = JpegTransform.Apply(source, destination, transform, session.Context);
                    foreach (EgamiDiagnostic warning in done.Warnings)
                    {
                        session.Warn($"{input.File.Display}: {warning.Message}");
                    }
                });
                session.Info($"{input.File.Display}: transformed losslessly");
                return [path];
            }
            catch (InvalidOperationException)
            {
                session.Info($"{input.File.Display}: a partial block would move, so it is decoded and encoded instead");
                return null;
            }
            catch (UnsupportedFormatException)
            {
                return null;
            }
        };
    }

    /// <summary>
    /// Takes the last input as a value when it is one (<c>rotate in.jpg 90</c>), so the value may follow the inputs as the design
    /// shows; the inputs left must still be at least one.
    /// </summary>
    private static string? TakeTrailingValue(ParseResult result, ImageOptions options, Func<string, bool> isValue)
    {
        string[] inputs = result.GetValue(options.Inputs) ?? [];
        if (inputs.Length >= 2 && isValue(inputs[^1]))
        {
            string value = inputs[^1];
            Array.Resize(ref inputs, inputs.Length - 1);
            options.InputsOverride = inputs;
            return value;
        }

        return null;
    }

    private static bool IsNumber(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}

/// <summary>The options of <c>resize</c> and <c>thumbnail</c>: size syntax, fit, gravity, filter, linear light and padding color.</summary>
internal sealed class ResizeOptions
{
    public ResizeOptions(bool required)
    {
        Size = new Option<string?>("--size", "-s")
        {
            Description = "The size: W, xH, WxH or P%, then optionally ! (ignore the aspect ratio), ^ (fill the box), > (only shrink) or < (only enlarge).",
            HelpName = "size",
            Required = required,
        };
    }

    public Option<string?> Size { get; }

    public Option<string?> Fit { get; } = new("--fit") { Description = "How a WxH box is filled: contain (inside, the default), cover (filled and cropped), fill (stretched) or pad.", HelpName = "mode" };

    public Option<string?> Gravity { get; } = new("--gravity") { Description = "What cover keeps and where pad places the image: center (the default), top, top-left, ...", HelpName = "anchor" };

    public Option<string?> Filter { get; } = new("--filter") { Description = "The resampling kernel: bicubic (Catmull-Rom, the default), lanczos3, mitchell, bilinear, nearest, ...", HelpName = "filter" };

    public Option<bool> Linear { get; } = new("--linear") { Description = "Blend in linear light rather than in the encoded values." };

    public Option<string?> Background { get; } = new("--background") { Description = "The color --fit pad fills with (transparent by default).", HelpName = "color" };

    public void AddTo(Command command)
    {
        command.Options.Add(Size);
        command.Options.Add(Fit);
        command.Options.Add(Gravity);
        command.Options.Add(Filter);
        command.Options.Add(Linear);
        command.Options.Add(Background);
    }

    /// <summary>The resize step; <paramref name="size"/> replaces <c>--size</c> when given.</summary>
    public BoundStep Step(ParseResult result, string? size)
    {
        return ImageCommand.Step(
            "resize",
            ("size", size ?? result.GetValue(Size)),
            ("fit", result.GetValue(Fit)),
            ("gravity", result.GetValue(Gravity)),
            ("filter", result.GetValue(Filter)),
            ("linear", result.GetValue(Linear) ? "true" : null),
            ("background", result.GetValue(Background)));
    }
}
