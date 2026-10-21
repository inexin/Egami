using System.CommandLine;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;

namespace Egami.Cli.Commands;

/// <summary><c>egami metadata show|strip|copy</c> and <c>egami color</c> (stage C).</summary>
internal static class MetadataCommands
{
    /// <summary>The metadata blocks strip can keep and copy can copy.</summary>
    private static readonly string[] Blocks = ["icc", "exif", "xmp", "iptc", "text", "resolution"];

    public static Command Create(CliSession session)
    {
        Command metadata = new("metadata", "Show, strip or copy metadata: color profile, EXIF, XMP, IPTC, text and resolution.");
        metadata.Subcommands.Add(Show(session));
        metadata.Subcommands.Add(Strip(session));
        metadata.Subcommands.Add(Copy(session));
        return metadata;
    }

    public static Command Color(CliSession session)
    {
        ImageOptions options = new();
        Option<string> to = new("--to") { Description = "The profile to convert to: srgb, display-p3, adobe-rgb, rec2020, rec2100-pq, ... or an ICC file.", HelpName = "profile", Required = true };
        Option<string?> intent = new("--intent") { Description = "perceptual (the default), relative-colorimetric, saturation or absolute-colorimetric.", HelpName = "intent" };
        Option<bool> blackPoint = new("--black-point-compensation") { Description = "Map black to black between profiles with different black points." };
        Option<string?> toneMap = new("--tone-map") { Description = "How HDR is brought into a standard range: bt2390 (the default), reinhard or clip.", HelpName = "method" };
        Option<double?> peak = new("--peak") { Description = "The HDR source's peak brightness in nits, when the file does not say.", HelpName = "nits" };
        Option<double?> white = new("--reference-white") { Description = "The brightness of SDR white in nits (203 by default).", HelpName = "nits" };
        Command command = new("color", "Convert images to another color profile (ICC, 2.1.2), tone mapping HDR to SDR where needed (2.9.1).");
        options.AddTo(command, read: false);
        command.Options.Add(options.AutoOrient);
        command.Options.Add(to);
        command.Options.Add(intent);
        command.Options.Add(blackPoint);
        command.Options.Add(toneMap);
        command.Options.Add(peak);
        command.Options.Add(white);
        command.SetAction(result => ImageCommand.Run(session, result, options, () =>
        {
            ColorProfile target = ValueParsers.ParseProfile(result.GetValue(to)!);
            ColorTransformOptions transform = new()
            {
                Intent = result.GetValue(intent) is { } i ? ValueParsers.Enum<RenderingIntent>(i) : RenderingIntent.Perceptual,
                BlackPointCompensation = result.GetValue(blackPoint),
                ToneMapping = result.GetValue(toneMap) is { } t ? ValueParsers.Enum<ToneMapping>(t) : ToneMapping.Bt2390,
                SourcePeakNits = result.GetValue(peak),
                ReferenceWhiteNits = result.GetValue(white) ?? new ColorTransformOptions().ReferenceWhiteNits,
            };
            List<KeyValuePair<string, string>> values = [new("profile", result.GetValue(to)!)];
            if (result.GetValue(intent) is { } shown)
            {
                values.Add(new("intent", shown));
            }

            StepInfo convert = new("convert-color", "ConvertColor", "Converts to a color profile.", [new OverloadInfo([])], (ops, _, _) => ops.ConvertColor(target, transform)) { KeepsSize = true };
            return new Plan(
                [.. ImageCommand.Leading(result, options), new StepItem(new BoundStep(convert, 0, [], values)), new SaveItem(options.Save(result))],
                ReadSpec.Default);
        }));
        return command;
    }

    private static Command Show(CliSession session)
    {
        ImageOptions options = new();
        Option<bool> json = new("--json") { Description = "Write JSON, with every EXIF entry, IPTC dataset, text entry and the XMP packet." };
        Command command = new("show", "Show every piece of metadata an image holds.");
        options.AddTo(command, write: false, read: false);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            bool asJson = result.GetValue(json);
            List<(InputFile File, RasterMetadata? Metadata)> found = [.. inputs.Select(i => (i, (RasterMetadata?)null))];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                RasterMetadata metadata = MetadataReport.Load(session, input);
                lock (found)
                {
                    found[input.File.Index] = (input.File, metadata);
                }

                return [];
            });
            if (asJson)
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("files");
                    foreach ((InputFile file, RasterMetadata? metadata) in found)
                    {
                        if (metadata is not null)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("path", file.Display);
                            MetadataReport.WriteJson(writer, metadata, everything: true);
                            writer.WriteEndObject();
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach ((InputFile file, RasterMetadata? metadata) in found)
                {
                    if (metadata is not null)
                    {
                        session.Console.Text.WriteLine($"{file.Display}:");
                        if (MetadataReport.Blocks(metadata).Count == 0 && metadata.Resolution is null && metadata.Gamma is null)
                        {
                            session.Console.Text.WriteLine("  no metadata");
                        }

                        MetadataReport.WriteText(session.Console.Text, metadata, everything: true);
                    }
                }
            }

            return code;
        });
        return command;
    }

    private static Command Strip(CliSession session)
    {
        ImageOptions options = new();
        Option<string?> keep = new("--keep") { Description = $"Blocks to keep, comma-separated: {string.Join(", ", Blocks)}.", HelpName = "blocks" };
        Option<bool> reencode = new("--reencode") { Description = "Allow writing a lossy format (JPEG, lossy WebP, AVIF, JPEG XL) again, which costs a generation." };
        Command command = new(
            "strip",
            "Remove metadata. A JPEG losing everything is rewritten without decoding; otherwise the image is written again, which a lossy format allows only with --reencode.");
        options.AddTo(command, read: false);
        command.Options.Add(keep);
        command.Options.Add(reencode);
        command.SetAction(result =>
        {
            HashSet<string> kept = ParseBlocks(result.GetValue(keep), "--keep");
            return ImageCommand.Run(
                session,
                result,
                options,
                () => new Plan([new EditItem("metadata-strip", image => Strip(image.Metadata, kept)), new SaveItem(options.Save(result))], ReadSpec.Default),
                (input, output) => (kept.Count == 0 && !options.Save(result).HasSettings ? LosslessJpegStrip(session, result, options, input, output) : null)
                    ?? RefuseLossy(session, result, options, reencode, input, output));
        });
        return command;
    }

    private static Command Copy(CliSession session)
    {
        ImageOptions options = new();
        Option<string> from = new("--from") { Description = "The image to copy metadata from.", HelpName = "image", Required = true };
        Option<string?> only = new("--only") { Description = $"Blocks to copy, comma-separated: {string.Join(", ", Blocks)}; all by default.", HelpName = "blocks" };
        Option<bool> reencode = new("--reencode") { Description = "Allow writing a lossy format (JPEG, lossy WebP, AVIF, JPEG XL) again, which costs a generation." };
        Command command = new("copy", "Copy metadata from one image to others, which are written again with it; a lossy format only with --reencode.");
        options.AddTo(command, read: false);
        command.Options.Add(from);
        command.Options.Add(only);
        command.Options.Add(reencode);
        command.SetAction(result =>
        {
            HashSet<string> blocks = ParseBlocks(result.GetValue(only), "--only");
            if (blocks.Count == 0)
            {
                blocks = [.. Blocks];
            }

            return ImageCommand.Run(
                session,
                result,
                options,
                () =>
                {
                    InputFile source = Globber.Expand(session, [result.GetValue(from)!], recursive: false)[0];
                    RasterMetadata donor = MetadataReport.Load(session, new InputSource(source));
                    return new Plan([new EditItem("metadata-copy", image => CopyInto(donor, image.Metadata, blocks)), new SaveItem(options.Save(result))], ReadSpec.Default);
                },
                (input, output) => RefuseLossy(session, result, options, reencode, input, output));
        });
        return command;
    }

    private static void Strip(RasterMetadata metadata, HashSet<string> keep)
    {
        if (!keep.Contains("icc"))
        {
            metadata.IccProfile = default;
        }

        if (!keep.Contains("exif"))
        {
            metadata.Exif = null;
        }

        if (!keep.Contains("xmp"))
        {
            metadata.Xmp = default;
            metadata.XmpData = null;
        }

        if (!keep.Contains("iptc"))
        {
            metadata.Iptc = null;
        }

        if (!keep.Contains("text"))
        {
            metadata.Text.Clear();
        }

        if (!keep.Contains("resolution"))
        {
            metadata.Resolution = null;
        }
    }

    private static void CopyInto(RasterMetadata from, RasterMetadata to, HashSet<string> blocks)
    {
        RasterMetadata source = from.Copy();
        if (blocks.Contains("icc"))
        {
            to.IccProfile = source.IccProfile;
        }

        if (blocks.Contains("exif"))
        {
            to.Exif = source.Exif;
        }

        if (blocks.Contains("xmp"))
        {
            to.Xmp = source.Xmp;
            to.XmpData = source.XmpData;
        }

        if (blocks.Contains("iptc"))
        {
            to.Iptc = source.Iptc;
        }

        if (blocks.Contains("text"))
        {
            to.Text.Clear();
            foreach (TextEntry entry in source.Text)
            {
                to.Text.Add(entry);
            }
        }

        if (blocks.Contains("resolution"))
        {
            to.Resolution = source.Resolution;
        }
    }

    private static HashSet<string> ParseBlocks(string? text, string option)
    {
        HashSet<string> blocks = new(StringComparer.Ordinal);
        foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.ToLowerInvariant();
            if (!Blocks.Contains(name))
            {
                throw new UsageException($"{option}: '{part}' is not one of {string.Join(", ", Blocks)}.");
            }

            blocks.Add(name);
        }

        return blocks;
    }

    /// <summary>
    /// Refuses to write a lossy format again for a metadata edit, which would lose quality the user did not ask to lose, unless
    /// --reencode allows it (review R5). Returns <see langword="null"/> so the plan runs when the output is lossless.
    /// </summary>
    private static IReadOnlyList<string>? RefuseLossy(CliSession session, System.CommandLine.ParseResult result, ImageOptions options, Option<bool> reencode, InputSource input, OutputTemplate? output)
    {
        if (result.GetValue(reencode))
        {
            return null;
        }

        SaveSpec save = options.Save(result);
        (_, _, FormatInfo format) = Runner.Resolve(session, input, save, output, input.Probe(session).Format);
        bool lossless = save.Common.TryGetValue("lossless", out string? value) && value == "true";
        if (format == JpegFormat.Info || (!lossless && WriterInfo.Normalize(format.Name) is "webp" or "avif" or "jpegxl"))
        {
            throw new UsageException($"{input.File.Display}: writing {format.Name} again loses quality; add --reencode to allow it, or write a lossless format.");
        }

        return null;
    }

    private static IReadOnlyList<string>? LosslessJpegStrip(CliSession session, System.CommandLine.ParseResult result, ImageOptions options, InputSource input, OutputTemplate? output)
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

        Output.Write(session, path, destination =>
        {
            using Stream source = input.Open(session);
            JpegTransform.Apply(source, destination, new JpegTransformOptions { CopyMetadata = false }, session.Context);
        });
        return [path];
    }
}
