namespace Egami.Colors;

/// <summary>How HDR light above reference white is brought into the 0..1 range of an SDR encoding.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="tone-map"/>
/// </example>
public enum ToneMapping
{
    /// <summary>
    /// The EETF of ITU-R BT.2390: a Hermite knee in PQ space, applied to max(R, G, B) so hue holds, that leaves darker tones
    /// alone and rolls highlights off smoothly to reference white. For photos and video frames.
    /// </summary>
    Bt2390,

    /// <summary>Extended Reinhard on max(R, G, B), with its white point at the source peak: simple and predictable.</summary>
    Reinhard,

    /// <summary>Clamps to 1. For tests, and for callers doing their own mapping.</summary>
    Clip,
}

/// <summary>The tone curves, on linear light relative to reference white (1.0), in double precision.</summary>
internal static class ToneCurves
{
    private const double ReferenceWhite = 203;

    /// <summary>
    /// Maps one linear value (relative to reference white) to 0..1 for a source whose brightest light is
    /// <paramref name="sourcePeak"/>, also relative to reference white. A peak at or below 1 needs no mapping.
    /// </summary>
    public static double Map(ToneMapping method, double value, double sourcePeak)
    {
        if (!(value > 0))
        {
            return 0;
        }

        if (method == ToneMapping.Clip || sourcePeak <= 1)
        {
            return Math.Min(value, 1);
        }

        if (method == ToneMapping.Reinhard)
        {
            double white = sourcePeak * sourcePeak;
            return Math.Min(value * (1 + (value / white)) / (1 + value), 1);
        }

        return Bt2390(value * ReferenceWhite, sourcePeak * ReferenceWhite, ReferenceWhite) / ReferenceWhite;
    }

    /// <summary>
    /// The BT.2390 EETF from a source of black 0 and peak <paramref name="sourcePeakNits"/> to a target of black 0 and peak
    /// <paramref name="targetPeakNits"/>, in cd/m²: normalize in PQ, apply the Hermite knee above KS = 1.5 × max − 0.5, and back.
    /// </summary>
    public static double Bt2390(double nits, double sourcePeakNits, double targetPeakNits)
    {
        double sourcePq = Pq(sourcePeakNits);
        double e1 = Math.Min(Pq(Math.Min(nits, sourcePeakNits)) / sourcePq, 1);
        double maxLum = Pq(targetPeakNits) / sourcePq;
        double ks = (1.5 * maxLum) - 0.5;
        double e2 = e1;
        if (e1 >= ks)
        {
            double t = (e1 - ks) / (1 - ks);
            double t2 = t * t, t3 = t2 * t;
            e2 = (((2 * t3) - (3 * t2) + 1) * ks) + ((t3 - (2 * t2) + t) * (1 - ks)) + (((-2 * t3) + (3 * t2)) * maxLum);
        }

        return Math.Min(PqInverse(e2 * sourcePq), targetPeakNits);
    }

    private static double Pq(double nits) => TransferFunction.Pq.FromLinear(nits / ReferenceWhite);

    private static double PqInverse(double signal) => TransferFunction.Pq.ToLinear(signal) * ReferenceWhite;
}
