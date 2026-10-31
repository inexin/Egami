using System.Buffers.Binary;

using Egami.Formats.Extended.Raw;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// RAW Build Plan task 11: Canon CR3 built in code, in the layout the sample files have: ftyp <c>crx </c>; moov with the Canon
/// uuid box (CMT1 to CMT4 and THMB) and two tracks (a full-size JPEG and a CMP1 raw track); the PRVW uuid box; then mdat.
/// </summary>
public class RawCr3Tests
{
    private static EgamiContext Context => TestImages.Context;

    [Fact]
    public void ProbesAndListsThePreviews()
    {
        byte[] file = Cr3();
        RasterInfo info = Raster.Probe(file, Context);
        Assert.Equal("RAW", info.Format.Name);
        Assert.Equal((96, 64), (info.Width, info.Height));
        RasterInfo preview = Raster.Probe(file, Context, new RawReaderOptions { Output = RawOutput.Preview });
        Assert.Equal((48, 32), (preview.Width, preview.Height));
    }

    [Theory]
    [InlineData(0, 48, 32)]
    [InlineData(1, 24, 16)]
    [InlineData(2, 16, 12)]
    public void LoadsEachPreviewWithTheMetadata(int index, int width, int height)
    {
        using Raster<Rgb8> preview = Raster.Load<Rgb8>(Cr3(), Context, new RawReaderOptions { Output = RawOutput.Preview, PreviewIndex = index });
        Assert.Equal((width, height), (preview.Width, preview.Height));
        RawDetails details = Assert.IsType<RawDetails>(preview.Metadata.FormatDetails);
        Assert.Equal("Canon", details.Make);
        Assert.Equal("Canon EOS TEST", details.Model);
        Assert.Equal(400, details.Iso);
        Assert.Equal(3, details.Previews.Count);
        Assert.Equal(16, details.MakerNote.Length);
        Assert.NotNull(preview.Metadata.Exif);
    }

    [Fact]
    public void DevelopingSaysTheSensorDataIsNotRead()
    {
        NotSupportedException error = Assert.ThrowsAny<NotSupportedException>(() => Raster.Load<Rgb16>(Cr3(), Context).Dispose());
        Assert.Contains("RawOutput.Preview", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(40)]
    [InlineData(200)]
    [InlineData(600)]
    public void TruncatedFilesAreCorruptOrUnsupported(int length)
    {
        byte[] file = Cr3()[..length];
        Exception? error = Record.Exception(() => Raster.Load<Rgb8>(file, Context, new RawReaderOptions { Output = RawOutput.Preview }).Dispose());
        Assert.True(error is EgamiException or NotSupportedException, error?.ToString());
    }

    private static byte[] Jpeg(int width, int height)
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            raster.Row(y).Fill(new Rgb8(200, 120, 40));
        }

        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info);
        return stream.ToArray();
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        int size = 8 + parts.Sum(p => p.Length);
        byte[] box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        int at = 8;
        foreach (byte[] p in parts)
        {
            p.CopyTo(box, at);
            at += p.Length;
        }

        return box;
    }

    private static byte[] Uuid(string hex, params byte[][] parts) => Box("uuid", [Convert.FromHexString(hex), .. parts]);

    private static byte[] U32(long v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
        return b;
    }

    private static byte[] U64(long v)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v);
        return b;
    }

    /// <summary>A track whose one sample (at <paramref name="offset"/>, <paramref name="size"/> bytes) has a CRAW entry of this size.</summary>
    private static byte[] Track(int width, int height, string kind, long offset, long size)
    {
        byte[] entry = new byte[82];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), (ushort)height);
        byte[] craw = Box("CRAW", entry, Box(kind, new byte[kind == "CMP1" ? 52 : 4]));
        byte[] stsd = Box("stsd", U32(0), U32(1), craw);
        byte[] stsz = Box("stsz", U32(0), U32(0), U32(1), U32(size));
        byte[] co64 = Box("co64", U32(0), U32(1), U64(offset));
        return Box("trak", Box("mdia", Box("minf", Box("stbl", stsd, stsz, co64))));
    }

    private static byte[] Tiff(TiffBuilder.Ifd ifd) => TiffBuilder.Write(ifd);

    /// <summary>Builds the file twice: once to learn where mdat's samples land, once with those offsets.</summary>
    private static byte[] Cr3()
    {
        byte[] full = Jpeg(48, 32), prvw = Jpeg(24, 16), thumb = Jpeg(16, 12);
        byte[] raw = new byte[64];
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "Canon"), TiffTag.Ascii(272, "Canon EOS TEST"), TiffTag.Short(274, 1)]);
        TiffBuilder.Ifd exif = new();
        exif.Tags.AddRange([TiffTag.Short(0x8827, 400), TiffTag.Rational(0x829A, 1.0 / 125), TiffTag.Rational(0x829D, 4)]);
        TiffBuilder.Ifd gps = new();
        gps.Tags.Add(TiffTag.Bytes(0x0000, 2, 3, 0, 0));
        byte[] thmbHeader = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(thmbHeader.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt16BigEndian(thmbHeader.AsSpan(6), 12);
        byte[] prvwHeader = new byte[16];

        byte[] Build(long fullAt, long rawAt)
        {
            byte[] canon = Uuid("85c0b687820f11e08111f4ce462b6a48",
                Box("CMT1", Tiff(ifd0)), Box("CMT2", Tiff(exif)), Box("CMT3", new byte[16]), Box("CMT4", Tiff(gps)), Box("THMB", thmbHeader, thumb));
            byte[] moov = Box("moov", canon, Track(48, 32, "JPEG", fullAt, full.Length), Track(96, 64, "CMP1", rawAt, raw.Length));
            byte[] preview = Uuid("eaf42b5e1c984b88b9fbb7dc406e4d16", new byte[8], Box("PRVW", prvwHeader, prvw));
            byte[] ftyp = Box("ftyp", "crx "u8.ToArray(), U32(1), "crx isom"u8.ToArray());
            byte[] mdat = Box("mdat", full, raw);
            return [.. ftyp, .. moov, .. preview, .. mdat];
        }

        byte[] first = Build(0, 0);
        long mdatData = first.Length - full.Length - raw.Length;
        return Build(mdatData, mdatData + full.Length);
    }
}
