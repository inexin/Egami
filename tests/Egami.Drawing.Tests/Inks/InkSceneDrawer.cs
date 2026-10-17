using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>Draws an <see cref="InkScene"/> on any canvas: the ink canvas's exact checks use it, and egami-compare's gates.</summary>
public static class InkSceneDrawer
{
    /// <summary>
    /// Returns the ink set a scene draws into: FOGRA39's process inks and the scene's spots, or the seven-ink profile's inks
    /// (<paramref name="sevenInkProfile"/>) for a scene of seven process inks.
    /// </summary>
    public static InkSet InksOf(InkScene scene, ColorProfile? sevenInkProfile = null)
    {
        if (scene.Inks.Count(i => !i.Spot) > 4)
        {
            return InkSet.FromProfile(sevenInkProfile ?? throw new ArgumentNullException(nameof(sevenInkProfile), $"{scene} needs the seven-ink profile."));
        }

        InkSet set = InkSet.FromProfile(ColorProfile.Fogra39);
        foreach (SceneInk ink in scene.Inks.Where(i => i.Spot))
        {
            set = set.WithSpot(ink.Name, Appearance(ink));
        }

        return set;
    }

    /// <summary>Draws the scene's steps in order.</summary>
    public static void Draw(InkScene scene, Egami.Drawing.Canvas canvas)
    {
        foreach (SceneStep step in scene.Steps)
        {
            Draw(scene, step, canvas);
        }
    }

    /// <summary>The paint of a scene fill.</summary>
    public static Paint PaintOf(InkScene scene, SceneFill fill)
    {
        SolidPaint solid = fill.Color switch
        {
            { Process: float[] c } => new SolidPaint(InkColor.Cmyk(c[0], c[1], c[2], c[3])),
            { RgbValue: float[] c } => new SolidPaint(new RgbaF32(c[0], c[1], c[2], 1)),
            { GrayLevel: float g } => new SolidPaint(new RgbaF32(g, g, g, 1)),
            { Inks: { Count: 0 } } => new SolidPaint(InkColor.None),
            { Inks: var inks } => new SolidPaint(InkColor.Mix([.. inks!.Select(t => new InkTint(ColorantOf(scene, t.Name), t.Tint))])),
        };
        return solid with
        {
            BlendMode = fill.BlendMode,
            Opacity = fill.Opacity,
            Overprint = fill.Overprint switch
            {
                SceneOverprint.On => Overprint.On,
                SceneOverprint.NonZero => Overprint.NonZero,
                _ => Overprint.Off,
            },
        };
    }

    /// <summary>The colorant a scene names: a process ink, or a spot with its appearance.</summary>
    public static Colorant ColorantOf(InkScene scene, string name)
    {
        SceneInk ink = scene.Inks.Append(InkScenes.Pantone300).First(i => i.Name == name);
        return ink.Spot ? Colorant.Spot(name, Appearance(ink)) : Colorant.Process(name, Appearance(ink));
    }

    private static InkAppearance? Appearance(SceneInk ink) => ink.Appearance is float[] a ? InkAppearance.Cmyk(a[0], a[1], a[2], a[3]) : null;

    private static void Draw(InkScene scene, SceneStep step, Egami.Drawing.Canvas canvas)
    {
        switch (step)
        {
            case SceneFill fill:
                SceneRect r = fill.Area;
                canvas.FillRect(new RectF(r.X, r.Y, r.Width, r.Height), PaintOf(scene, fill));
                break;
            case SceneGroup group:
                canvas.SaveLayer(new LayerOptions
                {
                    BlendMode = group.BlendMode,
                    Opacity = group.Opacity,
                    Isolated = group.Isolated,
                    Knockout = group.Knockout,
                    Mask = group.Mask is SceneMask mask
                        ? new SoftMask(c => { foreach (SceneStep s in mask.Steps) { Draw(scene, s, c); } }, mask.Kind == SceneMaskKind.Alpha ? SoftMaskKind.Alpha : SoftMaskKind.Luminosity)
                        : null,
                    ColorSpace = group.Space == SceneSpace.Rgb ? ColorProfile.Srgb : null,
                });
                foreach (SceneStep inner in group.Steps)
                {
                    Draw(scene, inner, canvas);
                }

                canvas.Restore();
                break;
        }
    }
}
