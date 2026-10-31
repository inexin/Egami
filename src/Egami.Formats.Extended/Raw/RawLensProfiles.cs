using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Lens profiles read at run time from files the user supplies: Lensfun's XML database files and Adobe's LCP lens profiles. Egami
/// ships no profile data (Lensfun's database is CC BY-SA, Adobe's profiles are Adobe's); <see cref="RawReaderOptions.LensProfiles"/>
/// names the folders, and a system Lensfun install is searched when there is one. The formats come from their published
/// documentation: Lensfun's database format pages and its model descriptions, and the XMP layout of LCP files, whose camera model
/// is the one Lensfun documents as "ACM" (distances in units of the focal length).
/// </summary>
internal static class RawLensProfiles
{
    /// <summary>Folders a system Lensfun install keeps its database in, searched after the user's.</summary>
    private static readonly string[] SystemFolders =
    [
        "/usr/share/lensfun/version_1", "/usr/local/share/lensfun/version_1", "/usr/share/lensfun", "/usr/local/share/lensfun",
        "/opt/homebrew/share/lensfun/version_1",
    ];

    /// <summary>Gets whether a system Lensfun install is there to search.</summary>
    public static bool HasSystemProfiles { get; } = SystemFolders.Any(Directory.Exists);

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, List<RawLensProfile> Profiles)> Cache = new(StringComparer.Ordinal);

    /// <summary>Returns the profiles of the given folders and of a system Lensfun install, read once per folder and file time.</summary>
    public static List<RawLensProfile> Load(IReadOnlyList<string> folders, RawWarnings? warnings)
    {
        List<RawLensProfile> all = [];
        foreach (string folder in folders.Concat(SystemFolders.Where(Directory.Exists)))
        {
            if (!Directory.Exists(folder))
            {
                warnings?.Add("LensProfilesMissing", $"The lens profile folder {folder} does not exist.");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToUpperInvariant();
                if (ext is not (".XML" or ".LCP"))
                {
                    continue;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(file);
                if (!Cache.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                {
                    List<RawLensProfile> profiles;
                    try
                    {
                        XDocument xml = XDocument.Load(file, LoadOptions.None);
                        profiles = ext == ".LCP" ? Lcp(xml) : Lensfun(xml);
                    }
                    catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or FormatException)
                    {
                        warnings?.Add("LensProfileDamaged", $"The lens profile file {file} could not be read ({e.Message}).");
                        profiles = [];
                    }

                    cached = (stamp, profiles);
                    Cache[file] = cached;
                }

                all.AddRange(cached.Profiles);
            }
        }

        return all;
    }

    /// <summary>
    /// Returns the profile that fits a camera and lens: the lens model's words must all be in the file's lens name (case and
    /// order ignored), as Lensfun matches names; among those, one for the same camera maker first.
    /// </summary>
    public static RawLensProfile? Match(List<RawLensProfile> profiles, string make, string model, string? lens)
    {
        if (string.IsNullOrWhiteSpace(lens))
        {
            return null;
        }

        string[] words = Words(lens);
        RawLensProfile? best = null;
        int bestScore = -1;
        foreach (RawLensProfile p in profiles)
        {
            string[] candidate = Words(p.Lens);
            if (candidate.Length == 0 || !candidate.All(w => words.Contains(w, StringComparer.Ordinal)))
            {
                continue;
            }

            int score = candidate.Length * 4;
            if (p.CameraMake is string m && string.Equals(m.Trim(), make.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (p.CameraModel is string c && string.Equals(c.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }

            if (score > bestScore)
            {
                best = p;
                bestScore = score;
            }
        }

        return best;
    }

    private static string[] Words(string s) => s.ToUpperInvariant().Split([' ', ',', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Reads a Lensfun database file: each <c>lens</c> with its maker, model, crop factor, aspect ratio and calibration.</summary>
    internal static List<RawLensProfile> Lensfun(XDocument xml)
    {
        List<RawLensProfile> list = [];
        foreach (XElement lens in xml.Descendants("lens"))
        {
            string? model = lens.Elements("model").FirstOrDefault(m => m.Attribute("lang") is null)?.Value ?? lens.Element("model")?.Value;
            if (model is null)
            {
                continue;
            }

            RawLensProfile profile = new(RawLensFormat.Lensfun, lens.Element("maker")?.Value.Trim() ?? "", model.Trim())
            {
                CropFactor = Number(lens.Element("cropfactor")?.Value) ?? 1,
                AspectRatio = Ratio(lens.Element("aspect-ratio")?.Value) ?? 1.5,
                Fisheye = lens.Element("type")?.Value.Contains("fisheye", StringComparison.OrdinalIgnoreCase) == true,
                CenterX = Number(lens.Element("center")?.Attribute("x")?.Value) ?? 0,
                CenterY = Number(lens.Element("center")?.Attribute("y")?.Value) ?? 0,
            };
            foreach (XElement e in lens.Element("calibration")?.Elements() ?? [])
            {
                double focal = Number(e.Attribute("focal")?.Value) ?? 0;
                string kind = e.Attribute("model")?.Value ?? "none";
                double A(string name, double fallback = 0) => Number(e.Attribute(name)?.Value) ?? fallback;
                switch (e.Name.LocalName)
                {
                    case "distortion" when kind is "poly3" or "poly5" or "ptlens" or "acm":
                        profile.Distortion.Add(new RawLensSample(focal, 0, 0, kind, kind switch
                        {
                            "poly3" => [A("k1")],
                            "poly5" => [A("k1"), A("k2")],
                            "ptlens" => [A("a"), A("b"), A("c")],
                            _ => [A("k1"), A("k2"), A("k3"), A("k4"), A("k5")],
                        }));
                        break;
                    case "tca" when kind is "linear" or "poly3":
                        profile.Tca.Add(new RawLensSample(focal, 0, 0, kind, kind == "linear"
                            ? [A("kr", 1), A("kb", 1)]
                            : [A("vr", 1), A("cr"), A("br"), A("vb", 1), A("cb"), A("bb")]));
                        break;
                    case "vignetting" when kind is "pa" or "acm":
                        profile.Vignetting.Add(new RawLensSample(focal, A("aperture"), A("distance", 1000), kind, [A("k1"), A("k2"), A("k3")]));
                        break;
                }
            }

            if (profile.Distortion.Count + profile.Tca.Count + profile.Vignetting.Count > 0)
            {
                list.Add(profile);
            }
        }

        return list;
    }

    /// <summary>
    /// Reads an Adobe LCP file: XMP whose <c>stCamera</c> entries each hold a focal length, aperture and focus distance, and a
    /// PerspectiveModel (or FisheyeModel) with its distortion, a VignetteModel and the red-green and blue-green chromatic models.
    /// Values may be attributes or elements, as XMP allows.
    /// </summary>
    internal static List<RawLensProfile> Lcp(XDocument xml)
    {
        List<RawLensProfile> list = [];
        Dictionary<string, RawLensProfile> byLens = new(StringComparer.Ordinal);
        // Every calibration entry names its lens; a manual-focus lens's entries may give no focal length (it is then fixed).
        foreach (XElement entry in xml.Descendants().Where(e => e.Name.LocalName == "li" && (Value(e, "LensPrettyName") ?? Value(e, "Lens")) is not null))
        {
            string lens = Value(entry, "LensPrettyName") ?? Value(entry, "Lens") ?? "";
            if (lens.Length == 0)
            {
                continue;
            }

            if (!byLens.TryGetValue(lens, out RawLensProfile? profile))
            {
                profile = new RawLensProfile(RawLensFormat.Lcp, "", lens)
                {
                    CameraMake = Value(entry, "Make"),
                    CameraModel = Value(entry, "Model"),
                    CropFactor = Number(Value(entry, "SensorFormatFactor")) ?? 1,
                };
                byLens[lens] = profile;
                list.Add(profile);
            }

            double focal = Number(Value(entry, "FocalLength")) ?? 0;
            double aperture = Number(Value(entry, "ApertureValue")) is double apex ? Math.Pow(2, apex / 2) : 0;
            double distance = Number(Value(entry, "FocusDistance")) ?? 1000;
            XElement? perspective = Child(entry, "PerspectiveModel");
            XElement? fisheye = Child(entry, "FisheyeModel");
            XElement? model = perspective ?? fisheye;
            if (model is null)
            {
                continue;
            }

            double M(XElement e, string name, double fallback = 0) => Number(Value(e, name)) ?? fallback;
            double[] geometry = [M(model, "FocalLengthX", 1), M(model, "FocalLengthY", 1), M(model, "ImageXCenter", 0.5), M(model, "ImageYCenter", 0.5)];
            profile.Fisheye = fisheye is not null && perspective is null;
            if (fisheye is not null && perspective is null)
            {
                profile.Distortion.Add(new RawLensSample(focal, aperture, distance, "lcp-fisheye", [.. geometry, M(model, "RadialDistortParam1"), M(model, "RadialDistortParam2")]));
            }
            else
            {
                profile.Distortion.Add(new RawLensSample(focal, aperture, distance, "lcp", [.. geometry,
                    M(model, "RadialDistortParam1"), M(model, "RadialDistortParam2"), M(model, "RadialDistortParam3"),
                    M(model, "TangentialDistortParam1"), M(model, "TangentialDistortParam2"), M(model, "ScaleFactor", 1)]));
            }

            if (Child(model, "VignetteModel") is XElement vignette)
            {
                profile.Vignetting.Add(new RawLensSample(focal, aperture, distance, "lcp", [.. geometry,
                    M(vignette, "VignetteModelParam1"), M(vignette, "VignetteModelParam2"), M(vignette, "VignetteModelParam3")]));
            }

            XElement? red = Child(model, "ChromaticRedGreenModel");
            XElement? blue = Child(model, "ChromaticBlueGreenModel");
            if (red is not null || blue is not null)
            {
                double[] Ca(XElement? e) => e is null ? [1, 0, 0, 0] : [M(e, "ScaleFactor", 1), M(e, "RadialDistortParam1"), M(e, "RadialDistortParam2"), M(e, "RadialDistortParam3")];
                profile.Tca.Add(new RawLensSample(focal, aperture, distance, "lcp", [.. geometry, .. Ca(red), .. Ca(blue)]));
            }
        }

        // A lens whose entries had no model the reader knows is no profile: matching it would correct nothing.
        list.RemoveAll(p => p.Distortion.Count == 0 && p.Vignetting.Count == 0 && p.Tca.Count == 0);
        return list;
    }

    /// <summary>
    /// Finds a struct-valued child, directly or inside an rdf:Description wrapping <paramref name="e"/>'s values (Adobe's own
    /// files put each calibration's values in one), and returns the element holding its values.
    /// </summary>
    private static XElement? Child(XElement e, string name)
    {
        XElement? child = e.Elements().FirstOrDefault(c => c.Name.LocalName == name)
            ?? e.Elements().FirstOrDefault(d => d.Name.LocalName == "Description")?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
        return child?.Let(c => c.Elements().FirstOrDefault(d => d.Name.LocalName == "Description") ?? c);
    }

    private static string? Value(XElement e, string name)
    {
        XAttribute? a = e.Attributes().FirstOrDefault(x => x.Name.LocalName == name);
        if (a is not null)
        {
            return a.Value;
        }

        XElement? child = e.Elements().FirstOrDefault(x => x.Name.LocalName == name && !x.HasElements);
        if (child is not null)
        {
            return child.Value;
        }

        // An rdf:Description wrapping the values.
        XElement? description = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Description");
        return description is null ? null : Value(description, name);
    }

    internal static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

    private static double? Ratio(string? s)
    {
        if (s is null)
        {
            return null;
        }

        int colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && Number(s[..colon]) is double a && Number(s[(colon + 1)..]) is double b && b > 0)
        {
            return a / b;
        }

        return Number(s);
    }

    private static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}

/// <summary>Where a lens profile came from.</summary>
internal enum RawLensFormat
{
    Lensfun,
    Lcp,
}

/// <summary>One calibration of a lens: at a focal length (and for vignetting an aperture and distance), a model and its parameters.</summary>
internal sealed record RawLensSample(double Focal, double Aperture, double Distance, string Model, double[] Parameters);

/// <summary>A lens's profile: its calibrations and the sensor they were made on.</summary>
internal sealed class RawLensProfile(RawLensFormat format, string maker, string lens)
{
    public RawLensFormat Format { get; } = format;

    public string Maker { get; } = maker;

    public string Lens { get; } = lens;

    public string? CameraMake { get; init; }

    public string? CameraModel { get; init; }

    public double CropFactor { get; init; } = 1;

    public double AspectRatio { get; init; } = 1.5;

    public bool Fisheye { get; set; }

    public double CenterX { get; init; }

    public double CenterY { get; init; }

    public List<RawLensSample> Distortion { get; } = [];

    public List<RawLensSample> Tca { get; } = [];

    public List<RawLensSample> Vignetting { get; } = [];
}
