using Egami.Operations;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// The Phase 2.6.5 test scenes: small pages of pixel-aligned rectangles that exercise every blend mode, overprint mode and group
/// kind on a set of inks. They are plain data, so the ink canvas's exact checks (tasks 7 to 11) draw them in Egami and
/// egami-compare writes them as PDF for Ghostscript and MuPDF to separate. Coordinates are pixels, y down; written as PDF, one
/// pixel is one point and the page is rendered at 72 dpi. Edges fall on whole pixels, so no antialiasing is involved.
/// </summary>
public static class InkScenes
{
    /// <summary>The spot every CMYK scene carries: a red with its CMYK appearance at full tint.</summary>
    public static readonly SceneInk Pantone186 = new("PANTONE 186 C", Spot: true, [0, 1, 0.81f, 0.04f]);

    /// <summary>A second spot, for scenes that need two.</summary>
    public static readonly SceneInk Gold = new("Gold", Spot: true, [0, 0.25f, 0.75f, 0.2f]);

    /// <summary>A spot no scene's ink set holds: it draws through its appearance into the process inks.</summary>
    public static readonly SceneInk Pantone300 = new("PANTONE 300 C", Spot: true, [1, 0.44f, 0, 0]);

    /// <summary>CMYK plus <see cref="Pantone186"/> and <see cref="Gold"/>.</summary>
    public static readonly IReadOnlyList<SceneInk> CmykTwoSpots =
        [SceneInk.Process("Cyan"), SceneInk.Process("Magenta"), SceneInk.Process("Yellow"), SceneInk.Process("Black"), Pantone186, Gold];

    /// <summary>
    /// The inks of the synthetic seven-colour profile, tests/images/icc/ncolor/7clr-mab-v4.icc, all process inks; Orange,
    /// Green and Violet carry rough CMYK appearances for backends without them (the PDF written for the oracles).
    /// </summary>
    public static readonly IReadOnlyList<SceneInk> SevenInks =
    [
        SceneInk.Process("Cyan"), SceneInk.Process("Magenta"), SceneInk.Process("Yellow"), SceneInk.Process("Black"),
        new("Orange", Spot: false, [0, 0.6f, 1, 0]), new("Green", Spot: false, [0.9f, 0, 0.9f, 0]), new("Violet", Spot: false, [0.7f, 0.9f, 0, 0]),
    ];

    /// <summary>The 12 separable and 4 non-separable blend modes PDF defines, which the oracles can render; Normal is SourceOver.</summary>
    public static readonly IReadOnlyList<BlendMode> PdfModes =
    [
        BlendMode.SourceOver, BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.Darken, BlendMode.Lighten,
        BlendMode.ColorDodge, BlendMode.ColorBurn, BlendMode.HardLight, BlendMode.SoftLight, BlendMode.Difference, BlendMode.Exclusion,
        BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity,
    ];

    /// <summary>
    /// Eight backdrop colours, one per 8-pixel column of a 64-pixel scene: paper, process inks alone and mixed, the spot alone
    /// and over process inks, a rich black, and the second spot. Picked so every blend mode's branches are reached on some plane.
    /// </summary>
    public static readonly IReadOnlyList<SceneColor> Backdrops =
    [
        SceneColor.Ink(),
        SceneColor.Ink(("Cyan", 1)),
        SceneColor.Ink(("Cyan", 0.2f), ("Magenta", 0.45f), ("Yellow", 0.8f)),
        SceneColor.Ink(("Magenta", 0.6f), ("Black", 0.3f)),
        SceneColor.Ink(("PANTONE 186 C", 0.7f)),
        SceneColor.Ink(("Cyan", 0.5f), ("Yellow", 0.35f), ("PANTONE 186 C", 0.4f)),
        SceneColor.Ink(("Cyan", 0.6f), ("Magenta", 0.5f), ("Yellow", 0.4f), ("Black", 1)),
        SceneColor.Ink(("Gold", 1), ("Black", 0.1f)),
    ];

    /// <summary>The source colour of the blend-mode scenes: every process ink and the first spot, none at 0 or 1.</summary>
    public static readonly SceneColor BlendSource = SceneColor.Ink(("Cyan", 0.15f), ("Magenta", 0.7f), ("Yellow", 0.3f), ("Black", 0.25f), ("PANTONE 186 C", 0.55f));

    /// <summary>Every scene, by name.</summary>
    public static IReadOnlyList<InkScene> All { get; } = Build();

    /// <summary>The scene named <paramref name="name"/>.</summary>
    public static InkScene Get(string name) => All.Single(s => s.Name == name);

    private static List<InkScene> Build()
    {
        List<InkScene> scenes = [];

        // Blend modes: rows 0-7 the backdrops alone; rows 8-15 the source over them at full opacity; rows 16-23 at 50%; rows 24-31
        // the source over paper. Every mode Egami has gets a scene; those PDF lacks are for the exact checks only.
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            scenes.Add(new InkScene($"blend-{Name(mode)}", 64, 32, CmykTwoSpots,
            [
                .. BackdropColumns(32),
                new SceneFill(new SceneRect(0, 8, 64, 8), BlendSource) { BlendMode = mode },
                new SceneFill(new SceneRect(0, 16, 64, 8), BlendSource) { BlendMode = mode, Opacity = 0.5f },
                new SceneFill(new SceneRect(0, 24, 64, 8), BlendSource) { BlendMode = mode },
            ]));
        }

        // Overprint: each mode with the source kinds that differ under it. Rows 0-7 backdrops alone, rows 8-23 the source.
        SceneColor[] sources =
        [
            SceneColor.Cmyk(0, 0.8f, 0, 0.2f),                                   // DeviceCMYK with zero components: NonZero keeps C and Y
            SceneColor.Ink(("PANTONE 186 C", 0.9f)),                             // a spot alone: On keeps every other plane
            SceneColor.Ink(("Magenta", 0), ("Black", 0.5f), ("Gold", 0.6f)),     // DeviceN naming Magenta at 0: NonZero still paints it, the zero rule being for CMYK colours
            SceneColor.Rgb(0.9f, 0.4f, 0.1f),                                    // converted RGB: names every process plate, no spot
        ];
        string[] kinds = ["cmyk", "spot", "devicen", "rgb"];
        foreach (SceneOverprint overprint in Enum.GetValues<SceneOverprint>())
        {
            for (int i = 0; i < sources.Length; i++)
            {
                scenes.Add(new InkScene($"overprint-{Name(overprint)}-{kinds[i]}", 64, 24, CmykTwoSpots,
                [
                    .. BackdropColumns(24),
                    new SceneFill(new SceneRect(0, 8, 64, 16), sources[i]) { Overprint = overprint },
                ]));
            }
        }

        // Overprint inside a Multiply group: unpainted planes keep the backdrop (PDF's compatible overprint).
        scenes.Add(new InkScene("overprint-in-multiply-group", 64, 24, CmykTwoSpots,
        [
            .. BackdropColumns(24),
            new SceneGroup([new SceneFill(new SceneRect(0, 8, 64, 16), sources[0]) { Overprint = SceneOverprint.NonZero }]) { BlendMode = BlendMode.Multiply, Isolated = false },
        ]));

        // A spot the ink set lacks draws its appearance into the process inks, overprint or not.
        scenes.Add(new InkScene("missing-spot", 64, 24, CmykTwoSpots,
        [
            .. BackdropColumns(24),
            new SceneFill(new SceneRect(0, 8, 64, 8), SceneColor.Ink(("PANTONE 300 C", 0.8f))),
            new SceneFill(new SceneRect(0, 16, 64, 8), SceneColor.Ink(("PANTONE 300 C", 0.8f))) { Overprint = SceneOverprint.On },
        ]));

        // Groups: two overlapping translucent Multiply shapes inside a group at 70% over the backdrops, in each kind of group.
        foreach ((string name, bool isolated, bool knockout) in new[] { ("isolated", true, false), ("non-isolated", false, false), ("knockout", true, true), ("non-isolated-knockout", false, true) })
        {
            scenes.Add(new InkScene($"group-{name}", 64, 32, CmykTwoSpots,
            [
                .. BackdropColumns(32),
                new SceneGroup(GroupContent()) { Isolated = isolated, Knockout = knockout, Opacity = 0.7f },
            ]));
        }

        // A group that blends its process part in RGB: two RGB shapes multiplied there, merged into the inks; the spot stays a plane.
        scenes.Add(new InkScene("group-rgb", 64, 32, CmykTwoSpots,
        [
            .. BackdropColumns(32),
            new SceneGroup(
            [
                new SceneFill(new SceneRect(0, 8, 40, 16), SceneColor.Rgb(0.95f, 0.6f, 0.1f)),
                new SceneFill(new SceneRect(24, 12, 40, 16), SceneColor.Rgb(0.2f, 0.7f, 0.9f)) { BlendMode = BlendMode.Multiply },
                new SceneFill(new SceneRect(16, 4, 16, 24), SceneColor.Ink(("PANTONE 186 C", 0.5f))) { BlendMode = BlendMode.Multiply },
            ]) { Space = SceneSpace.Rgb },
        ]));

        // Soft masks: an alpha mask from shapes at two opacities, and a luminosity mask from gray shapes, over a group's content.
        scenes.Add(new InkScene("mask-alpha", 64, 32, CmykTwoSpots,
        [
            .. BackdropColumns(32),
            new SceneGroup([new SceneFill(new SceneRect(0, 4, 64, 24), BlendSource)])
            {
                Mask = new SceneMask(SceneMaskKind.Alpha,
                [
                    new SceneFill(new SceneRect(0, 0, 32, 32), SceneColor.Gray(0)) { Opacity = 0.8f },
                    new SceneFill(new SceneRect(32, 0, 32, 16), SceneColor.Gray(0)) { Opacity = 0.3f },
                ]),
            },
        ]));
        scenes.Add(new InkScene("mask-luminosity", 64, 32, CmykTwoSpots,
        [
            .. BackdropColumns(32),
            new SceneGroup([new SceneFill(new SceneRect(0, 4, 64, 24), BlendSource)])
            {
                Mask = new SceneMask(SceneMaskKind.Luminosity,
                [
                    new SceneFill(new SceneRect(0, 0, 32, 32), SceneColor.Gray(0.8f)),
                    new SceneFill(new SceneRect(32, 0, 32, 16), SceneColor.Gray(0.3f)),
                ]),
            },
        ]));

        // Seven inks: the extra process inks mixed with Multiply and with a non-separable mode, where they take Normal.
        foreach (BlendMode mode in (BlendMode[])[BlendMode.Multiply, BlendMode.Hue])
        {
            scenes.Add(new InkScene($"seven-{Name(mode)}", 64, 24, SevenInks,
            [
                new SceneFill(new SceneRect(0, 0, 32, 24), SceneColor.Ink(("Orange", 0.8f), ("Cyan", 0.2f))),
                new SceneFill(new SceneRect(32, 0, 32, 24), SceneColor.Ink(("Green", 0.6f), ("Violet", 0.3f), ("Black", 0.2f))),
                new SceneFill(new SceneRect(16, 8, 32, 16), SceneColor.Ink(("Magenta", 0.5f), ("Violet", 0.7f), ("Yellow", 0.25f))) { BlendMode = mode },
            ]));
        }

        return scenes;
    }

    private static IEnumerable<SceneStep> BackdropColumns(int height)
    {
        for (int i = 0; i < Backdrops.Count; i++)
        {
            yield return new SceneFill(new SceneRect(i * 8, 0, 8, height), Backdrops[i]);
        }
    }

    private static List<SceneStep> GroupContent()
    {
        return
        [
            new SceneFill(new SceneRect(4, 6, 36, 18), SceneColor.Ink(("Magenta", 0.6f), ("Yellow", 0.9f))) { BlendMode = BlendMode.Multiply, Opacity = 0.8f },
            new SceneFill(new SceneRect(24, 10, 36, 18), SceneColor.Ink(("Cyan", 0.7f), ("PANTONE 186 C", 0.5f))) { BlendMode = BlendMode.Multiply, Opacity = 0.6f },
        ];
    }

    /// <summary>A mode's or setting's name in lower-case words joined by hyphens, as scene names use it.</summary>
    public static string Name<T>(T value)
        where T : struct, Enum
    {
        string text = value.ToString();
        return string.Concat(text.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }
}

/// <summary>
/// One ink of a scene: a process ink or a spot, with its CMYK appearance at full tint (none for Cyan, Magenta, Yellow and
/// Black, which are themselves).
/// </summary>
public sealed record SceneInk(string Name, bool Spot, float[]? Appearance)
{
    /// <summary>A process ink.</summary>
    public static SceneInk Process(string name) => new(name, Spot: false, null);
}

/// <summary>A scene: its size in pixels, the inks of the raster it draws into, and the steps in order.</summary>
public sealed record InkScene(string Name, int Width, int Height, IReadOnlyList<SceneInk> Inks, IReadOnlyList<SceneStep> Steps)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>A pixel rectangle.</summary>
public readonly record struct SceneRect(int X, int Y, int Width, int Height);

/// <summary>
/// A colour: named inks with tints (as PDF's DeviceN, or Separation for one; the names Cyan, Magenta, Yellow and Black are the
/// process inks), DeviceCMYK, RGB or gray. RGB and gray mean sRGB and sGray, converted through the ink set's process profile.
/// </summary>
public sealed record SceneColor(IReadOnlyList<(string Name, float Tint)>? Inks, float[]? Process, float[]? RgbValue, float? GrayLevel)
{
    /// <summary>Named inks with tints; none is paper.</summary>
    public static SceneColor Ink(params (string Name, float Tint)[] inks) => new(inks, null, null, null);

    /// <summary>A DeviceCMYK colour.</summary>
    public static SceneColor Cmyk(float c, float m, float y, float k) => new(null, [c, m, y, k], null, null);

    /// <summary>An RGB colour.</summary>
    public static SceneColor Rgb(float r, float g, float b) => new(null, null, [r, g, b], null);

    /// <summary>A gray level, 0 black to 1 white.</summary>
    public static SceneColor Gray(float level) => new(null, null, null, level);
}

/// <summary>A step of a scene.</summary>
public abstract record SceneStep;

/// <summary>A filled rectangle.</summary>
public sealed record SceneFill(SceneRect Area, SceneColor Color) : SceneStep
{
    /// <summary>Gets the blend mode, SourceOver by default.</summary>
    public BlendMode BlendMode { get; init; }

    /// <summary>Gets the opacity, 1 by default.</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>Gets the overprint mode, Off by default.</summary>
    public SceneOverprint Overprint { get; init; }
}

/// <summary>A transparency group (an Egami layer) holding steps.</summary>
public sealed record SceneGroup(IReadOnlyList<SceneStep> Steps) : SceneStep
{
    /// <summary>Gets the blend mode the group merges with.</summary>
    public BlendMode BlendMode { get; init; }

    /// <summary>Gets the opacity the group merges with.</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>Gets a value indicating whether the group is isolated.</summary>
    public bool Isolated { get; init; } = true;

    /// <summary>Gets a value indicating whether the group is a knockout group.</summary>
    public bool Knockout { get; init; }

    /// <summary>Gets the space the group blends in.</summary>
    public SceneSpace Space { get; init; }

    /// <summary>Gets the group's soft mask, if any.</summary>
    public SceneMask? Mask { get; init; }
}

/// <summary>A soft mask: what it is computed from, and the steps drawn to make it (in gray for a luminosity mask).</summary>
public sealed record SceneMask(SceneMaskKind Kind, IReadOnlyList<SceneStep> Steps);

/// <summary>What a soft mask is computed from.</summary>
public enum SceneMaskKind
{
    /// <summary>The mask group's alpha.</summary>
    Alpha,

    /// <summary>The mask group's luminosity, drawn over black.</summary>
    Luminosity,
}

/// <summary>The space a group blends in.</summary>
public enum SceneSpace
{
    /// <summary>The ink space of the raster.</summary>
    Inks,

    /// <summary>sRGB for the process part; spots stay planes.</summary>
    Rgb,
}

/// <summary>A fill's overprint mode, as the design's Paint.Overprint.</summary>
public enum SceneOverprint
{
    /// <summary>Every plane is painted: inks the colour does not name get 0.</summary>
    Off,

    /// <summary>Only the planes the colour names are painted.</summary>
    On,

    /// <summary>As On, but a CMYK colour's zero components are not painted (PDF overprint mode 1).</summary>
    NonZero,
}
