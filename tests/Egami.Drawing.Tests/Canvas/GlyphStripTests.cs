using System.Numerics;

using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// A run's glyphs blend a row at a time where no two of them blend the same pixel; the pixels must equal drawing the glyphs one
/// at a time, also when glyphs overlap, on premultiplied and straight targets over translucent pixels, clipped or not.
/// </summary>
public class GlyphStripTests
{
    [Theory]
    [InlineData(0f, false)]
    [InlineData(-4f, false)]
    [InlineData(-9f, true)]
    [InlineData(3.5f, true)]
    public void ARunBlendsAsItsGlyphsOneByOne(float spacing, bool clip)
    {
        Font font = new(TextTests.Noto.Value, 21);
        GlyphRun shaped = font.Shape("Wavy AVATAR jumps; ffi ||| WW");
        ushort[] glyphs = shaped.Glyphs.ToArray();
        Vector2[] positions = [.. shaped.Positions.ToArray().Select((p, i) => p + new Vector2(i * spacing, (i % 3) * 0.4f))];
        GlyphRun run = GlyphRun.Create(font, glyphs, positions);
        Paint[] paints = [new SolidPaint(new Rgba8(20, 40, 200, 255)), new SolidPaint(new Rgba8(200, 30, 60, 170)) { Opacity = 0.7f }, new SolidPaint(new Rgba8(250, 250, 250, 255)) { BlendMode = BlendMode.Multiply }];
        foreach (Paint paint in paints)
        {
            Same<PremulBgra8>(run, paint, clip);
            Same<Bgra8>(run, paint, clip);
            Same<Rgba8>(run, paint, clip);
        }
    }

    private static void Same<TPixel>(GlyphRun run, Paint paint, bool clip)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> whole = Draw<TPixel>(clip, canvas => canvas.DrawGlyphRun(run, new Vector2(3.3f, 30.7f), paint));
        using Raster<TPixel> single = Draw<TPixel>(clip, canvas =>
        {
            for (int i = 0; i < run.Count; i++)
            {
                canvas.DrawGlyphRun(GlyphRun.Create(run.Font, [run.Glyphs[i]], [run.Positions[i]]), new Vector2(3.3f, 30.7f), paint);
            }
        });

        for (int y = 0; y < whole.Height; y++)
        {
            Assert.True(whole.Row(y).SequenceEqual(single.Row(y)), $"{typeof(TPixel).Name} row {y}");
        }
    }

    private static Raster<TPixel> Draw<TPixel>(bool clip, Action<RasterCanvas<TPixel>> draw)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<Rgba8> background = Raster.Create<Rgba8>(420, 48);
        for (int y = 0; y < background.Height; y++)
        {
            for (int x = 0; x < background.Width; x++)
            {
                background.SetPixel(x, y, new Rgba8((byte)(x * 7), (byte)(y * 5), 90, (byte)((x + y) % 2 == 0 ? 255 : 40 + x % 200)));
            }
        }

        Raster<TPixel> image = background.As<TPixel>();
        using RasterCanvas<TPixel> canvas = image.CreateCanvas();
        if (clip)
        {
            canvas.ClipPath(Path.Oval(new RectF(20, 4, 360, 40)));
        }

        draw(canvas);
        return image;
    }
}
