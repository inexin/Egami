using Egami.Operations;

namespace Egami.Drawing.Tests.Inks;

public class InkSceneTests
{
    [Fact]
    public void EveryBlendModeOverprintModeAndGroupKindHasAScene()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            Assert.Contains(InkScenes.All, s => s.Name == $"blend-{InkScenes.Name(mode)}");
        }

        foreach (SceneOverprint overprint in Enum.GetValues<SceneOverprint>())
        {
            Assert.Contains(InkScenes.All, s => Steps(s.Steps).OfType<SceneFill>().Any(f => f.Overprint == overprint));
        }

        List<SceneGroup> groups = [.. InkScenes.All.SelectMany(s => Steps(s.Steps)).OfType<SceneGroup>()];
        foreach (bool isolated in (bool[])[true, false])
        {
            foreach (bool knockout in (bool[])[true, false])
            {
                Assert.Contains(groups, g => g.Isolated == isolated && g.Knockout == knockout);
            }
        }

        Assert.Contains(groups, g => g.Space == SceneSpace.Rgb);
        Assert.Contains(groups, g => g.Mask?.Kind == SceneMaskKind.Alpha);
        Assert.Contains(groups, g => g.Mask?.Kind == SceneMaskKind.Luminosity);
    }

    [Fact]
    public void ScenesAreWellFormed()
    {
        Assert.Equal(InkScenes.All.Count, InkScenes.All.Select(s => s.Name).Distinct().Count());
        foreach (InkScene scene in InkScenes.All)
        {
            HashSet<string> inks = [.. scene.Inks.Select(i => i.Name)];
            Assert.Equal(scene.Inks.Count, inks.Count);
            foreach (SceneStep step in Steps(scene.Steps))
            {
                if (step is not SceneFill fill)
                {
                    continue;
                }

                SceneRect r = fill.Area;
                Assert.True(r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0 && r.X + r.Width <= scene.Width && r.Y + r.Height <= scene.Height, $"{scene}: {r}");
                Assert.InRange(fill.Opacity, 0, 1);
                foreach ((string name, float tint) in fill.Color.Inks ?? [])
                {
                    Assert.InRange(tint, 0, 1);

                    // Only the missing-spot scene names an ink outside its set, and that ink has an appearance.
                    Assert.True(inks.Contains(name) || (name == InkScenes.Pantone300.Name && scene.Name == "missing-spot"), $"{scene}: {name}");
                }
            }
        }
    }

    private static IEnumerable<SceneStep> Steps(IEnumerable<SceneStep> steps)
    {
        foreach (SceneStep step in steps)
        {
            yield return step;
            if (step is SceneGroup group)
            {
                foreach (SceneStep inner in Steps(group.Steps).Concat(group.Mask is null ? [] : Steps(group.Mask.Steps)))
                {
                    yield return inner;
                }
            }
        }
    }
}
