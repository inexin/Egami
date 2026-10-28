namespace Egami.Fonts.Hinting;

/// <summary>
/// A zone of points the interpreter moves: the glyph zone (an outline plus its four phantom points) or the twilight zone. Per
/// point: the unscaled position in font units, the original scaled position and the current one in 26.6 pixels, the on-curve flag
/// and the touched flags of each axis.
/// </summary>
internal sealed class Zone
{
    /// <summary>Touched along x.</summary>
    public const byte TouchX = 1;

    /// <summary>Touched along y.</summary>
    public const byte TouchY = 2;

    public Zone(int count, bool twilight)
    {
        Count = count;
        IsTwilight = twilight;
        OrgX = new int[count];
        OrgY = new int[count];
        CurX = new int[count];
        CurY = new int[count];
        UnscaledX = new int[count];
        UnscaledY = new int[count];
        OnCurve = new bool[count];
        Touched = new byte[count];
    }

    /// <summary>Gets the number of points, phantom points included.</summary>
    public int Count { get; }

    /// <summary>Gets a value indicating whether this is the twilight zone, whose points have no unscaled positions.</summary>
    public bool IsTwilight { get; }

    public int[] OrgX { get; }

    public int[] OrgY { get; }

    public int[] CurX { get; }

    public int[] CurY { get; }

    public int[] UnscaledX { get; }

    public int[] UnscaledY { get; }

    public bool[] OnCurve { get; }

    public byte[] Touched { get; }

    /// <summary>Gets or sets the index of the last point of each contour; the phantom points belong to none.</summary>
    public int[] ContourEnds { get; set; } = [];

    /// <summary>Gets or sets how many points belong to the outline: the rest are the phantom points.</summary>
    public int OutlineCount { get; set; }

    /// <summary>Clears every position and flag.</summary>
    public void Reset()
    {
        Array.Clear(OrgX);
        Array.Clear(OrgY);
        Array.Clear(CurX);
        Array.Clear(CurY);
        Array.Clear(UnscaledX);
        Array.Clear(UnscaledY);
        Array.Clear(OnCurve);
        Array.Clear(Touched);
    }
}
