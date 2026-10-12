using System.Numerics;

using Egami.Pixels;
using Egami.Svg.Resolving;

namespace Egami.Svg.Tests;

/// <summary>Task 7: mesh gradients as Inkscape writes them (the comparison with Inkscape's export is in egami-compare).</summary>
public class MeshTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    [Fact]
    public void StopPathsReadAsCubicEdges()
    {
        Vector2 start = new(10, 20);
        Assert.True(SvgMesh.TryEdge("l 30,0", start, null, out Vector2[] line));
        Vector2[] expected = [new(10, 20), new(20, 20), new(30, 20), new(40, 20)];
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(Vector2.Distance(expected[i], line[i]) < 1e-4f));
        Assert.True(SvgMesh.TryEdge("c 1,2 3,4 5,6", start, null, out Vector2[] relative));
        Assert.Equal([new(10, 20), new(11, 22), new(13, 24), new(15, 26)], relative);
        Assert.True(SvgMesh.TryEdge("C 1,2 3,4 5,6", start, null, out Vector2[] absolute));
        Assert.Equal(new Vector2(5, 6), absolute[3]);

        // The edge that closes a patch may leave out its end, which is known.
        Assert.True(SvgMesh.TryEdge("c 1,2 3,4", start, new Vector2(0, 0), out Vector2[] closing));
        Assert.Equal(Vector2.Zero, closing[3]);
        Assert.True(SvgMesh.TryEdge("L", start, new Vector2(0, 0), out _));
        Assert.False(SvgMesh.TryEdge("m 1,2", start, null, out _));
        Assert.False(SvgMesh.TryEdge("c 1,2 3,4", start, null, out _));
        Assert.False(SvgMesh.TryEdge(null, start, null, out _));
    }

    [Fact]
    public void ACoonsPatchIsItsOwnTensorPatch()
    {
        // A flat square's net is the even 4×4 grid.
        Vector2[] square =
        [
            new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(3, 1), new(3, 2),
            new(3, 3), new(2, 3), new(1, 3), new(0, 3), new(0, 2), new(0, 1),
        ];
        Vector2[] net = SvgMesh.Tensor(square);
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                Assert.True(Vector2.Distance(new Vector2(col, row), net[(row * 4) + col]) < 1e-5f, $"({col}, {row}) is {net[(row * 4) + col]}");
            }
        }
    }

    [Theory]
    [InlineData("bilinear")]
    [InlineData("bicubic")]
    public void PatchesShareEdgesAndCornerColors(string type)
    {
        // Two patches side by side: red and blue at the left corners, green in the middle, red and blue at the right.
        SvgDocument document = SvgDocument.Parse($"""
            <svg {Ns} width='60' height='30'>
              <meshgradient id='m' gradientUnits='userSpaceOnUse' x='0' y='0' type='{type}'>
                <meshrow>
                  <meshpatch>
                    <stop path='l 30,0' stop-color='#f00'/>
                    <stop path='l 0,30' stop-color='#0f0'/>
                    <stop path='l -30,0' stop-color='#0f0'/>
                    <stop path='l 0,-30' stop-color='#00f'/>
                  </meshpatch>
                  <meshpatch>
                    <stop path='l 30,0'/>
                    <stop path='l 0,30' stop-color='#f00'/>
                    <stop path='l -30,0' stop-color='#00f'/>
                  </meshpatch>
                </meshrow>
              </meshgradient>
              <rect width='60' height='30' fill='url(#m)'/>
            </svg>
            """);
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Rgba8 corner = RenderTreeTests.Pixel(raster, 0, 0), middle = RenderTreeTests.Pixel(raster, 30, 15), right = RenderTreeTests.Pixel(raster, 59, 0);
        Assert.True(corner.R > 230 && corner.G < 25, corner.ToString());
        Assert.True(middle.G > 200, middle.ToString());
        Assert.True(right.R > 230 && right.G < 25, right.ToString());
        Assert.Empty(document.Warnings);
    }

    [Fact]
    public void OutsideTheMeshIsTransparentAndAnHrefSharesTheRows()
    {
        SvgDocument document = SvgDocument.Parse($"""
            <svg {Ns} width='40' height='40'>
              <meshgradient id='base' gradientUnits='userSpaceOnUse'>
                <meshrow>
                  <meshpatch>
                    <stop path='l 20,0' stop-color='#f00'/>
                    <stop path='l 0,20' stop-color='#f00'/>
                    <stop path='l -20,0' stop-color='#f00'/>
                    <stop path='l 0,-20' stop-color='#f00'/>
                  </meshpatch>
                </meshrow>
              </meshgradient>
              <meshgradient id='m' xlink:href='#base' x='10' y='10'/>
              <rect width='40' height='40' fill='url(#m)'/>
            </svg>
            """);
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(0, RenderTreeTests.Pixel(raster, 5, 5).A);
        Assert.Equal(new Rgba8(255, 0, 0, 255), RenderTreeTests.Pixel(raster, 20, 20));
        Assert.Equal(0, RenderTreeTests.Pixel(raster, 35, 35).A);
    }

    [Fact]
    public void AnUnreadableStopEndsTheMesh()
    {
        SvgDocument document = SvgDocument.Parse($"""
            <svg {Ns} width='20' height='20'>
              <meshgradient id='m' gradientUnits='userSpaceOnUse'>
                <meshrow><meshpatch><stop path='m 20,0' stop-color='#f00'/></meshpatch></meshrow>
              </meshgradient>
              <rect width='20' height='20' fill='url(#m)'/>
            </svg>
            """);
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(0, RenderTreeTests.Pixel(raster, 10, 10).A);
        Assert.Contains(document.Warnings, w => w.Id == "Svg.BadAttribute");
    }
}
