using System.Numerics;

using Egami.Drawing.Tests.Canvas;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Layers;

public class LayerTests
{
    private static readonly Rgba8 Red = new(220, 30, 40, 255);
    private static readonly Rgba8 Blue = new(20, 60, 230, 255);

    /// <summary>Exact on float targets; within two levels on 8-bit ones, whose layers round to premultiplied 8-bit values between calls.</summary>
    [Fact]
    public void AnOpaqueSourceOverLayerEqualsDrawingWithoutIt()
    {
        Compare<Rgba8>(2, (c, layered) =>
        {
            c.Clear(Rgba8.White);
            c.ClipPath(Path.Circle(new Vector2(40, 30), 26));
            if (layered)
            {
                c.SaveLayer();
            }

            c.FillRect(new RectF(5.5f, 5.25f, 40, 30), new SolidPaint(Red) { Opacity = 0.7f });
            c.FillCircle(new Vector2(50, 35), 20.3f, new LinearGradient(Vector2.Zero, new Vector2(80, 0), RgbaF32.FromRgba8(Blue), new RgbaF32(0, 1, 0, 0.5f)));
            if (layered)
            {
                c.Restore();
            }
        });

        Compare<RgbaF32>(0, (c, layered) =>
        {
            c.Clear(Rgba8.White);
            if (layered)
            {
                c.SaveLayer();
                c.SaveLayer();
            }

            c.FillRect(new RectF(5.5f, 5.25f, 40, 30), new SolidPaint(Red) { Opacity = 0.7f });
            if (layered)
            {
                c.Restore();
            }

            c.FillCircle(new Vector2(50, 35), 20.3f, new SolidPaint(Blue) { Opacity = 0.5f });
            if (layered)
            {
                c.Restore();
            }
        });
    }

    [Fact]
    public void GroupOpacityFadesOverlappingShapesAsOne()
    {
        using Raster<Rgba8> raster = Raster.Create(60, 30, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.SaveLayer(0.5f);
            canvas.FillRect(new RectF(5, 5, 30, 20), Red);
            canvas.FillRect(new RectF(25, 5, 30, 20), Red);
            canvas.Restore();
        }

        Rgba8 alone = raster.GetPixel(10, 10), overlap = raster.GetPixel(30, 10);
        Assert.Equal(alone, overlap);
        Assert.InRange(alone.R, 236, 239);
        Assert.InRange(alone.G, 141, 144);
    }

    [Fact]
    public void ALayersBlendModeAppliesToTheGroup()
    {
        using Raster<RgbaF32> layered = Raster.Create<RgbaF32>(20, 20), direct = Raster.Create<RgbaF32>(20, 20);
        using (RasterCanvas<RgbaF32> canvas = layered.CreateCanvas())
        {
            canvas.Clear(Red);
            canvas.SaveLayer(new LayerOptions { BlendMode = BlendMode.Multiply });
            canvas.FillRect(new RectF(0, 0, 20, 20), Blue);
            canvas.Restore();
        }

        using (RasterCanvas<RgbaF32> canvas = direct.CreateCanvas())
        {
            canvas.Clear(Red);
            canvas.FillRect(new RectF(0, 0, 20, 20), new SolidPaint(Blue) { BlendMode = BlendMode.Multiply });
        }

        Paints.GradientTests.Close(direct.GetPixel(9, 9), layered.GetPixel(9, 9));
    }

    [Fact]
    public void NonIsolatedLayersStartFromTheBackdrop()
    {
        SolidPaint multiply = new(Blue) { BlendMode = BlendMode.Multiply };
        using Raster<RgbaF32> isolated = Raster.Create<RgbaF32>(10, 10), shared = Raster.Create<RgbaF32>(10, 10);
        using (RasterCanvas<RgbaF32> canvas = isolated.CreateCanvas())
        {
            canvas.Clear(Red);
            canvas.SaveLayer();
            canvas.FillRect(new RectF(0, 0, 10, 10), multiply);
            canvas.Restore();
        }

        using (RasterCanvas<RgbaF32> canvas = shared.CreateCanvas())
        {
            canvas.Clear(Red);
            canvas.SaveLayer(new LayerOptions { Isolated = false });
            canvas.FillRect(new RectF(0, 0, 10, 10), multiply);
            canvas.Restore();
        }

        // Isolated, multiply meets transparency and leaves blue; shared, it multiplies the red below.
        Paints.GradientTests.Close(RgbaF32.FromRgba8(Blue), isolated.GetPixel(5, 5), 1e-6f);
        RgbaF32 r = RgbaF32.FromRgba8(Red), b = RgbaF32.FromRgba8(Blue);
        Paints.GradientTests.Close(new RgbaF32(r.R * b.R, r.G * b.G, r.B * b.B, 1), shared.GetPixel(5, 5), 1e-5f);
    }

    [Fact]
    public void ABlurredLayerNearTheClipMatchesPadBlurCrop()
    {
        using Raster<Rgba8> layered = Raster.Create(64, 64, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = layered.CreateCanvas())
        {
            canvas.ClipRect(new RectF(16, 16, 32, 32));
            canvas.SaveLayer(new LayerOptions { Filter = o => o.Blur(3) });
            canvas.FillRect(new RectF(6, 20, 20, 20), Blue);
            canvas.FillCircle(new Vector2(52, 30), 6, Red);
            canvas.Restore();
            Assert.Equal(new Rect(0, 0, 64, 64), canvas.TargetArea);
        }

        // By hand: draw everything on a transparent copy, blur it, then blend it inside the clip.
        using Raster<Rgba8> manual = Raster.Create<Rgba8>(64, 64);
        using (RasterCanvas<Rgba8> canvas = manual.CreateCanvas())
        {
            canvas.FillRect(new RectF(6, 20, 20, 20), Blue);
            canvas.FillCircle(new Vector2(52, 30), 6, Red);
        }

        manual.Apply(o => o.Blur(3), TestContext.Current.CancellationToken);
        using Raster<Rgba8> expected = Raster.Create(64, 64, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = expected.CreateCanvas())
        {
            canvas.ClipRect(new RectF(16, 16, 32, 32));
            canvas.DrawImage(manual, Vector2.Zero);
        }

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Rgba8 a = layered.GetPixel(x, y), e = expected.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - e.R) <= 1 && Math.Abs(a.G - e.G) <= 1 && Math.Abs(a.B - e.B) <= 1, $"({x}, {y}): {a} against {e}");
            }
        }
    }

    [Fact]
    public void ABackdropChainFrostsWhatLiesBehind()
    {
        using Raster<Rgba8> raster = Raster.Create(40, 20, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 20, 20), Rgba8.Black);
            canvas.ClipRect(new RectF(10, 0, 20, 20));
            canvas.SaveLayer(new LayerOptions { Backdrop = o => o.ColorMatrix(ColorMatrix.Invert()) });
            canvas.Restore();
        }

        Assert.Equal(Rgba8.Black, raster.GetPixel(5, 5));
        Assert.Equal(Rgba8.White, raster.GetPixel(15, 5));
        Assert.Equal(Rgba8.Black, raster.GetPixel(25, 5));
        Assert.Equal(Rgba8.White, raster.GetPixel(35, 5));
    }

    [Fact]
    public void LayerBoundsLimitTheContentsAndAnEmptyClipDrawsNothing()
    {
        using Raster<Rgba8> raster = Raster.Create(30, 30, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.SaveLayer(new LayerOptions { Bounds = new RectF(10, 10, 10, 10) });
            Assert.Equal(new Rect(10, 10, 10, 10), canvas.TargetArea);
            canvas.Clear(Red);
            canvas.Restore();

            canvas.Save();
            canvas.ClipRect(RectF.Empty);
            canvas.SaveLayer(0.5f);
            canvas.Clear(Blue);
            canvas.Restore();
            canvas.Restore();
        }

        Assert.Equal(Red, raster.GetPixel(15, 15));
        Assert.Equal(Rgba8.White, raster.GetPixel(5, 15));
        Assert.Equal(Rgba8.White, raster.GetPixel(25, 25));
    }

    [Fact]
    public void AFilterThatChangesTheSizeIsRejectedAtRestore()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(20, 20);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        canvas.SaveLayer(new LayerOptions { Filter = o => o.DropShadow(2, 2, 1, Rgba8.Black) });
        canvas.FillRect(new RectF(5, 5, 5, 5), Red);
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(canvas.Restore);
        Assert.Contains("Expand", e.Message, StringComparison.Ordinal);
        Assert.Equal(1, canvas.SaveCount);

        canvas.SaveLayer(new LayerOptions { Filter = o => o.DropShadow(2, 2, 1, Rgba8.Black, new DropShadowOptions { Expand = false }) });
        canvas.FillRect(new RectF(5, 5, 5, 5), Red);
        canvas.Restore();
        Assert.NotEqual(0, raster.GetPixel(11, 11).A);
    }

    [Fact]
    public void EveryPixelTypeMergesLayers()
    {
        Check<Gray8>();
        Check<Rgb8>();
        Check<Rgba16>();
        Check<PremulBgra8>();
        Check<RgbaF16>();

        static void Check<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            using Raster<TPixel> raster = Raster.Create<TPixel>(12, 12);
            using (RasterCanvas<TPixel> canvas = raster.CreateCanvas())
            {
                canvas.Clear(Rgba8.White);
                canvas.SaveLayer(0.5f);
                canvas.FillRect(new RectF(0, 0, 6, 12), Rgba8.Black);
                canvas.FillRect(new RectF(3, 0, 6, 12), new LinearGradient(Vector2.Zero, new Vector2(12, 0), new RgbaF32(0, 0, 0, 1), new RgbaF32(0, 0, 0, 1)));
                canvas.Restore();
            }

            Rgba8 inside = raster.GetPixel(4, 6).ToRgba8(), outside = raster.GetPixel(10, 6).ToRgba8();
            Assert.InRange(inside.R, 120, 135);
            Assert.Equal(255, outside.R);
        }
    }

    [Fact]
    public void ATraceBackendSeesLayerCalls()
    {
        using TraceCanvas canvas = new(50, 50);
        canvas.SaveLayer(new LayerOptions { Opacity = 0.25f, BlendMode = BlendMode.Screen, Isolated = false });
        canvas.Save();
        canvas.Restore();
        canvas.Restore();
        Assert.Equal(["SaveLayer 0.25 Screen False", "Save", "Restore", "RestoreLayer"], canvas.Calls);
    }

    private static void Compare<TPixel>(int tolerance, Action<Egami.Drawing.Canvas, bool> draw)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> layered = Raster.Create<TPixel>(80, 60), direct = Raster.Create<TPixel>(80, 60);
        using (RasterCanvas<TPixel> canvas = layered.CreateCanvas())
        {
            draw(canvas, true);
        }

        using (RasterCanvas<TPixel> canvas = direct.CreateCanvas())
        {
            draw(canvas, false);
        }

        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                Vector4 a = layered.GetPixel(x, y).ToScaled(), b = direct.GetPixel(x, y).ToScaled();
                float limit = tolerance == 0 ? 1e-5f : (tolerance + 0.01f) / 255;
                Assert.True(Vector4.Abs(a - b) is { X: var dx, Y: var dy, Z: var dz, W: var dw } && dx <= limit && dy <= limit && dz <= limit && dw <= limit, $"({x}, {y}): {a} against {b}");
            }
        }
    }
}
