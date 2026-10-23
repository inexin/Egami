using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

/// <summary>Task 17: tags, chapters and attachments read, written and kept through remux.</summary>
public class MetadataTests
{
    [Fact]
    public void MatroskaTagsChaptersAndAttachmentsAreRead()
    {
        MediaInfo info = MediaFile.Probe(Corpus.PathOf("mkv-metadata.mkv"));
        Assert.Equal("Corpus clip", info.Tags["TITLE"]);
        Assert.Equal("Egami", info.Tags["ARTIST"]);
        Assert.Equal("Test clip", info.Tags["COMMENT"]);
        Assert.Equal(["Opening", "Rest"], info.Chapters.Select(c => c.Title));
        Assert.Equal(MediaTime.FromSeconds(0.8), info.Chapters[1].Start);
        Assert.Equal(MediaTime.FromSeconds(2), info.Chapters[1].End);
        AttachmentInfo cover = Assert.Single(info.Attachments);
        Assert.Equal(("cover.jpg", "image/jpeg", true), (cover.Name, cover.MediaType, cover.IsCoverArt));
        Assert.Equal(0xFF, cover.Data.Span[0]);
        AudioTrackInfo audio = info.Tracks.OfType<AudioTrackInfo>().Single();
        Assert.Equal(("nor", "Tone"), (audio.Language, audio.Name));
        Assert.Contains("DURATION", audio.Tags.Keys);
    }

    [Fact]
    public void MatroskaRemuxKeepsTagsChaptersAndAttachments()
    {
        string output = AviWriterTests.TempFile("metadata-remux.mkv");
        MediaFile.Remux(Corpus.PathOf("mkv-metadata.mkv"), output, cancellationToken: TestContext.Current.CancellationToken);
        MediaInfo source = MediaFile.Probe(Corpus.PathOf("mkv-metadata.mkv")), copy = MediaFile.Probe(output);
        Assert.Equal(source.Tags.Where(t => t.Key is not ("MUXINGAPP" or "WRITINGAPP")).OrderBy(t => t.Key), copy.Tags.Where(t => t.Key is not ("MUXINGAPP" or "WRITINGAPP")).OrderBy(t => t.Key));
        Assert.Equal(source.Chapters, copy.Chapters);
        Assert.Equal(source.Attachments.Select(a => (a.Name, a.MediaType, a.Data.ToArray().Length)), copy.Attachments.Select(a => (a.Name, a.MediaType, a.Data.ToArray().Length)));
        Assert.True(source.Attachments[0].Data.Span.SequenceEqual(copy.Attachments[0].Data.Span));
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            Assert.Equal(source.Tracks[i].Tags.OrderBy(t => t.Key), copy.Tracks[i].Tags.OrderBy(t => t.Key));
            Assert.Equal((source.Tracks[i].Language, source.Tracks[i].Name), (copy.Tracks[i].Language, copy.Tracks[i].Name));
        }
    }

    [Fact]
    public void AviInfoTagsMapToMatroskaNamesAndBack()
    {
        MediaInfo avi = MediaFile.Probe(Corpus.PathOf("avi-tags.avi"));
        Assert.Equal("Corpus clip", avi.Tags["TITLE"]);
        Assert.Equal("Egami", avi.Tags["ARTIST"]);
        Assert.Equal("MIT", avi.Tags["COPYRIGHT"]);
        Assert.StartsWith("Lavf", avi.Tags["ENCODER"], StringComparison.Ordinal);

        string mkv = AviWriterTests.TempFile("avi-tags.mkv"), back = AviWriterTests.TempFile("avi-tags-back.avi");
        MediaFile.Remux(Corpus.PathOf("avi-tags.avi"), mkv, cancellationToken: TestContext.Current.CancellationToken);
        MediaFile.Remux(mkv, back, cancellationToken: TestContext.Current.CancellationToken);
        foreach (string path in (string[])[mkv, back])
        {
            MediaInfo copy = MediaFile.Probe(path);
            foreach ((string name, string value) in avi.Tags)
            {
                Assert.Equal(value, copy.Tags[name]);
            }
        }
    }

    [Fact]
    public void AviReportsWhatItCannotHold()
    {
        List<MediaWarning> warnings = [];
        MediaContext context = MediaContext.Default with { Warnings = warnings.Add };
        string output = AviWriterTests.TempFile("metadata.avi");
        MediaFile.Remux(Corpus.PathOf("mkv-metadata.mkv"), output, context: context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(warnings, w => w.Code == "metadata-dropped");
        Assert.Equal("Corpus clip", MediaFile.Probe(output).Tags["TITLE"]);
    }
}
