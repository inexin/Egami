using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// The curve linear-light operations (resize, blur, grayscale with <see cref="Operations.ColorSpace.Linear"/>) decode and encode
/// with: the raster's own transfer function. sRGB, and anything within 1e-4 of it, runs through the exact tables 1.0 used, so
/// untagged and sRGB rasters give 1.0's bytes; other curves get tables built the same way.
/// </summary>
internal sealed class LinearCurve
{
    private const int EncodeSize = 4096;
    private const int Kept = 64;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TransferFunction, LinearCurve> Curves = new();

    private readonly TransferFunction? _curve;
    private readonly float[]? _encode;   // encoded × 255 at linear i / 4095, and a guard entry; null to compute
    private byte[]? _encode16;
    private float[]? _linear255;

    private LinearCurve(TransferFunction? curve)
    {
        _curve = curve;
        if (curve is null)
        {
            LinearFrom8 = Pixels.Srgb.LinearFrom8;
            return;
        }

        LinearFrom8 = new float[256];
        for (int i = 0; i < 256; i++)
        {
            LinearFrom8[i] = (float)curve.ToLinear(i / 255.0);
        }

        if (!curve.IsHdr && Math.Abs(curve.ToLinear(1) - 1) < 1e-6)
        {
            _encode = new float[EncodeSize + 1];
            for (int i = 0; i < EncodeSize; i++)
            {
                _encode[i] = (float)(Math.Clamp(curve.FromLinear(i / (double)(EncodeSize - 1)), 0, 1) * 255.0);
            }

            _encode[EncodeSize] = _encode[EncodeSize - 1];
        }
    }

    /// <summary>Gets the sRGB curve, 1.0's.</summary>
    public static LinearCurve Srgb { get; } = new(null);

    /// <summary>Gets the linear value of each 8-bit encoded value.</summary>
    public float[] LinearFrom8 { get; }

    /// <summary>Returns the curve for a transfer function: sRGB's for none, or one close to sRGB's.</summary>
    public static LinearCurve For(TransferFunction? curve)
    {
        if (curve is null || curve.Shape == TransferFunction.Kind.Srgb || curve.IsCloseTo(TransferFunction.Srgb))
        {
            return Srgb;
        }

        // By value, as a raster's gamma makes a new TransferFunction each time its profile is read; a handful of curves are
        // ever in use, so the cache is simply emptied if it fills.
        if (Curves.TryGetValue(curve, out LinearCurve? known))
        {
            return known;
        }

        if (Curves.Count >= Kept)
        {
            Curves.Clear();
        }

        return Curves.GetOrAdd(curve, c => new LinearCurve(c));
    }

    public double ToLinear(double encoded) => _curve is null ? Pixels.Srgb.ToLinear(encoded) : _curve.ToLinear(encoded);

    public double FromLinear(double linear) => _curve is null ? Pixels.Srgb.FromLinear(linear) : _curve.FromLinear(linear);

    /// <summary>Gets <see cref="LinearFrom8"/> times 255, built on first use, for the byte paths of linear-light filters.</summary>
    internal float[] LinearFrom8Times255 => _linear255 ??= [.. LinearFrom8.Select(v => v * 255)];

    /// <summary>
    /// Gets the 8-bit encoding of each 16-bit linear value v / 65280 (a byte's range × 256), as <see cref="FromLinearTo8"/> gives
    /// it: 65,281 entries, built on first use, for the byte paths of linear-light filters.
    /// </summary>
    internal byte[] EncodeFrom16 => _encode16 ??= BuildEncodeFrom16();

    /// <summary>Encodes a linear value to the nearest 8-bit value, through an interpolated table where the curve allows.</summary>
    public byte FromLinearTo8(float linear)
    {
        if (_curve is null)
        {
            return Pixels.Srgb.FromLinearTo8(linear);
        }

        if (_encode is null)
        {
            double v = _curve.FromLinear(linear);
            return v > 0 ? v < 1 ? (byte)((v * 255) + 0.5) : (byte)255 : (byte)0;
        }

        if (!(linear > 0f))
        {
            return (byte)(_encode[0] + 0.5f);
        }

        if (linear >= 1f)
        {
            return (byte)(_encode[EncodeSize - 1] + 0.5f);
        }

        float x = linear * (EncodeSize - 1);
        int i = (int)x;
        float a = _encode[i];
        return (byte)(a + ((x - i) * (_encode[i + 1] - a)) + 0.5f);
    }

    private byte[] BuildEncodeFrom16()
    {
        byte[] table = new byte[(255 << 8) + 1];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = FromLinearTo8(i / (float)(255 << 8));
        }

        return table;
    }
}
