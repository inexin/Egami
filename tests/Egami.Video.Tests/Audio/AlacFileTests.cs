using System.Text;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.9 stage C, task 10: ALAC in MP4, QuickTime, Matroska and CAF. FFmpeg's files in tests/video/corpus/audio/alac-files (see
/// tools/video/make-audio-corpus.py --alac) must decode to FFmpeg's samples, and Egami.Video's own files must give back what went in.
/// </summary>
public class AlacFileTests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "alac-files");

    private static readonly Dictionary<string, AudioCorpus.Reference> Manifest = AudioCorpus.Load(Path.Combine(Folder, "manifest.json"));

    public static IEnumerable<object[]> Clips => Manifest.Keys.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Clips))]
    public void DecodesLikeFfmpeg(string name)
    {
        AudioCorpus.Reference expected = Manifest[name];
        using AudioReader reader = AudioReader.Open(Path.Combine(Folder, name), new AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Throw });
        Assert.Equal(CodecIds.Alac, reader.Track.CodecId);
        Assert.Equal(expected.Rate, reader.Track.SampleRate);
        Assert.Equal(expected.Channels, reader.Track.Channels);
        Assert.Equal(expected.Bits, reader.Track.BitsPerSample);
        (string md5, long samples) = AudioCorpus.Hash(reader.ReadFrames(), expected.Bits, expected.IsFloat);
        Assert.Equal(expected.Samples, samples);
        Assert.Equal(expected.Md5, md5);
    }

    [Theory]
    [InlineData("m4a", 2, 16, 44100)]
    [InlineData("m4a", 6, 24, 48000)]
    [InlineData("m4a", 2, 24, 96000)]
    [InlineData("mov", 1, 16, 22050)]
    [InlineData("mka", 8, 24, 48000)]
    [InlineData("mka", 3, 20, 32000)]
    [InlineData("caf", 4, 32, 44100)]
    public void RoundTripsLosslessly(string extension, int channels, int bits, int rate)
    {
        using AudioFrame input = AudioRoundTripTests.Signal(channels, bits, rate, samples: 10007);
        string path = AudioRoundTripTests.OutputPath($"alac-{channels}-{bits}-{rate}.{extension}");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = AlacCodec.Instance }))
        {
            writer.Write(input);
        }

        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(path).Tracks[0];
        Assert.Equal(CodecIds.Alac, track.CodecId);
        Assert.Equal(24, track.CodecPrivate.Length);
        Assert.Equal(rate, track.SampleRate);
        Assert.Equal(channels, track.Channels);
        Assert.Equal(bits, track.BitsPerSample);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Fact]
    public void WritesTheAlacBoxLikeFfmpeg()
    {
        // FFmpeg's m4a and Egami.Video's remux of it carry the same alac box: version and flags, then ALACSpecificConfig.
        string source = Path.Combine(Folder, "m4a-alac-24-51.m4a");
        using MemoryStream output = new();
        MediaFile.Remux(File.OpenRead(source), output, "m4a", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AlacBox(File.ReadAllBytes(source)), AlacBox(output.ToArray()));
    }

    [Fact]
    public void RefusesATrackWithoutItsConfig()
    {
        AudioTrackInfo track = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.Alac, TimeBase = new Rational(1, 44100), SampleRate = 44100, Channels = 2 };
        Assert.Throws<NotSupportedException>(() => Mp4Format.Instance.CreateMuxer(new MemoryStream(), [track], MediaContext.Default, leaveOpen: false));
    }

    /// <summary>The body of the alac box inside the alac sample entry (the second "alac" in the file).</summary>
    private static byte[] AlacBox(byte[] file)
    {
        byte[] type = Encoding.ASCII.GetBytes("alac");
        int entry = file.AsSpan().IndexOf(type);
        int at = entry + 4 + file.AsSpan(entry + 4).IndexOf(type);
        Assert.True(entry >= 4 && at > entry, "No alac box.");
        int size = (file[at - 4] << 24) | (file[at - 3] << 16) | (file[at - 2] << 8) | file[at - 1];
        return file[(at + 4)..(at - 4 + size)];
    }
}
