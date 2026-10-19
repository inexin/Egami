using Egami.Drawing;

namespace Egami.Barcodes.Rendering;

/// <summary>
/// Where a symbol's modules go: the top-left corner of the symbol (inside its quiet zone), the module size, the bar height of a
/// linear symbol, and how much each dark rectangle gives up on each side for bar width reduction.
/// </summary>
internal readonly record struct BarcodeGeometry(
    float X,
    float Y,
    float ModuleWidth,
    float ModuleHeight,
    float BarHeight,
    float ShrinkLeft = 0,
    float ShrinkRight = 0,
    float ShrinkTop = 0,
    float ShrinkBottom = 0)
{
    /// <summary>
    /// Builds one path for the whole symbol: each run of dark modules in a row is one rectangle (each bar for a linear symbol),
    /// all wound the same way, so neighbouring rectangles share edges and antialiasing leaves no seams between them.
    /// </summary>
    public Path ToPath(BarcodeModules modules)
    {
        var builder = new PathBuilder();
        if (modules.IsLinear)
        {
            int x = 0;
            ReadOnlySpan<int> runs = modules.Runs;
            for (int i = 0; i < runs.Length; i++)
            {
                if (i % 2 == 0)
                {
                    AddRect(builder, X + (x * ModuleWidth) + ShrinkLeft, Y, (runs[i] * ModuleWidth) - ShrinkLeft - ShrinkRight, BarHeight);
                }

                x += runs[i];
            }

            return builder.ToPath();
        }

        float height = ModuleHeight - ShrinkTop - ShrinkBottom;
        for (int y = 0; y < modules.Height; y++)
        {
            ReadOnlySpan<bool> row = modules.Row(y);
            float top = Y + (y * ModuleHeight) + ShrinkTop;
            for (int x = 0; x < row.Length;)
            {
                if (!row[x])
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < row.Length && row[x])
                {
                    x++;
                }

                AddRect(builder, X + (start * ModuleWidth) + ShrinkLeft, top, ((x - start) * ModuleWidth) - ShrinkLeft - ShrinkRight, height);
            }
        }

        return builder.ToPath();
    }

    private static void AddRect(PathBuilder builder, float x, float y, float width, float height)
    {
        if (width > 0 && height > 0)
        {
            builder.AddRect(new RectF(x, y, width, height));
        }
    }
}
