using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class WarpSamples
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BuiltInWarps()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region warps
        // Correct a wide-angle lens with the PanoTools coefficients a lens database gives (ImageMagick's Barrel), then swirl a
        // square region in place; size-keeping warps run inside Within.
        using Raster corrected = photo.Derive(ops => ops
            .Warp(Warps.Barrel(0.0, -0.018, 0.0))
            .Within(new Rect(300, 200, 200, 200), s => s.Warp(Warps.Swirl(90))), Ct);

        // Size-changing ones: wrap the image into a circle, or bend it into an arc.
        using Raster disc = photo.Derive(ops => ops.Warp(Warps.Polar(), new SamplingOptions { Background = Rgba8.White }), Ct);
        using Raster arc = photo.Derive(ops => ops.Warp(Warps.Arc(120)), Ct);

        // A magnifying glass over the duck's head, blending into the photo over 12 pixels.
        using Raster loupe = photo.Derive(ops => ops.Warp(Warps.Magnifier(new Rect(380, 60, 160, 160), 2.5f, 12)), Ct);
        #endregion

        Assert.Equal(photo.Width, corrected.Width);
        Assert.True(disc.Width == disc.Height && arc.Width > 0 && loupe.Width == photo.Width);
    }

    [Fact]
    public void CustomWarp()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region custom-warp
        // Any mapping from output points back to source points; the sampler antialiases wherever it shrinks the image.
        IWarp ripple = Warp.FromFunction(p => new Vector2(p.X + (6 * MathF.Sin(p.Y / 15)), p.Y));
        using Raster rippled = photo.Derive(ops => ops.Warp(ripple, new SamplingOptions { Edges = EdgeMode.Mirror }), Ct);

        // The same as an IWarp, mapping a whole row per call: no per-pixel delegate, so it is faster.
        using Raster again = photo.Derive(ops => ops.Warp(new Ripple(6, 15), new SamplingOptions { Edges = EdgeMode.Mirror }), Ct);
        #endregion

        Assert.Equal(rippled.Width, again.Width);
    }

    [Fact]
    public void DisplaceWithAMap()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("duck.png");

        #region displace
        // A displacement map the image's size: mid-gray moves nothing, red moves pixels sideways and green up and down.
        using Raster<Rgba8> map = Raster.Create<Rgba8>(photo.Width, photo.Height);
        map.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                byte wave = (byte)(128 + (100 * Math.Sin((x + y) / 12.0)));
                row[x] = new Rgba8(wave, 128, 128, 255);
            }
        });
        using Raster<Rgba8> rippled = photo.Derive(ops => ops.Displace(map, 16, 0), Ct);
        #endregion

        Assert.Equal(photo.Width, rippled.Width);
    }

    [Fact]
    public void OffsetAndTile()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region offset-tile
        // Roll the image half way round, so its edges meet in the middle, which shows whether it tiles seamlessly.
        using Raster rolled = photo.Derive(ops => ops.Offset(photo.Width / 2, photo.Height / 2), Ct);

        // Fill a 1920 × 1080 wallpaper with copies of the duck's head.
        using Raster wallpaper = photo.Derive(ops => ops.Tile(1920, 1080, source: new Rect(380, 60, 160, 160)), Ct);
        #endregion

        Assert.Equal((1920, 1080), (wallpaper.Width, wallpaper.Height));
        Assert.Equal(photo.Width, rolled.Width);
    }

    private sealed class Ripple(float amplitude, float period) : IWarp
    {
        public Size OutputSize(Size input) => input;

        public void Map(int y, Size input, Span<Vector2> source)
        {
            float cy = y + 0.5f, shift = amplitude * MathF.Sin(cy / period);
            for (int x = 0; x < source.Length; x++)
            {
                source[x] = new Vector2(x + 0.5f + shift, cy);
            }
        }
    }
}
