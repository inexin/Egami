using Egami.Formats;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests;

/// <summary>Small images and helpers the format tests share.</summary>
internal static class TestImages
{
    /// <summary>Gets a context with every format of the package.</summary>
    public static EgamiContext Context { get; } = new EgamiContext().WithExtendedFormats();

    /// <summary>Gets the package's formats.</summary>
    public static TheoryData<string> Formats => ["PCX", "SGI", "Sun raster", "XBM", "XPM", "WBMP", "Farbfeld"];

    public static FormatInfo Format(string name) => Context.Formats.Single(f => f.Name == name);

    /// <summary>An RGBA gradient with a few transparent pixels, odd-sized so row padding shows.</summary>
    public static Raster<Rgba8> Colors(int width = 13, int height = 7)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)(x * 19), (byte)(y * 37), (byte)((x * y * 7) + 3), (byte)(x == y ? 0 : 255));
            }
        }

        return raster;
    }

    /// <summary>A black and white checkerboard of 3-pixel squares.</summary>
    public static Raster<Gray8> Bitmap(int width = 21, int height = 9)
    {
        Raster<Gray8> raster = Raster.Create<Gray8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Gray8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Gray8((byte)(((x / 3) + (y / 3)) % 2 == 0 ? 0 : 255));
            }
        }

        return raster;
    }

    public static byte[] Save(Raster raster, string format, WriterOptions? options = null)
    {
        using MemoryStream output = new();
        raster.Save(output, Format(format), options);
        return output.ToArray();
    }

    public static void AssertEqual<TPixel>(Raster<TPixel> expected, Raster<TPixel> actual)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"Row {y} differs.");
        }
    }
}

/// <summary>A stream that cannot seek, as a network stream.</summary>
internal sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
{
    public override bool CanSeek => false;

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
}
