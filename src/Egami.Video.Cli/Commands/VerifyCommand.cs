using System.CommandLine;

using Egami.Video.Cli.Host;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video verify</c> (V6 design Q9): checks the fixity of FFV1 files without decoding pictures, through
/// <see cref="Ffv1Codec.Verify(Stream, MediaContext?, bool, CancellationToken)"/>: the configuration record's CRC and every
/// slice's CRC and footer. Exit code 1 when a file is damaged. Like decoding with <c>ffmpeg -err_detect crccheck -f null -</c>,
/// far faster.
/// </summary>
internal static class VerifyCommand
{
    public static Command Create(VideoSession session)
    {
        MediaOptions options = new();
        Option<bool> json = new("--json") { Description = "Write the results as JSON." };
        Command command = new("verify", "Check FFV1 files for damage without decoding: every slice's CRC and footer and the configuration CRC. Exit code 1 when a file is damaged.");
        options.AddTo(command, write: false, tracks: false);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            bool asJson = result.GetValue(json);
            ReportObject?[] reports = new ReportObject?[inputs.Count];
            int code = Batch.Run(session, inputs, batch, input =>
            {
                Ffv1VerifyResult verified;
                using (Stream stream = MediaIO.OpenStream(session, input))
                {
                    verified = Ffv1Codec.Verify(stream, session.Context, leaveOpen: false, session.Cancellation);
                }

                reports[input.Index] = Report(input, verified);
                if (!asJson)
                {
                    lock (session.Console.Text)
                    {
                        Write(session.Console.Text, input, verified);
                    }
                }

                return verified.IsIntact
                    ? []
                    : throw new InvalidDataException($"{input.Display}: {Problems(verified)} found.");
            });

            if (asJson)
            {
                ReportObject root = new();
                root.Add("schema", 1);
                root.List("files").AddRange(reports.OfType<ReportObject>());
                ReportWriter.Json(session.Console.Text, root);
            }

            return code;
        });
        return command;
    }

    private static void Write(TextWriter text, InputFile input, Ffv1VerifyResult result)
    {
        string crcs = result.HasSliceCrcs ? "slice CRCs checked" : "no slice CRCs to check (FFV1 version 0 or 1, or written without them); slice footers checked";
        text.WriteLine($"{input.Display}: {(result.IsIntact ? "intact" : Problems(result))}, {result.Frames} frames, {result.Slices} slices, {crcs}");
        if (result.ConfigurationCrcOk is false)
        {
            text.WriteLine("  the configuration record's CRC is wrong");
        }

        foreach (Ffv1VerifyError error in result.Errors)
        {
            text.WriteLine($"  frame {error.Frame} ({TimeValue.Format(error.Timestamp)}) slice {error.Slice}: {error.Problem}");
        }
    }

    private static ReportObject Report(InputFile input, Ffv1VerifyResult result)
    {
        ReportObject file = new();
        file.Add("path", input.Display);
        file.Add("intact", result.IsIntact);
        file.Add("frames", result.Frames);
        file.Add("slices", result.Slices);
        file.Add("sliceCrcs", result.HasSliceCrcs);
        file.Add("configurationCrcOk", result.ConfigurationCrcOk);
        List<ReportObject> errors = file.List("errors");
        foreach (Ffv1VerifyError error in result.Errors)
        {
            errors.Add(new ReportObject()
                .Add("frame", error.Frame)
                .Add("time", error.Timestamp.Seconds)
                .Add("slice", error.Slice)
                .Add("problem", error.Problem));
        }

        return file;
    }

    private static string Problems(Ffv1VerifyResult result) =>
        (result.Errors.Count + (result.ConfigurationCrcOk is false ? 1 : 0)) is var count && count == 1 ? "1 problem" : $"{count} problems";
}
