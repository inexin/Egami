using System.Buffers.Binary;

using Egami.Formats.Psd;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Formats.Psd;

/// <summary>
/// PSD allocation budgets: ReadLayers and flattening (a flattening Load and Flatten) allocate the same for 10 rows as for 3,000,
/// since pixels, planes and canvases come from the memory source and the pools.
/// </summary>
[Trait("Category", AllocationMeter.Category)]
public class PsdAllocationTests
{
    public static TheoryData<int> Compressions() => AllocationMeter.Rows(new TheoryData<int> { 0, 1, 2, 3 });

    [Theory]
    [MemberData(nameof(Compressions))]
    public void LoadsAndFlattensAllocateTheSameForTenRowsAndThousands(int compression)
    {
        AllocationMeter.SkipUnlessOnDemand();
        byte[] small = Layered(300, 10, compression), large = Layered(300, 3000, compression);
        PsdReaderOptions flatten = new() { Composite = PsdComposite.Flatten };
        foreach ((string what, Action<byte[]> action) in new (string, Action<byte[]>)[]
        {
            ("A flattening Load", data => Raster.Load(data, EgamiContext.Default, flatten).Dispose()),
            ("ReadLayers", data => PsdImage.ReadLayers(new MemoryStream(data)).Dispose()),
            ("Flatten", data =>
            {
                using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(data));
                document.Flatten().Dispose();
            }),
        })
        {
            long few = AllocationMeter.Fewest(() => action(small));
            long many = AllocationMeter.Fewest(() => action(large));
            Assert.True(many - few < 1024, $"{what}, compression {compression}: 3,000 rows allocated {many:N0} bytes against {few:N0} for 10 rows.");
        }
    }

    /// <summary>
    /// Writes a layered 8-bit RGB file: an opaque background, a half-transparent layer in multiply over part of it, and a raw
    /// composite; each layer channel in the given compression.
    /// </summary>
    internal static byte[] Layered(int width, int height, int compression)
    {
        using MemoryStream file = new();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); file.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); file.Write(b); }
        file.Write("8BPS"u8);
        U16(1);
        file.Write(new byte[6]);
        U16(3);
        U32(height);
        U32(width);
        U16(8);
        U16(3);
        U32(0);
        U32(0);

        byte[] Plane(int seed) => [.. Enumerable.Range(0, width * height).Select(i => (byte)((i * seed) + (i / width)))];
        byte[][] background = [Plane(3), Plane(5), Plane(7)];
        byte[][] top = [Plane(11), Plane(13), Plane(17), [.. Enumerable.Repeat((byte)128, width * height)]];
        byte[][] channels = [.. background.Select(p => Channel(p, width, height, compression)), .. top.Select(p => Channel(p, width, height, compression))];
        using MemoryStream info = new();
        void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); info.Write(b); }
        void I32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, (int)v); info.Write(b); }
        I16(2);
        for (int layer = 0; layer < 2; layer++)
        {
            I32(0);
            I32(0);
            I32(height);
            I32(width);
            int[] ids = layer == 0 ? [0, 1, 2] : [0, 1, 2, -1];
            I16(ids.Length);
            for (int c = 0; c < ids.Length; c++)
            {
                I16(ids[c]);
                I32(channels[(layer * 3) + c].Length);
            }

            info.Write("8BIM"u8);
            info.Write(layer == 0 ? "norm"u8 : "mul "u8);
            info.Write([255, 0, 0, 0]);
            I32(4 + 4 + 4);
            I32(0);
            I32(0);
            info.Write([2, (byte)'L', (byte)(layer == 0 ? 'b' : 't'), 0]);
        }

        foreach (byte[] channel in channels)
        {
            info.Write(channel);
        }

        if (info.Length % 2 == 1)
        {
            info.WriteByte(0);
        }

        U32(4 + info.Length + 4);
        U32(info.Length);
        info.Position = 0;
        info.CopyTo(file);
        U32(0);
        U16(0);
        foreach (byte[] plane in background)
        {
            file.Write(plane);
        }

        return file.ToArray();
    }

    private static byte[] Channel(byte[] plane, int width, int height, int compression)
    {
        using MemoryStream data = new();
        data.Write([0, (byte)compression]);
        switch (compression)
        {
            case 0:
                data.Write(plane);
                break;
            case 1:
                {
                    // Literal runs of up to 128 bytes, each row's byte count first.
                    List<byte[]> rows = [];
                    for (int y = 0; y < height; y++)
                    {
                        using MemoryStream row = new();
                        for (int x = 0; x < width; x += 128)
                        {
                            int n = Math.Min(128, width - x);
                            row.WriteByte((byte)(n - 1));
                            row.Write(plane, (y * width) + x, n);
                        }

                        rows.Add(row.ToArray());
                    }

                    foreach (byte[] row in rows)
                    {
                        data.Write([(byte)(row.Length >> 8), (byte)row.Length]);
                    }

                    foreach (byte[] row in rows)
                    {
                        data.Write(row);
                    }

                    break;
                }

            default:
                {
                    byte[] rows = (byte[])plane.Clone();
                    if (compression == 3)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = width - 1; x > 0; x--)
                            {
                                rows[(y * width) + x] -= rows[(y * width) + x - 1];
                            }
                        }
                    }

                    using (System.IO.Compression.ZLibStream zlib = new(data, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    {
                        zlib.Write(rows);
                    }

                    break;
                }
        }

        return data.ToArray();
    }
}
