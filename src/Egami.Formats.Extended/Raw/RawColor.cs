using Egami.Colors;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// The colour maths of DNG's "Mapping Camera Color Space to CIE XYZ Space" chapter: the white point an as-shot neutral stands
/// for, matrices interpolated between two calibration illuminants by inverse colour temperature, the ForwardMatrix and
/// ColorMatrix routes to XYZ D50, and Bradford adaptation. 3 × 3 matrices are row-major arrays of nine.
/// </summary>
internal static class RawColor
{
    /// <summary>The D50 white point, in xy and in XYZ with Y = 1.</summary>
    public static readonly (double X, double Y) D50 = (0.3457, 0.3585);

    public static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    private static readonly double[] Bradford = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];

    /// <summary>Returns the temperature in kelvin of an EXIF LightSource value, as DNG's CalibrationIlluminant uses them.</summary>
    public static double IlluminantTemperature(int illuminant) => illuminant switch
    {
        1 or 4 or 9 => 5500,     // daylight, flash, fine weather
        2 or 14 => 4150,         // fluorescent, cool white fluorescent
        3 => 2850,               // tungsten
        10 => 6500,              // cloudy
        11 => 7500,              // shade
        12 => 6430,              // daylight fluorescent
        13 => 5000,              // day white fluorescent
        15 => 3450,              // white fluorescent
        16 => 2940,              // warm white fluorescent
        17 => 2856,              // standard light A
        18 => 4874,              // standard light B
        19 => 6774,              // standard light C
        20 => 5503,              // D55
        21 => 6504,              // D65
        22 => 7504,              // D75
        23 => 5003,              // D50
        24 => 3200,              // ISO studio tungsten
        _ => 0,
    };

    public static double[] Multiply(double[] a, double[] b)
    {
        double[] r = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                r[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            }
        }

        return r;
    }

    public static (double, double, double) Apply(double[] m, double a, double b, double c)
    {
        return ((m[0] * a) + (m[1] * b) + (m[2] * c), (m[3] * a) + (m[4] * b) + (m[5] * c), (m[6] * a) + (m[7] * b) + (m[8] * c));
    }

    public static double[] Diagonal(double a, double b, double c) => [a, 0, 0, 0, b, 0, 0, 0, c];

    public static double[] Scale(double[] m, double s) => [.. m.Select(v => v * s)];

    /// <summary>Inverts a 3 × 3 matrix; a singular one gives the identity.</summary>
    public static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], k = m[8];
        double det = (a * ((e * k) - (f * h))) - (b * ((d * k) - (f * g))) + (c * ((d * h) - (e * g)));
        if (Math.Abs(det) < 1e-12 || !double.IsFinite(det))
        {
            return [.. Identity];
        }

        double s = 1 / det;
        return
        [
            ((e * k) - (f * h)) * s, ((c * h) - (b * k)) * s, ((b * f) - (c * e)) * s,
            ((f * g) - (d * k)) * s, ((a * k) - (c * g)) * s, ((c * d) - (a * f)) * s,
            ((d * h) - (e * g)) * s, ((b * g) - (a * h)) * s, ((a * e) - (b * d)) * s,
        ];
    }

    public static (double X, double Y, double Z) XyToXyz((double X, double Y) xy)
    {
        double y = Math.Max(xy.Y, 1e-6);
        return (xy.X / y, 1, (1 - xy.X - xy.Y) / y);
    }

    public static (double X, double Y) XyzToXy(double x, double y, double z)
    {
        double sum = x + y + z;
        return sum <= 1e-12 || !double.IsFinite(sum) ? D50 : (x / sum, y / sum);
    }

    /// <summary>Returns the Bradford chromatic adaptation from white <paramref name="from"/> to white <paramref name="to"/>.</summary>
    public static double[] Adaptation((double X, double Y) from, (double X, double Y) to)
    {
        (double sx, double sy, double sz) = XyToXyz(from);
        (double tx, double ty, double tz) = XyToXyz(to);
        (double sr, double sg, double sb) = Apply(Bradford, sx, sy, sz);
        (double tr, double tg, double tb) = Apply(Bradford, tx, ty, tz);
        double[] scale = Diagonal(tr / sr, tg / sg, tb / sb);
        return Multiply(Invert(Bradford), Multiply(scale, Bradford));
    }

    /// <summary>Returns the matrix from XYZ to linear RGB of the given primaries (XYZ in the primaries' own white).</summary>
    public static double[] XyzToRgb(ColorPrimaries primaries) => Invert(RgbToXyz(primaries));

    /// <summary>Returns the matrix from linear RGB of the given primaries to XYZ, white mapping to Y = 1.</summary>
    public static double[] RgbToXyz(ColorPrimaries p)
    {
        double[] m =
        [
            p.Red.X / p.Red.Y, p.Green.X / p.Green.Y, p.Blue.X / p.Blue.Y,
            1, 1, 1,
            (1 - p.Red.X - p.Red.Y) / p.Red.Y, (1 - p.Green.X - p.Green.Y) / p.Green.Y, (1 - p.Blue.X - p.Blue.Y) / p.Blue.Y,
        ];
        (double wx, double wy, double wz) = XyToXyz((p.White.X, p.White.Y));
        (double sr, double sg, double sb) = Apply(Invert(m), wx, wy, wz);
        return Multiply(m, Diagonal(sr, sg, sb));
    }

    /// <summary>ROMM (ProPhoto) RGB, the space DNG's HueSatMap and LookTable work in.</summary>
    public static readonly ColorPrimaries ProPhoto = new(new Chromaticity(0.7347, 0.2653), new Chromaticity(0.1596, 0.8404), new Chromaticity(0.0366, 0.0001), new Chromaticity(0.3457, 0.3585));

    /// <summary>
    /// Returns the correlated colour temperature of a white point, by Robertson's method over the isotemperature lines of the
    /// CIE 1960 UCS (Wyszecki and Stiles), and its tint: the distance along the isotemperature line from the Planckian locus,
    /// times 3000, positive toward magenta.
    /// </summary>
    public static (double Temperature, double Tint) Temperature((double X, double Y) xy)
    {
        (double u, double v) = Uv(xy);
        double last = 0;
        for (int i = 0; i < Robertson.Length; i++)
        {
            (double _, double ui, double vi, double ti) = Robertson[i];
            double distance = ((v - vi) - (ti * (u - ui))) / Math.Sqrt(1 + (ti * ti));
            if (i > 0 && ((distance <= 0) != (last <= 0) || i == Robertson.Length - 1))
            {
                double f = last == distance ? 0 : last / (last - distance);
                return Along(i, Math.Clamp(f, 0, 1), u, v);
            }

            last = distance;
        }

        return (5000, 0);
    }

    private static (double Temperature, double Tint) Along(int i, double f, double u, double v)
    {
        (double m0, double u0, double v0, double t0) = Robertson[i - 1];
        (double m1, double u1, double v1, double t1) = Robertson[i];
        double mired = m0 + (f * (m1 - m0));
        (double du, double dv) = Direction(t0, t1, f);
        double tint = ((u - (u0 + (f * (u1 - u0)))) * du) + ((v - (v0 + (f * (v1 - v0)))) * dv);
        return (1e6 / Math.Max(mired, 1), tint * 3000);
    }

    /// <summary>Returns the unit direction of the isotemperature line between two table rows, pointing toward magenta (lower v).</summary>
    private static (double U, double V) Direction(double t0, double t1, double f)
    {
        double n0 = Math.Sqrt(1 + (t0 * t0)), n1 = Math.Sqrt(1 + (t1 * t1));
        double du = ((1 - f) / n0) + (f / n1);
        double dv = ((1 - f) * t0 / n0) + (f * t1 / n1);
        double length = Math.Sqrt((du * du) + (dv * dv));
        return (du / length, dv / length);
    }

    /// <summary>Returns the white point of a temperature and tint, the inverse of <see cref="Temperature"/>.</summary>
    public static (double X, double Y) WhiteOf(double temperature, double tint)
    {
        double mired = 1e6 / Math.Clamp(temperature, 1667, 100000);
        int i = 1;
        while (i < Robertson.Length - 1 && Robertson[i].Mired < mired)
        {
            i++;
        }

        (double m0, double u0, double v0, double t0) = Robertson[i - 1];
        (double m1, double u1, double v1, double t1) = Robertson[i];
        double f = Math.Clamp((mired - m0) / (m1 - m0), 0, 1);
        (double du, double dv) = Direction(t0, t1, f);
        double u = u0 + (f * (u1 - u0)) + (du * tint / 3000);
        double v = v0 + (f * (v1 - v0)) + (dv * tint / 3000);
        double d = (2 * u) - (8 * v) + 4;
        return (3 * u / d, 2 * v / d);
    }

    private static (double U, double V) Uv((double X, double Y) xy)
    {
        double d = (-2 * xy.X) + (12 * xy.Y) + 3;
        return (4 * xy.X / d, 6 * xy.Y / d);
    }

    /// <summary>Robertson's table (1968): mired, then u, v and slope t of the isotemperature line in the CIE 1960 UCS.</summary>
    private static readonly (double Mired, double U, double V, double T)[] Robertson =
    [
        (0, 0.18006, 0.26352, -0.24341),
        (10, 0.18066, 0.26589, -0.25479),
        (20, 0.18133, 0.26846, -0.26876),
        (30, 0.18208, 0.27119, -0.28539),
        (40, 0.18293, 0.27407, -0.30470),
        (50, 0.18388, 0.27709, -0.32675),
        (60, 0.18494, 0.28021, -0.35156),
        (70, 0.18611, 0.28342, -0.37915),
        (80, 0.18740, 0.28668, -0.40955),
        (90, 0.18880, 0.28997, -0.44278),
        (100, 0.19032, 0.29326, -0.47888),
        (125, 0.19462, 0.30141, -0.58204),
        (150, 0.19962, 0.30921, -0.70471),
        (175, 0.20525, 0.31647, -0.84901),
        (200, 0.21142, 0.32312, -1.0182),
        (225, 0.21807, 0.32909, -1.2168),
        (250, 0.22511, 0.33439, -1.4512),
        (275, 0.23247, 0.33904, -1.7298),
        (300, 0.24010, 0.34308, -2.0637),
        (325, 0.24792, 0.34655, -2.4681),
        (350, 0.25591, 0.34951, -2.9641),
        (375, 0.26400, 0.35200, -3.5814),
        (400, 0.27218, 0.35407, -4.3633),
        (425, 0.28039, 0.35577, -5.3762),
        (450, 0.28863, 0.35714, -6.7262),
        (475, 0.29685, 0.35823, -8.5955),
        (500, 0.30505, 0.35907, -11.324),
        (525, 0.31320, 0.35968, -15.628),
        (550, 0.32129, 0.36011, -23.325),
        (575, 0.32931, 0.36038, -40.770),
        (600, 0.33724, 0.36051, -116.45),
    ];
}

/// <summary>
/// The colour transform of one develop: camera RGB, white-balanced, to XYZ D50, worked out from a frame's matrices and the
/// white balance asked for.
/// </summary>
internal sealed class RawCamera
{
    private RawCamera(double[] multipliers, double[] cameraToXyz, (double X, double Y) white, double[] colorMatrix, double[]? forwardMatrix)
    {
        Multipliers = multipliers;
        CameraToXyz = cameraToXyz;
        White = white;
        ColorMatrix = colorMatrix;
        ForwardMatrix = forwardMatrix;
    }

    /// <summary>Gets the white-balance multiplier of each colour plane, the smallest 1.</summary>
    public double[] Multipliers { get; }

    /// <summary>Gets the matrix from white-balanced camera RGB to XYZ D50; camera (1, 1, 1) maps to D50 white with Y = 1.</summary>
    public double[] CameraToXyz { get; }

    /// <summary>Gets the scene white point.</summary>
    public (double X, double Y) White { get; }

    /// <summary>Gets the XYZ-to-camera matrix used, interpolated for the white point.</summary>
    public double[] ColorMatrix { get; }

    /// <summary>Gets the ForwardMatrix used, interpolated, or <see langword="null"/>.</summary>
    public double[]? ForwardMatrix { get; }

    /// <summary>Gets the weight of the first calibration illuminant in the interpolation, 0 to 1.</summary>
    public double Weight1 { get; private init; } = 1;

    /// <summary>
    /// Works out the transform. <paramref name="neutral"/> is the camera's response to the scene white (from AsShotNeutral or a
    /// measurement); <paramref name="white"/> is used instead when it is given (AsShotWhiteXY or a custom temperature).
    /// </summary>
    public static RawCamera Create(RawFrame frame, double[]? neutral, (double X, double Y)? white)
    {
        RawProfile p = frame.Profile;
        double[] ab = frame.AnalogBalance is { Length: 3 } a ? RawColor.Diagonal(a[0], a[1], a[2]) : RawColor.Identity;
        double[]? cm1 = Matrix(p.ColorMatrix1);
        double[]? cm2 = Matrix(p.ColorMatrix2);
        if (cm1 is null)
        {
            // No colour data: treat the camera as sRGB, so the image is at least viewable.
            cm1 = RawColor.Multiply(RawColor.XyzToRgb(Colors.ColorProfile.Srgb.Primaries!), RawColor.Adaptation(RawColor.D50, (0.3127, 0.3290)));
        }

        double t1 = RawColor.IlluminantTemperature(p.Illuminant1);
        double t2 = RawColor.IlluminantTemperature(p.Illuminant2);
        bool dual = cm2 is not null && t1 > 0 && t2 > 0 && t1 != t2;

        double WeightOf((double X, double Y) xy)
        {
            if (!dual)
            {
                return 1;
            }

            double t = RawColor.Temperature(xy).Temperature;
            double low = Math.Min(t1, t2), high = Math.Max(t1, t2);
            double w = t <= low ? 1 : t >= high ? 0 : ((1 / t) - (1 / high)) / ((1 / low) - (1 / high));
            return t1 < t2 ? w : 1 - w;
        }

        double[] Blend(double[]? m1, double[]? m2, double w)
        {
            if (m1 is null)
            {
                return m2 ?? RawColor.Identity;
            }

            return m2 is null || !dual ? m1 : [.. m1.Zip(m2, (x, y) => (x * w) + (y * (1 - w)))];
        }

        double[] XyzToCamera(double w)
        {
            double[] cc = Blend(Matrix(p.CameraCalibration1), Matrix(p.CameraCalibration2), w);
            return RawColor.Multiply(ab, RawColor.Multiply(cc, Blend(cm1, cm2, w)));
        }

        (double X, double Y) scene;
        double weight;
        double[] n;
        if (white is (double, double) xy)
        {
            scene = xy;
            weight = WeightOf(scene);
            (double x, double y, double z) = RawColor.XyToXyz(scene);
            (double r, double g, double b) = RawColor.Apply(XyzToCamera(weight), x, y, z);
            n = [r, g, b];
        }
        else
        {
            n = neutral is { Length: 3 } && neutral.All(v => v > 0 && double.IsFinite(v)) ? neutral : [1, 1, 1];
            scene = RawColor.D50;
            weight = WeightOf(scene);
            for (int i = 0; i < 30; i++)
            {
                double[] inverse = RawColor.Invert(XyzToCamera(weight));
                (double x, double y, double z) = RawColor.Apply(inverse, n[0], n[1], n[2]);
                (double X, double Y) next = RawColor.XyzToXy(x, y, z);
                double nextWeight = WeightOf(next);
                bool done = Math.Abs(next.X - scene.X) + Math.Abs(next.Y - scene.Y) < 1e-7;
                scene = next;
                weight = nextWeight;
                if (done)
                {
                    break;
                }
            }
        }

        double max = Math.Max(n[0], Math.Max(n[1], n[2]));
        if (!(max > 0) || !double.IsFinite(max))
        {
            n = [1, 1, 1];
            max = 1;
        }

        n = [.. n.Select(v => Math.Max(v / max, 1e-6))];
        double[] xyzToCamera = XyzToCamera(weight);
        double[] cameraToXyz;
        double[]? fm = Blend(Matrix(p.ForwardMatrix1), Matrix(p.ForwardMatrix2), weight);
        if (Matrix(p.ForwardMatrix1) is not null)
        {
            // ForwardMatrix maps white-balanced camera RGB straight to XYZ D50.
            cameraToXyz = fm;
        }
        else
        {
            fm = null;
            double[] toXyz = RawColor.Multiply(RawColor.Adaptation(scene, RawColor.D50), RawColor.Invert(xyzToCamera));
            cameraToXyz = RawColor.Multiply(toXyz, RawColor.Diagonal(n[0], n[1], n[2]));
        }

        // Camera white (1, 1, 1) is D50 white with Y = 1.
        (double _, double wy, double _) = RawColor.Apply(cameraToXyz, 1, 1, 1);
        if (wy > 1e-9 && double.IsFinite(wy))
        {
            cameraToXyz = RawColor.Scale(cameraToXyz, 1 / wy);
        }

        double[] multipliers = [1 / n[0], 1 / n[1], 1 / n[2]];
        double least = multipliers.Min();
        multipliers = [.. multipliers.Select(m => m / least)];
        return new RawCamera(multipliers, cameraToXyz, scene, Blend(cm1, cm2, weight), fm) { Weight1 = weight };
    }

    private static double[]? Matrix(double[]? m) => m is { Length: 9 } && m.All(double.IsFinite) ? m : null;
}
