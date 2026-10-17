namespace Egami.Colors;

/// <summary>
/// The reference color pipeline: device values of one encoding to device values of another through the ICC connection space
/// (XYZ relative to D50), in double precision. It defines what a conversion means; compiled transforms sample it and are tested
/// against it. Device values are normalized: 0..1 for every channel, CMYK included.
/// </summary>
/// <remarks>
/// It follows ICC.1 and, where the specification leaves a choice, Little CMS 2, the reference Egami is checked against: the
/// rendering intent picks A2B and B2A tables with a fall back to the perceptual ones; absolute colorimetric scales XYZ by the
/// ratio of media whites, where a version 2 display profile's white counts as D50; black point compensation follows Adobe's
/// published algorithm, and is always on for perceptual and saturation conversions into a version 4 profile.
/// </remarks>
internal sealed class ColorPipeline
{
    private static readonly Vector3d PerceptualBlack = new(0.00336, 0.0034731, 0.00287);

    private readonly ToPcs _source;
    private readonly FromPcs _target;
    private readonly Vector3d _scale;
    private readonly Vector3d _offset;
    private readonly HdrStep _hdr;

    private ColorPipeline(ToPcs source, FromPcs target, Vector3d scale, Vector3d offset, HdrStep hdr)
    {
        (_source, _target, _scale, _offset, _hdr) = (source, target, scale, offset, hdr);
    }

    public int InputChannels => _source.Channels;

    public int OutputChannels => _target.Channels;

    /// <summary>Builds the pipeline from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <exception cref="UnsupportedFormatException">A profile has no tables for the direction it is used in.</exception>
    public static ColorPipeline Create(ColorProfile from, ColorProfile to, RenderingIntent intent, bool blackPointCompensation) => Build(from, to, intent, blackPointCompensation, null, 1000);

    /// <summary>
    /// Builds the pipeline with the HDR steps of <paramref name="options"/>: reference white scaling on PQ and HLG ends, and for HDR
    /// to SDR, tone mapping of the source's linear RGB (peak <paramref name="sourcePeakNits"/>) and a hue-keeping gamut clip.
    /// </summary>
    public static ColorPipeline Create(ColorProfile from, ColorProfile to, ColorTransformOptions options, double sourcePeakNits) =>
        Build(from, to, options.Intent, options.BlackPointCompensation, options, sourcePeakNits);

    /// <summary>
    /// Builds a pipeline from <paramref name="from"/> to connection space XYZ (D50), with the steps a conversion to an abstract
    /// Lab profile gets: absolute colorimetric against a D50 white, and black point compensation to a black of 0.
    /// </summary>
    public static ColorPipeline ToConnectionSpace(ColorProfile from, RenderingIntent intent, bool blackPointCompensation) => Build(from, null, intent, blackPointCompensation, null, 1000);

    /// <summary>
    /// Builds a pipeline from connection space XYZ (D50) to <paramref name="to"/>'s device values through its tables for the
    /// intent, with no compensation: for checking a profile's round trips.
    /// </summary>
    internal static ColorPipeline FromConnectionSpace(ColorProfile to, RenderingIntent intent) =>
        new(new ToPcs.Connection(), FromPcs.Create(to, intent), new Vector3d(1, 1, 1), default, HdrStep.None);

    // A null target is the connection space itself, as an abstract version 4 profile: D50 white, black 0.
    private static ColorPipeline Build(ColorProfile from, ColorProfile? to, RenderingIntent intent, bool blackPointCompensation, ColorTransformOptions? options, double sourcePeakNits)
    {
        double white = options?.ReferenceWhiteNits ?? 203;
        bool tone = options is not null && from.IsHdr && to is not null && !to.IsHdr && options.ToneMapping != ToneMapping.Clip;
        HdrStep hdr = new(
            from.IsHdr ? 203 / white : 1,
            to?.IsHdr == true ? white / 203 : 1,
            tone ? options!.ToneMapping : null,
            sourcePeakNits / white);
        ToPcs source = ToPcs.Create(from, intent, hdr);
        FromPcs target = to is null ? new FromPcs.Connection() : FromPcs.Create(to, intent, hdr);
        Vector3d scale = new(1, 1, 1), offset = default;
        if (intent == RenderingIntent.AbsoluteColorimetric)
        {
            Vector3d a = MediaWhite(from), b = to is null ? Cie.D50 : MediaWhite(to);
            scale = new Vector3d(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
        }
        else if (blackPointCompensation || ((to is null || IsVersion4(to)) && intent is RenderingIntent.Perceptual or RenderingIntent.Saturation))
        {
            (scale, offset) = Compensation(BlackPoint(from, intent), to is null ? default : DestinationBlackPoint(to, intent));
        }

        return new ColorPipeline(source, target, scale, offset, hdr);
    }

    // Black point compensation: a per-channel line through D50 white that takes the source black to the target black.
    private static (Vector3d Scale, Vector3d Offset) Compensation(Vector3d inBlack, Vector3d outBlack)
    {
        if (inBlack == outBlack)
        {
            return (new Vector3d(1, 1, 1), default);
        }

        Vector3d w = Cie.D50;
        Vector3d scale = new((outBlack.X - w.X) / (inBlack.X - w.X), (outBlack.Y - w.Y) / (inBlack.Y - w.Y), (outBlack.Z - w.Z) / (inBlack.Z - w.Z));
        Vector3d offset = new(-w.X * (outBlack.X - inBlack.X) / (inBlack.X - w.X), -w.Y * (outBlack.Y - inBlack.Y) / (inBlack.Y - w.Y), -w.Z * (outBlack.Z - inBlack.Z) / (inBlack.Z - w.Z));
        return (scale, offset);
    }

    /// <summary>
    /// Returns the pipeline as curves, an affine map and curves, when both ends are matrix or gray encodings whose curves are
    /// transfer functions: linear out = <see cref="LinearForm.Matrix"/> × linear in + <see cref="LinearForm.Offset"/>. A gray
    /// input is the first component of linear in; a gray output is the first component of linear out.
    /// </summary>
    public LinearForm? GetLinearForm()
    {
        if (_source.Linear is not (TransferFunction[] inCurves, Matrix3d toPcs) || _target.Linear is not (TransferFunction[] outCurves, Matrix3d fromPcs))
        {
            return null;
        }

        Matrix3d matrix = fromPcs * Matrix3d.Diagonal(_scale) * toPcs;
        return new LinearForm(inCurves, matrix, fromPcs * _offset, outCurves, _hdr, _hdr.Tone is null ? null : _target.Luminance);
    }

    /// <summary>Converts one color.</summary>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Vector3d xyz = _source.Run(input);
        _target.Run(new Vector3d((xyz.X * _scale.X) + _offset.X, (xyz.Y * _scale.Y) + _offset.Y, (xyz.Z * _scale.Z) + _offset.Z), output);
    }

    /// <summary>Returns the device values' connection space XYZ (relative to D50) under an intent, without the PCS steps.</summary>
    internal static Vector3d DeviceToXyz(ColorProfile profile, RenderingIntent intent, ReadOnlySpan<double> device) => ToPcs.Create(profile, intent).Run(device);

    /// <summary>Returns the media white Little CMS uses for absolute colorimetric: D50 unless the profile's wtpt says otherwise.</summary>
    internal static Vector3d MediaWhite(ColorProfile profile)
    {
        if (profile.IccModel is not IccModel icc || (icc.Data.Version.Major < 4 && icc.Data.ProfileClass == IccProfileClass.Display))
        {
            return Cie.D50;
        }

        return icc.MediaWhite;
    }

    /// <summary>Returns the black point of a source profile (Adobe's algorithm, as Little CMS's cmsDetectBlackPoint).</summary>
    internal static Vector3d BlackPoint(ColorProfile profile, RenderingIntent intent)
    {
        if (IsVersion4(profile) && intent is RenderingIntent.Perceptual or RenderingIntent.Saturation)
        {
            return HasMatrix(profile) ? DarkerColorant(profile, RenderingIntent.RelativeColorimetric) : PerceptualBlack;
        }

        // Little CMS 2.19 does this for n-colour output profiles too.
        if (intent == RenderingIntent.RelativeColorimetric && profile.Model is ColorModel.Cmyk or ColorModel.NColor && profile.Icc?.ProfileClass == IccProfileClass.Output)
        {
            return BlackThroughPerceptual(profile);
        }

        return DarkerColorant(profile, intent);
    }

    /// <summary>Returns the black point of a target profile (as Little CMS's cmsDetectDestinationBlackPoint).</summary>
    internal static Vector3d DestinationBlackPoint(ColorProfile profile, RenderingIntent intent)
    {
        if (IsVersion4(profile) && intent is RenderingIntent.Perceptual or RenderingIntent.Saturation)
        {
            return HasMatrix(profile) ? DarkerColorant(profile, RenderingIntent.RelativeColorimetric) : PerceptualBlack;
        }

        if (profile.IccModel?.PcsToDevice[TableIndex(intent)] is null)
        {
            return BlackPoint(profile, intent);
        }

        // A first guess: the source black point for relative colorimetric, else 0.
        Vector3d initial = default;
        if (intent == RenderingIntent.RelativeColorimetric)
        {
            initial = Cie.XyzToLab(BlackPoint(profile, intent), Cie.D50);
        }

        // Round-trip a ramp of L* through the profile and find where the output stops rising.
        Func<Vector3d, Vector3d> roundTrip = RoundTrip(profile, intent);
        double a = Math.Clamp(initial.Y, -50, 50), b = Math.Clamp(initial.Z, -50, 50);
        double[] inRamp = new double[256], outRamp = new double[256];
        for (int l = 0; l < 256; l++)
        {
            inRamp[l] = l * 100.0 / 255;
            outRamp[l] = roundTrip(new Vector3d(inRamp[l], a, b)).X;
        }

        for (int l = 254; l > 0; l--)
        {
            outRamp[l] = Math.Min(outRamp[l], outRamp[l + 1]);
        }

        if (!(outRamp[0] < outRamp[255]))
        {
            return default;
        }

        double minL = outRamp[0], maxL = outRamp[255];
        if (intent == RenderingIntent.RelativeColorimetric)
        {
            bool straight = true;
            for (int l = 0; l < 256; l++)
            {
                straight &= inRamp[l] <= minL + (0.2 * (maxL - minL)) || Math.Abs(inRamp[l] - outRamp[l]) < 4.0;
            }

            if (straight)
            {
                return Cie.LabToXyz(initial, Cie.D50);
            }
        }

        // Fit a quadratic to the shadow part of the curve, normalized 0..1, and take where it meets 0.
        (double lo, double hi) = intent == RenderingIntent.RelativeColorimetric ? (0.1, 0.5) : (0.03, 0.25);
        List<(double X, double Y)> points = [];
        for (int l = 0; l < 256; l++)
        {
            double y = (outRamp[l] - minL) / (maxL - minL);
            if (y >= lo && y < hi)
            {
                points.Add((inRamp[l], y));
            }
        }

        if (points.Count < 3)
        {
            return default;
        }

        double black = Math.Max(0, QuadraticRoot(points));
        return Cie.LabToXyz(new Vector3d(black, initial.Y, initial.Z), Cie.D50);
    }

    private static Vector3d Row(Matrix3d m, int row) => new(m[row, 0], m[row, 1], m[row, 2]);

    // Gray in: Y becomes D50 times Y. Gray out: Y is taken from XYZ.
    private static Matrix3d GrayIn => new(Cie.D50.X, 0, 0, Cie.D50.Y, 0, 0, Cie.D50.Z, 0, 0);

    private static Matrix3d GrayOut => new(0, 1, 0, 0, 0, 0, 0, 0, 0);

    private static bool IsVersion4(ColorProfile profile) => profile.Icc is not IccData icc || icc.Version.Major >= 4;

    // Little CMS's cmsIsMatrixShaper: the matrix and curve tags are there, whether or not tables are too.
    private static bool HasMatrix(ColorProfile profile) => profile.IccModel is not IccModel icc ? profile.Model != ColorModel.Cmyk : icc.Curves is not null;

    // The A2B or B2A tag an intent uses: absolute colorimetric uses the colorimetric one.
    private static int TableIndex(RenderingIntent intent) => intent == RenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;

    private static bool SupportsAsInput(ColorProfile profile, RenderingIntent intent) => HasMatrix(profile) || profile.IccModel?.DeviceToPcs[TableIndex(intent)] is not null;

    // The device's darkest color through the profile; Little CMS 2.19 keeps its tint, and treats an L* over 50 as no black.
    private static Vector3d DarkerColorant(ColorProfile profile, RenderingIntent intent)
    {
        if (!SupportsAsInput(profile, intent))
        {
            return default;
        }

        if (profile.Model == ColorModel.NColor)
        {
            // Little CMS knows no darkest colorant combination for n-colour spaces, so it finds no black point.
            return default;
        }

        ReadOnlySpan<double> black = profile.Model switch
        {
            ColorModel.Cmyk => [1, 1, 1, 1],
            ColorModel.Gray => [0],
            _ => [0, 0, 0],
        };
        Vector3d xyz = DeviceToXyz(profile, intent, black);
        double l = Cie.XyzToLab(xyz, Cie.D50).X;
        return l is > 50 or < 0 ? default : xyz;
    }

    // Lab black round-tripped through the perceptual tables, made neutral and no lighter than L* 50.
    private static Vector3d BlackThroughPerceptual(ColorProfile profile)
    {
        if (!SupportsAsInput(profile, RenderingIntent.Perceptual))
        {
            return default;
        }

        Vector3d lab = RoundTrip(profile, RenderingIntent.Perceptual)(default);
        return Cie.LabToXyz(new Vector3d(Math.Min(lab.X, 50), 0, 0), Cie.D50);
    }

    // Lab to the device through the profile's tables for the intent, and back through its colorimetric tables. The way in is a
    // conversion from an abstract Lab profile (black 0), so a version 4 target gets black point compensation in perceptual and
    // saturation, as everywhere.
    private static Func<Vector3d, Vector3d> RoundTrip(ColorProfile profile, RenderingIntent intent)
    {
        FromPcs toDevice = FromPcs.Create(profile, intent);
        ToPcs back = ToPcs.Create(profile, RenderingIntent.RelativeColorimetric);
        (Vector3d scale, Vector3d offset) = IsVersion4(profile) && intent is RenderingIntent.Perceptual or RenderingIntent.Saturation
            ? Compensation(default, DestinationBlackPoint(profile, intent))
            : (new Vector3d(1, 1, 1), default);
        double[] device = new double[toDevice.Channels];
        return lab =>
        {
            Vector3d xyz = Cie.LabToXyz(lab, Cie.D50);
            toDevice.Run(new Vector3d((xyz.X * scale.X) + offset.X, (xyz.Y * scale.Y) + offset.Y, (xyz.Z * scale.Z) + offset.Z), device);
            return Cie.XyzToLab(back.Run(device), Cie.D50);
        };
    }

    // The least-squares quadratic through the points, and its root at y = 0 that is between 0 and 50.
    private static double QuadraticRoot(List<(double X, double Y)> points)
    {
        double n = points.Count, sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sy = 0, syx = 0, syx2 = 0;
        foreach ((double x, double y) in points)
        {
            sx += x;
            sx2 += x * x;
            sx3 += x * x * x;
            sx4 += x * x * x * x;
            sy += y;
            syx += y * x;
            syx2 += y * x * x;
        }

        Matrix3d m = new(n, sx, sx2, sx, sx2, sx3, sx2, sx3, sx4);
        Vector3d solution;
        try
        {
            solution = m.Inverse() * new Vector3d(sy, syx, syx2);
        }
        catch (InvalidOperationException)
        {
            return 0;
        }

        (double c, double b, double a) = (solution.X, solution.Y, solution.Z);
        if (Math.Abs(a) < 1e-10)
        {
            // Little CMS clamps the linear case's root with min(0, max(50, root)), which is always 0.
            return 0;
        }

        double d = (b * b) - (4 * a * c);
        return d <= 0 ? 0 : Math.Clamp((-b + Math.Sqrt(d)) / (2 * a), 0, 50);
    }

    /// <summary>One profile used as a source: device values to connection space XYZ.</summary>
    private abstract class ToPcs
    {
        protected ToPcs(int channels)
        {
            Channels = channels;
        }

        public int Channels { get; }

        public static ToPcs Create(ColorProfile profile, RenderingIntent intent, HdrStep? hdr = null)
        {
            // An HDR profile from ICC is used through its cicp code points, not its fallback curve.
            if (profile.IccModel is not IccModel icc || (profile.IsHdr && profile.Primaries is not null))
            {
                return new Encoding(profile, hdr ?? HdrStep.None);
            }

            IccLut? lut = icc.DeviceToPcs[TableIndex(intent)] ?? icc.DeviceToPcs[0];
            if (lut is not null)
            {
                return new Table(icc, lut);
            }

            return icc.Curves is not null ? new Matrix(icc) : throw new UnsupportedFormatException($"The ICC profile '{profile.Name}' has no tables for converting from it.");
        }

        public abstract Vector3d Run(ReadOnlySpan<double> device);

        /// <summary>Gets the curves and the matrix from linear device values to XYZ, when the profile is that simple.</summary>
        public virtual (TransferFunction[] Curves, Matrix3d Matrix)? Linear => null;

        /// <summary>The connection space itself: XYZ in as it comes.</summary>
        public sealed class Connection() : ToPcs(3)
        {
            public override Vector3d Run(ReadOnlySpan<double> device) => new(device[0], device[1], device[2]);
        }

        /// <summary>
        /// A built-in or created encoding: its curve (scaled to the reference white, and tone mapped on max(R, G, B) for HDR to
        /// SDR), its primaries' matrix, and Bradford adaptation to D50.
        /// </summary>
        private sealed class Encoding(ColorProfile profile, HdrStep hdr) : ToPcs(profile.Model == ColorModel.Gray ? 1 : 3)
        {
            private readonly TransferFunction _curve = profile.Transfer!;
            private readonly Matrix3d _matrix = profile.Model == ColorModel.Gray ? Matrix3d.Identity : Matrix3d.Bradford(profile.White.ToXyz(), Cie.D50) * profile.ToXyz();

            public override (TransferFunction[] Curves, Matrix3d Matrix)? Linear => Channels == 1 ? ([_curve], GrayIn) : ([_curve, _curve, _curve], _matrix);

            public override Vector3d Run(ReadOnlySpan<double> device)
            {
                if (Channels == 1)
                {
                    double y = _curve.ToLinear(device[0]) * hdr.InScale;
                    return Cie.D50 * (hdr.Tone is ToneMapping m ? ToneCurves.Map(m, y, hdr.PeakRelative) : y);
                }

                Vector3d linear = new Vector3d(_curve.ToLinear(device[0]), _curve.ToLinear(device[1]), _curve.ToLinear(device[2])) * hdr.InScale;
                if (hdr.Tone is ToneMapping method)
                {
                    double max = Math.Max(linear.X, Math.Max(linear.Y, linear.Z));
                    linear *= max > 0 ? ToneCurves.Map(method, max, hdr.PeakRelative) / max : 0;
                }

                return _matrix * linear;
            }
        }

        /// <summary>An ICC matrix-shaper (curves, then the colorants) or gray profile (the curve times D50, or as L*).</summary>
        private sealed class Matrix(IccModel icc) : ToPcs(icc.Channels)
        {
            public override (TransferFunction[] Curves, Matrix3d Matrix)? Linear
            {
                get
                {
                    if (Channels == 1 && icc.PcsIsLab)
                    {
                        return null;
                    }

                    TransferFunction?[] curves = [.. icc.Curves!.Select(c => c.ToTransferFunction())];
                    return curves.Any(c => c is null) ? null : ([.. curves.Select(c => c!)], Channels == 1 ? GrayIn : icc.Colorants!.Value);
                }
            }

            public override Vector3d Run(ReadOnlySpan<double> device)
            {
                IccCurve[] curves = icc.Curves!;
                if (Channels == 1)
                {
                    double v = curves[0].Evaluate(device[0]);
                    return icc.PcsIsLab ? Cie.LabToXyz(new Vector3d(v * 100, 0, 0), Cie.D50) : Cie.D50 * v;
                }

                return icc.Colorants!.Value * new Vector3d(curves[0].Evaluate(device[0]), curves[1].Evaluate(device[1]), curves[2].Evaluate(device[2]));
            }
        }

        /// <summary>An ICC A2B table: its stages, then the connection space decoded.</summary>
        private sealed class Table(IccModel icc, IccLut lut) : ToPcs(lut.Inputs)
        {
            public override Vector3d Run(ReadOnlySpan<double> device)
            {
                Span<double> v = stackalloc double[16];
                device[..Channels].CopyTo(v);
                IccStages.Run(lut.Stages, v, Channels);
                return IccStages.DecodePcs(new Vector3d(v[0], v[1], v[2]), icc.PcsIsLab, lut.Type == IccLutType.Lut16);
            }
        }
    }

    /// <summary>One profile used as a target: connection space XYZ to device values.</summary>
    private abstract class FromPcs
    {
        protected FromPcs(int channels)
        {
            Channels = channels;
        }

        public int Channels { get; }

        public static FromPcs Create(ColorProfile profile, RenderingIntent intent, HdrStep? hdr = null)
        {
            hdr ??= HdrStep.None;
            if (profile.IccModel is not IccModel icc || (profile.IsHdr && profile.Primaries is not null))
            {
                return new Encoding(profile, hdr);
            }

            IccLut? lut = icc.PcsToDevice[TableIndex(intent)] ?? icc.PcsToDevice[0];
            if (lut is not null)
            {
                return new Table(icc, lut);
            }

            if (icc.Curves is not null && icc.DeviceToPcs[0] is null)
            {
                return new Matrix(icc, hdr.Tone is not null);
            }

            throw new UnsupportedFormatException($"The ICC profile '{profile.Name}' has no tables for converting to it.");
        }

        public abstract void Run(Vector3d xyz, Span<double> device);

        /// <summary>Gets the matrix from XYZ to linear device values and the curves, when the profile is that simple.</summary>
        public virtual (TransferFunction[] Curves, Matrix3d Matrix)? Linear => null;

        /// <summary>Gets the luminance of linear device RGB (the Y row of its matrix to XYZ), for the gamut clip; null for gray.</summary>
        public virtual Vector3d? Luminance => null;

        /// <summary>
        /// Brings a linear RGB color into 0..1 by moving it toward its luminance Y, which keeps its hue: the largest step back that
        /// puts every channel in range, and white where Y itself is above 1.
        /// </summary>
        internal static Vector3d GamutClip(Vector3d c, Vector3d luminance)
        {
            double y = Math.Clamp((luminance.X * c.X) + (luminance.Y * c.Y) + (luminance.Z * c.Z), 0, 1);
            double t = 1;
            foreach (double v in (ReadOnlySpan<double>)[c.X, c.Y, c.Z])
            {
                if (v > 1)
                {
                    t = Math.Min(t, (1 - y) / (v - y));
                }
                else if (v < 0)
                {
                    t = Math.Min(t, y / (y - v));
                }
            }

            return t >= 1 ? c : new Vector3d(y + ((c.X - y) * t), y + ((c.Y - y) * t), y + ((c.Z - y) * t));
        }

        /// <summary>The connection space itself: XYZ out as it comes.</summary>
        public sealed class Connection() : FromPcs(3)
        {
            public override void Run(Vector3d xyz, Span<double> device)
            {
                (device[0], device[1], device[2]) = (xyz.X, xyz.Y, xyz.Z);
            }
        }

        private sealed class Encoding(ColorProfile profile, HdrStep hdr) : FromPcs(profile.Model == ColorModel.Gray ? 1 : 3)
        {
            private readonly TransferFunction _curve = profile.Transfer!;
            private readonly Matrix3d _matrix = profile.Model == ColorModel.Gray ? Matrix3d.Identity : (Matrix3d.Bradford(profile.White.ToXyz(), Cie.D50) * profile.ToXyz()).Inverse();
            private readonly Vector3d? _luminance = profile.Model == ColorModel.Gray ? null : Row(profile.ToXyz(), 1);

            public override (TransferFunction[] Curves, Matrix3d Matrix)? Linear => Channels == 1 ? ([_curve], GrayOut) : ([_curve, _curve, _curve], _matrix);

            public override Vector3d? Luminance => _luminance;

            public override void Run(Vector3d xyz, Span<double> device)
            {
                if (Channels == 1)
                {
                    device[0] = _curve.FromLinear(hdr.Tone is null ? xyz.Y * hdr.OutScale : Math.Clamp(xyz.Y, 0, 1));
                    return;
                }

                Vector3d linear = _matrix * xyz;
                if (hdr.Tone is not null)
                {
                    linear = GamutClip(linear, _luminance!.Value);
                }

                linear *= hdr.OutScale;
                device[0] = _curve.FromLinear(linear.X);
                device[1] = _curve.FromLinear(linear.Y);
                device[2] = _curve.FromLinear(linear.Z);
            }
        }

        private sealed class Matrix(IccModel icc, bool clip = false) : FromPcs(icc.Channels)
        {
            private readonly Matrix3d _inverse = icc.Model == ColorModel.Gray ? Matrix3d.Identity : icc.Colorants!.Value.Inverse();

            public override Vector3d? Luminance => icc.Model == ColorModel.Gray ? null : Row(icc.Colorants!.Value, 1) * (1 / Cie.D50.Y);

            public override (TransferFunction[] Curves, Matrix3d Matrix)? Linear
            {
                get
                {
                    if (Channels == 1 && icc.PcsIsLab)
                    {
                        return null;
                    }

                    TransferFunction?[] curves = [.. icc.Curves!.Select(c => c.ToTransferFunction())];
                    return curves.Any(c => c is null) ? null : ([.. curves.Select(c => c!)], Channels == 1 ? GrayOut : _inverse);
                }
            }

            public override void Run(Vector3d xyz, Span<double> device)
            {
                IccCurve[] curves = icc.Curves!;
                if (Channels == 1)
                {
                    device[0] = curves[0].EvaluateInverse(icc.PcsIsLab ? Cie.XyzToLab(xyz, Cie.D50).X / 100 : xyz.Y);
                    return;
                }

                Vector3d linear = _inverse * xyz;
                if (clip)
                {
                    linear = GamutClip(linear, Luminance!.Value);
                }

                device[0] = curves[0].EvaluateInverse(linear.X);
                device[1] = curves[1].EvaluateInverse(linear.Y);
                device[2] = curves[2].EvaluateInverse(linear.Z);
            }
        }

        private sealed class Table(IccModel icc, IccLut lut) : FromPcs(lut.Outputs)
        {
            public override void Run(Vector3d xyz, Span<double> device)
            {
                Span<double> v = stackalloc double[16];
                Vector3d encoded = IccStages.EncodePcs(xyz, icc.PcsIsLab, lut.Type == IccLutType.Lut16);
                (v[0], v[1], v[2]) = (encoded.X, encoded.Y, encoded.Z);
                IccStages.Run(lut.Stages, v, 3, trilinear: icc.PcsIsLab);
                v[..Channels].CopyTo(device);
            }
        }
    }
}

/// <summary>
/// A pipeline as curves, an affine map and curves; see <see cref="ColorPipeline.GetLinearForm"/>. The HDR steps go around the map:
/// the input scale and tone mapping before it, the gamut clip toward <paramref name="Luminance"/> and the output scale after it.
/// </summary>
internal sealed record LinearForm(TransferFunction[] InCurves, Matrix3d Matrix, Vector3d Offset, TransferFunction[] OutCurves, HdrStep Hdr, Vector3d? Luminance);

/// <summary>
/// The HDR steps of a conversion: linear light from a PQ or HLG source times <paramref name="InScale"/> (203 over the reference
/// white), into a PQ or HLG target times <paramref name="OutScale"/>, and for HDR to SDR the tone curve with the source peak
/// relative to reference white.
/// </summary>
internal sealed record HdrStep(double InScale, double OutScale, ToneMapping? Tone, double PeakRelative)
{
    public static HdrStep None { get; } = new(1, 1, null, 1);
}

/// <summary>Evaluates the stages of an ICC table on normalized values, and the connection space encodings at its ends.</summary>
internal static class IccStages
{
    // u1Fixed15's largest value: XYZ in a table is normalized by it.
    private const double MaxXyz = 65535.0 / 32768;

    // Version 2 Lab (lut16Type) puts L* 100 at 0xFF00, not 0xFFFF.
    private const double LegacyLab = 65535.0 / 65280;

    /// <summary>
    /// Runs the stages in place on <paramref name="values"/>; returns the number of channels at the end. Tables indexed by Lab
    /// interpolate trilinearly, as Little CMS changes them to.
    /// </summary>
    public static int Run(IccStage[] stages, Span<double> values, int channels, bool trilinear = false)
    {
        Span<double> scratch = stackalloc double[16];
        foreach (IccStage stage in stages)
        {
            switch (stage)
            {
                case IccStage.Curves c:
                    for (int i = 0; i < c.Channels.Length; i++)
                    {
                        values[i] = c.Channels[i].Evaluate(values[i]);
                    }

                    break;
                case IccStage.Matrix m:
                    {
                        double[] k = m.Values;
                        double x = values[0], y = values[1], z = values[2];
                        values[0] = (k[0] * x) + (k[1] * y) + (k[2] * z) + m.Offsets[0];
                        values[1] = (k[3] * x) + (k[4] * y) + (k[5] * z) + m.Offsets[1];
                        values[2] = (k[6] * x) + (k[7] * y) + (k[8] * z) + m.Offsets[2];
                        break;
                    }

                case IccStage.Lookup l:
                    Interpolate(l.Table, values[..channels], scratch, trilinear);
                    channels = l.Table.Outputs;
                    scratch[..channels].CopyTo(values);
                    break;
            }
        }

        return channels;
    }

    public static Vector3d DecodePcs(Vector3d n, bool lab, bool legacy)
    {
        if (!lab)
        {
            return n * MaxXyz;
        }

        if (legacy)
        {
            n *= LegacyLab;
        }

        return Cie.LabToXyz(new Vector3d(n.X * 100, (n.Y * 255) - 128, (n.Z * 255) - 128), Cie.D50);
    }

    public static Vector3d EncodePcs(Vector3d xyz, bool lab, bool legacy)
    {
        if (!lab)
        {
            return xyz * (1 / MaxXyz);
        }

        Vector3d v = Cie.XyzToLab(xyz, Cie.D50);
        Vector3d n = new(v.X / 100, (v.Y + 128) / 255, (v.Z + 128) / 255);
        return legacy ? n * (1 / LegacyLab) : n;
    }

    /// <summary>
    /// Interpolates a CLUT as Little CMS does, with inputs and outputs at the table's 16-bit precision: linear for one input,
    /// bilinear for two, tetrahedral (or trilinear) for three, and for four to fifteen, linear along the first input between
    /// interpolations of the others, down to tetrahedral over the last three.
    /// </summary>
    public static void Interpolate(IccClut clut, ReadOnlySpan<double> values, Span<double> output, bool trilinear = false)
    {
        int[] grid = clut.Grid;
        int outputs = clut.Outputs;
        float[] table = clut.Values;
        Span<double> input = stackalloc double[values.Length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = Math.Round(Math.Clamp(double.IsNaN(values[i]) ? 0 : values[i], 0, 1) * 65535) / 65535;
        }

        switch (grid.Length)
        {
            case 1:
                {
                    (int i, double r) = Cell(input[0], grid[0]);
                    for (int o = 0; o < outputs; o++)
                    {
                        double a = table[(i * outputs) + o];
                        output[o] = a + ((table[((i + 1) * outputs) + o] - a) * r);
                    }

                    break;
                }

            case 3 when trilinear:
                Trilinear(table, grid[0], grid[1], grid[2], outputs, input[0], input[1], input[2], output);
                break;
            case 3:
                Tetrahedral(table, 0, grid[0], grid[1], grid[2], outputs, input[0], input[1], input[2], output);
                break;
            case 2:
                Bilinear(table, grid[0], grid[1], outputs, input[0], input[1], output);
                break;
            default:
                Linear(table, grid, 0, 0, outputs, input, output);
                break;
        }

        for (int o = 0; o < outputs; o++)
        {
            output[o] = Math.Round(output[o] * 65535) / 65535;
        }
    }

    // Four or more inputs: linear along input `dim` between the sub-tables at its cell's two ends, each interpolated over the
    // remaining inputs the same way, until three remain.
    private static void Linear(float[] t, int[] grid, int dim, int origin, int outputs, ReadOnlySpan<double> input, Span<double> output)
    {
        if (grid.Length - dim == 3)
        {
            Tetrahedral(t, origin, grid[dim], grid[dim + 1], grid[dim + 2], outputs, input[dim], input[dim + 1], input[dim + 2], output);
            return;
        }

        int stride = outputs;
        for (int i = dim + 1; i < grid.Length; i++)
        {
            stride *= grid[i];
        }

        (int k, double r) = Cell(input[dim], grid[dim]);
        Span<double> high = stackalloc double[outputs];
        Linear(t, grid, dim + 1, origin + (k * stride), outputs, input, output);
        Linear(t, grid, dim + 1, origin + ((k + 1) * stride), outputs, input, high);
        for (int o = 0; o < outputs; o++)
        {
            output[o] += (high[o] - output[o]) * r;
        }
    }

    private static void Bilinear(float[] t, int nx, int ny, int outputs, double x, double y, Span<double> output)
    {
        (int ix, double rx) = Cell(x, nx);
        (int iy, double ry) = Cell(y, ny);
        int sy = outputs, sx = ny * outputs;
        int c00 = (ix * sx) + (iy * sy);
        for (int o = 0; o < outputs; o++)
        {
            int c = c00 + o;
            double d0 = t[c] + ((t[c + sx] - t[c]) * rx);
            double d1 = t[c + sy] + ((t[c + sx + sy] - t[c + sy]) * rx);
            output[o] = d0 + ((d1 - d0) * ry);
        }
    }

    private static (int Index, double Fraction) Cell(double x, int points)
    {
        double p = Math.Clamp(double.IsNaN(x) ? 0 : x, 0, 1) * (points - 1);
        int i = Math.Min((int)p, points - 2);
        return (i, p - i);
    }

    private static void Trilinear(float[] t, int nx, int ny, int nz, int outputs, double x, double y, double z, Span<double> output)
    {
        (int ix, double rx) = Cell(x, nx);
        (int iy, double ry) = Cell(y, ny);
        (int iz, double rz) = Cell(z, nz);
        int sz = outputs, sy = nz * outputs, sx = ny * nz * outputs;
        int c000 = (ix * sx) + (iy * sy) + (iz * sz);
        for (int o = 0; o < outputs; o++)
        {
            int c = c000 + o;
            double d00 = t[c] + ((t[c + sx] - t[c]) * rx);
            double d01 = t[c + sz] + ((t[c + sx + sz] - t[c + sz]) * rx);
            double d10 = t[c + sy] + ((t[c + sx + sy] - t[c + sy]) * rx);
            double d11 = t[c + sy + sz] + ((t[c + sx + sy + sz] - t[c + sy + sz]) * rx);
            double e0 = d00 + ((d10 - d00) * ry), e1 = d01 + ((d11 - d01) * ry);
            output[o] = e0 + ((e1 - e0) * rz);
        }
    }

    private static void Tetrahedral(float[] t, int origin, int nx, int ny, int nz, int outputs, double x, double y, double z, Span<double> output)
    {
        (int ix, double rx) = Cell(x, nx);
        (int iy, double ry) = Cell(y, ny);
        (int iz, double rz) = Cell(z, nz);
        int sz = outputs, sy = nz * outputs, sx = ny * nz * outputs;
        int c000 = origin + (ix * sx) + (iy * sy) + (iz * sz);
        int c100 = c000 + sx, c010 = c000 + sy, c001 = c000 + sz;
        int c110 = c100 + sy, c101 = c100 + sz, c011 = c010 + sz, c111 = c110 + sz;
        for (int o = 0; o < outputs; o++)
        {
            double v000 = t[c000 + o], v111 = t[c111 + o];
            double d1, d2, d3;
            if (rx >= ry && ry >= rz)
            {
                (d1, d2, d3) = (t[c100 + o] - v000, t[c110 + o] - t[c100 + o], v111 - t[c110 + o]);
            }
            else if (rx >= rz && rz >= ry)
            {
                (d1, d2, d3) = (t[c100 + o] - v000, v111 - t[c101 + o], t[c101 + o] - t[c100 + o]);
            }
            else if (rz >= rx && rx >= ry)
            {
                (d1, d2, d3) = (t[c101 + o] - t[c001 + o], v111 - t[c101 + o], t[c001 + o] - v000);
            }
            else if (ry >= rx && rx >= rz)
            {
                (d1, d2, d3) = (t[c110 + o] - t[c010 + o], t[c010 + o] - v000, v111 - t[c110 + o]);
            }
            else if (ry >= rz && rz >= rx)
            {
                (d1, d2, d3) = (v111 - t[c011 + o], t[c010 + o] - v000, t[c011 + o] - t[c010 + o]);
            }
            else
            {
                (d1, d2, d3) = (v111 - t[c011 + o], t[c011 + o] - t[c001 + o], t[c001 + o] - v000);
            }

            output[o] = v000 + (d1 * rx) + (d2 * ry) + (d3 * rz);
        }
    }
}
