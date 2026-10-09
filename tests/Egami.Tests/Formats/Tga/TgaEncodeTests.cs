using System.Runtime.InteropServices;

using Egami.Formats.Tga;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tga;

public class TgaEncodeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTripsEveryLayout(bool rle)
    {
        using Raster<Rgba8> rgba = Pattern<Rgba8>(37, 11);
        using Raster<Rgb8> rgb = Pattern<Rgb8>(37, 11);
        using Raster<Gray8> gray = Pattern<Gray8>(37, 11);
        TgaWriterOptions options = new() { Rle = rle };
        AssertRoundTrip(rgba, options, 2, 32);
        AssertRoundTrip(rgb, options, 2, 24);
        AssertRoundTrip(gray, options, 3, 8);
    }

    [Fact]
    public void IndexedKeepsFewColorsWithAlphaInTheMap()
    {
        Rgba8[] colors = [new(255, 0, 0, 255), new(0, 255, 0, 128), new(0, 0, 255, 0), new(9, 9, 9, 255)];
        using Raster<Rgba8> source = Raster.Create<Rgba8>(23, 7);
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgba8> row = source.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = colors[((x / 3) + y) % colors.Length];
            }
        }

        foreach (bool rle in new[] { true, false })
        {
            byte[] data = Save(source, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Indexed8, Rle = rle });
            Assert.Equal(rle ? 9 : 1, data[2]);
            Assert.Equal(32, data[7]);
            using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
            Assert.Equal(Bytes(source), Bytes(back));
        }
    }

    [Fact]
    public void FooterStatesTheAlphaType()
    {
        using Raster<Rgba8> translucent = Pattern<Rgba8>(4, 4);
        using Raster<Rgb8> opaque = Pattern<Rgb8>(4, 4);
        using Raster translucentBack = Raster.Load(Save(translucent));
        using Raster opaqueBack = Raster.Load(Save(opaque));
        Assert.Equal(TgaAlphaType.Straight, ((TgaDetails)translucentBack.Metadata.FormatDetails!).AlphaType);
        Assert.Equal(TgaAlphaType.None, ((TgaDetails)opaqueBack.Metadata.FormatDetails!).AlphaType);
        Assert.Equal("TRUEVISION-XFILE.\0"u8.ToArray(), Save(opaque)[^18..]);
    }

    [Fact]
    public void AuthorCommentAndImageIdRoundTrip()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(3, 3);
        source.Metadata.Text.Add(new TextEntry("Author", "Lars"));
        source.Metadata.Text.Add(new TextEntry("Comment", "first line\nsecond line"));
        source.Metadata.FormatDetails = new TgaDetails { ImageId = "id"u8.ToArray() };
        using Raster back = Raster.Load(Save(source));
        Assert.Contains(new TextEntry("Author", "Lars"), back.Metadata.Text);
        Assert.Contains(new TextEntry("Comment", "first line\nsecond line"), back.Metadata.Text);
        Assert.Equal("id"u8.ToArray(), ((TgaDetails)back.Metadata.FormatDetails!).ImageId.ToArray());

        using Raster stripped = Raster.Load(Save(source, new TgaWriterOptions { StripMetadata = true }));
        Assert.Empty(stripped.Metadata.Text);
    }

    [Fact]
    public void PacketsNeverCrossARowEnd()
    {
        // Every pixel the same: a crossing encoder would need one packet per 128 pixels, this one needs one per row.
        using Raster<Rgb8> source = Raster.Create<Rgb8>(5, 4, new Rgb8(1, 2, 3));
        byte[] data = Save(source);
        Assert.Equal(18 + (4 * 4) + 495 + 26, data.Length);
    }

    [Fact]
    public void LongRowsSplitIntoPacketsOf128()
    {
        using Raster<Gray8> source = Raster.Create<Gray8>(300, 1, new Gray8(7));
        using Raster<Gray8> back = Raster.Load<Gray8>(Save(source));
        Assert.All(back.Row(0).ToArray(), p => Assert.Equal(7, p.L));
        using Raster<Gray8> noisy = Pattern<Gray8>(300, 2);
        using Raster<Gray8> noisyBack = Raster.Load<Gray8>(Save(noisy));
        Assert.Equal(Bytes(noisy), Bytes(noisyBack));
    }

    [Fact]
    public void TooLargeThrows()
    {
        using Raster<Gray8> wide = Raster.Create<Gray8>(70_000, 1);
        Assert.Throws<ArgumentException>(() => Save(wide));
    }

    private static void AssertRoundTrip<TPixel>(Raster<TPixel> source, TgaWriterOptions options, int type, int depth)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        byte[] data = Save(source, options);
        Assert.Equal(type + (options.Rle ? 8 : 0), data[2]);
        Assert.Equal(depth, data[16]);
        Assert.True(TgaFormat.Info.Matches(data.AsSpan(0, 18)));
        using Raster<TPixel> back = Raster.Load<TPixel>(data);
        Assert.Equal(Bytes(source), Bytes(back));
    }

    /// <summary>Random pixels with runs of repeats, so both packet kinds appear.</summary>
    private static Raster<TPixel> Pattern<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Random random = new((width * 7) + height);
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<TPixel> row = raster.Row(y);
            random.NextBytes(MemoryMarshal.AsBytes(row));
            for (int x = 1; x < row.Length; x++)
            {
                if (random.Next(3) == 0)
                {
                    row[x] = row[x - 1];
                }
            }
        }

        return raster;
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, TgaWriterOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, TgaFormat.Info, options ?? TgaWriterOptions.Default);
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
