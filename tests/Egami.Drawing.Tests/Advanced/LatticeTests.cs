using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class LatticeTests
{
    private static readonly SamplingOptions Nearest = new() { Filter = ResampleFilter.Nearest };

    [Fact]
    public void ADestinationOfTheImagesSizeReproducesTheImage()
    {
        using Raster<Rgba8> image = Pixels.Photo(30, 20);
        using Raster<Rgba8> nine = Pixels.Draw(40, 30, c => c.DrawImageNinePatch(image, new Rect(8, 6, 14, 8), new RectF(5, 4, 30, 20)));
        using Raster<Rgba8> plain = Pixels.Draw(40, 30, c => c.DrawImage(image, new Vector2(5, 4)));
        Pixels.Equal(nine, plain);

        // A lattice with every cell drawn takes the mesh path, and still gives the image back.
        Lattice lattice = new([4, 10, 20], [5, 12]) { CellKinds = Enumerable.Repeat(LatticeCellKind.Default, 12).ToArray() };
        using Raster<Rgba8> meshed = Pixels.Draw(40, 30, c => c.DrawImageLattice(image, lattice, new RectF(5, 4, 30, 20), Nearest));
        Pixels.Equal(meshed, plain);
    }

    [Fact]
    public void CornersKeepTheirSizeAndEdgesStretch()
    {
        using Raster<Rgba8> image = Pixels.Photo(20, 20);
        using Raster<Rgba8> drawn = Pixels.Draw(100, 60, c => c.DrawImageNinePatch(image, new Rect(5, 5, 10, 10), new RectF(0, 0, 100, 60), Nearest));

        // The four 5×5 corners are the image's own pixels.
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal(image.GetPixel(x, y), drawn.GetPixel(x, y));
                Assert.Equal(image.GetPixel(15 + x, y), drawn.GetPixel(95 + x, y));
                Assert.Equal(image.GetPixel(x, 15 + y), drawn.GetPixel(x, 55 + y));
                Assert.Equal(image.GetPixel(15 + x, 15 + y), drawn.GetPixel(95 + x, 55 + y));
            }
        }

        // The top edge stretches 10 columns over 90: column 5 + k maps to source column 5 + k·10/90.
        Assert.Equal(image.GetPixel(5, 2), drawn.GetPixel(5, 2));
        Assert.Equal(image.GetPixel(14, 2), drawn.GetPixel(94, 2));
    }

    [Fact]
    public void CornersShrinkInProportionWhenTheDestinationIsSmall()
    {
        (float[] source, float[] destination) = Lattice.Layout([5, 15], 0, 20, 0, 6);
        Assert.Equal([0f, 5, 15, 20], source);
        Assert.Equal(0, destination[0]);
        Assert.Equal(3, destination[1], 4);
        Assert.Equal(3, destination[2], 4);
        Assert.Equal(6, destination[3], 4);

        // Large enough: fixed spans keep their size, the stretchable span takes the rest.
        (_, float[] wide) = Lattice.Layout([5, 15], 0, 20, 10, 110);
        Assert.Equal([10f, 15, 105, 110], wide);

        // A division at the start makes the first span stretchable.
        (_, float[] first) = Lattice.Layout([0, 4], 0, 10, 0, 30);
        Assert.Equal([0f, 0, 24, 30], first);
    }

    [Fact]
    public void NeighboringCellsMeetWithoutSeams()
    {
        using Raster<Rgba8> image = Raster.Create(24, 24, new Rgba8(200, 30, 30, 255));
        foreach (RectF destination in new[] { new RectF(3.3f, 2.7f, 71.45f, 40.2f), new RectF(0.5f, 0.5f, 33.3f, 77.7f) })
        {
            using Raster<Rgba8> drawn = Pixels.Draw(80, 80, c => c.DrawImageNinePatch(image, new Rect(7, 7, 10, 10), destination), Rgba8.Black);
            Rect inner = new((int)MathF.Ceiling(destination.Left), (int)MathF.Ceiling(destination.Top), (int)MathF.Floor(destination.Right) - (int)MathF.Ceiling(destination.Left), (int)MathF.Floor(destination.Bottom) - (int)MathF.Ceiling(destination.Top));
            for (int y = inner.Y; y < inner.Bottom; y++)
            {
                for (int x = inner.X; x < inner.Right; x++)
                {
                    Assert.Equal(new Rgba8(200, 30, 30, 255), drawn.GetPixel(x, y));
                }
            }
        }
    }

    [Fact]
    public void CellsCanBeTransparentOrAFixedColor()
    {
        using Raster<Rgba8> image = Raster.Create(30, 30, new Rgba8(0, 0, 255, 255));
        LatticeCellKind[] kinds = [.. Enumerable.Repeat(LatticeCellKind.Default, 9)];
        kinds[4] = LatticeCellKind.FixedColor;
        kinds[0] = LatticeCellKind.Transparent;
        RgbaF32[] colors = new RgbaF32[9];
        colors[4] = new RgbaF32(0, 1, 0, 1);
        Lattice lattice = new([10, 20], [10, 20]) { CellKinds = kinds, CellColors = colors };
        using Raster<Rgba8> drawn = Pixels.Draw(60, 60, c => c.DrawImageLattice(image, lattice, new RectF(0, 0, 60, 60), Nearest));
        Assert.Equal(Rgba8.White, drawn.GetPixel(5, 5));
        Assert.Equal(new Rgba8(0, 255, 0, 255), drawn.GetPixel(30, 30));
        Assert.Equal(new Rgba8(0, 0, 255, 255), drawn.GetPixel(55, 55));
        Assert.Equal(new Rgba8(0, 0, 255, 255), drawn.GetPixel(30, 5));
    }

    [Fact]
    public void AndroidNinePatchMarkersBecomeALatticeAndPadding()
    {
        // A 12×10 image inside a one-pixel border: stretch columns 3..6 and 8..9, rows 2..5; content from (1, 1) to (10, 7).
        using Raster<Rgba8> file = Raster.Create(14, 12, default(Rgba8));
        Rgba8 black = new(0, 0, 0, 255);
        for (int x = 1; x < 13; x++)
        {
            for (int y = 1; y < 11; y++)
            {
                file.SetPixel(x, y, new Rgba8((byte)(x * 10), (byte)(y * 10), 100, 255));
            }
        }

        foreach (int x in new[] { 3, 4, 5, 8 })
        {
            file.SetPixel(x + 1, 0, black);
        }

        foreach (int y in new[] { 2, 3, 4 })
        {
            file.SetPixel(0, y + 1, black);
        }

        for (int x = 1; x < 10; x++)
        {
            file.SetPixel(x + 1, 11, black);
        }

        for (int y = 1; y < 7; y++)
        {
            file.SetPixel(13, y + 1, black);
        }

        (Raster inner, Lattice lattice, Rect? padding) = NinePatch.FromAndroid(file);
        using (inner)
        {
            Assert.Equal(12, inner.Width);
            Assert.Equal(10, inner.Height);
            Assert.Equal([3, 6, 8, 9], lattice.XDivs);
            Assert.Equal([2, 5], lattice.YDivs);
            Assert.Equal(new Rect(1, 1, 9, 6), padding);
            Assert.Equal(new Rgba8(10, 10, 100, 255), inner.As<Rgba8>().GetPixel(0, 0));
        }

        file.SetPixel(0, 5, new Rgba8(255, 0, 0, 255));
        Assert.Throws<ArgumentException>(() => NinePatch.FromAndroid(file));
    }

    [Fact]
    public void ALatticeMustFitTheImage()
    {
        using Raster<Rgba8> image = Pixels.Photo(10, 10);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        Assert.Throws<ArgumentException>(() => canvas.DrawImageLattice(image, new Lattice([12], [5]), new RectF(0, 0, 20, 20)));
        Assert.Throws<ArgumentException>(() => canvas.DrawImageLattice(image, new Lattice([5], [5]) { CellKinds = [LatticeCellKind.Default] }, new RectF(0, 0, 20, 20)));
        Assert.Throws<ArgumentException>(() => new Lattice([5, 5], [1]));
        Assert.Throws<ArgumentException>(() => Lattice.NinePatch(new Rect(2, 2, 0, 4)));
    }
}
