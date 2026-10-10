using System.Buffers.Binary;

namespace Egami.Colors;

/// <summary>
/// One ICC curve (curv or para), evaluated as ICC.1 defines it: tables interpolate linearly over 0..1 with the input clamped,
/// parametric curves follow their formula, with 0 where the power's base is negative.
/// </summary>
internal sealed class IccCurve
{
    private static readonly int[] ParameterCounts = [1, 3, 4, 5, 7];

    private readonly float[]? _table;
    private readonly int _type;   // -1 identity, 0..4 para types (curv gamma is type 0), 5 table
    private readonly double _g, _a, _b, _c, _d, _e, _f;

    private IccCurve(int type, double g = 1, double a = 1, double b = 0, double c = 0, double d = 0, double e = 0, double f = 0, float[]? table = null)
    {
        (_type, _g, _a, _b, _c, _d, _e, _f, _table) = (type, g, a, b, c, d, e, f, table);
    }

    public static IccCurve Identity { get; } = new(-1);

    public bool IsIdentity => _type == -1;

    /// <summary>Gets the table, for a sampled curve.</summary>
    public ReadOnlySpan<float> Table => _table;

    public static IccCurve Gamma(double g) => g == 1 ? Identity : new IccCurve(0, g);

    public static IccCurve FromTable(float[] table) => new(5, table: table);

    /// <summary>Reads a curv or para element at the start of <paramref name="data"/>; returns the curve and its padded length.</summary>
    public static (IccCurve Curve, int Length) Read(ReadOnlySpan<byte> data, string where)
    {
        if (data.Length < 12)
        {
            throw IccModel.Corrupt(where, "a curve is cut short");
        }

        string type = IccData.Signature(data);
        if (type == "curv")
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            if (count > (data.Length - 12) / 2)
            {
                throw IccModel.Corrupt(where, $"a curve of {count} entries is longer than its tag");
            }

            int length = Pad4(12 + ((int)count * 2));
            if (count == 0)
            {
                return (Identity, length);
            }

            if (count == 1)
            {
                double g = BinaryPrimitives.ReadUInt16BigEndian(data[12..]) / 256.0;
                return g > 0 ? (Gamma(g), length) : throw IccModel.Corrupt(where, "a curve has gamma 0");
            }

            float[] table = new float[count];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(12 + (i * 2))..]) / 65535f;
            }

            return (FromTable(table), length);
        }

        if (type == "para")
        {
            int function = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
            if (function > 4)
            {
                throw IccModel.Corrupt(where, $"a parametric curve has function type {function}, beyond the five ICC defines");
            }

            int n = ParameterCounts[function];
            if (data.Length < 12 + (n * 4))
            {
                throw IccModel.Corrupt(where, "a parametric curve is cut short");
            }

            Span<double> p = stackalloc double[7];
            for (int i = 0; i < n; i++)
            {
                p[i] = IccModel.S15(data[(12 + (i * 4))..]);
            }

            if (p[0] <= 0)
            {
                throw IccModel.Corrupt(where, "a parametric curve has an exponent of 0 or less");
            }

            IccCurve curve = function switch
            {
                0 => Gamma(p[0]),
                1 => new IccCurve(1, p[0], p[1], p[2]),
                2 => new IccCurve(2, p[0], p[1], p[2], p[3]),
                3 => new IccCurve(3, p[0], p[1], p[2], p[3], p[4]),
                _ => new IccCurve(4, p[0], p[1], p[2], p[3], p[4], p[5], p[6]),
            };
            return (curve, Pad4(12 + (n * 4)));
        }

        throw IccModel.Corrupt(where, $"'{type}' is not a curve type (curv or para)");
    }

    public double Evaluate(double x)
    {
        switch (_type)
        {
            case -1:
                return x;
            case 0:
                return x < 0 ? (_g == 1 ? x : 0) : Math.Pow(x, _g);
            case 1:
                {
                    // Y = (aX + b)^g for X ≥ −b/a, else 0.
                    double t = (_a * x) + _b;
                    return Above(x, _a, _b) && t > 0 ? Math.Pow(t, _g) : 0;
                }

            case 2:
                {
                    // Y = (aX + b)^g + c for X ≥ −b/a, else c.
                    double t = (_a * x) + _b;
                    return Above(x, _a, _b) && t > 0 ? Math.Pow(t, _g) + _c : _c;
                }

            case 3:
                {
                    double t = (_a * x) + _b;
                    return x >= _d ? (t > 0 ? Math.Pow(t, _g) : 0) : _c * x;
                }

            case 4:
                {
                    double t = (_a * x) + _b;
                    return x >= _d ? (t > 0 ? Math.Pow(t, _g) : 0) + _e : (_c * x) + _f;
                }

            default:
                return Interpolate(_table!, x);
        }
    }

    /// <summary>
    /// Returns x where the curve gives <paramref name="y"/>: the analytic inverse of a parametric curve (0 where the power has no
    /// real root), and for a table the last segment that contains the value, as Little CMS reverses tables, with values outside
    /// the table's range clamped to its ends.
    /// </summary>
    public double EvaluateInverse(double y)
    {
        switch (_type)
        {
            case -1:
                return y;
            case 0:
                return y < 0 ? (_g == 1 ? y : 0) : Math.Pow(y, 1 / _g);
            case 1:
                return y < 0 || _a == 0 ? 0 : Math.Max(0, (Math.Pow(y, 1 / _g) - _b) / _a);
            case 2:
                return y - _c < 0 || _a == 0 ? 0 : Math.Max(0, (Math.Pow(y - _c, 1 / _g) - _b) / _a);
            case 3:
                {
                    double start = (_a * _d) + _b;
                    double knee = start < 0 ? 0 : Math.Pow(start, _g);
                    return y >= knee ? (_a == 0 ? 0 : (Math.Pow(Math.Max(y, 0), 1 / _g) - _b) / _a) : (_c == 0 ? 0 : y / _c);
                }

            case 4:
                {
                    double start = (_a * _d) + _b;
                    double knee = (start < 0 ? 0 : Math.Pow(start, _g)) + _e;
                    if (y >= knee)
                    {
                        double t = y - _e;
                        return t < 0 || _a == 0 ? 0 : (Math.Pow(t, 1 / _g) - _b) / _a;
                    }

                    return _c == 0 ? 0 : (y - _f) / _c;
                }

            default:
                return InvertTable(_table!, y);
        }
    }

    /// <summary>
    /// Returns the curve as a <see cref="TransferFunction"/> (for a matrix-shaper profile's <see cref="ColorProfile.Transfer"/>),
    /// or <see langword="null"/> when a table goes down somewhere.
    /// </summary>
    public TransferFunction? ToTransferFunction()
    {
        switch (_type)
        {
            case -1:
                return TransferFunction.Linear;
            case 0:
                return TransferFunction.Gamma(_g);
            case 1:
                return TransferFunction.Parametric(_g, _a, _b, 0, Start(_a, _b), 0, 0);
            case 2:
                return TransferFunction.Parametric(_g, _a, _b, 0, Start(_a, _b), _c, _c);
            case 3:
                return TransferFunction.Parametric(_g, _a, _b, _c, _d, 0, 0);
            case 4:
                return TransferFunction.Parametric(_g, _a, _b, _c, _d, _e, _f);
        }

        for (int i = 1; i < _table!.Length; i++)
        {
            if (_table[i] < _table[i - 1])
            {
                return null;
            }
        }

        return TransferFunction.Table(_table);
    }

    public bool SameAs(IccCurve other) => _type == other._type && _g == other._g && _a == other._a && _b == other._b && _c == other._c && _d == other._d
        && _e == other._e && _f == other._f && (_table is null ? other._table is null : other._table is not null && _table.AsSpan().SequenceEqual(other._table));

    internal static double Interpolate(ReadOnlySpan<float> table, double x)
    {
        if (!(x > 0))
        {
            return table[0];
        }

        if (x >= 1)
        {
            return table[^1];
        }

        double position = x * (table.Length - 1);
        int i = (int)position;
        double t = position - i;
        return table[i] + ((table[i + 1] - table[i]) * t);
    }

    private static double InvertTable(float[] table, double y)
    {
        int last = table.Length - 1;
        bool ascending = table[last] >= table[0];
        double low = Math.Min(table[0], table[last]), high = Math.Max(table[0], table[last]);
        if (y < low)
        {
            return ascending ? 0 : 1;
        }

        if (y >= high)
        {
            return ascending ? 1 : 0;
        }

        // Ascending: the last segment that holds y; descending: the first.
        for (int k = 0; k < last; k++)
        {
            int i = ascending ? last - 1 - k : k;
            double y0 = table[i], y1 = table[i + 1];
            if (y >= Math.Min(y0, y1) && y <= Math.Max(y0, y1))
            {
                return y0 == y1 ? (ascending ? i + 1.0 : i) / last : (i + ((y - y0) / (y1 - y0))) / last;
            }
        }

        return 0;
    }

    private static bool Above(double x, double a, double b) => a == 0 ? b > 0 : (a > 0 ? x >= -b / a : x <= -b / a);

    private static double Start(double a, double b) => a == 0 ? 0 : -b / a;

    private static int Pad4(int n) => (n + 3) & ~3;
}

/// <summary>A color lookup table: grid points per input, output channels, and the samples normalized to 0..1, first input slowest.</summary>
internal sealed class IccClut(int[] grid, int outputs, float[] values)
{
    public int[] Grid { get; } = grid;

    public int Outputs { get; } = outputs;

    public float[] Values { get; } = values;
}

/// <summary>One step of an ICC LUT: curves per channel, a 3×3 matrix with offsets, or a CLUT.</summary>
internal abstract record IccStage
{
    public sealed record Curves(IccCurve[] Channels) : IccStage;

    /// <summary>A row-major 3×3 matrix and three offsets (zero for lut8 and lut16).</summary>
    public sealed record Matrix(double[] Values, double[] Offsets) : IccStage;

    public sealed record Lookup(IccClut Table) : IccStage;
}

/// <summary>Which tag type an A2B or B2A tag was, which sets how its PCS values are encoded.</summary>
internal enum IccLutType
{
    Lut8,
    Lut16,
    AToB,
    BToA,
}

/// <summary>An A2B or B2A tag as stages from its input channels to its output channels, all on normalized 0..1 values.</summary>
internal sealed class IccLut(IccLutType type, int inputs, int outputs, IccStage[] stages)
{
    public IccLutType Type { get; } = type;

    public int Inputs { get; } = inputs;

    public int Outputs { get; } = outputs;

    public IccStage[] Stages { get; } = stages;
}

/// <summary>
/// The tags of an ICC profile that conversion uses, read and checked: a matrix-shaper (colorants and curves), a gray curve, or
/// A2B and B2A tables per intent, with the media white, chromatic adaptation and black point. Building one throws
/// <see cref="CorruptDataException"/> for a broken tag that is needed and <see cref="UnsupportedFormatException"/> for profiles
/// Egami does not convert with.
/// </summary>
internal sealed class IccModel
{
    private static readonly Vector3d D50 = new(0.9642, 1.0, 0.8249);

    private IccModel(IccData data)
    {
        Data = data;
    }

    public IccData Data { get; }

    public ColorModel Model { get; private set; }

    public int Channels => Model switch
    {
        ColorModel.Rgb => 3,
        ColorModel.Gray => 1,
        _ => 4,
    };

    public bool PcsIsLab { get; private set; }

    /// <summary>Gets the media white (wtpt), or the D50 PCS white when the tag is missing.</summary>
    public Vector3d MediaWhite { get; private set; } = D50;

    /// <summary>Gets the chad tag's adaptation from the adopted white to D50, if the profile has one.</summary>
    public Matrix3d? Adaptation { get; private set; }

    /// <summary>Gets the RGB colorants as columns (rXYZ, gXYZ, bXYZ), for a matrix-shaper RGB profile.</summary>
    public Matrix3d? Colorants { get; private set; }

    /// <summary>Gets the tone curves: three for a matrix-shaper RGB profile, one for gray (kTRC), or null.</summary>
    public IccCurve[]? Curves { get; private set; }

    /// <summary>Gets the A2B0, A2B1 and A2B2 tables; null where the tag is missing.</summary>
    public IccLut?[] DeviceToPcs { get; } = new IccLut?[3];

    /// <summary>Gets the B2A0, B2A1 and B2A2 tables; null where the tag is missing.</summary>
    public IccLut?[] PcsToDevice { get; } = new IccLut?[3];

    /// <summary>Gets whether the profile converts through its matrix and curves (or gray curve) rather than A2B tables.</summary>
    public bool IsMatrixShaper => Curves is not null && DeviceToPcs[0] is null;

    public static IccModel Read(IccData data)
    {
        IccModel model = new(data);
        model.ReadTags();
        return model;
    }

    internal static CorruptDataException Corrupt(string where, string problem) => new("ICC", 0, $"{where}: {problem}.");

    internal static double S15(ReadOnlySpan<byte> d) => BinaryPrimitives.ReadInt32BigEndian(d) / 65536.0;

    private void ReadTags()
    {
        if (Data.Model is not ColorModel model)
        {
            throw new UnsupportedFormatException($"ICC profiles with data space '{Data.DataSpace}' are not supported; Egami converts RGB, gray and CMYK.");
        }

        if (Data.ProfileClass is not (IccProfileClass.Input or IccProfileClass.Display or IccProfileClass.Output or IccProfileClass.ColorSpace))
        {
            throw new UnsupportedFormatException($"ICC profiles of class {Data.ProfileClass} are not supported; Egami converts with input, display, output and color space profiles.");
        }

        if (Data.ConnectionSpace is not ("XYZ" or "Lab"))
        {
            throw Corrupt("header", $"the connection space is '{Data.ConnectionSpace}', not XYZ or Lab");
        }

        Model = model;
        PcsIsLab = Data.ConnectionSpace == "Lab";
        if (Data.TryGetTag("wtpt", out IccTag wtpt))
        {
            MediaWhite = Xyz(wtpt);
        }

        if (Data.TryGetTag("chad", out IccTag chad))
        {
            Adaptation = Chad(chad);
        }

        for (int intent = 0; intent < 3; intent++)
        {
            DeviceToPcs[intent] = Lut($"A2B{intent}", Channels, 3);
            PcsToDevice[intent] = Lut($"B2A{intent}", 3, Channels);
        }

        if (model == ColorModel.Rgb && Data.TryGetTag("rXYZ", out IccTag r) && Data.TryGetTag("gXYZ", out IccTag g) && Data.TryGetTag("bXYZ", out IccTag b)
            && Data.TryGetTag("rTRC", out IccTag rTrc) && Data.TryGetTag("gTRC", out IccTag gTrc) && Data.TryGetTag("bTRC", out IccTag bTrc))
        {
            Colorants = Matrix3d.FromColumns(Xyz(r), Xyz(g), Xyz(b));
            Curves = [Curve(rTrc), Curve(gTrc), Curve(bTrc)];
        }
        else if (model == ColorModel.Gray && Data.TryGetTag("kTRC", out IccTag kTrc))
        {
            Curves = [Curve(kTrc)];
        }

        if (Curves is null && DeviceToPcs[0] is null)
        {
            throw Corrupt("tags", model switch
            {
                ColorModel.Rgb => "an RGB profile needs either rXYZ, gXYZ, bXYZ with rTRC, gTRC, bTRC, or an A2B0 table",
                ColorModel.Gray => "a gray profile needs either kTRC or an A2B0 table",
                _ => "a CMYK profile needs an A2B0 table",
            });
        }
    }

    private static Vector3d Xyz(IccTag tag)
    {
        ReadOnlySpan<byte> d = tag.Data.Span;
        if (tag.Type != "XYZ" || d.Length < 20)
        {
            throw Corrupt(tag.Signature, "not an XYZType of 20 bytes");
        }

        Vector3d v = new(S15(d[8..]), S15(d[12..]), S15(d[16..]));
        return double.IsFinite(v.X) ? v : throw Corrupt(tag.Signature, "not a finite XYZ value");
    }

    private static Matrix3d Chad(IccTag tag)
    {
        ReadOnlySpan<byte> d = tag.Data.Span;
        if (tag.Type != "sf32" || d.Length < 44)
        {
            throw Corrupt(tag.Signature, "not an s15Fixed16ArrayType of nine values");
        }

        return new Matrix3d(S15(d[8..]), S15(d[12..]), S15(d[16..]), S15(d[20..]), S15(d[24..]), S15(d[28..]), S15(d[32..]), S15(d[36..]), S15(d[40..]));
    }

    private static IccCurve Curve(IccTag tag) => IccCurve.Read(tag.Data.Span, tag.Signature).Curve;

    private IccLut? Lut(string signature, int inputs, int outputs)
    {
        if (!Data.TryGetTag(signature, out IccTag tag))
        {
            return null;
        }

        ReadOnlySpan<byte> d = tag.Data.Span;
        if (d.Length < 12)
        {
            throw Corrupt(signature, "the tag is cut short");
        }

        if (d[8] != inputs || d[9] != outputs)
        {
            throw Corrupt(signature, $"the table has {d[8]} inputs and {d[9]} outputs, but this profile needs {inputs} and {outputs}");
        }

        return tag.Type switch
        {
            "mft1" => LutClassic(d, signature, inputs, outputs, false),
            "mft2" => LutClassic(d, signature, inputs, outputs, true),
            "mAB" => LutAB(d, signature, inputs, outputs, true),
            "mBA" => LutAB(d, signature, inputs, outputs, false),
            _ => throw Corrupt(signature, $"'{tag.Type}' is not a table type (mft1, mft2, mAB or mBA)"),
        };
    }

    // lut8Type and lut16Type: matrix (applied to three-channel input), input tables, CLUT, output tables.
    private static IccLut LutClassic(ReadOnlySpan<byte> d, string where, int inputs, int outputs, bool wide)
    {
        int header = wide ? 52 : 48;
        if (d.Length < header)
        {
            throw Corrupt(where, "the table header is cut short");
        }

        int points = d[10];
        if (points < 2)
        {
            throw Corrupt(where, $"the CLUT has {points} grid points per input; at least 2 are needed");
        }

        int inEntries = wide ? BinaryPrimitives.ReadUInt16BigEndian(d[48..]) : 256;
        int outEntries = wide ? BinaryPrimitives.ReadUInt16BigEndian(d[50..]) : 256;
        if (inEntries < 2 || outEntries < 2)
        {
            throw Corrupt(where, "a table has fewer than 2 entries");
        }

        int sample = wide ? 2 : 1;
        long cells = (long)Math.Pow(points, inputs) * outputs;
        long needed = header + (((long)inEntries * inputs) + cells + ((long)outEntries * outputs)) * sample;
        if (needed > d.Length)
        {
            throw Corrupt(where, $"the table needs {needed} bytes but the tag has {d.Length}");
        }

        List<IccStage> stages = [];
        double[] m = new double[9];
        for (int i = 0; i < 9; i++)
        {
            m[i] = S15(d[(12 + (i * 4))..]);
        }

        if (inputs == 3 && !(m[0] == 1 && m[1] == 0 && m[2] == 0 && m[3] == 0 && m[4] == 1 && m[5] == 0 && m[6] == 0 && m[7] == 0 && m[8] == 1))
        {
            stages.Add(new IccStage.Matrix(m, [0, 0, 0]));
        }

        int at = header;
        stages.Add(new IccStage.Curves(Tables(d, ref at, inputs, inEntries, wide)));
        float[] values = new float[cells];
        for (int i = 0; i < values.Length; i++, at += sample)
        {
            values[i] = wide ? BinaryPrimitives.ReadUInt16BigEndian(d[at..]) / 65535f : d[at] / 255f;
        }

        stages.Add(new IccStage.Lookup(new IccClut([.. Enumerable.Repeat(points, inputs)], outputs, values)));
        stages.Add(new IccStage.Curves(Tables(d, ref at, outputs, outEntries, wide)));
        return new IccLut(wide ? IccLutType.Lut16 : IccLutType.Lut8, inputs, outputs, [.. stages]);
    }

    private static IccCurve[] Tables(ReadOnlySpan<byte> d, ref int at, int channels, int entries, bool wide)
    {
        IccCurve[] curves = new IccCurve[channels];
        for (int c = 0; c < channels; c++)
        {
            float[] table = new float[entries];
            bool identity = true;
            for (int i = 0; i < entries; i++, at += wide ? 2 : 1)
            {
                table[i] = wide ? BinaryPrimitives.ReadUInt16BigEndian(d[at..]) / 65535f : d[at] / 255f;
                identity &= Math.Abs(table[i] - ((float)i / (entries - 1))) < 1e-6f;
            }

            curves[c] = identity ? IccCurve.Identity : IccCurve.FromTable(table);
        }

        return curves;
    }

    // lutAtoBType: A curves, CLUT, M curves, matrix, B curves. lutBtoAType: B curves, matrix, M curves, CLUT, A curves. B curves
    // are required; A curves come with a CLUT, M curves with a matrix, and a matrix only where its side has three channels.
    private static IccLut LutAB(ReadOnlySpan<byte> d, string where, int inputs, int outputs, bool aToB)
    {
        if (d.Length < 32)
        {
            throw Corrupt(where, "the table header is cut short");
        }

        uint offB = BinaryPrimitives.ReadUInt32BigEndian(d[12..]), offMatrix = BinaryPrimitives.ReadUInt32BigEndian(d[16..]);
        uint offM = BinaryPrimitives.ReadUInt32BigEndian(d[20..]), offClut = BinaryPrimitives.ReadUInt32BigEndian(d[24..]);
        uint offA = BinaryPrimitives.ReadUInt32BigEndian(d[28..]);
        if (offB == 0)
        {
            throw Corrupt(where, "the table has no B curves");
        }

        if ((offA == 0) != (offClut == 0))
        {
            throw Corrupt(where, "A curves and a CLUT must come together");
        }

        // The matrix and M curves sit on the PCS side: outputs for A to B, inputs for B to A.
        int pcsSide = aToB ? outputs : inputs;
        int deviceSide = aToB ? inputs : outputs;
        if ((offM == 0) != (offMatrix == 0) || (offMatrix != 0 && pcsSide != 3))
        {
            throw Corrupt(where, "M curves and a matrix must come together, on three channels");
        }

        if (offClut == 0 && inputs != outputs)
        {
            throw Corrupt(where, "a table without a CLUT must have as many inputs as outputs");
        }

        IccCurve[]? a = offA == 0 ? null : CurveSet(d, offA, deviceSide, where);
        IccClut? clut = offClut == 0 ? null : Clut(d, offClut, inputs, outputs, where);
        IccCurve[]? m = offM == 0 ? null : CurveSet(d, offM, pcsSide, where);
        IccStage.Matrix? matrix = offMatrix == 0 ? null : MatrixAt(d, offMatrix, where);
        IccCurve[] b = CurveSet(d, offB, pcsSide, where);

        List<IccStage> stages = [];
        if (aToB)
        {
            Add(stages, a);
            if (clut is not null)
            {
                stages.Add(new IccStage.Lookup(clut));
            }

            Add(stages, m);
            if (matrix is not null)
            {
                stages.Add(matrix);
            }

            Add(stages, b);
        }
        else
        {
            Add(stages, b);
            if (matrix is not null)
            {
                stages.Add(matrix);
            }

            Add(stages, m);
            if (clut is not null)
            {
                stages.Add(new IccStage.Lookup(clut));
            }

            Add(stages, a);
        }

        return new IccLut(aToB ? IccLutType.AToB : IccLutType.BToA, inputs, outputs, [.. stages]);

        static void Add(List<IccStage> stages, IccCurve[]? curves)
        {
            if (curves is not null && !curves.All(c => c.IsIdentity))
            {
                stages.Add(new IccStage.Curves(curves));
            }
        }
    }

    private static IccCurve[] CurveSet(ReadOnlySpan<byte> d, uint offset, int count, string where)
    {
        if (offset >= d.Length)
        {
            throw Corrupt(where, "a curve offset is outside the tag");
        }

        IccCurve[] curves = new IccCurve[count];
        int at = (int)offset;
        for (int i = 0; i < count; i++)
        {
            if (at >= d.Length)
            {
                throw Corrupt(where, "the curves run past the tag");
            }

            (curves[i], int length) = IccCurve.Read(d[at..], where);
            at += length;
        }

        return curves;
    }

    private static IccStage.Matrix MatrixAt(ReadOnlySpan<byte> d, uint offset, string where)
    {
        if (offset > d.Length - 48)
        {
            throw Corrupt(where, "the matrix is outside the tag");
        }

        ReadOnlySpan<byte> m = d[(int)offset..];
        double[] values = new double[9];
        double[] offsets = new double[3];
        for (int i = 0; i < 9; i++)
        {
            values[i] = S15(m[(i * 4)..]);
        }

        for (int i = 0; i < 3; i++)
        {
            offsets[i] = S15(m[(36 + (i * 4))..]);
        }

        return new IccStage.Matrix(values, offsets);
    }

    private static IccClut Clut(ReadOnlySpan<byte> d, uint offset, int inputs, int outputs, string where)
    {
        if (offset > d.Length - 20)
        {
            throw Corrupt(where, "the CLUT is outside the tag");
        }

        ReadOnlySpan<byte> c = d[(int)offset..];
        int[] grid = new int[inputs];
        long cells = outputs;
        for (int i = 0; i < inputs; i++)
        {
            grid[i] = c[i];
            if (grid[i] < 2)
            {
                throw Corrupt(where, $"the CLUT has {grid[i]} grid points on input {i + 1}; at least 2 are needed");
            }

            cells *= grid[i];
        }

        int precision = c[16];
        if (precision is not (1 or 2))
        {
            throw Corrupt(where, $"the CLUT precision is {precision}, not 1 or 2 bytes");
        }

        if (20 + (cells * precision) > c.Length)
        {
            throw Corrupt(where, $"the CLUT needs {20 + (cells * precision)} bytes but the tag has {c.Length} after its offset");
        }

        float[] values = new float[cells];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = precision == 2 ? BinaryPrimitives.ReadUInt16BigEndian(c[(20 + (i * 2))..]) / 65535f : c[20 + i] / 255f;
        }

        return new IccClut(grid, outputs, values);
    }
}
