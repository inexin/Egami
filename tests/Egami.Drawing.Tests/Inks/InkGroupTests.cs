using System.Numerics;

using Egami.Colors;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 11: groups and soft masks on plates, each pixel checked against a hand evaluation of the scene by the PDF
/// rules (ISO 32000-2 sections 11.4 to 11.6): isolated groups start transparent, non-isolated ones from the backdrop, knockout
/// groups composite each object with the group's first content, alpha and luminosity masks multiply the group; and an RGB
/// group equals converting its RGB result into the inks.
/// </summary>
public class InkGroupTests
{
    public static TheoryData<string> Scenes() => ["group-isolated", "group-non-isolated", "group-knockout", "group-non-isolated-knockout", "mask-alpha", "mask-luminosity"];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void GroupsEqualTheirHandEvaluation(string name)
    {
        InkScene scene = InkScenes.Get(name);
        InkSet inks = InkSceneDrawer.InksOf(scene);
        string[] names = [.. inks.Select(c => c.Name)];
        using InkRaster plates = new(inks, scene.Width, scene.Height);
        using (InkCanvas canvas = new(plates))
        {
            InkSceneDrawer.Draw(scene, canvas);
        }

        float worst = 0;
        string where = "";
        for (int y = 0; y < scene.Height; y++)
        {
            for (int x = 0; x < scene.Width; x++)
            {
                (float[] t, float a) = Evaluate(scene.Steps, x, y, new float[names.Length], 0, names);
                for (int p = 0; p <= names.Length; p++)
                {
                    InkPlate plate = p < names.Length ? plates.Plates[p] : plates.Alpha;
                    float want = p < names.Length ? t[p] * a : a;
                    float gap = MathF.Abs((plate.Row8(y)[x] / 255f) - want);
                    if (gap > worst)
                    {
                        (worst, where) = (gap, $"{plate.Name} at ({x}, {y}): {plate.Row8(y)[x]} against {want * 255:F1}");
                    }
                }
            }
        }

        Assert.True(worst * 255 <= 1.01f, $"{name}: {worst * 255:F2} levels, {where}");
    }

    [Fact]
    public void AnRgbGroupEqualsConvertingItsRgbResult()
    {
        // Two RGB shapes multiplied in an sRGB group at 80%, over a CMYK backdrop.
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39);
        RgbaF32 orange = new(0.95f, 0.6f, 0.1f, 1), blue = new(0.2f, 0.7f, 0.9f, 1);
        using InkRaster plates = new(inks, 30, 10);
        using (InkCanvas canvas = new(plates))
        {
            canvas.FillRect(new RectF(0, 0, 30, 10), new SolidPaint(InkColor.Cmyk(0.1f, 0.2f, 0, 0)));
            canvas.SaveLayer(new LayerOptions { ColorSpace = ColorProfile.Srgb, Opacity = 0.8f });
            canvas.FillRect(new RectF(0, 0, 20, 10), orange);
            canvas.FillRect(new RectF(10, 0, 20, 10), new SolidPaint(blue) { BlendMode = BlendMode.Multiply });
            canvas.Restore();
        }

        // The group's RGB result, converted, then composited at 80% over the backdrop.
        ColorTransform toInks = ColorTransform.Create(ColorProfile.Srgb, ColorProfile.Fogra39, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true });
        foreach ((int x, RgbaF32 rgb) in new[] { (5, orange), (15, new RgbaF32(orange.R * blue.R, orange.G * blue.G, orange.B * blue.B, 1)), (25, blue) })
        {
            float[] cmyk = new float[4];
            toInks.Apply([rgb.R, rgb.G, rgb.B], cmyk);
            float[] backdrop = [0.1f, 0.2f, 0, 0];
            string[] names = ["Cyan", "Magenta", "Yellow", "Black"];
            for (int c = 0; c < 4; c++)
            {
                float expected = (cmyk[c] * 0.8f) + (backdrop[c] * 0.2f);
                Assert.True(Math.Abs(plates.Plate(names[c])!.Row8(5)[x] - (expected * 255)) <= 1.5f, $"{names[c]} at {x}: {plates.Plate(names[c])!.Row8(5)[x]} against {expected * 255:F1}");
            }
        }
    }

    [Fact]
    public void AnRgbGroupKeepsSpotsAsPlates()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null);
        using InkRaster plates = new(inks, 4, 4);
        using (InkCanvas canvas = new(plates))
        {
            canvas.SaveLayer(new LayerOptions { ColorSpace = ColorProfile.Srgb });
            canvas.FillRect(new RectF(0, 0, 4, 4), new SolidPaint(InkColor.Spot(inks[4], 0.6f)));
            canvas.Restore();
        }

        Assert.Equal(153, plates.Plate("Gold")!.Row8(2)[2]);
        Assert.Equal(0, plates.Plate("Cyan")!.Row8(2)[2]);
    }

    // The scene's steps at one pixel, from a straight colour and an alpha; returns straight tints and alpha.
    private static (float[] T, float A) Evaluate(IReadOnlyList<SceneStep> steps, int x, int y, float[] t, float a, string[] names)
    {
        foreach (SceneStep step in steps)
        {
            (t, a) = Apply(step, x, y, t, a, names);
        }

        return (t, a);
    }

    private static (float[] T, float A) Apply(SceneStep step, int x, int y, float[] t, float a, string[] names)
    {
        switch (step)
        {
            case SceneFill fill when Inside(fill.Area, x, y):
                return Over(fill.BlendMode, Tints(fill.Color, names), fill.Opacity, t, a, names);
            case SceneGroup group:
                (float[] lt, float la) = group.Isolated ? (new float[names.Length], 0f) : (t, a);
                (float[] start, float startA) = (lt, la);
                foreach (SceneStep inner in group.Steps)
                {
                    // Knockout: each object composites with the group's first content and replaces what was there.
                    (lt, la) = group.Knockout && inner is SceneFill f && Inside(f.Area, x, y) ? Apply(inner, x, y, start, startA, names) : Apply(inner, x, y, lt, la, names);
                }

                if (group.Mask is SceneMask mask)
                {
                    la *= MaskValue(mask, x, y);
                }

                return Over(group.BlendMode, lt, group.Opacity * la, t, a, names);
            default:
                return (t, a);
        }
    }

    // A soft mask's value: the alpha of its fills, or the luminosity of gray fills over black.
    private static float MaskValue(SceneMask mask, int x, int y)
    {
        float alpha = 0, gray = 0;
        foreach (SceneFill fill in mask.Steps.OfType<SceneFill>().Where(f => Inside(f.Area, x, y)))
        {
            float level = fill.Color.GrayLevel ?? 0;
            gray = (level * fill.Opacity) + (gray * (1 - fill.Opacity));
            alpha = fill.Opacity + (alpha * (1 - fill.Opacity));
        }

        return mask.Kind == SceneMaskKind.Alpha ? alpha : gray;
    }

    // Source-over (or another mode) of a straight source with an alpha onto a straight backdrop, by InkBlendTests' reference.
    private static (float[] T, float A) Over(BlendMode mode, float[] s, float sa, float[] b, float ba, string[] names)
    {
        (float[] premultiplied, float alpha) = InkBlendTests.Reference(mode, s, sa, b, ba, names);
        return ([.. premultiplied.Select(v => alpha > 0 ? Math.Clamp(v / alpha, 0, 1) : 0)], alpha);
    }

    private static bool Inside(SceneRect r, int x, int y) => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;

    private static float[] Tints(SceneColor color, string[] names)
    {
        float[] t = new float[names.Length];
        foreach ((string name, float tint) in color.Inks ?? [])
        {
            t[Array.IndexOf(names, name)] = tint;
        }

        return t;
    }
}
