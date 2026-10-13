# CSS

Egami.Css parses and applies CSS in managed code, with no dependencies. Egami.Svg uses it for SVG's style sheets, `style`
attributes and presentation attributes; Egap's HTML reader will use it later. It covers the CSS Syntax 3 tokenizer and
parser, Selectors 4 matching, the cascade with custom properties and `var()`, media queries, and parsers for colors, lengths,
`calc()`, transforms and fonts. Layout and the computed values of each property stay with the consumer: Egami.Css hands back
component values, and the consumer reads them with the value parsers.

Egami.Css is at 0.1 (0.1.0); its API may still change before 1.0.

## Parsing style sheets

`CssStyleSheet.Parse` reads a sheet into rules. Parsing never throws on bad CSS: it recovers as CSS Syntax 3 says, and each
problem goes to `CssParseOptions.Errors` as a `CssParseError` with its line and column. A style rule whose selector does not
parse is dropped, as browsers drop it.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#parse)]

Every rule is a `CssRule`:

- `CssStyleRule`: a selector list and its declarations.
- `CssMediaRule` and `CssSupportsRule`: rules that apply when a media query or a condition holds.
- `CssFontFaceRule`: the descriptors of a web font, parsed as a `CssFontFace`.
- `CssImportRule`: a URL Egami.Css does not fetch. Load the sheet yourself and pass it to the cascade.
- `CssNamespaceRule`: a prefix for selectors; the sheet collects them in `CssStyleSheet.Namespaces`.
- `CssLayerRule`: cascade layers are not implemented, so the rules inside a layer block count as ordinary rules.
- `CssUnknownAtRule`: anything else, such as `@keyframes` or `@page`, kept as component values.

## Declarations

A `CssDeclaration` is a property name, its value and the `!important` flag. `CssDeclaration.ParseList` reads a `style`
attribute; `CssDeclaration.Create` makes one declaration, for example from an SVG presentation attribute. Names are
lowercased, except custom properties such as `--Accent`, which keep their case.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#declarations)]

## Tokens and component values

Values stay close to the syntax. `CssTokenizer` splits text into `CssToken`s of a `CssTokenKind`, each with its line and
column. The parser groups them into component values: a `CssComponentValue` is a `CssTokenValue`, a `CssFunction` or a
`CssSimpleBlock`. `CssComponentValue.Serialize` writes them back as CSS text.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#tokens)]

## Selectors

Selectors match a document tree through `ICssElement`. Egami.Svg's elements implement it, and an HTML DOM will. An element
gives its name and namespace, its parent and siblings, its ID, classes and attributes (as `CssAttribute`s). Optional members
cover HTML's case rules, the language for `:lang()`, the direction for `:dir()` and states such as `:hover` or `:checked`.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#element)]

`CssSelectorList.Parse` reads a selector list; `TryParse` does the same without throwing. Each `CssSelector` has its
`CssSpecificity` and its pseudo-element, if any. Prefixes such as `svg|rect` need a `CssNamespaces`. Matching runs right to
left and does not allocate, so files with thousands of elements stay fast.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#selectors)]

Egami.Css supports the Selectors 4 set: type, universal and namespaces; IDs, classes and every attribute operator with the
`i` and `s` flags; the four combinators; the structural pseudo-classes, including `:nth-child(An+B of S)`; `:not()`, `:is()`,
`:where()` and `:has()`; `:lang()` with wildcards, `:dir()`, `:scope` and `:defined`; and the pseudo-elements. An unknown
pseudo-class makes a selector invalid.

## The cascade and computed styles

`CssCascade` takes the sheets in document order, each with a `CssOrigin`, and `CssCascadeOptions`: the media context, a
callback for `@supports`, and a `CssPropertyRegistry`. It indexes the rules by ID, class and type, so each element tests
few rules.

`CssCascade.Cascade` gives an element's `CssCascadedStyle`: the winning declaration of each property, by origin and
importance, the style attribute, specificity and order. Presentation hints count as author declarations of specificity
zero, before every author rule. Shorthands are expanded first, so `font` in a sheet and `font-size` in a hint compete per
longhand.

`CssCascade.Compute` turns it into a `CssComputedStyle`: it resolves `inherit`, `initial`, `unset` and `revert`, applies
inheritance, and substitutes `var()` with fallbacks. Properties in a reference cycle become invalid. A computed style stores
only what its element sets and looks the rest up in its parent.

The registry says which properties inherit and what their initial values are. `CssPropertyRegistry.CreateDefault` knows the
common text properties of CSS. Consumers add their own with a `CssPropertyDefinition`, and shorthands with a
`CssShorthandDefinition`. A definition can validate values, so an invalid declaration is dropped and an earlier one wins.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#cascade)]

## Media queries

`CssMediaQueryList` evaluates Media Queries 4 against a `CssMediaContext`: the media type, the viewport, the resolution and
the user's preferences. It knows `not`, `only`, `and` and `or`, `min-` and `max-` prefixes and the range syntax. A query that
does not parse becomes `not all` without affecting the others; a feature the context does not know goes to its hook, or
counts as false.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#media)]

## Value parsers

Each parser has `TryParse` over text and over component values.

`CssColor` reads every CSS Color 4 syntax: the named and system colors, hex, `rgb()`, `hsl()`, `hwb()`, `lab()`, `lch()`,
`oklab()`, `oklch()` and `color()`. It converts to sRGB and clips to its gamut. `Level` says whether the text used CSS Color 3
or 4 syntax, for consumers that follow an older specification.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#colors)]

`CssLength` is a number with a `CssUnit`. `CssAngle` converts to degrees. `CssUrl` reads `url()` and `src()`. `CssCalc` reads
`calc()`, `min()`, `max()` and `clamp()`; the consumer resolves it by saying what each unit is worth.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#lengths)]

`CssTransformList` reads the CSS `transform` property into `CssTransformFunction`s:
`CssMatrixTransform`, `CssTranslateTransform`, `CssScaleTransform`, `CssRotateTransform` and `CssSkewTransform`. 3D functions
become `CssUnsupportedTransform` and the matrix ignores them. `CssTransformOrigin` reads `transform-origin`.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#transforms)]

`CssFont` reads the `font` shorthand. `CssFontFamilyList` holds `CssFontFamily` names and generic families. `CssFontWeight`
resolves `bolder` and `lighter` against the parent's weight, as its `CssFontWeightKind` says. `CssFontFace` reads
`@font-face`: its `CssFontSource`s, its weight and width as a `CssRange`, and its `CssUnicodeRange`s.

[!code-csharp[](../../../tests/Egami.DocSamples/Css.cs#fonts)]

## Not supported

- CSS Nesting: rules nested in a style rule are dropped and reported.
- Cascade layers: layer blocks count as ordinary rules.
- `@import` is not fetched, and animations and transitions are not part of the cascade.
- `@property`, `@container`, `@scope`, `@keyframes`, `@page` and similar rules are kept as unknown at-rules.
- Shadow DOM: `:host` is invalid, and `::part()` and `::slotted()` parse but never match. `:state()` and Selectors 5's
  `:heading` are invalid.
- CSS Color 5: `color-mix()`, relative colors and `device-cmyk()`. Colors outside sRGB are clipped, not gamut-mapped.
- `calc()` covers lengths, percentages and numbers, not angles or `calc()` inside transforms.
- Selectors keep their text as written, not CSSOM's canonical serialization.
