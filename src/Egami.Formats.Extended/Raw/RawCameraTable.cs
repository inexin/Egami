using System.Globalization;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// The camera colour table: per camera, the colour matrices Adobe DNG Converter writes for it, measured as a black box by
/// tools/make-raw-camera-table.py and embedded as RawCameras.txt (decision Q6). Maker formats, which carry no colour matrices of
/// their own, take theirs from here by the file's Make and Model; a DNG without a ColorMatrix1 does too.
/// </summary>
internal static class RawCameraTable
{
    private static readonly Lazy<Dictionary<string, RawCameraEntry>> Cameras = new(() => Parse(Load()));

    /// <summary>Gets the number of cameras in the table.</summary>
    public static int Count => Cameras.Value.Count;

    /// <summary>Returns the camera with this Make and Model (case and surrounding spaces ignored), or <see langword="null"/>.</summary>
    public static RawCameraEntry? Find(string make, string model)
    {
        return Cameras.Value.TryGetValue(Key(make, model), out RawCameraEntry? camera) ? camera : null;
    }

    /// <summary>Parses the table's lines; comment lines start with <c>#</c>, and a line that does not parse is skipped.</summary>
    internal static Dictionary<string, RawCameraEntry> Parse(string text)
    {
        Dictionary<string, RawCameraEntry> cameras = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 10 || Matrix(f[4]) is not double[] cm1)
            {
                continue;
            }

            RawProfile profile = new()
            {
                Name = f[2].Length > 0 ? f[2] : null,
                Illuminant1 = Int(f[3]),
                ColorMatrix1 = cm1,
                Illuminant2 = Int(f[5]),
                ColorMatrix2 = Matrix(f[6]),
                ForwardMatrix1 = Matrix(f[7]),
                ForwardMatrix2 = Matrix(f[8]),
                BaselineExposure = double.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) ? b : 0,
            };
            RawCameraEntry camera = new(profile);
            if (f.Length >= 16)
            {
                int[]? size = Ints(f[10]);
                camera = camera with
                {
                    Width = size is { Length: 2 } ? size[0] : 0,
                    Height = size is { Length: 2 } ? size[1] : 0,
                    ActiveArea = Ints(f[11]) is { Length: 4 } a ? new Rect(a[1], a[0], a[3] - a[1], a[2] - a[0]) : null,
                    DefaultCrop = Ints(f[12]) is { Length: 4 } c ? new Rect(c[0], c[1], c[2], c[3]) : null,
                    BlackRepeat = Ints(f[13]) is { Length: 2 } r ? (r[0], r[1]) : (1, 1),
                    Black = Numbers(f[14]) ?? [0],
                    White = Int(f[15]),
                    ExposureBias = f.Length >= 17 && Numbers(f[16]) is { Length: 1 } bias ? bias[0] : null,
                };
            }

            cameras.TryAdd(Key(f[0], f[1]), camera);
        }

        return cameras;
    }

    private static string Load()
    {
        using Stream? stream = typeof(RawCameraTable).Assembly.GetManifestResourceStream("Egami.Formats.Extended.Raw.RawCameras.txt");
        if (stream is null)
        {
            return "";
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string Key(string make, string model) => $"{make.Trim().ToUpperInvariant()}\u0001{model.Trim().ToUpperInvariant()}";

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static int[]? Ints(string s) => Numbers(s) is double[] v ? [.. v.Select(x => (int)Math.Round(x))] : null;

    private static double[]? Numbers(string s)
    {
        if (s == "-")
        {
            return null;
        }

        string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double[] values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values.Length > 0 ? values : null;
    }

    private static double[]? Matrix(string s)
    {
        if (s == "-")
        {
            return null;
        }

        string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 9)
        {
            return null;
        }

        double[] m = new double[9];
        for (int i = 0; i < 9; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out m[i]))
            {
                return null;
            }
        }

        return m;
    }
}

/// <summary>
/// One camera of the table: its colour profile and, as Adobe DNG Converter measured them, the sensor size, active area, default
/// crop, black levels (in the order DNG's BlackLevel gives them over the repeat pattern) and white level. A maker format uses the
/// levels and geometry only where its own file gives none.
/// </summary>
internal sealed record RawCameraEntry(RawProfile Profile)
{
    public int Width { get; init; }

    public int Height { get; init; }

    public Rect? ActiveArea { get; init; }

    public Rect? DefaultCrop { get; init; }

    public (int Rows, int Columns) BlackRepeat { get; init; } = (1, 1);

    public double[] Black { get; init; } = [0];

    public int White { get; init; }

    /// <summary>Gets, for a RAF camera, the exposure bias of the file the profile's baseline exposure was measured on, or <see langword="null"/>.</summary>
    public double? ExposureBias { get; init; }
}
