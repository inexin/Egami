using System.Text;

using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>
/// Phase 2.1.3 task 5: <see cref="RasterMetadata.XmpData"/> as a live view of <see cref="RasterMetadata.Xmp"/>, and PNG XMP read
/// into <see cref="RasterMetadata.Xmp"/> (no longer text) and written from it.
/// </summary>
public class XmpLiveViewTests
{
    [Fact]
    public void PngXmpIsMetadataNotText()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/xmp.png"));
        Assert.False(raster.Metadata.Xmp.IsEmpty);
        Assert.DoesNotContain(raster.Metadata.Text, t => t.Key == "XML:com.adobe.xmp");
        Assert.Equal("Mandarin duck", raster.Metadata.XmpData!.Title);
        Assert.True(Raster.Probe(MetadataCorpus.Bytes("synthetic/xmp.png")).HasMetadata);
    }

    [Theory]
    [InlineData("synthetic/xmp.png", "png")]
    [InlineData("synthetic/xmp.gif", "gif")]
    [InlineData("synthetic/xmp-elements.jpg", "jpeg")]
    public void UneditedPacketsSurviveEveryFormat(string file, string format)
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes(file));
        byte[] packet = raster.Metadata.Xmp.ToArray();
        _ = raster.Metadata.XmpData!.Title;   // reading the view does not change the packet
        foreach (FormatInfo target in (FormatInfo[])[Format(format), PngFormat.Info, JpegFormat.Info, GifFormat.Info])
        {
            using Raster again = Raster.Load(Save(raster, target));
            Assert.Equal(packet, again.Metadata.Xmp.ToArray());
        }
    }

    [Fact]
    public void JpegToPngToJpegKeepsXmp()
    {
        using Raster jpeg = Raster.Load(MetadataCorpus.Bytes("synthetic/xmp-structures.jpg"));
        using Raster png = Raster.Load(Save(jpeg, PngFormat.Info));
        using Raster back = Raster.Load(Save(png, JpegFormat.Info));
        Assert.Equal(jpeg.Metadata.Xmp.ToArray(), back.Metadata.Xmp.ToArray());
    }

    [Fact]
    public void EditsToTheViewAreWhatIsSaved()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/xmp-elements.jpg"));
        byte[] original = raster.Metadata.Xmp.ToArray();
        XmpData view = raster.Metadata.XmpData!;
        Assert.Same(view, raster.Metadata.XmpData);
        Assert.Equal(original, raster.Metadata.Xmp.ToArray());

        view.Keywords = ["edited"];
        Assert.NotEqual(original, raster.Metadata.Xmp.ToArray());
        foreach (FormatInfo format in (FormatInfo[])[JpegFormat.Info, PngFormat.Info, GifFormat.Info])
        {
            using Raster again = Raster.Load(Save(raster, format));
            Assert.Equal(["edited"], again.Metadata.XmpData!.Keywords);
            Assert.Equal("Mandarin duck", again.Metadata.XmpData.Title);
        }

        // Setting the bytes drops the view; a new view parses them.
        raster.Metadata.Xmp = original;
        Assert.NotSame(view, raster.Metadata.XmpData);
        Assert.Equal(["duck", "bird", "water"], raster.Metadata.XmpData!.Keywords);

        // Copies get their own view.
        RasterMetadata copy = raster.Metadata.Copy();
        copy.XmpData!.Label = "Copy";
        Assert.Null(raster.Metadata.XmpData.Label);
    }

    [Fact]
    public void AnEmptyViewStartsAPacket()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/iptc-utf8.jpg"));
        Assert.True(raster.Metadata.Xmp.IsEmpty);
        Assert.Null(raster.Metadata.XmpData);
        raster.Metadata.XmpData = new XmpData { Title = "Started" };
        using Raster again = Raster.Load(Save(raster, JpegFormat.Info));
        Assert.Equal("Started", again.Metadata.XmpData!.Title);

        raster.Metadata.XmpData = null;
        Assert.True(raster.Metadata.Xmp.IsEmpty);
    }

    [Fact]
    public void ABrokenPacketHasNoViewAndKeepsItsBytes()
    {
        RasterMetadata metadata = new() { Xmp = Encoding.UTF8.GetBytes("<x:xmpmeta") };
        Assert.Null(metadata.XmpData);
        Assert.Single(metadata.Warnings);
        Assert.Equal("<x:xmpmeta", Encoding.UTF8.GetString(metadata.Xmp.Span));
    }

    private static FormatInfo Format(string name) => name switch { "png" => PngFormat.Info, "gif" => GifFormat.Info, _ => JpegFormat.Info };

    private static byte[] Save(Raster raster, FormatInfo format)
    {
        using MemoryStream stream = new();
        raster.Save(stream, format);
        return stream.ToArray();
    }
}
