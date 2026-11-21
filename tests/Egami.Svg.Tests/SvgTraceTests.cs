using System.Numerics;

using Egami.Drawing;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>Bitmap traces written as SVG through SvgCanvas, and read back by Egami.Svg.</summary>
public class SvgTraceTests
{
    [Fact]
    public void TraceRoundTripsThroughSvg()
    {
        using Raster<Rgba8> source = Raster.Create(80, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = source.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(25, 30), 18, Rgba8.Black);
            canvas.FillRect(new RectF(50, 10, 20, 40), Rgba8.Black);
            canvas.FillRect(new RectF(56, 20, 8, 20), Rgba8.White);
        }

        TraceResult result = source.Trace();
        string svg = result.ToSvg();
        Assert.Contains("<path", svg, StringComparison.Ordinal);
        Assert.Contains("width=\"80\"", svg, StringComparison.Ordinal);

        SvgDocument document = SvgDocument.Parse(svg, new SvgLoadOptions { Strict = true });
        Assert.Equal(result.Shapes.Count, document.Root.Descendants().Count(e => e.Name == "path"));
        using Raster<Rgba8> back = document.Rasterize<Rgba8>();
        Assert.Equal((80, 60), (back.Width, back.Height));
        int differ = 0;
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                differ += (source.GetPixel(x, y).R < 128) != (back.GetPixel(x, y).R < 128) ? 1 : 0;
            }
        }

        Assert.True(differ < 80, $"{differ} pixels differ");
    }

    [Fact]
    public void SaveSvgWritesAFileAndAStream()
    {
        using Raster<Rgba8> source = Raster.Create(20, 20, Rgba8.White);
        source.SetPixel(5, 5, Rgba8.Black);
        source.SetPixel(6, 5, Rgba8.Black);
        TraceResult result = source.Trace(TraceOptions.BlackAndWhite);

        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-trace-{Guid.NewGuid():N}.svg");
        try
        {
            result.SaveSvg(file, new SvgWriteOptions { Precision = 2 });
            Assert.Equal(result.ToSvg(new SvgWriteOptions { Precision = 2 }), File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }

        using MemoryStream stream = new();
        stream.WriteByte(32);
        result.SaveSvg(stream);
        Assert.True(stream.Length > 1);
        Assert.True(stream.CanWrite);

        Assert.Throws<ArgumentNullException>(() => ((TraceResult)null!).ToSvg());
        Assert.Throws<ArgumentNullException>(() => result.SaveSvg((string)null!));
    }
}
