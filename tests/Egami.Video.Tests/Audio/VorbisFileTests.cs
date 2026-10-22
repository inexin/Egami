using System.IO.Compression;
using System.Text.Json;

using Egami.Video.Audio;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// Vorbis files read through AudioReader, from Ogg and from FFmpeg's WebM, Matroska and MP4 remuxes, against libvorbisfile's
/// float output (the V5.5 gate: every sample within 2^-16 of full scale, the same number of samples).
/// </summary>
public class VorbisFileTests
{
    private const float Tolerance = 1f / 65536;

    public static TheoryData<string> Clips => [.. Manifest().Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Clips))]
    public void OggDecodesLikeLibvorbis(string name) => AssertLikeLibvorbis(name, name);

    [Theory]
    [InlineData("q6-stereo-48k.mka")]
    [InlineData("q6-stereo-48k.webm")]
    [InlineData("q6-stereo-48k.mp4")]
    public void OtherContainersDecodeLikeLibvorbis(string file) =>

        // FFmpeg counts 128 samples (half a short block) for the first packet, which outputs none, so its DiscardPadding and
        // edit list end the stream that much early.
        AssertLikeLibvorbis(file, "q6-stereo-48k.ogg", missing: 128);

    [Fact]
    public void RemuxesBetweenOggAndWebm()
    {
        string webm = AudioRoundTripTests.OutputPath("vorbis-remux.webm");
        string ogg = AudioRoundTripTests.OutputPath("vorbis-remux.ogg");
        MediaFile.Remux(Path.Combine(VorbisTests.Directory, "q3-stereo-44k-bursts.ogg"), webm);
        Assert.Equal(5292, Decode(webm)[0].Length);
        MediaFile.Remux(webm, ogg);
        AssertLikeLibvorbis(ogg, "q3-stereo-44k-bursts.ogg", absolute: true);
    }

    [Fact]
    public void RemuxesToMp4()
    {
        string mp4 = AudioRoundTripTests.OutputPath("vorbis-remux.mp4");
        MediaFile.Remux(Path.Combine(VorbisTests.Directory, "q10-stereo-44k-noise.ogg"), mp4);
        AssertLikeLibvorbis(mp4, "q10-stereo-44k-noise.ogg", absolute: true);
    }

    [Fact]
    public void SeeksToTheSample()
    {
        string path = Path.Combine(VorbisTests.Directory, "q6-stereo-48k.ogg");
        float[] all = Decode(path)[0];
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { Errors = DecodeErrorMode.Throw });
        foreach (long target in new long[] { 2400, 600, 4000 })
        {
            reader.Seek(new MediaTime(target, new Rational(1, 48000)));
            Assert.True(reader.TryRead(out AudioFrame? frame));
            using (frame)
            {
                Assert.Equal(new MediaTime(target, new Rational(1, 48000)), frame.Timestamp);

                // One block of pre-roll gives the overlap back, so the samples equal a decode from the start.
                ReadOnlySpan<float> got = frame.AsSpan<float>(0);
                for (int i = 0; i < Math.Min(got.Length, all.Length - (int)target); i++)
                {
                    Assert.InRange(got[i] - all[target + i], -Tolerance, Tolerance);
                }
            }
        }
    }

    [Fact]
    public void ReportsTheTrack()
    {
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(Path.Combine(VorbisTests.Directory, "q3-51-16k.ogg")).Tracks[0];
        Assert.Equal(CodecIds.Vorbis, track.CodecId);
        Assert.Equal(6, track.Channels);
        using AudioReader reader = AudioReader.Open(Path.Combine(VorbisTests.Directory, "q3-51-16k.ogg"));
        Assert.Equal(ChannelLayout.Surround51, reader.Layout);
    }

    private static void AssertLikeLibvorbis(string file, string reference, bool absolute = false, int missing = 0)
    {
        Dictionary<string, JsonElement> manifest = Manifest();
        int channels = manifest[reference].GetProperty("channels").GetInt32();
        float[] expected = ReadReference(Path.Combine(VorbisTests.Directory, reference[..^4] + ".f32.gz"));
        float[][] got = Decode(absolute ? file : Path.Combine(VorbisTests.Directory, file));
        Assert.Equal(channels, got.Length);
        int samples = expected.Length / channels - missing;
        Assert.Equal(samples, got[0].Length);
        (_, int[] order) = Egami.Video.Audio.Codecs.VorbisChannelOrder.Layout(channels);
        float worst = 0;
        for (int i = 0; i < samples; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                worst = Math.Max(worst, Math.Abs(got[order[c]][i] - expected[(i * channels) + c]));
            }
        }

        Assert.True(worst < Tolerance, $"{file}: samples differ from libvorbis by up to {worst:E2}.");
    }

    private static float[][] Decode(string path)
    {
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { Errors = DecodeErrorMode.Throw });
        List<float>[] channels = [.. Enumerable.Range(0, reader.Layout.ChannelCount).Select(_ => new List<float>())];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    channels[c].AddRange(frame.AsSpan<float>(c).ToArray());
                }
            }
        }

        return [.. channels.Select(c => c.ToArray())];
    }

    private static Dictionary<string, JsonElement> Manifest() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(VorbisTests.Directory, "manifest.json")))!;

    private static float[] ReadReference(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw.ToArray())];
    }
}
