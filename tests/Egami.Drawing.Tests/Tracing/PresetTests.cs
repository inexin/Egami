using System.Numerics;

using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>The <see cref="TraceOptions"/> presets: what each sets, and that each suits the art it is named for.</summary>
public class PresetTests
{
    public static TheoryData<string> Names() => ["Default", "BlackAndWhite", "Logo", "Poster", "Photo", "LineArt", "Sketch", "PixelArt"];

    [Fact]
    public void PresetsSetTheirModes()
    {
        Assert.Equal(TraceMode.BlackAndWhite, TraceOptions.Default.Mode);
        Assert.False(TraceOptions.Default.IgnoreWhite);
        Assert.True(TraceOptions.BlackAndWhite.IgnoreWhite);
        Assert.Equal(TraceMode.Logo, TraceOptions.Logo.Mode);
        Assert.False(TraceOptions.Logo.IgnoreWhite);
        Assert.Equal((TraceMode.Color, 6), (TraceOptions.Poster.Mode, TraceOptions.Poster.Colors));
        Assert.Equal((TraceMode.Color, 16), (TraceOptions.Photo.Mode, TraceOptions.Photo.Colors));
        Assert.True(TraceOptions.Photo.Smoothing > 0);
        Assert.False(TraceOptions.Photo.SnapToArcs);
        Assert.Equal(TraceMode.Centerline, TraceOptions.LineArt.Mode);
        Assert.Equal(TraceMode.Centerline, TraceOptions.Sketch.Mode);
        Assert.True(TraceOptions.Sketch.Noise > TraceOptions.LineArt.Noise);
        Assert.Equal(TraceMode.PixelArt, TraceOptions.PixelArt.Mode);
        Assert.False(TraceOptions.PixelArt.Exact);

        // A preset is a value: with changes a copy and leaves the preset alone.
        TraceOptions four = TraceOptions.Logo with { Colors = 4 };
        Assert.Equal(4, four.Colors);
        Assert.Equal(8, TraceOptions.Logo.Colors);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryPresetTracesTheSameScene(string name)
    {
        TraceOptions options = (TraceOptions)typeof(TraceOptions).GetProperty(name)!.GetValue(null)!;
        using Raster<Rgba8> raster = Raster.Create(120, 90, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(40, 45), 25, new Rgba8(20, 20, 20, 255));
            canvas.StrokePath(new PathBuilder().MoveTo(70, 20).LineTo(110, 70).ToPath(), new Stroke(3), new Rgba8(20, 20, 20, 255));
        }

        TraceResult result = raster.Trace(options);
        Assert.NotEmpty(result.Shapes);
        Assert.Contains(result.Shapes, s => s.Color.R < 64);
    }

    [Fact]
    public void PhotoAndPosterFollowAPhoto()
    {
        using Raster loaded = Raster.Load(Image("filters", "duck.png"));
        using Raster<Rgba8> photo = loaded.As<Rgba8>();
        TraceResult traced = photo.Trace(TraceOptions.Photo);
        TraceResult poster = photo.Trace(TraceOptions.Poster);

        // Over five test photos the mean error is 0.029 (Photo) and 0.035 (Poster) of full scale; this duck is among the harder ones.
        Assert.True(Error(photo, traced) < 0.05, $"photo error {Error(photo, traced)}");
        Assert.True(Error(photo, poster) < 0.06, $"poster error {Error(photo, poster)}");
        Assert.True(poster.Palette.Count <= 6);
        Assert.True(poster.Shapes.Count < traced.Shapes.Count);
    }

    [Fact]
    public void SketchDropsPaperGrainThatLineArtKeeps()
    {
        (Raster<Rgba8> clean, List<(Path Path, float Width)> strokes) = CenterlineTraceTests.Drawing(2);
        using (clean)
        {
            using Raster<Rgba8> grainy = clean.Derive(_ => { }, TestContext.Current.CancellationToken);
            Random random = new(7);
            for (int y = 0; y < grainy.Height; y++)
            {
                Span<Rgba8> row = grainy.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    int grain = random.Next(-35, 35) - (random.NextDouble() < 0.004 ? 150 : 0);
                    byte v = (byte)Math.Clamp(row[x].R + grain, 0, 255);
                    row[x] = new Rgba8(v, v, v, 255);
                }
            }

            TraceResult lineArt = grainy.Trace(TraceOptions.LineArt);
            TraceResult sketch = grainy.Trace(TraceOptions.Sketch);
            Assert.True(lineArt.Shapes.Count > strokes.Count, $"{lineArt.Shapes.Count} shapes");
            Assert.Equal(strokes.Count, sketch.Shapes.Count);
            (double mean, _, double width, int matched) = CenterlineTraceTests.Measure(sketch, strokes, [new Vector2(135, 110) * 2], 1.5f);
            Assert.Equal(strokes.Count, matched);
            Assert.True(mean < 0.25, $"mean {mean}");
            Assert.InRange(width, 0.9, 1.1);
        }
    }

    private static double Error(Raster<Rgba8> source, TraceResult result)
    {
        using Raster<Rgba8> back = Raster.Create(source.Width, source.Height, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = back.CreateCanvas())
        {
            result.Draw(canvas);
        }

        using Raster<Rgba8> flat = Raster.Create(source.Width, source.Height, Rgba8.White);
        flat.Apply(ops => ops.DrawImage(source, default(Point)));
        return Raster.Compare(flat, back).Overall.Mae;
    }

    private static string Image(params string[] parts) =>
        System.IO.Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "images", .. parts]);
}
