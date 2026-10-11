using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Jbig2;
using Egami.Formats.Tiff;
using Egami.Pixels;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats.Jbig2;

/// <summary>JBIG2 through the 2.2 row reader: rows, regions and reductions as Load gives them, and striped pages a stripe at a time.</summary>
public class Jbig2StreamingTests
{
    public static TheoryData<string> Files() => [.. Jbig2Corpus.Main.Files().Where(f => f.EndsWith(".jb2", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Files))]
    public void RowsRegionsAndReductionsMatchLoad(string name)
    {
        byte[] data = Jbig2Corpus.Main.Read(name);
        using Raster whole = Raster.Load(data);
        using (RasterDecoder decoder = RasterDecoder.Open(data))
        {
            Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
            using Raster rows = decoder.ReadAll();
            Assert.Equal(TiffReference.Bytes(whole), TiffReference.Bytes(rows));
        }

        Rect region = new(whole.Width / 3, whole.Height / 4, Math.Max(1, whole.Width / 2), Math.Max(1, whole.Height / 3));
        using Raster cropped = Raster.Load(data, options: new ReaderOptions { Region = region });
        using Raster expected = whole.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(cropped));

        using Raster small = Raster.Load(data, options: new ReaderOptions { MinimumSize = new Size(Math.Max(1, whole.Width / 3), Math.Max(1, whole.Height / 3)) });
        Assert.True(small.Width < whole.Width || whole.Width < 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StripedPagesStreamAStripeAtATime(bool unknownHeight)
    {
        (byte[] file, byte[] expected, int width, int height) = Striped(unknownHeight);
        using Raster<Gray8> loaded = Raster.Load<Gray8>(file);
        Assert.Equal((width, height), (loaded.Width, loaded.Height));
        Assert.Equal(expected, TiffReference.Bytes(loaded));
        Assert.True(((Jbig2Details)loaded.Metadata.FormatDetails!).Striped);

        RasterInfo info = Raster.Probe(file);
        Assert.Equal(height, info.Height);
        Assert.Equal(Jbig2Reader.WorkingBytesOf(width, 64), info.WorkingBytes);

        using RasterDecoder decoder = RasterDecoder.Open(file);
        using Raster streamed = decoder.ReadAll();
        Assert.Equal(expected, TiffReference.Bytes(streamed));
    }

    /// <summary>
    /// Builds a striped page: random MMR generic regions, one per stripe of at most 64 rows, each followed by an end of stripe;
    /// returns the file and the expected Gray8 pixels.
    /// </summary>
    internal static (byte[] File, byte[] Pixels, int Width, int Height) Striped(bool unknownHeight)
    {
        const int width = 200;
        int[] ends = [63, 120, 184, 247, 299];
        int height = ends[^1] + 1;
        Random random = new(11);
        int rowBytes = (width + 7) / 8;
        byte[] packed = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (random.Next(5) == 0 || ((x / 9) + (y / 7)) % 4 == 0)
                {
                    packed[(y * rowBytes) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        using MemoryStream file = new();
        file.Write([0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0, 0, 0, 1]);
        uint number = 0;
        byte[] pageInfo = new byte[19];
        BinaryPrimitives.WriteUInt32BigEndian(pageInfo, width);
        BinaryPrimitives.WriteUInt32BigEndian(pageInfo.AsSpan(4), unknownHeight ? 0xFFFFFFFF : (uint)height);
        pageInfo[16] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pageInfo.AsSpan(17), 0x8000 | 64);
        Segment(file, number++, 48, pageInfo);
        int top = 0;
        foreach (int end in ends)
        {
            int rows = end + 1 - top;
            byte[] mmr = CcittEncoder.Encode(packed.AsSpan(top * rowBytes, rows * rowBytes), width, rows, group4: true);
            byte[] region = new byte[18 + mmr.Length];
            BinaryPrimitives.WriteUInt32BigEndian(region, width);
            BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(4), (uint)rows);
            BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(12), (uint)top);
            region[17] = 1;
            mmr.CopyTo(region, 18);
            Segment(file, number++, 39, region);
            byte[] stripe = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(stripe, (uint)end);
            Segment(file, number++, 50, stripe);
            top = end + 1;
        }

        Segment(file, number++, 49, []);
        Segment(file, number, 51, [], page: 0);
        byte[] pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            int x = i % width;
            int y = i / width;
            pixels[i] = (packed[(y * rowBytes) + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
        }

        return (file.ToArray(), pixels, width, height);
    }

    private static void Segment(MemoryStream file, uint number, int type, byte[] data, byte page = 1)
    {
        Span<byte> header = stackalloc byte[11];
        BinaryPrimitives.WriteUInt32BigEndian(header, number);
        header[4] = (byte)type;
        header[5] = 0;
        header[6] = page;
        BinaryPrimitives.WriteUInt32BigEndian(header[7..], (uint)data.Length);
        file.Write(header);
        file.Write(data);
    }
}
