using System.CommandLine;

using Egami.Video.Cli.Host;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Cli.Commands;

/// <summary><c>egami-video formats</c> (Build Plan task 5): the containers and codecs this build reads and writes.</summary>
internal static class FormatsCommand
{
    public static Command Create(VideoSession session)
    {
        Option<bool> json = new("--json") { Description = "Write JSON instead of text." };
        Command command = new("formats", "List the containers and codecs egami-video reads and writes, like ffmpeg -formats and -codecs.");
        command.Options.Add(json);
        command.SetAction(result =>
        {
            ReportObject report = Report(session.Context);
            if (result.GetValue(json))
            {
                ReportWriter.Json(session.Console.Text, report);
                return ExitCodes.Success;
            }

            TextWriter text = session.Console.Text;
            text.WriteLine("Containers (R read, W write):");
            foreach (ReportObject container in (List<ReportObject>)report["containers"]!)
            {
                string flags = ((bool)container["read"]! ? "R" : "-") + ((bool)container["write"]! ? "W" : "-");
                text.WriteLine($"  {flags} {container["name"],-12} {string.Join(' ', (IEnumerable<string>)container["extensions"]!)}");
            }

            text.WriteLine("Video codecs (D decode, E encode):");
            WriteCodecs(text, (List<ReportObject>)report["videoCodecs"]!);
            text.WriteLine("Audio codecs (D decode, E encode):");
            WriteCodecs(text, (List<ReportObject>)report["audioCodecs"]!);
            text.WriteLine("Other tracks (any codec) are copied as packets by remux, trim, concat, segment, extract and embed.");
            return ExitCodes.Success;
        });
        return command;
    }

    private static void WriteCodecs(TextWriter text, List<ReportObject> codecs)
    {
        foreach (ReportObject codec in codecs)
        {
            string flags = ((bool)codec["decode"]! ? "D" : "-") + ((bool)codec["encode"]! ? "E" : "-");
            text.WriteLine($"  {flags} {codec["name"],-12} {codec["codecId"]}");
        }
    }

    /// <summary>The containers and codecs as a report.</summary>
    internal static ReportObject Report(MediaContext context)
    {
        ReportObject report = new();
        List<ReportObject> containers = report.List("containers");
        foreach (ContainerFormat format in context.Containers)
        {
            containers.Add(new ReportObject()
                .Add("name", format.Name)
                .Add("extensions", format.Extensions.Select(e => e.TrimStart('.')).ToArray())
                .Add("read", true)
                .Add("write", format.CanMux));
        }

        List<ReportObject> video = report.List("videoCodecs");
        foreach (VideoCodec codec in context.Codecs)
        {
            video.Add(new ReportObject().Add("name", codec.Name).Add("codecId", codec.CodecId).Add("decode", true).Add("encode", codec.CanEncode));
        }

        List<ReportObject> audio = report.List("audioCodecs");
        foreach (Audio.AudioCodec codec in context.AudioCodecs)
        {
            audio.Add(new ReportObject().Add("name", codec.Name).Add("codecId", codec.CodecId).Add("decode", true).Add("encode", codec.CanEncode));
        }

        return report;
    }
}
