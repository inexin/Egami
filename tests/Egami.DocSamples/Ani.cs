using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Ani;
using Egami.Formats.Ico;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Ani
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();
        File.WriteAllBytes("busy.ani", Busy());

        #region ani-load
        // One frame per step of the animation, all at one size: the largest, as for an icon.
        using Raster<Rgba8> busy = Raster.Load<Rgba8>("busy.ani");
        foreach (Frame<Rgba8> frame in busy.Frames)
        {
            Point? hotspot = ((IcoDetails)frame.Metadata.FormatDetails!).Hotspot;
            Console.WriteLine($"{frame.Metadata.Delay.TotalMilliseconds:F0} ms, hotspot {hotspot}");
        }

        // MinimumSize picks the smallest size at least that big; LoadAll gives the animation at every size, largest first.
        using Raster<Rgba8> small = Raster.Load<Rgba8>("busy.ani", options: new ReaderOptions { MinimumSize = new Size(32, 32) });
        using RasterList sizes = Raster.LoadAll("busy.ani");
        #endregion

        Assert.Equal(4, busy.FrameCount);
        Assert.Equal(48, busy.Width);
        Assert.Equal(32, small.Width);
        Assert.Equal(2, sizes.Count);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();
        File.WriteAllBytes("busy.ani", Busy());

        #region ani-details
        // Probe reads the chunks and each frame's directory without decoding a pixel.
        RasterInfo info = Raster.Probe("busy.ani");
        Console.WriteLine($"{info.FrameCount} steps; {info.ImageCount} sizes; Load gives {info.Width}×{info.Height}");

        // The steps replay the stored frames in the file's order; a frame can show more than once.
        using Raster busy = Raster.Load("busy.ani");
        AniDetails details = (AniDetails)busy.Metadata.FormatDetails!;
        Console.WriteLine($"\"{details.Title}\" by {details.Artist}: {details.StoredFrames} frames stored, steps {string.Join(", ", details.Steps)}");
        #endregion

        Assert.Equal(4, info.FrameCount);
        Assert.Equal([0, 1, 2, 1], details.Steps);
    }

    /// <summary>Writes a small animated cursor byte by byte: three cursors at 32 and 48, played 0, 1, 2, 1, with a title.</summary>
    private static byte[] Busy()
    {
        using Raster<Rgba8> duck = Raster.Load<Rgba8>("duck.png");
        List<byte[]> frames = [];
        for (int i = 0; i < 3; i++)
        {
            using Raster<Rgba8> turned = duck.Derive(ops => ops.Rotate(i * 30f).Fit(48, 48));
            using MemoryStream cursor = new();
            turned.Save(cursor, CurFormat.Info, new IcoWriterOptions { Sizes = [32, 48], Hotspot = new Point(24, 24) });
            frames.Add(cursor.ToArray());
        }

        using MemoryStream body = new();
        body.Write("ACON"u8);
        Chunk(body, "LIST", [.. "INFO"u8, .. ChunkBytes("INAM", [.. "Busy duck"u8, 0]), .. ChunkBytes("IART", [.. "Egami"u8, 0])]);
        byte[] header = new byte[36];
        uint[] fields = [36, 3, 4, 0, 0, 0, 0, 8, 3];
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 4), fields[i]);
        }

        Chunk(body, "anih", header);
        Chunk(body, "seq ", Words(0, 1, 2, 1));
        Chunk(body, "LIST", [.. "fram"u8, .. frames.SelectMany(f => ChunkBytes("icon", f))]);
        byte[] riff = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Length);
        body.ToArray().CopyTo(riff, 8);
        return riff;
    }

    private static void Chunk(Stream output, string id, byte[] data) => output.Write(ChunkBytes(id, data));

    private static byte[] ChunkBytes(string id, byte[] data)
    {
        byte[] chunk = new byte[8 + data.Length + (data.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(id, chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Words(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }
}
