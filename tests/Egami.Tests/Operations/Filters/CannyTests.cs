using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>
/// Phase 2.5.4 task 9: CannyEdges' parts and properties. FilterReferenceTests gates the edge maps against ImageMagick's -canny:
/// every one of its edge pixels is one of Egami's and at least 99% of pixels agree. ImageMagick 7.1.1 keeps its tracing stack in
/// one row of its cache, so a trace deeper than the image is wide stops part way (and overwrites that row's magnitudes); Egami
/// follows every 8-connected pixel above the low threshold, so its maps hold 0 to 17% more edge pixels on the photos.
/// </summary>
public class CannyTests
{
    [Fact]
    public void SmoothingWeightsAreImageMagicksBlurKernel()
    {
        // ImageMagick 7.1.1's showKernel output for blur:0x1 and the middle of blur:0x1.4.
        float[] one = CannyOperation.Weights(1);
        Assert.Equal(9, one.Length);
        float[] expected = [0.000215825f, 0.00579007f, 0.059897f, 0.241811f, 0.384572f, 0.241811f, 0.059897f, 0.00579007f, 0.000215825f];
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(expected[i], one[i], 1e-6f);
        }

        float[] wider = CannyOperation.Weights(1.4f);
        Assert.Equal(13, wider.Length);
        Assert.Equal(0.279651f, wider[6], 1e-6f);
        Assert.Equal(3.94816e-05f, wider[0], 1e-9f);
    }

    [Fact]
    public void DirectionsSplitAt22AndAHalfDegrees()
    {
        // With y up: 0 vertical change (compare above and below), 1 down-right diagonal, 2 horizontal, 3 up-right diagonal.
        Assert.Equal(0, CannyOperation.Direction(0, 1));
        Assert.Equal(2, CannyOperation.Direction(1, 0));
        Assert.Equal(2, CannyOperation.Direction(1, 0.4f));
        Assert.Equal(3, CannyOperation.Direction(1, 0.5f));
        Assert.Equal(3, CannyOperation.Direction(1, 2.4f));
        Assert.Equal(0, CannyOperation.Direction(1, 2.5f));
        Assert.Equal(1, CannyOperation.Direction(1, -1));
        Assert.Equal(1, CannyOperation.Direction(-1, 1));
        Assert.Equal(0, CannyOperation.Direction(-0.1f, 1));
    }

    [Fact]
    public void EdgesAreOnePixelWideWhiteOnBlackWithAlphaKept()
    {
        // A disc on a half-transparent background: the outline is a closed one-pixel curve.
        using Raster<Rgba8> disc = Raster.Create<Rgba8>(64, 48);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                bool inside = ((x - 31.5) * (x - 31.5)) + ((y - 23.5) * (y - 23.5)) < 15 * 15;
                disc.Row(y)[x] = inside ? new Rgba8(220, 220, 220, 255) : new Rgba8(30, 30, 30, 255);
            }
        }

        using Raster<Rgba8> edges = disc.Derive(o => o.CannyEdges(), Ct);
        int marked = 0;
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Rgba8 p = edges.Row(y)[x];
                Assert.True(p == Rgba8.White || p == Rgba8.Black, $"({x}, {y}) is {p}");
                marked += p == Rgba8.White ? 1 : 0;

                // No 2 × 2 block is all edge: the curve is thin.
                if (x < 63 && y < 47 && p == Rgba8.White)
                {
                    Assert.False(edges.Row(y)[x + 1] == Rgba8.White && edges.Row(y + 1)[x] == Rgba8.White && edges.Row(y + 1)[x + 1] == Rgba8.White, $"thick at ({x}, {y})");
                }
            }
        }

        // About the circumference, 2π × 15.
        Assert.InRange(marked, 80, 120);

        using Raster<Rgba8> faded = disc.Derive(o => o.Opacity(0.5f), Ct);
        using Raster<Rgba8> fadedEdges = faded.Derive(o => o.CannyEdges(), Ct);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Assert.Equal(faded.Row(y)[x].A, fadedEdges.Row(y)[x].A);
                Assert.Equal(edges.Row(y)[x].R, fadedEdges.Row(y)[x].R);
            }
        }
    }

    [Fact]
    public void FlatImageHasNoEdges()
    {
        using Raster<Rgba8> flat = Raster.Create(40, 30, new Rgba8(90, 120, 200, 255));
        using Raster<Rgba8> result = flat.Derive(o => o.CannyEdges(1, 0, 0), Ct);
        using Raster<Rgba8> black = Raster.Create(40, 30, Rgba8.Black);
        Same(black, result);
    }

    [Fact]
    public void HysteresisFollowsWeakEdgesFromStrongOnes()
    {
        // A step whose height fades along it: with a high threshold above the faint end, the faint part is kept only because it
        // connects to the strong part; a faint step on its own is dropped.
        using Raster<GrayF32> image = Raster.Create<GrayF32>(80, 40);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                float connected = x < 40 && y >= 10 ? 0.2f + (0.8f * (40 - x) / 40) : 0;
                float alone = x >= 50 && y >= 30 ? 0.25f : 0;
                image.Row(y)[x] = new GrayF32(connected + alone);
            }
        }

        using Raster<GrayF32> edges = image.Derive(o => o.CannyEdges(0, 0.1f, 0.6f), Ct);
        Assert.Equal(1, edges.Row(9)[35].L);   // faint end of the connected step
        Assert.Equal(1, edges.Row(9)[5].L);    // strong end
        Assert.Equal(0, edges.Row(29)[65].L);  // the lone faint step
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("duck", o => o.CannyEdges(1.4f, 0.05f, 0.2f));
        SameInsideArea("egret", new Rect(30, 300, 200, 100), o => o.CannyEdges());
    }

    [Fact]
    public void WorksAsAMask()
    {
        // A gray edge map is a mask as it is; a color one is read by its luminance (Auto would read its alpha).
        using Raster<Rgba8> photo = Load("duck");
        using Raster<Gray8> gray = photo.As<Gray8>();
        using Raster<Gray8> edges = gray.Derive(o => o.CannyEdges(), Ct);
        using Raster<Rgba8> colorEdges = photo.Derive(o => o.CannyEdges(), Ct);
        using Raster<Rgba8> marked = photo.Derive(o => o.Within(new Mask(edges), s => s.FillColor(Rgba8.Red)), Ct);
        using Raster<Rgba8> markedToo = photo.Derive(o => o.Within(new Mask(colorEdges, Channel: MaskChannel.Luminance), s => s.FillColor(Rgba8.Red)), Ct);
        int count = 0;
        for (int y = 0; y < photo.Height; y++)
        {
            for (int x = 0; x < photo.Width; x++)
            {
                bool edge = edges.Row(y)[x].L == 255;
                count += edge ? 1 : 0;
                Assert.Equal(edge ? Rgba8.Red : photo.Row(y)[x], marked.Row(y)[x]);
                Assert.Equal(colorEdges.Row(y)[x] == Rgba8.White ? Rgba8.Red : photo.Row(y)[x], markedToo.Row(y)[x]);
            }
        }

        Assert.True(count > 1000);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(-1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(float.NaN), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(101), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(1, 0.5f, 0.3f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(1, -0.1f, 0.3f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(1, 0.1f, 1.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.CannyEdges(1, 0.1f, float.NaN), Ct));
    }
}
