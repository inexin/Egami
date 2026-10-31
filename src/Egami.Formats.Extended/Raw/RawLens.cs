namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Applies a lens profile from <see cref="RawLensProfiles"/> to demosaiced camera RGB: vignetting on the pixels where they are,
/// then distortion and lateral chromatic aberration as one resampling, every output pixel sampling the source where the lens put
/// it (bilinearly, red and blue at their own radii). Lensfun's models measure distortion and TCA radii with 1 at half the shorter
/// side of the calibration sensor and vignetting with 1 at half its diagonal, scaled to this camera by the two crop factors;
/// Adobe's camera model measures in units of the focal length, which is given in units of the image's longer side, about an
/// optical centre given as fractions of the width and height. Parameters between calibrated focal lengths and apertures are interpolated linearly.
/// </summary>
internal static class RawLens
{
    private const double FullFrameDiagonal = 43.2666;

    /// <summary>
    /// Corrects the plane, measuring from the part of it that is output (<paramref name="frame"/>, the default crop: the image the
    /// profiles were made for); returns what it applied, or <see langword="null"/> when the profile had nothing for this shot.
    /// </summary>
    public static string? Apply(Memory<float> rgb, int width, int height, Rect frame, RawLensProfile profile, double focal, double aperture, double distance, double cropFactor, ParallelOptions parallel)
    {
        RawLensSample? distortion = Interpolate(profile.Distortion, focal, 0, distance);
        RawLensSample? tca = Interpolate(profile.Tca, focal, 0, distance);
        RawLensSample? vignetting = Interpolate(profile.Vignetting, focal, aperture, distance);
        if (distortion is null && tca is null && vignetting is null)
        {
            return null;
        }

        Geometry g = new(profile, frame, cropFactor);
        if (vignetting is not null)
        {
            RawParallel.For(0, height, parallel, y =>
            {
                Span<float> row = rgb.Span.Slice(y * width * 3, width * 3);
                for (int x = 0; x < width; x++)
                {
                    float gain = (float)(1 / Vignette(g, vignetting, x, y));
                    row[x * 3] *= gain;
                    row[(x * 3) + 1] *= gain;
                    row[(x * 3) + 2] *= gain;
                }
            });
        }

        if (distortion is not null || tca is not null)
        {
            float[] source = System.Buffers.ArrayPool<float>.Shared.Rent(width * height * 3);
            rgb.Span[..(width * height * 3)].CopyTo(source);
            RawParallel.For(0, height, parallel, y =>
            {
                Span<float> row = rgb.Span.Slice(y * width * 3, width * 3);
                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        (double sx, double sy) = Source(g, distortion, tca, x, y, c);
                        row[(x * 3) + c] = Sample(source, width, height, c, sx, sy);
                    }
                }
            });
            System.Buffers.ArrayPool<float>.Shared.Return(source);
        }

        return string.Join(", ", new[] { distortion is null ? null : "distortion", tca is null ? null : "lateral chromatic aberration", vignetting is null ? null : "vignetting" }.Where(s => s is not null));
    }

    /// <summary>The image's geometry in the units each profile format measures in.</summary>
    private sealed class Geometry
    {
        public Geometry(RawLensProfile profile, Rect frame, double cropFactor)
        {
            int width = frame.Width, height = frame.Height;
            Profile = profile;
            Longer = Math.Max(width, height);
            Width = width;
            Height = height;
            OriginX = frame.X;
            OriginY = frame.Y;
            double shorter = Math.Min(width, height);
            CenterX = frame.X + (width / 2.0) + (profile.CenterX * shorter / 2);
            CenterY = frame.Y + (height / 2.0) + (profile.CenterY * shorter / 2);

            // Millimetres per pixel on this camera's sensor, then the calibration sensor's half shorter side and half diagonal.
            double diagonalMm = FullFrameDiagonal / Math.Max(cropFactor, 0.1);
            MmPerPixel = diagonalMm / Math.Sqrt(((double)width * width) + ((double)height * height));
            double calibrationDiagonal = FullFrameDiagonal / Math.Max(profile.CropFactor, 0.1);
            double aspect = Math.Max(profile.AspectRatio, 1);
            HalfShortMm = calibrationDiagonal / (2 * Math.Sqrt(1 + (aspect * aspect)));
            HalfDiagonalMm = calibrationDiagonal / 2;
        }

        public RawLensProfile Profile { get; }

        public double Longer { get; }

        public int Width { get; }

        public int OriginX { get; }

        public int OriginY { get; }

        public int Height { get; }

        /// <summary>
        /// Gets an Adobe profile's optical centre across in units of the longer side: the profile gives it as a fraction of the
        /// width (0.5 when it gives none).
        /// </summary>
        public double LcpCenterX(double[] p) => (double.IsNaN(p[2]) ? 0.5 : p[2]) * Width / Longer;

        /// <summary>Gets an Adobe profile's optical centre down in units of the longer side, from a fraction of the height.</summary>
        public double LcpCenterY(double[] p) => (double.IsNaN(p[3]) ? 0.5 : p[3]) * Height / Longer;

        public double CenterX { get; }

        public double CenterY { get; }

        public double MmPerPixel { get; }

        public double HalfShortMm { get; }

        public double HalfDiagonalMm { get; }
    }

    /// <summary>Returns the vignetting falloff at a pixel: the factor the lens dimmed it by.</summary>
    private static double Vignette(Geometry g, RawLensSample s, int x, int y)
    {
        double[] p = s.Parameters;
        double r2;
        if (s.Model == "lcp")
        {
            (double nx, double ny) = Focal(g, p, x + 0.5, y + 0.5);
            r2 = (nx * nx) + (ny * ny);
            return 1 + (r2 * (p[4] + (r2 * (p[5] + (r2 * p[6])))));
        }

        double dx = (x + 0.5 - g.CenterX) * g.MmPerPixel / g.HalfDiagonalMm, dy = (y + 0.5 - g.CenterY) * g.MmPerPixel / g.HalfDiagonalMm;
        r2 = (dx * dx) + (dy * dy);
        return 1 + (r2 * (p[0] + (r2 * (p[1] + (r2 * p[2])))));
    }

    /// <summary>Returns where an output pixel's channel lies in the source: the lens's distortion, then its colour's TCA.</summary>
    private static (double X, double Y) Source(Geometry g, RawLensSample? distortion, RawLensSample? tca, int x, int y, int channel)
    {
        double px = x + 0.5, py = y + 0.5;
        if (g.Profile.Format == RawLensFormat.Lcp)
        {
            double[] geometry = (distortion ?? tca)!.Parameters;
            (double nx, double ny) = Focal(g, geometry, px, py);
            if (distortion is not null)
            {
                (nx, ny) = LcpDistort(distortion, nx, ny);
            }

            if (tca is not null && channel != 1)
            {
                double[] t = tca.Parameters;
                int o = channel == 0 ? 4 : 8;
                double r2 = (nx * nx) + (ny * ny);
                double scale = t[o] * (1 + (r2 * (t[o + 1] + (r2 * (t[o + 2] + (r2 * t[o + 3]))))));
                nx *= scale;
                ny *= scale;
            }

            return ((((nx * geometry[0]) + g.LcpCenterX(geometry)) * g.Longer) + g.OriginX - 0.5, (((ny * geometry[1]) + g.LcpCenterY(geometry)) * g.Longer) + g.OriginY - 0.5);
        }

        // Lensfun: radii with 1 at the calibration sensor's half shorter side.
        double unit = g.HalfShortMm / g.MmPerPixel;
        double dx = (px - g.CenterX) / unit, dy = (py - g.CenterY) / unit;
        double ru = Math.Sqrt((dx * dx) + (dy * dy));
        double rd = distortion is null ? ru : LensfunDistort(distortion, ru, dx, dy, out dx, out dy);
        if (distortion is not null && distortion.Model != "acm" && ru > 1e-12)
        {
            dx = (px - g.CenterX) / unit * (rd / ru);
            dy = (py - g.CenterY) / unit * (rd / ru);
        }

        if (tca is not null && channel != 1)
        {
            double r = Math.Sqrt((dx * dx) + (dy * dy));
            double[] t = tca.Parameters;
            double scale = tca.Model == "linear" ? t[channel == 0 ? 0 : 1]
                : channel == 0 ? (t[2] * r * r) + (t[1] * r) + t[0]
                : (t[5] * r * r) + (t[4] * r) + t[3];
            dx *= scale;
            dy *= scale;
        }

        return ((dx * unit) + g.CenterX - 0.5, (dy * unit) + g.CenterY - 0.5);
    }

    private static double LensfunDistort(RawLensSample s, double ru, double x, double y, out double xd, out double yd)
    {
        double[] k = s.Parameters;
        double r2 = ru * ru;
        xd = x;
        yd = y;
        switch (s.Model)
        {
            case "poly3":
                return ru * (1 - k[0] + (k[0] * r2));
            case "poly5":
                return ru * (1 + (k[0] * r2) + (k[1] * r2 * r2));
            case "ptlens":
                return ru * ((k[0] * r2 * ru) + (k[1] * r2) + (k[2] * ru) + 1 - k[0] - k[1] - k[2]);
            default:
                double radial = 1 + (r2 * (k[0] + (r2 * (k[1] + (r2 * k[2])))));
                xd = (x * radial) + (2 * x * ((k[3] * y) + (k[4] * x))) + (k[4] * r2);
                yd = (y * radial) + (2 * y * ((k[3] * y) + (k[4] * x))) + (k[3] * r2);
                return Math.Sqrt((xd * xd) + (yd * yd));
        }
    }

    /// <summary>Adobe's camera model: rectilinear radial and tangential terms, or the fisheye's angle polynomial.</summary>
    private static (double X, double Y) LcpDistort(RawLensSample s, double x, double y)
    {
        double[] p = s.Parameters;
        double r2 = (x * x) + (y * y);
        if (s.Model == "lcp-fisheye")
        {
            double r = Math.Sqrt(r2);
            if (r < 1e-12)
            {
                return (x, y);
            }

            double theta = Math.Atan(r), t2 = theta * theta;
            double rd = theta * (1 + (t2 * (p[4] + (t2 * p[5]))));
            return (x * rd / r, y * rd / r);
        }

        double radial = 1 + (r2 * (p[4] + (r2 * (p[5] + (r2 * p[6])))));
        double k4 = p[7], k5 = p[8];
        return ((x * radial) + (2 * x * ((k4 * y) + (k5 * x))) + (k5 * r2), (y * radial) + (2 * y * ((k4 * y) + (k5 * x))) + (k4 * r2));
    }

    /// <summary>Returns a pixel's position in an Adobe profile's units: from the optical centre, over the focal length.</summary>
    private static (double X, double Y) Focal(Geometry g, double[] p, double px, double py)
    {
        return ((((px - g.OriginX) / g.Longer) - g.LcpCenterX(p)) / p[0], (((py - g.OriginY) / g.Longer) - g.LcpCenterY(p)) / p[1]);
    }

    private static float Sample(float[] s, int w, int h, int c, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = s[(((y0 * w) + x0) * 3) + c], b = s[(((y0 * w) + x1) * 3) + c];
        float d = s[(((y1 * w) + x0) * 3) + c], e = s[(((y1 * w) + x1) * 3) + c];
        return (((a * (1 - tx)) + (b * tx)) * (1 - ty)) + (((d * (1 - tx)) + (e * tx)) * ty);
    }

    /// <summary>
    /// Returns the calibration for a shot: linear between the two calibrated focal lengths around it (and, for vignetting, the
    /// two apertures around it at each), the nearest when the shot lies outside them; each focal length uses its calibrations
    /// at the focus distance nearest the shot's.
    /// </summary>
    internal static RawLensSample? Interpolate(List<RawLensSample> samples, double focal, double aperture, double distance)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        string model = samples[0].Model;
        List<RawLensSample> same = [.. samples.Where(s => s.Model == model)];
        double[] focals = [.. same.Select(s => s.Focal).Distinct().Order()];
        (double f0, double f1, double tf) = Bracket(focals, focal);
        double[] AtFocal(double f)
        {
            // Each focal length's calibrations at the focus distance nearest the shot's (in ratio), then its apertures.
            List<RawLensSample> atF = [.. same.Where(s => s.Focal == f)];
            if (distance > 0)
            {
                double nearest = atF.MinBy(s => Math.Abs(Math.Log(Math.Max(s.Distance, 0.01) / distance)))!.Distance;
                atF = [.. atF.Where(s => s.Distance == nearest)];
            }

            if (aperture <= 0 || atF.Count == 1)
            {
                return atF[0].Parameters;
            }

            double[] apertures = [.. atF.Select(s => s.Aperture).Distinct().Order()];
            (double a0, double a1, double ta) = Bracket(apertures, aperture);
            return Mix(atF.First(s => s.Aperture == a0).Parameters, atF.First(s => s.Aperture == a1).Parameters, ta);
        }

        return new RawLensSample(focal, aperture, distance, model, Mix(AtFocal(f0), AtFocal(f1), tf));
    }

    private static (double Low, double High, double T) Bracket(double[] values, double v)
    {
        if (v <= values[0] || values.Length == 1)
        {
            return (values[0], values[0], 0);
        }

        if (v >= values[^1])
        {
            return (values[^1], values[^1], 0);
        }

        int i = 1;
        while (values[i] < v)
        {
            i++;
        }

        return (values[i - 1], values[i], (v - values[i - 1]) / (values[i] - values[i - 1]));
    }

    private static double[] Mix(double[] a, double[] b, double t) => [.. a.Zip(b, (x, y) => x + ((y - x) * t))];
}
