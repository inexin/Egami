using System.CommandLine;

using Egami.Video.Cli.Host;
using Egami.Video.Containers;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video remux</c> (Build Plan task 5): copies the packets of the chosen tracks into another container without
/// decoding, keeping tags, chapters and attachments. Like <c>ffmpeg -i in.avi -map 0 -c copy out.mkv</c>.
/// </summary>
internal static class RemuxCommand
{
    public static Command Create(VideoSession session)
    {
        MediaOptions options = new();
        Option<string?> fragment = Fragments.CreateOption();
        Command command = new("remux", "Copy tracks into another container without decoding: change AVI to Matroska, pick tracks, keep tags, chapters and attachments.");
        options.AddTo(command);
        command.Options.Add(fragment);
        command.SetAction(result =>
        {
            BatchOptions batch = options.Batch(session, result);
            List<InputFile> inputs = options.ExpandInputs(session, result);
            OutputTemplate output = options.OutputTemplate(session, result, inputs);
            TrackSelection selection = options.Tracks(result);
            string? format = result.GetValue(options.Format);
            Mp4FragmentOptions? fragments = Fragments.Parse(result, fragment);
            return Batch.Run(session, inputs, batch, input =>
            {
                MediaTarget target = Fragments.Apply(MediaIO.Resolve(session, input, output, format, "mkv"), fragments);
                Run(session, input, target, selection, inputs.Count);
                return [target.Path];
            });
        });
        return command;
    }

    /// <summary>Copies one input's selected tracks into <paramref name="target"/>.</summary>
    public static void Run(VideoSession session, InputFile input, MediaTarget target, TrackSelection selection, int count)
    {
        HashSet<int> keep;
        using (IDemuxer probe = MediaIO.OpenDemuxer(session, input))
        {
            keep = [.. selection.Select(probe.Tracks, input.Display).Select(t => t.Number)];
            target = Fragments.ForTracks(target, probe.Tracks.Where(t => keep.Contains(t.Number)));

            // Standard input can be read only once: the job goes on with the demuxer that read the headers.
            if (input.IsStdin)
            {
                Copy(session, probe, input, target, keep, count);
                return;
            }
        }

        if (keep.Count == 0)
        {
            throw new UsageException($"{input.Display}: no tracks left to write.");
        }

        IProgress<MediaProgress>? progress = session.ProgressSink?.For(input, input.Index, count);
        MediaIO.Write(session, target.Path, output =>
        {
            using Stream source = MediaIO.OpenStream(session, input);
            MediaContext context = target.Container is Mp4Format { Fragments: not null } ? session.Context.WithContainer(target.Container) : session.Context;
            MediaFile.Remux(source, output, target.Container.Name, t => keep.Contains(t.Number), context, leaveOpen: true, progress, session.Cancellation);
        });
    }

    /// <summary>Copies the selected tracks from an open demuxer (standard input) packet by packet.</summary>
    private static void Copy(VideoSession session, IDemuxer demuxer, InputFile input, MediaTarget target, HashSet<int> keep, int count)
    {
        TrackInfo[] tracks = [.. demuxer.Tracks.Where(t => keep.Contains(t.Number))];
        if (tracks.Length == 0)
        {
            throw new UsageException($"{input.Display}: no tracks left to write.");
        }

        MediaIO.Write(session, target.Path, output =>
        {
            CountingStream counted = new(output);
            using IMuxer muxer = MediaIO.CreateMuxer(session, counted, target.Container, tracks, demuxer.Info.Metadata);
            JobProgress progress = new(session.ProgressSink?.For(input, input.Index, count), demuxer.Info.Duration, tracks, session.Cancellation, counted);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (keep.Contains(packet.TrackNumber))
                    {
                        muxer.WritePacket(packet);
                        progress.Packet(packet.TrackNumber, packet.Timestamp);
                    }
                }
            }

            muxer.Finish();
            progress.Finish();
        });
    }
}
