using System.Diagnostics.CodeAnalysis;

namespace Egami.Css;

/// <summary>
/// A length, percentage or number that may be a math function: <c>calc()</c> with <c>+ - * /</c> and parentheses, and
/// <c>min()</c>, <c>max()</c> and <c>clamp()</c> (CSS Values 4), nested freely. A plain length or number parses too, so a
/// consumer can handle every <c>&lt;length-percentage&gt;</c> the same way. <see cref="Resolve(Func{CssUnit, double})"/>
/// turns it into pixels once the consumer knows what each unit is worth.
/// </summary>
public sealed class CssCalc
{
    private readonly Node _root;
    private readonly string _text;

    private CssCalc(Node root, string text)
    {
        _root = root;
        _text = text;
    }

    /// <summary>Gets whether the value is a plain number (all its terms are unitless), such as <c>calc(2 * 3)</c>.</summary>
    public bool IsNumber => !_root.IsLength;

    /// <summary>Gets whether any term is a percentage, which the consumer must resolve against a reference length.</summary>
    public bool HasPercentage => _root.HasPercentage;

    /// <summary>Parses a length, percentage, number or math function.</summary>
    /// <param name="text">The CSS text, such as <c>calc(100% - 2em)</c>.</param>
    /// <param name="calc">The value.</param>
    /// <returns>True when the text is valid and its terms have consistent types.</returns>
    public static bool TryParse(string text, [NotNullWhen(true)] out CssCalc? calc)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out calc);
    }

    /// <summary>Parses component values as a length, percentage, number or math function.</summary>
    /// <param name="values">The component values (whitespace around the value is ignored).</param>
    /// <param name="calc">The value.</param>
    /// <returns>True when the values are one valid value.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, [NotNullWhen(true)] out CssCalc? calc)
    {
        ArgumentNullException.ThrowIfNull(values);
        calc = null;
        CssComponentValue? single = CssLength.Single(values);
        return single is not null && TryParse(single, out calc);
    }

    /// <summary>Parses one component value as a length, percentage, number or math function.</summary>
    /// <param name="value">The component value.</param>
    /// <param name="calc">The value.</param>
    /// <returns>True when the value is valid.</returns>
    public static bool TryParse(CssComponentValue value, [NotNullWhen(true)] out CssCalc? calc)
    {
        ArgumentNullException.ThrowIfNull(value);
        Node? node = value is CssSimpleBlock ? null : ParseValue(value);
        calc = node is null ? null : new CssCalc(node, value.ToString());
        return calc is not null;
    }

    /// <summary>Computes the value.</summary>
    /// <param name="pixelsPerUnit">
    /// Gives the pixels one unit is worth, such as the font size for <see cref="CssUnit.Em"/> or a hundredth of the reference
    /// length for <see cref="CssUnit.Percent"/>; <see cref="CssUnit.None"/> is not asked for. Return NaN for a unit that cannot
    /// be resolved.
    /// </param>
    /// <returns>The value in pixels (or the number, for <see cref="IsNumber"/>). NaN becomes 0 and infinities are clamped.</returns>
    public double Resolve(Func<CssUnit, double> pixelsPerUnit)
    {
        ArgumentNullException.ThrowIfNull(pixelsPerUnit);
        double result = _root.Evaluate(pixelsPerUnit);
        return double.IsNaN(result) ? 0 : Math.Clamp(result, double.MinValue, double.MaxValue);
    }

    /// <summary>Returns the value as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text;

    internal static bool TryParse(CssFunction function, [NotNullWhen(true)] out CssCalc? calc) => TryParse((CssComponentValue)function, out calc);

    // One operand: a number, length, percentage, parenthesized sum or math function.
    private static Node? ParseValue(CssComponentValue value)
    {
        switch (value)
        {
            case CssTokenValue { Token: var token }:
                switch (token.Kind)
                {
                    case CssTokenKind.Number:
                        return new Leaf(token.Number, CssUnit.None);
                    case CssTokenKind.Percentage:
                        return new Leaf(token.Number, CssUnit.Percent);
                    case CssTokenKind.Dimension when CssLength.TryParseUnit(token.Unit, out CssUnit unit):
                        return new Leaf(token.Number, unit);
                    case CssTokenKind.Ident:
                        return CssText.ToAsciiLower(token.Value) switch
                        {
                            "e" => new Leaf(Math.E, CssUnit.None),
                            "pi" => new Leaf(Math.PI, CssUnit.None),
                            "infinity" => new Leaf(double.PositiveInfinity, CssUnit.None),
                            "-infinity" => new Leaf(double.NegativeInfinity, CssUnit.None),
                            "nan" => new Leaf(double.NaN, CssUnit.None),
                            _ => null,
                        };
                    default:
                        return null;
                }

            case CssSimpleBlock { OpenToken: '(' } block:
                return ParseSum(block.Content);
            case CssFunction function:
                string name = CssText.ToAsciiLower(function.Name);
                if (name == "calc")
                {
                    return ParseSum(function.Arguments);
                }

                if (name is "min" or "max" or "clamp")
                {
                    List<Node> arguments = [];
                    int start = 0;
                    for (int i = 0; i <= function.Arguments.Count; i++)
                    {
                        if (i < function.Arguments.Count && !function.Arguments[i].IsToken(CssTokenKind.Comma))
                        {
                            continue;
                        }

                        Node? argument = ParseSum(Slice(function.Arguments, start, i));
                        if (argument is null)
                        {
                            return null;
                        }

                        arguments.Add(argument);
                        start = i + 1;
                    }

                    if ((name == "clamp" && arguments.Count != 3) || arguments.Any(a => a.IsLength != arguments[0].IsLength))
                    {
                        return null;
                    }

                    return new Function(name, [.. arguments]);
                }

                return null;
            default:
                return null;
        }
    }

    private static List<CssComponentValue> Slice(IReadOnlyList<CssComponentValue> values, int start, int end)
    {
        List<CssComponentValue> slice = new(end - start);
        for (int i = start; i < end; i++)
        {
            slice.Add(values[i]);
        }

        return slice;
    }

    // <calc-sum> = <calc-product> [ [ '+' | '-' ] <calc-product> ]*, with whitespace required around + and -.
    private static Node? ParseSum(IReadOnlyList<CssComponentValue> values)
    {
        List<CssComponentValue> items = CssSyntax.Trim(values);
        if (items.Count == 0)
        {
            return null;
        }

        List<(Node Node, bool Negate)> terms = [];
        int start = 0;
        bool negate = false;
        for (int i = 0; i <= items.Count; i++)
        {
            bool isOperator = i < items.Count && (items[i].IsDelim('+') || items[i].IsDelim('-'));
            if (i < items.Count && !isOperator)
            {
                continue;
            }

            if (isOperator && (i == 0 || i == items.Count - 1 || !items[i - 1].IsWhitespace || !items[i + 1].IsWhitespace))
            {
                return null;
            }

            Node? product = ParseProduct(Slice(items, start, i));
            if (product is null)
            {
                return null;
            }

            terms.Add((product, negate));
            if (isOperator)
            {
                negate = items[i].IsDelim('-');
            }

            start = i + 1;
        }

        if (terms.Count == 1 && !terms[0].Negate)
        {
            return terms[0].Node;
        }

        foreach ((Node node, _) in terms)
        {
            if (node.IsLength != terms[0].Node.IsLength)
            {
                return null;
            }
        }

        return new Sum([.. terms]);
    }

    // <calc-product> = <calc-value> [ [ '*' | '/' ] <calc-value> ]*
    private static Node? ParseProduct(List<CssComponentValue> values)
    {
        List<CssComponentValue> items = CssSyntax.Trim(values);
        Node? result = null;
        char op = '\0';
        foreach (CssComponentValue item in items)
        {
            if (item.IsWhitespace)
            {
                continue;
            }

            if (item.IsDelim('*') || item.IsDelim('/'))
            {
                if (result is null || op != '\0')
                {
                    return null;
                }

                op = item.IsDelim('*') ? '*' : '/';
                continue;
            }

            Node? operand = ParseValue(item);
            if (operand is null)
            {
                return null;
            }

            if (result is null)
            {
                result = operand;
                continue;
            }

            if (op == '\0' || (operand.IsLength && (result.IsLength || op == '/')))
            {
                return null;
            }

            result = new Product(result, operand, op == '/');
            op = '\0';
        }

        return op == '\0' ? result : null;
    }

    private abstract class Node
    {
        public abstract bool IsLength { get; }

        public abstract bool HasPercentage { get; }

        public abstract double Evaluate(Func<CssUnit, double> pixelsPerUnit);
    }

    private sealed class Leaf(double value, CssUnit unit) : Node
    {
        public override bool IsLength => unit != CssUnit.None;

        public override bool HasPercentage => unit == CssUnit.Percent;

        public override double Evaluate(Func<CssUnit, double> pixelsPerUnit) => unit == CssUnit.None ? value : value * pixelsPerUnit(unit);
    }

    private sealed class Sum((Node Node, bool Negate)[] terms) : Node
    {
        public override bool IsLength => terms[0].Node.IsLength;

        public override bool HasPercentage => terms.Any(t => t.Node.HasPercentage);

        public override double Evaluate(Func<CssUnit, double> pixelsPerUnit)
        {
            double total = 0;
            foreach ((Node node, bool negate) in terms)
            {
                double value = node.Evaluate(pixelsPerUnit);
                total += negate ? -value : value;
            }

            return total;
        }
    }

    private sealed class Product(Node left, Node right, bool divide) : Node
    {
        public override bool IsLength => left.IsLength || right.IsLength;

        public override bool HasPercentage => left.HasPercentage || right.HasPercentage;

        public override double Evaluate(Func<CssUnit, double> pixelsPerUnit)
        {
            double a = left.Evaluate(pixelsPerUnit);
            double b = right.Evaluate(pixelsPerUnit);
            return divide ? a / b : a * b;
        }
    }

    private sealed class Function(string name, Node[] arguments) : Node
    {
        public override bool IsLength => arguments[0].IsLength;

        public override bool HasPercentage => arguments.Any(a => a.HasPercentage);

        public override double Evaluate(Func<CssUnit, double> pixelsPerUnit)
        {
            if (name == "clamp")
            {
                double min = arguments[0].Evaluate(pixelsPerUnit);
                double value = arguments[1].Evaluate(pixelsPerUnit);
                double max = arguments[2].Evaluate(pixelsPerUnit);
                return Math.Max(min, Math.Min(value, max));
            }

            double result = arguments[0].Evaluate(pixelsPerUnit);
            for (int i = 1; i < arguments.Length; i++)
            {
                double value = arguments[i].Evaluate(pixelsPerUnit);
                result = name == "min" ? Math.Min(result, value) : Math.Max(result, value);
            }

            return result;
        }
    }
}
