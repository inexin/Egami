using System.Buffers.Binary;

using Egami.Formats.Iso;

namespace Egami.Tests.Formats.Iso;

public class IsoBoxReaderTests
{
    private static byte[] Box(string type, params byte[] content)
    {
        byte[] box = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), FourCC.Parse(type).Value);
        content.CopyTo(box, 8);
        return box;
    }

    [Fact]
    public void FourCCParsesAndPrints()
    {
        FourCC moov = FourCC.Parse("moov");
        Assert.Equal(0x6D6F6F76u, moov.Value);
        Assert.Equal("moov", moov.ToString());
        Assert.Equal(@"\xa9nam", FourCC.Parse("©nam").ToString());
        Assert.Equal(0x6D6F6F76u, (uint)moov);
        Assert.False(FourCC.TryParse("moo", out _));
        Assert.False(FourCC.TryParse("moĀv", out _));
        Assert.Throws<FormatException>(() => FourCC.Parse("toolong"));
    }

    [Fact]
    public void WalksLevelsWithOffsets()
    {
        byte[] file = [.. Box("ftyp", [1, 2, 3, 4]), .. Box("meta", [.. new byte[4], .. Box("hdlr", [9]), .. Box("pitm")]), .. Box("free")];
        IsoBoxReader top = new(file, "Test");
        List<IsoBox> boxes = [];
        while (top.Next(out IsoBox box))
        {
            boxes.Add(box);
        }

        Assert.Equal(["ftyp", "meta", "free"], boxes.Select(b => b.Type.ToString()));
        Assert.Equal(new IsoBox(FourCC.Parse("ftyp"), 0, 8, 12), boxes[0]);
        Assert.Equal([1, 2, 3, 4], top.Content(boxes[0]).ToArray());

        IsoBoxReader children = top.Children(boxes[1], skip: 4);
        Assert.True(children.Next(out IsoBox hdlr));
        Assert.Equal(new IsoBox(FourCC.Parse("hdlr"), 24, 32, 33), hdlr);
        Assert.Equal([9], children.Content(hdlr).ToArray());
        Assert.True(children.Next(out IsoBox pitm));
        Assert.Equal(FourCC.Parse("pitm"), pitm.Type);
        Assert.False(children.Next(out _));
    }

    [Fact]
    public void ReportsFileOffsetsFromAnOrigin()
    {
        byte[] moov = [.. Box("mvhd", [7]), .. Box("trak")];
        IsoBoxReader reader = new(moov, "MP4", offset: 1000);
        Assert.True(reader.Next(out IsoBox mvhd));
        Assert.Equal((1000L, 1008L, 1009L), (mvhd.Start, mvhd.ContentStart, mvhd.End));
        Assert.Equal([7], reader.Content(mvhd).ToArray());
        Assert.True(reader.Next(out IsoBox trak));
        Assert.Equal(1009, trak.Start);

        byte[] bad = [.. Box("mvhd"), 0, 0, 0, 99, (byte)'t', (byte)'r', (byte)'a', (byte)'k'];
        CorruptDataException e = Assert.Throws<CorruptDataException>(() =>
        {
            IsoBoxReader r = new(bad, "MP4", offset: 1000);
            while (r.Next(out _))
            {
            }
        });
        Assert.Equal(1008, e.Offset);
    }

    [Fact]
    public void ReadsLargeSizesSizeZeroAndUuid()
    {
        byte[] large = [0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 0, 0, 0, 0, 0, 0, 0, 18, 0xAA, 0xBB];
        Guid id = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
        byte[] uuid = [0, 0, 0, 25, (byte)'u', (byte)'u', (byte)'i', (byte)'d', .. id.ToByteArray(bigEndian: true), 5];
        byte[] rest = [0, 0, 0, 0, (byte)'f', (byte)'r', (byte)'e', (byte)'e', 1, 2, 3];
        byte[] file = [.. large, .. uuid, .. rest];
        IsoBoxReader reader = new(file, "Test");

        Assert.True(reader.Next(out IsoBox mdat));
        Assert.Equal((16L, 18L), (mdat.ContentStart, mdat.End));
        Assert.Null(mdat.UserType);
        Assert.True(reader.Next(out IsoBox u));
        Assert.Equal(id, u.UserType);
        Assert.Equal([5], reader.Content(u).ToArray());
        Assert.True(reader.Next(out IsoBox free));
        Assert.Equal(file.Length, free.End);
        Assert.Equal(3, free.ContentLength);
        Assert.False(reader.Next(out _));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 8, 1, 2 }, true)] // header cut off
    [InlineData(new byte[] { 0, 0, 0, 4, 1, 2, 3, 4 }, false)] // smaller than its header
    [InlineData(new byte[] { 0, 0, 0, 9, 1, 2, 3, 4 }, true)] // past the end
    [InlineData(new byte[] { 0, 0, 0, 1, 1, 2, 3, 4, 0, 0 }, true)] // large size cut off
    [InlineData(new byte[] { 0, 0, 0, 24, (byte)'u', (byte)'u', (byte)'i', (byte)'d', 0, 0 }, true)] // extended type cut off
    public void RejectsBrokenHeaders(byte[] data, bool truncation)
    {
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => new IsoBoxReader(data, "Test").Next(out _));
        Assert.Equal("Test", e.FormatName);
        Assert.Equal(0, e.Offset);
        Assert.Equal(truncation, e.IsTruncation);
    }

    [Fact]
    public void ChildrenCheckTheSkip()
    {
        byte[] file = Box("meta", [0, 0]);
        Assert.Throws<CorruptDataException>(() => Meta(file, out IsoBox meta).Children(meta, skip: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Meta(file, out IsoBox meta).Children(meta, skip: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Meta(file, out IsoBox meta).Content(meta with { End = 100 }).Length);

        static IsoBoxReader Meta(byte[] file, out IsoBox meta)
        {
            IsoBoxReader reader = new(file, "Test");
            Assert.True(reader.Next(out meta));
            return reader;
        }
    }

    [Fact]
    public void ReadsHeadersFromAStream()
    {
        byte[] file = [.. Box("ftyp", [1, 2, 3, 4]), 0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 0, 0, 0, 1, 0, 0, 0, 0];
        IsoBox ftyp = IsoBoxReader.ReadHeader(file, 0, long.MaxValue, "MP4");
        Assert.Equal(12, ftyp.End);

        // A 4 GB mdat is described from its 16 header bytes alone.
        IsoBox mdat = IsoBoxReader.ReadHeader(file.AsSpan(12), 12, long.MaxValue, "MP4");
        Assert.Equal((FourCC.Parse("mdat"), 28L, 12 + 0x1_0000_0000L), (mdat.Type, mdat.ContentStart, mdat.End));
        Assert.Throws<CorruptDataException>(() => IsoBoxReader.ReadHeader(file.AsSpan(12), 12, 100, "MP4"));

        // Size 0 runs to the end given.
        IsoBox last = IsoBoxReader.ReadHeader([0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t'], 50, 500, "MP4");
        Assert.Equal(500, last.End);
    }
}
