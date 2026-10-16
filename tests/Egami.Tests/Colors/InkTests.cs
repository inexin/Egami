using System.Numerics;

using Egami.Colors;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.6.5 task 2: colorants, ink sets and ink colours.</summary>
public class InkTests
{
    private static readonly InkAppearance Red186 = InkAppearance.Cmyk(0, 1, 0.81f, 0.04f);

    [Fact]
    public void ACmykProfileGivesTheFourProcessInks()
    {
        InkSet set = InkSet.FromProfile(ColorProfile.Fogra39);
        Assert.Same(ColorProfile.Fogra39, set.Profile);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black"], set.Select(c => c.Name));
        Assert.Equal(4, set.ProcessCount);
        Assert.All(set, c => Assert.True(c.IsCmyk));
        Assert.Throws<ArgumentException>(() => InkSet.FromProfile(ColorProfile.Srgb));
        Assert.Throws<ArgumentException>(() => InkSet.FromProfile(ColorProfile.GraySrgb));
    }

    [Fact]
    public void SpotsAreAddedAfterTheProcessInksAndTheSetStaysUnchanged()
    {
        InkSet process = InkSet.FromProfile(ColorProfile.Fogra39);
        InkSet set = process.WithSpot("PANTONE 186 C", Red186).WithSpot("Varnish", null, ColorantKind.Technical);
        Assert.Equal(4, process.Count);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "PANTONE 186 C", "Varnish"], set.Select(c => c.Name));
        Assert.Equal(4, set.ProcessCount);
        Assert.Equal(ColorantKind.Spot, set[4].Kind);
        Assert.Equal(Red186, set[4].Appearance);
        Assert.Equal(ColorantKind.Technical, set[5].Kind);
        Assert.Null(set[5].Appearance);
        Assert.Null(Colorant.Technical("White").Appearance);
    }

    [Fact]
    public void BadSpotsAreRefused()
    {
        InkSet set = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null);
        Assert.Throws<ArgumentException>(() => set.WithSpot("Gold", Red186));
        Assert.Throws<ArgumentException>(() => set.WithSpot("Cyan", null));
        Assert.Throws<ArgumentException>(() => set.WithSpot("Orange", null, ColorantKind.Process));
        Assert.Throws<ArgumentException>(() => set.WithSpot(Colorant.Process("Orange", null)));
        Assert.Throws<ArgumentException>(() => set.WithSpot("", null));
        Assert.Throws<ArgumentException>(() => set.WithSpot("None", null));
        Assert.Throws<ArgumentException>(() => set.WithSpot("All", null));
        Assert.Throws<ArgumentException>(() => set.WithSpot(new string('x', Colorant.MaxNameLength + 1), null));
        Assert.Throws<ArgumentNullException>(() => set.WithSpot(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => InkAppearance.Cmyk(0, 1.5f, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InkAppearance.Lab(new LabColor(101, 0, 0)));
    }

    [Fact]
    public void ASetHoldsAtMostSixtyFourInks()
    {
        InkSet set = InkSet.FromProfile(ColorProfile.Fogra39);
        for (int i = set.Count; i < InkSet.MaxInks; i++)
        {
            set = set.WithSpot($"Spot {i}", null);
        }

        Assert.Equal(InkSet.MaxInks, set.Count);
        Assert.Throws<ArgumentException>(() => set.WithSpot("One too many", null));
        Assert.Throws<ArgumentException>(() => InkColor.Mix([.. set.Append(Colorant.Spot("Extra", null)).Select(c => new InkTint(c, 1))]));
    }

    [Fact]
    public void NamesMatchExactly()
    {
        InkSet set = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", Red186);
        Assert.Equal(4, set.IndexOf("PANTONE 186 C"));
        Assert.Equal(-1, set.IndexOf("Pantone 186 C"));
        Assert.Equal(-1, set.IndexOf("PANTONE 186 C "));
        Assert.Equal(1, set.IndexOf("Magenta"));
        Assert.Equal(-1, set.IndexOf("magenta"));

        // A colorant is its name: the same spot with another appearance matches, and Process("Cyan") is the process cyan.
        Assert.Equal(Colorant.Spot("PANTONE 186 C", null), set[4]);
        Assert.Same(Colorant.Cyan, Colorant.Process("Cyan", null));
        Assert.True(Colorant.Process("Cyan", InkAppearance.Rgb(0, 1, 1)).IsCmyk);
        Assert.False(Colorant.Process("Orange", null).IsCmyk);

        InkColor mix = InkColor.Mix(new(Colorant.Magenta, 0.4f), new(set[4], 0.7f));
        Assert.Equal(0.7f, mix.TintOf("PANTONE 186 C"));
        Assert.Equal(0.4f, mix.TintOf("Magenta"));
        Assert.Null(mix.TintOf("Cyan"));
        Assert.Null(mix.TintOf("pantone 186 c"));
        Assert.Throws<ArgumentException>(() => InkColor.Mix(new(Colorant.Magenta, 0.4f), new(Colorant.Magenta, 0.2f)));
    }

    [Fact]
    public void ACmykColourNamesEveryProcessInkEvenAtZero()
    {
        InkColor cmyk = InkColor.Cmyk(0, 0.8f, 0, 0.2f);
        Assert.True(cmyk.IsCmyk);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black"], cmyk.Inks.Select(i => i.Colorant.Name));
        Assert.Equal(0, cmyk.TintOf("Cyan"));

        // The same inks mixed are a DeviceN colour: equal tints, but not a CMYK colour, so overprint's NonZero paints its zeros.
        InkColor mixed = InkColor.Mix([.. cmyk.Inks]);
        Assert.False(mixed.IsCmyk);
        Assert.NotEqual(cmyk, mixed);
        Assert.Equal(cmyk, InkColor.Cmyk(0, 0.8f, 0, 0.2f));
        Assert.Throws<ArgumentOutOfRangeException>(() => InkColor.Cmyk(0, -0.1f, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InkColor.Spot("Gold", float.NaN, null));
    }

    [Fact]
    public void AppearancesScaleByTintAndCombineInComplementSpace()
    {
        // Process CMYK is its own appearance.
        Assert.Equal(InkAppearance.Cmyk(0.1f, 0.2f, 0.3f, 0.4f), InkColor.Cmyk(0.1f, 0.2f, 0.3f, 0.4f).Appearance);

        // A spot at half tint: half its appearance.
        Assert.True(Near(new Vector4(0, 0.5f, 0.405f, 0.02f), InkColor.Spot("PANTONE 186 C", 0.5f, Red186).Appearance!.Value.Values));

        // A spot over cyan: each component is 1 − Π(1 − tint × value).
        InkColor mix = InkColor.Mix(new(Colorant.Cyan, 0.6f), new(Colorant.Spot("PANTONE 186 C", Red186), 0.5f), new(Colorant.Magenta, 0.4f));
        Vector4 expected = new(0.6f, 1 - ((1 - 0.5f) * (1 - 0.4f)), 0.405f, 0.02f);
        Assert.True(Near(expected, mix.Appearance!.Value.Values), mix.Appearance.ToString());

        // sRGB appearances multiply as filters.
        InkColor rgb = InkColor.Mix(new(Colorant.Spot("A", InkAppearance.Rgb(1, 0.5f, 0)), 1), new(Colorant.Spot("B", InkAppearance.Rgb(0.5f, 1, 1)), 0.5f));
        Assert.Equal(InkAppearanceKind.Rgb, rgb.Appearance!.Value.Kind);
        Assert.True(Near(new Vector4(0.75f, 0.5f, 0, 0), rgb.Appearance.Value.Values), rgb.Appearance.ToString());

        // Paper is white; a technical ink alone shows nothing; with a spot, only the spot shows.
        Assert.Equal(InkAppearance.Cmyk(0, 0, 0, 0), InkColor.None.Appearance);
        Assert.Null(InkColor.Spot(Colorant.Technical("Varnish"), 1).Appearance);
        Assert.True(Near(new Vector4(0, 1, 0.81f, 0.04f), InkColor.Mix(new(Colorant.Technical("Varnish"), 1), new(Colorant.Spot("PANTONE 186 C", Red186), 1)).Appearance!.Value.Values));
    }

    [Fact]
    public void MixedModelsCombineInLab()
    {
        // A Lab spot alone at full tint keeps its colour, at no tint it is paper white.
        Colorant gold = Colorant.Spot("Gold", InkAppearance.Lab(new LabColor(70, 5, 60)));
        Colorant blue = Colorant.Spot("Blue", InkAppearance.Rgb(0, 0, 1));
        LabColor full = InkColor.Spot(gold, 1).Appearance!.Value.ToLab();
        Assert.True(full.IsCloseTo(new LabColor(70, 5, 60), 1e-3f), full.ToString());

        LabColor none = InkColor.Mix(new(gold, 0), new(blue, 0)).Appearance!.Value.ToLab();
        Assert.True(none.IsCloseTo(new LabColor(100, 0, 0), 1e-3f), none.ToString());

        // Gold over blue is darker than either, and a CMYK appearance goes through FOGRA39 on the way.
        InkAppearance both = InkColor.Mix(new(gold, 1), new(blue, 1)).Appearance!.Value;
        Assert.Equal(InkAppearanceKind.Lab, both.Kind);
        Assert.True(both.Values.X < 30, both.ToString());
        LabColor cyan = InkColor.Mix(new(Colorant.Cyan, 1), new(gold, 0)).Appearance!.Value.ToLab();
        Assert.True(cyan.L is > 50 and < 60 && cyan.B < -40, cyan.ToString());   // FOGRA39's cyan: about L 55, b −50
    }

    [Fact]
    public void DisabledSpotsAreMarked()
    {
        InkSet set = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null).WithSpot("Silver", null);
        InkSet disabled = set.WithDisabled("Gold");
        Assert.True(disabled.IsDisabled(4));
        Assert.False(disabled.IsDisabled(5));
        Assert.False(set.IsDisabled(4));
        Assert.Throws<ArgumentException>(() => set.WithDisabled("Cyan"));
        Assert.Throws<ArgumentException>(() => set.WithDisabled("Copper"));
        Assert.Equal("Cyan, Magenta, Yellow, Black, Gold (disabled), Silver", disabled.ToString());
    }

    private static bool Near(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 1e-5f;
}
