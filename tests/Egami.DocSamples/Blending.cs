using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Blending
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Modes()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");
        using Raster<Rgba8> paper = Raster.Create(photo.Width, photo.Height, new Rgba8(240, 225, 190, 255));

        #region blend-mode
        // One enum names every mode. Blend modes (Multiply to Luminosity) blend the colors, then cover what is under them.
        BlendMode[] darkening = [BlendMode.Multiply, BlendMode.Darken, BlendMode.ColorBurn];
        BlendMode[] cutting = [BlendMode.DestinationIn, BlendMode.DestinationOut];   // shape the destination by the image's alpha
        #endregion

        #region draw-image-mode
        // Print the photo onto paper: Multiply keeps the paper's texture under every color.
        paper.Apply(ops => ops.DrawImage(photo, new Point(0, 0), BlendMode.Multiply), Ct);

        // Or lighten it in linear light at half strength; only where the photo overlaps changes.
        paper.Apply(ops => ops.DrawImage(photo, new Point(10, 10), BlendMode.Screen, 0.5f, ColorSpace.Linear), Ct);
        #endregion

        Assert.Equal(3, darkening.Length + cutting.Length - 2);
    }

    [Fact]
    public void Color()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region blend-color
        // Warm the photo with an orange tint, and give it a gray-blue color grade.
        photo.Apply(ops => ops
            .BlendColor(new Rgba8(255, 200, 150, 255), BlendMode.Multiply)
            .BlendColor(new Rgba8(60, 80, 110, 255), BlendMode.Color, opacity: 0.3f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Arithmetic()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");
        using Raster other = photo.Derive(ops => ops.FlipHorizontal(), Ct);

        #region draw-arithmetic
        // Average two images: each channel becomes 0.5·S + 0.5·D.
        photo.Apply(ops => ops.DrawArithmetic(other, new Point(0, 0), k1: 0, k2: 0.5f, k3: 0.5f, k4: 0), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Rows()
    {
        #region compositor
        // Blend rows in your own code: a source row, a destination row, and coverage from an antialiased edge.
        Rgba8[] source = new Rgba8[256], destination = new Rgba8[256];
        byte[] coverage = new byte[256];
        for (int x = 0; x < 256; x++)
        {
            source[x] = new Rgba8((byte)x, 80, 200, 255);
            destination[x] = new Rgba8(250, 250, 240, 255);
            coverage[x] = (byte)Math.Clamp((x - 100) * 8, 0, 255);   // a soft edge from x = 100 to 132
        }

        Compositor.Blend<Rgba8>(BlendMode.Multiply, source, destination, coverage);

        // One color through coverage, the way a rasterizer fills a shape's spans.
        Compositor.Blend(BlendMode.SourceOver, new Rgba8(255, 0, 0, 128), destination.AsSpan(), coverage);
        #endregion

        Assert.Equal(new Rgba8(250, 250, 240, 255), destination[0]);
    }

    [Fact]
    public void Recipes()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region watermark-multiply
        // A dark logo that should read as printed on the photo: Multiply darkens by the logo's colors and never adds white.
        using Raster<Rgba8> logo = Raster.Create(120, 40, new Rgba8(40, 60, 120, 255));
        photo.Apply(ops => ops.DrawImage(logo, new Point(photo.Width - 140, photo.Height - 60), BlendMode.Multiply, opacity: 0.6f), Ct);
        #endregion

        #region tint
        // Tint toward sepia-orange, keeping the shading: Color takes the tint's hue and saturation and the photo's luminosity.
        photo.Apply(ops => ops.BlendColor(new Rgba8(220, 150, 80, 255), BlendMode.Color, opacity: 0.5f), Ct);
        #endregion

        photo.Apply(ops => ops.Add(new ScreenGradient()), Ct);
        Assert.True(photo.Width > 0);
    }

    #region blend-row
    /// <summary>A custom step that screens a horizontal gradient over each row, blending with the Compositor.</summary>
    public sealed class ScreenGradient : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int width = input.Destination.Width;
            TPixel[] gradient = new TPixel[width];
            for (int x = 0; x < width; x++)
            {
                gradient[x] = TPixel.FromRgba8(new Rgba8(255, 220, 160, (byte)(255 * x / Math.Max(1, width - 1))));
            }

            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    input.Source.Row(y).CopyTo(input.Destination.Row(y));
                    Compositor.Blend<TPixel>(BlendMode.Screen, gradient, input.Destination.Row(y));
                }
            });
        }
    }
    #endregion
}
