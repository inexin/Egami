using Egami.GdiPlus;

namespace Egami.SystemDrawing;

/// <summary>The pen settings beyond width and brush.</summary>
public sealed partial class Pen
{
    private partial GdiPlusPen GeometryCore(GdiPlusPen pen) => pen;

    private partial void CopyTo(Pen copy)
    {
    }
}
