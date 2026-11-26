using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Metafiles.Tests;

/// <summary>
/// EMF writing, stage C: every WMF, EMF and EMF+ file of the test corpus drawn onto an <see cref="EmfCanvas"/> plays back as the
/// original does.
/// </summary>
public class EmfResaveTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files => [.. TestFiles.Synthetic()];

    [Theory]
    [MemberData(nameof(Files))]
    public void CorpusFilesRoundTrip(string name)
    {
        foreach (EmfText text in (EmfText[])[EmfText.Outlines, EmfText.Glyphs])
        {
            Metafile original = Metafile.Load(TestFiles.SyntheticPath(name));
            using Raster<Rgba8> expected = original.Rasterize<Rgba8>();
            int w = expected.Width, h = expected.Height;
            using MemoryStream ms = new();
            using (EmfCanvas canvas = new(ms, w, h, new EmfWriteOptions { Text = text }))
            {
                original.Draw(canvas, new RectF(0, 0, w, h));
            }

            using Raster<Rgba8> actual = Metafile.Load(ms.ToArray()).Rasterize<Rgba8>(new MetafileRenderOptions { Width = w, Height = h });
            (double mean, int max, int over) = EmfCanvasTests.Difference(expected, actual);
            output.WriteLine($"{name} {text} {w}x{h}: mean {mean:F3}, max {max}, over {over}");
            Assert.True(mean < 1, $"{name} {text}: mean {mean:F3}, max {max}, over {over}");
        }
    }
}
