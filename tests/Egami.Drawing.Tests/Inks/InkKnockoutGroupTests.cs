using Egami.Colors;
using Egami.Operations;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// A group inside a knockout group is one of its objects on plates too: it replaces what is under it by its shape, and a
/// non-isolated one starts from the knockout group's first content (ISO 32000-2 11.4.6.2).
/// </summary>
public class InkKnockoutGroupTests
{
    private static readonly InkSet Set = InkSet.FromProfile(ColorProfile.Fogra39);

    [Fact]
    public void AGroupReplacesWhatIsUnderItByItsShape()
    {
        using InkRaster plates = new(Set, 40, 4);
        using (InkCanvas canvas = new(plates))
        {
            canvas.Clear(InkColor.None);
            canvas.SaveLayer(new LayerOptions { Knockout = true, Isolated = true });
            canvas.FillRect(new RectF(0, 0, 25, 4), new SolidPaint(InkColor.Cmyk(1, 0, 0, 0)));
            canvas.SaveLayer(new LayerOptions { Isolated = true, Opacity = 0.5f });
            canvas.FillRect(new RectF(15, 0, 25, 4), new SolidPaint(InkColor.Cmyk(0, 1, 0, 0)));
            canvas.Restore();
            canvas.Restore();
        }

        Assert.Equal(255, plates.Plate("Cyan")!.Row8(2)[5]);
        Assert.Equal(0, plates.Plate("Cyan")!.Row8(2)[20]);

        // Half the magenta over paper, as the same group gives where there was no cyan.
        Assert.InRange(plates.Plate("Magenta")!.Row8(2)[20], 127, 128);
        Assert.Equal(plates.Plate("Magenta")!.Row8(2)[35], plates.Plate("Magenta")!.Row8(2)[20]);
    }

    [Fact]
    public void ANonIsolatedGroupStartsFromTheKnockoutGroupsFirstContent()
    {
        using InkRaster plates = new(Set, 40, 4);
        using (InkCanvas canvas = new(plates))
        {
            canvas.Clear(InkColor.None);
            canvas.SaveLayer(new LayerOptions { Knockout = true, Isolated = true });
            canvas.FillRect(new RectF(0, 0, 40, 4), new SolidPaint(InkColor.Cmyk(1, 0, 0, 0)));
            canvas.SaveLayer(new LayerOptions { Isolated = false });
            canvas.FillRect(new RectF(10, 0, 20, 4), new SolidPaint(InkColor.Cmyk(0, 1, 0, 0)) { BlendMode = BlendMode.Multiply });
            canvas.Restore();
            canvas.Restore();
        }

        // Inside the inner group only its magenta, the cyan below knocked out; outside it the cyan stays.
        Assert.Equal(0, plates.Plate("Cyan")!.Row8(2)[20]);
        Assert.Equal(255, plates.Plate("Magenta")!.Row8(2)[20]);
        Assert.Equal(255, plates.Plate("Cyan")!.Row8(2)[5]);
    }
}
