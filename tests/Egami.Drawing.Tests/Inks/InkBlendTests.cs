using Egami.Colors;
using Egami.Operations;
using Egami.Operations.Compose;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 9: every blend mode on CMYK plus two spots equals pixels computed by hand from ISO 32000-2 section 11.3: the
/// W3C blend functions on complements (Egami's scalar BlendReference), Hue, Saturation, Color and Luminosity on Cyan, Magenta
/// and Yellow with Black from the backdrop (from the source for Luminosity), and spots blending Normal under those four and
/// under Difference and Exclusion (as Ghostscript and MuPDF do): within
/// one level at 8 bits and two at 16, the float arithmetic's own error.
/// </summary>
public class InkBlendTests
{
    public static TheoryData<BlendMode> Modes() => [.. Enum.GetValues<BlendMode>()];

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryModeEqualsHandComputedPixels(BlendMode mode)
    {
        InkScene scene = InkScenes.Get($"blend-{InkScenes.Name(mode)}");
        InkSet inks = InkSceneDrawer.InksOf(scene);
        string[] names = [.. inks.Select(c => c.Name)];
        foreach (InkDepth depth in Enum.GetValues<InkDepth>())
        {
            using InkRaster plates = new(inks, scene.Width, scene.Height, depth);
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
                    SceneColor backdrop = InkScenes.Backdrops[x / 8];
                    // The backdrop as the plates store it, so steep modes are judged on what they were given.
                    float[] b = [.. Tints(backdrop, names).Select(t => depth == InkDepth.Unorm8 ? MathF.Floor((t * 255) + 0.5f) / 255 : MathF.Floor((t * 65535) + 0.5f) / 65535)];
                    (float[] expected, float alpha) = y < 8 ? (b, 1f) : Reference(mode, Tints(InkScenes.BlendSource, names), y is >= 16 and < 24 ? 0.5f : 1, b, 1, names);
                    for (int p = 0; p <= names.Length; p++)
                    {
                        InkPlate plate = p < names.Length ? plates.Plates[p] : plates.Alpha;
                        float want = p < names.Length ? expected[p] : alpha;
                        float got = depth == InkDepth.Unorm8 ? plate.Row8(y)[x] / 255f : plate.Row16(y)[x] / 65535f;
                        float gap = MathF.Abs(got - want);
                        if (gap > worst)
                        {
                            worst = gap;
                            where = $"{plate.Name} at ({x}, {y}): {got:F5} against {want:F5}";
                        }
                    }
                }
            }

            float levels = worst * (depth == InkDepth.Unorm8 ? 255 : 65535);
            Assert.True(levels <= (depth == InkDepth.Unorm8 ? 1.01f : 2.01f), $"{mode} {depth}: {levels:F2} levels, {where}");
        }
    }

    [Fact]
    public void NonSeparableModesKeepTheBackdropsBlackAndBlendSpotsNormally()
    {
        // Hue over a rich black: Black stays the backdrop's; the spot, under the source's alpha 1, takes the source's tint.
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null);
        using InkRaster plates = new(inks, 2, 1);
        using (InkCanvas canvas = new(plates))
        {
            canvas.FillRect(new RectF(0, 0, 2, 1), new SolidPaint(InkColor.Mix(new InkTint(Colorant.Cyan, 0.2f), new InkTint(Colorant.Black, 0.9f), new InkTint(inks[4], 0.3f))));
            canvas.FillRect(new RectF(0, 0, 1, 1), new SolidPaint(InkColor.Mix(new InkTint(Colorant.Magenta, 0.8f), new InkTint(Colorant.Black, 0.1f), new InkTint(inks[4], 0.7f))) { BlendMode = BlendMode.Hue });
            canvas.FillRect(new RectF(1, 0, 1, 1), new SolidPaint(InkColor.Mix(new InkTint(Colorant.Magenta, 0.8f), new InkTint(Colorant.Black, 0.1f), new InkTint(inks[4], 0.7f))) { BlendMode = BlendMode.Luminosity });
        }

        Assert.Equal(230, plates.Plate("Black")!.Row8(0)[0]);   // 0.9, the backdrop's
        Assert.Equal(26, plates.Plate("Black")!.Row8(0)[1]);    // 0.1, the source's
        Assert.Equal(179, plates.Plate("Gold")!.Row8(0)[0]);    // 0.7: Normal
        Assert.Equal(179, plates.Plate("Gold")!.Row8(0)[1]);
    }

    private static float[] Tints(SceneColor color, string[] names)
    {
        float[] t = new float[names.Length];
        foreach ((string name, float tint) in color.Inks ?? [])
        {
            t[Array.IndexOf(names, name)] = tint;
        }

        return t;
    }

    /// <summary>One source colour (straight tints, alpha 1 times opacity) over one backdrop, by the PDF rules; premultiplied tints out.</summary>
    internal static (float[] Tints, float Alpha) Reference(BlendMode mode, float[] s, float opacity, float[] b, float ba, string[] names)
    {
        double sa = opacity;
        float[] result = new float[s.Length];
        double alpha = 0;
        bool nonSeparable = BlendReference.IsNonSeparable(mode);
        int[] cmy = [Array.IndexOf(names, "Cyan"), Array.IndexOf(names, "Magenta"), Array.IndexOf(names, "Yellow")];
        if (nonSeparable)
        {
            var r = BlendReference.Blend(mode, (sa * (1 - s[cmy[0]]), sa * (1 - s[cmy[1]]), sa * (1 - s[cmy[2]]), sa), (ba * (1 - b[cmy[0]]), ba * (1 - b[cmy[1]]), ba * (1 - b[cmy[2]]), ba));
            (result[cmy[0]], result[cmy[1]], result[cmy[2]]) = ((float)(r.A - r.R), (float)(r.A - r.G), (float)(r.A - r.B));
            alpha = r.A;
            for (int p = 0; p < s.Length; p++)
            {
                if (cmy.Contains(p))
                {
                    continue;
                }

                double cs = 1 - s[p], cb = 1 - b[p];
                double blended = names[p] == "Black" && mode != BlendMode.Luminosity ? cb : cs;
                double colour = ((1 - sa) * ba * cb) + ((1 - ba) * sa * cs) + (sa * ba * blended);
                result[p] = (float)(alpha - colour);
            }

            return (result, (float)alpha);
        }

        for (int p = 0; p < s.Length; p++)
        {
            // Spots (after the four process inks) blend Normal under Difference and Exclusion.
            BlendMode own = p >= 4 && mode is BlendMode.Difference or BlendMode.Exclusion ? BlendMode.SourceOver : mode;
            var r = BlendReference.Blend(own, (sa * (1 - s[p]), 0, 0, sa), (ba * (1 - b[p]), 0, 0, ba));
            result[p] = (float)Math.Clamp(r.A - r.R, 0, 1);
            alpha = r.A;
        }

        return (result, (float)Math.Clamp(alpha, 0, 1));
    }
}
