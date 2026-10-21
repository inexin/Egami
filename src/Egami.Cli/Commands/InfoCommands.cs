using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Formats;
using Egami.Metadata;

namespace Egami.Cli.Commands;

/// <summary><c>egami info</c> and <c>egami formats</c>.</summary>
internal static class InfoCommands
{
    /// <summary>The version of the <c>info --json</c> schema.</summary>
    public const int InfoSchema = 1;

    public static Command Info(CliSession session)
    {
        ImageOptions options = new();
        Option<bool> all = new("--all", "-a") { Description = "Also read the metadata: color profile, EXIF, XMP, IPTC, text, resolution." };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {InfoSchema}, see the command reference) instead of text." };
        Command command = new("info", "Show what images are: format, size, frames, pixel layout, orientation; with --all, their metadata. Pixels are not decoded.");
        options.AddTo(command, write: false, read: false);
        command.Options.Add(all);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            bool everything = result.GetValue(all), asJson = result.GetValue(json);
            List<(InputFile File, RasterInfo? Info, RasterMetadata? Metadata, string? Error)> found = [.. inputs.Select(i => (i, (RasterInfo?)null, (RasterMetadata?)null, (string?)null))];
            int code = Batch.Run(session, inputs, batch with { FailFast = batch.FailFast }, input =>
            {
                RasterInfo info = input.Probe(session);
                RasterMetadata? metadata = everything ? MetadataReport.Load(session, input) : null;
                lock (found)
                {
                    found[input.File.Index] = (input.File, info, metadata, null);
                }

                return [];
            });

            if (asJson)
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", InfoSchema);
                    writer.WriteStartArray("files");
                    foreach ((InputFile file, RasterInfo? info, RasterMetadata? metadata, _) in found)
                    {
                        if (info is not null)
                        {
                            WriteJson(writer, file, info, metadata);
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach ((InputFile file, RasterInfo? info, RasterMetadata? metadata, _) in found)
                {
                    if (info is not null)
                    {
                        WriteText(session.Console.Text, file, info, metadata);
                    }
                }
            }

            return code;
        });
        return command;
    }

    public static Command Formats(CliSession session)
    {
        Argument<string?> format = new("format") { Description = "A format name or extension, to list its writer settings.", Arity = ArgumentArity.ZeroOrOne };
        Option<bool> json = new("--json") { Description = "Write JSON instead of text." };
        Command command = new("formats", "List the formats egami reads and writes, or one format's writer settings for --set.");
        command.Arguments.Add(format);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            GlobalOptions.Configure(session, result);
            string? name = result.GetValue(format);
            if (name is null)
            {
                ListFormats(session, result.GetValue(json));
                return ExitCodes.Success;
            }

            FormatInfo info = Runner.FindFormat(session, name) ?? throw new UsageException($"'{name}' is not a format egami knows; 'egami formats' lists them.");
            ShowFormat(session, info, result.GetValue(json));
            return ExitCodes.Success;
        });
        return command;
    }

    private static void WriteText(TextWriter output, InputFile file, RasterInfo info, RasterMetadata? metadata)
    {
        string frames = info.FrameCount is > 1 ? $", {info.FrameCount} frames" : string.Empty;
        string images = info.ImageCount is > 1 ? $", {info.ImageCount} images" : string.Empty;
        string orientation = info.Orientation is ImageOrientation.Normal or ImageOrientation.Unknown ? string.Empty : $", {MetadataReport.Orientation(info.Orientation)}";
        string meta = info.HasMetadata ? ", metadata" : string.Empty;
        output.WriteLine($"{file.Display}: {info.Format.Name} {info.Width}x{info.Height}{frames}{images}, {MetadataReport.Pixels(info.Layout)}{orientation}{meta}");
        if (metadata is not null)
        {
            if (info.LoopCount is { } loops && info.FrameCount is > 1)
            {
                output.WriteLine($"  loop count     {(loops == 0 ? "forever" : loops.ToString(CultureInfo.InvariantCulture))}");
            }

            MetadataReport.WriteText(output, metadata, everything: false);
        }
    }

    private static void WriteJson(Utf8JsonWriter json, InputFile file, RasterInfo info, RasterMetadata? metadata)
    {
        json.WriteStartObject();
        json.WriteString("path", file.Display);
        json.WriteString("format", info.Format.Name);
        json.Text("mimeType", info.Format.MimeTypes.Count > 0 ? info.Format.MimeTypes[0] : null);
        json.WriteNumber("width", info.Width);
        json.WriteNumber("height", info.Height);
        json.Number("frames", info.FrameCount);
        json.Number("images", info.ImageCount);
        json.Number("loopCount", info.LoopCount);
        json.WriteString("orientation", MetadataReport.Orientation(info.Orientation));
        json.WriteStartObject("pixels");
        json.WriteString("channels", ValueParsers.Kebab(info.Layout.ChannelOrder.ToString()));
        json.WriteNumber("channelCount", info.Layout.ChannelCount);
        json.WriteNumber("bitsPerChannel", info.Layout.BitsPerChannel);
        json.WriteString("channelType", ValueParsers.Kebab(info.Layout.ChannelType.ToString()));
        json.WriteString("alpha", info.Layout.HasAlpha ? ValueParsers.Kebab(info.Layout.Alpha.ToString()) : "none");
        json.WriteEndObject();
        json.WriteBoolean("hasMetadata", info.HasMetadata);
        json.WriteNumber("estimatedBytes", info.EstimatedBytes);
        json.WriteBoolean("streams", info.Access == RasterAccess.Streamed);
        if (metadata is not null)
        {
            MetadataReport.WriteJson(json, metadata, everything: false);
        }

        json.WriteEndObject();
    }

    private static void ListFormats(CliSession session, bool asJson)
    {
        List<FormatInfo> formats = [.. session.Formats.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
        if (asJson)
        {
            JsonOut.Write(session.Console.Text, json =>
            {
                json.WriteStartObject();
                json.WriteStartArray("formats");
                foreach (FormatInfo format in formats)
                {
                    json.WriteStartObject();
                    json.WriteString("name", format.Name);
                    json.WriteStartArray("extensions");
                    foreach (string extension in format.Extensions)
                    {
                        json.WriteStringValue(extension);
                    }

                    json.WriteEndArray();
                    json.WriteStartArray("mimeTypes");
                    foreach (string mime in format.MimeTypes)
                    {
                        json.WriteStringValue(mime);
                    }

                    json.WriteEndArray();
                    json.WriteBoolean("read", format.Reader is not null && !NeedsHeic(format));
                    json.WriteBoolean("write", format.Writer is not null);
                    if (NeedsHeic(format))
                    {
                        json.WriteString("needs", "egami-heic");
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            });
            return;
        }

        TextWriter output = session.Console.Text;
        output.WriteLine($"{"Format",-14} {"Read",-5} {"Write",-6} Extensions");
        foreach (FormatInfo format in formats)
        {
            output.WriteLine($"{format.Name,-14} {Reads(format),-5} {(format.Writer is null ? "-" : "yes"),-6} {string.Join(' ', format.Extensions)}{(NeedsHeic(format) ? "  (needs egami-heic)" : string.Empty)}");
        }

        output.WriteLine();
        output.WriteLine("'egami formats <format>' lists a format's writer settings for --set.");
    }

    private static void ShowFormat(CliSession session, FormatInfo format, bool asJson)
    {
        WriterInfo? writer = format.Writer is null ? null : WriterCatalog.Find(format);
        WriterOptions defaults = writer?.Defaults() ?? WriterOptions.Default;
        List<SettingInfo> settings = writer?.Settings.ToList() ?? [];
        string prefix = writer?.Prefix ?? WriterInfo.Normalize(format.Extensions.Count > 0 ? format.Extensions[0] : format.Name);
        if (asJson)
        {
            JsonOut.Write(session.Console.Text, json =>
            {
                json.WriteStartObject();
                json.WriteString("name", format.Name);
                json.WriteBoolean("read", format.Reader is not null);
                json.WriteBoolean("write", format.Writer is not null);
                json.WriteString("prefix", prefix);
                json.WriteStartArray("settings");
                foreach (SettingInfo setting in settings)
                {
                    json.WriteStartObject();
                    json.WriteString("key", $"{prefix}.{setting.Key}");
                    json.WriteString("type", setting.Type);
                    json.Text("default", Display(setting.Read(defaults)));
                    if (setting.Choices is { } choices)
                    {
                        json.WriteStartArray("choices");
                        foreach (string choice in choices)
                        {
                            json.WriteStringValue(choice);
                        }

                        json.WriteEndArray();
                    }

                    json.WriteString("help", setting.Help);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            });
            return;
        }

        TextWriter output = session.Console.Text;
        output.WriteLine($"{format.Name}: {string.Join(' ', format.Extensions)} ({string.Join(", ", format.MimeTypes)})");
        output.WriteLine($"  reads: {(NeedsHeic(format) ? "no, the egami-heic tool would (HEVC is covered by patents)" : format.Reader is null ? "no" : "yes")}, writes: {(format.Writer is null ? "no" : "yes")}");
        if (format.Writer is null)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Writer settings (--set key=value):");
        foreach (SettingInfo setting in settings)
        {
            string type = setting.Choices is { } choices ? string.Join('|', choices) : setting.Type;
            output.WriteLine($"  {prefix}.{setting.Key} <{type}>  default {Display(setting.Read(defaults)) ?? "none"}");
            if (setting.Help.Length > 0)
            {
                output.WriteLine($"      {setting.Help}");
            }
        }
    }

    /// <summary>
    /// Whether the format is HEIC, which core Egami lists with a reader that asks for Egami.Heic; the tool leaves HEVC out for
    /// its patents (CLI design Q2) and names the egami-heic tool that would read it (review R9).
    /// </summary>
    private static bool NeedsHeic(FormatInfo format) => format.Name == "HEIC";

    private static string Reads(FormatInfo format) => NeedsHeic(format) ? "-" : format.Reader is null ? "-" : "yes";

    private static string? Display(object? value)
    {
        return value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            Enum e => ValueParsers.Kebab(e.ToString()),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable<int> list => string.Join(',', list),
            _ => value.ToString(),
        };
    }
}
