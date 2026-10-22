namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// How a reference frame of another size maps onto the frame being decoded (vp9_scale.c): 14-bit fixed-point ratios and the
/// step, in 1/16 pixels, between neighboring predicted pixels.
/// </summary>
internal sealed class Vp9ScaleFactors
{
    private const int ScaleShift = 14;
    private const int NoScale = 1 << ScaleShift;
    private const int Invalid = -1;

    public int XScaleFp { get; private set; } = Invalid;

    public int YScaleFp { get; private set; } = Invalid;

    public int XStepQ4 { get; private set; }

    public int YStepQ4 { get; private set; }

    public bool IsValid => XScaleFp != Invalid && YScaleFp != Invalid;

    public bool IsScaled => IsValid && (XScaleFp != NoScale || YScaleFp != NoScale);

    /// <summary>Whether a reference of <paramref name="refWidth"/> by <paramref name="refHeight"/> can predict a frame of the other size: at most twice as large, at most 16 times smaller.</summary>
    public static bool ValidSize(int refWidth, int refHeight, int width, int height) =>
        2 * width >= refWidth && 2 * height >= refHeight && width <= 16 * refWidth && height <= 16 * refHeight;

    public void Setup(int refWidth, int refHeight, int width, int height)
    {
        if (!ValidSize(refWidth, refHeight, width, height))
        {
            XScaleFp = Invalid;
            YScaleFp = Invalid;
            return;
        }

        XScaleFp = (refWidth << ScaleShift) / width;
        YScaleFp = (refHeight << ScaleShift) / height;
        XStepQ4 = ScaledX(16);
        YStepQ4 = ScaledY(16);
    }

    public int ScaledX(int value) => (int)(((long)value * XScaleFp) >> ScaleShift);

    public int ScaledY(int value) => (int)(((long)value * YScaleFp) >> ScaleShift);

    /// <summary>The value scaled when the reference is scaled (sf->scale_value_x), else the value.</summary>
    public int ValueX(int value) => IsScaled ? ScaledX(value) : value;

    public int ValueY(int value) => IsScaled ? ScaledY(value) : value;
}
