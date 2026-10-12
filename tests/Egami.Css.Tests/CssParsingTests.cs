using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Egami.Css.Tests;

/// <summary>
/// The css-parsing-tests suite (SimonSapin/css-parsing-tests, CC0): tokenizer, component values, declarations, rules, An+B
/// and colors. The suite predates two changes in the current CSS Syntax 3, which Egami.Css follows: the match tokens
/// (<c>~=</c>, <c>|=</c>, <c>^=</c>, <c>$=</c>, <c>*=</c>, <c>||</c>) are now two delims, and <c>unicode-range</c> is no
/// longer a token. The expected output is adjusted for the first and the cases needing the second are skipped. Declaration
/// values are compared trimmed, since the current specification trims them.
/// </summary>
public sealed partial class CssParsingTests
{
    public static TheoryData<string, int> Cases(string file)
    {
        TheoryData<string, int> data = [];
        int count = TestData.ReadPairs(file).Count;
        for (int i = 0; i < count; i++)
        {
            data.Add(file, i);
        }

        return data;
    }

    private static (string Input, JsonNode? Expected) Case(string file, int index) => TestData.ReadPairs(file)[index];

    // Rewrites the old match tokens as the two delims the current tokenizer gives, and trims declaration values.
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                {
                    JsonArray result = [];
                    foreach (JsonNode? item in array)
                    {
                        // Egami.Css reports end-of-input errors through CssParseOptions.Errors, not as tokens.
                        if (item is JsonArray { Count: 2 } error && error[0] is JsonValue head && head.TryGetValue(out string? word) && word == "error"
                            && error[1] is JsonValue tail && tail.TryGetValue(out string? reason) && reason.StartsWith("eof-in-", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (item is JsonValue value && value.TryGetValue(out string? text) && text is "~=" or "|=" or "^=" or "$=" or "*=" or "||")
                        {
                            result.Add(text[0].ToString());
                            result.Add(text[1].ToString());
                            continue;
                        }

                        result.Add(Normalize(item));
                    }

                    if (result.Count == 4 && result[0] is JsonValue first && first.TryGetValue(out string? kind) && kind == "declaration" && result[2] is JsonArray declarationValue)
                    {
                        while (declarationValue.Count > 0 && declarationValue[0] is JsonValue v && v.TryGetValue(out string? s) && s == " ")
                        {
                            declarationValue.RemoveAt(0);
                        }

                        while (declarationValue.Count > 0 && declarationValue[^1] is JsonValue v && v.TryGetValue(out string? s) && s == " ")
                        {
                            declarationValue.RemoveAt(declarationValue.Count - 1);
                        }
                    }

                    return result;
                }

            default:
                return node?.DeepClone();
        }
    }

    private static void SkipUnicodeRange(JsonNode? expected)
    {
        if (expected?.ToJsonString().Contains("\"unicode-range\"", StringComparison.Ordinal) == true)
        {
            Assert.Skip("unicode-range tokens were removed from CSS Syntax 3; Egami.Css reads <urange> from ordinary tokens.");
        }
    }

    private static void AssertJson(JsonNode? expected, JsonNode actual, string input)
    {
        JsonNode? normalized = Normalize(expected);
        Assert.True(TestData.JsonEquals(normalized, actual), $"Input: {input}\nExpected: {normalized?.ToJsonString()}\nActual:   {actual.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(Cases), "component_value_list.json")]
    public void ComponentValueList(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        SkipUnicodeRange(expected);
        AssertJson(expected, TestData.ToJson(CssComponentValue.ParseList(input)), input);
    }

    [Theory]
    [MemberData(nameof(Cases), "one_component_value.json")]
    public void OneComponentValue(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        List<CssComponentValue> items = CssSyntax.ParseComponentValues(input, CssParseOptions.Default);
        List<CssComponentValue> trimmed = CssSyntax.Trim(items);
        JsonNode actual = trimmed.Count switch
        {
            0 => new JsonArray("error", "empty"),
            1 => TestData.ToJson(trimmed[0]),
            _ => new JsonArray("error", "extra-input"),
        };
        AssertJson(expected, actual, input);
    }

    [Theory]
    [MemberData(nameof(Cases), "one_declaration.json")]
    public void OneDeclaration(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        List<CssComponentValue> items = CssSyntax.Trim(CssSyntax.ParseComponentValues(input, CssParseOptions.Default));
        JsonNode actual;
        if (items.Count == 0)
        {
            actual = new JsonArray("error", "empty");
        }
        else
        {
            CssSyntax.Parser parser = new(items, CssParseOptions.Default);
            CssDeclaration? declaration = parser.ConsumeDeclaration(nested: false, reportErrors: false, toEnd: true);
            actual = declaration is null ? new JsonArray("error", "invalid") : TestData.ToJson(declaration);
        }

        AssertJson(expected, actual, input);
    }

    [Theory]
    [MemberData(nameof(Cases), "one_rule.json")]
    public void OneRule(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        List<CssComponentValue> items = CssSyntax.Trim(CssSyntax.ParseComponentValues(input, CssParseOptions.Default));
        JsonNode actual;
        if (items.Count == 0)
        {
            actual = new JsonArray("error", "empty");
        }
        else
        {
            CssSyntax.Parser parser = new(items, CssParseOptions.Default);
            object? rule = parser.PeekIsAtKeyword ? parser.ConsumeAtRule(nested: false) : parser.ConsumeQualifiedRule(nested: false);
            actual = rule is null ? new JsonArray("error", "invalid") : parser.SkipWhitespaceToEnd() ? TestData.ToJson(rule) : new JsonArray("error", "extra-input");
        }

        AssertJson(expected, actual, input);
    }

    [Theory]
    [MemberData(nameof(Cases), "rule_list.json")]
    public void RuleList(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        CssSyntax.Parser parser = new(CssSyntax.ParseComponentValues(input, CssParseOptions.Default), CssParseOptions.Default);
        AssertJson(expected, new JsonArray([.. parser.ConsumeRuleList(topLevel: false).Select(TestData.ToJson)]), input);
    }

    [Theory]
    [MemberData(nameof(Cases), "stylesheet.json")]
    public void Stylesheet(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        CssSyntax.Parser parser = new(CssSyntax.ParseComponentValues(input, CssParseOptions.Default), CssParseOptions.Default);
        AssertJson(expected, new JsonArray([.. parser.ConsumeRuleList(topLevel: true).Select(TestData.ToJson)]), input);
    }

    [Theory]
    [MemberData(nameof(Cases), "blocks_contents.json")]
    public void BlockContents(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        CssSyntax.Parser parser = new(CssSyntax.ParseComponentValues(input, CssParseOptions.Default), CssParseOptions.Default);
        List<object> items = [];
        parser.ConsumeBlockContents(items);
        AssertJson(expected, new JsonArray([.. items.Select(TestData.ToJson)]), input);
    }

    // declaration_list.json tests the older "parse a list of declarations"; Egami.Css parses declaration lists with the current
    // "consume a block's contents", which keeps qualified rules apart. Cases where the two algorithms agree must pass.
    [Theory]
    [MemberData(nameof(Cases), "declaration_list.json")]
    public void DeclarationList(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        if (input.StartsWith("@ media", StringComparison.Ordinal))
        {
            Assert.Skip("The older algorithm drops '@ media screen {...}' as an invalid declaration; the current one reads it as a nested qualified rule (blocks_contents.json covers it).");
        }

        CssSyntax.Parser parser = new(CssSyntax.ParseComponentValues(input, CssParseOptions.Default), CssParseOptions.Default);
        List<object> items = [];
        parser.ConsumeBlockContents(items);
        AssertJson(expected, new JsonArray([.. items.Select(TestData.ToJson)]), input);
    }

    [Theory]
    [MemberData(nameof(Cases), "An+B.json")]
    public void AnPlusBMicrosyntax(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        JsonNode? actual = AnPlusB.TryParse(input, out int a, out int b) ? new JsonArray(a, b) : null;
        Assert.True(TestData.JsonEquals(expected, actual), $"Input: {input}\nExpected: {expected?.ToJsonString()}\nActual: {actual?.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(Cases), "color_keywords_3.json")]
    [MemberData(nameof(Cases), "color_keywords_4.json")]
    [MemberData(nameof(Cases), "color_hexadecimal_3.json")]
    [MemberData(nameof(Cases), "color_hexadecimal_4.json")]
    [MemberData(nameof(Cases), "color_hsl_3.json")]
    [MemberData(nameof(Cases), "color_hsl_4.json")]
    [MemberData(nameof(Cases), "color_hwb_4.json")]
    [MemberData(nameof(Cases), "color_function_4.json")]
    [MemberData(nameof(Cases), "color_lab_4.json")]
    [MemberData(nameof(Cases), "color_lch_4.json")]
    [MemberData(nameof(Cases), "color_oklab_4.json")]
    [MemberData(nameof(Cases), "color_oklch_4.json")]
    [MemberData(nameof(Cases), "color_functions_5.json")]
    public void Color(string file, int index)
    {
        (string input, JsonNode? expected) = Case(file, index);
        if (file == "color_functions_5.json")
        {
            Assert.Skip("CSS Color 5 (device-cmyk(), color-mix(), relative colors, custom color spaces) is not supported.");
        }

        bool parsed = CssColor.TryParse(input, out CssColor color);
        string? serialization = expected?.GetValue<string>();
        Assert.True(parsed == serialization is not null, $"Input: {input}\nExpected: {serialization ?? "invalid"}\nActual: {(parsed ? color.ToString() : "invalid")}");
        if (serialization is null)
        {
            return;
        }

        // sRGB results compare channel by channel, within one 8-bit step; other color spaces are only checked for validity
        // and alpha, since their serialization keeps the space and Egami.Css converts to sRGB.
        Match rgb = RgbPattern().Match(serialization);
        Match srgb = SrgbPattern().Match(serialization);
        if (rgb.Success)
        {
            (byte r, byte g, byte b, byte a) = color.ToRgba8();
            Assert.InRange(r, Channel(rgb.Groups[1]) - 1, Channel(rgb.Groups[1]) + 1);
            Assert.InRange(g, Channel(rgb.Groups[2]) - 1, Channel(rgb.Groups[2]) + 1);
            Assert.InRange(b, Channel(rgb.Groups[3]) - 1, Channel(rgb.Groups[3]) + 1);
            double alpha = rgb.Groups[4].Success ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            Assert.InRange(a, (alpha * 255) - 1, (alpha * 255) + 1);
        }
        else if (srgb.Success)
        {
            Assert.Equal(Clip(srgb.Groups[1].Value), color.R, 0.0001);
            Assert.Equal(Clip(srgb.Groups[2].Value), color.G, 0.0001);
            Assert.Equal(Clip(srgb.Groups[3].Value), color.B, 0.0001);
            Assert.Equal(srgb.Groups[4].Success ? Clip(srgb.Groups[4].Value) : 1, color.A, 0.0001);
        }
        else
        {
            Match alpha = AlphaPattern().Match(serialization);
            Assert.Equal(alpha.Success ? double.Parse(alpha.Groups[1].Value, CultureInfo.InvariantCulture) : 1, color.A, 0.0001);
        }

        static double Channel(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);

        static double Clip(string value) => value == "none" ? 0 : Math.Clamp(double.Parse(value, CultureInfo.InvariantCulture), 0, 1);
    }

    [GeneratedRegex(@"^rgba?\(([\d.e-]+), ([\d.e-]+), ([\d.e-]+)(?:, ([\d.e-]+))?\)$")]
    private static partial Regex RgbPattern();

    [GeneratedRegex(@"^color\(srgb (\S+) (\S+) (\S+)(?: / (\S+))?\)$")]
    private static partial Regex SrgbPattern();

    [GeneratedRegex(@" / ([\d.e-]+)\)$")]
    private static partial Regex AlphaPattern();
}
