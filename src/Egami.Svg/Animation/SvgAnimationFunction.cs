using System.Globalization;

using Egami.Svg.Reading;
using Egami.Svg.Values;

namespace Egami.Svg.Animation;

/// <summary>How an animation moves between its values (the calcMode attribute).</summary>
internal enum SvgCalcMode : byte
{
    Discrete,
    Linear,
    Paced,
    Spline,
}

/// <summary>
/// The value of an animate or set at a point of its simple duration (SMIL Animation and SVG Animations Level 2): its values
/// from values, or from, to and by; calcMode with keyTimes and keySplines; additive and accumulate. Values that do not
/// interpolate (keywords, references, paths whose commands differ) change in steps, as calcMode discrete does.
/// </summary>
internal sealed class SvgAnimationFunction
{
    private readonly string[] _texts;
    private readonly SvgAnimatedValue[]? _values;
    private readonly double[]? _keyTimes;
    private readonly double[][]? _keySplines;
    private readonly string? _transformType;

    private SvgAnimationFunction(SvgValueKind kind, SvgCalcMode mode, string[] texts, SvgAnimatedValue[]? values, double[]? keyTimes, double[][]? keySplines, string? transformType)
    {
        Kind = kind;
        Mode = mode;
        _texts = texts;
        _values = values;
        _keyTimes = keyTimes;
        _keySplines = keySplines;
        _transformType = transformType;
    }

    /// <summary>Gets how the values are read.</summary>
    public SvgValueKind Kind { get; }

    /// <summary>Gets the calcMode in effect: discrete for values that do not interpolate.</summary>
    public SvgCalcMode Mode { get; private set; }

    /// <summary>Gets a value indicating whether the value is added to the underlying value (additive="sum", or a by animation).</summary>
    public bool Additive { get; private set; }

    /// <summary>Gets a value indicating whether each repetition builds on the last value (accumulate="sum").</summary>
    public bool Accumulates { get; private set; }

    /// <summary>Gets a value indicating whether this is a to animation, which runs from the underlying value to its to value.</summary>
    public bool IsTo { get; private set; }

    /// <summary>Gets a value indicating whether the result depends on the underlying value.</summary>
    public bool NeedsUnderlying => Additive || IsTo;

    /// <summary>The value kind of an attribute: colors, path data, transforms, else numbers and lengths (keywords fall back to steps).</summary>
    public static SvgValueKind KindOf(SvgAnimationKind element, string attribute) => element == SvgAnimationKind.AnimateTransform
        ? SvgValueKind.Transform
        : attribute switch
        {
            "fill" or "stroke" or "color" or "stop-color" or "flood-color" or "lighting-color" or "solid-color" => SvgValueKind.Color,
            "d" => SvgValueKind.Path,
            "transform" or "gradientTransform" or "patternTransform" or "class" or "href" => SvgValueKind.Discrete,
            _ => SvgValueKind.Numeric,
        };

    /// <summary>Reads an animation's values; null (reported) when they are in error, so the animation has no effect.</summary>
    public static SvgAnimationFunction? Create(SvgAnimation animation, SvgReport report)
    {
        SvgElement e = animation.Element;
        SvgAttributeCollection attributes = e.Attributes;
        SvgValueKind kind = KindOf(animation.Kind, animation.AttributeName!);
        string? transformType = animation.Kind == SvgAnimationKind.AnimateTransform ? (attributes["type"]?.Trim() ?? "translate") : null;

        if (animation.Kind == SvgAnimationKind.Set)
        {
            if (attributes["to"] is not string setTo)
            {
                report.Warn("Svg.BadAnimation", "The set has no to value; it has no effect.", e);
                return null;
            }

            return new SvgAnimationFunction(SvgValueKind.Discrete, SvgCalcMode.Discrete, [setTo.Trim()], null, null, null, null);
        }

        string[] texts;
        bool by = false, isTo = false;
        if (attributes["values"] is string valuesText)
        {
            List<string> list = [.. valuesText.Split(';').Select(v => v.Trim())];
            if (list.Count > 1 && list[^1].Length == 0)
            {
                list.RemoveAt(list.Count - 1);
            }

            if (list.Count == 0 || list.Exists(v => v.Length == 0))
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s values \"{Resolving.SvgGeometry.Clip(valuesText)}\" has an empty value; it has no effect.", e);
                return null;
            }

            texts = [.. list];
        }
        else if (attributes["from"] is string from)
        {
            if (attributes["to"] is string to)
            {
                texts = [from.Trim(), to.Trim()];
            }
            else if (attributes["by"] is string fromBy)
            {
                texts = [from.Trim(), fromBy.Trim()];
                by = true;
            }
            else
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name} has from but neither to nor by; it has no effect.", e);
                return null;
            }
        }
        else if (attributes["to"] is string onlyTo)
        {
            texts = [onlyTo.Trim()];
            isTo = true;
        }
        else if (attributes["by"] is string onlyBy)
        {
            texts = [onlyBy.Trim()];
            by = true;
        }
        else
        {
            report.Warn("Svg.BadAnimation", $"The {e.Name} has no values, from, to or by; it has no effect.", e);
            return null;
        }

        // Read every value in the attribute's kind; one that does not read makes the whole animation discrete.
        SvgAnimatedValue[]? values = null;
        if (kind != SvgValueKind.Discrete)
        {
            values = new SvgAnimatedValue[texts.Length];
            for (int i = 0; i < texts.Length && values is not null; i++)
            {
                if (SvgAnimatedValue.Parse(kind, texts[i], transformType) is SvgAnimatedValue v)
                {
                    values[i] = v;
                }
                else
                {
                    values = null;
                }
            }

            // Values that read but cannot be interpolated into each other (different list lengths or path commands) step.
            for (int i = 1; values is not null && i < values.Length; i++)
            {
                if ((by ? values[0].Add(values[i]) : values[i - 1].Interpolate(values[i], 0)) is null)
                {
                    values = null;
                }
            }
        }

        if (by)
        {
            if (values is null)
            {
                // by needs addition; a value that cannot be added has nothing to add to.
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s by value cannot be added to; it has no effect.", e);
                return null;
            }

            // from-by runs from from to from + by; a lone by runs from nothing to by, added to the underlying value.
            values = texts.Length == 2 ? [values[0], values[0].Add(values[1])!] : [values[0].Zero(), values[0]];
            texts = [.. values.Select(v => v.ToText())];
        }

        SvgCalcMode mode = attributes["calcMode"]?.Trim() switch
        {
            "discrete" => SvgCalcMode.Discrete,
            "paced" => SvgCalcMode.Paced,
            "spline" => SvgCalcMode.Spline,
            _ => SvgCalcMode.Linear,
        };
        bool interpolates = kind != SvgValueKind.Discrete && (values is not null || isTo);
        if (!interpolates)
        {
            mode = SvgCalcMode.Discrete;
        }

        int count = isTo ? 2 : texts.Length;
        double[]? keyTimes = null;
        if (mode != SvgCalcMode.Paced && attributes["keyTimes"] is string keyTimesText)
        {
            keyTimes = ReadNumbers(keyTimesText, ';');
            if (keyTimes is null || keyTimes.Length != count || keyTimes[0] != 0 || keyTimes.Any(k => k is < 0 or > 1) ||
                keyTimes.Zip(keyTimes.Skip(1)).Any(p => p.Second < p.First) ||
                (mode != SvgCalcMode.Discrete && keyTimes[^1] != 1))
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s keyTimes \"{Resolving.SvgGeometry.Clip(keyTimesText)}\" do not fit its values; it has no effect.", e);
                return null;
            }
        }

        double[][]? keySplines = null;
        if (mode == SvgCalcMode.Spline)
        {
            string? splinesText = attributes["keySplines"];
            keySplines = splinesText is null ? null : ReadSplines(splinesText);
            if (keySplines is null || keySplines.Length != count - 1)
            {
                report.Warn("Svg.BadAnimation", $"The {e.Name}'s keySplines \"{Resolving.SvgGeometry.Clip(splinesText ?? "")}\" do not fit its values; it has no effect.", e);
                return null;
            }
        }

        // Paced timing spaces the values by distance; a kind without distances is timed linearly.
        if (mode == SvgCalcMode.Paced && values is not null && !isTo)
        {
            keyTimes = Paced(values);
            if (keyTimes is null)
            {
                mode = SvgCalcMode.Linear;
            }
        }

        bool sum = attributes["additive"]?.Trim() == "sum";
        bool accumulate = attributes["accumulate"]?.Trim() == "sum";
        return new SvgAnimationFunction(kind, mode, texts, values, keyTimes, keySplines, transformType)
        {
            // A to animation ignores additive and accumulate; a lone by animation is always additive. Discrete values do not add.
            IsTo = isTo,
            Additive = !isTo && interpolates && (sum || (by && attributes["from"] is null)),
            Accumulates = !isTo && interpolates && accumulate,
            Mode = mode,
        };
    }

    /// <summary>
    /// The value at a fraction <paramref name="p"/> of the simple duration in repetition <paramref name="iteration"/> (from 0),
    /// on <paramref name="underlying"/>, the value below it in the sandwich (or the base value).
    /// </summary>
    public string Evaluate(double p, long iteration, string? underlying)
    {
        p = Math.Clamp(p, 0, 1);
        if (IsTo)
        {
            return EvaluateTo(p, underlying);
        }

        if (_values is null)
        {
            return _texts[Index(p, _texts.Length)];
        }

        SvgAnimatedValue value = At(_values, p);
        if (Accumulates && iteration > 0)
        {
            value = value.Add(_values[^1], iteration) ?? value;
        }

        if (Additive && underlying is not null)
        {
            if (Kind == SvgValueKind.Transform)
            {
                // Adding transforms appends this one to the transform list below it.
                string below = underlying.Trim();
                return below.Length == 0 || below == "none" ? value.ToText() : below + " " + value.ToText();
            }

            if (SvgAnimatedValue.Parse(Kind, underlying, _transformType) is SvgAnimatedValue belowValue)
            {
                value = belowValue.Add(value) ?? value;
            }
        }

        return value.ToText();
    }

    /// <summary>A to animation: from the underlying value to the to value, in steps when the two do not interpolate.</summary>
    private string EvaluateTo(double p, string? underlying)
    {
        string to = _texts[0];
        if (Kind == SvgValueKind.Transform && SvgTransformValue.TryParse(_transformType!, to) is SvgTransformValue toTransform)
        {
            // animateTransform: from the underlying transform when it is one of this type, else from the type's identity.
            return Mode == SvgCalcMode.Discrete
                ? (Index(p, 2) == 0 ? underlying ?? SvgTransformValue.From(null, toTransform).ToText() : toTransform.ToText())
                : SvgTransformValue.From(underlying, toTransform).Interpolate(toTransform, p)!.ToText();
        }

        if (underlying is not null && Mode != SvgCalcMode.Discrete && SvgAnimatedValue.Parse(Kind, underlying, _transformType) is SvgAnimatedValue from &&
            SvgAnimatedValue.Parse(Kind, to, _transformType) is SvgAnimatedValue target && from.Interpolate(target, 0) is not null)
        {
            return At([from, target], p).ToText();
        }

        // Steps from the underlying value (no value means none, which shows the base) to the to value.
        return Index(p, 2) == 0 ? underlying ?? to : to;
    }

    /// <summary>The interpolated value at p, by calcMode, keyTimes and keySplines.</summary>
    private SvgAnimatedValue At(SvgAnimatedValue[] values, double p)
    {
        int n = values.Length;
        if (n == 1 || Mode == SvgCalcMode.Discrete)
        {
            return values[n == 1 ? 0 : Index(p, n)];
        }

        double[] times = _keyTimes ?? [.. Enumerable.Range(0, n).Select(i => (double)i / (n - 1))];
        if (p >= 1)
        {
            return values[n - 1];
        }

        int segment = 0;
        while (segment < n - 2 && p >= times[segment + 1])
        {
            segment++;
        }

        double width = times[segment + 1] - times[segment];
        double local = width > 0 ? (p - times[segment]) / width : 1;
        if (Mode == SvgCalcMode.Spline && _keySplines is not null)
        {
            local = Spline(_keySplines[segment], local);
        }

        return values[segment].Interpolate(values[segment + 1], local) ?? values[local < 0.5 ? segment : segment + 1];
    }

    /// <summary>The value index at p in discrete timing: keyTimes, or n equal steps; the last value at the very end.</summary>
    private int Index(double p, int n)
    {
        if (p >= 1)
        {
            return n - 1;
        }

        if (_keyTimes is double[] times && times.Length == n)
        {
            int i = 0;
            while (i < n - 1 && p >= times[i + 1])
            {
                i++;
            }

            return i;
        }

        return Math.Min(n - 1, (int)Math.Floor(p * n));
    }

    /// <summary>The keyTimes of paced timing: each value placed by the distance travelled to reach it; null without distances.</summary>
    private static double[]? Paced(SvgAnimatedValue[] values)
    {
        int n = values.Length;
        if (n < 2)
        {
            return null;
        }

        double[] cumulative = new double[n];
        for (int i = 1; i < n; i++)
        {
            double d = values[i - 1].Distance(values[i]);
            if (double.IsNaN(d))
            {
                return null;
            }

            cumulative[i] = cumulative[i - 1] + d;
        }

        double total = cumulative[^1];
        if (!(total > 0))
        {
            return [.. Enumerable.Range(0, n).Select(i => i == n - 1 ? 1.0 : 0.0)];
        }

        return [.. cumulative.Select(c => c / total)];
    }

    /// <summary>A keySplines Bézier from (0, 0) to (1, 1): the y where x is <paramref name="x"/>.</summary>
    internal static double Spline(double[] control, double x)
    {
        double x1 = control[0], y1 = control[1], x2 = control[2], y2 = control[3];
        static double Curve(double a, double b, double t) => (3 * a * (1 - t) * (1 - t) * t) + (3 * b * (1 - t) * t * t) + (t * t * t);

        // x(t) rises from 0 to 1 because both control x values are in [0, 1], so bisection always finds t.
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 60; i++)
        {
            double fx = Curve(x1, x2, t) - x;
            if (Math.Abs(fx) < 1e-12)
            {
                break;
            }

            if (fx > 0)
            {
                hi = t;
            }
            else
            {
                lo = t;
            }

            t = (lo + hi) / 2;
        }

        return Curve(y1, y2, t);
    }

    internal static double[]? ReadNumbers(string text, char separator)
    {
        List<string> parts = [.. text.Split(separator).Select(p => p.Trim())];
        if (parts.Count > 1 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        double[] numbers = new double[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            if (!SvgNumbers.TryParse(parts[i], out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    internal static double[][]? ReadSplines(string text)
    {
        List<string> parts = [.. text.Split(';').Select(p => p.Trim())];
        if (parts.Count > 1 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        double[][] splines = new double[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            List<double> numbers = [];
            if (!SvgNumbers.TryParseList(parts[i], numbers) || numbers.Count != 4 || numbers.Any(v => v is < 0 or > 1))
            {
                return null;
            }

            splines[i] = [.. numbers];
        }

        return splines;
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} {Mode}: {string.Join("; ", _texts)}");
}
