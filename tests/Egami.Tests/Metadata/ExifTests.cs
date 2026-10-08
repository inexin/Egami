using System.Buffers.Binary;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Metadata;

/// <summary>
/// Typed EXIF (JPEG design, Metadata): values against Pillow's reading, byte-for-byte round trips of unedited blocks, rebuilds
/// after edits, and damaged blocks that must never throw.
/// </summary>
public class ExifTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg");

    public static TheoryData<string> WithPillowExif()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return [.. json.RootElement.GetProperty("files").EnumerateArray().Where(e => e.TryGetProperty("exif", out _)).Select(e => e.GetProperty("file").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(WithPillowExif))]
    public void EveryValuePillowReportsMatches(string file)
    {
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, file)));
        ExifData exif = Assert.IsType<ExifData>(raster.Metadata.Exif);
        Assert.False(exif.IsDamaged);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, file + ".exif.json")));
        foreach (JsonProperty ifd in json.RootElement.EnumerateObject())
        {
            ExifIfd kind = ifd.Name switch { "ifd0" => ExifIfd.Primary, "exif" => ExifIfd.Exif, _ => ExifIfd.Gps };
            Assert.Equal(ifd.Value.EnumerateObject().Count(), exif.Entries.Count(e => e.Key.Ifd == kind));
            foreach (JsonProperty tag in ifd.Value.EnumerateObject())
            {
                Assert.True(exif.TryGet(new ExifKey(ushort.Parse(tag.Name, System.Globalization.CultureInfo.InvariantCulture), kind), out ExifEntry? entry), tag.Name);
                AssertValue(tag.Value, entry);
            }
        }
    }

    [Theory]
    [MemberData(nameof(WithPillowExif))]
    public void UneditedBlocksRoundTripByteForByte(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        byte[] original = App1Exif(data);
        using Raster raster = Raster.Load(data);
        Assert.Equal(original, raster.Metadata.Exif!.ToBytes().ToArray());

        // Through a JPEG and a PNG save and back.
        foreach (FormatInfo format in (FormatInfo[])[JpegFormat.Info, Egami.Formats.Png.PngFormat.Info])
        {
            using MemoryStream stream = new();
            raster.Save(stream, format);
            using Raster back = Raster.Load(stream.ToArray());
            Assert.Equal(original, back.Metadata.Exif!.ToBytes().ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsEveryIfdTypeAndTheThumbnailInBothByteOrders(bool bigEndian)
    {
        byte[] block = SampleBlock(bigEndian);
        ExifData exif = ExifData.Parse(block);
        Assert.False(exif.IsDamaged);
        Assert.Equal(bigEndian, exif.IsBigEndian);
        Assert.Equal("Egami", exif.CameraMake);
        Assert.Equal("Model 1", exif.CameraModel);
        Assert.Equal(ImageOrientation.Rotate90, exif.Orientation);
        Assert.Equal(new DateTime(2026, 10, 8, 12, 34, 56), exif.DateTaken);
        Assert.True(exif.TryGet(ExifKey.ExposureTime, out ExifEntry? exposure));
        Assert.Equal(new ExifFraction(1, 250), exposure.AsFraction());
        Assert.True(exif.TryGet(ExifKey.ExposureBias, out ExifEntry? bias));
        Assert.Equal(new ExifFraction(-1, 3), bias.AsFraction());
        Assert.True(exif.TryGet(new ExifKey(0x0001, ExifIfd.Interoperability), out ExifEntry? interop));
        Assert.Equal("R98", interop.AsString());
        GeoPosition where = Assert.IsType<GeoPosition>(exif.Location);
        Assert.Equal(-(59 + (54 / 60.0) + (12.34 / 3600)), where.Latitude, 9);
        Assert.Equal(10.75, where.Longitude, 9);
        Assert.Equal(-234.5, where.Altitude);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], exif.Thumbnail.ToArray());

        // Unedited, the same bytes; edited, laid out again and read back the same.
        Assert.Equal(block, exif.ToBytes().ToArray());
        exif.Set(ExifKey.Artist, "Lars");
        ExifData rebuilt = ExifData.Parse(exif.ToBytes().Span);
        Assert.False(rebuilt.IsDamaged);
        Assert.Equal("Lars", rebuilt.TryGet(ExifKey.Artist, out ExifEntry? artist) ? artist.AsString() : null);
        Assert.Equal(exif.Entries.Select(e => (e.Key, e.Type, e.RawValue.ToArray())), rebuilt.Entries.Select(e => (e.Key, e.Type, e.RawValue.ToArray())));
        Assert.Equal(exif.Location, rebuilt.Location);
        Assert.Equal(exif.Thumbnail.ToArray(), rebuilt.Thumbnail.ToArray());
    }

    [Fact]
    public void EditsAddReplaceAndRemove()
    {
        ExifData exif = new();
        exif.Set(ExifKey.Make, "Ægami");
        exif.Set(ExifKey.IsoSpeed, (ushort)200);
        exif.Set(ExifKey.PixelXDimension, 4000u);
        exif.Set(ExifKey.FNumber, new ExifFraction(28, 10));
        exif.Set(ExifKey.GpsLatitude, new ExifFraction(1, 1), new ExifFraction(30, 1), new ExifFraction(0, 1));
        exif.Set(ExifKey.GpsLatitudeRef, "N");
        exif.Set(ExifKey.GpsLongitude, new ExifFraction(2, 1), new ExifFraction(0, 1), new ExifFraction(0, 1));
        exif.Set(ExifKey.GpsLongitudeRef, "W");
        exif.Orientation = ImageOrientation.FlipVertical;

        ExifData back = ExifData.Parse(exif.ToBytes().Span);
        Assert.Equal("Ægami", back.CameraMake);
        Assert.Equal(200, back.TryGet(ExifKey.IsoSpeed, out ExifEntry? iso) ? iso.AsInteger() : 0);
        Assert.Equal(4000, back.TryGet(ExifKey.PixelXDimension, out ExifEntry? width) ? width.AsInteger() : 0);
        Assert.Equal(2.8, back.TryGet(ExifKey.FNumber, out ExifEntry? f) ? f.AsDouble() : 0, 9);
        Assert.Equal(new GeoPosition(1.5, -2, null), back.Location);
        Assert.Equal(ImageOrientation.FlipVertical, back.Orientation);

        back.Set(ExifKey.IsoSpeed, (ushort)400);
        Assert.True(back.Remove(ExifKey.GpsInfo));
        Assert.False(back.Remove(ExifKey.GpsInfo));
        ExifData again = ExifData.Parse(back.ToBytes().Span);
        Assert.Equal(400, again.TryGet(ExifKey.IsoSpeed, out ExifEntry? iso2) ? iso2.AsInteger() : 0);
        Assert.Null(again.Location);
        Assert.DoesNotContain(again.Entries, e => e.Key.Ifd == ExifIfd.Gps);
        Assert.True(again.Remove(ExifKey.ExifInfo));
        Assert.DoesNotContain(ExifData.Parse(again.ToBytes().Span).Entries, e => e.Key.Ifd == ExifIfd.Exif);

        Assert.Throws<ArgumentException>(() => exif.Set(ExifKey.GpsInfo, 8u));
        Assert.Throws<ArgumentException>(() => exif.Set(ExifKey.Model, ExifType.Rational, [1, 2, 3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => exif.Set(ExifKey.FNumber, new ExifFraction(1L << 33, 1)));
        Assert.Throws<InvalidOperationException>(() => again.TryGet(ExifKey.Make, out ExifEntry? make) ? make.AsInteger() : 0);
    }

    [Fact]
    public void TheMakerNoteKeepsItsOffsetWhenItCan()
    {
        // The sample block puts its maker note after everything else, so dropping the GPS IFD leaves room before it.
        ExifData exif = ExifData.Parse(SampleBlock(bigEndian: false));
        Assert.True(exif.TryGet(ExifKey.MakerNote, out ExifEntry? note));
        int offset = note.Offset;
        exif.Remove(ExifKey.GpsInfo);
        ExifData smaller = ExifData.Parse(exif.ToBytes().Span);
        Assert.True(smaller.TryGet(ExifKey.MakerNote, out ExifEntry? kept));
        Assert.Equal(offset, kept.Offset);
        Assert.Equal(note.RawValue.ToArray(), kept.RawValue.ToArray());
        Assert.Empty(exif.Warnings);

        // A long new value pushes past it: the note moves, with a warning.
        exif.Set(ExifKey.ImageDescription, new string('x', 400));
        ExifData larger = ExifData.Parse(exif.ToBytes().Span);
        Assert.True(larger.TryGet(ExifKey.MakerNote, out ExifEntry? moved));
        Assert.NotEqual(offset, moved.Offset);
        Assert.Single(exif.Warnings);
    }

    [Fact]
    public void DamagedBlocksNeverThrow()
    {
        byte[] good = SampleBlock(bigEndian: true);
        Random random = new(17);
        for (int trial = 0; trial < 3000; trial++)
        {
            byte[] bad = trial switch
            {
                < 500 => good[..random.Next(good.Length)],
                _ => Flip(good, random),
            };
            ExifData exif = ExifData.Parse(bad);
            _ = (exif.Orientation, exif.DateTaken, exif.Location, exif.CameraMake, exif.Thumbnail, exif.Entries);
            foreach (ExifEntry entry in exif.Entries)
            {
                _ = entry.Type == ExifType.Ascii ? entry.AsString() : entry.Count > 0 && ExifEntry.Size(entry.Type) > 0 ? entry.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            }

            Assert.Equal(bad, exif.ToBytes().ToArray());
        }

        Assert.True(ExifData.Parse([]).IsDamaged);
        Assert.True(ExifData.Parse("not a TIFF"u8).IsDamaged);
    }

    [Fact]
    public void LoopsOffsetsAndTheMetadataLimitAreCaught()
    {
        // IFD0 says IFD1 is IFD0 itself.
        byte[] loop = SampleBlock(bigEndian: false);
        int ifd0Entries = BinaryPrimitives.ReadUInt16LittleEndian(loop.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(10 + (12 * ifd0Entries)), 8);
        ExifData looped = ExifData.Parse(loop);
        Assert.True(looped.IsDamaged);
        Assert.Contains("loop", looped.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("Egami", looped.CameraMake);

        // A value offset past the end: that entry is dropped, the rest kept.
        byte[] outside = SampleBlock(bigEndian: false);
        int makeAt = FindEntry(outside, ExifKey.Make.Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(makeAt + 8), 100_000);
        ExifData partial = ExifData.Parse(outside);
        Assert.True(partial.IsDamaged);
        Assert.Null(partial.CameraMake);
        Assert.Equal("Model 1", partial.CameraModel);

        // More value bytes than the metadata limit allows.
        Assert.True(ExifData.Parse(SampleBlock(bigEndian: false), maxBytes: 16).IsDamaged);
    }

    [Fact]
    public void ADamagedBlockStillLoadsTheImageWithAWarning()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Images, "exif-tags.jpg"));
        int exifAt = jpeg.AsSpan().IndexOf("Exif\0\0"u8) + 6;
        jpeg[exifAt + 4] = 0xFF; // IFD0 offset far outside the block
        using Raster raster = Raster.Load(jpeg);
        Assert.True(raster.Metadata.Exif!.IsDamaged);
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("EXIF", StringComparison.Ordinal));

        // Damaged metadata is not damaged pixels: strict reading reports it the same way, as damaged ICC and XMP are.
        using Raster strict = Raster.Load(jpeg, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.True(strict.Metadata.Exif!.IsDamaged);
    }

    [Fact]
    public void ATooLargeBlockLosesItsThumbnailWhenSavedAsJpeg()
    {
        ExifData exif = ExifData.Parse(SampleBlock(bigEndian: false));
        exif.Set(ExifKey.ImageDescription, "with a big thumbnail");
        using Raster<Rgb8> image = Raster.Create<Rgb8>(8, 8);
        image.Metadata.Exif = exif;
        byte[] thumbnail = new byte[70_000];
        thumbnail[0] = 0xFF;
        ExifData big = WithThumbnail(exif, thumbnail);
        image.Metadata.Exif = big;
        using MemoryStream stream = new();
        image.Save(stream, JpegFormat.Info);
        using Raster back = Raster.Load(stream.ToArray());
        Assert.Equal("with a big thumbnail", back.Metadata.Exif!.TryGet(ExifKey.ImageDescription, out ExifEntry? d) ? d.AsString() : null);
        Assert.True(back.Metadata.Exif.Thumbnail.IsEmpty);
    }

    private static ExifData WithThumbnail(ExifData exif, byte[] thumbnail)
    {
        // Built through raw bytes, as the public API reads thumbnails but does not set them.
        byte[] block = exif.ToBytes().ToArray();
        int ifd0Entries = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(8));
        int link = 10 + (12 * ifd0Entries);
        uint ifd1 = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(link));
        int at = (int)ifd1 + 2;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan((int)ifd1));
        byte[] grown = [.. block, .. thumbnail];
        for (int i = 0; i < count; i++, at += 12)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan(at));
            if (tag == 0x0201)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(at + 8), (uint)block.Length);
            }
            else if (tag == 0x0202)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(at + 8), (uint)thumbnail.Length);
            }
        }

        return ExifData.Parse(grown);
    }

    private static void AssertValue(JsonElement expected, ExifEntry entry)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), entry.AsString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetInt64(), entry.AsInteger());
                break;
            case JsonValueKind.Object:
                Assert.Equal(Convert.FromHexString(expected.GetProperty("bytes").GetString()!), entry.RawValue.ToArray());
                break;
            default:
                JsonElement[] items = [.. expected.EnumerateArray()];
                if (entry.Type is ExifType.Rational or ExifType.SRational && items.Length == 2 && items[0].ValueKind == JsonValueKind.Number && entry.Count == 1)
                {
                    Assert.Equal(new ExifFraction(items[0].GetInt64(), items[1].GetInt64()), entry.AsFraction());
                    break;
                }

                Assert.Equal(items.Length, entry.Count);
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i].ValueKind == JsonValueKind.Array)
                    {
                        Assert.Equal(new ExifFraction(items[i][0].GetInt64(), items[i][1].GetInt64()), entry.AsFraction(i));
                    }
                    else
                    {
                        Assert.Equal(items[i].GetInt64(), entry.AsInteger(i));
                    }
                }

                break;
        }
    }

    /// <summary>The TIFF block in a JPEG's APP1 Exif segment, as stored.</summary>
    private static byte[] App1Exif(byte[] jpeg)
    {
        int at = jpeg.AsSpan().IndexOf("Exif\0\0"u8);
        int length = (jpeg[at - 2] << 8) | jpeg[at - 1];
        return jpeg[(at + 6)..(at - 2 + length)];
    }

    /// <summary>
    /// A block written by hand, not by <see cref="ExifData"/>: IFD0 with text, a SHORT and the pointers; an EXIF IFD with rationals
    /// of both signs, an interoperability IFD; a GPS IFD; IFD1 with a 4-byte "thumbnail"; then a maker note, last, after padding.
    /// </summary>
    private static byte[] SampleBlock(bool bigEndian)
    {
        TiffWriter w = new(bigEndian);
        w.Ifd(ExifIfd.Primary, [
            (0x010F, 2, Ascii("Egami")),
            (0x0110, 2, Ascii("Model 1")),
            (0x0112, 3, w.Shorts(6)),
            (0x0132, 2, Ascii("2026:10:08 12:34:56")),
            (0x8769, 4, TiffWriter.Pointer(ExifIfd.Exif)),
            (0x8825, 4, TiffWriter.Pointer(ExifIfd.Gps)),
        ]);
        w.Ifd(ExifIfd.Thumbnail, [(0x0103, 3, w.Shorts(6)), (0x0201, 4, TiffWriter.Pointer(ExifIfd.Thumbnail + 10)), (0x0202, 4, w.Longs(4))]);
        w.Ifd(ExifIfd.Exif, [
            (0x829A, 5, w.Rationals(1, 250)),
            (0x9003, 2, Ascii("2026:10:08 12:34:56")),
            (0x9204, 10, w.Rationals(-1, 3)),
            (0x927C, 7, [.. "MAKERNOTE-0123456789"u8]),
            (0xA005, 4, TiffWriter.Pointer(ExifIfd.Interoperability)),
        ]);
        w.Ifd(ExifIfd.Interoperability, [(0x0001, 2, Ascii("R98"))]);
        w.Ifd(ExifIfd.Gps, [
            (0x0001, 2, Ascii("S")),
            (0x0002, 5, w.Rationals(59, 1, 54, 1, 1234, 100)),
            (0x0003, 2, Ascii("E")),
            (0x0004, 5, w.Rationals(10, 1, 45, 1, 0, 1)),
            (0x0005, 1, [1]),
            (0x0006, 5, w.Rationals(2345, 10)),
        ]);
        return w.Finish(thumbnail: [0xFF, 0xD8, 0xFF, 0xD9], makerNoteTag: 0x927C);
    }

    private static byte[] Ascii(string text) => [.. System.Text.Encoding.ASCII.GetBytes(text), 0];

    private static byte[] Flip(byte[] source, Random random)
    {
        byte[] copy = (byte[])source.Clone();
        for (int n = random.Next(1, 6); n > 0; n--)
        {
            copy[random.Next(copy.Length)] = (byte)random.Next(256);
        }

        return copy;
    }

    private static int FindEntry(byte[] block, ushort tag)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(8));
        for (int i = 0, at = 10; i < count; i++, at += 12)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at)) == tag)
            {
                return at;
            }
        }

        throw new InvalidOperationException();
    }

    /// <summary>Lays out IFDs by hand: IFD0 at 8, then each IFD with its values, the thumbnail, padding and the maker note last.</summary>
    private sealed class TiffWriter(bool bigEndian)
    {
        private readonly List<(ExifIfd Kind, (ushort Tag, ushort Type, byte[] Value)[] Entries)> _ifds = [];

        internal void Ifd(ExifIfd kind, (ushort Tag, ushort Type, byte[] Value)[] entries) => _ifds.Add((kind, entries));

        /// <summary>A placeholder value naming the IFD (or the thumbnail, as Thumbnail + 10) whose offset goes here.</summary>
        internal static byte[] Pointer(ExifIfd target) => [0xEE, (byte)target, 0, 0];

        internal byte[] Shorts(ushort value) => bigEndian ? [(byte)(value >> 8), (byte)value] : [(byte)value, (byte)(value >> 8)];

        internal byte[] Longs(uint value)
        {
            byte[] bytes = new byte[4];
            Put(bytes, value);
            return bytes;
        }

        internal byte[] Rationals(params int[] parts)
        {
            byte[] bytes = new byte[parts.Length * 4];
            for (int i = 0; i < parts.Length; i++)
            {
                Put(bytes.AsSpan(i * 4), (uint)parts[i]);
            }

            return bytes;
        }

        internal byte[] Finish(byte[] thumbnail, ushort makerNoteTag)
        {
            int[] typeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];
            Dictionary<int, int> offsets = [];
            int position = 8;
            foreach ((ExifIfd kind, (ushort, ushort, byte[] Value)[] entries) in _ifds)
            {
                offsets[(int)kind] = position;
                position += 2 + (12 * entries.Length) + 4 + entries.Where(e => e.Value.Length > 4 && e.Item1 != makerNoteTag).Sum(e => e.Value.Length + (e.Value.Length & 1));
            }

            offsets[(int)ExifIfd.Thumbnail + 10] = position;
            position += thumbnail.Length;
            int makerNoteAt = position + 64;
            byte[] makerNote = [.. _ifds.SelectMany(i => i.Entries).First(e => e.Tag == makerNoteTag).Value];
            byte[] block = new byte[makerNoteAt + makerNote.Length];
            block[0] = block[1] = bigEndian ? (byte)'M' : (byte)'I';
            Put16(block.AsSpan(2), 42);
            Put(block.AsSpan(4), 8);
            foreach ((ExifIfd kind, (ushort Tag, ushort Type, byte[] Value)[] entries) in _ifds)
            {
                int at = offsets[(int)kind];
                Put16(block.AsSpan(at), (ushort)entries.Length);
                int data = at + 2 + (12 * entries.Length) + 4;
                at += 2;
                foreach ((ushort tag, ushort type, byte[] value) in entries)
                {
                    Put16(block.AsSpan(at), tag);
                    Put16(block.AsSpan(at + 2), type);
                    Put(block.AsSpan(at + 4), (uint)(value.Length / typeSize[type]));
                    if (value is [0xEE, byte target, 0, 0])
                    {
                        Put(block.AsSpan(at + 8), (uint)offsets[target]);
                    }
                    else if (value.Length <= 4)
                    {
                        value.CopyTo(block.AsSpan(at + 8));
                    }
                    else
                    {
                        int valueAt = tag == makerNoteTag ? makerNoteAt : data;
                        Put(block.AsSpan(at + 8), (uint)valueAt);
                        value.CopyTo(block.AsSpan(valueAt));
                        if (tag != makerNoteTag)
                        {
                            data += value.Length + (value.Length & 1);
                        }
                    }

                    at += 12;
                }

                Put(block.AsSpan(at), kind == ExifIfd.Primary ? (uint)offsets[(int)ExifIfd.Thumbnail] : 0);
            }

            thumbnail.CopyTo(block.AsSpan(offsets[(int)ExifIfd.Thumbnail + 10]));
            return block;
        }

        private void Put(Span<byte> at, uint value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(at, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(at, value);
            }
        }

        private void Put16(Span<byte> at, ushort value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(at, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(at, value);
            }
        }
    }
}
