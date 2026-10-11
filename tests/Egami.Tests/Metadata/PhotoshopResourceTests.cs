using System.Buffers.Binary;
using System.Security.Cryptography;

using Egami.Formats.Jpeg;
using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>
/// Phase 2.1.3 task 3: JPEG APP13 Photoshop resources. Read (joined across segments) into <see cref="JpegDetails.PhotoshopResources"/>
/// and <see cref="RasterMetadata.Iptc"/>, written back byte for byte when unedited, and after an IPTC edit rewritten with the new
/// IPTC and a matching digest while every other resource keeps its bytes.
/// </summary>
public class PhotoshopResourceTests
{
    public static TheoryData<string> JpegsWithIptc() => IptcTests.JpegsWithIptc();

    [Theory]
    [MemberData(nameof(JpegsWithIptc))]
    public void LoadReadsTheIptcAndSaveKeepsApp13(string file)
    {
        byte[] original = MetadataCorpus.Bytes(file);
        using Raster raster = Raster.Load(original);
        IptcData iptc = Assert.IsType<IptcData>(raster.Metadata.Iptc);
        Assert.Equal(IptcTests.IimFromJpeg(original), iptc.ToBytes().ToArray());
        JpegDetails details = Assert.IsType<JpegDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(Block(original), details.PhotoshopResources.ToArray());
        Assert.True(Raster.Probe(original).HasMetadata);

        byte[] saved = Save(raster);
        Assert.Equal(Segments(original), Segments(saved));
    }

    [Fact]
    public void UnparsedIptcIsWrittenBackToo()
    {
        // Load and save without touching Iptc: the lazy source bytes go back out.
        byte[] original = MetadataCorpus.Bytes("synthetic/iptc-large.jpg");
        using Raster raster = Raster.Load(original);
        byte[] saved = Save(raster);
        Assert.Equal(2, Segments(saved).Count);
        Assert.Equal(Segments(original), Segments(saved));
    }

    [Fact]
    public void AnEditReplacesTheIptcAndItsDigestOnly()
    {
        byte[] original = MetadataCorpus.Bytes("synthetic/photoshop.jpg");
        using Raster raster = Raster.Load(original);
        raster.Metadata.Iptc!.Set(IptcKey.Headline, "Edited headline");
        byte[] saved = Save(raster);

        Dictionary<ushort, byte[]> before = Resources(Block(original)), after = Resources(Block(saved));
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (ushort id in before.Keys.Where(id => id is not 0x0404 and not 0x0425))
        {
            Assert.Equal(before[id], after[id]);
        }

        Assert.Equal("Edited headline", IptcData.Parse(after[0x0404]).Headline);
        Assert.Equal(Md5(after[0x0404]), after[0x0425]);
        using Raster again = Raster.Load(saved);
        Assert.Equal("Edited headline", again.Metadata.Iptc!.Headline);
        Assert.Equal(["photoshop"], again.Metadata.Iptc.Keywords);
    }

    [Fact]
    public void LargeIptcIsSplitAcrossSegments()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/iptc-large.jpg"));
        raster.Metadata.Iptc!.Add(IptcKey.Keywords, "one more");
        byte[] saved = Save(raster);
        Assert.Equal(2, Segments(saved).Count);
        using Raster again = Raster.Load(saved);
        Assert.Equal(1401, again.Metadata.Iptc!.Keywords.Count);
        Assert.Equal("one more", again.Metadata.Iptc.Keywords[^1]);
    }

    [Fact]
    public void IptcWithoutABlockGetsOne()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/xp-tags.jpg"));
        Assert.Null(raster.Metadata.Iptc);
        IptcData iptc = new();
        iptc.Set(IptcKey.Caption, "New caption, ø");
        raster.Metadata.Iptc = iptc;
        byte[] saved = Save(raster);
        Dictionary<ushort, byte[]> resources = Resources(Block(saved));
        Assert.Equal([(ushort)0x0404, (ushort)0x0425], resources.Keys.Order());
        Assert.Equal(Md5(resources[0x0404]), resources[0x0425]);
        using Raster again = Raster.Load(saved);
        Assert.Equal("New caption, ø", again.Metadata.Iptc!.Caption);

        // Removing every dataset removes the resource and its digest; nothing is left to write.
        again.Metadata.Iptc = new IptcData();
        Assert.Empty(Segments(Save(again)));
    }

    [Fact]
    public void StripMetadataDropsApp13()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/photoshop.jpg"));
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info, new JpegWriterOptions { StripMetadata = true });
        Assert.Empty(Segments(stream.ToArray()));
    }

    [Fact]
    public void CopiesAreIndependent()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/iptc-utf8.jpg"));
        RasterMetadata copy = raster.Metadata.Copy();
        copy.Iptc!.Set(IptcKey.City, "Oslo");
        Assert.Equal("Bergen", raster.Metadata.Iptc!.Get(IptcKey.City));
    }

#pragma warning disable CA5351 // Photoshop's IPTC digest is MD5.
    private static byte[] Md5(byte[] data) => MD5.HashData(data);
#pragma warning restore CA5351

    private static byte[] Save(Raster raster)
    {
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info);
        return stream.ToArray();
    }

    /// <summary>The APP13 "Photoshop 3.0" segment payloads, in order.</summary>
    private static List<byte[]> Segments(byte[] jpeg)
    {
        List<byte[]> segments = [];
        for (int i = 2; i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA;)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(i + 2));
            if (jpeg[i + 1] == 0xED && jpeg.AsSpan(i + 4).StartsWith("Photoshop 3.0\0"u8))
            {
                segments.Add(jpeg.AsSpan(i + 4, length - 2).ToArray());
            }

            i += 2 + length;
        }

        return segments;
    }

    private static byte[] Block(byte[] jpeg) => [.. Segments(jpeg).SelectMany(s => s[14..])];

    private static Dictionary<ushort, byte[]> Resources(byte[] block)
    {
        Dictionary<ushort, byte[]> resources = [];
        for (int at = 0; at + 12 <= block.Length;)
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 4));
            int skip = 6 + ((block[at + 6] + 2) & ~1);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + skip));
            resources[id] = block.AsSpan(at + skip + 4, size).ToArray();
            at += skip + 4 + ((size + 1) & ~1);
        }

        return resources;
    }
}
