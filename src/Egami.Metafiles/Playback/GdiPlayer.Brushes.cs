using Egami.Drawing;

namespace Egami.Metafiles.Playback;

/// <summary>Hatch and pattern brushes (task 7).</summary>
internal sealed partial class GdiPlayer
{
    private Paint? PatternPaint(GdiBrush brush) => brush.Image is null ? RopPaint(brush.Color) : new ImagePaint(brush.Image);

    private static GdiBrush DibBrush(ReadOnlySpan<byte> r, int usage, int offBmi, int cbBmi, int offBits, int cbBits)
    {
        _ = r;
        _ = usage;
        _ = offBmi;
        _ = cbBmi;
        _ = offBits;
        _ = cbBits;
        return new GdiBrush { Style = GdiBrush.Solid, Color = new Pixels.Rgba8(128, 128, 128, 255) };
    }

    private static GdiBrush WmfDibBrush(ReadOnlySpan<byte> p)
    {
        _ = p;
        return new GdiBrush { Style = GdiBrush.Solid, Color = new Pixels.Rgba8(128, 128, 128, 255) };
    }
}
