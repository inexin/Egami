using System.Buffers.Binary;

using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

/// <summary>Broken MP4 files: each raises its documented warning, and the part that can be read still demuxes.</summary>
public class Mp4BrokenFileTests
{
    private static byte[] Clip(string name) => File.ReadAllBytes(Path.Combine(Corpus.Directory, "mp4", name));

    private static (List<(int Track, long Pts, int Size)> Packets, List<MediaWarning> Warnings) Read(byte[] file, MediaLimits? limits = null)
    {
        List<MediaWarning> warnings = [];
        MediaContext context = MediaContext.Default with { Warnings = warnings.Add, Limits = limits ?? MediaLimits.Default };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file), context);
        List<(int, long, int)> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add((packet.TrackNumber, packet.Timestamp.Ticks, packet.Data.Length));
            }
        }

        return (packets, warnings);
    }

    /// <summary>Finds the <paramref name="n"/>th box of a type (from 0) by its four characters after a size.</summary>
    private static int Find(byte[] file, string type, int n = 0)
    {
        int at = -1;
        for (int i = 0; i <= n; i++)
        {
            at = file.AsSpan(at + 1).IndexOf(System.Text.Encoding.ASCII.GetBytes(type)) + at + 1;
            Assert.True(at > 0, $"no {type} number {n}");
        }

        return at - 4;
    }

    [Fact]
    public void ACutOffFileEndsAtTheCut()
    {
        byte[] whole = Clip("mp4-h264-faststart.mp4");
        byte[] cut = whole[..(whole.Length * 6 / 10)];
        var (all, _) = Read(whole);
        var (read, warnings) = Read(cut);
        Assert.Contains(warnings, w => w.Code == "mp4-truncated");
        Assert.InRange(read.Count, all.Count / 3, all.Count - 1);
        Assert.Equal(all.Where(p => read.Contains(p)), read);
    }

    [Fact]
    public void ABrokenTrackIsSkipped()
    {
        byte[] file = Clip("mp4-h264-aac.mp4");
        int mdia = Find(file, "mdia", 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mdia), 0x7FFFFFF0);
        var (read, warnings) = Read(file);
        Assert.Contains(warnings, w => w.Code == "mp4-bad-track");
        Assert.NotEmpty(read);
        Assert.All(read, p => Assert.Equal(1, p.Track));
    }

    [Fact]
    public void TablesThatDescribeMoreSamplesThanTheChunksHoldAreCut()
    {
        byte[] file = Clip("mov-mjpeg-sowt.mov");
        int stsz = Find(file, "stsz", 1);
        Assert.Equal(8000u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz + 16)));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(stsz + 16), 9000);
        var (read, warnings) = Read(file);
        Assert.Contains(warnings, w => w.Code == "mp4-sample-table" && w.TrackNumber == 2);
        Assert.Equal(8000, read.Where(p => p.Track == 2).Sum(p => p.Size) / 2);
    }

    [Fact]
    public void ABrokenFragmentIsSkipped()
    {
        byte[] file = Clip("mp4-frag.mp4");
        var (all, _) = Read(file);
        int trun = Find(file, "trun", 2);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(trun + 12), 1_000_000);
        var (read, warnings) = Read(file);
        Assert.Contains(warnings, w => w.Code == "mp4-bad-fragment");
        Assert.InRange(read.Count, 1, all.Count - 1);
        Assert.All(read, p => Assert.Contains(p, all));
    }

    [Fact]
    public void AFileWithoutMoovIsCorrupt()
    {
        byte[] file = Clip("mp4-mjpeg.mp4");
        int moov = Find(file, "moov");
        Assert.Throws<CorruptDataException>(() => Read(file[..moov]));
    }

    [Fact]
    public void MediaBeforeMoovNeedsASeekableStream()
    {
        byte[] file = Clip("mp4-mjpeg.mp4");
        Assert.True(Find(file, "mdat") < Find(file, "moov"));
        Assert.Throws<UnsupportedFormatException>(() => MediaFile.OpenDemuxer(new NonSeekableStream(file)));
    }

    [Fact]
    public void TableSizesAreLimited()
    {
        Assert.Throws<LimitExceededException>(() => Read(Clip("mov-mjpeg-sowt.mov"), MediaLimits.Default with { MaxIndexEntries = 1000 }));
        Assert.Throws<LimitExceededException>(() => Read(Clip("mp4-h264-aac.mp4"), MediaLimits.Default with { MaxTracks = 1 }));
    }
}
