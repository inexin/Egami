using System.CommandLine;

using Egami.Video.Containers;

namespace Egami.Video.Cli.Host;

/// <summary>
/// <c>--fragment[=2s]</c> on <c>remux</c> and <c>encode</c> (Video CLI Build Plan task 26, V4.3 design Q11): fragmented MP4,
/// moov with the setup only and the media as moof and mdat pairs, each starting at a video keyframe after the length given.
/// Like <c>ffmpeg -movflags frag_keyframe+empty_moov+default_base_moof -frag_duration</c>.
/// </summary>
internal static class Fragments
{
    /// <summary>The fragment length that stands for "at every video keyframe": one tick, so any keyframe after the first starts one.</summary>
    private static readonly TimeSpan EveryKeyframe = TimeSpan.FromTicks(1);

    /// <summary>Creates the option.</summary>
    public static Option<string?> CreateOption() => new("--fragment")
    {
        Description = "Write fragmented MP4, for streaming and browsers' Media Source Extensions: fragments start at the first video keyframe after this long (2s without a value), or at every one with --fragment=keyframe. Give a length as --fragment=4s.",
        HelpName = "length",
        Arity = ArgumentArity.ZeroOrOne,
    };

    /// <summary>
    /// The fragment options <c>--fragment</c> asks for, or <see langword="null"/> when it is not given; <see cref="UsageException"/>
    /// when the output is not MP4.
    /// </summary>
    public static Mp4FragmentOptions? Parse(ParseResult result, Option<string?> option)
    {
        if (result.GetResult(option) is null)
        {
            return null;
        }

        Mp4FragmentOptions fragments = new();
        return result.GetValue(option) switch
        {
            null => fragments,
            "keyframe" or "keyframes" => fragments with { Duration = EveryKeyframe },
            { } text => fragments with { Duration = TimeSpan.FromSeconds(TimeValue.ParseDuration(text, "--fragment", allowFrames: false).Seconds) },
        };
    }

    /// <summary>
    /// The target for the tracks a job writes: fragments at every video keyframe of a file without video make one fragment
    /// (up to <see cref="Mp4FragmentOptions.MaxBytes"/>), as ffmpeg's frag_keyframe does, instead of one per audio packet.
    /// </summary>
    public static MediaTarget ForTracks(MediaTarget target, IEnumerable<TrackInfo> tracks)
    {
        return target.Container is Mp4Format { Fragments: { } fragments } mp4 && fragments.Duration == EveryKeyframe && !tracks.Any(t => t.Kind == TrackKind.Video)
            ? target with { Container = new Mp4Format { Fragments = fragments with { Duration = TimeSpan.FromDays(1) }, IgnoreEditLists = mp4.IgnoreEditLists } }
            : target;
    }

    /// <summary>The target with an MP4 container that writes fragments; other containers fail.</summary>
    public static MediaTarget Apply(MediaTarget target, Mp4FragmentOptions? fragments)
    {
        if (fragments is null)
        {
            return target;
        }

        return target.Container is Mp4Format mp4
            ? target with { Container = new Mp4Format { Fragments = fragments, IgnoreEditLists = mp4.IgnoreEditLists } }
            : throw new UsageException($"--fragment writes fragmented MP4; the output is {target.Container.Name}. Write .mp4, .m4v or .mov.");
    }
}
