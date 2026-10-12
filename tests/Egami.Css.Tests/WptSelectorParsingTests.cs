using System.Text.RegularExpressions;

namespace Egami.Css.Tests;

/// <summary>
/// The Web Platform Tests' selector validity checks (css/selectors/parsing, 3-clause BSD): every <c>test_valid_selector</c>
/// must parse and every <c>test_invalid_selector</c> must not. Serialization is not checked: Egami.Css keeps a selector's
/// text as written rather than CSSOM's canonical form.
/// </summary>
public sealed partial class WptSelectorParsingTests
{
    private static readonly Lazy<List<(string File, string Selector, bool Valid)>> s_cases = new(Read);

    // Selectors outside what Egami.Css supports, by the text that marks them, with the reason.
    private static readonly (string Marker, string Reason)[] s_skips =
    [
        (":host", "Shadow DOM's :host and :host() are not supported: ICssElement does not model shadow trees."),
        (":state(", "Custom element states (:state()) are not supported."),
        (":heading", ":heading and :heading() are Selectors 5, not Selectors 4."),
        ("::part(", "::part() is parsed as a never-matching pseudo-element; the pseudo-classes and pseudo-elements allowed after it (Shadow Parts) are not modeled."),
        ("::slotted(", "::slotted() is parsed as a never-matching pseudo-element; the pseudo-elements allowed after it (Shadow DOM) are not modeled."),
    ];

    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> data = [];
        List<(string File, string Selector, bool Valid)> cases = s_cases.Value;
        for (int i = 0; i < cases.Count; i++)
        {
            data.Add(cases[i].File, i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Validity(string file, int index)
    {
        (_, string selector, bool valid) = s_cases.Value[index];
        bool parsed = CssSelectorList.TryParse(selector, out _);
        if (parsed != valid)
        {
            foreach ((string marker, string reason) in s_skips)
            {
                if (selector.Contains(marker, StringComparison.Ordinal))
                {
                    Assert.Skip(reason);
                }
            }
        }

        Assert.True(parsed == valid, $"{file}: '{selector}' should be {(valid ? "valid" : "invalid")}.");
    }

    private static List<(string, string, bool)> Read()
    {
        List<(string, string, bool)> cases = [];
        foreach (string path in Directory.GetFiles(TestData.Data(Path.Combine("wpt", "parsing")), "*.html").Order(StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in CallPattern().Matches(line))
                {
                    string selector = Unescape(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value);
                    cases.Add((file, selector, match.Groups[1].Value != "invalid"));
                }

                // parse-is-where.html: assert_valid(valid, "{}(...)") runs the pattern with :is and :where; a forgiving case is still valid.
                foreach (Match match in IsWherePattern().Matches(line))
                {
                    foreach (string pseudo in new[] { ":is", ":where" })
                    {
                        cases.Add((file, Unescape(match.Groups[2].Value).Replace("{}", pseudo, StringComparison.Ordinal), true));
                    }
                }

                // parse-anplusb.html: each pattern runs with the four nth pseudo-classes.
                foreach (Match match in AnPlusBPattern().Matches(line))
                {
                    foreach (string pseudo in new[] { ":nth-child", ":nth-last-child", ":nth-of-type", ":nth-last-of-type" })
                    {
                        cases.Add((file, pseudo + Unescape(match.Groups[2].Value), match.Groups[1].Value == "assert_selector_serializes_to"));
                    }
                }
            }
        }

        return cases;
    }

    private static string Unescape(string text)
    {
        System.Text.StringBuilder builder = new();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                char next = text[++i];
                builder.Append(next switch { 'n' => '\n', 't' => '\t', 'f' => '\f', 'r' => '\r', _ => next });
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"test_(valid|invalid|valid_forgiving)_selector\(\s*(?:'((?:\\.|[^'\\])*)'|""((?:\\.|[^""\\])*)""|`([^`]*)`)")]
    private static partial Regex CallPattern();

    [GeneratedRegex(@"assert_valid\((true|false),\s*""((?:\\.|[^""\\])*)""")]
    private static partial Regex IsWherePattern();

    [GeneratedRegex(@"(assert_selector_serializes_to|assert_invalid_selector)\(source \+ '((?:\\.|[^'\\])*)'")]
    private static partial Regex AnPlusBPattern();
}
