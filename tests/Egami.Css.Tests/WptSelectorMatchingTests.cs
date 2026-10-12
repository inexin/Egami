using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egami.Css.Tests;

/// <summary>
/// The Web Platform Tests' Selectors API matching suite (dom/nodes/selectors.js with ParentNode-querySelector-All-content.xht,
/// 3-clause BSD): every valid selector is matched against the test document in document and in-element context, as an XML
/// (XHTML) document and as an HTML document, and every invalid selector must fail to parse.
/// </summary>
public sealed partial class WptSelectorMatchingTests
{
    private const int TestQsa = 0x01;

    private static readonly Lazy<List<SelectorCase>> s_valid = new(() => Read("validSelectors"));
    private static readonly Lazy<List<SelectorCase>> s_invalid = new(() => Read("invalidSelectors"));
    private static readonly Lazy<TestElement> s_xhtml = new(() => LoadDocument(htmlDocument: false));
    private static readonly Lazy<TestElement> s_html = new(() => LoadDocument(htmlDocument: true));

    // Cases this implementation cannot run, with the reason.
    private static readonly Dictionary<string, string> s_skips = new(StringComparer.Ordinal)
    {
    };

    internal sealed record SelectorCase(string Name, string Selector, string[] Expect, string[] Exclude, int TestType);

    public static TheoryData<string, string, int> ValidCases()
    {
        TheoryData<string, string, int> data = [];
        List<SelectorCase> cases = s_valid.Value;
        foreach (string docType in new[] { "xhtml", "html" })
        {
            foreach (string context in new[] { "document", "element" })
            {
                for (int i = 0; i < cases.Count; i++)
                {
                    SelectorCase c = cases[i];
                    if ((c.TestType & TestQsa) != 0 && !c.Exclude.Contains(docType) && !c.Exclude.Contains(context))
                    {
                        data.Add(docType, context, i);
                    }
                }
            }
        }

        return data;
    }

    public static TheoryData<int> InvalidCases()
    {
        TheoryData<int> data = [];
        for (int i = 0; i < s_invalid.Value.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void Valid(string docType, string context, int index)
    {
        SelectorCase c = s_valid.Value[index];
        if (s_skips.TryGetValue(c.Selector, out string? reason))
        {
            Assert.Skip(reason);
        }

        TestElement document = docType == "html" ? s_html.Value : s_xhtml.Value;
        TestElement root = context == "document" ? document : document.FindById("root")!;
        ICssElement? scope = context == "document" ? null : root;
        Assert.True(CssSelectorList.TryParse(c.Selector, out CssSelectorList? list), $"{c.Name}: '{c.Selector}' should parse.");
        IEnumerable<TestElement> candidates = context == "document" ? root.DescendantsAndSelf() : root.DescendantsAndSelf().Skip(1);
        string[] found = [.. candidates.Where(e => list.Matches(e, scope)).Select(e => e.Id ?? "(no id)")];
        Assert.True(found.SequenceEqual(c.Expect), $"{c.Name}: '{c.Selector}'\nExpected: {string.Join(", ", c.Expect)}\nActual:   {string.Join(", ", found)}");
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid(int index)
    {
        SelectorCase c = s_invalid.Value[index];
        Assert.False(CssSelectorList.TryParse(c.Selector, out _), $"{c.Name}: '{c.Selector}' should not parse.");
    }

    private static TestElement LoadDocument(bool htmlDocument)
    {
        TestElement html = TestElement.Load(TestData.Data(Path.Combine("wpt", "ParentNode-querySelector-All-content.xht")), htmlDocument);
        SetupSpecialElements(html.FindById("root")!, htmlDocument);
        return html;
    }

    // setupSpecialElements() of ParentNode-querySelector-All.js.
    private static void SetupSpecialElements(TestElement parent, bool htmlDocument)
    {
        const string Xhtml = TestElement.XhtmlNamespace;
        parent.Append(new TestElement("null", Xhtml, htmlDocument));
        parent.Append(new TestElement("undefined", Xhtml, htmlDocument));
        foreach (string prefix in new[] { "any-namespace", "no-namespace" })
        {
            TestElement container = new("div", Xhtml, htmlDocument);
            container.SetAttribute("id", prefix);
            string?[] namespaces = [Xhtml, Xhtml, null, "http://www.example.org/ns"];
            for (int i = 0; i < namespaces.Length; i++)
            {
                TestElement div = new("div", namespaces[i], htmlDocument);
                div.SetAttribute("id", $"{prefix}-div{i + 1}");
                container.Append(div);
            }

            parent.Append(container);
        }

        parent.FindById("attr-presence-i1")!.SetAttribute("title", "", "http://www.example.org/ns");
    }

    private static List<SelectorCase> Read(string variable)
    {
        string script = File.ReadAllText(TestData.Data(Path.Combine("wpt", "selectors.js")));
        int start = script.IndexOf($"var {variable} = [", StringComparison.Ordinal);
        int end = script.IndexOf("\n];", start, StringComparison.Ordinal);
        List<SelectorCase> cases = [];
        foreach (string rawLine in script[start..end].Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("{name:", StringComparison.Ordinal))
            {
                continue;
            }

            string name = Unescape(NamePattern().Match(line).Groups[1].Value);
            string selector = Unescape(SelectorPattern().Match(line).Groups[1].Value);
            string[] expect = Strings(ExpectPattern().Match(line));
            string[] exclude = Strings(ExcludePattern().Match(line));
            Match type = TestTypePattern().Match(line);
            int testType = 0;
            if (type.Success)
            {
                testType |= type.Value.Contains("TEST_QSA", StringComparison.Ordinal) ? 0x01 : 0;
                testType |= type.Value.Contains("TEST_FIND", StringComparison.Ordinal) ? 0x04 : 0;
                testType |= type.Value.Contains("TEST_MATCH", StringComparison.Ordinal) ? 0x10 : 0;
            }

            cases.Add(new SelectorCase(name, selector, expect, exclude, testType));
        }

        return cases;

        static string[] Strings(Match match)
        {
            if (!match.Success)
            {
                return [];
            }

            return [.. StringPattern().Matches(match.Groups[1].Value).Select(m => Unescape(m.Groups[1].Value))];
        }
    }

    // JavaScript string escapes: \uXXXX, \xXX, \n and friends; any other escaped character stands for itself.
    private static string Unescape(string text)
    {
        StringBuilder builder = new();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                builder.Append(c);
                continue;
            }

            char next = text[++i];
            switch (next)
            {
                case 'u':
                    builder.Append((char)int.Parse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                case 'x':
                    builder.Append((char)int.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                default:
                    builder.Append(next);
                    break;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"name:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"selector:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex SelectorPattern();

    [GeneratedRegex(@"expect:\s*\[((?:""(?:[^""\\]|\\.)*""|[^\]""])*)\]")]
    private static partial Regex ExpectPattern();

    [GeneratedRegex(@"exclude:\s*\[([^\]]*)\]")]
    private static partial Regex ExcludePattern();

    [GeneratedRegex(@"testType:\s*([A-Z_ |]+)")]
    private static partial Regex TestTypePattern();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringPattern();
}
