# Egami.Css: guide for coding agents

Egami.Css parses and applies CSS, with no dependencies: the CSS Syntax 3 tokenizer and parser, Selectors 4 matching over any
element tree, the cascade with custom properties and `var()`, media queries, `@page` and value parsers for colors, lengths,
`calc()`, transforms and fonts. Egami.Svg styles SVG with it; you can use it for your own documents.

Namespace: `Egami.Css`. It does not depend on core Egami.

## Entry points

- `CssStyleSheet.Parse(text, CssParseOptions)`: a style sheet of rules; errors go to `CssParseOptions.Errors`.
- `CssDeclaration.ParseList(text)`: a `style` attribute's declarations.
- `ICssElement`: implement it for your own element type, then `CssSelectorList.Matches` and the cascade work on your tree.
- `CssCascade` with `CssPropertyRegistry`: cascade an element, then `Compute` it from its parent's computed style.
- `CssColor.TryParse`, and the parsers for lengths, transforms, fonts and page sizes.

## Samples

Parse a style sheet:

{{sample Css.cs#parse}}

Cascade and compute:

{{sample Css.cs#cascade}}

Colors:

{{sample Css.cs#colors}}

## Common mistakes

- Wrapping `Parse` in try/catch to find bad CSS. Parsing never throws on bad CSS: it recovers as CSS Syntax 3 says and reports
  each problem through `CssParseOptions.Errors`.
- Expecting the cascade to know your properties. Register which properties inherit and their initial values in a
  `CssPropertyRegistry` (`CreateDefault()` has the standard ones), and shorthands with `RegisterShorthand`.
- Expecting typed values from `CssComputedStyle`. It holds component values; parse each property by its type
  (`CssColor.TryParse` and the other value parsers).
- Computing a child before its parent. `Compute` takes the parent's computed style, so walk the tree from the root.

## More

- CSS guide: https://inexin.github.io/Egami/v1/articles/guides/css.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Css.html
