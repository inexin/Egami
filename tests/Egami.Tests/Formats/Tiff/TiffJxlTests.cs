using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Jxl;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>
/// TIFF compression 50002 and DNG 1.7's 52546: every strip or tile is a JPEG XL codestream, and the page equals those
/// codestreams decoded alone and put side by side.
/// </summary>
public class TiffJxlTests
{
    private const int Width = 150;
    private const int Height = 100;
    private const int Tile = 64;

    [Theory]
    [InlineData(50002, true)]
    [InlineData(52546, true)]
    [InlineData(50002, false)]
    public void Rgb8TilesEqualTheirCodestreams(int compression, bool tiled)
    {
        using Raster<Rgb8> source = Source<Rgb8>();
        Check(source, compression, tiled, 8, 3, 2, false);
    }

    [Fact]
    public void Rgba8TilesEqualTheirCodestreams()
    {
        using Raster<Rgba8> source = Source<Rgba8>();
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgba8> row = source.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x].A = (byte)((x * 5) + y);
            }
        }

        Check(source, 50002, true, 8, 4, 2, false);
    }

    [Fact]
    public void Gray8TilesEqualTheirCodestreams()
    {
        using Raster<Gray8> source = Source<Gray8>();
        Check(source, 50002, true, 8, 1, 1, false);
    }

    [Fact]
    public void Rgb16TilesEqualTheirCodestreams()
    {
        using Raster<Rgb16> source = Source<Rgb16>();
        Check(source, 50002, true, 16, 3, 2, false);
    }

    [Fact]
    public void FloatTilesEqualTheirCodestreams()
    {
        using Raster<RgbF32> source = Source<RgbF32>();
        Check(source, 52546, true, 32, 3, 2, true);
    }

    /// <summary>YCbCr pages with JPEG XL units are the decoder's RGB, as with JPEG: no subsampling of their own.</summary>
    [Fact]
    public void YCbCrPagesAreTheCodestreamsRgb()
    {
        using Raster<Rgb8> source = Source<Rgb8>();
        Check(source, 52546, true, 8, 3, 6, false);
    }

    /// <summary>A lossy (XYB) codestream from cjxl as the single strip of a page.</summary>
    [Fact]
    public void LossyStripEqualsTheFile()
    {
        byte[] jxl = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "jxl", "duck-d1-e3.jxl"));
        using Raster<Rgb8> alone = Raster.Load<Rgb8>(new MemoryStream(jxl));
        byte[] tiff = Build(alone.Width, alone.Height, false, alone.Height, 8, 3, 2, false, 50002, [jxl]);
        using Raster<Rgb8> page = Raster.Load<Rgb8>(new MemoryStream(tiff));
        AssertSame(alone, page);
    }

    [Fact]
    public void PlanarPagesAreRefused()
    {
        byte[] jxl = File.ReadAllBytes(Path.Combine(SimpleCorpus.Images, "jxl", "duck-d1-e3.jxl"));
        byte[] tiff = Build(16, 16, false, 16, 8, 3, 2, false, 50002, [jxl, jxl, jxl], planar: true);
        Assert.Throws<NotSupportedException>(() => Raster.Load(new MemoryStream(tiff)).Dispose());
    }

    private static Raster<T> Source<T>()
        where T : unmanaged, IPixelType<T>
    {
        // The tiles cover 192 × 128; the page shows 150 × 100 of it.
        using Raster<Rgb8> duck = Raster.Load<Rgb8>(Path.Combine(SimpleCorpus.Images, "ops", "duck.png"));
        using Raster<Rgb8> crop = duck.Derive(ops => ops.Crop(new Rect(20, 10, 3 * Tile, 2 * Tile)), TestContext.Current.CancellationToken);
        return crop.As<T>();
    }

    private static void Check<T>(Raster<T> source, int compression, bool tiled, int bits, int samples, int photometric, bool fl)
        where T : unmanaged, IPixelType<T>
    {
        int unitHeight = tiled ? Tile : 32;
        int unitWidth = tiled ? Tile : Width;
        List<byte[]> units = [];
        using Raster<T> expected = Raster.Create<T>(Width, Height);
        for (int y0 = 0; y0 < Height; y0 += unitHeight)
        {
            for (int x0 = 0; x0 < (tiled ? Width : 1); x0 += unitWidth)
            {
                // Tiles are whole, edge tiles included; the last strip has only the rows left.
                int h = tiled ? unitHeight : Math.Min(unitHeight, Height - y0);
                using Raster<T> part = source.Derive(ops => ops.Crop(new Rect(x0, y0, unitWidth, h)), TestContext.Current.CancellationToken);
                using MemoryStream jxl = new();
                part.Save(jxl, JxlFormat.Info, new JxlWriterOptions { Lossless = true, Effort = 3 });
                units.Add(jxl.ToArray());
                using Raster<T> alone = Raster.Load<T>(new MemoryStream(jxl.ToArray()));
                for (int y = 0; y < alone.Height && y0 + y < Height; y++)
                {
                    int w = Math.Min(alone.Width, Width - x0);
                    alone.Row(y)[..w].CopyTo(expected.Row(y0 + y).Slice(x0, w));
                }
            }
        }

        byte[] tiff = Build(Width, Height, tiled, unitHeight, bits, samples, photometric, fl, compression, units);
        using Raster<T> page = Raster.Load<T>(new MemoryStream(tiff));
        AssertSame(expected, page);
        Assert.Equal((TiffCompression)compression, ((TiffDetails)page.Metadata.FormatDetails!).Compression);
    }

    private static void AssertSame<T>(Raster<T> expected, Raster<T> actual)
        where T : unmanaged, IPixelType<T>
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(MemoryMarshal.AsBytes(expected.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(actual.Row(y))), $"row {y} differs");
        }
    }

    /// <summary>Writes a little-endian TIFF with one page whose strips or tiles are <paramref name="units"/>.</summary>
    internal static byte[] Build(int width, int height, bool tiled, int unitHeight, int bits, int samples, int photometric, bool fl, int compression, List<byte[]> units, bool planar = false, int tileWidth = Tile)
    {
        List<(int Tag, int Type, uint[] Values)> tags =
        [
            (256, 4, [(uint)width]),
            (257, 4, [(uint)height]),
            (258, 3, [.. Enumerable.Repeat((uint)bits, samples)]),
            (259, 3, [(uint)compression]),
            (262, 3, [(uint)photometric]),
            (277, 3, [(uint)samples]),
            (284, 3, [planar ? 2u : 1u]),
        ];
        if (photometric == 6)
        {
            tags.Add((530, 3, [2, 2]));
        }

        if (samples is 2 or 4)
        {
            tags.Add((338, 3, [2]));
        }

        if (fl)
        {
            tags.Add((339, 3, [.. Enumerable.Repeat(3u, samples)]));
        }

        // Data first, after the header; the IFD and its arrays after the data.
        using MemoryStream o = new();
        o.Write("II*\0"u8);
        o.Write(new byte[4]);
        uint[] offsets = new uint[units.Count];
        uint[] counts = new uint[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            offsets[i] = (uint)o.Position;
            counts[i] = (uint)units[i].Length;
            o.Write(units[i]);
            if (o.Position % 2 == 1)
            {
                o.WriteByte(0);
            }
        }

        if (tiled)
        {
            tags.Add((322, 3, [(uint)tileWidth]));
            tags.Add((323, 3, [(uint)unitHeight]));
            tags.Add((324, 4, offsets));
            tags.Add((325, 4, counts));
        }
        else
        {
            tags.Add((273, 4, offsets));
            tags.Add((278, 3, [(uint)unitHeight]));
            tags.Add((279, 4, counts));
        }

        tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        long ifd = o.Position;
        long extra = ifd + 2 + (12 * tags.Count) + 4;
        using MemoryStream arrays = new();
        byte[] entries = new byte[12 * tags.Count];
        for (int i = 0; i < tags.Count; i++)
        {
            (int tag, int type, uint[] values) = tags[i];
            Span<byte> e = entries.AsSpan(12 * i, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)tag);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], (ushort)type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)values.Length);
            int size = type == 3 ? 2 : 4;
            byte[] data = new byte[size * values.Length];
            for (int k = 0; k < values.Length; k++)
            {
                if (size == 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 * k), (ushort)values[k]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 * k), values[k]);
                }
            }

            if (data.Length <= 4)
            {
                data.CopyTo(e[8..]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)(extra + arrays.Position));
                arrays.Write(data);
            }
        }

        o.Write([(byte)tags.Count, (byte)(tags.Count >> 8)]);
        o.Write(entries);
        o.Write(new byte[4]);
        arrays.WriteTo(o);
        byte[] file = o.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)ifd);
        return file;
    }
}
