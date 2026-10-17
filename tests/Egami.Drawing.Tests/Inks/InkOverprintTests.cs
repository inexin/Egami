using Egami.Colors;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 10: overprint as PDF defines it. Off paints every plate; On only the inks the colour names (every process
/// plate for a converted RGB colour or an ink the canvas lacks); NonZero as On, leaving a CMYK colour's zero components alone.
/// Plates a fill does not paint keep the backdrop, at antialiased edges and inside groups with a blend mode too.
/// </summary>
public class InkOverprintTests
{
    public static TheoryData<string> Scenes() => [.. InkScenes.All.Where(s => s.Name.StartsWith("overprint-", StringComparison.Ordinal) && s.Name != "overprint-in-multiply-group").Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void TheOverprintMatrixIsExact(string name)
    {
        InkScene scene = InkScenes.Get(name);
        InkSet inks = InkSceneDrawer.InksOf(scene);
        using InkRaster plates = new(inks, scene.Width, scene.Height);
        using (InkCanvas canvas = new(plates))
        {
            InkSceneDrawer.Draw(scene, canvas);
        }

        SceneFill source = (SceneFill)scene.Steps[^1];
        float[] fill = new float[inks.Count];
        bool rgb = source.Color.RgbValue is not null;
        if (rgb)
        {
            // A converted RGB colour: its process tints are the canvas's own conversion.
            using InkRaster probe = new(inks, 1, 1);
            using (InkCanvas canvas = new(probe))
            {
                canvas.FillRect(new RectF(0, 0, 1, 1), InkSceneDrawer.PaintOf(scene, source with { Overprint = SceneOverprint.Off }));
            }

            for (int p = 0; p < inks.Count; p++)
            {
                fill[p] = probe.Plates[p].Row8(0)[0] / 255f;
            }
        }
        else
        {
            fill = Tints(source.Color, inks);
        }

        for (int y = 8; y < scene.Height; y++)
        {
            for (int x = 0; x < scene.Width; x++)
            {
                float[] backdrop = Tints(InkScenes.Backdrops[x / 8], inks);
                for (int p = 0; p < inks.Count; p++)
                {
                    float expected = Paints(source, inks[p].Name, p < 4, rgb) ? fill[p] : backdrop[p];
                    Assert.True(Math.Abs(plates.Plates[p].Row8(y)[x] - (expected * 255)) <= 0.51f, $"{name}, {inks[p].Name} at ({x}, {y}): {plates.Plates[p].Row8(y)[x]} against {expected * 255:F1}");
                }
            }
        }
    }

    [Fact]
    public void EdgesScaleOnlyThePaintedPlates()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null);
        using InkRaster plates = new(inks, 10, 1);
        using (InkCanvas canvas = new(plates))
        {
            canvas.FillRect(new RectF(0, 0, 10, 1), new SolidPaint(InkColor.Cmyk(0.6f, 0, 0, 0.4f)));
            canvas.FillRect(new RectF(2.25f, 0, 5.5f, 1), new SolidPaint(InkColor.Spot(inks[4], 1)) { Overprint = Overprint.On });
        }

        // Pixel 2 is a quarter covered, 7 three quarters: Gold takes that share; Cyan and Black are untouched everywhere.
        Assert.Equal([0, 0, 191, 255, 255, 255, 255, 191, 0, 0], plates.Plate("Gold")!.Row8(0).ToArray().Select(v => (int)v));
        Assert.All(plates.Plate("Cyan")!.Row8(0).ToArray(), v => Assert.Equal(153, v));
        Assert.All(plates.Plate("Black")!.Row8(0).ToArray(), v => Assert.Equal(102, v));
    }

    [Fact]
    public void InAMultiplyGroupUnpaintedPlatesKeepTheGroupsBackdrop()
    {
        InkScene scene = InkScenes.Get("overprint-in-multiply-group");
        InkSet inks = InkSceneDrawer.InksOf(scene);
        using InkRaster plates = new(inks, scene.Width, scene.Height);
        using (InkCanvas canvas = new(plates))
        {
            InkSceneDrawer.Draw(scene, canvas);
        }

        // DeviceCMYK 0, 0.8, 0, 0.2 under NonZero inside a non-isolated Multiply group. Inside, Cyan, Yellow and the spots keep the
        // backdrop the group started from; the group then multiplies onto the backdrop, so those plates are the backdrop
        // multiplied by itself, and Magenta and Black the fill's tints multiplied by it. Ghostscript agrees.
        for (int x = 0; x < scene.Width; x++)
        {
            float[] b = Tints(InkScenes.Backdrops[x / 8], inks);
            Expect(plates, "Cyan", x, Multiply(b[0], b[0]));
            Expect(plates, "Yellow", x, Multiply(b[2], b[2]));
            Expect(plates, "PANTONE 186 C", x, Multiply(b[4], b[4]));
            Expect(plates, "Gold", x, Multiply(b[5], b[5]));
            Expect(plates, "Magenta", x, Multiply(b[1], 0.8f));
            Expect(plates, "Black", x, Multiply(b[3], 0.2f));
        }

        static float Multiply(float a, float b) => 1 - ((1 - a) * (1 - b));

        static void Expect(InkRaster plates, string name, int x, float tint)
            => Assert.True(Math.Abs(plates.Plate(name)!.Row8(12)[x] - (tint * 255)) <= 1.5f, $"{name} at {x}: {plates.Plate(name)!.Row8(12)[x]} against {tint * 255:F1}");
    }

    // Whether the scene's source fill paints a plate.
    private static bool Paints(SceneFill fill, string ink, bool process, bool rgb)
    {
        if (fill.Overprint == SceneOverprint.Off)
        {
            return true;
        }

        if (rgb)
        {
            return process;
        }

        if (fill.Color.Process is float[] cmyk)
        {
            int i = Array.IndexOf(["Cyan", "Magenta", "Yellow", "Black"], ink);
            return i >= 0 && (fill.Overprint == SceneOverprint.On || cmyk[i] != 0);
        }

        return fill.Color.Inks!.Any(t => t.Name == ink);
    }

    private static float[] Tints(SceneColor color, InkSet inks)
    {
        float[] t = new float[inks.Count];
        if (color.Process is float[] cmyk)
        {
            cmyk.CopyTo(t, 0);
        }

        foreach ((string name, float tint) in color.Inks ?? [])
        {
            t[inks.IndexOf(name)] = tint;
        }

        return t;
    }
}
