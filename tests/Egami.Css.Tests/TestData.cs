using System.Text.Json.Nodes;

namespace Egami.Css.Tests;

/// <summary>Locates the test data and converts parser output to css-parsing-tests' JSON representation.</summary>
internal static class TestData
{
    private static readonly Lazy<string> s_root = new(() =>
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    });

    public static string Root => s_root.Value;

    public static string Data(string relative) => Path.Combine(Root, "tests", "Egami.Css.Tests", "data", relative);

    /// <summary>Reads a css-parsing-tests file: pairs of input and expected output.</summary>
    public static List<(string Input, JsonNode? Expected)> ReadPairs(string file)
    {
        JsonArray array = JsonNode.Parse(File.ReadAllText(Data(Path.Combine("css-parsing-tests", file))))!.AsArray();
        List<(string, JsonNode?)> pairs = [];
        for (int i = 0; i + 1 < array.Count; i += 2)
        {
            pairs.Add((array[i]!.GetValue<string>(), array[i + 1]));
        }

        return pairs;
    }

    public static JsonNode ToJson(CssComponentValue value)
    {
        switch (value)
        {
            case CssFunction function:
                {
                    JsonArray array = ["function", function.Name];
                    foreach (CssComponentValue argument in function.Arguments)
                    {
                        array.Add(ToJson(argument));
                    }

                    return array;
                }

            case CssSimpleBlock block:
                {
                    JsonArray array = [block.OpenToken + block.CloseToken.ToString()];
                    foreach (CssComponentValue item in block.Content)
                    {
                        array.Add(ToJson(item));
                    }

                    return array;
                }

            default:
                return ToJson(((CssTokenValue)value).Token);
        }
    }

    public static JsonNode ToJson(CssToken token)
    {
        string type = token.IsInteger ? "integer" : "number";
        return token.Kind switch
        {
            CssTokenKind.Ident => new JsonArray("ident", token.Value),
            CssTokenKind.AtKeyword => new JsonArray("at-keyword", token.Value),
            CssTokenKind.Hash => new JsonArray("hash", token.Value, token.IsIdHash ? "id" : "unrestricted"),
            CssTokenKind.String => new JsonArray("string", token.Value),
            CssTokenKind.BadString => new JsonArray("error", "bad-string"),
            CssTokenKind.Url => new JsonArray("url", token.Value),
            CssTokenKind.BadUrl => new JsonArray("error", "bad-url"),
            CssTokenKind.Delim => JsonValue.Create(token.Value)!,
            CssTokenKind.Number => new JsonArray("number", token.Value, token.Number, type),
            CssTokenKind.Percentage => new JsonArray("percentage", token.Value, token.Number, type),
            CssTokenKind.Dimension => new JsonArray("dimension", token.Value, token.Number, type, token.Unit),
            CssTokenKind.Whitespace => JsonValue.Create(" ")!,
            CssTokenKind.Cdo => JsonValue.Create("<!--")!,
            CssTokenKind.Cdc => JsonValue.Create("-->")!,
            CssTokenKind.Colon => JsonValue.Create(":")!,
            CssTokenKind.Semicolon => JsonValue.Create(";")!,
            CssTokenKind.Comma => JsonValue.Create(",")!,
            CssTokenKind.RightSquare => new JsonArray("error", "]"),
            CssTokenKind.RightParenthesis => new JsonArray("error", ")"),
            CssTokenKind.RightCurly => new JsonArray("error", "}"),
            _ => JsonValue.Create("?" + token.Kind)!,
        };
    }

    public static JsonArray ToJson(IEnumerable<CssComponentValue> values)
    {
        JsonArray array = [];
        foreach (CssComponentValue value in values)
        {
            array.Add(ToJson(value));
        }

        return array;
    }

    public static JsonNode ToJson(object item)
    {
        return item switch
        {
            CssDeclaration declaration => new JsonArray("declaration", declaration.Name, ToJson(declaration.Value), declaration.Important),
            RawAtRule atRule => new JsonArray("at-rule", atRule.Name, ToJson(atRule.Prelude), atRule.Block is null ? null : ToJson(atRule.Block.Content)),
            RawQualifiedRule rule => new JsonArray("qualified rule", ToJson(rule.Prelude), ToJson(rule.Block.Content)),
            _ => new JsonArray("error", "invalid"),
        };
    }

    /// <summary>Compares JSON structurally, numbers within a relative tolerance.</summary>
    public static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is JsonArray x && b is JsonArray y)
        {
            if (x.Count != y.Count)
            {
                return false;
            }

            for (int i = 0; i < x.Count; i++)
            {
                if (!JsonEquals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue(out double da) && vb.TryGetValue(out double db))
            {
                return da == db || Math.Abs(da - db) <= 1e-6 * Math.Max(Math.Abs(da), Math.Abs(db));
            }

            return va.ToJsonString() == vb.ToJsonString();
        }

        return false;
    }
}
