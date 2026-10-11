using System.Text;
using System.Text.Json;

using Egami.Formats.Jpeg;
using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>Phase 2.1.3 task 7: the Windows XP tags and the encoded strings read as ExifTool reads them, and written back.</summary>
public class ExifStringTests
{
    private static readonly (string Tag, ExifKey Key)[] Tags =
    [
        ("IFD0:XPTitle", ExifKey.XPTitle),
        ("IFD0:XPComment", ExifKey.XPComment),
        ("IFD0:XPAuthor", ExifKey.XPAuthor),
        ("IFD0:XPKeywords", ExifKey.XPKeywords),
        ("IFD0:XPSubject", ExifKey.XPSubject),
        ("ExifIFD:UserComment", ExifKey.UserComment),
        ("GPS:GPSProcessingMethod", ExifKey.GpsProcessingMethod),
        ("GPS:GPSAreaInformation", ExifKey.GpsAreaInformation),
    ];

    public static TheoryData<string> Files() =>
        [.. MetadataCorpus.Files().Where(f => f.EndsWith(".jpg", StringComparison.Ordinal) && Tags.Any(t => MetadataCorpus.Dump(f).TryGetProperty(t.Tag, out _)))];

    [Theory]
    [MemberData(nameof(Files))]
    public void TextMatchesExifTool(string file)
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes(file));
        ExifData exif = raster.Metadata.Exif!;
        JsonElement dump = MetadataCorpus.Dump(file);
        int checkedTags = 0;
        foreach ((string tag, ExifKey key) in Tags)
        {
            if (dump.TryGetProperty(tag, out JsonElement expected))
            {
                Assert.True(exif.TryGet(key, out ExifEntry? entry), tag);
                Assert.Equal(expected.GetString(), entry.AsString());
                checkedTags++;
            }
        }

        Assert.True(checkedTags > 0);
    }

    [Fact]
    public void SetWritesWhatExplorerAndCamerasWrite()
    {
        foreach (bool bigEndian in (bool[])[false, true])
        {
            using Raster raster = Raster.Create<Egami.Pixels.Rgb8>(8, 8);
            ExifData exif = ExifData.Parse(bigEndian ? [.. "MM\0*"u8, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0] : [.. "II*\0"u8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            raster.Metadata.Exif = exif;
            exif.Set(ExifKey.XPTitle, "Tittel æøå 水");
            exif.Set(ExifKey.UserComment, "Kommentar ø");
            exif.Set(ExifKey.GpsProcessingMethod, "GPS");
            using MemoryStream stream = new();
            raster.Save(stream, JpegFormat.Info);
            using Raster again = Raster.Load(stream.ToArray());
            ExifData read = again.Metadata.Exif!;
            Assert.Equal(bigEndian, read.IsBigEndian);

            Assert.True(read.TryGet(ExifKey.XPTitle, out ExifEntry? title));
            Assert.Equal(ExifType.Byte, title.Type);
            Assert.Equal([.. Encoding.Unicode.GetBytes("Tittel æøå 水"), 0, 0], title.RawValue.ToArray());   // little-endian in both blocks
            Assert.Equal("Tittel æøå 水", title.AsString());

            Assert.True(read.TryGet(ExifKey.UserComment, out ExifEntry? comment));
            Assert.Equal(ExifType.Undefined, comment.Type);
            Assert.True(comment.RawValue.Span.StartsWith("UNICODE\0"u8));
            Assert.Equal("Kommentar ø", comment.AsString());
            Assert.True(read.TryGet(ExifKey.GpsProcessingMethod, out ExifEntry? method));
            Assert.Equal("ASCII\0\0\0GPS"u8.ToArray(), method.RawValue.ToArray());
            Assert.Equal("GPS", method.AsString());
        }
    }

    [Fact]
    public void UnicodeInTheOtherByteOrderStillReads()
    {
        // A little-endian block whose camera wrote UTF-16 big-endian.
        ExifData exif = new();
        exif.Set(ExifKey.UserComment, ExifType.Undefined, [.. "UNICODE\0"u8, .. Encoding.BigEndianUnicode.GetBytes("Hello, world")]);
        Assert.True(exif.TryGet(ExifKey.UserComment, out ExifEntry? entry));
        Assert.Equal("Hello, world", entry.AsString());
    }

    [Fact]
    public void UndefinedAndJisPrefixesRead()
    {
        ExifData exif = new();
        exif.Set(ExifKey.UserComment, ExifType.Undefined, [0, 0, 0, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes("udefinert ø"), 0, 0]);
        Assert.True(exif.TryGet(ExifKey.UserComment, out ExifEntry? undefinedCode));
        Assert.Equal("udefinert ø", undefinedCode.AsString());

        // Without the code page provider, JIS reads as the bytes do; with it (an app's choice), as Shift-JIS.
        exif.Set(ExifKey.UserComment, ExifType.Undefined, [.. "JIS\0\0\0\0\0"u8, .. "ABC"u8]);
        Assert.True(exif.TryGet(ExifKey.UserComment, out ExifEntry? jis));
        Assert.Equal("ABC", jis.AsString());

        // Too short for a prefix: read as plain bytes, as 1.0 did.
        exif.Set(ExifKey.UserComment, ExifType.Undefined, "abc"u8);
        Assert.True(exif.TryGet(ExifKey.UserComment, out ExifEntry? shortValue));
        Assert.Equal("abc", shortValue.AsString());
    }
}
