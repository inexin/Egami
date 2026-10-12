# Egami.Css test data

Test data only; it ships in no package. Each set keeps its license file beside it.

| Folder | Source | License |
| --- | --- | --- |
| css-parsing-tests/ | [SimonSapin/css-parsing-tests](https://github.com/SimonSapin/css-parsing-tests) at commit 203ce36bffd617db7f118c551e32794561fb273d (2025-09-24): every JSON file except `stylesheet_bytes.json` (byte decoding is the consumer's job) | CC0 1.0 public domain dedication (css-parsing-tests/LICENSE) |
| wpt/selectors.js, wpt/ParentNode-querySelector-All-content.xht | [web-platform-tests](https://github.com/web-platform-tests/wpt) `dom/nodes/`, master at commit c7bbc237dc396576b7486224a40a2ae2fa6d1141 (fetched 2026-10-12): the Selectors API matching data and its test document | 3-clause BSD (wpt/LICENSE.md) |
| wpt/parsing/ | web-platform-tests `css/selectors/parsing/` at the same commit: every non-tentative file | 3-clause BSD (wpt/LICENSE.md) |

The tests read the files as they are: `CssParsingTests.cs` runs the JSON cases, `WptSelectorMatchingTests.cs` reads the
`validSelectors` and `invalidSelectors` tables of selectors.js and runs them against the XHTML document (as XML, and
emulating an HTML document), and `WptSelectorParsingTests.cs` reads the `test_valid_selector` and `test_invalid_selector`
calls of the parsing tests. Cases Egami.Css does not support are listed with their reasons in the tests' skip lists.
