using Egami.Video.Audio;

namespace Egami.Video.Tests.Audio;

/// <summary>Decodes every corpus clip and compares the samples with libFLAC's and FFmpeg's decode (V5.0 gates: FLAC bit-exact, WAV exact).</summary>
public class AudioCorpusTests
{
    public static IEnumerable<object[]> Clips => AudioCorpus.Clips;

    [Theory]
    [MemberData(nameof(Clips))]
    public void DecodesLikeTheReference(string name)
    {
        AudioCorpus.Reference expected = AudioCorpus.Manifest[name];
        using AudioReader reader = AudioReader.Open(AudioCorpus.PathOf(name), new AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Throw });
        Assert.Equal(expected.Rate, reader.Track.SampleRate);
        Assert.Equal(expected.Channels, reader.Track.Channels);
        (string md5, long samples) = AudioCorpus.Hash(reader.ReadFrames(), expected.Bits, expected.IsFloat);
        Assert.Equal(expected.Samples, samples);
        Assert.Equal(expected.Md5, md5);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ProbesTheTrack(string name)
    {
        AudioCorpus.Reference expected = AudioCorpus.Manifest[name];
        MediaInfo info = MediaFile.Probe(AudioCorpus.PathOf(name));
        AudioTrackInfo track = Assert.IsType<AudioTrackInfo>(Assert.Single(info.Tracks));
        Assert.Equal(expected.Rate, track.SampleRate);
        Assert.Equal(expected.Channels, track.Channels);
        Assert.NotNull(info.Duration);
        Assert.Equal(expected.Samples / (double)expected.Rate, info.Duration!.Value.Seconds, 2);
    }

    [Fact]
    public void ReadsWavTagsAndLayout()
    {
        MediaInfo tags = MediaFile.Probe(AudioCorpus.PathOf("wav-tags.wav"));
        Assert.Equal("Corpus tone", tags.Tags["TITLE"]);
        Assert.Equal("Egami", tags.Tags["ARTIST"]);
        AudioTrackInfo surround = (AudioTrackInfo)MediaFile.Probe(AudioCorpus.PathOf("wav-s24-51.wav")).Tracks[0];
        Assert.Equal(ChannelLayout.Surround51, surround.Layout);
        Assert.Equal(24, surround.BitsPerSample);
    }

    [Fact]
    public void ReadsFlacTagsAndPicture()
    {
        MediaInfo info = MediaFile.Probe(AudioCorpus.PathOf("flac-tags-picture.flac"));
        Assert.Equal("Corpus tone", info.Tags["TITLE"]);
        Assert.Equal("Egami", info.Tags["ARTIST"]);
        AttachmentInfo cover = Assert.Single(info.Attachments);
        Assert.True(cover.IsCoverArt);
        Assert.Equal("image/png", cover.MediaType);
    }

    [Fact]
    public void ChecksTheFlacMd5()
    {
        // A whole stream decoded from the start is checked against STREAMINFO's MD5; no warning means it matched.
        List<string> diagnostics = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = d => diagnostics.Add(d.Id) } };
        using (AudioReader reader = AudioReader.Open(AudioCorpus.PathOf("flac-l8-stereo.flac"), new AudioReaderOptions { Errors = DecodeErrorMode.Throw }, context))
        {
            foreach (AudioFrame frame in reader.ReadFrames())
            {
                frame.Dispose();
            }
        }

        Assert.DoesNotContain("Flac.Md5Mismatch", diagnostics);

        // Flip one residual bit: the frame's CRC-16 catches it.
        byte[] data = File.ReadAllBytes(AudioCorpus.PathOf("flac-l5-stereo.flac"));
        data[^200] ^= 0x10;
        diagnostics.Clear();
        using AudioReader broken = AudioReader.Open(new MemoryStream(data), new AudioReaderOptions(), context);
        foreach (AudioFrame frame in broken.ReadFrames())
        {
            frame.Dispose();
        }

        Assert.Contains(diagnostics, d => d is "Flac.BadCrc" or "Flac.CorruptFrame");
    }

    [Fact]
    public void IetfTestFilesDecodeLikeLibFlac()
    {
        // Fetched by make-audio-corpus.py --ietf; their terms are not recorded yet, so they are never committed.
        if (AudioCorpus.IetfDirectory is not { } directory)
        {
            Assert.Skip("Run python tools/video/make-audio-corpus.py --ietf to fetch the IETF FLAC test files.");
            return;
        }

        List<string> failures = [];
        foreach ((string name, AudioCorpus.Reference expected) in AudioCorpus.Load(Path.Combine(directory, "reference.json")))
        {
            if (expected.Md5.Length == 0)
            {
                continue;
            }

            try
            {
                using AudioReader reader = AudioReader.Open(Path.Combine(directory, name), new AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Throw });
                (string md5, long samples) = AudioCorpus.Hash(reader.ReadFrames(), expected.Bits, false);
                if (md5 != expected.Md5 || samples != expected.Samples)
                {
                    failures.Add($"{name}: {samples} samples, md5 {md5}; libFLAC {expected.Samples}, {expected.Md5}");
                }
            }
            catch (Exception e) when (e is EgamiException or InvalidOperationException or ArgumentException)
            {
                failures.Add($"{name}: {e.GetType().Name} {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
