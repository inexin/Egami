using Egami.Colors;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.6.5 task 3: the ink raster and its plates.</summary>
public class InkRasterTests
{
    private static readonly InkSet Inks = InkSet.FromProfile(ColorProfile.Fogra39)
        .WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f))
        .WithSpot("Varnish", null, ColorantKind.Technical);

    [Fact]
    public void ANewRasterHasAPlatePerInkAndAnAlphaPlaneAllBlank()
    {
        using InkRaster raster = new(Inks, 7, 5);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "PANTONE 186 C", "Varnish"], raster.Plates.Select(p => p.Name));
        Assert.Equal("Alpha", raster.Alpha.Name);
        Assert.Null(raster.Alpha.Colorant);
        Assert.Same(Inks[4], raster.Plate("PANTONE 186 C")!.Colorant);
        Assert.Null(raster.Plate("pantone 186 c"));
        foreach (InkPlate plate in raster.Plates.Append(raster.Alpha))
        {
            Assert.Equal((7, 5, InkDepth.Unorm8), (plate.Width, plate.Height, plate.Depth));
            for (int y = 0; y < 5; y++)
            {
                Assert.All(plate.Row8(y).ToArray(), v => Assert.Equal(0, v));
            }
        }
    }

    [Fact]
    public void DisabledSpotsHaveNoPlate()
    {
        using InkRaster raster = new(Inks.WithDisabled("PANTONE 186 C"), 4, 4);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "Varnish"], raster.Plates.Select(p => p.Name));
        Assert.Null(raster.Plate("PANTONE 186 C"));
    }

    [Theory]
    [InlineData(InkDepth.Unorm8)]
    [InlineData(InkDepth.Unorm16)]
    public void PlatesRoundTripHandSetPlanesWithoutCopying(InkDepth depth)
    {
        using InkRaster raster = new(Inks, 33, 17, depth);
        for (int p = 0; p < raster.Plates.Count; p++)
        {
            for (int y = 0; y < raster.Height; y++)
            {
                for (int x = 0; x < raster.Width; x++)
                {
                    int value = ((p * 37) + (y * 11) + (x * 5)) % 256;
                    if (depth == InkDepth.Unorm8)
                    {
                        raster.Plates[p].Row8(y)[x] = (byte)value;
                    }
                    else
                    {
                        raster.Plates[p].Row16(y)[x] = (ushort)(value * 257);
                    }
                }
            }
        }

        for (int p = 0; p < raster.Plates.Count; p++)
        {
            InkPlate plate = raster.Plates[p];
            if (depth == InkDepth.Unorm8)
            {
                using Raster<Gray8> view = plate.AsRaster<Gray8>();
                Assert.Equal((33, 17), (view.Width, view.Height));
                for (int y = 0; y < view.Height; y++)
                {
                    Assert.True(view.Frames[0].Plane.Row(y).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(plate.Row8(y))));
                    Assert.Equal(((p * 37) + (y * 11)) % 256, view.Frames[0].Plane.Row(y)[0].L);
                }

                // The view is the plate: writing one shows in the other.
                view.Frames[0].Plane.Row(3)[4] = new Gray8(201);
                Assert.Equal(201, plate.Row8(3)[4]);
                Assert.Throws<InvalidOperationException>(() => plate.AsRaster<Gray16>());
                Assert.Throws<InvalidOperationException>(() => plate.Row16(0));
            }
            else
            {
                using Raster<Gray16> view = plate.AsRaster<Gray16>();
                for (int y = 0; y < view.Height; y++)
                {
                    Assert.Equal(((((p * 37) + (y * 11)) % 256) * 257), view.Frames[0].Plane.Row(y)[0].L);
                }

                view.Frames[0].Plane.Row(3)[4] = new Gray16(40000);
                Assert.Equal(40000, plate.Row16(3)[4]);
                Assert.Throws<InvalidOperationException>(() => plate.AsRaster<Gray8>());
                Assert.Throws<InvalidOperationException>(() => plate.Row8(0));
            }
        }

        // Disposing a plate's view leaves the plate alone.
        if (depth == InkDepth.Unorm8)
        {
            raster.Plates[1].AsRaster<Gray8>().Dispose();
            Assert.Equal(37, raster.Plates[1].Row8(0)[0]);
        }
        else
        {
            raster.Plates[1].AsRaster<Gray16>().Dispose();
            Assert.Equal(37 * 257, raster.Plates[1].Row16(0)[0]);
        }
    }

    [Fact]
    public void ViewsStopWorkingWhenTheRasterIsDisposed()
    {
        InkRaster raster = new(Inks, 8, 8);
        Raster<Gray8> view = raster.Plates[0].AsRaster<Gray8>();
        raster.Dispose();
        raster.Dispose();
        Assert.Throws<ObjectDisposedException>(() => view.Frames[0].Plane.Row(0));
        Assert.Throws<ObjectDisposedException>(() => raster.Plates[0].Row8(0));
        Assert.Throws<ObjectDisposedException>(() => raster.Alpha.AsRaster<Gray8>());
        view.Dispose();
    }

    [Fact]
    public void SafetyLimitsApply()
    {
        EgamiContext small = new() { Limits = new SafetyLimits { MaxWidth = 100, MaxPixels = 5000, MaxTotalBytes = 6 * 5000 } };
        Assert.Throws<LimitExceededException>(() => new InkRaster(Inks, 101, 10, context: small));
        Assert.Throws<LimitExceededException>(() => new InkRaster(Inks, 100, 51, context: small));

        // Six inks and alpha at 8 bits: 7 bytes a pixel, so 5000 pixels need 35000 bytes, over the 30000 allowed.
        Assert.Throws<LimitExceededException>(() => new InkRaster(Inks, 100, 50, context: small));
        using (new InkRaster(Inks.WithDisabled("Varnish"), 100, 50, context: small))
        {
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new InkRaster(Inks, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InkRaster(Inks, 10, 10, (InkDepth)7));
        Assert.Throws<ArgumentNullException>(() => new InkRaster(null!, 10, 10));
    }
}
