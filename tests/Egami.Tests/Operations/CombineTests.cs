using Egami.Pixels;

namespace Egami.Tests.Operations;

public class CombineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AppendLaysImagesOutWithAlignment()
    {
        using Raster<Rgba8> a = Raster.Create(10, 6, Rgba8.Red);
        using Raster<Rgb8> b = Raster.Create(4, 10, new Rgb8(0, 255, 0));
        using Raster<Rgba8> strip = (Raster<Rgba8>)Raster.Append([a, b], Axis.Horizontal, 2, Rgba8.Blue, Alignment.End);
        Assert.Equal((16, 10), (strip.Width, strip.Height));
        Assert.Equal(Rgba8.Blue, strip.GetPixel(0, 0));
        Assert.Equal(Rgba8.Red, strip.GetPixel(0, 4));
        Assert.Equal(Rgba8.Blue, strip.GetPixel(11, 5));
        Assert.Equal(Rgba8.Lime, strip.GetPixel(12, 0));

        using Raster<Rgba8> column = (Raster<Rgba8>)Raster.Append([a, b], Axis.Vertical, alignment: Alignment.Center);
        Assert.Equal((10, 16), (column.Width, column.Height));
        Assert.Equal(default, column.GetPixel(2, 6));
        Assert.Equal(Rgba8.Lime, column.GetPixel(3, 6));
    }

    [Fact]
    public void GridCentersAndShrinksIntoCells()
    {
        using Raster<Rgba8> big = Raster.Create(40, 20, Rgba8.Red);
        using Raster<Rgba8> small = Raster.Create(6, 6, Rgba8.Lime);
        using Raster<Rgba8> sheet = (Raster<Rgba8>)Raster.Grid([big, small, small], columns: 2, cell: new Size(20, 20), spacing: 4, background: Rgba8.White);
        Assert.Equal((44, 44), (sheet.Width, sheet.Height));

        // The 40 × 20 image shrinks to 20 × 10 and sits in the middle of its cell; the small ones are centered as they are.
        Assert.Equal(Rgba8.White, sheet.GetPixel(10, 2));
        Assert.Equal(Rgba8.Red, sheet.GetPixel(10, 10));
        Assert.Equal(Rgba8.Lime, sheet.GetPixel(24 + 7, 7));
        Assert.Equal(Rgba8.Lime, sheet.GetPixel(7, 24 + 7));
        Assert.Equal(Rgba8.White, sheet.GetPixel(30, 30));
    }

    [Fact]
    public void SpriteSheetsPackWithoutOverlap()
    {
        Random random = new(42);
        List<Raster> sprites = [];
        try
        {
            for (int i = 0; i < 60; i++)
            {
                sprites.Add(Raster.Create(random.Next(8, 70), random.Next(8, 70), new Rgba8((byte)i, (byte)(i * 3), 100, 255)));
            }

            using SpriteSheet sheet = SpriteSheet.Pack(sprites, new SpritePackOptions { MaxWidth = 512 });
            using SpriteSheet again = SpriteSheet.Pack(sprites, new SpritePackOptions { MaxWidth = 512 });
            Assert.Equal(sheet.Frames, again.Frames);
            Rect canvas = new(0, 0, sheet.Image.Width, sheet.Image.Height);
            long area = 0;
            for (int i = 0; i < sprites.Count; i++)
            {
                Rect f = sheet.Frames[i];
                Assert.Equal((sprites[i].Width, sprites[i].Height), (f.Width, f.Height));
                Assert.True(canvas.Contains(f), $"sprite {i} at {f} leaves the sheet");
                for (int j = 0; j < i; j++)
                {
                    Assert.True(f.Intersect(sheet.Frames[j]).IsEmpty, $"sprites {i} and {j} overlap");
                }

                Raster<Rgba8> image = (Raster<Rgba8>)sheet.Image;
                Assert.Equal(new Rgba8((byte)i, (byte)(i * 3), 100, 255), image.GetPixel(f.X, f.Y));
                area += (long)f.Width * f.Height;
            }

            double fill = area / (double)((long)canvas.Width * canvas.Height);
            Assert.True(fill >= 0.85, $"fill {fill:P1}");
            Assert.True(sheet.Image.Width <= 512);
        }
        finally
        {
            foreach (Raster sprite in sprites)
            {
                sprite.Dispose();
            }
        }
    }

    [Fact]
    public void PaddingExtrusionTrimAndPowersOfTwo()
    {
        using Raster<Rgba8> sprite = Raster.Create<Rgba8>(20, 16);
        for (int y = 4; y < 10; y++)
        {
            sprite.Row(y).Slice(3, 8).Fill(Rgba8.Red);
        }

        using SpriteSheet sheet = SpriteSheet.Pack([sprite, sprite], new SpritePackOptions { Padding = 3, Extrude = 2, Trim = true, PowerOfTwo = true });
        Assert.Equal(new Point(3, 4), sheet.Offsets[0]);
        Assert.Equal((8, 6), (sheet.Frames[0].Width, sheet.Frames[0].Height));
        Assert.True(int.IsPow2(sheet.Image.Width) && int.IsPow2(sheet.Image.Height));
        Raster<Rgba8> image = (Raster<Rgba8>)sheet.Image;
        Rect f = sheet.Frames[0];
        Assert.Equal(Rgba8.Red, image.GetPixel(f.X - 2, f.Y - 2));
        Assert.Equal(Rgba8.Red, image.GetPixel(f.Right + 1, f.Bottom + 1));
        Assert.True(f.Intersect(new Rect(sheet.Frames[1].X - 2, sheet.Frames[1].Y - 2, 12, 10)).IsEmpty);
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        using Raster<Rgba8> wide = Raster.Create<Rgba8>(100, 4);
        Assert.Throws<ArgumentException>(() => Raster.Append([]));
        Assert.Throws<ArgumentNullException>(() => Raster.Append([wide, null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Append([wide], spacing: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Grid([wide], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Grid([wide], 1, new Size(0, 3)));
        ArgumentException e = Assert.Throws<ArgumentException>(() => SpriteSheet.Pack([wide, wide], new SpritePackOptions { MaxWidth = 64 }));
        Assert.Contains("Image 0", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpritePackOptions { Padding = -1 });
    }

    [Fact]
    public void TheFirstImagesTypeAndMetadataWin()
    {
        using Raster<Gray8> gray = Raster.Create(4, 4, new Gray8(128));
        using Raster<Rgba8> color = Raster.Create(4, 4, Rgba8.Red);
        using Raster strip = Raster.Append([gray, color]);
        Assert.IsType<Raster<Gray8>>(strip);
        Assert.Equal((8, 4), (strip.Width, strip.Height));
        _ = Ct;
    }
}
