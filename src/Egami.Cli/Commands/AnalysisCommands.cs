using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Egami.Analysis;
using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Formats;

namespace Egami.Cli.Commands;

/// <summary><c>egami compare</c> and <c>egami stats</c> (stage C), on the 2.5.5 analysis calls.</summary>
internal static class AnalysisCommands
{
    /// <summary>The metrics compare reports, and whether a larger value means more different.</summary>
    private static readonly (string Name, bool Higher)[] Metrics =
        [("ae", true), ("mae", true), ("mse", true), ("rmse", true), ("peak", true), ("psnr", false), ("ssim", false), ("dssim", true)];

    public static Command Compare(CliSession session)
    {
        Argument<string> first = new("image") { Description = "The first image." };
        Argument<string> second = new("other") { Description = "The image to compare it with, the same size." };
        Option<string> metric = new("--metric", "-m")
        {
            Description = "The number to print and test: ae (pixels that differ), mae, mse, rmse (the default), peak, psnr, ssim or dssim.",
            HelpName = "metric",
            DefaultValueFactory = _ => "rmse",
        };
        Option<double?> threshold = new("--threshold", "-t")
        {
            Description = "Exit with 1 when the metric is worse than this (above it, or below it for psnr and ssim); without it, any difference exits with 1.",
            HelpName = "value",
        };
        Option<double> fuzz = new("--fuzz") { Description = "How far apart two values may be and still count as equal, from 0 to 1, for ae and the exit code.", HelpName = "0-1" };
        Option<string?> diff = new("--diff", "-d") { Description = "Write a picture of the differences.", HelpName = "path" };
        Option<string> style = new("--diff-style") { Description = "highlight (differing pixels in red, as ImageMagick's compare) or absolute (|a - b|).", HelpName = "style", DefaultValueFactory = _ => "highlight" };
        Option<bool> json = new("--json") { Description = "Write every metric per channel as JSON." };
        Command command = new("compare", "Compare two images: print a metric and exit with 1 when they differ more than --threshold.");
        command.Arguments.Add(first);
        command.Arguments.Add(second);
        command.Options.Add(metric);
        command.Options.Add(threshold);
        command.Options.Add(fuzz);
        command.Options.Add(diff);
        command.Options.Add(style);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            string name = result.GetValue(metric)!.ToLowerInvariant();
            (string Name, bool Higher) chosen = Metrics.FirstOrDefault(m => m.Name == name);
            if (chosen.Name is null)
            {
                throw new UsageException($"'{name}' is not a metric; use {string.Join(", ", Metrics.Select(m => m.Name))}.");
            }

            DifferenceStyle diffStyle = ValueParsers.Enum<DifferenceStyle>(result.GetValue(style)!);
            List<InputFile> inputs = [.. Globber.Expand(session, [result.GetValue(first)!], recursive: false).Take(1), .. Globber.Expand(session, [result.GetValue(second)!], recursive: false).Take(1)];
            using Raster a = new InputSource(inputs[0]).Load(session);
            using Raster b = new InputSource(inputs[1]).Load(session);
            if (a.Width != b.Width || a.Height != b.Height)
            {
                session.Error($"the images differ in size: {a.Width}x{a.Height} and {b.Width}x{b.Height}.");
                return ExitCodes.Failed;
            }

            float fuzzValue = (float)result.GetValue(fuzz);
            CompareResult compared = Raster.Compare(a, b, new Analysis.CompareOptions { Ssim = name is "ssim" or "dssim" || result.GetValue(json), Fuzz = fuzzValue });
            double value = Value(compared, name);
            if (result.GetValue(diff) is { } diffPath)
            {
                using Raster picture = Raster.Difference(a, b, diffStyle, new DifferenceOptions { Fuzz = fuzzValue });
                string path = session.ResolvePath(diffPath);
                FormatInfo format = Runner.FindFormat(session, Path.GetExtension(path)) ?? throw new UsageException($"'{diffPath}' has no extension egami can write.");
                Output.Write(session, path, output => picture.Save(output, format));
            }

            if (result.GetValue(json))
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("identical", compared.IsIdentical);
                    writer.WriteNumber("pixels", compared.PixelCount);
                    writer.WriteNumber("differentPixels", compared.DifferentPixels);
                    foreach ((string channel, ChannelDifference d) in (ReadOnlySpan<(string, ChannelDifference)>)
                        [("overall", compared.Overall), ("red", compared.Red), ("green", compared.Green), ("blue", compared.Blue), ("alpha", compared.Alpha)])
                    {
                        writer.WriteStartObject(channel);
                        writer.Number("mae", d.Mae);
                        writer.Number("mse", d.Mse);
                        writer.Number("rmse", d.Rmse);
                        writer.Number("psnr", double.IsPositiveInfinity(d.Psnr) ? null : d.Psnr);
                        writer.Number("peak", d.PeakError);
                        writer.Number("ssim", d.Ssim);
                        writer.Number("dssim", d.Dssim);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                });
            }
            else
            {
                session.Console.Text.WriteLine(name == "ae" ? value.ToString(CultureInfo.InvariantCulture) : Format(value));
            }

            bool fails = result.GetValue(threshold) is { } limit
                ? (chosen.Higher ? value > limit : value < limit)
                : fuzzValue > 0 ? compared.DifferentPixels > 0 : !compared.IsIdentical;
            return fails ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    public static Command Stats(CliSession session)
    {
        ImageOptions options = new();
        Option<bool> json = new("--json") { Description = "Write JSON instead of text." };
        Option<int> frame = new("--frame") { Description = "The frame to measure, from 0.", HelpName = "index" };
        Option<bool> all = new("--all", "-a") { Description = "Also count the colors and give perceptual hashes and the pixel signature." };
        Command command = new("stats", "Measure images: per-channel minimum, maximum, mean, deviation, skewness, kurtosis and entropy, as ImageMagick's identify -verbose.");
        options.AddTo(command, write: false, read: false);
        command.Options.Add(json);
        command.Options.Add(frame);
        command.Options.Add(all);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            int index = result.GetValue(frame);
            bool everything = result.GetValue(all);
            bool asJson = result.GetValue(json);
            List<(InputFile File, Measured? Measured)> found = [.. inputs.Select(i => (i, (Measured?)null))];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                using Raster image = input.Load(session);
                if (index < 0 || index >= image.FrameCount)
                {
                    throw new UsageException($"{input.File.Display} has {image.FrameCount} frame(s); --frame {index} is not one.");
                }

                Measured measured = new(
                    image.Width,
                    image.Height,
                    image.Statistics(index),
                    image.IsOpaque(index),
                    image.IsGray(0, index),
                    everything ? image.CountColors(index) : null,
                    everything ? PerceptualHash.Dct(image) : null,
                    everything ? PerceptualHash.Difference(image) : null,
                    everything ? image.Signature() : null);
                lock (found)
                {
                    found[input.File.Index] = (input.File, measured);
                }

                return [];
            });

            if (asJson)
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("files");
                    foreach ((InputFile file, Measured? measured) in found)
                    {
                        if (measured is not null)
                        {
                            WriteJson(writer, file, measured);
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach ((InputFile file, Measured? measured) in found)
                {
                    if (measured is not null)
                    {
                        WriteText(session.Console.Text, file, measured);
                    }
                }
            }

            return code;
        });
        return command;
    }

    private static double Value(CompareResult result, string metric)
    {
        ChannelDifference overall = result.Overall;
        return metric switch
        {
            "ae" => result.DifferentPixels,
            "mae" => overall.Mae,
            "mse" => overall.Mse,
            "rmse" => overall.Rmse,
            "peak" => overall.PeakError,
            "psnr" => overall.Psnr,
            "ssim" => overall.Ssim,
            _ => overall.Dssim,
        };
    }

    private static string Format(double value)
    {
        return double.IsPositiveInfinity(value) ? "inf" : value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static void WriteText(TextWriter output, InputFile file, Measured measured)
    {
        RasterStatistics stats = measured.Statistics;
        output.WriteLine($"{file.Display}: {measured.Width}x{measured.Height}, {(measured.Opaque ? "opaque" : "has transparency")}, {(measured.Gray ? "gray" : "color")}");
        output.WriteLine($"  {"channel",-8} {"min",8} {"max",8} {"mean",8} {"stddev",8} {"skew",8} {"kurtosis",9} {"entropy",8}");
        foreach ((string name, ChannelStatistics channel) in Channels(stats))
        {
            output.WriteLine($"  {name,-8} {Format(channel.Min),8} {Format(channel.Max),8} {Format(channel.Mean),8} {Format(channel.StdDev),8} {Format(channel.Skewness),8} {Format(channel.Kurtosis),9} {Format(channel.Entropy),8}");
        }

        if (measured.Colors is { } colors)
        {
            output.WriteLine($"  colors     {colors}");
            output.WriteLine($"  phash      {measured.Phash!.Value:x16}");
            output.WriteLine($"  dhash      {measured.Dhash!.Value:x16}");
            output.WriteLine($"  signature  {measured.Signature}");
        }
    }

    private static void WriteJson(Utf8JsonWriter json, InputFile file, Measured measured)
    {
        json.WriteStartObject();
        json.WriteString("path", file.Display);
        json.WriteNumber("width", measured.Width);
        json.WriteNumber("height", measured.Height);
        json.WriteBoolean("opaque", measured.Opaque);
        json.WriteBoolean("gray", measured.Gray);
        json.WriteNumber("pixels", measured.Statistics.PixelCount);
        json.WriteStartObject("channels");
        foreach ((string name, ChannelStatistics channel) in Channels(measured.Statistics))
        {
            json.WriteStartObject(name);
            json.Number("min", channel.Min);
            json.Number("max", channel.Max);
            json.Number("mean", channel.Mean);
            json.Number("stdDev", channel.StdDev);
            json.Number("skewness", channel.Skewness);
            json.Number("kurtosis", channel.Kurtosis);
            json.Number("entropy", channel.Entropy);
            json.WriteEndObject();
        }

        json.WriteEndObject();
        if (measured.Colors is { } colors)
        {
            json.WriteNumber("colors", colors);
            json.WriteString("phash", measured.Phash!.Value.ToString("x16", CultureInfo.InvariantCulture));
            json.WriteString("dhash", measured.Dhash!.Value.ToString("x16", CultureInfo.InvariantCulture));
            json.WriteString("signature", measured.Signature);
        }

        json.WriteEndObject();
    }

    private static (string Name, ChannelStatistics Channel)[] Channels(RasterStatistics stats)
    {
        return [("red", stats.Red), ("green", stats.Green), ("blue", stats.Blue), ("alpha", stats.Alpha), ("luma", stats.Luma)];
    }

    private sealed record Measured(int Width, int Height, RasterStatistics Statistics, bool Opaque, bool Gray, long? Colors, ulong? Phash, ulong? Dhash, string? Signature);
}
