using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Formats.Bmp;

public class BmpEncodeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(37)]
    public void OpaqueImagesWrite24BitAndRoundTrip(int width)
    {
        using Raster<Rgb8> source = Random<Rgb8>(width, 5);
        byte[] data = Save(source);
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28)));
        using Raster raster = Raster.Load(data);
        Assert.IsType<Raster<Bgr8>>(raster);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(data);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    [Fact]
    public void OpaqueRgbaAlsoWrites24Bit()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(4, 4, new Rgba8(1, 2, 3, 255));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(Save(source).AsSpan(28)));
    }

    [Fact]
    public void AlphaWrites32BitWithAV4HeaderAndRoundTrips()
    {
        using Raster<Rgba8> source = Random<Rgba8>(13, 7);
        byte[] data = Save(source);
        Assert.Equal(108u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30)));
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    [Theory]
    [InlineData(BmpPixelFormat.Indexed1, 2)]
    [InlineData(BmpPixelFormat.Indexed2, 4)]
    [InlineData(BmpPixelFormat.Indexed4, 16)]
    [InlineData(BmpPixelFormat.Indexed8, 256)]
    public void PalettesKeepFewColorsExactly(BmpPixelFormat bits, int colors)
    {
        Random random = new(colors);
        Rgb8[] palette = [.. Enumerable.Range(0, colors).Select(_ => new Rgb8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))).Distinct()];
        using Raster<Rgb8> source = Raster.Create<Rgb8>(19, 9);
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgb8> row = source.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = palette[random.Next(palette.Length)];
            }
        }

        byte[] data = Save(source, new BmpWriterOptions { PixelFormat = bits });
        using Raster raster = Raster.Load(data);
        Assert.Equal(bits switch { BmpPixelFormat.Indexed1 => 1, BmpPixelFormat.Indexed2 => 2, BmpPixelFormat.Indexed4 => 4, _ => 8 }, ((BmpDetails)raster.Metadata.FormatDetails!).BitsPerPixel);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(data);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    [Theory]
    [InlineData(BmpPixelFormat.Indexed4, 16, 2u)]
    [InlineData(BmpPixelFormat.Indexed8, 256, 1u)]
    public void RunLengthRoundTripsRunsAndNoise(BmpPixelFormat bits, int colors, uint compression)
    {
        Random random = new(colors);
        Rgb8[] palette = [.. Enumerable.Range(0, colors).Select(i => new Rgb8((byte)i, (byte)(255 - i), (byte)(i * 7)))];
        foreach (int width in (int[])[1, 2, 3, 4, 5, 7, 254, 255, 256, 257, 600])
        {
            using Raster<Rgb8> source = Raster.Create<Rgb8>(width, 6);
            for (int y = 0; y < source.Height; y++)
            {
                Span<Rgb8> row = source.Row(y);
                int x = 0;
                while (x < width)
                {
                    // Runs of every length, alternating pairs and noise.
                    int kind = random.Next(3);
                    int length = Math.Min(width - x, random.Next(1, 300));
                    Rgb8 a = palette[random.Next(palette.Length)], b = palette[random.Next(palette.Length)];
                    for (int i = 0; i < length; i++, x++)
                    {
                        row[x] = kind switch { 0 => a, 1 => (i & 1) == 0 ? a : b, _ => palette[random.Next(palette.Length)] };
                    }
                }
            }

            byte[] data = Save(source, new BmpWriterOptions { PixelFormat = bits, RunLength = true });
            Assert.Equal(compression, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30)));
            Assert.Equal((uint)data.Length - BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(34)));
            using Raster raster = Raster.Load(data);
            Assert.Empty(raster.Metadata.Warnings);
            using Raster<Rgb8> back = Raster.Load<Rgb8>(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
            Assert.Equal(Bytes(source), Bytes(back));
        }
    }

    [Fact]
    public void RunLengthShrinksFlatImages()
    {
        using Raster<Rgb8> source = Raster.Create<Rgb8>(200, 100, new Rgb8(10, 20, 30));
        source.Row(50)[100] = new Rgb8(200, 0, 0);
        byte[] plain = Save(source, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8 });
        byte[] runs = Save(source, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8, RunLength = true });
        Assert.True(runs.Length < plain.Length / 20, $"{runs.Length} bytes against {plain.Length}");
        using Raster<Rgb8> back = Raster.Load<Rgb8>(runs);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    [Theory]
    [InlineData(BmpPixelFormat.Auto)]
    [InlineData(BmpPixelFormat.Indexed1)]
    [InlineData(BmpPixelFormat.Bgr24)]
    public void RunLengthNeedsA4Or8BitPalette(BmpPixelFormat layout)
    {
        using Raster<Rgb8> source = Raster.Create<Rgb8>(4, 4);
        Assert.Throws<ArgumentException>(() => Save(source, new BmpWriterOptions { PixelFormat = layout, RunLength = true }));
    }

    [Fact]
    public void ManyColorsAreQuantized()
    {
        using Raster<Rgba8> source = Random<Rgba8>(64, 64);
        byte[] data = Save(source, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed4, Palette = new PaletteOptions { Dither = DitherMode.None } });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        HashSet<Rgba8> colors = [];
        for (int y = 0; y < back.Height; y++)
        {
            colors.UnionWith(back.Row(y).ToArray());
        }

        Assert.InRange(colors.Count, 2, 16);
        Assert.All(colors, c => Assert.Equal(255, c.A));
    }

    [Fact]
    public void ForcedLayoutsDropOrAddAlpha()
    {
        using Raster<Rgba8> translucent = Raster.Create<Rgba8>(3, 3, new Rgba8(10, 20, 30, 40));
        using Raster<Rgba8> dropped = Raster.Load<Rgba8>(Save(translucent, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Bgr24 }));
        Assert.Equal(new Rgba8(10, 20, 30, 255), dropped.Row(0)[0]);

        using Raster<Rgb8> opaque = Raster.Create<Rgb8>(3, 3, new Rgb8(1, 2, 3));
        byte[] data = Save(opaque, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Bgra32 });
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28)));
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(new Rgba8(1, 2, 3, 255), back.Row(2)[2]);
    }

    [Fact]
    public void ResolutionIsWrittenInPixelsPerMeter()
    {
        using Raster<Rgb8> source = Raster.Create<Rgb8>(2, 2);
        source.Metadata.Resolution = new Resolution(300, 150, ResolutionUnit.PerInch);
        using Raster back = Raster.Load(Save(source));
        Assert.Equal(new Resolution(11811, 5906, ResolutionUnit.PerMeter), back.Metadata.Resolution);
    }

    [Fact]
    public void IccProfileGoesIntoAV5Header()
    {
        byte[] profile = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
        using Raster<Rgba8> source = Random<Rgba8>(5, 3);
        source.Metadata.IccProfile = profile;
        byte[] data = Save(source);
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14)));
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(profile, back.Metadata.IccProfile.ToArray());
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, back.Metadata.ColorSpace);
        Assert.Equal(Bytes(source), Bytes(back));

        byte[] stripped = Save(source, new BmpWriterOptions { StripMetadata = true });
        Assert.Equal(108u, BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(14)));
    }

    [Fact]
    public void SizesInTheHeaderAreRight()
    {
        using Raster<Rgb8> source = Random<Rgb8>(5, 3);
        byte[] data = Save(source);
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2)));
        Assert.Equal(54u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10)));
        Assert.Equal(16u * 3, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(34)));
    }

    private static Raster<TPixel> Random<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Random random = new((width * 31) + height);
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height);
        for (int y = 0; y < height; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(raster.Row(y)));
        }

        return raster;
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, BmpWriterOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, BmpFormat.Info, options ?? BmpWriterOptions.Default);
        return stream.ToArray();
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<byte> bytes = [];
        for (int y = 0; y < raster.Height; y++)
        {
            bytes.AddRange(MemoryMarshal.AsBytes(raster.Row(y)).ToArray());
        }

        return [.. bytes];
    }
}
