# SVG

Egami.Svg reads SVG 1.1 and SVG 2 files into an editable element tree, saves them back, and draws them through Egami.Drawing's
canvas, so the same document renders to a raster, to a PDF page or back out as SVG. It is fully managed and opens nothing
outside the file unless you allow it.

Phase 2.10 is landing: reading, saving and the element tree are in; styles, units, the render tree, drawing, text, filters and SVG
output follow, and this guide grows with them.

## Loading

`SvgDocument.Load` reads an .svg or .svgz file or stream (gzip is detected by its first bytes), and `SvgDocument.Parse` reads
markup from a string. Loading is lenient, as browsers and resvg read SVG: what cannot be used is skipped, listed in
`SvgDocument.Warnings` and sent to the context's diagnostics as `Svg.*` events.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#load)]

`SvgLoadOptions.Strict` throws an `SvgException` with the line and column instead, for checking files you produce:

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#strict)]

## The element tree

The document is a tree of `SvgElement`s, like XDocument's: each has a `Name` and `Namespace`, an `SvgAttributeCollection` of
`SvgAttribute`s and an `SvgContentList` of `SvgContent` items in file order (child elements, `SvgTextContent` for text and
CDATA, `SvgComment` and `SvgProcessingInstruction`). Elements and attributes Egami does not draw, such as Inkscape's and
Illustrator's, are kept. `SvgNamespaces` names the SVG, XLink, XML and xmlns namespaces. `FindById` finds an element by its
`id`.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#edit)]

`Save` writes the tree back as SVG, with everything read from the file (unknown elements, foreign namespaces, comments, the
DOCTYPE), so a load and save without edits keeps the document's meaning; entities come out expanded, and attribute order and
whitespace inside tags may change. `SvgSaveOptions` sets indentation (only where whitespace means nothing, never inside text),
the decimals of numbers and paths set in code, and gzip for .svgz.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#save)]

Every edit marks the document for re-resolving on its next draw. The tree is not safe for writes from several threads at once,
like XDocument.

## Untrusted files

An SVG file is untrusted input. A load honors the context's `SafetyLimits`: the file, after gzip, is bounded by
`MaxTotalBytes` (a gzip bomb stops there), entity expansion by `MaxMetadataBytes` (so a billion-laughs DTD stops), the number
of elements grows with `MaxTotalBytes`, and elements nest at most 256 deep. Internal DTD entities are expanded, because
Illustrator writes them; external entities and DTDs are never opened. Images, style sheets and fonts the file refers to open
only through `SvgLoadOptions.Resolver`; `SvgResolvers.SameDirectory` allows files in one folder and nothing else, and `data:`
URIs always work. Egami never opens a network connection itself.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#resolver)]
