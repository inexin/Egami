using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Paints
{
    [Fact]
    public void Gradients()
    {
        using Raster<Rgba8> raster = Raster.Create(400, 200, Rgba8.White);

        #region gradients
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // Two colors along a line; past the ends the end colors repeat (TileMode.Clamp).
            canvas.FillRect(new RectF(0, 0, 200, 100), new LinearGradient(new Vector2(0, 0), new Vector2(200, 0), new RgbaF32(0.1f, 0.3f, 0.9f, 1), new RgbaF32(1, 0.8f, 0.2f, 1)));

            // Several stops, mixed in Oklab for even midpoints, repeating every 40 pixels.
            canvas.FillRect(new RectF(200, 0, 200, 100), new LinearGradient(
                new Vector2(200, 0),
                new Vector2(240, 0),
                new GradientStop(0, new Rgba8(230, 40, 60, 255)),
                new GradientStop(0.5f, new Rgba8(250, 210, 60, 255)),
                new GradientStop(1, new Rgba8(230, 40, 60, 255)))
            {
                Tile = TileMode.Repeat,
                Interpolation = GradientInterpolation.Oklab,
            });

            // A radial gradient stretched into an ellipse by its local matrix, and a sweep around a center.
            canvas.FillOval(new RectF(10, 110, 180, 80), new RadialGradient(Vector2.Zero, 1, new RgbaF32(1, 1, 1, 1), new RgbaF32(0, 0.4f, 0.2f, 1))
            {
                LocalMatrix = Matrix3x2.CreateScale(90, 40) * Matrix3x2.CreateTranslation(100, 150),
            });
            canvas.FillCircle(new Vector2(300, 150), 45, new SweepGradient(
                new Vector2(300, 150),
                new GradientStop(0, new Rgba8(255, 0, 0, 255)),
                new GradientStop(0.5f, new Rgba8(0, 0, 255, 255)),
                new GradientStop(1, new Rgba8(255, 0, 0, 255))));
        }
        #endregion

        Assert.NotEqual(Rgba8.White, raster.GetPixel(100, 50));
    }

    [Fact]
    public void ComposedPaints()
    {
        using Raster<Rgba8> raster = Raster.Create(200, 100, Rgba8.White);

        #region composed-paints
        LinearGradient sky = new(new Vector2(0, 0), new Vector2(0, 100), new RgbaF32(0.2f, 0.4f, 0.9f, 1), new RgbaF32(0.9f, 0.6f, 0.4f, 1));
        NoisePaint clouds = NoisePaint.Fractal(0.02f, 0.05f, 4, seed: 3);

        // Noise multiplied onto the gradient, then the whole paint turned to sepia.
        Paint paint = new BlendPaint(sky, clouds, BlendMode.SoftLight).WithColorMatrix(ColorMatrix.Sepia(0.6f));
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.DrawPaint(paint);
        }
        #endregion

        Assert.Equal(255, raster.GetPixel(50, 50).A);
    }

    #region custom-paint
    // Concentric rings from C#: a record class whose Shade fills one row of premultiplied colors. It runs on several threads at
    // once, so it keeps no state.
    public sealed record class Rings(float Spacing) : CustomPaint
    {
        public override void Shade(int y, int x, Span<PremulRgbaF32> row, in Matrix3x2 inverse)
        {
            for (int i = 0; i < row.Length; i++)
            {
                Vector2 p = Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), inverse);
                float v = 0.5f + (0.5f * MathF.Cos(p.Length() * MathF.Tau / Spacing));
                row[i] = new PremulRgbaF32(v, v * 0.6f, 0.2f, 1);
            }
        }
    }
    #endregion

    [Fact]
    public void CustomPaint()
    {
        using Raster<Rgba8> raster = Raster.Create(100, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.Translate(50, 50);
            canvas.FillCircle(Vector2.Zero, 45, new Rings(12));
        }

        Assert.NotEqual(Rgba8.White, raster.GetPixel(50, 50));
    }

    [Fact]
    public void Noise()
    {
        using Raster<Rgba8> raster = Raster.Create(256, 256, Rgba8.White);

        #region noise
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // SVG's feTurbulence fractal noise, stitched so a 128-pixel tile repeats without a seam, made gray by a color matrix.
            Paint stone = NoisePaint.Fractal(0.03f, 0.03f, octaves: 5, seed: 7, tileSize: new Size(128, 128))
                .WithColorMatrix(new ColorMatrix([0.3f, 0.6f, 0.1f, 0, 0, 0.3f, 0.6f, 0.1f, 0, 0, 0.3f, 0.6f, 0.1f, 0, 0, 0, 0, 0, 0, 1]));
            canvas.DrawPaint(stone);

            // Smooth simplex noise as a soft overlay.
            canvas.DrawPaint(NoisePaint.Simplex(0.01f, octaves: 3) with { BlendMode = BlendMode.Overlay, Opacity = 0.5f });
        }
        #endregion

        Assert.Equal(255, raster.GetPixel(10, 10).A);
    }

    [Fact]
    public void ImagePaints()
    {
        using SampleFiles files = new();

        #region image-paint
        // Fill a shape with a photo tiled as a pattern, half size, turned 15 degrees.
        using Raster photo = Raster.Load("duck.png");
        using Raster<Rgba8> raster = Raster.Create(400, 300, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            ImagePaint pattern = new(photo, TileMode.Mirror, TileMode.Mirror)
            {
                LocalMatrix = Matrix3x2.CreateScale(0.5f) * Matrix3x2.CreateRotation(MathF.PI / 12),
            };
            canvas.FillPath(Path.Star(new Vector2(200, 150), 140, 60, 5), pattern);
        }
        #endregion

        Assert.NotEqual(Rgba8.White, raster.GetPixel(200, 150));
    }

    [Fact]
    public void Layers()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, Rgba8.White);

        #region layers
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // Group opacity: the overlapping circles fade as one shape, with no darker overlap.
            canvas.SaveLayer(0.5f);
            canvas.FillCircle(new Vector2(80, 100), 60, new Rgba8(200, 30, 30, 255));
            canvas.FillCircle(new Vector2(140, 100), 60, new Rgba8(200, 30, 30, 255));
            canvas.Restore();

            // A group blended with Multiply and blurred as a whole; the layer grows by the blur's reach, so nothing is cut.
            canvas.SaveLayer(new LayerOptions { BlendMode = BlendMode.Multiply, Filter = ops => ops.Blur(4) });
            canvas.FillRect(new RectF(180, 40, 90, 120), new Rgba8(40, 120, 220, 255));
            canvas.FillCircle(new Vector2(225, 100), 30, Rgba8.White);
            canvas.Restore();
        }
        #endregion

        Rgba8 overlap = raster.GetPixel(110, 100), single = raster.GetPixel(40, 100);
        Assert.Equal(single, overlap);
    }

    [Fact]
    public void SoftMasks()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");
        using Raster<Rgba8> raster = Raster.Create(photo.Width, photo.Height, Rgba8.White);

        #region soft-mask
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // A luminosity mask, as a PDF soft mask: white shows the photo, black hides it, a gradient fades it out to the right.
            RectF area = new(0, 0, photo.Width, photo.Height);
            SoftMask fade = new(mask => mask.FillRect(area, new LinearGradient(new Vector2(area.Width * 0.6f, 0), new Vector2(area.Width, 0), new RgbaF32(1, 1, 1, 1), new RgbaF32(0, 0, 0, 1))));
            canvas.SaveLayer(new LayerOptions { Mask = fade });
            canvas.DrawImage(photo, Vector2.Zero);
            canvas.Restore();
        }
        #endregion

        Assert.Equal(Rgba8.White, raster.GetPixel(photo.Width - 1, photo.Height / 2));
    }

    [Fact]
    public void Blur()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 120, Rgba8.White);

        #region blur
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // A soft-edged card (exact, from the error function) and a neon outline: the stroke's outer blur only, then the stroke.
            canvas.FillRoundRect(new RoundRect(new RectF(20, 20, 110, 80), 12), new SolidPaint(new Rgba8(30, 30, 30, 255)) { Blur = new MaskBlur(6) });
            Path ring = Path.Circle(new Vector2(220, 60), 40);
            canvas.StrokePath(ring, new Stroke(4), new SolidPaint(new Rgba8(255, 40, 200, 255)) { Blur = new MaskBlur(8, BlurStyle.Outer) });
            canvas.StrokePath(ring, new Stroke(4), new Rgba8(255, 200, 250, 255));
        }
        #endregion

        Assert.NotEqual(Rgba8.White, raster.GetPixel(75, 60));
    }

    [Fact]
    public void Shadows()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, new Rgba8(240, 240, 240, 255));

        #region shadows
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // A card floating 12 pixels up: its ambient and spot shadows, then the card itself.
            RoundRect card = new(new RectF(60, 40, 180, 110), 10);
            canvas.DrawShadow(Path.RoundRect(card), new ShadowOptions { Elevation = 12 });
            canvas.FillRoundRect(card, Rgba8.White);

            // A whole group's drop shadow: a layer with a drop shadow filter that keeps the layer's size.
            canvas.SaveLayer(new LayerOptions { Filter = ops => ops.DropShadow(3, 4, 3, new Rgba8(0, 0, 0, 90), new DropShadowOptions { Expand = false }) });
            canvas.FillCircle(new Vector2(110, 95), 20, new Rgba8(230, 80, 40, 255));
            canvas.FillRect(new RectF(140, 80, 60, 30), new Rgba8(40, 120, 220, 255));
            canvas.Restore();
        }
        #endregion

        Assert.Equal(Rgba8.White, raster.GetPixel(80, 60));
    }

    [Fact]
    public void SkyGradient()
    {
        using SampleFiles files = new();

        #region sky-gradient
        // A dusk sky: stops in Oklch so the hues turn smoothly, dithered so the wide soft ramp shows no bands, and a sun.
        using Raster<Rgba8> sky = Raster.Create<Rgba8>(800, 450);
        using (RasterCanvas<Rgba8> canvas = sky.CreateCanvas())
        {
            canvas.DrawPaint(new LinearGradient(
                new Vector2(0, 0),
                new Vector2(0, 450),
                new GradientStop(0, new Rgba8(20, 30, 90, 255)),
                new GradientStop(0.6f, new Rgba8(220, 110, 120, 255)),
                new GradientStop(1, new Rgba8(255, 200, 120, 255)))
            {
                Interpolation = GradientInterpolation.Oklch(),
                Dither = true,
            });
            canvas.FillCircle(new Vector2(560, 380), 160, new RadialGradient(new Vector2(560, 380), 160, new RgbaF32(1, 0.95f, 0.8f, 0.9f), new RgbaF32(1, 0.8f, 0.5f, 0)));
        }

        sky.Save("sky.png");
        #endregion

        Assert.Equal(255, sky.GetPixel(0, 0).A);
    }

    [Fact]
    public void CardWithShadow()
    {
        using SampleFiles files = new();

        #region card-shadow
        // A UI card: a Material-style shadow from its elevation, the card, and a soft inner highlight along its top edge.
        using Raster<Rgba8> screen = Raster.Create(400, 260, new Rgba8(236, 239, 244, 255));
        using (RasterCanvas<Rgba8> canvas = screen.CreateCanvas())
        {
            RoundRect card = new(new RectF(50, 40, 300, 170), 14);
            canvas.DrawShadow(Path.RoundRect(card), new ShadowOptions { Elevation = 16 });
            canvas.FillRoundRect(card, Rgba8.White);
            canvas.Save();
            canvas.ClipRoundRect(card);
            canvas.FillRect(new RectF(50, 40, 300, 30), new LinearGradient(new Vector2(0, 40), new Vector2(0, 70), new RgbaF32(0.85f, 0.9f, 1, 1), new RgbaF32(1, 1, 1, 1)));
            canvas.Restore();
        }

        screen.Save("card.png");
        #endregion

        Assert.Equal(Rgba8.White, screen.GetPixel(200, 150));
    }

    [Fact]
    public void FadedPhotoEdge()
    {
        using SampleFiles files = new();

        #region faded-edge
        // A photo whose edges fade to transparent: an alpha soft mask of a radial gradient, opaque in the middle.
        using Raster photo = Raster.Load("duck.png");
        using Raster<Rgba8> faded = Raster.Create<Rgba8>(photo.Width, photo.Height);
        using (RasterCanvas<Rgba8> canvas = faded.CreateCanvas())
        {
            Vector2 center = new(photo.Width / 2f, photo.Height / 2f);
            float radius = MathF.Min(photo.Width, photo.Height) / 2f;
            SoftMask vignette = new(
                mask => mask.DrawPaint(new RadialGradient(center, radius, new GradientStop(0.6f, Rgba8.Black), new GradientStop(1, new Rgba8(0, 0, 0, 0)))),
                SoftMaskKind.Alpha);
            canvas.SaveLayer(new LayerOptions { Mask = vignette });
            canvas.DrawImage(photo, Vector2.Zero);
            canvas.Restore();
        }

        faded.Save("faded.png");
        #endregion

        Assert.Equal(0, faded.GetPixel(0, 0).A);
    }

    [Fact]
    public void GroupOpacity()
    {
        using SampleFiles files = new();

        #region group-opacity
        // A translucent logo made of overlapping parts: drawn in a layer at 60%, the overlaps do not show through.
        using Raster<Rgba8> banner = Raster.Create(300, 120, new Rgba8(30, 40, 60, 255));
        using (RasterCanvas<Rgba8> canvas = banner.CreateCanvas())
        {
            canvas.SaveLayer(0.6f);
            canvas.FillCircle(new Vector2(60, 60), 40, new Rgba8(255, 200, 0, 255));
            canvas.FillRoundRect(new RoundRect(new RectF(60, 40, 200, 40), 20), new Rgba8(255, 200, 0, 255));
            canvas.Restore();
        }

        banner.Save("banner.png");
        #endregion

        Assert.Equal(banner.GetPixel(70, 60), banner.GetPixel(150, 60));
    }

    [Fact]
    public void InkPaints()
    {
        using Raster<Rgba8> raster = Raster.Create(100, 60, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();

        #region ink-paints
        // A spot colour, overprinting what is below. An ink canvas prints its plate; this RGB canvas draws its appearance.
        var logo = Colors.InkColor.Spot("PANTONE 186 C", 1, Colors.InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
        canvas.FillRect(new RectF(10, 10, 80, 40), new SolidPaint(logo) { Overprint = Overprint.On });
        #endregion

        Assert.True(raster.GetPixel(50, 30).R > 150 && raster.GetPixel(50, 30).G < 80);
    }
}
