using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Egami.Css;

/// <summary>
/// An easing function (CSS Easing 2): <c>linear</c>, <c>linear()</c> with stops, <c>ease</c>, <c>ease-in</c>,
/// <c>ease-out</c>, <c>ease-in-out</c>, <c>cubic-bezier()</c>, <c>step-start</c>, <c>step-end</c> or <c>steps()</c>. It maps
/// an input progress to an output progress.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="easing"/>
/// </example>
public sealed class CssEasingFunction
{
    private readonly Kind _kind;
    private readonly double _x1;
    private readonly double _y1;
    private readonly double _x2;
    private readonly double _y2;
    private readonly int _steps;
    private readonly StepPosition _position;
    private readonly (double Input, double Output)[]? _points;
    private readonly string _text;

    private CssEasingFunction(Kind kind, string text)
    {
        _kind = kind;
        _text = text;
    }

    private CssEasingFunction(double x1, double y1, double x2, double y2, string text)
        : this(Kind.CubicBezier, text)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    private CssEasingFunction(int steps, StepPosition position, string text)
        : this(Kind.Steps, text)
    {
        _steps = steps;
        _position = position;
    }

    private CssEasingFunction((double Input, double Output)[] points, string text)
        : this(Kind.LinearPoints, text)
    {
        _points = points;
    }

    private enum Kind
    {
        Linear,
        LinearPoints,
        CubicBezier,
        Steps,
    }

    private enum StepPosition
    {
        JumpStart,
        JumpEnd,
        JumpNone,
        JumpBoth,
    }

    /// <summary>Gets the <c>linear</c> easing function: the output equals the input.</summary>
    public static CssEasingFunction Linear { get; } = new(Kind.Linear, "linear");

    /// <summary>Gets the <c>ease</c> easing function, <c>cubic-bezier(0.25, 0.1, 0.25, 1)</c>: the initial value of <c>animation-timing-function</c>.</summary>
    public static CssEasingFunction Ease { get; } = new(0.25, 0.1, 0.25, 1, "ease");

    /// <summary>Gets a value indicating whether the function is <c>linear</c> or the identity in another form.</summary>
    public bool IsLinear => _kind == Kind.Linear || (_kind == Kind.CubicBezier && _x1 == _y1 && _x2 == _y2);

    /// <summary>Parses an easing function.</summary>
    /// <param name="text">The CSS text, such as <c>ease-in</c> or <c>steps(4, jump-end)</c>.</param>
    /// <returns>The easing function.</returns>
    /// <exception cref="FormatException"><paramref name="text"/> is not an easing function.</exception>
    public static CssEasingFunction Parse(string text) =>
        TryParse(text, out CssEasingFunction? easing) ? easing : throw new FormatException($"'{text}' is not an easing function.");

    /// <summary>Tries to parse an easing function.</summary>
    /// <param name="text">The CSS text, such as <c>ease-in</c> or <c>steps(4, jump-end)</c>.</param>
    /// <param name="easing">The easing function, when the text is one.</param>
    /// <returns><see langword="true"/> when the text is an easing function.</returns>
    public static bool TryParse(string text, [NotNullWhen(true)] out CssEasingFunction? easing)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out easing);
    }

    /// <summary>Tries to parse an easing function from component values.</summary>
    /// <param name="values">The component values; surrounding whitespace is ignored.</param>
    /// <param name="easing">The easing function, when the values are one.</param>
    /// <returns><see langword="true"/> when the values are an easing function.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, [NotNullWhen(true)] out CssEasingFunction? easing)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<CssComponentValue> trimmed = CssSyntax.Trim(values);
        easing = trimmed.Count == 1 ? TryParse(trimmed[0]) : null;
        return easing is not null;
    }

    /// <summary>
    /// Maps an input progress to an output progress. Inputs outside 0 to 1 extrapolate as CSS Easing 2 says, so the output
    /// may lie outside 0 to 1 too.
    /// </summary>
    /// <param name="progress">The input progress.</param>
    /// <param name="beforeFlag">The before flag of Web Animations 1: set when the animation is in its before phase, so a step at the input lands on its lower side.</param>
    /// <returns>The output progress.</returns>
    public double Evaluate(double progress, bool beforeFlag = false) => _kind switch
    {
        Kind.Linear => progress,
        Kind.LinearPoints => EvaluatePoints(progress),
        Kind.CubicBezier => EvaluateBezier(progress),
        _ => EvaluateSteps(progress, beforeFlag),
    };

    /// <summary>Returns the function as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text;

    private static CssEasingFunction? TryParse(CssComponentValue value)
    {
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            return CssText.ToAsciiLower(ident.Value) switch
            {
                "linear" => Linear,
                "ease" => Ease,
                "ease-in" => new CssEasingFunction(0.42, 0, 1, 1, "ease-in"),
                "ease-out" => new CssEasingFunction(0, 0, 0.58, 1, "ease-out"),
                "ease-in-out" => new CssEasingFunction(0.42, 0, 0.58, 1, "ease-in-out"),
                "step-start" => new CssEasingFunction(1, StepPosition.JumpStart, "step-start"),
                "step-end" => new CssEasingFunction(1, StepPosition.JumpEnd, "step-end"),
                _ => null,
            };
        }

        if (value is not CssFunction function)
        {
            return null;
        }

        List<List<CssComponentValue>>? arguments = SplitArguments(function.Arguments);
        if (arguments is null)
        {
            return null;
        }

        if (function.NameIs("cubic-bezier"))
        {
            return ParseCubicBezier(arguments);
        }

        if (function.NameIs("steps"))
        {
            return ParseSteps(arguments);
        }

        return function.NameIs("linear") ? ParseLinear(arguments) : null;
    }

    private static CssEasingFunction? ParseCubicBezier(List<List<CssComponentValue>> arguments)
    {
        if (arguments.Count != 4)
        {
            return null;
        }

        double[] numbers = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (arguments[i].Count != 1 || !TryNumber(arguments[i][0], out numbers[i]))
            {
                return null;
            }
        }

        // The x coordinates must lie in [0, 1] so the curve is a function of x.
        if (numbers[0] is < 0 or > 1 || numbers[2] is < 0 or > 1)
        {
            return null;
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"cubic-bezier({Format(numbers[0])}, {Format(numbers[1])}, {Format(numbers[2])}, {Format(numbers[3])})");
        return new CssEasingFunction(numbers[0], numbers[1], numbers[2], numbers[3], text);
    }

    private static CssEasingFunction? ParseSteps(List<List<CssComponentValue>> arguments)
    {
        if (arguments.Count is < 1 or > 2 || arguments[0].Count != 1 || !TryNumber(arguments[0][0], out double count) || count != Math.Floor(count))
        {
            return null;
        }

        if (arguments[0][0] is CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: false } })
        {
            return null;
        }

        StepPosition position = StepPosition.JumpEnd;
        string positionText = "";
        if (arguments.Count == 2)
        {
            if (arguments[1].Count != 1 || arguments[1][0] is not CssTokenValue { Token.Kind: CssTokenKind.Ident } keyword)
            {
                return null;
            }

            positionText = CssText.ToAsciiLower(keyword.Token.Value);
            switch (positionText)
            {
                case "jump-start":
                case "start":
                    position = StepPosition.JumpStart;
                    break;
                case "jump-end":
                case "end":
                    position = StepPosition.JumpEnd;
                    break;
                case "jump-none":
                    position = StepPosition.JumpNone;
                    break;
                case "jump-both":
                    position = StepPosition.JumpBoth;
                    break;
                default:
                    return null;
            }
        }

        // The step count must be positive, and above 1 for jump-none (which has one jump fewer than steps).
        if (count < 1 || (position == StepPosition.JumpNone && count < 2) || count > int.MaxValue)
        {
            return null;
        }

        string text = positionText is "" or "end" or "jump-end"
            ? string.Create(CultureInfo.InvariantCulture, $"steps({(int)count})")
            : string.Create(CultureInfo.InvariantCulture, $"steps({(int)count}, {positionText})");
        return new CssEasingFunction((int)count, position, text);
    }

    // linear( [ <number> && <percentage>{0,2} ]# ), canonicalized as CSS Easing 2 says.
    private static CssEasingFunction? ParseLinear(List<List<CssComponentValue>> arguments)
    {
        List<(double? Input, double Output)> points = [];
        List<string> parts = [];
        foreach (List<CssComponentValue> stop in arguments)
        {
            double? output = null;
            List<double> inputs = [];
            foreach (CssComponentValue item in stop)
            {
                if (item is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percentage })
                {
                    inputs.Add(percentage.Number / 100);
                }
                else if (output is null && TryNumber(item, out double number))
                {
                    output = number;
                }
                else
                {
                    return null;
                }
            }

            if (output is null || inputs.Count > 2)
            {
                return null;
            }

            if (inputs.Count == 0)
            {
                points.Add((null, output.Value));
            }

            foreach (double input in inputs)
            {
                points.Add((input, output.Value));
            }

            parts.Add(CssComponentValue.Serialize(stop));
        }

        if (arguments.Count < 2)
        {
            return null;
        }

        // Canonicalize: the ends default to 0 and 1, an input below an earlier one rises to it, and missing inputs spread
        // evenly between their known neighbours.
        if (points[0].Input is null)
        {
            points[0] = (0, points[0].Output);
        }

        if (points[^1].Input is null)
        {
            points[^1] = (Math.Max(1, MaxInput(points, points.Count - 1)), points[^1].Output);
        }

        double largest = double.NegativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Input is double input)
            {
                largest = Math.Max(largest, input);
                points[i] = (largest, points[i].Output);
            }
        }

        (double Input, double Output)[] result = new (double, double)[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Input is double known)
            {
                result[i] = (known, points[i].Output);
                continue;
            }

            int end = i + 1;
            while (points[end].Input is null)
            {
                end++;
            }

            double from = result[i - 1].Input;
            double to = points[end].Input!.Value;
            int gaps = end - i + 1;
            for (int j = i; j < end; j++)
            {
                result[j] = (from + ((to - from) * (j - i + 1) / gaps), points[j].Output);
            }

            i = end - 1;
        }

        return new CssEasingFunction(result, "linear(" + string.Join(", ", parts) + ")");
    }

    private static double MaxInput(List<(double? Input, double Output)> points, int count)
    {
        double max = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            if (points[i].Input is double input)
            {
                max = Math.Max(max, input);
            }
        }

        return max;
    }

    // Splits function arguments at top-level commas, dropping whitespace. Null when an argument is empty.
    private static List<List<CssComponentValue>>? SplitArguments(IReadOnlyList<CssComponentValue> values)
    {
        List<List<CssComponentValue>> arguments = [[]];
        foreach (CssComponentValue item in values)
        {
            if (item.IsWhitespace)
            {
                continue;
            }

            if (item.IsToken(CssTokenKind.Comma))
            {
                if (arguments[^1].Count == 0)
                {
                    return null;
                }

                arguments.Add([]);
                continue;
            }

            arguments[^1].Add(item);
        }

        return arguments[^1].Count == 0 ? null : arguments;
    }

    private static bool TryNumber(CssComponentValue value, out double number)
    {
        switch (value)
        {
            case CssTokenValue { Token: { Kind: CssTokenKind.Number } token }:
                number = token.Number;
                return double.IsFinite(number);
            case CssFunction function when CssCalc.TryParse(function, out CssCalc? calc) && calc.IsNumber:
                number = calc.Resolve(_ => 1);
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private double EvaluatePoints(double input)
    {
        (double Input, double Output)[] points = _points!;
        int a = 0;
        for (int i = points.Length - 1; i >= 0; i--)
        {
            if (points[i].Input <= input)
            {
                a = i;
                break;
            }
        }

        if (a == points.Length - 1)
        {
            a--;
        }

        (double Input, double Output) pointA = points[a];
        (double Input, double Output) pointB = points[a + 1];
        if (pointA.Input == pointB.Input)
        {
            return pointB.Output;
        }

        return pointA.Output + ((input - pointA.Input) / (pointB.Input - pointA.Input) * (pointB.Output - pointA.Output));
    }

    private double EvaluateBezier(double input)
    {
        // Outside [0, 1] the curve extends along its end tangents.
        if (input < 0)
        {
            double slope = _x1 > 0 ? _y1 / _x1 : _y1 == 0 && _x2 > 0 ? _y2 / _x2 : 0;
            return slope * input;
        }

        if (input > 1)
        {
            double slope = _x2 < 1 ? (_y2 - 1) / (_x2 - 1) : _y2 == 1 && _x1 < 1 ? (_y1 - 1) / (_x1 - 1) : 0;
            return 1 + (slope * (input - 1));
        }

        if (input is 0 or 1 || IsLinear)
        {
            return input;
        }

        double t = SolveCurveX(input);
        return SampleCurve(0, _y1, _y2, t);
    }

    // The curve parameter t in [0, 1] whose x is 'x': Newton's method from t = x, then bisection, which always converges
    // because x(t) is monotonic for control x coordinates in [0, 1].
    private double SolveCurveX(double x)
    {
        const double Epsilon = 1e-12;
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = SampleCurve(0, _x1, _x2, t) - x;
            if (Math.Abs(error) < Epsilon)
            {
                return t;
            }

            double derivative = SampleDerivative(_x1, _x2, t);
            if (Math.Abs(derivative) < 1e-9)
            {
                break;
            }

            t -= error / derivative;
            if (t is < 0 or > 1)
            {
                break;
            }
        }

        double low = 0;
        double high = 1;
        t = x;
        for (int i = 0; i < 100; i++)
        {
            double value = SampleCurve(0, _x1, _x2, t);
            if (Math.Abs(value - x) < Epsilon)
            {
                break;
            }

            if (value < x)
            {
                low = t;
            }
            else
            {
                high = t;
            }

            t = (low + high) / 2;
        }

        return t;
    }

    // One coordinate of the cubic Bézier from (0, 0) to (1, 1) with control coordinates p1 and p2, at t.
    private static double SampleCurve(double p0, double p1, double p2, double t)
    {
        double u = 1 - t;
        return (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t) + (u * u * u * p0);
    }

    private static double SampleDerivative(double p1, double p2, double t)
    {
        double u = 1 - t;
        return (3 * u * u * p1) + (6 * u * t * (p2 - p1)) + (3 * t * t * (1 - p2));
    }

    private double EvaluateSteps(double input, bool beforeFlag)
    {
        double scaled = input * _steps;
        double currentStep = Math.Floor(scaled);
        if (_position is StepPosition.JumpStart or StepPosition.JumpBoth)
        {
            currentStep++;
        }

        if (beforeFlag && scaled - Math.Floor(scaled) == 0)
        {
            currentStep--;
        }

        if (input >= 0 && currentStep < 0)
        {
            currentStep = 0;
        }

        // In long: steps(2147483647, jump-both) has one more jump than int holds.
        long jumps = _position switch
        {
            StepPosition.JumpNone => _steps - 1L,
            StepPosition.JumpBoth => _steps + 1L,
            _ => _steps,
        };
        if (input <= 1 && currentStep > jumps)
        {
            currentStep = jumps;
        }

        return currentStep / jumps;
    }
}
