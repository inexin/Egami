using System.Text;

using Egami.Video.Containers;
using Egami.Video.Containers.Mp4;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.10: AAC encoder delay in MP4, read from the edit list or from iTunes's iTunSMPB tag and written as both, against FFmpeg's
/// clips in tests/video/corpus/audio/aac (0.3 s of audio, with FFmpeg's 1024 samples of priming).
/// </summary>
public class AacPrimingTests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "aac");

    // The iTunSMPB clip's 14 frames as its sample table times them: FFmpeg gives the last one only its 942 real samples.
    private const long MediaSamples = (13 * 1024) + 942;

    [Theory]
    [InlineData("ff-lc-stereo.m4a", 44100)]
    [InlineData("ff-lc-51.mp4", 48000)]
    [InlineData("ff-lc-itunsmpb.m4a", 44100)]
    public void ShowsOnlyTheSourceSamples(string name, int rate)
    {
        List<MediaWarning> warnings = [];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(Folder, name), MediaContext.Default with { Warnings = warnings.Add });
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        (long first, long shown, bool discarded) = Shown(demuxer);

        // The priming packet starts 1024 samples before zero and is not shown; the last one stops at the source's end.
        long source = rate * 3 / 10;
        Assert.Equal(-1024, first);
        Assert.True(discarded);
        Assert.Equal(source, shown);
        Assert.Equal(source, track.Duration!.Value.Rescale(new Rational(1, rate)).Ticks);
        Assert.Empty(warnings);
    }

    [Fact]
    public void LeavesTheTagAloneWhenEditListsAreIgnored()
    {
        Mp4Format format = new() { IgnoreEditLists = true };
        using IDemuxer demuxer = format.OpenDemuxer(File.OpenRead(Path.Combine(Folder, "ff-lc-itunsmpb.m4a")), MediaContext.Default, leaveOpen: false);
        Assert.Equal((0, MediaSamples, false), Shown(demuxer));
    }

    [Fact]
    public void WarnsAboutATagThatDoesNotFit()
    {
        // The length field says 0xFFFFF samples, more than the 14 frames hold.
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, "ff-lc-itunsmpb.m4a"));
        byte[] length = Encoding.ASCII.GetBytes("00000000000033AE");
        int at = file.AsSpan().IndexOf(length);
        Encoding.ASCII.GetBytes("00000000000FFFFF").CopyTo(file, at);
        List<MediaWarning> warnings = [];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file), MediaContext.Default with { Warnings = warnings.Add });
        Assert.Equal((0, MediaSamples, false), Shown(demuxer));
        Assert.Equal("mp4-itunsmpb", Assert.Single(warnings).Code);
    }

    [Theory]
    [InlineData("ff-lc-stereo.m4a")]
    [InlineData("ff-lc-itunsmpb.m4a")]
    public void WritesTheEditListAndTheTag(string name)
    {
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, name)), output, "m4a", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        byte[] file = output.ToArray();

        // Priming 1024, padding 82, 13230 samples: what Apple's encoder would write for these packets.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal(" 00000000 00000400 00000052 00000000000033AE 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            demuxer.Info.Tags[Mp4Gapless.TagName]);
        Assert.Equal((-1024, 13230, true), Shown(demuxer));
        Assert.True(file.AsSpan().IndexOf("elst"u8) > 0);
        Assert.Equal(1, Count(file, "iTunSMPB"u8));

        // Without the edit list (its edts box renamed to free), the tag alone gives the same window.
        byte[] noEdits = file.ToArray();
        int elst = noEdits.AsSpan().IndexOf("edts"u8);
        "free"u8.CopyTo(noEdits.AsSpan(elst));
        using IDemuxer tagOnly = MediaFile.OpenDemuxer(new MemoryStream(noEdits));
        Assert.Equal((-1024, 13230, true), Shown(tagOnly));
    }

    [Fact]
    public void WritesNoTagWithoutPriming()
    {
        // Raw ADTS has no delay to state.
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(Path.Combine(Folder, "ff-lc-stereo-44k.aac")), output, "m4a", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, Count(output.ToArray(), "iTunSMPB"u8));
    }

    [Theory]
    [InlineData(" 00000000 00000840 000001CA 00000000003F31F6 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", 2112L, 458L, 4141558L)]
    [InlineData("00000000 00000400 00000000 0000000000000800", 1024L, 0L, 2048L)]
    public void ParsesTheTag(string value, long priming, long padding, long samples)
    {
        Assert.Equal((priming, padding, samples), Mp4Gapless.Parse(value));
        Assert.Equal((priming, padding, samples), Mp4Gapless.Parse(Mp4Gapless.Format(priming, padding, samples)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 00000000 00000840")]
    [InlineData(" 00000000 0000084G 000001CA 00000000003F31F6")]
    public void RefusesBrokenTags(string value) => Assert.Null(Mp4Gapless.Parse(value));

    /// <summary>The first packet's start, the samples shown from zero on, and whether the first packet was marked discardable.</summary>
    private static (long First, long Shown, bool Discarded) Shown(IDemuxer demuxer)
    {
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        Rational samples = new(1, track.SampleRate);
        long? first = null;
        long shown = 0;
        bool discarded = false;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                long start = packet.Timestamp.Rescale(samples).Ticks;
                long end = start + packet.Duration!.Value.Rescale(samples).Ticks;
                if (first is null)
                {
                    first = start;
                    discarded = packet.IsDiscardable;
                }

                shown += Math.Max(0, end - Math.Max(start, 0));
            }
        }

        return (first ?? 0, shown, discarded);
    }

    private static int Count(byte[] file, ReadOnlySpan<byte> text)
    {
        int count = 0;
        for (int at = file.AsSpan().IndexOf(text); at >= 0; count++)
        {
            int next = file.AsSpan(at + 1).IndexOf(text);
            at = next < 0 ? -1 : at + 1 + next;
        }

        return count;
    }
}
