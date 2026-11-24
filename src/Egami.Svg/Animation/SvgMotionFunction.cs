using System.Globalization;
using System.Numerics;

using Egami.Drawing;
using Egami.Svg.Reading;
using Egami.Svg.Values;

namespace Egami.Svg.Animation;

/// <summary>
/// The motion of an animateMotion at a point of its simple duration (SVG Animations Level 2, "The animateMotion element"): a
/// point on its motion path and the rotation there, as the supplemental transform that moves the target. The path comes from an
/// mpath child, else the path attribute, else values, else from, to and by as coordinate pairs. calcMode is paced by default;
/// keyPoints with keyTimes place the target along the path; rotate is a fixed angle, auto or auto-reverse.
/// </summary>
internal sealed class SvgMotionFunction
{
    /// <summary>The most path segments whose ends linear timing gives equal time to; longer paths are timed by distance.</summary>
    private const int MaxTimedSegments = 256;

    private readonly PathMeasure _measure;
    private readonly double[] _contourStarts;
    private readonly double[] _vertices;
    private readonly double[]? _keyTimes;
    private readonly double[]? _keyPoints;
    private readonly double[][]? _keySplines;

    private SvgMotionFunction(PathMeasure measure, double[] vertices, SvgCalcMode mode, double[]? keyTimes, double[]? keyPoints, double[][]? keySplines)
    {
        _measure = measure;
        _vertices = vertices;
        _keyTimes = keyTimes;
        _keyPoints = keyPoints;
        _keySplines = keySplines;
        Mode = mode;
        _contourStarts = new double[measure.Contours.Count + 1];
        for (int i = 0; i < measure.Contours.Count; i++)
        {
            _contourStarts[i + 1] = _contourStarts[i] + measure.Contours[i].Length;
        }
    }

    /// <summary>Gets the calcMode in effect.</summary>
    public SvgCalcMode Mode { get; }

    /// <summary>Gets a value indicating whether the motion adds to the motion below it (additive="sum", or a lone by).</summary>
    public bool Additive { get; private init; }

    /// <summary>Gets a value indicating whether each repetition starts where the last ended (accumulate="sum").</summary>
    public bool Accumulates { get; private init; }

    /// <summary>Gets the fixed rotation in degrees, or NaN for auto and auto-reverse.</summary>
    public double Angle { get; private init; }

    /// <summary>Gets a value indicating whether rotate is auto-reverse: the direction of travel turned half a turn.</summary>
    public bool Reverse { get; private init; }

    /// <summary>Reads an animateMotion's path and timing; null (reported) when it is in error, so the animation has no effect.</summary>
    public static SvgMotionFunction? Create(SvgAnimation animation, SvgDocument document, SvgReport report)
    {
        SvgElement e = animation.Element;
        SvgAttributeCollection attributes = e.Attributes;
        bool by = false;
        Path? path = null;
        double[]? vertices = null;
        string? source = null;

        if (MotionPath(e, document) is string mpath)
        {
            source = mpath;
        }
        else if (attributes["path"] is string pathText)
        {
            source = pathText;
        }

        if (source is not null)
        {
            if (!Path.TryParseSvg(source, out path))
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s motion path \"{Resolving.SvgGeometry.Clip(source)}\" has an error; the motion runs up to it.", e);
                path = Path.ParseSvg(source, lenient: true);
            }
        }
        else
        {
            List<Vector2>? points = null;
            if (attributes["values"] is string valuesText)
            {
                points = Points(valuesText.Split(';'));
            }
            else if (attributes["from"] is string from && (attributes["to"] ?? attributes["by"]) is string toOrBy)
            {
                points = Points([from, toOrBy]);
                if (points is not null && attributes["to"] is null)
                {
                    by = true;
                    points[1] += points[0];
                }
            }
            else if (attributes["to"] is string to)
            {
                // A to motion starts from the motion below it, which is none: the target's own place.
                points = Points(["0,0", to]);
            }
            else if (attributes["by"] is string onlyBy)
            {
                points = Points(["0,0", onlyBy]);
                by = true;
            }

            if (points is null)
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name} has no motion path, or its coordinate pairs cannot be read; it has no effect.", e);
                return null;
            }

            PathBuilder b = new();
            b.MoveTo(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                b.LineTo(points[i]);
            }

            path = b.ToPath();
            vertices = [.. Distances(points)];
        }

        if (path is null)
        {
            report.Warn("Svg.BadAnimation", $"The {e.Name}'s motion path is empty; it has no effect.", e);
            return null;
        }

        PathMeasure measure = new(path);
        vertices ??= source is null ? [0, measure.TotalLength] : PathVertices(source, measure.TotalLength);

        SvgCalcMode mode = attributes["calcMode"]?.Trim() switch
        {
            "discrete" => SvgCalcMode.Discrete,
            "linear" => SvgCalcMode.Linear,
            "spline" => SvgCalcMode.Spline,
            _ => SvgCalcMode.Paced,
        };

        // keyPoints place the target at fractions of the path's length at the keyTimes; paced timing ignores both.
        double[]? keyPoints = null;
        if (mode != SvgCalcMode.Paced && attributes["keyPoints"] is string keyPointsText)
        {
            keyPoints = SvgAnimationFunction.ReadNumbers(keyPointsText, ';');
            if (keyPoints is null || keyPoints.Any(k => k is < 0 or > 1 || double.IsNaN(k)))
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s keyPoints \"{Resolving.SvgGeometry.Clip(keyPointsText)}\" are not fractions from 0 to 1; it has no effect.", e);
                return null;
            }
        }

        int count = keyPoints?.Length ?? vertices.Length;
        double[]? keyTimes = null;
        if (mode != SvgCalcMode.Paced && attributes["keyTimes"] is string keyTimesText)
        {
            keyTimes = SvgAnimationFunction.ReadNumbers(keyTimesText, ';');
            if (keyTimes is null || keyTimes.Length != count || keyTimes[0] != 0 || keyTimes.Any(k => k is < 0 or > 1) ||
                keyTimes.Zip(keyTimes.Skip(1)).Any(p => p.Second < p.First) || (mode != SvgCalcMode.Discrete && keyTimes[^1] != 1))
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s keyTimes \"{Resolving.SvgGeometry.Clip(keyTimesText)}\" do not fit its motion; it has no effect.", e);
                return null;
            }
        }
        else if (keyPoints is not null)
        {
            report.Warn("Svg.BadAnimation", $"The {e.Name} has keyPoints without keyTimes; it has no effect.", e);
            return null;
        }

        double[][]? keySplines = null;
        if (mode == SvgCalcMode.Spline)
        {
            string? splinesText = attributes["keySplines"];
            keySplines = splinesText is null ? null : SvgAnimationFunction.ReadSplines(splinesText);
            if (keySplines is null || keySplines.Length != count - 1)
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s keySplines \"{Resolving.SvgGeometry.Clip(splinesText ?? "")}\" do not fit its motion; it has no effect.", e);
                return null;
            }
        }

        double angle = 0;
        bool reverse = false;
        string rotate = attributes["rotate"]?.Trim() ?? "0";
        if (rotate == "auto")
        {
            angle = double.NaN;
        }
        else if (rotate == "auto-reverse")
        {
            angle = double.NaN;
            reverse = true;
        }
        else if (!SvgAngle(rotate, out angle))
        {
            report.Warn("Svg.BadAnimation", $"The {e.Name}'s rotate \"{Resolving.SvgGeometry.Clip(rotate)}\" is not an angle, auto or auto-reverse; it is ignored.", e);
            angle = 0;
        }

        bool isTo = source is null && attributes["values"] is null && attributes["from"] is null && attributes["to"] is not null;
        return new SvgMotionFunction(measure, vertices, mode, keyTimes, keyPoints, keySplines)
        {
            Additive = !isTo && (attributes["additive"]?.Trim() == "sum" || (by && attributes["from"] is null)),
            Accumulates = !isTo && attributes["accumulate"]?.Trim() == "sum",
            Angle = angle,
            Reverse = reverse,
        };
    }

    /// <summary>
    /// The motion transform at a fraction <paramref name="p"/> of the simple duration in repetition <paramref name="iteration"/>:
    /// the rotation, then the move to the point on the path.
    /// </summary>
    public Matrix3x2 Evaluate(double p, long iteration)
    {
        double distance = Distance(Math.Clamp(p, 0, 1));
        (Vector2 point, Vector2 tangent) = At(distance);
        if (Accumulates && iteration > 0)
        {
            point += At(_contourStarts[^1]).Point * iteration;
        }

        double angle = Angle;
        if (double.IsNaN(angle))
        {
            angle = Math.Atan2(tangent.Y, tangent.X) * (180 / Math.PI);
            if (Reverse)
            {
                angle += 180;
            }
        }

        return Matrix3x2.CreateRotation((float)(angle * Math.PI / 180)) * Matrix3x2.CreateTranslation(point);
    }

    /// <summary>How far along the path the target is at p.</summary>
    private double Distance(double p)
    {
        double total = _contourStarts[^1];
        if (Mode == SvgCalcMode.Paced)
        {
            return p * total;
        }

        // keyPoints give the stops as fractions of the length; else the path's vertices are the stops.
        double[] stops = _keyPoints is double[] kp ? [.. kp.Select(k => k * total)] : _vertices;
        int n = stops.Length;
        if (n == 1)
        {
            return stops[0];
        }

        if (p >= 1)
        {
            return stops[n - 1];
        }

        if (Mode == SvgCalcMode.Discrete)
        {
            // Steps: keyTimes, or n equal parts.
            int index = Math.Min(n - 1, (int)Math.Floor(p * n));
            if (_keyTimes is double[] keys)
            {
                index = 0;
                while (index < n - 1 && p >= keys[index + 1])
                {
                    index++;
                }
            }

            return stops[index];
        }

        double[] times = _keyTimes ?? [.. Enumerable.Range(0, n).Select(i => (double)i / (n - 1))];
        int segment = 0;
        while (segment < n - 2 && p >= times[segment + 1])
        {
            segment++;
        }

        double width = times[segment + 1] - times[segment];
        double local = width > 0 ? (p - times[segment]) / width : 1;
        if (Mode == SvgCalcMode.Spline && _keySplines is not null)
        {
            local = SvgAnimationFunction.Spline(_keySplines[segment], local);
        }

        return stops[segment] + ((stops[segment + 1] - stops[segment]) * local);
    }

    /// <summary>The point and direction at a distance along the whole path, its contours one after another.</summary>
    private (Vector2 Point, Vector2 Tangent) At(double distance)
    {
        IReadOnlyList<ContourMeasure> contours = _measure.Contours;
        if (contours.Count == 0)
        {
            return (Vector2.Zero, Vector2.UnitX);
        }

        int i = 0;
        while (i < contours.Count - 1 && distance > _contourStarts[i + 1])
        {
            i++;
        }

        float local = (float)(distance - _contourStarts[i]);
        return (contours[i].GetPosition(local), contours[i].GetTangent(local));
    }

    /// <summary>The path an mpath child names, as path data; null when there is none.</summary>
    private static string? MotionPath(SvgElement animation, SvgDocument document)
    {
        foreach (SvgContent item in animation.Children)
        {
            if (item is SvgElement { Name: "mpath" } mpath)
            {
                string? href = mpath.Attributes["href"] ?? mpath.Attributes.Get("href", SvgNamespaces.XLink);
                string? id = href is null ? null : Styling.SvgStyleValues.LocalId(href);
                return id is not null && document.FindById(id) is SvgElement { Name: "path" } path ? path.Attributes["d"] : null;
            }
        }

        return null;
    }

    /// <summary>Coordinate pairs, one per value; null when one does not read.</summary>
    private static List<Vector2>? Points(IEnumerable<string> texts)
    {
        List<Vector2> points = [];
        List<double> numbers = [];
        foreach (string raw in texts)
        {
            string text = raw.Trim();
            if (text.Length == 0 && points.Count > 0)
            {
                continue;
            }

            numbers.Clear();
            if (!SvgNumbers.TryParseList(text, numbers) || numbers.Count != 2)
            {
                return null;
            }

            points.Add(new Vector2((float)numbers[0], (float)numbers[1]));
        }

        return points.Count == 0 ? null : points;
    }

    /// <summary>How far along a polyline each of its points lies.</summary>
    private static IEnumerable<double> Distances(List<Vector2> points)
    {
        double d = 0;
        yield return 0;
        for (int i = 1; i < points.Count; i++)
        {
            d += Vector2.Distance(points[i - 1], points[i]);
            yield return d;
        }
    }

    /// <summary>
    /// How far along a path each segment ends (its vertices, movetos left out), which linear and discrete timing step through;
    /// the start and end alone when the path does not read strictly or has too many segments.
    /// </summary>
    private static double[] PathVertices(string d, double total)
    {
        if (SvgPathValue.TryParse(d) is not SvgPathValue value || value.Segments.Length > MaxTimedSegments)
        {
            return [0, total];
        }

        List<double> stops = [0];
        SvgPathValue.Segment[] segments = value.Segments;
        for (int k = 1; k < segments.Length; k++)
        {
            if (segments[k].Command is 'M' or 'm')
            {
                continue;
            }

            SvgPathValue prefix = new(segments[..(k + 1)]);
            stops.Add(Path.TryParseSvg(prefix.ToText(), out Path? part) && part is not null ? new PathMeasure(part).TotalLength : stops[^1]);
        }

        return stops.Count < 2 ? [0, total] : [.. stops];
    }

    private static bool SvgAngle(string text, out double degrees)
    {
        string t = text.Trim();
        double scale = 1;
        if (t.EndsWith("deg", StringComparison.Ordinal))
        {
            t = t[..^3];
        }
        else if (t.EndsWith("grad", StringComparison.Ordinal))
        {
            t = t[..^4];
            scale = 0.9;
        }
        else if (t.EndsWith("rad", StringComparison.Ordinal))
        {
            t = t[..^3];
            scale = 180 / Math.PI;
        }
        else if (t.EndsWith("turn", StringComparison.Ordinal))
        {
            t = t[..^4];
            scale = 360;
        }

        bool ok = SvgNumbers.TryParse(t, out degrees) && double.IsFinite(degrees);
        degrees *= scale;
        return ok;
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"motion {Mode}, {_contourStarts[^1]:0.###} long");
}
