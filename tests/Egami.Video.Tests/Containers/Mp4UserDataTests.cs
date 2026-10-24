using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Iso;
using Egami.Video.Containers;
using Egami.Video.Containers.Mp4;

namespace Egami.Video.Tests.Containers;

/// <summary>QuickTime udta lists that end with a 32-bit zero, as QuickTime 7 wrote them (Big Buck Bunny's 2008 .mov files do).</summary>
public sealed class Mp4UserDataTests
{
    [Fact]
    public void MovieUserDataEndingInAZeroTerminatorIsRead()
    {
        byte[] file = UserData(TextItem("©nam", "Bunny"), Item("SelO", [0]), terminator: true);
        IsoBoxReader reader = new(file, "QuickTime");
        Assert.True(reader.Next(out IsoBox udta));
        Dictionary<string, string> tags = [];
        Mp4Metadata.ReadMovie(reader, udta, tags, [], []);
        Assert.Equal("Bunny", tags["TITLE"]);
    }

    [Fact]
    public void TrackUserDataEndingInAZeroTerminatorIsRead()
    {
        byte[] file = UserData(Item("name", Encoding.UTF8.GetBytes("Video")), [], terminator: true);
        IsoBoxReader reader = new(file, "QuickTime");
        Assert.True(reader.Next(out IsoBox udta));
        Assert.Equal("Video", Mp4Metadata.ReadTrackName(reader, udta));
    }

    [Fact]
    public void NonZeroTrailingBytesAreStillCorrupt()
    {
        byte[] file = UserData(TextItem("©nam", "Bunny"), [], terminator: true);
        file[^1] = 1;
        Assert.Throws<CorruptDataException>(() =>
        {
            IsoBoxReader reader = new(file, "QuickTime");
            reader.Next(out IsoBox udta);
            Mp4Metadata.ReadMovie(reader, udta, [], [], []);
        });
    }

    private static byte[] UserData(byte[] first, byte[] second, bool terminator)
    {
        byte[] items = [.. first, .. second, .. terminator ? new byte[4] : []];
        return Item("udta", items);
    }

    private static byte[] TextItem(string type, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] content = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(content, (ushort)bytes.Length);
        bytes.CopyTo(content, 4);
        return Item(type, content);
    }

    private static byte[] Item(string type, byte[] content)
    {
        byte[] box = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), FourCC.Parse(type).Value);
        content.CopyTo(box, 8);
        return box;
    }
}
